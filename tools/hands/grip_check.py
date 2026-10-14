"""Offline check of how the glove holds things.

Reproduces HandModel's hand on a held solid (palm settling onto it, then each finger closing joint by joint until it
touches, as SolveFinger does) against simple solids placed the way the mod places them: a magazine in the hand
(MagazineProp.HoldPose), a slide or bolt being racked (the grasp anchor), a handguard. Reports per scenario how far the
glove had to move off the tracked hand, which fingers touch, how many finger points are buried in the object, and the
fingertips' gaps. Right hand, the default glove size.

Usage: python tools/hands/grip_check.py [repo root] [scenario ...]
"""
import math
import os
import struct
import sys

import numpy as np

ROOT = sys.argv[1] if len(sys.argv) > 1 and os.path.isdir(sys.argv[1]) else os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
GROW = 0.0072          # GunVolume.Grow: held solids are grown by a finger's radius
SCALE = 0.88           # HandShape.GloveScale (GloveSize default)
FIST_HOLLOW = np.array([-0.026, 0.0, 0.034])  # VRHand.FistHollow


# ---- quaternions (x, y, z, w), Unity conventions ------------------------------------------------------------------

def qn(q):
    q = np.asarray(q, float)
    return q / np.linalg.norm(q)


def qmul(a, b):
    ax, ay, az, aw = a
    bx, by, bz, bw = b
    return np.array([aw * bx + ax * bw + ay * bz - az * by,
                     aw * by - ax * bz + ay * bw + az * bx,
                     aw * bz + ax * by - ay * bx + az * bw,
                     aw * bw - ax * bx - ay * by - az * bz])


def qinv(q):
    return np.array([-q[0], -q[1], -q[2], q[3]]) / np.dot(q, q)


def qrot(q, v):
    p = np.array([v[0], v[1], v[2], 0.0])
    return qmul(qmul(q, p), qinv(q))[:3]


def qaxis(deg, axis):
    a = math.radians(deg) * 0.5
    ax = np.asarray(axis, float) / np.linalg.norm(axis)
    return np.array([*(ax * math.sin(a)), math.cos(a)])


def qslerp(a, b, t):
    a, b = qn(a), qn(b)
    d = np.dot(a, b)
    if d < 0:
        b, d = -b, -d
    if d > 0.9995:
        return qn(a + t * (b - a))
    th = math.acos(min(1.0, d))
    return (math.sin((1 - t) * th) * a + math.sin(t * th) * b) / math.sin(th)


def qfrom_mat(m):
    t = m[0, 0] + m[1, 1] + m[2, 2]
    if t > 0:
        s = math.sqrt(t + 1) * 2
        return qn([(m[2, 1] - m[1, 2]) / s, (m[0, 2] - m[2, 0]) / s, (m[1, 0] - m[0, 1]) / s, 0.25 * s])
    i = int(np.argmax([m[0, 0], m[1, 1], m[2, 2]]))
    j, k = (i + 1) % 3, (i + 2) % 3
    s = math.sqrt(1 + m[i, i] - m[j, j] - m[k, k]) * 2
    q = [0.0, 0.0, 0.0, 0.0]
    q[i] = 0.25 * s
    q[j] = (m[j, i] + m[i, j]) / s
    q[k] = (m[k, i] + m[i, k]) / s
    q[3] = (m[k, j] - m[j, k]) / s
    return qn(q)


def look_rotation(fwd, up):
    z = np.asarray(fwd, float) / np.linalg.norm(fwd)
    x = np.cross(up, z)
    x /= np.linalg.norm(x)
    y = np.cross(z, x)
    return qfrom_mat(np.column_stack([x, y, z]))


def without_curl(open_q, pose, axis):
    q = qmul(qinv(open_q), pose)
    along = np.dot(q[:3], axis)
    twist = np.array([*(axis * along), q[3]])
    n = np.linalg.norm(twist)
    if n < 1e-6:
        return open_q
    return qmul(pose, qinv(twist / n))


# ---- the glove --------------------------------------------------------------------------------------------------

