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

## Task 107: the tactical glove, after its first in-game check

`src/Rig/HandShape.cs`, `src/Rig/HandModel.cs`, `src/Core/VRConfig.cs` (new `GloveSize`).

The glove itself was done last session; these are the points from the notes of its first in-game check:
- **Hand at rest:** curl 0 was Valve's open hand, fingers spread wide. It is now Valve's relaxed hand with its curl taken
  out: each joint's open-to-relaxed rotation is split into the curl about the joint's own curl axis and the rest (the
  sideways set, 10-15 degrees at the index, ring and little knuckles), and only the sideways set is kept. Curling still
  turns about the same axes, so fists and grips close as before. Checked offline with the repo's glove renderer
  (`tools/hands/preview_glove.py` driven by a scratch script): at rest the fingers lie together, slightly curled; the
  fist and a 0.7 grip look as before.
- **Support hand on a handguard** (thumb standing up through it, fingers flat under it): the support hand's thumb now
  takes the grip thumb pose (along the side, as on a pistol frame) instead of the relaxed one, and a thumb (any hand) or
  a support hand's finger that starts inside what the hand holds is first opened out of it (up to 0.35 past straight)
  and closes from there. Before, segments that started inside never stopped the finger, so it stayed inside.
- **Gun hand hanging under the grip with the fingers closed on air** (QUICKSILVER): the search along the grip accepted a
  spot where the middle finger closed fully on nothing. It now needs the middle finger to touch the grip, and searches up
  to 4 cm toward the thumb (before 1 cm).
- **Rack grip beside the scope** (JOLT-20 S): see task 105 (rack points where the reload hand takes the handle, on the
  model's surface).
- **Size:** new setting `GloveSize` (default 0.88 as before; 0.8 to try the smaller one), applies at the next start.

Check at home: the grip close-ups (`grip-*-out/in/under`, `support-*`), the glove pictures (`-rfvr-handshots`), and the
JOLT-20 S / PATRIOT GL / QUICKSILVER in the headset.

## Task 108: working the action nudges the gun

`src/Weapons/HeldWeapon.cs` (`Nudge`, `PeakShift`), `src/Weapons/ReloadInteractions.cs`.

Small pushes go into the gun's recoil spring (which settles them back, as it does a shot's kick); light guns move more
(pistols x1.3, machine guns x0.6):
- Racking: while the hand pulls the slide, bolt, handle or pump, the gun is tugged toward the pull (2.5 x the stroke the
  hand moved that frame as velocity, so a full pull moves the gun a few millimetres), and toward the muzzle when pushed
  forward.
- A spring-driven slide or bolt slamming home jolts the gun forward (0.22 m/s) and dips the muzzle a little; a bolt or
  pump pushed home by hand, a little less.
- A magazine seating pushes the gun along the insertion direction (0.14 m/s, rocket 0.1); pulling a seated magazine out
  tugs it the other way (0.08); a break-action or cylinder snapping shut flicks it up; a feed cover pressed shut pushes
  it down a little.
- Autotest (rack-grip check): the stroke is now pulled all the way and let go; metrics `racking: mm the gun moves with
  the pull` and `... when the slide goes home`, and the rack-grip report line says both.

Check at home: those two metrics (a few millimetres each), and how it feels in the headset (the numbers are in
`ReloadInteractions`: `moved * 2.5f`, `0.22f`, `0.14f`).

## Task 110: dirt on the view from close explosions

New `src/Rig/MudSplash.cs`; `src/Rig/VRRig.cs`, `src/Core/VRConfig.cs` (new `ExplosionDirt`, on by default),
`src/Core/AutoTest.cs`.

- Every explosion that goes through the game's damage code (grenades, shells, rockets, call-ins, explosive scripts;
  `ActorManager.OnExplosion`, re-subscribed after each level load because the game clears its listeners then) within
  1.4 x its damage range of your head throws dirt on an invisible visor 25 cm in front of the eyes (drawn like the
  damage ring, on the UI layer). Following your reference (Call of Duty's lens dirt, but dirt instead of sunlit
  bokeh): 8 to 48 soft, out-of-focus spots of dirt (brown-grey, see-through, a denser rim like a dried drop, grainy)
  spread over the whole view, a third of them leaning toward the side the blast came from; more, bigger and denser the
  closer and the larger the blast.
