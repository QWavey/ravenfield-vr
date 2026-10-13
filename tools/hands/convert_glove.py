"""
Converts Valve's SteamVR glove (steamvr_unity_plugin, BSD-3-Clause; assets/hands/steamvr-glove) into the mod's
glove resource (src/Rig/Glove/glove.bin + textures), in the hand model's palm space:

  origin   the palm centre the weapon analysis uses for a hand (55% of the way from the wrist to the finger roots)
  +Z       toward the finger roots
  +Y       across the knuckles toward the thumb
  palm     facing -X (right hand; the left hand is mirrored at runtime)

The mesh is re-skinned onto the mod's 16 bones (palm + five fingers x three joints). Finger poses come from Valve's
reference poses for this skeleton: the open hand is curl 0, the fist curl 1 (each joint turns about the axis that takes
it from open to fist), plus the relaxed and pointing poses.

Usage: python -I convert_glove.py [<repo root>]
"""
import io
import json
import math
import os
import re
import struct
import sys

import numpy as np

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import fbxparse as F  # noqa: E402

ROOT = os.path.abspath(sys.argv[1]) if len(sys.argv) > 1 else os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
SRC = os.path.join(ROOT, "assets", "hands", "steamvr-glove")
OUT = os.path.join(ROOT, "src", "Rig", "Glove")

FINGERS = ["thumb", "index", "middle", "ring", "pinky"]
# OpenVR hand skeleton order (the reference poses list the bones in this order).
SKELETON = ["Root", "wrist_r",
            "finger_thumb_0_r", "finger_thumb_1_r", "finger_thumb_2_r", "finger_thumb_r_end",
            "finger_index_meta_r", "finger_index_0_r", "finger_index_1_r", "finger_index_2_r", "finger_index_r_end",
            "finger_middle_meta_r", "finger_middle_0_r", "finger_middle_1_r", "finger_middle_2_r", "finger_middle_r_end",
            "finger_ring_meta_r", "finger_ring_0_r", "finger_ring_1_r", "finger_ring_2_r", "finger_ring_r_end",
            "finger_pinky_meta_r", "finger_pinky_0_r", "finger_pinky_1_r", "finger_pinky_2_r", "finger_pinky_r_end",
            "finger_thumb_r_aux", "finger_index_r_aux", "finger_middle_r_aux", "finger_ring_r_aux", "finger_pinky_r_aux"]


def joint_bones(f):
    if f == 0:
        return ["finger_thumb_0_r", "finger_thumb_1_r", "finger_thumb_2_r", "finger_thumb_r_end"]
    n = FINGERS[f]
    return [f"finger_{n}_0_r", f"finger_{n}_1_r", f"finger_{n}_2_r", f"finger_{n}_r_end"]


def our_bone(name):
    """The mod's bone for a glove bone: 0 palm, 1 + finger * 3 + segment."""
    for f in range(5):
        chain = joint_bones(f)
        if name in chain:
            return 1 + f * 3 + min(chain.index(name), 2)
    return 0


# ---- quaternions (x, y, z, w), Unity conventions -------------------------------------------------------------------

def qmul(a, b):
    ax, ay, az, aw = a
    bx, by, bz, bw = b
    return np.array([aw * bx + ax * bw + ay * bz - az * by,
                     aw * by - ax * bz + ay * bw + az * bx,
                     aw * bz + ax * by - ay * bx + az * bw,
                     aw * bw - ax * bx - ay * by - az * bz])


def qinv(q):
    return np.array([-q[0], -q[1], -q[2], q[3]]) / np.dot(q, q)


def qmat(q):
    x, y, z, w = q / np.linalg.norm(q)
    return np.array([[1 - 2 * (y * y + z * z), 2 * (x * y - z * w), 2 * (x * z + y * w)],
                     [2 * (x * y + z * w), 1 - 2 * (x * x + z * z), 2 * (y * z - x * w)],
                     [2 * (x * z - y * w), 2 * (y * z + x * w), 1 - 2 * (x * x + y * y)]])


