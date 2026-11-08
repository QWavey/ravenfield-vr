"""Switches and tables over the mod's own enums that name most of an enum's values but not all of them.

A value added to an enum without its row in every switch or table over it falls through unseen: `MechSound.HandGrab` (Tasks 365) had
no row in the sound cut table, `CutsFor` returned null for it, and a loop over that null threw in the weapons' frame for two releases
(HANDOVER, lens 8). This reads src/ as text (no compiler):

- every `enum Name { A, B, ... }` declared in the mod;
- every `switch (...)` block whose case labels are `Name.X`: reported when it has no `default:` and names at least half of the enum's
  values but not all (a switch over two of twelve values is a deliberate pick, not a table);
- every collection initializer with `[Name.X] =` or `{ Name.X, ... }` keys (a Dictionary keyed by the enum): reported the same way.

Usage: python tools/export_checks/enum_switches.py [--all]   (--all: also the switches with a default)
Exit code 0 always; the lines are for reading.
"""
import os
import re
import sys

ROOT = os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', '..', 'src')
# Known and meant (file name, enum): not counted. The Game preset has no profile of its own (the game's quality is used); the intro's
# Done phase does nothing.
KNOWN = {('GraphicsPresets.cs', 'GraphicsPreset'), ('IntroStage.cs', 'Phase')}

ENUM_RE = re.compile(r'\benum\s+(\w+)\s*(?::\s*\w+\s*)?\{([^}]*)\}', re.S)


def strip_comments(text):
    text = re.sub(r'/\*.*?\*/', '', text, flags=re.S)
    return re.sub(r'//[^\n]*', '', text)


def enum_members(body):
    names = []
    for part in body.split(','):
        part = part.strip()
        if not part:
            continue
        m = re.match(r'(\w+)', part)
        if m:
            names.append(m.group(1))
    return names


def block_after(text, start):
    """The text of the {...} block whose opening brace is the first at or after start."""
    i = text.find('{', start)
    if i < 0:
        return None, -1
    depth = 0
    for j in range(i, len(text)):
        c = text[j]
        if c == '{':
            depth += 1
        elif c == '}':
            depth -= 1
            if depth == 0:
                return text[i + 1:j], j
    return None, -1


def main():
    show_all = '--all' in sys.argv
    files = []
    for d, _, fs in os.walk(ROOT):
        for f in fs:
            if f.endswith('.cs'):
                files.append(os.path.join(d, f))
    texts = {}
    enums = {}
    for p in files:
        t = strip_comments(open(p, encoding='utf-8-sig', errors='ignore').read())
        texts[p] = t
        for m in ENUM_RE.finditer(t):
            members = enum_members(m.group(2))
            if len(members) >= 3:
                enums.setdefault(m.group(1), members)
    findings = []
    for p, t in texts.items():
        rel = os.path.relpath(p, os.path.join(ROOT, '..'))
        # switches
        for m in re.finditer(r'\bswitch\s*\(', t):
            body, end = block_after(t, m.end())
            if body is None:
                continue
            # only this switch's own labels: nested switches are taken on their own
            labels = re.findall(r'\bcase\s+(\w+)\.(\w+)\s*(?:when[^:]*)?:', body)
            if not labels:
                continue
            by_enum = {}
            for e, v in labels:
                if e in enums:
                    by_enum.setdefault(e, set()).add(v)
            has_default = re.search(r'\bdefault\s*:', body) is not None
            for e, named in by_enum.items():
                members = enums[e]
                missing = [v for v in members if v not in named]
                if missing and len(named) * 2 >= len(members) and (show_all or not has_default):
                    line = t.count('\n', 0, m.start()) + 1
                    findings.append(f'{rel}:{line}: switch over {e} names {len(named)} of {len(members)}{" (has a default)" if has_default else ""}; '
                                    f'missing: {", ".join(missing)}')
        # tables keyed by an enum: [E.X] = ... or { E.X, ... }
        for m in re.finditer(r'new\s+(?:System\.Collections\.Generic\.)?Dictionary\s*<\s*(\w+)\s*,', t):
            e = m.group(1)
            if e not in enums:
                continue
            body, end = block_after(t, m.end())
            if body is None:
                continue
            keys = set(re.findall(r'\[\s*' + re.escape(e) + r'\.(\w+)\s*\]\s*=', body))
            keys |= set(re.findall(r'\{\s*' + re.escape(e) + r'\.(\w+)\s*,', body))
            if not keys:
                continue
            members = enums[e]
            missing = [v for v in members if v not in keys]
            if missing and len(keys) * 2 >= len(members):
                line = t.count('\n', 0, m.start()) + 1
                findings.append(f'{rel}:{line}: table keyed by {e} has {len(keys)} of {len(members)}; missing: {", ".join(missing)}')
    known = [f for f in findings if any(f.split(':')[0].endswith(name) and (' over ' + e + ' ' in f or ' by ' + e + ' ' in f) for name, e in KNOWN)]
    for f in sorted(findings):
        print(('known: ' if f in known else '') + f)
    print(f'{len(findings) - len(known)} switch(es) or table(s) over {len(enums)} enum(s) missing values ({len(known)} known and meant)')


if __name__ == '__main__':
    main()
