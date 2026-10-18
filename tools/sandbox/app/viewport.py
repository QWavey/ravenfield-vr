"""The 3D view: an OpenGL 3.3 renderer for the exported weapon and the two gloves, with an orbit camera, picking and
dragging (move a hand in the view's plane; Shift turns it; Ctrl + drag moves the gun, Ctrl + Shift + drag turns it about
its grip), and Blender's transform gizmos on the selected hand or the gun (gizmo.py): arrows, plane squares and rings."""
import ctypes
import math

import numpy as np
from OpenGL import GL
import gizmo as gz
from PyQt6.QtCore import QPoint, Qt, pyqtSignal
from PyQt6.QtGui import QSurfaceFormat
from PyQt6.QtOpenGLWidgets import QOpenGLWidget

from mathutil import look_at, norm, perspective, quat_axis_angle, quat_mul, quat_norm, quat_to_mat3, ray_triangles

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


def _normal_matrix(model):
    """The matrix that turns a part's normals with its model matrix: the inverse transpose, which stays right for a scaled part."""
    return np.linalg.inv(model[:3, :3]).T


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
        self.tex_key = None
        if image is not None and uvs is not None:
            self.tex = _texture(image)
            self.tex_key = id(image)

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
            _release_texture(self.tex_key)
            self.tex = 0


# Textures by image, counted: freed (and the image let go) when the last mesh using one is deleted. Kept forever, every
# weapon opened stayed in memory; freed per mesh, a second mesh with the same image drew a deleted texture.
_tex_cache = {}


def _release_texture(key):
    entry = _tex_cache.get(key)
    if entry is None:
        return
    entry[2] -= 1
    if entry[2] <= 0:
        GL.glDeleteTextures(1, [entry[0]])
        del _tex_cache[key]


def _texture(image):
    key = id(image)
    if key in _tex_cache:
        _tex_cache[key][2] += 1
        return _tex_cache[key][0]
    img = image.convert('RGBA')
    data = np.asarray(img, dtype=np.uint8)
    t = GL.glGenTextures(1)
    GL.glBindTexture(GL.GL_TEXTURE_2D, t)
    GL.glTexImage2D(GL.GL_TEXTURE_2D, 0, GL.GL_SRGB8_ALPHA8, img.width, img.height, 0, GL.GL_RGBA, GL.GL_UNSIGNED_BYTE, data)
    GL.glGenerateMipmap(GL.GL_TEXTURE_2D)
    GL.glTexParameteri(GL.GL_TEXTURE_2D, GL.GL_TEXTURE_MIN_FILTER, GL.GL_LINEAR_MIPMAP_LINEAR)
    GL.glTexParameteri(GL.GL_TEXTURE_2D, GL.GL_TEXTURE_MAG_FILTER, GL.GL_LINEAR)
    _tex_cache[key] = [t, image, 1]  # the image kept alive with its id
    return t


