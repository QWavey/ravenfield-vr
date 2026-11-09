"""The trailer's seventh cut (2026-11-09 night): 2:00 on "Leave You Far Behind" by Lunatic Calm (song 6.1-126 s), every cut on the
beat (trailer.py's "beat" block), the song's landmarks pinned with cards (a card of 0 seconds is a pin only):
  8.9 title (the break) | 11.36 the full beat | 30.09 FULL MOD SUPPORT (the crash) | 39.09 REAL GAMEPLAY, the user's grenade throw with
  its blast on the re-entry (45.09) | 60 the stutter build: new scenes | 75 the quiet part: slow motion | 82.5 | 88.25 the jeep, its
  rocket on the accent (90) | 103.15 the helicopter, aimed in the drop-out (104), its blast on the re-entry (105.09) | 120 the end card.
The user (2026-11-09): "we need more clips ... more varity ... it also needs to match the song ... more", "vehicles driving at me
(enemys) and i shoot at them with RPGs and then at Helicopters too ... 2minutes ... not the same over and over".

  python -I tools/trailer/make_trailer_v7.py && python -I tools/trailer.py tools/trailer/trailer-v7.json showcase/clips/26-trailer-v7-2min.mp4
"""
import json
import os
import sys

R = os.path.abspath(os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', '..'))
sys.path.insert(0, os.path.join(R, 'tools', 'trailer'))
from make_montage import cut  # noqa: E402

T = os.path.join(R, 'tools', 'trailer')
V = 'showcase/gameplay/1109.mp4'
C = '2304:1296:0:72'
takes = {}
shots = []


def part(path, prefix):
    """An existing edit's shots (its closing card left out), its takes renamed with a prefix."""
    e = json.load(open(os.path.join(T, path)))
    for k, v in e['takes'].items():
        takes[prefix + k] = v
    for s in e['shots']:
        if 'card' in s:
            continue
        s = dict(s)
        if 'take' in s:
            s['take'] = prefix + s['take']
        shots.append(s)


def gp(a, b, spare=1.0):
    shots.append({"video": V, "in": a, "out": b, "crop": C, "spare": spare})


def card(title, seconds, at=None, sub=None):
    c = {"card": title, "seconds": seconds, "tail": 0.8}
    if sub:
        c['sub'] = sub
    if at is not None:
        c['at'] = at
    shots.append(c)


def pin(at):
    card('', 0.0, at)


def take(key, path, marks):
    """Shots from one take by marks: (mark, nth, in, out) with None for "the whole run"."""
    takes[key] = path
    for mark, nth, a, b in marks:
        s = {"take": key, "mark": mark}
        if nth:
            s['nth'] = nth
        if a is not None:
            s['in'] = a
        if b is not None:
            s['out'] = b
        shots.append(s)


def beat(key, path, kind):
    takes[key] = path
    shots.extend(cut(kind, key))


def fight(key, path, gun_mark='over the gun'):
    take(key, path, [('raise', 0, None, None), ('engage', 0, None, None), ('reverse', 0, None, 30), (gun_mark, 0, None, None)])


K = 'showcase/takes/'
# 6.1-30.09: the cold open, the title in the break, the fights from the full beat (as v6, the Canyon fight where slow motion was).
part('gp-cold-open.json', 'co.')
card('RAVENFIELD VR', 2.46, 8.9)
part('fight-day-v5.json', 'fd.')
gp(23.0, 25.1)
part('grab.json', 'gr.')
gp(1.0, 2.9)
gp(36.2, 37.6)
part('fight-night-v3.json', 'fn.')
gp(25.4, 26.8)
fight('kf', K + 'fight-canyon-20261109-2128')
gp(38.25, 39.0, 1.5)
# 30.09-60: FULL MOD SUPPORT on the crash, the montage, REAL GAMEPLAY, the grenade throw on the re-entry, the user's play to the accent.
card('FULL MOD SUPPORT', 1.5, 30.09, 'every workshop gun, in your hands')
part('montage-v9.json', 'm9.')
card('REAL GAMEPLAY', 0.94, 39.09)
part('gp-grenade-sequence.json', 'gs.')
part('gp-section-v5.json', 'gp.')
pin(60.0)
# 60-75: the stutter build: the night fight in the snow, Mountains, the pistol, more of the user's play, Coastline and Island beats.
take('tn', K + 'fight-temple_night-20261109-2127', [('contact', 0, 30, None), ('reverse', 0, None, 30), ('over the gun', 0, None, None)])
beat('m1', K + 'montage-01-hk416_a-swap-mountains-20261109-2131', 'swap')
beat('m2', K + 'montage-02-p220_t_s-rack-mountains-20261109-2131', 'rack')
beat('m3', K + 'montage-03-xm8-shotshelmet-mountains-20261109-2131', 'shots')
take('ps', K + 'pistol-mountains-20261109-1415', [('rack taking', 0, None, None), ('rack pulling back', 0, None, None), ('aim', 0, 0, 20)])
shots.extend([{"take": 'ps', "shot": k, "pre": 0, "post": 13} for k in range(2)])
gp(57.4, 59.0)
beat('m5', K + 'montage-05-p220_t_s-aimshots-mountains-20261109-2131', 'aim')
beat('m6', K + 'montage-06-asval-swap-mountains-20261109-2131', 'swap')
gp(32.0, 33.2)
beat('c5', K + 'montage-05-mwii_g17-aimshots-coastline-20261109-2113', 'aim')
beat('i6', K + 'montage-06-rk_44-swap-island-20261109-2115', 'swap')
beat('m7', K + 'montage-07-ak12h_squad-turn-mountains-20261109-2131', 'turn')
gp(46.5, 48.0)
take('fc', K + 'fight-dustbowl-20261109-1411', [('raise', 0, None, None), ('engage', 0, None, None), ('reverse', 0, None, 30)])
beat('c3', K + 'montage-03-ak12squad-shotshelmet-coastline-20261109-2113', 'shots')
gp(15.0, 16.4, 2.0)
pin(75.0)
# 75-82.5: the quiet part: slow motion, twice (Coastline, the QBZ192 and the AS VAL).
part('slowmo-v5.json', 's1.')
part('slowmo-v5.json', 's2.')
takes['s1.s'] = K + 'slowmo-coastline-20261109-1444'
takes['s2.s'] = K + 'slowmo-coastline-20261109-2129'
pin(82.5)
# 82.5-88.25: the scope's bolt by hand, another day fight's kill, more of the user's play.
take('sc', K + 'scope-mountains-20261109-1415', [('bolt taking', 0, None, None), ('bolt pulling back', 0, None, None), ('bolt let go', 0, None, 14)])
gp(44.7, 46.5)
take('fa', K + 'fight-dustbowl-20261109-1434', [('reverse', 0, None, 30), ('over the gun', 0, None, None)])
gp(12.6, 14.0, 1.5)
pin(88.25)
# 88.25-103.15: the jeep, its rocket on the accent (90); the user's ride, more kills and beats.
take('vj', K + 'vehicles-dustbowl-20261109-2138', [('jeep incoming', 0, None, None), ('aim', 0, None, None), ('fire', 0, None, None), ('jeep hit', 0, None, None)])
gp(3.0, 5.0)
take('na', K + 'fight-eagle_base_night-20261109-1413', [('reverse', 0, None, 40)])
beat('t6', K + 'montage-06-m4a1-swap-temple-20261109-1439', 'swap')
beat('t5', K + 'montage-05-glock_43x-aimshots-temple-20261109-1439', 'aim')
gp(39.0, 41.0)
beat('c6', K + 'montage-06-asval-swap-coastline-20261109-2113', 'swap')
beat('c7', K + 'montage-07-ak12squad-turn-coastline-20261109-2113', 'turn')
gp(66.6, 68.4, 2.0)
pin(103.15)
# 103.15-106.8: the helicopter, aimed in the drop-out, its blast on the re-entry (105.09).
take('vh', K + 'vehicles-dustbowl-20261109-2138', [('heli incoming', 0, None, None), ('aim', 1, None, None), ('fire', 1, None, None), ('heli hit', 0, None, None)])
# 106.8-120: the last burst, to the end card on the accent (120).
gp(26.8, 29.0)
take('fb', K + 'fight-dustbowl-20261109-1416', [('raise', 0, None, None), ('engage', 0, None, None)])
gp(51.9, 54.4)
beat('i7', K + 'montage-07-pkm-turn-island-20261109-2115', 'turn')
gp(70.81, 72.0, 0.0)
gp(59.0, 61.0)
gp(61.0, 63.4, 0.6)
card('RAVENFIELD VR', 2.6, 120.0, 'a virtual reality mod')
card('LEAVE YOU FAR BEHIND', 2.6, None, 'music by Lunatic Calm')

edit = {"fps": 60, "takes": takes, "shots": shots, "grade": True,
        "beat": {"start": 6.1, "grid0": 11.36, "period": 0.46875, "div": 2},
        "music": {"path": "showcase/music/Leave You Far Behind - Lunatic Calm.mp3", "start": 6.1, "gain": 0.7, "game": 0.8,
                  "fade_in": 0.3, "fade_out": 2.6}}
json.dump(edit, open(os.path.join(T, 'trailer-v7.json'), 'w'), indent=1)
print(f'trailer-v7.json: {len(shots)} cuts from {len(takes)} takes')
