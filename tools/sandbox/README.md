# Ravenfield VR Sandbox

A Windows program to try the mod's hands on the weapons without starting the game: put the gloves on a gun, pose
them, play the reload, point at parts and copy where they are, save poses, screenshots and GIFs.

## Start it

- `tools\sandbox\bin\RavenfieldVR-Sandbox\RavenfieldVR-Sandbox.exe` (build it with `python tools/sandbox/build_exe.py`), or
- `python tools/sandbox/app/main.py`

The first time, the weapons have to be exported from the game once: **Advanced → Export weapons** (or
`python tools/sandbox/export.py`). It starts Ravenfield in a window without the headset, lets the mod analyse every
weapon from the main menu, writes them, and closes the game (a few minutes for all of them). Export again after
changing the mod's analysis, or only some weapons: `python tools/sandbox/export.py --weapons "GLOCK,vector"`.

## Use it

- **Weapon**: pick one on the left (search box above the list). A saved pose comes back with it.
- **Hands on the gun**: the right hand on the grip exactly as the mod holds a gun (the gun's frame is the glove's palm
  frame at the grip point), the left on the handguard. **Left hand to** puts the left hand on the handguard, the
  slide or handle, the magazine or the cover.
- **Move a hand**: drag it. **Turn it**: Shift + drag. **Pose**: the list (the weapon's own grip and support poses
  first, then the mod's 31 poses) or the finger sliders.
- **Reload / Rack**: the parts move as the mod moves them (the slide by its travel, the magazine along its line, the
  cover on its hinge), the left hand on them. The sliders do it by hand.
- **Laser pointer** (L, or Alt + click): click a part to see its name, what the mod takes it for, and where it is: cm
  from the muzzle (back, up, right) as the test reports give it, and the mod's gun-space x, y, z. **Right-click** →
  *Copy coordinates* puts a line on the clipboard to paste to Claude, e.g.
  `vector rds (vector_rds_vector_rds): 'weapon_tdi_…_slide_rotation…' (moves with the slide) at back 10.5 up 2.9 right -3.2 cm from the muzzle (mod gun space x … y … z …)`.
- **Save pose** (Ctrl+S): `tools/sandbox/poses/<weapon>.json` with both hands' place and turn on the gun (in the
  mod's gun space and the sandbox's), their finger curls, the part each one holds and its job (grip, handguard...).
  The same file goes to the game (`BepInEx/config/RavenfieldVR/poses/`) and **the mod holds the weapon that way**:
  the gun hand's place, turn and finger curls, and the handguard hand's place (config `Weapons.UseSandboxPoses`, on).
  A pose saved without changes is exactly the mod's own, so only what you change changes in the game.
- **Settle**: after "Hands on the gun" the glove is put onto the gun's surface (within 2 cm), as it settles in VR;
  Advanced → *Settle hands on the gun* turns it off to see the raw place.
- **Screenshot** (F12) and **Record GIF**: into `reports/sandbox/captures/`; the message shows the path with a button
  to open the folder. The **Captures** page lists them.
- **Advanced**: exact numbers for the selected hand, the parts the mod found (click one to highlight it), the
  analysis (rig.json), views, GIF settings, and the game: export weapons, or *Test this gun in the game* (a
  vrshots run for that gun, only when really needed; never while Ravenfield is running).

Mouse: drag to look around, right-drag to pan, wheel to zoom. Keys: 1 / 2 select the right / left hand, Esc none,
F frame the gun, Space reload, L laser.

## What is where

| | |
|---|---|
| `app/main.py` | the window (PyQt6 + Fluent widgets), panels, reload timeline, capture, saving |
| `app/viewport.py` | the OpenGL 3.3 view: drawing, camera, picking, dragging |
| `app/scene.py` | the weapon (parts and roles from rig.json, their motion), hands on the gun, coordinates, the pose file |
| `app/glove.py` | the mod's glove (`src/Rig/Glove/glove.bin`) and its finger poses, built as `HandShape` / `HandModel` / `HandPoses` build them |
| `app/glb.py` | reads the export's .glb |
| `export.py` | runs the game once to export (`-novr -force-gfx-direct -rfvr-selftest -rfvr-export`) |
| `build_exe.py` | PyInstaller build into `bin/` |
| `src/Weapons/SandboxExport.cs` | the mod's side: model.glb + rig.json per weapon |
| `src/Weapons/GpuMeshReader.cs` | reads meshes the game keeps only on the graphics card (Direct3D 11) |

Skinned guns (one mesh on bones, the ARS family): the triangles on the slide's, handle's, magazine's or cover's bones
are exported as parts of their own, so they move too.

Grey boxes in a weapon are parts whose mesh could not be read at all (rare now: unreadable meshes are read back
from the graphics card); they are at the right place and size.

Coordinates: the export's gun space is the mod's made right-handed (z negated). The sandbox shows the barrel along +X,
the top up, the grip at the origin.
