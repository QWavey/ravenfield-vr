"""Offline audit over the export: each self-loader's magazine eject axis (the reload line, as analysed: before the runtime
fit to the drawn gun) against the magazine's own long axis (principal direction of its mesh points) and its chord from
base to tip (a curved magazine bends). Long magazines only (3.5 times longer than wide or more); shotguns, launchers, belt boxes and
break-actions load differently and are left out. A raked magazine whose meshes the game keeps only on the graphics card
shows here: the analysis cannot read its vertices in game and takes its upright box (the Vector family, task 293).
Curved magazines go in along their top, so a large chord angle alone is not a fault: draw it (mag_line_draw.py) or scan
it (mag_line_scan.py). Usage: mag_axis_audit.py"""
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


def angle(a, b):
    return float(np.degrees(np.arccos(np.clip(abs(np.dot(norm(a), norm(b))), -1, 1))))


rows = []
for d in sorted(glob.glob(os.path.join(DATA, '*'))):
    name = os.path.basename(d)
    try:
        rig = json.load(open(os.path.join(d, 'rig.json'), encoding='utf-8'))
        mags = [p for p in [rig.get('magazine')] + list(rig.get('magazineParts', [])) if p]
        eject = rig.get('magEject')
        if not mags or not eject or np.linalg.norm(eject) < 1e-6 or rig.get('action') != 'SelfLoading' or rig.get('machineGun'):
            continue
        nodes = glb.load(os.path.join(d, 'model.glb'))
    except Exception as e:  # one export failing must not stop the rest
        print(name, 'failed', e)
        continue
    pts = [n.triangles().reshape(-1, 3) for n in nodes if any(n.path == m or n.path.startswith(m + '/') for m in mags) and len(n.triangles())]
    if not pts:
        continue
    p = np.unique(np.concatenate(pts).round(5), axis=0)
    if len(p) < 20:
        continue
    c = p - p.mean(axis=0)
    w, v = np.linalg.eigh(c.T @ c)
    axis = v[:, -1]
    elong = float(np.sqrt(w[-1] / max(w[-2], 1e-12)))  # how much longer than wide (a box magazine is near 1: no axis)
    # The chord: from the end nearest the gun (along the eject axis, the top) to the far end, each the mean of its 10%.
    e = norm(eject)
    proj = p @ e
    lo, hi = np.quantile(proj, [0.1, 0.9])
    chord = p[proj >= hi].mean(axis=0) - p[proj <= lo].mean(axis=0)
    a_axis, a_chord = angle(e, axis), angle(e, chord)
    if elong < 3.5:
        continue
    rows.append((min(a_axis, a_chord), a_axis, a_chord, elong, rig.get('isPistol'), name, rig.get('magMethod', '')))

rows.sort(reverse=True)
print(f'{len(rows)} self-loaders with a long magazine and an eject axis')
print('worst (line vs the magazine: its long axis, its chord; elongation; pistol):')
for r in rows[:25]:
    print(f'  {r[0]:5.1f} deg  axis {r[1]:5.1f} chord {r[2]:5.1f}  elong {r[3]:4.1f}  {"pistol " if r[4] else "       "}{r[5][:48]}  [{str(r[6])[:40]}]')
print('over 6 deg off both:', sum(1 for r in rows if r[0] > 6), 'over 10:', sum(1 for r in rows if r[0] > 10))
