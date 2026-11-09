#!/bin/bash
# The trailer's third cut (2026-11-09): cut to the user's music, "Leave You Far Behind" by Lunatic Calm (showcase/music/, not in git),
# credited at the end. The song at 128 bpm: its intro under the cold open, the cold open's shot just before the song drops out
# (8.7 s), the title in that break, the fight starting on the full beat (11.36 s), FULL MOD SUPPORT on the crash at 30.09 s, the
# montage to the drop-out at 44.4 s, the end card in it and the credit on the re-entry (45.09 s). "start" in the music block is the
# song's second at the trailer's start.
cd "$(dirname "$0")/../.."
python -I tools/trailer/make_trailer.py tools/trailer/trailer-v3.json \
  tools/trailer/inspect-v3.json "card:RAVENFIELD VR::2.46" \
  tools/trailer/fight-day.json tools/trailer/grab.json tools/trailer/cover-v3.json \
  tools/trailer/fight-night.json tools/trailer/slowmo.json tools/trailer/grenade.json \
  "card:FULL MOD SUPPORT:every workshop gun, in your hands:1.5" tools/trailer/montage-v7.json \
  "card:RAVENFIELD VR:a virtual reality mod:2.6" "card:LEAVE YOU FAR BEHIND:music by Lunatic Calm:2.6"
python -I - << 'PY'
import json
p = 'tools/trailer/trailer-v3.json'
e = json.load(open(p))
e['music'] = {"path": "showcase/music/Leave You Far Behind - Lunatic Calm.mp3", "start": 6.15, "gain": 0.7, "game": 0.8, "fade_in": 0.3, "fade_out": 2.6}
json.dump(e, open(p, 'w'), indent=1)
PY
python -I tools/trailer.py --check tools/trailer/trailer-v3.json | tail -2
