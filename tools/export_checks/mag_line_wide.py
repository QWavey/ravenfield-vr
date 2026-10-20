"""Offline (CPU only), the wide search (fore/aft to 30 degrees, sideways to 12; mag_line_table.py takes its best unless on its own edge): a gun's magazine slid out along its line to several depths, against the gun body's side outline
(1 mm raster, magazine parts removed) and the body's thickness at each spot. Counts, per depth, magazine points hidden
seated that show beside the grip on the way (front/back), and points standing out of the grip's sides (through the
panels). Then the same over line directions tipped in the side plane (fore/aft) and sideways, to see if a better line
exists. Usage: mag_line_scan.py <weapon dir name> [more...]"""
import json
import os
import sys

import numpy as np
from PIL import Image, ImageDraw

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))  # the repository
sys.path.insert(0, os.path.join(ROOT, 'tools', 'sandbox', 'app'))
import glb  # noqa: E402

DATA = os.path.join(ROOT, 'reports', 'sandbox', 'data', 'weapons')
RES = 0.001  # 1 mm cells


def v3(a):
    return np.array(a, dtype=np.float64)


def norm(a):
    a = v3(a)
    n = np.linalg.norm(a)
    return a / n if n > 1e-9 else a


def quat_to_mat3(q):
    x, y, z, w = q
    return np.array([[1 - 2 * (y * y + z * z), 2 * (x * y - z * w), 2 * (x * z + y * w)],
                     [2 * (x * y + z * w), 1 - 2 * (x * x + z * z), 2 * (y * z - x * w)],
                     [2 * (x * z - y * w), 2 * (y * z + x * w), 1 - 2 * (x * x + y * y)]])


def prefix(path, part):
    return path == part or path.startswith(part + '/')


def rot_about(v, axis, deg):
    axis = norm(axis)
    t = np.radians(deg)
    return v * np.cos(t) + np.cross(axis, v) * np.sin(t) + axis * np.dot(axis, v) * (1 - np.cos(t))