- They stay 3-6 s; the big wet ones run slowly down the visor; then they fade.
- A blast in water (`WaterLevel.IsInWater`) throws grey, clearer drops instead, which run off faster.
- Nothing for a blast behind a wall or the ground between it and your head (a line cast against the level only), and
  nothing while dead.
- The spot pictures are drawn by the mod (4 variants), checked offline over a backdrop.
- Vehicle wrecks exploding do not go through that damage code, so they throw no dirt.

Autotest: right after the body pictures, a blast 2 m ahead-left (`mud-0s`, `mud-1-5s`, `mud-4s`) and a water blast
(`water-splash-0s`); metrics for spots right after and after 4 s. (With the 3-6 s life some spots can still be fading
at 4 s; the problem flag is only raised when nothing appeared.)

## Tasks 121 and 122 (new, from your notes): the gun against the ground and the world

`src/Weapons/GunCollider.cs` (`PropPhysics.TrackPlayer` / `PlayerGeneration` / `IsPlayerCollider`,
`GunCollider.SetHandPose` / `TryBlockedPose`, `GunProxyBody` contacts), `src/Weapons/HeldWeapon.cs`,
`src/Weapons/WeaponDrops.cs`, `src/Weapons/MagazineProp.cs`, `src/Core/VRConfig.cs` (new `WeaponWorldCollision`, on),
`src/Core/AutoTest.cs` (`WorldCollisionCheck`).

**121, a long gun against the ground pushed the player.** The held gun's physics stand-in is set to ignore the player's
colliders when it is made. Unity forgets an ignored pair whenever a collider is switched off and on, which the game does
with the player's colliders on respawn, in vehicles and on ladders. From then on the stand-in collided with the
player's own character controller, and a character controller pushes itself out of anything overlapping it: a long gun
held down against the ground (or held against your legs) pushed you around. Now a "player generation" counter changes
whenever the player's state that switches colliders changes (respawn, death, seat, ladder, falling over, controller
on/off, and once more 0.3 s later), and the stand-in, every lying weapon and every lying magazine ignore the player's
colliders again when it does. Contacts with the player's own colliders never count as the world holding the gun.

