"""The autotest's graphics bench as a table of what each variant saves or costs against its own preset: "Medium, small objects
without shadows" against "Medium", "Game, game cameras 1 px" against "Game (own quality)", "old High" against "High". The
bench measures every setting twice with the world frozen (AutoTest's graphics bench); a difference inside the spread of the two
rounds' GPU times is marked as noise. Read it to decide the settings to try (Tasks 356: keep one on by default only if it holds).

Run: python -I tools/bench_report.py [REPORT]   (default: the game's BepInEx/RavenfieldVR-autotest.txt)"""
import os
import re
import sys

GAME = r'C:\Program Files (x86)\Steam\steamapps\common\Ravenfield\BepInEx'
ROW = re.compile(r'^\s{2,}(\S.*?)\s+frame\s+([\d.]+) ms, GPU\s+(\S+) ms \(GPU by round ([^)]*)\)')


def number(text):
    try:
        return float(text)
    except ValueError:
        return None


def rows(text):
    """The bench's rows after its heading: (name, frame ms, GPU ms or None, [GPU by round])."""
    out, inside = [], False
    for line in text.splitlines():
        if 'Graphics bench' in line:
            inside, out = True, []  # (the last bench in the report)
            continue
        if not inside:
            continue
        m = ROW.match(line)
        if not m:
            if out and line.strip() and not line.startswith('    '):
                inside = False
            continue
        rounds = [r for r in (number(x.strip()) for x in m.group(4).split('/')) if r is not None]
        out.append((m.group(1).strip(), float(m.group(2)), number(m.group(3)), rounds))
    return out


def base_of(name, names):
    """The preset a variant is measured against: the part before its comma ('Game' is the bench's 'Game (own quality)'), an
    'old' preset's new one; None for a preset itself or one with no base in the bench."""
    if name.startswith('old '):
        b = name[4:]
    elif ', ' in name:
        b = name.split(', ', 1)[0]
        if b == 'Game':
            b = 'Game (own quality)'
    else:
        return None
    return b if b in names else None


def spread(rounds):
    return max(rounds) - min(rounds) if len(rounds) >= 2 else 0.0


def table(bench):
    by_name = {r[0]: r for r in bench}
    lines = []
    for name, frame, gpu, rounds in bench:
        b = base_of(name, by_name)
        if b is None:
            continue
        _, bframe, bgpu, brounds = by_name[b]
        d_frame = frame - bframe
        d_gpu = gpu - bgpu if gpu is not None and bgpu is not None else None
        noise = max(spread(rounds), spread(brounds))
        verdict = ''
        if d_gpu is not None:
            # (By the GPU only: the bench prints the frame interval's rounds averaged, with no spread to weigh it by.)
            if abs(d_gpu) <= noise:
                verdict = 'GPU: noise (inside the rounds\' spread)'
            else:
                verdict = 'GPU: saves' if d_gpu < 0 else 'GPU: costs'
        lines.append((name, b, d_frame, d_gpu, verdict))
    return lines


def main(argv):
    path = argv[1] if len(argv) > 1 else os.path.join(GAME, 'RavenfieldVR-autotest.txt')
    if not os.path.exists(path):
        print(f'No report at {path}')
        return 1
    bench = rows(open(path, encoding='utf-8', errors='replace').read())
    if not bench:
        print(f'No graphics bench in {path}')
        return 1
    print(f'{"variant":<40} {"against":<20} {"frame":>8} {"GPU":>8}')
    for name, b, d_frame, d_gpu, verdict in table(bench):
        gpu = f'{d_gpu:+.2f}' if d_gpu is not None else 'n/a'
        print(f'{name:<40} {b:<20} {d_frame:+8.2f} {gpu:>8}  {verdict}')
    return 0


if __name__ == '__main__':
    sys.exit(main(sys.argv))