class Viewport(QOpenGLWidget):
    """Signals: picked(point_world or None, what) on a laser / Alt click; selected(hand or None); changed(hand) while a hand
    moves or turns; drag_finished(hand) when that drag ends; weapon_moved() while the gun is moved or turned (Ctrl + drag);
    context(QPoint global) on a right click without a drag."""
    picked = pyqtSignal(object, object)
    selected = pyqtSignal(object)
    changed = pyqtSignal(object)  # the hand moved or turned
    drag_finished = pyqtSignal(object)  # the hand's drag ended: its job is worked out now, once
    weapon_moved = pyqtSignal()  # the gun moved or turned: the hands on it went along
    drag_started = pyqtSignal()  # a hand or the gun is about to be moved (an undo step)
    drag_cancelled = pyqtSignal()  # a gizmo drag taken back (Esc or a right click): its undo step is not needed
    context = pyqtSignal(QPoint)
    hovered = pyqtSignal(str)
    gizmo_info = pyqtSignal(str)  # what a gizmo drag moves and how far ('' when it ends)
    gizmo_target_changed = pyqtSignal(str)  # 'hand' or 'gun': what the gizmo is on

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
        self.touch_node = None  # the part the selected hand touches (main.py update_selected_label), tinted (review I9#2)
        self.part_node = None  # the part chosen in the parts list (kept highlighted)
        self.pick_point = None
        self.pick_line = None  # (from, to) world
        self.pick_normal = None  # the surface normal under the last laser / Alt click (world, unit, facing the viewer)
        self.show_grid = True
        self.show_markers = True
        self._gpu_nodes = {}
        self._gpu_hands = {}
        self._mouse = None
        self._press = None
        self._drag = None
        self._ready = False
        self.background = (0.22, 0.235, 0.26)
        # The transform gizmo (Blender's): on the selected hand or on the gun. Free: none (drag the hand itself, as before;
        # that still works with every gizmo).
        self.gizmo_mode = 'move'  # gizmo.MODES
        self.gizmo_space = 'global'  # gizmo.SPACES: the world's axes, the hand's own (or the gun's), the gun's
        self.gizmo_target = 'hand'
        self.gizmo_snap = False  # snap without Ctrl (Ctrl then moves freely), as Blender's magnet
        self.show_gizmo = True  # off for clean pictures
        self.show_axes = True  # the corner key of the world's axes (off for clean pictures too)
        self._drag_start = None  # what a drag began from: (hand, position, rotation) or the gun's place
        self._eat_release = None  # the button whose release ends a cancel (a right click: no menu after it)
        self.gizmo_hover = None
        self._gz = None  # the gizmo drag going on: _GizmoDrag

    # ---- GL setup -----------------------------------------------------------------------------------------------

    def initializeGL(self):
        # (Again for a new context, as Qt does when the view changes window: the old names are gone with the old context.)
        _tex_cache.clear()
        self._gpu_nodes = {}
        self._gpu_hands = {}
        self.lit = _compile(LIT_VS, LIT_FS)
        self.flat = _compile(FLAT_VS, FLAT_FS)
        # The uniform locations are looked up once here: they never change while the program lives, and a name lookup
        # on every draw is pure overhead (a gun with dozens of parts draws dozens of times per paint).
        self.lit_loc = {n: GL.glGetUniformLocation(self.lit, n)
                        for n in ('uMVP', 'uModel', 'uNormal', 'uColor', 'uTint', 'uHasTex', 'uTex', 'uCam')}
        self.flat_loc = {n: GL.glGetUniformLocation(self.flat, n) for n in ('uMVP', 'uColor')}
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

    def cleanup(self):
        """Frees every GPU resource while the context still exists (freed later, at exit, they crashed the program)."""
        if not self._ready:
            return
        self.makeCurrent()
        for meshes in self._gpu_nodes.values():
            for m in meshes:
                m.delete()
        for m in self._gpu_hands.values():
            m.delete()
        self._gpu_nodes = {}
        self._gpu_hands = {}
        GL.glDeleteVertexArrays(1, [self.line_vao])
        GL.glDeleteBuffers(1, [self.line_vbo])
        GL.glDeleteProgram(self.lit)
        GL.glDeleteProgram(self.flat)
        self._ready = False
        self.doneCurrent()

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
        if getattr(self, 'ortho', False):
            # Orthographic (O, as in Blender): parallel lines stay parallel, for checking a hand against the gun's lines. Its
            # half height is what the perspective view shows at the target, so dragging and panning keep their scale.
            half = self.dist * math.tan(math.radians(19))
            a = w / h
            m = np.eye(4)
            m[0, 0], m[1, 1] = 1.0 / (half * a), 1.0 / half
            m[2, 2], m[2, 3] = -2.0 / (20.0 - 0.005), -(20.0 + 0.005) / (20.0 - 0.005)
            return m
        return perspective(38.0, w / h, 0.005, 20.0)

    def toggle_ortho(self):
        self.ortho = not getattr(self, 'ortho', False)
        self.update()

    def frame(self, center, radius):
        self.target = np.array(center, dtype=np.float64)
        self.dist = max(0.15, radius / math.sin(math.radians(19)) * 1.05)
        self.update()

    def set_view(self, name):
        self.yaw, self.pitch = {'side': (0.0, 0.0), 'top': (0.0, 89.0), 'front': (90.0, 0.0), 'three': (-25.0, 12.0)}.get(name, (self.yaw, self.pitch))
        self.update()

    def _px_scale(self, depth):
        """World metres per screen pixel at this depth (the lens is 38 degrees high). The pan, the hands and the gun all use
        it, so the point under the pointer stays under it whatever the window's size."""
        return depth * 2 * math.tan(math.radians(19)) / max(1, self.height())

    # ---- drawing -------------------------------------------------------------------------------------------------

    def paintGL(self):
        # clear_alpha is 0 only while a transparent PNG is drawn (Workbench._grab_big sets it for that grab); else opaque.
        GL.glClearColor(*self.background, getattr(self, 'clear_alpha', 1.0))
        GL.glClear(GL.GL_COLOR_BUFFER_BIT | GL.GL_DEPTH_BUFFER_BIT)
        view, proj = self.view_matrix(), self.proj_matrix()
        vp = proj @ view
        eye = self.eye()
        draws_opaque, draws_blend = [], []
        w = self.scene.weapon
        # The gun faintly lit while the gizmo is on it (its grip is where the right hand is: else the two look the same).
        gun_on = (0.07, 0.05, 0.0) if self.show_gizmo and self.gizmo_target == 'gun' and self.gizmo_mode != 'free' else (0.0, 0.0, 0.0)
        if w is not None:
            for node in w.nodes:
                if node.hidden or node.away:
                    continue
                model = w.node_matrix(node)
                nm = _normal_matrix(model)  # once per part, not once per mesh of it
                tint = (0.18, 0.12, 0.02) if node is self.hover_node else (0.05, 0.16, 0.22) if node is self.part_node else gun_on
                # (A part by role, or the one the selected hand touches: a body's role would tint the whole gun, review I9#2.)
                if (self.scene.highlight_role and node.role == self.scene.highlight_role) or node is self.touch_node:
                    tint = (0.05, 0.1, 0.22)
                for g in self._gpu_nodes.get(id(node), []):
                    (draws_blend if g.blend else draws_opaque).append((g, model, nm, tint, False))
        for hand in self.scene.hands():
            g = self._hand_mesh(hand)
            tint = (0.1, 0.07, 0.0) if hand is self.selected_hand else (0.0, 0.0, 0.0)
            model = hand.model_matrix()
            draws_opaque.append((g, model, _normal_matrix(model), tint, hand.mirror < 0))
        for node, model in self.scene.extra_draws():
            nm = _normal_matrix(model)
            for g in self._gpu_nodes.get(id(node), []):
                (draws_blend if g.blend else draws_opaque).append((g, model, nm, (0, 0, 0), False))
        GL.glUseProgram(self.lit)
        GL.glUniform3f(self.lit_loc['uCam'], *eye)
        GL.glEnable(GL.GL_CULL_FACE)
        for g, model, nm, tint, mirrored in draws_opaque:
            self._draw(g, model, nm, vp, tint, mirrored)
        if draws_blend:
            GL.glEnable(GL.GL_BLEND)
            GL.glBlendFunc(GL.GL_SRC_ALPHA, GL.GL_ONE_MINUS_SRC_ALPHA)
            GL.glDepthMask(GL.GL_FALSE)
            for g, model, nm, tint, mirrored in draws_blend:
                self._draw(g, model, nm, vp, tint, mirrored)
            GL.glDepthMask(GL.GL_TRUE)
            GL.glDisable(GL.GL_BLEND)
        GL.glDisable(GL.GL_CULL_FACE)
        self._lines(vp)
        if self.show_gizmo:
            self._draw_gizmo(view, proj)
        if self.show_axes:
            self._axis_gizmo(view)

    def _draw(self, g, model, nm, vp, tint, mirrored):
        loc = self.lit_loc
        GL.glUniformMatrix4fv(loc['uMVP'], 1, GL.GL_TRUE, (vp @ model).astype(np.float32))
        GL.glUniformMatrix4fv(loc['uModel'], 1, GL.GL_TRUE, model.astype(np.float32))
        GL.glUniformMatrix3fv(loc['uNormal'], 1, GL.GL_TRUE, nm.astype(np.float32))
        GL.glUniform4f(loc['uColor'], *g.color)
        GL.glUniform3f(loc['uTint'], *tint)
        GL.glUniform1i(loc['uHasTex'], 1 if g.tex else 0)
        if g.tex:
            GL.glActiveTexture(GL.GL_TEXTURE0)
            GL.glBindTexture(GL.GL_TEXTURE_2D, g.tex)
            GL.glUniform1i(loc['uTex'], 0)
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
        GL.glUniformMatrix4fv(self.flat_loc['uMVP'], 1, GL.GL_TRUE, vp.astype(np.float32))
        GL.glBindVertexArray(self.line_vao)
        GL.glBindBuffer(GL.GL_ARRAY_BUFFER, self.line_vbo)
        for pts, color, depth in segs:
            data = np.ascontiguousarray(pts, dtype=np.float32)[:4096]
            GL.glBufferSubData(GL.GL_ARRAY_BUFFER, 0, data)
            GL.glUniform4f(self.flat_loc['uColor'], *color)
            if not depth:
                GL.glDisable(GL.GL_DEPTH_TEST)
            GL.glDrawArrays(GL.GL_LINES, 0, len(data))
            GL.glEnable(GL.GL_DEPTH_TEST)
        GL.glBindVertexArray(0)

    # ---- the transform gizmo -------------------------------------------------------------------------------------------

    def _px_at(self, p):
        """Metres per screen pixel at a point (the same everywhere in the orthographic view)."""
        if getattr(self, 'ortho', False):
            return self._px_scale(self.dist)
        forward = -self.view_matrix()[2, :3]
        return self._px_scale(max(0.01, float(np.dot(np.asarray(p) - self.eye(), forward))))

    def camera(self, view=None, proj=None):
        view = self.view_matrix() if view is None else view
        proj = self.proj_matrix() if proj is None else proj
        return gz.Camera(self.eye(), view, proj, self.width(), self.height(), getattr(self, 'ortho', False), self._px_at)

    def editing(self):
        """A hand or the gun is being dragged (by the gizmo or freely): keys and the wheel leave the scene alone then."""
        return self._drag is not None and self._drag[0] in ('gizmo', 'move', 'turn', 'gun_move', 'gun_turn')

    def gizmo_space_shown(self):
        """The axes the gizmo shows, by name: on the gun, Local is the gun's own axes."""
        return 'gun' if self.gizmo_target == 'gun' and self.gizmo_space != 'global' else self.gizmo_space

    def gizmo_frame(self):
        """(centre, axes as the columns of a 3x3) of the gizmo now, or None when it has nothing to act on. On a hand (the
        selected one, else the right one, as the panels): its palm centre as drawn, on the glove (a hand settled on the gun is
        drawn up to 2 cm off its place; a gizmo drag first takes the drawn place as its place, so nothing jumps: see
        _start_gizmo); on the gun: its grip (it turns about it, as Ctrl + Shift + drag does)."""
        if self.gizmo_mode not in ('move', 'rotate', 'both'):
            return None
        w = self.scene.weapon
        gun_axes = w.P[:3, :3].copy() if w is not None else np.eye(3)  # along the barrel, up, the right side
        if self.gizmo_target == 'gun':
            if w is None:
                return None
            return w.to_world(w.grip), (np.eye(3) if self.gizmo_space == 'global' else gun_axes)
        h = self.selected_hand or self.scene.right
        if self.gizmo_space == 'local':
            axes = quat_to_mat3(h.rotation)
        elif self.gizmo_space == 'gun':
            axes = gun_axes
        else:
            axes = np.eye(3)
        return h.drawn_position().copy(), axes

    def gizmo_hit(self, pos):
        f = self.gizmo_frame()
        if f is None:
            return None
        return gz.hit(self.camera(), f[0], f[1], self.gizmo_mode, pos.x(), pos.y())

    def _draw_gizmo(self, view, proj):
        f = self.gizmo_frame()
        if f is None:
            return
        cam = self.camera(view, proj)
        g = self._gz
        pieces = gz.build(cam, f[0], f[1], self.gizmo_mode, None if g else self.gizmo_hover, g.drag if g else None)
        if not pieces:
            return
        GL.glDisable(GL.GL_DEPTH_TEST)  # over the gun and the hands, always
        GL.glEnable(GL.GL_BLEND)
        GL.glBlendFunc(GL.GL_SRC_ALPHA, GL.GL_ONE_MINUS_SRC_ALPHA)
        GL.glUseProgram(self.flat)
        GL.glUniformMatrix4fv(self.flat_loc['uMVP'], 1, GL.GL_TRUE, np.eye(4, dtype=np.float32))
        GL.glBindVertexArray(self.line_vao)
        GL.glBindBuffer(GL.GL_ARRAY_BUFFER, self.line_vbo)
        for tris, rgba in pieces:
            if len(tris) == 0:
                continue
            ndc = np.column_stack([tris[:, 0] / cam.w * 2.0 - 1.0, 1.0 - tris[:, 1] / cam.h * 2.0, np.zeros(len(tris))])
            GL.glUniform4f(self.flat_loc['uColor'], *rgba)
            for k in range(0, len(ndc), 4095):  # (the buffer holds 4096 points: whole triangles of them)
                chunk = np.ascontiguousarray(ndc[k:k + 4095], dtype=np.float32)
                GL.glBufferSubData(GL.GL_ARRAY_BUFFER, 0, chunk)
                GL.glDrawArrays(GL.GL_TRIANGLES, 0, len(chunk))
        GL.glBindVertexArray(0)
        GL.glDisable(GL.GL_BLEND)
        GL.glEnable(GL.GL_DEPTH_TEST)

    def set_gizmo_target(self, target):
        if target != self.gizmo_target:
            self.gizmo_target = target
            self.gizmo_hover = None
            self.gizmo_target_changed.emit(target)
            self.update()

    def _start_gizmo(self, handle, pos):
        if self.gizmo_frame() is None:
            return False
        # One undo step for the drag (dropped again when it changes nothing); a reload still playing is run to its end
        # first (the Workbench), so the frame is taken after it.
        self.drag_started.emit()
        hand = (self.selected_hand or self.scene.right) if self.gizmo_target == 'hand' else None
        if hand is not None and not np.allclose(hand.drawn_position(), hand.position, atol=1e-12):
            # The glove settled onto the gun is drawn off its place: the drag starts from where it is seen (the gizmo is
            # there), so neither jumps on the first move; a cancel puts the place and the settle back (the undo step).
            hand.position = hand.drawn_position().copy()
            hand.settle = np.zeros(3)
            hand._settled_at = None
        f = self.gizmo_frame()
        if f is None:
            return False
        cam = self.camera()
        ring_r, _ = gz.sizes(self.gizmo_mode)
        drag = gz.Drag(cam, handle, f[0], f[1], pos.x(), pos.y(), ring_r or gz.ROT_R)
        self._gz = _GizmoDrag(drag, hand, self.scene.weapon)
        self._drag = ('gizmo',)
        self.gizmo_hover = handle
        self.setCursor(Qt.CursorShape.ClosedHandCursor)  # (a closed hand while the handle is dragged; restored at its end)
        self.update()
        return True

    def _gizmo_move(self, pos, mods):
        g = self._gz
        fine = bool(mods & Qt.KeyboardModifier.ShiftModifier)
        snap = bool(mods & Qt.KeyboardModifier.ControlModifier) != self.gizmo_snap  # Ctrl snaps; with the magnet on it frees
        step = g.drag.follow(pos.x(), pos.y(), fine)
        if not step.any():
            return
        cam = self.camera()
        kind = g.drag.handle[0]
        space = self.gizmo_space_shown()
        if kind in ('axis', 'plane', 'view'):
            moved = g.drag.move(cam, snap)
            self._gizmo_apply(moved=moved)
            self.gizmo_info.emit(gz.describe(g.drag, space, moved))
        elif kind in ('ring', 'vring'):
            g.drag.turn(cam, snap)
            self._gizmo_apply(turn=g.drag.angle)
            self.gizmo_info.emit(gz.describe(g.drag, space))
        else:  # the trackball: free, as Shift + drag on a hand, from where it began (so Ctrl snaps it too)
            yaw, pitch = g.drag.ball_turn(snap)
            q = quat_mul(quat_axis_angle(g.drag.cam_up, yaw), quat_axis_angle(g.drag.cam_right, pitch))
            self._gizmo_apply(free=q)
            self.gizmo_info.emit(gz.describe(g.drag, space))

    def _gizmo_apply(self, moved=None, turn=None, free=None):
        """The hand or the gun put where the drag has taken it, from where it began (nothing drifts)."""
        g = self._gz
        if g.hand is not None:
            h = g.hand
            if moved is not None:
                h.position = g.p0 + moved
            elif turn is not None:
                h.rotation = quat_norm(quat_mul(quat_axis_angle(g.drag.axis, turn), g.q0))
            else:
                h.rotation = quat_norm(quat_mul(free, g.q0))
            g.changed |= not (np.allclose(h.position, g.p0, atol=1e-12) and np.allclose(h.rotation, g.q0, atol=1e-12))
            self.changed.emit(h)
            return
        if g.weapon is None or g.weapon is not self.scene.weapon:
            return
        if moved is not None:
            want = np.eye(4)
            want[:3, 3] = moved
        elif turn is not None:
            want = _about(g.drag.c0, quat_to_mat3(quat_axis_angle(g.drag.axis, turn)))
        else:
            want = _about(g.drag.c0, quat_to_mat3(free))
        self.scene.move_weapon(want @ np.linalg.inv(g.applied))  # (the hands on it go along)
        g.applied = want
        g.changed |= not np.allclose(want, np.eye(4), atol=1e-12)
        self.weapon_moved.emit()

    def cancel_gizmo(self):
        """Esc or a right click while a gizmo drag goes on: everything back where the drag began. The place here; the rest
        (the part the hand was on, its job: a drag clears them) from the undo step the drag's start kept (drag_cancelled)."""
        g = self._gz
        if g is None:
            return False
        if g.hand is not None:
            g.hand.position, g.hand.rotation = g.p0.copy(), g.q0.copy()
        elif g.weapon is not None and g.weapon is self.scene.weapon:
            self.scene.move_weapon(np.linalg.inv(g.applied))
        self._gz = None
        self._drag = None
        self._drag_start = None
        self.gizmo_hover = None
        self._restore_cursor(self._mouse)  # (the closed hand of the drag goes)
        self.gizmo_info.emit('')
        self.drag_cancelled.emit()
        self.update()
        return True

    def keyPressEvent(self, e):
        if e.key() == Qt.Key.Key_Escape and self.cancel_gizmo():
            return
        # The arrows and + / - are the view's while it has the focus (a click on it): the weapon list keeps its arrows and the
        # text boxes their keys. Not while a gizmo drag goes on (it measures from where the camera stood).
        if not self.editing() and self._camera_key(e.key()):
            return
        super().keyPressEvent(e)

    def _camera_key(self, key):
        """The arrows orbit the view as a mouse drag in that direction does (5 degrees a press, the pitch clamped as the drag
        clamps it); + and - zoom by one wheel notch. True when the key was used."""
        if key in (Qt.Key.Key_Left, Qt.Key.Key_Right):
            self.yaw += 5.0 if key == Qt.Key.Key_Left else -5.0
        elif key in (Qt.Key.Key_Up, Qt.Key.Key_Down):
            self.pitch = max(-89.0, min(89.0, self.pitch + (-5.0 if key == Qt.Key.Key_Up else 5.0)))
        elif key in (Qt.Key.Key_Plus, Qt.Key.Key_Minus):
            self.dist = max(0.05, min(8.0, self.dist * (0.9 if key == Qt.Key.Key_Plus else 1 / 0.9)))  # (wheelEvent's notch)
        else:
            return False
        self.update()
        return True

    def _axis_gizmo(self, view):
        """A small key in the bottom-left corner: the world's X (red), Y (green) and Z (blue) axes, turning as the view turns
        (so the gun's up and aim can be read against them). Drawn in its own square of the window, over the scene."""
        dpr = self.devicePixelRatioF()
        full_w, full_h = int(round(self.width() * dpr)), int(round(self.height() * dpr))
        size, margin = int(round(88 * dpr)), int(round(10 * dpr))
        rot = np.array(view, dtype=np.float64)
        rot[:3, 3] = 0.0  # the view turned, not moved: the key stays at its own centre
        back = np.eye(4)
        back[2, 3] = -2.0  # the key's origin two units in front of the eye
        vp = perspective(30.0, 1.0, 0.1, 10.0) @ back @ rot
        GL.glViewport(margin, margin, size, size)
        GL.glDisable(GL.GL_DEPTH_TEST)
        GL.glUseProgram(self.flat)
        GL.glUniformMatrix4fv(self.flat_loc['uMVP'], 1, GL.GL_TRUE, vp.astype(np.float32))
        GL.glBindVertexArray(self.line_vao)
        GL.glBindBuffer(GL.GL_ARRAY_BUFFER, self.line_vbo)
        for axis, color in ((np.array([1.0, 0, 0]), (0.9, 0.3, 0.3, 1.0)), (np.array([0, 1.0, 0]), (0.4, 0.85, 0.4, 1.0)),
                            (np.array([0, 0, 1.0]), (0.35, 0.6, 1.0, 1.0))):
            GL.glBufferSubData(GL.GL_ARRAY_BUFFER, 0, np.array([[0, 0, 0], axis * 0.8], dtype=np.float32))
            GL.glUniform4f(self.flat_loc['uColor'], *color)
            GL.glDrawArrays(GL.GL_LINES, 0, 2)
        GL.glBindVertexArray(0)
        GL.glEnable(GL.GL_DEPTH_TEST)
        GL.glViewport(0, 0, full_w, full_h)  # the rest of the window is the scene's again

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
                try:
                    inv = np.linalg.inv(m)
                except np.linalg.LinAlgError:
                    continue  # a part scaled to nothing cannot be hit
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
                if node.hidden or node.away:
                    continue
                m = w.node_matrix(node)
                try:
                    inv = np.linalg.inv(m)
                except np.linalg.LinAlgError:
                    continue  # a part scaled to nothing cannot be hit
                lo = (inv @ np.append(o, 1))[:3]
                ld = (inv @ np.append(d, 0))[:3]
                if not _ray_box(lo, ld, node.bmin, node.bmax):
                    continue
                t, k = ray_triangles(lo, ld, node.tris)
                if t is not None:
                    pw = (m @ np.append(lo + ld * t, 1))[:3]
                    dist = np.linalg.norm(pw - o)
                    if best is None or dist < best[3]:
                        best = ('node', node, pw, dist, _surface_normal(m, node.tris[k], d, o, pw))
        return best

    # ---- mouse -----------------------------------------------------------------------------------------------------

    def mousePressEvent(self, e):
        if self._gz is not None:
            # While a gizmo drag goes on, a right click takes it back (as in Blender), and its release opens no menu; any
            # other button is left alone (a middle press ended the drag and started a pan; review).
            if e.button() == Qt.MouseButton.RightButton:
                self.cancel_gizmo()
                self._eat_release = Qt.MouseButton.RightButton
            return
        self._mouse = e.position().toPoint()
        self._press = self._mouse
        self._end_drag()  # (a drag never ended before this press is ended now, its hand worked out)
        alt = bool(e.modifiers() & Qt.KeyboardModifier.AltModifier)
        ctrl = bool(e.modifiers() & Qt.KeyboardModifier.ControlModifier)
        shift = bool(e.modifiers() & Qt.KeyboardModifier.ShiftModifier)
        if e.button() == Qt.MouseButton.LeftButton:
            # The gizmo's handles come first (Alt + click stays the laser's); inside its rings (the trackball) only where no
            # hand is under the pointer: dragging a hand itself moves it freely, as before.
            handle = None if alt else self.gizmo_hit(self._mouse)
            if handle is not None and handle != ('ball',) and self._start_gizmo(handle, self._mouse):
                return
            if self.laser or alt:
                hit = self.pick(self._mouse, hands=False)
                if ctrl and hit is not None:
                    self._start_gun_drag(hit, shift)  # Ctrl + drag moves the gun even with the laser on
                    return
                self._set_pick(hit)
                self._drag = ('orbit',)  # (with the laser on, dragging still looks around)
                return
            # The nearest of hands and gun: a glove behind the gun is not taken through it (the press orbits).
            hit = self.pick(self._mouse)
            if hit is not None and hit[0] == 'hand':
                if self.selected_hand is not hit[1]:
                    self.selected_hand = hit[1]
                    self.selected.emit(hit[1])
                self.set_gizmo_target('hand')
                self.drag_started.emit()
                self._drag = ('turn' if shift else 'move', hit[1], hit[2])
                self._drag_start = (hit[1], hit[1].position.copy(), hit[1].rotation.copy())
                self.setCursor(Qt.CursorShape.ClosedHandCursor)  # (a closed hand while it is dragged; restored on release)
                self.update()
                return
            if ctrl and hit is not None:
                self._start_gun_drag(hit, shift)
                return
            # The trackball only where nothing is under the pointer: a press on the gun inside the rings looks around, as
            # before (it turned the hand; review).
            if handle == ('ball',) and hit is None and self._start_gizmo(handle, self._mouse):
                return
            self._drag = ('orbit',)
        elif e.button() == Qt.MouseButton.RightButton:
            self._drag = ('pan',)
        elif e.button() == Qt.MouseButton.MiddleButton:
            self._drag = ('pan',)

    def _start_gun_drag(self, hit, shift):
        """Ctrl + drag on the gun: it moves in the view's plane; with Shift it turns about its grip. One undo step per drag."""
        self.drag_started.emit()
        w = self.scene.weapon
        self._drag_start = ('gun', w.P.copy()) if w is not None else None
        if shift:
            self._drag = ('gun_turn',)
        else:
            forward = -self.view_matrix()[2, :3]
            # The depth it is grabbed at: a move in the view's plane keeps it, so the pixel scale stays the same all drag.
            self._drag = ('gun_move', float(np.dot(hit[2] - self.eye(), forward)))
        self.update()

    def _end_drag(self):
        """Ends the drag. A hand that was moved is reported once (drag_finished): its job is worked out then, not on every
        move. A drag that changed nothing (a click on a hand, a handle or Ctrl + click on the gun) leaves no undo step and
        keeps the hand's part and job (drag_cancelled: they went back to the step its start kept; review)."""
        drag, self._drag = self._drag, None
        start, self._drag_start = self._drag_start, None
        if drag is None:
            return
        kind = drag[0]
        if kind in ('move', 'turn'):
            h = drag[1]
            same = start is not None and start[0] is h and np.allclose(h.position, start[1], atol=1e-12) \
                and np.allclose(h.rotation, start[2], atol=1e-12)
            if same:
                self.drag_cancelled.emit()
            else:
                self.drag_finished.emit(h)
        elif kind in ('gun_move', 'gun_turn'):
            w = self.scene.weapon
            if start is not None and w is not None and np.allclose(w.P, start[1], atol=1e-12):
                self.drag_cancelled.emit()
        elif kind == 'gizmo':
            g, self._gz = self._gz, None
            self.gizmo_hover = None
            if g is not None and not g.changed:
                self.drag_cancelled.emit()
            elif g is not None and g.hand is not None:
                self.drag_finished.emit(g.hand)  # (its job worked out once, as after a free drag)
            self.gizmo_info.emit('')

    def _set_pick(self, hit):
        if hit is None or hit[0] != 'node':
            self.pick_point = None
            self.pick_line = None
            self.pick_normal = None
            self.picked.emit(None, None)
        else:
            self.pick_point = hit[2]
            self.pick_normal = hit[4]
            src = self.scene.laser_origin()
            if src is None:
                o, d = self.ray(self._mouse)
                src = o + d * 0.02
            self.pick_line = [src, hit[2]]
            self.picked.emit(hit[2], hit[1])
        self.update()

    def _hover_handle(self, pos):
        """The gizmo handle under the pointer, or None. The trackball lights only where a press would start it: off the hand
        and the gun, and not with the laser (its press picks)."""
        handle = self.gizmo_hit(pos)
        if handle == ('ball',) and (self.laser or self.pick(pos) is not None):
            handle = None
        return handle

    def _hover_shape(self, pos, handle, alt):
        """The pointer's shape at pos while nothing is dragged, for what a press there does: an open hand over a gizmo handle
        (the press drags it, laser or not) or over a hand (the press takes it), a cross while the laser or Alt picks a part,
        else the arrow. A drag shows a closed hand instead (mousePressEvent, _start_gizmo)."""
        if handle is not None and not alt:
            return Qt.CursorShape.OpenHandCursor
        if self.laser or alt:
            return Qt.CursorShape.CrossCursor
        hit = self.pick(pos)  # (the nearest of hands and gun, as a press takes it)
        if hit is not None and hit[0] == 'hand':
            return Qt.CursorShape.OpenHandCursor
        return Qt.CursorShape.ArrowCursor

    def _restore_cursor(self, pos, alt=False):
        """The shape the pointer has over the view once a drag has ended (or was cancelled)."""
        if pos is None:
            self.setCursor(Qt.CursorShape.ArrowCursor)
        else:
            self.setCursor(self._hover_shape(pos, self._hover_handle(pos), alt))

    def mouseMoveEvent(self, e):
        pos = e.position().toPoint()
        if self._drag is not None and e.buttons() == Qt.MouseButton.NoButton:
            self._end_drag()  # (the release was lost, outside the window: plain moves no longer drag)
        if self._mouse is None or self._drag is None:
            handle = self._hover_handle(pos) if self._drag is None else None
            if handle != self.gizmo_hover:
                self.gizmo_hover = handle
                self.update()
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
            self.setCursor(self._hover_shape(pos, handle, bool(e.modifiers() & Qt.KeyboardModifier.AltModifier)))
            self._mouse = pos
            return
        dx, dy = pos.x() - self._mouse.x(), pos.y() - self._mouse.y()
        self._mouse = pos
        kind = self._drag[0]
        if kind == 'gizmo':
            self._gizmo_move(pos, e.modifiers())
        elif kind == 'orbit':
            self.yaw -= dx * 0.4
            self.pitch = max(-89.0, min(89.0, self.pitch + dy * 0.4))
        elif kind == 'pan':
            view = self.view_matrix()
            right, up = view[0, :3], view[1, :3]
            # The target is one distance ahead of the eye: the pixel scale there, the same as a hand's drag uses.
            self.target += (-right * dx + up * dy) * self._px_scale(self.dist)
        elif kind == 'move':
            hand = self._drag[1]
            view = self.view_matrix()
            right, up = view[0, :3], view[1, :3]
            # Metres per pixel where the hand is (the same everywhere in the orthographic view: there the hand's own depth ran
            # it ahead of or behind the pointer, real2 B4#4).
            hand.position = hand.position + (right * dx - up * dy) * self._px_at(hand.position)
            self.changed.emit(hand)
        elif kind == 'turn':
            hand = self._drag[1]
            view = self.view_matrix()
            right, up = view[0, :3], view[1, :3]
            q = quat_mul(quat_axis_angle(up, dx * 0.5), quat_axis_angle(right, dy * 0.5))
            hand.rotation = quat_norm(quat_mul(q, hand.rotation))
            self.changed.emit(hand)
        elif kind == 'gun_move':
            view = self.view_matrix()
            right, up = view[0, :3], view[1, :3]
            step = np.eye(4)
            step[:3, 3] = (right * dx - up * dy) * self._px_scale(self._drag[1])
            self.scene.move_weapon(step)  # the hands on the gun go along
            self.weapon_moved.emit()
        elif kind == 'gun_turn':
            view = self.view_matrix()
            right, up = view[0, :3], view[1, :3]
            turn = quat_to_mat3(quat_mul(quat_axis_angle(up, dx * 0.5), quat_axis_angle(right, dy * 0.5)))
            grip = self.scene.weapon.to_world(self.scene.weapon.grip)
            step = np.eye(4)
            step[:3, :3] = turn
            step[:3, 3] = grip - turn @ grip  # turned about the grip: the grip point does not move
            self.scene.move_weapon(step)
            self.weapon_moved.emit()
        self.update()

    def leaveEvent(self, e):
        if self.gizmo_hover is not None and self._gz is None:
            self.gizmo_hover = None
            self.update()
        if self.hover_node is not None:
            self.hover_node = None
            self.hovered.emit('')
            self.update()
        super().leaveEvent(e)

    def mouseReleaseEvent(self, e):
        if self._eat_release is not None and e.button() == self._eat_release:
            self._eat_release = None  # (the release of the right click that took a gizmo drag back: no menu)
            return
        moved = self._press is not None and (e.position().toPoint() - self._press).manhattanLength() > 3
        if e.button() == Qt.MouseButton.RightButton and not moved:
            if self.laser or e.modifiers() & Qt.KeyboardModifier.AltModifier:
                # The menu acts on the part under the laser now, not the one clicked last.
                self._mouse = e.position().toPoint()
                self._set_pick(self.pick(self._mouse, hands=False))
            self.context.emit(e.globalPosition().toPoint())
        if (e.button() == Qt.MouseButton.LeftButton and not moved and self._drag is not None
                and self._drag[0] in ('gun_move', 'gun_turn')):
            self.set_gizmo_target('gun')  # Ctrl + click on the gun: the gizmo on it (a click on a hand puts it back there)
        # (A click on the gun or on nothing keeps the selected hand: dropping it left the controls editing the right hand.)
        self._end_drag()
        self._restore_cursor(e.position().toPoint(), bool(e.modifiers() & Qt.KeyboardModifier.AltModifier))
        self.update()

    def wheelEvent(self, e):
        if self.editing():
            return  # (zoomed while dragging, the part jumped: the drag measures from where the camera stood)
        dy = e.angleDelta().y()
        if dy == 0:
            return  # a sideways scroll does not zoom
        # A notch (120) is 10 % in, and out the exact way back (1 / 0.9: with 1.1 a notch in and one out ended 1 % off); a
        # touchpad's small steps each count for their size, so a flick does not hit a limit.
        f = 0.9 ** (dy / 120)
        self.dist = max(0.05, min(8.0, self.dist * f))
        self.update()


