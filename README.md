# Ravenfield VR

A VR mod for Ravenfield (Steam, current Unity 2020.3 build) that runs through SteamVR. It was built and tuned for a Quest 3 on SteamVR (Steam Link, Air Link/Link cable or Virtual Desktop in SteamVR mode); Index, Vive and Reverb G2 controllers have default bindings too.

What you get:

- Stereo rendering through Valve's OpenVR XR plugin, roomscale tracking, snap or smooth turning, comfort vignette.
- Tracked hands. Your gun sits in your hand and fires where its barrel points. Grab the handguard with the other hand to aim with both hands.
- Manual reloading for every gun, including modded ones: drop the magazine, pull a fresh one from your pouch, seat it, rack the slide or charging handle. Bolt and pump actions are cycled by hand, shotguns take shells one at a time, launchers are loaded by hand.
- Grenades thrown with your arm, melee weapons that hit when you swing them.
- Scopes you look through (magnified lens with a reticle) on weapons that only had a 2D overlay.
- Every menu (main menu, pause, loadout, squad map, options, dialogs) on panels you point at with a laser. Text fields open the SteamVR keyboard.
- The HUD as a frame in front of you, lined up so objective and squad markers sit on their targets in the world.
- Vehicles with stick controls, a steering wheel and flight stick you can grab, and turrets that aim where you look.
- All controls rebindable in SteamVR's binding UI, plus an in-headset settings panel.

## Requirements

- Ravenfield on Steam (Windows).
- SteamVR. For a Quest, any way of running SteamVR games works; Steam Link is the simplest.
- Nothing else. BepInEx 5 is included and installed for you (an existing BepInEx 5 install, e.g. from RavenM, is reused).

## Install

1. Close Ravenfield.
2. Extract the release zip anywhere and double-click `install.cmd` (or run `install.ps1` in PowerShell).
   It finds Ravenfield through your Steam libraries. For a custom location: `install.cmd "D:\Games\Ravenfield"`.
3. Put on the headset, start SteamVR, then launch Ravenfield from Steam (or from the SteamVR library).

To remove it, run `uninstall.cmd`. Everything the installer replaced is restored from the backup it made in `Ravenfield/RavenfieldVR-backup/`.

**Flat play with the mod installed:** VR starts only when SteamVR is already running when you launch the game. Otherwise Ravenfield starts normally on your monitor and SteamVR is left alone. Steam launch options `-vr` force VR, `-novr` force flat mode, and the `AutoStartSteamVR` setting makes the game start SteamVR itself whenever a headset driver reports a headset.

## First minutes

- **Height** is calibrated automatically a moment after you first spawn: stand up straight. Re-do it any time from the settings panel ("Calibrate now"). Playing seated? Turn on "Seated play" and calibrate while sitting.
- **Settings panel:** double-click the left stick. **Recenter** is on that panel too.
- **Gun angle:** if guns feel tilted in your hand, use "Gun pitch" in the settings panel.

## Controls (Quest / Touch, right-handed)

| Input | On foot | In a vehicle |
| --- | --- | --- |
| Right trigger | Fire | Fire mounted weapon |
| Left trigger | Use: enter vehicle, ladder, interact | Use: leave vehicle |
| Grips | Grab: foregrip, magazine, slide, holsters | Grab: steering wheel, flight stick |
| Left stick | Move. Click: sprint. Double-click: VR settings | Steer / throttle (car), collective and yaw (helicopter), rudder and throttle (plane) |
| Right stick | Turn. Push up: jump. Flick down: crouch. Hold down: prone. Click: next weapon. Hold click: kick | Cyclic (helicopter, plane) |
| A | Magazine release | |
| B | Slide release. Hold: fire mode / under-barrel. Double: scoreboard | Countermeasures. Hold: auto-hover (helicopter) |
| X | Squad order (hold and point, release to send) | |
| Y | Squad map. Hold: pause menu. Double: loadout | |

Change anything in SteamVR: **Settings → Controllers → Manage controller bindings → Ravenfield**, or the "SteamVR bindings" button on the settings panel. Left-handed players: set "Weapon hand: Left" and restart; the default bindings mirror automatically.

Other gestures:

- **Holsters:** reach over your weapon-side shoulder and grip for your primary, to your weapon-side hip for the sidearm, to your chest for gear (three chest positions for gear slots 3-5).
- **Night vision:** grip with your off hand at the side of your head.
- **Map:** turn the back of your off-hand wrist toward your face. The minimap enlarges, and health and ammo appear on your wrist.
- **Crouching in real life** crouches your soldier.
- **Aiming down sights** is just bringing the sights to your eye. That gives you the game's aiming accuracy bonus, and scopes switch to their magnified lens.

## Reloading

The round counter on the side of your gun shows what is in it: `28+1` is 28 in the magazine plus one chambered, `RACK` means a round is waiting in the magazine but none is chambered, `OPEN` means the slide is locked back, `NO MAG` means the magazine is out.

**Magazine-fed weapons (rifles, pistols, SMGs, LMGs)**

