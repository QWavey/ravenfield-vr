import pickle, struct, sys
found = pickle.load(open(sys.argv[1], "rb"))
cls, count = sys.argv[2], int(sys.argv[3])
for f, pid, raw in found.get(cls, [])[:count]:
    nlen = struct.unpack_from("<i", raw, 28)[0]
    name = raw[32:32 + nlen].decode("utf-8", "replace")
    off = 32 + nlen
    off += (-off) % 4
    body = raw[off:]
    words = []
    for i in range(0, len(body) - 3, 4):
        iv, = struct.unpack_from("<i", body, i)
        fv, = struct.unpack_from("<f", body, i)
        if iv == 0:
            words.append("0")
        elif abs(iv) < 100000:
            words.append(f"i{iv}")
        elif 1e-4 < abs(fv) < 1e5:
            words.append(f"{fv:.4g}")
        else:
            words.append(f"x{iv & 0xffffffff:08x}")
    print(f"--- {cls} '{name}' ({f}, {len(body)} bytes)")
    print("   " + " ".join(words[:90]))
