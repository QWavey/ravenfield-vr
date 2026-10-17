"""The sandbox checking itself: every feature run as a user would (`main.py --selftest report.txt [--weapon NAME]`), each
step written PASS or FAIL with what it saw. It saves into folders of its own (reports/sandbox/selftest): the user's saved
poses and the game's are never touched (it deleted them and put them back only at the end before, review B3#0/B8#0)."""
import json
import os
import shutil
import time

import numpy as np
from PyQt6.QtCore import QEvent, QPointF, Qt, QTimer
from PyQt6.QtGui import QGuiApplication, QMouseEvent

from mathutil import quat_axis_angle, quat_mul


class SelfTest:
    def __init__(self, win, app, report, want=None):
        self.win = win
        self.wb = win.workbench
        self.app = app
        self.report = report
        self.want = want
        self.lines = []
        self.failed = 0
        self.backups = {}
        self.made = []
        self.steps = [self.s_list, self.s_open, self.s_hands, self.s_edit, self.s_save, self.s_reimport, self.s_export,
                      self.s_send_remove, self.s_bad_import, self.s_undo, self.s_draft, self.s_reload_start, self.s_reload_wait,
                      self.s_laser, self.s_screenshot, self.s_gif_start, self.s_gif_stop, self.s_error_raise, self.s_error_check,
                      self.s_cleanup]
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
        if not ok:
            self.failed += 1

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
            text = f"Ravenfield VR Sandbox self-test, {time.strftime('%Y-%m-%d %H:%M:%S')}: {len(self.lines) - self.failed} passed, {self.failed} failed\n" + '\n'.join(self.lines) + '\n'
            try:
                with open(self.report, 'w', encoding='utf-8') as f:
                    f.write(text)
            except OSError as e:
                code = 2  # (no report: said by the exit code, not a run left hanging)
                text += f'\nTHE REPORT COULD NOT BE WRITTEN: {e}\n'
            if __import__('sys').stdout is not None:
                print(text)
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
        ok = wb.import_pose(bad)
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
            self.check('unsaved hands kept when another weapon is opened', True, 'skipped: only one weapon exported')
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
        self.check('reload started', self.wb.timeline.running or not self.has_reload)
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
        if self.user_hidden is not None:
            self.check('a part hidden by the user stays hidden through the reload', self.user_hidden.hidden, self.user_hidden.short)
            self.user_hidden.hidden = False

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

        def boom():
            raise RuntimeError('self-test: a deliberate error in a button (the program must go on)')
        QTimer.singleShot(0, boom)
        return 400

    def s_error_check(self):
        path = self._error_log()
        size = os.path.getsize(path) if path and os.path.exists(path) else 0
        self.check('an error in the program is logged and the program goes on', size > self.error_before, path or 'no log path')

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
