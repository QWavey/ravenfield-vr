"""Every Harmony patch that names its game method by a string ("FixedUpdate", "GetInput") finds that method in the decompiled
game (.ref/decompiled): a nameof() target is checked by the compiler, a string one only fails at start-up, as a patch that
silently does nothing once a game update renames or removes the method. Skipped without the decompiled game (not in git).
Run: python -I tools/tests/harmony_targets_test.py   (exit code 0 when all pass)"""
import glob
import os
import re
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
# (RFVR_DECOMPILED: another decompile to check against, a game update's: tools/check-game-update.ps1)
DECOMPILED = os.environ.get('RFVR_DECOMPILED') or os.path.join(ROOT, '.ref', 'decompiled')
PATCH = re.compile(r'HarmonyPatch\(\s*typeof\(\s*([A-Za-z0-9_.]+)\s*\)\s*,\s*"([A-Za-z0-9_]+)"')
# Members read by name through reflection (a field ref, a method looked up): the same, and worse when wrong. A FieldRefAccess is built in a
# static initializer, so a renamed field throws TypeInitializationException from every use of its class (the real-time grenades' and
# bullets' prefixes run for every grenade and bullet every frame; 2026-11-08).
REFLECTED = [
    re.compile(r'FieldRefAccess<\s*([A-Za-z0-9_.]+)\s*,[^>]*>\(\s*"([A-Za-z0-9_]+)"'),
    re.compile(r'AccessTools\.(?:Method|Field|Property|PropertyGetter|PropertySetter|DeclaredMethod|DeclaredField)\(\s*typeof\(\s*([A-Za-z0-9_.]+)\s*\)\s*,\s*"([A-Za-z0-9_]+)"'),
]


def game_file(type_name):
    """The decompiled file of a game type (by its simple name), in either of the game's assemblies, or None."""
    simple = type_name.split('.')[-1]
    for assembly in ('Assembly-CSharp', 'firstpass'):
        hits = glob.glob(os.path.join(DECOMPILED, assembly, '**', simple + '.cs'), recursive=True)
        if hits:
            return hits[0]
    return None


def main():
    if not os.path.isdir(DECOMPILED):
        print('skipped: no decompiled game in ' + DECOMPILED)
        return 0
    failed = passed = skipped = 0
    for path in glob.glob(os.path.join(ROOT, 'src', '**', '*.cs'), recursive=True):
        text = open(path, encoding='utf-8').read()
        for m in PATCH.finditer(text):
            type_name, method = m.group(1), m.group(2)
            where = f'{os.path.relpath(path, ROOT)}: {type_name}.{method}'
            source = game_file(type_name)
            if source is None:
                skipped += 1  # (a Unity or other engine type: not decompiled here)
                continue
            body = open(source, encoding='utf-8', errors='replace').read()
            if re.search(r'\b' + re.escape(method) + r'\s*\(', body) and re.search(r'(void|bool|int|float|IEnumerator|Vector3|[A-Z]\w*)\s+' + re.escape(method) + r'\s*\(', body):
                passed += 1
            else:
                print(f'FAIL  {where}: no method {method} in {os.path.relpath(source, ROOT)}')
                failed += 1
        for pat in REFLECTED:
            for m in pat.finditer(text):
                type_name, member = m.group(1), m.group(2)
                where = f'{os.path.relpath(path, ROOT)}: {type_name}.{member} (by reflection)'
                source = game_file(type_name)
                if source is None:
                    skipped += 1
                    continue
                body = open(source, encoding='utf-8', errors='replace').read()
                # declared there: a field, a property or a method of that name
                if re.search(r'[\w>\]]\s+' + re.escape(member) + r'\s*(;|=|\(|\{)', body):
                    passed += 1
                else:
                    print(f'FAIL  {where}: no member {member} in {os.path.relpath(source, ROOT)}')
                    failed += 1
    print(f'{passed} passed, {failed} failed ({skipped} engine types not checked)')
    return 1 if failed else 0


if __name__ == '__main__':
    sys.exit(main())
