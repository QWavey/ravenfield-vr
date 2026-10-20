"""Checks src/Weapons/MagLineTable.cs against the sandbox export, in Unity's space: each line must be a unit vector, its stated
tilt (fore/aft, sideways) off the export's analysed line once that is turned back from glTF (x, y, -z), and keyed by its folder's
analysis key. The table was once written in glTF's space and every line was applied mirrored in z (134-178 degrees off in game,
2026-10-20 night); this catches that and any later slip. Exit code 1 when a line is wrong.
Usage: mag_line_table_check.py"""
import json
import os
import re
import sys

import numpy as np

sys.stdout.reconfigure(encoding='utf-8')
ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))  # the repository
DATA = os.path.join(ROOT, 'reports', 'sandbox', 'data', 'weapons')
TABLE = os.path.join(ROOT, 'src', 'Weapons', 'MagLineTable.cs')
rows = re.findall(r'\{ "([^"]+)", new Vector3\(([-\d.]+)f, ([-\d.]+)f, ([-\d.]+)f\) \}, // (\S+): ([+-][\d.]+) fore/aft, ([+-][\d.]+) sideways',
                  open(TABLE, encoding='utf-8').read())
bad = 0
for key, x, y, z, folder, fore, side in rows:
    v = np.array([float(x), float(y), float(z)])
    rig = json.load(open(os.path.join(DATA, folder, 'rig.json'), encoding='utf-8'))
    e = np.asarray(rig['magEject'], float)
    unity = np.array([e[0], e[1], -e[2]]) / np.linalg.norm(e)  # back from glTF's space, as the game holds it
    angle = float(np.degrees(np.arccos(np.clip(unity @ v / np.linalg.norm(v), -1, 1))))
    tilt = float(np.hypot(float(fore), float(side)))
    problems = []
    if abs(np.linalg.norm(v) - 1) > 1e-3:
        problems.append(f'length {np.linalg.norm(v):.4f}')
    if abs(angle - tilt) > 1.0:
        problems.append(f'{angle:.1f} deg off the analysed line, the comment says {tilt:.1f}')
    if rig.get('key') != key:
        problems.append(f"keyed {key!r}, its folder's key is {rig.get('key')!r}")
    if problems:
        bad += 1
        print(f'{folder}: ' + '; '.join(problems))
print(f'{len(rows)} lines, {bad} wrong')
sys.exit(1 if bad or not rows else 0)