class Glove:
    def __init__(self, path):
        d = open(path, "rb").read()
        assert d[:4] == b"RFVG"
        ver, nv, ni = struct.unpack_from("<iii", d, 4)
        o = 16 + nv * 12 * 2 + nv * 8 + nv * 4 + nv * 16 + ni * 2
        self.knuckle = np.zeros((5, 3))
        self.child = np.zeros((5, 3, 3))
        self.axis = np.zeros((5, 3, 3))
        self.angle = np.zeros((5, 3))
        self.radius = np.zeros((5, 3))
        self.pad = np.zeros((5, 3))
        self.pad_dir = np.zeros((5, 3, 3))
        self.open = np.zeros((5, 3, 4))
        self.fist = np.zeros((5, 3, 4))
        self.relaxed = np.zeros((5, 3, 4))
        self.point = np.zeros((5, 3, 4))
        for f in range(5):
            for s in range(3):
                v = struct.unpack_from("<35f", d, o)
                o += 35 * 4
                if s == 0:
                    self.knuckle[f] = v[0:3]
                self.child[f, s] = v[7:10]
                self.axis[f, s] = np.asarray(v[10:13]) / np.linalg.norm(v[10:13])
                self.angle[f, s], self.radius[f, s], self.pad[f, s] = v[13:16]
                self.pad_dir[f, s] = np.asarray(v[16:19]) / max(1e-9, np.linalg.norm(v[16:19]))
                self.open[f, s] = qn(v[19:23])
                self.fist[f, s] = qn(v[23:27])
                self.relaxed[f, s] = qn(v[27:31])
                self.point[f, s] = qn(v[31:35])
        self.palm_skin = np.array(struct.unpack_from("<36f", d, o)).reshape(12, 3)
        o += 36 * 4
        mnx, mxx, mny, mxy, mnz, mxz = struct.unpack_from("<6f", d, o)
        self.palm_min = np.array([mnx, mny, mnz])
        self.palm_max = np.array([mxx, mxy, mxz])
        # As HandShape.LoadGlove: curl 0 is the relaxed hand with its curl taken out (the thumb's base has its own rest).
        self.rest = self.open.copy()
        for f in range(5):
            for s in range(1 if f == 0 else 0, 3):
                self.rest[f, s] = without_curl(self.open[f, s], self.relaxed[f, s], self.axis[f, s])
        self.thumb_rest = self.relaxed[0, 0]
        self.thumb_fist = self.fist[0, 0]
        self.pad_reach = np.maximum(0.0, self.pad - GROW / SCALE)
        # Palm skin points pushed in by the grow (the solid is grown instead), as HandModel does.
        self.palm_skin_in = self.palm_skin + np.array([GROW / SCALE, 0, 0])


class Hand:
    """One glove posed like HandModel: root at the tracked palm centre (world metres), scaled, fingers chained."""

    def __init__(self, glove, root_pos, root_rot, thumb_base_t):
        self.g = glove
        self.pos = np.asarray(root_pos, float)
        self.rot = root_rot
        self.thumb_base = qslerp(glove.thumb_rest, glove.thumb_fist, thumb_base_t)
        self.joint = np.zeros((5, 3))

    def world(self, local):
        return self.pos + qrot(self.rot, np.asarray(local) * SCALE)

    def vec(self, local):
        return qrot(self.rot, np.asarray(local) * SCALE)

    def chain(self, f, c):
        """Joint positions (world) and per-segment pad offsets (world) for curls c[0..2], as HandModel.Chain."""
        g = self.g
        p = g.knuckle[f].copy()
        r = np.array([0.0, 0.0, 0.0, 1.0])
        pts = [self.world(p)]
        pads = []
        for s in range(3):
            rest = self.thumb_base if f == 0 and s == 0 else g.rest[f, s]
            r = qmul(qmul(r, rest), qaxis(g.angle[f, s] * c[s], g.axis[f, s]))
            p = p + qrot(r, g.child[f, s])
            pts.append(self.world(p))
            pads.append(self.vec(qrot(r, g.pad_dir[f, s]) * g.pad_reach[f, s]) if g.pad_reach[f, s] > 0 else np.zeros(3))
        return pts, pads

    @property
    def palm_normal(self):
        return qrot(self.rot, np.array([-1.0, 0, 0]))


