"""Offline check of magazine lines against the gun's walls (CPU, PIL). The scans (mag_line_scan.py, mag_line_wide.py) count a
magazine's points that stand out of the gun's side outline on the way out; a point pushed INTO the gun counts as hidden there,
so a line that drives the magazine through a wall of its well (a grip's wall, the magazine catch) scored well: the PPSh-41's
-16.5 pulled its top block back through the catch (2026-10-20). This counts those: the magazine slid out by a fraction of its
length, its points that land on the gun's body but off the seated magazine's own footprint (its well, widened by 1 mm), in
the side view (aim / up) and the front view (right / up), summed over a few fractions.
Usage: mag_line_body.py <weapon dir> [fore/aft deg] [sideways deg]   one line (default: the export's)
       mag_line_body.py --table                                        every line of src/Weapons/MagLineTable.cs against the export's
       mag_line_body.py --search [weapon dir ...]                      the tilt (fore/aft to 18, sideways to 6, 1.5 steps) through
                                                                       the least wall, for these guns or all of the export"""
import json
import os
import re
import sys

import numpy as np
from PIL import Image, ImageDraw

sys.stdout.reconfigure(encoding='utf-8')
ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))  # the repository
sys.path.insert(0, os.path.join(ROOT, 'tools', 'sandbox', 'app'))
import glb  # noqa: E402

DATA = os.path.join(ROOT, 'reports', 'sandbox', 'data', 'weapons')
TABLE = os.path.join(ROOT, 'src', 'Weapons', 'MagLineTable.cs')
S = 2000  # px per metre (2 px a millimetre)
FRACTIONS = (0.05, 0.1, 0.15, 0.2, 0.3)
WIDEN = 2  # px the seated footprint is widened by (1 mm: the magazine's play in its well)


def norm(a):
    a = np.asarray(a, float)
    return a / np.linalg.norm(a)


def rot_about(v, axis, deg):
    r = np.radians(deg)
    return v * np.cos(r) + np.cross(axis, v) * np.sin(r) + axis * np.dot(axis, v) * (1 - np.cos(r))


class Gun:
    def __init__(self, folder):
        self.folder = folder
        rig = json.load(open(os.path.join(DATA, folder, 'rig.json'), encoding='utf-8'))
        self.rig = rig
        nodes = glb.load(os.path.join(DATA, folder, 'model.glb'))
        parts = [p for p in [rig.get('magazine')] + list(rig.get('magazineParts', [])) if p]
        self.aim = norm(rig['aim'])
        up = np.asarray(rig.get('up', [0, 1, 0]), float)
        self.up = norm(up - self.aim * up.dot(self.aim))
        self.right = norm(np.cross(self.aim, self.up))
        self.eject = norm(rig.get('magEject', [0, -1, 0]))
        body, mag = [], []
        for n in nodes:
            t = n.triangles()
            if len(t) and not n.name.startswith('[stand-in] '):
                (mag if any(n.path == p or n.path.startswith(p + '/') for p in parts) else body).append(t)
        self.ok = bool(body) and bool(mag)
        if not self.ok:
            return
        self.body = np.concatenate(body)
        self.mag = np.concatenate(mag)
        self.mp = np.unique(self.mag.reshape(-1, 3).round(5), axis=0)
        # Two views: the side (along the aim, up) and the front (to the right, up).
        self.views = [self._view(self.aim), self._view(self.right)]

    def _view(self, across):
        flat = lambda p: np.stack([p @ across, -(p @ self.up)], axis=-1)
        reach = 0.25  # the magazine slid out up to 30% of its length: room around it
        lo = flat(self.mp).min(axis=0) - reach
        hi = flat(self.mp).max(axis=0) + reach
        W, H = int((hi[0] - lo[0]) * S), int((hi[1] - lo[1]) * S)

        def raster(tris):
            img = Image.new('L', (W, H), 0)
            dr = ImageDraw.Draw(img)
            for tri in flat(tris.reshape(-1, 3)).reshape(-1, 3, 2):
                dr.polygon([((x - lo[0]) * S, (y - lo[1]) * S) for x, y in tri], fill=255)
            return np.array(img) > 0

        body = raster(self.body)
        seat = raster(self.mag)
        wide = seat.copy()
        for dx in range(-WIDEN, WIDEN + 1):
            for dy in range(-WIDEN, WIDEN + 1):
                wide |= np.roll(np.roll(seat, dy, axis=0), dx, axis=1)
        return flat, lo, W, H, body & ~wide

    def through(self, eject):
        """Points of the magazine on the gun's body but off its own well, over the fractions, side and front."""
        length = float(np.ptp(self.mp @ eject))
        counts = []
        for flat, lo, W, H, wall in self.views:
            total = 0
            for f in FRACTIONS:
                q = (flat(self.mp + eject * (length * f)) - lo) * S
                x, y = q[:, 0].astype(int).clip(0, W - 1), q[:, 1].astype(int).clip(0, H - 1)
                total += int(wall[y, x].sum())
            counts.append(total)
        return counts

    def tilted(self, fore, side):
        return norm(rot_about(rot_about(self.eject, self.right, fore), self.aim, side))


