"""Offline (the sandbox export): grip and support points far from the gun's drawn surface, where a hand would float beside the
gun or sink into it. For each gun, the distance from the grip point and the support point to the nearest vertex of the drawn
model (magazine included; stand-ins left out). A palm sits a couple of centimetres off the surface; much more is a hand in the
air. Usage: hand_points.py [weapon dir ...]"""
import json
import os
import sys

import numpy as np

sys.stdout.reconfigure(encoding='utf-8')
ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))  # the repository
sys.path.insert(0, os.path.join(ROOT, 'tools', 'sandbox', 'app'))
import glb  # noqa: E402

DATA = os.path.join(ROOT, 'reports', 'sandbox', 'data', 'weapons')
rows = []
for folder in sys.argv[1:] or sorted(os.listdir(DATA)):
    try:
        rig = json.load(open(os.path.join(DATA, folder, 'rig.json'), encoding='utf-8'))
        nodes = glb.load(os.path.join(DATA, folder, 'model.glb'))
    except Exception:
        continue
    pts = [p.positions[np.unique(p.indices)] for n in nodes if not n.stand_in for p in n.primitives if len(p.indices)]
    if not pts:
        continue
    pts = np.concatenate(pts).astype(float)
    out = []
    for key in ('grip', 'support'):
        if key == 'support' and not rig.get('hasSupport'):
            continue
        q = rig.get(key)
        if not q:
            continue
        dist = float(np.sqrt(((pts - np.asarray(q, float)) ** 2).sum(axis=1).min()))
        out.append((key, dist))
    if out:
        rows.append((folder, rig.get('action'), out))
far = [(f, a, o) for f, a, o in rows if any(d > 0.05 for _, d in o)]
for f, a, o in sorted(far, key=lambda r: -max(d for _, d in r[2])):
    print(f'{f:50s} {a:12s} ' + ', '.join(f'{k} {d * 100:.1f} cm off the surface' for k, d in o))
print(f'{len(far)} of {len(rows)} guns with a hand point more than 5 cm from the drawn model')
