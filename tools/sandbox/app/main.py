"""Ravenfield VR Sandbox: put the mod's hands on the exported weapons, pose them, play reloads, point at parts, save
poses, screenshots and GIFs, without starting the game.

    python tools/sandbox/app/main.py              (or the built RavenfieldVR-Sandbox.exe)
    python tools/sandbox/app/main.py --shot out.png [--weapon NAME]   render one picture and quit (for checks)
"""
import codecs
import json
import math
import os
import re
import subprocess
import sys
import time

import numpy as np
from PIL import Image
from PyQt6.QtCore import QByteArray, QFile, QProcess, QSettings, QSize, Qt, QTimer, QUrl
from PyQt6.QtGui import QDesktopServices, QGuiApplication, QIcon, QImage, QKeySequence, QMovie, QPixmap, QShortcut
from PyQt6.QtWidgets import (QApplication, QFrame, QHBoxLayout, QLabel, QListView, QListWidgetItem, QSizePolicy, QStackedWidget, QVBoxLayout,
                             QWidget)
from qfluentwidgets import (Action, BodyLabel, CaptionLabel, CardWidget, CheckBox, ComboBox, DoubleSpinBox, FluentIcon as FIF, FluentWindow,
                            InfoBar, InfoBarPosition, LineEdit, MessageBox, ListWidget, NavigationItemPosition, Pivot, PlainTextEdit, PrimaryPushButton,
                            PushButton, RoundMenu, SearchLineEdit, SegmentedWidget, Slider, SmoothScrollArea, StrongBodyLabel, SubtitleLabel,
                            SwitchButton, Theme, TitleLabel, TogglePushButton, setTheme, setThemeColor)

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)

from glove import FINGERS, POSES, grip_thumb_base  # noqa: E402
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
    if '--selftest' in sys.argv:
        return None  # (the self-test never reads the user's settings)
    saved = QSettings('RavenfieldVR', 'Sandbox').value('repo', '')
    return saved if saved and os.path.exists(os.path.join(saved, 'src', 'Rig', 'Glove', 'glove.bin')) else None


def safe(name):
    return ''.join(c if c.isalnum() or c in '-_.' else '_' for c in name).strip('_') or 'capture'


def stamp():
    """A file name's time, to the millisecond (to the second, two screenshots in one second overwrote the first)."""
    t = time.time()
    return time.strftime('%Y%m%d-%H%M%S', time.localtime(t)) + f'-{int(t * 1000) % 1000:03d}'


