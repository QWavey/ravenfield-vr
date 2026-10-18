"""Blender-style transform gizmos for the 3D view (user, 2026-10-18: "we have this in blender to turn things and also the
arrows"): arrows to move along an axis, small squares to move in a plane, a white circle at the centre to move in the
view's plane; rings to turn about an axis, a white outer ring to turn about the view's axis, and the space inside the rings
to turn freely (a trackball). Plain numpy, no GL: the view draws the triangles build() returns (screen pixels) and feeds
the mouse to hit() and Drag. Free dragging of a hand stays as it was; the gizmo is extra."""
import math

import numpy as np

AXIS_COLORS = ((0.96, 0.21, 0.32), (0.53, 0.84, 0.10), (0.18, 0.53, 1.0))  # X red, Y green, Z blue, as in Blender
AXIS_NAMES = ('X', 'Y', 'Z')
MODES = ('free', 'move', 'rotate', 'both')  # free: no gizmo (drag the hand itself, as before)
SPACES = ('global', 'local', 'gun')

# Sizes in screen pixels (logical): the gizmo keeps its size on screen however near or far the hand is, as in Blender.
CENTER_R = 9.0  # the white circle that moves in the view's plane
MOVE_LEN = 82.0  # an arrow, from the centre to its tip
ROT_R = 72.0  # the axis rings (the trackball inside them)
BOTH_R, BOTH_LEN = 64.0, 100.0  # move and turn at once: the arrows reach past the rings
VIEW_RING = 1.2  # the white ring, times the axis rings' radius
HEAD_LEN, HEAD_HALF = 17.0, 6.5
LINE_HALF = 1.35  # half a line's width
GRAB = 7.0  # how near the pointer must be to a line to take it
PLANE_IN, PLANE_OUT = 0.24, 0.42  # a plane square, in arrow lengths from the centre
MOVE_SNAP, MOVE_FINE_SNAP = 0.01, 0.001  # Ctrl: 1 cm steps; Ctrl + Shift: 1 mm
TURN_SNAP, TURN_FINE_SNAP = 5.0, 1.0  # degrees
MAX_MOVE = 1.5  # metres: one drag never throws a hand further (an axis dragged toward its vanishing point)


class Camera:
    """What the gizmo needs of the view: the eye, its axes, the projection, the window's size (logical pixels) and the
    metres one pixel covers at a point."""

    def __init__(self, eye, view, proj, width, height, ortho, px_at):
        self.eye = np.asarray(eye, dtype=np.float64)
        self.right = np.asarray(view[0, :3], dtype=np.float64)
        self.up = np.asarray(view[1, :3], dtype=np.float64)
        self.forward = -np.asarray(view[2, :3], dtype=np.float64)
        self.vp = proj @ view
        self.inv = np.linalg.inv(self.vp)
        self.w, self.h = float(max(1, width)), float(max(1, height))
        self.ortho = bool(ortho)
        self.px_at = px_at

    def project(self, p):
        """A world point on the screen (logical pixels, y down), or None behind the eye."""
        c = self.vp @ np.append(np.asarray(p, dtype=np.float64), 1.0)
        if c[3] <= 1e-6:
            return None
        return np.array([(c[0] / c[3] + 1.0) * 0.5 * self.w, (1.0 - c[1] / c[3]) * 0.5 * self.h])

    def project_many(self, pts):
        """(n, 3) world points -> (n, 2) screen points and a mask of those in front of the eye."""
        pts = np.asarray(pts, dtype=np.float64)
        c = np.hstack([pts, np.ones((len(pts), 1))]) @ self.vp.T
        ok = c[:, 3] > 1e-6
        wv = np.where(ok, c[:, 3], 1.0)
        s = np.column_stack([(c[:, 0] / wv + 1.0) * 0.5 * self.w, (1.0 - c[:, 1] / wv) * 0.5 * self.h])
        return s, ok

    def ray(self, x, y):
        """The ray under a screen point: (origin, unit direction)."""
        nx, ny = 2.0 * x / self.w - 1.0, 1.0 - 2.0 * y / self.h
        a = self.inv @ np.array([nx, ny, -1.0, 1.0])
        b = self.inv @ np.array([nx, ny, 1.0, 1.0])
        a, b = a[:3] / a[3], b[:3] / b[3]
        d = b - a
        return a, d / max(1e-12, float(np.linalg.norm(d)))

    def to_eye(self, c):
        """The unit direction from a point toward the viewer (the same everywhere in an orthographic view)."""
        if self.ortho:
            return -self.forward
        d = self.eye - np.asarray(c, dtype=np.float64)
        n = float(np.linalg.norm(d))
        return d / n if n > 1e-12 else -self.forward


