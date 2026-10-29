"""Compare an autotest report's measurements and mod timings with the run before it.

  python tools/metric_diff.py                  reports/shots/autotest against reports/shots/autotest-previous
  python tools/metric_diff.py weapon           the same for a --weapon run
  python tools/metric_diff.py A.txt B.txt      two reports (new, old)

Prints the measurements whose average moved by more than --rel (default 20%) and --abs (default 0.05), the frame-time
line of each run, and each mod section's average and maximum. A measurement found in only one of the runs is listed too
(a check that stopped reporting, or a new one). First, the problems found in only one of the runs (new ones and gone ones),
matched with their numbers taken out: a gun's "shakes up to 75 deg" and "up to 11 deg" are the same problem.
"""
import argparse
import os
import re
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))


NUMBER = re.compile(r'[-+]?\d+(?:[.,]\d+)?')


def problem_key(text):
    """A problem line without its numbers (and its spacing), to match it across runs."""
    return ' '.join(NUMBER.sub('#', text).split())


# Lines of a measurement or section table that did not parse, per report (said in the output: dropped silently, a report
# format change made the comparison quietly smaller; sweep).
UNREAD = {}


def read(path):
    measurements, sections, frames, problems = {}, {}, '', {}
    where = None
    with open(path, encoding='utf-8', errors='replace') as f:
        for line in f:
            text = line.rstrip('\n')
            if text.lstrip().startswith('! '):
                body = text.lstrip()[2:].strip()
                problems.setdefault(problem_key(body), body)
                continue
            if text.startswith('Measurements'):
                where = 'm'
                continue
            if text.startswith('Mod sections'):
                where = 's'
                continue
            if text.startswith('Frames:'):
                frames = text
            if where and not text.startswith('  '):
                where = None
                continue
            if where == 'm':
                parts = text.rstrip().rsplit(None, 4)
                if len(parts) == 5:
                    try:
                        measurements[parts[0].strip()] = (float(parts[1]), int(float(parts[2])), float(parts[3]), float(parts[4]))
                    except ValueError:
                        UNREAD[path] = UNREAD.get(path, 0) + 1
            elif where == 's':
                head = text.split('slowest at')[0].rstrip()
                parts = head.rsplit(None, 5)
                if len(parts) == 6:
                    try:
                        sections[parts[0].strip()] = (float(parts[1]), float(parts[2]), int(parts[3]))
                    except ValueError:
                        UNREAD[path] = UNREAD.get(path, 0) + 1
    return measurements, sections, frames, problems


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument('runs', nargs='*', help='a mode (autotest, weapon) or two report paths (new, old)')
    ap.add_argument('--rel', type=float, default=0.2, help='relative change of the average to show (default 0.2)')
    ap.add_argument('--abs', type=float, default=0.05, help='absolute change of the average to show (default 0.05)')
    args = ap.parse_args()
    if len(args.runs) == 2:
        new_path, old_path = args.runs
    else:
        mode = args.runs[0] if args.runs else 'autotest'
        base = os.path.join(ROOT, 'reports', 'shots')
        new_path = os.path.join(base, mode, 'autotest.txt')
        old_path = os.path.join(base, mode + '-previous', 'autotest.txt')
    for p in (new_path, old_path):
        if not os.path.isfile(p):
            sys.exit(f'no report at {p}')
    new_m, new_s, new_f, new_p = read(new_path)
    old_m, old_s, old_f, old_p = read(old_path)

    print(f'new: {new_path}\nold: {old_path}\n')
    came, went = [new_p[k] for k in new_p if k not in old_p], [old_p[k] for k in old_p if k not in new_p]
    for path, count in UNREAD.items():
        print(f'(!) {count} table line(s) of {path} could not be read and are left out')
    print(f'Problems: {len(new_p)} now, {len(old_p)} before; {len(came)} new, {len(went)} gone, {len(new_p) - len(came)} in both')
    for text in came:
        print(f'  new  {text[:200]}')
    for text in went:
        print(f'  gone {text[:200]}')
    print()
    print('Frames')
    print(f'  new {new_f[len("Frames: "):] if new_f else "-"}')
    print(f'  old {old_f[len("Frames: "):] if old_f else "-"}\n')

    print('Mod sections (avg ms new / old, max ms new / old)')
    for name in sorted(set(new_s) | set(old_s), key=lambda n: -(new_s.get(n, old_s.get(n))[0])):
        n, o = new_s.get(name), old_s.get(name)
        if n and o:
            print(f'  {name:26} {n[0]:8.3f} / {o[0]:<8.3f} {n[1]:8.2f} / {o[1]:<8.2f}')
        else:
            print(f'  {name:26} {"only in the " + ("new" if n else "old") + " run"}')
    print()

    moved = []
    for name in sorted(set(new_m) | set(old_m)):
        n, o = new_m.get(name), old_m.get(name)
        if n is None or o is None:
            moved.append((float('inf'), f'  {name}: only in the {"new" if n else "old"} run'))
            continue
        a, b = n[2], o[2]
        diff = a - b
        rel = abs(diff) / max(abs(b), 1e-9)
        if abs(diff) > args.abs and rel > args.rel:
            moved.append((rel, f'  {name}: {b:g} -> {a:g} (avg; max {o[3]:g} -> {n[3]:g}, {o[1]} -> {n[1]} samples)'))
    print(f'Measurements that moved (avg over {args.rel:.0%} and {args.abs:g}): {len(moved)} of {len(set(new_m) | set(old_m))}')
    for _, text in sorted(moved, key=lambda t: -t[0]):
        print(text)


if __name__ == '__main__':
    main()
