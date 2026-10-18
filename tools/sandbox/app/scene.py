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
        up = up - self.aim * np.dot(up, self.aim)
        if np.linalg.norm(up) < 1e-4:
            # (An export whose up lies along the bore or is zero: the under-barrel grenade launchers before the mod gave
            # them one. The first axis across the bore, so the frame is never degenerate.)
            up = next(a - self.aim * np.dot(a, self.aim) for a in (np.array([0.0, 1, 0]), np.array([0.0, 0, 1]), np.array([1.0, 0, 0]))
                      if np.linalg.norm(a - self.aim * np.dot(a, self.aim)) > 0.1)
        self.up = norm(up)
        self.right = norm(np.cross(self.aim, self.up))
        self.grip = v3(r['grip'])
        self.muzzle = v3(r['muzzle'])
        self.muzzle_known = bool(np.linalg.norm(self.muzzle) > 1e-6)
        rot = np.vstack([self.aim, self.up, self.right])  # gun -> world: barrel +X, top +Y, right side +Z (toward the viewer)
        self.W = np.eye(4)
        self.W[:3, :3] = rot
        self.W[:3, 3] = -rot @ self.grip
        self.Wq = mat3_to_quat(rot)
        self.W0 = self.W.copy()
        self.P = np.eye(4)  # where the weapon was moved to in the scene (Ctrl + drag); poses are in gun space, unaffected
        self._winv = None  # inv(W) for to_gun, taken from the W in _winv_of (review I5#6: a drag asks on every move)
        self._winv_of = None
        self._parts = None  # the parts' stacked places and boxes for nearest_part (see _part_cache)
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
            node.hidden = False  # hidden by the user (Hide this part)
            node.away = False  # taken away by the reload (the old magazine, out)
            node.sim = np.eye(4)
            node.tris = node.triangles()
        self.hinge_m = np.eye(4)
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
        if not self.muzzle_known:
            # (SABRE, the smoke grenade, the squad kit, PATRIOT GL: no muzzle in the export. On the bore line at the gun's
            # front instead, so readouts and markers are not measured from the gun's origin; review B9#2.)
            front = max(float(np.dot(c, self.aim)) for c in _corners(self.bmin, self.bmax)) if self.nodes else 0.0
            self.muzzle = self.grip + self.aim * (front - float(np.dot(self.grip, self.aim)))

    # ---- spaces ---------------------------------------------------------------------------------------------------

    def set_place(self, P):
        self.P = np.array(P, dtype=np.float64)
        self.W = self.P @ self.W0
        self.Wq = mat3_to_quat(self.W[:3, :3])

    def to_world(self, p):
        return (self.W @ np.append(v3(p), 1.0))[:3]

    def dir_to_world(self, d):
        return self.W[:3, :3] @ v3(d)

    def to_gun(self, p):
        if self._winv is None or self._winv_of is not self.W:  # (set_place makes a new W; the inverse follows it)
            self._winv, self._winv_of = np.linalg.inv(self.W), self.W
        return self._winv @ np.append(v3(p), 1.0)

    def from_muzzle(self, p_gun):
        """cm from the muzzle: back, up, right (as the test reports and rig dumps give them)."""
        c = v3(p_gun) - self.muzzle
        return np.array([np.dot(c, -self.aim), np.dot(c, self.up), np.dot(c, self.right)]) * 100.0

    def muzzle_point(self, back, up, right):
        """The gun-space point at back, up, right cm from the muzzle: the exact inverse of from_muzzle (aim, up and right are
        one orthonormal frame), so the hand boxes can be typed in the muzzle's numbers (real2 I2#0)."""
        return self.muzzle + (-self.aim * float(back) + self.up * float(up) + self.right * float(right)) / 100.0

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
    def mag_size(self):
        return np.abs(v3(self.rig.get('magBoundsSize', [0.03, 0.12, 0.06])))

    @property
    def mag_extent(self):
        """The magazine's length along its eject axis (review B9#0, B5#1): its box turned by its seat rotation (as mag_center turns
        its centre) and projected onto the eject direction, the sum of |R column . eject| * size. The longest side only for a rig
        with no eject axis."""
        ej = v3(self.rig.get('magEject', [0, 0, 0]))
        if np.linalg.norm(ej) < 1e-6:
            return float(max(self.mag_size))
        R = quat_to_mat3(self.rig.get('magSeatRot', [0, 0, 0, 1]))
        return float(np.sum(np.abs(R.T @ norm(ej)) * self.mag_size))

    @property
    def mag_length(self):
        return self.mag_extent * 1.15 + 0.03

    def mag_center(self):
        q = self.rig.get('magSeatRot', [0, 0, 0, 1])
        c = v3(self.rig.get('magSeatPos', [0, 0, 0])) + quat_to_mat3(q) @ v3(self.rig.get('magBoundsCenter', [0, 0, 0]))
        return (self.hinge_m @ np.append(c, 1.0))[:3] if self.rig.get('seatOnHinge') else c

    def _mag_eject_rest(self):
        return norm(self.rig.get('magEject', [0, -1, 0]))

    def mag_eject(self):
        """The magazine's way out now: turned with the barrels when it sits in them (seatOnHinge, as the mod seats it)."""
        e = self._mag_eject_rest()
        return norm(self.hinge_m[:3, :3] @ e) if self.rig.get('seatOnHinge') else e

    def hinge_point(self, p):
        """A gun-space point on the cover or barrels where it is now (turned about the hinge by the cover's openness)."""
        return (self.hinge_m @ np.append(v3(p), 1.0))[:3]

    def hinge_dir(self, d):
        return self.hinge_m[:3, :3] @ v3(d)

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
        ej = self._mag_eject_rest() * self.mag_length
        pivot = v3(self.rig.get('hingePivot', [0, 0, 0]))
        axis = -v3(self.rig.get('hingeAxis', [1, 0, 0]))  # a rotation axis turned right-handed: (-x, -y, z) of Unity's
        hinge = trs([0, 0, 0], quat_axis_angle(axis, float(self.rig.get('hingeAngle', 0)) * self.cover)) if np.linalg.norm(axis) > 1e-6 else np.eye(4)
        about = np.eye(4)
        about[:3, 3] = pivot
        back = np.eye(4)
        back[:3, 3] = -pivot
        hinge_m = about @ hinge @ back
        self.hinge_m = hinge_m
        on_hinge = bool(self.rig.get('seatOnHinge'))
        for n in self.nodes:
            m = np.eye(4)
            if n.role in ('slide', 'companion', 'bolt'):
                m[:3, 3] = st * self.slide
            elif n.role == 'handle':
                m[:3, 3] = ht * self.slide
            elif n.role == 'magazine':
                # A magazine that waits parked away at rest (the M1's clip) is drawn on its seat.
                m[:3, 3] = ej * self.mag + self.mag_parked
            if n.role == 'hinge' or (self.rig.get('hinge') and _prefix(n.path, self.rig.get('hinge'))) or (on_hinge and n.role == 'magazine'):
                m = hinge_m @ m
            n.sim = m

    def home_matrix(self, node):
        """Where a part is drawn at rest (every part home): the export's own place, except a magazine parked away at rest
        (the M1's clip), which is drawn on its seat."""
        m = np.eye(4)
        if node.role == 'magazine':
            m[:3, 3] = self.mag_parked
        return m

    def rack_point(self):
        """Where the hand takes the slide or handle now (gun space)."""
        st = v3(self.rig.get('slideTravel', [0, 0, 0]))
        ht = v3(self.rig.get('handleTravel', [0, 0, 0]))
        travel = ht if self.rig.get('handle') and np.linalg.norm(ht) > 1e-5 else st
        return v3(self.rig['rackPoint']) + travel * self.slide

    def fresh_matrix(self, out):
        """Where the fresh magazine is drawn, out = 1 at the end of its pull and 0 seated: on the seat's line, from where its
        parts wait (a parked clip, the M1's, is drawn on the seat) and turned with the barrels when it sits in them."""
        m = np.eye(4)
        m[:3, 3] = self._mag_eject_rest() * self.mag_length * out + self.mag_parked
        return self.hinge_m @ m if self.rig.get('seatOnHinge') else m

    def _part_cache(self):
        """Every part's place, its inverse and its box, stacked for nearest_part (review I5#6). Rebuilt when a part's place
        changed: set_state gives each part a new place array, and the cache keeps the ones it was built from and compares."""
        sims = [n.sim for n in self.nodes]
        if self._parts is None or len(self._parts[0]) != len(sims) or any(a is not b for a, b in zip(self._parts[0], sims)):
            sims_arr = np.array(sims, dtype=np.float64).reshape(-1, 4, 4)
            inv = np.tile(np.eye(4), (len(sims), 1, 1))
            live = np.ones(len(sims), dtype=bool)
            for i, s in enumerate(sims_arr):
                try:
                    inv[i] = np.linalg.inv(s)
                except np.linalg.LinAlgError:
                    live[i] = False  # a part scaled to nothing cannot be picked
            bmin = np.array([n.bmin for n in self.nodes], dtype=np.float64).reshape(-1, 3)
            bmax = np.array([n.bmax for n in self.nodes], dtype=np.float64).reshape(-1, 3)
            vol = np.prod(np.maximum(bmax - bmin, 1e-4), axis=1)
            body = np.array([n.role == 'body' for n in self.nodes], dtype=bool)
            self._parts = (sims, sims_arr, inv, live, bmin, bmax, vol, body)
        return self._parts

    def nearest_part(self, p_gun):
        """The part nearest a gun-space point and the distance to its box (0 inside it). Hidden and away parts are skipped.
        Parts at the same distance (a point inside several boxes) give the smallest box (review B5#0). A point inside the big
        body box but within 1 cm of a smaller part's box is on that part (review B7#0: the body box holds the magazine seat
        and the charging handle's area)."""
        _, sims_arr, inv, live, bmin, bmax, vol, body = self._part_cache()
        shown = np.array([not (n.hidden or n.away) for n in self.nodes], dtype=bool)
        ok = live & shown
        if not ok.any():
            return None, 1e9
        p = np.append(v3(p_gun), 1.0)
        local = np.einsum('nij,j->ni', inv, p)[:, :3]
        q = np.clip(local, bmin, bmax)
        at = np.einsum('nij,nj->ni', sims_arr, np.append(q, np.ones((len(q), 1)), axis=1))[:, :3]
        d = np.where(ok, np.linalg.norm(at - p[:3], axis=1), np.inf)
        # The nearest, and among those within 0.1 mm of it the smallest box (a full tie: the first part in order).
        near = np.flatnonzero(d <= d.min() + 1e-4)
        bi = int(near[np.argmin(vol[near])])
        if body[bi] and d[bi] <= 1e-4:
            smaller = np.flatnonzero(ok & ~body & (d <= 0.01) & (vol < vol[bi]))
            if smaller.size:
                bi = int(smaller[np.lexsort((d[smaller], vol[smaller]))[0]])
        return self.nodes[bi], float(d[bi])


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
        for h in self.hands():
            # The game's grip thumb rule goes by the gun's class (glove.game_curls), so the hands are drawn again for it.
            h.pistol = bool(self.weapon.rig.get('isPistol'))
            h.machine_gun = bool(self.weapon.rig.get('machineGun'))
            h.update_mesh()

    def extra_draws(self):
        """(node, model) pairs drawn besides the weapon: the fresh magazine coming in."""
        w = self.weapon
        if w is None or w.fresh is None:
            return []
        return [(n, w.W @ w.fresh) for n in w.nodes if n.role == 'magazine' and not n.hidden]  # fresh: Weapon.fresh_matrix

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
        if w.muzzle_known:
            cross(w.muzzle, (1.0, 0.3, 0.3, 1))
            arrow(w.muzzle, w.aim * 0.06, (1.0, 0.3, 0.3, 1))
        seat = v3(r.get('magSeatPos', [0, 0, 0]))
        cross(seat, (1.0, 0.9, 0.3, 1), 0.008)
        arrow(w.mag_center(), w.mag_eject() * w.mag_length, (1.0, 0.9, 0.3, 0.8))
        if r.get('hingeKind', 'None') != 'None':
            cross(w.hinge_point(r['hingeGrab']), (0.8, 0.5, 1.0, 1))
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
        hand.thumb_set = False
        r = w.rig
        pistol = r.get('isPistol')
        if target == 'grip':
            # Exactly as the mod holds a gun (VRHand.GunFrame with the automatic gun angle, HeldWeapon.BeforeRender): the
            # glove's palm frame is the gun's frame at the grip point: the barrel along the fingers' direction, the top along
            # the thumb's side, the palm centre on the grip point. Then settled on the grip as the glove does in VR.
            hand.rotation = frame_quat(w.dir_to_world(w.right), w.dir_to_world(w.up))
            hand.position = w.to_world(w.grip)
            curls = r.get('gripCurls')
            if curls and len(curls) == 5:
                hand.set_pose("weapon's own grip", [0.35] + list(curls))
            else:
                hand.set_pose('Machine gun grip' if r.get('machineGun') else 'Pistol grip' if pistol else 'Rifle grip')
            hand.on_part = 'grip'
            self.settle(hand)
        elif target == 'support':
            at = v3(r['support']) if r.get('hasSupport') else w.grip + w.aim * 0.2
            self.place(hand, at, w.up, w.aim)
            curls = r.get('supportCurls')
            if curls and len(curls) == 5:
                hand.set_pose("weapon's own support", [0.35] + list(curls))
            else:
                hand.set_pose('Pistol cup' if pistol else 'Handguard')
            hand.on_part = 'handguard'
            if not r.get('hasSupport'):
                hand.role = ''  # (a place of the sandbox's own: not saved as the game's support point)
            self.settle(hand)
        elif target == 'rack':
            p = w.rack_point()
            off = p - w.muzzle
            off = off - w.aim * np.dot(off, w.aim)
            out = norm(off) if np.linalg.norm(off) > 0.015 else w.up
            if np.dot(out, w.up) > 0.7:
                thumb = -w.aim  # on top: the thumb back along the gun, the fingers across it
            else:
                # On the side: the fingers go up over the part (ReloadInteractions.RackHandPose: fingers = up for side parts),
                # the thumb along the barrel. (fingers = thumb x palm axis; the left glove is mirrored. The two were swapped and
                # the hand hung upside down, fingers down from above: real2 I6#3, seen on the AS Val, 2026-10-18.)
                thumb = np.cross(w.up, out) if hand.side == 'left' else np.cross(out, w.up)
            self.place(hand, p + out * 0.02, -out, thumb)
            pump = r.get('action') == 'ShellFed' and not r.get('handle')
            hand.set_pose('Pistol slide' if pistol else 'Pump' if pump else 'Charging handle' if r.get('handle') else 'Bolt handle')
            hand.on_part = 'slide / handle'
        elif target == 'magazine':
            ej = w.mag_eject()
            base = w.mag_center() + ej * (w.mag_extent * 0.5 + 0.01) + ej * w.mag_length * w.mag
            self.place(hand, base, -ej, w.aim)
            hand.set_pose('Pistol magazine' if pistol else 'Box magazine' if r.get('machineGun') else 'Rifle magazine')
            hand.on_part = 'magazine'
        elif target == 'cover':
            if r.get('hingeKind', 'None') == 'None':
                return
            # The latch where the cover is now: the mod turns its grab point with the cover (CoverGrabWorld).
            self.place(hand, w.hinge_point(v3(r['hingeGrab']) + w.up * 0.02), w.hinge_dir(-w.up), w.hinge_dir(-w.aim))
            hand.set_pose('Cover lift')
            hand.on_part = 'cover'

    def move_weapon(self, M):
        """The weapon moved by a world transform M (4x4); the hands on it (with a job there) go with it, so poses stay."""
        w = self.weapon
        if w is None:
            return
        w.set_place(M @ w.P)
        q = mat3_to_quat(M[:3, :3])
        for h in self.hands():
            if h.role or h.on_part:
                h.position = (M @ np.append(h.position, 1.0))[:3]
                h.rotation = quat_norm(quat_mul(q, h.rotation))
                if h._settled_at is not None:
                    h.set_settle(M[:3, :3] @ h.settle)

    def put_hand_on(self, hand, point_world, normal_world):
        """The palm onto the surface at this point (from the laser), facing it, the thumb toward the gun's top (or its back
        when the surface faces up), then settled as the glove settles."""
        w = self.weapon
        n = norm(normal_world)
        up_w, aim_w = w.dir_to_world(w.up), w.dir_to_world(w.aim)
        t = up_w - n * np.dot(up_w, n)
        if np.linalg.norm(t) < 0.3:
            t = -aim_w - n * np.dot(-aim_w, n)
        x = -n if hand.side == 'left' else n  # the palm faces the surface: -X (right) / +X (left, mirrored) toward it
        hand.rotation = frame_quat(x, t)
        hand.position = np.asarray(point_world, dtype=np.float64) + n * 0.03
        hand.on_part = ''
        hand.role = self.contact_role(hand)
        self.settle(hand)

    # ---- the glove settling on what it holds ----------------------------------------------------------------------------

    # The palm's skin (glove.bin's PalmSkin, palm space, right-handed): 12 points about 3 cm out along the palm's side.
    PALM_SKIN = np.array([[-0.03, -0.028, 0.035], [-0.039, 0.0, 0.035], [-0.039, 0.028, 0.035], [-0.034, -0.028, 0.012],
                          [-0.036, 0.0, 0.012], [-0.036, 0.028, 0.012], [-0.03, -0.028, -0.012], [-0.027, 0.0, -0.012],
                          [-0.036, 0.028, -0.012], [-0.026, -0.028, -0.034], [-0.025, 0.0, -0.034], [-0.029, 0.028, -0.034]])
    settle_on = True

    def ray_weapon(self, o, d, reach):
        """The nearest hit of a world ray on the weapon within reach, or None."""
        from mathutil import ray_triangles
        w = self.weapon
        best = None
        for node in w.nodes:
            if node.hidden or node.away or not len(node.tris):
                continue
            try:
                inv = np.linalg.inv(w.node_matrix(node))
            except np.linalg.LinAlgError:
                continue  # a part scaled to nothing cannot be hit
            lo = (inv @ np.append(o, 1))[:3]
            ld = (inv @ np.append(d, 0))[:3]
            # Only triangles near the ray's segment (a quick box test first).
            lo_end = lo + ld * reach
            if np.any(np.minimum(lo, lo_end) > node.bmax + 0.01) or np.any(np.maximum(lo, lo_end) < node.bmin - 0.01):
                continue
            t, _ = ray_triangles(lo, ld, node.tris)
            if t is not None and t <= reach and (best is None or t < best):
                best = t
        return best

    def settle(self, hand):
        """
        The glove onto the gun's surface as in VR (HandModel: the palm pushed out while its skin would sink in, drawn in up to
        2 cm while a gap is left): along the palm's normal by the smallest gap of its skin points, within 2 cm either way.
        """
        hand._settled_at = None
        if self.weapon is None or not self.settle_on:
            return 0.0
        m = hand.model_matrix()
        normal = norm(m[:3, :3] @ np.array([-1.0, 0, 0]))  # the glove's palm side (mirrored for the left hand by its matrix)
        back = 0.03
        gaps = []
        for p in self.PALM_SKIN:
            pw = (m @ np.append(p, 1.0))[:3]
            t = self.ray_weapon(pw - normal * back, normal, back + 0.06)
            if t is not None:
                gaps.append(t - back)
        if not gaps:
            return 0.0
        # As HandModel.PalmOffset: touching, out in 2 mm steps until it no longer does (4 cm at most); else in by 2 mm steps
        # while a gap is left, only when the skin touches within 2 cm (a gun farther off is left alone). Only the drawn glove
        # moves: in VR the hand (the gun's grip point) stays where it is and the glove is drawn settled; saved, the hand's
        # own place is kept.
        g = min(gaps)
        if g <= 0:
            move = -min(0.04, math.ceil(-g / 0.002 - 1e-9) * 0.002)
        elif g <= 0.0201:
            move = math.ceil(g / 0.002 - 1e-9) * 0.002 - 0.002
        else:
            move = 0.0
        hand.set_settle(normal * move)
        return move

    def contact_role(self, hand):
        """The job of a hand moved by hand: what it is on (the left hand on the gun's body or handguard supports it)."""
        w = self.weapon
        if w is None:
            return ''
        p = w.to_gun(hand.position)[:3]
        part, dist = w.nearest_part(p)
        if part is None or dist > 0.06:
            return ''
        job = {'magazine': 'magazine', 'slide': 'rack', 'handle': 'rack', 'bolt': 'rack', 'companion': 'rack', 'hinge': 'cover'}.get(part.role)
        if hand.side == 'right':
            # The grip: on the gun's body near the analysed grip (the game holds the gun wherever the right hand is saved).
            return 'grip' if job is None and np.linalg.norm(p - w.grip) < 0.12 else (job or '')
        if job is not None:
            return job  # the part the hand is on is its job, even where the support point is (review B5#0: a magazine by 5 cm)
        return 'support' if self.support_ok(p) else ''

    def held_grip(self):
        """The grip point the game holds the gun by, in gun space: the gun hand's place when it is saved as the grip (the game
        takes that point, review B10#0), else the analysed grip."""
        w = self.weapon
        return w.to_gun(self.right.position)[:3] if self.right.role == 'grip' else w.grip

    def support_ok(self, p):
        """Whether a left hand at this gun-space point is a support the game can take (review B10#1, B10#7): never on a gun with
        no support point; a pistol's cup within 12 cm of its grip; a long gun's support 7 cm or more ahead of the grip the game
        holds (held_grip) along the aim (the analysis keeps it there, on the forend), or within 5 cm of the analysed support."""
        w = self.weapon
        r = w.rig
        if not r.get('hasSupport'):
            return False
        if r.get('isPistol'):
            return float(np.linalg.norm(p - w.grip)) < 0.12
        return float(np.dot(p - self.held_grip(), w.aim)) >= 0.07 or float(np.linalg.norm(p - v3(r['support']))) < 0.05

    def pose_warnings(self):
        """What the game would do oddly with these hands, said before saving."""
        w = self.weapon
        out = []
        if w is None:
            return out
        p = w.to_gun(self.right.position)[:3]
        part, dist = w.nearest_part(p)
        off = float(np.linalg.norm(p - w.grip))
        if off > 0.08 or (part is not None and part.role != 'body' and dist < 0.03):
            where = f"on the {ROLE_NAMES.get(part.role, part.role)} '{part.short}'" if part is not None and dist < 0.06 else 'off the gun'
            # The game holds the gun by the right hand only when it is saved as the grip (review B10#0).
            held = 'the game will hold the gun there.' if self.right.role == 'grip' else 'the game keeps the analysed grip (this hand is not saved as the grip).'
            out.append(f'The right hand is {where}, {off * 100:.0f} cm from the analysed grip: {held}')
        elif self.right.role != 'grip':
            # Within 8 cm of the grip but on a magazine, a slide or in the air: not the grip the game would take (review B10#0).
            out.append(f"The right hand is {off * 100:.0f} cm from the analysed grip but its job is '{self.right.role or 'none'}', not the grip: "
                       'the game keeps the analysed grip, not this hand.')
        p_left = w.to_gun(self.left.position)[:3]
        if self.left.role != 'support':
            out.append('The left hand is not on the handguard (its job: ' + (self.left.role or 'none') +
                       '): the game keeps the analysed support point and the support hand\'s fingers.')
        elif not self.support_ok(p_left):
            # A support behind the grip turns the gun away from the off hand (review B10#1).
            if not w.rig.get('hasSupport'):
                why = 'on a gun with no support point'
            elif w.rig.get('isPistol'):
                why = f'{float(np.linalg.norm(p_left - w.grip)) * 100:.0f} cm from the pistol grip'
            else:
                ahead = float(np.dot(p_left - self.held_grip(), w.aim)) * 100
                why = f"{abs(ahead):.0f} cm {'behind' if ahead < 0 else 'ahead of'} the grip (a long gun's support is 7 cm or more ahead of it)"
            out.append(f'The left hand is saved as the support but sits {why}: the game keeps the analysed support point.')
        # The game clamps a held curl to 0.15-0.92 (HandModel.Pose, glove._held): the grip's four fingers (its thumb is its own),
        # the support hand's five. A negative curl is the game's "not measured", not a value (review B10#2).
        names = ('thumb', 'index', 'middle', 'ring', 'little')
        for side, h in (('right', self.right), ('left', self.left)):
            fingers = (1, 2, 3, 4) if h.role == 'grip' else (0, 1, 2, 3, 4) if h.role == 'support' else ()
            bad = [f'{names[i]} {h.curls[i]:.2f}' for i in fingers if h.curls[i] >= 0 and not 0.15 <= h.curls[i] <= 0.92]
            if bad:
                out.append(f"The {side} hand's curls outside 0.15-0.92 ({', '.join(bad)}): the game clamps them, so its hand differs from this one.")
        return out

    # ---- saving --------------------------------------------------------------------------------------------------------

    def hand_in_gun(self, hand):
        """A hand's place and turn in gun space (right-handed) and in the mod's gun space (left-handed)."""
        w = self.weapon
        p = w.to_gun(hand.position)[:3]
        q = quat_norm(quat_mul(quat_inv(w.Wq), hand.rotation))
        part, dist = w.nearest_part(p)
        return {
            'gun': {'position': p.tolist(), 'rotation': q.tolist()},
            'mod': {'position': Weapon.unity(p).round(6).tolist(), 'rotation': [round(-q[0], 7), round(-q[1], 7), round(q[2], 7), round(q[3], 7)]},
            'fromMuzzleCm': w.from_muzzle(p).round(2).tolist(),
            'pose': hand.pose_name, 'thumb': round(hand.thumb, 4), 'curls': [round(c, 4) for c in hand.curls],
            'thumbSet': bool(hand.thumb_set),
            # What the hand is on: its snap, or the part it touches within the 6 cm reach its job is worked out by (review I9#2).
            'on': hand.on_part or (part.short if part is not None and dist <= 0.06 else ''),
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

    @staticmethod
    def read_pose_document(doc):
        """A pose file's contents checked and put in order, or ValueError saying what is wrong. Nothing is changed here."""
        def numbers(v, n, what):
            if not isinstance(v, (list, tuple)) or len(v) != n:
                raise ValueError(f'{what}: expected {n} numbers')
            try:
                out = np.array([float(x) for x in v], dtype=np.float64)
            except (TypeError, ValueError):
                raise ValueError(f'{what}: not numbers') from None
            if not np.all(np.isfinite(out)):
                raise ValueError(f'{what}: not finite')
            return out

        if not isinstance(doc, dict) or not isinstance(doc.get('hands'), dict):
            raise ValueError('not a pose file (no hands in it)')
        hands = {}
        for side in ('right', 'left'):
            h = doc['hands'].get(side)
            if h is None:
                continue
            if not isinstance(h, dict):
                raise ValueError(f'{side} hand: not an object')
            pos = rot = None
            if isinstance(h.get('mod'), dict):
                mp = numbers(h['mod'].get('position'), 3, f'{side} hand mod.position')
                mq = numbers(h['mod'].get('rotation'), 4, f'{side} hand mod.rotation')
                pos, rot = np.array([mp[0], mp[1], -mp[2]]), np.array([-mq[0], -mq[1], mq[2], mq[3]])
            if isinstance(h.get('gun'), dict):
                gp = numbers(h['gun'].get('position'), 3, f'{side} hand gun.position')
                gq = numbers(h['gun'].get('rotation'), 4, f'{side} hand gun.rotation')
                # 'gun' keeps the sandbox's full precision; 'mod' (what the game reads, rounded) wins when it was edited.
                if pos is None or (np.linalg.norm(gp - pos) < 1e-4 and 1.0 - abs(float(np.dot(quat_norm(gq), quat_norm(rot)))) < 1e-6):
                    pos, rot = gp, gq
            if pos is None:
                raise ValueError(f'{side} hand: no place (mod or gun)')
            if np.linalg.norm(rot) < 1e-6:
                raise ValueError(f'{side} hand: a zero rotation')
            curls = h.get('curls', [0.1, 0.12, 0.16, 0.2, 0.24])
            if not isinstance(curls, (list, tuple)) or not curls:
                raise ValueError(f'{side} hand: curls must be a list')
            curls = [float(min(1.0, max(0.0, float(c)))) for c in list(curls)[:5]]
            curls += [curls[-1]] * (5 - len(curls))
            hands[side] = {'position': pos, 'rotation': quat_norm(rot), 'curls': curls,
                           'thumb': float(min(1.0, max(0.0, float(h.get('thumb', 0) or 0)))),
                           'pose': str(h.get('pose', 'Custom')), 'on': str(h.get('on', '') or ''), 'role': str(h.get('role', '') or ''),
                           'thumbSet': bool(h.get('thumbSet', False))}
        parts = doc.get('parts') if isinstance(doc.get('parts'), dict) else {}

        def part(k):
            try:
                return float(min(1.0, max(0.0, float(parts.get(k, 0) or 0))))
            except (TypeError, ValueError):
                return 0.0
        return {'hands': hands, 'parts': {'slide': part('slide'), 'magazine': part('magazine'), 'cover': part('cover')}}

    def apply_pose_document(self, doc):
        w = self.weapon
        checked = doc if isinstance(doc, dict) and doc.get('_checked') else dict(self.read_pose_document(doc), _checked=True)
        for side, hand in (('right', self.right), ('left', self.left)):
            h = checked['hands'].get(side)
            if not h:
                continue
            hand.position = w.to_world(h['position'])
            hand.rotation = quat_norm(quat_mul(w.Wq, h['rotation']))
            hand.pose_name = h['pose']
            hand.thumb = h['thumb']
            hand.curls = list(h['curls'])
            hand.on_part = h['on']
            hand.role = h['role']
            hand.thumb_set = h['thumbSet']
            hand.update_mesh()
        parts = checked['parts']
        w.set_state(parts['slide'], parts['magazine'], parts['cover'])
        if self.left.role == 'support' and not self.support_ok(w.to_gun(self.left.position)[:3]):
            # A saved support the game cannot take (behind the grip, or on a gun with no support point) gets the job its place
            # gives, as a drag would give it (review B10#1, B10#7): the parts are placed first, so the nearest part is the real one.
            self.left.on_part = ''
            self.left.role = self.contact_role(self.left)
            self.left.update_mesh()
        for hand in (self.right, self.left):
            if hand.role in ('grip', 'support'):
                self.settle(hand)


def _corners(a, b):
    return [np.array([x, y, z]) for x in (a[0], b[0]) for y in (a[1], b[1]) for z in (a[2], b[2])]