def segment_touches(solid, pts, pads, s):
    a, b = pts[s] + pads[s], pts[s + 1] + pads[s]
    end = 1.12 if s == 2 else 1.0
    return solid(a + (b - a) * 0.4) or solid(a + (b - a) * 0.75) or solid(a + (b - a) * end)


def first_touch(hand, solid, f, c, inside0):
    pts, pads = hand.chain(f, c)
    for s in range(3):
        if not inside0[s] and segment_touches(solid, pts, pads, s):
            return s
    return -1


def solve_finger(hand, solid, f, goal, opens_out):
    """HandModel.SolveFinger: closes joint by joint, stopping each at its touch; returns (curls, touched)."""
    steps = 10
    start = 0.0
    if opens_out:
        pts, pads = hand.chain(f, [0.0, 0.0, 0.0])
        if segment_touches(solid, pts, pads, 0) or segment_touches(solid, pts, pads, 1):
            c = -0.05
            while c >= -0.351:
                pts, pads = hand.chain(f, [c, c, c])
                if not segment_touches(solid, pts, pads, 0) and not segment_touches(solid, pts, pads, 1):
                    start = c
                    break
                c -= 0.05
    cur = [start, start, start]
    locked = [False, False, False]
    pts, pads = hand.chain(f, cur)
    inside0 = [segment_touches(solid, pts, pads, s) for s in range(3)]
    step = 1
    guard = 0
    while step <= steps and not locked[2] and guard < steps + 4:
        guard += 1
        t = start + (goal - start) * step / steps
        nxt = [cur[s] if locked[s] else t for s in range(3)]
        if first_touch(hand, solid, f, nxt, inside0) < 0:
            cur = nxt
            step += 1
            continue
        lo, hi = 0.0, 1.0
        for _ in range(5):
            mid = (lo + hi) * 0.5
            probe = [cur[s] + (nxt[s] - cur[s]) * mid for s in range(3)]
            if first_touch(hand, solid, f, probe, inside0) < 0:
                lo = mid
            else:
                hi = mid
        hit = first_touch(hand, solid, f, [cur[s] + (nxt[s] - cur[s]) * hi for s in range(3)], inside0)
        cur = [cur[s] + (nxt[s] - cur[s]) * lo for s in range(3)]
        for s in range(3):
            locked[s] = locked[s] or hit >= s
        # the free joints still have this step to close
    touched = any(locked)
    return cur, touched


def palm_touches(hand, solid, offset):
    n = hand.palm_normal
    return any(solid(hand.world(p) + n * offset) for p in hand.g.palm_skin_in)


def palm_offset(hand, solid):
    """HandModel.PalmOffset: along the palm's normal to just touch (negative: out of it)."""
    if palm_touches(hand, solid, 0.0):
        o = -0.002
        while o >= -0.04:
            if not palm_touches(hand, solid, o):
                return o
            o -= 0.002
        return -0.04
    o = 0.002
    while o <= 0.0201:
        if palm_touches(hand, solid, o):
            return o - 0.002
        o += 0.002
    return 0.0


# ---- solids -------------------------------------------------------------------------------------------------------

class Box:
    def __init__(self, centre, rot, half):
        self.c = np.asarray(centre, float)
        self.rot = rot
        self.inv = qinv(rot)
        self.h = np.asarray(half, float)

    def local(self, p):
        return qrot(self.inv, p - self.c)

    def inside(self, p, grow=0.0):
        q = np.abs(self.local(p))
        return bool(np.all(q <= self.h + grow))

    def distance(self, p):
        q = np.abs(self.local(p)) - self.h
        return float(np.linalg.norm(np.maximum(q, 0.0)) + min(max(q[0], q[1], q[2]), 0.0))


class Cylinder:
    """Along its local +Z, radius r, half length hl."""

    def __init__(self, centre, rot, r, hl):
        self.c = np.asarray(centre, float)
        self.rot = rot
        self.inv = qinv(rot)
        self.r = r
        self.hl = hl

    def local(self, p):
        return qrot(self.inv, p - self.c)

    def inside(self, p, grow=0.0):
        q = self.local(p)
        return math.hypot(q[0], q[1]) <= self.r + grow and abs(q[2]) <= self.hl + grow

    def distance(self, p):
        q = self.local(p)
        dr = math.hypot(q[0], q[1]) - self.r
        dz = abs(q[2]) - self.hl
        return float(math.hypot(max(dr, 0), max(dz, 0)) + min(max(dr, dz), 0))


