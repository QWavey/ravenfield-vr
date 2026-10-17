"""Small vector / quaternion / matrix helpers on numpy (quaternions as [x, y, z, w], matrices 4x4 column vectors)."""
import math

import numpy as np


def v3(a):
    return np.asarray(a, dtype=np.float64).reshape(3)


def norm(a):
    a = np.asarray(a, dtype=np.float64)
    n = np.linalg.norm(a)
    return a / n if n > 1e-12 else a


def quat_mul(a, b):
    ax, ay, az, aw = a
    bx, by, bz, bw = b
    return np.array([aw * bx + ax * bw + ay * bz - az * by,
                     aw * by - ax * bz + ay * bw + az * bx,
                     aw * bz + ax * by - ay * bx + az * bw,
                     aw * bw - ax * bx - ay * by - az * bz])


def quat_inv(q):
    return np.array([-q[0], -q[1], -q[2], q[3]]) / max(1e-12, float(np.dot(q, q)))


def quat_axis_angle(axis, deg):
    axis = norm(axis)
    h = math.radians(deg) * 0.5
    s = math.sin(h)
    return np.array([axis[0] * s, axis[1] * s, axis[2] * s, math.cos(h)])


def quat_norm(q):
    q = np.asarray(q, dtype=np.float64)
    n = np.linalg.norm(q)
    return q / n if n > 1e-12 else np.array([0, 0, 0, 1.0])


def quat_slerp(a, b, t):
    a = quat_norm(a)
    b = quat_norm(b)
    d = float(np.dot(a, b))
    if d < 0:
        b = -b
        d = -d
    if d > 0.9995:
        return quat_norm(a + (b - a) * t)
    th = math.acos(max(-1.0, min(1.0, d)))
    s = math.sin(th)
    return a * (math.sin((1 - t) * th) / s) + b * (math.sin(t * th) / s)


def quat_rotate(q, v):
    m = quat_to_mat3(q)
    return m @ np.asarray(v, dtype=np.float64)


def quat_to_mat3(q):
    x, y, z, w = quat_norm(q)
    return np.array([
        [1 - 2 * (y * y + z * z), 2 * (x * y - z * w), 2 * (x * z + y * w)],
        [2 * (x * y + z * w), 1 - 2 * (x * x + z * z), 2 * (y * z - x * w)],
        [2 * (x * z - y * w), 2 * (y * z + x * w), 1 - 2 * (x * x + y * y)]])


def mat3_to_quat(m):
    t = m[0, 0] + m[1, 1] + m[2, 2]
    if t > 0:
        s = math.sqrt(t + 1.0) * 2
        q = [(m[2, 1] - m[1, 2]) / s, (m[0, 2] - m[2, 0]) / s, (m[1, 0] - m[0, 1]) / s, 0.25 * s]
    elif m[0, 0] > m[1, 1] and m[0, 0] > m[2, 2]:
        s = math.sqrt(1.0 + m[0, 0] - m[1, 1] - m[2, 2]) * 2
        q = [0.25 * s, (m[0, 1] + m[1, 0]) / s, (m[0, 2] + m[2, 0]) / s, (m[2, 1] - m[1, 2]) / s]
    elif m[1, 1] > m[2, 2]:
        s = math.sqrt(1.0 + m[1, 1] - m[0, 0] - m[2, 2]) * 2
        q = [(m[0, 1] + m[1, 0]) / s, 0.25 * s, (m[1, 2] + m[2, 1]) / s, (m[0, 2] - m[2, 0]) / s]
    else:
        s = math.sqrt(1.0 + m[2, 2] - m[0, 0] - m[1, 1]) * 2
        q = [(m[0, 2] + m[2, 0]) / s, (m[1, 2] + m[2, 1]) / s, 0.25 * s, (m[1, 0] - m[0, 1]) / s]
    return quat_norm(q)


def frame_quat(x_axis, y_axis):
    """The rotation whose local x / y axes are these (y made perpendicular to x; z = x cross y)."""
    x = norm(x_axis)
    y = np.asarray(y_axis, dtype=np.float64)
    y = norm(y - x * np.dot(x, y))
    if np.linalg.norm(y) < 1e-9:
        y = norm(np.cross(x, [0, 0, 1.0]) if abs(x[2]) < 0.9 else np.cross(x, [0, 1.0, 0]))
    z = np.cross(x, y)
    return mat3_to_quat(np.column_stack([x, y, z]))


def trs(pos, quat, scale=(1, 1, 1)):
    m = np.eye(4)
    m[:3, :3] = quat_to_mat3(quat) * np.asarray(scale, dtype=np.float64)
    m[:3, 3] = pos
    return m


def quat_to_euler_deg(q):
    """x (pitch), y (yaw), z (roll) in degrees, applied z then x then y (for the number boxes only)."""
    m = quat_to_mat3(q)
    sy = max(-1.0, min(1.0, -m[1, 2]))
    x = math.degrees(math.asin(sy))
    if abs(sy) < 0.9999:
        y = math.degrees(math.atan2(m[0, 2], m[2, 2]))
        z = math.degrees(math.atan2(m[1, 0], m[1, 1]))
    else:
        y = math.degrees(math.atan2(-m[2, 0], m[0, 0]))
        z = 0.0
    return np.array([x, y, z])


def euler_deg_to_quat(e):
    qx = quat_axis_angle([1, 0, 0], e[0])
    qy = quat_axis_angle([0, 1, 0], e[1])
    qz = quat_axis_angle([0, 0, 1], e[2])
    return quat_norm(quat_mul(qy, quat_mul(qx, qz)))


def perspective(fovy_deg, aspect, near, far):
    f = 1.0 / math.tan(math.radians(fovy_deg) / 2)
    m = np.zeros((4, 4))
    m[0, 0] = f / aspect
    m[1, 1] = f
    m[2, 2] = (far + near) / (near - far)
    m[2, 3] = 2 * far * near / (near - far)
    m[3, 2] = -1
    return m


def look_at(eye, target, up):
    eye, target, up = v3(eye), v3(target), v3(up)
    f = norm(target - eye)
    s = norm(np.cross(f, up))
    u = np.cross(s, f)
    m = np.eye(4)
    m[0, :3] = s
    m[1, :3] = u
    m[2, :3] = -f
    m[:3, 3] = -m[:3, :3] @ eye
    return m


def ray_triangles(origin, direction, tris):
    """Möller-Trumbore against many triangles (n, 3, 3): the nearest hit distance and index, or (None, -1)."""
    if len(tris) == 0:
        return None, -1
    v0, v1, v2 = tris[:, 0], tris[:, 1], tris[:, 2]
    e1 = v1 - v0
    e2 = v2 - v0
    p = np.cross(direction, e2)
    det = np.einsum('ij,ij->i', e1, p)
    ok = np.abs(det) > 1e-12
    inv = np.zeros_like(det)
    inv[ok] = 1.0 / det[ok]
    t = origin - v0
    u = np.einsum('ij,ij->i', t, p) * inv
    q = np.cross(t, e1)
    v = (q @ direction) * inv
    d = np.einsum('ij,ij->i', e2, q) * inv
    hit = ok & (u >= 0) & (v >= 0) & (u + v <= 1) & (d > 1e-6)
    if not hit.any():
        return None, -1
    idx = np.where(hit)[0]
    k = idx[np.argmin(d[idx])]
    return float(d[k]), int(k)