class _GizmoDrag:
    """A gizmo drag: the Drag (gizmo.py), the hand it moves (or None: the gun), and where it began."""

    def __init__(self, drag, hand, weapon):
        self.drag = drag
        self.hand = hand
        self.weapon = weapon
        self.p0 = hand.position.copy() if hand is not None else None
        self.q0 = hand.rotation.copy() if hand is not None else None
        self.applied = np.eye(4)  # the gun: the move given to it so far (taken back by a cancel)
        self.changed = False  # anything moved or turned yet (a click alone leaves no undo step)


def _about(pivot, rot3):
    """A 4x4 turn by rot3 about a point (the point stays)."""
    m = np.eye(4)
    m[:3, :3] = rot3
    m[:3, 3] = np.asarray(pivot) - rot3 @ np.asarray(pivot)
    return m


def _surface_normal(m, tri, d, o, p):
    """The unit normal, in world space, of the triangle a ray hit at p: tri is in its part's own frame and m is the part's
    matrix. Turned to face the ray's origin o (the side the viewer sees)."""
    n = np.linalg.inv(m[:3, :3]).T @ np.cross(tri[1] - tri[0], tri[2] - tri[0])
    length = float(np.linalg.norm(n))
    if length < 1e-12:  # a sliver with no area: the ray's own direction stands in for it
        n = -np.asarray(d, dtype=np.float64)
        length = float(np.linalg.norm(n))
    n = n / length
    if np.dot(n, o - p) < 0:
        n = -n
    return n


def _ray_box(o, d, bmin, bmax):
    inv = 1.0 / np.where(np.abs(d) < 1e-12, 1e-12, d)
    t1 = (bmin - 0.002 - o) * inv
    t2 = (bmax + 0.002 - o) * inv
    tmin = np.max(np.minimum(t1, t2))
    tmax = np.min(np.maximum(t1, t2))
    return tmax >= max(tmin, 0)