**122, guns passed through walls like ghosts.** The stand-in already collided with the level (it is on a layer that
collides with the level's layers), but the gun you see followed the hand regardless. Now:
- The stand-in chases where the hand wants the gun (not where the gun was drawn last frame) and is interpolated.
- While it touches something solid that does not give way (the level, a vehicle, a body; loose props are pushed instead),
  the gun is drawn where the stand-in is stopped, blended in within a few hundredths of a second: pushed into a wall it
  stays at the wall, lowered onto a sandbag or a crate it rests on it, pushed into the ground it stays on top. The
  glove on the weapon's own grip pose and the support hand on its handguard stay on the gun; shots go where the drawn
  gun points.
- Free again (no contact for 0.12 s), it eases back to the hand in 0.1 s. The stand-in jumps back to the hand if the
  hand goes more than 45 cm through something (before 30 cm).
- `WeaponWorldCollision = false` gives the old behaviour.

Also for task 114: lying weapons and magazines ignore the bodies around them afresh every 2 s (a ragdoll switched off
and on collided with them again and pushed them).

Autotest: with the first rifle, a wall is put in front of the player and the gun hand pushed 50 cm into it (pictures
`world-wall`, `world-wall-side`), then pushed down into the ground (`world-ground`); report line `gun against the
world: ...` and metrics `gun pushed into a wall: muzzle inside it (0/1)` (must be 0) and `gun pushed into the ground: cm
the player moved` (must be about 0).

## Task 117 (new, from your note): flip magnifiers stay where the hand put them

`src/Weapons/PoseSampler.cs` (`ModePoses`), `src/Weapons/WeaponAnalyzer.cs` (`AnalyzeSightParts`),
`src/Weapons/WeaponRig.cs` (`SightPartPaths`, `SightPartPoses`), `src/Weapons/HeldWeapon.cs` (`ApplySightParts`),
`src/Core/AutoTest.cs`.

Pulling a magnifier switched the weapon's sight mode, but the weapon's animation only moves a magnifier into its
place in the aim pose, and in VR the game is told you aim only while your eye is at the sight: the magnifier showed
while you looked through it and folded away when you lowered the gun. Now the analysis samples every part's pose
(relative to the gun) in each sight mode while aiming, and keeps the parts that a sight mode moves (up to 8: a flip
magnifier, a folding sight). While the weapon is held, those parts are put where the selected sight mode puts them,
every frame and whether you aim or not, swinging over in about a sixth of a second when you switch. They go back to
the animation when the weapon is put away. Weapons whose magnifier is a script key (a mod's own toggle) are unchanged.
The weapon report notes `N part(s) moved by the sight mode`.

Autotest: on the first such weapon, the sight mode is switched with the gun held low (picture
`sight-mode-N-not-aiming-...`), line `sight mode a -> b not aiming: ... where the mode puts them` and a metric (mm off).

## Tasks 118, 119, 120 (new, from your notes): the machine-gun belt

`src/Weapons/ReloadInteractions.cs` (belt states, `UpdateBeltStrip`), `src/Weapons/BeltStrip.cs`,
`src/Weapons/WeaponAnalyzer.cs` (`FindBeltParts`), `src/Weapons/WeaponRig.cs` (`BeltPartPaths`),
`src/Weapons/HeldWeapon.cs` (`ApplyBeltParts`).

- **118, pulled out too far, the belt vanished:** the strip drew at most 24 rounds back from the hand (28 cm) while the
  hand could go 45 cm before the belt was let go and hidden. Now the belt comes out of the box at most 32 cm (the strip
  stops there even if the hand goes on); pulled 6 cm further it slips from the fingers and hangs from the box mouth,
  where it can be taken again (anywhere along it). It never disappears while the box is in and the belt not laid.
- **119, a belt stayed on the gun:** the gun model's own belt (links and rounds between the box and the feed, a mesh of
  its own apart from the box) never hid. The analysis now finds such parts (belt-named, visible at rest, near the feed
  tray or the box, not part of the box, the slide or the cover) on guns with a feed cover; they are hidden while no
  box is in or its belt is not laid, and shown when it is. The report notes `belt in the model: ...`.
- **120, the laid belt did not show:** with the model's belt shown once laid (119) it does; on a gun whose model has no
  belt of its own, the strip now stays draped from the box mouth onto the feed tray after you laid it, until the box
  comes out.

Autotest (machine guns): `belt pulled too far: slipped from the hand and hangs from the box` and `belt laid: the
model's own belt (...) shows / the strip stays on the feed tray`.

## Task 109: Boneworks gun features

Compared with what Boneworks has (researched from reviews, the Boneworks/Bonelab modding sources and player guides):

| Boneworks | This mod |
| --- | --- |
| Weight and inertia, harder one-handed | had it (droop, trailing wrist, one-hand recoil by class) |
| Second hand steadies recoil and aim | had it |
| Gun collides with the world | **new** (task 122) |
| Chambered round; racking ejects a live round | had it (now also visible, see below) |
| Physical spent cases | **new** |
| Slide locks back empty, slide release | had it |
| Mag eject, pulling the mag, tap reload | had it (mag kick, H3VR palming) |
| Ammo pouch matching the held gun | had it |
| Body slots (shoulders, under the arms, back) | **changed** to the Boneworks layout (task 116) |
| Ammo count | had it (gun counter, looking at the magazine) |
| Attachments (laser, light) | had it (mod keys) |
| Stock bearing on the body | **new** (shouldered stock) |
| Press check (looking into the chamber) | **new** |
| Grabbing the gun anywhere | the support hand anywhere on the handguard and the action; the gun hand on the grip (not added) |

New:
- **Spent cases** (`src/Weapons/Casings.cs`, new): a self-loading gun throws a brass case out of its ejection port
  (where the game's casing particles are) with every shot, to the right, up and a little back, with the hand's speed;
  a bolt or pump gun throws the fired case (or a live round) when the action is worked; a revolver or a break-action
  drops its fired cases when opened. Four sizes (9 mm, 5.56, 7.62, 12-gauge shell with a red hull) chosen from the
  weapon class; they tumble, bounce with a synthesised brass tink (new `MechSound.Casing`) and lie for 25 s (40 at
  most, oldest first), on the prop layer (bullets pass, the player never touches them). Only weapons that have casing
  particles in the flat game throw cases; the held weapon's casing particles are hidden instead.
- **Press check:** with the slide or bolt eased back between 12% and 80% of its stroke (not far enough to eject), the
  chambered round (or a bolt gun's fired case) shows in the ejection port.
- **Shouldered stock:** a long gun whose stock butt (its rearmost part, 12 cm or more behind the grip) is within 12 cm of
  your weapon-side shoulder pocket kicks 25% less and does not droop, held in one hand or two.
- Autotest: `cases thrown: N` per fired weapon that ejects, metric `cases thrown per shot`.

## Task 111: hand poses (at least as many as Boneworks)

New `src/Rig/HandPoses.cs`; `src/Rig/HandModel.cs`, `src/Rig/HandShape.cs` (`ThumbFist`), `src/Rig/HandPreview.cs`
(`CapturePoseSheet`), `src/Weapons/HeldWeapon.cs`, `src/Weapons/ReloadInteractions.cs`, `src/Rig/CockpitControls.cs`,
`src/Weapons/WeaponSelfTest.cs`.

Boneworks' pose library, as extracted by its modding community, has 30 hand poses (shape grips such as cylinder, box,
sphere and pinch, plus gun poses such as Glock, slide, magazine, charging handle and foregrip). The mod had per-role
finger targets (gun hand, support hand, magazine, pinch, pointer) on top of closing every finger on the measured shape
of what it holds. It now has a named library of **30 poses**, each with how far each finger closes and where the thumb's
base turns (from its rest to the fist), chosen from what the hand is doing and holding:

- Free hand: Relaxed (tracked), Fist (controllers without finger tracking, grip and trigger pressed), LaserPoint (menus).
- Gun hand, by weapon class: PistolGrip, RifleGrip, MachineGunGrip, LauncherGrip, KnifeGrip, ToolGrip (long melee),
  Grenade.
- Support hand: PistolCup, Handguard, LauncherTube, TwoHandedMelee.
- Working the action: PistolSlide (overhand claw), BoltHandle (pinch), ChargingHandle (two-finger hook), Pump.
- Magazines, by size and kind: RifleMagazine, PistolMagazine, BoxMagazine (machine gun box or drum), TwoMagazines
  (H3VR palming), Shell (pinched), Rocket.
- Other: CoverLift, PinPull (index hooked through the ring), BeltPinch, OpticFlip (thumb pushing), SteeringWheel,
  FlightStick (index on the trigger).

The tracked fingers still close further than a pose, contact closing still shapes the hand onto measured models, and
weapons with their own authored hand pose still use it for the gun hand and the support hand (the library is what they
fall back to, and what everything else uses). The flat self-test now also writes `BepInEx/RavenfieldVR-hand-poses.png`
(every pose on a right glove with its name) and the report line `hand poses ... 30`; `-rfvr-handshots` writes it too.

## Task 37: the weapon's own arms (option next to the VR hands)

`src/Weapons/ArmIK.cs`, `src/Core/VRConfig.cs` (`HandsNow`, `TestHands`), `src/Weapons/HeldWeapon.cs`,
`src/Core/AutoTest.cs` (`WeaponArmsCheck`).

`Hands = WeaponArms` (settings panel "Hands: Weapon's arms") already re-rooted both of the weapon's arms at shoulders on
your body, kept the gun hand on its authored grip, put the off hand on the authored support grip while it holds the
gun and on the controller otherwise, with two-bone IK, elbow poles and a shoulder that slides when out of reach. What
the task still asked for, now added:
- The off arm eases between the support grip and the controller over a tenth of a second (it snapped).
- Working a bolt, handle or cover with the off hand, the weapon's own hand settles onto it as the VR glove does (the
  grasp anchor, up to 5 cm).
- The solve reports how far each hand bone ended from its target and whether a shoulder had to slide.
- Autotest: on the first rifle with both arm chains, the arms mode is switched on in memory only (never saved), the
  weapon drawn again, the off hand put on the handguard and then let go; pictures `weapon-arms-*-on-the-handguard` /
  `-free-hand`; line `weapon's arms: gun hand x cm off its grip, off hand y cm off the handguard and z cm off the
  controller` (problem above 2 cm), metrics.

Known limit (unchanged): for left-handed play the gun arm is still the model's right arm (meshes are not mirrored).

## Task 33: performance pass (code part; the measurement needs the game)

`src/UI/CanvasManager.cs`, `src/UI/UISystem.cs`, `src/UI/VRInputModule.cs`, `src/Rig/VRRig.cs`,
`src/Weapons/GunCollider.cs`, `src/Weapons/WeaponDrops.cs`.

The instrumentation the task asks for already existed (`-rfvr-perf`: every per-frame section's time, allocations and
the share of calls that allocated; frame-time percentiles; gen-0 collections). Per-frame allocations and repeated
searches found by reading the hot paths and removed:
- The laser's list of panels was rebuilt every frame from an iterator (a new enumerator object per frame, plus
  `AddRange` copying through an array), and the menu check iterated it once more: the canvas manager now fills the
  list directly and keeps an `AnyVisible` flag. The input module holds the panels as a `List` (iterated without a
  boxed enumerator).
