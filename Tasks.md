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
19. [ ] Weapon analysis that holds for every modded rig
20. [ ] All-weapons self-test report (every installed weapon, including workshop mods)
21. [ ] Free hands: no snapping, live fingers, holster to empty hands
22. [ ] Magazine sliding (insert along the well, slide out on release) for mags, shells and rockets
23. [ ] Weapon sounds per action from the mod's own audio, no continuous reload audio
24. [ ] Enemy weapons drop on death and can be picked up (swap with your own)
25. [ ] Easy installer: one-click Setup.exe with install, update and uninstall
26. [ ] Private GitHub repo and release
27. [ ] Placeholder sweep over all new code
28. [ ] Bug hunt, 10 rounds
29. [ ] Rebuild, in-game verification (flat, self-test, virtual headset), docs


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

For 26. Create the private GitHub repo QWavey/ravenfield-vr, commit the project (dependencies and decompiled game code excluded), push, and attach Setup.exe and the zip to a release. Done when: the repo and release exist and are private.

For 27. Run the placeholder sweep over all source (stubs, fake/simulated values, truncated logic, settings that do nothing). Done when: no real placeholder remains.

For 28. Ten bug-hunt rounds over different areas, each verified and fixed. Done when: ten rounds are reported with their fixes.

For 29. Build, install, flat launch, self-test over all installed weapons, virtual-headset launch, README and memory updated. Done when: logs show no exceptions and the report is reviewed.

