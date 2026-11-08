"""make_montage: writes a montage edit list (tools/trailer/montage-*.json) from kept montage takes on several maps.

  python tools/trailer/make_montage.py OUT.json MAP:TAG [MAP:TAG ...]      e.g. montage-v3.json dustbowl:d temple:t canyon:k

Each map's run has the beats in Showcase.Montage's order (1 swap, 2 rack, 3 shots on the helmet cam, 4 walking fire on the gun cam,
5 aimed shots, 6 swap, 7 turn, 8 walking fire on the gun cam); the newest take of each beat on each map is used. The order below
moves between maps and kinds of beat so no two cuts in a row look alike; each beat is cut to 0.3-1 s.
"""
import json
import sys

ORDER = [('swap-full', 'd', 1), ('walkfire', 't', 4), ('rack', 'd', 2), ('shots', 't', 3), ('swap', 'k', 1), ('walkfire', 'd', 4),
         ('aim', 't', 5), ('rack', 'k', 2), ('swap', 't', 6), ('walkfire', 'k', 4), ('shots', 'd', 3), ('turn', 't', 7),
         ('walkfire', 'd', 8), ('swap', 'k', 6), ('aim', 'd', 5), ('turn', 'k', 7), ('rack', 't', 2), ('shots', 'k', 3),
         ('swap', 'd', 6), ('walkfire', 't', 8), ('turn', 'd', 7), ('walkfire', 'k', 8), ('aim-last', 'k', 5)]


def cut(kind, t):
    if kind == 'swap-full':
        return [{"take": t, "mark": "dropping", "in": 4}, {"take": t, "mark": "from the pouch"}, {"take": t, "mark": "inserting"}]
    if kind == 'swap':
        return [{"take": t, "mark": "from the pouch", "in": 0}, {"take": t, "mark": "inserting"}]
    if kind == 'rack':
        return [{"take": t, "mark": "rack taking"}, {"take": t, "mark": "rack pulling back"}, {"take": t, "mark": "rack let go", "in": 0, "out": 10}]
    if kind == 'walkfire':
        return [{"take": t, "in": 4, "out": 70}]
    if kind == 'shots':
        return [{"take": t, "shot": 0, "pre": 0, "post": 14}, {"take": t, "shot": 1, "pre": 0, "post": 14}]
    if kind == 'aim':
        return [{"take": t, "shot": 0, "pre": 0, "post": 13}, {"take": t, "shot": 1, "pre": 0, "post": 14}]
    if kind == 'aim-last':
        return [{"take": t, "shot": k, "pre": 0, "post": 13 if k < 2 else 30} for k in range(3)]
    if kind == 'turn':
        return [{"take": t, "mark": "turn", "in": 20, "out": 72}]
    raise SystemExit(kind)


def main():
    out, maps = sys.argv[1], dict(a.split(':')[::-1] for a in sys.argv[2:])  # tag -> map
    takes, shots = {}, []
    for kind, tag, beat in ORDER:
        if tag not in maps:
            continue
        name = f'{tag}{beat}'
        takes[name] = f'showcase/takes/montage-0{beat}-*-{maps[tag]}-*'
        shots += cut(kind, name)
    shots.append({"card": "RAVENFIELD VR", "seconds": 1.6, "tail": 1.2})
    json.dump({"fps": 60, "takes": takes, "shots": shots, "grade": True}, open(out, 'w', encoding='utf-8'), indent=1)
    print(f'{out}: {len(shots)} cuts from {len(takes)} takes')


if __name__ == '__main__':
    main()
