"""The 3D view: an OpenGL 3.3 renderer for the exported weapon and the two gloves, with an orbit camera, picking and
dragging (move a hand in the view's plane; Shift turns it)."""
import ctypes
import math

import numpy as np
from OpenGL import GL
from PyQt6.QtCore import QPoint, Qt, pyqtSignal
from PyQt6.QtGui import QSurfaceFormat
from PyQt6.QtOpenGLWidgets import QOpenGLWidget

from mathutil import look_at, norm, perspective, quat_axis_angle, quat_mul, quat_norm, ray_triangles

LIT_VS = """
#version 330 core
layout(location=0) in vec3 aPos;
layout(location=1) in vec3 aNor;
layout(location=2) in vec2 aUv;
uniform mat4 uMVP;
uniform mat4 uModel;
uniform mat3 uNormal;
out vec3 vNor;
out vec2 vUv;
out vec3 vWorld;
void main() {
    vec4 w = uModel * vec4(aPos, 1.0);
    vWorld = w.xyz;
    vNor = normalize(uNormal * aNor);
    vUv = aUv;
    gl_Position = uMVP * vec4(aPos, 1.0);
}
"""
LIT_FS = """
#version 330 core
in vec3 vNor;
in vec2 vUv;
in vec3 vWorld;
uniform vec4 uColor;
uniform sampler2D uTex;
uniform int uHasTex;
uniform vec3 uTint;
uniform vec3 uCam;
out vec4 frag;
void main() {
    vec4 base = uColor;
    if (uHasTex == 1) base *= texture(uTex, vUv);
    vec3 n = normalize(vNor);
    vec3 v = normalize(uCam - vWorld);
    if (dot(n, v) < 0.0) n = -n;
    vec3 l1 = normalize(vec3(0.4, 0.8, 0.5));
    vec3 l2 = normalize(vec3(-0.6, 0.3, -0.7));
    float d = 0.22 + 0.75 * max(dot(n, l1), 0.0) + 0.28 * max(dot(n, l2), 0.0) + 0.12 * max(dot(n, v), 0.0);
    vec3 h = normalize(l1 + v);
    float s = pow(max(dot(n, h), 0.0), 40.0) * 0.12;
    vec3 c = base.rgb * d + s + uTint;
    frag = vec4(pow(max(c, vec3(0.0)), vec3(1.0 / 2.2)), base.a);  // linear light out to the screen's sRGB
}
"""
FLAT_VS = """
#version 330 core
layout(location=0) in vec3 aPos;
uniform mat4 uMVP;
void main() { gl_Position = uMVP * vec4(aPos, 1.0); }
"""
FLAT_FS = """
#version 330 core
uniform vec4 uColor;
out vec4 frag;
void main() { frag = uColor; }
"""


def _compile(vs, fs):
    def shader(src, kind):
        s = GL.glCreateShader(kind)
        GL.glShaderSource(s, src)
        GL.glCompileShader(s)
        if not GL.glGetShaderiv(s, GL.GL_COMPILE_STATUS):
            raise RuntimeError(GL.glGetShaderInfoLog(s).decode())
        return s
    p = GL.glCreateProgram()
    GL.glAttachShader(p, shader(vs, GL.GL_VERTEX_SHADER))
    GL.glAttachShader(p, shader(fs, GL.GL_FRAGMENT_SHADER))
    GL.glLinkProgram(p)
    if not GL.glGetProgramiv(p, GL.GL_LINK_STATUS):
        raise RuntimeError(GL.glGetProgramInfoLog(p).decode())
    return p


