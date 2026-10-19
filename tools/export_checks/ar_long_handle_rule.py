"""Offline: an AR's charging handle modelled as one long part (the rod and its T together, as the MWII M4s have it: 17 cm
along the bore, 5.6 cm across its T, 1.8 cm tall), which WeaponAnalyzer.ArOwnHandleNoSlide's short-T limits (3-12 cm
long, 1.5 cm tall) refuse. For every AR-pattern gun in the export: the parts such a rule would take (12-22 cm along the
bore and longest that way, 3 cm or more across, 2.2 cm tall at most, its middle 0-3.2 cm over the bore and within 1.5 cm
of its side, its back end within 6 cm of the rule's guess along the bore), next to what the rig has now.
Usage: ar_long_handle_rule.py"""
import glob
import json
import os
import re
import sys

import numpy as np

sys.stdout.reconfigure(encoding='utf-8')
ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))  # the repository
sys.path.insert(0, os.path.join(ROOT, 'tools', 'sandbox', 'app'))
import glb  # noqa: E402

DATA = os.path.join(ROOT, 'reports', 'sandbox', 'data', 'weapons')
AR = re.compile(r'(?<![a-z])(m4|m4a1|m16|ar-?15|ar-?10|hk ?416|hk ?417|m ?417|mr ?308|mr ?762|lmt ?308|mk ?18|mk ?12|m733|m727|xm177|car-?15|cqbr|urgi|sr-?25|m110|mcx|honey ?badger|ddm4|m27|g28|lvoa|k13)(?!\d)', re.I)
OPTIC = re.compile(r'scope|optic|sight|acog|eotech|holo|reflex|red ?dot|aimpoint|elcan|kimme|visier|瞄', re.I)


def norm(a):
    a = np.array(a, float)
    return a / np.linalg.norm(a)


for d in sorted(glob.glob(os.path.join(DATA, '*'))):
    try:
        rig = json.load(open(os.path.join(d, 'rig.json'), encoding='utf-8'))
    except Exception:
        continue
    if not AR.search(rig.get('key') or '') or rig.get('action') != 'SelfLoading' or rig.get('handgun') or rig.get('machineGun'):
        continue
    aim = norm(rig['aim'])
    up = np.array(rig['up'], float)
    up = norm(up - aim * (up @ aim))
    across = np.cross(up, aim)
    grip, muzzle = np.array(rig['grip'], float), np.array(rig['muzzle'], float)
    bore_above_grip = muzzle + aim * ((grip - muzzle) @ aim)
    guess = bore_above_grip - aim * 0.05 + up * 0.03
    mags = [p for p in [rig.get('magazine')] + list(rig.get('magazineParts', [])) if p]
    picks = []
    for n in glb.load(os.path.join(d, 'model.glb')):
        t = n.triangles()
        if n.stand_in or len(t) < 4 or any(n.path == m or n.path.startswith(m + '/') for m in mags) or OPTIC.search(n.name.split('/')[-1]):
            continue
        p = t.reshape(-1, 3)
        a, u, s = p @ aim, p @ up, p @ across
        length, height, width = np.ptp(a), np.ptp(u), np.ptp(s)
        c = p.mean(axis=0)
        above = (c - muzzle) @ up
        aside = abs((c - muzzle) @ across)
        rear = a.min()
        if not (0.12 <= length <= 0.22 and length >= max(height, width) and width >= 0.03 and height <= 0.022):
            continue
        if not (0.0 <= above <= 0.032 and aside <= 0.015 and abs(rear - guess @ aim) <= 0.06):
            continue
        picks.append(f"{n.path} '{n.name.split('/')[-1][:30]}' {length * 100:.1f}x{width * 100:.1f}x{height * 100:.1f} cm, {above * 100:.1f} over the bore, back end {(rear - guess @ aim) * 100:+.1f} cm from the guess")
    now = f"slide {rig.get('slide')} handle {rig.get('handle')} [{(rig.get('slideMethod') or '')[:60]}]"
    print(f"{os.path.basename(d)[:48]:48s} {now}\n    {'; '.join(picks) if picks else '(none)'}")