def sizes(mode):
    """(ring radius, arrow length) in pixels for a mode (0 where it has none)."""
    if mode == 'move':
        return 0.0, MOVE_LEN
    if mode == 'rotate':
        return ROT_R, 0.0
    if mode == 'both':
        return BOTH_R, BOTH_LEN
    return 0.0, 0.0


def _perp(a):
    """Two unit vectors across a (u, v = a x u): a ring about a is c + r (u cos t + v sin t)."""
    a = np.asarray(a, dtype=np.float64)
    helper = np.array([1.0, 0, 0]) if abs(a[0]) < 0.9 else np.array([0, 1.0, 0])
    u = np.cross(a, helper)
    u /= np.linalg.norm(u)
    return u, np.cross(a, u)


def _clamp01(x):
    return max(0.0, min(1.0, x))


def axis_visibility(cam, c, a, length_px):
    """How much of an arrow shows (0..1): one pointing at the viewer is too short on screen to aim along, so it fades
    out and cannot be taken (as Blender hides it)."""
    px = cam.px_at(c)
    s0, s1 = cam.project(c), cam.project(c + a * length_px * px)
    if s0 is None or s1 is None:
        return 0.0
    return _clamp01((float(np.linalg.norm(s1 - s0)) / length_px - 0.16) / 0.2)


def plane_visibility(cam, c, n):
    """How much of a plane square shows (0..1): one seen edge-on fades out."""
    return _clamp01((abs(float(np.dot(n, cam.to_eye(c)))) - 0.14) / 0.2)


def ring_points(c, a, r, n=96):
    u, v = _perp(a)
    t = np.linspace(0.0, 2.0 * math.pi, n + 1)
    return c + r * (np.outer(np.cos(t), u) + np.outer(np.sin(t), v)), t


# ---- triangles in screen pixels ----------------------------------------------------------------------------------------

def _segs(a, b, half):
    """Thick lines from the points a[k] to b[k] (each (n, 2)): two triangles each, (6 n, 2)."""
    a, b = np.atleast_2d(a), np.atleast_2d(b)
    d = b - a
    length = np.hypot(d[:, 0], d[:, 1])
    ok = length > 1e-6
    if not ok.any():
        return np.zeros((0, 2))
    a, b, d, length = a[ok], b[ok], d[ok], length[ok]
    n = np.column_stack([-d[:, 1], d[:, 0]]) / length[:, None] * half
    return np.stack([a + n, a - n, b + n, b + n, a - n, b - n], axis=1).reshape(-1, 2)


def _seg(a, b, half):
    return _segs(np.asarray(a, dtype=np.float64), np.asarray(b, dtype=np.float64), half)


def _polyline(pts, keep, half):
    """A thick line through screen points; keep[i] says whether the piece from i to i + 1 is drawn."""
    pts = np.asarray(pts, dtype=np.float64)
    keep = np.asarray(keep, dtype=bool)
    if len(pts) < 2 or not keep.any():
        return np.zeros((0, 2))
    return _segs(pts[:-1][keep], pts[1:][keep], half)


def _circle(c, r, half, n=64):
    t = np.linspace(0.0, 2.0 * math.pi, n + 1)
    pts = c + np.column_stack([np.cos(t), np.sin(t)]) * r
    return _polyline(pts, np.ones(n, dtype=bool), half)


def _disc(c, r, n=48):
    t = np.linspace(0.0, 2.0 * math.pi, n + 1)
    rim = c + np.column_stack([np.cos(t), np.sin(t)]) * r
    out = []
    for i in range(n):
        out += [c, rim[i], rim[i + 1]]
    return np.array(out)


def _fan(c, rim):
    out = []
    for i in range(len(rim) - 1):
        out += [c, rim[i], rim[i + 1]]
    return np.array(out) if out else np.zeros((0, 2))


def _lighter(color, hot):
    return tuple(min(1.0, x + (1.0 - x) * 0.45) for x in color) if hot else color


# ---- the gizmo's parts --------------------------------------------------------------------------------------------------

