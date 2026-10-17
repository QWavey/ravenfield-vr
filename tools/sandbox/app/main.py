"""Ravenfield VR Sandbox: put the mod's hands on the exported weapons, pose them, play reloads, point at parts, save
poses, screenshots and GIFs, without starting the game.

    python tools/sandbox/app/main.py              (or the built RavenfieldVR-Sandbox.exe)
    python tools/sandbox/app/main.py --shot out.png [--weapon NAME]   render one picture and quit (for checks)
"""
import json
import math
import os
import subprocess
import sys
import time

import numpy as np
from PIL import Image
from PyQt6.QtCore import QProcess, QSettings, Qt, QTimer, QUrl
from PyQt6.QtGui import QDesktopServices, QGuiApplication, QIcon, QKeySequence, QShortcut
from PyQt6.QtWidgets import QApplication, QFrame, QHBoxLayout, QListWidgetItem, QStackedWidget, QVBoxLayout, QWidget
from qfluentwidgets import (Action, BodyLabel, CaptionLabel, CardWidget, ComboBox, DoubleSpinBox, FluentIcon as FIF, FluentWindow,
                            InfoBar, InfoBarPosition, LineEdit, ListWidget, NavigationItemPosition, Pivot, PlainTextEdit, PrimaryPushButton,
                            PushButton, RoundMenu, SearchLineEdit, Slider, SmoothScrollArea, StrongBodyLabel, SubtitleLabel, SwitchButton,
                            Theme, TitleLabel, TogglePushButton, setTheme, setThemeColor)

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)

from glove import FINGERS, POSES  # noqa: E402
from mathutil import quat_norm, quat_slerp, quat_to_euler_deg, euler_deg_to_quat, quat_mul, quat_inv  # noqa: E402
from scene import ROLE_NAMES, Scene, Weapon  # noqa: E402
from viewport import Viewport  # noqa: E402

APP_NAME = 'Ravenfield VR Sandbox'


def find_repo():
    """The mod's repository: the folder with src/Rig/Glove/glove.bin, upward from here (or the exe), or the one set before."""
    starts = [HERE, os.path.dirname(sys.executable), os.getcwd()]
    for s in starts:
        d = os.path.abspath(s)
        for _ in range(8):
            if os.path.exists(os.path.join(d, 'src', 'Rig', 'Glove', 'glove.bin')):
                return d
            d = os.path.dirname(d)
    saved = QSettings('RavenfieldVR', 'Sandbox').value('repo', '')
    return saved if saved and os.path.exists(os.path.join(saved, 'src', 'Rig', 'Glove', 'glove.bin')) else None


def safe(name):
    return ''.join(c if c.isalnum() or c in '-_.' else '_' for c in name).strip('_') or 'capture'


def card(title, *widgets, layout=None):
    c = CardWidget()
    v = QVBoxLayout(c)
    v.setContentsMargins(14, 12, 14, 12)
    v.setSpacing(8)
    if title:
        v.addWidget(StrongBodyLabel(title))
    for w in widgets:
        if isinstance(w, QWidget):
            v.addWidget(w)
        else:
            v.addLayout(w)
    return c


def row(*widgets, stretch=None):
    h = QHBoxLayout()
    h.setSpacing(6)
    for i, w in enumerate(widgets):
        h.addWidget(w, 1 if stretch is None else stretch[i])
    return h


class LabeledSlider(QWidget):
    def __init__(self, label, on_change, minimum=0, maximum=100, value=0, width=96):
        super().__init__()
        h = QHBoxLayout(self)
        h.setContentsMargins(0, 0, 0, 0)
        self.label = BodyLabel(label)
        self.label.setFixedWidth(width)
        self.slider = Slider(Qt.Orientation.Horizontal)
        self.slider.setRange(minimum, maximum)
        self.slider.setValue(value)
        self.value = CaptionLabel('')
        self.value.setFixedWidth(34)
        h.addWidget(self.label)
        h.addWidget(self.slider, 1)
        h.addWidget(self.value)
        self._cb = on_change
        self.slider.valueChanged.connect(self._changed)
        self._changed(value, emit=False)

    def _changed(self, v, emit=True):
        self.value.setText(f'{v}%')
        if emit:
            self._cb(v / 100.0)

    def set(self, f):
        self.slider.blockSignals(True)
        self.slider.setValue(int(round(f * 100)))
        self.slider.blockSignals(False)
        self.value.setText(f'{int(round(f * 100))}%')


class Timeline:
    """Steps run one after another, each a duration and an update(t in 0..1), at 60 frames a second."""

    def __init__(self, view, on_frame, on_done=None):
        self.view = view
        self.on_frame = on_frame
        self.on_done = on_done
        self.steps = []
        self.timer = QTimer()
        self.timer.timeout.connect(self._tick)
        self.index = 0
        self.start = 0.0

    def run(self, steps):
        self.steps = [s for s in steps if s]
        self.index = 0
        self.start = time.perf_counter()
        if self.steps:
            self.steps[0][2]() if len(self.steps[0]) > 2 else None
            self.timer.start(16)

    def stop(self):
        self.timer.stop()

    @property
    def running(self):
        return self.timer.isActive()

    def _tick(self):
        if self.index >= len(self.steps):
            self.timer.stop()
            if self.on_done:
                self.on_done()
            return
        dur, update = self.steps[self.index][0], self.steps[self.index][1]
        t = min(1.0, (time.perf_counter() - self.start) / max(1e-3, dur))
        e = t * t * (3 - 2 * t)
        update(e)
        self.on_frame()
        if t >= 1.0:
            self.index += 1
            self.start = time.perf_counter()
            if self.index < len(self.steps) and len(self.steps[self.index]) > 2:
                self.steps[self.index][2]()


