# Tasks

1. [x] Set up build tooling (publicizer + Roslyn build script)
2. [x] Plugin skeleton, config and logging
3. [x] OpenVR XR runtime loader and event pump
4. [x] SteamVR Input: action manifest, default bindings, input reader
5. [x] Game input bridge (SteelInput patches, context-aware mapping)
6. [x] VR rig: stereo camera, tracking space, game-camera following, camera suppression
7. [x] Locomotion, snap/smooth turn, height calibration, physical crouch
8. [x] Procedural articulated hands
9. [x] Weapon rig analysis (gun body, grips, mag, slide, ADS line, action type)
10. [x] Weapon in hand: placement, two-handed aim, VR aim detection, fire, recoil
11. [x] Manual reload system (mag out/in, belt pouch, slide/bolt racking, chamber, slide lock, shells)
12. [x] Grenade throwing and melee swings
13. [x] World-space UI: canvas conversion, HUD frame, menu placement
14. [x] Laser pointer input module, dropdowns, SteamVR keyboard
15. [x] In-VR settings panel and wrist/gun ammo display
16. [x] Vehicles, ladders, death/spectator cameras
17. [x] Installer / uninstaller and packaging
18. [x] Placeholder sweep, flat-mode load test, docs
19. [x] Weapon analysis that holds for every modded rig
20. [x] All-weapons self-test report (every installed weapon, including workshop mods)
21. [x] Free hands: no snapping, live fingers, holster to empty hands
22. [x] Magazine sliding (insert along the well, slide out on release) for mags, shells and rockets
23. [x] Weapon sounds per action from the mod's own audio, no continuous reload audio
24. [x] Enemy weapons drop on death and can be picked up (swap with your own)
25. [x] Easy installer: one-click Setup.exe with install, update and uninstall
26. [x] Every weapon has a working magazine/feed and a cycling handle
27. [x] Machine guns: big box/drum/belt magazines and feed covers
28. [x] Semi/auto fire selector that respects each gun's real modes
29. [x] Visible ammo pouch, mag-kick tactical reloads, one-handed reloads
30. [x] Quest 3 (Touch Plus) controller support
31. [x] Graphics presets applied in game
32. [x] Launcher with VR/flat choice and graphics preset, opened from Steam's Play button
33. [ ] Measured performance pass over the mod's per-frame work
34. [x] Automated in-game smoke test (match with every mutator, every weapon equipped)
35. [x] Mutator compatibility
36. [x] Gun feel pass and VR UI de-slop
37. [ ] Weapon's own arms with IK (option next to the VR hands)
38. [x] Controllers satisfy "press any key"; gunner sights and HUD reticles collimated at infinity
39. [x] Physical steering wheel in cars and Humvees, weapons usable while driving
40. [x] Magazine and slide controls: B ejects, A releases slide/bolt, pull a seated magazine by hand, no mag kick on deep-well guns
41. [x] Every weapon fires one-handed, with more recoil than two-handed
42. [x] Placeholder sweep over all new code
43. [x] Bug hunt, 10 rounds
44. [x] Rebuild, in-game verification (flat, self-test, virtual headset), docs
45. [x] Private GitHub repo and release
46. [x] Grenade pins: pulled by hand (or A), fuse starts on the throw, pinned throws are duds you can pick up
47. [x] Dropped magazines keep their rounds, any free hand picks them up; loose props collide with the gun, never the player's body
48. [x] Belt-fed machine guns: box locked under a closed cover, belt laid on the feed tray by hand
49. [x] Scope lens on the scope model's own ocular, not an estimated eye point
50. [x] Materials that would render magenta repaired on copies (props, pouch, drops) and held weapons
51. [x] Autotest with simulated controllers: draw-skip, visible-model and grenade-pin checks
52. [x] Draw animation skipped on every weapon; weapon sounds never silenced after a reload slice
53. [x] First Quest session: head and hands from one tracking source, calmer HUD, Desktop Theater, no extra depth pass
54. [x] Second Quest session: laser bend, invisible third magazine and stuck grab pose, mag kick, dashboard resume, game weapon panel hidden
55. [x] Hands close around what they hold: fist on the controller handle, fingers stop on the object
56. [x] Holsters by weapon type: long guns over the shoulders, handguns on the hips, gear on the chest
57. [x] Magazines lying on the ground can be picked up
58. [ ] Headset re-test of 53-57
59. [ ] Invisible VR body that takes the hits when bots shoot you
60. [ ] Mag kick: knocking a seated magazine out with a fresh one still fails
61. [ ] Thumb placement on pistols
62. [ ] Better-looking, cheaper hand model and glove material
63. [ ] Tactical reload with both magazines in one hand (H3VR style)
64. [ ] Pistol magazines will not eject
65. [ ] Some M4s: pulling the charging handle does not cycle the bolt
66. [ ] Some M4s and other guns: magazines held at a strange position or angle
67. [ ] Some M4s: the magazine does not come out
68. [ ] Smoothing pass over the whole feel (the mod is far from perfect yet)
69. [ ] Bug hunt, 20 rounds, then improve, 10 rounds
---

