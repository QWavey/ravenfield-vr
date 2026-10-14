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
125. [ ] /improve 20 on the hands: holding magazines (rifle, pistol), chambering rifles and pistols, hand poses and gestures on the weapons, up to Boneworks (rounds 1-6 done, 14 to go)
126. [ ] Seating a pistol magazine: keep the hand out of the grip for the whole way in, not only on the rail, and push the base plate home with a closed hand
127. [ ] Machine gun box magazine: the thumb lies inside the box when it is held
128. [ ] Shotgun shells: hold a shell between thumb and fingers to load it, not in the fist
129. [ ] Free-hand gestures: pointing, fist, open hand and thumbs-up with Valve's own pointing pose
130. [ ] Check the support hand on the handguard and the pistol cup in grip_check.py and fix what it shows
131. [ ] Gun-hand gestures: the thumb goes to the magazine release when a magazine is dropped, and to the slide or bolt release when the action is sent home
132. [ ] Optional: hook a bolt knob or charging handle with the fingers when the hand reaches over the gun (now always a thumb-and-index pinch)
133. [ ] /bug-hunt 10 over the hand work (125-132)
134. [ ] Test the hand work in the headset (checklist under "For 134")
135. [x] Spawning puts the primary in your hand: spawn with empty hands, weapons in their holsters (done 2026-10-14: SpawnEmptyHanded; autotest "Spawned with empty hands: yes")
136. [x] EOTech with a magnifier in front (BF HK mods, e.g. HK416-B): the sight view is black until the magnifier is flipped with the sight gesture; an EOTech alone must stay clear too (done 2026-10-14: the lens and the portal drawn on it are shown only while the sight line runs through the lens itself (its camera does not move with the magnifier); deep autotest: mode 0 lens hidden (3.3 cm off the line), mode 1 shown with the magnified picture; the user confirmed both pictures)
137. [ ] Some pistols (P320?) and snipers (MRAD?) have no recoil (code done 2026-10-14: kick from the round and a floor per kind of gun; a mod gun without muzzles (MRAD No script) now gets one; verify)
138. [ ] Grenades stick in the hand after a throw by letting go of the grip; a pulled pin was back after putting the grenade away (code done 2026-10-14: grip throw empties the hand, refused throw is a let-go, pulled pin stays pulled; pin/dud autotest passes; verify)
139. [ ] Racking: the bolt, charging handle or slide does not visibly move for many weapons: check every weapon one by one, with pictures (deep autotest 2026-10-14: the part under the hand moves on every weapon tested (0.0-1.3 cm); AR-pattern rifles get the charging handle at the back of the receiver; check the rest in the full run)
140. [x] MK18 RIII (mk18riii#mk18risiii): the magazine does not eject (the analysis took 'mag_001', the advanced reload's second magazine: "magazine sits far from the grip") (done 2026-10-14: the real magazine is picked (one thrown 1.6 m in the reload was skipped), the spare mag_001 is hidden)
141. [x] Loadout weapon-pick submenu (the list of every weapon for a slot): the panel turns 3D, then jumps flat a little to the left, an almost invisible grey box where the list should be (done 2026-10-14: the list is converted the frame it opens; pictures at 0.1, 0.5 and 1.5 s show it in place in 3D, no jump, no grey box)
142. [ ] BF pistols: check how each one is held (some look wrong in the hand)
143. [x] NVO-228E (#ACE 32): the magazine is not held in the hand (it hovers) and the mag kick never fires (pull axis (-0.31, 0.33, -0.89), kicks measured 14-15 cm off its axis) (done 2026-10-14: the floating magazine was the reload's spare (Mag.001, 62 cm out), hidden while held; axis from its vertices)
144. [ ] Mag kick: verify it on every kickable gun
145. [ ] Colt M1911 (and pistols in general): seating a magazine is hard, the controllers meet and the pistol is small; check every pistol (code done 2026-10-14: pistol rail taken more loosely (4.5 cm, 40 deg) and drawn home from a quarter in; verify)
146. [ ] Looking at the HP on the left wrist (and the scoreboard) makes the map flicker very fast (code done 2026-10-14: the wrist look had no hysteresis; verify)
147. [x] A magazine slides in in front of the grip instead of up into the magwell (which guns: check all) (done 2026-10-14: magazine axis from its vertices (raked pistol magazines), the seated magazine pinned to its seat; deep autotest 0.0 cm off the magwell on every pistol)
148. [ ] The warm-up analysed 0 weapons at start ("Pre-analysed 0 weapon rigs and 0 physics shapes in 0,0 s"): every first draw in a match hitches (code done 2026-10-14: the saved loadout is analysed first)
149. [ ] /bug-hunt 50 (raised from 40 on 2026-10-14; rounds 1-17 done: stand-in self-heal, explosion listener guard, ..., 17: a hand left marked as holding the weapon after the dominant side changed stayed busy for good, 25: firing (a bolt left half open with the chamber empty feeds when pushed home; destroyed scope cameras forgotten), 24: body zones after passing the weapon (the wrist look, display and night-vision gesture use the free hand; the stock pocket follows the weapon hand and the pouch the free hand; pouch and wrist display hidden in menus; the free hand reaches past the drawn weapon's empty holster), 23: the menu laser (Use held back while a panel is under the laser; only menus take the laser; a release by a pointer that went away or a second hand never clicks), 22: grenades (no slow throw at the feet, an in-car grenade can be put back, no second throw right after a trigger throw), 21: review of the session's changes (the double-press bindings broke the squad hold and slowed slide release: moved into code; the wrist gesture and move direction stay with the configured hands; the dry-fire click follows the gun hand; no exception per frame in the main menu; pins in after death; a larger ground-cast buffer), 20: vehicles and dropped weapons (own drops never deleted, hand motion forgotten at seat changes), 19: hand state (a magazine in the gun hand when the game draws a weapon is let go instead of floating; support roles without a weapon are freed), 18: per-frame exceptions at scene changes (a held magazine whose object went with the scene, grab-zone debug spheres, the input hook, the squad camera read, the loading check; plus frozen hands after quit to menu, 174))
150. [ ] /improve 20, toward AAA (set to 20 on 2026-10-14, done while the user plays; measured in the next test run)
151. [ ] Full VR test run of every weapon (autotest and self-test) once everything above is done
152. [ ] Weapons in either hand: pass the main weapon or a pistol to the other hand, and hold a second weapon in the free hand (draw, shoot and pick up the pistol while the main weapon is held) (step one code done 2026-10-14: the weapon goes into the hand that draws it from a holster or picks it up, and stays in the other hand when the gun hand lets go while that hand holds the handguard; the buttons follow the weapon (trigger fire/use, lower face button slide release/squad, upper magazine release/map trade places); a weapon taken with the free hand puts the drawn one in its holster; verify. Step two, a second weapon in the free hand at the same time: open)
153. [ ] Last, after the bug hunt and the improve rounds: more Boneworks gun logic (the user has Boneworks' code on disk: learn how its mechanics work from it, do not copy it)
154. [x] P220 GR (and maybe other pistols): the counter says RACK but the slide cannot be grabbed and racked; its magazine also seats a little in front of the magwell, floating, not up in the grip (see 147) (done 2026-10-14: a 6 m reticle quad on the RMR stretched the slide's bounds (rack point a metre off); spare P220 MAG.001 hidden; magwell 0.0 cm)
155. [ ] A magazine in the hand passes through the gun: it must collide with the gun and only go in through the magwell (code done 2026-10-14: a held magazine is drawn out of the gun's solid (its well stays open); verify)
156. [ ] On an empty gun the trigger releases the magazine (the same as the magazine release button) (code done 2026-10-14: EmptyTriggerDropsMag; verify)
157. [ ] AR-style rifles: racking moves only the bolt inside, the charging handle stays put (looks broken): the charging handle must come back with the hand and the bolt with it (part of 139) (code done 2026-10-14: see 139)
158. [x] MWII-G17: the magazine does not come out (release, tactical reload, pulling it); a held magazine floats in the air beside the hand instead of in it (NVO-228E too: check every gun) (done 2026-10-14: as 140 (the real mag was skipped for flying 1.6 m in the reload; the spare 58 cm out floated in the air))
159. [ ] Double-barrel shotguns: open the barrels by hand (they only open with the button) (code done 2026-10-14: break actions found by their barrels (the M1878 was taken for a pump with its trigger as the slide); barrels swing open and shut by hand; autotest break-open passes; verify by hand)
160. [ ] The game's own menu on A and X pressed together (code done 2026-10-14: double-press A or X opens the menu; first as SteamVR double clicks, which made the squad button lose its hold and every slide release wait; now detected in code with plain bindings; verify)
161. [ ] Squad orders (Go to and the other flags): an arrow on the hand pointing at the flag; the Go to message not in the middle of the view (code done 2026-10-14: the order is written on the pointing hand with an arrow to its target; the game's middle text is hidden; verify)
162. [ ] Squad orders with either hand, taken only while the hand points straight (the button still has to be pressed) (code done 2026-10-14: either hand held out straight points (autotest: straight counts, at the chest does not); verify)
163. [ ] MAJOR: a magazine (dropped at the feet) shoves the gun, moves the player and turns the view (the game's ground check took any light body under the feet for a moving platform; code done 2026-10-14: the ground cast ignores light loose bodies; verify)
164. [ ] A red square on the added charging handle (mwii M4): it took the material of the holo sight's 4 m reticle quad (code done 2026-10-14: the gun's own opaque metal; verify)
165. [ ] Magazines still pass through guns (the keep-out of 155 is not enough): fix and check on several guns
166. [x] The game's pause menu: only one button reacts (quit / shut down); Options and the others do nothing (done 2026-10-14: a click survives the trigger pull's wobble (4 degrees, 0.8 s), a drag needs 3 degrees; the Options panel lives on across scenes and was left where the main menu had put it (now kept converted); autotest clicks Resume (menu closes) and Options (panel 1.3 m in front, 4 deg off the view))
167. [ ] AR rifles empty: the charging handle shows pulled back (only the bolt locks back; the handle stays forward)
168. [ ] AR rifles: the charging handle's grab zone sits too far in / forward of the handle; check other guns too
169. [ ] The trigger on an empty gun must release the locked slide or bolt (send it forward), not drop the magazine: all guns (replaces 156)
170. [ ] (Good news from the headset: the M4A1 CCO's charging works; check the other M4s)
171. [x] The Options panel opened from the pause menu shows its tab buttons (Game, Input, Video) and Apply but an empty page: nothing to click (seen in the autotest picture, confirmed by the user in the headset) (found 2026-10-14: the Game page sits 150 units deep inside the panel, which never showed on the flat screen; in the world it stood 15 cm behind the panel's 93% dark background. Code done: depth inside a converted canvas is flattened; verify) (fixed 2026-10-14: its pages are switched off until opened, and a switched-off page reports itself as a root canvas: the scan converted each page as a panel of its own (scaled to 0.0008 and moved), so inside the open menu it showed at 0.0008 of its size; canvases inside another canvas are no longer converted. Also: depth inside menus kept flat every frame, stencil masks turned into rectangle clips. Autotest picture: the Game page with all its settings)
172. [x] Self-test: the left-handed pass left the left hand marked as holding the weapon, so on those weapons the rack, two-hand and magazine checks were skipped silently ("the off hand is busy") (done 2026-10-14: a hand marked as holding a weapon it does not hold is freed every frame)
173. [ ] Footsteps play the whole time while standing still (on one map; the user heard them constantly) (code done 2026-10-14: the room-scale step moved the body along the floor only, which broke the ground contact; the next physics step landed it again and every landing plays a footstep; the step now presses into the ground while on it; verify)
174. [ ] MAJOR: after "Quit to menu" the hands no longer move with the controllers (code done 2026-10-14: the game threw every frame reading the destroyed player's camera, the view mode stayed "on foot" and placing the play space threw before the hands were updated; the camera read is guarded, the hands update whatever happens to the play space; verify: quit to the menu and move the controllers)
175. [ ] Cars: the wheel can only be steered with one hand (keep that, but two hands on the wheel must work too); clicking with one hand exits the vehicle: exiting needs its own deliberate mechanism or button (code done 2026-10-14: the left trigger is Use, and Use gets you out: a fist closed on the wheel squeezed it. Now Use must be held a second in a seat, with ticks building up in the hand, and never counts from a hand on the wheel or stick; the wheel already took two hands; verify)
176. [ ] Grabbing weapons while in a vehicle and then leaving it drops the equipped weapons, and they disappear (code done 2026-10-14: a weapon let go of on foot drops as an object and its slot is emptied; the cleanup deleted it after 90 s or when it fell out of the world even when it was your own (only returned to the holster while on foot), and a release right after getting out threw it with the car's speed. Your own drop now always goes back to its holster, and the hands forget their motion at every seat change; verify)
177. [ ] Holding a weapon with both hands and letting go with the gun (trigger) hand puts the weapon away (into the inventory) although the other hand still holds it: it must stay in that hand (part of 152) (code done 2026-10-14: the weapon passes to the hand on the handguard; verify)
178. [ ] ARS Glock 19: the magazine reload does not work: the pouch hands out ONE round (as for a shell-fed gun) and the magazine does not come out (code done 2026-10-14: the reload moved the round on top of the magazine, not the magazine; the magazine holding a moved round is taken (also while an advanced reload is still classed shell-fed); deep autotest: magazine copy 17.6 cm, pulled straight down out of the grip on a 7.4 cm rail; verify; its animator ignores the slide-lock pose (another script drives it)) (log: the part taken for its magazine measures 3.3 cm and ejects sideways on a 3 cm rail: a round, not the magazine; needs a rig dump)
179. [ ] (code done 2026-10-14: a shell is now a real shell (red hull, brass head) or a live rifle round; a rocket is copied from the launcher's own rocket when one of a rocket's size was found; verify) Rocket launchers show a yellow placeholder instead of the rocket model (the round in the hand / pouch); shotguns sometimes too (the shell) (found: only magazines are copied from the weapon; a shell is always a brass capsule (the yellow thing), a rocket an olive one, so a launcher showing yellow was handled as shell-fed (see 180))
180. [ ] Rocket launchers cannot be reloaded at the moment
181. [ ] Shotguns: the hand on the pump slides much too far along it, and the pump (on the tube) sometimes does not move at all
182. [ ] MP9 PDW: pulling the bolt back (racking) does not work (code done 2026-10-14: its bolt (the part named "ejection window", 5 cm in every shot) was paired with a loose round the fire animation throws; a round is never a slide or bolt now; deep autotest: the bolt alone moves, 30 mm for a half pull; verify) (log: its "magazine" ejects straight back along the gun, 9 cm: the bolt or charging handle was taken for the magazine; needs a rig dump)
183. [ ] M733: pulling the bolt does not work, the bolt does not move, and a black square shows behind the bolt
184. [ ] QBZ 95-1 and all its variants: pulling the bolt does not work
185. [ ] Some pistols: inserting a magazine sends the locked-back slide forward by itself, but the counter still says RACK and the pistol must be racked to fire. The slide must stay back until it is released or racked (code done 2026-10-14: their animator shows the slide locked from "loaded ammo" = 0, and a fresh magazine made the game count rounds as loaded; held at 0 while the slide is locked; verify) (deep autotest: on pistols whose animator follows it the pose is held; the rack checks right after the lock check measured no slide travel, with or without the pose fix (-rfvr-noslidepose): to try in the headset: fire a pistol empty, put a magazine in, release the slide, then rack it)
186. [ ] CZ BREN 2 BR: racking works, but the grab zone sits inside the gun instead of on the charging handle / bolt (code done 2026-10-14: its only moving part is the bolt inside the receiver (no handle in the model); a rifle racked by such a bolt has its grab zone put on the receiver's surface over it: 4.4 cm out; verify)
187. [ ] BRN 180 (all variants): cannot be racked, or the grab zone cannot be found (deep autotest 2026-10-14: its rack moves the charging handle 52 mm with the hand; its magazine is the wrong copy: the reload animation hides the magazine by moving it 25 m away and shrinking it to 1%, which the analysis took for motion; the dropped copy came out tiny and 12 m off. The analysis must treat a part parked that far as hidden)
188. [ ] The game's start-up page ("loading mods") follows the view: it must stand still as a flat panel (code done 2026-10-14: before a match every screen is a panel in front of you; it was taken for HUD, having nothing to click; verify)
189. [ ] Hide the match bar (battalions/soldiers left and flags across the top) completely; it shows only in the wrist view (code done 2026-10-14: the game mode's canvas faded out with the mod's own canvas group; verify)
190. [ ] Wrist view: looking at the wrist must be held for 2 s, with a ring filling on the wrist, before the map, the scoreboard, the match bar and the wrist display show; all of it goes as soon as the wrist turns away; nothing of it otherwise (code done 2026-10-14; verify; the flicker of 146 should be gone with it)
191. [ ] Haiku scouts (read-only) review every weapon one by one from a fresh full self-test report and report what looks wrong
192. [ ] (next play session, while the user plays) Improve the machine-gun reload experience and the shotgun experience overall
193. [ ] (next play session) Fix reload issues on all guns
194. [ ] (next play session) Fix visual and hand placement issues; verify every magazine to the centimetre and millimetre
195. [ ] (next play session) Fix general issues; a new bug hunt until 20 bugs are found
196. [ ] Weapon review (8 Haiku scouts, all 308 weapons one by one, 2026-10-14): wrong parts picked by the analysis:
     - MP9 family (7, incl. MP9 PDW = 182): the bolt is the loose round '单独子弹' and the handle the ejection window '抛壳窗'
     - ARS Glock 19 (= 178): the magazine is the bone [Bullet]; the real magazine is another part
     - mw-m870: the pump is 'receiver'; 76 EAGLE: the shell's pull points into the gun; .357 CONDOR: the "magazine" is a 4 cm casing
     - SUPERNOVA (L85): the bolt is 'EjectionPort'; mrad / mwii-mrad: the bolt handle is a Romeo-sight mesh (15 cm); f2000 exps3: the handle is a '_base_' mesh; P320/P320s: the slide is '...receiver...' (check)
     - FA1911 10R / FAQ1911: the slide is 'P220 SHELL' with handle 'TT33 TT', 8.1 cm; FA1911 20R: magazine 36 cm; MK46 ET: magazine 65 cm; G BRN180 EO SS (3): magazine 50 cm, 85 deg off its box
     - HK416-A/A-S/B/B-S: the magazine is the controller 'HK416弹匣控制器', mag-in step missing
197. [ ] Weapon review: sub-weapons on the parent's model have a static rig of their own (virtual magazine and bolt, estimated hands): CZ Bren 2 BR "Bren Semi" (6 weapons, likely 186's grab zone inside the gun in semi mode), PATRIOT GL launcher, JOLT SecretSubWeapon, THUMPER smoke, Helo Signal Smoke (All): use the parent's rig for the shared parts
198. [ ] Weapon review: magazine axis from the shape far off the animated pull on whole families (1911 ~80 deg, P220 78, asval 92, ak12 57, mwii-ak105 78, QJB201 MG 97, HK416-A/B 64, NVO 53): check which is right per family (part of 194, magazines to the millimetre)
199. [ ] Weapon review (minor): reload timelines without mag-in or slide-home on several families (sound timing), FNX-45 family sights only estimated, launchers AT-1K (264 cm) and Panzerfaust 3 (135 cm) rockets too long for a copy (made rocket used)
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

For 125. Improve, 20 measured rounds on the hands: holding magazines (rifle, pistol), chambering rifles and pistols, hand poses and gestures on the weapons, up to Boneworks. Measured offline with tools/hands/grip_check.py (the glove's real joints against boxes and cylinders shaped like the parts; numbers and pictures). Rounds 1-6 done 2026-10-14 on branch tasks-98-113 (each in changed.md under "Third pass"; not yet run in the game):
1. magazines rest on the palm instead of half inside it;
2. no finger or thumb through the rifle or AK magazine;
3. the pinky curls in line instead of sticking out;
4. a pistol magazine is held index-point (index along its front, not the full fist), and a magazine being seated slides through the hand instead of taking it into the gun;
5. racking a pistol, the palm goes on the slide (over the top or on its side) instead of floating above a pinch;
6. a racking hand closes on the gun around the part too (receiver, stock, frame, gun hand), not only on the moving part.
Still to do: 14 more rounds. Use 126-132 first, then whatever grip_check.py shows next. Every round needs a before/after number, a log entry in changed.md, the compile check and a commit. When all 20 are done, add a roll-up to changed.md and tick this task.

For 126. Found in hands round 7 (started and reverted on 2026-10-14, not committed). Two problems. First, a pistol's rail covers only the last 45% of a magazine: 5.4 of 12 cm, from RailLength = extent x 0.9. A flush pistol magazine is therefore about 3.6 cm inside the grip before it lines up. The glove keep-out from round 4 (ReloadInteractions.KeepOutOfGun) only runs while the magazine is on the rail, so the index-point hand is already in the grip before it starts. Second, HandModel.ClearanceAlong returns 0 when no 5 mm step up to its limit is clear, so fully seated the glove snapped back into the grip (36 points inside it in the model). And once the glove has slid past the base plate, the index still points at the plate and the finger solver stops it there, so it pokes the plate instead of the hand pushing it. To do:
- run the keep-out while the magazine is lined up under the well (within about 6 cm sideways and 40 degrees), not only on the rail;
- when nothing is clear, keep the largest clear shift (or the last frame's) instead of 0;
- past the base plate (ReloadInteractions can tell from the base plate's height over the grip point), let the index close without solving it against the magazine.
grip_check.py's seating check has to model the whole way in: the gun's opening at the seated magazine's base for a flush pistol, the line-up 3 cm before the rail, and the pose and keep-out settling on each other over a few frames. The reverted draft did this. Done when: the seating check shows no glove points in a flush pistol grip at every depth, and the hand ends closed under the base plate.

For 127. Open since the hands baseline: holding a machine gun's box (10 x 8 x 12 cm in grip_check.py), 10 of 72 hand points are inside it, mostly the thumb, because the box is wider than the hand can grasp. Options: hold a box by its side with the thumb on top, the way a box is carried, or open the thumb out over it. Done when: grip_check.py's "MG box" shows 0 buried with the thumb touching.

For 128. Shells (PropKind.Shell) are held at their middle in the Shell pose, which closes the whole fist on them. Loading a tube, you pinch the shell between the thumb and the index and middle fingers at its rim end. Add a shell scenario to grip_check.py (a cylinder of about 2 x 7 cm), find a pinch placement the way round 4 found the pistol magazine's, and use it in MagazineProp.HoldPose for shells. Check rockets the same way. Done when: the shell is held by three fingers and nothing is buried.

For 129. The free hand on a controller without finger tracking: grip alone points, both grip and trigger make a fist (HandModel.TargetCurls). glove.bin has Valve's own "point" pose for each joint, unused so far; HandPoseId.LaserPoint uses plain curls. Use Valve's point pose for pointing and for the laser pointer, and check fist, open hand and thumbs-up (thumb up when the thumb is off the touchpad or stick) in pictures. Done when: grip_check.py pictures of each gesture look right and the thumb no longer sticks out when pointing.

For 130. The support hand on a rifle's handguard (the fist hollow on the handguard's centre line, HandguardPoint) and the pistol cup (closing on the gun and the gun hand's skin) have not been measured in grip_check.py. Add a handguard cylinder and, for the cup, a pistol grip with the gun hand's fingers as capsules. Measure palm gap, buried points and fingers touching, then fix what is off. Done when: both scenarios show nothing buried and the palm on the handguard.

For 131. Boneworks-style touches on the gun hand. The thumb moves to the magazine release for a moment when the magazine is dropped, and the support thumb (or the gun hand's thumb on a pistol) moves to the slide or bolt release when the action is sent home with the button. These are short pose blends on the existing HandModel goals. Done when: the self-test pictures show the thumb on the release during the drop and the release.

For 132. Optional. Racking a rifle's bolt knob or charging handle always pinches it between thumb and index (UpdateGraspAnchor). Reaching over the receiver, the fingers would hook it more naturally. A first try (2026-10-14) put the hook point on the knob and pulled the palm 4 cm into the receiver, so this needs the gun-aware solid from round 6 and a better anchor. Only do it if the pinch looks wrong in the headset.

For 133. Bug hunt, 10 rounds, a different area each round, over the hand work (125-132): HandModel (keep-out, pistol magazine index, palm grips), MagazineProp.HoldPose and PalmedPose (left hand, palming two pistol magazines), ReloadInteractions (grip choice when racking starts, PalmOnPart, KeepOutOfGun), HeldWeapon.RackSolid (the part's rest volume cut out, solid version per 2 mm), and grip_check.py itself. Same rules as 112 and 124: verify each catch, fix it, log it in changed.md.

For 134. In the headset, with the hand work (125-132):
- pistol magazine from the pouch: index along its front, held diagonally, not in the fist; left hand too;
- seating a pistol magazine and an M4 magazine: the hand stops at the grip or magwell, and the magazine slides on through the fingers;
- racking a pistol over the top, from the side and slingshot: palm on the slide for the first two, pinch for the slingshot; no fingers in the frame;
- racking an AK knob and an AR charging handle: no fingers in the receiver;
- rifle and AK magazines: no finger through them, pinky curled;
- palming two pistol magazines (H3VR style) still works and the second one sits beside the first.
Note anything that looks wrong, with the weapon's name.