def _arrow(cam, c, a, length_px):
    """(shaft start, shaft end, tip) on screen, or None."""
    px = cam.px_at(c)
    s0 = cam.project(c + a * CENTER_R * 1.6 * px)
    s1 = cam.project(c + a * (length_px - HEAD_LEN) * px)
    tip = cam.project(c + a * length_px * px)
    if s0 is None or s1 is None or tip is None:
        return None
    return s0, s1, tip


def _head(s1, tip):
    d = tip - s1
    length = float(np.hypot(d[0], d[1]))
    if length < 2.0:
        d = d / max(length, 1e-9) * 2.0 if length > 1e-9 else np.array([0.0, -2.0])
        length = 2.0
    n = np.array([-d[1], d[0]]) / length * HEAD_HALF
    return np.array([tip, s1 + n, s1 - n])


def _plane_quad(cam, c, axes, i, length_px):
    """The square of the plane across axis i (spanned by the other two), on screen: 4 points or None."""
    px = cam.px_at(c)
    j, k = (i + 1) % 3, (i + 2) % 3
    u, v = axes[:, j], axes[:, k]
    a, b = PLANE_IN * length_px * px, PLANE_OUT * length_px * px
    pts = [c + u * a + v * a, c + u * b + v * a, c + u * b + v * b, c + u * a + v * b]
    s = [cam.project(p) for p in pts]
    return None if any(p is None for p in s) else np.array(s)


def _front_ring(cam, c, a, r_px):
    """The front half of a ring (the half toward the viewer, as Blender draws it): screen points, the pieces kept, the
    angles of the points."""
    px = cam.px_at(c)
    pts, t = ring_points(c, a, r_px * px)
    s, ok = cam.project_many(pts)
    front = (pts - c) @ cam.to_eye(c) >= -0.02 * r_px * px  # (a ring facing the viewer is all front: kept whole)
    keep = ok[:-1] & ok[1:] & front[:-1] & front[1:]
    return s, keep, t


def build(cam, c, axes, mode, hover=None, drag=None):
    """The gizmo as (triangles in screen pixels (n, 2), rgba) pieces, back to front. hover: the handle under the pointer
    (drawn lighter). drag: the Drag going on (only its handle is drawn then, with its line or the angle turned so far)."""
    out = []
    cs = cam.project(c)
    if cs is None or mode not in ('move', 'rotate', 'both'):
        return out
    ring_r, arrow_len = sizes(mode)
    if drag is not None:
        return _build_drag(cam, c, axes, mode, drag, cs, ring_r, arrow_len)
    if ring_r > 0:
        # The trackball's sphere: a faint outline (filled faintly while the pointer is in it).
        if hover == ('ball',):
            out.append((_disc(cs, ring_r), (1.0, 1.0, 1.0, 0.07)))
        out.append((_circle(cs, ring_r, 0.8), (1.0, 1.0, 1.0, 0.22)))
        for i in range(3):
            s, keep, _ = _front_ring(cam, c, axes[:, i], ring_r)
            hot = hover == ('ring', i)
            out.append((_polyline(s, keep, LINE_HALF * (1.5 if hot else 1.0)), _lighter(AXIS_COLORS[i], hot) + (1.0,)))
        hot = hover == ('vring',)
        out.append((_circle(cs, ring_r * VIEW_RING, LINE_HALF * (1.5 if hot else 1.0)), (1.0, 1.0, 1.0, 0.95 if hot else 0.6)))
    if arrow_len > 0:
        for i in range(3):
            n = axes[:, i]
            f = plane_visibility(cam, c, n)
            q = _plane_quad(cam, c, axes, i, arrow_len) if f > 0 else None
            if q is not None:
                hot = hover == ('plane', i)
                out.append((np.array([q[0], q[1], q[2], q[0], q[2], q[3]]),
                            _lighter(AXIS_COLORS[i], hot) + ((0.75 if hot else 0.4) * f,)))
        for i in range(3):
            a = axes[:, i]
            f = axis_visibility(cam, c, a, arrow_len)
            arr = _arrow(cam, c, a, arrow_len) if f > 0 else None
            if arr is None:
                continue
            hot = hover == ('axis', i)
            col = _lighter(AXIS_COLORS[i], hot) + (f,)
            out.append((_seg(arr[0], arr[1], LINE_HALF * (1.5 if hot else 1.0)), col))
            out.append((_head(arr[1], arr[2]), col))
        hot = hover == ('view',)
        out.append((_circle(cs, CENTER_R, LINE_HALF * (1.4 if hot else 1.0), 40), (1.0, 1.0, 1.0, 1.0 if hot else 0.85)))
    return out


