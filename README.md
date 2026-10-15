# Effects source (for the Ravenfield VR mod)

Source code for the suggested on-screen effects, so they can be rebuilt in the mod (not only copied from pictures).
Python 3 + numpy + Pillow (OpenCV optional).

- `muzzle-flash-sim/flash_sim.py`: the muzzle flash on the lens. It holds every formula, size, colour and timing, plus
  the flash limiter and the tests. See its README.
  - `python flash_sim.py`: tests and animations.
  - `--live`: fire it yourself.
- `screen-effects/screen_effects.py`: draws all approved effects into one sheet: `python screen_effects.py out.png`.
  - A-C: muzzle flash;
  - F: hit from the side;
  - G: low health;
  - I: water drops;
  - the mod's current mud.

  The functions to port are `lens_flash`, `drop_field` + `shade_drops` and `mud_view`.
- `screen-effects/renders/`, `muzzle-flash-sim/all_guns.*`: the approved look.

To port to the mod: Tasks2.md task 209 (branch tasks-98-113), "How 209 A-C should be built at home".