def matq(m):
    t = np.trace(m)
    if t > 0:
        s = math.sqrt(t + 1.0) * 2
        q = [(m[2, 1] - m[1, 2]) / s, (m[0, 2] - m[2, 0]) / s, (m[1, 0] - m[0, 1]) / s, 0.25 * s]
    elif m[0, 0] > m[1, 1] and m[0, 0] > m[2, 2]:
        s = math.sqrt(1.0 + m[0, 0] - m[1, 1] - m[2, 2]) * 2
        q = [0.25 * s, (m[0, 1] + m[1, 0]) / s, (m[0, 2] + m[2, 0]) / s, (m[2, 1] - m[1, 2]) / s]
    elif m[1, 1] > m[2, 2]:
        s = math.sqrt(1.0 + m[1, 1] - m[0, 0] - m[2, 2]) * 2
        q = [(m[0, 1] + m[1, 0]) / s, 0.25 * s, (m[1, 2] + m[2, 1]) / s, (m[0, 2] - m[2, 0]) / s]
    else:
        s = math.sqrt(1.0 + m[2, 2] - m[0, 0] - m[1, 1]) * 2
        q = [(m[0, 2] + m[2, 0]) / s, (m[1, 2] + m[2, 1]) / s, 0.25 * s, (m[1, 0] - m[0, 1]) / s]
    q = np.array(q)
    q /= np.linalg.norm(q)
    return q if q[3] >= 0 else -q


def axis_angle(q):
    q = q / np.linalg.norm(q)
    if q[3] < 0:
        q = -q
    angle = 2 * math.degrees(math.acos(min(1.0, q[3])))
    s = math.sqrt(max(1e-12, 1 - q[3] * q[3]))
    return q[:3] / s, angle


def trs(pos, q):
    m = np.eye(4)
    m[:3, :3] = qmat(q)
    m[:3, 3] = pos
    return m


def rigid(m):
    """Position and rotation of a 4x4 (any scale removed)."""
    r = m[:3, :3].copy()
    for i in range(3):
        r[:, i] /= np.linalg.norm(r[:, i])
    return m[:3, 3].copy(), matq(r)


# ---- reading -------------------------------------------------------------------------------------------------------

S4 = np.diag([-1.0, 1.0, 1.0, 1.0])  # FBX (right-handed) to Unity (left-handed), as Unity's importer: x -> -x


def load_fbx(path):
    _, root = F.load(path)
    objs = root.find("Objects")
    byid = {o.props[0]: o for o in objs.children}
    kids = {}
    for c in root.find("Connections").findall("C"):
        kids.setdefault(c.props[2], []).append(c.props[1])

    def name(o):
        return o.props[1].split("\x00")[0]

    geo = [o for o in objs.children if o.name == "Geometry"][0]
    bones = {}
    mesh_global = None
    for o in objs.children:
        if o.name == "Deformer" and o.props[2] == "Cluster":
            model = [byid[k] for k in kids.get(o.props[0], []) if k in byid and byid[k].name == "Model"]
            if not model:
                continue
            t = o.find("Transform").props[0].reshape(4, 4).T
            tl = o.find("TransformLink").props[0].reshape(4, 4).T
            idx = o.find("Indexes")
            w = o.find("Weights")
            bones[name(model[0])] = {
                "bind": S4 @ tl @ S4,
                "indexes": idx.props[0] if idx is not None else np.zeros(0, int),
                "weights": w.props[0] if w is not None else np.zeros(0),
            }
            g = tl @ t
            mesh_global = g if mesh_global is None else mesh_global
    parents = {}
    models = {o.props[0]: name(o) for o in objs.children if o.name == "Model"}
    for c in root.find("Connections").findall("C"):
        if c.props[0] == "OO" and c.props[1] in models and c.props[2] in models:
            parents[models[c.props[1]]] = models[c.props[2]]
    return geo, bones, S4 @ mesh_global @ S4, mesh_global, parents


