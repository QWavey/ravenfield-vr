# Changes on branch `tasks-98-113`

Work on the open items of `Tasks.md` (98-113, plus 33 and 37, and the new items 114-122 from your notes during the
session), done on 2026-10-14 on a PC **without Ravenfield or Steam installed**. Nothing here has been run in the game
yet. Every change was compile-checked: the whole `src/` against Unity 2020.3.49's modules, BepInEx, Harmony and the
game's own `Assembly-CSharp` / `-firstpass` / `UnityEngine.UI` / TextMeshPro of version 1.08.26 (the reference DLLs the
other Ravenfield VR mod keeps in its repository; publicized with this repo's Publicizer, kept outside the repo), with a
stub only for the post-processing package. Your installed game is newer than 1.08.26: 5 members the mod already used
before this branch (`SoundBank.clipVolume`, `Actor.PlaySwingAnimation`, `LocalPlayer.GetCurrentCameraIncludeSpectator`,
an 8-argument `MeleeWeapon.SphereCast`) do not exist in that DLL; the check compared against `main` and this branch adds
no compile error of its own. It still needs the real build, the self-test, the VR autotest and a headset session.

`main` is untouched; this branch holds everything below.

How to check at home:

```powershell
./tools/fetch-deps.ps1
./build.ps1 -Package
./install.ps1
# flat self-test, then the VR autotest on the null driver (PROJECT_DOCUMENTATION.md, section 6)
```

Each section names the files touched and what to look at in the test report or the headset.

---

## Task 104: the Steam menu button no longer opens Ravenfield's pause menu

Already done in the previous session for the Quest (Touch) bindings: the left menu button is SteamVR's and the dashboard
pauses the match without the game's menu (`VRManager.OnDashboard`).

Changed now:
- `src/Input/SteamVR/bindings_hpmotioncontroller.json`: the HP Reverb G2 profile ("same layout as Touch") still bound the
  left menu button to Pause and Y-hold to Mod keys. It now matches Touch: Y = map, hold = pause, double = loadout; the
  menu buttons are left to SteamVR; mod keys are on the settings panel.

Check at home: on the Quest, the menu button opens only the SteamVR dashboard; closing it resumes the match.

## Task 98: mag kick works more often

`src/Weapons/ReloadInteractions.cs` (`TryMagKick`), `src/Weapons/MagazineProp.cs` (`CrossHalf`).

The headset log showed kicks held back by the angle limit (61-126 deg) and the lateral limit (9-10 cm). Now:
- Any magazine of the gun kicks, empty ones too (before: only magazines with rounds).
- A slow, firm push counts as well as a fast strike: the fresh magazine's top pressed at least 1.2 cm past the old
  magazine's base and held there 0.12 s (or pushed 3 cm in), not being pulled back out.
- The window scales with the magazine: a strike may land anywhere within 2x the magazine's half width + 2.5 cm of the
  base centre (6-13 cm; before a fixed 7 cm), a slow push within half the width + 2.5 cm; how far below and past the base
  counts scales with the magazine's length.
- Tilt limit 75 deg (before 60 deg).
- Strike speeds a little lower (0.2 m/s in the window, 0.08 m/s when the base is crossed between frames; before 0.25 /
  0.12).
- The headset log line now says whether a kick was a strike or a push, and the reach used.

Autotest (`H3vrReloadSelfCheck`): the mag kick is now tried four ways on the test rifle: a fast strike (0.9 m/s), a slow
push (0.08 m/s), tilted 45 deg (0.6 m/s), and a magazine passing 20 cm beside the gun, which must knock nothing out.
Metrics `mag kick (...): knocked out (0/1)` per way. A magazine knocked out by an earlier pass is no longer counted as
this pass's.

Check at home: report lines `mag kick, fast strike / slow push / tilted 45 deg: old magazine knocked out ... seated`
and `mag kick, passing beside the gun: nothing knocked out`; in the headset, kicks on the M4 and AK slow and fast.

