"""Offline: parts a holstered or dropped copy should not draw: those standing apart from the gun's body (more than 5 cm
from the cluster of touching parts grown from the biggest one, and under 40% of the body's size,
or farther from it than half its size and under 70% of it) and beams or light cones by name. The copies are made from
the weapon's third-person model; the export holds the first-person one, which has the same parts on these mods, so this
shows what the rule (WeaponDrops.StripStrays) would take. Usage: stray_parts_rule.py [gun dir ...]"""
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
BEAM = re.compile(r'beam|(?<![a-z])cone|volumetric|light ?shaft|light ?ray|flare|glow', re.I)
GAP = 0.05


def gap(a, b):
    d = np.maximum(0, np.maximum(a[0] - b[1], b[0] - a[1]))
    return float(np.linalg.norm(d))


names = sys.argv[1:] or sorted(os.path.basename(x) for x in glob.glob(os.path.join(DATA, '*')))
total = 0
for n in names:
    try:
        nodes = [x for x in glb.load(os.path.join(DATA, n, 'model.glb')) if not x.stand_in and len(x.triangles())]
    except Exception as e:
        print(n, 'failed', e)
        continue
    if not nodes:
        continue
    boxes = [(x.bmin, x.bmax) for x in nodes]
    vol = [float(np.prod(np.maximum(b[1] - b[0], 1e-3))) for b in boxes]
    seed = int(np.argmax(vol))
    inc = [False] * len(nodes)
    inc[seed] = True
    grew = True
    while grew:
        grew = False
        for i in range(len(nodes)):
            if not inc[i] and any(inc[j] and gap(boxes[i], boxes[j]) <= GAP for j in range(len(nodes))):
                inc[i] = grew = True
    body_min = np.min([boxes[i][0] for i in range(len(nodes)) if inc[i]], axis=0)
    body_max = np.max([boxes[i][1] for i in range(len(nodes)) if inc[i]], axis=0)
    body_size = float(np.linalg.norm(body_max - body_min))
    # The parts apart, in their own clusters of touching parts, each judged whole: under 40% of the body's size they go (an
    # akimbo pair's second gun, many small parts as big together as the first, stays).
    group = [0 if inc[i] else -1 for i in range(len(nodes))]
    stray = []
    for i in range(len(nodes)):
        if group[i] != -1:
            continue
        members = [i]
        group[i] = 1
        grew = True
        while grew:
            grew = False
            for k in range(len(nodes)):
                if group[k] == -1 and any(gap(boxes[k], boxes[m]) <= GAP for m in members):
                    group[k] = 1
                    members.append(k)
                    grew = True
        gmin = np.min([boxes[m][0] for m in members], axis=0)
        gmax = np.max([boxes[m][1] for m in members], axis=0)
        size = float(np.linalg.norm(gmax - gmin))
        far = gap((gmin, gmax), (body_min, body_max)) > 0.5 * body_size
        if size < 0.4 * body_size or far and size < 0.7 * body_size:
            stray += [nodes[m].name.split('/')[-1][:30] + f' ({min(gap(boxes[m], boxes[j]) for j in range(len(nodes)) if inc[j]) * 100:.0f} cm off)' for m in members]
    beams = [x.name.split('/')[-1][:30] for x in nodes if any(BEAM.search(s) for s in x.name.split('/')[-2:])]
    if stray or beams:
        total += 1
        print(f'{n[:50]:50s} apart: {stray}  beams: {beams}')
print(total, 'guns with something to strip')