def _build_drag(cam, c, axes, mode, drag, cs, ring_r, arrow_len):
    out = []
    kind = drag.handle[0]
    if kind == 'axis':
        i = drag.handle[1]
        a = axes[:, i]
        # The axis it is held to, through where the drag began, as a long thin line (Blender's constraint line).
        px = cam.px_at(drag.c0)
        reach = 40.0 * MOVE_LEN * px
        s, ok = cam.project_many([drag.c0 - a * reach, drag.c0 + a * reach])
        for _ in range(6):
            if ok.all():
                break
            reach *= 0.4
            s, ok = cam.project_many([drag.c0 - a * reach, drag.c0 + a * reach])
        if ok.all():
            out.append((_seg(s[0], s[1], 0.7), AXIS_COLORS[i] + (0.6,)))
        arr = _arrow(cam, c, a, arrow_len)
        if arr is not None:
            col = _lighter(AXIS_COLORS[i], True) + (1.0,)
            out.append((_seg(arr[0], arr[1], LINE_HALF * 1.5), col))
            out.append((_head(arr[1], arr[2]), col))
    elif kind == 'plane':
        i = drag.handle[1]
        q = _plane_quad(cam, c, axes, i, arrow_len)
        if q is not None:
            out.append((np.array([q[0], q[1], q[2], q[0], q[2], q[3]]), _lighter(AXIS_COLORS[i], True) + (0.75,)))
    elif kind == 'view':
        out.append((_circle(cs, CENTER_R, LINE_HALF * 1.4, 40), (1.0, 1.0, 1.0, 1.0)))
    elif kind in ('ring', 'vring'):
        a = drag.axis
        r = ring_r * (VIEW_RING if kind == 'vring' else 1.0)
        col = AXIS_COLORS[drag.handle[1]] if kind == 'ring' else (1.0, 1.0, 1.0)
        px = cam.px_at(c)
        pts, _ = ring_points(c, a, r * px)
        s, ok = cam.project_many(pts)
        out.append((_polyline(s, ok[:-1] & ok[1:], LINE_HALF * 1.5), _lighter(col, True) + (1.0,)))
        # The angle turned so far, as a pie from where the ring was taken (Blender's dial).
        turn = math.radians(drag.angle)
        if abs(turn) > 1e-4:
            u, v = _perp(a)
            steps = max(2, int(abs(turn) / (2 * math.pi) * 96))
            t = drag.phi0 + np.linspace(0.0, turn, steps + 1)
            arc = c + r * px * (np.outer(np.cos(t), u) + np.outer(np.sin(t), v))
            sa, oka = cam.project_many(arc)
            if oka.all():
                out.append((_fan(cs, sa), col + (0.22,)))
                out.append((_seg(cs, sa[-1], 0.9), col + (0.9,)))
                out.append((_seg(cs, sa[0], 0.7), col + (0.5,)))
    elif kind == 'ball':
        out.append((_disc(cs, ring_r), (1.0, 1.0, 1.0, 0.07)))
        out.append((_circle(cs, ring_r, 1.1), (1.0, 1.0, 1.0, 0.6)))
    return out


# ---- what the pointer is on ----------------------------------------------------------------------------------------------

def _dist_seg(p, a, b):
    d = b - a
    L2 = float(np.dot(d, d))
    t = 0.0 if L2 < 1e-12 else _clamp01(float(np.dot(p - a, d)) / L2)
    return float(np.linalg.norm(p - (a + d * t)))


def _dist_polyline(p, pts, keep):
    """The nearest distance from p to the kept pieces of a polyline (inf when none is kept)."""
    pts = np.asarray(pts, dtype=np.float64)
    keep = np.asarray(keep, dtype=bool)
    if len(pts) < 2 or not keep.any():
        return float('inf')
    a, b = pts[:-1][keep], pts[1:][keep]
    d = b - a
    L2 = np.maximum((d * d).sum(1), 1e-12)
    t = np.clip(((p - a) * d).sum(1) / L2, 0.0, 1.0)
    return float(np.min(np.linalg.norm(p - (a + d * t[:, None]), axis=1)))