class Workbench(QWidget):
    def __init__(self, win, repo):
        super().__init__()
        self.setObjectName('workbench')
        self.win = win
        self.repo = repo
        self.data = os.path.join(repo, 'reports', 'sandbox', 'data', 'weapons')
        self.poses_dir = os.path.join(repo, 'tools', 'sandbox', 'poses')
        self.captures = os.path.join(repo, 'reports', 'sandbox', 'captures')
        os.makedirs(self.poses_dir, exist_ok=True)
        os.makedirs(self.captures, exist_ok=True)
        self.scene = Scene(os.path.join(repo, 'src', 'Rig', 'Glove'))
        self.view = Viewport(self.scene)
        self.timeline = Timeline(self.view, self.view.update, self._timeline_done)
        self.picked_point = None
        self.picked_node = None
        self.recording = None
        self.process = None

        h = QHBoxLayout(self)
        h.setContentsMargins(0, 0, 0, 0)
        h.setSpacing(0)
        side = QWidget()
        side.setFixedWidth(360)
        sv = QVBoxLayout(side)
        sv.setContentsMargins(12, 10, 6, 10)
        self.pivot = Pivot()
        self.stack = QStackedWidget()
        sv.addWidget(self.pivot)
        sv.addWidget(self.stack, 1)
        self.easy = self._scroll(self._easy_panel())
        self.advanced = self._scroll(self._advanced_panel())
        self.stack.addWidget(self.easy)
        self.stack.addWidget(self.advanced)
        self.pivot.addItem('easy', 'Easy', lambda: self.stack.setCurrentWidget(self.easy))
        self.pivot.addItem('advanced', 'Advanced', lambda: self.stack.setCurrentWidget(self.advanced))
        self.pivot.setCurrentItem('easy')
        h.addWidget(side)
        viewbox = QWidget()
        vv = QVBoxLayout(viewbox)
        vv.setContentsMargins(4, 10, 10, 10)
        vv.addWidget(self.view, 1)
        h.addWidget(viewbox, 1)

        # The readout over the view (the laser's last point).
        self.readout = CardWidget(self.view)
        rv = QVBoxLayout(self.readout)
        rv.setContentsMargins(12, 8, 12, 8)
        self.readout_text = BodyLabel('')
        self.readout_text.setWordWrap(True)
        rv.addWidget(self.readout_text)
        copy = PushButton(FIF.COPY, 'Copy coordinates')
        copy.clicked.connect(self.copy_point)
        rv.addWidget(copy)
        self.readout.hide()
        self.hover = CaptionLabel('', self.view)
        self.hover.move(12, 8)

        self.view.picked.connect(self.on_pick)
        self.view.selected.connect(self.on_select)
        self.view.changed.connect(self.on_hand_moved)
        self.view.context.connect(self.on_context)
        self.view.hovered.connect(lambda s: (self.hover.setText(s), self.hover.adjustSize()))
        self.load_weapon_list()
        self._shortcuts()

    # ---- panels ----------------------------------------------------------------------------------------------------

    @staticmethod
    def _scroll(widget):
        s = SmoothScrollArea()
        s.setWidget(widget)
        s.setWidgetResizable(True)
        s.setFrameShape(QFrame.Shape.NoFrame)
        s.setStyleSheet('QScrollArea{background:transparent;border:none}')
        widget.setStyleSheet('QWidget#panel{background:transparent}')
        return s

    def _easy_panel(self):
        p = QWidget()
        p.setObjectName('panel')
        v = QVBoxLayout(p)
        v.setContentsMargins(0, 8, 8, 8)
        v.setSpacing(10)

        self.search = SearchLineEdit()
        self.search.setPlaceholderText('Search weapons')
        self.search.textChanged.connect(self.filter_weapons)
        self.weapon_list = ListWidget()
        self.weapon_list.setFixedHeight(190)
        self.weapon_list.currentItemChanged.connect(self.on_weapon_chosen)
        self.weapon_info = CaptionLabel('')
        self.weapon_info.setWordWrap(True)
        v.addWidget(card('Weapon', self.search, self.weapon_list, self.weapon_info))

        on_gun = PrimaryPushButton(FIF.FINGERPRINT, 'Hands on the gun')
        on_gun.clicked.connect(self.hands_on_gun)
        self.sel_right = TogglePushButton('Right hand')
        self.sel_left = TogglePushButton('Left hand')
        self.sel_right.clicked.connect(lambda: self.select_hand(self.scene.right))
        self.sel_left.clicked.connect(lambda: self.select_hand(self.scene.left))
        snaps = []
        for text, target in (('Handguard', 'support'), ('Slide', 'rack'), ('Magazine', 'magazine'), ('Cover', 'cover')):
            b = PushButton(text)
            b.clicked.connect(lambda _, t=target: self.snap_left(t))
            snaps.append(b)
        self.pose = ComboBox()
        self.pose.addItems(list(POSES.keys()))
        self.pose.currentTextChanged.connect(self.on_pose_chosen)
        self.finger_sliders = []
        fingers = QVBoxLayout()
        fingers.setSpacing(2)
        for i, name in enumerate(FINGERS):
            s = LabeledSlider(name, lambda f, i=i: self.on_finger(i, f))
            self.finger_sliders.append(s)
            fingers.addWidget(s)
        self.thumb_base = LabeledSlider('Thumb base', self.on_thumb_base)
        fingers.addWidget(self.thumb_base)
        hint = CaptionLabel('Drag a hand to move it, Shift + drag to turn it. Drag elsewhere to look around, right-drag to pan, wheel to zoom.')
        hint.setWordWrap(True)
        self.hand_label = BodyLabel('Pose of the right hand')
        v.addWidget(card('Hands', on_gun, row(self.sel_right, self.sel_left), CaptionLabel('Left hand to:'), row(*snaps),
                         self.hand_label, self.pose, fingers, hint))

        play = PrimaryPushButton(FIF.PLAY, 'Reload')
        play.clicked.connect(self.play_reload)
        rack = PushButton(FIF.PLAY, 'Rack')
        rack.clicked.connect(self.play_rack)
        reset = PushButton(FIF.SYNC, 'Reset')
        reset.clicked.connect(self.reset_parts)
        self.s_slide = LabeledSlider('Slide back', lambda f: self.set_part(slide=f), width=110)
        self.s_mag = LabeledSlider('Magazine out', lambda f: self.set_part(mag=f), width=110)
        self.s_cover = LabeledSlider('Cover open', lambda f: self.set_part(cover=f), width=110)
        v.addWidget(card('Reload', row(play, rack, reset), self.s_slide, self.s_mag, self.s_cover))

        self.laser_btn = TogglePushButton(FIF.ALIGNMENT, 'Laser pointer  (L)')
        self.laser_btn.toggled.connect(self.set_laser)
        laser_hint = CaptionLabel('Click a part with the laser (or Alt + click) to see what it is and where; right-click to copy it.')
        laser_hint.setWordWrap(True)
        save = PrimaryPushButton(FIF.SAVE, 'Save pose')
        save.clicked.connect(self.save_pose)
        shot = PushButton(FIF.CAMERA, 'Screenshot')
        shot.clicked.connect(self.screenshot)
        self.rec_btn = TogglePushButton(FIF.VIDEO, 'Record GIF')
        self.rec_btn.toggled.connect(self.toggle_record)
        v.addWidget(card('Point, save, capture', self.laser_btn, laser_hint, save, row(shot, self.rec_btn)))
        v.addStretch(1)
        return p

    def _advanced_panel(self):
        p = QWidget()
        p.setObjectName('panel')
        v = QVBoxLayout(p)
        v.setContentsMargins(0, 8, 8, 8)
        v.setSpacing(10)

        self.sel_name = BodyLabel('Selected: right hand')
        self.num = []
        grid = QVBoxLayout()
        for label, unit in (('Position in the gun (cm): forward, up, right', 'cm'), ('Turn (degrees): pitch, yaw, roll', 'deg')):
            grid.addWidget(CaptionLabel(label))
            boxes = []
            for _ in range(3):
                b = DoubleSpinBox()
                b.setRange(-500, 500)
                b.setDecimals(1)
                b.setSingleStep(0.5 if unit == 'cm' else 5)
                b.valueChanged.connect(self.on_numbers)
                boxes.append(b)
            self.num.append(boxes)
            grid.addLayout(row(*boxes))
        mirror = PushButton('Copy this pose to the other hand')
        mirror.clicked.connect(self.copy_pose_to_other)
        v.addWidget(card('Selected hand', self.sel_name, grid, mirror))

        self.parts = ListWidget()
        self.parts.setFixedHeight(200)
        self.parts.currentItemChanged.connect(self.on_part_chosen)
        show_all = PushButton('Show every part')
        show_all.clicked.connect(self.show_all_parts)
        v.addWidget(card('Parts the mod found', self.parts, show_all))

        self.rig_text = PlainTextEdit()
        self.rig_text.setReadOnly(True)
        self.rig_text.setFixedHeight(220)
        v.addWidget(card('Analysis (rig.json)', self.rig_text))

        views = []
        for text, name in (('Side', 'side'), ('Top', 'top'), ('Front', 'front'), ('3/4', 'three')):
            b = PushButton(text)
            b.clicked.connect(lambda _, n=name: self.view.set_view(n))
            views.append(b)
        self.grid_sw = SwitchButton()
        self.grid_sw.setChecked(True)
        self.grid_sw.checkedChanged.connect(lambda c: (setattr(self.view, 'show_grid', c), self.view.update()))
        self.mark_sw = SwitchButton()
        self.mark_sw.setChecked(True)
        self.mark_sw.checkedChanged.connect(lambda c: (setattr(self.view, 'show_markers', c), self.view.update()))
        self.gif_fps = DoubleSpinBox()
        self.gif_fps.setRange(5, 30)
        self.gif_fps.setValue(15)
        self.gif_fps.setDecimals(0)
        self.gif_scale = DoubleSpinBox()
        self.gif_scale.setRange(0.2, 1.0)
        self.gif_scale.setSingleStep(0.1)
        self.gif_scale.setValue(0.6)
        self.settle_sw = SwitchButton()
        self.settle_sw.setChecked(True)
        self.settle_sw.checkedChanged.connect(lambda c: setattr(self.scene, 'settle_on', c))
        markers_hint = CaptionLabel('Markers: green grip, blue support, orange rack point and its travel, red muzzle and aim, '
                                    'yellow magazine seat and its line, purple cover latch.')
        markers_hint.setWordWrap(True)
        settle_hint = CaptionLabel("Settle: after a snap the glove is put onto the gun's surface, as it settles in VR (up to 2 cm).")
        settle_hint.setWordWrap(True)
        v.addWidget(card('View', row(*views), row(BodyLabel('Grid'), self.grid_sw, BodyLabel('Markers'), self.mark_sw),
                         row(BodyLabel('Settle hands on the gun'), self.settle_sw), settle_hint,
                         markers_hint, row(BodyLabel('GIF fps'), self.gif_fps, BodyLabel('size'), self.gif_scale)))

        self.export_names = LineEdit()
        self.export_names.setPlaceholderText('Weapons to export (empty: all)')
        export = PushButton(FIF.DOWNLOAD, 'Export weapons')
        export.clicked.connect(self.export_weapons)
        game = PushButton(FIF.GAME, 'Test this gun in the game')
        game.clicked.connect(self.test_in_game)
        self.job_log = PlainTextEdit()
        self.job_log.setReadOnly(True)
        self.job_log.setFixedHeight(160)
        v.addWidget(card('Game (only when really needed)', self.export_names, row(export, game), self.job_log))
        v.addStretch(1)
        return p

    def _shortcuts(self):
        for key, fn in (('L', lambda: self.laser_btn.toggle()), ('F', self.frame_weapon), ('Escape', lambda: self.select_hand(None)),
                        ('1', lambda: self.select_hand(self.scene.right)), ('2', lambda: self.select_hand(self.scene.left)),
                        ('Space', self.play_reload), ('Ctrl+S', self.save_pose), ('F12', self.screenshot)):
            QShortcut(QKeySequence(key), self, activated=fn)

    # ---- weapons -----------------------------------------------------------------------------------------------------

    def load_weapon_list(self):
        self.weapon_list.clear()
        self.items = []
        if os.path.isdir(self.data):
            for name in sorted(os.listdir(self.data), key=str.lower):
                rig = os.path.join(self.data, name, 'rig.json')
                if os.path.exists(rig) and os.path.exists(os.path.join(self.data, name, 'model.glb')):
                    try:
                        key = json.load(open(rig, encoding='utf-8')).get('key', name)
                    except (OSError, ValueError):
                        key = name
                    pose = os.path.exists(os.path.join(self.poses_dir, name + '.json'))
                    it = QListWidgetItem(key.split('#')[0] + ('   ✓ pose' if pose else ''))
                    it.setData(Qt.ItemDataRole.UserRole, name)
                    self.weapon_list.addItem(it)
                    self.items.append(it)
        if not self.items:
            self.weapon_info.setText('No weapons exported yet. Advanced → "Export weapons" runs the game once (no headset) '
                                     'and writes them; or run python tools/sandbox/export.py.')
        else:
            self.weapon_info.setText(f'{len(self.items)} weapon(s).')
            self.weapon_list.setCurrentRow(0)

    def filter_weapons(self, text):
        t = text.lower().strip()
        for it in self.items:
            it.setHidden(bool(t) and t not in it.text().lower())

    def on_weapon_chosen(self, item, _prev=None):
        if item is None:
            return
        self.open_weapon(item.data(Qt.ItemDataRole.UserRole))

    def open_weapon(self, wid):
        self.timeline.stop()
        try:
            self.scene.load_weapon(os.path.join(self.data, wid))
        except Exception as e:  # a broken export
            self.toast(f'Could not open {wid}', str(e), error=True)
            return
        w = self.scene.weapon
        self.view.pick_point = None
        self.view.pick_line = None
        self.readout.hide()
        self.view.rebuild()
        r = w.rig
        info = f"{r.get('action', '')}{' · pistol' if r.get('isPistol') else ''} · {len(w.nodes)} parts"
        if w.stand_ins:
            info += f' · {w.stand_ins} shown as grey boxes (their mesh could not be read from the game)'
        self.weapon_info.setText(info)
        self.rig_text.setPlainText(json.dumps(r, indent=1))
        self.fill_poses()
        self.parts.clear()
        for n in sorted(w.nodes, key=lambda n: (n.role == 'body', n.role, n.short.lower())):
            it = QListWidgetItem(f'{n.short}  —  {ROLE_NAMES.get(n.role, n.role)}')
            it.setData(Qt.ItemDataRole.UserRole, id(n))
            self.parts.addItem(it)
        pose_file = os.path.join(self.poses_dir, wid + '.json')
        if os.path.exists(pose_file):
            try:
                self.scene.apply_pose_document(json.load(open(pose_file, encoding='utf-8')))
                self.toast('Saved pose loaded', pose_file)
            except (OSError, ValueError, KeyError) as e:
                self.toast('The saved pose could not be read', str(e), error=True)
                self.hands_on_gun()
        else:
            self.hands_on_gun()
        self.sync_sliders()
        self.frame_weapon()
        self.select_hand(self.scene.right)

    def frame_weapon(self):
        w = self.scene.weapon
        if w is None:
            return
        c, r = w.center_world()
        self.view.frame(c, max(0.12, r))

    # ---- hands ----------------------------------------------------------------------------------------------------------

    def hands_on_gun(self):
        if self.scene.weapon is None:
            return
        self.scene.snap(self.scene.right, 'grip')
        self.scene.snap(self.scene.left, 'support')
        self.after_hand_change()

    def snap_left(self, target):
        if self.scene.weapon is None:
            return
        if target == 'cover' and self.scene.weapon.rig.get('hingeKind', 'None') == 'None':
            self.toast('This weapon has no cover or barrels that open', '')
            return
        self.scene.snap(self.scene.left, target)
        self.select_hand(self.scene.left)
        self.after_hand_change()

    def select_hand(self, hand):
        self.view.selected_hand = hand
        self.on_select(hand)
        self.view.update()

    def on_select(self, hand):
        self.sel_right.setChecked(hand is self.scene.right)
        self.sel_left.setChecked(hand is self.scene.left)
        h = hand or self.scene.right
        self.hand_label.setText(f"Pose of the {h.side} hand")
        self.sel_name.setText(f"Selected: {h.side} hand" + (f" (on the {h.on_part})" if h.on_part else ''))
        self.pose.blockSignals(True)
        if self.pose.findText(h.pose_name) >= 0:
            self.pose.setCurrentText(h.pose_name)
        self.pose.blockSignals(False)
        self.sync_hand_controls()

    def current_hand(self):
        return self.view.selected_hand or self.scene.right

    def fill_poses(self):
        """The pose list: this weapon's own grip and support poses (from its animation) first, then the mod's library."""
        w = self.scene.weapon
        self.pose.blockSignals(True)
        self.pose.clear()
        names = []
        if w is not None:
            if w.rig.get('gripCurls'):
                names.append("weapon's own grip")
            if w.rig.get('supportCurls'):
                names.append("weapon's own support")
        names += list(POSES.keys())
        self.pose.addItems(names)
        self.pose.blockSignals(False)

    def on_pose_chosen(self, name):
        h = self.current_hand()
        w = self.scene.weapon
        if w is not None and name in ("weapon's own grip", "weapon's own support"):
            curls = w.rig.get('gripCurls' if 'grip' in name else 'supportCurls')
            if curls:
                h.set_pose(name, [0.35] + list(curls))
                self.sync_hand_controls()
                self.view.update()
            return
        h.set_pose(name)
        self.sync_hand_controls()
        self.view.update()

    def on_finger(self, i, f):
        h = self.current_hand()
        h.curls[i] = f
        h.pose_name = 'Custom'
        h.update_mesh()
        self.view.update()

    def on_thumb_base(self, f):
        h = self.current_hand()
        h.thumb = f
        h.pose_name = 'Custom'
        h.update_mesh()
        self.view.update()

    def sync_hand_controls(self):
        h = self.current_hand()
        for s, c in zip(self.finger_sliders, h.curls):
            s.set(c)
        self.thumb_base.set(h.thumb)
        self.sync_numbers()

    def sync_numbers(self):
        w = self.scene.weapon
        h = self.current_hand()
        if w is None:
            return
        g = w.to_gun(h.position)[:3]
        local = np.array([np.dot(g - w.grip, w.aim), np.dot(g - w.grip, w.up), np.dot(g - w.grip, w.right)]) * 100
        q = quat_mul(quat_inv(w.Wq), h.rotation)
        e = quat_to_euler_deg(q)
        for box, val in zip(self.num[0] + self.num[1], list(local) + list(e)):
            box.blockSignals(True)
            box.setValue(float(val))
            box.blockSignals(False)

    def on_numbers(self):
        w = self.scene.weapon
        h = self.current_hand()
        if w is None:
            return
        f, u, r = (b.value() / 100 for b in self.num[0])
        g = w.grip + w.aim * f + w.up * u + w.right * r
        h.position = w.to_world(g)
        h.rotation = quat_norm(quat_mul(w.Wq, euler_deg_to_quat([b.value() for b in self.num[1]])))
        self.view.update()

    def on_hand_moved(self):
        self.current_hand().on_part = ''
        self.sync_numbers()

    def after_hand_change(self):
        self.on_select(self.current_hand())
        self.view.update()

    def copy_pose_to_other(self):
        h = self.current_hand()
        o = self.scene.left if h is self.scene.right else self.scene.right
        o.thumb, o.curls, o.pose_name = h.thumb, list(h.curls), h.pose_name
        o.update_mesh()
        self.view.update()

    # ---- parts and the reload ---------------------------------------------------------------------------------------------

    def set_part(self, slide=None, mag=None, cover=None):
        w = self.scene.weapon
        if w is None:
            return
        w.set_state(slide, mag, cover)
        self.view.update()

    def sync_sliders(self):
        w = self.scene.weapon
        if w is None:
            return
        self.s_slide.set(w.slide)
        self.s_mag.set(w.mag)
        self.s_cover.set(w.cover)

    def reset_parts(self):
        self.timeline.stop()
        w = self.scene.weapon
        if w is None:
            return
        w.fresh = None
        for n in w.nodes:
            n.hidden = False
        w.set_state(0, 0, 0)
        self.sync_sliders()
        self.hands_on_gun()

    def _move_hand(self, hand, target):
        """A step taking the hand from where it is to where target() (a snap) would put it."""
        start = {}

        def begin():
            start['p'], start['q'] = hand.position.copy(), hand.rotation.copy()
            tmp_p, tmp_q, tmp_pose = hand.position.copy(), hand.rotation.copy(), (hand.pose_name, hand.thumb, list(hand.curls))
            target()
            start['tp'], start['tq'], start['pose'] = hand.position.copy(), hand.rotation.copy(), (hand.pose_name, hand.thumb, list(hand.curls))
            hand.position, hand.rotation = tmp_p, tmp_q
            hand.pose_name, hand.thumb, hand.curls = tmp_pose[0], tmp_pose[1], tmp_pose[2]

        def update(t):
            hand.position = start['p'] + (start['tp'] - start['p']) * t
            hand.rotation = quat_slerp(start['q'], start['tq'], t)
            if t >= 1:
                hand.pose_name, hand.thumb, hand.curls = start['pose'][0], start['pose'][1], list(start['pose'][2])
                hand.update_mesh()
        return (0.4, update, begin)

    def _follow(self, hand, target, dur, part_update):
        """A step moving a part (part_update(t)) with the hand kept on it (target() each frame)."""
        def update(t):
            part_update(t)
            target()
        return (dur, update)

    def play_reload(self):
        w = self.scene.weapon
        if w is None or self.timeline.running:
            return
        s, left = self.scene, self.scene.left
        r = w.rig
        has_mag = bool(r.get('magazine'))
        cover = r.get('hingeKind') == 'FeedCover'
        steps = []
        if cover:
            steps.append(self._move_hand(left, lambda: s.snap(left, 'cover')))
            steps.append(self._follow(left, lambda: s.snap(left, 'cover') if False else None, 0.5, lambda t: w.set_state(cover=t)))
        if has_mag:
            steps.append(self._move_hand(left, lambda: s.snap(left, 'magazine')))
            steps.append(self._follow(left, lambda: s.snap(left, 'magazine'), 0.55, lambda t: w.set_state(mag=t)))

            def hide_old():
                for n in w.nodes:
                    if n.role == 'magazine':
                        n.hidden = True
            steps.append((0.3, lambda t: setattr(left, 'position', left.position + np.array([0, -0.004, 0])), hide_old))
            ej = w.mag_eject() * w.mag_length

            def fresh_at(t):
                m = np.eye(4)
                m[:3, 3] = ej * (1.0 - t)
                w.fresh = m
                w.mag = 1.0 - t
            steps.append((0.6, lambda t: (fresh_at(t), s.snap(left, 'magazine')), lambda: fresh_at(0)))

            def seated():
                w.fresh = None
                for n in w.nodes:
                    n.hidden = False
                w.set_state(mag=0)
            steps.append((0.05, lambda t: None, seated))
        if cover:
            steps.append(self._follow(left, lambda: None, 0.45, lambda t: w.set_state(cover=1 - t)))
        if r.get('rackPoint') is not None and (r.get('slide') or r.get('addedHandle') or r.get('carveHandle')):
            steps.append(self._move_hand(left, lambda: s.snap(left, 'rack')))
            steps.append(self._follow(left, lambda: s.snap(left, 'rack'), 0.35, lambda t: w.set_state(slide=t)))
            steps.append((0.18, lambda t: w.set_state(slide=1 - t)))
        steps.append(self._move_hand(left, lambda: s.snap(left, 'support')))
        self.timeline.run(steps)

    def play_rack(self):
        w = self.scene.weapon
        if w is None or self.timeline.running:
            return
        s, left = self.scene, self.scene.left
        self.timeline.run([self._move_hand(left, lambda: s.snap(left, 'rack')),
                           self._follow(left, lambda: s.snap(left, 'rack'), 0.4, lambda t: w.set_state(slide=t)),
                           (0.2, lambda t: w.set_state(slide=1 - t)),
                           self._move_hand(left, lambda: s.snap(left, 'support'))])

    def _timeline_done(self):
        self.sync_sliders()
        self.on_select(self.current_hand())

    def on_part_chosen(self, item, _prev=None):
        w = self.scene.weapon
        if w is None or item is None:
            return
        nid = item.data(Qt.ItemDataRole.UserRole)
        node = next((n for n in w.nodes if id(n) == nid), None)
        self.view.hover_node = node
        self.view.update()

    def show_all_parts(self):
        if self.scene.weapon:
            for n in self.scene.weapon.nodes:
                n.hidden = False
            self.view.update()

    # ---- the laser ---------------------------------------------------------------------------------------------------------

    def set_laser(self, on):
        self.view.laser = on
        self.scene.laser_hand = self.scene.right if on else None
        if not on:
            self.view.hover_node = None
        self.view.update()

    def on_pick(self, point, node):
        self.picked_point, self.picked_node = point, node
        if point is None:
            self.readout.hide()
            return
        self.readout_text.setText(self.point_text(rich=True))
        self.readout.adjustSize()
        self.readout.setFixedWidth(min(560, max(320, self.view.width() - 24)))
        self.readout.adjustSize()
        self.readout.move(12, self.view.height() - self.readout.height() - 12)
        self.readout.show()
        self.readout.raise_()

    def point_text(self, rich=False):
        w = self.scene.weapon
        n = self.picked_node
        g = w.to_gun(self.picked_point)[:3]
        b, u, r = w.from_muzzle(g)
        x, y, z = Weapon.unity(g)
        role = ROLE_NAMES.get(n.role, n.role)
        name = n.name[len('[stand-in] '):] if n.stand_in else n.name
        if rich:
            return (f"<b>{n.short}</b> — {role}{' (grey stand-in box)' if n.stand_in else ''}<br>"
                    f"back <b>{b:.1f}</b> · up <b>{u:.1f}</b> · right <b>{r:.1f}</b> cm from the muzzle<br>"
                    f"<span style='color:gray'>gun space x {x:.4f} y {y:.4f} z {z:.4f}</span>")
        return (f"{w.name} ({w.id}): '{name}' ({role}) at back {b:.1f} up {u:.1f} right {r:.1f} cm from the muzzle "
                f"(mod gun space x {x:.4f} y {y:.4f} z {z:.4f})")

    def copy_point(self):
        if self.picked_point is None:
            return
        QGuiApplication.clipboard().setText(self.point_text())
        self.toast('Copied', 'The part and its coordinates are on the clipboard: paste them to Claude.')

    def on_context(self, pos):
        menu = RoundMenu(parent=self)
        if self.picked_point is not None:
            menu.addAction(Action(FIF.COPY, 'Copy coordinates', triggered=self.copy_point))
            menu.addAction(Action(FIF.TAG, 'Copy the part name',
                                  triggered=lambda: QGuiApplication.clipboard().setText(self.picked_node.name)))
            menu.addAction(Action(FIF.HIDE, 'Hide this part', triggered=self.hide_picked))
            menu.addSeparator()
        menu.addAction(Action(FIF.VIEW, 'Show every part', triggered=self.show_all_parts))
        menu.addAction(Action(FIF.FINGERPRINT, 'Hands on the gun', triggered=self.hands_on_gun))
        menu.addAction(Action(FIF.ZOOM, 'Frame the gun  (F)', triggered=self.frame_weapon))
        menu.exec(pos)

    def test_pick(self):
        """Self-check: the laser on the slide's (or the first part's) middle, as a click there would."""
        w = self.scene.weapon
        if w is None:
            return
        node = next((n for n in w.nodes if n.role in ('slide', 'handle')), w.nodes[0])
        self.view.laser = True
        self.scene.laser_hand = self.scene.right
        point = w.to_world((node.bmin + node.bmax) * 0.5 + node.sim[:3, 3])
        self.view.pick_point = point
        self.view.pick_line = [self.scene.laser_origin(), point]
        self.on_pick(point, node)

    def hide_picked(self):
        if self.picked_node is not None:
            self.picked_node.hidden = True
            self.view.update()

    # ---- saving ------------------------------------------------------------------------------------------------------------

    def save_pose(self):
        w = self.scene.weapon
        if w is None:
            return
        path = os.path.join(self.poses_dir, w.id + '.json')  # the export's folder name: the mod looks for it by that name
        doc = self.scene.pose_document()
        with open(path, 'w', encoding='utf-8') as f:
            json.dump(doc, f, indent=1)
        # And where the mod reads it (BepInEx/config/RavenfieldVR/poses), when the game is where Steam puts it.
        game = os.path.join(os.environ.get('ProgramFiles(x86)', r'C:\Program Files (x86)'), 'Steam', 'steamapps', 'common', 'Ravenfield', 'BepInEx', 'config')
        text = path
        if os.path.isdir(game):
            target = os.path.join(game, 'RavenfieldVR', 'poses')
            os.makedirs(target, exist_ok=True)
            with open(os.path.join(target, w.id + '.json'), 'w', encoding='utf-8') as f:
                json.dump(doc, f, indent=1)
            text += '\nand for the game: ' + os.path.join(target, w.id + '.json')
        for it in self.items:
            if it.data(Qt.ItemDataRole.UserRole) == w.id and '✓ pose' not in it.text():
                it.setText(it.text() + '   ✓ pose')
        self.toast('Pose saved', text, path)

    def screenshot(self):
        w = self.scene.weapon
        img = self.view.grabFramebuffer()
        path = os.path.join(self.captures, f"{safe(w.name if w else 'sandbox')}-{time.strftime('%Y%m%d-%H%M%S')}.png")
        img.save(path)
        self.toast('Screenshot saved', path, path)
        return path

    def toggle_record(self, on):
        if on:
            self.recording = {'frames': [], 'timer': QTimer(), 'scale': float(self.gif_scale.value()), 'fps': int(self.gif_fps.value())}
            self.recording['timer'].timeout.connect(self._grab_frame)
            self.recording['timer'].start(int(1000 / self.recording['fps']))
            self.rec_btn.setText('Stop recording')
        else:
            rec, self.recording = self.recording, None
            self.rec_btn.setText('Record GIF')
            if not rec:
                return
            rec['timer'].stop()
            if not rec['frames']:
                return
            w = self.scene.weapon
            path = os.path.join(self.captures, f"{safe(w.name if w else 'sandbox')}-{time.strftime('%Y%m%d-%H%M%S')}.gif")
            frames = [f.quantize(colors=256, method=Image.Quantize.MEDIANCUT, dither=Image.Dither.NONE) for f in rec['frames']]
            frames[0].save(path, save_all=True, append_images=frames[1:], duration=int(1000 / rec['fps']), loop=0, disposal=2)
            self.toast(f'GIF saved ({len(frames)} frames)', path, path)

    def _grab_frame(self):
        rec = self.recording
        if rec is None or len(rec['frames']) >= 600:
            return
        img = self.view.grabFramebuffer()
        if rec['scale'] < 0.999:
            img = img.scaledToWidth(max(64, int(img.width() * rec['scale'])), Qt.TransformationMode.SmoothTransformation)
        img = img.convertToFormat(img.Format.Format_RGB888)
        ptr = img.constBits()
        ptr.setsize(img.sizeInBytes())
        arr = np.frombuffer(ptr, np.uint8).reshape(img.height(), img.bytesPerLine())[:, :img.width() * 3].reshape(img.height(), img.width(), 3)
        rec['frames'].append(Image.fromarray(arr.copy()))
        self.rec_btn.setText(f"Stop recording ({len(rec['frames'])})")

    def toast(self, title, content, path=None, error=False):
        bar = (InfoBar.error if error else InfoBar.success)(title, content, orient=Qt.Orientation.Vertical, isClosable=True,
                                                            position=InfoBarPosition.BOTTOM_RIGHT, duration=8000, parent=self.win)
        if path:
            b = PushButton('Show in folder')
            b.clicked.connect(lambda: subprocess.Popen(['explorer', '/select,', os.path.normpath(path)]))
            bar.addWidget(b)

    # ---- the game (only when really needed) -----------------------------------------------------------------------------

    def _python(self):
        return sys.executable if not getattr(sys, 'frozen', False) else 'python'

    def _run(self, args, title):
        if self.process is not None and self.process.state() != QProcess.ProcessState.NotRunning:
            self.toast('Something is still running', 'Wait for it to finish.', error=True)
            return
        out = subprocess.run(['tasklist', '/FI', 'IMAGENAME eq ravenfield.exe'], capture_output=True, text=True, errors='ignore').stdout
        if 'ravenfield.exe' in out.lower():
            self.toast('Ravenfield is running', 'Not starting it again (someone is playing?).', error=True)
            return
        self.job_log.setPlainText(f'{title}…\n')
        self.process = QProcess(self)
        self.process.setWorkingDirectory(self.repo)
        self.process.setProcessChannelMode(QProcess.ProcessChannelMode.MergedChannels)
        self.process.readyReadStandardOutput.connect(
            lambda: self.job_log.appendPlainText(bytes(self.process.readAllStandardOutput()).decode('utf-8', 'ignore').rstrip()))
        self.process.finished.connect(lambda code, _s: self._job_done(title, code))
        env = self.process.processEnvironment()
        env.insert('PYTHONIOENCODING', 'utf-8')
        self.process.setProcessEnvironment(env)
        self.process.start(self._python(), args)

    def _job_done(self, title, code):
        self.job_log.appendPlainText(f'\n{title}: finished ({code}).')
        if 'Export' in title:
            self.load_weapon_list()
        self.toast(f'{title}: finished', 'Exit code ' + str(code), error=code != 0)

    def export_weapons(self):
        names = self.export_names.text().strip()
        self._run([os.path.join(self.repo, 'tools', 'sandbox', 'export.py')] + (['--weapons', names] if names else []), 'Export weapons')

    def test_in_game(self):
        w = self.scene.weapon
        if w is None:
            return
        self._run([os.path.join(self.repo, 'tools', 'vrshots.py'), '--weapon', '=' + w.name, '--out', 'sandbox-' + safe(w.name)],
                  f'Test {w.name} in the game')


