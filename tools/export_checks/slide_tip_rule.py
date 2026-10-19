"""Offline: the rack point on the moving part's tip farthest from the bore (WeaponAnalyzer.RackOnOutstandingPiece's vertex
step), for guns whose moving part the game keeps only on the graphics card, so the analysis could not read it and the
rack point stayed on the part's middle, inside the receiver (the QJB 95 / 95-1: its cocking hook 19 cm ahead of the rack
point, out of the hand's reach; task 305). In game the tip comes from the part's depth-rendered surface (HeldWeapon); here
from the export's vertices. Lists, per gun the rule would act on, how far the rack point moves and where to.
Usage: slide_tip_rule.py"""
import glob
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


def norm(a):
    a = np.array(a, float)
    return a / np.linalg.norm(a)


for d in sorted(glob.glob(os.path.join(DATA, '*'))):
    try:
        rig = json.load(open(os.path.join(d, 'rig.json'), encoding='utf-8'))
    except Exception:
        continue
    m = rig.get('slideMethod') or ''
    if (rig.get('handgun') or not rig.get('slide') or rig.get('handle') or rig.get('carveHandle') or rig.get('addedHandle')
            or m.startswith('reload motion (charging handle)') or '(taken by' in m or 'reload hand' in m or AR.search(rig.get('key') or '')):
        continue
    if 'read back from the graphics card' not in (rig.get('export') or ''):
        continue  # readable: the analysis already took the tip from its vertices
    aim = norm(rig['aim'])
    muzzle, grip, rack = (np.array(rig[k], float) for k in ('muzzle', 'grip', 'rackPoint'))
    slide = rig['slide']
    pts = [n.triangles().reshape(-1, 3) for n in glb.load(os.path.join(d, 'model.glb'))
           if (n.path == slide or n.path.startswith(slide + '/')) and len(n.triangles()) and not n.stand_in]
    if not pts:
        continue
    p = np.concatenate(pts)
    if (p.mean(0) - grip) @ aim > 0.27:
        continue  # a pump's forend: the hand closes round it
    off = lambda q: np.linalg.norm((q - muzzle) - np.outer((q - muzzle) @ aim, aim) if q.ndim == 2 else (q - muzzle) - aim * ((q - muzzle) @ aim), axis=-1)
    grip_along = (grip - muzzle) @ aim
    along = (p - muzzle) @ aim
    keep = (along <= -0.05) & (along >= grip_along - 0.25)
    if not keep.any():
        continue
    o = off(p)
    o[~keep] = -1
    best = off(rack) + 0.015
    i = int(np.argmax(o))
    name = os.path.basename(d)[:52]
    if o[i] > best:
        tip = p[i]
        out = (tip - muzzle) - aim * ((tip - muzzle) @ aim)
        new = tip - out / np.linalg.norm(out) * 0.008
        print(f'{name:52s} rack moves {np.linalg.norm(new - rack) * 100:5.1f} cm: {off(rack) * 100:4.1f} -> {o[i] * 100:4.1f} cm off the bore, '
              f'{(new - rack) @ aim * 100:+5.1f} cm along the bore  [{m[:40]}]')
    else:
        print(f'{name:52s} (kept: no tip over {best * 100:.1f} cm off the bore)  [{m[:40]}]')