def _in_tri(p, t):
    def side(a, b, c):
        return (a[0] - c[0]) * (b[1] - c[1]) - (b[0] - c[0]) * (a[1] - c[1])
    d1, d2, d3 = side(p, t[0], t[1]), side(p, t[1], t[2]), side(p, t[2], t[0])
    neg = d1 < 0 or d2 < 0 or d3 < 0
    pos = d1 > 0 or d2 > 0 or d3 > 0
    return not (neg and pos)


def hit(cam, c, axes, mode, x, y):
    """The handle under the screen point (x, y), or None: ('view',), ('axis', i), ('plane', i), ('ring', i), ('vring',)
    or ('ball',) (inside the rings: the lowest; the view lets a hand under the pointer win over it). The centre circle
    first, then the nearest line within GRAB pixels."""
    cs = cam.project(c)
    if cs is None or mode not in ('move', 'rotate', 'both'):
        return None
    p = np.array([x, y], dtype=np.float64)
    ring_r, arrow_len = sizes(mode)
    from_c = float(np.linalg.norm(p - cs))
    if from_c > max(ring_r * VIEW_RING, arrow_len) + GRAB + HEAD_HALF:
        return None  # (far from every handle: nothing to test)
    if arrow_len > 0 and from_c <= CENTER_R + 4.0:
        return ('view',)
    best, best_d = None, GRAB
    if arrow_len > 0:
        for i in range(3):
            a = axes[:, i]
            if axis_visibility(cam, c, a, arrow_len) < 0.2:
                continue  # (drawn fainter than that: too foreshortened to aim along)
            arr = _arrow(cam, c, a, arrow_len)
            if arr is None:
                continue
            d = 0.0 if _in_tri(p, _head(arr[1], arr[2])) else _dist_seg(p, arr[0], arr[2])
            if d < best_d:
                best, best_d = ('axis', i), d
        for i in range(3):
            if plane_visibility(cam, c, axes[:, i]) < 0.2:
                continue
            q = _plane_quad(cam, c, axes, i, arrow_len)
            if q is not None and (_in_tri(p, q[[0, 1, 2]]) or _in_tri(p, q[[0, 2, 3]])):
                d = 1.0  # (a square: an arrow's line right over it wins)
                if d < best_d:
                    best, best_d = ('plane', i), d
    if ring_r > 0:
        for i in range(3):
            s, keep, _ = _front_ring(cam, c, axes[:, i], ring_r)
            d = _dist_polyline(p, s, keep)
            if d < best_d:
                best, best_d = ('ring', i), d
        d = abs(from_c - ring_r * VIEW_RING)
        if d < best_d:
            best, best_d = ('vring',), d
        if best is None and from_c < ring_r:
            return ('ball',)
    return best


# ---- dragging --------------------------------------------------------------------------------------------------------

def line_param(o, d, c, a):
    """Where along the line c + a t the ray o + d s comes nearest (a and d unit), or None when they run parallel."""
    w = c - o
    b = float(np.dot(a, d))
    den = 1.0 - b * b
    if den < 1e-6:
        return None
    return (b * float(np.dot(d, w)) - float(np.dot(a, w))) / den


def plane_hit(o, d, c, n, perspective=True):
    """Where the ray meets the plane through c across n, or None (parallel, or behind the eye)."""
    den = float(np.dot(d, n))
    if abs(den) < 1e-6:
        return None
    s = float(np.dot(c - o, n)) / den
    if perspective and s < 0:
        return None
    return o + d * s