class Captures(QWidget):
    def __init__(self, folder):
        super().__init__()
        self.setObjectName('captures')
        self.folder = folder
        v = QVBoxLayout(self)
        v.setContentsMargins(24, 18, 24, 18)
        v.addWidget(TitleLabel('Captures'))
        v.addWidget(CaptionLabel(folder))
        self.list = ListWidget()
        self.list.itemDoubleClicked.connect(self.open_item)
        v.addWidget(self.list, 1)
        open_folder = PushButton(FIF.FOLDER, 'Open the folder')
        open_folder.clicked.connect(lambda: QDesktopServices.openUrl(QUrl.fromLocalFile(folder)))
        refresh = PushButton(FIF.SYNC, 'Refresh')
        refresh.clicked.connect(self.refresh)
        v.addLayout(row(open_folder, refresh, QWidget(), stretch=[0, 0, 1]))
        self.refresh()

    def refresh(self):
        self.list.clear()
        if not os.path.isdir(self.folder):
            return
        files = sorted((f for f in os.listdir(self.folder) if f.lower().endswith(('.png', '.gif'))),
                       key=lambda f: os.path.getmtime(os.path.join(self.folder, f)), reverse=True)
        for f in files:
            it = QListWidgetItem(f)
            it.setData(Qt.ItemDataRole.UserRole, os.path.join(self.folder, f))
            self.list.addItem(it)

    def showEvent(self, e):
        self.refresh()
        super().showEvent(e)

    def open_item(self, item):
        QDesktopServices.openUrl(QUrl.fromLocalFile(item.data(Qt.ItemDataRole.UserRole)))


