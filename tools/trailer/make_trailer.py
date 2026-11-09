"""make_trailer: one trailer edit list from several scene edits (tools/trailer/*.json) and cards, in order.

  python tools/trailer/make_trailer.py OUT.json PART [PART ...]

A PART is an edit file (its shots taken in order, its own closing card dropped: the trailer has its own cards) or a card written
card:TITLE[:SUB[:SECONDS]] (e.g. "card:FULL MOD SUPPORT:workshop weapons:1.4"). Each edit's take names get the edit's name in front,
so two edits may both call a take "f". Missing takes are left to trailer.py --check to report.

  python tools/trailer/make_trailer.py tools/trailer/trailer-v1.json tools/trailer/fight-day.json "card:RAVENFIELD VR" ...
"""
import json
import os
import sys


def part_shots(path, takes):
    """The edit's shots with its take names made unique (prefixed by the edit's file name); its trailing card left out."""
    edit = json.load(open(path, encoding='utf-8'))
    tag = os.path.splitext(os.path.basename(path))[0]
    names = {k: f'{tag}.{k}' for k in edit['takes']}
    for k, v in edit['takes'].items():
        takes[names[k]] = v
    shots = []
    for s in edit['shots']:
        if 'card' in s:
            continue
        if 'video' in s:
            shots.append(dict(s))  # (a piece of a recorded video: no take)
            continue
        s = dict(s)
        s['take'] = names[s['take']]
        shots.append(s)
    return shots


def card(spec):
    """card:TITLE[:SUB[:SECONDS]] as a card shot (a short one between parts, the last shot's sound ringing into it)."""
    bits = spec.split(':')[1:]
    c = {"card": bits[0], "seconds": float(bits[2]) if len(bits) > 2 and bits[2] else 1.2, "tail": 0.8}
    if len(bits) > 1 and bits[1]:
        c['sub'] = bits[1]
    return c


def main():
    if len(sys.argv) < 3:
        raise SystemExit(__doc__)
    out, parts = sys.argv[1], sys.argv[2:]
    takes, shots = {}, []
    for p in parts:
        shots += [card(p)] if p.startswith('card:') else part_shots(p, takes)
    json.dump({"fps": 60, "takes": takes, "shots": shots, "grade": True}, open(out, 'w', encoding='utf-8'), indent=1)
    print(f'{out}: {len(shots)} cuts from {len(takes)} takes')


if __name__ == '__main__':
    main()
