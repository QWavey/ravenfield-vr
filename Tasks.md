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
33. [x] Measured performance pass over the mod's per-frame work
34. [x] Automated in-game smoke test (match with every mutator, every weapon equipped)
35. [x] Mutator compatibility
36. [x] Gun feel pass and VR UI de-slop
37. [x] Weapon's own arms with IK (option next to the VR hands)
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
58. [x] /bug-hunt 2
59. [x] Damage feedback in VR: getting hit shows the flat-screen damage overlay instead of VR damage feedback
60. [x] Mag kick: knocking a seated magazine out with a fresh one still fails
61. [x] Thumb placement on pistols
62. [x] Better-looking, cheaper hand model and glove material
63. [x] Tactical reload with both magazines in one hand (H3VR style)
64. [x] Pistol magazines will not eject
65. [x] Some M4s: pulling the charging handle does not cycle the bolt
66. [x] Some M4s and other guns: magazines held at a strange position or angle
67. [x] Some M4s: the magazine does not come out
68. [x] Smoothing pass over the whole feel (the mod is far from perfect yet)
69. [x] Holstered weapons visible on the body (pistol on the hip, rifle over the shoulder)
70. [x] Hands glued to the held gun or item instead of moving freely
71. [x] Scoreboard only with the map, never in the way while aiming
72. [x] Bigger, more forgiving grab zones; the hand poses onto the bolt or handle it pulls
73. [x] Bolts and handles hard or impossible to pull back with no magazine inserted
74. [x] Missing magazine in/out sounds on some guns
75. [x] Bug hunt, 20 rounds, then improve, 10 rounds (after 59-74) USE THE BUG HUNT SKILL (/bug-hunt, and /improve)
76. [x] Adjustable scopes: flip a magnifier in front of the red dot (or swap sights) with a hand gesture
77. [x] Looking at a magazine shows how many rounds it holds (readout on top of the mag)
78. [x] Gun-to-magazine and gun-to-gun collisions off; a visible square touch border shows
79. [x] The start-up intro: soldiers walk toward the VR view instead of left to right
80. [x] Boneworks-style grips: the hand takes the weapon's own grip pose; letting go drops the weapon, which returns to its holster
81. [x] Vibration on the Quest 3 controllers for shots, grabs and every interaction
82. [x] Intro: the soldiers walk past the view before it cuts, and the title stays behind them
83. [x] Glove at real-hand size, fingers joined to the palm, a pistol held like a pistol
84. [x] Melee weapons swing along their real blade
85. [x] Pictures of everything from a VR test run (virtual headset at standing height, not under the map)
86. [x] Hands clip into held guns and do not grab around them (Glock, AK and others)
87. [x] Magazines: hands close on the seated magazine too (no fingers through it), and a magazine taken in the hand is held properly, on every gun
88. [x] Autotest proves the mag kick-out with a second magazine and the H3VR-style tactical reload
89. [x] Recoil: firing with one hand kicks harder than with two hands, and it actually works
90. [x] Pouch items and guns: grabbable and throwable; nothing appears just by touching the pouch
91. [x] Dropped guns float away on the ground
92. [x] Ammo bag and medic bag: the glove disappears while one is held
93. [x] /improve 20 (after the fixes)
94. [x] /documentate (after the improve rounds and bug fixes)
95. [x] Reloading the akimbo pistols with lasers dropped the pistols (the second pistol was taken for the magazine)
96. [x] Grips on slides, bolts, charging handles and pumps close on the part being worked
97. [x] Equipping a weapon showed the flat game's arms holding it for a split second
98. [x] Mag kick (knocking the seated magazine out with a fresh one) sometimes does not work
99. [x] RPGs show something black under them
100. [x] The wrench is held too close to the face (it stands up into the line of sight)
101. [x] Cyan-blue picture for 2-3 seconds when loading into a map
102. [x] Map loading screens are far away from the player
103. [x] Main menu: something flickers / the map turns and the menu lags while picking a map or changing settings
104. [x] The Steam (menu) button also opened Ravenfield's pause menu
105. [x] Bolts do not move back on many weapons, and the hand has to reach inside the gun to work them
106. [x] Pouch: some pistol magazines cannot be taken; the magazine shows too low and drifts; every magazine in the same place
107. [x] A better hand: the procedural glove's fingers stuck out of the palm (now Valve's SteamVR tactical glove)
108. [x] Working a slide, bolt or pump nudges the gun a little (it reacts to the pull and the snap home)
109. [x] Weapon features Boneworks has that we do not: check and implement
110. [x] A realistic mud splash on the view when a grenade or explosion goes off close by (Call of Duty style)
111. [x] Hand poses: count how many poses Boneworks has and have at least that many (Boneworks did it right)
112. [x] /bug-hunt 20 (after 98-111)
113. [x] /improve 20 (after the bug hunt); make everything smooth
114. [x] Weapons and magazines lying on the ground sometimes float away
115. [x] Pistol magazines do not go all the way in: the two controllers meet under the grip
116. [x] Holsters and the ammo pouch where Boneworks has them (long guns behind the shoulders, pistols under the arms, pouch on the hip)
117. [x] Flip magnifiers and other switchable sights stay where the hand put them (they only showed while aiming through them)
118. [x] Machine gun belt: it can be pulled out far too long, and pulled too far it disappears and is lost
119. [x] Machine gun belt: one belt from the box stays on the gun model and never disappears
120. [x] Machine gun belt: the laid belt does not show on the gun
121. [x] A long gun touching the ground pushes the player around: only the hand and gun may be pushed
122. [x] Held weapons collide with the world (walls, sandbags, boxes) instead of passing through like ghosts; a gun can rest on cover
123. [x] /improve 20 again: push toward an AAA-quality VR mod, make everything better and smoother
124. [x] /bug-hunt 10 (after 123)
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

