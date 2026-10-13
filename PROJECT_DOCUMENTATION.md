# Ravenfield VR — Technical Documentation

A SteamVR mod for Ravenfield (Steam build, Unity 2020.3.49, Mono) delivered as a BepInEx 5 plugin with its own OpenVR
XR loader. It turns the flat shooter into a hands-on VR game: tracked hands that close on what they hold, manual
reloading of every vanilla and workshop weapon, physical magazines, holsters, a VR HUD and menus, vehicles and an
automated test harness that plays the game by itself. Written 2026-10-13 (last refreshed: 2026-10-13, commit
`6e063e8`), after the session that added model-accurate hand posing, model-shaped physics, H3VR-style reloads and the
measurement tooling described below.

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
- **Status (2026-10-13).** Tasks 1–97 of `Tasks.md` done except 33 (measured performance pass), 37 (weapon's own arms
  with IK as an option — partially present as `Hands = WeaponArms`) and 94 (this document). The latest VR quick
  autotest (60 weapons) reports 2 problems, both a workshop weapon's own `LaserSight.LateUpdate` null reference, not the
  mod. The full flat self-test analyses 308 weapons with 0 failures.

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
  sets the choice from Steam's Play button.
- `VRManager` owns the fixed update order shown above. Every section is called through `Log.Guard(name, action)`,
  which catches exceptions (logged once) and, when `PerfProbe.Enabled`, times the section and counts allocations.

### 3.2 Input (`Input/*`)

- `VRInput`: SteamVR action manifest embedded in the DLL and extracted at runtime (`Input/SteamVR/*.json`; left-handed
  variants generated by `BindingsWriter`). Haptics are lengthened to a felt minimum on Quest Touch Plus controllers.
  Test hooks: `SimulatedGrab[]`, `SimulateGrabPress(hand)` (now also queues a pressed edge), `SimulatePress(action,
  hand)`, `SimulatedTrigger[]` (finger pose only, never fires).
- `GameInputBridge`: context-aware mapping into SteelInput (fire, use, jump, crouch, scoreboard only with the map
  gesture, etc.). `ScriptKeys` lists keys that weapon/mutator scripts listen to (mod-keys panel). `TurretAim` steers
  turrets toward where you look; `VehicleInput` maps sticks to cars/helicopters/planes.

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
- Cached per key in a 12-entry LRU; a failed build is not kept (measured again next time). `InFlight` counts builds
  waiting for read-back (the menu warm-up waits while more than 4 are in flight).
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
  capsules + palm box, published at the end of the gun hand's `Apply`; the dominant hand is now posed first).
- Working the action (`HandRole.Slide`): the **worked part's own volume** (charging handle if separate, else the slide /
  bolt / pump), measured the first time it is taken, moving with the part (`HeldWeapon.PartSolid`).
- `MagazineProp` in a hand: each held magazine's volume at its own pose (the palmed second magazine too).
- Kits carried like a bag (`HeldWeapon.IsKit`: a projectile with `Ammobox`/`Medipack`/`ResupplyCrate`, or the name)
  hide the glove instead (`HandModel.HiddenByHeld`).

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

Per-weapon overrides: `BepInEx/config/RavenfieldVR.weapons.json` (see README).

### 3.6 Physics shapes (`Weapons/ShapeFit.cs`, `Weapons/GunCollider.cs`)

- `ShapeFit` cuts a model into slices along its longest axis, splits slices at gaps, keeps the outermost vertices per
  slice in 16 directions, thins and thickens degenerate pieces (PCA) and builds convex `MeshCollider`s (box fallback when
  PhysX cannot cook). Readable meshes give vertices (capped at 8000 per mesh); unreadable meshes (about a quarter of all
  weapon meshes: 1447 of ~6070) used to join as **bounding boxes** — the "invisible square touch border".
- `GunCollider` is the held gun's physics stand-in (velocity-driven rigidbody on the prop layer, ignores the player).
  Once the gun's `GunVolume` is ready, the shape is chosen by fit to the drawn model: unreadable weapons always take the
  hull fitted from the rendered surface; readable weapons are compared once per type (share of the model covered minus
  share of the shape outside it, coarse sampling) and the better one is kept (`HeldWeapon.ChooseShape`).
- `PropPhysics` (in `GunCollider.cs`) puts loose props on a layer bullets skip, ignores the player's colliders, and now
  `IgnoreBodiesNear` (hitboxes 8, ragdolls 10, seated 16, actor colliders 17) so lying props are not pushed.

### 3.7 Holding, reloading, recoil (`Weapons/HeldWeapon.cs`, `Weapons/ReloadInteractions.cs`, `Weapons/AmmoState.cs`, `Weapons/MagazineProp.cs`)

- `HeldWeapon` re-anchors the animated weapon each frame so its gun body sits in the hand (grip point from the
  analysis), adds two-handed steering, droop and trailing for long guns held in one hand, sight-to-eye aiming detection,
  arm hiding, part overrides and **recoil**: a critically damped spring on position and rotation. Per shot: kick =
  max(0.5, game kickback) × strength; two hands on a long gun 0.4×, one hand `oneHandRecoil` × 0.4 (1.9 for rifles,
  2.6 machine guns, 2.2 shotguns, 1.5 SMGs); pistols 1.15 one-handed, 0.8 cupped. About 150°/s and 0.6 m/s per kick unit
  (≈ 3.4° climb before hand factors).