def is_repo(folder):
    return bool(folder) and os.path.exists(os.path.join(folder, 'src', 'Rig', 'Glove', 'glove.bin'))


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
    def __init__(self, label, on_change, minimum=0, maximum=100, value=0, width=96, on_press=None):
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
        if on_press is not None:
            self.slider.sliderPressed.connect(on_press)
        self._changed(value, emit=False)

    def _changed(self, v, emit=True):
        self.value.setText(f'{v}%')
        if emit:
            self._cb(v / 100.0)

    def set(self, f):
        v = int(round(min(1.0, max(0.0, f)) * 100))
        self.slider.blockSignals(True)
        self.slider.setValue(v)
        self.slider.blockSignals(False)
        self.value.setText(f'{v}%')


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

    def finish(self):
        """Runs what is left to its end at once (the state the reload ends in), then stops."""
        if not self.timer.isActive():
            return
        self.timer.stop()
        for i in range(self.index, len(self.steps)):
            if i > self.index and len(self.steps[i]) > 2:
                self.steps[i][2]()
            self.steps[i][1](1.0)
        self.index = len(self.steps)
        self.on_frame()
        if self.on_done:
            self.on_done()

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
        self.last_gif = None  # the GIF the last stop saved (closeEvent shows its path: the toast would not be seen)
        self.process = None
        self.selftest = '--selftest' in sys.argv  # the self-test: nothing of the user's is read or written then
        self.game_dir_override = None  # the self-test's own game poses folder
        self.drafts = {}  # weapon id -> its hands as left when another weapon was opened (unsaved work is kept)
        self.saved_doc = None  # the open weapon's saved pose, as read (for Revert and for marking changes)
        self.settings = QSettings('RavenfieldVR', 'Sandbox')
        self.undo_stack, self.redo_stack = [], []
        self._numbers_undo_at = 0.0
        # hand side -> {'q': its turn (a quaternion, gun-relative), 'pos_box' / 'turn_box': the boxes as shown last (to see which
        # one the user changed), 'turn': the exact angles they were shown from (the boxes round to 0.1)}
        self._numbers_shown = {}
        # The hand boxes' frame: from the analysed grip point (the first), or from the muzzle (set_numbers_frame; real2 I2#0).
        self.numbers_frame = 'grip'
        if not self.selftest:
            saved_frame = str(self.settings.value('numbersFrame', 'grip') or 'grip')
            self.numbers_frame = saved_frame if saved_frame in ('grip', 'muzzle') else 'grip'

        h = QHBoxLayout(self)
        h.setContentsMargins(0, 0, 0, 0)
        h.setSpacing(0)
        side = QWidget()
        side.setFixedWidth(380)
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
        # The side panel as wide as its widest card needs (with its margins and the scroll bar): a fixed 360 cut the
        # Advanced tab's number boxes and texts off on the right.
        need = max(self.easy.widget().minimumSizeHint().width(), self.advanced.widget().minimumSizeHint().width())
        side.setFixedWidth(max(380, need + 12 + 6 + 16))
        self.pivot.addItem('easy', 'Easy', lambda: self.stack.setCurrentWidget(self.easy))
        self.pivot.addItem('advanced', 'Advanced', lambda: self.stack.setCurrentWidget(self.advanced))
        self.pivot.setCurrentItem('easy')
        h.addWidget(side)
        viewbox = QWidget()
        vv = QVBoxLayout(viewbox)
        vv.setContentsMargins(4, 10, 10, 10)
        vv.setSpacing(6)
        vv.addWidget(self._gizmo_bar())
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
        self.view.drag_started.connect(self._drag_starting)
        self.view.context.connect(self.on_context)
        self.view.hovered.connect(lambda s: (self.hover.setText(s), self.hover.adjustSize()))
        self.view.drag_finished.connect(self._moved)  # a hand's job is worked out when its drag ends, not on every move
        self.view.weapon_moved.connect(self.sync_numbers)  # the gun moved: the numbers of the hand shown follow
        self.view.weapon_moved.connect(self._pick_follows)
        self.view.drag_cancelled.connect(self._drag_cancelled)
        self.view.gizmo_info.connect(self._gizmo_info)
        self.view.gizmo_target_changed.connect(lambda t: self.gizmo_on.setCurrentItem(t))
        # Weapons chosen in quick succession (the arrow keys held down) open once the keys pause: see on_weapon_chosen.
        self._open_timer = QTimer(self)
        self._open_timer.setSingleShot(True)
        self._open_timer.setInterval(120)
        self._open_timer.timeout.connect(self._open_pending)
        self._pending_wid = None
        self._opened_at = 0.0  # when the last weapon opened (monotonic seconds)
        # The window is shown before the first weapon is parsed: the list fills on the next turn of the event loop.
        self.items = []
        QTimer.singleShot(0, self.load_weapon_list)
        self._shortcuts()

    # ---- panels ----------------------------------------------------------------------------------------------------

    def _gizmo_bar(self):
        """The view's header, as Blender's: the transform gizmo (Free, Move, Turn, or both), its axes, snapping, and what it
        is on (the selected hand, or the gun)."""
        bar = QWidget()
        h = QHBoxLayout(bar)
        h.setContentsMargins(0, 0, 0, 0)
        h.setSpacing(8)
        self.gizmo_seg = SegmentedWidget()
        for key, text, tip in (
                ('free', 'Free', 'No gizmo (W). Drag a hand to move it, Shift + drag to turn it: that works with every gizmo.'),
                ('move', 'Move', 'Arrows to move along an axis, squares to move in a plane, the white circle to move in the '
                                 "view's plane (G). Ctrl snaps to 1 cm, Shift moves finely."),
                ('rotate', 'Turn', 'Rings to turn about an axis, the white ring to turn about the view, inside the rings to '
                                   'turn freely (R). Ctrl snaps to 5 degrees, Shift turns finely.'),
                ('both', 'Move + turn', 'Arrows and rings together (T).')):
            # (The item's click signal passes its checked state: taken as the mode, a click on Turn set the mode to
            # False and every gizmo was gone until a restart; user, 2026-10-18. The key is bound, the signal's value dropped.)
            self.gizmo_seg.addItem(key, text, lambda *_, k=key: self.set_gizmo_mode(k))
            self.gizmo_seg.widget(key).setToolTip(tip)
        self.axes_box = ComboBox()
        self.axes_box.addItems(['Global', 'Local', 'Gun'])
        self.axes_box.setToolTip("The gizmo's axes (comma cycles them): Global, the world's (red X, green Y, blue Z); Local, "
                                 "the hand's own (on the gun: the gun's); Gun, the gun's: X along the barrel, Y up, Z its right side.")
        self.axes_box.currentIndexChanged.connect(lambda i: self.set_gizmo_space(('global', 'local', 'gun')[i]))
        self.snap_btn = TogglePushButton('Snap')
        self.snap_btn.setToolTip('Snap gizmo moves to 1 cm and turns to 5 degrees (Shift: 1 mm, 1 degree). Without it, Ctrl '
                                 'snaps while held; with it, Ctrl moves freely.')
        self.snap_btn.toggled.connect(self.set_gizmo_snap)
        self.gizmo_on = SegmentedWidget()
        self.gizmo_on.addItem('hand', 'Hand', lambda *_: self.view.set_gizmo_target('hand'))
        self.gizmo_on.addItem('gun', 'Gun', lambda *_: self.view.set_gizmo_target('gun'))
        self.gizmo_on.widget('hand').setToolTip('The gizmo on the selected hand (a click on a hand, or 1 / 2)')
        self.gizmo_on.widget('gun').setToolTip('The gizmo on the gun, at its grip (or Ctrl + click on the gun). The hands on it go '
                                               'along; poses are kept in the gun\'s own space, so this only changes the view.')
        self.gizmo_on.setCurrentItem('hand')
        h.addWidget(self.gizmo_seg)
        h.addWidget(CaptionLabel('Axes'))
        h.addWidget(self.axes_box)
        h.addWidget(self.snap_btn)
        h.addWidget(CaptionLabel('Gizmo on'))
        h.addWidget(self.gizmo_on)
        h.addStretch(1)
        # Remembered between runs (not in the self-test, which never reads or writes the user's settings).
        mode, space, snap = 'move', 'global', False
        if not self.selftest:
            mode = str(self.settings.value('gizmoMode', mode) or mode)
            space = str(self.settings.value('gizmoSpace', space) or space)
            snap = str(self.settings.value('gizmoSnap', 'false')).lower() == 'true'
        self.set_gizmo_mode(mode if mode in ('free', 'move', 'rotate', 'both') else 'move')
        self.set_gizmo_space(space if space in ('global', 'local', 'gun') else 'global')
        self.snap_btn.setChecked(snap)
        return bar

    def set_gizmo_mode(self, mode):
        if mode not in ('free', 'move', 'rotate', 'both'):
            return  # (only a real mode: anything else left no gizmo at all)
        v = self.view
        v.gizmo_mode = mode
        v.gizmo_hover = None
        if self.gizmo_seg.currentRouteKey() != mode:
            self.gizmo_seg.setCurrentItem(mode)
        if not self.selftest:
            self.settings.setValue('gizmoMode', mode)
        v.refresh_cursor()  # (other handles under the pointer now)
        v.update()

    def set_gizmo_space(self, space):
        if space not in ('global', 'local', 'gun'):
            return
        self.view.gizmo_space = space
        i = ('global', 'local', 'gun').index(space)
        if self.axes_box.currentIndex() != i:
            self.axes_box.setCurrentIndex(i)
        if not self.selftest:
            self.settings.setValue('gizmoSpace', space)
        self.view.update()

    def cycle_gizmo_space(self):
        order = ('global', 'local', 'gun')
        self.set_gizmo_space(order[(order.index(self.view.gizmo_space) + 1) % 3])

    def set_gizmo_snap(self, on):
        self.view.gizmo_snap = bool(on)
        if self.snap_btn.isChecked() != bool(on):
            self.snap_btn.setChecked(bool(on))
        if not self.selftest:
            self.settings.setValue('gizmoSnap', 'true' if on else 'false')

    def _gizmo_info(self, text):
        """The readout of a gizmo drag, over the view's top left corner (where a part's name shows under the laser)."""
        self.hover.setText(text)
        self.hover.adjustSize()

    def _drag_cancelled(self):
        """A drag taken back (Esc, right click) or one that changed nothing (a click): the scene back to the undo step its
        start kept, and that step dropped. (Restored, not only dropped: the drag had cleared the part the hand was on and
        its job; review.)"""
        if self.undo_stack:
            self.restore(self.undo_stack.pop())
            self.redo_stack = list(getattr(self, '_redo_before', []))  # (nothing changed: the redo history stays; review C5)

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

        # The first thing a new user reads: the steps, and where the rest is explained (Help is the last item of the left bar).
        start = BodyLabel('<b>Start here:</b> pick a weapon, press Hands on the gun, drag a hand to move it (Shift + drag turns '
                          'it) or use the arrows and rings on it (Move / Turn over the view, keys G and R), then Save pose. '
                          'Help in the left bar explains everything.')
        start.setWordWrap(True)
        start.setTextFormat(Qt.TextFormat.RichText)
        v.addWidget(start)

        self.search = SearchLineEdit()
        self.search.setPlaceholderText('Search weapons')
        self.search.textChanged.connect(self.filter_weapons)
        self.weapon_list = ListWidget()
        self.weapon_list.setFixedHeight(190)
        self.weapon_list.currentItemChanged.connect(self.on_weapon_chosen)
        self.weapon_info = CaptionLabel('')
        self.weapon_info.setWordWrap(True)
        self._card_job_text = None  # (the text a job last put on this card: _run keeps the text before it, not that one)
        v.addWidget(card('Weapon', self.search, self.weapon_list, self.weapon_info))

        on_gun = PrimaryPushButton(FIF.FINGERPRINT, 'Hands on the gun')
        on_gun.clicked.connect(self.hands_on_gun)
        on_gun.setToolTip('Right hand on the grip, left hand on the handguard. Ctrl+Z undoes it, Ctrl+Y redoes it.')
        self.sel_right = TogglePushButton('Right hand')
        self.sel_left = TogglePushButton('Left hand')
        self.sel_right.clicked.connect(lambda: self.select_hand(self.scene.right))
        self.sel_left.clicked.connect(lambda: self.select_hand(self.scene.left))
        self.sel_right.setToolTip('Edit the right hand (key 1). Drag a hand to move it, Shift + drag to turn it.')
        self.sel_left.setToolTip('Edit the left hand (key 2). Drag a hand to move it, Shift + drag to turn it.')
        snaps = []
        for text, target, tip in (('Handguard', 'support', 'Left hand onto the handguard'),
                                  ('Slide', 'rack', 'Left hand onto the slide or the charging handle'),
                                  ('Magazine', 'magazine', 'Left hand onto the magazine'),
                                  ('Cover', 'cover', 'Left hand onto the cover latch, where the gun opens')):
            b = PushButton(text)
            b.clicked.connect(lambda _, t=target: self.snap_left(t))
            b.setToolTip(tip + '. Ctrl+Z undoes it.')
            snaps.append(b)
        self.pose = ComboBox()
        self.pose.addItems(list(POSES.keys()))
        self.pose.textActivated.connect(self.on_pose_chosen)  # (also a re-pick of the text it shows)
        self.finger_sliders = []
        fingers = QVBoxLayout()
        fingers.setSpacing(2)
        for i, name in enumerate(FINGERS):
            s = LabeledSlider(name, lambda f, i=i: self.on_finger(i, f), on_press=self.push_undo)
            self.finger_sliders.append(s)
            fingers.addWidget(s)
        self.thumb_base = LabeledSlider('Thumb base', self.on_thumb_base, on_press=self.push_undo)
        fingers.addWidget(self.thumb_base)
        self.hands_hint_text = ('Drag a hand to move it, Shift + drag to turn it; or drag its arrows (move along one axis), '
                                'squares (in a plane) and rings (turn about one axis). Drag elsewhere to look around, right-drag '
                                'to pan, wheel to zoom.')
        self.hands_hint = CaptionLabel(self.hands_hint_text)  # (set_laser says on it that the laser takes the clicks)
        self.hands_hint.setWordWrap(True)
        self.hand_label = BodyLabel('Pose of the right hand')
        # The hint leads the card: at its bottom, under six sliders, a new user never reached it (real2 I1#6).
        v.addWidget(card('Hands', self.hands_hint, on_gun, row(self.sel_right, self.sel_left), CaptionLabel('Left hand to:'),
                         row(*snaps), self.hand_label, self.pose, fingers))

        play = PrimaryPushButton(FIF.PLAY, 'Reload')
        play.clicked.connect(self.play_reload)
        play.setToolTip('Plays the reload with the hands (Space). Ctrl+Z undoes it.')
        rack = PushButton(FIF.PLAY, 'Rack')
        rack.clicked.connect(self.play_rack)
        rack.setToolTip('Plays the rack of the slide or the charging handle. Ctrl+Z undoes it.')
        reset = PushButton(FIF.SYNC, 'Reset parts and hands')
        reset.clicked.connect(self.reset_parts)
        reset.setToolTip('Parts back to rest (magazine in, slide forward, cover shut), every hidden part shown, '
                         'hands back on the gun.')
        self.s_slide = LabeledSlider('Slide back', lambda f: self.set_part(slide=f), width=110, on_press=self.push_undo)
        self.s_mag = LabeledSlider('Magazine out', lambda f: self.set_part(mag=f), width=110, on_press=self.push_undo)
        self.s_cover = LabeledSlider('Cover open', lambda f: self.set_part(cover=f), width=110, on_press=self.push_undo)
        # An empty gun's reload starts with its slide caught back, as after the last shot (play_reload; real2 I7#2).
        self.empty_sw = SwitchButton()
        self.empty_sw.setChecked(False)
        self.empty_sw.setToolTip('On: the reload starts with the slide locked back, as an empty gun is left after its last shot, '
                                 'and the slide is released at the end. Only guns whose slide locks on empty (or en-bloc '
                                 'rifles) take it.')
        v.addWidget(card('Reload', row(play, rack, reset), row(BodyLabel('Empty gun (after the last shot)'), self.empty_sw),
                         self.s_slide, self.s_mag, self.s_cover))

        self.laser_btn = TogglePushButton(FIF.ALIGNMENT, 'Laser pointer  (L)')
        self.laser_btn.toggled.connect(self.set_laser)
        self.laser_btn.setToolTip('Laser pointer (L). Click a part to see what it is and where; Alt + click does the same without '
                                  'the laser. While the laser is on, clicks pick parts and a hand cannot be dragged freely (its '
                                  'gizmo still moves it): press L again. Right-click for the menu.')
        self.laser_hint_text = ('Click a part with the laser (or Alt + click) to see what it is and where. Right-click, then Copy '
                                'coordinates, to copy it with the hand nearest to it.')
        self.laser_hint = CaptionLabel(self.laser_hint_text)  # (set_laser changes it while the laser is on)
        self.laser_hint.setWordWrap(True)
        v.addWidget(card('Point', self.laser_btn, self.laser_hint))

        save = PrimaryPushButton(FIF.SAVE, 'Save pose  (also for the game)')
        save.clicked.connect(self.save_pose)
        save.setToolTip("Saves this weapon's hands as its pose (Ctrl+S): tools/sandbox/poses, and the game's copy when "
                        'Ravenfield is found.')
        imp = PushButton(FIF.FOLDER, 'Import pose…')
        imp.clicked.connect(self.import_pose)
        exp = PushButton(FIF.SHARE, 'Export pose…')
        exp.clicked.connect(self.export_pose)
        copy_text = PushButton(FIF.COPY, 'Copy pose as text')  # the same text as the viewport's menu entry (real2 I8#0)
        copy_text.clicked.connect(self.copy_hands_text)
        copy_text.setToolTip('The weapon and both hands as text on the clipboard (where each palm is, its pose, the part it holds), '
                             'to paste to Claude. Export pose… saves the file instead.')
        self.game_status = CaptionLabel('')
        self.game_status.setWordWrap(True)
        send = PushButton(FIF.SEND, 'Send to game')
        send.clicked.connect(self.send_to_game)
        remove = PushButton(FIF.DELETE, 'Remove from game')
        remove.clicked.connect(self.remove_from_game)
        shot = PushButton(FIF.CAMERA, 'Screenshot')
        shot.clicked.connect(lambda: self.screenshot())  # (a plain connect would pass the button's checked state as 'clean')
        shot.setToolTip('Screenshot of the view (F12), saved to reports/sandbox/captures.')
        # The same setting as Advanced's 'Pictures without grid and markers': the two are kept in step (on_clean_check).
        self.clean_check = CheckBox('Clean picture')
        self.clean_check.setToolTip('Leaves out the grid, the markers, the laser line and the highlights (the same switch as '
                                    'Advanced, Pictures without grid and markers).')
        self.clean_check.toggled.connect(self.on_clean_check)
        self.rec_btn = TogglePushButton(FIF.VIDEO, 'Record GIF')
        self.rec_btn.toggled.connect(self.toggle_record)
        turn = PushButton(FIF.ROTATE, 'Turntable GIF')
        turn.clicked.connect(lambda: self.turntable_gif())
        reload_gif = PushButton(FIF.PLAY, 'Record the reload')
        reload_gif.clicked.connect(lambda: self.record_reload())
        v.addWidget(card('Save and share', save, row(imp, exp), copy_text, self.game_status, row(send, remove),
                         row(shot, self.clean_check), row(self.rec_btn, turn), reload_gif))
        v.addStretch(1)
        return p

    def _advanced_panel(self):
        p = QWidget()
        p.setObjectName('panel')
        v = QVBoxLayout(p)
        v.setContentsMargins(0, 8, 8, 8)
        v.setSpacing(10)

        self.sel_name = BodyLabel('Selected: right hand')
        self.sel_name.setWordWrap(True)  # (it names what the hand touches: long; the panel is narrow)
        self.num = []
        # The frame the position boxes count in (set_numbers_frame): the analysed grip point, or the muzzle, as the laser readout
        # and Copy coordinates give the place (real2 I2#0).
        self.frame_box = ComboBox()
        self.frame_box.addItems(['from the grip point', 'from the muzzle'])
        self.frame_box.setToolTip('The position boxes count from the analysed grip point (forward along the barrel, up, '
                                  'right), or from the muzzle (back, up, right: the numbers the laser readout and Copy '
                                  'coordinates give).')
        self.frame_box.setCurrentIndex(1 if self.numbers_frame == 'muzzle' else 0)  # (before the signal is connected: no save)
        self.frame_box.currentIndexChanged.connect(lambda i: self.set_numbers_frame(('grip', 'muzzle')[i]))
        grid = QVBoxLayout()
        for label, unit in (('Position in cm from the analysed grip point: forward along the barrel, up, right', 'cm'),
                            ('Turn in degrees, relative to the gun: pitch, yaw, roll', 'deg')):
            cap = CaptionLabel(label)
            cap.setWordWrap(True)
            if unit == 'cm':
                self.pos_caption = cap  # (its words follow the frame: _sync_number_labels)
            grid.addWidget(cap)
            boxes = []
            for _ in range(3):
                b = DoubleSpinBox()
                # (Wide: a hand 6 m off showed 500 and was pulled back to it when another box changed. Each box now moves only
                # its own axis, so a clamped one cannot move the hand either.)
                b.setRange(-100000, 100000)
                b.setDecimals(1)
                b.setSingleStep(0.5 if unit == 'cm' else 5)
                # Three in a row in the narrow side panel: the arrows would cut the number off (120 showed as 12), so the keys
                # and the mouse wheel step the value instead.
                b.setSymbolVisible(False)
                # Three must fit the side panel with its margins and scroll bar: the box's own size hint overflowed it.
                b.setMinimumWidth(64)
                b.setSizePolicy(QSizePolicy.Policy.Ignored, QSizePolicy.Policy.Fixed)
                b.valueChanged.connect(self.on_numbers)
                boxes.append(b)
            self.num.append(boxes)
            grid.addLayout(row(*boxes))
        self.numbers_note = CaptionLabel('')  # (its words follow the frame: _sync_number_labels)
        self.numbers_note.setWordWrap(True)
        # What the hand is on or touches, and how far its glove is drawn from its place (the settle).
        self.touch_text = CaptionLabel('')
        self.touch_text.setWordWrap(True)
        # The hand against the saved pose (the file the game reads), and the Revert that goes back to it.
        self.saved_text = CaptionLabel('')
        self.saved_text.setWordWrap(True)
        self.revert_btn = PushButton('Revert to saved')
        self.revert_btn.setToolTip('Puts both hands and the parts back to the saved pose (Ctrl+Z undoes that)')
        self.revert_btn.clicked.connect(self.revert_to_saved)
        mirror = PushButton('Copy this pose to the other hand')
        mirror.clicked.connect(self.copy_pose_to_other)
        v.addWidget(card('Selected hand', self.sel_name, row(BodyLabel('Position'), self.frame_box, stretch=[0, 1]), grid,
                         self.numbers_note, self.touch_text, self.saved_text, self.revert_btn, mirror))
        self._sync_number_labels()

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
        for text, name, tip in (('Side', 'side', 'Side view (keypad 1, Num Lock on)'), ('Top', 'top', 'Top view (keypad 7)'),
                                ('Front', 'front', 'Front view (keypad 3)'),
                                ('3/4', 'three', 'Three-quarter view (keypad 5 or 0)')):
            b = PushButton(text)
            b.clicked.connect(lambda _, n=name: self.view.set_view(n))
            b.setToolTip(tip)
            views.append(b)
        self.grid_sw = SwitchButton()
        self.grid_sw.setChecked(True)
        self.grid_sw.checkedChanged.connect(lambda c: (setattr(self.view, 'show_grid', c), self.view.update()))
        self.grid_sw.setToolTip('The floor grid, in the view and in pictures.')
        self.mark_sw = SwitchButton()
        self.mark_sw.setChecked(True)
        self.mark_sw.checkedChanged.connect(lambda c: (setattr(self.view, 'show_markers', c), self.view.update()))
        self.mark_sw.setToolTip('The markers on the gun (listed below), in the view and in pictures.')
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
        self.settle_sw.checkedChanged.connect(self.on_settle_switch)
        self.settle_sw.setToolTip('Settle: the hands are put onto the gun as they settle in VR (up to 2 cm).')
        markers_hint = CaptionLabel('Markers: green grip, blue support, orange rack point and its travel, red muzzle and aim, '
                                    'yellow magazine seat and its line, purple cover latch.')
        markers_hint.setWordWrap(True)
        settle_hint = CaptionLabel("Settle: after a snap the glove is put onto the gun's surface, as it settles in VR (up to 2 cm).")
        settle_hint.setWordWrap(True)
        # Pictures and GIFs without the grid, markers, laser line and highlights (off by default: they show the evidence).
        self.clean_sw = SwitchButton()
        self.clean_sw.setChecked(False)
        self.clean_sw.checkedChanged.connect(self.on_clean_switch)
        # Screenshots at the view's size, or drawn again at twice or four times it, for a post (the 1x, 2x, 4x of SIZES).
        self.size_box = ComboBox()
        self.size_box.addItems(['1x', '2x', '4x'])
        self.size_box.setCurrentIndex(0)
        self.size_box.setToolTip('The screenshot at the view\'s size, or drawn again at 2x or 4x for a post (a bigger one takes '
                                 'longer; a size the graphics card cannot hold gives way to the next smaller one).')
        # A PNG screenshot with the background see-through, for placing it on another background (GIFs stay solid).
        self.transp_sw = SwitchButton()
        self.transp_sw.setChecked(False)
        self.transp_sw.setToolTip('PNG screenshots keep the background see-through. GIFs stay solid.')
        v.addWidget(card('View', row(*views), row(BodyLabel('Grid'), self.grid_sw, BodyLabel('Markers'), self.mark_sw),
                         row(BodyLabel('Settle hands on the gun'), self.settle_sw), settle_hint,
                         markers_hint, row(BodyLabel('GIF fps'), self.gif_fps, BodyLabel('size'), self.gif_scale),
                         row(BodyLabel('Pictures without grid and markers'), self.clean_sw),
                         row(BodyLabel('Screenshot size'), self.size_box),
                         row(BodyLabel('Transparent background (PNG)'), self.transp_sw)))

        self.export_names = LineEdit()
        self.export_names.setPlaceholderText('Weapons to export, comma-separated (empty: all)')  # (a space is no separator: real2 I2#1)
        export = PushButton(FIF.DOWNLOAD, 'Export weapons')
        export.clicked.connect(self.export_weapons)
        game = PushButton(FIF.GAME, 'Test this gun in the game')
        game.clicked.connect(self.test_in_game)
        self.job_log = PlainTextEdit()
        self.job_log.setReadOnly(True)
        self.job_log.setFixedHeight(160)
        folder = PushButton(FIF.FOLDER, 'Game folder…')
        folder.clicked.connect(self.choose_game_folder)
        self.game_label = CaptionLabel(self.game_folder())
        self.game_label.setWordWrap(True)
        v.addWidget(card('Game (only when really needed)', row(folder, self.game_label, stretch=[0, 1]), self.export_names,
                         export, game, self.job_log))
        v.addStretch(1)
        return p

    def _shortcuts(self):
        def idle(fn):
            # (Not while a hand or the gun is dragged: 1, 2, the gizmo keys, Ctrl+Z, O and F changed what the drag was
            # measuring from, and the hand jumped; review.)
            return lambda: None if self.view.editing() else fn()
        for key, fn in (('L', lambda: self.laser_btn.toggle()), ('F', self.frame_weapon),
                        ('Shift+F', self.frame_hand),
                        ('1', lambda: self.select_hand(self.scene.right)), ('2', lambda: self.select_hand(self.scene.left)),
                        ('Space', self.play_reload), ('Ctrl+S', self.save_pose), ('F12', self.screenshot),
                        ('Ctrl+Z', self.undo), ('Ctrl+Y', self.redo), ('Ctrl+Shift+Z', self.redo), ('O', self.view.toggle_ortho),
                        ('W', lambda: self.set_gizmo_mode('free')), ('G', lambda: self.set_gizmo_mode('move')),
                        ('R', lambda: self.set_gizmo_mode('rotate')), ('T', lambda: self.set_gizmo_mode('both')),
                        (',', self.cycle_gizmo_space)):
            QShortcut(QKeySequence(key), self, activated=idle(fn))
        # Blender-like view keys on the keypad (with Num Lock on). The keypad modifier keeps them apart from the plain 1 and 2
        # keys that select the hands (tested: each key fires only its own shortcut).
        keypad = Qt.KeyboardModifier.KeypadModifier
        for key, name in ((Qt.Key.Key_1, 'side'), (Qt.Key.Key_3, 'front'), (Qt.Key.Key_7, 'top'),
                          (Qt.Key.Key_5, 'three'), (Qt.Key.Key_0, 'three')):
            QShortcut(QKeySequence(keypad | key), self, activated=idle(lambda n=name: self.view.set_view(n)))
        QShortcut(QKeySequence(keypad | Qt.Key.Key_Period), self, activated=idle(self.frame_hand))  # (as Blender's keypad .)
        # ? opens Help: Shift + / on a US keyboard, Shift + ß on a German one. Both forms are bound; the layout sends one.
        for key in ('?', Qt.KeyboardModifier.ShiftModifier | Qt.Key.Key_Question):
            QShortcut(QKeySequence(key), self, activated=idle(self.show_help))

    # ---- weapons -----------------------------------------------------------------------------------------------------

    def load_weapon_list(self, keep_open=False):
        """The exported weapons. keep_open: the weapon open now stays open (opened again from its new files, with the hands as
        they are); else the one opened last time, or the first."""
        current = self.scene.weapon.id if self.scene.weapon is not None else None
        if keep_open and current:
            self.settle_timeline()  # (a reload still playing: its hands half way were kept and could be saved, real2 B2#2)
        hands = self.scene.pose_document() if keep_open and current else None
        self.weapon_list.blockSignals(True)
        self.weapon_list.clear()
        self.items = []
        if os.path.isdir(self.data):
            for name in sorted(os.listdir(self.data), key=str.lower):
                if name.endswith(('.new', '.old')):
                    continue  # (an export's copy being swapped in, or left by one cut off half way)
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
        self.weapon_list.blockSignals(False)
        self.filter_weapons(self.search.text())
        if not self.items:
            self.weapon_info.setText('No weapons exported yet. Advanced → "Export weapons" runs the game once (no headset) '
                                     'and writes them; or run python tools/sandbox/export.py.')
            return
        self.weapon_info.setText(f'{len(self.items)} weapon(s).')
        want = current if keep_open and current else (None if self.selftest else str(self.settings.value('lastWeapon', '') or ''))
        item = next((it for it in self.items if it.data(Qt.ItemDataRole.UserRole) == want), None) or self.items[0]
        self.weapon_list.blockSignals(True)
        self.weapon_list.setCurrentItem(item)
        self.weapon_list.blockSignals(False)
        self.open_weapon(item.data(Qt.ItemDataRole.UserRole), hands_doc=hands if item.data(Qt.ItemDataRole.UserRole) == current else None)

    def filter_weapons(self, text):
        t = text.lower().strip()
        for it in self.items:
            it.setHidden(bool(t) and t not in it.text().lower())

    def on_weapon_chosen(self, item, _prev=None):
        if item is None:
            return
        wid = item.data(Qt.ItemDataRole.UserRole)
        # Each open parses the whole gun on the GUI thread, so a key held down (one choice per repeat) queued loads that fell
        # behind the keys. A choice that comes soon after the last open waits 120 ms and restarts that wait; the last one opens.
        # The self-test, and a choice after a pause, open at once.
        if self.selftest or time.monotonic() - self._opened_at > 0.15:
            self._pending_wid = None
            self._open_timer.stop()
            self._open_now(wid)
            return
        self._pending_wid = wid
        self._open_timer.start()

    def _open_pending(self):
        wid, self._pending_wid = self._pending_wid, None
        if wid is not None:
            self._open_now(wid)

    def _open_now(self, wid):
        self.open_weapon(wid)
        # Stamped when the load is over, not when it starts (real2 I5#0): the keys queued behind a slow load then find a recent
        # open and restart the wait, so a burst opens only its last weapon.
        self._opened_at = time.monotonic()

    def open_weapon(self, wid, hands_doc=None):
        self.settle_timeline()
        old = self.scene.weapon
        if old is not None and old.id != wid and self.unsaved():
            self.drafts[old.id] = self.scene.pose_document()  # unsaved hands kept: they come back when it is opened again
            self.mark_draft(old.id, True)
        self.parts_home()  # (the open weapon was stopped where its reload was)
        try:
            self.scene.load_weapon(os.path.join(self.data, wid))
        except Exception as e:  # noqa: BLE001 (a broken export)
            self.log_error(f'opening {wid}', e)
            self.toast(f'Could not open {wid}', f'{type(e).__name__}: {e}\nThe weapon shown is still {old.name if old else "none"}.', error=True)
            if old is not None:  # the list back on the weapon still shown
                self.weapon_list.blockSignals(True)
                self.weapon_list.setCurrentItem(next((it for it in self.items if it.data(Qt.ItemDataRole.UserRole) == old.id), None))
                self.weapon_list.blockSignals(False)
            return
        if not self.selftest:
            self.settings.setValue('lastWeapon', wid)
            self.settings.sync()  # (on disk now, not only at the exit: real2 I3#0)
        self.undo_stack, self.redo_stack = [], []
        self.view.part_node = None
        w = self.scene.weapon
        self.view.pick_point = None
        self.view.pick_line = None
        self.picked_point = None  # (the last weapon's: copied, its part name and point went out with this weapon's name)
        self.picked_node = None
        self.readout.hide()
        self.view.rebuild()
        r = w.rig
        info = f"{r.get('action', '')}{' · pistol' if r.get('isPistol') else ''} · {len(w.nodes)} parts"
        if w.stand_ins:
            info += f' · {w.stand_ins} shown as grey boxes (their mesh could not be read from the game)'
        if not w.muzzle_known:
            info += ' · no muzzle in the export: distances are from the gun\'s front'
        warns = r.get('warnings') or []
        if isinstance(warns, str):
            # (The export writes them as one string, "a; b; c": joined as a list it came out a letter at a time, real2 B9#4.)
            warns = [x.strip() for x in warns.split(';') if x.strip()]
        if warns:
            info += ' · the analysis warns: ' + '; '.join(str(x) for x in warns[:3]) + (' …' if len(warns) > 3 else '')
        self.weapon_info.setText(info)
        self.rig_text.setPlainText(json.dumps(r, indent=1))
        self.fill_poses()
        self.parts.clear()
        for n in sorted(w.nodes, key=lambda n: (n.role == 'body', n.role, n.short.lower())):
            it = QListWidgetItem(f'{n.short}  —  {ROLE_NAMES.get(n.role, n.role)}')
            it.setData(Qt.ItemDataRole.UserRole, id(n))
            self.parts.addItem(it)
        pose_file = os.path.join(self.poses_dir, wid + '.json')
        self.saved_doc = None
        if os.path.exists(pose_file):
            try:
                with open(pose_file, encoding='utf-8') as f:
                    self.saved_doc = json.load(f)
                self.scene.read_pose_document(self.saved_doc)
            except Exception as e:  # noqa: BLE001 (a hand-made or cut file)
                self.log_error(f'reading the saved pose {pose_file}', e)
                try:
                    kept = self.keep_old(pose_file, 'unreadable')
                except OSError:
                    kept = None  # (logged; the message below then names no copy)
                self.toast('The saved pose could not be read', f'{pose_file}\n{type(e).__name__}: {e}'
                           + (f'\nA copy is in {kept}.' if kept else ''), error=True)
                self.saved_doc = None
        source = hands_doc or self.drafts.pop(wid, None)
        self.mark_draft(wid, False)
        try:
            if source is not None:
                self.scene.apply_pose_document(source)
            elif self.saved_doc is not None:
                self.scene.apply_pose_document(self.saved_doc)
                self.toast('Saved pose loaded', pose_file)
            else:
                self.hands_on_gun()
        except Exception as e:  # noqa: BLE001
            self.log_error(f'putting the hands on {wid}', e)
            self.hands_on_gun()
        # Putting the hands on when a weapon opens is not an edit (closing then asked "Close without saving?" for nothing); hands
        # brought back from an unsaved draft still count as not saved.
        self.undo_stack, self.redo_stack = [], []
        self.opened_with_draft = source is not None and hands_doc is None
        self.sync_sliders()
        self.frame_weapon()
        self.select_hand(self.scene.right)
        self.update_game_status()

    def frame_weapon(self, hands=True):
        """F: the camera on the gun and on both hands, so a hand moved away from it is in view too (hands=False: the gun alone,
        the context menu's entry). Shift + F frames the selected hand (frame_hand)."""
        w = self.scene.weapon
        if w is None:
            return
        c, r = w.center_world()
        r = max(0.12, r)
        if hands:
            for h in self.scene.hands():
                # The smallest sphere holding the gun's sphere and the hand's (a glove reaches about 10 cm from its palm centre).
                d = float(np.linalg.norm(h.position - c))
                rh = 0.1
                if d + rh <= r:
                    continue
                if d < 1e-9:  # the hand sits on the gun's centre: the sphere grows to hold it
                    r = rh
                    continue
                big = (r + d + rh) / 2
                c = c + (h.position - c) * ((big - r) / d)
                r = big
        self.view.frame(c, r)

    def frame_hand(self):
        """Shift + F and keypad .: the camera on the selected hand (else the right one), about 35 cm off, so its grip is seen
        close. The gun is out of the view then; F frames the gun and both hands as before."""
        h = self.view.selected_hand or self.scene.right
        self.view.frame(h.drawn_position(), 0.11)  # (a radius of 11 cm: Viewport.frame backs the camera off to about 35 cm)

    def show_help(self):
        """?: the Help page, as the left bar's Help item shows it."""
        self.win.switchTo(self.win.help)

    # ---- hands ----------------------------------------------------------------------------------------------------------

    def hands_on_gun(self):
        if self.scene.weapon is None:
            return
        self.push_undo()
        self.scene.snap(self.scene.right, 'grip')
        self.scene.snap(self.scene.left, 'support')
        self.after_hand_change()

    def snap_left(self, target):
        if self.scene.weapon is None:
            return
        if target == 'cover' and self.scene.weapon.rig.get('hingeKind', 'None') == 'None':
            self.toast('This weapon has no cover or barrels that open', '')
            return
        if target == 'rack' and not self._has_rack():
            self.toast('This weapon has no slide or handle that moves', '')
            return
        self.push_undo()
        self.scene.snap(self.scene.left, target)
        self.select_hand(self.scene.left)
        self.after_hand_change()

    def select_hand(self, hand):
        self.view.selected_hand = hand
        self.view.set_gizmo_target('hand')
        self.on_select(hand)
        self.view.refresh_cursor()  # (the gizmo moved to the other hand)
        self.view.update()

    def on_select(self, hand):
        self.sel_right.setChecked(hand is self.scene.right)
        self.sel_left.setChecked(hand is self.scene.left)
        h = hand or self.scene.right
        self.hand_label.setText(f"Pose of the {h.side} hand")
        self.update_selected_label()
        self.show_pose_name(h)
        self.sync_hand_controls()

    def update_selected_label(self):
        """The Selected hand card: the hand, what it is on or touches (also after a drag), how its glove is drawn, and how it
        differs from the saved pose."""
        h = self.current_hand()
        w = self.scene.weapon
        if w is None:  # (Escape with no weapon open still asks for the card)
            self.sel_name.setText(f'Selected: {h.side} hand')
            self.touch_text.setText('')
            self.saved_text.setText('')
            self.revert_btn.setEnabled(False)
            if self.view.touch_node is not None:
                self.view.touch_node = None
                self.view.update()
            return
        self._pick_follows()  # (a part or a hand moved: the laser's point and its readout follow; every such path comes here)
        part, dist = w.nearest_part(w.to_gun(h.position)[:3])
        near = part is not None and dist <= 0.06  # (the reach a hand gets its job by: Scene.contact_role)
        # The part the hand touches, tinted in the view (review I9#2); not a body part: on many guns the body is the whole
        # receiver and stock, and a palm on the grip tinted the whole gun.
        touch = part if near and part.role != 'body' else None
        if touch is not self.view.touch_node:
            self.view.touch_node = touch
            self.view.update()
        part_name = f"{ROLE_NAMES.get(part.role, part.role)} '{part.short}'" if part is not None else ''
        if h.on_part:
            on = f' (on the {h.on_part})'
        elif near:
            on = f' (touches the {ROLE_NAMES.get(part.role, part.role)})'
        else:
            on = ' (touches nothing within 6 cm)'
        self.sel_name.setText(f'Selected: {h.side} hand{on}')
        lines = []
        if part is None:
            lines.append('touches: nothing, the gun has no parts')
        elif near:
            lines.append(f'touches: {part_name}, {dist * 100:.1f} cm')
        else:
            lines.append(f'touches nothing within 6 cm (nearest: {part_name}, {dist * 100:.1f} cm)')
        if h.side == 'left' and h.role != 'support':
            lines.append('the game takes the left hand only as support (on the handguard)')
        drawn = float(np.linalg.norm(h.drawn_position() - h.position)) * 100
        lines.append(f'glove drawn {drawn:.1f} cm from its place (settle)' if drawn > 0.005 else 'glove drawn at its place (no settle here)')
        self.touch_text.setText('\n'.join(lines))
        text, changed = self.saved_line(h)
        self.saved_text.setText(text)
        self.revert_btn.setEnabled(changed)

    def saved_line(self, h):
        """'as saved', or what differs from the saved pose (the file the game reads) for this hand, and whether anything in the
        pose differs at all (the Revert button is only useful then)."""
        w = self.scene.weapon
        if self.saved_doc is None:
            return 'No saved pose for this weapon yet.', False
        try:
            saved = self.scene.read_pose_document(self.saved_doc)
        except Exception as e:  # noqa: BLE001 (a saved file cut or changed since it was read)
            return f'The saved pose cannot be read: {e}', False
        here = self.pose_changes(h, saved['hands'].get(h.side))
        others = any(self.pose_changes(o, saved['hands'].get(o.side)) for o in self.scene.hands() if o is not h)
        p = saved['parts']
        others = others or any(abs(a - b) > 1e-3 for a, b in ((w.slide, p['slide']), (w.mag, p['magazine']), (w.cover, p['cover'])))
        elsewhere = ' · the other hand or the parts differ too' if others else ''
        if here:
            return f"{', '.join(here)} changed from the saved pose{elsewhere}", True
        if others:
            return 'as saved for this hand' + elsewhere, True
        return 'as saved', False

    def pose_changes(self, h, s):
        """What differs for one hand from its saved state s (as read), with the tolerances unsaved() uses: a list of words,
        empty when the hand is as saved (or the saved pose has no place for it)."""
        if s is None:
            return []
        w = self.scene.weapon
        out = []
        cm = float(np.linalg.norm(w.to_gun(h.position)[:3] - s['position'])) * 100
        q = quat_norm(quat_mul(quat_inv(w.Wq), h.rotation))
        dot = min(1.0, abs(float(np.dot(q, s['rotation']))))
        if cm > 0.01:
            out.append(f'{cm:.1f} cm')
        if 1.0 - dot > 1e-6:
            out.append(f'{math.degrees(2 * math.acos(dot)):.1f} degrees')
        fingers = sum(1 for a, b in zip(h.curls, s['curls']) if abs(a - b) > 1e-3)
        if fingers:
            out.append(f"{fingers} finger{'s' if fingers > 1 else ''}")
        if abs(h.thumb - s['thumb']) > 1e-3:
            out.append('thumb')
        return out

    def show_pose_name(self, h):
        self.pose.blockSignals(True)
        self.pose.setCurrentText(h.pose_name if self.pose.findText(h.pose_name) >= 0 else 'Custom')
        self.pose.blockSignals(False)
        self.update_selected_label()  # (the comparison with the saved pose follows the fingers as they are moved)

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
        names += list(POSES.keys()) + ['Custom']
        self.pose.addItems(names)
        self.pose.blockSignals(False)

    def on_pose_chosen(self, name):
        h = self.current_hand()
        w = self.scene.weapon
        if name == 'Custom':
            return  # (the sliders make a custom pose)
        self.push_undo()
        if w is not None and name in ("weapon's own grip", "weapon's own support"):
            curls = w.rig.get('gripCurls' if 'grip' in name else 'supportCurls')
            if curls:
                h.thumb_set = False  # the weapon's own pose: the game keeps its thumb rule (set before the pose: the mesh follows it)
                base = grip_thumb_base(h.pistol, h.machine_gun) if 'grip' in name else 0.35  # (the class's, as the snap gives it)
                h.set_pose(name, [base] + list(curls))
                self.sync_hand_controls()
                self.view.update()
            return
        h.thumb_set = True  # a pose picked by hand: its thumb goes to the game too (set before the pose: the mesh follows it)
        h.set_pose(name)
        self.sync_hand_controls()
        self.view.update()

    def on_finger(self, i, f):
        h = self.current_hand()
        h.curls[i] = f
        h.pose_name = 'Custom'
        if i == 0:
            h.thumb_set = True
        h.update_mesh()
        self.show_pose_name(h)
        self.view.update()

    def on_thumb_base(self, f):
        h = self.current_hand()
        h.thumb = f
        h.pose_name = 'Custom'
        h.thumb_set = True
        h.update_mesh()
        self.show_pose_name(h)
        self.view.update()

    def sync_hand_controls(self):
        h = self.current_hand()
        for s, c in zip(self.finger_sliders, h.curls):
            s.set(c)
        self.thumb_base.set(h.thumb)
        self.sync_numbers()

    def set_numbers_frame(self, frame):
        """The position boxes' frame: 'grip' (forward along the barrel from the analysed grip point) or 'muzzle' (back, up, right
        from the muzzle, as the laser readout and Copy coordinates give them). Remembered between runs, as the gizmo's choices are
        (real2 I2#0)."""
        if frame not in ('grip', 'muzzle'):
            return
        self.numbers_frame = frame
        i = ('grip', 'muzzle').index(frame)
        if self.frame_box.currentIndex() != i:
            self.frame_box.setCurrentIndex(i)
        if not self.selftest:
            self.settings.setValue('numbersFrame', frame)
        self._sync_number_labels()
        self.sync_numbers()

    def _sync_number_labels(self):
        """The position boxes' caption and the card's note say the frame the boxes show now."""
        if self.numbers_frame == 'muzzle':
            self.pos_caption.setText('Position in cm from the muzzle: back along the barrel, up, right')
            self.numbers_note.setText("The boxes count as the laser readout and Copy coordinates do (back, up, right from the "
                                      "muzzle); those give the picked point on the part, not this hand's palm centre. The "
                                      'frame above switches the boxes to forward, up and right from the grip point.')
        else:
            self.pos_caption.setText('Position in cm from the analysed grip point: forward along the barrel, up, right')
            self.numbers_note.setText('The laser readout and Copy use back, up, right from the muzzle instead, so their numbers '
                                      'differ from these.')

    def hand_numbers(self, w, h):
        """The hand's place in the boxes' frame, exact (cm): back, up, right from the muzzle (the laser readout's frame), or
        forward, up, right from the analysed grip point."""
        g = w.to_gun(h.position)[:3]
        if self.numbers_frame == 'muzzle':
            return w.from_muzzle(g)
        return np.array([np.dot(g - w.grip, w.aim), np.dot(g - w.grip, w.up), np.dot(g - w.grip, w.right)]) * 100

    def sync_numbers(self):
        w = self.scene.weapon
        h = self.current_hand()
        if w is None:
            return
        local = self.hand_numbers(w, h)
        q = quat_mul(quat_inv(w.Wq), h.rotation)
        turn = self.turn_numbers(h, q)
        for box, val in zip(self.num[0] + self.num[1], list(local) + list(turn)):
            box.blockSignals(True)
            box.setValue(float(val))
            box.blockSignals(False)
        self.remember_numbers(h, q, turn)
        self.update_selected_label()

    def turn_numbers(self, h, q):
        """The turn boxes' angles for the turn q (relative to the gun). The angles shown last for this same turn are kept: a
        typed 120 stays 120 instead of coming back as 60 with yaw and roll 180 (the same turn). After a drag the Euler form
        nearest the angles shown last is used, so pitch does not jump at 90 degrees."""
        last = self._numbers_shown.get(h.side)
        if last is not None and 1.0 - abs(float(np.dot(q, last['q']))) < 1e-9:
            return last['turn'].copy()
        e = quat_to_euler_deg(q)
        if last is None:
            return e
        # The same turn written another way: pitch 180 - p with yaw and roll turned by 180. Each angle may also move by whole
        # turns (360 degrees is the same turn): the form nearest the angles shown last wins.
        best, best_d = e, None
        for c in (e, np.array([180.0 - e[0], e[1] + 180.0, e[2] + 180.0])):
            c = c + 360.0 * np.round((last['turn'] - c) / 360.0)
            d = float(np.sum((c - last['turn']) ** 2))
            if best_d is None or d < best_d:
                best, best_d = c, d
        return best

    def remember_numbers(self, h, q, turn):
        """The boxes as they show now for this hand (to see which one the user changes next) and the exact angles they show:
        the next change moves only the axis of the box that was changed (the boxes round to 0.1, which must not move the hand),
        and a drag keeps the angles the numbers were shown with."""
        self._numbers_shown[h.side] = {'q': np.array(q, dtype=np.float64), 'turn': np.array(turn, dtype=np.float64),
                                       'pos_box': np.array([b.value() for b in self.num[0]]),
                                       'turn_box': np.array([b.value() for b in self.num[1]])}

    def on_numbers(self):
        w = self.scene.weapon
        h = self.current_hand()
        if w is None:
            return
        if time.monotonic() - self._numbers_undo_at > 1.5:
            self.push_undo()
        self._numbers_undo_at = time.monotonic()
        last = self._numbers_shown.get(h.side)
        pos_box = np.array([b.value() for b in self.num[0]])
        turn_box = np.array([b.value() for b in self.num[1]])
        # Only the axes whose own box changed take the box's value; the others keep the hand's exact place and turn (rebuilt
        # from all three rounded boxes, one typed number moved the hand by up to 0.5 mm on the others, and a box clamped at its
        # limit pulled the hand back to it: review of the sandbox extras).
        # (Absolute only: np.isclose's relative part let a 0.1 step at 10000 pass as no change.)
        pos_changed = np.ones(3, bool) if last is None else ~np.isclose(pos_box, last['pos_box'], rtol=0.0, atol=1e-9)
        turn_changed = np.ones(3, bool) if last is None else ~np.isclose(turn_box, last['turn_box'], rtol=0.0, atol=1e-9)
        if pos_changed.any():
            local = np.where(pos_changed, pos_box, self.hand_numbers(w, h))
            if self.numbers_frame == 'muzzle':
                # Back, up, right from the muzzle: the exact inverse of from_muzzle, which the laser readout uses.
                h.position = w.to_world(w.muzzle_point(local[0], local[1], local[2]))
            else:
                f, u, r = local / 100
                h.position = w.to_world(w.grip + w.aim * f + w.up * u + w.right * r)
        q = quat_mul(quat_inv(w.Wq), h.rotation)
        turn = self.turn_numbers(h, q)  # (the exact angles shown for this turn)
        if turn_changed.any():
            turn = np.where(turn_changed, turn_box, turn)
            # The angles as typed (kept as typed: sync_numbers shows them again while the hand keeps this turn).
            h.rotation = quat_norm(quat_mul(w.Wq, euler_deg_to_quat(turn)))
            q = quat_mul(quat_inv(w.Wq), h.rotation)
        self.remember_numbers(h, q, turn)
        self._moved(h)
        self.view.update()

    def on_hand_moved(self, hand=None):
        """A hand is being dragged (on every move): it is no longer on the part it was snapped to, and its numbers follow. Its
        job is worked out once the drag ends (_moved, through drag_finished)."""
        h = hand if hand is not None else self.current_hand()
        h.on_part = ''
        self.update_selected_label()
        self.sync_numbers()

    def _moved(self, h):
        """A hand whose drag ended: its job is what it touches now (a left hand dragged from the magazine onto the handguard is
        the support hand again; its stale job dropped it from the saved pose)."""
        h.on_part = ''
        h.role = self.scene.contact_role(h)
        h.update_mesh()  # (the job decides how the glove is drawn: it kept the old job's curls until the next pose change)
        self.update_selected_label()

    def after_hand_change(self):
        self.on_select(self.current_hand())
        self.view.update()

    def on_settle_switch(self, on):
        self.scene.settle_on = on
        for h in self.scene.hands():
            if h.role in ('grip', 'support') or not on:
                self.scene.settle(h)
        self.sync_numbers()
        self.view.update()

    def copy_pose_to_other(self):
        self.push_undo()
        h = self.current_hand()
        o = self.scene.left if h is self.scene.right else self.scene.right
        o.thumb, o.curls, o.pose_name, o.thumb_set = h.thumb, list(h.curls), h.pose_name, h.thumb_set
        o.update_mesh()
        self.view.update()

    # ---- parts and the reload ---------------------------------------------------------------------------------------------

    def set_part(self, slide=None, mag=None, cover=None):
        w = self.scene.weapon
        if w is None:
            return
        w.set_state(slide, mag, cover)
        self.update_selected_label()  # (what the hand touches changes when a part moves under it)
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
        self.push_undo()
        self.parts_home()
        for n in w.nodes:
            n.hidden = False
        self.hands_on_gun()
        # Reset replaces the hands' poses without a word otherwise: say what it did and that Ctrl+Z gets them back.
        self.toast('Parts and hands reset (Ctrl+Z undoes it)',
                   'The parts are home, both hands are back on the gun and the parts you hid are shown again. The saved pose is not changed.')

    def parts_home(self):
        """The parts as the reload leaves them: the magazine in, nothing moved (the parts the user hid stay hidden)."""
        w = self.scene.weapon
        if w is None:
            return
        w.fresh = None
        for n in w.nodes:
            n.away = False
        w.set_state(0, 0, 0)
        self.sync_sliders()
        self._pick_follows()
        self.view.update()

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
            hand.update_mesh()

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

    def _ease(self, what, to, dur):
        """A step taking one part from where it is at the step's start to `to` (the reload starts from the parts as they are:
        a slider left part way snapped back on the first frame)."""
        w = self.scene.weapon
        start = {}

        def begin():
            start['v'] = getattr(w, {'slide': 'slide', 'mag': 'mag', 'cover': 'cover'}[what])

        def update(t):
            w.set_state(**{what: start['v'] + (to - start['v']) * t})
            self.sync_sliders()  # (the sliders follow the parts as they move, not only at the end: real2 I7#2)
            self._pick_follows()  # (and a laser point on the moving part)
        return (dur, update, begin)

    def _follow_ease(self, hand, target, what, to, dur):
        """A part eased from where it is to `to` with the hand kept on it."""
        dur, update, begin = self._ease(what, to, dur)
        return (dur, lambda t: (update(t), target()), begin)

    def _has_rack(self):
        r = self.scene.weapon.rig
        return r.get('rackPoint') is not None and bool(r.get('slide') or r.get('handle') or r.get('addedHandle') or r.get('carveHandle'))

    def play_reload(self):
        w = self.scene.weapon
        if w is None or self.timeline.running:
            return
        self.push_undo()
        s, left = self.scene, self.scene.left
        r = w.rig
        has_mag = bool(r.get('magazine'))
        cover = r.get('hingeKind') in ('FeedCover', 'BreakAction')  # break-action barrels open to load too (review I7#1)
        rack = self._has_rack()
        if not has_mag and not cover and not rack:
            self.toast('Nothing to reload on this weapon', 'The analysis found no magazine, cover or slide that moves.')
            return
        # The Empty gun switch under Reload (real2 I7#2): the gun starts as after its last shot, its slide caught back, so the
        # locked reload below runs. Only a rig whose slide locks on empty (or en bloc) takes it; the slider is set here.
        if rack and self.empty_sw.isChecked() and (r.get('lockOnEmpty') or r.get('enBloc')):
            w.set_state(slide=1.0)
            self.sync_sliders()
        steps = []
        # An empty gun's slide is caught back (lockOnEmpty), and an en-bloc rifle takes its clip only with the bolt back: the
        # reload starts with the slide back, and ends with it released (review I7#2, I7#6).
        locked = rack and (bool(r.get('enBloc')) or (bool(r.get('lockOnEmpty')) and w.slide > 0.5))
        if locked and w.slide < 0.99:
            steps.append(self._move_hand(left, lambda: s.snap(left, 'rack')))
            steps.append(self._follow_ease(left, lambda: s.snap(left, 'rack'), 'slide', 1.0, 0.35))
        if cover:
            steps.append(self._move_hand(left, lambda: s.snap(left, 'cover')))
            steps.append(self._follow_ease(left, lambda: s.snap(left, 'cover'), 'cover', 1.0, 0.5))
        if has_mag:
            steps.append(self._move_hand(left, lambda: s.snap(left, 'magazine')))
            steps.append(self._follow_ease(left, lambda: s.snap(left, 'magazine'), 'mag', 1.0, 0.55))

            dip = {}

            def hide_old():
                dip['from'] = left.position.copy()
                for n in w.nodes:
                    if n.role == 'magazine':
                        n.away = True  # (not hidden: the fresh one, drawn from the same parts, is drawn while it goes in)
            # The hand with the old magazine dips away, 5 cm over the step (per frame it sank 7.6 cm at 60 fps, more when faster).
            steps.append((0.3, lambda t: setattr(left, 'position', dip['from'] + np.array([0, -0.05 * t, 0])), hide_old))

            def fresh_at(t):
                w.fresh = w.fresh_matrix(1.0 - t)  # from where its parts wait, on the seat's line (review B2#0, B5#2)
                w.mag = 1.0 - t
                self.sync_sliders()

            def fresh_step(t):
                fresh_at(t)
                to_p, to_q = left.position.copy(), left.rotation.copy()
                s.snap(left, 'magazine')
                # The hand comes back up from its dip onto the fresh magazine over the first third (it snapped up 5 cm).
                k = min(1.0, t * 3.0)
                if k < 1.0:
                    left.position = to_p + (left.position - to_p) * k
                    left.rotation = quat_slerp(to_q, left.rotation, k)
            steps.append((0.6, fresh_step, lambda: fresh_at(0)))

            def seated():
                w.fresh = None
                for n in w.nodes:
                    n.away = False  # (a part the user hid stays hidden)
                w.set_state(mag=0)
                self.sync_sliders()
            steps.append((0.05, lambda t: None, seated))
        if cover:
            steps.append(self._move_hand(left, lambda: s.snap(left, 'cover')))
            steps.append(self._follow_ease(left, lambda: s.snap(left, 'cover'), 'cover', 0.0, 0.45))
        if rack:
            steps.append(self._move_hand(left, lambda: s.snap(left, 'rack')))
            if locked:
                steps.append(self._follow_ease(left, lambda: s.snap(left, 'rack'), 'slide', 0.0, 0.18))  # released: it runs home
            else:
                steps.append(self._follow_ease(left, lambda: s.snap(left, 'rack'), 'slide', 1.0, 0.35))
                steps.append(self._ease('slide', 0.0, 0.18))
        elif w.slide > 0.001:
            steps.append(self._ease('slide', 0.0, 0.18))
        steps.append(self._move_hand(left, lambda: s.snap(left, 'support')))
        self.timeline.run(steps)

    def play_rack(self):
        w = self.scene.weapon
        if w is None or self.timeline.running:
            return
        if not self._has_rack():
            self.toast('Nothing to rack on this weapon', 'The analysis found no slide, bolt or handle that moves.')
            return
        self.push_undo()
        s, left = self.scene, self.scene.left
        self.timeline.run([self._move_hand(left, lambda: s.snap(left, 'rack')),
                           self._follow_ease(left, lambda: s.snap(left, 'rack'), 'slide', 1.0, 0.4),
                           self._ease('slide', 0.0, 0.2),
                           self._move_hand(left, lambda: s.snap(left, 'support'))])

    # ---- undo / redo, unsaved work ---------------------------------------------------------------------------------------

    _HAND_KEYS = ('position', 'rotation', 'pose_name', 'thumb', 'curls', 'on_part', 'role', 'thumb_set', 'settle', '_settled_at')

    def snapshot(self):
        w = self.scene.weapon
        if w is None:
            return None

        def copy(v):
            if isinstance(v, np.ndarray):
                return v.copy()
            if isinstance(v, (list, tuple)):
                return type(v)(copy(x) for x in v)
            return v
        return {'weapon': w.id, 'hands': [{k: copy(getattr(h, k)) for k in self._HAND_KEYS} for h in self.scene.hands()],
                'parts': (w.slide, w.mag, w.cover), 'hidden': [n.hidden for n in w.nodes],
                'place': w.P.copy() if hasattr(w, 'P') else None}

    def restore(self, snap):
        w = self.scene.weapon
        if w is None or snap is None or snap['weapon'] != w.id:
            return
        if snap.get('place') is not None and hasattr(w, 'set_place'):
            w.set_place(snap['place'])
        for h, state in zip(self.scene.hands(), snap['hands']):
            for k, v in state.items():
                setattr(h, k, v.copy() if isinstance(v, np.ndarray) else (list(v) if isinstance(v, list) else v))
            h.update_mesh()
        w.fresh = None
        for n, hidden in zip(w.nodes, snap['hidden']):
            n.hidden, n.away = hidden, False
        w.set_state(*snap['parts'])
        self.sync_sliders()
        self.on_select(self.current_hand())
        self.view.update()

    def push_undo(self):
        """The scene as it is now, kept before an edit (Ctrl+Z goes back to it)."""
        s = self.snapshot()
        if s is None:
            return
        self.undo_stack.append(s)
        del self.undo_stack[:-100]
        self._redo_before = list(self.redo_stack)  # (a drag that changes nothing gives the redo history back)
        self.redo_stack.clear()

    def _drag_starting(self):
        """A hand or the gun is about to be dragged: a reload or rack still playing is run to its end first (it moved the
        dragged hand on every tick, and undo then went back to a half-played reload; review C2), then the undo step."""
        self.settle_timeline()
        self.push_undo()

    def undo(self):
        self.settle_timeline()
        if not self.undo_stack:
            self.toast('Nothing to undo', '')
            return
        self.redo_stack.append(self.snapshot())
        self.restore(self.undo_stack.pop())

    def redo(self):
        self.settle_timeline()
        if not self.redo_stack:
            self.toast('Nothing to redo', '')
            return
        self.undo_stack.append(self.snapshot())
        self.restore(self.redo_stack.pop())

    def unsaved(self):
        """The hands differ from the saved pose (or were changed at all, when none is saved)."""
        w = self.scene.weapon
        if w is None:
            return False
        if self.saved_doc is None:
            return bool(self.undo_stack) or getattr(self, 'opened_with_draft', False)
        try:
            saved = self.scene.read_pose_document(self.saved_doc)
        except Exception:  # noqa: BLE001
            return True
        for side, h in (('right', self.scene.right), ('left', self.scene.left)):
            s = saved['hands'].get(side)
            if s is None:
                return True
            p = w.to_gun(h.position)[:3]
            q = quat_norm(quat_mul(quat_inv(w.Wq), h.rotation))
            if (np.linalg.norm(p - s['position']) > 1e-4 or 1.0 - abs(float(np.dot(q, s['rotation']))) > 1e-6
                    or max(abs(a - b) for a, b in zip(h.curls, s['curls'])) > 1e-3 or abs(h.thumb - s['thumb']) > 1e-3):
                return True
        return False

    def mark_draft(self, wid, on):
        """A weapon in the list whose hands were changed and not saved shows it."""
        for it in self.items:
            if it.data(Qt.ItemDataRole.UserRole) == wid:
                text = it.text().replace('   • not saved', '')
                it.setText(text + ('   • not saved' if on else ''))

    def revert_to_saved(self):
        if self.scene.weapon is None:
            return
        if self.saved_doc is None:
            self.toast('No saved pose for this weapon', 'Hands on the gun puts them where the mod does.')
            return
        self.settle_timeline()
        self.push_undo()
        try:
            self.scene.apply_pose_document(self.saved_doc)
        except Exception as e:  # noqa: BLE001
            self.toast('The saved pose could not be read', str(e), error=True)
            return
        self.sync_sliders()
        self.on_select(self.current_hand())
        self.view.update()

    def settle_timeline(self):
        """A reload or rack playing is run to its end at once (what is saved or exported is its end state)."""
        if self.timeline.running:
            self.timeline.finish()

    def _timeline_done(self):
        self.sync_sliders()
        self.on_select(self.current_hand())

    def on_part_chosen(self, item, _prev=None):
        w = self.scene.weapon
        if w is None or item is None:
            return
        nid = item.data(Qt.ItemDataRole.UserRole)
        node = next((n for n in w.nodes if id(n) == nid), None)
        self.view.part_node = node
        self.view.update()

    def show_all_parts(self):
        if self.scene.weapon:
            self.push_undo()
            for n in self.scene.weapon.nodes:
                n.hidden = False
            self.view.update()

    # ---- the laser ---------------------------------------------------------------------------------------------------------

    def set_laser(self, on):
        self.view.laser = on
        self.scene.laser_hand = self.scene.right if on else None
        # With the laser on, a press picks a part and does not drag a hand: the hints say so, and how to get the drag back
        # (real2 I1#2).
        self.laser_hint.setText('Laser on: clicks pick parts; press L or this button to drag hands freely again.' if on
                                else self.laser_hint_text)
        self.hands_hint.setText('Laser on: clicks pick parts, so a hand cannot be dragged freely (its gizmo still moves it). '
                                'Press L or the Laser pointer button to drag hands freely again.' if on
                                else self.hands_hint_text)
        if not on:
            # Turning the laser off takes its pick with it: the readout, the pick line and Copy coordinates go, so a right click
            # afterwards does not copy the part the laser picked (real2 I9#0). (An Alt + click with the laser off picks too, and
            # that pick stays, readout shown, until the next pick or an Alt + click on empty space.) The hover label drops the
            # part's name too.
            self.view.pick_point = None
            self.view.pick_line = None
            self.view.pick_normal = None
            self.on_pick(None, None)
            if self.view.hover_node is not None:
                self.view.hover_node = None
                self.view.hovered.emit('')
        self.view.refresh_cursor()  # (the cross with the laser on, at once)
        self.view.update()

    def on_pick(self, point, node):
        self.picked_point, self.picked_node = point, node
        # Kept on the part it was taken on, in the part's own frame: the gun moved or turned (review C1), a slider moved the
        # part, or Ctrl+Z or Reset put either back, and the point goes with it. Kept in gun space only, Copy coordinates gave
        # numbers off by the move after an undo or a slider, while the readout still showed the pick's (review of the
        # sandbox extras).
        w = self.scene.weapon
        self._picked_gun = None
        if point is not None and w is not None:
            try:
                inv = np.linalg.inv(node.sim) if node is not None else np.eye(4)
            except np.linalg.LinAlgError:
                inv = np.eye(4)
            n = self.view.pick_normal
            self._picked_gun = ((inv @ np.append(w.to_gun(point)[:3], 1.0))[:3],
                                inv[:3, :3] @ (np.linalg.inv(w.W[:3, :3]) @ n) if n is not None else None)
        if point is None:
            self.readout.hide()
            return
        self.readout.setFixedWidth(min(560, max(320, self.view.width() - 24)))
        self._show_readout()
        self.readout.show()
        self.readout.raise_()

    def _show_readout(self):
        """The readout card's text for the pick as it is now, sized and kept at the view's bottom left."""
        self.readout_text.setText(self.point_text(rich=True))
        self.readout.adjustSize()
        self.readout.move(12, self.view.height() - self.readout.height() - 12)

    def _pick_follows(self):
        """The gun moved or turned, the part the pick is on moved, or a hand moved: the laser's point (its line's end and the
        surface's normal) goes with its part, and the readout says where it is now and which hand is nearest (its hand line
        stayed as at the pick, review of the sandbox extras)."""
        w = self.scene.weapon
        held = getattr(self, '_picked_gun', None)
        if w is None or held is None or self.picked_point is None or self.picked_node is None:
            return
        sim = self.picked_node.sim
        self.picked_point = w.to_world((sim @ np.append(held[0], 1.0))[:3])
        v = self.view
        v.pick_point = self.picked_point
        if held[1] is not None:
            n = w.dir_to_world(sim[:3, :3] @ held[1])
            v.pick_normal = n / max(1e-12, float(np.linalg.norm(n)))
        if v.pick_line is not None:
            v.pick_line = [v.pick_line[0], self.picked_point]
        if not self.readout.isHidden():  # (shown, also while another page is in front)
            self._show_readout()

    def point_text(self, rich=False):
        w = self.scene.weapon
        n = self.picked_node
        # The same point on the part at rest: the point in the part's own frame (kept at the pick, on_pick) out to the part's
        # home (a magazine parked away at rest is drawn on its seat: its home; taken back to the raw export, the M1's clip read
        # 1 m off, real2 B2#3).
        home = w.home_matrix(n)
        moved = not np.allclose(n.sim, home, atol=1e-6)
        held = getattr(self, '_picked_gun', None)
        if held is not None:
            rest = (home @ np.append(held[0], 1.0))[:3]
        else:
            g = w.to_gun(self.picked_point)[:3]
            try:
                rest = (home @ np.linalg.solve(n.sim, np.append(g, 1.0)))[:3] if moved else g
            except np.linalg.LinAlgError:
                rest = g
        b, u, r = w.from_muzzle(rest)
        x, y, z = Weapon.unity(rest)
        role = ROLE_NAMES.get(n.role, n.role)
        name = n.name[len('[stand-in] '):] if n.stand_in else n.name
        ref = 'from the muzzle' if w.muzzle_known else "from the gun's front (no muzzle in the export)"
        state = []
        if w.slide > 0.001:
            state.append(f'slide {w.slide * 100:.0f}% back')
        if w.mag > 0.001:
            state.append(f'magazine {w.mag * 100:.0f}% out')
        if w.cover > 0.001:
            state.append(f'cover {w.cover * 100:.0f}% open')
        note = (f" (the part at rest; it is moved now: {', '.join(state)})" if moved and state else '')
        # The hand nearest the picked point, by its palm centre (the place hands_text gives), and how far it is (real2 I8#0).
        hand = min(self.scene.hands(), key=lambda h: float(np.linalg.norm(h.position - self.picked_point)))
        hand_cm = float(np.linalg.norm(hand.position - self.picked_point)) * 100
        laser = ' (the laser hand)' if self.scene.laser_hand is hand else ''
        hand_line = f'Nearest hand: {hand.side} hand{laser}, palm centre {hand_cm:.1f} cm from the picked point'
        if rich:
            return (f"<b>{n.short}</b> — {role}{' (grey stand-in box)' if n.stand_in else ''}<br>"
                    f"back <b>{b:.1f}</b> · up <b>{u:.1f}</b> · right <b>{r:.1f}</b> cm {ref}{note}<br>"
                    f"{hand_line}<br>"
                    f"<span style='color:gray'>gun space x {x:.4f} y {y:.4f} z {z:.4f}</span>")
        return (f"{w.name} ({w.id}): '{name}' ({role}) at back {b:.1f} up {u:.1f} right {r:.1f} cm {ref}{note} "
                f"(mod gun space x {x:.4f} y {y:.4f} z {z:.4f})\n{hand_line}")

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
            if self.scene.weapon is not None:
                menu.addAction(Action(FIF.PIN, 'Put the left hand here', triggered=lambda: self.put_hand_here(self.scene.left)))
                menu.addAction(Action(FIF.PIN, 'Put the right hand here', triggered=lambda: self.put_hand_here(self.scene.right)))
            menu.addSeparator()
        menu.addAction(Action(FIF.VIEW, 'Show every part', triggered=self.show_all_parts))
        menu.addAction(Action(FIF.FINGERPRINT, 'Hands on the gun', triggered=self.hands_on_gun))
        if self.scene.weapon is not None:
            # The same text as the Save and share button (real2 I8#0): the weapon and both hands, for Claude.
            menu.addAction(Action(FIF.ROBOT, 'Copy pose as text', triggered=self.copy_hands_text))
            menu.addAction(Action(FIF.RETURN, "Reset the weapon's place", triggered=self.reset_place))
        menu.addAction(Action(FIF.ZOOM, 'Frame the gun and both hands  (F)', triggered=lambda: self.frame_weapon()))
        menu.addAction(Action(FIF.ZOOM, 'Frame the gun alone', triggered=lambda: self.frame_weapon(hands=False)))
        menu.exec(pos)

    def put_hand_here(self, hand):
        """The hand's palm onto the part the laser picked, facing it (undo: Ctrl+Z); the part is named on the hand."""
        if self.scene.weapon is None or self.picked_point is None:
            return
        self.push_undo()
        normal = getattr(self.view, 'pick_normal', None)  # the surface's normal at the pick, when the view has one
        if normal is None:
            normal = self.view.eye() - self.picked_point  # else the palm faces the camera
        self.scene.put_hand_on(hand, self.picked_point, normal)
        if self.picked_node is not None:
            hand.on_part = self.picked_node.short  # (the label and the saved pose name the part, as a snap does)
        self.select_hand(hand)

    def hands_text(self):
        """The two hands in words for Claude: each hand's job, the part it is on, its palm's place (cm from the grip and from
        the muzzle), its turn against the gun (degrees), its pose, thumb base and curls."""
        w = self.scene.weapon
        if w is None:
            return ''
        ref = 'from the muzzle' if w.muzzle_known else "from the gun's front (no muzzle in the export)"
        state = f'slide {w.slide * 100:.0f}% back, magazine {w.mag * 100:.0f}% out, cover {w.cover * 100:.0f}% open'
        lines = [f'{w.name} ({w.id}); parts: {state}. Places in cm (the palm centre), turns in degrees (pitch, yaw, roll) '
                 'against the gun.']
        jobs = {'grip': 'grip', 'support': 'support (handguard)', 'rack': 'rack (slide or handle)', 'magazine': 'magazine',
                'cover': 'cover'}
        for h in self.scene.hands():
            p = w.to_gun(h.position)[:3]
            g = p - w.grip
            fwd, up, right = (float(np.dot(g, a)) * 100 for a in (w.aim, w.up, w.right))
            back, mup, mright = w.from_muzzle(p)
            pitch, yaw, roll = quat_to_euler_deg(quat_norm(quat_mul(quat_inv(w.Wq), h.rotation)))
            part, dist = w.nearest_part(p)
            bits = [f"job: {jobs.get(h.role, 'none')}"]
            if h.on_part:
                bits.append(f'snapped to the {h.on_part}')
            if part is not None:
                bits.append(f"nearest part '{part.short}' ({ROLE_NAMES.get(part.role, part.role)}), {dist * 100:.1f} cm away")
            label = f"{h.side.capitalize()} hand" + (' (the laser hand)' if self.scene.laser_hand is h else '')
            lines.append(f"{label}: " + '; '.join(bits) + '.')
            lines.append(f"  Palm {fwd:.1f} cm forward, {up:.1f} up, {right:.1f} right of the grip; "
                         f"{back:.1f} back, {mup:.1f} up, {mright:.1f} right {ref}.")
            # (int(round()) keeps a turn of nothing from printing as -0)
            turn = ', '.join(f'{name} {int(round(v))}' for name, v in (('pitch', pitch), ('yaw', yaw), ('roll', roll)))
            curls = ', '.join(f'{finger} {c:.2f}' for finger, c in zip(FINGERS, h.curls))
            lines.append(f"  Turn against the gun: {turn} degrees. Pose: {h.pose_name}; thumb base {h.thumb:.2f} "
                         f"(0 rest, 1 fist); curls {curls}.")
        return '\n'.join(lines)

    def copy_hands_text(self):
        text = self.hands_text()
        if not text:
            # (The button is there with no weapon open too: it said nothing and left the old clipboard, review of the extras.)
            self.toast('Nothing copied', 'No weapon is open: open one, then Copy pose as text copies its hands.', warn=True)
            return
        QGuiApplication.clipboard().setText(text)
        self.toast('Copied', 'The hands and where they are on the gun are on the clipboard: paste them to Claude.')

    def reset_place(self):
        """The weapon back to where it was opened (the hands on it go with it, so their poses stay)."""
        w = self.scene.weapon
        if w is None:
            return
        if np.allclose(w.P, np.eye(4)):
            self.toast('The weapon is already in its place', '')
            return
        self.push_undo()
        self.scene.move_weapon(np.linalg.inv(w.P))
        self._pick_follows()  # (the gun went back without a drag: weapon_moved is not sent)
        self.sync_numbers()
        self.view.update()

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
        self.view.pick_normal = None  # (not measured here: put-a-hand then faces the camera)
        self.view.pick_line = [self.scene.laser_origin(), point]
        self.on_pick(point, node)

    def hide_picked(self):
        if self.picked_node is not None:
            self.push_undo()
            self.picked_node.hidden = True
            self.view.update()

    # ---- saving ------------------------------------------------------------------------------------------------------------

    DEFAULT_GAME = os.path.join(os.environ.get('ProgramFiles(x86)', r'C:\Program Files (x86)'), 'Steam', 'steamapps', 'common', 'Ravenfield')

    @staticmethod
    def steam_libraries():
        """Steam's library folders, Steam's own first: its path from the registry (else the default place), then the "path"
        values of its libraryfolders.vdf, found the way tools/common.ps1 finds them. A missing or odd file leaves them out."""
        steam = None
        try:
            import winreg
            # (The keys tools/common.ps1 and the installer read, in their order: the user's, then the machine's.)
            keys = ((winreg.HKEY_CURRENT_USER, r'Software\Valve\Steam'), (winreg.HKEY_LOCAL_MACHINE, r'SOFTWARE\WOW6432Node\Valve\Steam'),
                    (winreg.HKEY_LOCAL_MACHINE, r'SOFTWARE\Valve\Steam'))
            for hive, sub in keys:
                for name in ('SteamPath', 'InstallPath'):
                    try:
                        with winreg.OpenKey(hive, sub) as key:
                            steam = str(winreg.QueryValueEx(key, name)[0]) or None
                    except OSError:
                        continue
                    if steam:
                        break
                if steam:
                    break
        except ImportError:
            steam = None  # (not Windows)
        steam = os.path.normpath(steam or os.path.join(os.environ.get('ProgramFiles(x86)', r'C:\Program Files (x86)'), 'Steam'))
        libs = [steam]
        try:
            with open(os.path.join(steam, 'steamapps', 'libraryfolders.vdf'), encoding='utf-8', errors='replace') as f:
                text = f.read()
        except OSError:
            text = ''
        for raw in re.findall(r'"path"\s+"([^"]+)"', text):
            lib = os.path.normpath(raw.replace('\\\\', '\\'))  # (the file doubles each backslash)
            if os.path.normcase(lib) not in {os.path.normcase(x) for x in libs}:
                libs.append(lib)
        return libs

    def game_folder(self):
        """Ravenfield's folder: the one chosen in Advanced (Game folder...), else the first Steam library that holds it (every
        library, not only Steam's default one: real2 B8#6), else Steam's default. The self-test always takes the default."""
        chosen = '' if self.selftest else str(self.settings.value('gameFolder', '') or '')
        if chosen and os.path.isdir(chosen):
            return chosen
        if not self.selftest:  # (the self-test does not look at the user's Steam libraries)
            for lib in self.steam_libraries():
                folder = os.path.join(lib, 'steamapps', 'common', 'Ravenfield')
                if os.path.isfile(os.path.join(folder, 'ravenfield.exe')):
                    return folder
        return self.DEFAULT_GAME

    def game_poses_dir(self):
        """Where the mod reads saved poses (BepInEx/config/RavenfieldVR/poses), or None when the game (with the mod) is not found."""
        if self.game_dir_override is not None:
            return self.game_dir_override
        cfg = os.path.join(self.game_folder(), 'BepInEx', 'config')
        return os.path.join(cfg, 'RavenfieldVR', 'poses') if os.path.isdir(cfg) else None

    def choose_game_folder(self):
        from PyQt6.QtWidgets import QFileDialog
        chosen = QFileDialog.getExistingDirectory(self, 'Where is Ravenfield? (the folder with ravenfield.exe and BepInEx)', self.game_folder())
        if not chosen:
            return
        if not os.path.isdir(os.path.join(chosen, 'BepInEx')):
            self.toast('That folder has no BepInEx', 'Pick the Ravenfield folder the mod is installed in (it holds ravenfield.exe '
                       'and BepInEx).', error=True)
            return
        self.settings.setValue('gameFolder', chosen)
        self.game_label.setText(chosen)
        self.update_game_status()

    def pose_path(self):
        """This weapon's pose file in the repository (named as the export names the weapon: the mod looks for that name)."""
        w = self.scene.weapon
        return os.path.join(self.poses_dir, w.id + '.json') if w else None

    @staticmethod
    def _write(path, doc):
        """Written to a temporary file and moved into place: an error part way never leaves a cut file."""
        os.makedirs(os.path.dirname(path), exist_ok=True)
        tmp = path + '.tmp'
        with open(tmp, 'w', encoding='utf-8') as f:
            json.dump(doc, f, indent=1)
        os.replace(tmp, path)

    def keep_old(self, path, why=''):
        """A pose file about to be overwritten or removed is copied to tools/sandbox/poses/history first (the last 30 per
        weapon are kept): a pose saved over by mistake can be got back. None when there is nothing to keep; raises OSError
        when the copy could not be made, so the caller leaves the file as it is (it went on and overwrote or removed it
        without its copy: review of the sandbox extras)."""
        if not path or not os.path.exists(path):
            return None
        import shutil
        hist = os.path.join(self.poses_dir, 'history')
        base = os.path.splitext(os.path.basename(path))[0]
        where = 'game' if os.path.normcase(os.path.dirname(path)) != os.path.normcase(self.poses_dir) else 'saved'
        dst = os.path.join(hist, f'{base}--{where}{("-" + why) if why else ""}--{stamp()}.json')
        try:
            os.makedirs(hist, exist_ok=True)
            shutil.copy2(path, dst)
        except OSError as e:
            self.log_error(f'keeping the old pose {path}', e)
            try:
                if os.path.exists(dst):
                    os.remove(dst)  # (a copy cut off half way is no copy)
            except OSError:
                pass
            raise OSError(f'no copy of the old pose could be kept in {hist}, so it was left as it is: {e}') from e
        try:  # (the oldest copies beyond 30 go; one that cannot go only stays)
            old = sorted((f for f in os.listdir(hist) if f.startswith(base + '--')), key=lambda f: os.path.getmtime(os.path.join(hist, f)))
            for f in old[:-30]:
                os.remove(os.path.join(hist, f))
        except OSError as e:
            self.log_error(f'clearing old copies of {base} in {hist}', e)
        return dst

    def save_pose(self):
        """Saves the hands for this weapon, in the repository and for the game (the mod holds the weapon that way)."""
        w = self.scene.weapon
        if w is None:
            return None
        self.settle_timeline()  # (a reload playing: its end state, not the hands half way)
        warnings = self.scene.pose_warnings()
        if warnings and not self.selftest:
            box = MessageBox('Save this pose?', '\n\n'.join(warnings) + '\n\nSave it anyway?', self.win)
            if not box.exec():
                return None
        path = self.pose_path()
        doc = self.scene.pose_document()
        try:
            self.keep_old(path)
            self._write(path, doc)
        except OSError as e:
            self.toast('The pose could not be saved', f'{path}\n{e}', error=True)
            return None
        text = path
        game = self.game_poses_dir()
        if game:
            gpath = os.path.join(game, w.id + '.json')
            try:
                self.keep_old(gpath)
                self._write(gpath, doc)
                text += '\nand for the game: ' + gpath
            except OSError as e:
                self.toast('Saved here, but not for the game', f'{game}\n{e}', error=True)
        self.saved_doc = doc
        self.drafts.pop(w.id, None)
        self.mark_saved(w.id)
        self.update_game_status()
        self.toast('Pose saved', text + ('\n' + '\n'.join(warnings) if warnings else ''), path)
        return path

    def mark_saved(self, wid):
        for it in self.items:
            if it.data(Qt.ItemDataRole.UserRole) == wid and '✓ pose' not in it.text():
                it.setText(it.text() + '   ✓ pose')

    def import_pose(self, path=None):
        """A pose file from anywhere (one Claude sent, one exported on another PC) put on this weapon. Save pose keeps it."""
        w = self.scene.weapon
        if w is None:
            return False
        if not path:
            from PyQt6.QtWidgets import QFileDialog
            path, _ = QFileDialog.getOpenFileName(self, 'Import a pose', self.poses_dir, 'Pose files (*.json)')
            if not path:
                return False
        self.settle_timeline()
        try:
            with open(path, encoding='utf-8') as f:
                doc = json.load(f)
            checked = dict(self.scene.read_pose_document(doc), _checked=True)  # all checked before anything changes
        except Exception as e:  # noqa: BLE001 (a file from anywhere: any error is "could not import")
            self.toast('Could not import the pose', f'{path}\n{e}', error=True)
            return False
        self.push_undo()
        self.scene.apply_pose_document(checked)
        self.sync_sliders()
        self.on_select(self.current_hand())
        self.view.update()
        made_for = str(doc.get('weapon', '') or '')
        note = '' if made_for in ('', w.id) else f'\nIt was made for {doc.get("name", made_for)}: check it fits this gun.'
        self.toast('Pose imported', f'{path}{note}\nPress Save pose to keep it for this weapon (and the game).')
        return True

    def export_pose(self, path=None):
        """The current pose as a file anywhere (to share it, or to send it to Claude)."""
        w = self.scene.weapon
        if w is None:
            return None
        if not path:
            from PyQt6.QtWidgets import QFileDialog
            start = os.path.join(os.path.expanduser('~'), 'Documents', w.id + '-pose.json')
            path, _ = QFileDialog.getSaveFileName(self, 'Export the pose', start, 'Pose files (*.json)')
            if not path:
                return None
        self.settle_timeline()
        try:
            self._write(path, self.scene.pose_document())
        except OSError as e:
            self.toast('The pose could not be exported', f'{path}\n{e}', error=True)
            return None
        self.toast('Pose exported', path, path)
        return path

    def send_to_game(self):
        """This weapon's saved pose copied to the game (after an import, or to put a removed one back)."""
        w = self.scene.weapon
        game = self.game_poses_dir()
        if w is None:
            return
        if game is None:
            self.toast('The game was not found', self.game_not_found(), error=True)
            return
        src = self.pose_path()
        if not os.path.exists(src):
            self.toast('No saved pose for this weapon yet', 'Press Save pose first.', error=True)
            return
        try:
            with open(src, encoding='utf-8') as f:
                doc = json.load(f)
            self.keep_old(os.path.join(game, w.id + '.json'))
            self._write(os.path.join(game, w.id + '.json'), doc)
        except (OSError, ValueError) as e:
            self.toast('Could not send it to the game', f'{w.id}: {type(e).__name__}: {e}\n{os.path.join(game, w.id + ".json")}',
                       error=True)  # (the weapon and the file, not only the bare message: real2 I10#4)
            return
        self.update_game_status()
        self.toast('Sent to the game', os.path.join(game, w.id + '.json'))

    def remove_from_game(self):
        """The game holds this weapon the mod's own way again (the pose stays saved here)."""
        w = self.scene.weapon
        if w is None:
            return
        game = self.game_poses_dir()
        if game is None:  # (it said nothing at all: review of the sandbox extras)
            self.toast('The game was not found', self.game_not_found(), error=True)
            return
        target = os.path.join(game, w.id + '.json')
        if not os.path.exists(target):
            self.update_game_status()
            self.toast('Nothing to remove', 'The game has no pose for this weapon: it already holds it the mod\'s own way.', warn=True)
            return
        try:
            self.keep_old(target, 'removed')
            os.remove(target)
        except OSError as e:
            self.toast('Could not remove it from the game', f'{w.id}: {type(e).__name__}: {e}\n{target}', error=True)  # (real2 I10#4)
            return
        self.update_game_status()
        self.toast('Removed from the game', 'The game holds this weapon the mod\'s own way again; the pose is still saved here.')

    def game_not_found(self):
        """Where the game was looked for, for the messages that say it was not found."""
        return (f'No BepInEx/config folder in {self.game_folder()} (the mod is not installed there, or Ravenfield is elsewhere). '
                'Advanced → Game folder… points to it.')

    def update_game_status(self):
        w = self.scene.weapon
        if w is None:
            self.game_status.setText('')
            return
        game = self.game_poses_dir()
        saved = os.path.exists(self.pose_path())
        if game is None:
            text = f'The game (with the mod) was not found in {self.game_folder()}: poses are saved here only. Advanced → Game folder… to point to it.'
        elif os.path.exists(os.path.join(game, w.id + '.json')):
            text = 'In the game: this weapon is held with your saved pose (from the next time it is analysed: restart or reload).'
        else:
            text = 'In the game: the mod\'s own grip' + (' (your saved pose is not sent: Send to game).' if saved else '.')
        self.game_status.setText(text)

    def screenshot(self, clean=None):
        """A picture of the view as it is now (F12 or the Screenshot button). clean=None takes the 'Clean picture' switch; the
        size box draws the same camera again at 2x or 4x, for a post, and the transparent switch keeps the background
        see-through (PNG only)."""
        w = self.scene.weapon
        if clean is None:
            clean = self.clean_sw.isChecked()
        asked = self.picture_scale()
        transparent = self.transp_sw.isChecked()
        img, got, see_through = self._picture(clean, asked, transparent)
        path = os.path.join(self.captures, f"{safe(w.name if w else 'sandbox')}-{stamp()}.png")
        if not img.save(path):
            self.toast('The screenshot could not be saved', path, error=True)
            return None
        self._write_notes(path)
        note = ''
        if got < asked:
            note += f'\n{asked}x is more than this graphics card can draw: {got}x was saved.'
        if transparent and not see_through:
            note += '\nThe background is not see-through: this graphics card could not draw the picture into a buffer.'
        self.toast('Screenshot saved', path + note, path)
        return path

    SIZES = (1, 2, 4)  # the Screenshot size box, in its order: the view's own size, twice it, four times it

    def picture_scale(self):
        return self.SIZES[max(0, self.size_box.currentIndex())]

    def _picture(self, clean, scale, transparent):
        """(the picture, the scale it has, whether its background is see-through). The size asked for first, then each smaller
        one: a buffer the graphics card cannot hold (or will not draw) gives way to the next size down. The view's own grab is
        the last resort, and is opaque."""
        for s in [x for x in self.SIZES if x <= scale][::-1]:
            if s == 1 and not transparent:
                break  # (the view's own grab, below, needs no buffer)
            img = self._grab_big(clean, s, transparent)
            if img is not None:
                return img, s, transparent
        return self._grab_view(clean), 1, False

    def on_clean_check(self, on):
        """The Easy tab's 'Clean picture' and Advanced's switch are one setting: each follows the other."""
        self.clean_sw.blockSignals(True)  # (no echo back to the box that changed)
        self.clean_sw.setChecked(on)
        self.clean_sw.blockSignals(False)

    def on_clean_switch(self, on):
        self.clean_check.blockSignals(True)
        self.clean_check.setChecked(on)
        self.clean_check.blockSignals(False)

    def picture_notes(self, turned=False):
        """The text saved beside a picture (the same name, .txt): the weapon and its hands in words (hands_text), the view, and
        the laser's point when one is picked, so a picture pasted into Claude comes with what it shows (real2 I8#1)."""
        w = self.scene.weapon
        v = self.view
        lines = [self.hands_text() if w is not None else 'No weapon was open.']
        lines.append(f"View: yaw {v.yaw:.1f} deg, pitch {v.pitch:.1f} deg, distance {v.dist * 100:.0f} cm, "
                     + ('orthographic' if getattr(v, 'ortho', False) else 'perspective')
                     + (' (a turntable: the view turned once round; this is where it started)' if turned else '') + '.')
        if w is not None and self.picked_point is not None and self.picked_node is not None:
            lines.append('Laser point: ' + self.point_text())
        return '\n'.join(lines) + '\n'

    def _write_notes(self, path, turned=False):
        """The text beside a picture (picture_notes). A file that cannot be written is logged; the picture itself is kept."""
        try:
            with open(os.path.splitext(path)[0] + '.txt', 'w', encoding='utf-8') as f:
                f.write(self.picture_notes(turned))
        except OSError as e:
            self.log_error(f'writing the text beside {path}', e)

    # What a clean picture leaves out: the grid, the markers, the laser line, the hover and part highlights, the hand's tint and
    # the tint of the part the selected hand touches.
    OVERLAYS = ('show_grid', 'show_markers', 'pick_line', 'hover_node', 'part_node', 'selected_hand', 'show_gizmo', 'show_axes',
                'touch_node')
    TURN_SECONDS = 4.0  # the turntable GIF: one turn of the view

    def _overlays_off(self):
        """Turns the overlays off for a clean picture; returns what they were, for _overlays_back."""
        v = self.view
        saved = {k: getattr(v, k) for k in self.OVERLAYS}
        v.show_grid, v.show_markers, v.show_gizmo, v.show_axes = False, False, False, False
        v.pick_line, v.hover_node, v.part_node, v.selected_hand, v.touch_node = None, None, None, None, None
        return saved

    def _overlays_back(self, saved):
        for k, value in saved.items():
            setattr(self.view, k, value)

    def _grab_view(self, clean):
        """The view as a picture at its own size. A clean one has the overlays off for the grab only, put back after it."""
        if not clean:
            return self.view.grabFramebuffer()
        saved = self._overlays_off()
        try:
            return self.view.grabFramebuffer()
        finally:
            self._overlays_back(saved)

    def _grab_big(self, clean, scale=2, transparent=False):
        """The view drawn again into an offscreen buffer at scale times its size: the same camera, so the same picture, sharper.
        A transparent one is drawn over alpha 0 (Viewport.clear_alpha) and keeps its alpha. None when the buffer is bigger than
        the graphics card holds, or cannot be drawn (the caller then takes a smaller size)."""
        # (Imported here: these are only needed for a bigger picture.)
        from OpenGL import GL
        from PyQt6.QtCore import QSize
        from PyQt6.QtOpenGL import QOpenGLFramebufferObject, QOpenGLFramebufferObjectFormat
        v = self.view
        dpr = v.devicePixelRatioF()  # (the grab is in device pixels, so the scale is of those)
        size = QSize(int(round(v.width() * dpr * scale)), int(round(v.height() * dpr * scale)))
        fmt = QOpenGLFramebufferObjectFormat()
        fmt.setAttachment(QOpenGLFramebufferObject.Attachment.CombinedDepthStencil)  # the scene is drawn with depth testing
        fmt.setSamples(4)  # (the view's own antialiasing, so the big picture is not jagged)
        saved = self._overlays_off() if clean else None
        v.makeCurrent()
        prev_alpha = getattr(v, 'clear_alpha', 1.0)
        try:
            # The colour buffer is a texture and the depth one a renderbuffer: the bigger size must fit both limits.
            limit = min(int(np.ravel(GL.glGetIntegerv(GL.GL_MAX_TEXTURE_SIZE))[0]),
                        int(np.ravel(GL.glGetIntegerv(GL.GL_MAX_RENDERBUFFER_SIZE))[0]))
            if max(size.width(), size.height()) > limit:
                return None
            v.clear_alpha = 0.0 if transparent else 1.0  # (set for this grab only: the view itself stays opaque)
            # What was bound and the viewport, put back whatever the drawing does, so the view itself is unchanged. Read before
            # the buffer is made (making and binding it changes the binding: read after, the grab's own buffer was 'put back').
            prev_fbo = int(np.ravel(GL.glGetIntegerv(GL.GL_FRAMEBUFFER_BINDING))[0])
            prev_view = [int(x) for x in np.ravel(GL.glGetIntegerv(GL.GL_VIEWPORT))]
            fbo = QOpenGLFramebufferObject(size, fmt)
            try:
                if not fbo.isValid() or not fbo.bind():
                    return None
                GL.glViewport(0, 0, size.width(), size.height())
                v.paintGL()  # the same drawing as on screen, into the bigger buffer
                img = fbo.toImage()
            finally:
                fbo.release()
                GL.glBindFramebuffer(GL.GL_FRAMEBUFFER, prev_fbo)
                GL.glViewport(*prev_view)
                del fbo  # (its buffers freed here, while the view's context is still current, also when the drawing failed)
            # A transparent picture keeps its alpha; any other is opaque, as the view's own grab is.
            return img.convertToFormat(QImage.Format.Format_ARGB32 if transparent else QImage.Format.Format_RGB32)
        except Exception as e:  # (a graphics driver that refuses the buffer: a smaller picture is still taken)
            self.log_error(f'drawing the {scale}x screenshot', e)
            return None
        finally:
            v.clear_alpha = prev_alpha
            if saved is not None:
                self._overlays_back(saved)
            v.doneCurrent()

    def toggle_record(self, on):
        if on:
            self.recording = {'frames': [], 'times': [], 'bytes': 0, 'timer': QTimer(), 'scale': float(self.gif_scale.value()),
                              'fps': int(self.gif_fps.value()), 'clean': self.clean_sw.isChecked()}
            self.recording['timer'].timeout.connect(self._grab_frame)
            self.recording['timer'].start(int(1000 / self.recording['fps']))
            self.rec_btn.setText('Stop recording')
        else:
            rec, self.recording = self.recording, None
            self.rec_btn.setText('Record GIF')
            if not rec:
                return
            rec['timer'].stop()
            if 'turn_timer' in rec:  # a turntable: the camera goes back to where its turn began, however the GIF ended
                rec['turn_timer'].stop()
                self.view.yaw = rec['turn_yaw']
                self.view.update()
            if not rec['frames']:
                return
            w = self.scene.weapon
            path = os.path.join(self.captures, f"{safe(w.name if w else 'sandbox')}-{stamp()}.gif")
            frames = rec['frames']  # (quantized as they were grabbed)
            # Each frame as long as it really lasted, rounded on the running time to the GIF's centiseconds (each frame
            # rounded down on its own played about 10% fast).
            t = rec['times'] + [rec['times'][-1] + 1.0 / rec['fps']]
            cs = [int(round((x - t[0]) * 100)) for x in t]
            durations = [max(2, cs[i + 1] - cs[i]) * 10 for i in range(len(frames))]
            # The encode is one long call on this thread: the button says so, painted once before it, and is back after it.
            self.rec_btn.setText('Saving GIF...')
            self.rec_btn.setEnabled(False)
            QApplication.processEvents()
            try:
                frames[0].save(path, save_all=True, append_images=frames[1:], duration=durations, loop=0, disposal=1)
            except (OSError, ValueError) as e:
                self.log_error(f'saving the GIF {path}', e)
                self.toast('The GIF could not be saved', f'{path}\n{e}', error=True)
                return
            finally:
                self.rec_btn.setText('Record GIF')
                self.rec_btn.setEnabled(True)
            self._write_notes(path, turned='turn_timer' in rec)
            self.last_gif = path
            self.toast(f'GIF saved ({len(frames)} frames, {sum(durations) / 1000:.1f} s)',
                       path + ('\nStopped at the 400 MB limit of the recording.' if rec.get('limit') else ''), path)

    def _grab_frame(self):
        rec = self.recording
        if rec is None:
            return
        if rec.get('reload'):
            # A reload's GIF runs until 0.3 s after its timeline stops, however it stopped (the end, Reset, another weapon
            # opened, or the end state taken for a save). A reload started again in that 0.3 s keeps the recording going.
            if self.timeline.running:
                rec['reload_end'] = None
            elif rec.get('reload_end') is None:
                rec['reload_end'] = time.perf_counter()
            elif time.perf_counter() - rec['reload_end'] >= 0.3:
                self.rec_btn.setChecked(False)
                return
        if rec['bytes'] > 400 * 1024 * 1024:
            # About 400 MB of frames kept (a big view at full size was 3 MB a frame, 2 GB at 600 frames): stopped and saved.
            rec['limit'] = True
            self.rec_btn.setChecked(False)
            return
        img = self._grab_view(rec.get('clean', False))
        if rec['scale'] < 0.999:
            img = img.scaledToWidth(max(64, int(img.width() * rec['scale'])), Qt.TransformationMode.SmoothTransformation)
        img = img.convertToFormat(img.Format.Format_RGB888)
        ptr = img.constBits()
        ptr.setsize(img.sizeInBytes())
        arr = np.frombuffer(ptr, np.uint8).reshape(img.height(), img.bytesPerLine())[:, :img.width() * 3].reshape(img.height(), img.width(), 3)
        # One palette for the whole GIF, made from its first frame: each frame's own made the same colours shimmer from
        # frame to frame (real2 I2#7, I5#1, I8#6).
        # (No copy of arr: quantize makes a new image, and img, which the buffer belongs to, lives until this function returns.)
        if rec.get('palette') is None:
            frame = Image.fromarray(arr).quantize(colors=256, method=Image.Quantize.MEDIANCUT, dither=Image.Dither.NONE)
            rec['palette'] = frame
        else:
            frame = Image.fromarray(arr).quantize(palette=rec['palette'], dither=Image.Dither.NONE)
        rec['frames'].append(frame)
        rec['times'].append(time.perf_counter())
        rec['bytes'] += frame.width * frame.height
        self.rec_btn.setText(f"Stop recording ({len(rec['frames'])})")

    def turntable_gif(self):
        """A GIF of the weapon turning once round in about 4 s: the view's yaw goes round with the clock while the recording
        runs, and the recording stops by itself at the end of the turn (the camera goes back to where it was)."""
        if self.recording is not None or self.timeline.running:
            self.toast('Already recording', 'Stop the GIF or the reload that is running first.', error=True)
            return
        self.rec_btn.setChecked(True)  # (starts the recording, as the Record GIF button does)
        rec = self.recording
        if rec is None:
            return
        rec['turn_yaw'] = self.view.yaw
        rec['turn_start'] = time.perf_counter()
        rec['turn_timer'] = QTimer()
        rec['turn_timer'].timeout.connect(self._turn_tick)
        rec['turn_timer'].start(16)  # about 60 steps a second, so the turn is smooth on screen and in the frames

    def _turn_tick(self):
        rec = self.recording
        if rec is None or 'turn_timer' not in rec:
            return
        frac = (time.perf_counter() - rec['turn_start']) / self.TURN_SECONDS
        if frac >= 1.0:
            self.rec_btn.setChecked(False)  # (ends the GIF; toggle_record stops this timer and puts the view back)
            return
        self.view.yaw = rec['turn_yaw'] + 360.0 * frac
        self.view.update()

    def record_reload(self):
        """Records one reload: the GIF starts with it and stops by itself 0.3 s after the reload's timeline ends."""
        if self.recording is not None or self.timeline.running:
            self.toast('Already recording', 'Stop the GIF or the reload that is running first.', error=True)
            return
        self.rec_btn.setChecked(True)  # (starts the recording, as the Record GIF button does)
        rec = self.recording
        if rec is None:
            return
        rec['reload'] = True
        self.play_reload()
        if not self.timeline.running:  # play_reload found nothing to reload (it said why): nothing to record either
            self.rec_btn.setChecked(False)

    def log_error(self, what, exc):
        """A handled error into sandbox-errors.log as well (the message on screen goes after a few seconds)."""
        import traceback
        try:
            main = sys.modules.get('__main__')
            log = main.error_log_path() if main is not None and hasattr(main, 'error_log_path') else None
            if log:
                with open(log, 'a', encoding='utf-8') as f:
                    f.write(f"--- {time.strftime('%Y-%m-%d %H:%M:%S')} {what}\n"
                            + ''.join(traceback.format_exception(type(exc), exc, exc.__traceback__)) + '\n')
        except OSError:
            pass

    def toast(self, title, content, path=None, error=False, warn=False):
        """A message at the bottom right: an error (red, also written to sandbox-errors.log), a warning (yellow: nothing went
        wrong, but nothing was done either) or a success."""
        if error and not self.selftest:
            # Every error shown is in sandbox-errors.log too (save, import, export, send: shown only; real2 I10#4).
            try:
                main = sys.modules.get('__main__')
                log = main.error_log_path() if main is not None and hasattr(main, 'error_log_path') else None
                if log:
                    with open(log, 'a', encoding='utf-8') as f:
                        f.write(f"--- {time.strftime('%Y-%m-%d %H:%M:%S')} {title}: {content}{(' (' + path + ')') if path else ''}\n")
            except OSError:
                pass
        if self.selftest:
            # (The self-test's messages say so: its deliberate bad file and error looked like real faults on screen.)
            expected = getattr(self, 'expect_error', '')
            title = 'Self-test: ' + title + (f' (expected: {expected})' if error and expected else '')
        bar = (InfoBar.error if error else InfoBar.warning if warn else InfoBar.success)(title, content, orient=Qt.Orientation.Vertical, isClosable=True,
                                                            position=InfoBarPosition.BOTTOM_RIGHT, duration=8000, parent=self.win)
        if path:
            path = os.path.normpath(path)
            # The buttons share one row under the text (a vertical stack made the message tall). A PNG also gets Copy picture,
            # so it can go into a chat straight from the message (a GIF gets none: the clipboard would take its first frame).
            buttons = QWidget()
            h = QHBoxLayout(buttons)
            h.setContentsMargins(0, 0, 0, 0)
            h.setSpacing(6)
            show = PushButton('Show in folder')
            show.clicked.connect(lambda: subprocess.Popen(['explorer', '/select,', path]))
            h.addWidget(show)
            copy_path = PushButton('Copy path')
            copy_path.clicked.connect(lambda: QGuiApplication.clipboard().setText(path))
            h.addWidget(copy_path)
            if path.lower().endswith('.png'):
                copy_pic = PushButton('Copy picture')
                copy_pic.clicked.connect(lambda: Captures.copy_picture(path, self.win))
                h.addWidget(copy_pic)
            bar.addWidget(buttons)

    # ---- the game (only when really needed) -----------------------------------------------------------------------------

    def _python(self):
        return sys.executable if not getattr(sys, 'frozen', False) else 'python'

    def _run(self, args, title, busy=''):
        if self.process is not None and self.process.state() != QProcess.ProcessState.NotRunning:
            self.toast('Something is still running', 'Wait for it to finish.', error=True)
            return
        out = subprocess.run(['tasklist', '/FI', 'IMAGENAME eq ravenfield.exe'], capture_output=True, text=True, errors='ignore').stdout
        if 'ravenfield.exe' in out.lower():
            self.toast('Ravenfield is running', 'Not starting it again (someone is playing?).', error=True)
            return
        self.job_log.setPlainText(f'{title}…\n')
        # The Easy card says the job runs (busy), and how it ended (_job_done, _job_failed). The text it had is kept for after
        # a game test (real2 I1#7). A result an earlier job left on the card is not kept (a test that passed after one that
        # failed would say 'did not work' for ever): the text before that result is.
        if self.weapon_info.text() != self._card_job_text:
            self._info_before_job = self.weapon_info.text()
        self._job_busy = busy
        if busy:
            self.weapon_info.setText(busy)
            self._card_job_text = busy
        self.process = QProcess(self)
        self.process.setWorkingDirectory(self.repo)
        self.process.setProcessChannelMode(QProcess.ProcessChannelMode.MergedChannels)
        decoder = codecs.getincrementaldecoder('utf-8')('replace')  # (a character split between two reads was lost)
        self.process.readyReadStandardOutput.connect(
            lambda: self.job_log.appendPlainText(decoder.decode(bytes(self.process.readAllStandardOutput())).rstrip()))
        self.process.finished.connect(lambda code, _s: self._job_done(title, code))
        self.process.errorOccurred.connect(lambda err: self._job_failed(title, err))
        env = self.process.processEnvironment()
        env.insert('PYTHONIOENCODING', 'utf-8')
        self.process.setProcessEnvironment(env)
        self.process.start(self._python(), args)

    def _job_failed(self, title, err):
        if err != QProcess.ProcessError.FailedToStart:
            return  # (a crash or a kill also ends in finished)
        reason = self.process.errorString()
        self.job_log.appendPlainText(f'\n{title}: could not start {self._python()} ({reason}).')
        # (From source the job runs this same Python, so 'not installed' was wrong there: Qt's reason is said instead; review
        # of the sandbox extras.)
        advice = (' Python has to be installed (and on PATH) for the game jobs.' if getattr(sys, 'frozen', False) else '')
        if self.weapon_info.text() == self._job_busy:  # (the Easy card says so as well, unless a weapon was opened since)
            self._card_job_text = f'{title} did not start: {self._python()}: {reason}.{advice}'
            self.weapon_info.setText(self._card_job_text)
        self.toast(f'{title}: could not start', f'{self._python()}: {reason}.{advice}', error=True)

    def _job_done(self, title, code):
        # The output before this job's own closing line. Its reason can sit above the last lines: vrshots raises 'build failed'
        # and the build output is printed after it (real2 I1#7). The first line is the job's title (set by _run).
        lines = [ln.rstrip() for ln in self.job_log.toPlainText().splitlines() if ln.strip()][1:]
        self.job_log.appendPlainText(f'\n{title}: finished ({code}).')
        # The Easy card showed the job while it ran: an export reads the weapon list again, and another job goes back to the text
        # it had, unless a weapon was opened meanwhile (that weapon has its own text then).
        shows_job = self.weapon_info.text() == self._job_busy
        if 'Export' in title:
            self.load_weapon_list(keep_open=True)  # (after this, the card holds the list: a failure below replaces its text)
            shows_job = True
        elif shows_job and code == 0:
            self.weapon_info.setText(self._info_before_job)
        if code != 0:
            keyed = [i for i, ln in enumerate(lines) if any(k in ln.lower() for k in ('error', 'failed', 'traceback'))]
            shown = sorted(set(keyed[-3:]) | set(range(max(0, len(lines) - 3), len(lines))))
            reason = lines[keyed[-1]] if keyed else (lines[-1] if lines else 'it printed nothing')
            if shows_job:
                self._card_job_text = f'{title} did not work: {reason}\nThe whole output: Advanced, under the Game card.'
                self.weapon_info.setText(self._card_job_text)
            self.toast(f'{title}: it did not work', '\n'.join(lines[i] for i in shown)
                       + '\n(the whole output: Advanced, under the Game card)', error=True)
        else:
            self.toast(f'{title}: finished', 'The weapons are read again; the open weapon and its hands stay as they were.'
                       if 'Export' in title else 'Done.')

    def export_weapons(self):
        names = self.export_names.text().strip()
        self._run([os.path.join(self.repo, 'tools', 'sandbox', 'export.py'), '--game', self.game_folder()]
                  + (['--weapons', names] if names else []), 'Export weapons',
                  busy=f'Exporting {names or "all weapons"}… Ravenfield runs once without a headset; the weapons appear here when '
                       'it is done (a few minutes).')

    def test_in_game(self):
        w = self.scene.weapon
        if w is None:
            return
        self._run([os.path.join(self.repo, 'tools', 'vrshots.py'), '--game', self.game_folder(), '--weapon', '=' + w.name,
                   '--out', 'sandbox-' + safe(w.name)],
                  f'Test {w.name} in the game', busy=f'Testing {w.name} in the game…')