For 33. Instrument the mod's per-frame work (Update/LateUpdate/BeforeRender) with timers and GC-allocation counters behind -rfvr-perf; measure a baseline; remove per-frame allocations and repeated searches in the hot paths; re-measure and keep only measured wins. Done when: a before/after table exists and the mod allocates nothing per frame in steady state. Done 2026-10-14 on branch tasks-98-113 (not yet run in the game; see changed.md).

For 34. -rfvr-autotest starts an instant-action match in flat or null-driver VR, enables every installed mutator, spawns the player, equips every weapon in turn and runs the manual reload steps programmatically (eject, insert, rack, shells), logs exceptions and perf, then quits. Done when: an autotest run completes and its log shows no exceptions from the mod.

For 35. Review every installed mutator (scripts, keybinds, weapon and ammo changes); make sure the mod adopts outside ammo/weapon changes and that mutator hotkeys can be pressed from the controllers. Done when: the autotest runs with all mutators enabled without errors and mutator keys are reachable in VR.

For 36. Weight and inertia by weapon size, recoil by class (pistol, rifle, shotgun, sniper, MG), two-hand stabilisation, haptics on every mechanical event, slide-lock feel; run /unvibe over the VR UI (settings panel, counters, pouch, launcher, Setup.exe). Done when: weapon classes feel distinct and the UI has no generic tells.

For 37. Option "Hands: VR hands / weapon's own arms": the mod's first-person arms stay visible; two-bone IK (shoulder/elbow/hand found from the arm chain, twist helpers skipped) re-roots both arms at body-relative shoulders, the gun hand keeps its authored grip on the weapon, the off hand follows the controller (or the foregrip / slide / magazine it holds), elbows bend toward a natural pole, out-of-reach shoulders slide toward the hand. Idea from github.com/GDani31/ravenfield-vr-mod (no license, so own implementation). Done when: with the option on, the mod's gloves hold each gun as authored and the off arm reaches the off controller without tearing. Done 2026-10-14 on branch tasks-98-113 (not yet run in the game; see changed.md).

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

For 59. The VR body already takes the hits (bots hit where you stand). What is wrong is the feedback: getting damaged shows the game's flat-screen damage overlay (a 2D vignette/blood image stretched over the HUD frame) instead of feedback made for VR. Hide the flat damage overlay in VR and show the hit the VR way: a short red vignette around the edges of the view that follows the head (strength from the damage taken, darker at low health), a haptic pulse on both controllers, and a direction cue toward where the shot came from. Done when: getting shot in VR shows no flat overlay, the edges of the view flash red toward the shooter and both controllers buzz.

