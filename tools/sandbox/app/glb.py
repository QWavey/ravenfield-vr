"""Reads the .glb files the mod's sandbox export writes (src/Weapons/SandboxExport.cs): one node per drawn renderer,
vertices already in gun space (right-handed), materials with an optional embedded PNG."""
import io
import json
import struct

import numpy as np
from PIL import Image

COMPONENTS = {5126: np.float32, 5125: np.uint32, 5123: np.uint16, 5121: np.uint8}
SIZES = {'SCALAR': 1, 'VEC2': 2, 'VEC3': 3, 'VEC4': 4}


class Material:
    def __init__(self, name, color, image, blend, double):
        self.name = name
        self.color = color  # rgba floats
        self.image = image  # PIL RGBA image or None
        self.blend = blend
        self.double = double


class Primitive:
    def __init__(self, positions, normals, uvs, indices, material):
        self.positions = positions  # (n, 3) float32
        self.normals = normals
        self.uvs = uvs  # (n, 2) or None
        self.indices = indices  # (m,) uint32
        self.material = material


class Node:
    def __init__(self, name, path, primitives):
        self.name = name
        self.path = path  # "0/3/1": the transform's child-index path in the weapon (matches rig.json's part paths)
        self.primitives = primitives
        allp = np.concatenate([p.positions for p in primitives]) if primitives else np.zeros((0, 3), np.float32)
        self.bmin = allp.min(axis=0) if len(allp) else np.zeros(3)
        self.bmax = allp.max(axis=0) if len(allp) else np.zeros(3)
        self.stand_in = name.startswith('[stand-in] ')

    @property
    def short(self):
        n = self.name[len('[stand-in] '):] if self.stand_in else self.name
        return n.split('/')[-1]

    def triangles(self):
        """All triangles (n, 3, 3) in gun space, for picking."""
        out = [p.positions[p.indices.reshape(-1, 3)] for p in self.primitives if len(p.indices)]
        return np.concatenate(out).astype(np.float64) if out else np.zeros((0, 3, 3))


def load(path):
    data = open(path, 'rb').read()
    magic, version, length = struct.unpack_from('<III', data, 0)
    if magic != 0x46546C67:
        raise ValueError('not a glb file')
    off = 12
    js = None
    binary = b''
    while off < length:
        clen, ctype = struct.unpack_from('<II', data, off)
        chunk = data[off + 8: off + 8 + clen]
        if ctype == 0x4E4F534A:
            js = json.loads(chunk.decode('utf-8'))
        elif ctype == 0x004E4942:
            binary = chunk
        off += 8 + clen
    if js is None:
        raise ValueError('glb without json')

    views = js.get('bufferViews', [])

    def view_bytes(i):
        v = views[i]
        o = v.get('byteOffset', 0)
        return binary[o: o + v['byteLength']]

    def accessor(i):
        a = js['accessors'][i]
        dtype = COMPONENTS[a['componentType']]
        n = SIZES[a['type']]
        raw = view_bytes(a['bufferView'])
        arr = np.frombuffer(raw, dtype=dtype, count=a['count'] * n)
        return arr.reshape(a['count'], n) if n > 1 else arr

    images = []
    for img in js.get('images', []):
        try:
            images.append(Image.open(io.BytesIO(view_bytes(img['bufferView']))).convert('RGBA'))
        except Exception:
            images.append(None)
    textures = [images[t['source']] if t.get('source') is not None and t['source'] < len(images) else None for t in js.get('textures', [])]
    materials = []
    for m in js.get('materials', []):
        pbr = m.get('pbrMetallicRoughness', {})
        tex = pbr.get('baseColorTexture')
        materials.append(Material(m.get('name', ''), tuple(pbr.get('baseColorFactor', [0.7, 0.7, 0.7, 1])),
                                  textures[tex['index']] if tex and tex['index'] < len(textures) else None,
                                  m.get('alphaMode') == 'BLEND', bool(m.get('doubleSided'))))
    default = Material('default', (0.7, 0.7, 0.7, 1), None, False, False)
    nodes = []
    for n in js.get('nodes', []):
        mesh = js['meshes'][n['mesh']]
        prims = []
        for p in mesh['primitives']:
            at = p['attributes']
            pos = accessor(at['POSITION']).astype(np.float32)
            nor = accessor(at['NORMAL']).astype(np.float32) if 'NORMAL' in at else np.tile([0, 1, 0], (len(pos), 1)).astype(np.float32)
            uv = accessor(at['TEXCOORD_0']).astype(np.float32) if 'TEXCOORD_0' in at else None
            idx = accessor(p['indices']).astype(np.uint32) if 'indices' in p else np.arange(len(pos), dtype=np.uint32)
            mat = materials[p['material']] if p.get('material') is not None and p['material'] < len(materials) else default
            prims.append(Primitive(pos, nor, uv, idx, mat))
        nodes.append(Node(n.get('name', 'part'), (n.get('extras') or {}).get('path', ''), prims))
    return nodes
