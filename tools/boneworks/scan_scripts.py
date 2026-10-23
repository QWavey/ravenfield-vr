"""Read-only: find Boneworks MonoBehaviours of the reload classes in every asset file and keep their raw bytes.

A MonoBehaviour's data (Unity 2018.4, no type trees): m_GameObject PPtr (4+8), m_Enabled (1, aligned to 4), m_Script PPtr
(4+8), m_Name (int32 length + bytes, aligned to 4), then the script's own serialized fields in declaration order.
"""
import os, struct, sys, pickle, UnityPy
data, out = sys.argv[1], sys.argv[2]
SCRIPTS = {154: "Gun", 284: "VirtualController", 567: "CylinderSlideGrip", 666: "PumpSlideGrip", 708: "Socket", 1046: "Grip",
           1268: "MagazineData", 1489: "MagazinePlug", 1522: "SlideGrip", 1577: "Magazine", 1674: "AlignPlug", 1754: "PhysHand",
           1949: "HandGunGrip", 2162: "MagazineSocket", 2265: "GunGrip"}
found = {}
files = sorted(f for f in os.listdir(data) if (f.startswith("level") and "." not in f) or f.endswith(".assets"))
for f in files:
    if f == "globalgamemanagers.assets":
        continue
    try:
        env = UnityPy.load(os.path.join(data, f))
    except Exception as e:
        print(f"{f}: load failed {e}", flush=True)
        continue
    counts = {}
    for sf in env.files.values():
        ext = [e.path.lower() for e in getattr(sf, "externals", [])]
        for obj in sf.objects.values():
            if obj.type.name != "MonoBehaviour":
                continue
            raw = obj.get_raw_data()
            if len(raw) < 32:
                continue
            fid, pid = struct.unpack_from("<iq", raw, 16)
            name = SCRIPTS.get(pid)
            if name is None or fid == 0 or fid - 1 >= len(ext) or "globalgamemanagers" not in ext[fid - 1]:
                continue
            counts[name] = counts.get(name, 0) + 1
            lst = found.setdefault(name, [])
            if len(lst) < 40:
                nlen = struct.unpack_from("<i", raw, 28)[0]
                lst.append((f, obj.path_id, bytes(raw)))
    print(f"{f}: {counts}", flush=True)
    pickle.dump(found, open(out, "wb"))
print("done", {k: len(v) for k, v in found.items()}, flush=True)