class Help(QWidget):
    def __init__(self):
        super().__init__()
        self.setObjectName('help')
        v = QVBoxLayout(self)
        v.setContentsMargins(24, 18, 24, 18)
        v.addWidget(TitleLabel('How to use it'))
        text = BodyLabel(
            '<p><b>Pick a weapon</b> on the left. The hands go on it the way the mod puts them: the right hand on the grip, '
            'the left on the handguard. A saved pose comes back when you open the weapon again.</p>'
            '<p><b>Move a hand</b>: drag it. <b>Turn it</b>: Shift + drag. Pick a <b>pose</b> from the list or drag the finger '
            'sliders. <b>Left hand to</b> puts the left hand on the handguard, the slide, the magazine or the cover.</p>'
            '<p><b>Reload</b> and <b>Rack</b> play the parts the mod moves (the slide by its travel, the magazine along its line, '
            'the cover on its hinge), with the left hand on them. The sliders do it by hand.</p>'
            '<p><b>Laser pointer</b> (L, or Alt + click): click a part to see what it is and where it is, in cm from the '
            'muzzle (back, up, right) as the test reports give it. <b>Right-click</b> copies it for Claude.</p>'
            '<p><b>Save pose</b> (Ctrl+S) writes this weapon\'s hands to tools/sandbox/poses/. <b>Screenshot</b> (F12) and '
            '<b>Record GIF</b> save into reports/sandbox/captures (the message shows where, with a button to open it).</p>'
            '<p>Grey boxes are parts whose mesh the game does not let the mod read: they are at the right place and size.</p>'
            '<p>Mouse: drag to look around, right-drag to pan, wheel to zoom. Keys: 1 / 2 select the right / left hand, Esc none, '
            'F frame the gun, Space reload.</p>'
            '<p><b>Advanced</b>: exact numbers for the selected hand, the parts the mod found, its analysis, views, GIF settings, '
            'and the game: export weapons (runs Ravenfield once without the headset) or test this gun in the game.</p>')
        text.setWordWrap(True)
        text.setTextFormat(Qt.TextFormat.RichText)
        v.addWidget(text)
        v.addStretch(1)