class Drag:
    """One drag of a handle: where it began, and the move or turn so far (from its start, so nothing drifts).
    A Shift held makes the pointer move a tenth as far (fine); Ctrl (or the snap switch) snaps to 1 cm / 5 degrees
    steps (1 mm / 1 degree with Shift)."""

    def __init__(self, cam, handle, c0, axes, x, y, ring_r=ROT_R):
        self.handle = handle
        self.c0 = np.asarray(c0, dtype=np.float64)
        self.axes = np.array(axes, dtype=np.float64)
        self.m0 = np.array([x, y], dtype=np.float64)
        self.mouse = self.m0.copy()  # the pointer as the drag sees it (Shift moves it slower)
        self.delta = np.zeros(3)  # the move so far (world, metres)
        self.angle = 0.0  # the turn so far (degrees, right-handed about self.axis)
        self.axis = None
        self.phi0 = 0.0
        self.snapped = False
        self.fine = False
        self.normal = None
        self.cam_right, self.cam_up = cam.right.copy(), cam.up.copy()  # (the camera stands still while a drag goes on)
        self.ball = (0.0, 0.0)  # the trackball's turn so far: degrees about the view's up and right
        kind = handle[0]
        o, d = cam.ray(x, y)
        if kind == 'axis':
            self.t0 = line_param(o, d, self.c0, self.axes[:, handle[1]])
            if self.t0 is None:
                self.t0 = 0.0
        elif kind in ('plane', 'view'):
            self.normal = self.axes[:, handle[1]] if kind == 'plane' else cam.forward.copy()
            self.hit0 = plane_hit(o, d, self.c0, self.normal, not cam.ortho)
            if self.hit0 is None:
                self.hit0 = self.c0.copy()
        elif kind in ('ring', 'vring'):
            self.axis = self.axes[:, handle[1]].copy() if kind == 'ring' else cam.to_eye(self.c0)
            self.radius_px = ring_r * (VIEW_RING if kind == 'vring' else 1.0)  # the ring as drawn
            u, v = _perp(self.axis)
            # The ring's point nearest the pointer: where the pie starts, and the ring's way on screen there.
            ray_p = plane_hit(o, d, self.c0, self.axis, not cam.ortho)
            r = (ray_p - self.c0) if ray_p is not None else u
            r = r - self.axis * float(np.dot(r, self.axis))
            if np.linalg.norm(r) < 1e-12:
                r = u
            self.phi0 = math.atan2(float(np.dot(r, v)), float(np.dot(r, u)))
            self.u, self.v = u, v
            facing = float(np.dot(self.axis, cam.to_eye(self.c0)))
            self.edge_on = abs(facing) < 0.25 or ray_p is None
            self.last_ang = self.phi0
            self.turned = 0.0  # radians, unwrapped
            # Seen nearly edge-on, the ring is a line on screen: it turns by the drag along its way at its point nearest the
            # viewer (pushing the ring's near side), the way that shows best.
            near = cam.to_eye(self.c0)
            near = near - self.axis * float(np.dot(near, self.axis))
            if float(np.linalg.norm(near)) < 1e-6:
                near = u
            g = self.c0 + near / float(np.linalg.norm(near)) * cam.px_at(self.c0) * self.radius_px
            tan = np.cross(self.axis, g - self.c0)
            tan /= max(1e-12, float(np.linalg.norm(tan)))
            a, b = cam.project(g), cam.project(g + tan * cam.px_at(self.c0) * 10.0)
            self.tangent = (b - a) / max(1e-9, float(np.linalg.norm(b - a))) if a is not None and b is not None else np.array([1.0, 0.0])
            # The ring's radius on screen at that near point (nearer than the centre, so larger: dividing by the centre's
            # radius turned about 5 % too far).
            self.edge_r_px = self.radius_px * cam.px_at(self.c0) / max(1e-12, cam.px_at(g))
        self.last = np.array([x, y], dtype=np.float64)

    def follow(self, x, y, fine):
        """The pointer moved to (x, y): the drag's own pointer moves as far (a tenth with fine)."""
        step = np.array([x, y], dtype=np.float64) - self.last
        self.last = np.array([x, y], dtype=np.float64)
        self.mouse = self.mouse + step * (0.1 if fine else 1.0)
        self.fine = fine
        return step * (0.1 if fine else 1.0)

    def move(self, cam, snap):
        """The move so far (world, metres) along the handle's axis or plane, or in the view's plane."""
        o, d = cam.ray(*self.mouse)
        kind = self.handle[0]
        if kind == 'axis':
            a = self.axes[:, self.handle[1]]
            t = line_param(o, d, self.c0, a)
            if t is not None:
                p = self.c0 + a * t
                # Only a point of the axis in front of the eye, where the ray meets it ahead (dragged past the axis's
                # vanishing point, the nearest point jumped behind the eye and the hand with it).
                ahead = float(np.dot(p - o, d)) > 0 and (cam.ortho or float(np.dot(p - cam.eye, cam.forward)) > 0.02)
                if ahead:
                    self.delta = a * (t - self.t0)
        else:
            h = plane_hit(o, d, self.c0, self.normal, not cam.ortho)
            # (Near the plane's horizon the ray meets it far away: such a hit is left, the move stays where it was.)
            if h is not None and float(np.linalg.norm(h - self.c0)) < 2.0 * MAX_MOVE:
                self.delta = h - self.hit0
                self.delta -= self.normal * float(np.dot(self.delta, self.normal))  # (stays in its plane exactly)
        length = float(np.linalg.norm(self.delta))
        if length > MAX_MOVE:
            self.delta *= MAX_MOVE / length
        self.snapped = bool(snap)
        if snap:
            # Rounded in the directions it may move in: its axis, its plane's two, or the view's right and up (rounded
            # along the gizmo's axes, a move in the view's plane came out of it, up to a centimetre toward the viewer).
            step = MOVE_FINE_SNAP if self.fine else MOVE_SNAP
            if kind == 'axis':
                basis = self.axes[:, [self.handle[1]]]
            elif kind == 'plane':
                i = self.handle[1]
                basis = self.axes[:, [(i + 1) % 3, (i + 2) % 3]]
            else:
                basis = np.column_stack([self.cam_right, self.cam_up])
            parts = basis.T @ self.delta
            return basis @ (np.round(parts / step) * step)
        return self.delta.copy()

    def ball_turn(self, snap):
        """The trackball's turn so far, from where the drag began: (degrees about the view's up, degrees about its right),
        half a degree per pixel as Shift + drag on a hand; snapped like the rings."""
        yaw, pitch = (self.mouse - self.m0) * 0.5
        self.snapped = bool(snap)
        if snap:
            step = TURN_FINE_SNAP if self.fine else TURN_SNAP
            yaw, pitch = round(yaw / step) * step, round(pitch / step) * step
        self.ball = (float(yaw), float(pitch))
        return self.ball

    def turn(self, cam, snap):
        """The turn so far (degrees, right-handed about self.axis): the angle, about the centre and in the ring's own plane,
        from where the ring was taken to where the pointer's ray meets that plane now (so a drag along a tilted ring turns
        exactly as far as the ring shows). A ring seen nearly edge-on turns by the drag along its way on screen instead."""
        if self.edge_on:
            ang = math.degrees(float(np.dot(self.mouse - self.m0, self.tangent)) / self.edge_r_px)
        else:
            o, d = cam.ray(*self.mouse)
            p = plane_hit(o, d, self.c0, self.axis, not cam.ortho)
            if p is not None:
                r = p - self.c0
                # (Not too near the centre: there the angle jumps about with a pixel of the pointer.)
                if float(np.linalg.norm(r)) > 0.15 * self.radius_px * cam.px_at(self.c0):
                    a = math.atan2(float(np.dot(r, self.v)), float(np.dot(r, self.u)))
                    self.turned += (a - self.last_ang + math.pi) % (2 * math.pi) - math.pi
                    self.last_ang = a
            ang = math.degrees(self.turned)
        self.angle = ang
        self.snapped = bool(snap)
        if snap:
            step = TURN_FINE_SNAP if self.fine else TURN_SNAP
            self.angle = round(ang / step) * step
        return self.angle


