# Handover

Where the work stands, for whoever picks it up next, a compacted session included. Kept current during a run; the detail is in `docs/Tasks.md` (the newest numbered entry) and the mechanisms in `docs/PROJECT_DOCUMENTATION.md`.

## Right now

- Branch `tasks-98-113`. Nothing is pushed: the user has problems with GitHub, so no push until they say so.
- Last setup: **1.16.11** (`dist/RavenfieldVR-Setup-1.16.11.exe`), built 2026-11-07 at the user's word; it carries night vision and all of Tasks 366. `dist/notes-next.md` is empty after it. A new setup is built only when the user asks, with a new patch version.
- Installed in the game: the 1.16.11 build, the suite passing on it.
- Current run: Tasks 367, `/work-until-limit 60` (2026-11-08). Ceiling pinned in `.claude/wul-config`; meter: `bash .claude/wul-read.sh`. The user played 1.16.11 before it: their log is `reports/shots/user-1107/game-log.txt`.
- The game is not started for tests ("DO NOT open the Game to test and stuff"). None of 1.16.10 or later has been seen in a headset.

## Open from Tasks 367 (the user's reports of 2026-11-08)

- The M1 GARRET's bolt and clip: two causes fixed (a sound that threw in the middle of the clip's seat; an empty clip taken off the ground and pressed in). Its log lines now tell the rest: "the last round fired, the empty clip thrown out, the bolt locked back / NOT LOCKED", "a clip refused ...". Ask what happens now.
- A lens over a real scope: my own Tasks 365 rule reverted; the log names each gun the mod draws a lens for. Ask which guns still do.
- The MG belt through the box (the MG3): not found. The strip keeps out of `Data.MagBounds` in the seat's frame, and the MG3 shows its model's own belt when laid. Ask which belt: the one hanging from the box with the cover up, the one held, or the one lying on the gun; and which box: the one in the gun, one in the hand, or one on the ground.
- Two scouts restarted (pickups, seats with cameras of their own): their findings go into Tasks 367.

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