class Preview(QLabel):
    """The selected capture as large as its box allows, fitted with its proportions kept, and fitted again when the window is
    resized. A GIF plays here. clear_file stops it and lets its file go (a capture is moved to the Recycle Bin after it, and a
    file still open cannot be moved)."""

    def __init__(self):
        super().__init__()
        self.setAlignment(Qt.AlignmentFlag.AlignCenter)
        self.setMinimumSize(320, 180)
        # (The picture's own size does not set the box's: the picture is fitted into whatever room the page gives.)
        self.setSizePolicy(QSizePolicy.Policy.Ignored, QSizePolicy.Policy.Ignored)
        self.still = None  # a PNG's picture
        self.movie = None  # a GIF's movie, played frame by frame

    def show_file(self, path):
        self.clear_file()
        if path.lower().endswith('.gif'):
            self.movie = QMovie(path)
            self.movie.frameChanged.connect(self.draw)
            self.movie.start()
        else:
            self.still = QPixmap(path)
        self.draw()

    def clear_file(self):
        if self.movie is not None:
            self.movie.stop()
            self.movie = None  # (the last reference: the movie is freed now, and with it the file it reads)
        self.still = None
        self.clear()

    def draw(self, *_):
        pm = self.movie.currentPixmap() if self.movie is not None else self.still
        if pm is None or pm.isNull():
            return
        self.setPixmap(pm.scaled(self.size(), Qt.AspectRatioMode.KeepAspectRatio, Qt.TransformationMode.SmoothTransformation))

    def resizeEvent(self, e):
        super().resizeEvent(e)
        self.draw()


