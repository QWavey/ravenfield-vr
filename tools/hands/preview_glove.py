"""
Renders src/Rig/Glove/glove.bin with its textures (software rasteriser, numpy + PIL), posed by the same joint data the
mod uses: python -I preview_glove.py <repo root> <out.png> [curl ...]. Checks the converter without starting the game.
"""
import math
import os
import struct
import sys

import numpy as np
from PIL import Image

ROOT = os.path.abspath(sys.argv[1])
OUT = sys.argv[2]
GLOVE = os.path.join(ROOT, "src", "Rig", "Glove")


def qmat(q):
    x, y, z, w = q / np.linalg.norm(q)
    return np.array([[1 - 2 * (y * y + z * z), 2 * (x * y - z * w), 2 * (x * z + y * w)],
                     [2 * (x * y + z * w), 1 - 2 * (x * x + z * z), 2 * (y * z - x * w)],
                     [2 * (x * z - y * w), 2 * (y * z + x * w), 1 - 2 * (x * x + y * y)]])


def axis_mat(axis, deg):
    a = math.radians(deg)
    x, y, z = axis / np.linalg.norm(axis)
    c, s = math.cos(a), math.sin(a)
    return np.array([[c + x * x * (1 - c), x * y * (1 - c) - z * s, x * z * (1 - c) + y * s],
                     [y * x * (1 - c) + z * s, c + y * y * (1 - c), y * z * (1 - c) - x * s],
                     [z * x * (1 - c) - y * s, z * y * (1 - c) + x * s, c + z * z * (1 - c)]])


def load():
    d = open(os.path.join(GLOVE, "glove.bin"), "rb").read()
    assert d[:4] == b"RFVG"
    ver, nv, ni = struct.unpack_from("<iii", d, 4)
    o = 16

    def take(dt, count):
        nonlocal o
        a = np.frombuffer(d, dt, count, o)
        o += a.nbytes
        return a

    pos = take("<f4", nv * 3).reshape(-1, 3)
    nrm = take("<f4", nv * 3).reshape(-1, 3)
    uv = take("<f4", nv * 2).reshape(-1, 2)
    bi = take("<u1", nv * 4).reshape(-1, 4)
    bw = take("<f4", nv * 4).reshape(-1, 4)
    tri = take("<u2", ni).reshape(-1, 3).astype(int)
    joints = []
    for f in range(5):
        row = []
        for s in range(3):
            p = take("<f4", 3)
            bind = take("<f4", 4)
            child = take("<f4", 3)
            axis = take("<f4", 3)
            angle, radius, pad = take("<f4", 3)
            pad_dir = take("<f4", 3)
            poses = {k: take("<f4", 4) for k in ("open", "fist", "relaxed", "point")}
            row.append(dict(pos=p, bind=bind, child=child, axis=axis, angle=angle, radius=radius, poses=poses))
        joints.append(row)
    return pos, nrm, uv, bi, bw, tri, joints


def bone_mats(joints, curls, pose=None):
    """World (palm space) 4x4 of the 16 bones: at bind, and posed (open pose + curl about the curl axis)."""
    bind = [np.eye(4)]
    posed = [np.eye(4)]
    for f in range(5):
        pb = np.eye(4)
        pp = np.eye(4)
        for s in range(3):
            j = joints[f][s]
            lb = np.eye(4)
            lb[:3, :3] = qmat(j["bind"])
            lp = np.eye(4)
            base = j["poses"][pose] if pose else j["poses"]["open"]
            lp[:3, :3] = qmat(base) @ (axis_mat(j["axis"], j["angle"] * curls[f]) if not pose else np.eye(3))
            if s == 0:
                lb[:3, 3] = j["pos"]
                lp[:3, 3] = j["pos"]
            else:
                prev = joints[f][s - 1]["child"]
                lb[:3, 3] = prev
                lp[:3, 3] = prev
            pb = pb @ lb if s else lb
            pp = pp @ lp if s else lp
            bind.append(pb.copy())
            posed.append(pp.copy())
    return bind, posed


def skin(pos, nrm, bi, bw, bind, posed):
    skin_m = [posed[i] @ np.linalg.inv(bind[i]) for i in range(16)]
    out_p = np.zeros_like(pos, dtype=np.float64)
    out_n = np.zeros_like(nrm, dtype=np.float64)
    hp = np.c_[pos, np.ones(len(pos))]
    for k in range(4):
        for b in range(16):
            sel = bi[:, k] == b
            if not sel.any():
                continue
            m = skin_m[b]
            out_p[sel] += bw[sel, k][:, None] * (hp[sel] @ m.T)[:, :3]
            out_n[sel] += bw[sel, k][:, None] * (nrm[sel] @ m[:3, :3].T)
    out_n /= np.linalg.norm(out_n, axis=1, keepdims=True)
    return out_p, out_n


