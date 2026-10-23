"""Read-only dump of an IL2CPP global-metadata.dat (v24.1 layout): every type with its fields, properties and methods
(parameter names too), grouped by assembly image, plus the code's string literals and constant field values.

Names only: member TYPES live in GameAssembly.dll's metadata registration, which this does not parse.
Usage: python -I bw_meta.py <global-metadata.dat> <out_dir>
"""
import os
import struct
import sys

src, out = sys.argv[1], sys.argv[2]
os.makedirs(out, exist_ok=True)
data = open(src, "rb").read()
hdr = struct.unpack_from("<64i", data, 0)
assert hdr[0] & 0xFFFFFFFF == 0xFAB11BAF and hdr[1] == 24, "not a v24 metadata file"
SEC = ["strLit", "strLitData", "string", "events", "props", "methods", "paramDefVals", "fieldDefVals", "fieldParamDefData",
       "fieldMarshal", "params", "fields", "genParams", "genParamConstr", "genContainers", "nested", "interfaces", "vtable",
       "ifaceOffsets", "typeDefs", "rgctx", "images", "assemblies"]
sec = {n: (hdr[2 + 2 * i], hdr[3 + 2 * i]) for i, n in enumerate(SEC)}
str_off = sec["string"][0]


def name(idx):
    if idx < 0:
        return "?"
    end = data.index(b"\0", str_off + idx)
    return data[str_off + idx:end].decode("utf-8", "replace")


def table(section, size, fmt):
    off, length = sec[section]
    assert length % size == 0, (section, length, size)
    return [struct.unpack_from(fmt, data, off + i * size) for i in range(length // size)]


# v24.1 record layouts
typedefs = table("typeDefs", 100, "<19i8H2I")   # name, ns, byval, byref, declaring, parent, element, rgctxStart, rgctxCount,
                                               # genericContainer, flags, fieldStart, methodStart, eventStart, propertyStart,
                                               # nestedStart, interfacesStart, vtableStart, ifaceOffStart, 8 counts, bitfield, token
methods = table("methods", 52, "<10iI4H")      # name, declaringType, returnType, paramStart, genericContainer, methodIndex,
                                               # invoker, delegateWrapper, rgctxStart, rgctxCount, token, flags, iflags, slot, paramCount
fields = table("fields", 12, "<iiI")           # name, typeIndex, token
params = table("params", 12, "<iIi")           # name, token, typeIndex
props = table("props", 20, "<iiiII")           # name, get, set, attrs, token
nested = table("nested", 4, "<i")
images = table("images", 40, "<10i")           # name, assembly, typeStart, typeCount, exportedStart, exportedCount, entry, token, caStart, caCount
fdv = table("fieldDefVals", 12, "<iii")        # fieldIndex, typeIndex, dataIndex

const_of = {f: d for f, t, d in fdv}
dv_off = sec["fieldParamDefData"][0]

# nesting: child typedef index -> parent typedef index
outer = {}
for ti, t in enumerate(typedefs):
    start, count = t[15], t[23]
    for k in range(count):
        outer[nested[start + k][0]] = ti


def full(ti):
    t = typedefs[ti]
    n = name(t[0])
    if ti in outer:
        return full(outer[ti]) + "/" + n
    ns = name(t[1])
    return (ns + "." if ns else "") + n


METHOD_STATIC = 0x10
lines_by_image = {}
for img in images:
    img_name = name(img[0])
    buf = []
    for ti in range(img[2], img[2] + img[3]):
        t = typedefs[ti]
        fstart, mstart, pstart = t[11], t[12], t[14]
        mcount, pcount, fcount = t[19], t[20], t[21]
        buf.append(f"\nclass {full(ti)}")
        for k in range(fcount):
            fi = fstart + k
            f = fields[fi]
            extra = ""
            if fi in const_of:
                raw = data[dv_off + const_of[fi]: dv_off + const_of[fi] + 4]
                if len(raw) == 4:
                    (iv,), (fv,) = struct.unpack("<i", raw), struct.unpack("<f", raw)
                    extra = f"  = {iv} / {fv:.6g}f"
            buf.append(f"    field {name(f[0])}{extra}")
        for k in range(pcount):
            buf.append(f"    prop  {name(props[pstart + k][0])}")
        for k in range(mcount):
            m = methods[mstart + k]
            ps = ", ".join(name(params[m[3] + j][0]) for j in range(m[14]))
            static = "static " if m[11] & METHOD_STATIC else ""
            buf.append(f"    {static}method {name(m[0])}({ps})")
    lines_by_image[img_name] = buf

with open(os.path.join(out, "images.txt"), "w", encoding="utf-8") as fh:
    for img in images:
        fh.write(f"{name(img[0])}\t{img[3]} types\n")
for img_name, buf in lines_by_image.items():
    safe = img_name.replace("/", "_")
    with open(os.path.join(out, "types_" + safe + ".txt"), "w", encoding="utf-8") as fh:
        fh.write("\n".join(buf))

# string literals used by the code (log messages, asset names...)
lit = table("strLit", 8, "<Ii")                # length, dataIndex
lit_off = sec["strLitData"][0]
with open(os.path.join(out, "string_literals.txt"), "w", encoding="utf-8") as fh:
    for length, idx in lit:
        fh.write(data[lit_off + idx: lit_off + idx + length].decode("utf-8", "replace").replace("\n", "\\n") + "\n")
print(f"{len(typedefs)} types, {len(methods)} methods, {len(fields)} fields, {len(images)} images -> {out}")