class Captures(QWidget):
    THUMB = QSize(192, 108)  # a thumbnail's size; a picture of another shape is fitted inside it
    CELL = QSize(216, 160)   # a grid cell: the thumbnail, then room for the name on two lines

    def __init__(self, folder):
        super().__init__()
        self.setObjectName('captures')
        self.folder = folder
        self.thumbs = {}  # file -> (modified time, icon): a thumbnail is made once per version of a file, not on each refresh
        v = QVBoxLayout(self)
        v.setContentsMargins(24, 18, 24, 18)
        v.addWidget(TitleLabel('Captures'))
        self.folder_label = CaptionLabel(folder)
        v.addWidget(self.folder_label)
        self.list = ListWidget()
        # Thumbnails in a grid: a file name alone does not show which picture it is.
        self.list.setViewMode(QListView.ViewMode.IconMode)
        self.list.setIconSize(self.THUMB)
        self.list.setGridSize(self.CELL)
        self.list.setResizeMode(QListView.ResizeMode.Adjust)
        self.list.setWordWrap(True)
        self.list.itemDoubleClicked.connect(self.open_item)
        self.list.itemSelectionChanged.connect(self.update_buttons)
        self.list.setContextMenuPolicy(Qt.ContextMenuPolicy.CustomContextMenu)
        self.list.customContextMenuRequested.connect(self.on_context)
        # The selected capture large, with the text saved beside it (its .txt) under it: a name in a grid says little about a
        # picture. The .txt files are not listed themselves (refresh only takes .png and .gif).
        self.preview = Preview()
        self.notes = PlainTextEdit()
        self.notes.setReadOnly(True)
        self.notes.setFixedHeight(170)
        self.notes.setPlaceholderText('No text was saved beside this capture.')
        self.list.itemSelectionChanged.connect(self.show_preview)
        panel = CardWidget()  # (a card as card() makes them; built here so the picture gets the room the page gives it)
        pv = QVBoxLayout(panel)
        pv.setContentsMargins(14, 12, 14, 12)
        pv.setSpacing(8)
        pv.addWidget(StrongBodyLabel('Selected capture'))
        pv.addWidget(self.preview, 1)
        pv.addWidget(self.notes)
        v.addLayout(row(self.list, panel, stretch=[3, 2]), 1)
        # These act on the selected capture, so they stay off until one is selected.
        self.item_buttons = []
        for icon, text, slot in ((FIF.PHOTO, 'Open', self.open_selected), (FIF.COPY, 'Copy picture', self.copy_selected),
                                 (FIF.LINK, 'Copy path', self.copy_selected_path),
                                 (FIF.SEARCH, 'Show in folder', self.show_selected), (FIF.DELETE, 'Delete', self.delete_selected)):
            b = PushButton(icon, text)
            b.clicked.connect(slot)
            self.item_buttons.append(b)
        self.copy_btn = self.item_buttons[1]  # (Copy picture, the second of them: a GIF cannot be copied as a picture)
        v.addLayout(row(*self.item_buttons, QWidget(), stretch=[0] * len(self.item_buttons) + [1]))
        open_folder = PushButton(FIF.FOLDER, 'Open the folder')
        # (The folder as it is then: set_folder can change it.)
        open_folder.clicked.connect(lambda: QDesktopServices.openUrl(QUrl.fromLocalFile(self.folder)))
        refresh = PushButton(FIF.SYNC, 'Refresh')
        refresh.clicked.connect(self.refresh)
        v.addLayout(row(open_folder, refresh, QWidget(), stretch=[0, 0, 1]))
        self.refresh()

    def set_folder(self, folder):
        """Another captures folder: the self-test's own, so its pictures never land among the user's (review of the extras)."""
        self.folder = folder
        self.folder_label.setText(folder)
        self.thumbs.clear()
        self.refresh()

    def refresh(self):
        selected = self.selected_path()  # (the same capture stays selected when the list is read again)
        self.list.blockSignals(True)  # (the list is rebuilt: the preview and the buttons are set once, at the end)
        self.list.clear()
        files = []
        if os.path.isdir(self.folder):
            for f in os.listdir(self.folder):
                path = os.path.join(self.folder, f)
                if f.lower().endswith(('.png', '.gif')):
                    try:
                        files.append((os.path.getmtime(path), path))
                    except OSError:
                        pass  # (removed while the folder was read)
        files.sort(reverse=True)  # the newest first
        for _mtime, path in files:
            it = QListWidgetItem(self.thumbnail(path), os.path.basename(path))
            # The list's stylesheet gives every item a 35 px height, which cropped the thumbnails: the item's own size
            # hint is the grid cell instead.
            it.setSizeHint(self.CELL)
            it.setData(Qt.ItemDataRole.UserRole, path)
            self.list.addItem(it)
            if path == selected:
                self.list.setCurrentItem(it)
        # The thumbnails of files that are gone are dropped, so a deleted capture does not stay in memory.
        live = {path for _mtime, path in files}
        self.thumbs = {p: hit for p, hit in self.thumbs.items() if p in live}
        self.list.blockSignals(False)
        self.update_buttons()
        self.show_preview()  # (a capture that is gone is no longer shown, nor its GIF played)

    def thumbnail(self, path):
        """The small copy of a picture for the grid. A GIF shows its first frame."""
        try:
            mtime = os.path.getmtime(path)
        except OSError:
            return QIcon()
        hit = self.thumbs.get(path)
        if hit is not None and hit[0] == mtime:
            return hit[1]
        icon = QIcon()
        img = QImage(path)  # (the full picture is read once here; only the small copy is kept)
        if not img.isNull():
            small = img.scaled(self.THUMB, Qt.AspectRatioMode.KeepAspectRatio, Qt.TransformationMode.SmoothTransformation)
            icon = QIcon(QPixmap.fromImage(small))
        self.thumbs[path] = (mtime, icon)
        return icon

    def showEvent(self, e):
        self.refresh()
        super().showEvent(e)

    def hideEvent(self, e):
        self.preview.clear_file()  # (a GIF is not played while the page is not shown; showEvent shows it again)
        super().hideEvent(e)

    def selected_item(self):
        return self.list.currentItem()

    def selected_path(self):
        item = self.selected_item()
        return item.data(Qt.ItemDataRole.UserRole) if item is not None else None

    def update_buttons(self):
        on = self.selected_item() is not None
        for b in self.item_buttons:
            b.setEnabled(on)
        # A GIF is not copied as a picture (the clipboard would take its first frame alone): the button is not offered for one.
        self.copy_btn.setVisible(not (self.selected_path() or '').lower().endswith('.gif'))

    def show_preview(self):
        """The selected capture in the preview, with the text saved beside it (its .txt) under it. Nothing selected: empty."""
        path = self.selected_path()
        self.preview.clear_file()  # (the capture shown before is let go first: its GIF stops, its file is free)
        self.notes.setPlainText('')
        if path is None:
            return
        self.preview.show_file(path)
        side = os.path.splitext(path)[0] + '.txt'
        if os.path.isfile(side):
            try:
                with open(side, encoding='utf-8') as f:
                    self.notes.setPlainText(f.read())
            except (OSError, UnicodeDecodeError):
                pass  # (removed, or not text, while it was read: the picture still shows)

    def on_context(self, pos):
        item = self.list.itemAt(pos)
        if item is None:
            return
        self.list.setCurrentItem(item)
        menu = RoundMenu(parent=self)
        menu.addAction(Action(FIF.PHOTO, 'Open', triggered=self.open_selected))
        if not item.data(Qt.ItemDataRole.UserRole).lower().endswith('.gif'):  # (a GIF is not copied as a picture)
            menu.addAction(Action(FIF.COPY, 'Copy picture', triggered=self.copy_selected))
        menu.addAction(Action(FIF.LINK, 'Copy path', triggered=self.copy_selected_path))
        menu.addAction(Action(FIF.SEARCH, 'Show in folder', triggered=self.show_selected))
        menu.addSeparator()
        menu.addAction(Action(FIF.DELETE, 'Delete (to the Recycle Bin)', triggered=self.delete_selected))
        menu.exec(self.list.viewport().mapToGlobal(pos))

    def open_item(self, item):
        QDesktopServices.openUrl(QUrl.fromLocalFile(item.data(Qt.ItemDataRole.UserRole)))

    def open_selected(self):
        item = self.selected_item()
        if item is not None:
            self.open_item(item)

    @staticmethod
    def tell(title, content, parent, error=False):
        """A short message at the bottom right: the only sign a copy gives, so it goes away by itself."""
        (InfoBar.error if error else InfoBar.success)(title, content, position=InfoBarPosition.BOTTOM_RIGHT,
                                                      duration=5000 if error else 2500, parent=parent)

    @staticmethod
    def copy_picture(path, parent):
        """Put a picture on the clipboard, so it pastes into a chat. A GIF gives its first frame (QImage reads one frame)."""
        img = QImage(path)
        if img.isNull():
            Captures.tell('Could not copy the picture', f'{os.path.basename(path)} is not a picture this program can read.',
                          parent, error=True)
            return
        QGuiApplication.clipboard().setImage(img)
        what = 'The picture is on the clipboard.'
        if path.lower().endswith('.gif'):
            what = 'The first frame of the GIF is on the clipboard.'
        Captures.tell('Copied', what, parent)

    def copy_selected(self):
        path = self.selected_path()
        if path:
            self.copy_picture(path, self.window())

    def copy_selected_path(self):
        path = self.selected_path()
        if path:
            QGuiApplication.clipboard().setText(os.path.normpath(path))
            self.tell('Copied', 'The path is on the clipboard.', self.window())

    def show_selected(self):
        path = self.selected_path()
        if path:
            subprocess.Popen(['explorer', '/select,', os.path.normpath(path)])

    def delete_selected(self):
        path = self.selected_path()
        if not path:
            return
        box = MessageBox('Move this capture to the Recycle Bin?',
                         f'{os.path.basename(path)} goes to the Recycle Bin, where it can still be restored.', self.window())
        box.yesButton.setText('Move to Recycle Bin')
        if not box.exec():
            return
        self.preview.clear_file()  # (a file still open, as a GIF being played is, cannot be moved: the preview lets it go first)
        # Moved to the Recycle Bin, never deleted for good. (QFile.moveToTrash returns (ok, path in the bin).)
        ok, _in_bin = QFile.moveToTrash(path)
        if not ok:
            self.tell('Could not move it to the Recycle Bin', path, self.window(), error=True)
            self.show_preview()
            return
        side = os.path.splitext(path)[0] + '.txt'  # the text saved beside it goes with it (it means nothing alone)
        if os.path.isfile(side):
            ok_txt, _ = QFile.moveToTrash(side)
            if not ok_txt:  # (open in another program: said, as for the picture; the picture is gone, so the list is refreshed)
                self.tell('Could not move its text to the Recycle Bin', side, self.window(), error=True)
        self.refresh()