Follow-up from your note (M4s): the kick rule (`WeaponAnalyzer.MagKickRule`) could switch the kick off on rifles. A
weapon whose grip position was only estimated (no right-hand bone) could have its "grip" land on the magazine well,
which read as "magazine in the grip"; and a rifle whose support hand holds the magazine well is marked as held like a
pistol, which also switched the kick off. Now only a measured grip can say "magazine in the grip", and handguns are
decided by their slot (secondaries), not by where the support hand sits. The reason is written to the weapon report
(`mag kick off (handgun / magazine in the grip / deep well)`).

## Task 99: black box / shadow under RPGs

`src/Weapons/WeaponAnalyzer.cs`, `src/Weapons/PouchDisplay.cs`, `src/Weapons/HeldWeapon.cs`, `src/Core/AutoTest.cs`.

The analysis took the RPG-7's `Reload_Cube` (80 cm) for the rocket: a helper the mod's reload animation only shows
while reloading. The pouch shows a copy of "what the gun feeds from" at your belt, so a full-size copy of that cube hung
there, casting a dark square shadow on the ground under you (your description: a black cube shadow under yourself).
- Magazine/rocket search: a part that is hidden at rest (inactive, scaled to nothing, or only shadow-casting renderers)
  is never taken, nor are sights that come off in the reload (the Javelin's command unit "Javelin Scope", 240 cm) or
  anything over 1.6 m. A part named like a magazine but hidden at rest is not used either. The report notes
  `'...' moves in the reload but is hidden at rest (helper, not the magazine)`.
- The pouch display casts no shadow at all (it is a picture of what you can take, not an object).
- Renderers of the held weapon that only cast a shadow (`ShadowsOnly`) are switched off while it is held (given back
  when it is put away).
- Autotest: every launcher is in the quick run and pictured, plus a picture looking down at the ground and the pouch
  (`weapon-NNN-<name>-looking-down`), and the report line `launcher: rocket from <method>, <size> cm`.

Check at home: the self-test report for RPG-7 / RPG-7V, SMAW, Javelin, Carl Gustav, Panzerfaust (which part is the
rocket now), and the looking-down pictures; in the headset, no shadow under you with the RPG.

## Task 100: the wrench (and tools) too close to the face, holsters laid out like Boneworks (new task 116)

You clarified that the wrench is too near the face while it is holstered, not in the hand. Tools were carried upright
in the middle of the chest, about 34 cm under the eyes, so a 30-40 cm tool stood up into view. Following your request,
the holsters now copy Boneworks (researched from Boneworks/Bonelab mod sources and player guides: long guns behind the
shoulders, pistols high under the arms, small items at the small of the back, the ammo pouch on the belt at the side of
the off-hand hip, slots following the torso rather than head pitch).

`src/Core/VRConfig.cs` (new `HolsterLayout`, default `Boneworks`), `src/Weapons/BodyZones.cs`,
`src/Weapons/HolsterDisplay.cs`, `src/UI/VRSettingsPanel.cs` ("Holsters: Boneworks / Classic" on the panel).

Boneworks layout (new default):
- Rifles, launchers, long melee: behind the shoulders (weapon-hand side first), shown hanging down the back with the
  grip at the shoulder and the muzzle low (before: muzzle up beside the head).
- Pistols and small guns: under the arms, just below the armpit against the ribs (weapon-hand side first), shown
  muzzle-down along the ribs with the grip forward.
- Gear (grenades, tools, kits): the first at the small of the back (lying across it, handle toward the weapon hand),
  the next on the belt's front at the weapon-hand side, then the other side, hanging handle-up.
- Ammo pouch ("Hip"): on the belt at the side of the off-hand hip ("Other hip": the weapon-hand side).
- Nothing is carried in front of the chest any more, so re-gripping a shouldered rifle never touches a holster.

Classic layout (the earlier one, still selectable): unchanged, except that gear on the chest now lies diagonally
across it (head toward the weapon-hand shoulder) and 7 cm lower, so no tool stands up into view.

Holsters are still taken with the weapon hand and the pouch with the other hand, as before.

Check at home: the body pictures of the autotest (`body-looking-down`, `body-from-behind`, `body-from-the-side`), and
in the headset whether the spots are easy to reach. The spot offsets are in `BodyZones.ComputeSpots` if something
needs moving (underarm: 28 cm under the neck pivot, 17 cm to the side).

## Task 114 (new, from your note): things lying on the ground float away

`src/Weapons/GunCollider.cs`, `src/Weapons/WeaponDrops.cs`, `src/Weapons/MagazineProp.cs`.

Two causes found in the code:
- The physics stand-in of the gun in your hand chases the hand by velocity and collides with loose props (on purpose:
  dropped magazines bounce off the gun). It shoved anything it touched with effectively unlimited force: bending down
  with the gun low next to a weapon or magazine you wanted to pick up pushed it away, and when the stand-in jumped back
  to the gun (held back 30 cm) it could land inside a lying item and pop it out. Now weapons and magazines that have
  been lying still for a moment are ignored by the stand-in; anything falling or flying (faster than 0.8 m/s) still
  bounces off the gun.
- A soldier's weapon is created inside his dying ragdoll; the bodies around it were only ignored from the next frame,
  after a physics step had already pushed it out of them (it skidded or floated off). Drops and dropped magazines now
  ignore the bodies around them before their first physics step.

Check at home: the autotest's ground checks (`... on the ground: still`, walker checks); in the headset, pick up
weapons and magazines near your feet with the gun held low.

## Task 115 (new, from your note): pistol magazines blocked by the controllers

`src/Weapons/ReloadInteractions.cs` (`UpdateHeldProp`, `WellInGrip`).

A pistol's magazine well is in its grip, where your gun hand's controller is, so the two controllers meet before the
magazine is all the way in. For wells inside the grip (pistols, magazine-in-grip designs) a magazine that is lined up
on the rail and at least 45% in is now drawn home the rest of the way (about 0.06 s, a light click in the hand) and
seats, even if you let go at that moment. Rifle magazines still need the full push.

## Tasks 101 and 102: map loading (cyan-blue picture, loading screen far away)

New `src/UI/LoadingView.cs`; `src/UI/UISystem.cs`, `src/Rig/VRRig.cs` (`Blackout`), `src/Core/Shots.cs`,
`src/Core/AutoTest.cs`.

What the game does (from its code, via public mod sources): `GameManager.StartLevel` loads the loading scene (build
index 2). `LoadingScreen` puts a random 3D decoration in front of `LoadingScreenCamera`, which sits 10 m back
(`staticOffset (0, 1.6, -10)`) and sways a few degrees; a canvas says "LOADING <map>". Then the map's scene is loaded
asynchronously and set up over a few more frames. In VR the rig followed the loading camera (decoration 10 m away,
swaying), the "LOADING" canvas became a HUD frame 4 m out, and once the map's scene took over the headset showed
whatever that scene's first camera cleared to until the level was ready (the cyan-blue for 2-3 seconds).

Now, from the moment the loading scene is active:
- The loading scene's cameras draw into a 1600x900 picture (with its canvases, which are no longer turned into a world
  panel), shown on a 2.4 m wide screen 2.5 m in front of you at eye height, in a dark room: the world camera draws
  nothing and clears to black, the UI camera draws only the screen. The screen comes back in front of you if you turn
  away for 1.5 s. The camera's sway shows on the screen only, never moves you.