For 1. Write `tools/Publicizer` (Mono.Cecil from BepInEx core) that makes Assembly-CSharp and firstpass members public for compile-time access, and `build.ps1` that runs Roslyn `csc.dll` on the .NET runtime with `-nostdlib` against the game's Managed folder, BepInEx and Harmony. Done when: `build.ps1` produces `RavenfieldVR.dll` from a trivial source file without errors.

For 2. `VRPlugin` BepInEx entry point with typed config sections (rendering, comfort, controls, weapons, UI), Harmony bootstrap, a scene/lifecycle hook, and a flat fallback when VR fails. Done when: the plugin compiles and its config binds every documented option.

For 3. `OpenVRRuntime` calls `SetUserDefinedSettings`, creates/starts "OpenVR Display" and "OpenVR Input", sets floor tracking origin, render scale and stereo mode, registers the tick callback and pumps `VREvent`s (quit, dashboard pause, keyboard). Done when: the loader compiles, reports init errors cleanly and leaves the game playable flat if SteamVR is missing.

For 4. Generate `actions.json` plus default bindings for oculus_touch, knuckles and vive_controller in the plugin folder; `VRInput` resolves handles, updates action state each frame, exposes digital edges, analog values, hand poses (Standing universe, Unity-converted), skeletal finger curls and haptics. Done when: every action used by the mod is declared, bound by default on Touch, and readable through `VRInput`.

For 5. Harmony postfixes on `SteelInput.GetAxis/GetButton/GetButtonDown/GetButtonUp` that merge VR actions into game KeyBinds by context (on foot, seated by vehicle type, menu, dead), and zero AimX/AimY. Done when: movement, jump, crouch, sprint, use, weapon switch, loadout, map, squad, pause and vehicle axes all map to VR actions.

For 6. `VRRig` owns the only HMD camera, follows the game's active reference (feet + yaw on foot, full seat rotation in vehicles, active camera otherwise), writes head pose into `fpCamera`, suppresses other screen cameras from the HMD and copies culling, far plane, background layer and clear settings. Done when: rig logic compiles and handles FP, seated, third-person, override and menu cameras.

For 7. Thumbstick locomotion relative to head or hand, snap or smooth turn, sprint, jump, height calibration, physical crouch driving the game stance with view compensation, optional vignette. Done when: movement direction, turning and stance changes are wired to the game controller.

For 8. Procedural hand meshes (palm + 3-segment fingers) driven by SteamVR skeletal summary curls, with poses for gun grip, support grip, pointing and fist. Done when: both hands render from tracked poses and curl their fingers from input.

For 9. `WeaponRig` analyzes a weapon on an inert clone: gun body, grip and support points from the arm rig, aim axis, ADS eye point, magazine(s), slide/bolt and travel, action type; cached per prefab with name-based and geometric fallbacks. Done when: analysis yields a complete rig (real or fallback) for any `Weapon` without throwing.

For 10. `VRWeaponController` places the animated weapon so the gun body sits in the hand after animation, supports two-handed steering, hides arm meshes, detects sight-to-eye aiming, fires on the trigger, applies recoil to the hand pose and suppresses 2D scope overlays. Done when: the player's active weapon follows the hand and shoots along its barrel.

