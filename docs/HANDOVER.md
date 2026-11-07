# Handover

Where the work stands, for whoever picks it up next, a compacted session included. Kept current during a run; the detail is in `docs/Tasks.md` (the newest numbered entry) and the mechanisms in `docs/PROJECT_DOCUMENTATION.md`.

## Right now

- Branch `tasks-98-113`. Nothing is pushed: the user has problems with GitHub, so no push until they say so.
- Last setup: **1.16.10** (`dist/RavenfieldVR-Setup-1.16.10.exe`), built 2026-11-07. Everything after it is in `dist/notes-next.md`: **the night vision fix is installed in the game but is not in that setup.** A new setup is built only when the user asks, with a new patch version.
- Installed in the game: 4ad80c3 (DLL hash d9f50d4a6af9), the suite passing on it (436 in the main block).
- The run of Tasks 366 (`/work-until-limit 50`) was paused by the user at session 33%, week 58% (2026-11-07: "you can pause the limit just finish this run"). It does not resume by itself: only on a new request.
- The game is not started for tests ("DO NOT open the Game to test and stuff"). None of 1.16.10 or later has been seen in a headset.

## Tomorrow

In this order:

1. **The user's headset test** of the list below, night vision first. Copy their log before anything else, and read it for the lines named there.
2. **Setup 1.16.11** if they want one: it would carry everything in `dist/notes-next.md`. Only when asked.
3. **Two scouts were stopped before they reported**, so their areas are still unread. Start them only if the user wants scouts again:
   - Drops, pickups and the pouch against what the game still expects (`WeaponDrops.Pickup.cs`, `WeaponDrops.OwnWeapon.cs`, `PouchDisplay.cs`, `WeaponOverrides.cs`; a resupply with a magazine out, a script giving a weapon while one is held, a respawn with a new loadout).
   - Seats with cameras of their own: `BombCamera`, `LookAtCamera` (the attack boat's mortar), `AimTransform` (a helicopter minigun, the TOW), `PlayerRecoilCamera` (tank and APC guns). Can each be aimed in VR, or is its view stuck?
4. **A small one, planned and not made:** a rocket held nose first stops at the launcher's mouth with no cue (`LooselyLined` wants the tail first, `ReloadInteractions.Magazines.cs` near line 568). Say TAIL FIRST there as WRONG MAG does, and add the phrase to `tools/tests/failure_phrases_test.py`.
5. **Noted, low, if there is time:** Lua's `Input.mousePosition` is the desktop's; a flat hand at the sky buzzes with no order; a living swimmer held by the body keeps his AI's swim force; a limb still inside its trunk 5 s after a throw stays uncollided; a snap turn in the very frame a hand takes the wheel. `SpookOpsMode` restarts on Return only at its end panel (the pause menu still works).

## First things to check when the user next plays

1. Night vision: off hand's grip at the side of the head. The log should say "Goggles: the hand at the side of the head switched them." and "Night vision on: the eyes' post-processing sees layers 10000000". Green, grain, bloom, a dark edge. If the lines come and the view stays plain, suspect the post-processing stack on the eye camera, not the gesture. If the first line never comes, the gesture.
2. Diving: the view turns deep blue under water. Too dark is `UnderwaterVeil.Most`.
3. The gun and arms indoors with the sun out: they should drop out of the sunlight.
4. The squad map: a click sends the squad where the laser is. B on a loadout entry opens its options. Tactics map: drag an arrow, hold B and sweep to take it off.
5. Squad pointing: a car says ENTER, the sky NOTHING THERE.
6. A soldier on the move taken by the neck: he stops walking and firing.
7. A pistol in the free hand throws cases. A trigger held on a menu does not fire when the laser leaves it.
8. From the last run: a seated magazine squeezed and let go; a locked slide touched; machine gun cover and belt sounds; a gun rested on a ledge; a sister magazine.

The user's log is `BepInEx/LogOutput.log`, overwritten by the next game start: copy it before anything else.

## How the user plays

Quest 3 through Link into SteamVR. Settings that differ from the defaults: `Hands = WeaponArms` (the gun's own arms, so `ArmIK`, `ArmFingers`, `EmptyArms`, not the gloves), `GraphicsPreset = Game`, `RenderScale = 0.8`, smooth turning, `TurretAim = Hand`, `GameCamerasOnePixel`, `ScopeHalfRate`, `WaterDrops`. Read `BepInEx/config/com.qwavey.ravenfieldvr.cfg` before reading a report.

## Rules that keep getting forgotten

- Sounds: only the game's own. Nothing made, nothing downloaded.
- Subagents: Haiku only, read-only scouts, briefed positively. Verify every claim against the code: about a third do not hold.
- `build.ps1 -Package` is only for building a setup. Compile with plain `build.ps1`.
- Edit scripts: Python written with the Write tool into the scratchpad. Git Bash heredocs mangle escapes. Never PowerShell Get-Content or Set-Content on sources. Never insert code between a member and its `/// <summary>`.
- Install only with `ravenfield.exe` closed and the suite green: copy `build/RavenfieldVR.dll` and `.pdb` to `BepInEx/plugins/RavenfieldVR`, compare hashes.
- A new two-word capital message on a gun needs an entry in `tools/tests/failure_phrases_test.py`.
- `dist/` is ignored: `git add -f dist/notes-next.md`. Commit with `git -c core.safecrlf=false commit`.

## Where the game's own code and data are

- `.ref/decompiled/Assembly-CSharp`, `firstpass`, `postprocessing` (ilspycmd in `tools/ilspy`, run with `dotnet`).
- Prefabs: `resources.assets` with UnityPy. Type trees are stripped: parse MonoBehaviours by hand (game object PPtr at 0, script PPtr at 16, name at 28; script ids are in `globalgamemanagers.assets`).
- Sound banks and clips: `tools/bundles/sound_banks.py`, `tools/bundles/clip_motion.py`.

## Lenses that have found the most

1. A feature written from the code and never seen running: read what the game needs for it to take effect (a mask, a field, a camera) and check the mod really gives it. Night vision sat broken for three weeks this way.
2. What the flat game does on one of its own cameras, and the eyes never get (`FPLightIntensity`, the post-processing layers).
3. A controller or `Update` switched off: what the game still reads from it (`AiActorController.Velocity`, `fire`).
4. The free hand against the gun hand: what one does and the other forgot (cases, flats).
5. One action walked frame by frame: "what if the hand does half of it".
6. A state left set after its action ends; a cache keyed by type filled in a passing state; clocks mixed.
7. A review of the run's own diff before the end.

## Open questions for the user

- Leaving a vehicle draws the last weapon, even if the hand went in empty.
- The QJB201 in AR mode reloads as a belt gun.
- Two-handed aim comes on in 0.1 s (a quick snap of up to 25 degrees): softer would feel laggier.