class Window(FluentWindow):
    def __init__(self, repo):
        super().__init__()
        self.setWindowTitle(APP_NAME)
        icon = os.path.join(HERE, 'icon.ico')
        if os.path.exists(icon):
            self.setWindowIcon(QIcon(icon))
        self.workbench = Workbench(self, repo)
        self.captures = Captures(self.workbench.captures)
        self.help = Help()
        self.addSubInterface(self.workbench, FIF.EDIT, 'Workbench')
        self.addSubInterface(self.captures, FIF.PHOTO, 'Captures')
        self.addSubInterface(self.help, FIF.HELP, 'Help', NavigationItemPosition.BOTTOM)
        self.navigationInterface.setExpandWidth(180)
        # A solid dark window (Mica lets the desktop show through: pictures of the window and some setups showed white panels).
        try:
            self.setMicaEffectEnabled(False)
        except AttributeError:
            pass
        # Fits the screen it opens on (1500 x 920 at 150% scaling was wider than a laptop screen), centred.
        avail = QGuiApplication.primaryScreen().availableGeometry()
        self.resize(min(1500, int(avail.width() * 0.94)), min(920, int(avail.height() * 0.92)))
        self.move(avail.x() + (avail.width() - self.width()) // 2, avail.y() + (avail.height() - self.height()) // 2)


def main():
    QApplication.setHighDpiScaleFactorRoundingPolicy(Qt.HighDpiScaleFactorRoundingPolicy.PassThrough)
    app = QApplication(sys.argv)
    app.setApplicationName(APP_NAME)
    setTheme(Theme.DARK)
    setThemeColor('#d79b45')
    repo = find_repo()
    if repo is None:
        from PyQt6.QtWidgets import QFileDialog
        repo = QFileDialog.getExistingDirectory(None, 'Where is the Ravenfield-VR folder?')
        if not repo:
            return
        QSettings('RavenfieldVR', 'Sandbox').setValue('repo', repo)
    win = Window(repo)
    args = sys.argv[1:]
    if '--shot' in args:
        out = args[args.index('--shot') + 1]
        want = args[args.index('--weapon') + 1].lower() if '--weapon' in args else None
        win.show()

        def shoot():
            wb = win.workbench
            if want:
                for it in wb.items:
                    if want in it.text().lower() or want in str(it.data(Qt.ItemDataRole.UserRole)).lower():
                        wb.weapon_list.setCurrentItem(it)
                        break
            for action in [a for a in args if a.startswith('--do=')]:
                getattr(wb, action[5:])()
            v = wb.view
            for a in args:
                if a.startswith('--cam='):  # --cam=yaw,pitch,dist[,hand]
                    parts = a[6:].split(',')
                    v.yaw, v.pitch, v.dist = float(parts[0]), float(parts[1]), float(parts[2])
                    if len(parts) > 3:
                        v.target = (wb.scene.right if parts[3] == 'right' else wb.scene.left).position.copy()
            whole = '--window' in args
            delay = int(next((a[8:] for a in args if a.startswith('--delay=')), '900'))
            QTimer.singleShot(delay, lambda: ((win.screen().grabWindow(int(win.winId())) if whole else v.grabFramebuffer()).save(out), app.quit()))
        QTimer.singleShot(900, shoot)
    else:
        win.show()
    app.exec()


if __name__ == '__main__':
    main()