def load_pose(path):
    """Right hand of a SteamVR_Skeleton_Pose asset: [(position, rotation)] per skeleton bone (Unity, parent space)."""
    text = open(path, encoding="utf-8").read()
    right = text[text.index("rightHand:"):]
    vec = re.compile(r"\{x: ([-\d.e]+), y: ([-\d.e]+), z: ([-\d.e]+)(?:, w: ([-\d.e]+))?\}")
    pos_block = right[right.index("bonePositions:"):right.index("boneRotations:")]
    rot_block = right[right.index("boneRotations:"):]
    positions = [np.array([float(m.group(1)), float(m.group(2)), float(m.group(3))]) for m in vec.finditer(pos_block)]
    rotations = [np.array([float(m.group(1)), float(m.group(2)), float(m.group(3)), float(m.group(4))]) for m in vec.finditer(rot_block)][:len(positions)]
    assert len(positions) == 31 and len(rotations) == 31, (path, len(positions), len(rotations))
    return list(zip(positions, rotations))


def main():
    geo, bones, mesh_global_u, mesh_global_fbx, parents = load_fbx(os.path.join(SRC, "vr_glove_right_model_slim.fbx"))
    world = {n: b["bind"] for n, b in bones.items()}

    # The reference poses must describe the same skeleton as the mesh's bind pose (checks the axis conversion).
    bind_pose = load_pose(os.path.join(SRC, "ReferencePose_BindPose.asset"))
    worst_p, worst_r = 0.0, 0.0
    for i, n in enumerate(SKELETON):
        p = parents.get(n)
        if p is None or p == "Root" or n == "wrist_r":
            continue
        local = np.linalg.inv(world[p]) @ world[n]
        lp, lq = rigid(local)
        yp, yq = bind_pose[i]
        worst_p = max(worst_p, np.linalg.norm(lp - yp))
        worst_r = max(worst_r, axis_angle(qmul(qinv(lq), yq / np.linalg.norm(yq)))[1])
    print(f"bind pose vs mesh skeleton: {worst_p * 1000:.2f} mm, {worst_r:.2f} deg")

    # Palm frame, as the weapon analysis reads a hand: wrist to the finger roots, across the knuckles toward the thumb.
    pos = {n: m[:3, 3] for n, m in world.items()}
    wrist = pos["wrist_r"]
    roots = np.mean([pos[f"finger_{n}_0_r"] for n in FINGERS[1:]], axis=0)
    z = roots - wrist
    z /= np.linalg.norm(z)
    across = pos["finger_index_0_r"] - pos["finger_pinky_0_r"]
    across -= z * np.dot(across, z)
    if np.dot(across, pos["finger_thumb_0_r"] - wrist) < 0:
        across = -across
    y = across / np.linalg.norm(across)
    x = np.cross(y, z)
    palm = np.eye(4)
    palm[:3, 0], palm[:3, 1], palm[:3, 2] = x, y, z
    palm[:3, 3] = wrist + (roots - wrist) * 0.55
    to_palm = np.linalg.inv(palm)

    def P(m):
        return to_palm @ m

    # ---- joints: bind frames, child offsets ------------------------------------------------------------------------
    joints = []
    for f in range(5):
        chain = joint_bones(f)
        segs = []
        for s in range(3):
            bp, bq = rigid(P(world[chain[s]]))
            np_, _ = rigid(P(world[chain[s + 1]]))
            child = qmat(bq).T @ (np_ - bp)
            segs.append({"pos": bp, "bind": bq, "child": child})
        joints.append(segs)

    # ---- poses: forward kinematics from the wrist (fixed where the mesh has it) -----------------------------------
    def pose_locals(path):
        pose = load_pose(os.path.join(SRC, path))
        wp = P(world["wrist_r"])
        glob = {"wrist_r": wp}
        for i, n in enumerate(SKELETON):
            if i < 2:
                continue
            p = parents[n]
            if p not in glob:
                continue
            glob[n] = glob[p] @ trs(pose[i][0], pose[i][1])
        out = []
        for f in range(5):
            chain = joint_bones(f)
            segs = []
            prev = np.eye(3)
            for s in range(3):
                _, wq = rigid(glob[chain[s]])
                local = matq(prev.T @ qmat(wq)) if s > 0 else wq
                prev = qmat(wq)
                segs.append(local)
            out.append(segs)
        tips = {f: rigid(glob[joint_bones(f)[3]])[0] for f in range(5)}
        return out, tips

    poses = {}
    for key, path in (("open", "ReferencePose_OpenHand.asset"), ("fist", "ReferencePose_Fist.asset"),
                      ("relaxed", "fallback_relaxed.asset"), ("point", "fallback_point.asset"), ("bind", "ReferencePose_BindPose.asset")):
        poses[key], tips = pose_locals(path)
        if key == "fist":
            print("fist fingertips (palm space, cm):", {FINGERS[f]: np.round(t * 100, 1).tolist() for f, t in tips.items()})

    curls = []
    for f in range(5):
        row = []
        for s in range(3):
            d = qmul(qinv(poses["open"][f][s]), poses["fist"][f][s])
            axis, angle = axis_angle(d)
            row.append((axis, angle))
        curls.append(row)
    print("curl angles (deg):", [[round(a, 1) for _, a in row] for row in curls])

    # ---- mesh ------------------------------------------------------------------------------------------------------
    verts_fbx = geo.find("Vertices").props[0].reshape(-1, 3)
    pvi = geo.find("PolygonVertexIndex").props[0]
    corner_pos = np.where(pvi < 0, -pvi - 1, pvi)
    ne = geo.find("LayerElementNormal")
    normals = ne.find("Normals").props[0].reshape(-1, 3)
    assert ne.find("MappingInformationType").props[0] == "ByPolygonVertex"
    ue = geo.find("LayerElementUV")
    uv = ue.find("UV").props[0].reshape(-1, 2)
    uvi = ue.find("UVIndex").props[0]
    assert len(corner_pos) % 3 == 0 and (np.diff(np.concatenate([[-1], np.where(pvi < 0)[0]])) == 3).all(), "triangles only"

    to_palm_from_mesh = to_palm @ mesh_global_u @ S4  # FBX mesh space -> Unity world -> palm
    hp = np.c_[verts_fbx, np.ones(len(verts_fbx))]
    vpos = (to_palm_from_mesh @ hp.T).T[:, :3]
    nrm = (to_palm_from_mesh[:3, :3] @ normals.T).T
    nrm /= np.linalg.norm(nrm, axis=1, keepdims=True)

    # Skin weights per control point, on the mod's bones.
    w = np.zeros((len(verts_fbx), 16))
    for n, b in bones.items():
        ob = our_bone(n)
        for i, wt in zip(b["indexes"], b["weights"]):
            w[i, ob] += wt
    total = w.sum(axis=1)
    assert (total > 0.5).all(), "unweighted vertices"
    w /= total[:, None]

    key_to_vertex = {}
    vp, vn, vt, vw, vi = [], [], [], [], []
    tris = []
    for c in range(len(corner_pos)):
        p = int(corner_pos[c])
        t = int(uvi[c])
        nk = tuple(np.round(nrm[c], 3))
        k = (p, t, nk)
        idx = key_to_vertex.get(k)
        if idx is None:
            idx = len(vp)
            key_to_vertex[k] = idx
            vp.append(vpos[p])
            vn.append(nrm[c])
            vt.append(uv[t])
            order = np.argsort(-w[p])[:4]
            ws = w[p][order]
            ws = ws / ws.sum()
            vi.append(order)
            vw.append(ws)
        tris.append(idx)
    tris = np.array(tris).reshape(-1, 3)
    vp = np.array(vp)
    vn = np.array(vn)
    # Unity's front faces: the face normal cross(b - a, c - a) along the vertex normals.
    fn = np.cross(vp[tris[:, 1]] - vp[tris[:, 0]], vp[tris[:, 2]] - vp[tris[:, 0]])
    agree = np.sum(fn * (vn[tris[:, 0]] + vn[tris[:, 1]] + vn[tris[:, 2]]), axis=1)
    if (agree < 0).mean() > 0.5:
        tris = tris[:, [0, 2, 1]]
        print("winding flipped")
    print(f"mesh: {len(vp)} vertices, {len(tris)} triangles, bounds {np.round(vp.min(0) * 100, 1)} .. {np.round(vp.max(0) * 100, 1)} cm")

    # ---- measurements the hand model needs (palm skin, finger thickness) ------------------------------------------
    vw_arr = np.array(vw)
    vi_arr = np.array(vi)
    dom = vi_arr[:, 0]
    palm_mask = (dom == 0)
    mid = palm_mask & (np.abs(vp[:, 1]) < 0.02) & (np.abs(vp[:, 2]) < 0.02)
    palm_x = (float(vp[mid, 0].min()), float(vp[mid, 0].max()))
    wide = palm_mask & (np.abs(vp[:, 0]) < 0.01) & (np.abs(vp[:, 2]) < 0.02)
    palm_y = (float(vp[wide, 1].min()), float(vp[wide, 1].max()))
    print(f"palm: x {palm_x[0] * 100:.2f} .. {palm_x[1] * 100:.2f} cm (palm side .. back), y {palm_y[0] * 100:.2f} .. {palm_y[1] * 100:.2f} cm")
    # Each finger segment: how far its skin reaches from the bone toward where the segment closes (its pad) and on
    # average all round; the hand model tests contact at the pad.
    radii, pads, pad_dirs = [], [], []
    for f in range(5):
        row_r, row_p, row_d = [], [], []
        for s in range(3):
            j = joints[f][s]
            a = j["pos"]
            rot = qmat(j["bind"])
            ab = rot @ j["child"]
            # Curling turns the child about the curl axis (open pose frame ~ bind frame for this): it moves toward
            # axis x child, the pad side.
            axis, _ = curls[f][s]
            pad_local = np.cross(axis, j["child"])
            pad_local /= np.linalg.norm(pad_local)
            pad_world = rot @ pad_local
            t = (vp - a) @ ab / np.dot(ab, ab)
            sel = (dom == 1 + f * 3 + s) & (t > 0.25) & (t < 0.75)
            r = (vp - a)[sel] - np.outer(t[sel], ab)
            d = np.linalg.norm(r, axis=1)
            along_pad = r @ pad_world
            row_r.append(float(np.median(d)) if len(d) else 0.008)
            row_p.append(float(np.percentile(along_pad[along_pad > 0], 80)) if (along_pad > 0).sum() > 3 else 0.008)
            row_d.append(pad_local)
        radii.append(row_r)
        pads.append(row_p)
        pad_dirs.append(row_d)
    print("finger radii (mm):", [[round(r * 1000, 1) for r in row] for row in radii])
    print("finger pads (mm):", [[round(r * 1000, 1) for r in row] for row in pads])

    # The palm's skin: a grid of surface points on the palm side (the hand model rests the palm on held things there).
    not_thumb = ~np.isin(dom, [1, 2, 3])
    grid = []
    for z0 in (-0.035, -0.012, 0.012, 0.034):
        for y0 in (-0.028, 0.0, 0.028):
            near = not_thumb & (vp[:, 0] < 0) & (np.hypot(vp[:, 1] - y0, vp[:, 2] - z0) < 0.007)
            x0 = float(vp[near, 0].min()) if near.any() else palm_x[0]
            grid.append((x0, y0, z0))
    print("palm skin (cm):", [tuple(round(v * 100, 1) for v in g) for g in grid])
    body = not_thumb & (dom == 0)
    box = (float(np.percentile(vp[body, 0], 2)), float(np.percentile(vp[body, 0], 98)),
           float(np.percentile(vp[body, 1], 2)), float(np.percentile(vp[body, 1], 98)),
           float(vp[body, 2].min()), float(np.percentile(vp[body, 2], 98)))
    print("palm box (cm):", [round(v * 100, 1) for v in box])

    # ---- write ------------------------------------------------------------------------------------------------------
    os.makedirs(OUT, exist_ok=True)
    buf = io.BytesIO()
    buf.write(b"RFVG")
    buf.write(struct.pack("<iii", 2, len(vp), tris.size))
    buf.write(np.asarray(vp, "<f4").tobytes())
    buf.write(np.asarray(vn, "<f4").tobytes())
    buf.write(np.asarray(vt, "<f4").tobytes())
    buf.write(np.asarray(vi_arr, "<u1").tobytes())
    buf.write(np.asarray(vw_arr, "<f4").tobytes())
    buf.write(np.asarray(tris.reshape(-1), "<u2").tobytes())
    # Joints: position, bind rotation (palm space for the first joint, else local), child offset, curl axis + angle,
    # radius, and the open / fist / relaxed / point local rotations.
    for f in range(5):
        prev = None
        for s in range(3):
            j = joints[f][s]
            bind_local = j["bind"] if s == 0 else matq(qmat(joints[f][s - 1]["bind"]).T @ qmat(j["bind"]))
            axis, angle = curls[f][s]
            buf.write(np.asarray(j["pos"], "<f4").tobytes())
            buf.write(np.asarray(bind_local, "<f4").tobytes())
            buf.write(np.asarray(j["child"], "<f4").tobytes())
            buf.write(np.asarray(axis, "<f4").tobytes())
            buf.write(struct.pack("<fff", angle, radii[f][s], pads[f][s]))
            buf.write(np.asarray(pad_dirs[f][s], "<f4").tobytes())
            for key in ("open", "fist", "relaxed", "point"):
                buf.write(np.asarray(poses[key][f][s], "<f4").tobytes())
    for g in grid:
        buf.write(struct.pack("<fff", *g))
    buf.write(struct.pack("<ffffff", *box))
    open(os.path.join(OUT, "glove.bin"), "wb").write(buf.getvalue())

    from PIL import Image
    Image.open(os.path.join(SRC, "vr_glove_color.jpg")).convert("RGB").save(os.path.join(OUT, "glove_color.jpg"), quality=92)
    Image.open(os.path.join(SRC, "vr_glove_normal.png")).convert("RGB").save(os.path.join(OUT, "glove_normal.jpg"), quality=95)
    summary = {
        "vertices": len(vp), "triangles": int(len(tris)),
        "palm_x_cm": [round(v * 100, 2) for v in palm_x], "palm_y_cm": [round(v * 100, 2) for v in palm_y],
        "joints_cm": [[np.round(j["pos"] * 100, 2).tolist() for j in row] for row in joints],
        "child_cm": [[np.round(j["child"] * 100, 2).tolist() for j in row] for row in joints],
        "curl_deg": [[round(a, 1) for _, a in row] for row in curls],
        "radii_mm": [[round(r * 1000, 1) for r in row] for row in radii],
        "pads_mm": [[round(r * 1000, 1) for r in row] for row in pads],
        "palm_skin_cm": [[round(v * 100, 2) for v in g] for g in grid],
        "palm_box_cm": [round(v * 100, 2) for v in box],
    }
    open(os.path.join(OUT, "glove.json"), "w").write(json.dumps(summary, indent=1))
    print("written", os.path.join(OUT, "glove.bin"), os.path.getsize(os.path.join(OUT, "glove.bin")), "bytes")


if __name__ == "__main__":
    main()
