# Ravenfield VR

A VR mod for Ravenfield (Steam, Unity 2020.3 build) that runs through SteamVR. Built and tuned for a Quest 3 on SteamVR (Steam Link, Air Link / Link cable, or Virtual Desktop in SteamVR mode); Index, Vive and Reverb G2 controllers have default bindings too.

What you get:

- Stereo rendering through Valve's OpenVR XR plugin, roomscale tracking, snap or smooth turning, comfort vignette, graphics presets made for VR.
- Free hands. The gun sits in your hand and fires where its barrel points; nothing is glued to anything. Each weapon's own finger pose (from its mod's arm rig) shapes your hand around its grip, or switch to the weapon's own first-person arms, moved to your controllers by IK.
- Manual reloading for every gun, modded ones included: magazines slide in and out along the well, slides, bolts, charging handles and pumps move with your hand, break-action shotguns and revolvers open, machine guns have feed covers. Your ammo pouch shows what the gun takes.
- The mod's own sounds for each action (magazine out, magazine in, rack back, slam home), cut out of its reload audio, so a reload sounds like what your hands are doing instead of the whole reload clip playing at once.
- Semi/auto switching on guns that have it, and the fire-mode, flashlight, laser, bipod and inspect keys of weapon and mutator scripts reachable from the controllers.
- Fallen soldiers drop their weapons; pick one up and it replaces the one in that slot.
- Grenades with a pin you pull and an arm you throw with; melee weapons that hit when you swing them.
- Scopes you look through (magnified lens with a reticle) on weapons that only had a 2D overlay.
- Every menu on panels you point at with a laser; text fields open the SteamVR keyboard. The HUD as a frame in front of you that follows your view loosely (small head movements leave it still), with its markers lined up with the world. Vehicle gunner sights shown at infinity, so they never double.
- Cars and Humvees with a steering wheel you grab and turn, and your own weapon usable from the driver seat. Helicopters, planes and tanks keep stick controls; turrets aim where you look.

## Install

1. Close Ravenfield.
2. Run `RavenfieldVR-Setup-1.0.0.exe`. It finds Ravenfield in your Steam libraries (or pick the folder) and installs. Keep "Pressing Play in Steam opens the launcher" ticked: Steam then asks every time you press Play whether to start in VR or on the monitor, and with which graphics preset. If Steam is running, Setup asks to close it for a moment (Steam rewrites its launch options when it exits) and starts it again.
3. Put on the headset, press Play on Ravenfield in Steam, choose **Play in VR**.

To update, run the newer Setup. To remove the mod, run Setup and press Uninstall: everything it replaced is restored from `Ravenfield/RavenfieldVR-backup/`, and the launcher is taken out of Steam's launch options again (your own launch options stay).

No Setup? The release zip has `install.cmd` / `uninstall.cmd` that do the same from PowerShell, without the Steam launch option; start the game with `RavenfieldVR-Launcher.exe` in the game folder instead.

The launcher remembers your choice when you tick "Start this way from now on"; hold **Shift** while pressing Play to see it again. Without the launcher, VR starts only when SteamVR is already running; `-vr` forces VR, `-novr` forces flat.

## First minutes

- **Height** is calibrated a moment after you first spawn: stand up straight. Redo it from the settings panel ("Calibrate now"). Playing seated? Turn on "Seated play" and calibrate while sitting.
- **Settings panel:** double-click the left stick. Recenter, SteamVR bindings and Mod keys are at the bottom.
- **Gun angle:** if guns feel tilted in your hand, use "Gun pitch" on the panel.

## Controls (Quest Touch, right-handed)

| Input | On foot | In a vehicle |
| --- | --- | --- |
| Right trigger | Fire | Fire mounted weapon |
| Left trigger | Use: enter vehicle, ladder, interact | Use: leave vehicle |
| Grips | Grab: foregrip, magazine, slide, bolt, cover, grenade pin, holsters, pouch, anything lying on the ground | Grab: steering wheel, flight stick |
| Left stick | Move. Click: sprint. Double-click: VR settings. Hold: scoreboard | Steer and throttle (car), collective and yaw (helicopter), rudder and throttle (plane) |
| Right stick | Turn. Up: jump. Flick down: crouch. Hold down: prone. Click: next weapon. Hold click: kick | Cyclic (helicopter, plane) |
| B | Drop the magazine / open a break-action or revolver | |
| A | Slide or bolt release; pulls a grenade's pin. Hold: fire mode (semi/auto, or the weapon's own mode switch) | Countermeasures. Hold: auto-hover (helicopter) |
| X | Squad order (hold and point, release to send) | |
| Y | Squad map. Hold: pause menu. Double: loadout | |
| Menu (≡) | SteamVR's own dashboard (the match pauses behind it); not bound by the mod | |

