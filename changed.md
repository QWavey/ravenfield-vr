# Changes on branch `tasks-98-113`

Work on the open items of `Tasks.md` (98-113, plus 33 and 37), done on 2026-10-14 on a PC **without Ravenfield or
Steam installed**. Nothing here has been run in the game yet: every change was compile-checked against Unity 2020.3.49,
BepInEx and Harmony (the game's own `Assembly-CSharp` was not available, so lines that touch game types were checked by
reading only) and needs the build, self-test, VR autotest and a headset session at home.

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
