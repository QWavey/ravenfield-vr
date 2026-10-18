# Ravenfield VR Sandbox

A Windows program to try the mod's hands on the weapons without starting the game: put the gloves on a gun, pose
them, play the reload, point at parts and copy where they are, save poses, screenshots and GIFs.

## Start it

- `tools\sandbox\bin\RavenfieldVR-Sandbox\RavenfieldVR-Sandbox.exe` (build it with `python tools/sandbox/build_exe.py`), or
- `python tools/sandbox/app/main.py`

The program finds the mod's folder by looking upward from where it is. Keep the exe under `tools/sandbox/bin`; if it is
moved out of the mod's folder, it asks once for the Ravenfield-VR folder (the one with `src` and `tools`).

The first time, the weapons have to be exported from the game once: **Advanced → Export weapons** (or
`python tools/sandbox/export.py`). It starts Ravenfield in a window without the headset, lets the mod analyse every
weapon from the main menu, writes them, and closes the game (a few minutes for all of them). Export again after
changing the mod's analysis, or only some weapons: `python tools/sandbox/export.py --weapons "GLOCK,vector"`.

The Easy tab starts with a short "Start here" line. **Help** (the last item of the left bar) explains everything the
program does; this file is the longer version.

## Use it

### Weapons and hands

- **Weapon**: pick one on the left (search box above the list). A saved pose comes back with it. A weapon whose hands
  you changed and did not save shows **• not saved** in the list.
- **Unsaved hands are kept**: opening another weapon keeps the hands you left (for this session), and they come back
  when you return to it. Closing the window asks first when some hands are unsaved. **Revert** puts the open weapon's
  hands back to its saved pose.
- **Hands on the gun**: the right hand on the grip exactly as the mod holds a gun (the gun's frame is the glove's palm
  frame at the grip point), the left on the handguard. **Left hand to** puts the left hand on the handguard, the
  slide or handle, the magazine or the cover.
- **Put a hand on any part**: the laser's right-click menu (see below) puts a hand on the part under the laser, not only
  on the four parts above.
- **Move a hand**: drag it. **Turn it**: Shift + drag. **Move or turn the weapon itself**: Ctrl + drag (Shift turns it,
  as for a hand), to look at the grip from another side.
- **Arrows and rings** (Blender's transform gizmos), chosen in the header over the view:
  - **Move** (G): arrows on the selected hand. Drag an arrow to move along its axis (red X, green Y, blue Z), a small
    square to move in its plane, the white circle in the middle to move in the view's plane.
  - **Turn** (R): rings. Drag a ring to turn about its axis (the front half is drawn, as in Blender), the white outer
    ring to turn about the view, inside the rings to turn freely (a trackball).
  - **Move + turn** (T) shows both; **Free** (W) none. Dragging the hand itself still moves it freely with any of them.
  - **Ctrl** snaps (1 cm, 5 degrees), **Shift** is fine (a tenth as fast; with Ctrl 1 mm and 1 degree); the **Snap**
    button snaps without Ctrl (Ctrl then moves freely). **Esc** or a right click while dragging takes the drag back.
  - **Axes**: Global (the world's), Local (the hand's own) or Gun (X along the barrel, Y up, Z its right side); the comma
    key cycles them. **On**: Hand or Gun; Ctrl + click on the gun puts the gizmo on it (at its grip, the gun faintly lit),
    a click on a hand puts it back. While dragging, the top left of the view says what moves how far.
- **Pose**: the list (the weapon's own grip and support poses first, then the mod's poses) or the finger sliders.
  **Copy this pose to the other hand** (Advanced) copies a pose across.
- **Reload / Rack**: the parts move as the mod moves them (the slide by its travel, the magazine along its line, the
  cover on its hinge), the left hand on them. The sliders do it by hand. **Record the reload** saves the reload as a GIF.
- **Reset**: the parts go back home, both hands go back on the gun and the parts you hid are shown again. Reset does
  not change the saved pose file, and **Ctrl+Z** undoes it.
- **Settle**: after "Hands on the gun" the glove is drawn on the gun's surface (in by up to 2 cm, out by up to 4), as
  it settles in VR. Only the drawing moves: the hand's place (what is saved, the gun's grip point in the game) stays,
  as in VR. Advanced → *Settle hands on the gun* turns it off to see the raw place.

