"""The mod's glove (src/Rig/Glove/glove.bin: Valve's SteamVR glove as tools/hands/convert_glove.py writes it) and its
finger poses, built the way the mod builds them (src/Rig/HandShape.cs, HandModel.cs, HandPoses.cs).

The file is in Unity's left-handed palm space (origin palm centre, +Z toward the fingers, +Y the thumb's side, palm facing
-X). Here everything is right-handed with z negated: vectors (x, y, -z), rotations (-x, -y, z, w), so in a hand's own
axes the fingers point -Z, the thumb's side is +Y and the palm faces -X (the left hand is the right one mirrored in x,
as in the mod: its palm faces +X)."""
import os
import struct

import numpy as np
from PIL import Image

from mathutil import quat_axis_angle, quat_inv, quat_mul, quat_norm, quat_slerp, quat_to_mat3

GLOVE_SCALE = 0.88  # HandShape.GloveScale
FINGERS = ['Thumb', 'Index', 'Middle', 'Ring', 'Little']

# HandPoses.cs: thumb base (0 rest .. 1 fist), then the curls of thumb, index, middle, ring, little (0 open .. 1 fist).
POSES = {
    'Relaxed': [0, 0.1, 0.12, 0.16, 0.2, 0.24],
    'Fist': [0.8, 0.6, 0.95, 1, 1, 1],
    'Pointing (laser)': [0.55, 0.45, 0.05, 0.82, 0.84, 0.86],
    'Pistol grip': [0.35, 0.06, 0.3, 0.7, 0.72, 0.74],
    'Rifle grip': [0.35, 0.25, 0.3, 0.7, 0.72, 0.74],
    'Machine gun grip': [0.45, 0.4, 0.32, 0.76, 0.78, 0.8],
    'Launcher grip': [0.5, 0.35, 0.35, 0.75, 0.78, 0.8],
    'Knife grip': [0.7, 0.65, 0.85, 0.88, 0.9, 0.9],
    'Tool grip': [0.55, 0.5, 0.75, 0.8, 0.82, 0.84],
    'Grenade': [0.55, 0.5, 0.6, 0.62, 0.64, 0.66],
    'Pistol cup': [0.35, 0.3, 0.55, 0.6, 0.62, 0.65],
    'Handguard': [0.35, 0.3, 0.6, 0.65, 0.68, 0.7],
    'Launcher tube': [0.3, 0.25, 0.45, 0.5, 0.52, 0.55],
    'Two-handed melee': [0.6, 0.55, 0.8, 0.84, 0.86, 0.88],
    'Pistol slide': [0.5, 0.45, 0.65, 0.7, 0.7, 0.7],
    'Bolt handle': [0.45, 0.5, 0.6, 0.8, 0.85, 0.85],
    'Charging handle': [0.2, 0.15, 0.7, 0.72, 0.45, 0.4],
    'Pump': [0.55, 0.5, 0.75, 0.78, 0.8, 0.82],
    'Rifle magazine': [0.3, 0.55, 0.7, 0.75, 0.78, 0.8],
    'Pistol magazine': [0.3, 0.5, 0.68, 0.74, 0.78, 0.8],
    'Box magazine': [0.35, 0.35, 0.5, 0.55, 0.58, 0.6],
    'Two magazines': [0.5, 0.5, 0.65, 0.7, 0.72, 0.75],
    'Palm push': [0, 0.08, 0.32, 0.4, 0.46, 0.52],
    'Shell': [0.55, 0.55, 0.55, 0.85, 0.9, 0.9],
    'Rocket': [0.3, 0.3, 0.45, 0.5, 0.52, 0.55],
    'Cover lift': [0.3, 0.3, 0.45, 0.5, 0.55, 0.6],
    'Pin pull': [0.4, 0.4, 0.85, 0.55, 0.5, 0.45],
    'Belt pinch': [0.45, 0.5, 0.6, 0.85, 0.88, 0.9],
    'Optic flip': [0.25, 0.1, 0.35, 0.7, 0.75, 0.78],
    'Steering wheel': [0.45, 0.45, 0.7, 0.72, 0.74, 0.76],
    'Flight stick': [0.5, 0.5, 0.35, 0.85, 0.86, 0.88],
}


def _rh_vec(x, y, z):
    return np.array([x, y, -z])


def _rh_quat(x, y, z, w):
    return quat_norm([-x, -y, z, w])


def _rh_axis(x, y, z):
    a = np.array([-x, -y, z])
    n = np.linalg.norm(a)
    return a / n if n > 1e-12 else a


