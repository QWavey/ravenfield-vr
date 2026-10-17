"""The sandbox checking itself: every feature run as a user would (`main.py --selftest report.txt [--weapon NAME]`), each
step written PASS or FAIL with what it saw. Pose files that existed before are put back; the ones it made are removed."""
import json
import os
import time

import numpy as np
from PyQt6.QtCore import Qt, QTimer
from PyQt6.QtGui import QGuiApplication

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
                      self.s_send_remove, self.s_reload_start, self.s_reload_wait, self.s_laser, self.s_screenshot,
                      self.s_gif_start, self.s_gif_stop, self.s_cleanup]
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
        text = f"Ravenfield VR Sandbox self-test, {time.strftime('%Y-%m-%d %H:%M:%S')}: {len(self.lines) - self.failed} passed, {self.failed} failed\n" + '\n'.join(self.lines) + '\n'
        with open(self.report, 'w', encoding='utf-8') as f:
            f.write(text)
        print(text)
        code = 1 if self.failed else 0
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
        # Pose files of this weapon kept aside (put back at the end).
        for path in (os.path.join(self.wb.poses_dir, wid + '.json'),
                     os.path.join(self.wb.game_poses_dir(), wid + '.json') if self.wb.game_poses_dir() else None):
            if path:
                self.backups[path] = open(path, encoding='utf-8').read() if os.path.exists(path) else None
                if os.path.exists(path):
                    os.remove(path)
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
        if game:
            self.check('pose saved for the game', os.path.exists(gpath), gpath)
            self.check('the game copy is the same', json.load(open(gpath, encoding='utf-8')) == doc)
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
        if not game:
            self.check('send to / remove from the game', True, 'skipped: the game folder was not found')
            return
        target = os.path.join(game, wb.scene.weapon.id + '.json')
        wb.remove_from_game()
        gone = not os.path.exists(target)
        status_off = "mod's own grip" in wb.game_status.text()
        wb.send_to_game()
        back = os.path.exists(target)
        status_on = 'saved pose' in wb.game_status.text()
        self.check('remove from and send to the game', gone and back and status_off and status_on, wb.game_status.text())

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
        ok = not tl.running and w.fresh is None and abs(w.mag) < 1e-6 and abs(w.slide) < 1e-6 and not any(n.away for n in w.nodes)
        self.check('reload played to the end, parts home', ok, f'magazine {w.mag:.2f}, slide {w.slide:.2f}, away {sum(n.away for n in w.nodes)}')
        if w.rig.get('magazine'):
            self.check('the fresh magazine is drawn while it goes in', self.fresh_seen)
        if self.user_hidden is not None:
            self.check('a part hidden by the user stays hidden through the reload', self.user_hidden.hidden, self.user_hidden.short)
            self.user_hidden.hidden = False

    def s_laser(self):
        wb = self.wb
        wb.test_pick()
        text = wb.point_text() if wb.picked_point is not None else ''
        wb.copy_point()
        clip = QGuiApplication.clipboard().text()
        self.check('laser readout and copy', bool(text) and clip == text and 'from the muzzle' in text, text[:140])

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

    def s_cleanup(self):
        removed = 0
        for path in self.made:
            if path and os.path.exists(path) and path not in self.backups:
                os.remove(path)
                removed += 1
        for path, content in self.backups.items():
            if content is None:
                if os.path.exists(path):
                    os.remove(path)
            else:
                with open(path, 'w', encoding='utf-8') as f:
                    f.write(content)
        self.check('test files removed, earlier poses put back', True, f'{removed} removed, {sum(c is not None for c in self.backups.values())} put back')
