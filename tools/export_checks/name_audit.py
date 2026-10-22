"""Offline: what the mod's part-name patterns take in the exported weapons.

Every `static readonly Regex Name = new Regex(@"...")` in src/ is read from the source itself (so this never drifts from the
code), compiled with Python's re (the few .NET-only constructs are reported and skipped) and run over the node names of the
sandbox export (reports/sandbox/data/weapons/*/model.glb: the leaf name of every part of every weapon). For each pattern:
how many names and weapons it takes, and the names, most common first, so a word that takes the wrong parts shows (the GLOCK
43X's '43X_BJ' read as a 3x magnifier; 'Charging Handle' as an arm bone).

A pattern is run on the leaf name as it is, out of the context the code uses it in: the finger patterns only look under a
hand bone, the analyzer's arm test keeps arm-named parts that are meshes of their own, so a name listed here is a lead to
check in the code, not a defect by itself. Where the code changes a name before testing it, the same change is made here
(NORMALIZE: the grenade pin's name split at its capitals and its non-letters, as ThrowableHandler.FindModPin does).

Usage: python -I tools/export_checks/name_audit.py [--pattern NAME] [--all] [--top N]
"""
import argparse
import collections
import json
import os
import re
import struct
import sys

REPO = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
DATA = os.path.join(REPO, 'reports', 'sandbox', 'data', 'weapons')

# A Regex field's start; its argument is read to the closing parenthesis by _argument (strings joined with '+', comments
# between them, verbatim or not: a pattern split over commented lines, MagName's, was missed by a single expression).
FIELD = re.compile(r'Regex\s+(\w+)\s*=\s*new\s+Regex\(')
ESCAPES = {'n': '\n', 't': '\t', 'r': '\r', '0': '\0', '\\': '\\', '"': '"', "'": "'"}


def _argument(text, at):
    """From just after 'new Regex(': (the pattern, the options text), the string parts joined; comments skipped."""
    parts, options, depth, i, first = [], [], 0, at, True
    while i < len(text):
        c = text[i]
        if text.startswith('//', i):
            i = text.find('\n', i)
            i = len(text) if i < 0 else i
            continue
        if c == '@' and text.startswith('@"', i):
            j, buf = i + 2, []
            while j < len(text):
                if text[j] == '"' and text.startswith('""', j):
                    buf.append('"')
                    j += 2
                elif text[j] == '"':
                    break
                else:
                    buf.append(text[j])
                    j += 1
            (parts if first else options).append(''.join(buf))
            i = j + 1
            continue
        if c == '"':
            j, buf = i + 1, []
            while j < len(text) and text[j] != '"':
                if text[j] == '\\' and j + 1 < len(text):
                    buf.append(ESCAPES.get(text[j + 1], '\\' + text[j + 1]))
                    j += 2
                else:
                    buf.append(text[j])
                    j += 1
            (parts if first else options).append(''.join(buf))
            i = j + 1
            continue
        if c == '(':
            depth += 1
        elif c == ')':
            if depth == 0:
                break
            depth -= 1
        elif c == ',' and depth == 0:
            first = False
        if not first:
            options.append(c)
        i += 1
    return ''.join(parts), ''.join(options)


def _pin_words(name):
    """ThrowableHandler.FindModPin: 'm67_safety_pin' -> 'm safety pin' (capitals split, non-letters to spaces, lower case)."""
    return re.sub('[^A-Za-z]+', ' ', re.sub('(?<=[a-z])(?=[A-Z])', ' ', name)).lower()


# (file, field) -> the change the code makes to a name before the pattern sees it.
NORMALIZE = {('src/Weapons/ThrowAndMelee.cs', 'PinName'): _pin_words}


def patterns():
    """(file, field name, pattern text, ignore case) for every Regex field in src/."""
    out = []
    for folder, _, files in os.walk(os.path.join(REPO, 'src')):
        for f in sorted(files):
            if not f.endswith('.cs'):
                continue
            path = os.path.join(folder, f)
            text = open(path, encoding='utf-8').read()
            for m in FIELD.finditer(text):
                body, options = _argument(text, m.end())
                out.append((os.path.relpath(path, REPO).replace('\\', '/'), m.group(1), body, 'IgnoreCase' in options))
    return out


def node_names():
    """{weapon folder: [leaf node names]} from the export's GLB files (their JSON chunk)."""
    names = {}
    if not os.path.isdir(DATA):
        return names
    for w in sorted(os.listdir(DATA)):
        p = os.path.join(DATA, w, 'model.glb')
        try:
            data = open(p, 'rb').read()
            length = struct.unpack_from('<I', data, 12)[0]
            gltf = json.loads(data[20:20 + length].decode('utf-8'))
        except (OSError, ValueError, struct.error):
            continue
        names[w] = [n.get('name', '').split('/')[-1] for n in gltf.get('nodes', [])]
    return names


def main():
    sys.stdout.reconfigure(encoding='utf-8')
    ap = argparse.ArgumentParser()
    ap.add_argument('--pattern', help='only fields whose name contains this')
    ap.add_argument('--all', action='store_true', help='list every name taken, not only the most common')
    ap.add_argument('--top', type=int, default=12)
    a = ap.parse_args()
    names = node_names()
    if not names:
        print(f'no export at {DATA} (the sandbox export writes it)')
        return 0
    total = sum(len(v) for v in names.values())
    print(f'{len(names)} weapons, {total} part names')
    skipped = []
    for file, field, body, icase in patterns():
        if a.pattern and a.pattern.lower() not in field.lower():
            continue
        try:
            rx = re.compile(body, re.I if icase else 0)
        except re.error as e:
            skipped.append(f'{file} {field}: {e}')
            continue
        taken = collections.Counter()
        weapons = set()
        prepare = NORMALIZE.get((file, field), lambda n: n)
        for w, ns in names.items():
            for n in ns:
                if rx.search(prepare(n)):
                    taken[n] += 1
                    weapons.add(w)
        shown = taken.most_common() if a.all else taken.most_common(a.top)
        print(f'\n{field} ({file}): {sum(taken.values())} names in {len(weapons)} weapons')
        if shown:
            print('  ' + ', '.join(f'{n} x{c}' if c > 1 else n for n, c in shown))
    for s in skipped:
        print('skipped (not Python re): ' + s)
    return 0


if __name__ == '__main__':
    sys.exit(main())
