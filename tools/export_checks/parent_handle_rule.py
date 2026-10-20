"""Offline check of a rule (Tasks 307, the K13): a self-loading AR whose moving part (the slide) hangs under a node that draws
a charging handle of its own: a mesh on the slide's parent, long along the bore (12-25 cm), flat (at most 1.5 cm tall), a T
across (3 cm or more) at its back. Racked, the mod moved the slide and a small latch as "the handle", and the T stayed. Lists
the guns the rule takes, to see it takes the K13s and nothing else.
Usage: parent_handle_rule.py"""
import json
import os
import sys

import numpy as np

sys.stdout.reconfigure(encoding='utf-8')
ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))  # the repository
sys.path.insert(0, os.path.join(ROOT, 'tools', 'sandbox', 'app'))
import glb  # noqa: E402

DATA = os.path.join(ROOT, 'reports', 'sandbox', 'data', 'weapons')
norm = lambda v: np.asarray(v, float) / np.linalg.norm(v)
taken = 0
for folder in sorted(os.listdir(DATA)):
    try:
        rig = json.load(open(os.path.join(DATA, folder, 'rig.json'), encoding='utf-8'))
    except OSError:
        continue
    slide = rig.get('slide')
    if rig.get('action') != 'SelfLoading' or not slide or '/' not in slide or rig.get('handgun'):
        continue
    parent = slide.rsplit('/', 1)[0]
    try:
        nodes = {n.path: n for n in glb.load(os.path.join(DATA, folder, 'model.glb'))}
    except Exception:
        continue
    p = nodes.get(parent)
    if p is None:
        continue  # the slide's parent draws nothing of its own
    aim = norm(rig['aim'])
    up = np.asarray(rig.get('up', [0, 1, 0]), float)
    up = norm(up - aim * up.dot(aim))
    right = np.cross(aim, up)
    pts = np.concatenate([pr.positions[np.unique(pr.indices)] for pr in p.primitives if len(pr.indices)])
    along, tall, across = np.ptp(pts @ aim), np.ptp(pts @ up), np.ptp(pts @ right)
    # the T: its back fifth, wide
    a = pts @ aim
    back = pts[a <= a.min() + 0.2 * (a.max() - a.min())]
    t_across = np.ptp(back @ right) if len(back) else 0
    ok = 0.12 <= along <= 0.25 and tall <= 0.015 and t_across >= 0.03
    if ok or 'k13' in folder.lower():
        taken += ok
        print(f'{folder:40s} {"TAKEN" if ok else "not  "} parent {parent} {p.short!r}: {along*100:.1f} along, {tall*100:.1f} tall, '
              f'{across*100:.1f} across, its back fifth {t_across*100:.1f} across; handle now {rig.get("handle")}')
print(taken, 'guns taken')