class Help(QWidget):
    def __init__(self):
        super().__init__()
        self.setObjectName('help')
        v = QVBoxLayout(self)
        v.setContentsMargins(24, 18, 24, 18)
        v.addWidget(TitleLabel('How to use it'))
        # Longer than the window at its usual size, so the text scrolls.
        text = BodyLabel(
            '<p><b>Start</b>: pick a weapon on the left (the search box filters the list). The hands go on it the way the mod '
            'puts them: the right hand on the grip, the left on the handguard. Its saved pose comes back when you open it.</p>'
            '<p><b>Unsaved hands are kept</b>: when you open another weapon, the hands you left stay as they were and come back '
            'when you return. A weapon with unsaved hands says <b>not saved</b> in the list. <b>Revert</b> puts the open weapon\'s '
            'hands back to its saved pose.</p>'
            '<p><b>Move a hand</b>: drag it. <b>Turn it</b>: Shift + drag. <b>Ctrl + drag</b> moves the weapon itself (Shift turns '
            'it, as for a hand). <b>Hands on the gun</b> puts both hands on it the way the mod holds it. <b>Left hand to</b> puts '
            'the left hand on the handguard, the slide, the magazine or the cover. The laser\'s right-click menu can put a hand on '
            'any part the mod found.</p>'
            '<p><b>Arrows and rings</b> (as in Blender), chosen over the view: <b>Move</b> (G) puts arrows on the selected hand: '
            'drag one to move along its axis (red X, green Y, blue Z), a small square to move in its plane, the white circle in '
            'the middle to move in the view\'s plane. <b>Turn</b> (R) puts rings on it: drag a ring to turn about its axis, the '
            'white outer ring to turn about the view, inside the rings (off the hand and the gun) to turn freely. <b>Move + '
            'turn</b> (T) shows both, '
            '<b>Free</b> (W) none. Dragging the hand itself still moves it freely with any of them. Ctrl snaps (1 cm, 5 degrees), '
            'Shift is fine (a tenth as fast; with Ctrl, 1 mm and 1 degree), the <b>Snap</b> button snaps without Ctrl. Esc or a '
            'right click while dragging takes the drag back. <b>Axes</b>: Global (the world\'s), Local (the hand\'s own) or Gun '
            '(X along the barrel, Y up, Z its right side); the comma key cycles them. <b>On</b>: Hand or Gun (Ctrl + click on the '
            'gun puts the gizmo on it, a click on a hand back on the hand).</p>'
            '<p><b>Pose</b>: pick one from the list (the weapon\'s own grip and support poses first, then the mod\'s) or drag the '
            'finger sliders. <b>Copy this pose to the other hand</b> is under Advanced.</p>'
            '<p><b>Reload</b> and <b>Rack</b> play the parts the mod moves (the slide by its travel, the magazine along its line, '
            'the cover on its hinge), with the left hand on them. The sliders do it by hand. <b>Record the reload</b> saves that '
            'reload as a GIF. <b>Reset</b> puts the parts back home and both hands on the gun; the saved pose is not changed.</p>'
            '<p><b>Undo and redo</b>: Ctrl+Z goes back and Ctrl+Y (or Ctrl+Shift+Z) forward, for hand moves, poses, fingers, parts, '
            'snaps and Reset. The history holds up to 100 steps for the open weapon.</p>'
            '<p><b>Laser pointer</b> (L, or Alt + click): click a part to see what it is and where it is, in cm from the muzzle '
            '(back, up, right) as the test reports give it, and the mod\'s gun-space x, y, z. While it is on, a click picks a part '
            'and a hand cannot be dragged freely (its gizmo still moves it): press L again. <b>Right-click</b> opens a menu '
            '(Copy coordinates, Copy pose as text, Hide this part, Hands on the gun and more). <b>Copy pose as text</b> (also on the '
            'Save and share card) puts the weapon and both hands (where each one is, its pose and the part it holds) on the '
            'clipboard as text to paste.</p>'
            '<p>Grey boxes are parts whose mesh the game does not let the mod read: they are at the right place and size.</p>'
            '<p><b>Save pose</b> (Ctrl+S) writes this weapon\'s hands to tools/sandbox/poses/, and to the game\'s '
            'BepInEx/config/RavenfieldVR/poses/ folder when Ravenfield is found (that is the copy the mod reads). The game folder '
            'is looked for in every Steam library; if Ravenfield is elsewhere, pick it under Advanced, <b>Game folder…</b>. A pose '
            'file about to be overwritten is first kept in tools/sandbox/poses/history (the last 30 per weapon), so a pose saved '
            'over by mistake can be got back.</p>'
            '<p><b>Import pose…</b> and <b>Export pose…</b> move a pose file in and out (to share it or send it to Claude). '
            '<b>Send to game</b> and <b>Remove from game</b> put the saved pose in the game or take it out.</p>'
            '<p><b>Screenshot</b> (F12) and <b>Record GIF</b> save into reports/sandbox/captures (the message shows where, with a '
            'button to open it). <b>Clean pictures</b> leave out the grid, the markers, the laser line and the highlights. A GIF '
            'can <b>turn</b> the weapon once round for a seamless loop (a turntable; its length is under Advanced, GIF settings).</p>'
            '<p>The <b>Captures</b> page lists the pictures and GIFs, with what you can do with each one: open it, copy it, show it '
            'in Explorer, or move it to the Recycle Bin.</p>'
            '<p><b>Advanced</b>: exact numbers for the selected hand, the parts the mod found (click one to highlight it), its '
            'analysis, views, GIF settings, and the game: <b>Game folder…</b>, <b>Export weapons</b> (runs Ravenfield once without '
            'the headset) or <b>Test this gun in the game</b> (only when really needed).</p>'
            '<p><b>Problems</b> show a message, and the program goes on. Every error is also written to '
            'reports/sandbox/sandbox-errors.log, with its details.</p>'
            '<p>Mouse: drag to look around, right-drag to pan, wheel to zoom; Ctrl + drag on the gun moves it, Ctrl + Shift + drag turns it (the hands on it go along). Keys: 1 / 2 select the right / left hand, G / R / T / W move / turn / both / no gizmo, comma the gizmo axes, Esc takes a gizmo drag back, F frame the gun and the hands, Shift+F (or keypad .) the selected hand, arrows orbit the view and + / - zoom it (with the view clicked: the weapon list keeps its arrows), keypad 1 / 3 / 7 / 5 side, front, top and 3/4 views, O orthographic view, Space reload, L laser, Ctrl+S save, F12 screenshot, Ctrl+Z / Ctrl+Y undo / redo, ? this Help.</p>'
            '<p><b>Self-test</b>: <code>python tools/sandbox/app/main.py --selftest report.txt --weapon NAME</code> runs the '
            'features as a user would and writes PASS, FAIL or SKIP for each check (add --all to open every exported weapon '
            'once). It saves into a folder of its own: your saved poses and the game copies are never touched.</p>')
        text.setWordWrap(True)
        text.setTextFormat(Qt.TextFormat.RichText)
        body = QWidget()
        body.setObjectName('panel')
        bv = QVBoxLayout(body)
        bv.setContentsMargins(0, 0, 8, 0)
        bv.addWidget(text)
        bv.addStretch(1)
        v.addWidget(Workbench._scroll(body), 1)