class GpuMesh:
    """One drawable: vertices, normals, uvs, indices, a colour and maybe a texture."""

    def __init__(self, positions, normals, uvs, indices, color, image=None, blend=False, double=False, dynamic=False):
        self.count = int(len(indices))
        self.color = color
        self.blend = blend
        self.double = double
        self.vao = GL.glGenVertexArrays(1)
        GL.glBindVertexArray(self.vao)
        usage = GL.GL_DYNAMIC_DRAW if dynamic else GL.GL_STATIC_DRAW
        self.vbo_pos = self._buffer(0, np.ascontiguousarray(positions, dtype=np.float32), 3, usage)
        self.vbo_nor = self._buffer(1, np.ascontiguousarray(normals, dtype=np.float32), 3, usage)
        uv = uvs if uvs is not None else np.zeros((len(positions), 2), np.float32)
        self.vbo_uv = self._buffer(2, np.ascontiguousarray(uv, dtype=np.float32), 2, GL.GL_STATIC_DRAW)
        self.ebo = GL.glGenBuffers(1)
        GL.glBindBuffer(GL.GL_ELEMENT_ARRAY_BUFFER, self.ebo)
        GL.glBufferData(GL.GL_ELEMENT_ARRAY_BUFFER, np.ascontiguousarray(indices, dtype=np.uint32), GL.GL_STATIC_DRAW)
        GL.glBindVertexArray(0)
        self.tex = 0
        if image is not None and uvs is not None:
            self.tex = _texture(image)

    @staticmethod
    def _buffer(loc, data, size, usage):
        b = GL.glGenBuffers(1)
        GL.glBindBuffer(GL.GL_ARRAY_BUFFER, b)
        GL.glBufferData(GL.GL_ARRAY_BUFFER, data, usage)
        GL.glEnableVertexAttribArray(loc)
        GL.glVertexAttribPointer(loc, size, GL.GL_FLOAT, GL.GL_FALSE, 0, ctypes.c_void_p(0))
        return b

    def update(self, positions, normals):
        GL.glBindBuffer(GL.GL_ARRAY_BUFFER, self.vbo_pos)
        GL.glBufferSubData(GL.GL_ARRAY_BUFFER, 0, np.ascontiguousarray(positions, dtype=np.float32))
        GL.glBindBuffer(GL.GL_ARRAY_BUFFER, self.vbo_nor)
        GL.glBufferSubData(GL.GL_ARRAY_BUFFER, 0, np.ascontiguousarray(normals, dtype=np.float32))

    def delete(self):
        GL.glDeleteVertexArrays(1, [self.vao])
        GL.glDeleteBuffers(4, [self.vbo_pos, self.vbo_nor, self.vbo_uv, self.ebo])
        if self.tex:
            GL.glDeleteTextures(1, [self.tex])


_tex_cache = {}


def _texture(image):
    key = id(image)
    if key in _tex_cache:
        return _tex_cache[key][0]
    img = image.convert('RGBA')
    data = np.asarray(img, dtype=np.uint8)
    t = GL.glGenTextures(1)
    GL.glBindTexture(GL.GL_TEXTURE_2D, t)
    GL.glTexImage2D(GL.GL_TEXTURE_2D, 0, GL.GL_SRGB8_ALPHA8, img.width, img.height, 0, GL.GL_RGBA, GL.GL_UNSIGNED_BYTE, data)
    GL.glGenerateMipmap(GL.GL_TEXTURE_2D)
    GL.glTexParameteri(GL.GL_TEXTURE_2D, GL.GL_TEXTURE_MIN_FILTER, GL.GL_LINEAR_MIPMAP_LINEAR)
    GL.glTexParameteri(GL.GL_TEXTURE_2D, GL.GL_TEXTURE_MAG_FILTER, GL.GL_LINEAR)
    _tex_cache[key] = (t, image)  # the image kept alive with its id
    return t


