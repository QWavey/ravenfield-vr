"""Offline side view (CPU, PIL): the gun body's outline (grey), the magazine seated (blue) and slid out along its line by a
fraction of its length (green), its points showing beside the outline while past the mouth (red). Usage:
mag_line_draw.py <weapon dir> <fraction> <out.png> [fore/aft deg] [sideways deg] [front]  (the line tilted as mag_line_scan.py
tilts it; "front": seen from the front, across the gun, as mag_line_body.py's second view)"""
import json
import os
import sys

import numpy as np
from PIL import Image, ImageDraw

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))  # the repository
sys.path.insert(0, os.path.join(ROOT, 'tools', 'sandbox', 'app'))
import glb  # noqa: E402

DATA = os.path.join(ROOT, 'reports', 'sandbox', 'data', 'weapons')
name, frac, out = sys.argv[1], float(sys.argv[2]), sys.argv[3]
rig = json.load(open(os.path.join(DATA, name, 'rig.json'), encoding='utf-8'))
nodes = glb.load(os.path.join(DATA, name, 'model.glb'))
parts = [p for p in [rig.get('magazine')] + list(rig.get('magazineParts', [])) if p]


def norm(a):
    a = np.array(a, float)
    return a / np.linalg.norm(a)


aim = norm(rig['aim'])
up = np.array(rig.get('up', [0, 1, 0]), float)
up = norm(up - aim * up.dot(aim))
eject = norm(rig.get('magEject', [0, -1, 0]))
if len(sys.argv) > 4 and sys.argv[4] != 'front':
    def rot_about(v, axis, deg):
        axis = norm(axis)
        r = np.radians(deg)
        return v * np.cos(r) + np.cross(axis, v) * np.sin(r) + axis * np.dot(axis, v) * (1 - np.cos(r))
    right = norm(np.cross(aim, up))
    eject = norm(rot_about(rot_about(eject, right, float(sys.argv[4])), aim, float(sys.argv[5]) if len(sys.argv) > 5 else 0.0))
body, mag = [], []
for n in nodes:
    t = n.triangles()
    if len(t) and not n.name.startswith('[stand-in] '):
        (mag if any(n.path == p or n.path.startswith(p + '/') for p in parts) else body).append(t)
body, mag = np.concatenate(body), np.concatenate(mag)
S = 4000  # px per metre (4 px a millimetre)
across = norm(np.cross(aim, up)) if 'front' in sys.argv[4:] else aim
pts2 = lambda p: np.stack([p @ across, -(p @ up)], axis=-1)
mp = np.unique(mag.reshape(-1, 3).round(5), axis=0)
allp = np.concatenate([pts2(body.reshape(-1, 3)), pts2(mp), pts2(mp + eject * 0.15)])
lo = allp.min(axis=0) - 0.01
# Crop to the grip: around the magazine seated and slid.
mlo = np.minimum(pts2(mp).min(axis=0), pts2(mp + eject * 0.15).min(axis=0)) - 0.03
mhi = np.maximum(pts2(mp).max(axis=0), pts2(mp + eject * 0.15).max(axis=0)) + 0.03
W, H = int((mhi[0] - mlo[0]) * S), int((mhi[1] - mlo[1]) * S)
img = Image.new('RGB', (W, H), (250, 250, 250))
dr = ImageDraw.Draw(img)
px = lambda q: [((x - mlo[0]) * S, (y - mlo[1]) * S) for x, y in q]
for tri in pts2(body.reshape(-1, 3)).reshape(-1, 3, 2):
    dr.polygon(px(tri), fill=(190, 190, 190))
occ = np.array(img)[:, :, 0] < 220
length = float(np.ptp(mp @ eject))
moved = mp + eject * (length * frac)
for tri in pts2(mag.reshape(-1, 3)).reshape(-1, 3, 2):
    dr.polygon(px(tri), outline=(60, 90, 220))
for tri in pts2((mag.reshape(-1, 3) + eject * (length * frac))).reshape(-1, 3, 2):
    dr.polygon(px(tri), outline=(40, 160, 60))
seat_in_max = None
q0 = np.array(px(pts2(mp)))
inside0 = occ[q0[:, 1].astype(int).clip(0, H - 1), q0[:, 0].astype(int).clip(0, W - 1)]
seat_in_max = (mp @ eject)[inside0].max()
q = np.array(px(pts2(moved)))
inside = occ[q[:, 1].astype(int).clip(0, H - 1), q[:, 0].astype(int).clip(0, W - 1)]
gone_in = (mp @ eject) + length * frac <= seat_in_max
bad = inside0 & gone_in & ~inside
for x, y in q[bad]:
    dr.ellipse([x - 3, y - 3, x + 3, y + 3], fill=(220, 30, 30))
print(f'{name} at {frac:.0%}: {int(bad.sum())} points beside the outline; image {W}x{H}')
img.save(out)
