"""The trailer's eighth cut (2026-11-09 night): v7 after the user's notes. Out: the user's pistol kill (v7 0:42), the pistol's rack and
the trench (0:57-1:00, "hands are extending"), everything of 1:00-1:08 ("shit too"), the second slow motion and the first one's raise
(1:12-1:15, "hands wrong placed infront of face"), the scope's bolt (1:16), the user's clips at 1:17 and 1:20. The vehicles filmed again
with the RPG-7 itself (v7 drew the revolver), the rocket in slow motion with a camera behind it (0.15 of time), each rocket's impact pinned
to the song: the jeep's on the accent at 90, the helicopter's on the re-entry at 105.09. New: fights on Mountains, Island, Coastline;
vehicles on Canyon (a tank) and Island. Every cut ends on the beat (shortened onto the grid only).

  python -I tools/trailer/make_trailer_v8.py && python -I tools/trailer.py tools/trailer/trailer-v8.json showcase/clips/27-trailer-v8-2min.mp4
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
K = 'showcase/takes/'
takes = {}
shots = []


def part(path, prefix, drop=()):
    e = json.load(open(os.path.join(T, path)))
    for k, v in e['takes'].items():
        takes[prefix + k] = v
    for s in e['shots']:
        if 'card' in s or s.get('mark') in drop:
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


def kills(key, path, contact=False):
    """A fight's kills: (the soldiers coming,) the sights and the burst, the reverse shot (ended before a rifle flies), over the gun."""
    m = [('contact', 0, 30, None)] if contact else [('raise', 0, None, None), ('engage', 0, None, None)]
    take(key, path, m + [('reverse', 0, None, 30), ('over the gun', 0, None, None)])


def rocket(key, path, which, incoming=True, flight=True):
    """A vehicle's part of the vehicles take: (it coming,) the sights, the rocket fired, its flight in slow motion, the blast."""
    nth = 0 if which == 'jeep' else 1
    m = ([(which + ' incoming', 0, None, None)] if incoming else []) + [('aim', nth, None, None), ('fire', nth, None, None)]         + ([(which + ' rocket', 0, None, None)] if flight else []) + [(which + ' hit', 0, None, None)]
    take(key, path, m)


# 6.1-30.09: as v7.
part('gp-cold-open.json', 'co.')
card('RAVENFIELD VR', 2.46, 8.9)
part('fight-day-v5.json', 'fd.')
takes['fd.f'] = K + 'fight-dustbowl-20261109-1446'
gp(23.0, 25.1)
part('grab.json', 'gr.')
takes['gr.g'] = K + 'grab-dustbowl-20261109-1447'  # (the newest grabs, Coastline and Island, missed their hold)
gp(1.0, 2.9)
gp(36.2, 37.6)
part('fight-night-v3.json', 'fn.')
takes['fn.f'] = K + 'fight-eagle_base_night-20261109-1442'  # (the newest found no place for soldiers)
gp(25.4, 26.8)
kills('kf', K + 'fight-canyon-20261109-2128')
gp(38.25, 39.0, 3.0)
# 30.09-60: FULL MOD SUPPORT on the crash, the montage, REAL GAMEPLAY, the grenade throw on the re-entry, the user's play (without the
# pistol kill) to the accent.
card('FULL MOD SUPPORT', 1.5, 30.09, 'every workshop gun, in your hands')
part('montage-v9.json', 'm9.')
gp(59.0, 59.6, 2.5)
card('REAL GAMEPLAY', 0.94, 39.09)
part('gp-grenade-sequence.json', 'gs.')
for a, b in [(29.0, 31.8), (49.9, 51.9), (55.0, 57.0), (5.2, 7.2), (63.4, 66.6)]:
    gp(a, b)