For 11. `ManualReload` ammo state (mag rounds, chamber, slide lock) with mag release, dropping mags, belt pouch grab, insertion at the mag well, slide/charging handle/bolt racking with travel, pump/bolt cycling, shell-by-shell loading and launcher reload; patches `CanFire`, `Shoot`, `Reload`, auto-reload and resupply. Done when: a VR weapon only fires when chambered and every reload step changes game ammo correctly.

For 12. Throwables thrown with averaged hand velocity (projectile velocity override), melee hits from blade sweep velocity using `SphereCast` + `DamageInfo` with cooldown and haptics. Done when: grenades leave the hand on trigger release and melee damages on fast swings.

For 13. `CanvasManager` converts overlay canvases to world space, keeps pixel sizing, classifies HUD/menu/fullscreen effects, head-locks the HUD frame matched to `fpCamera` fov/aspect, and places menus in front when they open. Done when: all root canvases render in the HMD with correct layout.

For 14. `VRInputModule` laser pointer: hover, press, click, drag, scroll by thumbstick, dropdown lists, and SteamVR overlay keyboard for input fields. Done when: menus are fully operable with the controller.

For 15. In-VR settings panel (turn mode, heights, handedness, grip angle, reload realism, open SteamVR bindings) and ammo readout on the gun and wrist. Done when: settings change live and persist to the BepInEx config.

For 16. Seats (cockpit-locked rig, vehicle controls, mounted weapons aim by head/hand), ladders, death and kill cameras, spectator and override cameras. Done when: entering/leaving vehicles, ladders and dying keep the rig consistent.

For 17. `install.ps1` copies BepInEx, the plugin, XR runtime DLLs and the UnitySubsystems manifest with a backup; `uninstall.ps1` restores; `build.ps1 -Package` zips a release. Done when: install then uninstall leaves the game folder as it was.

For 18. Run the placeholder sweep over all source, build, install, launch flat to confirm the plugin loads and patches apply without errors, and write README with controls. Done when: the BepInEx log shows the plugin loaded with no exceptions.

For 19. Accept skinned bones (mag/slide bones with no renderer of their own) as parts; detect magazines that the reload animation hides/shows instead of moving; collect co-moving pieces (mag body + rounds) as one magazine; bake the skinned-mesh triangles weighted to the magazine bones into the prop mesh; compute the sight eye point from the player prefab's rest offsets so analysis does not need a live player; record reload/fire timelines (part motion keyframes and animation events). Done when: the analyzer handles mesh-renderer, skinned and toggle-based rigs and records timelines without a player in the scene.

For 20. A self-test (config flag or `-rfvr-selftest`) that analyses every weapon in WeaponManager.allWeapons after content loads and writes BepInEx/RavenfieldVR-weapon-report.txt: per weapon the action, how mag/slide/hands/sights were found (motion, visibility, name, virtual), sound cues mapped, and warnings. Done when: the report covers every installed weapon with no analysis exceptions.

For 21. The off hand stays at its tracked pose while gripping a foregrip, slide or wheel (the gun follows the hands, not the other way round); fingers keep responding to trigger/grip/thumb input while holding; putting the gun back to its holster and letting go leaves the hand empty and free. Done when: no code path pins a hand model to a fixed point on a weapon.

For 22. Inserting: within reach of the well the magazine locks onto the insertion axis and slides with the hand's motion along it; pulling back releases it; pushing to the end seats it with a click. Ejecting: the magazine slides out along the well before falling. Same for shells (loading port) and rockets (tube). Done when: seating only happens at full insertion depth and the prop never teleports into the gun.

For 23. Per action (mag out, mag in, rack back, rack forward, shot) play the mod's own sound: SoundBank events from its animations, the matching slice of its reload audio clip (cut at the moments the mag leaves, seats and the slide moves in its animation), or objects whose audio its animation triggers; procedural sounds only as fallback. The full reload clip never plays in manual mode; manual-action fire animations (automatic bolt cycling) are suppressed so bolt sounds and motion come from your hands. Done when: no sound plays that the player's action did not cause.