- In menus and death cams the rig's camera fallback read `Camera.allCameras` (a new array every call, every frame):
  now a reused buffer with `Camera.GetAllCameras`.
- A menu canvas's role check allocated an array of its buttons twice a second: now a reused list.
- The new resting-props check (task 114) walked read-only list interfaces (boxed enumerators): index loops, and a
  dropped weapon's rigidbody is looked up once.
- The player's colliders were gathered by walking the player's whole hierarchy every frame they were needed (every
  physics contact of the held gun now asks): gathered when the player changes (respawn, seat, ladder) and at most once a
  second, with a set for the lookups.

To fill in at home: run the same short VR session (or the quick VR autotest) with `-rfvr-perf` on `main` and on this
branch, and compare the "Mod sections" block (`% of calls that allocated` should be 0 for UI.Update, Rig.Update,
Rig.BeforeRender and Input in steady play) and the frame-time line:

| Section | main avg ms / % allocating | branch avg ms / % allocating |
| --- | --- | --- |
| UI.Update | | |
| Rig.Update | | |
| Weapons.BeforeRender | | |
| HandModel | | |
| frame time median / 95th | | |

---

## Task 112: bug hunt, 20 rounds

Each round swept one area: read the code, traced what breaks it, fixed only what could be shown to happen, and ran
the compile check again (no new diagnostics after any round). Severity: Major = wrong under common conditions, Minor =
edge case or rare, Tiny = defensive gap. None of this has been run in the game.

