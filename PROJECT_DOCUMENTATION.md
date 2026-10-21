# Ravenfield VR — Technical Documentation

A SteamVR mod for Ravenfield (Steam build, Unity 2020.3.49, Mono) delivered as a BepInEx 5 plugin with its own OpenVR
XR loader. It turns the flat shooter into a hands-on VR game: tracked hands that close on what they hold, manual
reloading of every vanilla and workshop weapon, physical magazines, holsters, a VR HUD and menus, vehicles and an
automated test harness that plays the game by itself. Written 2026-10-13 (commit `6e063e8`), after the session that
added model-accurate hand posing, model-shaped physics, H3VR-style reloads and the measurement tooling described below.
**Last refreshed: 2026-10-16** (commit `8e6eec8`): rockets loaded by hand (Task 250), pistol magazine hold, push and
pose fades (240-242), the magazine seating check, `Handgun` against `IsPistol`, the bug hunts 248/249/251 and the
split of the work between a lead and a helper branch, the 24-entry gun-shape cache, the dropped-weapon changes and the
picture/film tools. Sections not about these are as written on 2026-10-13.

Repository: private `QWavey/ravenfield-vr` (this folder). The user-facing manual is `README.md`; the task checklist with
a "For N." paragraph per item is `Tasks.md`; the improve-run log is `Improve20.md`. This document is the engineering
view: how it works, why, what was tried and what was learned.

---

## 1. Overview & Purpose

- **What it is.** A drop-in mod (`BepInEx/plugins/RavenfieldVR/RavenfieldVR.dll` plus the native OpenVR XR runtime under
  `ravenfield_Data/`) that renders Ravenfield in stereo through SteamVR and replaces mouse-and-keyboard interaction with
  tracked controllers. Built and tuned for a Meta Quest 3 on SteamVR (Link / Air Link / Steam Link / Virtual Desktop);
  Index, Vive and Reverb G2 have default bindings.
- **Problem it solves.** Ravenfield has no VR support, and its thousands of workshop weapons are rigged in arbitrary ways
  (any bone names, Chinese part names, skinned or rigid parts, unreadable meshes). The mod has to *discover* every
  weapon's structure at runtime and make it physically usable without per-weapon authoring.
- **Who it is for.** The repository owner, who plays on a Quest 3 and reports bugs from headset sessions (screenshots,
  short descriptions), and anyone installing the packaged release (Setup.exe / launcher).
- **Status (2026-10-16).** `Tasks.md` runs to Task 251; its top lists what is still to do, in progress and code-done
  awaiting a headset check. Open work on 2026-10-16 included rockets by hand on all 14 launchers (250, code done, film
  queued), pistol magazine seating (126/240-243), racking parts that do not move on some guns (139/227/245/246), the
  per-gun review fixes (234), one eye drawn brighter on Island (225, not reproduced), weapons in either hand step two
  (152) and the trigger finger landing on the blade (202). (On 2026-10-13: tasks 1–97 done except 33, 37 and 94; the VR
  quick autotest reported 2 problems, both a workshop weapon's own laser script; the flat self-test analysed 308
  weapons with 0 failures.)

---

## 2. How It Works — Architecture

```
ravenfield.exe
 └─ BepInEx 5 chainloader
     └─ VRPlugin (Core/VRPlugin.cs)            Awake: config, install check, Harmony patches (~47 applied), VR start
         ├─ OpenVRRuntime (Runtime/)           own XR loader: XRSDKOpenVR.dll subsystems + OpenVR event pump
         └─ VRManager (Core/VRManager.cs)      fixed per-frame order, every section wrapped in Log.Guard (PerfProbe)
             Update:        Input → Rig.Update → UI.Update → WeaponSystem.Update
             LateUpdate:    CameraSuppressor → WeaponSystem.LateUpdate
             onBeforeRender:Rig.BeforeRender → WeaponSystem.BeforeRender → UI.BeforeRender
                            → HandModel.Apply (dominant hand first, then the other)
```

Data flow per frame:

1. **Input** (`Input/VRInput.cs`) polls SteamVR Input actions (digital, analog, poses, skeleton) and keeps per-hand edge
   state (`Pressed`, `Held`, `Released`, `Consumed`, `Unclaimed` + `HeldSince` for "reaching" grabs).
2. **Game input** (`Input/GameInputBridge.cs`) writes the game's SteelInput binds by context (on foot, seated, dead,
   menu) through Harmony patches, merged with keyboard/mouse.
3. **Rig** (`Rig/VRRig.cs`) places the tracking origin from whatever the game is looking through (`OnFoot`, `Anchored`
   seat, `Follow` menu/intro camera), calibrates height, applies turning and writes the head pose back into the game
   camera so the game's own logic (use ray, audio, squad orders) follows the head.
4. **Weapons** (`Weapons/WeaponSystem.cs`) attaches the player's active weapon as a `HeldWeapon`, runs reload/throw/
   melee interactions, holsters, drops, loose props.
5. **Before render**, the rig takes the final head/hand poses; `HeldWeapon.BeforeRender` re-anchors the game's animated
   weapon so its gun body sits in the tracked hand, applies recoil and part overrides (slide offset, hinge, hidden
   magazine) and sets the gloves' snap poses and solids; finally each `HandModel.Apply` poses the procedural glove.
6. **UI** (`UI/*`) converts every screen-space canvas to world space: a head-locked HUD frame matched to the game
   camera's frustum, menus placed in front of the player, laser-pointer input, SteamVR keyboard.

The weapon side rests on three runtime analyses:

```
WeaponAnalyzer + PoseSampler  (what is the slide, magazine, grip, sights... — sampled animations on an inert clone)
ShapeFit                       (physics shape from vertices: convex pieces per slice)
GunVolume                      (the drawn model as depth from 5 directions: a solid for hands and a hull for physics)
```

---

## 3. Component Breakdown

### 3.1 Runtime boot (`Core/VRPlugin.cs`, `Runtime/OpenVRRuntime.cs`, `Core/InstallCheck.cs`, `Core/VRManager.cs`)

- Unity's XR Management package is not in the game, so the mod loads Valve's `XRSDKOpenVR.dll` itself: it pushes the
  provider's user settings through the native API and creates the "OpenVR Display" / "OpenVR Input" subsystems that
  Unity discovered from `ravenfield_Data/UnitySubsystems/XRSDKOpenVR/UnitySubsystemsManifest.json` (checked by
  `InstallCheck`). It also pumps OpenVR events (quit, dashboard, keyboard, device changes).