For 24. When a bot dies its active weapon drops as a physical model (third-person mesh, rigidbody) with the ammo it had; grabbing it equips it into its slot and drops your own weapon from that slot in its place; drops expire and are capped. Done when: dropped weapons appear, can be picked up and the swapped weapon fires and reloads normally.

For 25. Compile a Windows setup program (C#/.NET Framework 4.8 WinForms) that carries the release inside it: finds Ravenfield, shows what is installed, installs/updates/uninstalls with the same manifest logic as install.ps1, one window, no PowerShell needed. Done when: Setup.exe installs and uninstalls on this PC.

For 26. From the self-test: advanced-reload weapons with a real magazine are magazine weapons; charging handles that only move in the reload animation are found from that motion; static rigs (no animator) get their magazine and bolt from names and mesh geometry (a separate mesh under the receiver ahead of the grip, a part on top of the receiver behind the sight line); sound cues match one per action, leading the motion. Done when: the report shows a real or deliberately virtual part for every magazine/bolt weapon and no magazine weapon is treated as shell-fed.

For 27. Detect machine guns (automatic, large capacity, box/drum/belt magazine) and give them a longer, heavier insertion; detect a feed cover (a part the reload animation swings open on top of the receiver) that must be lifted before a new box goes in and closed after; heavy weapons steer with lag when held one-handed. Done when: an MG with a cover requires open, insert, close; one without slides its box/drum in along its own axis.

For 28. The FireMode button cycles the mod's own sub-weapons when it has them (their design decides the modes); otherwise an automatic weapon toggles semi/auto through the game's SetSingleFire/SetAutoFire, with looped fire audio cut to one shot in semi; a semi-only weapon (shotguns, pistols, rifles, DMRs authored without auto) never gets an auto mode. Done when: toggling works on automatic weapons and is refused on semi-only ones.

For 29. A pouch on the body shows what the held weapon feeds from (its own magazine mesh, shell or rocket) and the spare count; grabbing it gives you one. Pushing a fresh magazine into an occupied well knocks the old one out (mag kick), so the off hand alone can do a tactical reload. Done when: the pouch shows the current weapon's ammo and a fresh mag can kick out the inserted one and seat.

For 30. Quest 3 Touch Plus over Link, Air Link, Steam Link and Virtual Desktop: verify which controller type SteamVR reports for each path and ship bindings for all of them (oculus_touch, plus Index emulation), including thumbstick touch/click, A/B/X/Y, menu button, capacitive finger curl, haptics, and the grip pose offsets. Done when: every action is bound by default on each controller type a Quest 3 can show up as.

For 31. A Graphics preset (Low, Medium, High, Ultra) applied at startup and live from the VR settings panel: render scale, MSAA, shadow distance/resolution/cascades, LOD bias, pixel lights, texture mip limit, terrain detail and tree distance, particle/soft-particle settings, camera far clip, all layered over (not fighting) the game's own options. Done when: switching presets visibly changes these settings and the game's own options menu still works.

For 32. RavenfieldVR-Launcher.exe (WinForms) asks Play in VR / Play flat and the graphics preset, remembers the choice (hold Shift to show it again), and starts the game with the matching arguments while keeping Steam's playing state; Setup.exe sets Ravenfield's Steam launch option to run the launcher (with Steam closed, backed up, removed again on uninstall). Done when: pressing Play in Steam opens the launcher and both buttons start the game in the chosen mode.

For 33. Instrument the mod's per-frame work (Update/LateUpdate/BeforeRender) with timers and GC-allocation counters behind -rfvr-perf; measure a baseline; remove per-frame allocations and repeated searches in the hot paths; re-measure and keep only measured wins. Done when: a before/after table exists and the mod allocates nothing per frame in steady state.

For 34. -rfvr-autotest starts an instant-action match in flat or null-driver VR, enables every installed mutator, spawns the player, equips every weapon in turn and runs the manual reload steps programmatically (eject, insert, rack, shells), logs exceptions and perf, then quits. Done when: an autotest run completes and its log shows no exceptions from the mod.

For 35. Review every installed mutator (scripts, keybinds, weapon and ammo changes); make sure the mod adopts outside ammo/weapon changes and that mutator hotkeys can be pressed from the controllers. Done when: the autotest runs with all mutators enabled without errors and mutator keys are reachable in VR.

For 36. Weight and inertia by weapon size, recoil by class (pistol, rifle, shotgun, sniper, MG), two-hand stabilisation, haptics on every mechanical event, slide-lock feel; run /unvibe over the VR UI (settings panel, counters, pouch, launcher, Setup.exe). Done when: weapon classes feel distinct and the UI has no generic tells.

For 37. Option "Hands: VR hands / weapon's own arms": the mod's first-person arms stay visible; two-bone IK (shoulder/elbow/hand found from the arm chain, twist helpers skipped) re-roots both arms at body-relative shoulders, the gun hand keeps its authored grip on the weapon, the off hand follows the controller (or the foregrip / slide / magazine it holds), elbows bend toward a natural pole, out-of-reach shoulders slide toward the hand. Idea from github.com/GDani31/ravenfield-vr-mod (no license, so own implementation). Done when: with the option on, the mod's gloves hold each gun as authored and the off arm reaches the off controller without tearing.

For 38. Controller buttons count for Input.anyKeyDown (and the Lua proxy) so "press any key" screens continue; vehicle seat HUDs (gunner reticles, near 3D sight renderers) are head-locked at a large distance with their angular size kept, drawn by an overlay camera so the barrel never covers them. Done when: a press-any-key prompt continues from the controller and a tank gunner reticle shows single in stereo.

For 39. In first person in a car or Humvee (vanilla vehicles, and mod cars whose wheel the analysis finds: the transform the game turns with A/D), the steering wheel can be grabbed with one or both hands and turned; its angle drives the car's steering instead of the stick, and the hands stay on the rim while held. With a hand off the wheel, the player's weapon can be drawn and fired from the seat (normally impossible), with the other hand still steering. Tanks, helicopters and planes keep stick controls. Done when: turning the grabbed wheel steers the car, and a weapon fires from the driver seat.

For 40. Quest bindings: B ejects the magazine, A releases a locked slide / bolt catch (also by pulling the slide or bolt back and letting go). The off hand can grab the seated magazine and pull it out along the well. Mag kicking only works where the magazine sticks out of the gun: guns whose well swallows most of the magazine (KRISS Vector style) need the button or a hand pull. Done when: bindings match, a seated magazine can be pulled out by hand, and the kick is refused on deep-well guns.

For 41. Every weapon fires while held in one hand; recoil is stronger one-handed (scaled by weapon class: pistols barely, rifles more, shotguns, LMGs and launchers most) and heavy weapons sag and lag when not supported. Done when: one-handed recoil is visibly larger than two-handed for a rifle and similar for a pistol.

For 42. Run the placeholder sweep over all source (stubs, fake/simulated values, truncated logic, settings that do nothing). Done when: no real placeholder remains.

For 43. Ten bug-hunt rounds over different areas, each verified and fixed. Done when: ten rounds are reported with their fixes.

For 44. Build, install, flat launch, self-test over all installed weapons, virtual-headset launch, README and memory updated. Done when: logs show no exceptions and the report is reviewed.

For 45. Create the private GitHub repo QWavey/ravenfield-vr, commit the project (dependencies and decompiled game code excluded), push, and attach Setup.exe, the launcher and the zip to a release. Done when: the repo and release exist and are private.

For 46. Fused grenades (projectile is a GrenadeProjectile; bags, kits and charges excluded) carry a pin: the mod's own pin/ring part when it has one, else a split ring on top. The off hand grips it and pulls it 5.5 cm away (or A flicks it out); holding does nothing, the fuse starts when the grenade leaves the hand. Thrown with the pin in, a physical dud lands instead; grabbing it (or the clean-up) refunds the grenade. Done when: the autotest reports "dud, no live grenade, refunded" and "live grenade thrown" for every grenade.

For 47. Ejected and dropped magazines keep their rounds (Owner = the ammo they came from); any free hand picks up any loose magazine; seating gives the rounds, stowing returns them, despawn returns them capped at the reserve limit. Loose props live on a layer bullets ignore and never collide with the player's controller, hitboxes or ragdoll; a light physics stand-in on the held gun lets them bounce off it. Done when: no rounds are created or lost across eject, pick-up, seat, stow and despawn.

For 48. With a feed cover: B and the hand pull refuse while the cover is shut over a laid belt ("OPEN COVER"); after seating a box the belt end is taken from the box mouth (a strip of rounds follows the hand) and laid on the feed tray; the counter walks through LAY BELT, CLOSE COVER, RACK. Option BeltByHand. Done when: the autotest's MG reloads report "belt laid".

For 49. For weapons with a 2D scope overlay, the analysis measures the scope model (meshes named like a scope, a slim tube over the bore) and puts the eye line and the lens on its ocular end, sized to the tube. Done when: the self-test lists "scope model (ocular measured)" for the vanilla RECON LRR.

For 50. A material whose shader cannot run (or an empty slot) is swapped for the game's own compiled shader of the same name, else Standard, else a neutral gunmetal; working materials are untouched. Applied to magazine props, the pouch display, dropped weapons, grenade duds and the held weapon. Done when: copies never render magenta.

For 51. Without controllers (virtual headset) the autotest holds simulated hands where a player would, so the full held-weapon path runs and the mirror shows the real VR view; it checks the draw animation is skipped, that each weapon has a visible model, and throws every grenade with and without its pin. Done when: a VR autotest completes with only mod-side problems.

For 52. The draw clip is stepped past in 0.1 s increments (one transition per animator update), now and on the next frame for animators enabled that frame; one-shot weapon sounds play through their own source so a faded reload slice can no longer leave them at zero volume. Done when: the autotest finds no draw animation playing and every magazine weapon has a mag-in and mag-out sound.

For 53. The first headset session showed the hands in the upper right, a wobbling view and the HUD glued to the face. Head cameras now take their pose from the OpenVR compositor (Unity's own XR camera tracking off) and controllers are read in the compositor's tracking space, so head and hands agree; the HUD frame follows the head lazily; the forced depth pass per eye is gone; README explains turning off Steam's Desktop Theater. Done when: the log's input check shows the head at standing height with both controllers below it (seen: head 1.62 m, 81-90 fps in the menu).

For 54. From the second session: the laser is drawn from the current hand pose against the current panels; magazines are copied at their recorded scale (the third one was copied from the hidden, shrunken magazine: invisible but held, so the hand stayed closed); the mag kick takes a wider window and measures speed relative to the gun; closing the SteamVR dashboard resumes the match the mod paused for it (sound, physics, held props); the game's weapon panel is hidden in VR (HideGameWeaponHud); holsters tick when the hand reaches one. Done when: built and committed (d42b228); the headset check is 58.

For 55. SteamVR's grip pose is the middle of the controller handle (measured on the Quest Plus render model: within 6 mm of the handle axis), so it is inside the real fist. The hand model now puts its fist hollow there (3.3 cm toward the fingers, 2.6 cm in front of the palm, measured on the model's finger chain) instead of its palm centre, guns are placed from the model's palm centre (where the weapon's own hand holds its grip), and magazines held at the grip pose land in the curled fingers. Wrapping fingers stop at 0.85 curl on a grip, magazine, grenade or wheel and 0.75 on a handguard instead of closing through it. Done when: built; the visual check is 58.