Change anything in SteamVR: **Settings → Controllers → Manage controller bindings → Ravenfield**, or the "SteamVR bindings" button on the settings panel. Left-handed players: set "Weapon hand: Left" and restart; the default bindings mirror automatically.

Other gestures:

- **Holsters:** weapons are carried by what they are. Rifles, launchers and other long weapons hang over your shoulders (weapon side first, then the other shoulder), pistols sit on your hips (weapon side, then a cross-draw on the other hip), and grenades and kits on your chest. Reach there with your weapon hand and grip; a short buzz tells you your hand is at a holster with something in it. Put a gun back where it lives and let go to have both hands free. The chest holsters are ignored while you aim, so re-gripping a shouldered rifle never switches weapons.
- **Night vision:** grip with your off hand at the side of your head.
- **Map:** turn the back of your off-hand wrist toward your face. Health and ammo appear on your wrist.
- **Crouching in real life** crouches your soldier.
- **Aiming down sights** is just bringing the sights to your eye. Scopes switch to their magnified lens.

## Reloading

The counter on the side of your gun shows its state: `28+1` (28 in the magazine plus one chambered), `RACK` (no round chambered), `OPEN` (slide locked back, or a break-action open), `NO MAG`, `COVER OPEN` / `CLOSE COVER` on machine guns. The pouch on your body shows a copy of what the gun takes and how many spares you have.

**Magazine-fed weapons**

1. Press B: the magazine slides out and drops. Or grab the seated magazine with your off hand and pull it out. A dropped magazine keeps the rounds left in it: pick it up again (with either free hand, whatever you are holding) and seat it to use them. Magazines lying around are cleared after a minute (`DroppedMagLifetime`); rounds still in them then go back to your reserve, so nothing is lost.
2. Grip at your pouch (off-hand hip by default): a fresh magazine is in your hand.
3. Bring its top to the well. Near it, the magazine locks onto the well's axis and slides with your hand; push it all the way to seat it.
4. If the slide locked back on empty, press A, or pull the slide or charging handle back and let it go.

Tactical reload with one hand: push a fresh magazine hard into the base of the seated one and it knocks the old one out, then slide the new one in. This works where the magazine sticks out of the gun; pistols and guns whose well swallows the magazine (KRISS Vector style) need B or a hand pull.

**Bolt-action, lever and pump guns** move only by hand: pull the bolt or pump back and push it forward again to chamber the next round. Let go half way and it stays there.

**Shotguns with a tube** take shells one at a time at the loading port; semi-automatic ones cycle themselves after each shot, pump guns need the pump.

**Break-action shotguns, revolvers and break-action launchers:** press B to open (the barrels tip down, the cylinder swings out, fired cases fall out). Push shells into the chambers from your pouch (a revolver with a speedloader takes all six at once). Flick your wrist up or press B to close.

**Machine guns** with a feed cover: grab the cover's rear latch and lift it (with the cover shut and the belt in the feed the box will not come off). Drop the old box (B), slide a new box onto its rails (a short, heavy slide), then take the belt end at the mouth of the box and lay it on the feed tray under the cover; it clicks in. Press the cover down over the belt and rack if the bolt is forward. The counter walks you through it: `LAY BELT`, `CLOSE COVER`, `RACK`.

**Rocket and grenade launchers:** grip at the pouch for a round and push it in.

**Grenades:** pull the pin with your other hand (grip the ring at the top and pull it away), or press A to flick it out one-handed. Nothing happens while you keep holding the grenade; hold the trigger, throw with your arm and let go of the trigger at the end of the throw, and the fuse starts as it leaves your hand. Thrown with the pin still in, it is a dud: it bounces and lies there, and picking it up puts it back in your pouch. Put the grenade away with the pin out and the pin goes back in. Smoke grenades work the same; ammo bags, medic bags and charges have no pin. **Melee weapons** hit what you swing them through.