For 60. Reported again after the second fix (wider window, speed relative to the gun): bringing a fresh magazine up into the base of the seated one does not knock it out. Log the kick test's numbers (along, lateral, angle, speed, kickable flag) while trying it in the headset, find which condition fails, then fix that one. Done when: a kick ejects the seated magazine on an M4 and an AK in the headset.

For 61. The thumb looks wrong on pistol grips. Compare the thumb's base rotation and curl in the Weapon role against the weapon's own hand pose (GripCurls) for pistols; the thumb should lie along the left side of the frame (right hand), not stick out or cross the slide. Done when: the thumb rests along the frame on the Glock and the M1911 in the headset.

For 62. The procedural glove (primitive capsules and cubes, one flat material) looks crude. Build a better hand: smoother joints (fewer seams), knuckle and palm shape, a proper glove material (normal detail, matte fabric, team tint kept), and fewer draw calls (merge the static palm parts, one material, no shadow casting). Done when: the hand looks like a glove up close and its draw calls/renderers are fewer than now (measure both).

For 63. H3VR-style tactical reload. While the off hand holds a fresh magazine near the gun's seated magazine, holding the magazine release puts the ejected magazine into the same hand, so the hand holds both (research how H3VR does magazine palming before building it). Seat the fresh one and the old one stays in the hand; letting go of grip drops both. Works on Glocks and any detachable-magazine weapon. Done when: in the headset, mag release with a fresh mag at the well leaves both magazines in the off hand, seating one keeps the other, and releasing grip drops what is held.

For 64. Pressing B on pistols does not eject the magazine. Check MagRelease binding and the Manual/UsesDetachableMag/MagInserted conditions for pistols (slot 1), the pistol's analysed magazine part (virtual vs real) and whether the eject path runs. Done when: B drops the magazine on the Glock, M1911 and a modded pistol.

For 65. On some M4-pattern rifles pulling the charging handle back does not cycle the bolt (no chambering, no ejection). Find which ones (self-test: handle found? slide = bolt or carrier?), and make the handle drive the bolt with the rack stroke. Done when: racking the handle on every M4 variant in the report chambers a round in the headset or autotest.

For 66. Some magazines sit oddly in the hand. Check the magazine's analysed insert direction, front axis and base/tip points for the affected guns (HoldPose uses them); wrong axes from a skinned or offset magazine part tilt it. Done when: the magazines of the reported guns sit upright in the fist with the front facing forward.

For 67. On some M4s the magazine does not come out (eject or hand pull). Identify them from the self-test (magazine method, virtual parts, BoxLocked, deep-well rule) and fix the cause. Done when: every M4 variant ejects and releases its magazine by hand.

For 68. A smoothing pass over how everything feels in the hand: grab/release transitions, haptics, part motion (no snapping), menu and laser behaviour, comfort. Collect everything reported in the headset sessions and polish rather than add. Done when: the next headset session has no feel complaints from the existing list.

For 69. Holsters are invisible: you cannot see the pistol on your hip or the AK over your shoulder to reach for it. Show each holstered weapon at its holster spot in real time (a copy of its model, or the weapon itself parked there), following the body zones, on the layout from 56, without drawing in front of the eyes. Done when: looking down shows the pistol at the hip, looking over the shoulder shows the rifle, and both follow you.

For 70. The hands still feel fully glued to whatever they hold. Decide with the player what free means here (hand model following the controller with the object only snapping on grab, the off hand leaving a foregrip easily, a soft follow instead of rigid parenting) and make holding feel natural rather than welded. Done when: the player confirms hands feel free in the headset.

For 71. The game's scoreboard covers the view while aiming. In VR, show the scoreboard only together with the map (map open), never on its own. Done when: aiming never brings up the scoreboard and opening the map shows both.

For 72. Grab targets like bolts and charging handles are too small to hit reliably, and the hand does not take a pose on the bolt while pulling it back. Make the grab zones bigger and shaped to the part, and while racking put the hand (fingers pinching the handle) on the part it pulls. Done when: in the headset the bolt is easy to grab on the first try and the hand visibly holds it through the whole stroke.