For 56. Holsters are assigned from what each weapon is, not the loadout slot it came in: primary and large-gear weapons (rifles, launchers, long melee) over the dominant shoulder, then the other shoulder; secondaries and small guns on the dominant hip, then a cross-draw on the other hip; grenades, kits and tools on the chest. A full place passes the weapon on to the next free one; ShowGrabZones draws the holsters in use. Done when: built; the headset check is 58.

For 57. A dropped magazine is judged by its own shape (collider), not its transform pivot, and a magazine within reach wins over the pouch (which comes down with you when you bend over). The off hand picks loose magazines up whatever the drawn gun's reload mode, and dropped magazines stay 60 s instead of 20 (existing configs still at 20 are moved by a one-time ConfigRevision migration). Done when: built; the headset check is 58.

For 58. In the Quest: laser straight while looking around, magazines and gun grips inside the closed hand, mag kick, third and later magazines from the pouch, open hand after letting go, holsters on the back and hips, dashboard round trip with sound, and picking a magazine up off the ground with either hand. Done when: the player confirms each in a session.

For 59. Check first what the game already does: the player actor's hitboxes sit where the game puts the actor, which in VR may not match where you physically stand, crouch or lean. Give the player an invisible body that follows the headset (head, torso from the body zones, arms from the hands) and carries the actor's hitboxes, so bots aim at and hit where you really are, and damage, headshots and hit feedback (haptics, screen tint) land on it. Done when: a bot's shot at your real head/torso registers on the player, and leaning out of cover exposes you.

