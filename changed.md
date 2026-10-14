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
