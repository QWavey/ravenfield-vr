"""Offline: the belt of a belt-fed gun's model modelled as a part of its own with no belt name: the MG3's '对象010', from the
top of the box hanging at its left across the feed tray into the receiver. It is not part of the box, so it stayed when
the box came out, and the mod laid its own belt beside it (task 312). A candidate touches the box (within 1.5 cm), runs
across the receiver over the bore's line, lies under the cover's top, is smaller than the box (80%), at least 4 cm across
and at most 15 cm along the bore, and is not the box, the cover, the bolt or the handle, nor named as a tray, feed,
cover, lid, release, trigger, stock or grip. The rule (WeaponAnalyzer.FindBeltParts) takes
it as the model's belt: hidden while no belt is laid, the laid belt's place. Usage: belt_part_rule.py"""
import glob
import json
import os
import sys

import numpy as np

sys.stdout.reconfigure(encoding='utf-8')
ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))  # the repository
sys.path.insert(0, os.path.join(ROOT, 'tools', 'sandbox', 'app'))
import glb  # noqa: E402

DATA = os.path.join(ROOT, 'reports', 'sandbox', 'data', 'weapons')


def norm(a):
    a = np.array(a, float)
    return a / np.linalg.norm(a)


def rel(path, part):
    return part and (path == part or path.startswith(part + '/') or part.startswith(path + '/'))


for d in sorted(glob.glob(os.path.join(DATA, '*'))):
    try:
        rig = json.load(open(os.path.join(d, 'rig.json'), encoding='utf-8'))
    except Exception:
        continue
    if rig.get('hingeKind') != 'FeedCover' or not rig.get('magazine'):
        continue
    aim = norm(rig['aim'])
    up = np.array(rig['up'], float)
    up = norm(up - aim * (up @ aim))
    side = np.cross(aim, up)
    nodes = [n for n in glb.load(os.path.join(d, 'model.glb')) if not n.stand_in and len(n.triangles())]
    box_parts = [rig['magazine']] + list(rig.get('magazineParts') or [])
    boxp = [n.triangles().reshape(-1, 3) for n in nodes if any(rel(n.path, b) for b in box_parts)]
    if not boxp:
        continue
    bp = np.concatenate(boxp)
    ba, bu, bs = bp @ aim, bp @ up, bp @ side
    top = bu.max()
    height = np.ptp(bu)
    picks = []
    bmin, bmax = bp.min(axis=0), bp.max(axis=0)
    muzzle = np.array(rig['muzzle'], float)
    bore_side = muzzle @ side
    cover = [n.triangles().reshape(-1, 3) for n in nodes if rel(n.path, rig.get('hinge'))]
    cover_top = float(np.concatenate(cover).__matmul__(up).max()) if cover else np.inf
    import re
    for n in nodes:
        if any(rel(n.path, b) for b in box_parts) or rel(n.path, rig.get('hinge')) or rel(n.path, rig.get('slide')) or rel(n.path, rig.get('handle')) or rel(n.path, rig.get('bolt')):
            continue
        if re.search(r'tray|feed|cover|lid|release|trigger|stock|grip', n.name.split('/')[-1], re.I):
            continue
        p = n.triangles().reshape(-1, 3)
        pmin, pmax = p.min(axis=0), p.max(axis=0)
        gap = float(np.linalg.norm(np.maximum(0, np.maximum(pmin - bmax, bmin - pmax))))
        s_ = p @ side
        crosses = s_.min() <= bore_side <= s_.max()
        under = (p @ up).max() <= cover_top + 0.005
        small = np.linalg.norm(pmax - pmin) <= 0.8 * np.linalg.norm(bmax - bmin)
        wide = np.ptp(s_) >= 0.04 and np.ptp(p @ aim) <= 0.15  # across the receiver, not a long part
        if gap <= 0.015 and crosses and under and small and wide:
            picks.append(f"{n.path} '{n.name.split('/')[-1][:24]}' {np.ptp(p @ aim) * 100:.1f}x{np.ptp(p @ up) * 100:.1f}x{np.ptp(s_) * 100:.1f} cm, {gap * 100:.1f} cm from the box")
    print(f"{os.path.basename(d)[:44]:44s} {'; '.join(picks) if picks else '(none)'}")
