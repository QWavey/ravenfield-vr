"""Offline audit over the export: how far each gun's grip point and support point (where the hands hold it, rig.json
'grip' and 'support') lie from the gun's own surface (its drawn triangles, magazine and stand-in parts left out). A point
far outside the model means the hand holds air beside the gun; one deep inside means the hand sinks into it. The grip
point is the palm's centre, so 1-3 cm off the grip's surface is normal; over 4 cm is worth a look. Firearms only
(throwables and melee are held by their own poses). Lists the worst of each. Usage: hold_points_audit.py [gun dir name ...] (all guns when none is given)"""
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


def closest_on_triangles(p, tri):
    """The closest point to p on each triangle (n, 3, 3) -> (n, 3): Ericson's region test, vectorised."""
    a, b, c = tri[:, 0], tri[:, 1], tri[:, 2]
    ab, ac, ap = b - a, c - a, p - a
    d1, d2 = np.einsum('ij,ij->i', ab, ap), np.einsum('ij,ij->i', ac, ap)
    bp = p - b
    d3, d4 = np.einsum('ij,ij->i', ab, bp), np.einsum('ij,ij->i', ac, bp)
    cp = p - c
    d5, d6 = np.einsum('ij,ij->i', ab, cp), np.einsum('ij,ij->i', ac, cp)
    va = d3 * d6 - d5 * d4
    vb = d5 * d2 - d1 * d6
    vc = d1 * d4 - d3 * d2
    out = np.empty_like(a)
    done = np.zeros(len(a), bool)

    def put(mask, value):
        nonlocal done
        m = mask & ~done
        out[m] = value[m] if value.ndim == 2 else value
        done |= m

    put((d1 <= 0) & (d2 <= 0), a)
    put((d3 >= 0) & (d4 <= d3), b)
    put((d6 >= 0) & (d5 <= d6), c)
    with np.errstate(divide='ignore', invalid='ignore'):
        v = d1 / (d1 - d3)
        put((vc <= 0) & (d1 >= 0) & (d3 <= 0), a + v[:, None] * ab)
        w = d2 / (d2 - d6)
        put((vb <= 0) & (d2 >= 0) & (d6 <= 0), a + w[:, None] * ac)
        w2 = (d4 - d3) / ((d4 - d3) + (d5 - d6))
        put((va <= 0) & ((d4 - d3) >= 0) & ((d5 - d6) >= 0), b + w2[:, None] * (c - b))
        denom = 1.0 / (va + vb + vc)
        v3, w3 = vb * denom, vc * denom
        put(np.ones(len(a), bool), a + ab * v3[:, None] + ac * w3[:, None])
    return out


def surface_distance(p, tri):
    """Distance from p to the nearest triangle, and whether p is inside the model (a ray along +x crosses it an odd
    number of times, the ray tilted a hair off x; a model with holes can fool this, so it is a hint)."""
    if not len(tri):
        return float('nan'), False
    q = closest_on_triangles(p, tri)
    dist = float(np.sqrt(((q - p) ** 2).sum(axis=1)).min())
    # Inside: crossings of the ray p + t*d, t > 0 (Moller-Trumbore with the fixed direction).
    a, b, c = tri[:, 0], tri[:, 1], tri[:, 2]
    e1, e2 = b - a, c - a
    d = np.array([1.0, 0.0013, 0.0007]) / np.linalg.norm([1.0, 0.0013, 0.0007])  # (tilted: never along a shared edge)
    h = np.cross(d, e2)
    det = np.einsum('ij,ij->i', e1, h)
    ok = np.abs(det) > 1e-12
    inv = np.where(ok, 1.0 / np.where(ok, det, 1.0), 0.0)
    s = p - a
    u = inv * np.einsum('ij,ij->i', s, h)
    qv = np.cross(s, e1)
    v = inv * (qv @ d)
    t = inv * np.einsum('ij,ij->i', e2, qv)
    hits = ok & (u >= 0) & (v >= 0) & (u + v <= 1) & (t > 1e-6)
    return dist, bool(hits.sum() % 2)


def audit(name):
    d = os.path.join(DATA, name)
    rig = json.load(open(os.path.join(d, 'rig.json'), encoding='utf-8'))
    mags = [p for p in [rig.get('magazine')] + list(rig.get('magazineParts', [])) if p]
    tris = [n.triangles() for n in glb.load(os.path.join(d, 'model.glb'))
            if not n.stand_in and not any(n.path == m or n.path.startswith(m + '/') for m in mags)]
    tri = np.concatenate([t for t in tris if len(t)]) if tris else np.zeros((0, 3, 3))
    out = {}
    for key in ('grip', 'support'):
        if key == 'support' and not rig.get('hasSupport'):
            continue
        p = rig.get(key)
        if p:
            out[key] = surface_distance(np.array(p, float), tri)
    return rig, out


if __name__ == '__main__':
    names = sys.argv[1:] or sorted(os.path.basename(x) for x in glob.glob(os.path.join(DATA, '*')))
    rows = {'grip': [], 'support': []}
    for n in names:
        try:
            rig = json.load(open(os.path.join(DATA, n, 'rig.json'), encoding='utf-8'))
            if rig.get('action') in ('Throwable', 'Melee', 'NoAmmo'):
                continue  # held by their own poses, not a grip and a support point
            rig, res = audit(n)
        except Exception as e:  # one gun's export failing must not stop the rest
            print(f'{n}: failed: {e!r}')
            continue
        for key, (dist, inside) in res.items():
            rows[key].append((dist, inside, n, rig.get('isPistol'), rig.get('action')))
    for key in ('grip', 'support'):
        r = rows[key]
        outside = sorted((x for x in r if not x[1]), reverse=True)
        print(f'{key}: {len(r)} guns; outside the model: {len(outside)}, of them over 2 cm: {sum(1 for x in outside if x[0] > 0.02)}, over 4 cm: {sum(1 for x in outside if x[0] > 0.04)}')
        for dist, inside, n, pistol, action in outside[:15]:
            print(f'    {dist * 100:5.1f} cm off the surface  {"pistol " if pistol else "       "}{str(action)[:11]:11s} {n[:60]}')