class GloveShape:
    """glove.bin read once: the skinned mesh at its bind pose and each joint's data."""

    def __init__(self, folder):
        data = open(os.path.join(folder, 'glove.bin'), 'rb').read()
        if data[:4] != b'RFVG':
            raise ValueError('not a glove file')
        o = 4
        version, vc, ic = struct.unpack_from('<iii', data, o)
        o += 12
        if version != 2:
            raise ValueError(f'glove file version {version}')
        f = np.frombuffer(data, dtype='<f4', count=vc * 3, offset=o).reshape(vc, 3).astype(np.float64)
        o += vc * 12
        self.positions = f * np.array([1, 1, -1])
        n = np.frombuffer(data, dtype='<f4', count=vc * 3, offset=o).reshape(vc, 3).astype(np.float64)
        o += vc * 12
        self.normals = n * np.array([1, 1, -1])
        self.uvs = np.frombuffer(data, dtype='<f4', count=vc * 2, offset=o).reshape(vc, 2).astype(np.float32).copy()
        o += vc * 8
        self.bone_index = np.frombuffer(data, dtype=np.uint8, count=vc * 4, offset=o).reshape(vc, 4).astype(np.int64)
        o += vc * 4
        self.bone_weight = np.frombuffer(data, dtype='<f4', count=vc * 4, offset=o).reshape(vc, 4).astype(np.float64)
        o += vc * 16
        tri = np.frombuffer(data, dtype='<u2', count=ic, offset=o).reshape(-1, 3).astype(np.uint32)
        o += ic * 2
        self.indices = tri[:, [0, 2, 1]].reshape(-1).copy()  # turned round with the z flip

        def rd(k):
            nonlocal o
            v = struct.unpack_from('<' + 'f' * k, data, o)
            o += 4 * k
            return v

        self.knuckle = [None] * 5
        self.bind = [[None] * 3 for _ in range(5)]
        self.child = [[None] * 3 for _ in range(5)]
        self.curl_axis = [[None] * 3 for _ in range(5)]
        self.curl_angle = [[0.0] * 3 for _ in range(5)]
        opened = [[None] * 3 for _ in range(5)]
        fist = [[None] * 3 for _ in range(5)]
        relaxed = [[None] * 3 for _ in range(5)]
        self.point = [[None] * 3 for _ in range(5)]
        for fi in range(5):
            for s in range(3):
                p = _rh_vec(*rd(3))
                if s == 0:
                    self.knuckle[fi] = p
                self.bind[fi][s] = _rh_quat(*rd(4))
                self.child[fi][s] = _rh_vec(*rd(3))
                self.curl_axis[fi][s] = _rh_axis(*rd(3))
                self.curl_angle[fi][s] = rd(1)[0]
                rd(1)  # radius
                rd(1)  # pad
                rd(3)  # pad direction
                opened[fi][s] = _rh_quat(*rd(4))
                fist[fi][s] = _rh_quat(*rd(4))
                relaxed[fi][s] = _rh_quat(*rd(4))
                self.point[fi][s] = _rh_quat(*rd(4))  # Valve's pointing hand
        # Curl 0 is Valve's relaxed hand with its curl taken out (HandShape.LoadGlove), the thumb's base apart.
        self.rest = [[opened[fi][s] if fi == 0 and s == 0 else _without_curl(opened[fi][s], relaxed[fi][s], self.curl_axis[fi][s])
                      for s in range(3)] for fi in range(5)]
        self.thumb_rest = relaxed[0][0]
        self.thumb_fist = fist[0][0]
        img = os.path.join(folder, 'glove_color.jpg')
        # Unity's uvs start at the image's bottom row; OpenGL takes the first row given as the bottom: turned upside down.
        self.image = Image.open(img).convert('RGBA').transpose(Image.Transpose.FLIP_TOP_BOTTOM) if os.path.exists(img) else None
        # The bind pose's joint matrices (palm space), inverted once.
        self.inv_bind = [np.linalg.inv(m) for m in self._joint_matrices([[self.bind[fi][s] for s in range(3)] for fi in range(5)])]

    def _joint_matrices(self, rotations):
        """16 world (palm-space) matrices: the palm, then each finger's three joints."""
        mats = [np.eye(4)]
        for fi in range(5):
            parent = np.eye(4)
            for s in range(3):
                local = np.eye(4)
                local[:3, :3] = quat_to_mat3(rotations[fi][s])
                local[:3, 3] = self.knuckle[fi] if s == 0 else self.child[fi][s - 1]
                parent = parent @ local
                mats.append(parent)
        return mats

    def pose(self, thumb_base, curls, point=False):
        """The skinned vertices and normals (palm space, unscaled) for this pose. point: Valve's own pointing hand, every
        joint (the thumb's base too), as the mod draws a pointing hand (HandModel: a curl alone left the thumb sticking out
        and the index bent like a hook)."""
        thumb = quat_slerp(self.thumb_rest, self.thumb_fist, thumb_base)
        rots = [[None] * 3 for _ in range(5)]
        for fi in range(5):
            for s in range(3):
                if point:
                    rots[fi][s] = self.point[fi][s]
                    continue
                rest = thumb if fi == 0 and s == 0 else self.rest[fi][s]
                rots[fi][s] = quat_mul(rest, quat_axis_angle(self.curl_axis[fi][s], self.curl_angle[fi][s] * curls[fi]))
        mats = self._joint_matrices(rots)
        skin = np.stack([m @ ib for m, ib in zip(mats, self.inv_bind)])  # (16, 4, 4)
        w = self.bone_weight
        idx = self.bone_index
        m = np.einsum('vk,vkij->vij', w, skin[idx])  # (n, 4, 4) blended per vertex
        p = np.einsum('vij,vj->vi', m[:, :3, :3], self.positions) + m[:, :3, 3]
        nn = np.einsum('vij,vj->vi', m[:, :3, :3], self.normals)
        nn /= np.maximum(1e-9, np.linalg.norm(nn, axis=1, keepdims=True))
        return p.astype(np.float32), nn.astype(np.float32), mats