1. Press A: the magazine drops out.
2. Move your off hand to your pouch (off-hand hip by default) and grip. A fresh magazine appears in your hand.
3. Bring its top to the magazine well. It seats with a click when close and roughly aligned.
4. If the slide locked back on empty, press B or grab the slide and let it go. If you reloaded before running dry you still have a round chambered and can keep shooting. Otherwise grab the slide or charging handle with your off hand, pull it back all the way and release.

Racking with a round already chambered ejects that round. Releasing a magazine near your pouch puts it back. By default rounds left in a dropped magazine go back to your reserve, as in normal Ravenfield. Turn off `ReturnRoundsOnMagDrop` and they stay in the magazine instead, so you can catch it and use it again.

**Bolt-action, pump and lever guns** chamber the next round only when you cycle them: after every shot, grab the bolt (or the pump, which sits where your support hand already is), pull back and release.

**Shotguns and other shell-fed weapons** take rounds one at a time (or in clips, when the weapon is set up for that). Grip at the pouch for a shell, push it into the gun's loading port, repeat. Rack to chamber.

**Rocket and grenade launchers:** grip at the pouch for a round and push it into the launcher.

**Grenades:** hold the trigger, throw with your arm, let go of the trigger at the end of the throw.

**Melee weapons** hit what you swing them through. The trigger still does the game's normal attack.

Prefer the game's normal reloads? Set "Reloading: Assisted" and the A button plays the standard reload.

### Odd modded weapons

Weapon rigs are worked out automatically from each gun's own animations: which part moves when it fires (the slide or bolt), which part leaves during the reload (the magazine), and where the animator puts the hands and sights. If a mod still behaves strangely, add an entry to `BepInEx/config/RavenfieldVR.weapons.json` (it is created with an example):

```json
{ "weapons": [ { "match": "mosin", "action": "Manual", "slide": "bolt", "slideTravel": 0.09, "gripForward": 0.01, "pitch": -4 } ] }
```

`action` is one of `SelfLoading`, `Manual`, `ShellFed`, `SingleShot`, `Throwable`, `Melee`, `NoAmmo`. `magazine` and `slide` take transform names from the weapon. Set `DumpWeaponRigs = true` in the config to log what was detected for every gun you pick up.

## Settings

Everything lives in `Ravenfield/BepInEx/config/com.qwavey.ravenfieldvr.cfg` and most of it is also on the in-headset panel. Notable ones:

| Setting | Default | What it does |
| --- | --- | --- |
| `StereoMode` | MultiPass | SinglePassInstanced is faster, but some map shaders then draw in one eye only |
| `RenderScale` | 1.0 | Resolution on top of SteamVR's own setting |
| `PostProcessing` | off | Copy the game's colour grading, bloom and AO to the headset (costs frame rate) |
| `TurnMode` / `SnapTurnAngle` | Snap / 30 | |
| `Vignette` | 0.35 | Tunnel vignette while moving with the stick |
| `PhysicalCrouch` | on | |
| `RoomscaleMovement` | on | Walking in your room moves your soldier, with collision |
| `MagazinePouch` | OffHandHip | Also Chest, DominantHip, OffHandChest |
| `TwoHandRecoilReduction` | 0.6 | How much a second hand on the gun tames recoil |
| `TurretAim` | Head | Turrets follow your head or your weapon hand |
| `HudDistance` / `HudHorizontalFov` | 4 m / 72° | Where the HUD frame sits and how wide it is |

## Building from source

No .NET SDK needed:

```powershell
./tools/fetch-deps.ps1     # BepInEx, OpenVR XR plugin, Roslyn compiler (into tools/)
./build.ps1 -Package       # compiles against your installed game, writes dist/RavenfieldVR and a zip
./install.ps1
```

`build.ps1` publicizes the game's assemblies into `build/refs` for compile-time access only; nothing from the game is redistributed. `tools/inspect_weapon_bundles.py` (needs `pip install UnityPy`) prints the rig of any workshop weapon bundle, which helps when writing overrides.

## Troubleshooting

- **The game opens on the monitor only.** Check `BepInEx/LogOutput.log` for lines starting with `[Ravenfield VR]`. "Not starting VR: SteamVR is not running" means SteamVR was not open yet: start it first or add `-vr`. "OpenVR failed to initialise" includes SteamVR's reason (for example `Init_HmdNotFound` when the headset is not streaming).
- **"Unity did not register the 'OpenVR Display' subsystem".** The runtime files under `ravenfield_Data` are missing. Run the installer again. A game update or "Verify integrity of game files" can remove them.
- **Controls do nothing in the headset.** Open the SteamVR binding UI for Ravenfield and select "Ravenfield VR default".
- **Low frame rate.** Lower `RenderScale` or SteamVR's resolution, keep `PostProcessing` off, reduce the bot count. Ravenfield is CPU-heavy with many bots.
- **A weapon sits wrong in the hand.** Use "Gun pitch" for all guns, or a per-weapon override (above).

## Known limitations

- Night vision and other effects the game applies to its own camera are not copied to the headset camera.
- Third-person vehicle cameras and the death camera follow the game's camera position, turning with it only around the vertical axis.
- Ravenscript mods that draw their own screen-space UI get the same head-locked HUD treatment as the game's HUD.
- Weapons with very unusual rigs fall back to estimated grab points until they get an override entry.