### Round 1: the loading view (`src/UI/LoadingView.cs`, `src/UI/CanvasManager.cs`)

- **Minor:** the loading scene's cameras were switched to draw into the picture during `Update`, before the camera
  suppressor runs in `LateUpdate`. The suppressor skips render-texture cameras, so they kept their headset eye setting
  and Unity's automatic head tracking, which can move a camera with the headset or draw it as an eye. Fix: a captured
  camera gets `stereoTargetEye = None` and its automatic XR tracking switched off, as the suppressor does for screen cameras.
- **Minor:** if the loading scene had no camera up yet on its first frame, its canvases were not registered. The canvas
  manager scans right after the loading view, so it could turn "LOADING ..." into a world-space panel that the loading
  view then skipped (the UI camera draws only the screen while loading). The loading text would then be missing. Fix:
  the canvases are registered every frame, and drawn into the picture once a camera exists.
- **Tiny:** every map load added the loading scene's canvases to the canvas manager's own-canvas set and never removed
  them. Fix: destroyed canvases are dropped from it on each scene change.

### Round 2: fired cases, the belt rounds, explosion dirt (`src/Weapons/Casings.cs`, `src/Weapons/BeltStrip.cs`, `src/Rig/MudSplash.cs`)

- **Major:** the turned meshes of the fired cases, live rounds and shotgun shells, and of the MG belt's rounds and
  links, were wound inside out. Unity draws triangles that are clockwise as seen from the camera, and every triangle
  here faced inward (checked numerically: 120 of 120 faces against the outward normals). With the Standard shader's
  back-face culling you saw only the far inner wall of each case, lit from the wrong side (dark and hollow-looking).
  Fix: the winding is reversed in both builders, so the outside is drawn (checked again: every curved face points out).
