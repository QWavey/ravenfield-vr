"""Offline (the sandbox export): bolts and carriers left still by the rack. A self-loading gun whose moving part is its charging
handle, or a slide apart from its bolt, may have a bolt or carrier of its own (seen in the ejection port) that is none of the
parts the mod moves: racked, the handle comes back and the bolt in the port stays (Tasks 307, guns not named). Lists, per gun,
the drawn nodes named as a bolt or carrier outside the slide's, bolt's and handle's subtrees, with their size.
Usage: still_bolts.py [weapon dir ...]   (all of the export by default)"""
import json
import os
import re
import sys

sys.stdout.reconfigure(encoding='utf-8')
ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))  # the repository
sys.path.insert(0, os.path.join(ROOT, 'tools', 'sandbox', 'app'))
import glb  # noqa: E402

DATA = os.path.join(ROOT, 'reports', 'sandbox', 'data', 'weapons')
# A bolt or its carrier by name (as the analysis's SlideName words for one; not a bolt catch, release, stop or lock).
BOLT = re.compile(r'(^|[^a-z])(bolt|carrier|bcg|verschluss|culasse|cerrojo|zatvor)(?!.*(catch|release|stop|lock|knob|handle|key|button|hold))'
                  r'|затвор|枪机|机框|枪击', re.IGNORECASE)
NOT = re.compile(r'catch|release|stop|lock|knob|handle|key|button|hold|空仓|挂机|拉机柄|机柄|lever', re.IGNORECASE)


def under(path, roots):
    return any(r and (path == r or path.startswith(r + '/')) for r in roots)


folders = sys.argv[1:] or sorted(os.listdir(DATA))
found = 0
for folder in folders:
    try:
        rig = json.load(open(os.path.join(DATA, folder, 'rig.json'), encoding='utf-8'))
    except OSError:
        continue
    if rig.get('action') != 'SelfLoading':
        continue
    moving = [rig.get('slide'), rig.get('bolt'), rig.get('handle')]
    if not any(moving):
        continue
    try:
        nodes = glb.load(os.path.join(DATA, folder, 'model.glb'))
    except Exception:
        continue
    total = sum(sum(len(p.indices) // 3 for p in n.primitives) for n in nodes) or 1
    still = []
    for n in nodes:
        if n.stand_in or under(n.path, moving) or under(n.path, [rig.get('magazine')] + list(rig.get('magazineParts', []))):
            continue
        full = n.name
        if BOLT.search(full) and not NOT.search(n.short):
            tris = sum(len(p.indices) // 3 for p in n.primitives)
            size = (n.bmax - n.bmin) * 100
            still.append(f'{n.path} {n.short!r} ({tris} tris, {size[0]:.1f} x {size[1]:.1f} x {size[2]:.1f} cm)')
    if still:
        found += 1
        print(f'{folder}: moving slide {rig.get("slide")} bolt {rig.get("bolt")} handle {rig.get("handle")}; still: ' + '; '.join(still))
print(f'{found} guns with a bolt or carrier the rack leaves still')