class Union:
    def __init__(self, *parts):
        self.parts = parts

    def inside(self, p, grow=0.0):
        return any(s.inside(p, grow) for s in self.parts)

    def distance(self, p):
        return min(s.distance(p) for s in self.parts)


# ---- a hold -------------------------------------------------------------------------------------------------------

def hold(glove, obj, palm_pos, palm_rot, goals, thumb_base, opens_out=(True, False, False, False, False), settle=True, grasp=None):
    """The hand on obj as HandModel.Apply leaves it: grasp-anchor shift, palm settled, fingers closed to goals."""
    root = palm_pos + qrot(palm_rot, -FIST_HOLLOW)
    hand = Hand(glove, root, palm_rot, thumb_base)
    if grasp is not None:
        anchor_local, anchor_world = grasp
        want = anchor_world - hand.world(anchor_local / SCALE) if anchor_local is not None else np.zeros(3)
        n = np.linalg.norm(want)
        if n > 0.05:
            want *= 0.05 / n
        hand.pos = hand.pos + want
    grown = lambda p: obj.inside(p, GROW)
    push = palm_offset(hand, grown) if settle else 0.0
    hand.pos = hand.pos + hand.palm_normal * push
    touched = []
    for f in range(5):
        c, t = solve_finger(hand, grown, f, goals[f], opens_out[f])
        hand.joint[f] = c
        touched.append(t)
    return hand, push


def measure(hand, obj):
    """As HandModel.Measure: buried bone points (in the ungrown object), fingers touching (a little more curl meets it)."""
    buried = samples = 0
    touching = []
    tips = []
    for f in range(5):
        c = hand.joint[f]
        pts, pads = hand.chain(f, c)
        for s in range(3):
            for i in range(1, 5):
                samples += 1
                if obj.inside(pts[s] + (pts[s + 1] - pts[s]) * (i * 0.25) + pads[s]):
                    buried += 1
        more = [min(1.0, x + 0.04) for x in c]
        p2, pd2 = hand.chain(f, more)
        grown = lambda p: obj.inside(p, GROW)
        touching.append(any(segment_touches(grown, p2, pd2, s) for s in range(3)))
        a, b = pts[2] + pads[2], pts[3] + pads[2]
        tip = a + (b - a) * 1.0
        tips.append(obj.distance(tip) - hand.g.radius[f, 2] * SCALE)
    mid_x = (hand.g.palm_min[0] + hand.g.palm_max[0]) * 0.5
    for p in hand.g.palm_skin:
        samples += 1
        if obj.inside(hand.world([mid_x, p[1], p[2]])):
            buried += 1
    return buried, samples, touching, tips


def report(name, hand, push, obj):
    buried, samples, touching, tips = measure(hand, obj)
    fingers = sum(touching[1:])
    names = "TIMRL"
    gaps = " ".join(f"{names[f]}{tips[f] * 1000:+.0f}" for f in range(5))
    print(f"{name:34s} glove moved {abs(push) * 100:4.1f} cm | touching: thumb {'yes' if touching[0] else 'no '}, fingers {fingers}/4 "
          f"| buried {buried}/{samples} | tip gaps mm {gaps}")
    return dict(push=push, buried=buried, samples=samples, fingers=fingers, thumb=touching[0], tips=tips)


# ---- scenarios ----------------------------------------------------------------------------------------------------

def palm_surface_from_grip(glove, z_lo=-1.0, z_hi=1.0, y_lo=-1.0, y_hi=1.0):
    """HandModel.PalmSurfaceFromGrip: from the controller's grip point out to the palm's skin, over the part of the palm
    an object covering z_lo..z_hi and y_lo..y_hi (palm frame, metres from the palm centre) lies against."""
    skin = None
    for p in glove.palm_skin:
        x, y, z = p * SCALE
        if z_lo - GROW <= z <= z_hi + GROW and y_lo - GROW <= y <= y_hi + GROW:
            skin = -x if skin is None else max(skin, -x)
    if skin is None:
        skin = max(-p[0] * SCALE for p in glove.palm_skin)
    return max(0.0, skin - (-FIST_HOLLOW[0]))


