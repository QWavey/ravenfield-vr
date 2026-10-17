"""Ravenfield VR Sandbox: put the mod's hands on the exported weapons, pose them, play reloads, point at parts, save
poses, screenshots and GIFs, without starting the game.

    python tools/sandbox/app/main.py              (or the built RavenfieldVR-Sandbox.exe)
    python tools/sandbox/app/main.py --shot out.png [--weapon NAME]   render one picture and quit (for checks)
"""
import codecs
import json
import math
import os
import subprocess
import sys
import time

import numpy as np
from PIL import Image
from PyQt6.QtCore import QByteArray, QProcess, QSettings, Qt, QTimer, QUrl
from PyQt6.QtGui import QDesktopServices, QGuiApplication, QIcon, QKeySequence, QShortcut
from PyQt6.QtWidgets import QApplication, QFrame, QHBoxLayout, QListWidgetItem, QStackedWidget, QVBoxLayout, QWidget
from qfluentwidgets import (Action, BodyLabel, CaptionLabel, CardWidget, ComboBox, DoubleSpinBox, FluentIcon as FIF, FluentWindow,
                            InfoBar, InfoBarPosition, LineEdit, MessageBox, ListWidget, NavigationItemPosition, Pivot, PlainTextEdit, PrimaryPushButton,
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
        self.process = None
        self.selftest = '--selftest' in sys.argv  # the self-test: nothing of the user's is read or written then
        self.game_dir_override = None  # the self-test's own game poses folder
        self.drafts = {}  # weapon id -> its hands as left when another weapon was opened (unsaved work is kept)
        self.saved_doc = None  # the open weapon's saved pose, as read (for Revert and for marking changes)
        self.settings = QSettings('RavenfieldVR', 'Sandbox')
        self.undo_stack, self.redo_stack = [], []
        self._numbers_undo_at = 0.0

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
        self.view.drag_started.connect(self.push_undo)
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
        self.s_slide = LabeledSlider('Slide back', lambda f: self.set_part(slide=f), width=110, on_press=self.push_undo)
        self.s_mag = LabeledSlider('Magazine out', lambda f: self.set_part(mag=f), width=110, on_press=self.push_undo)
        self.s_cover = LabeledSlider('Cover open', lambda f: self.set_part(cover=f), width=110, on_press=self.push_undo)
        v.addWidget(card('Reload', row(play, rack, reset), self.s_slide, self.s_mag, self.s_cover))

        self.laser_btn = TogglePushButton(FIF.ALIGNMENT, 'Laser pointer  (L)')
        self.laser_btn.toggled.connect(self.set_laser)
        laser_hint = CaptionLabel('Click a part with the laser (or Alt + click) to see what it is and where; right-click to copy it.')
        laser_hint.setWordWrap(True)
        v.addWidget(card('Point', self.laser_btn, laser_hint))

        save = PrimaryPushButton(FIF.SAVE, 'Save pose  (also for the game)')
        save.clicked.connect(self.save_pose)
        imp = PushButton(FIF.FOLDER, 'Import pose…')
        imp.clicked.connect(self.import_pose)
        exp = PushButton(FIF.SHARE, 'Export pose…')
        exp.clicked.connect(self.export_pose)
        self.game_status = CaptionLabel('')
        self.game_status.setWordWrap(True)
        send = PushButton(FIF.SEND, 'Send to game')
        send.clicked.connect(self.send_to_game)
        remove = PushButton(FIF.DELETE, 'Remove from game')
        remove.clicked.connect(self.remove_from_game)
        shot = PushButton(FIF.CAMERA, 'Screenshot')
        shot.clicked.connect(lambda: self.screenshot())  # (a plain connect would pass the button's checked state as 'clean')
        self.rec_btn = TogglePushButton(FIF.VIDEO, 'Record GIF')
        self.rec_btn.toggled.connect(self.toggle_record)
        turn = PushButton(FIF.ROTATE, 'Turntable GIF')
        turn.clicked.connect(lambda: self.turntable_gif())
        reload_gif = PushButton(FIF.PLAY, 'Record the reload')
        reload_gif.clicked.connect(lambda: self.record_reload())
        v.addWidget(card('Save and share', save, row(imp, exp), self.game_status, row(send, remove), row(shot, self.rec_btn),
                         row(turn, reload_gif)))
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
        self.settle_sw.checkedChanged.connect(self.on_settle_switch)
        markers_hint = CaptionLabel('Markers: green grip, blue support, orange rack point and its travel, red muzzle and aim, '
                                    'yellow magazine seat and its line, purple cover latch.')
        markers_hint.setWordWrap(True)
        settle_hint = CaptionLabel("Settle: after a snap the glove is put onto the gun's surface, as it settles in VR (up to 2 cm).")
        settle_hint.setWordWrap(True)
        # Pictures and GIFs without the grid, markers, laser line and highlights (off by default: they show the evidence).
        self.clean_sw = SwitchButton()
        self.clean_sw.setChecked(False)
        self.big_sw = SwitchButton()  # screenshots drawn again at twice the view's size, for a post
        self.big_sw.setChecked(False)
        v.addWidget(card('View', row(*views), row(BodyLabel('Grid'), self.grid_sw, BodyLabel('Markers'), self.mark_sw),
                         row(BodyLabel('Settle hands on the gun'), self.settle_sw), settle_hint,
                         markers_hint, row(BodyLabel('GIF fps'), self.gif_fps, BodyLabel('size'), self.gif_scale),
                         row(BodyLabel('Pictures without grid and markers'), self.clean_sw),
                         row(BodyLabel('Screenshots at 2x size'), self.big_sw)))

        self.export_names = LineEdit()
        self.export_names.setPlaceholderText('Weapons to export (empty: all)')
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
                         row(export, game), self.job_log))
        v.addStretch(1)
        return p

    def _shortcuts(self):
        for key, fn in (('L', lambda: self.laser_btn.toggle()), ('F', self.frame_weapon),
                        ('1', lambda: self.select_hand(self.scene.right)), ('2', lambda: self.select_hand(self.scene.left)),
                        ('Space', self.play_reload), ('Ctrl+S', self.save_pose), ('F12', self.screenshot),
                        ('Ctrl+Z', self.undo), ('Ctrl+Y', self.redo), ('Ctrl+Shift+Z', self.redo)):
            QShortcut(QKeySequence(key), self, activated=fn)

    # ---- weapons -----------------------------------------------------------------------------------------------------

    def load_weapon_list(self, keep_open=False):
        """The exported weapons. keep_open: the weapon open now stays open (opened again from its new files, with the hands as
        they are); else the one opened last time, or the first."""
        current = self.scene.weapon.id if self.scene.weapon is not None else None
        hands = self.scene.pose_document() if keep_open and current else None
        self.weapon_list.blockSignals(True)
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
        self.open_weapon(item.data(Qt.ItemDataRole.UserRole))

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
                kept = self.keep_old(pose_file, 'unreadable')
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
        self.sync_sliders()
        self.frame_weapon()
        self.select_hand(self.scene.right)
        self.update_game_status()

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
        self.on_select(hand)
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
        h = self.current_hand()
        self.sel_name.setText(f"Selected: {h.side} hand" + (f" (on the {h.on_part})" if h.on_part else ''))

    def show_pose_name(self, h):
        self.pose.blockSignals(True)
        self.pose.setCurrentText(h.pose_name if self.pose.findText(h.pose_name) >= 0 else 'Custom')
        self.pose.blockSignals(False)

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
                h.set_pose(name, [0.35] + list(curls))
                h.thumb_set = False  # the weapon's own pose: the game keeps its thumb rule
                self.sync_hand_controls()
                self.view.update()
            return
        h.set_pose(name)
        h.thumb_set = True  # a pose picked by hand: its thumb goes to the game too
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
        if time.monotonic() - self._numbers_undo_at > 1.5:
            self.push_undo()
        self._numbers_undo_at = time.monotonic()
        f, u, r = (b.value() / 100 for b in self.num[0])
        g = w.grip + w.aim * f + w.up * u + w.right * r
        h.position = w.to_world(g)
        h.rotation = quat_norm(quat_mul(w.Wq, euler_deg_to_quat([b.value() for b in self.num[1]])))
        self._moved(h)
        self.view.update()

    def on_hand_moved(self, hand=None):
        self._moved(hand if hand is not None else self.current_hand())
        self.sync_numbers()

    def _moved(self, h):
        """A hand moved by hand: no longer on the part it was snapped to; its job is what it touches now (a left hand dragged
        from the magazine onto the handguard is the support hand again; its stale job dropped it from the saved pose)."""
        h.on_part = ''
        h.role = self.scene.contact_role(h)
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
        return (dur, lambda t: w.set_state(**{what: start['v'] + (to - start['v']) * t}), begin)

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
        self.redo_stack.clear()

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
            return bool(self.undo_stack)
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
        moved = np.linalg.norm(n.sim[:3, 3]) > 1e-6 or np.linalg.norm(n.sim[:3, :3] - np.eye(3)) > 1e-6
        try:
            rest = np.linalg.solve(n.sim, np.append(g, 1.0))[:3] if moved else g  # the same point on the part at rest
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
        if rich:
            return (f"<b>{n.short}</b> — {role}{' (grey stand-in box)' if n.stand_in else ''}<br>"
                    f"back <b>{b:.1f}</b> · up <b>{u:.1f}</b> · right <b>{r:.1f}</b> cm {ref}{note}<br>"
                    f"<span style='color:gray'>gun space x {x:.4f} y {y:.4f} z {z:.4f}</span>")
        return (f"{w.name} ({w.id}): '{name}' ({role}) at back {b:.1f} up {u:.1f} right {r:.1f} cm {ref}{note} "
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
            self.push_undo()
            self.picked_node.hidden = True
            self.view.update()

    # ---- saving ------------------------------------------------------------------------------------------------------------

    DEFAULT_GAME = os.path.join(os.environ.get('ProgramFiles(x86)', r'C:\Program Files (x86)'), 'Steam', 'steamapps', 'common', 'Ravenfield')

    def game_folder(self):
        """Ravenfield's folder: the one chosen in Advanced (Game folder...), else Steam's default."""
        chosen = '' if self.selftest else str(self.settings.value('gameFolder', '') or '')
        return chosen if chosen and os.path.isdir(chosen) else self.DEFAULT_GAME

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
        weapon are kept): a pose saved over by mistake can be got back."""
        if not path or not os.path.exists(path):
            return None
        hist = os.path.join(self.poses_dir, 'history')
        os.makedirs(hist, exist_ok=True)
        base = os.path.splitext(os.path.basename(path))[0]
        where = 'game' if os.path.normcase(os.path.dirname(path)) != os.path.normcase(self.poses_dir) else 'saved'
        dst = os.path.join(hist, f'{base}--{where}{("-" + why) if why else ""}--{stamp()}.json')
        try:
            import shutil
            shutil.copy2(path, dst)
            old = sorted((f for f in os.listdir(hist) if f.startswith(base + '--')), key=lambda f: os.path.getmtime(os.path.join(hist, f)))
            for f in old[:-30]:
                os.remove(os.path.join(hist, f))
        except OSError as e:
            self.log_error(f'keeping the old pose {path}', e)
            return None
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
            self.toast('The game was not found', 'Ravenfield is not in the usual Steam folder.', error=True)
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
            self.toast('Could not send it to the game', str(e), error=True)
            return
        self.update_game_status()
        self.toast('Sent to the game', os.path.join(game, w.id + '.json'))

    def remove_from_game(self):
        """The game holds this weapon the mod's own way again (the pose stays saved here)."""
        w = self.scene.weapon
        game = self.game_poses_dir()
        if w is None or game is None:
            return
        target = os.path.join(game, w.id + '.json')
        try:
            if os.path.exists(target):
                self.keep_old(target, 'removed')
                os.remove(target)
        except OSError as e:
            self.toast('Could not remove it from the game', str(e), error=True)
            return
        self.update_game_status()
        self.toast('Removed from the game', 'The game holds this weapon the mod\'s own way again; the pose is still saved here.')

    def update_game_status(self):
        w = self.scene.weapon
        if w is None:
            self.game_status.setText('')
            return
        game = self.game_poses_dir()
        saved = os.path.exists(self.pose_path())
        if game is None:
            text = 'The game (with the mod) was not found: poses are saved here only. Advanced → Game folder… to point to it.'
        elif os.path.exists(os.path.join(game, w.id + '.json')):
            text = 'In the game: this weapon is held with your saved pose (from the next time it is analysed: restart or reload).'
        else:
            text = 'In the game: the mod\'s own grip' + (' (your saved pose is not sent: Send to game).' if saved else '.')
        self.game_status.setText(text)

    def screenshot(self, clean=None):
        """A picture of the view as it is now (F12 or the Screenshot button). clean=None takes the 'Pictures without grid and
        markers' switch; the 2x switch draws the same camera again at twice the size, for a post."""
        w = self.scene.weapon
        if clean is None:
            clean = self.clean_sw.isChecked()
        img = self._grab_big(clean) if self.big_sw.isChecked() else None
        if img is None:  # (also when the 2x picture could not be drawn: the view's own size then)
            img = self._grab_view(clean)
        path = os.path.join(self.captures, f"{safe(w.name if w else 'sandbox')}-{stamp()}.png")
        if not img.save(path):
            self.toast('The screenshot could not be saved', path, error=True)
            return None
        self.toast('Screenshot saved', path, path)
        return path

    # What a clean picture leaves out: the grid, the markers, the laser line, the hover and part highlights and the hand's tint.
    OVERLAYS = ('show_grid', 'show_markers', 'pick_line', 'hover_node', 'part_node', 'selected_hand')
    TURN_SECONDS = 4.0  # the turntable GIF: one turn of the view

    def _overlays_off(self):
        """Turns the overlays off for a clean picture; returns what they were, for _overlays_back."""
        v = self.view
        saved = {k: getattr(v, k) for k in self.OVERLAYS}
        v.show_grid, v.show_markers = False, False
        v.pick_line, v.hover_node, v.part_node, v.selected_hand = None, None, None, None
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

    def _grab_big(self, clean):
        """The view drawn again into an offscreen buffer at twice its size: the same camera, so the same picture, sharper.
        None when the buffer cannot be drawn (the caller then takes the picture at the view's own size)."""
        # (Imported here: these are only needed for a 2x picture.)
        from OpenGL import GL
        from PyQt6.QtCore import QSize
        from PyQt6.QtOpenGL import QOpenGLFramebufferObject, QOpenGLFramebufferObjectFormat
        v = self.view
        dpr = v.devicePixelRatioF()  # (the grab is in device pixels, so the 2x is of those)
        size = QSize(int(round(v.width() * dpr * 2)), int(round(v.height() * dpr * 2)))
        fmt = QOpenGLFramebufferObjectFormat()
        fmt.setAttachment(QOpenGLFramebufferObject.Attachment.CombinedDepthStencil)  # the scene is drawn with depth testing
        fmt.setSamples(4)  # (the view's own antialiasing, so the big picture is not jagged)
        saved = self._overlays_off() if clean else None
        v.makeCurrent()
        try:
            fbo = QOpenGLFramebufferObject(size, fmt)
            if not fbo.isValid() or not fbo.bind():
                return None
            # Put back what was bound and the viewport, whatever the drawing did, so the view itself is unchanged.
            prev_fbo = int(np.ravel(GL.glGetIntegerv(GL.GL_FRAMEBUFFER_BINDING))[0])
            prev_view = [int(x) for x in np.ravel(GL.glGetIntegerv(GL.GL_VIEWPORT))]
            try:
                GL.glViewport(0, 0, size.width(), size.height())
                v.paintGL()  # the same drawing as on screen, into the bigger buffer
                img = fbo.toImage()
            finally:
                fbo.release()
                GL.glBindFramebuffer(GL.GL_FRAMEBUFFER, prev_fbo)
                GL.glViewport(*prev_view)
            del fbo  # (its buffers are freed now, while the view's context is still current)
            return img
        except Exception as e:  # (a graphics driver that refuses the buffer: the 1x picture is still taken)
            self.log_error('drawing the 2x screenshot', e)
            return None
        finally:
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
            try:
                frames[0].save(path, save_all=True, append_images=frames[1:], duration=durations, loop=0, disposal=1)
            except (OSError, ValueError) as e:
                self.log_error(f'saving the GIF {path}', e)
                self.toast('The GIF could not be saved', f'{path}\n{e}', error=True)
                return
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
        frame = Image.fromarray(arr.copy()).quantize(colors=256, method=Image.Quantize.MEDIANCUT, dither=Image.Dither.NONE)
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
        self.job_log.appendPlainText(f'\n{title}: could not start {self._python()} ({self.process.errorString()}).')
        self.toast(f'{title}: could not start', f'{self._python()} was not found: Python has to be installed (and on PATH) '
                                                 'for the game jobs.', error=True)

    def _job_done(self, title, code):
        self.job_log.appendPlainText(f'\n{title}: finished ({code}).')
        if 'Export' in title:
            self.load_weapon_list(keep_open=True)
        if code != 0:
            tail = [ln for ln in self.job_log.toPlainText().splitlines() if ln.strip()][-6:]
            self.toast(f'{title}: it did not work', '\n'.join(tail[-5:]) + '\n(the whole output: Advanced, under the Game card)', error=True)
        else:
            self.toast(f'{title}: finished', 'The weapons are read again; the open weapon and its hands stay as they were.'
                       if 'Export' in title else 'Done.')

    def export_weapons(self):
        names = self.export_names.text().strip()
        self._run([os.path.join(self.repo, 'tools', 'sandbox', 'export.py'), '--game', self.game_folder()]
                  + (['--weapons', names] if names else []), 'Export weapons')

    def test_in_game(self):
        w = self.scene.weapon
        if w is None:
            return
        self._run([os.path.join(self.repo, 'tools', 'vrshots.py'), '--game', self.game_folder(), '--weapon', '=' + w.name,
                   '--out', 'sandbox-' + safe(w.name)],
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
            wb.rec_btn.setChecked(False)  # (the recording so far is saved, not dropped)
        if not wb.selftest:
            wb.settings.setValue('geometry', self.saveGeometry())
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
        geometry = QSettings('RavenfieldVR', 'Sandbox').value('geometry')
        if isinstance(geometry, QByteArray) and '--selftest' not in sys.argv and '--shot' not in sys.argv:
            self.restoreGeometry(geometry)  # where it was last time (Qt keeps it on a screen that still exists)


def error_log_path():
    """Where errors are written: next to the captures when the repository is known, else in the user's local app data."""
    repo = find_repo()
    base = os.path.join(repo, 'reports', 'sandbox') if repo else os.path.join(os.environ.get('LOCALAPPDATA', HERE), 'RavenfieldVR')
    os.makedirs(base, exist_ok=True)
    return os.path.join(base, 'sandbox-errors.log')


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
        test = SelfTest(win, app, report, want)
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
    os._exit(code or 0)


if __name__ == '__main__':
    main()
