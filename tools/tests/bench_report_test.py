"""Checks of tools/bench_report.py on a made-up graphics bench: each variant against its own preset (the part before its comma,
'Game' as 'Game (own quality)', an 'old' preset against the new one), a difference inside the rounds' GPU spread as noise, and
the last bench of a report read when it holds two.
Run: python -I tools/tests/bench_report_test.py   (exit code 0 when all pass)"""
import os
import sys
import tempfile

sys.path.insert(0, os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__)))))
import bench_report  # noqa: E402

failed = passed = 0


def check(ok, what):
    global failed, passed
    if ok:
        passed += 1
    else:
        failed += 1
        print('FAIL', what)


def row(name, frame, gpu, r1, r2, f1=None, f2=None):
    frames = f" (frame by round {f1:.2f} / {f2:.2f})" if f1 is not None else ''
    return (f"    {name:<26} frame {frame:6.2f} ms, GPU {gpu:6.2f} ms (GPU by round {r1:.2f} / {r2:.2f}){frames}   last: frame  10.18 ms "
            f"(avg  10.54, 95th  12.37), GPU   5.00 ms   [Medium]\n")


HEAD = ("  Graphics bench (standing still, the world frozen, 2 rounds; frame = CPU frame interval, GPU = the compositor's timing of "
        "our frames; medians):\n")
text = ("  something before\n" + HEAD + row('Medium', 9.0, 6.0, 5.9, 6.1) + row('Medium, MSAA off', 8.0, 5.0, 4.9, 5.1)
        + row('Medium, small objects without shadows', 8.9, 5.95, 5.8, 6.1) + row('Game (own quality)', 8.7, 4.0, 3.9, 4.1)
        + row('Game, game cameras 1 px', 8.2, 3.9, 3.8, 4.0) + row('old Medium', 11.7, 9.0, 8.9, 9.1) + row('Low again', 9.2, 2.7, 2.6, 2.8)
        + "  next section\n")
bench = bench_report.rows(text)
check(len(bench) == 7, f"rows: seven bench rows read ({len(bench)})")
lines = {name: (b, d_frame, d_gpu, verdict) for name, b, d_frame, d_gpu, verdict in bench_report.table(bench)}
check(lines.get('Medium, MSAA off', (None,))[0] == 'Medium' and abs(lines['Medium, MSAA off'][2] + 1.0) < 1e-6
      and lines['Medium, MSAA off'][3] == 'GPU: saves', f"a variant against its preset, outside the spread: saves ({lines.get('Medium, MSAA off')})")
check(lines.get('Medium, small objects without shadows', (None, 0, 0, ''))[3].startswith('GPU: noise'),
      f"a difference inside the rounds' spread is noise ({lines.get('Medium, small objects without shadows')})")
check(lines.get('Game, game cameras 1 px', (None,))[0] == 'Game (own quality)', "'Game, ...' against 'Game (own quality)'")
check(lines.get('old Medium', (None,))[0] == 'Medium' and lines['old Medium'][3] == 'GPU: costs', "an old preset against the new one: costs")
check('Low again' not in lines and 'Medium' not in lines, "a preset itself and 'Low again' have no line")
two = text.replace(row('Medium, MSAA off', 8.0, 5.0, 4.9, 5.1), '') + HEAD + row('High', 12.0, 9.0, 8.9, 9.1)
check([r[0] for r in bench_report.rows(two)] == ['High'], "the last bench of a report read when it holds two")
# A bench with the frame interval's rounds (since 2026-10-30): the frame's difference weighed by its own spread.
framed = (HEAD + row('Medium', 9.0, 6.0, 5.9, 6.1, 8.9, 9.1) + row('Medium, MSAA off', 8.0, 5.95, 5.8, 6.1, 7.9, 8.1)
          + row('Medium, LOD bias 0.3', 8.95, 6.0, 5.9, 6.1, 8.5, 9.4))
flines = {name: verdict for name, b, d_frame, d_gpu, verdict in bench_report.table(bench_report.rows(framed))}
check(flines.get('Medium, MSAA off') == 'GPU: noise (inside the rounds\' spread); frame: saves',
      f"frame rounds: a frame saving outside their spread with the GPU inside its own ({flines.get('Medium, MSAA off')!r})")
check((flines.get('Medium, LOD bias 0.3') or '').endswith('frame: noise'), f"frame rounds: inside their spread is noise ({flines.get('Medium, LOD bias 0.3')!r})")
check('frame:' not in (lines.get('Medium, MSAA off', (0, 0, 0, ''))[3]), "an older bench without the frame's rounds says nothing of the frame")
# The summary line's settings: one that saves beyond the noise named by its panel name, one inside the noise left out.
kept = bench_report.worth_keeping(bench_report.table(bench_report.rows(HEAD + row('Medium', 9.0, 6.0, 5.9, 6.1, 8.9, 9.1)
                                                                        + row('Medium, tiny distant objects hidden', 8.0, 5.9, 5.8, 6.0, 7.9, 8.1)
                                                                        + row('Medium, repeated objects instanced', 8.95, 6.0, 5.9, 6.1, 8.6, 9.2))))
check(kept == ['Tiny distant objects hidden (-1.00 ms frame)'], f"worth keeping: the tiny-object setting only ({kept!r})")
with tempfile.TemporaryDirectory() as root:
    path = os.path.join(root, 'autotest.txt')
    with open(path, 'w', encoding='utf-8') as f:
        f.write(text)
    check(bench_report.main(['x', path]) == 0, "main: a report with a bench")
    with open(path, 'w', encoding='utf-8') as f:
        f.write('no bench here\n')
    check(bench_report.main(['x', path]) == 1, "main: a report with no bench says so")
print(f'{passed} passed, {failed} failed')
sys.exit(1 if failed else 0)