- VR starts when SteamVR is running or `-vr` is given; `-novr` forces flat. The launcher (`installer/Launcher.cs`)
  sets the choice from Steam's Play button. When VR does not start (SteamVR not running, the runtime or the VR systems
  failing), the flat game shows a notice saying what to do, and **F6** tries again (F8 is the game's photo mode).
- `VRManager` owns the fixed update order shown above. Every section is called through `Log.Guard(name, action)`,
  which catches exceptions (logged once) and, when `PerfProbe.Enabled`, times the section and counts allocations.

### 3.2 Input (`Input/*`)

- `VRInput`: SteamVR action manifest embedded in the DLL and extracted at runtime (`Input/SteamVR/*.json`; left-handed
  variants generated by `BindingsWriter`). Haptics are lengthened to a felt minimum on Quest Touch Plus controllers.
  Test hooks: `SimulatedGrab[]`, `SimulateGrabPress(hand)` (now also queues a pressed edge), `SimulatePress(action,
  hand)`, `SimulatedTrigger[]` (finger pose only, never fires).
- `GameInputBridge`: context-aware mapping into SteelInput (fire, use, jump, crouch, scoreboard only with the map
  gesture, etc.). `ScriptKeys` lists keys that weapon/mutator scripts listen to (mod-keys panel). `TurretAim` steers
  turrets toward where you look; `VehicleInput` maps sticks to cars/helicopters/planes. A seat is left by holding the
  trigger of the hand on the seat's side for a second (`HeldToLeaveSeat`); since 2026-10-20 that trigger counts only after
  it was let go once in the seat (the trigger that entered the car, still held, threw the player out again), a support hand
  on a gun whose trigger only steps a light or laser no longer blocks it (`SupportTriggerLightsOnly`), and the seat cycle
  skips seats past the seventh (no game key) instead of ending there.

### 3.3 Rig and body (`Rig/*`)

- `VRRig`: the only HMD camera and both `VRHand`s; modes `OnFoot`, `Anchored`, `Follow`; height calibration
  (`EyeHeight`), physical crouch, snap/smooth turn, comfort vignette, `CameraSuppressor` (keeps the game's screen
  cameras alive for logic but stops them drawing).
- `VRHand`: pose from SteamVR (raw, grip, tip), `Palm` frame, `Role` (`Free, Weapon, Foregrip, Slide, Magazine,
  Throwable, Pointer, Cockpit, Holster, Hinge, Pin, Belt, Optic`), velocity history for throws (`ThrowVelocity` blends
  the 0.1 s average with the peak). In the autotest, hands without controllers are simulated (`SimulatedLocal`) and can
  be moved by script (`VRHand.Scripted[]`, `ScriptedVelocity[]`).
- `DamageFeedback` (head-locked damage ring replacing the flat vignette), `IntroStage` (stages the start-up intro so the
  soldiers walk past the view and the title is placed behind them), `ComfortVignette`, `CockpitControls` (steering
  wheel and flight stick you grab).

### 3.4 The glove and how hands hold things (`Rig/HandModel.cs`, `Rig/GloveMesh.cs`, `Weapons/ModHand.cs`, `Weapons/GunVolume.cs`)

This is the system rebuilt in this session. The user's report: "the hands are clipping inside the Glock and the AK …
and don't grab around them".

**The glove.** `GloveMesh` builds one skinned mesh: a lofted palm (rounded superellipse rings; the three cuff rings are
now elliptical and taper, ending in a shallow dome), and per finger a tube rooted inside the palm with shared joint
rings, fingertip caps and a weave normal map. `HandModel` poses it: palm frame +Z toward the fingers, +Y toward the
thumb side, palm facing −X (mirrored for the left hand), scale `HandScale = 0.9`.

**Where the glove goes.** Normally at the tracked palm (`VRHand.Palm` minus the fist-hollow offset). On a weapon whose
arm rig has hand bones, the glove takes the weapon's own hand pose (`SnapPose`, "Boneworks-style"):
`ModHand.TryPose` reads the live (hidden) arm bones. The frame used to point +Y at the thumb root; on most rigs that
root sits toward the palm, so the glove came out rolled 45–90° (palm down on pistol grips). It now uses the line
**across the knuckles from the little finger to the index** (sign checked against the thumb). For a left-handed player
the right-hand pose is reflected across the gun's vertical middle plane (`HeldWeapon.MirrorAcrossGun`).

**Closing on the real shape.** `GunVolume` measures the drawn model:
- Renders the weapon's visible renderers (no arms, no magazine, nothing the mod added) with
  `Hidden/Internal-DepthNormalsTexture` from both ends of five directions (x, y, z and the two diagonals round the
  barrel; magazines and parts use the three axis views) with a hidden orthographic camera on a layer verified empty
  per scene. Works for unreadable and skinned meshes because it uses what the GPU draws.
- Reads the pictures back with `AsyncGPUReadback` (no stall), then decodes and **grows the depth spans by a finger's
  radius** (`Grow = 7.2 mm`, sphere-shaped per texel) on a `ThreadPool` worker. A point is solid if it lies between
  the near and far surface in every view (a visual hull that keeps trigger-guard openings and gaps open).
- Optionally fits a physics hull from the measured surface on the worker (`HullPieces`, see 3.6).
- Cached per key in a **24-entry LRU** (was 12; 2026-10-16, `5c9af24`). A held weapon alone keeps three or four
  entries (the gun, its seated magazine, worked parts), so with 12 three weapons used in turn were measured again on
  every switch (about 12 ms on the main thread and 11 MB of garbage each). Dropped weapons' entries are evicted first,
  and the read-back pictures go into pooled buffers. A failed build is not kept (measured again next time). `InFlight`
  counts builds waiting for read-back (the menu warm-up waits while more than 4 are in flight).
- `HeldWeapon.HandSolid` asks for the gun's volume a moment after the draw (at rest, or after 5 s anyway). A request
  that returns nothing or whose read-back fails is retried a second later, three times at most per hold (it used to
  latch the failure for the whole hold; 251.16). Infinite-magazine guns (`ammo` −1, analysed as `NoAmmo`) are measured
  too (251.15); kits are not (their glove is hidden).
- Query: `Solid(world)` (grown, per-view combined world→view rotation, multiplies instead of divides, early out),
  `InsideModel(world)` (not grown, self-test), `SolidAt/InsideModelAt` (explicit pose, for several props sharing one
  volume).

`HandModel.Apply` with a `Solid` delegate set and a holding role (Weapon, Foregrip, Throwable, Magazine, Slide):
1. **Slide on the grip** (gun hand only): when the hand moved on the solid (3 mm / 2°, tracked via `SolidAnchor` +
   `SolidVersion`), up to 9 offsets along the grip are tried and the nearest one where the middle finger can close
   (proximal curl ≥ 0.2 before touching) is eased to — the weapon's hand pose often sits a finger too high and the
   middle finger then lay in the trigger guard.
2. **Settle the palm** along its normal: pushed out while palm-skin points (inside the skin by the grow distance) are
   in the solid, drawn in up to 2 cm while a gap is left.
3. **Close each finger joint by joint** (`SolveFinger`): all joints curl together in 10 steps toward the goal; when a
   segment touches, a 5-step bisection finds the contact, the joints up to that segment lock, the outer ones keep
   curling — so a finger wraps a grip. Segments already inside at the open pose do not stop it.
4. **Trigger finger**: rests where it touches the frame; pulling the (analog) trigger keeps its knuckle there and curls
   the two outer joints onto the trigger.
5. **Reuse**: while nothing changed (grip not moved, solid same, palm/slide settled, finger targets and trigger
   unchanged, thumb base settled) last frame's solves are reused — this took the per-hand cost from 0.100 to
   0.025 ms per frame.
6. **Release** eases palm push, slide and joints back to the tracked hand instead of popping.

Who provides solids:
- `HeldWeapon` (gun hand on a Weapon/Throwable, support hand on the handguard): the gun volume **plus the seated
  magazine's own volume** while it is in the gun (`UpdateSeatedMagSolid`, 3 views, follows the magazine transform).
- Two-handed pistols: the support hand's solid is the gun **or the gun hand's skin** (`HandModel.NearSkin`: finger
  capsules + palm box, published at the end of the gun hand's `Apply`; the dominant hand is now posed first). Its
  solve is redone only when `HandModel.SkinKey` changes (the gun hand's skin moved against the gun body by about half a
  millimetre, or the seated magazine came or went), not every frame (248, `288f492`/`f8ed393`).
- The support glove stays where it gripped until the grip opens (task 324, `7945ea2`): its snap pose is the weapon's own
  support pose slid along the handguard to where the palm was **at the grab** (`_supportSlideHeld`, taken once per hold;
  it followed the palm every frame before), and a gun with no usable support pose keeps the glove at its grab pose in the
  gun body's frame (`_supportGrabInGun`, from `HandModel.TrackedGlovePose`). The gun is still steered toward the tracked
  palm; only the drawn glove stays. The autotest's support pull slides the hand 10 cm along the gun and fails when the
  glove follows.
- Working the action (`HandRole.Slide`): the **worked part's own volume** (charging handle if separate, else the slide /
  bolt / pump), measured the first time it is taken, moving with the part (`HeldWeapon.PartSolid`).
- `MagazineProp` in a hand: each held magazine's volume at its own pose (the palmed second magazine too).
- Kits carried like a bag (`HeldWeapon.IsKit`: a projectile with `Ammobox`/`Medipack`/`ResupplyCrate`, or the name for
  something that fires no bullets; never a `MeleeWeapon`) hide the glove instead (`HandModel.HiddenByHeld`).
- `Hands = WeaponArms`: each VR glove gives way only to an arm the weapon actually draws (the gun hand to the model's
  right arm chain, the free hand to its left one); a model with one arm chain used to hide both gloves (251.1).

Without a solid (volume not ready, depth shader missing) the glove falls back to the earlier fixed curls with a cap.

### 3.5 Weapon analysis (`Weapons/WeaponAnalyzer.cs`, `Weapons/PoseSampler.cs`, `Weapons/WeaponRig.cs`, `Weapons/WeaponWarmup.cs`, `Weapons/WeaponOverrides.cs`)

`WeaponAnalyzer` samples each weapon's animator on an inert clone (`PoseSampler`: idle, fire, reload, aim and sight
modes) to find the gun body, the reciprocating part (slide/bolt/pump), the magazine (parts that travel or hide in the
reload; names only break ties), hinges (break actions, cylinders, feed covers), charging handles, grip and support
points and finger curls from the arm rig, sight lines (per sight mode, magnified or not) and the timing of the mod's
reload sounds. Every part has a virtual stand-in, so nothing fails outright. Results (`WeaponRigData`) are cached per
weapon key for the session (no disk cache). `WeaponWarmup` analyses every installed weapon while the main menu is up
(one per frame, paused in matches), pre-fits physics shapes and prepares dropped models.

Today's analysis fix (task 95): a magazine candidate that is **the other gun of a pair** — named dual/akimbo/off-hand,
inside such a part, or holding two kinds of gun parts (slide, bolt, barrel, trigger, hammer, laser, muzzle) — is no
longer taken, and a name-found "magazine" measured under 5 cm is rejected. Only the three P220 akimbo variants changed
(their "P220 DUAL" — the second pistol, 128 cm with its laser beam — became an invisible magazine).