def magazine_hold(glove, depth, thick, length, grip_at, goals, thumb_base, palm_rot=None, on_palm=True):
    """MagazineProp.HoldPose: front along the palm's forward, insert axis toward palm up (LookRotation drops the rest),
    the point grip_at of the way from base to tip at the controller's grip point (the fist hollow); with on_palm (the
    mod since this tool was written) moved out along the palm's normal until its side rests on the palm."""
    palm_rot = palm_rot if palm_rot is not None else np.array([0.0, 0.0, 0.0, 1.0])
    fwd, up = qrot(palm_rot, [0, 0, 1]), qrot(palm_rot, [0, 1, 0])
    insert_target = (up * 0.75 + fwd * 0.65)
    rot = look_rotation(fwd, insert_target)
    held = np.array([0.0, -length / 2 + grip_at * length, 0.0])
    palm_pos = FIST_HOLLOW.copy()  # the root then sits at the origin
    centre = palm_pos - qrot(rot, held)
    if on_palm:
        # The part of the palm it covers, in the palm frame (the root sits at the origin here).
        lo_z, hi_z = centre[2] - depth / 2, centre[2] + depth / 2
        lo_y, hi_y = centre[1] - length / 2, centre[1] + length / 2
        centre = centre + qrot(palm_rot, [-1.0, 0, 0]) * (palm_surface_from_grip(glove, lo_z, hi_z, lo_y, hi_y) + thick / 2)
    obj = Box(centre, rot, [thick / 2, length / 2, depth / 2])
    return obj, palm_pos, palm_rot


def goals_for(pose_curls, trigger=0.0, grip=1.0):
    """TargetCurls for a held-object role on a controller without finger tracking, grip squeezed: input fill, the rest
    minimum, the pose's minimum, then the closing minimum (0.55, 0.9, 0.95, 0.95, 0.95)."""
    g = [grip * 0.6, trigger, grip, grip, grip]
    for f, m in enumerate([0.1, 0.12, 0.16, 0.2, 0.24]):
        g[f] = max(g[f], m)
    for f in range(5):
        g[f] = max(g[f], pose_curls[f])
    for f, m in enumerate([0.55, 0.9, 0.95, 0.95, 0.95]):
        g[f] = max(g[f], m)
    return g


POSES = {
    # From HandPoses.cs (thumb base, curls).
    "RifleMagazine": (0.55, [0.55, 0.7, 0.75, 0.78, 0.8]),
    "PistolMagazine": (0.6, [0.6, 0.45, 0.75, 0.8, 0.82]),
    "BoxMagazine": (0.35, [0.35, 0.5, 0.55, 0.58, 0.6]),
}


def main():
    glove = Glove(os.path.join(ROOT, "src", "Rig", "Glove", "glove.bin"))
    want = set(a for a in sys.argv[1:] if not os.path.isdir(a) and not a.startswith("--"))
    results = {}

    def run(name, fn):
        if want and name not in want:
            return
        results[name] = fn()

    on_palm = "--before" not in sys.argv

    def mag(name, pose, depth, thick, length, grip_at):
        thumb, curls = POSES[pose]
        obj, pp, pr = magazine_hold(glove, depth, thick, length, grip_at, None, thumb, on_palm=on_palm)
        hand, push = hold(glove, obj, pp, pr, goals_for(curls), thumb)
        return report(name, hand, push, obj)

    run("rifle magazine (STANAG)", lambda: mag("rifle magazine (STANAG)", "RifleMagazine", 0.064, 0.023, 0.18, 0.33))
    run("pistol magazine", lambda: mag("pistol magazine", "PistolMagazine", 0.032, 0.022, 0.12, 0.33))
    run("AK magazine", lambda: mag("AK magazine", "RifleMagazine", 0.07, 0.026, 0.2, 0.33))
    run("MG box", lambda: mag("MG box", "BoxMagazine", 0.1, 0.08, 0.12, 0.33))
    return results


if __name__ == "__main__":
    main()