### Undo and redo

**Ctrl+Z** goes back, **Ctrl+Y** (or **Ctrl+Shift+Z**) forward. The history covers hand moves, poses, finger sliders,
parts, the snaps (Handguard, Slide, Magazine, Cover), Reset, hiding a part and importing a pose, up to 100 steps. It belongs to the open weapon: opening
another weapon starts its own history, and the unsaved hands of the first one are kept as described above.

### The laser and copying

- **Laser pointer** (L, or Alt + click): click a part to see its name, what the mod takes it for, and where it is: cm
  from the muzzle (back, up, right) as the test reports give it, and the mod's gun-space x, y, z.
- **Right-click** opens a menu: *Copy coordinates*, *Copy the part name*, *Hide this part*, *Show every part*,
  *Hands on the gun*, *Frame the gun*, and the entry that puts a hand on the part. *Copy coordinates* puts a line on the
  clipboard to paste to Claude, e.g.
  `vector rds (vector_rds_vector_rds): 'weapon_tdi_…_slide_rotation…' (moves with the slide) at back 10.5 up 2.9 right -3.2 cm from the muzzle (mod gun space x … y … z …)`.
- **Copy the hands for Claude**: both hands as text on the clipboard: where each one is, its pose and the part it holds,
  to paste next to a picture or the coordinates.

### Saving poses

- **Save pose** (Ctrl+S): `tools/sandbox/poses/<weapon>.json` with both hands' place and turn on the gun (in the
  mod's gun space and the sandbox's), their finger curls, the part each one holds and its job (grip, handguard...).
  When the game with the mod is found, the same file is also written to the game's
  `BepInEx/config/RavenfieldVR/poses/`, and **the mod holds the weapon that way**: the gun hand's place, turn and finger
  curls, its thumb when you posed it here (a pose from the list, or the thumb sliders), and the handguard hand's place
  (config `Weapons.UseSandboxPoses`, on). A pose saved without changes is exactly the mod's own (with the weapon's config
  overrides), so only what you change changes in the game.
- **The game folder**: the game is looked for in Steam's usual place. If Ravenfield is somewhere else, choose its folder
  under **Advanced → Game folder…** (it must hold BepInEx). The line above *Send to game* says which copy the game uses.
- **Pose history**: a pose file about to be overwritten or removed (by Save pose, Send to game or Remove from game) is
  first copied to `tools/sandbox/poses/history/`, and the last 30 per weapon are kept. A pose file that cannot be read
  is kept there too before anything can overwrite it. So a pose saved over by mistake can be got back.
- **Import pose… / Export pose…**: a pose file from anywhere onto this weapon (then Save pose keeps it), or the current
  pose to any file (to share it or send it to Claude).
- **Send to game / Remove from game**: the saved pose on or off in the game.

### Pictures and GIFs

- **Screenshot** (F12) and **Record GIF**: into `reports/sandbox/captures/`; the message shows the path with a button
  to open the folder.
- **Clean pictures** leave out the grid, the markers, the laser line and the highlights. The **Clean picture** box beside
  Screenshot (Easy) and the switch under Advanced → View are the same setting.
