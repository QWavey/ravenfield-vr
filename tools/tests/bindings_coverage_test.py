"""Every controller's SteamVR bindings bind every button action of the manifest, but for the gaps listed here on purpose (a
binding dropped by accident is a dead button on that controller, found only by someone who owns it).
Run: python -I tools/tests/bindings_coverage_test.py   (exit code 0 when all pass)"""
import glob
import re
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

# The code against the manifest: VRInput looks each button up by its DigitalAction name ("/actions/main/in/" + name), and the
# analog, pose, skeleton and haptic actions by their paths. A name the manifest lacks is a handle SteamVR never fills (that
# button or stick does nothing, with only a warning in the log); a button the manifest has and the code does not is bound
# for nothing.
code = open(os.path.join(ROOT, 'src', 'Input', 'VRInput.cs'), encoding='utf-8').read()
enum_body = re.search(r'public enum DigitalAction\s*\{(.*?)\}', code, re.S).group(1)
enum_names = re.findall(r'^\s*(?:,\s*)?(\w+)', re.sub(r'//[^\n]*', '', enum_body).replace(',', '\n'), re.M)
check(sorted(enum_names) == sorted(buttons), f'the code\'s DigitalAction names are the manifest\'s button actions '
      f'(only in the code: {sorted(set(enum_names) - set(buttons))}, only in the manifest: {sorted(set(buttons) - set(enum_names))})')
kinds = {a['name']: a.get('type') for a in manifest['actions']}
want = {'TriggerPull': 'vector1', 'GripPull': 'vector1', 'Move': 'vector2', 'TurnStick': 'vector2', 'Pose': 'pose', 'PoseGrip': 'pose',
        'PoseTip': 'pose', 'SkeletonLeft': 'skeleton', 'SkeletonRight': 'skeleton', 'Haptic': 'vibration'}
used = set(re.findall(r'"(/actions/main/(?:in|out)/\w+)"', code))
wrong = sorted(f'{p} ({kinds.get(p, "missing")})' for p in used if kinds.get(p) != want.get(p.split('/')[-1], kinds.get(p)) or p not in kinds)
check(len(used) >= 10 and not wrong, f'every action path the code asks for is in the manifest with its type ({len(used)} paths; wrong: {wrong})')

print(f'{passed} passed, {failed} failed')
sys.exit(1 if failed else 0)