def describe(drag, space, moved=None):
    """The readout while dragging: what moves how far."""
    kind = drag.handle[0]
    names = AXIS_NAMES
    if space == 'gun':
        names = ('X barrel', 'Y up', 'Z side')
    tail = ('  snap' if drag.snapped else '') + ('  fine' if drag.fine else '')
    if kind in ('axis', 'plane', 'view'):
        d = moved if moved is not None else drag.delta
        parts = drag.axes.T @ d * 100.0
        if kind == 'axis':
            i = drag.handle[1]
            return f'Move {names[i]}: {parts[i]:+.1f} cm' + tail
        if kind == 'plane':
            i = drag.handle[1]
            j, k = (i + 1) % 3, (i + 2) % 3
            # (Named by the square's colour, its axis across it, and the plane it moves in.)
            return (f'Move across {AXIS_NAMES[i]} (in {AXIS_NAMES[j]}{AXIS_NAMES[k]}): {names[j]} {parts[j]:+.1f}, '
                    f'{names[k]} {parts[k]:+.1f} cm' + tail)
        return f'Move in the view: {np.linalg.norm(d) * 100.0:.1f} cm' + tail
    if kind == 'ring':
        return f'Turn about {names[drag.handle[1]]}: {drag.angle:+.1f}°' + tail
    if kind == 'vring':
        return f'Turn about the view: {drag.angle:+.1f}°' + tail
    return f'Turn freely: {drag.ball[0]:+.1f}° across, {drag.ball[1]:+.1f}° up and down' + tail