**`Handgun` and `IsPistol`** (2026-10-16, bug hunt 251.2, `daccda3`/`479f066`). `WeaponRigData.IsPistol` says how the
gun is *held*: it is also set for a long gun whose support hand cups the grip (no handguard to steer by). What the gun
*is* goes by `WeaponRigData.Handgun`, taken from its loadout slot: bolt travel (a rifle held like a pistol kept a
pistol's 7 cm instead of 23 cm), the AR, named and side charging-handle rules, eye distance, virtual parts, the
machine-gun class, and in the held weapon the shape envelope, casings, the stock check, one-handed recoil, mass and
one-handed lag. A rifle held like a pistol had its hand solid cut to a pistol's box before this split.

Per-weapon overrides: `BepInEx/config/RavenfieldVR.weapons.json` (see README).

**The magazine-line table** (2026-10-20, `src/Weapons/MagLineTable.cs`, applied by `MagLineTableApply.cs` right after
the analysis and the sandbox export). The in-game fit of a magazine's line (`HeldWeapon.FitMagazineLine`) only tilts
fore/aft and counts on depth-rendered volumes; lines measured offline on the real meshes replace it for 101 guns. They
come from `tools/export_checks/mag_line_body.py --both` (points pushed into the gun's walls off the magazine's own seated
footprint, plus points standing out of the outline, side and front views, the least of the two over a tilt grid) through
`mag_line_table.py --both`. The first tables came from the side-outline scans alone, which count a point pushed *into*
the gun as hidden, and 37 of their 70 lines drove magazines through the well's walls. **Coordinates:** the sandbox export
is glTF space (`SandboxExport` writes `(x, y, -z)`); anything computed from `reports/sandbox/data` and written back as a
Unity vector must negate z. The table once shipped without it and every line was applied mirrored, 134-178 degrees off
(the log line `magazine line from the offline table, N deg` shows it; N must be the tilt, at most ~19). Guards:
`mag_line_table_check.py` (offline) and `MagLineTable.MaxTurn` (a line more than 25 degrees off the game's own analysis
is not taken). Capture onto the line from aside, and off it from inside the well, goes round under the well
(`MagazineProp.Routed`) instead of easing straight through the grip.

### 3.6 Physics shapes (`Weapons/ShapeFit.cs`, `Weapons/GunCollider.cs`)

- `ShapeFit` cuts a model into slices along its longest axis, splits slices at gaps, keeps the outermost vertices per
  slice in 16 directions; a piece with more than 180 outline points keeps the farthest point in each of 180 directions
  over the sphere (task 260: the old every-n-th thinning dropped the outermost points, so pieces stopped short of the
  model), thickens degenerate pieces (PCA) and builds convex `MeshCollider`s (box fallback when
  PhysX cannot cook). Readable meshes give vertices (capped at 8000 per mesh); unreadable meshes (about a quarter of all
  weapon meshes: 1447 of ~6070) used to join as **bounding boxes** — the "invisible square touch border".
- `GunCollider` is the held gun's physics stand-in (velocity-driven rigidbody on the prop layer, ignores the player).
  Once the gun's `GunVolume` is ready, the shape is chosen by fit to the drawn model: unreadable weapons always take the
  hull fitted from the rendered surface; readable weapons are compared once per type (share of the model covered minus
  share of the shape outside it, coarse sampling) and the better one is kept (`HeldWeapon.ChooseShape`).
- `PropPhysics` (in `GunCollider.cs`) puts loose props on a layer bullets skip, ignores the player's colliders, and now
  `IgnoreBodiesNear` (hitboxes 8, ragdolls 10, seated 16, actor colliders 17) so lying props are not pushed.
  `TrackPlayer` (noticing the player's colliders switched off and on) runs once a frame.
- `GunCollider.SeeThrough` keeps reticles, glows and glass out of the stand-in and the hands' solid (render queue above
  2500, additive/HUD/particle shaders). Its size test uses the mesh's own bounds at its scale with a 3.5 m limit; on
  the world box with 1.5 m it dropped long launcher tubes, and depended on the pose (251.21).
- The stand-in is destroyed at the start of `HeldWeapon.Release` and when the `HeldWeapon` constructor throws after
  creating it, so it is never left at a stale pose or the world origin (249).

### 3.7 Holding, reloading, recoil (`Weapons/HeldWeapon.cs`, `Weapons/ReloadInteractions.cs`, `Weapons/AmmoState.cs`, `Weapons/MagazineProp.cs`)

- `HeldWeapon` re-anchors the animated weapon each frame so its gun body sits in the hand (grip point from the
  analysis), adds two-handed steering, droop and trailing for long guns held in one hand, sight-to-eye aiming detection,
  arm hiding, part overrides and **recoil**: a critically damped spring on position and rotation (stiffness 150, 90
  for a long gun in one hand). Per shot (`ShotKick`, Tasks 238): the largest of the game's kickback, the round's own
  push (damage, muzzle speed and pellets) and a floor by class (pistol 0.9, machine gun 0.7, launcher 1.2, shotgun 1.6,
  bolt rifle 1.5, other long guns 0.7), with a climb share (pistol 1.8, launcher 0.8, bolt rifle 0.85). About 180°/s and
  0.9 m/s per kick unit; then × the strength setting, × 0.75 against the shoulder, × the support hand's share, or ×
  the class's one-handed factor (1.15 handgun, 1.9 rifles, 2.6 machine guns, 2.2 shotguns, 1.5 SMGs, 1.6 launchers). A
  sub weapon (an under-barrel launcher) kicks with its own rig, class and one-handed factor (249).
- **Weight** (Tasks 204/208/203): an unsupported long gun droops (3–11° by class) and trails the wrist; a spring on
  position and pitch, driven by the hand's acceleration against the body, makes a heavy gun lag a quick lift. The
  acceleration goes through a soft 1.2 m/s² dead band and 12/s smoothing (controller velocity jitter, differenced, shook
  a still launcher). Whether the stock is in the shoulder pocket (12 cm in, 15 out) is read from where the tracked hands
  hold the gun, before droop, lag and recoil: read from the drawn gun, the droop it switches moved a launcher's tube end
  (about 50 cm behind the grip) by some 7 cm across the band, and the launcher rocked without being fired (`b22ddd7`).
- `ReloadInteractions`: magazines ride a rail into the well and seat at full depth; B ejects (with a fresh magazine near
  the well it is a **tactical eject** into the same hand — H3VR palming); a fresh magazine driven into the seated one
  knocks it out (**mag kick**, swept window, smoothed hand speed, a light tick on the old base, no kick for 0.4 s after
  a seat); the knocked-out one flies forward and turns end over end (`KickOut`, task 260); the seated one is solid to the
  fresh one until it is knocked out (the well is open to the gun's body only: `MagazineProp.KeepOffGun`).
  Slides/bolts/handles/pumps follow the hand along their stroke; covers, belts, hinges, optics (magnifier flip).
  The pouch only answers a squeeze made at it (`_pressPalm`), and a magazine let go at the pouch is stowed only if the
  hand is slow (< 1.2 m/s) — otherwise it is thrown.
- `MagazineProp`: magazines, shells and rockets as props (copies of the weapon's own magazine meshes, baked if skinned),
  held, palmed, dropped, caught; colliders from `ShapeFit`, or from the rendered model once measured if the meshes are
  unreadable (`TakeRenderedShape`; later magazines of the type start with it).
- `AmmoState` keeps the physical ammo (magazine, chamber, seated, slide lock) and syncs `weapon.ammo`. A fire-mode
  variant on its parent's model reads and moves the parent's magazine (`MagazineOf`): with the magazine out, switching
  to it used to draw the magazine again and fire without one (249.1).

**Holding and seating magazines (2026-10-16, Tasks 240-242, `2467c07`, `32af006`, `9ca386f`).**
- The grip thickness is measured where the hand holds the magazine (a band near the base plate, from the vertices,
  else from the magazine's rendered shape once measured), not from a box tilted by the insert axis. That box made most
  rifle and pistol magazines a machine gun's box held out on the fingers (on the back of the hand on 91 guns). The
  measured thickness applies to every magazine of the type, palmed ones too, and eases in over 0.1 s.
- Pistol magazines are held by the base in the fist, out between thumb and index (the user's pictures; the index-point
  hold is gone). Sliding up the grip, the glove turns from the grip into a push on the base plate with an opening hand,
  the thumb coming away, eased with the depth (20–75 % of the way). The push applies only while the magazine moves in;
  its direction is tracked with 3 mm of play, so a slow push still turns into the push and a slow pull lets go of it.
- Pose changes (one magazine to two, a palmed one promoted, the push) fade over 0.3 s instead of snapping; a
  tactically ejected magazine eases from the well into the hand.
- A magazine started straight into a well inside the grip is drawn home by the rail on a damped spring (about a third
  of a second, at most 0.6 m/s, its start speed capped at that), not at a constant 0.9 m/s: the hand's turn into the
  push was over in a tenth of a second and never seen.
- Magazines go in along the gun's side plane: an insert axis tipped sideways by less than 45° is put back into it (the
  1911's came in 23° from the side, up beside the grip); side-feeders keep theirs (`772658a`).

**Rockets by hand (2026-10-16, Task 250, `aa03500`, `96667e6`, `b0a99c2`, `bf8e0cb`, `670b6db`, `e883241`).**
- The rocket in the hand collides with the launcher: it is kept out of the launcher's measured shape the way a
  magazine is kept out of a gun, sampled every 6 cm along it, with no well open to it from the side.
- It is loaded at the **front**: the rail runs from the muzzle to where the tail sits seated, and the rocket always goes
  in back along the bore whatever the reload animation's first motion. It is taken onto the line only at the tube's
  mouth (its tail at most 8 cm in, within 12 cm of the tube's line in front of the mouth, pointing into the tube within
  60°, tail first only). A rocket held at the back or beside the tube is not taken.
- It slides in **with the hand** the whole way (no drawing in by itself, no letting go) and stays on the line until it
  is pulled back out of the mouth. The hand holds it by its front part, 6 cm past what goes in. A rocket that goes all
  the way into its tube turns the hand into a push on its nose for the last of it.
- **The launcher's own rocket**: the analysis keeps which of the rocket's renderers show while it sits loaded (an
  RPG-7V's rocket holds five warheads and shows one), and `MagazineProp.KeepLoadedLook` copies the rocket while it sits
  loaded (after the shot the game shrinks and moves it, so the RPG-7V variants held a made-up olive stand-in). A copy is
  refused when shrunk or moved away; tries back off (twice a second for five tries, then every 10 s, 30 at most). A
  near-empty rocket part is replaced by the container above it that holds a rocket-sized mesh, never the launcher itself
  (the Panzerfaust 3 once handed out its whole launcher).
- **Missile tubes** that show no rocket of their own (HYDRA, SCALPEL, the signal launcher) hand out the projectile they
  fire, its own meshes, seated in the tube with its nose at the mouth (it was a 75 cm olive capsule).
- Inside the tube the rocket is held by it: it comes out only back through the mouth, and stays on the line until the
  hand is 35 cm off it. A rocket's rail is accepted only between 5 cm and 1.8 m deep. (Earlier the same day a rocket
  started into its tube was drawn home on its own by a spring, up to 1.6 m/s; that was replaced by sliding with the
  hand.)

### 3.8 Drops, holsters, pouch, throws (`Weapons/WeaponDrops.cs`, `Weapons/WeaponSystem.cs`, `Weapons/HolsterDisplay.cs`, `Weapons/PouchDisplay.cs`, `Weapons/ThrowAndMelee.cs`, `Weapons/GrenadeDud.cs`, `Weapons/BodyZones.cs`)

- **Dropped weapons**: bots drop their weapon as an inert copy of its third-person model, now copied from **only the
  third-person part of the prefab** (`BuildWorldModelFromSubtree`; whole-prefab copy only when skinned parts use bones
  outside it). Shapes come from `ShapeFit` or, for unreadable models, from a depth volume measured during the menu
  warm-up (`MeasureRenderedShape`, scaled per drop). Drops have a grippy, low-bounce material and ignore bodies near
  them. The player's own gun let go away from the body drops with the velocity **taken when the hand opened** and
  returns to its holster after `WeaponReturnSeconds`; its collider is the held stand-in's current shape plus the inserted
  magazine.
- **Kept weapons** (`DroppedWeapon.Keep`, 2026-10-16): picking a weapon up into a full slot puts the one you had there
  down where the picked-up one lay; it is the only copy of it, so it does not expire with ordinary drops. Your own
  dropped weapon whose holster slot you have filled since (it cannot go back) becomes kept too; it used to expire after
  90 s and was lost from both the ground and the loadout (249). Past six kept weapons the oldest stops being kept and
  becomes an ordinary drop with a fresh lifetime, still grabbable; the cap (added `303be9d`) first deleted it outright
  (249). Ordinary drops are capped at `MaxDroppedWeapons` (16), oldest first.
- **Picking up** uses `GrabPressed || GrabReaching` (a squeeze up to 0.25 s before the weapon is in reach still takes
  it). The nearest drop is found per hand per frame: a drop is skipped with one read of its root when the root is
  farther than the best distance plus the drop's reach (a radius over all its colliders, computed at spawn), and each
  collider's box is tested before its exact closest point (248, `0f83c14`).
- **Holsters** by weapon type (`BodyZones`), shown as copies (`HolsterDisplay`; a type without a third-person model is
  remembered as missing instead of rebuilt every frame). The pouch sits a hand's width from every holster spot (it used
  to overlap the off-hip holster).
- **Throwables**: grenades with pins (`GrenadeDud` for pinned throws), throws with real hand velocity, melee along the
  measured blade. A gun swung into an enemy hits with four points swept from butt to muzzle (`MeleeHandler.Bash`), and
  since 2026-10-20 a magazine, shell or rocket in a hand or a grenade held to throw hits lightly too (`HandBlows`: the
  palm's path, the knife's speed against the body, credited to the gun in use; task 306).
- **First drops without the analysis** (`ParkedPartsCache`, 2026-10-20): a drop template hides the parked ammunition the
  analysis found (`HideParkedAmmo`), and that was the only reason a type's first drop ran the whole analysis on a soldier's
  death frame (235-300 ms for the PKP, PKM, Type 80, BRN180s when the warm-up had reached 78 of 286 weapons). Every prefab
  analysis now writes its `SparePartPaths` to `BepInEx/cache/RavenfieldVR-parked-parts.txt`, keyed by the analysis key and
  hashed against the prefab's transforms (count, names, child counts; a mod update misses and is analysed again). From the
  second session on, a first drop takes the paths from there; the idle builder (player dead or in a menu) then builds the
  type's model, its shape and its analysis, one a turn, so a pickup waits for nothing. Each analysis rewrites its entry,
  so a rule changed in a later build is carried over the next time that weapon is analysed. The slow-drop log line says
  how much of a first template its analysis was.
- **Equip take-over**: `WeaponSystem.BeforeRender` attaches a weapon the game drew after the mod's `Update`, so it is
  never drawn where the flat game holds it (task 97).

### 3.9 UI (`UI/*`)

`CanvasManager` converts canvases (HUD frame head-locked and frustum-matched; menus placed in front), `VRInputModule`
laser pointer, `VRKeyboard` (SteamVR overlay keyboard), `VRSettingsPanel` (double-click left stick), `WristHud`,
`ModKeysPanel`, `SeatSights` (vehicle sights collimated at infinity), `UiKit` shared look. The game camera's aspect
(and so its vertical FOV, which places the screen-projected markers) is taken from the largest visible HUD canvas;
it used to follow whichever canvas was enumerated last, so a small or 4:3 panel shifted the markers (249). A canvas
that turns back into a menu without reopening is placed and flattened again like a fresh opening. Menus are stacked
toward the player by sorting order (4 mm a step, `PlaceMenu`), so the laser's ball lands on the nearest canvas that
**draws something** where it points (`VRInputModule.ShowsAt`: a shown, unculled graphic with its groups' alpha), not on
the nearest rectangle: a clear canvas above the menu was an invisible wall a few centimetres in front (task 323). A click
goes to the first raycast hit whose root canvas is a laser target.

### 3.10 Test tooling (`Core/AutoTest.cs`, `Weapons/WeaponSelfTest.cs`, `Core/Shots.cs`, `Core/PerfProbe.cs`, `Rig/HandPreview.cs`)

- **Reading a play session's log** (no flag needed): every few seconds of a diagnose window `VRInput` logs an
  `Input check` line with the fps over the last window, **`mod N ms a frame`** (the mod's own Update, LateUpdate and
  before-render work, `VRManager.TakeModMsPerFrame`), **`drops N (avg, max ms)`** (the death-frame drop spawns since the
  last line, `WeaponDrops.TakeDropTiming`) and **`gpu N ms a frame`** (SteamVR's compositor timing; through Link it
  reads far below the real cost and is not the frame interval). The user's session of 2026-10-20 night read 0.4-0.5 ms
  of mod time in play at 42-58 fps (the frame was the game's), 1-3 ms later in the match after kills, 2-8 ms dead in the
  follow view (the idle builder's analyses, since moved to the respawn and loadout screens) and 6-23 ms on the loading
  screen (the drop builds, meant to be there). Slow first drops also log `its analysis N ms of it`.

- **`-rfvr-selftest`** (flat or VR, from the menu, does not quit): analyses every weapon and writes
  `BepInEx/RavenfieldVR-weapon-report.txt` — summary counts, per-weapon parts and how they were found, physics shape
  pieces and fitting time, sounds, notes, dropped-weapon costs (first drop with warm-up measuring included, prepared
  drop, and the model copy both ways), and renders the glove picture `RavenfieldVR-hands.png`.
- **`-rfvr-autotest`** (`-vr -map island2023 -nointro -rfvr-autotest [-rfvr-quick] [-rfvr-perf]`): plays a match
  (every mutator on, 4 bots a side, invulnerable player, runs in the background), equips each weapon (`-rfvr-quick`: a
  spread of ~60), skips draw animations, checks the model is visible, runs the reload, fires, and in VR with simulated
  hands:
  - pictures per weapon plus **grip close-ups** from three sides and with the weapon's own arms shown;
  - **hand measurements**: points of the glove (finger axes and palm middle, 72) inside the model and fingers touching,
    compared in the same run with the old posing (`HandModel.ClosingDisabled`), trigger pulled new vs earlier,
    support hand on long guns and pistols (and inside the gun hand), left-handed (`VRConfig.TestLeftHanded`, never the
    saved setting), rack grip on the worked part at rest and pulled half way, magazine hand;
  - **reloads**: tactical (direct), H3VR mag kick and H3VR tactical reload driven through the real hand path (scripted
    hand, simulated mag-release press), rounds counted without the pouch (a mutator refills it);
  - **recoil**: one hand vs two hands per shot (degrees and the impulse each shot gave);
  - **pouch / throws / drops**: touching takes nothing, squeezing takes a magazine, a swing throws it; a gun let go in a
    swing flies and returns; dropped weapons and a magazine stay put on the ground and when a body-sized capsule walks
    through them; bags hide the glove; reaching grab; grenade pins (call-ins on cooldown are skipped);
  - **physics shape match** (share outside / covered) for held guns and a thrown magazine;
  - a **Measurements** block (total / count / average / max per metric), the five most buried hands, and counters of
    frames drawn with a weapon not yet in the VR hand or with the flat arms showing.
  Writes `BepInEx/RavenfieldVR-autotest.txt` progressively and quits.
- **`Shots.Capture`** renders mono pictures from the VR head (or any pose) to `BepInEx/RavenfieldVR-shots/`.
- **`-rfvr-perf`** enables `PerfProbe` in any session (per-section ms, allocations, frame-time percentiles).
- **Other autotest switches**: `-rfvr-deep` (the full weapons run: every launcher is emptied and reloaded by hand, the
  magazine seating pictures), `-rfvr-flashfilm` / `-rfvr-flashcheck` (the eye film of shots), `-rfvr-filmonemag`,
  `-rfvr-grabshots`, `-rfvr-uishots`, `-rfvr-menutest`, `-rfvr-perfbench`. Self-check lines put failures in capitals
  (NOT SEATED, NEVER LINED UP, TOO SMALL, TAKEN FROM THE BACK, BESIDE THE WELL, STILL SHOWS, SHAKES, …) and the run
  collects them into one failure list.

Added up to 2026-10-16:
- **Rocket film / checks** (`ReloadInteractions` self-check, in every weapons run with or without pictures): per
  launcher, the rocket from the pouch is pushed into the launcher's side and down across the tube (it must stay out),
  probed at the back (must not be taken), lined up at the mouth (how far in front it is taken), slid in (it must not get
  ahead of the hand) and loaded with the hand on it; whether the rocket in the hand is the launcher's own model (its
  meshes against those the launcher shows loaded). It waits up to 6 s for the launcher's measured shape and says when
  there is none. A launcher that did not fire is unloaded instead of skipped.
- **Magazine seating check**: pictures at 30, 60 and 85 % in (between the base plate and the grip, wide enough to show
  the gun), how far the hand has turned into the push, glove points inside the gun hand, and how much of the magazine
  that has gone **past the well's mouth** shows beside the gun's sides (from the side view of its measured shape,
  compared with the same points seated), plus the insert line's tilt out of the gun's side plane. Counted from the whole
  rail, rifle magazines still under their well read as beside it on almost every gun (`35f9f8a`).
- **Eye film** (`Core/EyeFilm.cs`, `-rfvr-flashfilm`): each eye's finished picture per frame, read back without
  stalling and written as PNGs on a thread of its own (a frame is skipped for both eyes while the writer is behind),
  with `frames.txt` (frame, eye, time, note, the lens flash and glow in that eye; invariant numbers since 251.18).
- **Steady check** (Tasks 203): a still hand held 1.5 s one-handed, with 2 cm/s of tracking noise in its velocity, and
  two-handed; the most the gun body turns and moves between frames (fails above 0.2° or 2 mm).
- **Rack picture check** (Tasks 246): read-only Haiku agents compare each gun's rack close-ups at rest and pulled half
  way (does the gun's own part come back with the hand). First run 53 of 233 guns; after fixing the test pose and the
  close-up camera, 116 of 233 move their own part, 54 do not, 7 an added knob, 56 unclear
  (`reports/agents/rack-check-2026-10-16/SUMMARY.txt`, per gun under "For 246" in `Tasks.md`).

Tools (`tools/`, Python, local files only):
- `vrshots.py`: runs the game under SteamVR's null driver, lets the autotest take pictures, and always restores the
  user's SteamVR settings (hash-checked backup); `--weapons`, `--weapon NAME|ID|mod:TEXT|=NAME`, `--ui`, `--hands`,
  `--bench`, `--autotest`, `--film [GUN]`, `--dumprig`. Output under `reports/shots/<mode>/`.
- `flashfilm.py`: the eye films as side-by-side MP4, a slow GIF and a per-shot strip per gun, plus `film-summary.txt`
  (each eye's brightness standing still, which must match, and the lens flash per shot and eye). Reads comma decimals
  too (a German Windows).
- `seqgif.py`: GIFs from a weapons run's stills per gun: the magazine sequence (seated, out, in the hand, lined up, 30 /
  half / 60 / 85 % in, seated again) and racking (rest and half pulled, both sides).
- `showcase.py`: one folder with an index page of reload GIFs, the pose sheet and before/after pairs of two runs.
- `montage.py`: one MP4/GIF montage of a run's pictures, weapon by weapon; `shots_categories.py`: sorts a run's weapon
  sheets into folders by kind (action, pistol grip, machine gun) for review; `inspect_weapon_bundles.py`: prints how the
  guns in workshop `.rfc` bundles are rigged and which transforms each reload animation moves.

---

## 4. Key Files Reference

| Path | What lives there | Why it matters |
| --- | --- | --- |
| `src/Core/VRPlugin.cs` | BepInEx entry, Harmony, VR start | where everything begins |
| `src/Core/VRManager.cs` | per-frame order, hand apply order | ordering bugs live here |
| `src/Core/VRConfig.cs` | every setting (+ `TestLeftHanded`) | defaults and migrations |
| `src/Core/AutoTest.cs` | automated VR/flat match test, Measurements block, test patches | the main verification tool |
| `src/Runtime/OpenVRRuntime.cs` | own XR loader and event pump | VR boot without XR Management |
| `src/Rig/VRRig.cs`, `VRHand.cs` | camera rig, hands, roles, simulated/scripted hands | tracking and test hooks |
| `src/Rig/HandModel.cs` | glove posing: snap, settle, slide, finger solve, reuse, skin | how hands hold things |
| `src/Rig/GloveMesh.cs` | glove geometry | looks |
| `src/Weapons/GunVolume.cs` | depth-rendered solid, async read-back, worker growth, hull | the model as the hands see it |
| `src/Weapons/ModHand.cs` | the weapon's own hand pose from its arm bones | authored grips |
| `src/Weapons/HeldWeapon.cs` | weapon in hand, recoil, solids for hands, shape choice | central weapon logic |
| `src/Weapons/ReloadInteractions.cs` | manual reloads, H3VR kick/palming, pouch, rack grips, self-checks | reload feel |
| `src/Weapons/WeaponAnalyzer.cs`, `PoseSampler.cs` | rig analysis by sampled animation | works for any mod |
| `src/Weapons/ShapeFit.cs`, `GunCollider.cs` | convex physics shapes, prop physics, body ignoring | "only the model is the hitbox" |
| `src/Weapons/MagazineProp.cs` | physical magazines, shells, rockets | reload props |
| `src/Weapons/WeaponDrops.cs` | dropped weapons, pickup, return, warm-up prep | drops |
| `src/Weapons/WeaponSystem.cs` | owner of held weapon, holsters, let-go, equip take-over | weapon lifecycle |
| `src/Weapons/WeaponSelfTest.cs` | all-weapons report | analysis regressions |
| `src/Weapons/WeaponPatches.cs` | Harmony hooks on the game's weapons (CanFire, Shoot, Reload, Update, aiming) | where a shot reaches the mod; the Shoot prefix records the muzzle that fires (the game moves on to the next barrel before the postfix) |
| `src/Rig/LensEffects.cs`, `src/Weapons/LensFlash.cs`, `src/Weapons/MuzzleFireball.cs` | on-lens flash, hit, low health, drops (worker threads); the flash per shot; its core in the world | screen effects (Tasks 209/237) |
| `src/Core/EyeFilm.cs` | per-eye frames to PNG for the films | flash and eye-brightness checks |
| `src/Input/VRInput.cs` | SteamVR input, edges, haptics, test hooks | input semantics |
| `build.ps1`, `tools/fetch-deps.ps1` | Roslyn build without an SDK, packaging | building |
| `tools/vrshots.py`, `flashfilm.py`, `seqgif.py`, `showcase.py` | headless picture runs and what is made of them | checking by eye (and by agents) |
| `reports/agents/*` | read-only agent reports (bug hunts, reviews, rack check) with their skeptics | finding history |
| `Tasks.md`, `Improve20.md` | task history and the improve run | intent and results |

---

## 5. Configuration, Knobs & Flags

All in `BepInEx/config/com.qwavey.ravenfieldvr.cfg` (most also on the in-headset panel; read live).

| Section | Key | Default | Effect |
| --- | --- | --- | --- |
| General | `EnableVR`, `AutoStartSteamVR`, `DominantHand` | true, false, Right | VR on, SteamVR autostart, weapon hand |
| Rendering | `StereoMode`, `GraphicsPreset`, `RenderScale`, `MirrorView`, `NearClip`, `PostProcessing`, `MinFarClip` | MultiPass, Medium, 1.0, Right, 0.02, off, 1500 | rendering cost/quality |
| Comfort | `TurnMode`, `SnapTurnAngle`, `SmoothTurnSpeed`, `MoveDirection`, `Vignette`, `PhysicalCrouch`(+`Ratio`), `RoomscaleMovement`, `EyeHeight`, `SeatedMode`, `LevelHorizonInVehicles` | Snap, 30, 120, Head, 0.35, on (0.75), on, 0 (calibrated), off, off | comfort |
| Controls | `TurnStickJumpCrouch`, `StickDeadzone`, `SprintMode`, `HapticStrength`, `GunAngleMode`, `GunRotationOffset`, `GunPositionOffset`, `GunPitchTrim`, `HandModelRotation/Position`, `LaserRotationOffset`, `TurretAim` | on, 0.15, Toggle, 1, Auto, "35,…", "0,…", 0, —, "40,…", Head | input feel and offsets |
| Weapons | `ReloadMode` | Manual | Assisted = game reloads |
| Weapons | `ChamberPlusOne`, `ReturnRoundsOnMagDrop`, `LoseRoundOnRack` | on, on, on | ammo rules |
| Weapons | `MagazinePouch`, `ShowPouch` | OffHandHip, on | pouch place (now a hand's width from holsters) |
| Weapons | `DroppedMagLifetime`, `MaxDroppedMags` | 60 s, 12 | prop clean-up |
| Weapons | `RecoilStrength`, `TwoHandRecoilReduction` | 1, 0.6 | recoil scale, second hand's share |
| Weapons | `Hands` | VRHands | or WeaponArms (the weapon's own arms by IK) |
| Weapons | `SkipDrawAnimation`, `WeaponWeight`, `TwoHandedAiming` | on, on, on | handling |
| Weapons | `VRScopes`, `ScopeResolution` | on, 768 | scope lenses |
| Weapons | `HolsterSwitching`, `ShowHolsters`, `GripToHoldWeapon`, `ReleaseDropsWeapon`, `WeaponReturnSeconds` | on, on, on, on, 8 s | holding and letting go |
| Weapons | `MagazinePalming`, `ThrowVelocityMultiplier`, `GrenadePins`, `BeltByHand`, `MeleeSwingSpeed` | on, 1.4, on, on, 2.6 | interactions |
| Weapons | `HideGameWeaponHud`, `WeaponsWhileDriving`, `FireSelector`, `ModWeaponSounds`, `GrabRadius` | on, on, on, on, 0.09 m | misc |
| Weapons | `EnemyWeaponDrops`, `DroppedWeaponLifetime`, `MaxDroppedWeapons`, `DroppedWeaponAmmo` | on, 90 s, 16, 0.5 | drops |
| UI | `MagazineReadout`, `HudDistance`, `HudHorizontalFov`, `MenuDistance`, `MenuWidth`, `WristHud`, `GunAmmoCounter`, `LaserColor` | on, 4 m, 72°, 1.5 m, 1.7 m, on, on, #E8B44A | UI |
| Debug | `VerboseLogging`, `ShowGrabZones`, `DumpWeaponRigs`, `WeaponSelfTest`, `ConfigRevision` | off, off, off, off, 0 | diagnostics |

Command-line flags: `-vr`, `-novr`, `-nointro`, `-map <name>`, `-rfvr-selftest`, `-rfvr-autotest`, `-rfvr-quick`,
`-rfvr-perf`, `-rfvr-shots` (pictures outside the autotest, e.g. of the intro), `-rfvr-introdump`,
`-rfvr-dumprig=<name>`.

In-code constants worth knowing: `GunVolume.Grow` 7.2 mm, `FineTexel` 1.5 mm, `MaxTexels` 448, `CacheSize` 24 (12 until
2026-10-16), in-flight limit 4 (warm-up); `HandModel.HandScale` 0.9, slide steps 0…3 cm down / 1 cm up, palm settle range −4…+2 cm;
`ShapeFit.MaxPointsPerMesh` 8000; drop body check radius 1.5 m (magazines 1 m).

---

## 6. How to Run, Build & Test

Build (no .NET SDK needed):

```powershell
./tools/fetch-deps.ps1              # BepInEx, OpenVR XR plugin, Roslyn
./build.ps1                         # compiles against the installed game into build/
./build.ps1 -Package                # + dist/RavenfieldVR, zip, launcher, Setup.exe
```

From a bash shell use `powershell -NoProfile -ExecutionPolicy Bypass -File build.ps1`. Install during development by
copying `build/RavenfieldVR.dll` and `.pdb` to `<game>/BepInEx/plugins/RavenfieldVR/`.

**Steam must be running** before the game starts, or workshop weapons do not load (the self-test then sees ~41
weapons instead of 308).

Flat self-test: `ravenfield.exe -novr -nointro -rfvr-selftest`, wait for "Glove picture" in
`BepInEx/LogOutput.log`, read `BepInEx/RavenfieldVR-weapon-report.txt`, close the game.

VR autotest without a headset (null driver):

1. Back up `Steam/config/steamvr.vrsettings`, set `"forcedDriver": "null"` and enable `driver_null`.
2. `ravenfield.exe -vr -map island2023 -nointro -rfvr-autotest -rfvr-quick -rfvr-perf -screen-fullscreen 0
   -screen-width 1280 -screen-height 720` (can be started minimized; the test runs in the background).
3. The first launch after enabling the null driver is often closed by SteamVR's room setup (`steamvr_room_setup.exe`):
   close that window and relaunch.
4. Read `BepInEx/RavenfieldVR-autotest.txt` (Problems, frame counters, Measurements, Performance, Results) and the
   pictures in `BepInEx/RavenfieldVR-shots/`.
5. **Restore** `steamvr.vrsettings`: stop all `vr*` processes first (a running SteamVR rewrites the file on exit),
   then copy the backup back.

Checks without the game (2026-10-20, issue #18): `powershell -File tools/tests/run-tests.ps1` compiles
`tools/tests/OfflineTests.cs` with the installer's sources and `BindingsWriter` and runs 20 checks: the installer's path
containment (`Installer.Inside`: zip entries and manifest lines stay inside the game folder), the Steam `localconfig.vdf`
reader (a truncated file is a `FormatException`), the left-handed bindings. Exit code 0 when all pass.

Dependencies are pinned by SHA-256 in `tools/fetch-deps.ps1` (checked against GitHub's release digests and nuget.org's
catalog hashes); a mismatching download is deleted and not unpacked. The in-game self-repair (`InstallCheck`) copies the
bundled runtime only when it matches the XR plugin 1.2.4's hashes. `ucrtbased.dll` (the XR package's debug C runtime) is
not shipped: neither `XRSDKOpenVR.dll` nor `openvr_api.dll` imports it, directly or delay-loaded.

What "working" looks like (latest quick run): Problems 2 (the mk18riii laser script), 0 frames with a weapon not in
the VR hand, gun-hand points inside the gun ≈ 1 of 72 on average, 0 cm drift for lying weapons, rounds add up in all
reload checks, one-handed recoil impulse above two-handed on every measured weapon.

---

## 7. Decisions & Rationale

- **Own XR loader** instead of Unity's XR Management: the game ships without it; creating the subsystems directly from
  the provider's manifest avoids patching game assets.
- **Analyse weapons by their animations, not names**: workshop weapons use any naming (often Chinese); what moves when
  it fires or reloads is reliable. Names only break ties.
- **Never pin the hand to the gun**: parts follow the hand; the glove may snap to an authored pose, the tracked hand
  still decides where the gun is.
- **Depth-rendered volume instead of collider queries for the hands**: works on unreadable and skinned meshes (24% of
  meshes are unreadable), costs ~0.2 µs per query, and stays exact for what is drawn. PhysX `ClosestPoint` on the gun's
  colliders would have inherited the box approximations and the proxy's one-step lag.
- **Visual hull from 5 directions** (3 for magazines and parts): axis views alone leave square corners on round
  handguards; the two diagonals around the barrel cut them; magazines are box-like.
- **Asynchronous read-back + worker-thread growth**: the first draw of a weapon used to stall the main thread 78–89 ms;
  now 5–9 ms (only the renders).
- **Contact-limited joint-by-joint curl** rather than fixed curls: fixed curls either float off a thin grip or sink into
  a thick one; stopping each joint at contact wraps any shape.
- **Read the authored hand across the knuckles**: the thumb root sits palm-side on most rigs (measured in the hand
  diagnostics: e.g. AK-12 thumb root at x −4.7 cm, index/pinky roots at y +4.2/−3.8 cm).
- **Mirror the authored right hand for left-handed players**: rigs only have a right gun hand and a left support hand.
- **Test-only changes stay test-only**: bot count, invulnerability, measurement comparisons (e.g. the before/after
  shape match on unreadable weapons) run only with `-rfvr-autotest`, so play never pays for them.
- **Measure in the same run where possible** (A/B toggles: `ClosingDisabled`, `LegacyTriggerFinger`,
  `LegacySupportSolid`, `GunVolume.LegacyQueries`, `TimeModelCopy(partOnly)`): runs differ (bots, mutators, timing),
  same-run comparisons do not.
- **Drops ignore bodies per collider pair, not by layer matrix**: the prop layer may be shared with game objects
  (e.g. triggers on layer 2); changing the global collision matrix could break capture zones.
- **Bug hunts by read-only finders, each finding checked by skeptics** (2026-10-15/16): Haiku agents read one area each
  and report findings with file, line, trigger and fix; two more Haiku agents try to refute each one. Only confirmed (or
  plausible, with the doubt written down) findings are fixed, and the reasons are kept (`Tasks.md` "For 248/249/251",
  `reports/agents/`). Line numbers from the finders are often a few lines off and some suggested fixes were wrong as
  written (an early return that would have dropped palm hits; a muzzle index read after the game had advanced it), so
  every fix is checked against the code first.
- **Two branches split by files** (2026-10-16): a lead session on `tasks-98-113` and a helper session on its own branch
  and worktree (`helper-248-249`, `../Ravenfield-VR-helper`), each owning a fixed set of files (the helper: held weapon,
  drops, weapon system, throws, stand-in, scope, lens effects and flash, casings, belt, UI, input, `VRManager`,
  `VRRig`, `WeaponPatches`, this document; the lead: analysis, rig data, magazines, reloads, ammo, gun volume, hands,
  autotest, tools, `Tasks.md`). Only the lead writes `Tasks.md` and merges; the helper commits one finished item at a
  time, each building cleanly, and never runs the game while the user plays. With no file edited by both, the merges
  are conflict-free.

---

## 8. What Was Tried & Abandoned ("What Was Lost")

- **Hand frame from the thumb root** — rolled the glove 45–90° (palm down on grips). Replaced by the knuckle line.
- **Fixed finger curls with a cap (0.85 / 0.75)** as the main holding pose — kept only as the fallback when no solid is
  measured.
- **Bounding boxes for unreadable meshes** as physics shape — replaced by hulls fitted to the rendered surface
  (improve round 10, then all weapons where better, magazines and bot drops).
- **Vertex cap 3000 per mesh** (improve round 7) — saved ~8 ms per weapon at warm-up but changed 13 weapon shapes;
  reverted (fidelity over menu time).
- **Synchronous `ReadPixels`** for the volume — replaced by `AsyncGPUReadback` (78–89 ms → 5–9 ms main thread).
- **Scaling the glove to the weapon's own hand size** (mod hands measure ~7.8–8.2 cm wrist-to-knuckle vs the glove's
  9.6 cm) — rejected: a hand that changes size per weapon reads wrong; wrapping the real shape made the size mismatch
  much less visible.
- **Changing the physics layer matrix** so drops ignore bodies — rejected (see Decisions).
- **Locking the trigger finger's knuckle on the frame** (from the swarm review) — kept, but measured neutral (87 vs 87,
  97 vs 97, 134 vs 139 buried points with the trigger pulled).
- **A broad "looks like a weapon" magazine filter** (any child named …receiver…) — rejected three real magazines
  (M733, M870, P320 meshes are named after the receiver asset); narrowed to two kinds of gun parts, receiver excluded.
- **A 5 cm minimum for name-found magazines on unmeasurable bounds** — rejected five real magazines (HK416, MP9,
  sentry guns) whose bounds read 0; the minimum now applies only to measured sizes.
- **Pump-only part solids** — generalised to every worked part (charging handle, slide, bolt, pump).
- **The fine (7³ / 400-point) shape comparison in play** — moved to the self-test only (saved 5–14 ms per first draw).
- **Rockets drawn into the tube by a spring** (2026-10-16, up to 1.6 m/s) — replaced the same day by the rocket sliding
  with the hand the whole way (the user's request: no drawing in by itself, no letting go).
- **Rockets taken onto the line inside the tube, or from any side** — replaced by loading at the front only, tail
  first.
- **The index-point hold for pistol magazines** — replaced by the base in the fist (the user's pictures).
- **A magazine's grip thickness from its box tilted by the insert axis** — made most magazines a machine gun's box held
  on the fingers; replaced by measuring where the hand holds it.
- **Seating counted along the whole rail** — read rifle magazines still under their well as beside it; replaced by
  measuring only what has gone past the well's mouth.
- **The 1.5 m world-box limit in `SeeThrough`** — dropped long launcher tubes; replaced by mesh bounds and 3.5 m.
- **Rejected review findings**: melee "misses are distance 0" (the game's `SphereCast` returns `float.MaxValue` for
  misses, and its own code relies on that) and "fire stays suppressed after a grenade" (reset every frame).

---

## 9. Findings & Discoveries

Measured on the VR quick autotest (60 weapons) and the flat self-test (308 weapons) on 2026-10-13:

| What | Before | After |
| --- | --- | --- |
| Gun-hand points buried in the gun (of 72), same run old vs new posing | 12.4 avg | 1.0 avg |
| Buried points over 33 pictured weapons (first measurement) | 383 | 57 |
| Support hand round a pistol: points inside the gun hand | 13.1 avg (max 23) | 0.6 avg |
| Magazine hand: points inside the magazine | 7–10 | 0 |
| Hand cost per hand per frame | 0.100 ms | 0.025 ms |
| Solid query | 208–226 ns | 171–187 ns (99.99% identical answers) |
| Volume main-thread cost per first draw | 78–89 ms | 5–9 ms (magazines 2.5–2.8 ms) |
| Unreadable weapons' physics shape (outside / covered) | 64% / 50% | 36% / 83% |
| Thrown magazine shape | — | 1–3% outside / 95–100% covered |
| Dropped model copy (40 weapons) | 0.74–1.21 ms (whole prefab) | 0.09–0.34 ms (third-person part) |
| Weapon drop in a match | 11.8–15.6 ms avg, max 125 | 3.9 ms avg, max 40 |
| Lying weapon / magazine moved by a body walking through | (pushed) | 0 cm |
| Recoil, one hand vs two hands (degrees, per shot) | "didn't work" | e.g. 3.0 vs 1.6, 1.8 vs 0.5; impulse 171 vs 90 deg/s |
| Frames with a new weapon not yet in the VR hand | (visible flash) | 0 |

Other discoveries:
- About a quarter of all weapon meshes (1447 of ~6070) cannot be read by the CPU; only rendering sees them.
- Weapon arm rigs put the hand bones near the gun's middle plane, so the authored hand overlaps the grip; the palm has
  to be settled out of the model.
- The weapon's own hands are ~82% of a real hand; guns look small next to a real-size glove.
- `AsyncGPUReadback` rows came back in the same order as `ReadPixels` (async and sync runs measured the same).
- Test pitfalls found the hard way: SteamVR room setup closes the first null-driver launch; bots killing the test player
  cut checks short ("NOT EQUIPPED", drops never returning); the "Infinite Things" mutator refills spare ammo mid-check;
  a revolver's shot can land after the measurement window; call-in smoke grenades refuse to be thrown on cooldown;
  simulated squeezes need a pressed edge.
- Process memory in a full VR test reaches ~9.8 GB private (stable, not growing) with the workshop content loaded.
- The 2 remaining autotest "problems" are a workshop weapon's own laser script (`LaserSight.LateUpdate` on the
  mk18riii) throwing once per draw.

---

## 10. Known Issues, Limitations & Risks

- **Rifle thumb** reaches the grip on only 15–25% of rifles; **pulled trigger finger** still buries ~3.6–4 points on
  average (one weapon 48).
- **Shape choice hitch**: comparing a readable weapon's two shapes costs 9–18 ms once per weapon type on its first
  draw (mostly cooking colliders).
- **Volumes are measured once at rest** (drawn, not reloading, slide home, hinge shut; after 5 s anyway): an empty gun
  drawn with its slide locked back keeps that shape for the session.
- **Rack grips** are pictured on every gun of the weapons run since 2026-10-16 (pistol slides held overhand, the hand
  over the top unless the handle is clearly to the side); see the rack picture check below.
- **Left-handed play** is verified only with simulated hands.
- **Equip flash**: the frame-order gap is closed (0 frames counted); if a flash of the soldier body (rather than the
  flat arms) remains, the counter does not cover it.
- **First drop of a heavy model** can still cost ~40 ms when the menu warm-up did not reach that weapon type.
- **AT-1K** keeps an odd magazine analysis ("AK", 1060 cm) that predates this session.
- **Depth shader dependency**: without `Hidden/Internal-DepthNormalsTexture` the hands fall back to fixed curls.
- **Memory**: up to 24 cached volumes keep their maps (a few MB each; 12 before 2026-10-16).
- **One eye brighter on Island** (225, some runs, not reproduced; the film's still check had both eyes within 0.5 %):
  the mod's two headset cameras (world and UI) are set the same for both eyes. Suspects outside the mod's code: map
  shaders without instancing support when `StereoMode` is single-pass instanced (they draw in one eye), post-processing
  auto exposure keeping a separate history per eye in multi-pass (only when `PostProcessing` is on; off by default),
  and per-eye water reflections or shadow cascades.
- **Rack**: on the 2026-10-16 evening run the gun's own part moved with the hand on 116 of 233 guns; 54 do not, 7 move
  an added knob (`Tasks.md` "For 246"). 27 guns with unreadable meshes needed an added knob (245); some still do.
- **Trigger finger** curls until it touches; there is no trigger point in the analysis, so it does not aim at the
  trigger blade (202). The finger also presses visibly on each shot and on a dry click.
- **Weapons in either hand**: passing the weapon to the other hand works (152 step one); holding a second weapon at the
  same time (step two) needs a second held weapon next to the game's single active weapon and is not started.
- Long-standing (README): night vision and camera effects not copied to the headset; third-person/death cameras turn
  only around the vertical axis; magenta materials replaced by approximations.

---

## 11. Open Questions & Next Steps

The current list is the top of `Tasks.md` ("Still to do", "In progress", "Code done, to check in the headset"). On
2026-10-16 the headset checks waiting were: rockets by hand on the 14 launchers (250), the pistol magazine push and
fades (240-242), launchers held still (203), the gloves with `Hands = WeaponArms` on one-arm models (251.1), long
launchers in the hand's solid (251.21) and the per-eye brightness on Island (225). The list below is from 2026-10-13.

1. Headset session on the Quest 3 to judge the new grips, rack grips (pistol slides, pumps), recoil strength and the
   equip flash in person.
2. Extend the rack-grip check to pistols and pump shotguns (the quick list's first 8 picture weapons are rifles).
3. Task 33 (measured performance pass) and task 37 (weapon's own arms with IK as an option) remain open.
4. Thumb wrap on rifles and the pulled trigger finger: a per-finger target that aims at the trigger blade
   specifically.
5. Spread the one-off shape choice over frames, or decide it at the menu warm-up.
6. Investigate the AT-1K analysis.
7. A second read-only swarm wave on the areas not reviewed this session (UI, input bridge, vehicles, analyzer).

---

## 12. Work Log (session of 2026-10-13)

1. **Hands** (task 86): diagnosed with new grip close-ups and per-weapon hand diagnostics; knuckle-line frame,
   `GunVolume`, palm settle, grip slide, joint-by-joint finger solve, trigger finger; throwables included.
2. **Improve 10** (task 75, rounds 1–10): shape prefit at warm-up, vertex buffer reuse, fire-mode variants share the
   parent's magazine, prepared drops, snap guard, optics never taken for magazines, 3000-point cap (reverted), forgiving
   pickups, asynchronous read-back, rendered-model hulls for unreadable weapons.
3. **Tasks 87–92**: seated/held magazine solids, H3VR kick and tactical reload checks, recoil one vs two hands, pouch
   only on a squeeze made at it, stow only when slow, throw velocity at release, drops ignore bodies + friction, held
   drop keeps the stand-in shape + magazine, glove hidden on bags.
4. **Swarm review** (3 read-only Haiku analysts, findings triaged): 8 fixes from the small-systems scan (holster rebuild
   every frame, pouch on the holster spot, readout/intro allocations, title height, damage-ring dip, pouch label, label
   caches), 16 fixes from the hands/reload reviews (listed in commit `62b44e3`), 7 findings rejected with reasons.
5. **Improve 20** (task 93, `Improve20.md`): best-of-both shapes, rifle thumb, pistol and left-handed coverage, held
   magazines once per frame, cheaper shape choice, rounder cuff, 3-view magazine solids, worst-hands list, grip-slide
   and solve reuse, bag/walker/trigger checks, magazine and bot-drop hulls from the rendered model, faster queries,
   report-only comparison out of play, magazines ignore bodies, third-person-only drop copies.
6. **Fixes from watching the tests**: two-handed pistols close on the gun hand; test harness (4 bots, invulnerable
   player, background running, squeeze edge, pouch-free round accounting, per-shot recoil, call-in grenades);
   akimbo pistols no longer drop their second gun (task 95); rack grips on worked parts (task 96); equip take-over before
   rendering (task 97).

Commits of the session: `b219cfc` … `6e063e8` (see `git log`).

## 13. Work Log (2026-10-16)

About 80 commits, from `10a2c2d` to `8e6eec8`; the lead on `tasks-98-113`, the helper on `helper-248-249` from the
evening on (merged twice the same night).

1. **Screen and feel** (Tasks 230, 237-239): the muzzle flash's hot core drawn in the world and hidden by the gun, recoil
   that is seen and felt (`ShotKick`), a whole-view flash now and then, aim steadied through magnifying scopes (a
   one-euro style filter on the gun's turn above 1.8x).
2. **Magazines** (240-242, 241 seating, 232): grip thickness where the hand holds it, pistol magazines by the base and
   pushed home with an opening hand, pose fades, the rail's damped draw-in, insert axes put back into the gun's side
   plane, the seating check measured from the well's mouth; skinned guns' second magazine bones hidden with the
   magazine.
3. **Rockets by hand** (231, 250): loaded at the front, taken only at the mouth, kept out of the launcher, sliding with
   the hand, the launcher's own rocket copied while it sits loaded, missile tubes handing out their projectile; the
   rocket film and checks in every weapons run.
4. **Racking** (233, 243, 245, 246): the racking hand rests on the receiver over an ejection port (recesses up to 3.4 cm
   closed over in the shape it closes on), pistol slides held overhand in the self-check, guns' own charging handles
   kept instead of added knobs where they can be found, the rack picture check (53 → 116 of 233 guns moving their own
   part).
5. **Weight** (203, 204, 208): the hand's acceleration through a dead band; the shoulder check read from where the hands
   hold the gun.
6. **Bug hunts and reviews**: an 85-agent review and regression guards (F6 retry, optics vs calibre names, kept-weapon
   cap, weight against the body's motion); 248 (30 performance findings) and 249 (22 bugs) by 5 Haiku finders each;
   251 (8 finders, 2 skeptics per finding: 20 confirmed, 3 plausible, 9 refuted). Each finding's result and reason is
   in `Tasks.md` "For 248", "For 249", "For 251". Notable fixes: the 24-entry shape cache with pooled read-back buffers,
   `Handgun` by slot, lens band threads at normal priority, kept weapons that are never silently deleted, the HUD
   camera's aspect from the main HUD, the stand-in never orphaned, sub weapons kicking as themselves, the flash read on
   the barrel that fired, a failed hand-solid measurement retried, invariant numbers in the film log.
7. **Helper's follow-ups** (215, 203, 202, 229): per-frame UI writes only on change (ammo counter, squad label, canvas
   scales, laser colour), the trigger press on a dry click, a melee weapon never taken for a kit.
8. **Work split** between the lead and the helper by files (see Decisions); the Boneworks study (244, read-only agents
   on its decompiled class structure; the game never launched).