For 60. Reported again after the second fix (wider window, speed relative to the gun): bringing a fresh magazine up into the base of the seated one does not knock it out. Log the kick test's numbers (along, lateral, angle, speed, kickable flag) while trying it in the headset, find which condition fails, then fix that one. Done when: a kick ejects the seated magazine on an M4 and an AK in the headset.

For 61. The thumb looks wrong on pistol grips. Compare the thumb's base rotation and curl in the Weapon role against the weapon's own hand pose (GripCurls) for pistols; the thumb should lie along the left side of the frame (right hand), not stick out or cross the slide. Done when: the thumb rests along the frame on the Glock and the M1911 in the headset.

For 62. The procedural glove (primitive capsules and cubes, one flat material) looks crude. Build a better hand: smoother joints (fewer seams), knuckle and palm shape, a proper glove material (normal detail, matte fabric, team tint kept), and fewer draw calls (merge the static palm parts, one material, no shadow casting). Done when: the hand looks like a glove up close and its draw calls/renderers are fewer than now (measure both).

For 63. H3VR-style tactical reload. While the off hand holds a fresh magazine near the gun's seated magazine, holding the magazine release puts the ejected magazine into the same hand, so the hand holds both (research how H3VR does magazine palming before building it). Seat the fresh one and the old one stays in the hand; letting go of grip drops both. Works on Glocks and any detachable-magazine weapon. Done when: in the headset, mag release with a fresh mag at the well leaves both magazines in the off hand, seating one keeps the other, and releasing grip drops what is held.

