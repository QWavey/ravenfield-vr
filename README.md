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
2. Download `RavenfieldVR-Setup-<version>.exe` from the newest release on GitHub (Releases; the older ones are the earlier versions) and run it. It finds Ravenfield in your Steam libraries (or pick the folder) and installs. Keep "Pressing Play in Steam opens the launcher" ticked: Steam then asks every time you press Play whether to start in VR or on the monitor, and with which graphics preset. If Steam is running, Setup asks to close it for a moment (Steam rewrites its launch options when it exits) and starts it again.
3. Put on the headset, press Play on Ravenfield in Steam, choose **Play in VR**.

Setup also adds **Ravenfield VR** to the list Steam shows when you press Play (next to "Ravenfield (No Mods)" and "Ravenfield (Safe Mode)"): pick it to start straight in VR. Steam gets that list from Valve with the game's info, so a Ravenfield update can take the entry away again; run Setup again to put it back.

To update, run the newer Setup. Or, in any Setup, use **Advanced: install another version from GitHub…** at the bottom to pick and install any published version (newer or older) straight from the Releases page — handy for rolling back without hunting down an old download. To remove the mod, run Setup and press Uninstall: everything it replaced is restored from `Ravenfield/RavenfieldVR-backup/`, the launcher is taken out of Steam's launch options again (your own launch options stay), and the Ravenfield VR entry leaves Steam's Play list.

Setup checks a release before it touches the game (a damaged or cut-off download changes nothing), says **Downgrade** when it is older than what is installed, and offers **Repair** after an install that stopped part way (a file locked by another program, a full disk): that install's record is kept, so Uninstall still puts everything back. Moving the game with Steam's "Move install folder" keeps the backups working. If BepInEx is already installed but its `winhttp.dll` is missing, Setup adds the loader and keeps your BepInEx; if the game had another tool's `winhttp.dll`, Uninstall gives it back (or leaves BepInEx's while BepInEx stays for your settings or other plugins).

No Setup? The release zip has `install.cmd` / `uninstall.cmd` that do the same from PowerShell, without the Steam launch option; start the game with `RavenfieldVR-Launcher.exe` in the game folder instead.

The launcher remembers your choice when you tick "Start this way from now on"; hold **Shift** while pressing Play to see it again. Without the launcher, VR starts only when SteamVR is already running (or, with `AutoStartSteamVR = true` in `BepInEx/config/com.qwavey.ravenfieldvr.cfg`, also when a headset is connected: the mod then starts SteamVR itself); `-vr` forces VR, `-novr` forces flat.

## First minutes

- **Height** is calibrated a moment after you first spawn: stand up straight. Redo it from the settings panel ("Calibrate now"). Playing seated? Turn on "Seated play" and calibrate while sitting.
- **Settings panel:** double-click the left stick. Recenter, SteamVR bindings and Mod keys are at the bottom.
- **Gun angle:** if guns feel tilted in your hand, use "Gun pitch" on the panel.

## Controls (Quest Touch, right-handed)