def table_rows():
    t = open(TABLE, encoding='utf-8').read()
    return [(m[0], float(m[1]), float(m[2])) for m in re.findall(r'// (\S+): ([+-][\d.]+) fore/aft, ([+-][\d.]+) sideways', t)]


def search(g):
    """The tilt through the least wall (side and front together); among those within 5% (and 3 points) of it, the smallest."""
    results = []
    for fore in np.arange(-18.0, 18.01, 1.5):
        for side in np.arange(-6.0, 6.01, 1.5):
            c = g.through(g.tilted(fore, side))
            results.append((sum(c), float(np.hypot(fore, side)), float(fore), float(side), c))
    least = min(r[0] for r in results)
    near = [r for r in results if r[0] <= least * 1.05 + 3]
    return min(near, key=lambda r: (r[1], r[0]))


if __name__ == '__main__':
    if sys.argv[1] == '--search':
        table = {f: (fo, si) for f, fo, si in table_rows()}
        folders = sys.argv[2:] or sorted(os.listdir(DATA))
        for folder in folders:
            try:
                g = Gun(folder)
            except Exception as e:  # a folder without a model, an unreadable one
                print(f'{folder}: unreadable ({type(e).__name__})', flush=True)
                continue
            if not g.ok:
                print(f'{folder}: no body or no magazine drawn', flush=True)
                continue
            a = g.through(g.eject)
            best = search(g)
            t = ''
            if folder in table:
                b = g.through(g.tilted(*table[folder]))
                t = f'; table {table[folder][0]:+.1f}/{table[folder][1]:+.1f} wall {sum(b)}'
            print(f'{folder}: analysed wall {sum(a)} (side {a[0]}, front {a[1]}); least {best[2]:+.1f}/{best[3]:+.1f} wall {best[0]} '
                  f'(side {best[4][0]}, front {best[4][1]}){t}', flush=True)
    elif sys.argv[1] == '--table':
        worse = 0
        for folder, fore, side in table_rows():
            g = Gun(folder)
            if not g.ok:
                print(f'{folder}: no body or no magazine drawn')
                continue
            a, b = g.through(g.eject), g.through(g.tilted(fore, side))
            flag = '  <-- THROUGH A WALL' if sum(b) > sum(a) + 20 and sum(b) > 1.3 * sum(a) else ''
            worse += bool(flag)
            print(f'{folder:45s} {fore:+5.1f}/{side:+4.1f}: analysed side {a[0]:5d} front {a[1]:5d}, table side {b[0]:5d} front {b[1]:5d}{flag}')
        print(f'{worse} table lines through more wall than the analysed line')
    else:
        g = Gun(sys.argv[1])
        fore = float(sys.argv[2]) if len(sys.argv) > 2 else 0.0
        side = float(sys.argv[3]) if len(sys.argv) > 3 else 0.0
        a, b = g.through(g.eject), g.through(g.tilted(fore, side))
        print(f'{sys.argv[1]}: analysed side {a[0]} front {a[1]}; at {fore:+.1f}/{side:+.1f} side {b[0]} front {b[1]} (points on the gun off the well, over {len(FRACTIONS)} fractions)')