- When the map's scene takes over, the last loading picture stays up until the game shows something (you are deployed,
  the loadout or a menu is open, or you spectate), plus 0.4 s; at most 20 s. Going back to the main menu ends it.

Autotest: pictures every half second from the loading screen to the start of the match (`load-NN[-loading].png`).
Check at home: those pictures (no sky-coloured frame), and in the headset the loading screen at a comfortable distance.

## Task 103: main menu turning, flickering and lagging

`src/Rig/VRRig.cs` (Follow mode in the main menu), `src/Weapons/WeaponWarmup.cs`, `src/UI/VRInputModule.cs`
(`LastUsed`), `src/Core/AutoTest.cs` (`-rfvr-menutest`).

- The menu's background camera drifts and turns over the map, and the rig followed it (position and heading), so the
  world turned around you while you read the menu. In the main menu you now stay where that camera first was; only a
  real cut (the camera put somewhere else, 2 m or more in one frame) moves you, through a fade.
- The weapon warm-up analysed one weapon every frame; one analysis takes tens of milliseconds, so the menu ran as a
  string of long frames (lag, reprojection flicker). Now: after each weapon the warm-up rests a frame per 6 ms it took
  (about 6 ms of warm-up per frame on average), and it pauses while you use the menu (pointing along it, clicking,
  scrolling; it goes on 1.5 s after you stop). The whole warm-up takes longer (it still finishes in the menu).