For 64. Pressing B on pistols does not eject the magazine. Check MagRelease binding and the Manual/UsesDetachableMag/MagInserted conditions for pistols (slot 1), the pistol's analysed magazine part (virtual vs real) and whether the eject path runs. Done when: B drops the magazine on the Glock, M1911 and a modded pistol.

For 65. On some M4-pattern rifles pulling the charging handle back does not cycle the bolt (no chambering, no ejection). Find which ones (self-test: handle found? slide = bolt or carrier?), and make the handle drive the bolt with the rack stroke. Done when: racking the handle on every M4 variant in the report chambers a round in the headset or autotest.

For 66. Some magazines sit oddly in the hand. Check the magazine's analysed insert direction, front axis and base/tip points for the affected guns (HoldPose uses them); wrong axes from a skinned or offset magazine part tilt it. Done when: the magazines of the reported guns sit upright in the fist with the front facing forward.

For 67. On some M4s the magazine does not come out (eject or hand pull). Identify them from the self-test (magazine method, virtual parts, BoxLocked, deep-well rule) and fix the cause. Done when: every M4 variant ejects and releases its magazine by hand.

For 68. A smoothing pass over how everything feels in the hand: grab/release transitions, haptics, part motion (no snapping), menu and laser behaviour, comfort. Collect everything reported in the headset sessions and polish rather than add. Done when: the next headset session has no feel complaints from the existing list.

For 69. Run /bug-hunt 20 (twenty different areas, verify and fix), then /improve 10 (ten measured rounds, keep only what proves better). Done when: both runs are finished, built, installed, committed and summarised.
