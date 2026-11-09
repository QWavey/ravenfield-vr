#!/bin/bash
# The trailer's first cut: cold open (the gun turned in the hands, a shot), the title, the fight by day, cover, the grab, the fight at
# night through the goggles, slow motion, a grenade; then the workshop part: the 3-map montage; the end card. Re-run after new takes:
# each scene edit takes the newest take of its scene.
cd "$(dirname "$0")/../.."
python -I tools/trailer/make_trailer.py tools/trailer/trailer-v1.json \
  tools/trailer/inspect.json "card:RAVENFIELD VR::1.6" \
  tools/trailer/fight-day.json tools/trailer/cover.json tools/trailer/grab.json \
  tools/trailer/fight-night.json tools/trailer/slowmo.json tools/trailer/grenade.json \
  "card:FULL MOD SUPPORT:every workshop gun, in your hands:1.5" tools/trailer/montage-v5.json \
  "card:RAVENFIELD VR:a virtual reality mod:2.2"
python -I tools/trailer.py --check tools/trailer/trailer-v1.json | tail -3
