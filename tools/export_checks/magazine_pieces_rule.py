"""Offline: three magazine faults the export shows, each found in the 2026-10-29 sweep and fixed in the analysis or at runtime.
(1) copies: a drawn part named as a magazine, the magazine's size within 12% and a magazine's length or more from its seat
(the K13's spare behind the stock, grouped with the gun through it: WeaponAnalyzer.ParkedAmmo's copy rule hides it now);
(2) detached: pieces under the magazine part 5 cm or more apart from its body (the largest mesh and all touching it within
2 cm) and lighter than it, not on a launcher (the PKM/PKP/Type 80 box's belt links, the MK46's: DetachedMagazinePieces);
(3) box: the analysed magazine box against the drawn magazine (its centre off by 2 cm or more): what MagazineProp's
AdoptCopyBounds corrects in game from the copy (the ARS guns, the G BRN180 EO SS). An export made after those changes should
list (1) and (2) no more; (3) stays, as the correction is made at runtime. Usage: magazine_pieces_rule.py [gun dir ...]"""
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
# WeaponAnalyzer's MagName / NotMagName / NotParkedName, as far as a magazine's own name goes.
MAG = re.compile(r"(^|[^a-z])(mag(azine?)?s?($|[^a-z])|clip|drum|cargador|chargeur)|弹匣|弹夹|弹鼓|магазин", re.I)
NOT_MAG = re.compile(r"release|catch|button|latch|well|housing|lever|pouch|holder|switch|key|handle|grip|in_?gun|chamber|"
                     r"glass|lens|scope|sight|optic|laser|flash ?light", re.I)


def gap(a, b):
    return float(np.linalg.norm(np.maximum(0, np.maximum(a[0] - b[1], b[0] - a[1]))))


def touch(a, b, margin=0.02):
    return bool(np.all(a[0] - margin <= b[1]) and np.all(b[0] - margin <= a[1]))


def vol(b):
    return float(np.prod(np.maximum(b[1] - b[0], 0.002)))


def rotate(q, v):
    x, y, z, w = q
    u = np.array([x, y, z])
    v = np.asarray(v, float)
    return v + 2.0 * np.cross(u, np.cross(u, v) + w * v)


names = sys.argv[1:] or sorted(os.path.basename(x) for x in glob.glob(os.path.join(DATA, '*')))
copies, detached, boxes, parked = [], [], [], []
for n in names:
    try:
        rig = json.load(open(os.path.join(DATA, n, 'rig.json'), encoding='utf-8'))
        nodes = [x for x in glb.load(os.path.join(DATA, n, 'model.glb')) if not x.stand_in and len(x.triangles())]
    except Exception as e:
        print(n, 'failed', e)
        continue
    mp = rig.get('magazine')
    size = rig.get('magBoundsSize')
    if not mp or not size:
        continue
    seat = np.asarray(rig['magSeatPos']) + rotate(rig.get('magSeatRot', [0, 0, 0, 1]), rig['magBoundsCenter'])
    dims = np.sort(np.abs(np.asarray(size)))
    mine = [x for x in nodes if x.path == mp or (x.path or '').startswith(mp + '/')]
    # (1) copies of the magazine away from its seat
    if dims[2] > 0.03:
        for x in nodes:
            if x in mine or not MAG.search(x.name.split('/')[-1]) or NOT_MAG.search(x.name.split('/')[-1]):
                continue
            d = np.sort(x.bmax - x.bmin)
            far = np.linalg.norm((x.bmin + x.bmax) / 2 - seat)
            if np.all(np.abs(d - dims) <= dims * 0.12 + 0.004) and far >= max(dims[2], 0.1):
                copies.append((far, n, x.name))
    if not mine:
        continue
    # (2) pieces under the magazine apart from its body
    bx = [(x.bmin, x.bmax) for x in mine]
    seed = max(range(len(bx)), key=lambda k: vol(bx[k]))
    body = {seed}
    grew = True
    while grew:
        grew = False
        for k in range(len(bx)):
            if k not in body and any(touch(bx[k], bx[j]) for j in body):
                body.add(k)
                grew = True
    rest = [k for k in range(len(bx)) if k not in body]
    if rest and rig.get('action') != 'SingleShot':
        g = min(gap(bx[k], bx[j]) for k in rest for j in body)
        if sum(vol(bx[k]) for k in rest) < sum(vol(bx[j]) for j in body) and g >= 0.05:
            detached.append((g, n, len(rest), mine[rest[0]].name))
    # (3) the analysed box against the drawn magazine. Not a magazine parked away from its seat at rest (the M1 GARRET's clip
    # waits a metre off): the drawn part is where it waits, the box at the seat, and they read 107 cm apart for no fault.
    rest_at = rig.get('magRestPos')
    if rest_at is not None and float(np.linalg.norm(np.asarray(rest_at) - np.asarray(rig['magSeatPos']))) > 0.05:
        parked.append(n)
    elif rig.get('action') not in ('ShellFed', 'BreakOpen', 'SingleShot'):
        lo = np.min([b[0] for b in bx], axis=0)
        hi = np.max([b[1] for b in bx], axis=0)
        off = float(np.linalg.norm((lo + hi) / 2 - seat))
        if off >= 0.02:
            boxes.append((off, n, dims[2], float(np.max(hi - lo))))

print(f'(1) copies of the magazine away from its seat: {len(copies)}')
for far, n, name in sorted(copies, reverse=True):
    print(f'    {far * 100:5.0f} cm from the seat  {n}  {name}')
print(f'(2) pieces under the magazine apart from its body: {len(detached)}')
for g, n, k, name in sorted(detached, reverse=True):
    print(f'    {g * 100:5.0f} cm off, {k} piece(s)  {n}  e.g. {name}')
print(f'(3) analysed box off the drawn magazine by 2 cm or more: {len(boxes)}'
      + (f' ({len(parked)} parked away from the seat at rest not compared)' if parked else ''))
for off, n, a, b in sorted(boxes, reverse=True):
    print(f'    {off * 100:5.1f} cm  (analysed {a * 100:.0f} cm long, drawn {b * 100:.0f})  {n}')
