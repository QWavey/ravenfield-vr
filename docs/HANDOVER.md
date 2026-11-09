# Handover

Where the work stands, for whoever picks it up next, a compacted session included. Kept current during a run; the detail is in `docs/Tasks.md` (the newest numbered entry) and the mechanisms in `docs/PROJECT_DOCUMENTATION.md`.

## Right now

- Branch `tasks-98-113`. Nothing is pushed: the user has problems with GitHub, so no push until they say so.
- Last setup: **1.16.15** (`dist/RavenfieldVR-Setup-1.16.15.exe`), built 2026-11-08 night at the user's word ("BUILD A SETUP EXE PLEASE!"): Tasks 371 (notes in `dist/notes-1.16.15.md`), none of it run in the game. `dist/notes-next.md` is empty after it. A new setup only when the user asks, each a new patch version.
- Installed in the game: 1.16.15 (the setup's DLL, game closed, the suite passing: 460 in the main block). Not verified in the game: Tasks 370's last changes and all of Tasks 371; the last game run froze at the let-go check, cause not found.
- **The showcase (Tasks 372), 2026-11-09: `/work-until-limit 80` (session), the trailer being finished.** Done today: the user's bug (Steam gloves on the Ravenfield arms) fixed: the showcase runs on the user's WeaponArms, the body's upper arm leads into the weapon arm's elbow, the weapon's upper arm folded per picture (`ShowcaseBody.FoldUpper`, no spikes); the montage filmed again on Dustbowl and Temple (Canyon next); the night map fixed (StartLevel sets `nightVersion` from `parameters.nightMode`, now set by the prefix); the fight puts its soldiers where the eyes see them (14-22 m) with an over-the-enemy reverse shot; a new `cover` scene (a wall's edge: step out, burst, back); the slow motion uses the natural swap; single-gun scenes take the first `--guns`. Takes are named without spaces (`eagle_base_night`). `tools/trailer/make_trailer.py` joins scene edits and cards into one trailer edit. Watch `showcase/showcase-all.mp4`; clips in `showcase/clips/` (07 the user's favourite, KEEP), takes in `showcase/takes/`, edits in `tools/trailer/`, the recipe at the top of docs/Showcase-Plan.md. How to film: `python -I tools/vrshots.py --showcase SCENE --fast [--mods IDS --guns "A,B"] [--map MAP] [--night] --no-build --timeout 20` (scenes: pilot, reload, inspect, grenade, pistol, slowmo, scope, hands, fight, cover, montage).
- Run: Tasks 370, `/work-until-limit 75` (2026-11-08, second day), ended at the user's word at about 37% ("you can pause after the tasks. dont search for new ones then. ignore the work till limit"). Not done because of it: the full autotest (the sound lists, rack splits and 2D scope pictures of every gun) and the shotgun-loading scout.
- Last run: Tasks 371, `/work-until-limit 75 weekly 81` (2026-11-08 evening), ended at the weekly ceiling (week 81%, session 66%): performance and bugs, the game NOT started (the user's words). Done: magazines go kinematic without the engine's CCD warning; the frame rate hold's 5/6 see-saw (the user's Ultra session); terrain settings written only on change; a drawn pistol put away on a seat switch (`EnterSeatPutAwayPatch`); water (no drops for a submarine crew, no gun wrested into the water, the goggles grab felt as nothing); the real-time grenade/bullet prefixes guarded; the lens (a failed rebuild, mode lenses over a sight's own reticle, magnification kept after a mode switch, the stepped warm-up reading the picture); the chest pouch after a hand pass; once-logs built every draw; source checks `enum_switches.py` and `mixed_clocks.py` in the suite, reflected names in the target test (37). Not in a setup yet (`dist/notes-next.md`); suite green (460). None of it run in the game. The squad-orders scout was stopped unread at the ceiling.
- Game tests: allowed again for the showcase (2026-11-08 night), now PAUSED: the user is playing (21:40). Earlier, 2026-11-08 18:04: "pause the test please and dont start another ... dont start the game anymore. close it rn". Ask before the next run. (Before that they were allowed anytime, in the background, with `vrshots`.)
- The MG belt: the user asked to look for it in the game ("maybe you see the issue too somehow on mgs"). The MG reload self-check now measures the hanging and held belt against the box as drawn (`BeltAgainstDrawnBox`, gun level and rolled 25/-25/50 deg) and takes `belt-1-hanging*`/`belt-2-held` pictures. First run: MG3 and PKM.

## Open from Tasks 370 (2026-11-08, second day)

- Sounds: a gun's own sounds and what a vanilla gun lends it are logged at its first draw ("its own sounds: ...; lent: ..."); a pull's clip or slice holding both strokes is cut at its gap ("holds both strokes of a rack"). Most guns' slide sound is lent (a slice of a vanilla reload): the likeliest source of the user's "back and forth on the pull". Read the user's next log for "holds both strokes" and "cannot be read". A short clip named as a whole reload is bound to no action (the M1's 'Garand Reload Full').
- Settled today: the slow-motion magazine flies in real time (the check had dropped it onto the gun); the slowed hands' magazine goes at the world's pace (0.92 was the catching-up hand); the mag kick's slow push seats.
- The M1 passes its en-bloc check in the game (refused with the bolt shut, in through the open action, bolt home by itself, 8 shots, clip out, bolt locked open). Its slide is bound to 'Garand Reload Full' (0.78 s, one stroke): the new "its own sounds" line (first draw) says for which action; a whole-reload sound on a slide is what Tasks 364 cut for clips over 1 s.
- The soldier steps after "gun bash": the free hand had become the gun hand. A check before leaves the gun in the other hand; the boxing step holsters it, the weapon system puts the gun hand back on the dominant one (no weapon out), and the gun was drawn into the hand the check calls free. The check now starts with the gun drawn again into the dominant hand. Also: no soldier's gun taken on a squeeze that just landed a punch; the bash's swing line fits the gun's length.
- P220 T S: "THE SLIDE RAN FORWARD ON ITS OWN when the magazine went in (60 mm)" with the state still locked: the check now names the part that moved (maybe the magazine drawn at its seat, not the slide). Run it again: `vrshots --weapon "=P220 T S"`.
- Buried drops: the way back toward the eyes met a bank of terrain near the eyes ("Terrain, 2.02 m before it"); a thing under surface ground (terrain, an open mesh) is now lifted straight up onto it when that way is refused (`PropPhysics.UpOnto`). `GroundBetween` and `UpOnto` take only surface ground, not a table's top.
- Deploy: runs ended "the player could not be deployed": the map's spawn point not the team's, and once no spawn wave took him in 72 s. The autotest picks a team spawn point and, after two unanswered clicks, spawns the player as the wave does.
- A takeover of a soldier while alive (`ActorManager.PlayerTakeOverBot`, map triggers, Lua mods) never showed the mod a dead frame: `WeaponSystem.LetGoForNewLife` now also runs as the player spawns (held soldiers released, squeezes claimed so a grip held through the death screen does not take the next steering wheel).
- Slowed hands' magazine (0.91 m/s): the check dropped it in front of the hand still catching up; it now waits for the hand.
- The lens's reticle: the gun's own, from its 2D scope picture, when that picture is a texture clear in the middle (mods' pictures; the two vanilla scoped guns' are textureless dark meshes and keep the duplex); the duplex otherwise. One line per scoped gun in the log ("the lens draws ..."). The full autotest's "2D scope picture" lines list each gun's picture.
- Slowed hands: a magazine let go at 1 m/s went 0.92 m/s in real time (0.18 the day before): the line now says its launch speed (1 = the world's pace from the drop; a later rise = the catching-up hand pushing it).
- Mag kick, slow push: the fresh magazine does not seat (it did on 10-27): the line now says where it was left.

## Open from Tasks 367 (the user's reports of 2026-11-08)

- The M1 GARRET's bolt and clip: two causes fixed (a sound that threw in the middle of the clip's seat; an empty clip taken off the ground and pressed in). Its log lines now tell the rest: "the last round fired, the empty clip thrown out, the bolt locked back / NOT LOCKED", "a clip refused ...". Ask what happens now.
- A lens over a real scope: my own Tasks 365 rule reverted; a scope whose model draws its own reticle (a `reticle`-named material, e.g. `reticle_ui_stencil`) gets no mod lens at all and is seen unmagnified; the log names each gun the mod draws a lens for, and each it steps aside for. Ask which guns still do, and whether a real scope should be magnified again (a design call: the mod's lens over it hides its reticle).
- New controls to tell the user about: in a gun seat a free hand's grip aims (lock-ons, zoom cameras); on the game's menus B is the right mouse button; at a mortar the right stick sets the range; with the C4 in the hand A sets off its thrown charges (they could never go off in VR: the vanilla C4 is a `RemoteDetonatorWeapon`, set off in flat play by aim and fire).
- Found by reading the game's weapon kinds one by one (a lens that worked: a subclass of `Weapon` with a `Fire` or hit of its own the mod never names): the C4 above, the Air Horn (its blast is `Airhorn.ShootMeleeRay` on the trigger; the mod suppressed every melee trigger), thrown bags (`RigidbodyProjectile` sets its body's speed once in `Awake`, so `Projectile.velocity` did nothing). Also: the logs' "reprojected" count counted the always-on async flag (0x04); `PerfProbe.Reused` counts real reuse now, so older logs' "64 of 64 reprojected" means nothing.
- The MG belt through the box (the MG3): not found. The strip keeps out of `Data.MagBounds` in the seat's frame, and the MG3 shows its model's own belt when laid. Ask which belt: the one hanging from the box with the cover up, the one held, or the one lying on the gun; and which box: the one in the gun, one in the hand, or one on the ground.
- Two scouts restarted (pickups, seats with cameras of their own): their findings go into Tasks 367.
- Tasks 368 (after 1.16.12): a start from Steam with SteamVR closed came up flat on the Oculus desktop; the mod now starts SteamVR itself with -vr and keeps trying for 2 minutes (in 1.16.13, built and installed). Ask the user to try: Steam > Ravenfield VR with SteamVR closed.
- Later on 2026-11-08 (game runs allowed): fixed the C4 (A sets it off), the Air Horn, bags' speed, the reserve shown as without end, the reprojection count, a magazine hanging in slow motion beside the gun (Tasks 351 made the gun solid, 352's real-time flight ended at its touch), thrown grenades/bags/rounds/molotovs/charges in slow motion (real time until they land: `RealTimeGrenades`, `RealTimeProjectiles`), a pause ending a real-time flight, the free hand's launcher left selected, its OVERHEATED, the weapon arms' grip on a free-hand gun. New autotest checks: the belt against the box as drawn (rolled), a bag's toss speed, the C4 set off by A, a grenade in slow motion. The full autotest ran at 11:50 (report: reports/shots/autotest/ after vrshots copies it).
- Questions for the user at the end of this run: which belt clips (the hanging one with the cover up, the held one, the one on the tray) and through which box (in the gun, in the hand, on the ground)? The MG3 measured clean in the game, level; the rolled measure follows. Should real scopes with their own reticle be magnified again (they are seen unmagnified now)? The mod's lens draws its own duplex: worth putting each gun's own 2D scope picture on the lens (the log now describes those pictures: "its 2D picture: ...")? Knocked over, the view goes third person: keep?