def scan(name):
    d = os.path.join(DATA, name)
    rig = json.load(open(os.path.join(d, 'rig.json'), encoding='utf-8'))
    nodes = glb.load(os.path.join(d, 'model.glb'))
    nodes = nodes if isinstance(nodes, list) else getattr(nodes, 'nodes', nodes)
    parts = [p for p in [rig.get('magazine')] + list(rig.get('magazineParts', [])) if p]
    aim = norm(rig['aim'])
    up = v3(rig.get('up', [0, 1, 0]))
    up = norm(up - aim * np.dot(up, aim))
    right = norm(np.cross(aim, up))
    body, mag = [], []
    for n in nodes:
        if n.name.startswith('[stand-in] '):
            continue
        t = n.triangles()
        if not len(t):
            continue
        (mag if any(prefix(n.path, p) for p in parts) else body).append(t)
    if not body or not mag:
        return f'{name}: no body or no magazine triangles'
    body = np.concatenate(body)
    mag = np.concatenate(mag)
    mpts = np.unique(mag.reshape(-1, 3).round(5), axis=0)
    # Side raster of the body: (aim, up) coordinates, per cell the body's min/max along `right` (its thickness there).
    b2 = np.stack([body @ aim, body @ up], axis=-1)  # (n, 3, 2)
    lo = b2.reshape(-1, 2).min(axis=0) - 0.02
    hi = b2.reshape(-1, 2).max(axis=0) + 0.02
    wpx, hpx = int((hi[0] - lo[0]) / RES) + 1, int((hi[1] - lo[1]) / RES) + 1
    occ = Image.new('L', (wpx, hpx), 0)
    dr = ImageDraw.Draw(occ)
    rmin = np.full((hpx, wpx), np.inf)
    rmax = np.full((hpx, wpx), -np.inf)
    br = body @ right
    for tri, r3 in zip(b2, br):
        px = [((x - lo[0]) / RES, (y - lo[1]) / RES) for x, y in tri]
        dr.polygon(px, fill=255)
        x0, x1 = int(min(p[0] for p in px)), int(max(p[0] for p in px)) + 1
        y0, y1 = int(min(p[1] for p in px)), int(max(p[1] for p in px)) + 1
        rmin[y0:y1, x0:x1] = np.minimum(rmin[y0:y1, x0:x1], r3.min())
        rmax[y0:y1, x0:x1] = np.maximum(rmax[y0:y1, x0:x1], r3.max())
    occ = np.array(occ) > 0

    def cells(p):
        x = ((p @ aim - lo[0]) / RES).astype(int).clip(0, wpx - 1)
        y = ((p @ up - lo[1]) / RES).astype(int).clip(0, hpx - 1)
        return y, x

    def measure(pts):
        y, x = cells(pts)
        inside = occ[y, x]
        r = pts @ right
        through = inside & ((r < rmin[y, x] - 0.0005) | (r > rmax[y, x] + 0.0005))
        return inside, through

    eject = norm(rig.get('magEject', [0, -1, 0]))
    seat_in, seat_through = measure(mpts)
    if not seat_in.any():
        # Nothing of it hidden by the gun when seated (an en-bloc clip inside the receiver, a magazine beside it): no well to slide in.
        return f'{name}: no magazine point hidden by the gun when seated; nothing to scan'
    length = float(np.ptp(mpts @ eject))
    depths = [length * f for f in (0.1, 0.25, 0.4, 0.55, 0.7)]

    def run(direction):
        shown, poke = [], []
        for dep in depths:
            moved = mpts + direction * dep
            inside, through = measure(moved)
            # Hidden seated, and still where the grip is (its cell was inside the outline seated): showing now = beside the well.
            # Only the points that are past the mouth now: their moved cell lies inside the outline's rows of the seated part.
            showing = seat_in & ~inside & occ[cells(mpts - eject * 0.0)[0], cells(mpts)[1]]
            gone_in = seat_in & ((mpts @ eject) + dep <= (mpts @ eject)[seat_in].max())
            shown.append(int((showing & gone_in).sum()))
            poke.append(int((through & gone_in).sum()))
        return shown, poke

    base_shown, base_poke = run(eject)
    out = [f'{name}: {len(mpts)} magazine points, {length * 100:.1f} cm along the line; seated: {int(seat_through.sum())} through the sides',
           f'  line as analysed: beside the outline per depth {base_shown}, through the sides {base_poke}']
    # The magazine's own long axis (principal direction of its points), against the analysed line.
    c = mpts - mpts.mean(axis=0)
    w, vecs = np.linalg.eigh(c.T @ c)
    axis = vecs[:, -1] * (1 if np.dot(vecs[:, -1], eject) > 0 else -1)
    fa = np.degrees(np.arctan2(np.dot(axis, aim), np.dot(axis, -up))) - np.degrees(np.arctan2(np.dot(eject, aim), np.dot(eject, -up)))
    out.append(f'  its own long axis is {np.degrees(np.arccos(np.clip(np.dot(axis, eject), -1, 1))):.1f} deg off the line ({fa:+.1f} fore/aft, {np.degrees(np.arcsin(np.clip(np.dot(axis, right), -1, 1))) - np.degrees(np.arcsin(np.clip(np.dot(eject, right), -1, 1))):+.1f} sideways)')
    s, p = run(axis)
    out.append(f'    along it: beside {s}, through {p}')
    tried = [(sum(base_shown) + sum(base_poke), 0.0, 0.0)]
    for fore in [x * 1.5 for x in range(-20, 21)]:
        for side in [x * 1.5 for x in range(-8, 9)]:
            dvec = norm(rot_about(rot_about(eject, right, fore), aim, side))
            s, p = run(dvec)
            tried.append((sum(s) + sum(p), fore, side))
    # The smallest tilt among the (near) best: the count is flat over a range of lines (often 0), and the first one tried, the
    # steepest, was taken; tilted far, a magazine also hides inside the receiver, which the side outline cannot see.
    least = min(t[0] for t in tried)
    near = [t for t in tried if t[0] <= least + max(2, least * 0.1)]
    best = min(near, key=lambda t: (t[1] ** 2 + t[2] ** 2, t[0]))
    # Within a plausible tilt (fore/aft 18, sideways 6): beyond it the magazine can hide inside the receiver, which the side
    # outline cannot see, and the count falls for that alone (the HK416's -28.5 drew it through the lower receiver).
    capped_all = [t for t in tried if abs(t[1]) <= 18.01 and abs(t[2]) <= 6.01]
    capped_least = min(t[0] for t in capped_all)
    capped = min([t for t in capped_all if t[0] <= capped_least + max(2, capped_least * 0.1)], key=lambda t: (t[1] ** 2 + t[2] ** 2, t[0]))
    out.append(f'  best tip (wide): fore/aft {best[1]:+.1f} deg, sideways {best[2]:+.1f} deg, score {best[0]} (analysed {sum(base_shown) + sum(base_poke)})' + (' (edge)' if abs(best[1]) >= 29.9 or abs(best[2]) >= 11.9 else ''))
    out.append(f'  best tip (capped): fore/aft {capped[1]:+.1f} deg, sideways {capped[2]:+.1f} deg, score {capped[0]} (analysed {sum(base_shown) + sum(base_poke)})')
    if best[1] or best[2]:
        s, p = run(norm(rot_about(rot_about(eject, right, best[1]), aim, best[2])))
        out.append(f'    there: beside {s}, through {p}')
    return '\n'.join(out)


if __name__ == '__main__':
    for n in sys.argv[1:]:
        try:
            print(scan(n), flush=True)
        except Exception as e:  # one gun's export failing must not stop the rest
            print(f'{n}: failed: {e!r}', flush=True)