- The dirt on the view was checked: layer, draw order, near clip, pool reuse, resubscribing per level, the
  wall check. Nothing to fix.

### Round 3: the gun in your hand against the world (`src/Weapons/GunCollider.cs`)

- **Minor:** a magazine you picked up had its colliders switched off, and Unity then forgets its ignored pairs with
  the gun. If it had lain still before, it stayed marked "ignored" with no real ignore behind it. Set down gently
  (never faster than 0.8 m/s), the gun shoved it again: the "things on the ground float away" bug for magazines you
  put down. Fix: a prop that becomes physical again is judged from scratch.
- **Minor:** the same after leaving a vehicle: the gun's pieces were switched off while seated, so every "lying still,
  ignored" mark was stale. Fix: the marks and the pass-through list are cleared when the pieces come back on.
- **Minor:** "the gun is held back by the world" was a real-time window of 0.12 s refreshed by physics contacts. In the
  game's slow motion (it has `TriggerSlowmotion` / `SetTimeScale`) the physics steps come further apart in real time,
  so the gun could flicker between held back and inside the wall. Fix: the window is in game time and at least three
  physics steps long.
- **Minor:** only static and kinematic things held the gun back, so it went into vehicles (non-kinematic, heavy) even
  though the code comment said vehicles count. Fix: a moving body of 30 kg or more holds it back like the level. Loose
  props and ragdoll limbs are still pushed.

### Round 4: the magazine rail, mag kick and pistol pull-in (`src/Weapons/ReloadInteractions.cs`)

- Traced the rail (lining up, pushing along, letting go half way), the kick's windows and timers, the pull-in for
  wells in the grip (finishing on let-go, cancelled when the gun no longer takes it), seating each kind of prop, the
  palmed second magazine, and finishing all of it when the weapon goes away. Rounds in and out balance on every path.
- **Tiny:** the off-hand glove kept its two-magazines pose after the weapon was put away with a magazine palmed (the
  palmed one is dropped then). The hand still holding the other magazine showed the wrong grip until another gun was
  drawn. Fix: cleared on release.

### Round 5: the machine-gun belt (`src/Weapons/ReloadInteractions.cs`, `src/Weapons/BeltStrip.cs`, `src/Weapons/HeldWeapon.cs`)

- Traced taking the belt (at the box mouth and along the hanging belt), the pull limit, slipping and hanging, laying it
  on the tray, closing the cover over it, letting go, switching weapons mid-pull, and the model's own belt hidden
  until laid and restored on release. All consistent.
- **Minor:** the belt drawn from the box always showed up to 28 rounds, even from a box with 3 rounds left (or none).
  Fix: the strip shows at most the rounds left in the box, counted from the hand (or tray) end, so a short belt ends
  short of the box.

### Round 6: holsters and hand poses (`src/Weapons/BodyZones.cs`, `src/Weapons/HolsterDisplay.cs`, `src/Rig/HandPoses.cs`)

- Swept, no real bugs found. Checked:
  - which hand reaches which spot (holsters only take the weapon hand, the pouch the off hand, so the pouch and the
    off-side belt gear 13 cm apart never compete);
  - the order spots fill in for each kind of weapon in both layouts;
  - the shouldered-rifle grip hand against the underarm spots (more than the holster radius away);
  - each display orientation (barrel and top are orthogonal or projected);
  - the hide-near-the-eyes rule;
  - the pose library (30 poses, one per id, the index built after the list) and the pose chosen for every hand role.

### Round 7: weapon analysis (`src/Weapons/WeaponAnalyzer.cs`)

- Swept, no real bugs found. Checked:
  - the parts a sight mode moves (index paths survive duplicate names, missing modes fall back to mode 0, at most 8 parts);
  - the belt-part search (box, slide and cover excluded, near the tray or box, outermost part only);
  - the reload-motion magazine search with the hidden-helper and optic filters, and the parent climb;
  - the mag-kick rule by slot and measured grip;
  - the reload-hand rack stroke and when it replaces a found moving part.

