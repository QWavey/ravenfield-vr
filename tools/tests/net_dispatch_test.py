"""Every message kind of the multiplayer game (NetGame's kind constants) reaches the handler meant for it in NetGame.OnMessage's
switch: no kind left without a case, none twice, and no other kind's case dropped into the middle of a group. A case for the
pings put inside the vehicles' group of cases in 1.17.12 took five of the vehicles' seven kinds, and no vehicle reached a joined
PC for three versions (multiplayer scout, 2026-11-12).
Run: python -I tools/tests/net_dispatch_test.py   (exit code 0 when all pass)"""
import os
import re
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
SRC = open(os.path.join(ROOT, 'src', 'Net', 'NetGame.cs'), encoding='utf-8').read()

failed = passed = 0


def check(ok, what):
    global failed, passed
    if ok:
        passed += 1
    else:
        failed += 1
        print('FAIL: ' + what)


# The kinds: "internal const byte MatchStart = 16, ActorInfo = 17, ..." (one declaration, over lines).
decl = re.search(r'internal const byte (MatchStart = 16.*?);', SRC, re.S)
check(decl is not None, 'the kind constants are found')
kinds = dict((name, int(value)) for name, value in re.findall(r'(\w+)\s*=\s*(\d+)', decl.group(1))) if decl else {}
check(len(kinds) >= 20, f'{len(kinds)} kinds read')
check(len(set(kinds.values())) == len(kinds), 'no two kinds share a number')

# OnMessage's switch: groups of case labels, each followed by its body.
start = SRC.find('private static void OnMessage(byte kind')
body = SRC[start:start + 20000] if start >= 0 else ''
check(start >= 0, 'OnMessage is found')
labels = re.findall(r'case (\w+):', body)
for name in kinds:
    check(labels.count(name) == 1, f'kind {name} has exactly one case ({labels.count(name)})')

# The vehicles' kinds: one group, its body the vehicles' handler, no other label in between.
vehicle = [k for k in kinds if k.startswith('Vehicle')]
group = re.search(r'((?:\s*case \w+:)+)\s*(?://[^\n]*\n\s*)*([^\n]*)', body[body.find('case ' + vehicle[0] + ':') - 40:]) if vehicle else None
names = re.findall(r'case (\w+):', group.group(1)) if group else []
check(sorted(names) == sorted(vehicle), f'the vehicles\' cases form one group of their own ({names})')
check(group is not None and 'NetVehicles.OnMessage' in group.group(2), 'that group goes to NetVehicles.OnMessage')

print(f'{passed} passed, {failed} failed')
sys.exit(1 if failed else 0)
