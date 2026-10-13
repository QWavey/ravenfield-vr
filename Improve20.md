# /improve 20 — round log

Baseline: the first VR quick autotest on commit 95349c0 + the measurement block ("Measurements" at the top of
BepInEx/RavenfieldVR-autotest.txt). The game cannot be launched while the user plays, so rounds are built now and
measured in the next test session; each round carries its own in-run comparison where possible.

| # | Kind | Change | Measured by | Result |
|---|------|--------|-------------|--------|
| 1 | fidelity | Readable weapons: the physics shape fitted to the rendered model is compared once per weapon type with the one from the meshes (covered % minus outside %), the better one is kept | per-weapon log line "physics shape from the ... (meshes: ...; rendered model: ...)"; metrics "physics shape (...)" | pending test |
| 2 | feel | Rifle thumb wraps over the grip until it touches (goal 0.65 -> 0.95, contact stops it); pistol thumb unchanged | metric "gun hand: rifle thumb on the grip" (before: never touched, joints stayed at 0.65) and buried points unchanged | pending test |
| 3 | test coverage | The two-handed check and support-hand close-ups now include pistols (a hand cupping a pistol was never pictured or measured) | metrics "support hand on a pistol: ..." appear; pictures support-*-in/out for pistols | pending test |