gp(68.4, 70.81, 1.2)
pin(60.0)
# 60-75: the stutter build: fights on five maps.
kills('tn', K + 'fight-temple_night-20261109-2127', contact=True)
beat('m1', K + 'montage-01-hk416_a-swap-mountains-20261109-2131', 'swap')
beat('m2', K + 'montage-02-p220_t_s-rack-mountains-20261109-2131', 'rack')
beat('m3', K + 'montage-03-xm8-shotshelmet-mountains-20261109-2131', 'shots')
kills('fm', K + 'fight-mountains-20261109-2201')
kills('fi', K + 'fight-island-20261109-2202')
take('fa', K + 'fight-dustbowl-20261109-1434', [('reverse', 0, None, 30), ('over the gun', 0, None, None)])
beat('i7', K + 'montage-07-pkm-turn-island-20261109-2115', 'turn')
take('fx', K + 'fight-coastline-20261109-2204', [('contact', 0, 30, None)])
take('fz', K + 'fight-dustbowl-20261109-1409', [('reverse', 0, None, 30)])
take('fy', K + 'fight-island-20261109-2202', [('after', 0, None, None)])
gp(26.8, 29.0, 1.5)
pin(75.0)
# 75-82.5: the quiet part: slow motion (the swap, not the raise after it), the Coastline fight, more beats.
part('slowmo-v5.json', 's1.', drop=('back',))
takes['s1.s'] = K + 'slowmo-coastline-20261109-1444'
kills('fc', K + 'fight-coastline-20261109-2204')
take('na', K + 'fight-eagle_base_night-20261109-1413', [('reverse', 0, None, 40)])
beat('c6', K + 'montage-06-asval-swap-coastline-20261109-2113', 'swap')
gp(0.0, 1.0, 1.5)
pin(82.5)
# 82.5-87.01: the user's ride, a turn, the user with teammates; then the jeep, its rocket landing on the accent (90).
gp(3.0, 5.12)
beat('c7', K + 'montage-07-ak12squad-turn-coastline-20261109-2113', 'turn')
gp(51.9, 54.4, 1.0)
pin(87.49)  # (the blast cut 2.51 s after: on the accent at 90)
rocket('vj', K + 'vehicles-dustbowl-20261109-2159', 'jeep')
# 91.4-101.69: the tank on Canyon, more kills and beats; then the helicopter, its rocket landing on the re-entry (105.09).
take('vt', K + 'vehicles-canyon-20261109-2200', [('jeep incoming', 0, None, None), ('aim', 0, None, None), ('jeep hit', 0, None, None)])
take('fb', K + 'fight-dustbowl-20261109-1416', [('raise', 0, None, None), ('engage', 0, None, None)])
gp(61.0, 63.4)
beat('t6', K + 'montage-06-m4a1-swap-temple-20261109-1439', 'swap')
beat('t5', K + 'montage-05-glock_43x-aimshots-temple-20261109-1439', 'aim')
gp(66.6, 68.4, 1.5)
pin(102.26)  # (the blast cut 2.83 s after: on the re-entry at 105.09)
rocket('vh', K + 'vehicles-dustbowl-20261109-2159', 'heli')
# 106.5-120: the last burst: the Canyon helicopter, the Island jeep, kills, to the end card on the accent (120).
rocket('vk', K + 'vehicles-canyon-20261109-2200', 'heli', incoming=False)
# (Island's rocket cams left out: the jeep's went under the sea, the helicopter's through the grass.)
rocket('vi', K + 'vehicles-island-20261109-2203', 'jeep', flight=False)
take('fw', K + 'fight-coastline-20261109-2204', [('over the gun', 0, None, None), ('after', 0, None, None)])
take('fu', K + 'fight-mountains-20261109-2201', [('contact', 0, 30, None)])
rocket('vj2', K + 'vehicles-island-20261109-2203', 'heli', flight=False)
take('ft', K + 'fight-canyon-20261109-2128', [('contact', 0, 30, None)])
take('fs', K + 'fight-island-20261109-2202', [('contact', 0, 30, None)])
take('fv', K + 'fight-island-20261109-2202', [('raise', 0, None, None), ('engage', 0, None, None)])
gp(70.81, 72.0, 0.0)
card('RAVENFIELD VR', 2.6, 120.0, 'a virtual reality mod')
card('LEAVE YOU FAR BEHIND', 2.6, None, 'music by Lunatic Calm')

edit = {"fps": 60, "takes": takes, "shots": shots, "grade": True,
        "beat": {"start": 6.1, "grid0": 11.36, "period": 0.46875, "div": 2},
        "music": {"path": "showcase/music/Leave You Far Behind - Lunatic Calm.mp3", "start": 6.1, "gain": 0.7, "game": 0.8,
                  "fade_in": 0.3, "fade_out": 2.6}}
json.dump(edit, open(os.path.join(T, 'trailer-v8.json'), 'w'), indent=1)
print(f'trailer-v8.json: {len(shots)} cuts from {len(takes)} takes')