class Window(FluentWindow):
    def closeEvent(self, e):
        wb = self.workbench
        job = wb.process
        if job is not None and job.state() != QProcess.ProcessState.NotRunning:
            box = MessageBox('Something is still running',
                             'An export or game test is still running. Close anyway? The sandbox stops waiting for it; '
                             'a game it started closes itself when it is done.', self)
            if not box.exec():
                e.ignore()
                return
            job.kill()
            job.waitForFinished(2000)
        if not wb.selftest and wb.scene.weapon is not None and (wb.unsaved() or wb.drafts):
            names = ([wb.scene.weapon.name] if wb.unsaved() else []) + [d for d in wb.drafts]
            box = MessageBox('Close without saving?', 'These weapons have hands that are not saved: ' + ', '.join(names[:6])
                             + (' …' if len(names) > 6 else '') + '.\n\nClose anyway (they are lost)?', self)
            if not box.exec():
                e.ignore()
                return
        wb.timeline.stop()
        if wb.recording:
            wb.last_gif = None
            wb.rec_btn.setChecked(False)  # (the recording so far is saved, not dropped)
            if wb.last_gif and not wb.selftest:
                # The 'GIF saved' message hangs off this window, which is closing: the path is shown in one that waits.
                box = MessageBox('GIF saved', f'The recording was saved to:\n{wb.last_gif}', self)
                box.hideCancelButton()
                box.exec()
        if not wb.selftest:
            wb.settings.setValue('geometry', self.saveGeometry())
            wb.settings.sync()  # (leave() ends the program with os._exit, which does not flush the settings: real2 I3#0)
        wb.view.cleanup()
        super().closeEvent(e)

    def __init__(self, repo):
        super().__init__()
        self.setWindowTitle(APP_NAME)
        icon = os.path.join(HERE, 'icon.ico')
        if os.path.exists(icon):
            self.setWindowIcon(QIcon(icon))
        self.workbench = Workbench(self, repo)
        self.captures = Captures(self.workbench.captures)
        self.help = Help()
        self.addSubInterface(self.workbench, FIF.EDIT, 'Weapons & hands')  # (the object name 'workbench' stays: the route key)
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
        geometry = QSettings('RavenfieldVR', 'Sandbox').value('geometry')
        if isinstance(geometry, QByteArray) and '--selftest' not in sys.argv and '--shot' not in sys.argv:
            self.restoreGeometry(geometry)  # where it was last time (Qt keeps it on a screen that still exists)