- `ReloadInteractions`: magazines ride a rail into the well and seat at full depth; B ejects (with a fresh magazine near
  the well it is a **tactical eject** into the same hand — H3VR palming); a fresh magazine driven into the seated one
  knocks it out (**mag kick**, swept window, smoothed hand speed, a light tick on the old base, no kick for 0.4 s after
  a seat); slides/bolts/handles/pumps follow the hand along their stroke; covers, belts, hinges, optics (magnifier flip).
  The pouch only answers a squeeze made at it (`_pressPalm`), and a magazine let go at the pouch is stowed only if the
  hand is slow (< 1.2 m/s) — otherwise it is thrown.
- `MagazineProp`: magazines, shells and rockets as props (copies of the weapon's own magazine meshes, baked if skinned),
  held, palmed, dropped, caught; colliders from `ShapeFit`, or from the rendered model once measured if the meshes are
  unreadable (`TakeRenderedShape`; later magazines of the type start with it).
- `AmmoState` keeps the physical ammo (magazine, chamber, seated, slide lock) and syncs `weapon.ammo`.

### 3.8 Drops, holsters, pouch, throws (`Weapons/WeaponDrops.cs`, `Weapons/WeaponSystem.cs`, `Weapons/HolsterDisplay.cs`, `Weapons/PouchDisplay.cs`, `Weapons/ThrowAndMelee.cs`, `Weapons/GrenadeDud.cs`, `Weapons/BodyZones.cs`)

- **Dropped weapons**: bots drop their weapon as an inert copy of its third-person model, now copied from **only the
  third-person part of the prefab** (`BuildWorldModelFromSubtree`; whole-prefab copy only when skinned parts use bones
  outside it). Shapes come from `ShapeFit` or, for unreadable models, from a depth volume measured during the menu
  warm-up (`MeasureRenderedShape`, scaled per drop). Drops have a grippy, low-bounce material and ignore bodies near
  them. The player's own gun let go away from the body drops with the velocity **taken when the hand opened** and
  returns to its holster after `WeaponReturnSeconds`; its collider is the held stand-in's current shape plus the inserted
  magazine.
- **Picking up** uses `GrabPressed || GrabReaching` (a squeeze up to 0.25 s before the weapon is in reach still takes
  it).
- **Holsters** by weapon type (`BodyZones`), shown as copies (`HolsterDisplay`; a type without a third-person model is
  remembered as missing instead of rebuilt every frame). The pouch sits a hand's width from every holster spot (it used
  to overlap the off-hip holster).
- **Throwables**: grenades with pins (`GrenadeDud` for pinned throws), throws with real hand velocity, melee along the
  measured blade.
- **Equip take-over**: `WeaponSystem.BeforeRender` attaches a weapon the game drew after the mod's `Update`, so it is
  never drawn where the flat game holds it (task 97).

### 3.9 UI (`UI/*`)

`CanvasManager` converts canvases (HUD frame head-locked and frustum-matched; menus placed in front), `VRInputModule`
laser pointer, `VRKeyboard` (SteamVR overlay keyboard), `VRSettingsPanel` (double-click left stick), `WristHud`,
`ModKeysPanel`, `SeatSights` (vehicle sights collimated at infinity), `UiKit` shared look.

### 3.10 Test tooling (`Core/AutoTest.cs`, `Weapons/WeaponSelfTest.cs`, `Core/Shots.cs`, `Core/PerfProbe.cs`, `Rig/HandPreview.cs`)

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
| `src/Input/VRInput.cs` | SteamVR input, edges, haptics, test hooks | input semantics |
| `build.ps1`, `tools/fetch-deps.ps1` | Roslyn build without an SDK, packaging | building |
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

In-code constants worth knowing: `GunVolume.Grow` 7.2 mm, `FineTexel` 1.5 mm, `MaxTexels` 448, `CacheSize` 12, in-flight
limit 4 (warm-up); `HandModel.HandScale` 0.9, slide steps 0…3 cm down / 1 cm up, palm settle range −4…+2 cm;
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
- **Rack grips** were measured on rifles (charging handles, bolts); pistol slides and pump shotguns have not been
  pictured by the autotest yet.
- **Left-handed play** is verified only with simulated hands.
- **Equip flash**: the frame-order gap is closed (0 frames counted); if a flash of the soldier body (rather than the
  flat arms) remains, the counter does not cover it.
- **First drop of a heavy model** can still cost ~40 ms when the menu warm-up did not reach that weapon type.
- **AT-1K** keeps an odd magazine analysis ("AK", 1060 cm) that predates this session.
- **Depth shader dependency**: without `Hidden/Internal-DepthNormalsTexture` the hands fall back to fixed curls.
- **Memory**: 12 cached volumes keep their maps (a few MB each).
- Long-standing (README): night vision and camera effects not copied to the headset; third-person/death cameras turn
  only around the vertical axis; magenta materials replaced by approximations.

---

## 11. Open Questions & Next Steps

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
