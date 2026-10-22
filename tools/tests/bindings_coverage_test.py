"""Every controller's SteamVR bindings bind every button action of the manifest, but for the gaps listed here on purpose (a
binding dropped by accident is a dead button on that controller, found only by someone who owns it).
Run: python -I tools/tests/bindings_coverage_test.py   (exit code 0 when all pass)"""
import glob
import json
import os
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
STEAMVR = os.path.join(ROOT, 'src', 'Input', 'SteamVR')

# Not bound on purpose, and how the player reaches them there instead.
INTENDED = {
    'rift': None,  # the headset's own profile: no buttons the game uses (an empty binding keeps SteamVR from asking for one)
    'oculus_touch': {'ModKeys': 'from the VR settings panel (left stick double-click)',
                     'Countermeasures': 'A in a seat with no gun in hand (GameInputBridge.ComputeSeated)'},
    'hpmotioncontroller': {'ModKeys': 'from the VR settings panel', 'Countermeasures': 'A in a seat with no gun in hand'},
    'vive_controller': {'Scoreboard': 'the wrist view', 'Countermeasures': 'the menu button in a seat with no gun in hand'},
    'knuckles': {},
}

failed = passed = 0


def check(ok, what):
    global failed, passed
    if ok:
        passed += 1
    else:
        failed += 1
        print('FAIL', what)


manifest = json.load(open(os.path.join(STEAMVR, 'actions.json'), encoding='utf-8'))
buttons = {a['name'].split('/')[-1]: a['name'].lower() for a in manifest['actions'] if a.get('type') == 'boolean'}
check(len(buttons) >= 15, f'the manifest has its button actions ({len(buttons)})')
files = sorted(glob.glob(os.path.join(STEAMVR, 'bindings_*.json')))
seen = set()
for path in files:
    doc = json.load(open(path, encoding='utf-8'))
    kind = doc.get('controller_type')
    seen.add(kind)
    bound = set()
    for action_set in doc['bindings'].values():
        for src in action_set.get('sources', []):
            for v in (src.get('inputs') or {}).values():
                bound.add(v.get('output', '').lower())
        for chord in action_set.get('chords', []) or []:
            bound.add(chord.get('output', '').lower())
    if kind not in INTENDED:
        check(False, f'{os.path.basename(path)}: controller type {kind!r} has no entry in INTENDED')
        continue
    allowed = INTENDED[kind]
    if allowed is None:
        continue
    missing = sorted(name for name, full in buttons.items() if full not in bound and name not in allowed)
    check(not missing, f'{kind}: every button action bound (missing: {missing})')
    stale = sorted(name for name in allowed if buttons.get(name) in bound)
    check(not stale, f'{kind}: the intended gaps are still gaps (now bound: {stale}; drop them from INTENDED)')
check(seen >= set(INTENDED), f'a bindings file for every controller listed ({sorted(set(INTENDED) - seen)} missing)')

print(f'{passed} passed, {failed} failed')
sys.exit(1 if failed else 0)
