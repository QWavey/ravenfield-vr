# Showcase plan: screenshots and a trailer (planned 2026-11-08; started that night, Tasks 372)

**Status:** in progress (Tasks 372): the pilot filmed, the reload and inspect scenes written; game runs only when the user says the game is free.

## What the user asked for

- Many in-game screenshots and a short trailer of the mod, **in-game sounds only, no music**, made with the test tool.
- Trailer: **60-90 s**, cut like a modern military-shooter reveal trailer (the user named CoD MW and Battlefield V trailers as the
  feel): many angles, sometimes first person, sometimes cinematic; e.g. **a camera on the gun looking back at the face while
  walking, the camera shaking on every shot**.
- **Vanilla first, then a "full mod support" part** (workshop guns, the gore mod and the like), clearly separated.
- Design and type with the weight of those trailers, **no "vibe slop"** (no gradients, glows, neon, emoji, stock phrases).
- Inspired by their pacing and typography only: no logos, names, fonts, music or footage of those games.

## What exists today (read 2026-11-08)

| Piece | Where | State |
|---|---|---|
| Launcher for test runs | `tools/vrshots.py` | Null-driver SteamVR, `--map`, `--mods`/`--fast`, `--res`, collects to `reports/shots/<mode>/`, restores SteamVR settings |
| Stills | `src/Core/Shots.cs` (`Shots.Capture(name, pos, rot, fov)`) | Mono copy of the eye camera, **fixed 1280x800**, synchronous read-back (a frame stall), HUD and mod overlays drawn |
| Eye film | `src/Core/EyeFilm.cs` + `tools/flashfilm.py` | Each eye's picture every rendered frame, **480 px wide**, async read-back, PNGs on a worker thread, MP4 via OpenCV, **no fixed frame rate, no audio** |
| Scripted hands | `VRHand.Scripted[]`, `ScriptedVelocity[]` (`src/Rig/VRHand.cs`) | Linear moves in the autotest; **no scripted head** (the null headset's head does not move) |
| Staged moments | `src/Core/AutoTest.cs`, `src/Weapons/ReloadInteractions.SelfChecks.cs` | Magazine reload sequence, rack, MG belt, soldier grab/throw/shield/punch/neck, strapped grenade, mud and water on the view, body and holsters, wrist display, squad pointing, menus. Missing: C4, driving, night vision, dual wield, grenade throw, slow-motion pictures |
| Side-camera helper | `Snap()` in `BodyGrabCheck` | Fixed offset, FOV 70 |
| Gallery and montage | `tools/showcase.py`, `tools/montage.py`, `tools/seqgif.py` | Index page, picture montage (OpenCV MP4), GIFs |
| Audio | none | Nothing records sound; the game's music has its own mixer (`Options.musicAudioMixer`) |
| On the PC | | OpenCV, Pillow, numpy; **no ffmpeg**; OBS Studio installed; Windows' **Bahnschrift** (DIN-style, condensed widths) |

## How it works now (2026-11-08 night; supersedes the approach below where they differ)

- **One clip per game run**, each checked and improved before the next, on different maps where possible (the user: "record
  things singlely so clips single so every clip is smooth ... also diff maps if possible. check after every clip and improve
  things dont jump to a new one"). `python -I tools/vrshots.py --showcase SCENE --fast --no-build --timeout 15 [--map MAP]`.
- **Two passes per scene** from the same spot: the picture pass frame-locked at 60 fps (`Time.captureFramerate`, the clock kept
  half a physics step off a step's boundary so every frame takes exactly one), then the sound pass in real time recording the live
  listener (`ShowcaseSoundTap`). `AudioRenderer` stops this game's mixer and is not used. Both passes follow one schedule
  (`ShowcaseRecorder.Planned`); the sound is lined up by the shots' onsets (within a frame, `showcase_take.py`).
- **The body** is posed for the cinematic cameras before rendering (`ShowcaseBody`: turned and put under the headset, arms to the
  gloves, its hands inside them); the player is not knocked over or hurt during the showcase (`Showcase.Steady`).
- **The gun** is placed from its grip measured once per gun (`PlaceGun`): measured every frame it carried the gun's physics lag and
  shook while walking. Poses: `HipToEye(aim)` (hip to sights), `InFront(local, yaw, pitch, tiltAway)` (in front of the chest,
  tipped so the well and the hands at it face the eyes); blended with `GunPose.Lerp`.
- **Cameras**: gun cam (on the muzzle looking back at the face), over the shoulder, helmet cam (the user's pick), first person through
  the sights (`FirstPersonAim`), first person looking at the hands (`FirstPersonLook`), close-up on a point of the gun. No orbit (the
  user: "the orbit view is not good lets remove that"). Third-person cameras stop short of walls and ground.
- **Stage**: dry, flat ground 30-160 m from the spawn, 45 m (else 30 m) clear of vehicles and vehicle spawners, 25 m from soldiers'
  spawns, 2.5 m of room; facing the best view (`View`: a 48-degree cone clear for 25 m, ground under the walk, the battle ahead).
- **Tools**: `showcase_take.py DIR [--mux]` (frames, sound, sync, ping-pong jitter per camera), `showcase_sheet.py DIR` (contact
  sheet), `trailer.py --marks DIR` (the take's marks as frame ranges), `trailer.py EDIT.json OUT.mp4` (the edit: shots by frame or
  by mark, hard cuts, the sound cut with its picture and faded 4 ms at each cut, cards with a ring-out tail, the grade),
  `showcase_clip.py DIR OUT.mp4` (one whole take plus a card).
- **Pacing** (the user: "the transitioning between views is too slow"): in the scenes, moves are short and quick-then-settling
  (`EaseOut`: the raise 0.55 s, a present 0.5 s); in the edit, a view is held 0.4-1.7 s, cuts land on a shot's frame.

## Scenes (single clips; written = in code and compiled, filmed = a take checked)

| Scene | What the camera sees | Hands in the picture | State |
|---|---|---|---|
| `pilot` (6.5 s) | walk at the hip (gun cam on the face), quick raise (over the shoulder), 3 shots through the sights, 2 and a burst on the helmet cam | the gloves on the gun (first person), the arms on it (third person) | filmed (takes 1-11, clip 02, tight cut 03); now 2.2 s walk, 0.55 s raise |
| `reload` (5 s) | the rifle brought up in front of the chest, the eyes down on the well; the support hand leaves the handguard, pulls the magazine, holds it (close-up from the gun's side), pushes it home; back on the handguard, up to the sights, 2 shots | the whole time | written |
| `inspect` (3 s) | the rifle held up and turned over from its right side to its left (first person, looking at it), then up to the sights on the helmet cam, a shot | the whole time | written |
| `pistol` | drawn from the hip holster, slide racked by the free hand, two-handed aim, shots | yes | to write (the autotest's holster and rack steps) |
| `grenade` | taken from the chest, the pin pulled by the other hand, thrown; the blast far off | yes | to write (the autotest's grenade checks) |
| `slowmo` | time slowed, a magazine flies in at real speed | yes | to write (`GameManager.SetTimeScale`) |
| `scope` | a scoped rifle, its own lens in first person | partly | to write |
| `brawl` | a soldier grabbed and thrown, a punch | yes | later (needs soldiers brought: the autotest's `Bring`) |
| mods | MG belt by hand, the M1's clip, the gore mod | yes | after the user picks the mods |

Maps to rotate through (lowercase `-map` names): island2023 (pilot), then dustbowl, temple, coastline, mountains, canyon, eagle base.

## Approach

Everything is rendered **inside the game, frame-locked**: `Time.captureFramerate = 60`, so each video frame is one game step no
matter how slow the PC renders it. Sound is taken from the game's own mixer in the same lock-step with Unity's `AudioRenderer`
(the API Unity's own recorder uses): sample-exact sync, no desktop loopback, nothing from other programs, and the game's music
mixer muted for the run. The edit (cuts, speed ramps, titles, letterbox, mixing the clips' sound) is done afterwards by a
Python tool with ffmpeg.

### 1. In-game: a showcase director (test-only, `-rfvr-showcase`)

New `src/Core/Showcase/` (only active with the command line flag, like the autotest):

- **Scenes** as coroutines, one per shot group, reusing the autotest's staging: `Bring` (soldiers respawned where wanted),
  the hands' `Scripted` poses, `GameManager.SetTimeScale` for slow motion, the weapon draw/give helpers. Moves become eased
  curves (start slow, end slow) instead of the checks' linear steps; small hand sway so it never looks robotic.
- **Scripted head**: a test-only head pose override in `VRRig` (the null headset's head stands still), so first-person
  shots can walk, turn and look.
- **Camera rigs** (one `Camera`, mono, 16:9), each a small class:
  - *gun cam*: on the gun body, looking back at the face (the user's example), damped follow;
  - *over the shoulder*, *low angle*, *orbit*, *dolly* along a path, *close-up* on the magazine well / slide / belt;
  - *first person*: the eye camera copied mono at 16:9 (what the headset sees);
  - *shake*: a short noise impulse on each shot (the weapon system's fire event), a stronger one on blasts, recoil kick on the
    gun cam; FOV punch-ins.
- **The player's own body seen by the cinematic cameras**: the eye hides the head/body renderers (`VRRig.HideBody`,
  `forceRenderingOff`); they are shown for the cinematic camera's render only (pre/post render).
- **Clean frame**: the game's HUD and the mod's test overlays off; mutator HUDs not loaded (see content).
- **Recorder**: each frame rendered at 1920x1080 (stills at 3840x2160), async GPU read-back into a pool, JPEG (quality 95) on a
  worker thread (the `EyeFilm` pattern); `AudioRenderer.Render` each frame into a WAV writer; a `shots.txt` with scene, shot,
  camera and frame range. Music mixer at -80 dB for the run, put back after.
- **Stills ("photo mode")**: at chosen beats each scene also saves a 3840x2160 PNG from the cinematic and the first-person camera.

### 2. `tools/vrshots.py --showcase`

`--showcase [--scenes a,b] [--content vanilla|mods]`: the game started with `-rfvr-showcase`, long timeout, the mod list from
`--mods` (vanilla run: `--mods none`; mod run: the chosen workshop guns and the gore mod, **no HUD mutators**), output in
`reports/showcase/<date>/` (`clips/<scene>/` frames + `audio.wav` + `shots.txt`, `stills/`). SteamVR restore as today.

### 3. The edit: `tools/trailer.py`

- An edit list (`tools/trailer/edit.json`): clip, in/out frame, speed (ramps for slow-motion beats), title cards.
- Assembly with ffmpeg: 1920x1080 at 60 fps, H.264 + AAC; hard cuts on gunshots; 1-2 white flash frames on big cuts; optional
  2.39:1 letterbox; sound from each clip's WAV, short fades at cuts, peaks limited (no music).
- **Type**: Bahnschrift (Windows' own DIN-style face) Condensed Bold for titles, SemiCondensed for small labels; white on black,
  wide tracking on labels, one restrained accent colour (a desaturated amber) used once or twice. No gradients, glows, emoji,
  stock phrases. Copy is short and concrete.
- Output: `trailer.mp4`, a 15 s cut for social, a contact sheet of the stills, and a gallery page (extend `tools/showcase.py`).

## Trailer outline (about 75 s)

| Time | Beat | Shots | Sound |
|---|---|---|---|
| 0-5 | Cold open | Black. A magazine slides in, the bolt is racked: first person close-up, then the gun cam on the face | Mag in, rack (game sounds) |
| 5-8 | Title | `RAVENFIELD VR` on black, one flash frame | A single gunshot |
| 8-20 | Hands on every gun | Pistol drawn from the hip holster (over the shoulder), slide racked (close-up), fired (first person, shake); rifle magazine out, pouch, in, bolt release (close-ups) | Each action's own sound |
| 20-32 | The fight | Walking with the rifle (gun cam on the face), firing (shake), scope lens view onto a soldier, grenade pin pulled and thrown, blast with mud on the lens | Shots, pin, blast |
| 32-45 | Your hands are weapons | Soldier grabbed and thrown (orbit), human shield while his friend fires (low angle), punch in slow motion, C4 thrown and set off | Impacts, blast |
| 45-52 | Slow motion | Time slows, the hands stay fast: a magazine flies in real time, a reload done in slowed time | Slowed world, real-time hands |
| 52-55 | Card | `FULL MOD SUPPORT` | Silence, then a belt rattle |
| 55-68 | Mods | Workshop guns: MG belt laid by hand, the M1's clip and its ping, a scoped mod rifle through its own lens, the gore mod on hits | Their own sounds |
| 68-75 | End | The title; how to get it (text decided with the user); credits for the workshop mods shown | One last rack |

Stills: each beat gives one cinematic and one first-person 4K frame (about 40), the best 20 picked for the gallery.

## Steps (when the user says go)

1. **Permissions first**: game runs allowed again; installing ffmpeg (`winget install Gyan.FFmpeg`, a download: asked first).
2. **Pilot (one scene, ~10 s)**: recorder + one camera rig + AudioRenderer; check frame pacing at `captureFramerate` with the null
   VR headset, A/V sync (a gunshot's muzzle flash frame against its sound peak), the body seen by the cinematic camera.
3. Camera rigs and shake; the scripted head.
4. Scenes, vanilla: hands/reload, fight, grab and throw, slow motion, C4 (new), driving (new), night vision (new), dual wield.
5. Scenes, mods: choose the workshop guns and the gore mod with the user; credits list.
6. `tools/trailer.py` and the edit list; title cards; first cut; review with the user; second cut.
7. Stills: pick, contact sheet, gallery page.

## Verification

- The pilot's clip: frame count = seconds x 60; the gunshot's flash frame and its sound peak within one frame (16 ms).
- No music in the WAV (the music mixer muted; listen to a calm stretch).
- Each scene's `shots.txt` against the edit list (no missing frames, no "writer behind" lines).
- The user watches the first cut before anything is published.

## Open with the user

- Which workshop guns and which gore mod for the mod part, and their authors' names for the credits.
- The end card text (where to get the mod) and whether the trailer goes public.
- Letterbox (2.39:1) or full 16:9.
