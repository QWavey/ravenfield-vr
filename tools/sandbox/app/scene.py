"""What is in the sandbox: one exported weapon (its parts and what the mod found), two gloves, the reload's moving parts,
and everything said in the mod's gun space.

Gun space here is the export's: the mod's gun space made right-handed (z negated). The weapon is shown with its barrel
along +X and its top up (+Y), the grip at the origin. Numbers for people (and for the mod) are given the way the test
reports give them: cm from the muzzle, back / up / right, and the mod's own gun-space x, y, z."""
import json
import math
import os
import time

import numpy as np

import glb
from glove import POSES, GloveShape, Hand
from mathutil import frame_quat, norm, quat_axis_angle, quat_inv, quat_mul, quat_norm, quat_to_mat3, mat3_to_quat, trs, v3

ROLE_NAMES = {'slide': 'slide / bolt', 'companion': 'moves with the slide', 'handle': 'charging handle', 'bolt': 'bolt',
              'magazine': 'magazine', 'hinge': 'cover / barrels', 'body': 'gun body'}


def _prefix(path, part):
    return bool(part) and (path == part or path.startswith(part + '/'))


class Weapon:
    def __init__(self, folder):
        self.folder = folder
        self.id = os.path.basename(folder)
        self.rig = json.load(open(os.path.join(folder, 'rig.json'), encoding='utf-8'))
        self.name = self.rig.get('key', self.id).split('#')[0]
        self.nodes = glb.load(os.path.join(folder, 'model.glb'))
        r = self.rig
        self.aim = norm(r['aim'])
        up = v3(r['up'])
        self.up = norm(up - self.aim * np.dot(up, self.aim))
        self.right = norm(np.cross(self.aim, self.up))
        self.grip = v3(r['grip'])
        self.muzzle = v3(r['muzzle'])
        rot = np.vstack([self.aim, self.up, self.right])  # gun -> world: barrel +X, top +Y, right side +Z (toward the viewer)
        self.W = np.eye(4)
        self.W[:3, :3] = rot
        self.W[:3, 3] = -rot @ self.grip
        self.Wq = mat3_to_quat(rot)
        roles = []
        for key, role in (('magazine', 'magazine'), ('hinge', 'hinge'), ('handle', 'handle'), ('bolt', 'bolt'), ('slide', 'slide')):
            if r.get(key):
                roles.append((r[key], role))
        for p in r.get('magazineParts', []):
            roles.append((p, 'magazine'))
        for p in r.get('slideCompanions', []):
            roles.append((p, 'companion'))
        for node in self.nodes:
            node.role = 'body'
            # The deepest matching part wins (a handle under the slide is the handle).
            best = -1
            for path, role in roles:
                if _prefix(node.path, path) and len(path) > best:
                    best = len(path)
                    node.role = role
            node.hidden = False
            node.sim = np.eye(4)
            node.tris = node.triangles()
        rest = r.get('magRestPos')
        park = v3(r.get('magSeatPos', [0, 0, 0])) - v3(rest) if rest is not None else np.zeros(3)
        self.mag_parked = park if np.linalg.norm(park) > 0.05 else np.zeros(3)
        self.slide = 0.0
        self.mag = 0.0
        self.cover = 0.0
        self.fresh = None  # the fresh magazine's place (gun-space 4x4) while one comes in, or None
        shift = [self.mag_parked if n.role == 'magazine' else np.zeros(3) for n in self.nodes]
        allmin = np.min([n.bmin + s for n, s in zip(self.nodes, shift)], axis=0) if self.nodes else np.zeros(3)
        allmax = np.max([n.bmax + s for n, s in zip(self.nodes, shift)], axis=0) if self.nodes else np.zeros(3)
        self.bmin, self.bmax = allmin, allmax
        self.stand_ins = sum(1 for n in self.nodes if n.stand_in)

    # ---- spaces ---------------------------------------------------------------------------------------------------

    def to_world(self, p):
        return (self.W @ np.append(v3(p), 1.0))[:3]

    def dir_to_world(self, d):
        return self.W[:3, :3] @ v3(d)

    def to_gun(self, p):
        return np.linalg.inv(self.W) @ np.append(v3(p), 1.0)

    def from_muzzle(self, p_gun):
        """cm from the muzzle: back, up, right (as the test reports and rig dumps give them)."""
        c = v3(p_gun) - self.muzzle
        return np.array([np.dot(c, -self.aim), np.dot(c, self.up), np.dot(c, self.right)]) * 100.0

    @staticmethod
    def unity(p_gun):
        """The mod's own gun-space coordinates (left-handed: z back)."""
        return np.array([p_gun[0], p_gun[1], -p_gun[2]])

    def node_matrix(self, node):
        return self.W @ node.sim

    def center_world(self):
        return self.to_world((self.bmin + self.bmax) * 0.5), float(np.linalg.norm(self.bmax - self.bmin)) * 0.5

    # ---- the moving parts -------------------------------------------------------------------------------------------

    @property
    def mag_length(self):
        size = v3(self.rig.get('magBoundsSize', [0.03, 0.12, 0.06]))
        return float(max(size)) * 1.15 + 0.03

    def mag_center(self):
        q = self.rig.get('magSeatRot', [0, 0, 0, 1])
        return v3(self.rig.get('magSeatPos', [0, 0, 0])) + quat_to_mat3(q) @ v3(self.rig.get('magBoundsCenter', [0, 0, 0]))

    def mag_eject(self):
        return norm(self.rig.get('magEject', [0, -1, 0]))

    def set_state(self, slide=None, mag=None, cover=None):
        if slide is not None:
            self.slide = float(slide)
        if mag is not None:
            self.mag = float(mag)
        if cover is not None:
            self.cover = float(cover)
        st = v3(self.rig.get('slideTravel', [0, 0, 0]))
        ht = v3(self.rig.get('handleTravel', [0, 0, 0]))
        if np.linalg.norm(ht) < 1e-5:
            ht = st
        ej = self.mag_eject() * self.mag_length
        pivot = v3(self.rig.get('hingePivot', [0, 0, 0]))
        axis = -v3(self.rig.get('hingeAxis', [1, 0, 0]))  # a rotation axis turned right-handed: (-x, -y, z) of Unity's
        hinge = trs([0, 0, 0], quat_axis_angle(axis, float(self.rig.get('hingeAngle', 0)) * self.cover)) if np.linalg.norm(axis) > 1e-6 else np.eye(4)
        about = np.eye(4)
        about[:3, 3] = pivot
        back = np.eye(4)
        back[:3, 3] = -pivot
        hinge_m = about @ hinge @ back
        for n in self.nodes:
            m = np.eye(4)
            if n.role in ('slide', 'companion', 'bolt'):
                m[:3, 3] = st * self.slide
            elif n.role == 'handle':
                m[:3, 3] = ht * self.slide
            elif n.role == 'magazine':
                # A magazine that waits parked away at rest (the M1's clip) is drawn on its seat.
                m[:3, 3] = ej * self.mag + self.mag_parked
            if n.role == 'hinge' or (self.rig.get('hinge') and _prefix(n.path, self.rig.get('hinge'))):
                m = hinge_m @ m
            n.sim = m

    def rack_point(self):
        """Where the hand takes the slide or handle now (gun space)."""
        st = v3(self.rig.get('slideTravel', [0, 0, 0]))
        ht = v3(self.rig.get('handleTravel', [0, 0, 0]))
        travel = ht if self.rig.get('handle') and np.linalg.norm(ht) > 1e-5 else st
        return v3(self.rig['rackPoint']) + travel * self.slide

    def nearest_part(self, p_gun):
        best, bd = None, 1e9
        for n in self.nodes:
            q = np.clip(p_gun, n.bmin + n.sim[:3, 3], n.bmax + n.sim[:3, 3])
            d = np.linalg.norm(q - p_gun)
            if d < bd:
                best, bd = n, d
        return best, bd


