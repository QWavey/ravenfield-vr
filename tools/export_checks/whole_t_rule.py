"""Offline check of a rule (the M4A1 [SQUAD] family's rack, 2026-10-20 night): an AR whose moving part is its bolt carrier and
whose T was not found (ArStaticHandle takes only a T 3-12 cm long) may draw its charging handle whole, T and shaft in one part
(the squad's '对象003': 16.3 cm along the bore, 1.1 cm tall, 5.5 cm across). A second pass takes such a part (12-22 cm, at most
1.5 cm tall, 3 cm or more across, its middle 0-6 cm over the bore and at most 1.5 cm over the carrier's top, within 7 cm of the
carrier's rear top), only where the first found nothing. Lists the guns it would take (no animation data here: the game also
needs the part still in the reload).
Usage: whole_t_rule.py"""
import json
import os
import re
import sys

import numpy as np

sys.stdout.reconfigure(encoding='utf-8')
ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))  # the repository
sys.path.insert(0, os.path.join(ROOT, 'tools', 'sandbox', 'app'))
import glb  # noqa: E402

DATA = os.path.join(ROOT, 'reports', 'sandbox', 'data', 'weapons')
AR = re.compile(r'(?<![a-z])(m4|m4a1|m16|ar-?15|ar-?10|hk ?416|hk ?417|m ?417|mr ?308|mr ?762|lmt ?308|mk ?18|mk ?12|m733|m727|xm177|car-?15|cqbr|urgi|sr-?25|m110|mcx|honey ?badger|ddm4|m27|g28|lvoa|k13)(?!\d)', re.I)
norm = lambda v: np.asarray(v, float) / np.linalg.norm(v)
taken = 0
for folder in sorted(os.listdir(DATA)):
    try:
        rig = json.load(open(os.path.join(DATA, folder, 'rig.json'), encoding='utf-8'))
    except OSError:
        continue
    sm = rig.get('slideMethod') or ''
    if (rig.get('action') != 'SelfLoading' or rig.get('handgun') or rig.get('handle') or not rig.get('slide') or not AR.search(rig.get('key', ''))
            or not (sm.startswith('reload motion (charging handle)') or sm.startswith('fire motion'))):
        continue
    nodes = glb.load(os.path.join(DATA, folder, 'model.glb'))
    aim = norm(rig['aim'])
    up = np.asarray(rig.get('up', [0, 1, 0]), float)
    up = norm(up - aim * up.dot(aim))
    right = np.cross(aim, up)
    muzzle = np.asarray(rig['muzzle'], float)
    slide = rig['slide']
    sp = [n for n in nodes if n.path == slide or n.path.startswith(slide + '/')]
    if not sp:
        continue
    spts = np.concatenate([p.positions[np.unique(p.indices)] for n in sp for p in n.primitives if len(p.indices)])
    # The carrier's rear top (its box's back end along the aim, top along the up).
    rear_top = (spts @ aim).min() * aim + (spts @ up).max() * up + ((spts @ right).mean()) * right
    best = None
    for n in nodes:
        if n.stand_in or n.path == slide or n.path.startswith(slide + '/') or slide.startswith(n.path + '/'):
            continue
        if re.search(r'mag|sight|scope|optic|lens|glass|rail', n.name, re.I):
            continue
        pts = np.concatenate([p.positions[np.unique(p.indices)] for p in n.primitives if len(p.indices)]) if n.primitives else None
        if pts is None or not len(pts):
            continue
        a, u, r = pts @ aim, pts @ up, pts @ right
        along, tall, across = np.ptp(a), np.ptp(u), np.ptp(r)
        centre = pts.mean(axis=0)
        above = (centre - muzzle) @ up
        if not (0.12 <= along <= 0.22 and tall <= 0.015 and across >= 0.03 and 0 <= above <= 0.06):
            continue
        if (centre - rear_top) @ up > 0.015:
            continue
        lo, hi = pts.min(axis=0), pts.max(axis=0)
        gap = float(np.linalg.norm(np.maximum(0, np.maximum(lo - rear_top, rear_top - hi))))
        if gap < 0.07 and (best is None or gap < best[0]):
            best = (gap, n, along, tall, across)
    if best:
        taken += 1
        g, n, along, tall, across = best
        print(f'{folder:40s} takes {n.path} {n.short!r}: {along*100:.1f} along, {tall*100:.1f} tall, {across*100:.1f} across, {g*100:.1f} cm from the carrier\'s rear top ({sm})')
print(taken, 'guns taken')
