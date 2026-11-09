#!/bin/bash
# The trailer's second cut (2026-11-09, after the user's notes: the arms, the aim, no shots into the air): cold open (the gun turned in the hands, a shot), the title, the fight by day, cover, the grab, the fight at
# night through the goggles, slow motion, a grenade; then the workshop part: the 3-map montage; the end card. Re-run after new takes:
# each scene edit takes the newest take of its scene.
cd "$(dirname "$0")/../.."
# (montage-v6.json: made by make_montage.py, its d1 pinned to the Dustbowl swap of 14:37: the newest did not seat)
python -I tools/trailer/make_trailer.py tools/trailer/trailer-v2.json \
  tools/trailer/inspect.json "card:RAVENFIELD VR::1.6" \
  tools/trailer/fight-day.json tools/trailer/grab.json tools/trailer/cover.json \
  tools/trailer/fight-night.json tools/trailer/slowmo.json tools/trailer/grenade.json \
  "card:FULL MOD SUPPORT:every workshop gun, in your hands:1.5" tools/trailer/montage-v6.json \
  "card:RAVENFIELD VR:a virtual reality mod:2.2"
python -I tools/trailer.py --check tools/trailer/trailer-v2.json | tail -3
