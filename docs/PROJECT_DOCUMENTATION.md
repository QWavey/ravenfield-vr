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
**Refreshed again 2026-10-22** (commit `ab52699`): shotgun shells by hand (loading ports, break-action chambers, the gun's
own shells; 3.7), the loading screen drawn by the compositor (3.9), the offline checks (3.10) and the work log (section 15).

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
- Fire mode on the gun hand's lower button (A): on Touch it switches on A's single (which SteamVR sends on the release,
  since A's long press is the `FireMode` action) with no further wait; slow motion is A pressed twice, and a semi / auto
  switch made by its first press is put back (`UndoSelectorToggle`). Other controllers wait 0.4 s, since there a double
  press opens the pause menu. A fire-mode variant the game switches to is ready after 0.05 s instead of its
  `changeFireModeTime` (`WeaponFireModeSwitchPatch`). Gun input (`GunInputBlocked`) is off in a menu, with the laser on a
  panel, and while the match is paused (dashboard, headset off).

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

**A lens worth repeating (2026-11-07):** a feature written from reading the code and never seen in a headset or a run is not done. Task 252 (w) added post-processing for the goggles, the README said it worked, and the mask was wrong for three weeks; a silent no-op has no log line and no failing test. Features with a mechanism in the game's data want a check in `AutoTest` (`GogglesCheck`) and a log line that says the step took.

**How the game's picture reaches the eyes (2026-11-07, Tasks 366).** The eyes are one camera of the mod's (`VRRig.Camera`); the game's cameras
are suppressed (`CameraSuppressor`: no stereo target, their `PostProcessLayer`s and image effects off). The game's look is not on its world
cameras. `PostProcessingManager` sits on a last camera of its own (`_Managers/Global Post-processing`) whose `finalLayer` sees layer 28: the
`Ingame` volume (grade and bloom, priority -1), the goggles' volume (`Night Vision` profile: ColorGrading, Bloom, Grain; global, priority 100,
switched by `NightVision.ToggleEnable` through its `activatedObject`) and a mod's volumes. `RegisterWorldCamera` gives each world camera a layer
that sees layer 29 alone, the AO volume. So anything that wants the game's look in the eyes takes `finalLayer`'s mask, never the reference
camera's: `VRRig.UpdatePostProcessing` adds a `PostProcessLayer` to the eye camera while the goggles are on or `PostProcessing` is set, with the
final mask (plus the world camera's for AO only with `PostProcessing`), and turns it off after. Without either no post-processing runs in the
eyes at all, which is the default and the reason VR costs what it does. The goggles are toggled by the off hand's grip at the side of the head
(`BodyZones.UpdateGestures`, `VRGameInput.GogglesGesturePressed`, `KeyBinds.Goggles`) or by a holster press of a toggled item
(`WeaponSystem`), and the flat tube vignette and fade are hidden (`NightVisionVignettePatch`, `NightVisionFadePatch`); `ComfortVignette` holds
a floor (`GogglesTube`) as the tube instead. Other things the flat game does on a camera of its own and the eyes must do themselves: the view model
out of the sun in shade (`FPLightIntensity`, here `VRRig.UpdateGunShade` by the sun's culling mask), the water's blue (`UnderwaterCamera.OnGUI`, here
`UnderwaterVeil`). `IRFlaresRenderer` draws for every camera, so its strobes need nothing. Anything in the game's `OnGUI`, `OnRenderImage`, `OnPreCull` or
`OnPostRender` is a place to look when something the flat game shows is missing in the headset.

**Seats, falls and vehicle feel (Tasks 383, 2026-11-11).** In a seat with a camera of the vehicle's own, the eyes are where that camera rests
in the vehicle, taken once as the seat is taken and carried with the seat (`VRRig.SeatEyes`): taken every frame, a turret's camera on the
turning gun swung the view around the pivot (the Blackhawk's minigun). A vehicle camera more than 1.5 m from the seat's eye point (0, 0.85,
0.2 in the seat, the game's own) is an outside chase view: the eyes are the seat's then, and the body is not drawn there (planes). Knocked over,
the view is anchored at the drawn head (the ragdoll's while it is drawn, so getting up does not jump), its own head shrunk away, the yaw only
(`FallInFirstPerson`). The flight stick reads the hand's tilt since the grab as well as its movement (25 degrees a full throw). Hits, jolts
over 25 m/s a second and a faint engine hum reach the hands (`Rig/VehicleFeel`). Seat switching: the left stick's click, the turn stick's
long press anywhere, its click where the seat has no second mounted weapon and no own gun.

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

**Contact (2026-10-23, after Boneworks' `PhysHand`).** The glove is not placed at the tracked hand and then pushed out of the
gun: it is swept there. `Rig/GloveSweep.cs` (pure, offline-tested) moves it from where it was drawn last frame (kept in the
gun's space, so the gun moving does not drag it through) toward the tracked hand in small steps, stops at the last clear
step and slides what is left along the gun's axes. Once the tracked hand is clear of the gun by 15 cm the glove goes back
to it (Boneworks' `CheckStuck`/`SetHand`). This fixed a fast swipe through a thin receiver (the old search started from
the hand's new place, already past the gun) and the double-magazine reload's promoted magazine hand (it had no keep-out).
A hand working a part (bolt, handle, slide, pump, feed cover, optic) keeps its glove on the part for as far as it holds it
(46 cm), the arm with it. Slamming a magazine into a locked gun at 0.9 m/s or more sends the slide home
(`MagSlapReleasesSlide`), and a weaker haptic no longer cuts a stronger one short (`VRInput.HapticAllowed`).

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
- Letting go under the ground: terrain and mesh ground are a surface, not a solid. A magazine or weapon let go below it is
  not pushed out by `ComputePenetration` or by PhysX and falls through the world. `PropPhysics.NothingUnder` (no level
  floor within 3 m below) plus `PropPhysics.BackToward` (box-cast from a point in the open, the player's eyes or a dying
  soldier's chest, to the thing; it is put just short of the first level surface when that surface is at most 0.8 m
  before it) put it back on top. `MagazineProp.OutOfTheLevel` also uses `BackToward` with no limit when the push out of
  a wall would be over 25 cm (it was skipped and PhysX flung the magazine). `WeaponDrops.Spawn` takes the open point
  (`open:`), `DropHeld` uses the eyes.
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
  The keep-out samples a held magazine in rows along it (9, at most 2 cm apart on a long one, up to 16), nine points a row
  (middle, edge middles, corners), a long gun's magazine to 95% of its box and a pistol's to its edges; it advances in
  5 mm steps toward the hand and slides along the gun's axes when blocked. Measured by the autotest's keep-out sweep
  (`vrshots.py --weapon NAME`: a dense 5 mm probe of the magazine's box counts frames with points in the gun's body):
  with five points a row at 80% (before 2026-10-21) a PATRIOT GL magazine put up to 222 probe points in the gun sideways,
  after the change none.
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

**Shotgun shells by hand (2026-10-22, task 331, `5865c19` and after).** The analysis's "magazine" of a shotgun is the
reload animation's shell, and its seat had been where that shell waits at rest, out of the flat view (46 cm behind the
R870's grip, 29 cm under the bore): no shotgun could be loaded by hand, while the autotest seated shells itself and read
8/8. Now (`WeaponAnalyzer.AnalyzeShells`, run after the hinge):
- **Tube guns** (`ShellPort`): the shell goes in at the loading port, where the reload pushes its shell nearest the bore
  under it (ahead of the grip, short of the muzzle, while drawn), seated as it lies there (`PoseSampler.ReloadRotation`
  keeps every part's rotation per reload step) and going in the way its path went over its last 3 cm. Measured on all
  17 shotguns: 17-30 cm ahead of the grip, 1.6-4.7 cm under the bore, 23-52 degrees up from the bore (the R870's matches
  its `loading_gate`). No such point: half way from the grip to the forend, 3 cm under the bore (`EstimatedPort`; also a
  tube gun without a shell part). The reload's shell itself is a spare part, hidden while held (it floated under the
  R870 and above the mw-m870's stock).
- **Break actions** (`ChamberShells`): every chamber's shell at rest is found (the magazine's and those beside it across
  the bore, of its size, level with it), the seat swings with the barrels (`SeatOnHinge`, whatever the shells hang
  from: the DOUBLE-BARREL's hang off the root) and a shell goes in along the bore. `HeldWeapon.ApplyChamberShells` draws a
  chamber's shell only while it holds one (`ChamberFull`, kept in step with live plus fired-not-out) and carries
  root-hung shells with the barrels; a shell goes into the empty chamber it is held by (`NearestEmptyChamber`). Opening
  throws the live shells out with the fired ones, back into the reserve (`AmmoState.TakeLiveShells`; a revolver keeps
  its live rounds).
- **One shell per pouch draw** (`ShellChunk`): the flat reload's largest chunk gave a double barrel authored to load two
  nothing with one barrel empty.
- **The gun's own shell** in the hand, the pouch and thrown out (`MagazineProp.CopiesModel`, `Casings.OwnShellOf`) when
  its part is a shell's size (`WeaponAnalyzer.ShellSized`: 4-13 cm long, 1.5 cm thick or more, under half as thick as
  long); slug guns throw shells too. 16 of the 17 shotguns hold their own (the M500 (Bot Friendly) has no shell part).
- The autotest pushes every shell in along its line with the simulated hand (`LoadShellByHand`), reports the port's
  place, an opened break action's shells out and its chamber shells in their chambers, and pictures a shell half in.

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

- **Sounds by name** (`CueNames` in `WeaponAnalyzer.Sounds.cs`): a clip goes to an action by what the mod called it before when it
  plays; `CueNames.Allows` keeps a named clip off another action's slot, and `FillFromBank` gives an action with no cue a clip of the
  gun's own bank. Long clips are cut at the reload's actions, the cover's lift and shut among them.
- **Which magazines fit** (`WeaponRigData.TakesMagazineOf`): its own gun, or a gun with the same magazine part of the same size within
  5 mm (the HK416-A to -E). `ReloadInteractions.Ours` is that for any prop: a sister's rounds count as this gun's when stowed. Another
  gun's magazine pushed at the well says `WRONG MAG`.
- **A rest** (`HeldWeapon.Rested`): a long gun about level with a level surface just under its bipod's feet (`FindBipod`, by name) or
  under the support hand kicks half as hard. One ray per shot, nothing per frame.
- **The trigger** (`FindTrigger`, `ApplyTrigger`): the part by name near the grip, moved by the finger: the fire clip's own motion, else
  14 degrees about its origin, else a 6 mm slide where its origin is not on the part. `TickTriggerReset` is a tick as a semi-automatic
  gun's trigger is let forward.
- **Made stand-ins measure for themselves.** A magazine made because the gun's own part is parked keeps its shape under its own key
  (`OwnCopy`, `ShapeKey`): under the type's key its cube was every later magazine's grip for the session.

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
  measured blade. A melee weapon hits only what its blade is swung through: the game's own trigger attack (along the head's
  facing) is suppressed while one is held (`WeaponSystem.Update`), and the blade's sweep switches the player's hitboxes off
  around the game's `SphereCast` only when a cheap cast of the path finds something but the player (`PathHasTarget`). A
  gun swung into an enemy hits with points every 10 cm (4 to 12) swept from butt to muzzle (`MeleeHandler.Bash`), after one
  overlap round the whole gun says a soldier is near at all (`SoldierNear`), and since 2026-10-20 a magazine, shell or
  rocket in a hand, a grenade held to throw or an empty fist hits too (`HandBlows`: the palm's path, at least `BlowSpeed`
  against the body, credited to the gun in use; task 306). A hand-sized throwable is held
  by its drawn middle (`HeldWeapon.CentreGrenadeGrip`, for either hand), and that middle rests on the palm
  (`HeldWeapon.HoldFrame`: from the fist's hollow, the controller's grip origin on the palm's skin, out along the palm's
  normal by the skin it covers and its own half thickness, as a magazine rests), not on the palm centre where a gun's
  grip goes (2.6 cm inside the hand: a frag had 14 of 72 glove points in it).
- **Soldiers** (`Weapons/BodyGrab.cs`, tasks 336-338): a free, empty hand gripping near a soldier knocks him over
  (`Actor.FallOver`) and holds his nearest ragdoll bone through a locked `ConfigurableJoint` to a kinematic anchor that
  follows the palm (`MovePosition`, so the physics pulls the body); a hand jump over 25 cm (a snap turn) carries the whole
  body instead (`CarryBody`, once a frame for a body in both hands). While held and for 3 s after the throw the soldier is
  "armed": `BodyImpacts` on each bone (removed once disarmed) hurts him and whoever he hits by the impact speed squared
  (Boneworks' `Enemy_Health`), caps bone speed at 25 m/s and depenetration at 5, and his joint motors are off
  (`ActiveRaggy.SetDrive(0, 1)`; the living 700-1000 kept him stiff in his falling pose; while held, again after the game's
  death drive of 50). The game's own drive is put back on disarm, the death drive for a dead body. Tried and dropped
  (each blew a limb up in the soldier check): the death drive of 50 on a living soldier, motors off again on a body that
  died in flight, more solver passes. The held gun's stand-in ignores the held body (`GunCollider.IgnoreCollisionsWith`).
  A throw sets
  one velocity on every bone (at most 22 m/s). Held soldiers are a human shield (`HumanShieldPatch` on
  `Projectile.CanHitHitbox`: bullets hit them whoever fired). An enemy's head taken from behind is a neck hold (AI paused);
  a 70 degree turn of the wrist against the body kills him and leaves the ragdoll in the hand. A live grenade let go slowly
  on a held body sticks to the bone (`StrappedGrenade`); its explosion kills the carrier (enemies only) and deals the
  grenade's own damage to everyone else. Checked by `vrshots --fast --grabcheck` (nine steps with pictures).
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

**The loading screen** (`LoadingView`, 2026-10-22): while a map loads the world is blacked out and the loading scene's
camera draws into a picture shown 2.5 m in front of you. The game freezes once for about two seconds while the map's
scene comes in (2,076 and 2,369 ms measured; the mod's own work on that screen is at most about 50 ms a frame), and the
compositor, given no frames, faded to its own dark blue-grey surroundings and back (the user's "black / with blue shades
glitching"). The picture is now SteamVR's own overlay (placed where the screen stands; the rig's root is the tracking
origin, `OpenVRMath.FromUnity`) and the compositor's skybox is black meanwhile (`SetSkyboxOverride`), as Valve's
SteamVR_LoadLevel does; the game's quad stands in where the compositor cannot take the picture. At its end the view logs
its frames: time up, the longest frame, frames over 100 and 500 ms, and the drop models' time.

### 3.10 Test tooling (`Core/AutoTest.cs`, `Weapons/WeaponSelfTest.cs`, `Core/Shots.cs`, `Core/PerfProbe.cs`, `Rig/HandPreview.cs`)

- **Since 2026-10-21**: the autotest first prepares the weapons it will test as the menu's warm-up does
  (`WeaponWarmup.Prepare`, one a frame; the run starts a map at once, so the warm-up never ran) and starts the
  profiler after it, so its numbers are a match after the menu. The profiler names what the test was doing
  (`PerfProbe.SetContext`: the weapon under test and the seconds since it began) at each section's slowest call and
  at the six slowest frames ("slowest at PKP +0.3 s"); magazine props are a section of their own ("magazine prop
  made"). New checks: `BuriedMagazineCheck` (a magazine let go 15 and 40 cm under the ground and a weapon 30 cm under
  it, with a no-fix control drop), `SecondWeaponFloatCheck` (the gun in either hand, a second weapon from its holster
  and from the ground, its drawn model's distance from the hand each frame, the glove's state and pictures), and the
  gentle set-down check now runs before the drop checks (after them the gun was in its holster and it was skipped).

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

### 3.11 Sandbox (`Sandbox/Sandbox.cs`, `Sandbox/SandboxPanel.cs`, `Sandbox/SandboxTool.cs`, `Sandbox/SandboxCheck.cs`; Tasks 374)

Soldiers, vehicles and weapons made on the spot in any match, for testing. Opened from the VR settings' footer ("Sandbox").

- **Core (`Sandbox`).** The game's own ways throughout: `ActorManager.CreateAIActor(team)` + `Actor.SpawnAt(pos, rot, loadout)` for a
  soldier (a squad of his own when he has none: the AI reads its squad every frame), `VehicleSpawner.SpawnVehicleAt` for a vehicle
  (`RollPrefab` first: a map's side may have none of a kind), `Actor.EquipNewWeaponEntry` for a weapon into the player's hands. What is
  spawned fights, dies and is counted as any other soldier; once dead it leaves the sandbox's lists and the match respawns it as its own.
- **Behaviours.** Normal; Neutral (never fires); Stand still (no step, fights from there); Frozen (`Actor.Freeze`, no step, no shot);
  Follow me (a friendly put into the player's squad). Two Harmony postfixes on the AI's answers, `AiActorController.WantsToFire` and
  `.Velocity`, put on at the first soldier that needs them (`Harmony(PluginInfo.Guid + ".sandbox")`); their bodies are one flag test
  while no such soldier lives. Any soldier of the match can be given a behaviour (`SetBehaviour`), not only the sandbox's.
- **Weapons:** the side's own (null loadout), random (one usable-by-AI entry per slot), none (an empty `LoadoutSet`: `Actor.SpawnWeapon`
  takes a null entry), mine (the player's five slots). **Health:** normal, no damage (`isInvulnerable`), one hit (health 1).
- **Quiet battlefield.** The match's own soldiers are taken off (`KillSilently`) and a prefix on `GameModeBase.SpawnActorListDeferred`
  empties each spawn wave's list of AI (the list is a fresh one from `SpawnDeadActorsOfTeam`); switched off, the next wave brings them back.
- **A sandbox soldier's life.** `Made` holds every soldier the sandbox made in this match; the wave filter keeps them out of every spawn
  wave (so the first sentence above no longer holds: a dead one is NOT the match's again). Dead, he is noted in `DiedAt`; after
  `BodySeconds` (panel: 5 s to 5 min) `PutAway` calls `Actor.Deactivate` (hidden, frozen, hitboxes off) and puts him in `Pool`;
  `TakeFromPool(team)` gives him back (`Activate`) for the next spawn of that side. Why a pool and not `Destroy`: `ActorManager.Register`
  numbers each actor (`actorIndex`, `teamActorIndex`) into tables that only grow (`actorsCanSeeEachOther`, `actorData`), and nothing in
  the game takes an actor out of them.
- **The spawn gun** is primitives parented to one object placed at the free hand's `TrackedPointer` each frame before render; its lamp
  material's colour is the mode's; the beam starts at the lamp. `VRHand.Scripted` + `VRInput.SimulatedTrigger` drive it in the check.
- **Removing a vehicle:** `ActorManager.DropVehicle` before `Destroy`. A destroyed vehicle left in the game's lists threw every frame
  (`VehicleParticleManager.UpdateMonitoredVehicles`, the minimap's "Could not blit vehicle"; found by the sandbox check, 2026-11-10).
- **The tool (`SandboxTool`).** While taken, the free hand (role Free only) points a beam; a ring lies where it ends; a pull of that
  hand's trigger (0.7 on, 0.35 off, the game's Use consumed) spawns there facing the player. Pointed at a soldier (a `Hitbox` whose
  parent is an AI actor) the pull gives him the panel's behaviour. With the tool taken, the menu button opens the sandbox panel.
- **The panels' base (`UI/ModPanel.cs`).** Rows built top down and rebuilt whole when marked changed, never under a held press
  (the pressed button would be gone at release). `SandboxPanel` and `MultiplayerPanel` derive from it. Note for new files outside
  `RavenfieldVR.UI`: the game has a global class `ModPanel`; write `UI.ModPanel`.
- **Check:** `python -I tools/vrshots.py --sandboxcheck --fast` (in the game, about a minute): every behaviour measured (metres moved,
  shots fired, the player's health), weapons compared by name, health by damage dealt, the quiet field over two spawn waves, a vehicle
  made and removed, a weapon given, the panel's four pages built, the clearing.

**Added 2026-11-10 late (Tasks 376; built, not yet run in the game):**

- **The mode.** `Sandbox.InitMode` (from `UISystem`'s constructor) patches `GameManager.AvailableGameModes` (postfix `AddMode`: a `ScriptableObject.Instantiate` copy of Point Match's `GameModeInfo`, its `name` field set to "Sandbox", `sourceMod` copied by hand because it is not serialized), `GameManager.GetBuiltInGameModeByName` (postfix `FindMode`: saved configurations look a mode up by name), `GameManager.StartLevel` (prefix `OnStartLevel`: `countTeam0/1 = 0`; a restart passes no parameters, so the game's held ones are taken) and `VictoryUi.EndGame` (prefix `HoldEnd`). `Sandbox.InMode` compares `GameManager.instance.gameModeParameters.gameMode` with the copy. In the mode `Sandbox.Update` arms the tool once per match. The map's `GameModeLevelLayout` is matched by the mode's name, so a map's Point Match layout is not used: the default owners are.
- **Pause.** `Sandbox.Paused` makes the two behaviour postfixes (`WantsToFire`, `Velocity`) hold every AI soldier; reset at a scene change.
- **Flying.** `Sandbox.FlyTick(head)` (from `SandboxTool.Update`, every frame): while flying the game's `FirstPersonController` is disabled (no gravity, no walking) and the `CharacterController` is moved by `VRGameInput.MoveStick` along the head's forward and its level right, 9 m/s. `_flyHolds` remembers that the controller is off, so death, a seat or turning it off give it back at once.
- **The tool's hand and model.** `SandboxTool.ToolHand`: the dominant hand while it is tracked, free (or pointing) and no weapon is held, else the off hand; `_side` carries it to `BeforeRender`. `TryModel` (once, at the first showing in a match): the first vanilla secondary whose third-person model lies along z and is 8 to 45 cm long is copied (scripts, colliders, audio, lights off), posed as it sits in the weapon's own frame (`Inverse(root.rotation) * source.rotation`: the game hangs its muzzles on that model, so the weapon's forward is its barrel), its box taken from its mesh bounds because a hidden renderer keeps none.
- **Pictures.** The Weapons page builds tiles from `WeaponManager.WeaponEntry.uiSprite` (an `Image` with `preserveAspect` over the button, the name under it). Vehicles have no sprite in the game's data.
- **A log line per pull and per soldier** (`Sandbox tool (...)`, `Sandbox: a soldier made at ...`): the first report of spawning that did nothing on a modded map left no trace.

### 3.12 Multiplayer (`Net/*`, `UI/MultiplayerPanel.cs`; Tasks 373)

Opened from the VR settings' footer ("Multiplayer"): host over Steam (public or friends) or on a UDP port, Steam's list of open games,
join from the list, by a friend's invitation, or by address.

- **Layers.** `NetWire` (writer/reader: varints, floats, 4-byte rotations, strings) -> `INetTransport` (`SteamTransport`:
  SteamNetworkingMessages on channel 7133, relayed by Valve, reliable kept by Steam; `UdpTransport` with its own `ReliableChannel`;
  `LoopbackNetwork` for tests) -> `NetSession` (host = player 0, a star: clients talk to the host, who relays; hello/welcome/refuse,
  pings, players joined and left; message kinds from 16 on are the game's) -> `Multiplayer` (what the panel calls: `HostSteam`,
  `HostLan`, `JoinLobby`, `JoinAddress`, `Leave`, `RefreshLobbies`) -> `NetGame` (the match).
- **No fixed game build.** RavenM compared the game's build with a constant and refused every other build, which is what broke it on
  each update. Here the lobby carries the host's build number and `NetSession.ProtocolVersion`; the list shows a game of another build
  or protocol as not joinable with the reason, and the session's hello compares the two players' builds with each other.
- **Lobbies (`SteamLobbies`).** Lobby data: `rfvr=1` (the list's filter), `name`, `map`, `mode`, `players`, `proto`, `build`, `mod`,
  `match`. Only lobby members may open a line (`SteamTransport.Accept`). `GameLobbyJoinRequested_t` (invitation, "Join game" in
  Steam) joins at once. The game runs Steam's callbacks itself (`SteamworksWrapper`); the Steamworks.NET it ships has all of this.
- **The match (`NetGame`).** Each PC owns soldiers: its player; the host also every soldier of the match. An owner sends
  `ActorInfo` (reliable: id, name, weapon in hand) when new or changed and `ActorStates` (unreliable, 15 Hz, 19 bytes a soldier, 60 to
  a packet: flags, position, flat velocity, yaw, pitch). Every other PC shows each as a real soldier of the game (a stand-in:
  `CreateAIActor` + `SpawnAt`) steered by postfixes on the AI's answers (`Velocity`: toward the told position; `FacingDirection`;
  `WantsToFire`; `Crouch`; `Prone`), put where told when more than 4 m off. A PC that joined starts the match with no soldiers of
  its own (`countTeam0/1 = 0`); the spawn wave never respawns a stand-in (prefix on `SpawnActorListDeferred`).
- **Hits: the shooter's PC decides.** A prefix on `Actor.Damage`: a hit on a stand-in by one of my soldiers is sent to its owner
  (`Hit`, reliable) and not taken here; a hit by a stand-in on one of mine is dropped (its owner's PC sends what it hit there); a
  received `Hit` is applied with the shooter's stand-in as the source, so the kill is his on every PC.
- **Match start.** A postfix on `GameManager.StartLevel` on the host sends map (built-in scene name, or a workshop map's file name),
  mode, night, team, respawn time and length; clients find the map in `ModManager.AllBuiltInMaps/AllCustomMaps` and start it; a
  missing map is said in the headset. Sent again to whoever joins during the match.
- **On foot for now.** In a session every PC takes the map's vehicles off and stops their spawners (`NetGame.NoVehicles`: at the match's
  start, when a session begins in a running match, and every 5 s); the fixed guns stay. **A PC that loaded late** has state for soldiers
  it has no info for (a new scene forgets the old one's) and asks the owner (`NeedInfo`), who tells all of his again. **Shots** are told
  by the controller's answer or the gun's own last shot (`lastFiredTimestamp` within 0.2 s: the VR hand fires the gun itself). **A
  stand-in knocked over** locally stands up where its owner has it after 2.5 s (`InstantGetUpAtPosition`). **A player's name** is a
  `TextMesh` over his stand-in (id 0 of each owner), scaled with the distance. Note: `ActorManager.instance` is the same object in
  every match; a new match is told by its new player actor.
- **Start choice and lobby (2026-11-10 evening).** `UI/StartChoicePanel.cs` opens once a game start over the main menu (`MainMenu.instance`
  there, a menu canvas visible, not in a test run); while it is open it is the laser's only target. A joined player outside a match
  (`following` in `UISystem.Update`): `CanvasManager.HideMenus` disables the game's menu canvases (given back when it ends) and leaves
  them out of the laser's targets, and the multiplayer panel is reopened whenever it is shut. The host's `NetGame.TellLobby` reads
  `InstantActionConfigMenu.instance` (selected map and mode, the two soldier counts, `PreviewTerrain.isNight`, respawn time) twice a
  second and sends `LobbyState` (kind 21: up to 12 lines of text) when the text changed or someone joined.
- **One speed.** In a session `NetGame.Update` undoes any time scale but 1 (`GameManager.UnpauseGame` + `SetTimeScale(1)`): the game's slow
  motion, its pause menu's pause, the mod's own dashboard pause.
- **Loadout.** `ActorInfo` carries, after the weapon in the hands, the slot in the hands and the five slots' entry names; the stand-in
  is spawned with all of them, changed slots are re-equipped, and `SwitchWeapon(slot)` is called when its hands hold another (checked
  twice a second: its own AI picks weapons too). An info without that tail (older check code) still reads.
- **Checks:** `vrshots --lobbycheck` (main menu: choice, panel, following a host made in-process, menus hidden and back);
  `--netcheck` also makes a hidden Steam lobby (`k_ELobbyTypePrivate`), reads its data back and asks for the list.
- **VR presence (`Net/NetAvatar.cs`).** `VrPose` (kind 23, unreliable, 30 Hz, about 45 bytes): flags (gun, which hands tracked, gun in the
  left hand, magazine in the free hand), head forward, both palms and the gun's muzzle (place and turn), all relative to the player's
  own soldier's place. Receiving side, for a player's stand-in only: a Harmony prefix on `ActorIk.OnAnimatorIK` (the game's humanoid
  arm IK: `handTargetL/R`, `SetHandIkEnabled`, `aimPoint`) puts our own target transforms in; the gun's model
  (`Weapon.thirdPersonTransform`, the muzzles are its children on an AI's weapon) is set before render so that its muzzle is on the
  told one (`model = toldMuzzle * inverse(muzzleInModel)`), its local pose given back when no pose is told. The gun hand's target is
  the hand's place on the gun at rest (the bone against the model at its original local pose, taken ONCE: taken each frame it reads
  last frame's IK result and follows itself), carried to the gun's told pose, so the wrist is right without the glove's axes. The
  other hand keeps the game's own left-hand target (a child of the gun's model) while the player's free hand is within 22 cm of it.
  A player's stand-in is turned to his head's direction each frame (the game turns a soldier only into his walk). Poses are eased
  (`1 - exp(-25 dt)`) between tellings. A test pose must be one an arm can take: the report line gives the grip's distance from the
  shoulder and the arm's length.
- **Knock-downs and the match's state.** `Knock` (22): a postfix on `Actor.KnockOver` tells a stand-in's owner, who knocks his own
  soldier over with the same force. `MatchState` (24): the host's flag owners (`SpawnPoint.owner`, applied with `SetOwner`) and the
  end (`VictoryUi.EndGame` is held back by a prefix on a PC that joined and called with the host's winner).
- **Vehicles (1.17.10, `Net/NetVehicles.cs`, kinds 31-37; Tasks 383.11):** the host keeps his vehicles and spawners and numbers each (turrets too);
  a joined PC takes off its own vehicles, stops its spawners, and makes a copy of each of the host's from its own prefab of that name
  (`PrefabOf`: every loaded `Vehicle` prefab and the scene's spawners' prefabs; a missing one is said once). A turret is matched to this PC's
  own turret of that name within 2.5 m instead (the map's or its spawner's), made only after three tellings with none. ONE owner a vehicle:
  the host, or the PC whose player sits in its driver's seat (seat 0); a joined PC takes it only from the host and gives it back on leaving
  the seat; the host takes back one nobody of the owner's drives for 5 s (unless the owner's word says he drives it). The owner tells place,
  turn, speeds, health, burning and death 15 times a second (3 when idle); elsewhere its rigidbody is kinematic and follows (eased,
  extrapolated 0.25 s at most, put there past 12 m), and its cached speeds are the told ones (wheels, sound). Seats: each PC tells where its
  own soldiers sit (`VehicleSeat`, on change and every 3 s); a stand-in enters or leaves a shared vehicle's seat only by that word
  (prefixes on `Actor.EnterSeat`/`LeaveSeat`), the lower player number keeps a seat two claim, a refused seat is tried again every 2 s; this
  PC's player cannot take a seat another PC's soldier holds. Hits: `Vehicle.Damage` on a vehicle not owned here is sent to its owner with
  the shooter's PC (passed on once if the vehicle changed hands meanwhile); a stand-in's own hits are dropped (its PC sends them); the owner
  tells the shooter's PC of a disable or kill (`VehicleKilled`, the game's `OnGotVehicleKill`). A destroyed vehicle dies on every PC by the
  owner's word. The self-check (`--netcheck`, joining side) tells a vehicle, moves it and takes it away.
- **Scoreboard (kind 39):** each PC tells its own soldiers' kills, deaths, flags and vehicles (on change every 2 s, all every 10 s) and the others set them on the stand-ins' rows; a kill credited from another PC (`KilledBy`) runs the game's `OnGotKill` on the player (the KILLED line and his own row). Weapon keys are kept per entry (`KeyOfEntry`): built per soldier each telling, they were the multiplayer's main garbage.
- **Not carried yet:** a soldier knocked over or held (ragdolls are each PC's own), a mutator's own objects, a dud grenade (pin in).
- **Checks:** offline `tools/tests/net-tests.ps1` (wire, reliability on a bad line, session over memory and UDP loopback; in
  `run-tests.ps1`); in the game `python -I tools/vrshots.py --netcheck --fast`: the game hosts on UDP port 7141 and a second player made
  in the same process joins and sends what a real one does (join, stand-in made and following, a hit passed out, a hit taken, death and
  return, the host's soldiers told, leaving).

---

**The mods' own files (`tools/bundles/`).** The analyser's report only shows what its sampler saw, so what it missed cannot be read there.
`clip_motion.py` decodes a mod's clip (streamed, dense and constant curves, bound to parts by the crc of their path): what it really turns and
moves, and a part's real geometry. `sound_banks.py` reads every SoundBank raw (mods' bundles carry no type trees) with its clips' names and
lengths. They found the QJB201's real cover, the AS Val's bolt, the MK46's belt and the sounds on the wrong action (Tasks 364, 365). First thing
to run when a gun misbehaves. UnityPy condition modes in a controller: If 1, IfNot 2, Greater 3, Less 4, Equals 6, NotEqual 7.

### 3.13 The chamber and the slide's catch (`Weapons/AmmoState.cs`, `Weapons/ReloadInteractions.Slide.cs`, `Weapons/HandLoading.cs`; Tasks 381)

The states a self-loading gun's slide can be in, and what moves it between them (a bolt or pump worked by hand has `BoltOpen` in place of the catch):

| State | In `AmmoState` | Trigger | Counter |
|---|---|---|---|
| Shut on a round | `Chambered`, not `SlideLocked` | fires | `N+1` |
| Caught back | `SlideLocked` (the game is told 0, the animator 0) | click; with `TriggerReleasesSlide` it lets the slide go when there is a round to shut on | `N OPEN` |
| Shut on nothing | `HomeOnEmpty`: not `SlideLocked`, not `Chambered`, on a gun that locks open on empty | click | `EMPTY`, or `N RACK` with rounds in the magazine |

- **Caught back** comes from the last round fired or a pull let go at the back, with nothing to chamber AND something to hold the catch up (`CatchHolds`: a magazine in, or a gun fed from inside or from a belt). With the magazine out the slide runs home: shut on nothing.
- **Shut on nothing** comes from the hand bringing the caught slide forward (ridden home, or let go in the front half of its way) and from its release with an empty magazine (`RackForward(home: true)`). The animator is told 1 there (`AnimatorRoundsHome`): many pistols draw the slide back at 0.
- **A round by hand** (`ChamberByHand`) goes into `ChamberOpenEmpty`: the slide caught back or a bolt left open, no round and no fired case in it. The next `RackForward` shuts on it and takes none from the magazine. The round comes from the reserve (the free hand's trigger held as its grip squeezes at the pouch: `TakeFromPouch`), or off the ground (a live round racked out with `LoseRoundOnRack`); it is a `Casings.Case` in the fingers, followed and let go by `HandLoading.BeforeRender`, put in by `HandLoading.TryChamber` within 6 cm of the ejection port (`Casings.TryPort`).
- The rounds' fuzz (`tools/tests/AmmoFuzz.cs`) plays both as random actions among the others: nothing made or lost, no catch with the magazine out, no shot from a gun shut on nothing.

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
| `src/Weapons/HeldWeapon.cs` (+ `.Shape.cs`, `.Parts.cs`) | weapon in hand, recoil; the measured shape and solids for hands; parts posed on the gun | central weapon logic |
| `src/Weapons/ReloadInteractions.cs` (+ `.Magazines`, `.Parts`, `.Slide`, `.SelfChecks`) | manual reloads, H3VR kick/palming, pouch, rack grips; the in-game self-checks apart | reload feel |
| `src/Weapons/WeaponAnalyzer.cs` (+ `.HandsAndSights`, `.Action`, `.Magazine`, `.Sounds`, `.Helpers`), `PoseSampler.cs` | rig analysis by sampled animation | works for any mod |
| `src/Weapons/ShapeFit.cs`, `GunCollider.cs` | convex physics shapes, prop physics, body ignoring | "only the model is the hitbox" |
| `src/Weapons/MagazineProp.cs` (+ `.Look`, `.Holding`) | physical magazines, shells, rockets | reload props |
| `src/Weapons/WeaponDrops.cs` (+ `.Spawning`, `.Pickup`, `.OwnWeapon`) | dropped weapons, pickup, return, warm-up prep | drops |
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
`-rfvr-dumprig=<name>`, `-rfvr-mods=<tokens>` (the fast test mode below).

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

`python tools/vrshots.py --autotest` does all of this (backup, null driver, relaunch, hash-checked restore) and copies the
report to `reports/shots/autotest/` (the run before to `autotest-previous/`); `--no-build --weapon "=NAME" ...` runs every
check on just those weapons into `reports/shots/weapon/`. `python tools/metric_diff.py [autotest|weapon]` then lists the
frame times, each mod section's average and maximum, and the measurements that moved against the run before.

**Fast test mode (2026-10-24).** The game loads every subscribed mod before anything else (the user's 89 mods, 729 MB: 45-49 s
in Player.log's "Mod Content loaded in"), then their mutators in every match. `-rfvr-mods=TOKENS` (src/Core/TestHost.cs)
makes it load only the mods named: a workshop id or a word of the mod's title, comma separated; `none` loads only the
game's own content (its weapons and built-in maps such as island2023). After every full load the mod writes which mod each
weapon comes from to `BepInEx/cache/RavenfieldVR-weapon-mods.tsv`. `python tools/vrshots.py --fast ...` uses it: `--weapon`
and `--film` runs load only their guns' mods (weapon ids are turned into exact names first, as the list's order changes),
the autotest, UI, bench and flash-check runs load none; a filter it cannot resolve offline loads every mod, as before.
`--mods TOKENS` names the mods by hand, `--map NAME` picks another built-in map. It is still the game's own Unity player
(the mod needs Unity's engine, and no Unity editor is installed to build another), but without the minute of mod loading.
Measured 2026-10-24 on the same RK-44 deep run: every mod 226 s (89 mods loaded in 73.7 s), `--fast` 178 s (no mod,
the game's content step 7.1 s), the same results and pictures (reports/shots/fast-baseline, fast-mode).

Checks without the game (2026-10-20, issue #18): `powershell -File tools/tests/run-tests.ps1` compiles
`tools/tests/OfflineTests.cs` with the installer's sources and `BindingsWriter` and runs 25 checks: the installer's path
containment (`Installer.Inside`: zip entries and manifest lines stay inside the game folder), the Steam `localconfig.vdf`
reader (a truncated file is a `FormatException`), the left-handed bindings and their descriptions, and the Steam Play-list
entry changed and changed back on a copy of `appinfo.vdf`. Exit code 0 when all pass.
As of 2026-10-22 it runs 90 C# checks (the launcher's config edits and remembered choice, the setup's install and
uninstall on a made-up game and Steam, zip-slip, a damaged `appinfo.vdf`, JSON nesting, what counts as a shell, the pose
conversion to OpenVR, the graphics presets, the input actions against the manifest) and the Python ones:
`failure_phrases_test.py` (every capital phrase a self-check writes, nested strings included, counts as a problem in the
autotest's `Failure` pattern: 201 phrases), `uninstall_ps1_test.py`, `install_ps1_test.py`, `bindings_coverage_test.py`,
the vrshots restore test, the stacked-docs and unused-private guards and the magazine-line table check.

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
| Magazine let go 15 / 40 cm under the ground (2026-10-21, `BuriedMagazineCheck`) | fell through, 14.8 m/s, 11 m under | on the surface, 1.6 m/s, still |

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
- **Weapons in either hand**: passing the weapon to the other hand works (152 step one), and the free hand holds a second
  weapon of another kind beside it (`SecondWeapon`: from its holster or picked up from the ground; fired with that hand's
  trigger, reloaded by a timer, put back when let go). Its glove follows its kick, its draw clip is skipped as the gun
  hand's is, and its shape is measured from the first frame in the hand (2026-10-21: before, the measurement waited for
  the game's draw time and failed, and the glove had nothing to close on while the weapon hung beside it).
- Long-standing (README): camera effects other than the game's post-processing are not copied to the headset; third-person/death
  cameras turn only around the vertical axis; magenta materials replaced by approximations.

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

## 14. Work Log (2026-10-21)

A work-until-limit run (session 85 / weekly 30), the game started for null-driver tests while the user was away.

1. **Bug hunts** by read-only Haiku finders, each finding verified against the code before a fix: weapon pickups and
   drops, magazine prop physics, the rig and locomotion, throwables, melee and the second weapon, vehicles and the
   cockpit, the VR lifecycle. Logged in `Tasks.md` 326.
2. **Let go under the ground** (`PropPhysics.NothingUnder` / `BackToward`): magazines and weapons let go below terrain
   or mesh ground fell through the world (14.8 m/s, 11 m under); they are put back on the surface along the line from
   the eyes (1.3 m/s, lying still). Measured by `BuriedMagazineCheck` with a no-fix control drop.
3. **Fire mode without the wait** (user): on Touch the gun hand's A switches semi / auto at once (a slow-motion double
   press puts it back), and the game's fire-mode variant switch is ready after 0.05 s instead of its 0.3 s+.
4. **The second object in the other hand** (user: "sometimes isnt in the hand and floats in the air"): measured with
   `SecondWeaponFloatCheck` (the gun in either hand, a pistol from the holster and from the ground, each frame and in
   pictures). The weapon was in the hand's place, the glove was not on it: its shape was measured only after the game's
   draw time (the measurement failed, the glove had nothing to close on), and the glove grasped beside its grip. It is
   now measured from the first frame, the glove goes onto the weapon's own grip pose and follows its kick.
5. **Paused means paused**: throws, melee, the free hand's gun, the trigger, holster grabs, the wheel and flight stick,
   a door grab and the crouch timer no longer act through a dashboard or headset-off pause, and slow motion survives it.
6. **Smaller fixes**: a controller losing tracking keeps its tip and grip offsets (no step of the held gun), death lets
   go of your own weapons on the ground, the drop cap takes the soldiers' first, deep-in-wall magazines are moved out,
   setting ranges for drop lifetimes and caps, VR-restart leaks in the vignette and damage veil.
7. **Tooling**: the profiler names the weapon under test at each section's slowest call and the six slowest frames;
   35 review items already fixed in the code were ticked in `Tasks.md`. The autotest prepares its weapons as the menu
   does before measuring; profiler sections for magazine props, shape fits, shape measurings and bolt cuts.
8. **Input**: no re-press of a still-held button after an input outage, a trigger held into a hand swap needs a new pull,
   the script keys still update if the input bridge throws, sprint is cleared by a menu or death, the any-hand slot comes
   from the two hand reads (17 fewer SteamVR calls a frame), and the left-handed binding descriptions name the mirrored
   buttons.
9. **Grenades** rest on the palm (from the fist's hollow out by the palm's skin and their half thickness): a frag's glove
   went from 14 to 2 of 72 points inside it at the hollow, with all four fingers on it.
10. **Holsters**: after a steep look down and a turn the body's facing catches up at most 150 degrees a second (the
   holsters jumped round the body at once); the free hand cannot draw where the game does not (deep water,
   toggled items); a refused draw leaves the gun where it was.
11. **Final validation of the shipped set**: the full autotest (`metric_diff.py`) held or improved every measurement
   against the previous run (frame time 10.47 vs 10.63 ms, `Weapons.BeforeRender` max 291 vs 366 ms), and the two
   earlier problems (a DAGGER launcher false alarm, a JOLT-20-S support slide) were gone. The run's six problems proved
   to be two intermittent flakes — an RPG single-frame pose spike (0.00 under a deep repro and in the previous run; the
   run had a 562 ms frame hitch) and a GREASER slow-push mag seat (not sampled before, did not recur) — shown not to
   come from the change set (`HoldFrame` is a no-op over 20 cm; the reload diff is only a perf wrapper). The deep run
   also ran 264 s across three weapons as the squad/doc build's smoke test.
12. **Pause audit, completed across the hands** (read-only scouts, then verified fixes, each guarded by the pause so
   normal play is provably unchanged): a reload already in progress is frozen (its per-role switch had kept seating,
   kicking, racking and letting go while paused; the kick and rack baselines re-anchor on resume); an off-hand grenade
   is not thrown by a trigger or a grip let go while paused (the trigger release is resynced on the frame back, the
   grip-throw now checks `GunInputBlocked` as the main hand does); a grenade pin grab ends on pause. Plus guard fixes
   the scouts found: the palmed magazine's drop and the view mode (a player with no actor) against destroyed objects, a
   reset of the mag-kick trackers when the weapon is let go, and the level push-out seeing 32 colliders instead of 16.
   Still open for a headset session: the flight stick and steering wheel measured against a one-frame-stale palm (pin
   to full throw / creep through a corner at speed), and the pin's world-space pull test.

---

## 15. Work Log (2026-10-22)

1. **Improve 20 and bug hunt 10** (no game; Tasks.md 330): offline checks 65 to 84, the autotest's `Failure` pattern
   missed four failure words (magazines through the gun were never problems), `uninstall.ps1` deleted the launcher
   Steam still starts, `install.ps1` could delete outside the game folder, the setup hung on a damaged Steam app cache, the
   glove's keep-out tested a stale place during the grip's ease-out, the wrist HUD hung at a level load, a UI press stuck
   across the module's switch-off.
2. **Shotguns** (the user: not reloadable, no real shells, the double barrel's shells never out, one floating): see 3.7.
   Verified on all 17 in the game (null driver): each loads by hand one shell at a time to full.
3. **Improve after it**: the failure-phrase test reads nested strings (76 to 201 phrases; 11 failure words that never
   counted, 4 of them seen 47 times: THROUGH THE GRIP, HIDDEN IN THE GUN, GLOVES OVERLAP, ONTO THE RAIL BESIDE THE GRIP),
   thrown shells are the gun's own, pictures and sheets of shells going in, the mw-m870's floating shell hidden.
4. **The loading screen** (the user: "black / with blue shades glitching"): see 3.9. To check in the headset: steady,
   the right way round.
5. **A half-built weapon system** is disposed and no longer left as `WeaponSystem.Instance`.
6. **Physics pieces reach their ends** (`ShapeFit`): a piece's end slices put their outline at the slice's outer end; a
   gun-like cloud had 148 of 2600 points outside every piece (up to 13 mm), now none (new offline check, same fit time).
7. **Read-only Haiku scouts, each lead checked before a fix**: a machine gun dropped with its cover open and no belt laid
   comes back so; a belt laid under an open cover shows again on a redraw; the free hand's machine gun lays its belt in
   its self-reload; a swapped-away free-hand gun keeps its reload's rounds; shots are routed by live owners only; the
   gun's stand-in after a snap turn; no ignore calls on switched-off colliders; a stale mod-keys row presses nothing; a
   sound donor whose analysis failed is asked again; the autotest's silent skip and two lower-case failures.
8. **A pump is its own handle** (the ARS M500 was racked at its ejector bone); script-key taps stay targeted when their
   script is destroyed; the loading overlay fades with the view; reload rotations are kept only for drawn moving parts.
9. **Tests**: 95 C# checks, the sheets test; an `AmmoState` check is not possible offline (the game's `Weapon` type
   initializer runs native code).

## 16. Work Log (2026-10-23 and 24)

1. **Boneworks studied from its files** (IL2CPP: a self-made `global-metadata.dat` parser and raw asset reads; never
   launched, nothing written there; Tasks.md 334). Its reload geometry matches ours (4 cm plug radius); imported its
   behaviour: the glove contact sweep, hands staying on worked parts, the slam release and the haptic stack (see 3.4).
2. **The user's hand-through-gun bug** (fast swipes, the double-magazine reload): fixed by the sweep. Released as 1.11.0.
3. **Performance, measured offline** (`tools/tests/bench.ps1`): gun-volume point test 92 -> 24 ns, the glove skin's
   nearness 59 -> 18 ns, `ShapeFit` (a drop's collider pieces, main thread) 1.54 -> 0.41 ms with the same pieces (a
   checksum of every point), per-frame and per-shot garbage removed in the input module, lens flash, melee, the menu
   notice (no GUI layout pass) and the magnifier grip.
4. **Bug hunts by read-only Haiku scouts and three workflows** (finders plus skeptics), every finding checked before a fix;
   all listed with their status in `reports/haiku-findings-2026-10-23.txt` and `-24.txt`. Among them: a launcher or
   revolver reloaded in the free hand lost a round; a semi-switched gun stayed semi-only after VR stopped; VR input kept
   its state over a restart; shadow-only renderers drew their mesh while held; the second weapon was holstered on a
   one-frame tracking loss; teardowns (OpenVR, the rig, weapons, UI) each guarded per step.
5. **Setup**: an uninstall no longer clobbers another tool's `winhttp.dll`, a bad download changes nothing, a stopped
   install keeps its record, moved games keep their backups, Steam's files are replaced in one step, quoted or relative
   paths are made whole, Downgrade/Repair labels; each with an offline test (154 checks).
6. **Clean**: `HeldWeapon`, `ReloadInteractions`, `WeaponAnalyzer`, `WeaponDrops` and `MagazineProp` split into partial
   files at their own sections (pure moves, verified by line multiset; see the file table in 4).
7. **Measured before changed**: two analysis rules a fix would change for every gun (arm-named groups, the first magazine
   name match) now write notes; a `-rfvr-selftest` run lists the guns they affect.

## 17. Work Log (2026-10-25 to 28)

1. **Held soldiers** (`Weapons/BodyGrab.cs`, Tasks 341-345): the spin and fling came from a kinematic grab anchor moved by
   `MovePosition`, which PhysX solved as standing still; the anchor is now a 10 000 kg dynamic body driven by its velocity each
   step (`GrabAnchor`), at the hand's place at the step's own time. The hold is locked at the bone's centre of mass with a
   slerp drive for its turn (held at the touched point instead, the body's weight levered it into 15-68 m/s spins). The drawn
   glove goes on the body where the palm met the bone's surface (`SurfaceAt`, `Hold.SeatAt`, the hand model's snap pose and
   solid), a hand inside several bones takes the heaviest, and held bones are drawn extrapolated (a jump carries their physics
   poses, not the drawn ones). Impacts hurt by force: the contact speed capped by the impulse over a 16 kg trunk bone
   (`BodyImpacts.ImpactMass`), the hardest blow in 0.15 s counted. Friends are treated as enemies (neck, blows, thrown bodies,
   strapped grenades), as the game's own guns and knife treat them.
2. **A soldier's gun** (`Weapons/SoldierGuns.cs`, Tasks 346): a hand swung into it knocks his aim aside and holds his fire
   (Harmony postfixes on `AiActorController.FacingDirection` and `WantsToFire`); a squeeze under its middle empties it (his AI
   reloads an empty weapon) and gives a magazine of its kind; elsewhere it is wrested into the hand as a dropped weapon, and he
   draws his other weapon. Never from a soldier with no other weapon: the game's AI reads `activeWeapon` without a null check.
3. **A seated magazine stays in the hand** until the grip opens (`ReloadInteractions.Magazines.cs`, the `SeatedHold` marker,
   Tasks 347), only from the two real insert paths (the self-checks seat through `SeatProp` directly).
4. **Where the frame goes** (`Core/FramePhases.cs`): every player-loop step and every camera's render on the main thread, the
   scene's bodies, renderers and cameras, the game canvases the UI camera draws, and the mod's costliest guarded sections, in
   the once-a-minute frame rate line of every session. The user's headset session (Tasks 348) showed the Game preset at 90 Hz
   and Medium/Ultra at SteamVR's half rate with the GPU at 7-11 ms: the eyes' render on the CPU (shadows, LOD; paid per eye in
   multi-pass) put the frame past the budget. Single-pass instanced is not possible: the game's shaders have no
   `STEREO_INSTANCING_ON` variants.
5. **Cheaper frames**: the game's scope cameras draw only while a lens of theirs is shown, without shadows; the background camera
   is off in VR; suppressed game cameras and the mod's UI camera render without shadow maps (a camera drawing nothing still
   rendered them); the low-health lens edge is painted 30 times a second; weapon builds in the dead view wait a second after a
   slow step. "Hold the frame rate" (`GraphicsPresets.HoldLevel`, opt-in) steps a preset down while frames miss the rate.
6. **Tests**: the soldier check gained friends, the soldier's gun, the quick reach and the slowing fist out of reach;
   `tools/tests/readme_settings_test.py` keeps the README's settings table true to `VRConfig.cs`; vrshots keeps the user's own
   last session log (`LogOutput.player.log`).
7. **Saying why at start-up** (bug hunt round 4): an install-check or Harmony failure shows its reason on the screen with only OK
   (`VRManager.StartProblem`; F6 cannot mend those), and controller input that did not start says so in the headset
   (`UI/HeadNotice.cs`, a wrapped text plate ahead of the eyes) and on the monitor (`VRInput.InitError`). A level load no longer
   drops the dashboard or headset pause (`VRManager.PauseAfterLoad`, once when the player is in the match); the on-foot yaw is
   worked out again at the headset's first pose; the Game preset re-applies the game's current quality level; infinite-magazine
   guns (classed NoAmmo) are measured as guns (`WeaponRigData.IsTool`); left-handed binding texts mirror the hand words.
8. **Hot paths trimmed**: a finger solve reads the hand's root pose once (`HandModel._chainRoot`); the muzzle flash's visibility
   steps each line to the eye only where it crosses the gun's box (`GunVolume.LineInsideModel`); the soldier's-gun reach makes
   one pass over the match's soldiers a frame for both hands; the match bar's canvas group is written only when it changes.
   `tools/tests/harmony_targets_test.py` checks every Harmony patch named by a string against the decompiled game, and since
   Tasks 371 every member read by reflection (`FieldRefAccess`, `AccessTools.Method/Field/Property`) too.
9. **Magazines in slow motion and on the move** (the user's report): a magazine let go in slow motion flies as in real time
   until its first touch (`Weapons/RealTimeFlight.cs`: the hand's real-time velocity divided by the time scale, gravity g/s²,
   given back to the world's pace at the touch); eases that start from a hand or the gun keep their start against the rig
   (`MagazineProp.ToRigSpace`), so a magazine no longer trails a walking player. The autotest checks both.
10. **1.14.0** (built, not published): rounds 5-7 of bug hunts fixed (the free hand's grenade pin, sound voices, multi-barrel
   splits, the rack carve warmed at its snapped point (`BepInEx/cache/RavenfieldVR-rack-points.txt`), replaced hull meshes
   destroyed, the installer's keep/restore/record paths and its tests, the launcher's preset kept from the headset, the
   weapon overrides' broken saves and unknown keys, the analysis's geometric muzzle and next magazine name, the autotest's
   silent passes). The user's session showed the Game preset keeping Medium's settings (Unity ignores re-applying the current
   Quality level: fixed with a level-aware copy) and every phase slowing 2-6x over ten minutes with a steady scene; the frame
   rate line now times a fixed sum against the first minute, to tell a slowing CPU from growing work, and splits Input into its
   parts.
11. **The frame rate hold and a CPU-bound PC** (after 1.14.0): the hold's last step lowers the render scale only when the GPU
   is the cause (`GraphicsPresets.RenderScaleHelps`: its time at 70% of the refresh interval or more, given back under 45%),
   and weighs the window just measured. The user's Medium had the GPU at 3.3 of 11.1 ms with frames at half rate: the step
   only blurred the view. The wrist says "CPU-bound" in that case, and `tools/frame_report.py` has a matching 'bound' column.
   Since Tasks 371 (2026-11-08) the step also needs the GPU heavy for `_scaleNeeds` windows in a row (2, doubled after each
   give-back up to 16, back to 2 after five quiet minutes): one heavy window took it and the next gave it back, ten times in the
   user's Ultra session, each a new pair of eye textures. `ApplyTerrains` writes a terrain setting only when it differs.
12. **Bug hunt round 8**: the menu's camera getter after "Quit to menu" (a caught exception every frame), the muzzle flash
   size under Unity's scaling modes, the gun stand-in's ignore lists (a held soldier's limbs freed with his trunk) and its
   45 cm jump, a let-go body's bone pairs retried until they part, a launcher's shown rocket kept per launcher, the
   material repair cache cleared on a content reload, and vrshots/frame_report fixes (a timed-out run reported as partial,
   weapon sheets kept by the prune, the newest session read).
13. **1.14.1** (built and installed, not published): the user's test of a 16:02 build found every weapon's analysis failing,
   from round 7's weapon-overrides key regex ("\]" closed its class; the class's initializer threw on first use). Fixed within
   minutes; OfflineTests now run every mod class's static initializer. Round 9: the buttons back to the main hand's layout
   once no weapon is out, a soldier thrown in slow motion flying in real time (`RealTimeFlight.BeginGroup`, his bones as one;
   impacts, the crush let-go and the armed caps at the world's pace), the VR keyboard's field, VSync and the frame cap put back
   when VR stops. Open question for the user: grenades, dropped guns and casings in slow motion.
14. **Hands in slow motion** (`SlowMotionHands`, Normal or Slowed; Tasks 352): with Slowed, each hand follows its controller
   in game time during slow motion (`VRHand.Slowed` and `VRHand.SpringStep`: a critically damped spring of 20/s, 10 m/s and
   30 rad/s at most, all in game time), so everything that follows the hand is slowed with it; the head is not, nor the menu
   laser and the walking direction (`VRHand.TrackedPointer`, the controller's own pointer). A slowed hand's velocities are in
   game units, so `RealTimeFlight` and `BodyGrab.SlowMotion()` (the divisor that turns a hand's velocity into game units) step
   aside. The spring's first version fed the step's average velocity back and overshot by 4%: the offline tests caught it.
15. **Loose magazines against the gun and the hands** (Tasks 351): the gun's stand-in meets magazines again (it ignored them
   since 2026-10-20 because it flung them) and pushes a light prop no faster than the gun moves plus 1 m/s
   (`GunProxyBody.Touch`); empty hands push loose magazines they move into faster than 0.5 m/s (`HandBody.PushProps`,
   `HandBody.Pushed`), so a slow reach still takes one.
16. **Bench: the game's cameras into 1 px** (`CameraSuppressor.TinyRect`, bench only): what the suppressed cameras still cost
   (about 0.7 ms) is measured before it goes into play.
17. **Work-until-limit 45 of 2026-10-29** (rounds 11-12 and the other skills): a slowed hand's gestures read the controller
   (`BodyRelativeVelocity` and `OwnAngularVelocity` are the controller's real-time ones in both modes; `Velocity` and
   `AngularVelocity` are the hand's world motion), a slowed hand on a 1.5 m leash (`VRHand.SlowLeash`) and a long frame clamped
   rather than dropped, grenade safety across tracking gaps, the free hand's own holster path with Grip to hold off
   (`WeaponSystem.PutBackSecond`), volumes and magazine looks retiring their hull meshes, the surface sample's odd stride,
   stalled measurings released (`GunVolume.ReleaseStalled`), the Drive backup's worktree and empty-bundle cases, and the
   sandbox putting SteamVR back when it is closed during a game test.
18. **1.15.0** (built and installed, not published; notes in `dist/notes-1.15.0.md`): the weapon's arms hold a magazine as
   the gun's reload animation does (`WeaponRigData.MagHold`, sampled by `WeaponAnalyzer.MagHoldOf` with the support hand's
   rotations kept, `PoseSampler.KeepRotationsUnder`) and turn the free off hand from the glove's frame (`ArmIK`); and the
   five-skill Haiku sweep (Tasks 354, reports in `reports/sweeps/`): the lens glow only at the held gun, the scene change's
   steps guarded one by one, the pouch tick, all four pouch places and Grab reach on the panel, Calibrate now's notice, the
   mud splash from the headset's projection, the autotest's skipped-check count and the canvas's cached CanvasGroup.

19. **After 1.15.0** (2026-10-29/30, not published): the second Haiku sweep's fixes: machine-gun boxes measured, hidden and
   copied without the belt links hanging under them (`WeaponAnalyzer.DetachedMagazinePieces`: the PKM/PKP/Type 80/MK46), the K13's
   spare behind the stock grouped on its own in `ParkedAmmo`, the belt strip drawn to the hand's reach and eased onto the tray, a
   pump's let-go a stroke past its stops and its TRIGGER TO PUMP cue, the tube port estimate at 0.6 of the way to the forend, parked
   seats turned as the reload holds them. Weapon's arms: the free hand's fingers from the controller (`ArmFingers`), and arms on
   empty hands (`EmptyArms`: the last held gun's arm skeleton and meshes copied as it is put away, both arms IK'd to the
   controllers by `ArmIK.FreeTarget`/`SolveArm`, the gloves hidden). The settings panel's tabs, holster
   copies easing out when reached, a swapped weapon's ground copy without its magazine when it was out, and autotest checks for
   the free fingers, the empty-hand arms and the settings pages.
20. **The work-until-limit 60 of 2026-10-30** (the game not started): the magazine box taken from the gun's own copy where they
   disagree, once (`MagazineProp.AdoptCopyBounds`; the ARS guns, the G BRN180 EO SS; `WeaponRigData.AnalysedMagBounds` for
   the casings), the PKM family's belt laid at its model belt's feed (`HeldWeapon.BeltFeedGun`), parked seats turned as the
   reload holds them, a swapped weapon's ground copy without its magazine; the weapon's arms' trigger finger
   (`ArmFingers.Pull`) and empty-hand arms from a carried weapon at the start; soldier grabs (a body caught in its slow-motion
   flight, one taken by the second hand, a strapped grenade on a soldier who gets up); four settings to try for the frame rate,
   each said in the frame rate line and `frame_report`'s try column: `GameCamerasOnePixel`, `ScopeHalfRate`,
   `GarbageCollectionSliceMs`, `SmallObjectsNoShadows` (`SmallShadows`); offline tests for the copy box, the belt's rounds,
   the holster band and the shell port; the export check `magazine_pieces_rule.py`. Each batch read by a Haiku reviewer and its
   findings fixed. Later the same evening: the reload's helper hidden while held only when named as ammunition
   (`WeaponAnalyzer.HelperNamedAsAmmunition`, the name also read without spaces; 36 of the export's 63 helpers; checked over an
   export by `reload_helper_rule.py`), an arm mesh named as ammunition never drawn on Weapon's arms nor copied onto the empty
   hands (`NamedAsAmmunition`), which copy only the arm meshes drawn; the small-shadow scan waiting for the level and measured
   in the graphics bench (`SmallShadows.Bench`); offline tests for both name rules (249 checks). Then: the empty-hand arms'
   copy carried with the head and solved from its copied pose each frame (`EmptyArms._rootInHead`, `_restLocal`), a game
   camera's mask saved once and put back at the eyes when its post-render never came (`CameraSuppressor.RestoreMasks`), the
   autotest's weapon's-arms check run with the mode already chosen and its readings said for what they are (the IK's reach),
   `tools/bench_report.py` (each bench variant against its own preset), and the export's rig.json with the arm meshes' and
   hidden parts' names for the export checks (252 checks).
21. **1.16.0** (2026-10-30, built and installed, not published; notes in `dist/notes-1.16.0.md`, setup
   `dist/RavenfieldVR-Setup-1.16.0.exe`): items 19 and 20, and the remaining known bugs fixed before it: a shotgun's short
   tube kept shell-fed (`ReclassifyAdvancedReload`: pellets, three rounds or more, a shell-sized reload part), the shadow
   distance put back after every suppressed camera's render, an arm mesh on bones outside the weapon copied with stand-ins
   (`EmptyArms`). Still open, needing the game or the user: the first magazine after a dry draw on the un-fitted line
   (Tasks 355's plan), which guns insert magazines in the wrong place, which shotgun loads at the wrong opening.
22. **FPS, after 1.16.0** (2026-10-31, the game not started; read against the user's 10-27 Medium session, where the eyes took
   3.5 ms of main thread, the mod's UI camera 1.1 ms and the mod itself about 1 ms): the UI and scope lens cameras take the shadow
   distance away at their pre-cull (`VRScope.ScopeNoShadows`; at OnPreRender the level's shadow casters were still culled for them),
   a gun shape's measuring views render without shadows, the suppressed game cameras' image effects are off while VR runs
   (`CameraSuppressor._effects`), and the mod's presets draw the terrain instanced as the game's own top terrain quality does.
   Two more settings to try, each in the bench, the frame rate line and `frame_report`'s try column: `HideTinyDistantObjects`
   (`TinyObjects`: standing props a few pixels across not drawn, everything while a scope is magnified) and
   `InstanceRepeatedObjects` (`InstancedObjects`). The bench prints each round's frame interval; `bench_report.py` weighs both,
   names the settings worth keeping and gives the "every setting to try" run's figure; the bench opens with the rendering
   state read from the scene (`AutoTest.RenderingStateCheck`). From two Haiku reviews: an older map's (pre-2020.3) fallback
   terrain is left as the game sets it (`GraphicsPresets.OlderMap`), the game's quality and terrains go back when VR stops
   (`OnVrStopped`), the game's own scope pictures count as magnified for the tiny objects, animated props are left out of it.
   The tiny objects' threshold is three pixels of the eyes as drawn (`TinyObjects.AngleFor`: the eye texture's width over the
   left eye's projection, `GetStereoProjectionMatrix`, as the head camera's own fieldOfView stays Unity's 60 in VR), so a bigger
   eye (Ultra) hides only smaller angles. The mod's loose bodies (cases, magazines, weapons on the ground) are drawn without
   interpolation while they lie asleep (`RestingBodies`). A third review: one image-effect test by name for the suppressor and
   the autotest, post-processing given back to a camera that starts drawing into a picture, the bench's state read a few frames
   in (278 checks).
23. **1.16.1** (2026-10-31, built and installed, not published; notes in `dist/notes-1.16.1.md`, setup
   `dist/RavenfieldVR-Setup-1.16.1.exe`): item 22's frame rate work, not yet measured in the game.
24. **1.16.2** (2026-11-03, built and installed, not published; notes in `dist/notes-1.16.2.md`, setup
   `dist/RavenfieldVR-Setup-1.16.2.exe`): the user's list of 2026-11-03 (Tasks 357: the weapon's hand off the gun and on
   magazines, slow motion leaving the hands, a yanked magazine, see-through magazines, the MP9 PDW, the M1 GARRET, the M417A2's
   scope, MG belts, scope pictures and launchers, a gun held where a hand took it), the experimental magazines loaded by hand
   (`HandLoading`, off by default, set off by the setup on every install), then a bug hunt to 60% of the session in eight
   rounds (today's changes, second weapon, rendering, throwing, soldier grabbing, input, holsters and pickup; 289 checks). Not
   started in the game: every item waits for a game run.
25. **1.16.3** (2026-11-03, built and installed, not published; notes in `dist/notes-1.16.3.md`, setup
   `dist/RavenfieldVR-Setup-1.16.3.exe`): the handling details (Tasks 358): the well's edge heard, the rack's spring, the
   slide's spring return, dropped magazines heard, the bolt catch's click, rounds clicking into a magazine, the magazine detent
   (`MagazineDetent`, on; `UnderSeatedMagazineFalls`, off), bolt actions turned up before the pull, `AkMagazinesStayIn` (off)
   and curved magazines rocked in. Every new sound is a slice of the game's own weapon sounds (`GameSounds`). Not started in
   the game: every item waits for a game run.
26. **1.16.4** (2026-11-03, built and installed, not published; notes in `dist/notes-1.16.4.md`, setup
   `dist/RavenfieldVR-Setup-1.16.4.exe`): the user's two plays of 1.16.3 (Tasks 359): magazines kept as they look loaded and empty
   once the gun has settled (`HeldWeapon.SettledLoaded/SettledEmpty`), round-sized pieces drawn in a full magazine before that,
   magazines taken off the well's line routed only when needed (`MagazineProp.StraightCutsMouth`), the line's limits allowing for the
   gun's weight lag, the weapon's arms with magazines and passed guns (`ArmIK.SolveWhereTaken`, `SteadyGrip`), a machine gun's belt
   (hanging with the cover shut, left out of the box's copy), magazines loaded by hand in the user's second version (reserve as
   usual, empty magazines and rounds without end), holsters without an ejected magazine, game-sound slices faded.
27. **1.16.5** (2026-11-03, built and installed, not published; notes in `dist/notes-1.16.5.md`, setup
   `dist/RavenfieldVR-Setup-1.16.5.exe`): a machine gun's belt goes round its box in one piece (`BeltStrip.PathRound`), the weapon's
   arms hold off hand-bone poses off the gun as long as they last (`SteadyGrip`), the pouch shows an empty magazine when it hands those
   out, a slow push knocks a magazine out only roughly in line (302 checks).
28. **1.16.6** (2026-11-03, built and installed, not published; notes in `dist/notes-1.16.6.md`, setup
   `dist/RavenfieldVR-Setup-1.16.6.exe`): an empty magazine with no empty look kept leaves out round-shaped pieces
   (`MagazineProp.RoundShaped`), the game's own reload passes through the steadied grips (306 checks)
29. **1.16.7** (2026-11-04, built and installed, not published; notes in `dist/notes-1.16.7.md`, setup
   `dist/RavenfieldVR-Setup-1.16.7.exe`): every sound the game's own (`GameSounds`' table of measured cuts, `SoundVoices` the pool, no clip
   made in code); the settings panel in tabs with `ShowAdvanced`; the UI blue; belt guns' animators told the box's rounds (the PKM's
   belt only comes back above ten), a skinned model belt hidden by its renderers, belts kept out of the box end to end; the pouch shown
   with the gun away; a held round stood on the hand.
30. **1.16.8** (2026-11-04, built and installed, not published; notes in `dist/notes-1.16.8.md`, setup
   `dist/RavenfieldVR-Setup-1.16.8.exe`): what the rest of the 80% run found (Tasks 362), `GameSounds` holding only its table's clips; the
   ammo fuzz (`tools/tests/AmmoFuzz.cs`, the real AmmoState.cs against stubs) and the belt fuzz.
31. **1.16.9** (2026-11-05, built and installed, not published; notes in `dist/notes-1.16.9.md`, setup
   `dist/RavenfieldVR-Setup-1.16.9.exe`): the bug hunt on magazines, belts and machine guns (Tasks 364). Long reload clips cut at the
   actions; the QJB201's real cover and length; the game's `random` set in the sampler (the AS Val's bolt); belts a model draws itself
   left to it (`AmmoDrawsParts`, `BeltDrawnByAnimator`); `tools/bundles/clip_motion.py`.
32. **1.16.10** (2026-11-07, built and installed, not published; notes in `dist/notes-1.16.10.md`, setup
   `dist/RavenfieldVR-Setup-1.16.10.exe`): the improve and bug hunt run to 75% (Tasks 365). Clips matched by name as well as timing; a seated
   magazine let go stays in, `TuggedFromSeat`; sister magazines (`TakesMagazineOf`); a made stand-in keeps its own shape key; the game's
   `ReloadDone` skipped in Manual; `BoltLeftAt`; `Rested`; `RealTimeFlight`; `RemoveInstalled` retryable.
33. **1.16.11** (2026-11-07, built and installed, not published; notes in `dist/notes-1.16.11.md`, setup
   `dist/RavenfieldVR-Setup-1.16.11.exe`): the run of Tasks 366. Night vision in the eyes (`finalLayer`'s mask; section 3.3), `GogglesTube`,
   `UpdateGunShade`, `UnderwaterVeil`, `MenuMouse` and B as the right button, a free hand's cases, squad pointing at vehicles and the sky, a neck hold
   that stops a soldier's path and trigger, `ScriptKeys.Caller`, `SteamPlayEntry.Ensure`.
34. **1.16.12** (2026-11-08, built and installed, not published; notes in `dist/notes-1.16.12.md`, setup
   `dist/RavenfieldVR-Setup-1.16.12.exe`): the run of Tasks 367, with game tests again. `BeltStrip.Place` keeps a round's ends out of the box
   (`BeltAgainstDrawnBox` measures it in the MG check), `ThrowableHandler.Detonate` (the C4), `Airhorn` on the trigger, `ThrowableHandler.Launch`
   (bags' bodies), `RealTimeGrenades` and `RealTimeProjectiles` (slow-motion throws), `RealTimeFlight` counting flight time, `GameSounds.NoCuts`,
   `VRScope.HasOwnReticle`, `PerfProbe.Reused`, `AmmoState.ShownSpare`, `MortarRangePatch`, `MagazineProp.ReleasePassedHulls`, `HandBody.GiveLayerBack`.
35. **1.16.13** (2026-11-08, built and installed, not published; notes in `dist/notes-1.16.13.md`, setup
   `dist/RavenfieldVR-Setup-1.16.13.exe`): Tasks 368. Started with -vr and SteamVR closed, `VRManager.Start` starts SteamVR (vrstartup),
   waits for its compositor and tries `OpenVRRuntime.Start` every 3 s for up to 2 minutes (`SteamVRWait`) before it plays flat;
   `HeadsetWaitForm` waits 2 minutes for an answer once it started SteamVR itself.
36. **1.16.14** (2026-11-08, built and installed, not published; notes in `dist/notes-1.16.14.md`, setup
   `dist/RavenfieldVR-Setup-1.16.14.exe`): Tasks 369 and 370. `WeaponRigData.HandleCycles` (the M1's operating rod locks back with the bolt),
   `RackSplit` (a pull plays the first stroke of a clip or slice holding both), `WeaponAnalyzer.WholeReloadName`, `SoldierGuns.BodyDistance`,
   `VRScope.OwnPictureMaterial` (a gun's own reticle texture from its 2D scope picture on the lens, vetted once on the GPU, warmed in
   `WeaponWarmup.Prepare`), `PropPhysics.GroundBetween` and `UpOnto` (surface ground only), `BackToward` passing the held gun's stand-in,
   `WeaponSystem.LetGoForNewLife` (also from a prefix on the player's `Actor.SpawnAt`: a takeover has no dead frame), a soldier's gun not taken
   within 0.6 s of a hand's blow. Not verified in the game: the runs were stopped at the user's word; the last one froze at the let-go check
   (Tasks 370).
37. **1.16.15** (2026-11-08, built and installed, not published; notes in `dist/notes-1.16.15.md`, setup
   `dist/RavenfieldVR-Setup-1.16.15.exe`): Tasks 371, made with the game not started. `GraphicsPresets` (the hold's render-scale
   step after `_scaleNeeds` heavy GPU windows in a row; terrain settings only on change), `PropPhysics.MakeKinematic`/`MakeDynamic`,
   `EnterSeatPutAwayPatch`, `MudSplash` and `SoldierGuns` in water, the real-time projectile prefixes guarded, `HeldWeapon`'s lens
   rebuild and mode lenses (`HasOwnReticle`), `VRScope.WarmPicture` in `PrepareInSteps`, the chest pouch on the free hand's side, the
   suite's `enum_switches.py` and `mixed_clocks.py`, reflected names in `harmony_targets_test.py`. Not verified in the game.

## 18. Splitscreen as whole game copies (2026-11-14/15, Tasks 398-399)

Each extra player (2-4) is a whole Ravenfield process of its own, joined to a LAN game that player 1's game hosts on this PC. Every feature works for them because they are ordinary multiplayer players; the splitscreen part is only input, windows and start-up. Code: `src/Local/SplitGames.cs` (host and copy sides), `FlatPlayerInput.cs` (bindings), `RawInput.cs` (devices), `SplitDeploy.cs`, `SplitCopyMenu.cs`, `CopyTest.cs` (the copy's scripted test). The older way (extra players as AI soldiers in player 1's game, `SplitPlayers`) stays behind `Splitscreen.FullGames = false`.

**Start-up.** The host calls `HostLan(7140)` and starts the copies one after another, 5 s apart (started together, two found the mod loader's files taken), through ShellExecute hidden, with the DOORSTOP_* variables removed from its own environment first (inherited, they made a copy load no mod). Arguments: `-rfvr-splitclient=i -rfvr-splitn=n -rfvr-splithost=127.0.0.1:7140 -popupwindow -screen-width/-height -rfvr-splitowner=<hwnd>`, plus `-rfvr-splittest` and `-rfvr-mods=` when the check runs. A copy that fails to start is queued again, three times at most. A copy that exits is started again unless its log (`BepInEx/RavenfieldVR-split-N.log`) says "this player left"; the log is read at the moment the exit is seen.

**Loading and go.** The host shows "Loading players" full screen with a line per player, keeps its window topmost while a game window is in front (AttachThreadInput to take the focus back from a copy that showed itself), and hides every copy's window each frame until it is in the match. It reads the copies' logs every 30 frames for "in the host's match". When all are in (or after 240 s) the go flag travels in the input packets; each copy shows its window (owned by the host's, WS_EX_TOOLWINDOW, so one taskbar entry and always over player 1's) and deploys. Embedding with `-parentHWND` crashed the copies (DestroyWindow inside UnityPlayer), hence owned windows.

**Input.** The host reads every keyboard and mouse through Raw Input and sends each copy a 52-byte packet ('K', a 32-byte key bitmap, mouse buttons, mouse totals X/Y as floats, wheel, generation, go, the send time) on UDP 7150+i. A copy in front forwards its own raw events ('E' packets, accepted only from the copies' ports) so the host still tells devices apart. The copy evaluates the game's own bindings (`FlatPlayerInput.Value`): positive key minus negative key, or a Unity axis ("Mouse X" = +dx x 0.1, "Mouse Y" = -dy x 0.1, the wheel), then the binding's own invert; held = value > 0. Controllers are read by each copy through XInput (the game's sideways axes are positive to the left, so the pad's x is negated). Switch team is an extra bind (T, D-pad right). Packet ages use `Stopwatch` (QPC, shared by all processes); `Environment.TickCount` moves in 15.6 ms steps and showed input 16 ms late that was not.

**Speed.** Copies: quality level 0, no shadows, sound off, one frame queued, power throttling off (`SetProcessInformation` ProcessPowerThrottling), above-normal CPU priority, a 1 ms timer (`timeBeginPeriod`), above-normal GPU scheduling (`D3DKMTSetProcessSchedulingPriorityClass`; Windows puts the foreground program's GPU work first and every copy is behind the host). Frame rate: the screen's rate rounded down to 144/120/90/60, held every frame (the game sets its own as it loads), stepped down by `AdaptRate` when more than one frame in ten is a quarter late in two 240-frame windows in a row; never back up. The host is capped at the screen's rate with one frame queued while copies run. Measured with 4 players (2026-11-15): copies 120-144 fps, mouse to turning 7-13 ms (one frame), W to walking 75-94 ms (the soldier's own run-up), input 4-5 ms late. At 60 fps the user called it "very very delayed" next to player 1's 160.

**Stand-ins.** Players see each other as the multiplayer stand-ins: told 30 times a second on a LAN (60 from a copy, which tells only its own player), dead-reckoned and pulled toward the told place (pull 10 on a LAN, 5 over Steam); they walk only while their player moves (`IsMoving` = moving or walking: no moonwalk) and hold the gun up (`IdlePose` false). The check samples them at 0.00-0.04 m from where their player is.

**Copies' own things.** Esc menu (`SplitCopyMenu`, no pause): resume, team, respawn, next spawn's weapons, mouse sensitivity and invert (kept per player in `BepInEx/RavenfieldVR-split-playerN.cfg`; the copies share player 1's game settings), leave. Names carry "(Player N)". The cursor is never locked in a copy.

**Checks.** `vrshots --splitgamescheck` (a copy played by `CopyTest`: deploy, stand, walk, strafe, turn, look, fire, reload, next weapon, Esc menu, team switch, damage both ways, kill and rejoin), `--splitlagcheck --players N` (Raw Input injected on the host, the copies' probes: input age, W to walking, mouse to turning, frame pacing), `--splittry --players N` (by hand; F12 hands the one keyboard to the next player).

**Traps met.** A copy's UDP socket got "connection reset" from ICMP: `SIO_UDP_CONNRESET` off. A destroyed player controller's `GetActiveCamera` threw during a joined map change: `Rig/ActiveCameraGuard.cs`. Per-frame garbage on the host (LINQ, iterators, new sets) stops player 1's game for the collector: the per-frame paths use kept lists.

## 19. Vehicles by hand: mounted guns, insides, the vehicle check (2026-11-15, Tasks 400-401)

**Open mounted guns (`Rig/TurretGrip.cs`).** A mounted turret (stabilised or not) of the player's seat is taken by hand when the seat is open (not `enclosed`), it is not a driver's gun of something that drives (a tank's main gun, a gunship's), it is not aimed through its own `overrideCamera`, and its pivot (`pitchTransform`, else `bearingTransform`) is within 1.7 m of the eyes. Handles: the seat's `handTargetL/R` within 1.5 m of the pivot (stored in the pivot's frame so they move with the gun), else two made at the rear of the gun's own renderers (the point furthest back along the barrel, 12 cm either side). A grip within 17 cm takes it (hand role Cockpit: no snap turn, no seat change, the leave trigger off). Held, the gun is turned as a rigid body with the grip round its pivot: sideways by the signed angle the grip's horizontal arm turns round the vehicle's up, then up and down round the gun's across axis; an arm under 20 cm on an axis takes that axis's turn from the hand's own rotation instead. Let go, it gets no input and stays. `TurretAim.Steer` takes the wanted direction (proportional steering, as the game's AI gunners); the reference is the game's own (`AimReference`: a stabilised turret's pitch part, else the muzzle). Either holding hand's trigger fires (`GameInputBridge`), each shot buzzes the holding hands. Manual reload: a gun with a magazine size (`configuration.ammo > 0`) gets no reload of its own while governed (`WeaponReloadPatch` returns false unless the hand allows it); the cocking handle (receiver's right, 40% from the handles to the pivot) pulled 7 cm back calls the game's `Reload` (its time and sound). Config `Controls/GrabMountedGuns`.

**Insides (`Rig/VehicleInterior.cs`).** An enclosed seat of a ground vehicle or boat gets a cabin parented to the rig's root at the head's neutral pose (so it turns with the turret for a gunner, with the hull for a driver): walls, roof and floor tinted from the vehicle's largest material (halfway to grey: team colours), a 56 x 20 cm slot in the front wall to look out, a lamp; a gunner's seat gets traverse (left) and elevation (right) handwheels and a red fire button; a driver's seat forward/back/left/right buttons (`VehicleInput.DriveButtons`, added to the stick in `Car()`). The gun of such a seat is never aimed by the head: a wanted aim (vehicle frame) is kept from the seat's start, turned by the right stick (70 deg/s, 35 deg/s up and down; snap turning off) and the wheels (a crank turn = 90 deg traverse, 30 deg elevation), held within 20 deg of the gun. The wheels' rim angle is read in their fixed mount, not the turning wheel. Burning or wrecked: a red point light and red emission flash twice a second (no sound). Config `Vehicles/Interiors`. The user found the drawn cabin "very bad" (2026-11-15): free CC0 interior parts are proposed (`Util/ObjModel.cs` reads OBJ + MTL at run time for them).

**Steering wheels.** `CockpitControls` takes the `SteeringWheel` linked to the driver's seat, else one linked to no seat within 1.5 m of it (mods leave it unlinked).

**The vehicle check (`vrshots --vehicleshots`, `Core/VehicleShots.cs`).** Every vehicle and turret prefab (the game's 11 vehicles and 3 turrets, the loaded mods' 25 here) is made ahead of the player's start by its size, held kinematic; the player leaves the last vehicle (which is then dropped the game's way, `ActorManager.DropVehicle`) and goes seat to seat in the new one (driver, every gunner, the first passenger), waiting for `CanEnterSeat`. Per seat: the head against the seat, pictures (ahead, down, held, the inside to the left and behind, the alarm); a driver's wheel turned 60 degrees by a scripted hand; a grabbable gun taken, swung 20 cm (or turned 25 degrees for handles under it), let go, moved again, its wanted against its actual turn, its reload emptied and done by the cocking handle; an inside's gun left alone and cranked half a turn. The view's biggest half-second turn is reported (one-frame seat cuts left out).

**Traps met.** Seat to seat across two vehicles throws in the game (its camera switched, the player left in the old seat): leave first. A vehicle made where the player stands knocks him over (the user saw the view spin and ended under the map). Setting `Vehicle.burning` for a picture burned the vehicle and killed the player (a test switch shows the alarm instead). A model's turning parent is often tipped (Z-up exports): its local y is not up. Mono's `Stopwatch` counts per process (the splitscreen clock, section 18).

**Later the same day (1.19.1-1.19.2 and after).** The inside's drawn box was dropped: the hull itself is the cabin. `HullInside` takes the vehicle's parts whose objects are active (the game switches off the hull's renderers for a seat inside so its own camera sees out; those are kept and their twins always drawn), leaves out named hitboxes and colliders and the game's water-occlusion shader (`Custom/StandardOcclusionVertex`), and clears a corridor before the eyes and a channel out of the hull along the view (small parts hidden, the twins' triangles cut, back-drawn parts crossing it left out, a seat's cuts undone before the next seat's). The inside and the solid hull are judged 0.3 s after a seat is taken, once (kept until the seat changes), and only where the eyes are inside the vehicle's three biggest parts' boxes and not more than 1.4 m above the seat (the APC driver's and the AU220M's standing views look out directly). Every enclosed gunner seat aims by the right stick and the handwheels whether its inside is drawn or not (head aim on a turret's own camera chased itself: a 378-degree spin). The periscope camera turns along the gun's own aim with the world's up. Destroyed seats, guns and vehicles are compared by reference (Unity's null equality left ghost state). Feel: live gauges (speed, engine, hull), a slow pulse in both hands while an open vehicle burns, a seat-change notice, an auto-hover click, glows at the rim, the stick, the levers and a gun's handles as a free hand nears, a filling glow at the palm while holding to get in, a collective lever for helicopters (`VehicleInput.Throttle` read as the collective; kept through the game's airplane-input read for a helicopter's stick). Aircraft gun seats (door guns, a tail gun) are taken by hand though "enclosed". The game's `FpsActorController.OnVehicleWasDamaged` NullReferenceException on each hit to the player's vehicle is caught (`Rig/VehicleDamageGuard.cs`). Full vehicle check on 1.19.2: 0 problems (20 closed seats with insides, 10 open guns by hand; buttons, both handwheels and wheels pass).

**After 1.19.2 (not yet run in the game).** A closed wheeled vehicle's driver gets a steering wheel in the cabin (a third of a turn is full lock, straight when let go; written to `VehicleInput.WheelSteer` after `CockpitControls` clears it, so the cabin's value is the one read), and the cabin holds the gun's rounds (a cannon's five shells in a rack, a machine gun's two boxes). The periscope camera draws every other frame. Multiplayer: a VR gunner's told facing is the barrel's real direction (`TurretGrip.AimReference`), since a gun aimed by handles, stick or wheels no longer points where the head looks. Killed in a seat, the view is held 1.4 s at the seat's last view (`_lastSeatAnchor`, kept for a seat's and a sight seat's view, cleared on a seat change; the `DeathHold` key) with a dark-red fade, also when the seat was destroyed with its vehicle. Bug hunts: seats change with a gun drawn; the collective lever is not doubled by auto-hover; the airbrake click is a plane's only; a trigger held out of a seat is not Use on foot; a burning vehicle's own damage ticks do not rumble as hits. Offline tests: `TurretGrip.Swing`/`Rotate` and `VehicleInterior.Cranked` are pure and tested (512 pass).

## 20. The tank cockpit (2026-11-15 night, Tasks 402; 1.19.3)

The user called the first inside "kinda beta": the fire button too far from the hands, the cockpit to be improved for every tank. Everything below is in `Rig/VehicleInterior.cs` and built from primitives tinted from the vehicle's own largest material (no downloaded assets: the sci-fi kit was refused, no CC0 tank interior exists).

**Layout (cabin frame: the eyes at the origin, +z the seat's forward).** Periscope viewer at z 0.42-0.47 (screen 56 x 20 cm), five dials over it on a gun seat (speed, engine, hull; turret way at x -0.24, elevation at x 0.24), handwheels at (+-0.26, -0.36, 0.3) (+-0.36 with a steering wheel between them), the fire box at (0.05, -0.3, 0.31) beside the elevation wheel's inner rim (outside it, x 0.57, with a steering wheel), the readout at (0, -0.2, 0.41), the zoom knob at (0.35, -0.05, 0.4), the LIGHT toggle at (-0.36, 0.2, 0.38), the rack at x 0.52 (+0.2 beside a steering wheel), the breech centred at (-0.6, -0.1, 0.3), levers at x +-0.42 on the floor, the drive panel 26 cm wide on the floor between the rims. A box script over these (scratch `layout_check.py`, the parts' boxes for a cannon gunner, a machine-gun gunner and a tank driver over three floor heights) found the radio running through the recoil guard's rails and the panel's corners in the rims; both moved.

**Fitting each hull (`Room`).** A ray from inside a collider meets nothing, so the room is cast in from outside: a ray from 1.5 m out along the direction back to the point, against each of the vehicle's own colliders (not triggers, wheel colliders or seated soldiers), the outermost hit kept. Measured left at (0, -0.15, 0.2), right at (0, -0.25, 0.1), down at (0, 0, 0.3). Thresholds: breech 0.73 m left, radio 0.55, stowage 0.5, rack 0.59 right (0.79 beside a steering wheel), cable run 0.48. A fitting that does not fit is left out, never squeezed: with no rack there is nothing to load by hand, and the game reloads. The fit goes to the log line and the vehicle check (`Fit`).

**Loading by hand.** In Manual reload with a breech and a rack, `HoldsReload(gun)` is true for the breech's cannon (kept as `_cannon` when the breech is built: the seat's drawn gun can be the coaxial machine gun), and `WeaponReloadPatch` returns false for it: the game's `AutoReload` -> `Reload()` waits. A grip within 12 cm of a shown shell's middle takes a copy into the fist (the rack shows one less); with the gun not full, spare ammunition and the shell's nose within 17 cm of the breech block, `Weapon.InstantlyReload()` (ReloadDone: CancelReload, then the spare taken) loads it, the gun's own `reloadAudio` plays (InstantlyReload plays none), a heavy haptic, the breech jolts 2 cm. Unholster-is-reload guns go through `InstantlyReload` and are not held. The ready lamp: green loaded, amber blinking reloading or waiting ("load"), red empty; the readout says LOAD SHELL; a glow pulses on the next shell, then on the breech (7 cm toward the eyes, or it sat inside the shell).

**Tracked drivers.** `Tracked(v)`: the game's `Tank` class, or any vehicle with a `TankTurret` in it: the vanilla tank and the mods' Leopards are `Car` to the game (the vehicle check names them "car"), and before this they got a car's steering wheel. They get two levers instead: the angle of the palm round the pivot over 30 degrees is the lever's value; `TillerDrive(l, r)` = ((l - r) / 2, (l + r) / 2) into `VehicleInput.DriveButtons` (added to the stick), after the drive buttons' own press (or the forward button sank with the lever). Let go, a lever springs back at 5 a second.

**The sight.** The periscope of a gun seat gets a reticle (unlit `Unlit/Color` boxes 1.5 mm in front of the screen) and a zoom knob: x1 is 40 degrees, x3 13; zoomed, the stick lays the gun at 13/40 of its rate. The knob and the LIGHT toggle act on the press's edge (`_pressed[5]`, `_pressed[6]`), one change a press.

**Other.** A trigger of a hand on a handwheel fires (the dominant hand's fired anyway through the game input; the off hand's now too). A hand on the fire button never takes the wheel beside it. A drop of over 0.5 % in health flickers the cabin light for 0.35 s (Perlin noise, deeper for bigger hits). A cannon's breech runs back 14 cm in 0.05 s and forward in 0.5 s at each shot (`Kick`), its block down 6 cm while reloading or empty. Buttons sink along their own up (the world's up added to a cabin-frame position sank them sideways in a tipped hull).

**Checks.** Offline: `TillerDrive`, `Azimuth`, `Elevation` (15 tests, 527 in all). In the game (`vrshots --vehicleshots`, not yet run for these): the zoom knob twice with the x3 picture saved, the hand-load (waits empty, shell taken, loaded), the right lever 25 degrees forward (forward and left, back in the middle), the light switch twice (pictured), the readout, ready lamp and fit; after the run `build_cockpit_sheets` puts a sheet per closed seat and an overview in `<run>/cockpits`.

**After 1.19.3 (in code, not installed).** `Tracked(v)` was in 1.19.3. Since: the breech, its rack and the hold are tied to `_cannon` (the gun the breech was built for), not the drawn gun. The rack swaps a taken shell into its last shown slot (it hides by count). A wheel held by both hands takes half of each hand's turn. A hand carrying a shell presses nothing. The rangefinder: a `RaycastNonAlloc` along `TurretGrip.AimReference(gun)` over 3 km (layer 12 left out, as the pilot readout), the nearest hit not of this vehicle nor of a soldier seated in it, rounded to 5 m, shown 4 s over the ammunition while the gun is ready. The hatch: its handle is a group (`_hatch`) moved down with the hand; 8 cm down sets `_hatchFrames = 3`, and `GameInputBridge` sets `KeyBinds.Use` while `HatchPulled()` counts them down (with `_useHeldThroughSwap`, so the trigger is not Use on foot). The eyes-in-hull judgement (`Wanted`) runs for the first 3 s in a seat only.