def _without_curl(open_q, pose_q, axis):
    """The swing of open^-1 * pose about the curl axis kept, its twist taken out (HandShape.WithoutCurl)."""
    q = quat_mul(quat_inv(open_q), pose_q)
    along = q[0] * axis[0] + q[1] * axis[1] + q[2] * axis[2]
    twist = np.array([axis[0] * along, axis[1] * along, axis[2] * along, q[3]])
    n = np.linalg.norm(twist)
    if n < 1e-6:
        return open_q
    twist /= n
    return quat_norm(quat_mul(pose_q, quat_inv(twist)))


class Hand:
    """One glove: a place and turn (its palm centre, in the scene), a pose, and the posed mesh."""

    def __init__(self, shape, side):
        self.shape = shape
        self.side = side  # 'right' | 'left'
        self.position = np.zeros(3)
        self.rotation = np.array([0, 0, 0, 1.0])
        self.thumb = 0.0
        self.curls = [0.1, 0.12, 0.16, 0.2, 0.24]
        self.pose_name = 'Relaxed'
        self.on_part = ''  # what it holds (set by the snap or found when saved)
        self.role = ''  # its job on the gun (grip, support, rack, magazine, cover): from the snap, or what it touches when moved
        self.thumb_set = False  # the thumb posed by hand here (sent to the game); else the game keeps its own thumb rule
        self.settle = np.zeros(3)  # where the glove is drawn from its place: settled onto the gun as in VR (drawn only)
        self._settled_at = None
        self.dirty = True
        self.vertices = None
        self.normals = None
        self.joint_mats = None
        self.update_mesh()

    @property
    def mirror(self):
        return -1.0 if self.side == 'left' else 1.0

    def set_pose(self, name, values=None):
        v = values if values is not None else POSES.get(name)
        if v is None:
            return
        self.pose_name = name
        self.thumb = float(v[0])
        self.curls = [float(c) for c in v[1:6]]
        self.update_mesh()

    def update_mesh(self):
        self.vertices, self.normals, self.joint_mats = self.shape.pose(self.thumb, self.curls, self.pose_name == 'Pointing (laser)')
        self.dirty = True

    def set_settle(self, offset):
        """The glove drawn this far from its place while the hand stays where it is (moved, it is drawn at its place)."""
        self.settle = np.asarray(offset, dtype=np.float64)
        self._settled_at = (self.position.copy(), self.rotation.copy())

    def drawn_position(self):
        a = self._settled_at
        if a is not None and np.allclose(a[0], self.position, atol=1e-9) and np.allclose(a[1], self.rotation, atol=1e-9):
            return self.position + self.settle
        return self.position

    def model_matrix(self):
        from mathutil import trs
        return trs(self.drawn_position(), self.rotation, (self.mirror * GLOVE_SCALE, GLOVE_SCALE, GLOVE_SCALE))

    def fingertip(self, finger=1):
        """A fingertip in the scene (for the laser: the index finger's tip)."""
        m = self.joint_mats[1 + finger * 3 + 2]
        tip = m @ np.append(self.shape.child[finger][2], 1.0)
        return (self.model_matrix() @ tip)[:3]

    def state(self):
        return {'position': self.position.tolist(), 'rotation': self.rotation.tolist(), 'pose': self.pose_name,
                'thumb': self.thumb, 'curls': list(self.curls), 'on': self.on_part, 'thumbSet': self.thumb_set}

    def load(self, st):
        self.position = np.array(st['position'], dtype=np.float64)
        self.rotation = quat_norm(st['rotation'])
        self.pose_name = st.get('pose', 'Custom')
        self.thumb = float(st.get('thumb', 0))
        self.curls = [float(c) for c in st.get('curls', self.curls)]
        self.on_part = st.get('on', '')
        self.thumb_set = bool(st.get('thumbSet', False))
        self.update_mesh()