### Round 8: the autotest harness (`src/Core/AutoTest.cs`)

- **Minor:** when the left-handed check or the weapon's-arms check found the weapon no longer in the hand afterwards,
  it wrote the failure into the weapon's lines and skipped to the next weapon. The lines are only reported at the end
  of the loop body, so the failure, and everything else measured for that weapon, never reached the report. Fix: the
  reporting moved into `ReportWeapon`, which also runs before those early exits.
- **Minor:** the menu test (task 103's measurement) counted the frame each picture was taken in. Rendering and
  saving a picture is a hitch of its own, and with 10 pictures in ~900 frames those are exactly the p99 and max
  frame times. Fix: the frame after each picture is left out of the frame times.
- Also checked: the wall test (wall removed in `finally`, scripted hand cleared), the load pictures, the sight-mode,
  pouch and arms checks (test overrides restored in `finally`).

### Round 9: dropped weapons and magazines (`src/Weapons/GunCollider.cs` `PropPhysics`, `src/Weapons/WeaponDrops.cs`, `src/Weapons/MagazineProp.cs`)

- **Minor:** the bodies around a dropped weapon or magazine were found with a fixed buffer of 64 colliders. A soldier
  has about 20 (ragdoll and hitboxes), so a weapon dropped by a soldier who fell inside his squad filled it. The query
  then returned the same first 64 every pass, and the soldiers beyond them kept shoving the drop (task 114's "floating
  away"). Fix: the buffer grows (up to 1024) until every body fits.
- Also checked: the re-ignore of bodies every two seconds and of the player on every respawn, seat or ladder change;
  picking up and swapping a weapon (ammo carried both ways); expiry and the cap (your own weapon waiting to return
  is never culled).

### Round 10: input and the menu warm-up (`src/Input/SteamVR/*.json`, `src/UI/VRInputModule.cs`, `src/Weapons/WeaponWarmup.cs`)

- All five binding files parse, and every action they bind exists in `actions.json`. The HP G2 layout matches Touch
  (Y: map, hold: pause, double: loadout; left stick double-click: VR settings, with mod keys inside).
- **Minor:** the weapon warm-up checked "a match is running" first and then waited until the menu had been left alone
  for 1.5 s. A map that loaded within those 1.5 s (Start clicked, then hands off) got one weapon analysis (tens of ms)
  at the start of the match. Fix: both conditions are checked together on every pass.

### Round 11: ammunition accounting (`src/Weapons/AmmoState.cs`, `src/Weapons/ReloadInteractions.cs`)

- **Minor:** opening a break-action or a revolver dropped "capacity minus loaded" fired cases every time it was
  opened. Open, close and open again on an empty double-barrel: two more cases fell out each time, out of nothing (and
  a gun spawned part-loaded dropped cases it never fired). Fix: the weapon's ammo state counts the cases fired since it
  was last opened, and opening drops exactly those.
- **Tiny:** taking over the game's ammo (spawn, the game's own reload) left a bolt gun's "spent case in the chamber"
  mark set while the chamber was given a live round. Fix: cleared there, together with the new count.
- Also checked (they balance): firing, the magazine out and in (overflow back to the reserve), racking with and without
  losing the round, loading single rounds, outside refills and drains, and the pouch's fresh magazine with and
  without chamber +1.

### Round 12: the rig in the main menu and the glove's fingers (`src/Rig/VRRig.cs`, `src/Rig/HandModel.cs`)

- Main menu: the view stays where the menu camera first was, a jump of 2 m or more is a cut through a fade, and
  leaving and coming back re-anchors. During loading the rig keeps its place and only the screen is drawn. Nothing to
  fix.
- Fingers: opening a thumb (or a support finger) out of the solid first, the slide search that now needs a touch, and
  the pose minimums per role. All bounded, no new loops.
- **Tiny:** the fist a controller without finger tracking makes (grip and trigger squeezed with an empty hand) kept
  the thumb at its relaxed rest, sticking up beside the curled fingers. Empty hands keep the rest so the tracked thumb
  can move from there, but this fist has no tracked thumb. Fix: that fist turns the thumb's base over the fingers as
  its pose says.
