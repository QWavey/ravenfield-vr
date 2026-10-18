"""The sandbox checking itself: every feature run as a user would (`main.py --selftest report.txt [--weapon NAME]`), each
step written PASS or FAIL with what it saw. It saves into folders of its own (reports/sandbox/selftest): the user's saved
poses and the game's are never touched (it deleted them and put them back only at the end before, review B3#0/B8#0)."""
import json
import math
import os
import shutil
import time

import numpy as np
from PyQt6.QtCore import QEvent, QPointF, Qt, QTimer
from PyQt6.QtGui import QGuiApplication, QKeyEvent, QMouseEvent

import gizmo as gz
from mathutil import quat_axis_angle, quat_inv, quat_mul, v3


class SelfTest:
    def __init__(self, win, app, report, want=None, all_weapons=False):
        self.win = win
        self.wb = win.workbench
        self.app = app
        self.report = report
        self.want = want
        self.all_weapons = all_weapons  # --all: every exported weapon opened once at the end
        self.lines = []
        self.passed = 0
        self.failed = 0
        self.skipped = 0  # checks that could not run here (a part this gun does not have, a feature not built yet): not passed
        self.backups = {}
        self.made = []
        self.shot = None  # the screenshot and the GIF taken in this run (the Captures page must list them)
        self.gif = None
        self.steps = [self.s_list, self.s_open, self.s_hands, self.s_edit, self.s_save, self.s_reimport, self.s_export,
                      self.s_send_remove, self.s_bad_import, self.s_undo, self.s_draft, self.s_reload_start, self.s_reload_wait,
                      self.s_laser, self.s_screenshot, self.s_gif_start, self.s_gif_stop, self.s_error_raise, self.s_error_check,
                      self.s_reset, self.s_snap_support, self.s_snap_rack, self.s_snap_magazine, self.s_snap_cover,
                      self.s_slide_slider, self.s_mag_slider, self.s_cover_slider, self.s_hide_show, self.s_parts_list,
                      self.s_numbers, self.s_copy_pose, self.s_views, self.s_frame, self.s_keys, self.s_captures,
                      self.s_move_weapon, self.s_gizmo_move, self.s_gizmo_snap_plane, self.s_gizmo_turn, self.s_gizmo_cancel,
                      self.s_gizmo_local, self.s_gizmo_gun, self.s_gizmo_buttons, self.s_gizmo_keys, self.s_gizmo_free, self.s_put_hand, self.s_copy_hands, self.s_put_hand_menu, self.s_clean_and_big,
                      self.s_turntable_start, self.s_turntable_check, self.s_record_reload_start, self.s_record_reload_check,
                      self.s_later_features] + ([self.s_all_weapons] if self.all_weapons else []) + [self.s_cleanup]
        # Its own folders: the user's poses (tools/sandbox/poses) and the game's are never read or written by the test.
        self.root = os.path.join(self.wb.repo, 'reports', 'sandbox', 'selftest')
        shutil.rmtree(self.root, ignore_errors=True)
        self.wb.poses_dir = os.path.join(self.root, 'poses')
        self.wb.game_dir_override = os.path.join(self.root, 'game-poses')
        os.makedirs(self.wb.poses_dir, exist_ok=True)
        os.makedirs(self.wb.game_dir_override, exist_ok=True)
        self.wb.drafts.clear()
        self.index = 0
        self.saved = None

    def check(self, name, ok, detail=''):
        self.lines.append(f"{'PASS' if ok else 'FAIL'}  {name}{(': ' + detail) if detail else ''}")
        if ok:
            self.passed += 1
        else:
            self.failed += 1

    def skip(self, name, why):
        """A check that could not run here: its line says SKIP, and it is counted as neither passed nor failed (a SKIP
        written as PASS hid the checks that did not run)."""
        self.lines.append(f'SKIP  {name}: {why}')
        self.skipped += 1

    def start(self):
        QTimer.singleShot(800, self.next)

    def next(self, delay=150):
        if self.index >= len(self.steps):
            self.finish()
            return
        step = self.steps[self.index]
        self.index += 1
        try:
            wait = step()
        except Exception as e:  # a step that throws is a failure, the rest still run
            self.check(step.__name__, False, f'{type(e).__name__}: {e}')
            wait = None
        QTimer.singleShot(wait if wait is not None else delay, self.next)

    def finish(self):
        code = 1 if self.failed else 0
        try:
            text = (f"Ravenfield VR Sandbox self-test, {time.strftime('%Y-%m-%d %H:%M:%S')}: {self.passed} passed, {self.failed} failed, "
                    f"{self.skipped} skipped\n" + '\n'.join(self.lines) + '\n')
            try:
                with open(self.report, 'w', encoding='utf-8') as f:
                    f.write(text)
            except OSError as e:
                code = 2  # (no report: said by the exit code, not a run left hanging)
                text += f'\nTHE REPORT COULD NOT BE WRITTEN: {e}\n'
            out = __import__('sys').stdout
            if out is not None:
                # (A console that cannot show a character, cp1252 and the readout's degree sign or a Chinese part name,
                # threw here, and the error message it raised kept the program open after the test; the report is the file.)
                enc = getattr(out, 'encoding', None) or 'utf-8'
                try:
                    print(text.encode(enc, 'replace').decode(enc, 'replace'))
                except (OSError, ValueError, LookupError):
                    pass
        finally:
            self.win.close()  # frees the GPU resources as a user's close does
            self.app.exit(code)

    # ---- steps ---------------------------------------------------------------------------------------------------------

    def s_list(self):
        n = len(self.wb.items)
        self.check('weapons listed', n > 0, f'{n} weapon(s)')

    def s_open(self):
        items = self.wb.items
        pick = None
        if self.want:
            pick = next((it for it in items if self.want.lower() in it.text().lower() or self.want.lower() in str(it.data(Qt.ItemDataRole.UserRole)).lower()), None)
        pick = pick or items[0]
        wid = pick.data(Qt.ItemDataRole.UserRole)
        if self.want and self.want.lower() not in (pick.text() + ' ' + wid).lower():
            self.check(f'the weapon asked for ({self.want}) is in the list', False, f'opened {wid} instead')
        self.wb.weapon_list.setCurrentItem(pick)
        w = self.wb.scene.weapon
        self.check('a weapon just opened is not marked as changed (closing does not ask for nothing)', not self.wb.unsaved())
        self.check('weapon opened', w is not None and w.id == wid, f"{wid}: {len(w.nodes) if w else 0} parts, {w.stand_ins if w else 0} stand-ins")

    def s_hands(self):
        wb, s = self.wb, self.wb.scene
        wb.hands_on_gun()
        w = s.weapon
        g = w.to_gun(s.right.position)[:3]
        settle = np.linalg.norm(s.right.drawn_position() - s.right.position)
        self.check('right hand on the grip', np.linalg.norm(g - w.grip) < 1e-6 and settle < 0.041,
                   f'{np.linalg.norm(g - w.grip) * 1000:.3f} mm from the grip point, the glove drawn {settle * 100:.1f} cm off it (settled)')
        self.check('a snapped grip keeps the game\'s thumb', not s.right.thumb_set)
        self.check('left hand on the gun', s.left.role == 'support', f'job {s.left.role!r}')

    def s_edit(self):
        wb, s = self.wb, self.wb.scene
        h = s.right
        wb.select_hand(h)
        wb.pose.setCurrentText('Fist')
        if h.pose_name != 'Fist':
            wb.on_pose_chosen('Fist')
        ok_pose = h.pose_name == 'Fist' and abs(h.thumb - 0.8) < 1e-6
        h.position = h.position + np.array([0.01, 0.005, -0.003])
        h.rotation = quat_mul(quat_axis_angle([0, 1, 0], 7.0), h.rotation)
        wb.finger_sliders[1].slider.setValue(42)
        self.edited = (h.position.copy(), h.rotation.copy(), list(h.curls), h.thumb)
        self.check('pose and fingers changed', ok_pose and abs(h.curls[1] - 0.42) < 1e-6, f'pose {h.pose_name}, index {h.curls[1]:.2f}')

    def s_save(self):
        wb = self.wb
        if not wb.unsaved():
            self.check('a changed pose is marked as not saved', False)
        path = wb.save_pose()
        self.made.append(path)
        game = wb.game_poses_dir()
        gpath = os.path.join(game, wb.scene.weapon.id + '.json') if game else None
        ok = path and os.path.exists(path)
        doc = json.load(open(path, encoding='utf-8')) if ok else {}
        r = (doc.get('hands') or {}).get('right') or {}
        fields = len((r.get('mod') or {}).get('position', [])) == 3 and len((r.get('mod') or {}).get('rotation', [])) == 4 and len(r.get('curls', [])) == 5
        self.check('pose saved', bool(ok and fields), path or 'no file')
        self.check('a thumb posed by hand goes to the game', r.get('thumbSet') is True, f"thumbSet {r.get('thumbSet')!r}")
        self.check('pose saved for the game', bool(game) and os.path.exists(gpath), gpath or 'no game folder')
        self.check('the game copy is the same', bool(gpath) and os.path.exists(gpath) and json.load(open(gpath, encoding='utf-8')) == doc)
        self.check('saved: no longer marked as changed', not wb.unsaved())
        self.saved = doc

    def s_reimport(self):
        wb, s = self.wb, self.wb.scene
        wb.hands_on_gun()  # the hands moved away from the saved pose
        moved = np.linalg.norm(s.right.position - self.edited[0]) > 0.005
        ok = wb.import_pose(wb.pose_path())
        h = s.right
        dp = np.linalg.norm(h.position - self.edited[0])
        dq = 1 - abs(float(np.dot(h.rotation, self.edited[1])))
        dc = max(abs(a - b) for a, b in zip(h.curls, self.edited[2]))
        self.check('pose imported back exactly', ok and moved and dp < 1e-6 and dq < 1e-9 and dc < 1e-4,
                   f'place {dp * 1000:.4f} mm, turn {dq:.1e}, fingers {dc:.1e}')

    def s_export(self):
        wb = self.wb
        out = os.path.join(wb.captures, 'selftest-export-pose.json')
        path = wb.export_pose(out)
        self.made.append(path)
        a = json.load(open(path, encoding='utf-8'))
        b = dict(self.saved)
        a.pop('saved', None)
        b.pop('saved', None)

        def diff(x, y, at=''):
            if isinstance(x, dict) and isinstance(y, dict):
                out = []
                for k in set(x) | set(y):
                    out += diff(x.get(k), y.get(k), f'{at}.{k}')
                return out
            if isinstance(x, list) and isinstance(y, list) and len(x) == len(y):
                return [d for i in range(len(x)) for d in diff(x[i], y[i], f'{at}[{i}]')]
            if isinstance(x, float) and isinstance(y, float):
                return [] if abs(x - y) < 1e-6 else [f'{at}: {x} vs {y}']
            return [] if x == y else [f'{at}: {x!r} vs {y!r}']
        d = diff(a, b)
        self.check('pose exported (the same as saved)', not d, path if not d else '; '.join(d[:6]))

    def s_send_remove(self):
        wb = self.wb
        game = wb.game_poses_dir()
        target = os.path.join(game, wb.scene.weapon.id + '.json')
        wb.remove_from_game()
        gone = not os.path.exists(target)
        status_off = "mod's own grip" in wb.game_status.text()
        wb.send_to_game()
        back = os.path.exists(target)
        status_on = 'saved pose' in wb.game_status.text()
        self.check('remove from and send to the game', gone and back and status_off and status_on, wb.game_status.text())

    def s_bad_import(self):
        wb, s = self.wb, self.wb.scene
        before = (s.right.position.copy(), list(s.right.curls), s.left.position.copy())
        bad = os.path.join(self.root, 'bad-pose.json')
        with open(bad, 'w', encoding='utf-8') as f:
            json.dump({'hands': {'right': {'gun': {'position': [0, 0, 0], 'rotation': [0, 0, 0, 1]}, 'curls': 'x'},
                                 'left': {'gun': {'position': [1, 2], 'rotation': [0, 0, 0, 1]}}}}, f)
        wb.expect_error = 'a broken pose file, on purpose'
        ok = wb.import_pose(bad)
        wb.expect_error = ''
        same = (np.allclose(before[0], s.right.position) and before[1] == list(s.right.curls) and np.allclose(before[2], s.left.position))
        self.check('a bad pose file is refused and changes nothing', ok is False and same)

    def s_undo(self):
        wb, s = self.wb, self.wb.scene
        h = s.right
        p0 = h.position.copy()
        wb.push_undo()  # as a drag's start does
        h.position = h.position + np.array([0.03, 0.0, 0.0])
        wb.on_hand_moved(h)
        moved = h.position.copy()
        wb.undo()
        back = np.allclose(h.position, p0)
        wb.redo()
        again = np.allclose(h.position, moved)
        wb.undo()
        self.check('undo and redo a hand move', back and again and np.allclose(h.position, p0))

    def s_draft(self):
        wb, s = self.wb, self.wb.scene
        w0 = s.weapon.id
        h = s.right
        wb.push_undo()
        h.position = h.position + np.array([0.0, 0.02, 0.0])
        wb.on_hand_moved(h)
        moved = s.weapon.to_gun(h.position)[:3].copy()
        other = next((it for it in wb.items if it.data(Qt.ItemDataRole.UserRole) != w0), None)
        if other is None:
            self.skip('unsaved hands kept when another weapon is opened', 'only one weapon exported')
            return
        wb.weapon_list.setCurrentItem(other)
        marked = any(it.data(Qt.ItemDataRole.UserRole) == w0 and 'not saved' in it.text() for it in wb.items)
        wb.weapon_list.setCurrentItem(next(it for it in wb.items if it.data(Qt.ItemDataRole.UserRole) == w0))
        kept = s.weapon.id == w0 and np.allclose(s.weapon.to_gun(s.right.position)[:3], moved, atol=1e-6)
        self.check('unsaved hands kept when another weapon is opened', marked and kept,
                   f'marked {marked}, back on {s.weapon.id}, {np.linalg.norm(s.weapon.to_gun(s.right.position)[:3] - moved) * 1000:.3f} mm')
        wb.revert_to_saved()
        self.check('revert to the saved pose', not wb.unsaved())

    def s_reload_start(self):
        w = self.wb.scene.weapon
        self.has_reload = bool(w.rig.get('magazine') or w.rig.get('slide'))
        # A part the user hid stays hidden through the reload (it came back when the magazine seated).
        self.user_hidden = next((n for n in w.nodes if n.role == 'body'), None) if len(w.nodes) > 1 else None
        if self.user_hidden is not None:
            self.user_hidden.hidden = True
        self.fresh_seen = False
        self.wb.play_reload()
        if self.has_reload:
            self.check('reload started', self.wb.timeline.running)
        else:
            self.skip('reload started', 'no magazine or slide that moves on this gun')
        return 300

    def s_reload_wait(self):
        tl = self.wb.timeline
        deadline = time.time() + 8
        w = self.wb.scene.weapon
        while tl.running and time.time() < deadline:
            self.app.processEvents()
            if w.fresh is not None and self.wb.scene.extra_draws():
                self.fresh_seen = True
            time.sleep(0.01)
        ok = not tl.running and w.fresh is None and abs(w.mag) < 1e-6 and abs(w.slide) < 1e-6 and abs(w.cover) < 1e-6 and not any(n.away for n in w.nodes)
        self.check('reload played to the end, parts home', ok, f'magazine {w.mag:.2f}, slide {w.slide:.2f}, away {sum(n.away for n in w.nodes)}')
        if w.rig.get('magazine'):
            self.check('the fresh magazine is drawn while it goes in', self.fresh_seen)
        else:
            self.skip('the fresh magazine is drawn while it goes in', 'no magazine on this gun')
        if self.user_hidden is not None:
            self.check('a part hidden by the user stays hidden through the reload', self.user_hidden.hidden, self.user_hidden.short)
            self.user_hidden.hidden = False
        else:
            self.skip('a part hidden by the user stays hidden through the reload', 'the gun has one part only')

    def s_laser(self):
        wb = self.wb
        w = wb.scene.weapon
        v = wb.view
        wb.laser_btn.setChecked(True)
        node = next((n for n in w.nodes if n.role in ('slide', 'handle') and not n.hidden), None) or max(
            (n for n in w.nodes if not n.hidden), key=lambda n: float(np.prod(np.maximum(n.bmax - n.bmin, 1e-4))))
        world = w.to_world((node.bmin + node.bmax) * 0.5 + node.sim[:3, 3])
        clip_pos = (v.proj_matrix() @ v.view_matrix()) @ np.append(world, 1.0)
        ndc = clip_pos[:3] / clip_pos[3]
        pos = QPointF((ndc[0] + 1) * 0.5 * v.width(), (1 - ndc[1]) * 0.5 * v.height())
        wb.picked_point = None
        for kind in (QEvent.Type.MouseButtonPress, QEvent.Type.MouseButtonRelease):
            ev = QMouseEvent(kind, pos, v.mapToGlobal(pos), Qt.MouseButton.LeftButton,
                             Qt.MouseButton.LeftButton if kind == QEvent.Type.MouseButtonPress else Qt.MouseButton.NoButton,
                             Qt.KeyboardModifier.NoModifier)
            QGuiApplication.sendEvent(v, ev)
        hit = wb.picked_point is not None and wb.picked_node is not None
        text = wb.point_text() if hit else ''
        wb.copy_point()
        clip = QGuiApplication.clipboard().text()
        self.check('laser: a click on the gun picks a part through the view', hit, f"{wb.picked_node.short if hit else 'nothing'}")
        self.check('laser readout and copy', bool(text) and clip == text and ' cm from ' in text, text[:140])
        wb.laser_btn.setChecked(False)

    def s_screenshot(self):
        path = self.wb.screenshot()
        self.made.append(path)
        self.shot = path
        self.check('screenshot saved', os.path.exists(path) and os.path.getsize(path) > 10000, f'{path} ({os.path.getsize(path) // 1024} KB)')

    def s_gif_start(self):
        self.gif_before = set(f for f in os.listdir(self.wb.captures) if f.endswith('.gif'))
        self.wb.rec_btn.setChecked(True)
        self.wb.play_rack()
        # Something moving even on a gun without moving parts: frames that do not change are merged into one by the GIF writer.
        self.orbit = QTimer()
        self.orbit.timeout.connect(self._orbit)
        self.orbit.start(40)
        return 1500

    def _orbit(self):
        self.wb.view.yaw += 2.0
        self.wb.view.update()

    def s_gif_stop(self):
        self.orbit.stop()
        self.wb.rec_btn.setChecked(False)
        new = [f for f in os.listdir(self.wb.captures) if f.endswith('.gif') and f not in self.gif_before]
        ok = bool(new)
        frames = 0
        if ok:
            path = os.path.join(self.wb.captures, new[0])
            self.made.append(path)
            self.gif = path
            from PIL import Image
            with Image.open(path) as g:
                frames = getattr(g, 'n_frames', 1)
        self.check('GIF recorded', ok and frames >= 5, f'{new[0] if new else "none"}: {frames} frames')

    def _error_log(self):
        import sys as _sys
        main = _sys.modules.get('__main__')
        return main.error_log_path() if main is not None and hasattr(main, 'error_log_path') else None

    def s_error_raise(self):
        path = self._error_log()
        self.error_before = os.path.getsize(path) if path and os.path.exists(path) else 0

        self.wb.expect_error = 'an error on purpose'

        def boom():
            raise RuntimeError('self-test: a deliberate error in a button (the program must go on)')
        QTimer.singleShot(0, boom)
        return 400

    def s_error_check(self):
        path = self._error_log()
        size = os.path.getsize(path) if path and os.path.exists(path) else 0
        self.wb.expect_error = ''
        self.check('an error in the program is logged and the program goes on', size > self.error_before, path or 'no log path')

    # ---- the controls and the parts, each as the user reaches them (the buttons and keys call these same methods) -----------

    # The features other branches add now, with the Workbench method each would have. Until that method exists the check is
    # a SKIP line; once it is merged, the check for it is written here (a SKIP still shows it is not checked yet).
    LATER = ()

    def _settle(self):
        """A reload still playing is run to its end first: the checks look at the parts as they are."""
        self.wb.settle_timeline()

    def _key(self, widget, key, text):
        """A key pressed and let go, sent to the widget as the keyboard sends it."""
        QGuiApplication.sendEvent(widget, QKeyEvent(QEvent.Type.KeyPress, key, Qt.KeyboardModifier.NoModifier, text))
        QGuiApplication.sendEvent(widget, QKeyEvent(QEvent.Type.KeyRelease, key, Qt.KeyboardModifier.NoModifier, text))

    def _hide(self, node):
        """Hides a part as the context menu's Hide this part does: the part is picked first (as the laser picks it), then hidden."""
        w = self.wb.scene.weapon
        point = w.to_world((node.bmin + node.bmax) * 0.5 + node.sim[:3, 3])
        self.wb.on_pick(point, node)
        self.wb.hide_picked()

    def s_reset(self):
        """Reset: the parts go home, the parts the user hid show again, and the hands go back on the gun."""
        wb, s = self.wb, self.wb.scene
        w = s.weapon
        self._settle()
        wb.s_slide.slider.setValue(60)  # (the sliders move the parts; Reset must bring every one of them back)
        wb.s_mag.slider.setValue(40)
        wb.s_cover.slider.setValue(30)
        self._hide(w.nodes[0])
        s.right.position = s.right.position + np.array([0.04, 0.0, 0.0])  # the hand taken off the gun
        wb.reset_parts()
        home = abs(w.slide) < 1e-9 and abs(w.mag) < 1e-9 and abs(w.cover) < 1e-9 and not any(n.away for n in w.nodes)
        shown = not any(n.hidden for n in w.nodes)
        self.check('Reset: the parts go home and every part shows', home and shown,
                   f'slide {w.slide:.2f}, magazine {w.mag:.2f}, cover {w.cover:.2f}, hidden {sum(n.hidden for n in w.nodes)}')
        on_grip = bool(np.linalg.norm(w.to_gun(s.right.position)[:3] - w.grip) < 1e-6)
        self.check('Reset: the right hand back on the grip, the left on the handguard',
                   on_grip and s.right.on_part == 'grip' and s.left.on_part == 'handguard',
                   f'right on {s.right.on_part!r}, left on {s.left.on_part!r}')

    def s_snap_support(self):
        """Left hand to Handguard: on the support point (or, on a gun with none, the spot ahead of the grip)."""
        wb, s = self.wb, self.wb.scene
        w = s.weapon
        r = w.rig
        self._settle()
        wb.snap_left('support')
        at = v3(r['support']) if r.get('hasSupport') else w.grip + w.aim * 0.2
        off = float(np.linalg.norm(w.to_gun(s.left.position)[:3] - at))
        where = 'the support point' if r.get('hasSupport') else 'the spot ahead of the grip'
        self.check('Left hand to Handguard: on the handguard', s.left.on_part == 'handguard' and off < 1e-6,
                   f"{off * 1000:.3f} mm from {where}, on {s.left.on_part!r}")

    def s_snap_rack(self):
        """Left hand to Slide: on the slide or handle, 2 cm off its rack point (as the reload's hand is)."""
        wb, s = self.wb, self.wb.scene
        w = s.weapon
        self._settle()
        if not wb._has_rack():  # (the test main.py makes before it snaps)
            self.skip('Left hand to Slide: on the slide or handle', 'this gun has no slide or handle that moves')
            return
        wb.snap_left('rack')
        dist = float(np.linalg.norm(w.to_gun(s.left.position)[:3] - w.rack_point()))
        self.check('Left hand to Slide: on the slide or handle',
                   s.left.role == 'rack' and s.left.on_part == 'slide / handle' and abs(dist - 0.02) < 1e-4,
                   f'{dist * 100:.2f} cm from the rack point, job {s.left.role!r}')
        # On a handle on the side the fingers go up over it, as the game's rack hold (RackHandPose) takes it: the glove's
        # fingers are its -Z (real2 I6#3: the hand hung upside down, fingers down from above).
        off = w.rack_point() - w.muzzle
        off = off - w.aim * float(np.dot(off, w.aim))
        out = off / max(1e-9, float(np.linalg.norm(off)))
        if float(np.linalg.norm(off)) > 0.015 and float(np.dot(w.dir_to_world(out), w.dir_to_world(w.up))) <= 0.7:
            from mathutil import quat_to_mat3
            fingers = quat_to_mat3(s.left.rotation) @ np.array([0.0, 0.0, -1.0])
            up = float(np.dot(fingers, w.dir_to_world(w.up)))
            self.check('Left hand to Slide on a side handle: the fingers up over it, as the game holds it', up > 0.5, f'fingers {up:+.2f} along up')
        else:
            self.skip('Left hand to Slide on a side handle: the fingers up over it', "this gun's handle is on top")

    def s_snap_magazine(self):
        """Left hand to Magazine: at the magazine's place, between its half width and its length from its centre."""
        wb, s = self.wb, self.wb.scene
        w = s.weapon
        self._settle()
        if not w.rig.get('magazine'):
            self.skip('Left hand to Magazine: on the magazine', 'this gun has no magazine in the analysis')
            return
        wb.snap_left('magazine')
        half = float(np.max(w.mag_size)) * 0.5
        dist = float(np.linalg.norm(w.to_gun(s.left.position)[:3] - w.mag_center()))
        self.check('Left hand to Magazine: on the magazine',
                   s.left.role == 'magazine' and s.left.on_part == 'magazine' and half <= dist <= w.mag_length,
                   f"{dist * 100:.1f} cm from the magazine's centre, job {s.left.role!r}")

    def s_snap_cover(self):
        """Left hand to Cover: on the latch of the cover or barrels, where the cover is now (with the cover shut here)."""
        wb, s = self.wb, self.wb.scene
        w = s.weapon
        r = w.rig
        self._settle()
        if r.get('hingeKind', 'None') == 'None':  # (the test snap_left makes before it snaps)
            self.skip('Left hand to Cover: on the latch', 'this gun has no cover or barrels that open')
            return
        wb.snap_left('cover')
        latch = w.hinge_point(v3(r['hingeGrab']) + w.up * 0.02)
        off = float(np.linalg.norm(w.to_gun(s.left.position)[:3] - latch))
        self.check('Left hand to Cover: on the latch', s.left.role == 'cover' and s.left.on_part == 'cover' and off < 1e-6,
                   f"{off * 1000:.3f} mm from the latch, job {s.left.role!r}")

    def s_slide_slider(self):
        """The Slide slider at 50% moves the slide, its companions and its handle along their travel."""
        wb, w = self.wb, self.wb.scene.weapon
        self._settle()
        wb.reset_parts()
        wb.s_slide.slider.setValue(50)
        movers = [n for n in w.nodes if n.role in ('slide', 'companion', 'bolt', 'handle')]
        if not movers:
            self.skip('Slide slider: moves the slide', 'no slide, bolt or handle part in the analysis')
        else:
            moved = max(float(np.linalg.norm(n.sim[:3, 3])) for n in movers)
            self.check('Slide slider at 50% moves the slide', abs(w.slide - 0.5) < 1e-9 and moved > 0.001,
                       f'slide {w.slide:.2f}, its parts {moved * 100:.1f} cm along their travel')
        wb.reset_parts()

    def s_mag_slider(self):
        """The Magazine slider at 50% draws the magazine out along its line, from its seat (and the parked clip's place)."""
        wb, w = self.wb, self.wb.scene.weapon
        self._settle()
        wb.reset_parts()
        mags = [n for n in w.nodes if n.role == 'magazine']
        if not w.rig.get('magazine') or not mags:
            self.skip('Magazine slider: draws the magazine out', 'no magazine part in the analysis')
            return
        wb.s_mag.slider.setValue(50)
        want = w.mag_eject() * w.mag_length * 0.5 + w.mag_parked
        off = max(float(np.linalg.norm(n.sim[:3, 3] - want)) for n in mags)
        self.check('Magazine slider at 50% draws the magazine out along its line', abs(w.mag - 0.5) < 1e-9 and off < 1e-6,
                   f'magazine {w.mag:.2f}, {off * 1000:.3f} mm from its line')
        wb.reset_parts()

    def s_cover_slider(self):
        """The Cover slider at 50% opens the cover (or the barrels) on its hinge."""
        wb, w = self.wb, self.wb.scene.weapon
        self._settle()
        wb.reset_parts()
        if w.rig.get('hingeKind', 'None') == 'None' or not float(w.rig.get('hingeAngle', 0) or 0):
            self.skip('Cover slider: opens the cover', 'this gun has no cover or barrels that open (or no angle to open them by)')
            return
        wb.s_cover.slider.setValue(50)
        turned = float(np.linalg.norm(w.hinge_m[:3, :3] - np.eye(3)))
        hinged = [n for n in w.nodes if n.role == 'hinge']
        parts_turned = not hinged or any(float(np.linalg.norm(n.sim[:3, :3] - np.eye(3))) > 1e-3 for n in hinged)
        self.check('Cover slider at 50% opens the cover on its hinge',
                   abs(w.cover - 0.5) < 1e-9 and turned > 1e-3 and parts_turned, f'cover {w.cover:.2f}, turned {turned:.3f}')
        wb.reset_parts()

    def s_hide_show(self):
        """Hide this part hides the part picked; Show every part shows all the parts again."""
        wb, w = self.wb, self.wb.scene.weapon
        self._settle()
        wb.reset_parts()
        node = w.nodes[-1]
        self._hide(node)
        hidden = bool(node.hidden) and sum(n.hidden for n in w.nodes) == 1
        wb.show_all_parts()
        shown = not any(n.hidden for n in w.nodes)
        self.check('Hide this part hides it; Show every part shows all again', hidden and shown,
                   f"'{node.short}' hidden {hidden}, all shown after {shown}")

    def s_parts_list(self):
        """A part chosen in the parts list is the one the view highlights (the first and the last line)."""
        wb, w = self.wb, self.wb.scene.weapon
        self._settle()
        rows = wb.parts.count()
        if not rows:
            self.skip('the parts list', 'no parts listed for this gun')
            return
        self.check('the parts list has one line per part', rows == len(w.nodes), f'{rows} lines, {len(w.nodes)} parts')
        for row in sorted({0, rows - 1}):
            wb.parts.setCurrentRow(-1)  # (so that choosing the row changes the choice, whatever it was)
            wb.parts.setCurrentRow(row)
            item = wb.parts.item(row)
            node = next((n for n in w.nodes if id(n) == item.data(Qt.ItemDataRole.UserRole)), None)
            self.check(f'a part chosen in the list (line {row + 1}) is the one the view highlights',
                       node is not None and wb.view.part_node is node, node.short if node is not None else 'no part for that line')

    def s_numbers(self):
        """The Advanced number boxes: a typed forward (cm) moves the selected hand along the barrel from the grip, and the
        up and right boxes keep the hand where it is on those."""
        wb, s = self.wb, self.wb.scene
        w = s.weapon
        self._settle()
        wb.reset_parts()
        wb.select_hand(s.right)
        box = wb.num[0][0]
        target = round(box.value() + 6.0, 1)
        # Typed in the box's own number format (a German PC takes 6,0, and '6.0' there reads as 60): then taken as the box
        # takes it when it is left.
        box.lineEdit().setText(box.locale().toString(float(target), 'f', 1))
        box.interpretText()
        p = w.to_gun(s.right.position)[:3] - w.grip
        along = float(np.dot(p, w.aim)) * 100
        up = float(np.dot(p, w.up)) * 100
        right = float(np.dot(p, w.right)) * 100
        ok = abs(along - target) < 0.01 and abs(up) < 0.01 and abs(right) < 0.01 and abs(box.value() - target) < 1e-9
        self.check('Advanced: a typed forward (cm) moves the selected hand along the barrel from the grip', ok,
                   f'typed {target:.1f} cm, the hand {along:.2f} cm forward, {up:.2f} up, {right:.2f} right')
        wb.reset_parts()

    def s_copy_pose(self):
        """Copy this pose to the other hand: the selected hand's pose, fingers and thumb go to the other hand."""
        wb, s = self.wb, self.wb.scene
        self._settle()
        wb.reset_parts()
        wb.select_hand(s.right)
        wb.on_pose_chosen('Fist')
        wb.finger_sliders[2].slider.setValue(63)  # a finger of its own, so the copy is not only of a named pose
        wb.copy_pose_to_other()
        r, l = s.right, s.left
        ok = (np.allclose(l.curls, r.curls, atol=1e-9) and abs(l.thumb - r.thumb) < 1e-9 and l.pose_name == r.pose_name
              and l.thumb_set == r.thumb_set)
        self.check('Advanced: Copy this pose to the other hand', ok,
                   f'left {l.pose_name!r}, fingers {[round(c, 2) for c in l.curls]}')
        wb.reset_parts()

    def s_views(self):
        """The View card's presets: Side, Top, Front and 3/4 turn the camera; Top looks straight down (the pitch changes)."""
        v = self.wb.view
        v.set_view('side')
        side_pitch = v.pitch
        v.set_view('top')
        top_pitch = v.pitch
        self.check('View: Top looks down (the pitch changes from Side)', top_pitch == 89.0 and top_pitch != side_pitch,
                   f'pitch {side_pitch:.0f} from Side to {top_pitch:.0f} from Top')
        good = True
        for name, yaw, pitch in (('side', 0.0, 0.0), ('top', 0.0, 89.0), ('front', 90.0, 0.0), ('three', -25.0, 12.0)):
            v.set_view(name)
            good = good and abs(v.yaw - yaw) < 1e-9 and abs(v.pitch - pitch) < 1e-9
        self.check('View presets: Side, Top, Front and 3/4 set the camera', good, 'yaw and pitch as the presets give them')

    def s_frame(self):
        """Frame the gun (F, and the context menu's Frame the gun): the view centres on the gun and backs off to fit it."""
        wb, v = self.wb, self.wb.view
        w = self.wb.scene.weapon
        self._settle()
        c, r = w.center_world()
        v.target = c + np.array([0.5, 0.0, 0.0])
        v.dist = 3.0
        wb.frame_weapon(hands=False)  # (Shift + F: the gun alone)
        fit = max(0.15, max(0.12, r) / math.sin(math.radians(19)) * 1.05)  # (as the view frames it)
        self.check('Frame the gun alone (Shift + F): the view centres on it and backs off to fit', np.allclose(v.target, c) and abs(v.dist - fit) < 1e-9,
                   f'{v.dist:.3f} m away, fitting {fit:.3f} m')
        wb.frame_weapon()  # F: the gun and both hands
        reach = max(float(np.linalg.norm(h.position - v.target)) + 0.1 for h in self.wb.scene.hands())
        sphere = v.dist * math.sin(math.radians(19)) / 1.05
        self.check('Frame (F): the gun and both hands in view', reach <= sphere + 1e-6 and v.dist >= fit - 1e-9,
                   f'hands reach {reach * 100:.0f} cm from the centre, the view fits {sphere * 100:.0f} cm')

    def s_keys(self):
        """The keys: L toggles the laser, 1 and 2 pick the right and left hand, F frames the gun. Sent to the Workbench as the
        keyboard sends them; the shortcuts act only in the active window, so the window is brought to the front first."""
        wb, s, v = self.wb, self.wb.scene, self.wb.view
        w = s.weapon
        self._settle()
        self.win.activateWindow()
        self.app.processEvents()
        if self.app.activeWindow() is not self.win:
            self.skip('keys L, 1, 2 and F', 'the program is not the active window on this PC, so the keys are not sent to it')
            return
        wb.laser_btn.setChecked(False)
        self._key(wb, Qt.Key.Key_L, 'l')
        on = bool(wb.laser_btn.isChecked() and v.laser)
        self._key(wb, Qt.Key.Key_L, 'l')
        off = bool(not wb.laser_btn.isChecked() and not v.laser)
        self.check('key L toggles the laser pointer', on and off, f'on after one press {on}, off after the second {off}')
        self._key(wb, Qt.Key.Key_2, '2')
        to_left = v.selected_hand is s.left
        self._key(wb, Qt.Key.Key_1, '1')
        to_right = v.selected_hand is s.right
        self.check('keys 2 and 1 select the left and right hand', to_left and to_right, f'after 2 left {to_left}, after 1 right {to_right}')
        wb.frame_weapon()
        want = v.target.copy()
        v.target = want + np.array([0.5, 0.0, 0.0])
        self._key(wb, Qt.Key.Key_F, 'f')
        self.check('key F frames the gun and the hands', bool(np.allclose(v.target, want)))

    def s_captures(self):
        """The Captures page's refresh lists the screenshot and the GIF taken in this run."""
        cap = self.win.captures
        cap.refresh()

        def norm(p):  # (the paths compared as Windows writes them)
            return os.path.normcase(os.path.normpath(p))
        listed = {norm(cap.list.item(i).data(Qt.ItemDataRole.UserRole)) for i in range(cap.list.count())}
        taken = [p for p in (self.shot, self.gif) if p and os.path.exists(p)]
        if not taken:
            self.skip('Captures lists the screenshot and the GIF', 'no picture or GIF was taken in this run')
            return
        missing = [os.path.basename(p) for p in taken if norm(p) not in listed]
        self.check('Captures lists the screenshot and the GIF taken in this run', not missing,
                   f'{len(listed)} listed' + (f', not listed: {missing}' if missing else ''))

    def s_move_weapon(self):
        """Moving the weapon (Ctrl + drag): the weapon and the hands on it move by the same amount, the hands keep their
        places on the gun, and the weapon goes back the way it came."""
        wb, s = self.wb, self.wb.scene
        w = s.weapon
        self._settle()
        wb.reset_parts()
        place0 = w.P.copy()
        grip0 = w.to_world(w.grip)
        hands0 = [(h, h.position.copy(), w.to_gun(h.position)[:3].copy()) for h in s.hands()]
        t = np.array([0.08, -0.03, 0.05])
        step = np.eye(4)
        step[:3, 3] = t
        s.move_weapon(step)
        moved_gun = bool(np.allclose(w.to_world(w.grip) - grip0, t, atol=1e-9))
        moved_hands = all(np.allclose(h.position - p0, t, atol=1e-9) for h, p0, _ in hands0)
        same_places = all(np.allclose(w.to_gun(h.position)[:3], g0, atol=1e-6) for h, _, g0 in hands0)
        s.move_weapon(np.linalg.inv(step))
        back = bool(np.allclose(w.P, place0, atol=1e-9) and all(np.allclose(h.position, p0, atol=1e-9) for h, p0, _ in hands0))
        self.check('move the weapon: the hands go with it, keep their places on the gun, and it goes back',
                   moved_gun and moved_hands and same_places and back,
                   f'moved {np.linalg.norm(t) * 100:.1f} cm: weapon {moved_gun}, hands {moved_hands}, places kept {same_places}, back {back}')
        wb.view.update()

    # ---- the transform gizmo (Blender's arrows and rings) ------------------------------------------------------------------

    def _gizmo_ready(self, mode, space='global', target='hand', hand=None):
        """The view as a user would have it for a gizmo check: parts home, hands on the gun, the 3/4 view framed on them,
        this gizmo on the hand (or the gun). Returns (view, camera, centre, axes) or None."""
        wb, s, v = self.wb, self.wb.scene, self.wb.view
        self._settle()
        wb.reset_parts()
        wb.select_hand(hand or s.right)
        wb.set_gizmo_mode(mode)
        wb.set_gizmo_space(space)
        v.set_gizmo_target(target)
        wb.frame_weapon()
        v.set_view('three')
        self.app.processEvents()
        f = v.gizmo_frame()
        if f is None:
            return None
        return v, v.camera(), f[0], f[1]

    def _mouse(self, kind, x, y, buttons, mods=Qt.KeyboardModifier.NoModifier, button=Qt.MouseButton.LeftButton):
        v = self.wb.view
        pos = QPointF(float(x), float(y))
        QGuiApplication.sendEvent(v, QMouseEvent(kind, pos, v.mapToGlobal(pos), button, buttons, mods))

    def _drag(self, points, mods=Qt.KeyboardModifier.NoModifier, release=True):
        """A left-button drag through screen points (the first is the press), as the mouse sends it."""
        left = Qt.MouseButton.LeftButton
        self._mouse(QEvent.Type.MouseButtonPress, *points[0], left, mods)
        for p in points[1:]:
            self._mouse(QEvent.Type.MouseMove, *p, left, mods, Qt.MouseButton.NoButton)
        if release:
            self._mouse(QEvent.Type.MouseButtonRelease, *points[-1], Qt.MouseButton.NoButton, mods)

    @staticmethod
    def _px(p):
        """A screen point as the view gets it: Qt rounds an event's position to whole pixels."""
        return np.floor(np.asarray(p, dtype=np.float64) + 0.5)

    def _expect_axis(self, cam, c, a, press, release):
        """The move along axis a that a drag from the press to the release point (whole pixels) gives: the ray's nearest
        points on the axis line."""
        t0 = gz.line_param(*cam.ray(*self._px(press)), c, a)
        t1 = gz.line_param(*cam.ray(*self._px(release)), c, a)
        return a * (t1 - t0) if t0 is not None and t1 is not None else None

    def _expect_plane(self, cam, c, n, press, release):
        h0 = gz.plane_hit(*cam.ray(*self._px(press)), c, n, not cam.ortho)
        h1 = gz.plane_hit(*cam.ray(*self._px(release)), c, n, not cam.ortho)
        return h1 - h0 if h0 is not None and h1 is not None else None

    @staticmethod
    def _path(cam, a, b, n=8):
        """Screen points from world point a to world point b, in n steps (a hand's drag)."""
        return [tuple(cam.project(a + (b - a) * t)) for t in np.linspace(0.0, 1.0, n + 1)]

    def _arrow_grab(self, cam, c, axes, i, mode='move'):
        """A point on arrow i's shaft (60% out), in the world: where a user takes it. None when it faces the viewer."""
        _, length = gz.sizes(mode)
        if gz.axis_visibility(cam, c, axes[:, i], length) < 0.6:
            return None
        return c + axes[:, i] * length * 0.6 * cam.px_at(c)

    def s_gizmo_move(self):
        """Move gizmo, Global axes: dragging the red arrow 4 cm along it moves the right hand 4 cm along X and nothing else;
        the drag is one undo step; the readout says how far; the hand's job is worked out at the end."""
        wb, s = self.wb, self.wb.scene
        r = self._gizmo_ready('move')
        if r is None:
            self.skip('gizmo: move along an arrow', 'no gizmo on the right hand')
            return
        v, cam, c, axes = r
        h = s.right
        p0 = h.position.copy()
        undo0 = len(wb.undo_stack)
        texts = []
        v.gizmo_info.connect(texts.append)
        self.check('gizmo: on a hand it sits on the glove as drawn (settled onto the gun)',
                   np.allclose(c, h.drawn_position(), atol=1e-12), f'{np.linalg.norm(h.drawn_position() - h.position) * 100:.1f} cm settle')
        drawn0 = h.drawn_position().copy()
        i = next((k for k in range(3) if self._arrow_grab(cam, c, axes, k) is not None), None)
        if i is None:
            self.skip('gizmo: move along an arrow', 'every arrow faces the viewer here')
            return
        g = self._arrow_grab(cam, c, axes, i)
        hit = gz.hit(cam, c, axes, 'move', *cam.project(g))
        path = self._path(cam, g, g + axes[:, i] * 0.04)
        self._drag(path)
        v.gizmo_info.disconnect(texts.append)
        moved = h.drawn_position() - drawn0  # (the glove: it does not jump by its settle, it moves as the pointer)
        along = float(np.dot(moved, axes[:, i]))
        off = float(np.linalg.norm(moved - axes[:, i] * along))
        want = self._expect_axis(cam, c, axes[:, i], path[0], path[-1])
        self.check(f'gizmo: the {gz.AXIS_NAMES[i]} arrow is taken where it is drawn', hit == ('axis', i), str(hit))
        self.check(f'gizmo: dragging the {gz.AXIS_NAMES[i]} arrow 4 cm moves the hand along it only, as far as the pointer went',
                   want is not None and np.allclose(moved, want, atol=1e-9) and abs(along - 0.04) < 0.006 and off < 1e-9,
                   f'{along * 100:.2f} cm along (the pointer: {float(np.dot(want, axes[:, i])) * 100 if want is not None else 0:.2f}), '
                   f'{off * 1000:.4f} mm off the axis')
        self.check('gizmo: a drag is one undo step, and undo puts the hand back', len(wb.undo_stack) == undo0 + 1, f'{len(wb.undo_stack) - undo0} step(s)')
        wb.undo()
        self.check('gizmo: undo after a gizmo move (the place and the settled glove back)', np.allclose(h.position, p0, atol=1e-9)
                   and np.allclose(h.drawn_position(), drawn0, atol=1e-9))
        self.check('gizmo: the readout says what moves how far, and clears at the end',
                   any(t.startswith(f'Move {gz.AXIS_NAMES[i]}') and 'cm' in t for t in texts) and texts[-1:] == [''],
                   (texts[-2] if len(texts) > 1 else str(texts))[:80])

    def s_gizmo_snap_plane(self):
        """Ctrl snaps an arrow's move to whole centimetres; a plane square moves the hand in its plane only."""
        wb, s = self.wb, self.wb.scene
        r = self._gizmo_ready('move')
        if r is None:
            self.skip('gizmo: snap and plane', 'no gizmo')
            return
        v, cam, c, axes = r
        h = s.right
        i = next((k for k in (1, 0, 2) if self._arrow_grab(cam, c, axes, k) is not None), None)
        if i is not None:
            p0 = h.drawn_position().copy()
            g = self._arrow_grab(cam, c, axes, i)
            self._drag(self._path(cam, g, g + axes[:, i] * 0.027), Qt.KeyboardModifier.ControlModifier)
            along = float(np.dot(h.drawn_position() - p0, axes[:, i]))
            self.check(f'gizmo: Ctrl snaps a move to whole cm (2.7 cm dragged along {gz.AXIS_NAMES[i]})', abs(along - 0.03) < 1e-9,
                       f'{along * 100:.4f} cm')
            wb.undo()
        n = next((k for k in range(3) if gz.plane_visibility(cam, c, axes[:, k]) > 0.8), None)
        if n is None:
            self.skip('gizmo: a plane square moves in its plane', 'no plane faces the view')
            return
        q = gz._plane_quad(cam, c, axes, n, gz.MOVE_LEN)
        start = None
        for wts in ((0.25, 0.25, 0.25, 0.25), (0.1, 0.4, 0.4, 0.1), (0.1, 0.1, 0.4, 0.4), (0.4, 0.1, 0.1, 0.4)):
            pt = np.array(wts) @ q
            if gz.hit(cam, c, axes, 'move', *pt) == ('plane', n):
                start = pt
                break
        if start is None:
            self.skip('gizmo: a plane square moves in its plane', 'the square sits under an arrow here')
            return
        o, d = cam.ray(*start)
        p_on = gz.plane_hit(o, d, c, axes[:, n], not cam.ortho)
        j, k = (n + 1) % 3, (n + 2) % 3
        want = axes[:, j] * 0.025 - axes[:, k] * 0.015
        p0 = h.drawn_position().copy()
        path = [tuple(start)] + self._path(cam, p_on, p_on + want)[1:]
        self._drag(path)
        moved = h.drawn_position() - p0
        exact = self._expect_plane(cam, c, axes[:, n], path[0], path[-1])
        if exact is not None:
            exact = exact - axes[:, n] * float(np.dot(exact, axes[:, n]))
        self.check(f'gizmo: the {gz.AXIS_NAMES[j]}{gz.AXIS_NAMES[k]} square moves the hand in that plane only, as far as the pointer went',
                   exact is not None and np.allclose(moved, exact, atol=1e-9) and np.allclose(moved, want, atol=0.008)
                   and abs(float(np.dot(moved, axes[:, n]))) < 1e-9, f'{np.round(moved * 100, 2)} cm (aimed {np.round(want * 100, 2)})')
        wb.undo()

    def s_gizmo_turn(self):
        """Turn gizmo: dragging a ring along itself turns the hand about that ring's axis (its place stays); Ctrl snaps the
        turn to 5 degrees."""
        wb, s = self.wb, self.wb.scene
        r = self._gizmo_ready('rotate')
        if r is None:
            self.skip('gizmo: turn with a ring', 'no gizmo')
            return
        v, cam, c, axes = r
        h = s.right
        facing = [abs(float(np.dot(axes[:, k], cam.to_eye(c)))) for k in range(3)]
        i = int(np.argmax(facing))  # the ring seen most face-on
        sc, keep, t = gz._front_ring(cam, c, axes[:, i], gz.ROT_R)
        ks = [k for k in range(len(keep)) if keep[k]]
        k0 = None
        for k in ks:
            if gz.hit(cam, c, axes, 'rotate', *sc[k]) == ('ring', i):
                k0 = k
                break
        if k0 is None:
            self.skip('gizmo: turn with a ring', 'no point of the ring is free of the others here')
            return
        u, vv = gz._perp(axes[:, i])
        rad = gz.ROT_R * cam.px_at(c)

        def ring(phi):
            return tuple(cam.project(c + rad * (u * math.cos(phi) + vv * math.sin(phi))))
        for snap, want in ((False, 30.0), (True, 30.0)):
            p0, q0 = h.drawn_position().copy(), h.rotation.copy()
            turn = 31.7 if snap else 30.0
            pts = [ring(t[k0] + math.radians(a)) for a in np.linspace(0.0, turn, 13)]
            self._drag(pts, Qt.KeyboardModifier.ControlModifier if snap else Qt.KeyboardModifier.NoModifier)
            rel = quat_mul(h.rotation, quat_inv(q0))
            ang = math.degrees(2 * math.acos(min(1.0, abs(float(rel[3])))))
            axis = rel[:3] / max(1e-12, float(np.linalg.norm(rel[:3])))
            about = abs(float(np.dot(axis, axes[:, i])))
            stay = float(np.linalg.norm(h.drawn_position() - p0))  # (it turns about the glove as drawn, where the rings are)
            ok = (abs(ang - want) < (1e-6 if snap else 0.6)) and about > 0.9999 and stay < 1e-9
            self.check(f'gizmo: the {gz.AXIS_NAMES[i]} ring dragged {turn:g} degrees along itself turns the hand '
                       f'{"(Ctrl: snapped to 5 degrees) " if snap else ""}about {gz.AXIS_NAMES[i]}, its place kept', ok,
                       f'{ang:.3f} deg, axis {about:.5f} along {gz.AXIS_NAMES[i]}, moved {stay * 1000:.4f} mm')
            wb.undo()

    def s_gizmo_cancel(self):  # noqa: C901
        """Esc while dragging an arrow puts the hand back where the drag began (the part it was on and its job too), with no
        undo step left; so does a right click, whose release opens no menu. A click on an arrow alone changes nothing and
        leaves no undo step; the wheel while dragging does not zoom."""
        wb, s = self.wb, self.wb.scene
        self._settle()
        wb.reset_parts()
        wb.snap_left('support')
        r = self._gizmo_ready('move', hand=s.left)
        if r is None:
            self.skip('gizmo: Esc and right click take a drag back', 'no gizmo')
            return
        v, cam, c, axes = r
        h = s.left
        on0, role0 = h.on_part, h.role
        menus = []
        v.context.connect(menus.append)
        i = next((k for k in range(3) if self._arrow_grab(cam, c, axes, k) is not None), None)
        if i is None:
            self.skip('gizmo: Esc and right click take a drag back', 'every arrow faces the viewer')
            return
        results = []
        for how in ('Esc', 'right click'):
            p0, d0 = h.position.copy(), h.drawn_position().copy()
            undo0 = len(wb.undo_stack)
            g = self._arrow_grab(cam, c, axes, i)
            self._drag(self._path(cam, g, g + axes[:, i] * 0.05), release=False)
            moved = float(np.linalg.norm(h.position - p0))
            if how == 'Esc':
                QGuiApplication.sendEvent(v, QKeyEvent(QEvent.Type.KeyPress, Qt.Key.Key_Escape, Qt.KeyboardModifier.NoModifier))
            else:
                x, y = cam.project(g + axes[:, i] * 0.05)
                self._mouse(QEvent.Type.MouseButtonPress, x, y, Qt.MouseButton.LeftButton | Qt.MouseButton.RightButton,
                            button=Qt.MouseButton.RightButton)
            x, y = cam.project(g + axes[:, i] * 0.05)
            self._mouse(QEvent.Type.MouseButtonRelease, x, y, Qt.MouseButton.NoButton)
            back = bool(np.allclose(h.position, p0, atol=1e-12)) and bool(np.allclose(h.drawn_position(), d0, atol=1e-12))
            kept = h.on_part == on0 and h.role == role0
            results.append(f'{how}: moved {moved * 100:.1f} cm then back {back}, undo steps +{len(wb.undo_stack) - undo0}, '
                           f'on {h.on_part!r} as {h.role!r}')
            self.check(f'gizmo: {how} while dragging takes the drag back (no undo step left, the hand still on {on0!r})',
                       moved > 0.02 and back and kept and len(wb.undo_stack) == undo0 and v._gz is None, results[-1])
        v.context.disconnect(menus.append)
        self.check('gizmo: the right click that takes a drag back opens no menu', not menus, f'{len(menus)} menu(s)')
        # A click on the arrow alone: nothing changes, no undo step, the hand stays on its part.
        undo0 = len(wb.undo_stack)
        g = self._arrow_grab(cam, c, axes, i)
        x, y = cam.project(g)
        self._mouse(QEvent.Type.MouseButtonPress, x, y, Qt.MouseButton.LeftButton)
        self._mouse(QEvent.Type.MouseButtonRelease, x, y, Qt.MouseButton.NoButton)
        self.check('gizmo: a click on an arrow alone changes nothing and leaves no undo step',
                   len(wb.undo_stack) == undo0 and h.on_part == on0 and h.role == role0,
                   f'undo steps +{len(wb.undo_stack) - undo0}, on {h.on_part!r}')
        # ... and keeps the redo history: a step undone, a click, and redo still brings it back.
        p_before = h.position.copy()
        self._drag(self._path(cam, g, g + axes[:, i] * 0.02))
        p_moved = h.position.copy()
        wb.undo()
        self._mouse(QEvent.Type.MouseButtonPress, x, y, Qt.MouseButton.LeftButton)
        self._mouse(QEvent.Type.MouseButtonRelease, x, y, Qt.MouseButton.NoButton)
        redo_ok = bool(wb.redo_stack)
        wb.redo()
        self.check('gizmo: a click that changes nothing keeps the redo history', redo_ok and np.allclose(h.position, p_moved, atol=1e-12),
                   f'redo there {redo_ok}')
        wb.undo()
        self.check('gizmo: and undo goes back again', np.allclose(h.position, p_before, atol=1e-12))
        # The wheel while dragging: no zoom (the drag measures from where the camera stood).
        dist0 = v.dist
        self._drag(self._path(cam, g, g + axes[:, i] * 0.02), release=False)
        from PyQt6.QtGui import QWheelEvent
        from PyQt6.QtCore import QPoint
        at = QPointF(*cam.project(g + axes[:, i] * 0.02))
        QGuiApplication.sendEvent(v, QWheelEvent(at, v.mapToGlobal(at), QPoint(0, 0), QPoint(0, 240), Qt.MouseButton.LeftButton,
                                                 Qt.KeyboardModifier.NoModifier, Qt.ScrollPhase.NoScrollPhase, False))
        self._mouse(QEvent.Type.MouseButtonRelease, at.x(), at.y(), Qt.MouseButton.NoButton)
        self.check('gizmo: the wheel while dragging does not zoom', v.dist == dist0, f'{dist0:.3f} -> {v.dist:.3f}')
        wb.undo()

    def s_gizmo_local(self):
        """Local axes: the red arrow lies along the hand's own X, and dragging it moves the hand along that."""
        wb, s = self.wb, self.wb.scene
        r = self._gizmo_ready('move', space='local')
        if r is None:
            self.skip('gizmo: local axes', 'no gizmo')
            return
        v, cam, c, axes = r
        h = s.right
        from mathutil import quat_to_mat3
        own = quat_to_mat3(h.rotation)
        self.check('gizmo: Local axes are the hand\'s own', np.allclose(axes, own, atol=1e-9))
        i = next((k for k in range(3) if self._arrow_grab(cam, c, axes, k) is not None), None)
        if i is None:
            self.skip('gizmo: move along a local arrow', 'every arrow faces the viewer')
            return
        p0 = h.drawn_position().copy()
        g = self._arrow_grab(cam, c, axes, i)
        path = self._path(cam, g, g + axes[:, i] * 0.03)
        self._drag(path)
        moved = h.drawn_position() - p0
        along = float(np.dot(moved, own[:, i]))
        want = self._expect_axis(cam, c, axes[:, i], path[0], path[-1])
        self.check(f'gizmo: dragging the local {gz.AXIS_NAMES[i]} arrow moves the hand along its own {gz.AXIS_NAMES[i]}, as far as the pointer went',
                   want is not None and np.allclose(moved, want, atol=1e-9) and abs(along - 0.03) < 0.006
                   and float(np.linalg.norm(moved - own[:, i] * along)) < 1e-9, f'{along * 100:.2f} cm')
        wb.undo()
        wb.set_gizmo_space('global')

    def s_gizmo_gun(self):
        """Ctrl + click on the gun puts the gizmo on it (at the grip); its arrow moves the gun, the hands go along and keep
        their places on it; undo puts it back; a click on a hand puts the gizmo back on the hand."""
        wb, s = self.wb, self.wb.scene
        w = s.weapon
        r = self._gizmo_ready('move')
        if r is None:
            self.skip('gizmo on the gun', 'no gizmo')
            return
        v, cam, c, axes = r
        # The gun's centre seen through the view: a Ctrl + click there (a click, no drag).
        node = max((n for n in w.nodes if not n.hidden and not n.away and n.role == 'body'),
                   key=lambda n: float(np.prod(np.maximum(n.bmax - n.bmin, 1e-4))))
        at = w.to_world((node.bmin + node.bmax) * 0.5 + node.sim[:3, 3])
        hit = v.pick(QPointF(*cam.project(at)).toPoint())
        if hit is None or hit[0] != 'node':
            self.skip('gizmo on the gun', 'the gun is not under its centre on screen here')
            return
        undo0 = len(wb.undo_stack)
        x, y = cam.project(at)
        self._mouse(QEvent.Type.MouseButtonPress, x, y, Qt.MouseButton.LeftButton, Qt.KeyboardModifier.ControlModifier)
        self._mouse(QEvent.Type.MouseButtonRelease, x, y, Qt.MouseButton.NoButton, Qt.KeyboardModifier.ControlModifier)
        on_gun = v.gizmo_target == 'gun' and wb.gizmo_on.currentRouteKey() == 'gun'
        f = v.gizmo_frame()
        at_grip = f is not None and np.allclose(f[0], w.to_world(w.grip), atol=1e-9)
        self.check('gizmo: Ctrl + click on the gun puts the gizmo on it, at its grip, and leaves no undo step',
                   on_gun and at_grip and len(wb.undo_stack) == undo0,
                   f'target {v.gizmo_target}, header {wb.gizmo_on.currentRouteKey()}, undo steps +{len(wb.undo_stack) - undo0}')
        cam = v.camera()
        c, axes = f
        i = next((k for k in range(3) if self._arrow_grab(cam, c, axes, k) is not None), None)
        if i is None:
            self.skip('gizmo moves the gun', 'every arrow faces the viewer')
        else:
            place0 = w.P.copy()
            grip0 = w.to_world(w.grip)
            hands0 = [(h, h.position.copy(), w.to_gun(h.position)[:3].copy()) for h in s.hands() if h.role or h.on_part]
            # A laser pick on the gun goes with it (Copy coordinates and Put the hand here use it).
            pn = max((n for n in w.nodes if not n.hidden and not n.away), key=lambda n: float(np.prod(np.maximum(n.bmax - n.bmin, 1e-4))))
            pick = w.to_world((pn.bmin + pn.bmax) * 0.5 + pn.sim[:3, 3])
            wb.on_pick(pick, pn)
            g = self._arrow_grab(cam, c, axes, i)
            self._drag(self._path(cam, g, g + axes[:, i] * 0.05))
            d = w.to_world(w.grip) - grip0
            self.check('a laser pick goes with the gun when the gizmo moves it', wb.picked_point is not None
                       and np.allclose(wb.picked_point - pick, d, atol=1e-9), f'{np.round((wb.picked_point - pick) * 100, 2)} cm')
            wb.on_pick(None, None)
            along = float(np.dot(d, axes[:, i]))
            kept = all(np.allclose(w.to_gun(h.position)[:3], g0, atol=1e-6) for h, _, g0 in hands0)
            self.check(f'gizmo on the gun: its {gz.AXIS_NAMES[i]} arrow moves the gun 5 cm along it, the hands go along',
                       abs(along - 0.05) < 0.003 and float(np.linalg.norm(d - axes[:, i] * along)) < 1e-6 and kept and len(hands0) > 0,
                       f'{along * 100:.2f} cm, {len(hands0)} hand(s) kept their places on it: {kept}')
            wb.undo()
            self.check('gizmo on the gun: undo puts the gun and the hands back', np.allclose(w.P, place0, atol=1e-9)
                       and all(np.allclose(h.position, p0, atol=1e-9) for h, p0, _ in hands0))
        # A click on the right hand: the gizmo back on it. (A point of the glove away from the gizmo's handles: the gun's
        # grip, where its gizmo sits, is the right hand's palm, and the handles come first.)
        cam = v.camera()
        sc = cam.project(s.right.drawn_position())
        hp, hh = None, None
        for rad in (16.0, 24.0, 32.0, 40.0):
            for ang in np.linspace(0.0, 2 * math.pi, 16, endpoint=False):
                p = sc + rad * np.array([math.cos(ang), math.sin(ang)])
                pick = v.pick(QPointF(*p).toPoint())
                if pick is not None and pick[0] == 'hand' and pick[1] is s.right and v.gizmo_hit(QPointF(*p).toPoint()) is None:
                    hp, hh = tuple(p), pick
                    break
            if hp is not None:
                break
        if hh is not None:
            self._mouse(QEvent.Type.MouseButtonPress, *hp, Qt.MouseButton.LeftButton)
            self._mouse(QEvent.Type.MouseButtonRelease, *hp, Qt.MouseButton.NoButton)
            self.check('gizmo: a click on a hand puts the gizmo back on the hand (no undo step)',
                       v.gizmo_target == 'hand' and len(wb.undo_stack) == undo0, f'undo steps +{len(wb.undo_stack) - undo0}')
        else:
            self.skip('gizmo: a click on a hand puts the gizmo back on the hand', 'no point of the right glove free of the gizmo here')
            v.set_gizmo_target('hand')

    def s_gizmo_buttons(self):
        """The header's buttons, clicked as a user clicks them: each one shows its gizmo, back and forth (a click passed the
        button's checked state as the mode, and after the first switch no gizmo showed again; user, 2026-10-18)."""
        wb, v = self.wb, self.wb.view
        self._gizmo_ready('move')
        seen = []
        for key in ('rotate', 'move', 'both', 'free', 'rotate', 'move'):
            wb.gizmo_seg.widget(key).click()
            self.app.processEvents()
            shows = v.gizmo_frame() is not None
            seen.append(f"{key}: mode {v.gizmo_mode!r}, {'drawn' if shows else 'none'}")
            if v.gizmo_mode != key or shows != (key != 'free') or wb.gizmo_seg.currentRouteKey() != key:
                break
        self.check('gizmo header: each tool button clicked shows its gizmo, back and forth', len(seen) == 6
                   and v.gizmo_mode == 'move' and v.gizmo_frame() is not None, '; '.join(seen))
        for key in ('gun', 'hand'):
            wb.gizmo_on.widget(key).click()
            self.app.processEvents()
        self.check('gizmo header: Gun and Hand clicked put the gizmo on the gun and back on the hand', v.gizmo_target == 'hand')

    def s_gizmo_keys(self):
        """The keys G, R, T and W choose the gizmo (move, turn, both, none); the comma cycles its axes. The header follows."""
        wb, v = self.wb, self.wb.view
        self.win.activateWindow()
        self.app.processEvents()
        if self.app.activeWindow() is not self.win:
            self.skip('gizmo keys G, R, T, W and comma', 'the program is not the active window on this PC')
            return
        seen = []
        for key, text, want in ((Qt.Key.Key_R, 'r', 'rotate'), (Qt.Key.Key_T, 't', 'both'), (Qt.Key.Key_W, 'w', 'free'),
                                (Qt.Key.Key_G, 'g', 'move')):
            self._key(wb, key, text)
            seen.append(v.gizmo_mode == want and wb.gizmo_seg.currentRouteKey() == want)
        self.check('gizmo keys: R turn, T both, W none, G move (the header follows)', all(seen), str(seen))
        wb.set_gizmo_space('global')
        order = []
        for _ in range(3):
            self._key(wb, Qt.Key.Key_Comma, ',')
            order.append(v.gizmo_space)
        self.check('gizmo key comma: Global, Local, Gun in turn', order == ['local', 'gun', 'global'], str(order))

    def s_gizmo_free(self):
        """Free (no gizmo): nothing to take; dragging the hand itself still moves it freely, as before. And a clean picture
        leaves the gizmo out, and puts it back."""
        wb, s = self.wb, self.wb.scene
        r = self._gizmo_ready('free')
        v = wb.view
        cam = v.camera()
        c = s.right.drawn_position()
        sc = cam.project(c)
        none = r is None and v.gizmo_hit(QPointF(*sc).toPoint()) is None
        # The hand dragged by its own body (a point of the glove under the pointer), 3 cm to the right on screen.
        hit = v.pick(QPointF(*sc).toPoint())
        moved = 0.0
        if hit is not None and hit[0] == 'hand' and hit[1] is s.right:
            p0 = s.right.position.copy()
            self._drag([tuple(sc), tuple(sc + np.array([15.0, 0])), tuple(sc + np.array([30.0, 0]))])
            moved = float(np.linalg.norm(s.right.position - p0))
            wb.undo()
        self.check('gizmo Free: no gizmo to take, and dragging the hand itself still moves it', none and moved > 0.001,
                   f'gizmo hit {not none}, the hand moved {moved * 100:.2f} cm')
        wb.set_gizmo_mode('move')
        drawn = []
        real = v._draw_gizmo
        v._draw_gizmo = lambda *a: (drawn.append(1), real(*a))
        try:
            wb._grab_view(True)
            in_clean = len(drawn)
            wb._grab_view(False)
            in_plain = len(drawn) - in_clean
        finally:
            v._draw_gizmo = real
        self.check('a clean picture draws no gizmo (a plain one does), and the gizmo is back after it',
                   in_clean == 0 and in_plain > 0 and v.show_gizmo and v.show_axes, f'clean {in_clean}, plain {in_plain}')

    def s_put_hand(self):
        """A hand put on a surface of the gun (the point a ray from above finds, as the laser would): the palm lands within
        7 cm of that point (its centre 3 cm off the skin, the drawn glove settled up to 4 cm), and the hand gets a job or none."""
        wb, s = self.wb, self.wb.scene
        w = s.weapon
        self._settle()
        wb.reset_parts()
        up_w = w.dir_to_world(w.up)
        c, r = w.center_world()
        start = c + up_w * (r + 0.1)
        t = s.ray_weapon(start, -up_w, 2 * (r + 0.1) + 0.05)
        if t is None:
            self.skip("a hand put on the gun's surface", 'no surface of the gun under a ray from above')
            return
        point = start - up_w * t
        for hand in (s.right, s.left):
            s.put_hand_on(hand, point, up_w)
            d = float(np.linalg.norm(hand.drawn_position() - point))
            self.check(f'a {hand.side} hand put on the gun: its palm within 7 cm of the point, a job or none',
                       d < 0.07 and hand.role in ('', 'grip', 'support', 'rack', 'magazine', 'cover'),
                       f'palm {d * 100:.1f} cm from the point, job {hand.role!r}')
        wb.reset_parts()

    def s_copy_hands(self):
        """Copy the hands for Claude: the text names the weapon, both hands, their jobs and places."""
        wb = self.wb
        wb.copy_hands_text()
        text = QGuiApplication.clipboard().text()
        w = wb.scene.weapon
        ok = w.id in text and 'Right hand' in text and 'Left hand' in text and 'of the grip' in text and 'from the muzzle' in text
        self.check('Copy the hands for Claude', ok, text.splitlines()[0][:120] if text else 'nothing copied')

    def s_put_hand_menu(self):
        """The laser's menu entry: a part picked, the left hand put on it (undo puts it back)."""
        wb, s = self.wb, self.wb.scene
        w = s.weapon
        node = max((n for n in w.nodes if not n.hidden), key=lambda n: float(np.prod(np.maximum(n.bmax - n.bmin, 1e-4))))
        point = w.to_world((node.bmin + node.bmax) * 0.5 + node.sim[:3, 3])
        wb.view.pick_normal = w.dir_to_world(w.up)
        wb.on_pick(point, node)
        before = s.left.position.copy()
        wb.put_hand_here(s.left)
        moved = float(np.linalg.norm(s.left.position - point))
        wb.undo()
        self.check('the menu puts the left hand on the picked part, undo puts it back',
                   moved < 0.07 and np.allclose(s.left.position, before), f'palm {moved * 100:.1f} cm from the point')

    def s_clean_and_big(self):
        """Pictures: a clean one has no grid, markers or highlights and puts them back after; the 2x one is twice the size."""
        wb, v = self.wb, self.wb.view
        before = {k: getattr(v, k) for k in wb.OVERLAYS}
        normal = wb._grab_view(False)
        clean = wb._grab_view(True)
        after = {k: getattr(v, k) for k in wb.OVERLAYS}
        differs = normal.size() == clean.size() and normal != clean
        self.check('a clean picture leaves the grid and markers out, and puts them back',
                   differs and all(before[k] is after[k] or before[k] == after[k] for k in before))
        wb.big_sw.setChecked(True)
        path = wb.screenshot()
        wb.big_sw.setChecked(False)
        self.made.append(path)
        from PyQt6.QtGui import QImage
        img = QImage(path) if path else QImage()
        self.check('a 2x screenshot is twice the view\'s size', not img.isNull() and img.width() >= normal.width() * 2 - 2,
                   f'{img.width()} x {img.height()} against {normal.width()} x {normal.height()}')

    def s_turntable_start(self):
        self.tt_before = set(f for f in os.listdir(self.wb.captures) if f.endswith('.gif'))
        self.tt_yaw = self.wb.view.yaw
        self.wb.turntable_gif()
        return int(self.wb.TURN_SECONDS * 1000) + 1500

    def s_turntable_check(self):
        new = [f for f in os.listdir(self.wb.captures) if f.endswith('.gif') and f not in self.tt_before]
        frames = 0
        if new:
            path = os.path.join(self.wb.captures, new[0])
            self.made.append(path)
            from PIL import Image
            with Image.open(path) as g:
                frames = getattr(g, 'n_frames', 1)
        self.check('the turntable GIF records one turn and stops by itself, the camera back',
                   bool(new) and frames >= 10 and self.wb.recording is None and abs(self.wb.view.yaw - self.tt_yaw) < 1e-6,
                   f'{new[0] if new else "none"}: {frames} frames')

    def s_record_reload_start(self):
        self.rr_before = set(f for f in os.listdir(self.wb.captures) if f.endswith('.gif'))
        w = self.wb.scene.weapon
        self.rr_has = bool(w.rig.get('magazine') or w.rig.get('slide') or w.rig.get('hingeKind', 'None') != 'None')
        self.wb.record_reload()
        return 200

    def s_record_reload_check(self):
        deadline = time.time() + 12
        while (self.wb.recording is not None or self.wb.timeline.running) and time.time() < deadline:
            self.app.processEvents()
            time.sleep(0.01)
        new = [f for f in os.listdir(self.wb.captures) if f.endswith('.gif') and f not in self.rr_before]
        for f in new:
            self.made.append(os.path.join(self.wb.captures, f))
        if not self.rr_has:
            self.skip('Record the reload', 'nothing to reload on this gun')
            return
        self.check('Record the reload: a GIF of the reload, stopped by itself', bool(new) and self.wb.recording is None,
                   new[0] if new else 'none')

    def s_later_features(self):
        """The features other branches are adding: SKIP until their Workbench method is merged in."""
        for name, method in self.LATER:
            if hasattr(self.wb, method):
                self.skip(name, f'Workbench.{method} is there, its check is not written yet')
            else:
                self.skip(name, f'not in this build (no Workbench.{method})')

    def s_all_weapons(self):
        """--all: every exported weapon opened, its hands put on, a reload played to its end and saved to the test's own
        folder. Any error is a failure naming the weapon (the barrel-up grenade launchers closed the program)."""
        wb = self.wb
        bad, n = [], 0
        log = getattr(__import__('sys').modules.get('__main__'), 'error_log_path', None)
        log_path = log() if log else None
        log_size = os.path.getsize(log_path) if log_path and os.path.exists(log_path) else 0
        for it in list(wb.items):
            wid = it.data(Qt.ItemDataRole.UserRole)
            n += 1
            try:
                wb.drafts.clear()
                wb.weapon_list.setCurrentItem(it)
                if wb.scene.weapon is None or wb.scene.weapon.id != wid:
                    bad.append(f'{wid}: did not open')
                    continue
                wb.hands_on_gun()
                wb.play_reload()
                wb.settle_timeline()
                if wb.save_pose() is None:
                    bad.append(f'{wid}: not saved')
                wb.undo_stack.clear()
            except Exception as e:  # noqa: BLE001
                bad.append(f'{wid}: {type(e).__name__}: {e}')
            self.app.processEvents()
        new_errors = (os.path.getsize(log_path) if log_path and os.path.exists(log_path) else 0) > log_size
        self.check(f'every exported weapon opens, takes the hands, reloads and saves ({n})', not bad and not new_errors,
                   '; '.join(bad[:8]) + (' …' if len(bad) > 8 else '') + (' (errors in the log)' if new_errors else ''))

    def s_cleanup(self):
        removed = 0
        for path in self.made:
            try:
                if path and os.path.exists(path):
                    os.remove(path)
                    removed += 1
            except OSError:
                pass
        shutil.rmtree(self.root, ignore_errors=True)
        self.check('test files removed (the user\'s poses never touched)', not os.path.exists(self.root), f'{removed} captures removed')
