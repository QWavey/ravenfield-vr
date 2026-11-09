#!/bin/bash
# The trailer's sixth cut (2026-11-09 evening): v5 with the montage's beats from five maps (two new runs, Coastline and Island). v5 was: no over-the-shoulder opening on the day fight, its reverse shot ended
# before the shot soldier's rifle flies up, no cover (its sidestep stuttered), slow motion without its last shot, the montage without
# the gun cam's walking fire and the Vector's swap; the user's grenade sequence whole and once (picked up, pressed onto a soldier, the
# countdown, the throw, the blast), placed so he flies through the song's drop-out (44.4 s) and the blast lands on the re-entry (45.09 s).
# The rest as v4: on "Leave You Far Behind" by Lunatic Calm (credited), the title in the break, the fights from the full beat (11.36 s),
# FULL MOD SUPPORT on the crash (30.09 s), the end card on the accent (60 s).
cd "$(dirname "$0")/../.."
python -I tools/trailer/make_trailer.py tools/trailer/trailer-v6.json \
  tools/trailer/gp-cold-open.json "card:RAVENFIELD VR::2.46" \
  tools/trailer/fight-day-v5.json tools/trailer/gp-point-blank-v5.json tools/trailer/grab.json tools/trailer/gp-vehicle.json \
  tools/trailer/gp-red-dot.json tools/trailer/fight-night-v3.json tools/trailer/gp-pistol-tree.json tools/trailer/slowmo-v5.json \
  tools/trailer/gp-explosion-v5.json \
  "card:FULL MOD SUPPORT:every workshop gun, in your hands:1.5" tools/trailer/montage-v9.json \
  "card:REAL GAMEPLAY::0.94" tools/trailer/gp-grenade-sequence.json tools/trailer/gp-section-v5.json \
  "card:RAVENFIELD VR:a virtual reality mod:2.6" "card:LEAVE YOU FAR BEHIND:music by Lunatic Calm:2.6"
python -I - << 'PY'
import json
p = 'tools/trailer/trailer-v6.json'
e = json.load(open(p))
e['music'] = {"path": "showcase/music/Leave You Far Behind - Lunatic Calm.mp3", "start": 6.1, "gain": 0.7, "game": 0.8, "fade_in": 0.3, "fade_out": 2.6}
json.dump(e, open(p, 'w'), indent=1)
PY
python -I tools/trailer.py --check tools/trailer/trailer-v6.json | tail -2