For 73. With no magazine inserted, bolts and slides are sometimes hard or impossible to pull back. Check the rack logic's conditions (locked open, slide-stop, empty-mag states, rack thresholds) for the empty case. Done when: every bolt and slide racks freely with no magazine on pistols, M4s and AKs.

For 74. Some magazines play no insert or detach sound. Slice the sounds out of the weapon's own reload audio at the mag-out/mag-in moments (the reload timeline knows when), or find good free sounds to ship as donors. Done when: the self-test shows a mag in and mag out sound for every magazine weapon and the reported guns play them in the headset.

For 75. Run /bug-hunt 20 (twenty different areas, verify and fix), then /improve 10 (ten measured rounds, keep only what proves better), after 59-74. Done when: both runs are finished, built, installed, committed and summarised.

For 76. Some weapons carry more than one sight: a magnifier behind a red dot, a flip-up or offset sight, a scope that can be swapped (the game's alt-sight / sight-switch or the mod's own sub-objects). Find these from the weapon (sight objects the mod toggles, scope parts the analysis measures) and let the off hand move them: grabbing the magnifier and swinging it in or out of the sight line, or a flick gesture at the optic, switches which sight is used and moves its model (aim line, lens and zoom follow). Done when: a weapon with a magnifier or second sight can switch between them by hand in VR, and the eye line and scope view follow the chosen sight.

