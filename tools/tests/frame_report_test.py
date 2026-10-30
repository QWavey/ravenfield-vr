"""Checks of tools/frame_report.py on made-up frame rate lines: the bound column (CPU or GPU by the GPU's median against the
refresh interval, as the mod's frame rate hold decides), the costliest steps with a phase's "(own)" time, and the default log
(the newer of the kept session and the current log, never a test run's).
Run: python -I tools/tests/frame_report_test.py   (exit code 0 when all pass)"""
import os
import shutil
import sys
import tempfile
import time

sys.path.insert(0, os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__)))))
import frame_report  # noqa: E402

failed = passed = 0


def check(ok, what):
    global failed, passed
    if ok:
        passed += 1
    else:
        failed += 1
        print('FAIL', what)


def line(on_time, gpu, steps='PlayerLoop 1.23, Update (own) 0.50, Render 0.40,'):
    return (f"[Info   :Ravenfield VR] Frame rate over the last minute of play: {on_time}% of 4185 frames at the headset's 90 Hz "
            f"(11.1 ms), median 11.5 ms, 95th 23.0 ms, GPU median {gpu} ms, 95th 5.5 ms; Medium, 1357x1497 per eye (render scale 0.9); "
            f"costliest steps: {steps}; the mod's costliest sections: Input 0.18, Weapons.BeforeRender 0.10,\n")


def main():
    root = tempfile.mkdtemp(prefix='frame-report-')
    try:
        log = os.path.join(root, 'session.log')
        with open(log, 'w', encoding='utf-8') as f:
            f.write(line(41, '3.3') + line(19, '11.0') + line(97, '2.0') + line(60, '7.8'))
        table = list(frame_report.rows(log))
        check([r['bound'] for r in table] == ['CPU', 'GPU', '', 'GPU'],
              f"bound: CPU at GPU 3.3 of 11.1 ms, GPU at 11.0, none when on time, GPU at 7.8 (70%): {[r['bound'] for r in table]}")
        check(table[0]['steps'] == 'PlayerLoop 1.23, Update (own) 0.50, Render 0.40', f"steps keep '(own)': {table[0]['steps']!r}")
        check(table[0]['mod sections'] == 'Input 0.18, Weapons.BeforeRender 0.10', f"sections without stray spaces: {table[0]['mod sections']!r}")
        cpu_log = os.path.join(root, 'cpu.log')
        with open(cpu_log, 'w', encoding='utf-8') as f:
            f.write(line(90, '3.0').replace('; costliest steps', "; CPU speed 88% of the session's first minute (0.71 ms for a fixed sum); costliest steps"))
        cpu_rows = list(frame_report.rows(cpu_log))
        check(cpu_rows[0]['cpu'] == '88%' and table[0]['cpu'] == '', f"cpu column: 88% when the line has it, empty when not ({cpu_rows[0]['cpu']!r})")
        try_log = os.path.join(root, 'try.log')
        with open(try_log, 'w', encoding='utf-8') as f:
            f.write(line(90, '3.0').replace('(render scale 0.9); ', '(render scale 0.9), MSAA 2x, shadows 30 m, LOD bias 0.7, game cameras into 1 px, scope lens every other frame; '))
        try_rows = list(frame_report.rows(try_log))
        check(try_rows[0]['try'] == 'cams 1px, scope 1/2' and table[0]['try'] == '', f"try column: the settings to try when on ({try_rows[0]['try']!r})")

        game = os.path.join(root, 'BepInEx')
        os.makedirs(game)
        kept, current = os.path.join(game, 'LogOutput.player.log'), os.path.join(game, 'LogOutput.log')
        before = frame_report.GAME
        frame_report.GAME = game
        try:
            for p, text, age in ((kept, line(50, '3.0'), 3600), (current, line(40, '3.0'), 0)):
                with open(p, 'w', encoding='utf-8') as f:
                    f.write(text)
                os.utime(p, (time.time() - age, time.time() - age))
            check(frame_report.newest_session() == current, 'default log: a session played since the kept one is the one read')
            with open(current, 'w', encoding='utf-8') as f:
                f.write('Test run: -rfvr-autotest\n' + line(40, '3.0'))
            check(frame_report.newest_session() == kept, "default log: a test run's log is never the user's session")
            os.remove(kept)
            check(frame_report.newest_session() is None, 'default log: none when only a test run is there')
        finally:
            frame_report.GAME = before
    finally:
        shutil.rmtree(root, ignore_errors=True)
    print(f'{passed} passed, {failed} failed')
    return 1 if failed else 0


if __name__ == '__main__':
    sys.exit(main())
