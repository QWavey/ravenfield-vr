"""Minimal FBX 7.x binary reader: nodes with typed properties (arrays decoded with numpy)."""
import struct, zlib, sys
import numpy as np


class Node:
    __slots__ = ("name", "props", "children")

    def __init__(self, name, props, children):
        self.name = name
        self.props = props
        self.children = children

    def find(self, name):
        for c in self.children:
            if c.name == name:
                return c
        return None

    def findall(self, name):
        return [c for c in self.children if c.name == name]

    def __repr__(self):
        return f"Node({self.name}, {len(self.props)} props, {len(self.children)} children)"


_ARRAY = {b"f": ("<f4", 4), b"d": ("<f8", 8), b"l": ("<i8", 8), b"i": ("<i4", 4), b"b": ("<u1", 1)}


def _read_prop(data, pos):
    t = data[pos:pos + 1]
    pos += 1
    if t == b"Y":
        return struct.unpack_from("<h", data, pos)[0], pos + 2
    if t == b"C":
        return data[pos] != 0, pos + 1
    if t == b"I":
        return struct.unpack_from("<i", data, pos)[0], pos + 4
    if t == b"F":
        return struct.unpack_from("<f", data, pos)[0], pos + 4
    if t == b"D":
        return struct.unpack_from("<d", data, pos)[0], pos + 8
    if t == b"L":
        return struct.unpack_from("<q", data, pos)[0], pos + 8
    if t in _ARRAY:
        n, enc, clen = struct.unpack_from("<III", data, pos)
        pos += 12
        raw = data[pos:pos + clen]
        pos += clen
        if enc == 1:
            raw = zlib.decompress(raw)
        dt, size = _ARRAY[t]
        return np.frombuffer(raw, dtype=dt, count=n).copy(), pos
    if t in (b"S", b"R"):
        n = struct.unpack_from("<I", data, pos)[0]
        pos += 4
        raw = data[pos:pos + n]
        pos += n
        if t == b"S":
            return raw.decode("utf-8", "replace"), pos
        return raw, pos
    raise ValueError(f"unknown property type {t!r} at {pos - 1}")


def _read_node(data, pos, version):
    if version >= 7500:
        end, nprops, plen = struct.unpack_from("<QQQ", data, pos)
        pos += 24
        null_len = 25
    else:
        end, nprops, plen = struct.unpack_from("<III", data, pos)
        pos += 12
        null_len = 13
    nlen = data[pos]
    pos += 1
    if end == 0:
        return None, pos
    name = data[pos:pos + nlen].decode("ascii", "replace")
    pos += nlen
    props = []
    for _ in range(nprops):
        v, pos = _read_prop(data, pos)
        props.append(v)
    children = []
    while pos < end:
        if data[pos:pos + null_len] == b"\0" * null_len and pos + null_len == end:
            pos += null_len
            break
        child, pos = _read_node(data, pos, version)
        if child is None:
            break
        children.append(child)
    return Node(name, props, children), end


def load(path):
    data = open(path, "rb").read()
    assert data[:20] == b"Kaydara FBX Binary  ", "not a binary FBX"
    version = struct.unpack_from("<I", data, 23)[0]
    pos = 27
    top = []
    while pos < len(data):
        node, pos = _read_node(data, pos, version)
        if node is None:
            break
        top.append(node)
    return version, Node("root", [], top)


def props70(node):
    """Properties70 P records -> {name: values}."""
    out = {}
    p70 = node.find("Properties70")
    if p70 is None:
        return out
    for p in p70.findall("P"):
        out[p.props[0]] = p.props[4:]
    return out


if __name__ == "__main__":
    version, root = load(sys.argv[1])
    print("version", version)
    for n in root.children:
        print(n.name, [type(p).__name__ for p in n.props][:4], len(n.children))