- **Screenshot size** (Advanced → View): 1x (the view's size), 2x or 4x, drawn again from the same camera for a post. A
  size the graphics card cannot hold gives way to the next smaller one, with a message. **Transparent background** keeps
  a PNG's background see-through; GIFs stay solid.
- Each picture and GIF has a `.txt` beside it with the same name: the weapon, its hands in words, the view, and the laser
  point when one is picked. Paste it with the picture to Claude.
- **Turntable**: a GIF can turn the weapon once round for a seamless loop. Its length is under Advanced → GIF settings.
- **Captures** page: lists the pictures and GIFs, and what you can do with each one: open it, copy it (a PNG only),
  show it in Explorer, or move it to the Recycle Bin. The selected one is shown large, with its `.txt` under it, and a
  GIF plays there.

### Advanced

Exact numbers for the selected hand, the parts the mod found (click one to highlight it), the analysis (rig.json),
views, GIF settings, and the game: **Game folder…**, **Export weapons**, or **Test this gun in the game** (a vrshots run
for that gun, only when really needed; never while Ravenfield is running).

### When something goes wrong

A problem shows a message and the program goes on. Every error, shown or not, is also written to
`reports/sandbox/sandbox-errors.log` with its details.

### Self-test

`python tools/sandbox/app/main.py --selftest report.txt [--weapon NAME]` (or the exe with the same arguments) runs
every feature as a user would and writes PASS, FAIL or SKIP for each check, with what it saw (about 60 checks per gun).
`--all` also opens every exported weapon once, puts the hands on, plays its reload and saves it. It saves into its own
folder, `reports/sandbox/selftest`: the weapons' saved poses and the game's copies are not touched.

### Keys and mouse

Mouse: drag to look around, right-drag to pan, wheel to zoom; Ctrl + drag on the gun moves it, Ctrl + Shift + drag turns
it (the hands on it go along). Keys: 1 / 2 select the right / left hand, G / R / T / W the move / turn / both / no gizmo,
comma the gizmo's axes, Esc takes a gizmo drag back, F frame the gun and the hands (Shift+F the gun
alone), keypad 1 / 3 / 7 / 5 side, front, top and 3/4 views, O orthographic view, Space reload, L laser, Ctrl+S save,
F12 screenshot, Ctrl+Z undo, Ctrl+Y or Ctrl+Shift+Z redo.

## Building the exe

`python tools/sandbox/build_exe.py` builds into `tools/sandbox/bin/`. The exe carries a version resource (company
QWavey, product and file description "Ravenfield VR Sandbox", the version in `VERSION` in `build_exe.py`). The build
leaves out the Qt parts the sandbox never loads (PDF, web engine, QML and Quick, 3D, multimedia, Bluetooth, NFC,
positioning, sensors, serial port, WebSockets, Designer), the Qt translations (no translator is installed), Qt's PDF
image plug-in, and PyOpenGL's GLUT and GLE DLLs. The taskbar groups the program under its own name
(`QWavey.RavenfieldVR.Sandbox`), also when it runs from source.

## What is where

| | |
|---|---|
| `app/main.py` | the window (PyQt6 + Fluent widgets), panels, reload timeline, capture, saving, error log, Help |
| `app/viewport.py` | the OpenGL 3.3 view: drawing, camera, picking, dragging |
| `app/gizmo.py` | the transform gizmos (arrows, plane squares, rings): their shapes on screen, what the pointer is on, the drag math |
| `app/scene.py` | the weapon (parts and roles from rig.json, their motion), hands on the gun, coordinates, the pose file |
| `app/glove.py` | the mod's glove (`src/Rig/Glove/glove.bin`) and its finger poses, built as `HandShape` / `HandModel` / `HandPoses` build them |
| `app/glb.py` | reads the export's .glb |
| `app/selftest.py` | the self-test (`--selftest`) |
| `export.py` | runs the game once to export (`-novr -force-gfx-direct -rfvr-selftest -rfvr-export`) |
| `build_exe.py` | PyInstaller build into `bin/` (version resource, unused Qt parts left out) |
| `src/Weapons/SandboxExport.cs` | the mod's side: model.glb + rig.json per weapon |
| `src/Weapons/GpuMeshReader.cs` | reads meshes the game keeps only on the graphics card (Direct3D 11) |

Skinned guns (one mesh on bones, the ARS family): the triangles on the slide's, handle's, magazine's or cover's bones
are exported as parts of their own, so they move too.

Grey boxes in a weapon are parts whose mesh could not be read at all (rare now: unreadable meshes are read back
from the graphics card); they are at the right place and size.

Coordinates: the export's gun space is the mod's made right-handed (z negated). The sandbox shows the barrel along +X,
the top up, the grip at the origin.
