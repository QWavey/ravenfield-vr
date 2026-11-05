"""What a weapon mod's animation clip really does: how far each part turns and moves, decoded from the bundle.

The analyser only sees what its sampler plays, so what it missed is invisible in its own reports. This reads the clip itself:
the streamed, dense and constant curves of a Unity muscle clip, bound to transforms by the crc of their path. It found the QJB201's
real feed cover (the analyser had its latch) and the AS Val's bolt (a fire clip the sampler never played).

    python -I tools/bundles/clip_motion.py "<bundle.rfc>"                    the clips, largest first
    python -I tools/bundles/clip_motion.py "<bundle.rfc>" r1                 what clip r1 turns and moves
    python -I tools/bundles/clip_motion.py "<bundle.rfc>" r1 --curve topcover  and the position curve of the parts matching
    python -I tools/bundles/clip_motion.py "<bundle.rfc>" --part "Cap$"      parts by name: transform, parent, mesh box

Needs UnityPy. Bundles: steamapps/workshop/content/636480/<id>/*.rfc.
"""
import argparse
import re
import struct
import sys
import zlib

import UnityPy

sys.stdout.reconfigure(encoding="utf-8", errors="replace")

TRANSFORM = 4
POSITION, EULER, SCALE = 1, 4, 3
HUMAN = re.compile(r"hand|arm|finger|thumb|index|middle|ring|pinky|shoulder|clav|spine|neck|head|camera|bone|ik", re.I)
ROUND = re.compile(r"bullet|bulet|round|shell", re.I)


def path_names(env):
    """Every suffix of every transform path by its crc: a binding's path is relative to the Animator, wherever that sits."""
    names = {}

    def walk(transform, prefix):
        go = transform.m_GameObject.read()
        path = (prefix + "/" + go.m_Name) if prefix else go.m_Name
        parts = path.split("/")
        for i in range(len(parts)):
            suffix = "/".join(parts[i:])
            names.setdefault(zlib.crc32(suffix.encode("utf-8")), suffix)
        for child in transform.m_Children:
            walk(child.read(), path)

    for o in env.objects:
        if o.type.name == "Transform":
            t = o.read()
            if not (t.m_Father and t.m_Father.path_id):
                try:
                    walk(t, "")
                except Exception:
                    pass
    return names


def curves(clip):
    """Curve index -> [(time, value)]: streamed first, then dense, then constant. A streamed key's last coefficient is its value."""
    series = {}
    streamed = clip["m_StreamedClip"]
    words = streamed["data"]
    raw = struct.pack("<%dI" % len(words), *words)
    count = streamed.get("curveCount", 0)
    pos = 0
    while pos + 8 <= len(raw):
        time, keys = struct.unpack_from("<fI", raw, pos)
        pos += 8
        for _ in range(keys):
            index, _a, _b, _c, value = struct.unpack_from("<I4f", raw, pos)
            pos += 20
            series.setdefault(index, []).append((time, value))
    dense = clip["m_DenseClip"]
    width = dense["m_CurveCount"]
    if width:
        for frame in range(dense["m_FrameCount"]):
            for i in range(width):
                series.setdefault(count + i, []).append((frame / dense["m_SampleRate"], dense["m_SampleArray"][frame * width + i]))
    for i, value in enumerate(clip["m_ConstantClip"]["data"]):
        series.setdefault(count + width + i, []).append((0.0, value))
    return series


def spread(series):
    """The greatest change of any axis from its first key, and when."""
    best, at = 0.0, 0.0
    for keys in series:
        keys = [k for k in keys if k[0] > -1e30]
        if len(keys) >= 2:
            first = keys[0][1]
            for time, value in keys:
                if abs(value - first) > best:
                    best, at = abs(value - first), time
    return best, at


def show_clip(env, name, curve):
    names = path_names(env)
    for o in env.objects:
        if o.type.name != "AnimationClip" or o.read().m_Name != name:
            continue
        tree = o.read_typetree()
        clip = tree["m_MuscleClip"]["m_Clip"]
        series = curves(clip.get("data", clip))
        turned, moved = [], []
        index = 0
        for b in tree["m_ClipBindingConstant"]["genericBindings"]:
            attribute, on_transform = b["attribute"], b.get("typeID", 0) == TRANSFORM
            width = 3 if on_transform and attribute in (POSITION, EULER, SCALE) else 1
            part = names.get(b["path"], hex(b["path"]))
            if on_transform and attribute in (POSITION, EULER):
                axes = [series.get(index + k, []) for k in range(3)]
                amount, at = spread(axes)
                (moved if attribute == POSITION else turned).append((amount, at, part))
                if attribute == POSITION and curve and re.search(curve, part):
                    for k in range(3):
                        print(f"  {'xyz'[k]} {part[-40:]}: {[(round(t, 3), round(v, 4)) for t, v in axes[k] if t > -1e30][:40]}")
            index += width
        print(f"{name}: {len(series)} curves")
        for amount, at, part in sorted(turned, reverse=True):
            if amount >= 8 and not HUMAN.search(part) and not ROUND.search(part):
                print(f"  turned {amount:6.1f} deg at {at:5.2f} s  {part[-70:]}")
        for amount, at, part in sorted(moved, reverse=True):
            if amount >= 0.003 and not HUMAN.search(part) and not ROUND.search(part):
                print(f"  moved  {amount * 100:6.1f} cm  at {at:5.2f} s  {part[-70:]}")
        return
    print("no clip named", name)


def show_parts(env, pattern):
    want = re.compile(pattern)
    for o in env.objects:
        if o.type.name != "GameObject":
            continue
        go = o.read()
        if not want.search(go.m_Name):
            continue
        transform, mesh = None, None
        for c in go.m_Components:
            ptr = c.component if hasattr(c, "component") else c
            try:
                r = ptr.read()
            except Exception:
                continue
            kind = r.__class__.__name__
            if kind == "Transform":
                transform = r
            elif kind in ("MeshFilter", "SkinnedMeshRenderer") and r.m_Mesh and r.m_Mesh.path_id:
                mesh = r.m_Mesh.read()
        if transform is None:
            continue
        p, s = transform.m_LocalPosition, transform.m_LocalScale
        parent = transform.m_Father.read().m_GameObject.read().m_Name if transform.m_Father and transform.m_Father.path_id else None
        print(f"{go.m_Name}: at {[round(x, 4) for x in (p.x, p.y, p.z)]}, scale {[round(x, 3) for x in (s.x, s.y, s.z)]}, under {parent}")
        if mesh is not None:
            c, e = mesh.m_LocalAABB.m_Center, mesh.m_LocalAABB.m_Extent
            print(f"  mesh {mesh.m_Name}: centre {[round(x, 4) for x in (c.x, c.y, c.z)]}, half size {[round(x, 4) for x in (e.x, e.y, e.z)]}")


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("bundle")
    parser.add_argument("clip", nargs="?")
    parser.add_argument("--curve", help="print the position curve of parts matching this")
    parser.add_argument("--part", help="print parts whose name matches this, instead of a clip")
    args = parser.parse_args()
    env = UnityPy.load(args.bundle)
    if args.part:
        show_parts(env, args.part)
    elif args.clip:
        show_clip(env, args.clip, args.curve)
    else:
        rows = sorted(((getattr(c, "m_MuscleClipSize", 0), c.m_Name) for c in (o.read() for o in env.objects if o.type.name == "AnimationClip")), reverse=True)
        print(len(rows), "clips:", ", ".join(n for _, n in rows))


if __name__ == "__main__":
    main()