| Input | On foot | In a vehicle |
| --- | --- | --- |
| Right trigger | Fire | Fire mounted weapon |
| Left trigger | Use: enter vehicle, ladder, interact | Hold 1 s (the hand on your seat's side): leave vehicle |
| Grips | Grab: foregrip, magazine, slide, bolt, cover, grenade pin, magnifier, holsters, pouch, anything lying on the ground, a soldier. Closed on an empty hand: a fist. Hold half a second on a vehicle: get in | Grab: steering wheel (one or both hands), flight stick |
| Left stick | Move. Click: sprint (you can shoot while sprinting). Double-click: VR settings. Hold: scoreboard | Steer and throttle (car), collective and yaw (helicopter), rudder and throttle (plane). Click: next seat |
| Right stick | Turn. Up: jump. Flick down: crouch. Hold down: prone. Click: next weapon. Hold click: kick | Cyclic (helicopter, plane) |
| B | Drop the magazine / open a break-action or revolver. Hold: slow motion (also in menus and while dead) | Reload a mounted gun |
| A | Slide or bolt release; pulls a grenade's pin. Hold: fire mode (semi/auto, or the weapon's own mode switch) | Countermeasures. Hold: auto-hover (helicopter) |
| X | Squad order (hold and point, release to send; the objectives' flags show with their names while you point; point at an enemy you can see to have the squad attack them) | |
| Y | Squad map. Hold: pause menu. Double: loadout | |
| Menu (≡) | SteamVR's own dashboard (the match pauses behind it); not bound by the mod | |

Change anything in SteamVR: **Settings → Controllers → Manage controller bindings → Ravenfield**, or the "SteamVR bindings" button on the settings panel. Left-handed players: set "Weapon hand: Left" and restart; the default bindings mirror automatically.

Other gestures:

- **Holsters** (laid out like Boneworks): weapons are carried by what they are. Rifles, launchers and other long weapons hang down your back from the shoulders, grip up (weapon side first, then the other shoulder); pistols sit under your arms; grenades, kits and tools at the small of your back, then on the front of your belt. Reach there with your weapon hand and grip; a short buzz tells you your hand is at a holster with something in it. Put a gun back where it lives and let go to have both hands free. `HolsterLayout = Classic` (or "Holsters: Classic" on the panel) brings back the earlier layout: pistols on the hips, gear on the chest.
- **Night vision:** grip with your off hand at the side of your head.
- **Map:** turn the back of your off-hand wrist toward your face. Health and ammo appear on your wrist.
- **Crouching in real life** crouches your soldier.
- **Aiming down sights** is just bringing the sights to your eye. Scopes switch to their magnified lens.
- **Flip magnifier:** grip it with your free hand and push it about 4 cm sideways (or twist it); the aim button no longer flips a magnifier your hand can reach.
- **Squad hand signs** (free hand, raised to your shoulder, held half a second): flat hand pointing forward sends the squad there; a fist tells it to stay.
- **Hitting with a gun:** swing it into an enemy (any part of it); the faster and heavier, the harder.

## Reloading

The counter on the side of your gun shows its state: `28+1` (28 in the magazine plus one chambered), `RACK` (no round chambered), `OPEN` (slide locked back, or a break-action open), `NO MAG`, `COVER OPEN` / `CLOSE COVER` on machine guns. The pouch on your body shows a copy of what the gun takes and how many spares you have.

**Magazine-fed weapons**

1. Press B: the magazine slides out and drops. Or grab the seated magazine with your off hand and pull it out. A dropped magazine keeps the rounds left in it: pick it up again (with either free hand, whatever you are holding) and seat it to use them. Magazines lying around are cleared after a minute (`DroppedMagLifetime`); rounds still in them then go back to your reserve, so nothing is lost.
2. Grip at your pouch (on your belt at the side of the off-hand hip by default): a fresh magazine is in your hand.
3. Bring its top to the well. Near it, the magazine locks onto the well's axis and slides with your hand; push it all the way to seat it. Your hand stays on the seated magazine until you open your grip; to pull it out again, let go and grab it once more.
4. If the slide locked back on empty, press A, or pull the slide or charging handle back and let it go. Or slam the magazine home: driven in hard, it knocks the slide forward and chambers a round, as in Boneworks (a gentle push leaves it locked).

Tactical reload with one hand: push a fresh magazine hard into the base of the seated one and it knocks the old one out, then slide the new one in. This works where the magazine sticks out of the gun; pistols and guns whose well swallows the magazine (KRISS Vector style) need B or a hand pull.

**Bolt-action, lever and pump guns** move only by hand: pull the bolt or pump back and push it forward again to chamber the next round. Let go half way and it stays there.

**Shotguns with a tube** take shells one at a time at the loading port; semi-automatic ones cycle themselves after each shot, pump guns need the pump.

**Break-action shotguns, revolvers and break-action launchers:** press B to open (the barrels tip down, the cylinder swings out, fired cases fall out). Push shells into the chambers from your pouch (a revolver with a speedloader takes all six at once). Flick your wrist up or press B to close.

**Machine guns** with a feed cover: grab the cover's rear latch and lift it (with the cover shut and the belt in the feed the box will not come off). Drop the old box (B), slide a new box onto its rails (a short, heavy slide), then take the belt end at the mouth of the box and lay it on the feed tray under the cover; it clicks in. Press the cover down over the belt and rack if the bolt is forward. The counter walks you through it: `LAY BELT`, `CLOSE COVER`, `RACK`.

**Rocket and grenade launchers:** grip at the pouch for a round and push it in.

**Grenades:** pull the pin with your other hand (grip the ring at the top and pull it away), or press A to flick it out one-handed. Nothing happens while you keep holding the grenade; hold the trigger, throw with your arm and let go of the trigger at the end of the throw, and the fuse starts as it leaves your hand. Thrown with the pin still in, it is a dud: it bounces and lies there, and picking it up puts it back in your pouch. Put the grenade away with the pin out and the pin goes back in. Smoke grenades work the same; ammo bags, medic bags and charges have no pin. **Melee weapons** hit what you swing them through. So does any gun, anywhere from its butt to its muzzle, and an empty hand closed into a fist (grip pressed, nothing held): punch.

**Soldiers:** reach for a soldier with an empty hand and grip. They go limp and hang from your hand; drag them, slam them into things, or swing and let go to throw them. A hard impact hurts them and any soldier they hit, by the speed squared as in Boneworks (a shove does nothing, a slam a fifth of their health, at most a third in one impact), so it takes a few slams to finish one off. Your own team's soldiers are hurt like anyone else, as by your bullets and knife. Your hand sits on the body where it took hold (chest, arm, hand, leg), its fingers round it. Only an empty hand takes hold: the hand on your gun, a grenade or a magazine never grabs a soldier, the other, empty hand does.

- **Human shield:** hold a soldier up in front of you; bullets hit him, his own friends' too.
- **His gun:** swing a hand into a soldier's gun to knock it aside (his aim goes off and he holds his fire for a moment). Grip under the middle of his gun to pull its magazine out: he has to reload, and the magazine is in your hand. Grip it anywhere else to take it from him: he draws his other weapon (one with no other weapon only has it knocked aside). Gripping his hand or arm takes him, not the gun.
- **From behind:** reach for an enemy's head from behind and grip: he is held still. Turn your wrist hard (a quarter turn) to break his neck; let go and he is free again. It works on your own team too.
- **Strapped grenade:** holding a soldier, draw a grenade in the other hand, pull the pin (or press A) and let it go slowly on his body (walking is fine): it sticks to him. Throw him away before it goes off: he dies in the blast, everyone near takes the grenade's normal explosion damage. It kills one of your own team the same way.

Loose magazines, shells and duds are physical: they bounce off your gun while they fall, lie still once they have landed, and never catch on your own body. Bullets pass through them. Fired cases fly out of the ejection port and lie on the ground for a while.

Pistol magazines go in the grip, where your gun hand is: once a magazine is lined up and half way in, it is drawn home the rest of the way, so the two controllers never have to touch.

Easing a slide or bolt back part way (a press check) shows the round in the chamber; pulled all the way it comes out.

The gun in your hand is stopped by the world: pushed into a wall it stays at the wall (you feel a knock when it meets something), and it can rest on a sandbag or a crate. A stock set in your shoulder takes part of the kick and holds the gun up.

A grenade or shell going off close by throws dirt on your view (water for a blast in water); it runs down and fades in a few seconds (`ExplosionDirt`).

A scope shows its shadow when your eye is off the sight line: a dark crescent comes in from the side your eye has moved to, so you know which way to move your head.

Taking the headset off pauses the match (on headsets with a wear sensor), as opening the SteamVR dashboard does; putting it back on carries on.

Holding a long gun with one hand works, but it kicks harder, droops a little and trails your wrist; rest your other hand on the handguard to steady it. Pistols are made for one hand.

Prefer the game's normal reloads? Set "Reloading: Assisted" and B plays the standard reload.

## Mod keys and fire modes

Many workshop weapons and mutators come with scripts that listen for keyboard keys: X for a fire-mode switch, H for a flashlight, J for a laser, I to inspect, T for a bipod or HUD toggle. Open **Mod keys** from the settings panel (double-click the left stick, "Mod keys" at the bottom): a panel listing every key the held weapon's scripts and the running mutators listen for, labelled with what it does. Point and press. The press goes only to that script.

Holding A on a weapon with its own fire-mode script presses that script's key (it switches with its own animation and sound). Weapons whose mod built the modes as sub-weapons cycle those. Other automatic weapons switch between semi and auto; semi-only weapons (most pistols, shotguns, rifles and DMRs) stay semi.

## Driving

Get in by holding your grip on a vehicle for half a second with an empty hand (or the left trigger as before); get out by holding the trigger of the hand on your seat's side for a second; click the left stick to move to the next seat. Grab the steering wheel of a car or Humvee with one or both hands and turn it; the car steers with it. Let go and the left stick steers again. While driving, reach to a holster to draw that weapon and shoot with one hand while steering with the other; put it back to get the horn again. Tanks, helicopters and planes use the sticks (helicopters and planes also have a grabbable flight stick).

## Graphics

Pick **Low, Medium, High or Ultra** in the launcher or on the settings panel ("Graphics"). A preset sets the render resolution, MSAA, shadow distance and quality, level of detail, per-pixel lights, texture size, vegetation and view distance, layered on top of the game's own options. "Game" leaves graphics to the game's options menu. Render scale stays adjustable after picking a preset. The presets are tuned for VR, where shadows, MSAA and per-pixel lights are paid once per eye: Low has no shadows (about the game's "Fast" quality), Medium hard shadows to 30 m ("Simple"), High soft shadows to 60 m ("Good"), Ultra to 100 m with 4x MSAA ("Beautiful"). If you see ghosting or things lagging in front of you, the frame rate is below the headset's: pick a lower preset (or "Game" with a low quality in the game's options).

## Odd modded weapons

Weapon rigs are worked out from each gun's own animations: what moves when it fires (slide, bolt), what moves in the reload (magazine, charging handle, cover, barrels, cylinder), where the mod's hands hold it and how its fingers curl. Where a bolt is part of the receiver mesh, the mod's own racking hand shows where the handle is, and that piece of the mesh is cut out to move with your hand (or a small handle is added when the mesh cannot be read).

If a mod still behaves strangely, add an entry to `BepInEx/config/RavenfieldVR.weapons.json` (created with an example):

```json
{ "weapons": [ { "match": "mosin", "contains": true, "action": "Manual", "slide": "bolt", "slideTravel": 0.09, "gripForward": 0.01, "pitch": -4, "magKick": "false" } ] }
```

`match` is the weapon's whole name (its prefab's, its entry's or its display name, in any case), so `"match": "mosin nagant"` names only that gun. `"contains": true` lets `match` be a part of the name: `"mosin"` then reaches every gun with it in its name. `action` is one of `SelfLoading`, `Manual`, `ShellFed`, `SingleShot`, `BreakOpen`, `Throwable`, `Melee`, `NoAmmo`. `magazine` and `slide` take transform names from the weapon. `magKick` allows or forbids knocking out the magazine with a fresh one. `pitch` and `gripUp` turn the gun about its own upright, which since 2026-10-16 is the gun body's nearest axis to upright (not the weapon root's, which many rigs leave a few degrees rolled): an entry tuned before may need a degree or two less.

**Checking every installed weapon:** start the game with `-rfvr-selftest` (works flat, from the main menu). `BepInEx/RavenfieldVR-weapon-report.txt` then lists for every weapon how each part was found, the sounds bound to each action and where each reload sound comes from (the mod, a vanilla weapon, or synthesised), and anything suspicious. `-rfvr-dumprig=name` adds the full transform tree of matching weapons (`RavenfieldVR-rig-dump.txt`).

**Automated test:** `ravenfield.exe -map island2023 -nointro -rfvr-autotest [-vr]` plays a match by itself with every installed mutator on, equips and fires every weapon and (in VR, holding it in simulated hands when no controllers are connected) checks that its draw animation is skipped and its model is visible, runs its reload and throws each grenade with and without its pin, then writes `BepInEx/RavenfieldVR-autotest.txt` and quits. Add `-rfvr-quick` for a short run: a few representative weapons instead of every installed one.

**Checks without the game:** `powershell -File tools/tests/run-tests.ps1` builds and runs the offline checks (the installer's path containment, the Steam config reader, the left-handed bindings). `-rfvr-perf` measures the mod's per-frame cost in any session. `-vr -rfvr-menutest` records ten seconds of the main menu (pictures, how far the view moved, frame times) into `BepInEx/RavenfieldVR-menutest.txt` and quits.

## Settings

Everything lives in `Ravenfield/BepInEx/config/com.qwavey.ravenfieldvr.cfg`; most of it is also on the in-headset panel. Notable ones:

| Setting | Default | What it does |
| --- | --- | --- |
| `GraphicsPreset` | Medium | Low, Medium, High, Ultra or Game |
| `RenderScale` | 1.0 | Resolution on top of SteamVR's own setting (a preset sets it) |
| `StereoMode` | MultiPass | Leave it: the game's own shaders have no single-pass variants, so with SinglePassInstanced the world draws in one eye only |
| `HoldFrameRate` | off | While frames keep missing the headset's rate, lower the preset's costliest settings a step at a time (shadow distance, shadows, level of detail, grass, per-pixel lights, last 15% of the resolution) and bring them back when there is room. The wrist display says how many steps. Also on the panel ("Hold the frame rate") |
| `PostProcessing` | off | Copy the game's colour grading, bloom and AO to the headset (costs frame rate) |
| `WristFrameRate` | on | The wrist display also shows the share of frames at the headset's rate and the GPU's time a frame (last few seconds) |
| `BodyShadow` | off | Your own soldier's shadow on foot and in first-person seats. Its arms and gun move in the game's animation, not yours, so the shadow wanders across your hands |
| `Hands` | VRHands | VRHands, or WeaponArms for the weapon's own first-person arms |
| `TurnMode` / `SnapTurnAngle` | Snap / 30 | |
| `HolsterLayout` | Boneworks | Boneworks, or Classic (pistols on the hips, gear on the chest) |
| `MagazinePouch` | OffHandHip | Also Chest, DominantHip, OffHandChest |
| `ShowPouch` | on | Show the gun's ammo at the pouch |
| `WeaponWorldCollision` | on | The gun in your hand is stopped by walls, crates and the ground |
| `GloveSize` | 0.88 | Size of the tactical glove (applies at the next start) |
| `ExplosionDirt` | on | Dirt on your view from close explosions |
| `TwoHandRecoilReduction` | 0.4 | How much a second hand on the gun tames recoil |
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
| `MagSlapReleasesSlide` | on | A loaded magazine slammed home sends a locked slide or bolt forward; off leaves it for A or a rack |
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
- **Low frame rate, ghosting, things lagging or doubled in front of you.** The frame rate is below the headset's, and SteamVR fills in the missing frames. Look at your wrist: its last line says how many frames of the last few seconds kept the headset's rate and how long the GPU took for each ("90 Hz · 97% on time · GPU 6.2 ms"), so you can switch presets on the settings panel and see the effect. GPU time over the headset's frame time (11.1 ms at 90 Hz) means the preset is too heavy: pick a lower one (Low has no shadows) or a smaller render scale. GPU time well under it with frames still late means the CPU is the limit: reduce the bot count (Ravenfield is CPU-heavy with many bots). Once a minute of play the log has a line "Frame rate over the last minute of play: N% of frames at the headset's 90 Hz" with the GPU's median, the eye size and the settings in use. Keep `PostProcessing` off.
- **A weapon sits wrong in the hand.** Use "Gun pitch" for all guns, or a per-weapon override (above).
- **Windows Defender flags Setup as `Program:Win32/Wacapew.A!ml` (versions before 1.10.1).** A false positive. `!ml` is Defender's machine-learning guess, and **releases up to and including 1.10.0** were built as an unsigned installer **with no version metadata and no icon** (`FileVersion 0.0.0.0`, empty product/company) — exactly what that heuristic flags on a fresh, low-reputation download. **Fixed in 1.10.1 and later:** the setup and launcher are now compiled with full assembly metadata (product, company, version, description) and a product icon, which is what tells those models a file is normal software; on current Defender the rebuilt binaries scan clean. **Use the latest release.** If a build still flags (reputation builds only after a release has been downloaded a few times), allow the file in Windows Security and — the authoritative fix — report it to Microsoft at <https://www.microsoft.com/en-us/wdsi/filesubmission> (pick "Software developer"; usually cleared within a day). A code-signing certificate removes it for good, but costs money and is optional for a free mod.

## Known limitations

- Night vision and other effects the game applies to its own camera are not copied to the headset camera.
- Third-person vehicle cameras and the death camera follow the game's camera position, turning with it only around the vertical axis.
- Ravenscript mods that draw their own screen-space UI get the same head-locked HUD treatment as the game's HUD.
- Weapons without any animator get their parts by name only; a few of those keep an estimated handle position.
- Materials a mod ships with a shader the graphics card cannot run (magenta) are swapped for the game's own version of that shader, or a plain one, so they may look a little different from what the modder intended.