class Scene:
    def __init__(self, glove_folder):
        self.shape = GloveShape(glove_folder)
        self.right = Hand(self.shape, 'right')
        self.left = Hand(self.shape, 'left')
        self.right.position = np.array([0.0, -0.02, 0.12])
        self.left.position = np.array([0.18, -0.02, 0.12])
        self.weapon = None
        self.highlight_role = None
        self.laser_hand = None

    def hands(self):
        return [self.right, self.left]

    @property
    def floor(self):
        if self.weapon is None:
            return -0.2
        lo = min(self.weapon.to_world(c)[1] for c in _corners(self.weapon.bmin, self.weapon.bmax))
        return lo - 0.03

    def load_weapon(self, folder):
        self.weapon = Weapon(folder)
        self.weapon.set_state(0, 0, 0)

    def extra_draws(self):
        """(node, model) pairs drawn besides the weapon: the fresh magazine coming in."""
        w = self.weapon
        if w is None or w.fresh is None:
            return []
        return [(n, w.W @ w.fresh) for n in w.nodes if n.role == 'magazine' and not n.hidden]

    def laser_origin(self):
        h = self.laser_hand
        return h.fingertip(1) if h is not None else None

    def marker_lines(self):
        w = self.weapon
        if w is None:
            return []
        out = []
        r = w.rig

        def cross(p, color, s=0.012):
            p = w.to_world(p)
            pts = []
            for a in np.eye(3):
                pts += [p - a * s, p + a * s]
            out.append((np.array(pts), color))

        def arrow(p, d, color):
            a, b = w.to_world(p), w.to_world(v3(p) + v3(d))
            out.append((np.array([a, b]), color))

        cross(w.grip, (0.3, 0.9, 0.4, 1))
        if r.get('hasSupport'):
            cross(r['support'], (0.35, 0.6, 1.0, 1))
        cross(w.rack_point(), (1.0, 0.6, 0.15, 1))
        arrow(w.rack_point(), r.get('slideTravel', [0, 0, 0]), (1.0, 0.6, 0.15, 1))
        cross(w.muzzle, (1.0, 0.3, 0.3, 1))
        arrow(w.muzzle, w.aim * 0.06, (1.0, 0.3, 0.3, 1))
        seat = v3(r.get('magSeatPos', [0, 0, 0]))
        cross(seat, (1.0, 0.9, 0.3, 1), 0.008)
        arrow(w.mag_center(), w.mag_eject() * w.mag_length, (1.0, 0.9, 0.3, 0.8))
        if r.get('hingeKind', 'None') != 'None':
            cross(r['hingeGrab'], (0.8, 0.5, 1.0, 1))
        return out

    # ---- putting the hands on the gun -------------------------------------------------------------------------------

    def place(self, hand, pos_gun, palm_normal_gun, thumb_gun):
        """The hand's palm at this gun-space point, its palm facing palm_normal, its thumb's side toward thumb."""
        w = self.weapon
        n = w.dir_to_world(palm_normal_gun)
        t = w.dir_to_world(thumb_gun)
        x = n if hand.side == 'left' else -n  # the glove's palm faces -X (right) / +X (left, mirrored)
        hand.rotation = frame_quat(x, t)
        hand.position = w.to_world(pos_gun)

    def snap(self, hand, target):
        w = self.weapon
        if w is None:
            return
        hand.role = target
        r = w.rig
        pistol = r.get('isPistol')
        if target == 'grip':
            # Exactly as the mod holds a gun (VRHand.GunFrame with the automatic gun angle, HeldWeapon.BeforeRender): the
            # glove's palm frame is the gun's frame at the grip point: the barrel along the fingers' direction, the top along
            # the thumb's side, the palm centre on the grip point.
            hand.rotation = frame_quat(w.dir_to_world(w.right), w.dir_to_world(w.up))
            hand.position = w.to_world(w.grip)
            curls = r.get('gripCurls')
            if curls and len(curls) == 5:
                hand.set_pose("weapon's own grip", [0.35] + list(curls))
            else:
                hand.set_pose('Machine gun grip' if r.get('machineGun') else 'Pistol grip' if pistol else 'Rifle grip')
            hand.on_part = 'grip'
        elif target == 'support':
            at = v3(r['support']) if r.get('hasSupport') else w.grip + w.aim * 0.2
            self.place(hand, at, w.up, w.aim)
            curls = r.get('supportCurls')
            if curls and len(curls) == 5:
                hand.set_pose("weapon's own support", [0.35] + list(curls))
            else:
                hand.set_pose('Pistol cup' if pistol else 'Handguard')
            hand.on_part = 'handguard'
        elif target == 'rack':
            p = w.rack_point()
            off = p - w.muzzle
            off = off - w.aim * np.dot(off, w.aim)
            out = norm(off) if np.linalg.norm(off) > 0.015 else w.up
            thumb = -w.aim if np.dot(out, w.up) > 0.7 else w.up
            self.place(hand, p + out * 0.02, -out, thumb)
            pump = r.get('action') == 'ShellFed' and not r.get('handle')
            hand.set_pose('Pistol slide' if pistol else 'Pump' if pump else 'Charging handle' if r.get('handle') else 'Bolt handle')
            hand.on_part = 'slide / handle'
        elif target == 'magazine':
            ej = w.mag_eject()
            size = v3(r.get('magBoundsSize', [0.03, 0.12, 0.06]))
            base = w.mag_center() + ej * (float(np.max(size)) * 0.5 + 0.01) + ej * w.mag_length * w.mag
            self.place(hand, base, -ej, w.aim)
            hand.set_pose('Pistol magazine' if pistol else 'Box magazine' if r.get('machineGun') else 'Rifle magazine')
            hand.on_part = 'magazine'
        elif target == 'cover':
            if r.get('hingeKind', 'None') == 'None':
                return
            self.place(hand, v3(r['hingeGrab']) + w.up * 0.02, -w.up, -w.aim)
            hand.set_pose('Cover lift')
            hand.on_part = 'cover'

    # ---- saving --------------------------------------------------------------------------------------------------------

    def hand_in_gun(self, hand):
        """A hand's place and turn in gun space (right-handed) and in the mod's gun space (left-handed)."""
        w = self.weapon
        p = w.to_gun(hand.position)[:3]
        q = quat_norm(quat_mul(quat_inv(w.Wq), hand.rotation))
        part, dist = w.nearest_part(p)
        return {
            'gun': {'position': p.tolist(), 'rotation': q.tolist()},
            'mod': {'position': Weapon.unity(p).round(5).tolist(), 'rotation': [round(-q[0], 5), round(-q[1], 5), round(q[2], 5), round(q[3], 5)]},
            'fromMuzzleCm': w.from_muzzle(p).round(2).tolist(),
            'pose': hand.pose_name, 'thumb': round(hand.thumb, 4), 'curls': [round(c, 4) for c in hand.curls],
            'on': hand.on_part or (part.short if part is not None else ''),
            'role': hand.role,
            'nearestPart': {'name': part.name if part else '', 'role': ROLE_NAMES.get(part.role, part.role) if part else '',
                            'cm': round(dist * 100, 2)},
        }

    def pose_document(self):
        w = self.weapon
        return {
            'weapon': w.id, 'key': w.rig.get('key', w.id), 'name': w.name, 'saved': time.strftime('%Y-%m-%d %H:%M:%S'),
            'about': ("Hands on this weapon. 'mod' is the mod's gun space (left-handed, as in the rig dumps); a hand's rotation is "
                      "its palm frame (fingers +Z, thumb side +Y, palm facing -X for the right hand, +X for the mirrored left). "
                      "'gun' is the same right-handed (z negated), as the sandbox uses it. fromMuzzleCm = back, up, right."),
            'hands': {'right': self.hand_in_gun(self.right), 'left': self.hand_in_gun(self.left)},
            'parts': {'slide': w.slide, 'magazine': w.mag, 'cover': w.cover},
        }

    def apply_pose_document(self, doc):
        w = self.weapon
        for side, hand in (('right', self.right), ('left', self.left)):
            h = (doc.get('hands') or {}).get(side)
            if not h:
                continue
            g = h['gun']
            hand.position = w.to_world(g['position'])
            hand.rotation = quat_norm(quat_mul(w.Wq, g['rotation']))
            hand.pose_name = h.get('pose', 'Custom')
            hand.thumb = float(h.get('thumb', 0))
            hand.curls = [float(c) for c in h.get('curls', hand.curls)]
            hand.on_part = h.get('on', '')
            hand.role = h.get('role', '')
            hand.update_mesh()
        parts = doc.get('parts') or {}
        w.set_state(parts.get('slide', 0), parts.get('magazine', 0), parts.get('cover', 0))


def _corners(a, b):
    return [np.array([x, y, z]) for x in (a[0], b[0]) for y in (a[1], b[1]) for z in (a[2], b[2])]
