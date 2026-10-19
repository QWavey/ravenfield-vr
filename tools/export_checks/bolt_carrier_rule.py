"""Offline: the bolt-carrier rule over every exported gun. A part on the bore line (within 1.5 cm in height, 1 cm aside),
5-20 cm long along the aim, at least 2.5x longer than wide and at most 3.2 cm wide or tall, 5-22 cm ahead of the grip,
not the magazine. For guns with a known bolt (found from motion), does the rule pick it? For the AR-pattern guns with
nothing found to move, what does it pick?"""
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
    n = np.linalg.norm(a)
    return a / n if n > 1e-9 else a


def candidates(rig, nodes):
    aim = norm(rig['aim'])
    up = np.array(rig.get('up', [0, 1, 0]), float)
    up = norm(up - aim * up.dot(aim))
    side = np.cross(aim, up)
    grip, muzzle = np.array(rig['grip'], float), np.array(rig['muzzle'], float)
    mags = [p for p in [rig.get('magazine')] + list(rig.get('magazineParts', [])) if p]
    out = []
    for n in nodes:
        if n.name.startswith('[stand-in] ') or any(n.path == m or n.path.startswith(m + '/') for m in mags):
            continue
        t = n.triangles()
        if len(t) < 4:
            continue
        p = t.reshape(-1, 3)
        a, u, s = p @ aim, p @ up, p @ side
        length, height, width = np.ptp(a), np.ptp(u), np.ptp(s)
        c = p.mean(axis=0)
        ahead = (c - grip) @ aim
        h = (c - muzzle) @ up
        aside = abs((c - muzzle) @ side)
        if not (0.05 <= length <= 0.20 and height <= 0.032 and width <= 0.032 and length >= 2.5 * max(height, width)):
            continue
        if abs(h) > 0.015 or aside > 0.01 or not (0.05 <= ahead <= 0.22):
            continue
        out.append((n.path, n.name, length, height, width, ahead, h))
    return out


known_hit = known_miss = known_none = 0
for d in sorted(glob.glob(os.path.join(DATA, '*'))):
    name = os.path.basename(d)
    try:
        rig = json.load(open(os.path.join(d, 'rig.json'), encoding='utf-8'))
        if rig.get('isPistol') or rig.get('handgun') or rig.get('action') != 'SelfLoading':
            continue
        nodes = glb.load(os.path.join(d, 'model.glb'))
    except Exception as e:  # one export failing must not stop the rest
        print(name, 'failed', e)
        continue
    c = candidates(rig, nodes)
    bolt, slide = rig.get('bolt'), rig.get('slide')
    method = rig.get('slideMethod') or ''
    nothing = slide is None and rig.get('handle') is None and bolt is None
    if bolt:
        picked = [x for x in c if x[0] == bolt or x[0].startswith(bolt + '/')]
        if picked:
            known_hit += 1
        elif c:
            known_miss += 1
            print(f'MISS {name[:40]}: known bolt {bolt}, rule picked {[(x[0], x[1][:30]) for x in c]}')
        else:
            known_none += 1
    if nothing or 'AR-pattern' in method and not slide:
        print(f'NOTHING-TO-MOVE {name[:45]}: {[(x[0], x[1][-30:], round(x[2] * 100, 1), round(x[5] * 100, 1), round(x[6] * 100, 1)) for x in c]}')
print(f'known bolts: rule picked it {known_hit}, picked something else {known_miss}, picked nothing {known_none}')
