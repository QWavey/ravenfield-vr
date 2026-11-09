#!/bin/bash
# The trailer's fourth cut (2026-11-09): the user's own play (showcase/gameplay/1109.mp4, not in git) cut in with the showcase scenes,
# all to "Leave You Far Behind" by Lunatic Calm (credited). The song's landmarks: the break 8.7-10.9 s, the full beat at 11.36 s, the
# crash at 30.09 s, the drop-out at 44.4 s, the re-entry at 45.09 s, the accent at 60 s. Cold open from the user's play (shots over a
# wall, a red dot) on the intro; the title in the break; the showcase's fights, cover, grab and slow motion from the entry with three of
# the user's moments cut in (a soldier thrown, a point-blank kill, a blast); FULL MOD SUPPORT on the crash; the workshop montage to the
# drop-out; REAL GAMEPLAY in it; the user's play from the re-entry to the accent; the end card; the credit. Inspect and the grenade
# are out, and the night fight's first-person opening (the user's notes).
cd "$(dirname "$0")/../.."
python -I tools/trailer/make_trailer.py tools/trailer/trailer-v4.json \
  tools/trailer/gp-cold-open.json "card:RAVENFIELD VR::2.46" \
  tools/trailer/fight-day.json tools/trailer/gp-throw.json tools/trailer/grab.json tools/trailer/cover-v3.json \
  tools/trailer/gp-point-blank.json tools/trailer/fight-night-v3.json tools/trailer/slowmo.json tools/trailer/gp-explosion.json \
  "card:FULL MOD SUPPORT:every workshop gun, in your hands:1.5" tools/trailer/montage-v7.json \
  "card:REAL GAMEPLAY::0.69" tools/trailer/gp-section.json \
  "card:RAVENFIELD VR:a virtual reality mod:2.6" "card:LEAVE YOU FAR BEHIND:music by Lunatic Calm:2.6"
python -I - << 'PY'
import json
p = 'tools/trailer/trailer-v4.json'
e = json.load(open(p))
e['music'] = {"path": "showcase/music/Leave You Far Behind - Lunatic Calm.mp3", "start": 6.1, "gain": 0.7, "game": 0.8, "fade_in": 0.3, "fade_out": 2.6}
json.dump(e, open(p, 'w'), indent=1)
PY
python -I tools/trailer.py --check tools/trailer/trailer-v4.json | tail -2