## First things to check when the user next plays

1. Night vision: off hand's grip at the side of the head. The log should say "Goggles: the hand at the side of the head switched them." and "Night vision on: the eyes' post-processing sees layers 10000000". Green, grain, bloom, a dark edge. If the lines come and the view stays plain, suspect the post-processing stack on the eye camera, not the gesture. If the first line never comes, the gesture.
2. Diving: the view turns deep blue under water. Too dark is `UnderwaterVeil.Most`.
3. The gun and arms indoors with the sun out: they should drop out of the sunlight.
4. The squad map: a click sends the squad where the laser is. B on a loadout entry opens its options. Tactics map: drag an arrow, hold B and sweep to take it off.
5. Squad pointing: a car says ENTER, the sky NOTHING THERE.
6. A soldier on the move taken by the neck: he stops walking and firing.
7. A pistol in the free hand throws cases. A trigger held on a menu does not fire when the laser leaves it.
8. C4: throw two charges, press A with the C4 in the hand: both go off ("A set off 2 charge(s)" in the log). The Air Horn on the trigger knocks soldiers in front of its bell over. An ammo bag tossed gently lands near.
9. The MG belt: a box in with the cover up, the gun turned on its side: the hanging rounds' tips stay out of the box (MG3, MG3 ACOG).
10. Slow motion: a grenade, bag or molotov thrown flies at the throw's speed until it lands; a magazine let go beside the gun flies on.
11. From the last run: a seated magazine squeezed and let go; a locked slide touched; machine gun cover and belt sounds; a gun rested on a ledge; a sister magazine.

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
8. A value added to an enum without its row in every switch over it (`MechSound.HandGrab` had no cut table: a null looped over threw in the weapons' frame for two releases). And read a user's log for "Thrown again": `Log.ExceptionOnce` writes an exception once, so before 2026-11-08 one line could stand for hundreds.
9. A run's own rule that widened what a feature applies to (a scope tube counting as an optic put the mod's lens over real scopes): when a report says "still", look at what the last run widened first.
10. Every subclass of the game's `Weapon` with a `Fire`, a hit or an `Update` of its own, against what the mod calls (`RemoteDetonatorWeapon`: the C4 never went off in VR; `Airhorn.ShootMeleeRay`; `RigidbodyProjectile` setting its speed in `Awake`). List them with `grep -l ": .*Weapon\b" .ref/decompiled/Assembly-CSharp/*.cs` and read the game's prefab scripts (`tools/export_checks/prefab_scripts.py` lists which vanilla prefab has which script).
11. A game run's autotest after a design change: checks written before it still test the old rule and either fail where the new one is right (the gentle set-down after Tasks 351) or find what the change broke (Tasks 352's real-time flight ended by the gun Tasks 351 made solid). Run the whole autotest after any change to what touches what.

## Open questions for the user

- Leaving a vehicle draws the last weapon, even if the hand went in empty.
- The QJB201 in AR mode reloads as a belt gun.
- Two-handed aim comes on in 0.1 s (a quick snap of up to 25 degrees): softer would feel laggier.