Loose magazines, shells and duds are physical: they bounce off your gun, your gun can push them around, and they never catch on your own body. Bullets pass through them.

Holding a long gun with one hand works, but it kicks harder, droops a little and trails your wrist; rest your other hand on the handguard to steady it. Pistols are made for one hand.

Prefer the game's normal reloads? Set "Reloading: Assisted" and B plays the standard reload.

## Mod keys and fire modes

Many workshop weapons and mutators come with scripts that listen for keyboard keys: X for a fire-mode switch, H for a flashlight, J for a laser, I to inspect, T for a bipod or HUD toggle. Open **Mod keys** from the settings panel (double-click the left stick, "Mod keys" at the bottom): a panel listing every key the held weapon's scripts and the running mutators listen for, labelled with what it does. Point and press. The press goes only to that script.

Holding A on a weapon with its own fire-mode script presses that script's key (it switches with its own animation and sound). Weapons whose mod built the modes as sub-weapons cycle those. Other automatic weapons switch between semi and auto; semi-only weapons (most pistols, shotguns, rifles and DMRs) stay semi.

## Driving

Grab the steering wheel of a car or Humvee with one or both hands and turn it; the car steers with it. Let go and the left stick steers again. While driving, reach to a holster to draw that weapon and shoot with one hand while steering with the other; put it back to get the horn again. Tanks, helicopters and planes use the sticks (helicopters and planes also have a grabbable flight stick).

## Graphics

Pick **Low, Medium, High or Ultra** in the launcher or on the settings panel ("Graphics"). A preset sets the render resolution, MSAA, shadow distance and quality, level of detail, per-pixel lights, texture size, vegetation and view distance, layered on top of the game's own options. "Game" leaves graphics to the game's options menu. Render scale stays adjustable after picking a preset.

## Odd modded weapons

Weapon rigs are worked out from each gun's own animations: what moves when it fires (slide, bolt), what moves in the reload (magazine, charging handle, cover, barrels, cylinder), where the mod's hands hold it and how its fingers curl. Where a bolt is part of the receiver mesh, the mod's own racking hand shows where the handle is, and that piece of the mesh is cut out to move with your hand (or a small handle is added when the mesh cannot be read).

If a mod still behaves strangely, add an entry to `BepInEx/config/RavenfieldVR.weapons.json` (created with an example):

```json
{ "weapons": [ { "match": "mosin", "action": "Manual", "slide": "bolt", "slideTravel": 0.09, "gripForward": 0.01, "pitch": -4, "magKick": "false" } ] }
```

`action` is one of `SelfLoading`, `Manual`, `ShellFed`, `SingleShot`, `BreakOpen`, `Throwable`, `Melee`, `NoAmmo`. `magazine` and `slide` take transform names from the weapon. `magKick` allows or forbids knocking out the magazine with a fresh one.

**Checking every installed weapon:** start the game with `-rfvr-selftest` (works flat, from the main menu). `BepInEx/RavenfieldVR-weapon-report.txt` then lists for every weapon how each part was found, the sounds bound to each action and where each reload sound comes from (the mod, a vanilla weapon, or synthesised), and anything suspicious. `-rfvr-dumprig=name` adds the full transform tree of matching weapons (`RavenfieldVR-rig-dump.txt`).

**Automated test:** `ravenfield.exe -map island2023 -nointro -rfvr-autotest [-vr]` plays a match by itself with every installed mutator on, equips and fires every weapon and (in VR, holding it in simulated hands when no controllers are connected) checks that its draw animation is skipped and its model is visible, runs its reload and throws each grenade with and without its pin, then writes `BepInEx/RavenfieldVR-autotest.txt` and quits. `-rfvr-perf` measures the mod's per-frame cost in any session.

## Settings

Everything lives in `Ravenfield/BepInEx/config/com.qwavey.ravenfieldvr.cfg`; most of it is also on the in-headset panel. Notable ones:

| Setting | Default | What it does |
| --- | --- | --- |
| `GraphicsPreset` | Medium | Low, Medium, High, Ultra or Game |
| `RenderScale` | 1.0 | Resolution on top of SteamVR's own setting (a preset sets it) |
| `StereoMode` | MultiPass | SinglePassInstanced is faster, but some map shaders then draw in one eye only |
| `PostProcessing` | off | Copy the game's colour grading, bloom and AO to the headset (costs frame rate) |
| `Hands` | VRHands | VRHands, or WeaponArms for the weapon's own first-person arms |
| `TurnMode` / `SnapTurnAngle` | Snap / 30 | |
| `MagazinePouch` | OffHandHip | Also Chest, DominantHip, OffHandChest |
| `ShowPouch` | on | Show the gun's ammo at the pouch |
| `TwoHandRecoilReduction` | 0.6 | How much a second hand on the gun tames recoil |
| `WeaponWeight` | on | Long guns held in one hand droop and trail the wrist |
| `FireSelector` | on | Semi/auto switching on automatic weapons |
| `WeaponsWhileDriving` | on | Draw your weapon from the driver seat of cars |
| `EnemyWeaponDrops` | on | Fallen soldiers drop their weapon |
| `ModWeaponSounds` | on | Use each mod's own sounds for manual actions (a vanilla weapon's when the mod has none for an action) |
| `GrenadePins` | on | Grenades need their pin pulled; off makes every throw live |
| `BeltByHand` | on | Lay a machine gun's belt on the feed tray yourself; off lays it when the cover closes |
| `HideGameWeaponHud` | on | Hide the game's weapon panel (name, ammo, slots); the gun, pouch and wrist show ammo |
| `SkipDrawAnimation` | on | Skip the weapon's draw animation in VR (you draw it with your hand) |
| `ReturnRoundsOnMagDrop` | on | Rounds in magazines that get cleared away return to your reserve |
| `HudDistance` / `HudHorizontalFov` | 4 m / 72° | Where the HUD frame sits and how wide it is |

## Building from source

No .NET SDK needed:

```powershell
./tools/fetch-deps.ps1     # BepInEx, OpenVR XR plugin, Roslyn compiler (into tools/)
./build.ps1 -Package       # compiles against your installed game: dist/RavenfieldVR, the zip, the launcher and Setup.exe
./install.ps1
```

`build.ps1` publicizes the game's assemblies into `build/refs` for compile-time access only; nothing from the game is redistributed. Setup.exe also runs unattended: `--install [--game DIR] [--no-steam-option]`, `--uninstall [--keep-settings]`.

## Troubleshooting

- **Steam offers to play it on a big screen / the view fights with a flat screen.** That is SteamVR's Desktop Theater, which Steam uses for games it thinks are flat. Turn it off for Ravenfield once: Steam Library, right-click Ravenfield, Properties, General, untick "Use Desktop Game Theatre while SteamVR is active".
- **The game opens on the monitor only.** Check `BepInEx/LogOutput.log` for lines starting with `[Ravenfield VR]`. "Not starting VR: SteamVR is not running" means SteamVR was not open and the game was not started through the launcher's Play in VR. "OpenVR failed to initialise" includes SteamVR's reason (for example `Init_HmdNotFound` when the headset is not streaming).
- **"Unity did not register the 'OpenVR Display' subsystem".** The runtime files under `ravenfield_Data` are missing. Run Setup again; a game update or "Verify integrity of game files" can remove them.
- **Controls do nothing in the headset.** Open the SteamVR binding UI for Ravenfield and select "Ravenfield VR default".
- **Low frame rate.** Pick a lower graphics preset, keep `PostProcessing` off, reduce the bot count. Ravenfield is CPU-heavy with many bots.
- **A weapon sits wrong in the hand.** Use "Gun pitch" for all guns, or a per-weapon override (above).

## Known limitations

- Night vision and other effects the game applies to its own camera are not copied to the headset camera.
- Third-person vehicle cameras and the death camera follow the game's camera position, turning with it only around the vertical axis.
- Ravenscript mods that draw their own screen-space UI get the same head-locked HUD treatment as the game's HUD.
- Weapons without any animator get their parts by name only; a few of those keep an estimated handle position.
- Materials a mod ships with a shader the graphics card cannot run (magenta) are swapped for the game's own version of that shader, or a plain one, so they may look a little different from what the modder intended.