- New `-rfvr-menutest` (with VR): ten seconds in the main menu during the warm-up; a picture every second
  (`menu-NN.png`), how far the view moved (should be 0), frame-time percentiles and frames over the 90 Hz budget, in
  `BepInEx/RavenfieldVR-menutest.txt`; then the game quits.

Check at home: `ravenfield.exe -vr -rfvr-menutest` and read the report; in the headset, a still menu that keeps up while
picking a map.

## Task 105: bolts that do not move, rack point inside the gun

`src/Weapons/WeaponAnalyzer.cs` (`RackStrokeFromHands`, `FinishVirtualParts`), `src/Weapons/WeaponRig.cs`
(`RackOnSurface`, `CarveHandle`), `src/Weapons/HeldWeapon.cs` (`RackPointGun`, `SnapRackPoint`, `VisibleHandle`),
`src/Weapons/BoltPiece.cs`, `src/Weapons/ReloadInteractions.cs`, `src/Core/AutoTest.cs`.

- Where the weapon's own reload animation racks (a hand near the receiver moving straight back) is now looked for on
  every animated self-loading or manual weapon, not only on those with no moving part. On a rifle whose part that moves
  when firing is a carrier inside the receiver (its rack point more than 3 cm from where the reload hand takes hold, no
  separate charging handle) the hand now takes the handle where the reload hand does. If that spot is not on the moving
  part, the handle there is baked into the receiver's mesh: it is cut out (as for weapons with no moving part) and moves
  back with the pull, together with the carrier inside. Pistol slides and separate charging handles are unchanged.
- Estimated rack points (from the reload hand, or the virtual one above the grip on the 72 weapons with no moving part)
  are put on the drawn model's surface once it is measured: moved straight out from the bore until they leave the
  model (out of the receiver), or in until they meet it (onto the receiver). An added or cut handle follows the new
  point. The log says `rack point put on the model's surface (...)`.
- Autotest: rack grips are now checked on up to 4 rifles, 3 pistols and 3 pump / bolt guns (before: the first 8
  pictured weapons, all rifles), and report what moves with the hand (`the charging handle`, `the slide / bolt`,
  `a piece cut out of the receiver (and the carrier inside)`, `an added handle`, or `NOTHING VISIBLE`, which counts as
  a problem), with the metric `rack grip: something visible moves with the pull (0/1)`.

Check at home: the rack-grip pictures (`rack-grip-*`, `rack-grip-pulled-*`) of the JOLT-20 S, M4s and AKs, a pistol and
a pump; in the headset, whether the bolt handle is where you reach for it and moves back.

## Task 106: pouch magazines (implemented in the last session, check added)

The placement fix from 2026-10-13 is unchanged (it now applies at the new pouch spot of the Boneworks layout too). The
autotest now also takes a magazine from the pouch with the first pistol (`pouch: ...` line for a pistol and for a
rifle), as the task's check asks.