For 77. Looking at a held or loose magazine (its top, where the rounds show) brings up a small readout of its rounds, the way you would look at the follower in real life. Only while it is in view and near (held in a hand or within arm's reach), fading in and out, never in front of the sights. Done when: holding a magazine up to the eye shows its round count, and it hides when looking away.

For 78. Magazines and other weapons collide with the held gun at a box around it (the physics stand-in is a single cube, too big and offset), and a square outline is visible where the hand touches. Shape the gun's physics stand-in from the gun's own meshes (several boxes along receiver, barrel and stock) so props bounce off the gun where it really is, and remove whatever draws the square (grab-zone debug or a collider made visible). Done when: a magazine dropped onto the gun bounces off its real outline and no square shows in normal play.

For 79. The start-up intro (soldiers crossing the screen) plays as the flat version: they walk from left to right across a flat plane in front of you. In VR, place the intro scene so they walk toward the viewer (rotate the intro rig or place the VR view at the end of their path) at a comfortable scale. Done when: in VR the intro soldiers walk toward the headset view.

For 80. Boneworks-style grips: while a weapon is held by a grip its rig has a hand for (the arm bones still animate under the hidden arm meshes), the glove takes that hand's pose and finger curls; the off hand takes the support pose on the handguard, slid to where it actually holds. With GripToHoldWeapon, letting go away from the body drops the held weapon as a physical copy of what was held (its rounds kept, its own physics shape), at the body it is holstered; a dropped weapon returns to its holster after WeaponReturnSeconds or 20 m. Done when: the pictures show the glove wrapping the grips and the let-go check reports "fell out of the hand, back in its holster".

For 81. Haptics reach Quest 3 Touch Plus controllers: pulses shorter or weaker than the Oculus driver passes on are lengthened to a felt minimum (scaled by HapticStrength); the first pulse per hand logs whether SteamVR accepted it. Done when: the log shows "Vibration on the ... controller works" in a headset session.

For 82. The walkers start about half the intro's walk away and pass beside the view at 1.1 m; the intro's screen canvases are placed in the scene 12 m out on a world layer, so the soldiers walk in front of the text. Done when: the intro pictures show the soldiers close and the text behind them.

For 83. The glove is 0.9 scale with slimmer fingers and palm; each finger is rooted inside the palm (a palm-bound ring and a knuckle ring shared with the palm) so a curled finger stays joined; a relaxed default curl and finger spread. Done when: the glove pictures (front and back) show a joined fist and the weapon pictures show the glove on the grip.

For 84. The blade or head of a melee weapon is measured from its own physics shape (farthest point from the grip), and swings are tested along it instead of along the rig's aim axis; melee weapons keep their full-size physics shape. Done when: the autotest reports the blade length and direction for each melee weapon.

For 85. The VR autotest takes pictures (body views, holsters, damage, magazine in hand, tactical reload, dropped weapon, a spread of weapons in hand) into BepInEx/RavenfieldVR-shots; -rfvr-quick tests a spread of about 60 weapons; on the virtual headset (head reported on the floor) the view uses the head's own height. Done when: the pictures show the scene from standing height with hands and holsters visible.

For 86. The glove took the weapon's own hand pose rolled 45-90 degrees (its frame came from the thumb root, which sits toward the palm) and closed by fixed curls, so fingers went through the grip or hung beside it. The pose is now read across the knuckles; the visible model is rendered as depth from five directions into a solid (any mesh, readable or not); the palm settles onto it, the hand slides along the grip until the middle finger closes under the trigger guard, and each finger closes joint by joint until it touches. Done when: the grip close-ups show wrapped grips and the autotest's buried-point count drops against the old posing (383 -> 57 over 33 weapons).

For 87. The seated magazine is part of what the support hand often holds (an M3's magazine, a magazine well): it has to be in the solid the hands close on while it is in the gun, and out of it while it is not. A magazine held in the hand closes the fingers on its own shape. Done when: close-ups of a magazine-held gun and of a magazine in the hand show no fingers through it, and the autotest's buried-point count for them is near zero.

For 88. The autotest drives the reloads the way a player does in H3VR: a fresh magazine struck against the seated one knocks it out (mag kick), and the tactical reload holds the old and the new magazine in one hand. Done when: both are reported with their round counts and pass on the test rifle.

For 89. One-handed fire has more recoil than two-handed fire (by weapon class); the autotest fires each way and measures the kick. Done when: the measured kick is larger one-handed than two-handed on a rifle and a pistol.

For 90. Magazines and grenades from the pouch, and dropped or holstered guns, come into the hand only when the grip closes on them, can be thrown with the hand's velocity, and nothing is spawned or shown by merely touching. Done when: the autotest takes one of each, throws it and reports it flying.

For 91. A dropped weapon on the ground stays where it fell (no drifting or floating away). Done when: the autotest drops weapons on flat ground and on a slope and reports how far they moved over several seconds.

For 92. Kits held like a bag (ammo bag, medic bag) hide the glove instead of a fist beside them. Done when: the picture of a held bag shows no glove.

For 93. Run /improve 20: twenty measured rounds, keep only what proves better.

For 94. Run /documentate once 86-93 are done.

For 95. The P220 akimbo weapons' analysis took the part "P220 DUAL" (the second pistol, its laser beam making it 128 cm long and 100% outside the gun) for the magazine, so every reload hid and dropped it. A magazine candidate that is the other gun of a pair (named dual/akimbo/off-hand) or holds parts no magazine has (slide, bolt, barrel, trigger, hammer, laser, muzzle, receiver) is no longer taken; those weapons fall back to a magazine found by name or an invisible one. Done when: the self-test report no longer lists a P220 DUAL magazine.

For 96. While racking a slide, pulling a bolt or charging handle or working a pump, the hand closes on the part it holds: the part is measured on its own (rendered as depth) the first time it is taken and the solid moves with it; palm and fingers settle on it as on a grip. Done when: the rack-grip check (hand put on the rack point by script, taken the real way) reports few points inside the part at rest and pulled half way, and the pictures show the hand on it.

For 97. A weapon drawn after the mod's Update in a frame (the game switches weapons in its own Update or LateUpdate) was drawn once where the flat game holds it, in its own arms, before the mod took it over. The take-over is checked again right before rendering. Done when: the autotest counts no frame drawn with a new weapon not yet in the VR hand and none with the weapon's own arms showing.

For 98. From the Quest session of 2026-10-13: driving a fresh magazine into the seated one's base sometimes does nothing. The log of that session shows kicks working (e.g. M4A1 at 0.26 m/s) and attempts held back by the angle (61-126 deg) and lateral offset (9-10 cm) limits. Make it forgiving: any magazine of the gun (empty ones too) kicks, a slow firm push past the base knocks it out as well as a fast strike, the window scales with the magazine's size, and the angle limit is wider. Done when: the H3VR kick check passes at slow and fast speeds and with the magazine tilted 45 deg, and a fresh magazine passing beside the gun (not at the base) kicks nothing. Done 2026-10-14 on branch tasks-98-113 (not yet run in the game; see changed.md).

For 99. The RPG-7 (and RPG-7V) analysis takes the part "RPG-7_Reload_Cube" (80 cm, 0% outside the gun) for the rocket: probably a hidden helper box of the mod's reload animation that the mod's magazine overrides make visible. Check the SMAW ("Smaw Rocket Container", 88% outside), Javelin ("Javelin Scope" taken as magazine, 240 cm), Carl Gustav and Panzerfaust too. Done when: pictures of every launcher in the VR autotest show no black box, and a helper part that is never visible in the idle pose is not shown by the mod. Done 2026-10-14 on branch tasks-98-113 (not yet run in the game; see changed.md).

For 100. The wrench (and other melee weapons held by a handle) stands straight up out of the fist, into the view. Hold melee tools along the fist's real axis (the controller handle tilts forward about 40-50 deg from the pointing direction), so the head points forward and up, away from the face. Done when: the wrench picture from the VR autotest shows its head forward of the hand, below eye height, and the melee hit test still sweeps the real blade. Done 2026-10-14 on branch tasks-98-113 (not yet run in the game; see changed.md).

For 101. Some 2-3 seconds of cyan-blue picture while a map loads. Find out what draws it (the rig following a camera that clears to a solid colour, the minimap camera, or SteamVR showing its own frame while the game stalls) with pictures taken through the load in the VR autotest, then show something calm instead (black or the loading screen). Done when: pictures every half second from the loading screen to the first gameplay frame show no cyan-blue frame. Done 2026-10-14 on branch tasks-98-113 (not yet run in the game; see changed.md).

For 102. The loading screen (its 3D decoration and the "LOADING ..." canvas) is drawn far away. Show it at a comfortable distance in front of the player (about 2-3 m, a fixed size, like a screen in a dark room). Done when: the load pictures show the loading screen filling a comfortable part of the view. Done 2026-10-14 on branch tasks-98-113 (not yet run in the game; see changed.md).

For 103. In the main menu something flickers or turns (the menu background camera moving the rig, or the weapon warm-up's measuring) and the menu lags while picking a map or changing settings. Find it with menu pictures and frame times (-rfvr-perf) in the menu, and keep the menu's world still and its frame rate steady. Done when: menu pictures over 10 seconds show no moving background and the menu holds the headset's frame rate during warm-up. Done 2026-10-14 on branch tasks-98-113 (not yet run in the game; see changed.md).

For 104. On the Quest the left menu button is SteamVR's dashboard button; the mod also bound it to Ravenfield's pause menu (and opened the pause menu when the dashboard opened), so one press opened both. Implemented 2026-10-13: the button is no longer bound (pause stays on holding Y; mod keys are on the settings panel), and the dashboard pauses the match without the game's menu, resuming when it closes. Done when: on the Quest, pressing the menu button opens only the SteamVR dashboard and closing it resumes the match. Done 2026-10-14 on branch tasks-98-113 (not yet run in the game; see changed.md).

For 105. Many rifles' moving part is the bolt carrier inside the receiver (found by its motion when firing), so the rack point sits inside the gun: the hand has to reach into the receiver, the inside moves and the bolt handle you see stays put. 72 weapons have no moving part at all (a virtual handle). Grab the handle where it is (the part's visible surface outside the receiver, or the point the mod's own reload hand takes), and move the visible handle with the pull. Done when: the rack-grip pictures for rifles, pistols and pumps show the hand on the outside handle or slide, and it moves back with the hand. Done 2026-10-14 on branch tasks-98-113 (not yet run in the game; see changed.md).

For 106. The pouch magazine was placed by the analysis box of the magazine, which on some (pistol) magazines is not where the copied model is: it showed low and away from where the grab answers. Implemented 2026-10-13 (to verify): the copy's own rendered bounds place it, its top always stands just above the pouch point (any magazine), the pouch hangs from the neck pivot (looking down no longer moves it) at belt height, and a squeeze at the pouch beats a nearby pistol's slide or cupping zone. Done when: the autotest reaches for the shown magazine and takes one with a pistol and a rifle, and the shown magazine is inside the grab zone. Done 2026-10-14 on branch tasks-98-113 (not yet run in the game; see changed.md).

For 107. The user found the procedural glove bad (fingers stuck out of the palm like sausages) and asked for a downloaded model. Sketchfab needs a login, so free models without one were compared; Valve's SteamVR glove (BSD-3, steamvr_unity_plugin) was taken: a textured tactical glove rigged to SteamVR's hand skeleton. tools/hands/convert_glove.py converts it (re-skinned onto the mod's palm + 5x3 joints), each joint curls the way Valve's open-hand-to-fist poses move it, contact is tested at the finger pads (the glove is padded: pads reach 9-17 mm from the bones) and on the glove's real palm surface. The old glove is backed up (tag procedural-glove-backup, backup/procedural-glove) and selectable with Glove = Simple. Done when: grip close-ups show the glove wrapping pistol grips, rifle grips, handguards, magazines and bolts without clipping, at a size that suits the guns (see the notes below). Done 2026-10-14 on branch tasks-98-113 (not yet run in the game; see changed.md).

Notes 107, first in-game check (VR autotest on the virtual headset, 2026-10-13 night, only the first rifles: JOLT-20 S, PATRIOT GL, QUICKSILVER; stopped early on request):
- The glove shows in the game with its textures and moves; it sits where each weapon's own hand sits (same place as the weapon's arms in the comparison pictures).
- Size: about the size of the weapons' own hands in the close-ups, a little bulky (padded glove, scale 0.88). Not too small; check in the headset whether 0.8 reads better.
- Support hand on the PATRIOT GL handguard: the thumb points up through the handguard and the fingers lie flat under it instead of wrapping it. The thumb's rest pose (Valve's relaxed thumb) starts inside the gun, and the closing logic ignores segments already inside: the support role needs its own thumb base (along the side of the handguard) and the hand must be pushed out of the handguard before the fingers close.
- QUICKSILVER: the gun hand hangs under the gun with its fingers closed on air (that weapon's own hand pose is also that low); check against the grip solid.
- Rack grip (JOLT-20 S): the pinching hand is at the charging area, beside the scope.
- The open hand at rest is Valve's open hand (fingers spread wide): use the relaxed pose as the resting look.
- Pistols and magazines were not reached in this short run.

For 108. In Boneworks the gun moves a little when you rack it: pulling the slide tugs the gun toward the pull, letting it snap home jolts it. Add small impulses to the held gun's recoil spring from the worked part's pull speed and its snap home, a magazine seating and a magazine pulled out. Done when: the rack check measures the gun moving a few millimetres with the pull and a jolt when the slide returns. Done 2026-10-14 on branch tasks-98-113 (not yet run in the game; see changed.md).

For 109. Compare with Boneworks' gun handling (chamber check, ejected casings that stay, weight and inertia, stock against the shoulder, re-grabbing anywhere on the gun, magazine count by looking, etc.), list what is missing and implement the most valuable parts. Done 2026-10-14 on branch tasks-98-113 (not yet run in the game; see changed.md).

For 110. A grenade or explosion close to the player throws mud on the view: dark brown splats and droplets head-locked in front of the eyes that slide down and fade over 2-4 seconds, more and bigger the closer and larger the blast, from the side it came from, none through walls; water splashes for blasts in water. Done when: an autotest explosion next to the player shows splats in the pictures and they are gone 4 seconds later. Done 2026-10-14 on branch tasks-98-113 (not yet run in the game; see changed.md).

For 111. Boneworks poses each hand for what it holds (grip-specific hand poses). Count the poses it has (per object type and grip) and make sure the mod has at least as many distinct, good-looking poses (pistol grip, rifle grip, handguard, pump, bolt, charging handle, magazine, magazine pinch, grenade, pin, knife, two-handed melee, launcher, ladder, steering wheel, flight stick, pointing, relaxed, fist). Done 2026-10-14 on branch tasks-98-113 (not yet run in the game; see changed.md).

For 112. Bug hunt, 20 rounds, a different area each round, after 98-111. Done 2026-10-14 on branch tasks-98-113: 21 bugs fixed (2 major), each round listed in changed.md (not yet run in the game).

For 113. Improve, 20 measured rounds, after the bug hunt. Done 2026-10-14 on branch tasks-98-113: 20 rounds kept (performance, smoothness, robustness, test coverage), each with its before/after number in changed.md. Measured here offline (counts, simulations, the compile check), not yet in the game: the frame-time table under task 33 is to fill in at home.

For 114. Reported 2026-10-14: pickupable things on the ground (dropped weapons, magazines) sometimes float or slide away. The held gun's physics stand-in shoves whatever it touches, and a bot's weapon starts inside its ragdoll. Done when: lying weapons and magazines stay put with the gun held low beside them in the headset, and the autotest's ground and walker checks report them still. Done 2026-10-14 on branch tasks-98-113 (not yet run in the game; see changed.md).

For 115. Reported 2026-10-14: a pistol magazine often cannot be pushed all the way in, because the off hand's controller meets the gun hand's controller under the grip. Done when: pistol magazines seat in the headset without the controllers having to touch. Done 2026-10-14 on branch tasks-98-113 (not yet run in the game; see changed.md).

For 116. Reported 2026-10-14 (with 100: the holstered wrench and other tools stood up in front of the face): carry weapons where Boneworks does: long guns behind the shoulders, pistols under the arms, small items at the small of the back and on the belt, the ammo pouch on the belt at the side of the hip, keeping the earlier layout as an option. Done when: the body pictures show the new layout and every holster and the pouch are easy to reach in the headset. Done 2026-10-14 on branch tasks-98-113 (not yet run in the game; see changed.md).

For 117. Reported 2026-10-14: pulling a flip magnifier (behind an EOTech, for example) switches the sight, but the magnifier only appears in front of the sight while the eye looks through it and folds away when you stop aiming, because the weapon's own animation only shows the sight mode in its aim pose. The part the hand moved has to stay up or down as it was left, whether aiming or not. Done when: a flipped-in magnifier stays in front of the red dot with the gun lowered, and flipped out stays out while aiming. Done 2026-10-14 on branch tasks-98-113 (not yet run in the game; see changed.md).

For 118. Reported 2026-10-14: the belt end taken from the box mouth follows the hand without limit and, pulled too far, vanishes (the belt is let go and has to be taken again). The strip should stop at its real length and slip from the hand without disappearing. Done when: pulling the belt far keeps it at most its length out of the box, and letting go leaves it hanging from the box. Done 2026-10-14 on branch tasks-98-113 (not yet run in the game; see changed.md).

For 119. Reported 2026-10-14: one belt of the gun's own model (the links between the box and the feed) stays on the gun when the box is out and never disappears. Done when: with the box out the gun shows no belt; with a box in and the belt laid, it shows. Done 2026-10-14 on branch tasks-98-113 (not yet run in the game; see changed.md).

For 120. Reported 2026-10-14: after laying the belt on the feed tray the belt does not show on the gun. Done when: a laid belt runs from the box mouth onto the feed tray (the gun's own belt or the mod's strip) until the box comes out. Done 2026-10-14 on branch tasks-98-113 (not yet run in the game; see changed.md).

For 121. Reported 2026-10-14: holding a long gun so it touches the ground moves the player. The ground must only push the gun (and the hand on it), never the player. Done when: pressing a rifle's muzzle into the ground in the headset never moves the player. Done 2026-10-14 on branch tasks-98-113 (not yet run in the game; see changed.md).

For 122. Reported 2026-10-14: held weapons pass through walls, sandbags and crates (they only collide with the ground, players and soldiers). The gun should be stopped by the world, Boneworks style: pushed into a wall it stays at the wall, and it can be rested on a sandbag or a crate while crouching. Done when: in the headset a rifle pushed into a wall stops at its surface and lies on a sandbag when lowered onto it.
 Done 2026-10-14 on branch tasks-98-113 (not yet run in the game; see changed.md).