def render(pos, nrm, uv, tri, tex, view, size=480, scale=1700.0):
    """Orthographic render looking along view[2] (rows: right, up, forward), lit from the camera's upper left."""
    img = np.zeros((size, size, 3), np.float32) + np.array([0.55, 0.6, 0.66])
    zbuf = np.full((size, size), np.inf)
    p = pos @ view.T
    n = nrm @ view.T
    sx = p[:, 0] * scale + size / 2
    sy = size / 2 - p[:, 1] * scale
    light = np.array([-0.4, 0.5, -0.75])
    light /= np.linalg.norm(light)
    th, tw = tex.shape[:2]
    for a, b, c in tri:
        xs = np.array([sx[a], sx[b], sx[c]])
        ys = np.array([sy[a], sy[b], sy[c]])
        x0, x1 = max(0, int(xs.min())), min(size - 1, int(xs.max()) + 1)
        y0, y1 = max(0, int(ys.min())), min(size - 1, int(ys.max()) + 1)
        if x1 < x0 or y1 < y0:
            continue
        den = (ys[1] - ys[2]) * (xs[0] - xs[2]) + (xs[2] - xs[1]) * (ys[0] - ys[2])
        if abs(den) < 1e-9:
            continue
        gx, gy = np.meshgrid(np.arange(x0, x1 + 1) + 0.5, np.arange(y0, y1 + 1) + 0.5)
        w0 = ((ys[1] - ys[2]) * (gx - xs[2]) + (xs[2] - xs[1]) * (gy - ys[2])) / den
        w1 = ((ys[2] - ys[0]) * (gx - xs[2]) + (xs[0] - xs[2]) * (gy - ys[2])) / den
        w2 = 1 - w0 - w1
        inside = (w0 >= 0) & (w1 >= 0) & (w2 >= 0)
        if not inside.any():
            continue
        z = w0 * p[a, 2] + w1 * p[b, 2] + w2 * p[c, 2]
        zb = zbuf[y0:y1 + 1, x0:x1 + 1]
        draw = inside & (z < zb)
        if not draw.any():
            continue
        u = w0 * uv[a, 0] + w1 * uv[b, 0] + w2 * uv[c, 0]
        v = w0 * uv[a, 1] + w1 * uv[b, 1] + w2 * uv[c, 1]
        nn = w0[..., None] * n[a] + w1[..., None] * n[b] + w2[..., None] * n[c]
        nn /= np.linalg.norm(nn, axis=-1, keepdims=True)
        shade = 0.35 + 0.75 * np.clip(-(nn @ light), 0, 1)
        tu = np.clip((u % 1.0) * tw, 0, tw - 1).astype(int)
        tv = np.clip((1 - (v % 1.0)) * th, 0, th - 1).astype(int)
        col = tex[tv, tu] * shade[..., None]
        zb[draw] = z[draw]
        img[y0:y1 + 1, x0:x1 + 1][draw] = col[draw]
    return np.clip(img * 255, 0, 255).astype(np.uint8)


def main():
    pos, nrm, uv, bi, bw, tri, joints = load()
    tex = np.asarray(Image.open(os.path.join(GLOVE, "glove_color.jpg")).convert("RGB"), np.float32) / 255.0
    curl_sets = [("open", [0, 0, 0, 0, 0], None), ("half", [0.4, 0.5, 0.5, 0.5, 0.5], None), ("fist", [1, 1, 1, 1, 1], None),
                 ("relaxed", None, "relaxed"), ("point", None, "point")]
    # Palm space: +Z fingers, +Y thumb, palm toward -X. Views: back of the hand, palm, thumb side.
    views = {
        "back": np.array([[0, -1, 0], [0, 0, 1], [-1, 0, 0]], float),
        "palm": np.array([[0, 1, 0], [0, 0, 1], [1, 0, 0]], float),
        "side": np.array([[1, 0, 0], [0, 0, 1], [0, -1, 0]], float),
    }
    tiles = []
    for name, curls, pose in curl_sets:
        bind, posed = bone_mats(joints, curls or [0] * 5, pose)
        sp, sn = skin(pos, nrm, bi, bw, bind, posed)
        row = [render(sp, sn, uv, tri, tex, v) for v in views.values()]
        tiles.append(np.concatenate(row, axis=1))
    Image.fromarray(np.concatenate(tiles, axis=0)).save(OUT)
    print("saved", OUT)


if __name__ == "__main__":
    main()