def error_log_path():
    """Where errors are written: next to the captures when the repository is known, else in the user's local app data."""
    repo = find_repo()
    base = os.path.join(repo, 'reports', 'sandbox') if repo else os.path.join(os.environ.get('LOCALAPPDATA', HERE), 'RavenfieldVR')
    os.makedirs(base, exist_ok=True)
    return os.path.join(base, 'sandbox-errors.log')


def set_app_identity():
    """Windows groups the taskbar button under this name, so the program does not show as python.exe when run from source."""
    if sys.platform != 'win32':
        return
    try:
        import ctypes
        ctypes.windll.shell32.SetCurrentProcessExplicitAppUserModelID('QWavey.RavenfieldVR.Sandbox')
    except Exception:  # noqa: BLE001 (the taskbar name is cosmetic: the program runs without it)
        pass


def install_error_handler(app):
    """An error in a button's or the view's code is written to sandbox-errors.log and shown, and the program goes on. (Without
    this PyQt6 ends the whole program on any such error: opening a weapon whose export it could not read closed it.)"""
    import traceback

    def hook(kind, value, tb):
        text = ''.join(traceback.format_exception(kind, value, tb))
        try:
            with open(error_log_path(), 'a', encoding='utf-8') as f:
                f.write(f"--- {time.strftime('%Y-%m-%d %H:%M:%S')}\n{text}\n")
        except OSError:
            pass
        if sys.stderr is not None:
            try:
                sys.stderr.write(text)
            except (OSError, ValueError):
                pass
        win = next((w for w in app.topLevelWidgets() if isinstance(w, Window)), None)
        if win is not None and hasattr(win, 'workbench'):
            try:
                win.workbench.toast('Something went wrong (the program goes on)', f'{kind.__name__}: {value}\nDetails in {error_log_path()}',
                                    error=True)
            except Exception:  # noqa: BLE001 (the message itself must not fail the hook)
                pass
    sys.excepthook = hook


