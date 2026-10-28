"""Every setting the README's settings table names exists in VRConfig.cs under that key, with the default the README gives (the
README said SinglePassInstanced drew only some shaders in one eye and friends were never hurt, long after the code said otherwise:
2026-10-28). Defaults compared: on/off against bools, numbers against numbers, words against enum values.
Run: python -I tools/tests/readme_settings_test.py   (exit code 0 when all pass)"""
import os
import re
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))


def readme_rows():
    text = open(os.path.join(ROOT, 'README.md'), encoding='utf-8').read()
    m = re.search(r'^## Settings\n(.*?)^## ', text, re.S | re.M)
    rows = []
    for line in (m.group(1) if m else '').splitlines():
        cells = [c.strip() for c in line.strip().strip('|').split('|')]
        if len(cells) < 2 or not cells[0].startswith('`'):
            continue
        keys = re.findall(r'`([A-Za-z0-9]+)`', cells[0])
        defaults = [d.strip() for d in cells[1].split('/')]
        for i, key in enumerate(keys):
            rows.append((key, defaults[i] if i < len(defaults) else ''))
    return rows


def config_defaults():
    """Key -> default text, from every cfg.Bind("Section", "Key", default, ...) in VRConfig.cs."""
    text = open(os.path.join(ROOT, 'src', 'Core', 'VRConfig.cs'), encoding='utf-8').read()
    out = {}
    for m in re.finditer(r'cfg\.Bind\(\s*"[^"]*"\s*,\s*"([^"]+)"\s*,\s*([^,]+?)\s*,', text):
        out[m.group(1)] = m.group(2).strip()
    return out


def same(readme_default, code_default):
    r = readme_default.lower().replace('°', '').replace(' m', '').strip()
    c = code_default.strip()
    if r == '':
        return True  # (no default given in the README)
    if c in ('true', 'false'):
        return (r == 'on') == (c == 'true') and r in ('on', 'off')
    number = re.match(r'^-?[\d.]+f?$', c)
    if number:
        try:
            return abs(float(r) - float(c.rstrip('f'))) < 1e-6
        except ValueError:
            return False
    return c.split('.')[-1].lower() == r.replace(' ', '').lower()  # an enum value: Snap, MultiPass, OffHandHip


failed = passed = 0
defaults = config_defaults()
for key, default in readme_rows():
    if key not in defaults:
        print(f'FAIL  {key}: in the README, no such setting in VRConfig.cs')
        failed += 1
    elif not same(default, defaults[key]):
        print(f'FAIL  {key}: README says {default!r}, VRConfig.cs binds {defaults[key]!r}')
        failed += 1
    else:
        passed += 1
print(f'{passed} passed, {failed} failed')
sys.exit(1 if failed else 0)