class Viewport(QOpenGLWidget):
    """Signals: picked(point_world or None, what) on a laser / Alt click; selected(hand or None); changed() after a drag;
    context(QPoint global) on a right click without a drag."""
    picked = pyqtSignal(object, object)
    selected = pyqtSignal(object)
    changed = pyqtSignal()
    context = pyqtSignal(QPoint)
    hovered = pyqtSignal(str)

    def __init__(self, scene, parent=None):
        fmt = QSurfaceFormat()
        fmt.setVersion(3, 3)
        fmt.setProfile(QSurfaceFormat.OpenGLContextProfile.CoreProfile)
        fmt.setSamples(4)
        fmt.setDepthBufferSize(24)
        QSurfaceFormat.setDefaultFormat(fmt)
        super().__init__(parent)
        self.setFormat(fmt)
        self.scene = scene
        self.setMouseTracking(True)
        self.setFocusPolicy(Qt.FocusPolicy.StrongFocus)
        self.yaw, self.pitch, self.dist = -25.0, 12.0, 0.75
        self.target = np.array([0.05, 0.02, 0.0])
        self.laser = False
        self.selected_hand = None
        self.hover_node = None
        self.pick_point = None
        self.pick_line = None  # (from, to) world
        self.show_grid = True
        self.show_markers = True
        self._gpu_nodes = {}
        self._gpu_hands = {}
        self._mouse = None
        self._press = None
        self._drag = None
        self._ready = False
        self.background = (0.22, 0.235, 0.26)

    # ---- GL setup -----------------------------------------------------------------------------------------------

    def initializeGL(self):
        self.lit = _compile(LIT_VS, LIT_FS)
        self.flat = _compile(FLAT_VS, FLAT_FS)
        self.line_vao = GL.glGenVertexArrays(1)
        self.line_vbo = GL.glGenBuffers(1)
        GL.glBindVertexArray(self.line_vao)
        GL.glBindBuffer(GL.GL_ARRAY_BUFFER, self.line_vbo)
        GL.glBufferData(GL.GL_ARRAY_BUFFER, 4 * 3 * 4096, None, GL.GL_DYNAMIC_DRAW)
        GL.glEnableVertexAttribArray(0)
        GL.glVertexAttribPointer(0, 3, GL.GL_FLOAT, GL.GL_FALSE, 0, ctypes.c_void_p(0))
        GL.glBindVertexArray(0)
        GL.glEnable(GL.GL_DEPTH_TEST)
        GL.glEnable(GL.GL_MULTISAMPLE)
        self._ready = True
        self.rebuild()

    def rebuild(self):
        """(Re)uploads the weapon's meshes (after a weapon was loaded)."""
        if not self._ready:
            return
        self.makeCurrent()
        for meshes in self._gpu_nodes.values():
            for m in meshes:
                m.delete()
        self._gpu_nodes = {}
        w = self.scene.weapon
        if w is not None:
            for node in w.nodes:
                self._gpu_nodes[id(node)] = [GpuMesh(p.positions, p.normals, p.uvs, p.indices, p.material.color, p.material.image,
                                                     p.material.blend, p.material.double) for p in node.primitives]
        self.doneCurrent()
        self.update()

    def _hand_mesh(self, hand):
        g = self._gpu_hands.get(hand.side)
        if g is None:
            shape = hand.shape
            g = GpuMesh(hand.vertices, hand.normals, shape.uvs, shape.indices, (1, 1, 1, 1), shape.image, dynamic=True)
            self._gpu_hands[hand.side] = g
            hand.dirty = False
        elif hand.dirty:
            g.update(hand.vertices, hand.normals)
            hand.dirty = False
        return g

    # ---- camera -------------------------------------------------------------------------------------------------

    def eye(self):
        y, p = math.radians(self.yaw), math.radians(self.pitch)
        return self.target + self.dist * np.array([math.sin(y) * math.cos(p), math.sin(p), math.cos(y) * math.cos(p)])

    def view_matrix(self):
        return look_at(self.eye(), self.target, [0, 1, 0])

    def proj_matrix(self):
        w, h = max(1, self.width()), max(1, self.height())
        return perspective(38.0, w / h, 0.005, 20.0)

    def frame(self, center, radius):
        self.target = np.array(center, dtype=np.float64)
        self.dist = max(0.15, radius / math.sin(math.radians(19)) * 1.05)
        self.update()

    def set_view(self, name):
        self.yaw, self.pitch = {'side': (0.0, 0.0), 'top': (0.0, 89.0), 'front': (90.0, 0.0), 'three': (-25.0, 12.0)}.get(name, (self.yaw, self.pitch))
        self.update()

    # ---- drawing -------------------------------------------------------------------------------------------------

    def paintGL(self):
        GL.glClearColor(*self.background, 1.0)
        GL.glClear(GL.GL_COLOR_BUFFER_BIT | GL.GL_DEPTH_BUFFER_BIT)
        view, proj = self.view_matrix(), self.proj_matrix()
        vp = proj @ view
        eye = self.eye()
        draws_opaque, draws_blend = [], []
        w = self.scene.weapon
        if w is not None:
            for node in w.nodes:
                if node.hidden:
                    continue
                model = w.node_matrix(node)
                tint = (0.18, 0.12, 0.02) if node is self.hover_node else (0.0, 0.0, 0.0)
                if self.scene.highlight_role and node.role == self.scene.highlight_role:
                    tint = (0.05, 0.1, 0.22)
                for g in self._gpu_nodes.get(id(node), []):
                    (draws_blend if g.blend else draws_opaque).append((g, model, tint, False))
        for hand in self.scene.hands():
            g = self._hand_mesh(hand)
            tint = (0.1, 0.07, 0.0) if hand is self.selected_hand else (0.0, 0.0, 0.0)
            draws_opaque.append((g, hand.model_matrix(), tint, hand.mirror < 0))
        for node, model in self.scene.extra_draws():
            for g in self._gpu_nodes.get(id(node), []):
                (draws_blend if g.blend else draws_opaque).append((g, model, (0, 0, 0), False))
        GL.glUseProgram(self.lit)
        GL.glUniform3f(GL.glGetUniformLocation(self.lit, 'uCam'), *eye)
        GL.glEnable(GL.GL_CULL_FACE)
        for g, model, tint, mirrored in draws_opaque:
            self._draw(g, model, vp, tint, mirrored)
        if draws_blend:
            GL.glEnable(GL.GL_BLEND)
            GL.glBlendFunc(GL.GL_SRC_ALPHA, GL.GL_ONE_MINUS_SRC_ALPHA)
            GL.glDepthMask(GL.GL_FALSE)
            for g, model, tint, mirrored in draws_blend:
                self._draw(g, model, vp, tint, mirrored)
            GL.glDepthMask(GL.GL_TRUE)
            GL.glDisable(GL.GL_BLEND)
        GL.glDisable(GL.GL_CULL_FACE)
        self._lines(vp)

    def _draw(self, g, model, vp, tint, mirrored):
        p = self.lit
        GL.glUniformMatrix4fv(GL.glGetUniformLocation(p, 'uMVP'), 1, GL.GL_TRUE, (vp @ model).astype(np.float32))
        GL.glUniformMatrix4fv(GL.glGetUniformLocation(p, 'uModel'), 1, GL.GL_TRUE, model.astype(np.float32))
        nm = np.linalg.inv(model[:3, :3]).T
        GL.glUniformMatrix3fv(GL.glGetUniformLocation(p, 'uNormal'), 1, GL.GL_TRUE, nm.astype(np.float32))
        GL.glUniform4f(GL.glGetUniformLocation(p, 'uColor'), *g.color)
        GL.glUniform3f(GL.glGetUniformLocation(p, 'uTint'), *tint)
        GL.glUniform1i(GL.glGetUniformLocation(p, 'uHasTex'), 1 if g.tex else 0)
        if g.tex:
            GL.glActiveTexture(GL.GL_TEXTURE0)
            GL.glBindTexture(GL.GL_TEXTURE_2D, g.tex)
            GL.glUniform1i(GL.glGetUniformLocation(p, 'uTex'), 0)
        if g.double:
            GL.glDisable(GL.GL_CULL_FACE)
        else:
            GL.glEnable(GL.GL_CULL_FACE)
            GL.glFrontFace(GL.GL_CW if mirrored else GL.GL_CCW)
        GL.glBindVertexArray(g.vao)
        GL.glDrawElements(GL.GL_TRIANGLES, g.count, GL.GL_UNSIGNED_INT, ctypes.c_void_p(0))
        GL.glBindVertexArray(0)
        GL.glFrontFace(GL.GL_CCW)

    def _lines(self, vp):
        segs = []  # (points (n,3), rgba, depth_test)
        if self.show_grid:
            g = []
            y = self.scene.floor
            for i in range(-10, 11):
                t = i * 0.05
                g += [(-0.5, y, t), (0.5, y, t), (t, y, -0.5), (t, y, 0.5)]
            segs.append((np.array(g), (0.32, 0.34, 0.38, 1.0), True))
        if self.show_markers:
            for pts, color in self.scene.marker_lines():
                segs.append((pts, color, False))
        if self.pick_line is not None:
            segs.append((np.array(self.pick_line), (1.0, 0.18, 0.15, 1.0), False))
        if not segs:
            return
        GL.glUseProgram(self.flat)
        GL.glUniformMatrix4fv(GL.glGetUniformLocation(self.flat, 'uMVP'), 1, GL.GL_TRUE, vp.astype(np.float32))
        GL.glBindVertexArray(self.line_vao)
        GL.glBindBuffer(GL.GL_ARRAY_BUFFER, self.line_vbo)
        for pts, color, depth in segs:
            data = np.ascontiguousarray(pts, dtype=np.float32)[:4096]
            GL.glBufferSubData(GL.GL_ARRAY_BUFFER, 0, data)
            GL.glUniform4f(GL.glGetUniformLocation(self.flat, 'uColor'), *color)
            if not depth:
                GL.glDisable(GL.GL_DEPTH_TEST)
            GL.glDrawArrays(GL.GL_LINES, 0, len(data))
            GL.glEnable(GL.GL_DEPTH_TEST)
        GL.glBindVertexArray(0)

    # ---- picking -------------------------------------------------------------------------------------------------

    def ray(self, pos):
        w, h = max(1, self.width()), max(1, self.height())
        x = 2.0 * pos.x() / w - 1.0
        y = 1.0 - 2.0 * pos.y() / h
        inv = np.linalg.inv(self.proj_matrix() @ self.view_matrix())
        a = inv @ np.array([x, y, -1.0, 1.0])
        b = inv @ np.array([x, y, 1.0, 1.0])
        a, b = a[:3] / a[3], b[:3] / b[3]
        return a, norm(b - a)

    def pick(self, pos, hands=True, weapon=True):
        """The nearest thing under the mouse: ('hand', hand, point) or ('node', node, point) or None."""
        o, d = self.ray(pos)
        best = None
        if hands:
            for hand in self.scene.hands():
                m = hand.model_matrix()
                inv = np.linalg.inv(m)
                lo = (inv @ np.append(o, 1))[:3]
                ld = (inv @ np.append(d, 0))[:3]
                tris = hand.vertices[hand.shape.indices.reshape(-1, 3)].astype(np.float64)
                t, _ = ray_triangles(lo, ld, tris)
                if t is not None:
                    pw = (m @ np.append(lo + ld * t, 1))[:3]
                    dist = np.linalg.norm(pw - o)
                    if best is None or dist < best[3]:
                        best = ('hand', hand, pw, dist)
        if weapon and self.scene.weapon is not None:
            w = self.scene.weapon
            for node in w.nodes:
                if node.hidden:
                    continue
                m = w.node_matrix(node)
                inv = np.linalg.inv(m)
                lo = (inv @ np.append(o, 1))[:3]
                ld = (inv @ np.append(d, 0))[:3]
                if not _ray_box(lo, ld, node.bmin, node.bmax):
                    continue
                t, _ = ray_triangles(lo, ld, node.tris)
                if t is not None:
                    pw = (m @ np.append(lo + ld * t, 1))[:3]
                    dist = np.linalg.norm(pw - o)
                    if best is None or dist < best[3]:
                        best = ('node', node, pw, dist)
        return best

    # ---- mouse -----------------------------------------------------------------------------------------------------

    def mousePressEvent(self, e):
        self._mouse = e.position().toPoint()
        self._press = self._mouse
        self._drag = None
        alt = bool(e.modifiers() & Qt.KeyboardModifier.AltModifier)
        if e.button() == Qt.MouseButton.LeftButton:
            if self.laser or alt:
                hit = self.pick(self._mouse, hands=False)
                self._set_pick(hit)
                return
            hit = self.pick(self._mouse, weapon=False)
            if hit is not None and hit[0] == 'hand':
                if self.selected_hand is not hit[1]:
                    self.selected_hand = hit[1]
                    self.selected.emit(hit[1])
                shift = bool(e.modifiers() & Qt.KeyboardModifier.ShiftModifier)
                self._drag = ('turn' if shift else 'move', hit[1], hit[2])
                self.update()
                return
            self._drag = ('orbit',)
        elif e.button() == Qt.MouseButton.RightButton:
            self._drag = ('pan',)
        elif e.button() == Qt.MouseButton.MiddleButton:
            self._drag = ('pan',)

    def _set_pick(self, hit):
        if hit is None or hit[0] != 'node':
            self.pick_point = None
            self.pick_line = None
            self.picked.emit(None, None)
        else:
            self.pick_point = hit[2]
            src = self.scene.laser_origin()
            if src is None:
                o, d = self.ray(self._mouse)
                src = o + d * 0.02
            self.pick_line = [src, hit[2]]
            self.picked.emit(hit[2], hit[1])
        self.update()

    def mouseMoveEvent(self, e):
        pos = e.position().toPoint()
        if self._mouse is None or self._drag is None:
            if self.laser or e.modifiers() & Qt.KeyboardModifier.AltModifier:
                hit = self.pick(pos, hands=False)
                node = hit[1] if hit else None
                if node is not self.hover_node:
                    self.hover_node = node
                    self.hovered.emit(node.short if node else '')
                    self.update()
            elif self.hover_node is not None:
                self.hover_node = None
                self.hovered.emit('')
                self.update()
            self._mouse = pos
            return
        dx, dy = pos.x() - self._mouse.x(), pos.y() - self._mouse.y()
        self._mouse = pos
        kind = self._drag[0]
        if kind == 'orbit':
            self.yaw -= dx * 0.4
            self.pitch = max(-89.0, min(89.0, self.pitch + dy * 0.4))
        elif kind == 'pan':
            view = self.view_matrix()
            right, up = view[0, :3], view[1, :3]
            s = self.dist * 0.0016
            self.target += (-right * dx + up * dy) * s
        elif kind == 'move':
            hand = self._drag[1]
            view = self.view_matrix()
            right, up = view[0, :3], view[1, :3]
            depth = np.linalg.norm(hand.position - self.eye())
            s = depth * 2 * math.tan(math.radians(19)) / max(1, self.height())
            hand.position = hand.position + (right * dx - up * dy) * s
            self.changed.emit()
        elif kind == 'turn':
            hand = self._drag[1]
            view = self.view_matrix()
            right, up = view[0, :3], view[1, :3]
            q = quat_mul(quat_axis_angle(up, dx * 0.5), quat_axis_angle(right, dy * 0.5))
            hand.rotation = quat_norm(quat_mul(q, hand.rotation))
            self.changed.emit()
        self.update()

    def mouseReleaseEvent(self, e):
        moved = self._press is not None and (e.position().toPoint() - self._press).manhattanLength() > 3
        if e.button() == Qt.MouseButton.RightButton and not moved:
            self.context.emit(e.globalPosition().toPoint())
        if e.button() == Qt.MouseButton.LeftButton and not moved and self._drag and self._drag[0] == 'orbit':
            # A click on nothing: nothing selected.
            if self.selected_hand is not None:
                self.selected_hand = None
                self.selected.emit(None)
        self._drag = None
        self.update()

    def wheelEvent(self, e):
        f = 0.9 if e.angleDelta().y() > 0 else 1.1
        self.dist = max(0.05, min(8.0, self.dist * f))
        self.update()


def _ray_box(o, d, bmin, bmax):
    inv = 1.0 / np.where(np.abs(d) < 1e-12, 1e-12, d)
    t1 = (bmin - 0.002 - o) * inv
    t2 = (bmax + 0.002 - o) * inv
    tmin = np.max(np.minimum(t1, t2))
    tmax = np.min(np.maximum(t1, t2))
    return tmax >= max(tmin, 0)
