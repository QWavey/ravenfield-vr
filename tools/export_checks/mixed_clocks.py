"""Timestamps set from one of Unity's clocks and read against another (handover lens 6, "clocks mixed").

Time.time slows with slow motion and stops in a pause; Time.unscaledTime and Time.realtimeSinceStartup do not. A field set from one and
compared with another drifts by however long slow motion or a pause lasted: a cooldown that never ends, or ends at once.

Reads src/ as text: for each name assigned `= Time.<clock>` (with an optional `+ offset`), the clocks it is read against
(`Time.<clock> - name`, `name - Time.<clock>`, `name < Time.<clock>`, `Time.<clock> >= name`, ...). Reported: names set from one clock and
read against another, in the same file.

Usage: python tools/export_checks/mixed_clocks.py
"""
import os
import re

ROOT = os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', '..', 'src')
CLOCK = r'Time\.(time|unscaledTime|realtimeSinceStartup|fixedTime|unscaledDeltaTime|deltaTime)'
NAME = r'([A-Za-z_][\w\.\[\]]*)'


def strip_comments(text):
    text = re.sub(r'/\*.*?\*/', '', text, flags=re.S)
    return re.sub(r'//[^\n]*', '', text)


def main():
    findings = []
    for d, _, fs in os.walk(ROOT):
        for f in fs:
            if not f.endswith('.cs'):
                continue
            p = os.path.join(d, f)
            t = strip_comments(open(p, encoding='utf-8-sig', errors='ignore').read())
            set_from = {}
            for m in re.finditer(NAME + r'\s*=\s*' + CLOCK + r'\b', t):
                name, clock = m.group(1), m.group(2)
                if clock in ('deltaTime', 'unscaledDeltaTime'):
                    continue
                set_from.setdefault(name.split('.')[-1], set()).add((clock, t.count('\n', 0, m.start()) + 1))
            read_as = {}
            pats = [
                CLOCK + r'\s*-\s*' + NAME,                      # Time.x - name
                NAME + r'\s*-\s*' + CLOCK,                      # name - Time.x
                CLOCK + r'\s*[<>]=?\s*' + NAME,                 # Time.x < name
                NAME + r'\s*[<>]=?\s*' + CLOCK,                 # name < Time.x
            ]
            for i, pat in enumerate(pats):
                for m in re.finditer(pat, t):
                    if i in (0, 2):
                        clock, name = m.group(1), m.group(2)
                    else:
                        name, clock = m.group(1), m.group(2)
                    if clock in ('deltaTime', 'unscaledDeltaTime'):
                        continue
                    read_as.setdefault(name.split('.')[-1], set()).add((clock, t.count('\n', 0, m.start()) + 1))
            for name, sets in set_from.items():
                reads = read_as.get(name, set())
                set_clocks = {c for c, _ in sets}
                bad = [(c, ln) for c, ln in reads if c not in set_clocks and not (c == 'realtimeSinceStartup' and 'unscaledTime' in set_clocks)
                       and not (c == 'unscaledTime' and 'realtimeSinceStartup' in set_clocks)]
                if bad:
                    rel = os.path.relpath(p, os.path.join(ROOT, '..'))
                    findings.append(f'{rel}: {name} set from {", ".join(sorted(f"Time.{c} (line {ln})" for c, ln in sets))}; '
                                    f'read against {", ".join(sorted(f"Time.{c} (line {ln})" for c, ln in bad))}')
    for f in sorted(findings):
        print(f)
    print(f'{len(findings)} timestamp(s) read against another clock')


if __name__ == '__main__':
    main()