def main():
    set_app_identity()  # first: the taskbar takes its group from the process as it starts
    QApplication.setHighDpiScaleFactorRoundingPolicy(Qt.HighDpiScaleFactorRoundingPolicy.PassThrough)
    app = QApplication(sys.argv)
    install_error_handler(app)
    app.setApplicationName(APP_NAME)
    setTheme(Theme.DARK)
    setThemeColor('#d79b45')
    repo = find_repo()
    while repo is None:
        from PyQt6.QtWidgets import QFileDialog, QMessageBox
        QMessageBox.information(None, APP_NAME, 'The sandbox needs the Ravenfield-VR mod folder (the one that holds src, tools '
                                'and reports). Pick it in the next window.')
        chosen = QFileDialog.getExistingDirectory(None, 'Pick the Ravenfield-VR folder')
        if not chosen:
            QMessageBox.information(None, APP_NAME, 'No folder picked: the sandbox closes. Start it again to pick the folder.')
            return
        if is_repo(chosen):
            repo = chosen
            if '--selftest' not in sys.argv:
                QSettings('RavenfieldVR', 'Sandbox').setValue('repo', repo)
        else:
            QMessageBox.warning(None, APP_NAME, f'{chosen} is not the Ravenfield-VR folder (it has no src\\Rig\\Glove\\glove.bin). '
                                'Pick the folder again.')
    win = Window(repo)
    args = sys.argv[1:]
    if '--selftest' in args:
        from selftest import SelfTest
        report = args[args.index('--selftest') + 1]
        want = args[args.index('--weapon') + 1] if '--weapon' in args else None
        win.show()
        test = SelfTest(win, app, report, want, all_weapons='--all' in args)
        test.start()
        leave(app.exec())
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
            def take():
                ok = (win.screen().grabWindow(int(win.winId())) if whole else v.grabFramebuffer()).save(out)
                if not ok:
                    print(f'--shot: could not write {out}', file=sys.stderr)
                app.exit(0 if ok else 1)
            QTimer.singleShot(delay, take)
        QTimer.singleShot(900, shoot)
    else:
        win.show()
    leave(app.exec())


def leave(code):
    """Ends the program once the window is closed and its GPU resources freed. A normal Python exit then tore down Qt and
    PyOpenGL objects in an arbitrary order and crashed now and then (access violation after a clean close)."""
    for stream in (sys.stdout, sys.stderr):
        if stream is not None:  # (None in the exe: it has no console)
            try:
                stream.flush()
            except (OSError, ValueError):
                pass
    QSettings('RavenfieldVR', 'Sandbox').sync()  # the settings go to disk before os._exit (real2 I3#0)
    os._exit(code or 0)


if __name__ == '__main__':
    main()
