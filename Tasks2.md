# Tasks 2

Started 2026-10-15. Tasks.md is left as it is. This file has:
- a check of last night's work (the commits b185028..077d751 of 2026-10-14) against what Tasks.md says;
- today's new tasks, numbered from 200 so they never clash with Tasks.md.

## Status check: last night's work against Tasks.md

### Tasks.md not updated (the commits did more than the file says)

| Task | Tasks.md says | The commits say | Left to do |
|---|---|---|---|
| 156 (empty-gun trigger drops the magazine) | open, "code done" | replaced by 169 in e50f4f6: the empty-gun trigger now sends a locked slide or bolt home and no longer drops the magazine | close as replaced by 169 |
| 167 (AR handle shows pulled back when empty) | open, no note | code done in e50f4f6: the charging handle stays forward unless racked | verify in the headset |
| 168 (AR handle grab zone too far in) | open, no note | code done in e50f4f6: the grab zone sits at the back of the receiver (2.5 cm behind the rack point for a cut-out handle) | verify in the headset |
| 169 (empty-gun trigger sends the slide home) | open, no note | code done in e50f4f6 | verify on all guns |
| 183 (M733 bolt does not move, black square) | open, no note | code done in 50befcf: its charging handle (mk18_拉机柄) is found by name, the real handle moves, and there is no cut-out stand-in (no dark square). Racking passes the virtual-headset autotest | verify in the headset |
| 184 (QBZ 95-1 racking) | open, no note | racking passes the virtual-headset autotest (50befcf) | verify the variants in the headset |
| 191 (weapon review by scouts) | open | done: the review ran (8 scouts, all 308 weapons) and produced 196-199 | tick |
| 196 (weapon review findings) | open | the MP9 family's bolt (= 182) and the ARS Glock 19's magazine (= 178) were fixed in 7523ec1 | the other lines are still open |
| 149 (bug hunt 50) | open, rounds listed up to 25 | rounds 1-6 (d20f656), 18-19 (9dfaf9d), 20-25 (50befcf) | 25 of 50 rounds left |
| 150 (improve 20) | open | no improve round in any commit | not started |
| 187 (BRN 180) | open | racking passes the autotest (50befcf: the handle moves 52 mm with the hand) | the magazine copy is still wrong (the reload hides it by moving it 25 m away); no fix in the commits |
| 165 (magazines still pass through guns) | open | the keep-out of 155 is in 67b1141 and e50f4f6 | still open. Seen today: pushed hard or fast, a held magazine still goes through the gun; dropped magazines collide fine |

### Ticked in Tasks.md and confirmed by the commits

135, 136, 140, 141, 143, 147, 154, 158, 166, 171, 172.

### Code done, passed only the virtual-headset autotest (still to check in the headset)

173 (footsteps while standing still), 177 (passing the weapon to the other hand), 178 (Glock 19 magazine), 182 (MP9 PDW bolt), 185 (slide stays locked with a fresh magazine), 186 (BREN 2 grab zone).

### Code done, not verified yet

137, 138, 145, 146, 148, 152 (step one), 155, 157, 159, 160, 161, 162, 163, 164, 174, 175, 176, 179, 188, 189, 190.

### No work on them last night

139 (partly checked by the deep autotest), 142, 144, 151, 153, 170 (a note only), 180, 181, 192, 193, 194, 195, 197, 198, 199.

## New tasks (2026-10-15)

200. [ ] No sound when a magazine knocks against a gun
201. [ ] Two-handed reload (mag kick): the old magazine comes out of the inside of the gun instead of being knocked out clear to the side
202. [ ] The trigger finger visibly presses the trigger on each shot
203. [ ] RPGs shake in the hand without being fired
204. [ ] Machine guns: weight and delay when picked up and moved (Boneworks)
205. [ ] A grabbed RPG rocket is invisible, and putting it in does nothing
206. [ ] Pulling hard away from a grip makes the hand come off it
207. [ ] The hands have no collision with weapons
208. [ ] Weight and force like Boneworks: guns stay rigid in one hand and do not swing when the hand moves up or down; one-handed shots should push the muzzle up more
209. [ ] On-screen effects: pick from the suggestions (A-G and I, pictures) and build them
210. [ ] More hand poses on guns and free-hand gestures: pick from the suggestions (pictures) and build them
211. [ ] A small view shake when shooting, by gun (recoil and muzzle flash)

For 200. Reported 2026-10-15: a magazine touching or hitting a gun makes no sound. Checked in the code: the sound set (`MechSound` in src/Weapons/ProceduralSounds.cs) has no knock or scrape, and MagazineProp plays no sound. A held magazine is only drawn out of the gun's solid (`MagazineProp.KeepOffGun`), silently. To do: a short metal knock when a held magazine first meets the gun, louder the faster it came in, and a softer scrape while it slides along. Add a click at the magwell lip when it goes onto the rail. Use the existing procedural sound pool, rate-limited like the gun's contact knock (at most 4 a second). Done when: knocking a magazine against the receiver, the handguard and the magwell lip each sounds and buzzes the hand, and resting a magazine against the gun does not machine-gun knocks.

For 201. Reported 2026-10-15 (right-handed player): in a two-handed reload the old magazine does not drop clear on the side of the gun. It comes out of the inside of the gun, where magazines go in, and falls from there. Checked in the code: the mag kick (`ReloadInteractions.TryMagKick`) calls `EjectMagazine`, which creates the old magazine at its seat inside the gun, slides it straight out along the well's axis, then drops it with the gun hand's speed, down and back along the axis (`FinishEject`). It has no sideways push, and it slides out through the fresh magazine that is coming up. To do: a knocked-out magazine starts at the well's mouth (not deep inside), gets the strike's push (the fresh magazine's speed) plus a sideways kick away from the gun hand's side and down, and spins a little. The drop for the release button stays straight down out of the well. Done when: in the headset the old magazine flies out clear to the side under the gun and never shows inside the receiver or through the new one. For left-handers it flies to the other side.

For 202. Reported 2026-10-15: the trigger finger should visibly press the trigger when shooting. Checked in the code: HandModel already moves the index with the trigger's analogue value. On a measured gun its knuckle stays on the frame and the two outer joints curl until they touch the trigger. On a weapon with its own hand pose it blends from the pose's index to 0.85. So if the headset shows no press, either the trigger is not part of the measured solid (the finger stops on the guard or the frame), or the curl is too small to see. To do: check in the headset and in close-up pictures (an autotest close-up with the trigger at 0 and 1); make the pad land on the trigger blade and travel with it. Add a short extra press on each shot for controllers that only report the trigger as a click. Done when: close-ups show the finger along the frame at rest and on the trigger blade when firing, on a pistol, a rifle and a machine gun.

For 203. Reported 2026-10-15: RPGs shake in the hand far too much, even without firing. Maybe a weight bug. Candidates seen in the code:
- launchers droop 8 degrees and trail the wrist at 8 per second in one hand (`HeldWeapon.Handling`);
- the shouldered-stock check fades the droop in and out at the edge of the shoulder pocket (a long tube resting on the shoulder sits right at that edge);
- the gun's physics stand-in pushes a long launcher against the player's body or the ground and lets go again (world collision, task 122).

To do: measure the held launcher's jitter in the autotest (frame-to-frame angle and position change while the hand is still; one hand, two hands, on the shoulder), find which of these moves it, and fix that. Done when: a still hand gives a still launcher (under 0.2 degrees and 2 mm of frame-to-frame motion) in all three holds.

For 204. Reported 2026-10-15: machine guns should have weight and delay when picked up and moved, with the same feel as Boneworks. Today a machine gun only droops 11 degrees, trails the wrist at 7 per second in one hand, and kicks 2.6 times harder in one hand (`HeldWeapon.Handling`). There is no lift-up delay and no positional inertia. To do: as part of 208, a heavy weapon lags the hand when lifted or swung, settles with a little overshoot, and needs the second hand to aim steadily. Picking it up from the ground or a holster eases it up over about half a second. Done when: in the headset an MG clearly feels heavier than a rifle, and a pistol still feels light and snappy.

For 205. Reported 2026-10-15: grabbing an RPG rocket (from the pouch) makes it invisible, and putting the invisible rocket in does nothing; it is simply not there. It is probably linked to task 179's change, where a rocket is now copied from the launcher's own rocket model (it may be copied hidden, scaled to almost nothing or far away), and to task 180 (launchers cannot be reloaded). To do: log the copied rocket's renderers, scale, position and layer when it is handed out; fall back to the made rocket when the copy is not visible; make the rail accept it. Done when: on the RPG-7 and two other launchers a rocket from the pouch shows in the hand, slides in and fires.

For 206. Reported 2026-10-15: pulling hard enough away from a grip, the hand comes off it. Checked in the code:
- the support hand lets go of the handguard when the hand is more than 35 cm from the support point (ReloadInteractions, start of the foregrip update);
- a racking hand lets go after drifting 25 cm sideways;
- a feed cover after 25 cm;
- a gun held back by a wall or the ground (world collision) stays at the obstacle while the hand goes on, and only a weapon with its own authored grip pose keeps the glove on its grip. Every other weapon's glove leaves the grip with the tracked hand.

To do: decide with the user which one this is and what it should do. Suggested: while the grip button is held, the glove stays on the grip and shows how far the real hand is (a stretched arm with stronger haptics), and lets go only past a much larger distance with a clear haptic, as Boneworks does. Done when: the user confirms the hand stays on while pulling hard in the headset.

For 207. Checked 2026-10-15: the hands have no physics colliders. An empty hand passes through the held gun, through the other hand's gun and through the world. A hand holding something only closes its fingers onto what it holds (the gun hand on its gun, the support hand on the gun and the gun hand's fingers, a racking hand on the part and the gun). To do: the empty hand's glove stops at the held gun's solid and the gun hand's skin, drawn out of them the shortest way the way `MagazineProp.KeepOffGun` does for magazines. This is visual only and never pushes the gun or moves the tracked hand. Its fingers do not close through the gun either. Later, optionally: the world (walls, crates). Done when: an empty hand pushed through a held rifle stops at its surface in the pictures and in the headset.

For 208. Reported 2026-10-15: all guns are straight in the hand and do not wobble when the hand moves up or down in one hand (Boneworks has that force), and one-handed shots should push the gun up. Checked in the code: today an unsupported long gun droops a fixed 3-11 degrees by class and its rotation trails the wrist at 7-22 per second (`HeldWeapon.Handling`, the `WeaponWeight` setting, on by default). Recoil is 1.15-2.6 times stronger one-handed by class, and the springs settle in about a tenth of a second. There is no positional inertia and no swing. To do (the `WeaponWeight` setting switches it):
- a mass per weapon (from its class and size);
- a spring and damper on the gun's position and rotation, driven by the hand's acceleration, so a heavy gun lags a quick lift and swings back, and the muzzle dips when the hand jerks up;
- one-handed shots climb more and recover more slowly than two-handed ones;
- the second hand or a shouldered stock stiffens it, and pistols stay snappy.

Measure it in the autotest: the hand moved up and down at 1 m/s, giving the muzzle's lag and overshoot per class, and the climb per shot one-handed against two-handed. Done when: those numbers rise with weight, and the headset feels like Boneworks without making aiming sloppy.

For 209. On-screen effects, suggested 2026-10-15 with mockup pictures (both eyes, right-handed; v2 shows the effects alone, without the gun or props). H (barrel smoke and heat haze) and J (landing vignette) were dropped by the user on 2026-10-15; the letters of the others stay as in the pictures. Pick which to build:
- **A.** muzzle flash at the shoulder: a warm wash on the gun side, stronger in the eye nearer the muzzle;
- **B.** pistol close to or turned toward the face: a hot bloom and a brief lift of the image;
- **C.** how it scales: flash size x how much the muzzle faces the eyes / distance; suppressed is almost nothing, never full white;
- **D.** explosion close by: a flash from that side, dust haze, mud (task 110), muffled audio and ringing for 1-2 s;
- **E.** bullets cracking past: darker edges, colour drains, a faint streak;
- **F.** hit direction: a red glow on that side, both eyes, and a haptic on that hand;
- **G.** low health: colour drains, a slow red pulse with a heartbeat;
- **I.** water drops after surfacing or in heavy rain (optional, off by default). They must be realistic drops, not
  blobs: each drop is a small wide-angle lens that shows the scene upside down and much wider than itself (sky in its
  lower half, ground in its upper half), with a thin dark lower edge, a bright upper rim, a sharp highlight and light
  gathered at its bottom. Some drops run down as teardrops, leaving a faint wet trail and a few droplets behind; fine
  mist between them. They gather toward the edges, not the middle of the view, and dry within about 3 s. They are the
  same drops in both eyes, shifted as if at one fixed depth, so the eyes never disagree.

All subtle and short, each with its own setting, with VR comfort first. The flash per eye uses the muzzle's position against each eye's camera. Done when: the chosen effects show in the autotest pictures and the user signs them off in the headset.

For 210. More hand poses and gestures, suggested 2026-10-15 with pictures. Pick which to build:
- gun poses:
  - the C-clamp on the handguard (thumb over the top);
  - a vertical foregrip wrap;
  - a magwell grip;
  - the trigger finger along the frame when not firing and on the trigger when firing (202);
  - the thumb on the magazine release while dropping a magazine (131);
- already done in hands rounds 4-5: the pistol magazine index-point, the overhand slide rack, the slingshot pinch;
- free-hand gestures, mapped to squad orders where it fits:
  - Valve's own pointing pose (in glove.bin, unused so far);
  - thumbs up, fist (hold), open palm (halt), two fingers, OK, beckon (follow me), finger gun, rock on.

Boneworks has about 30 hand poses (counted from its modding community's extraction for task 111; no official number is published). The mod also has 30, but on an empty glove most of them look almost alike (a curled fist) and only differ once the fingers close on an object. Distinct authored shapes per grip would read better. Done when: the chosen poses show in the self-test's pose sheet and look right in the headset.

For 211. Suggested by the user 2026-10-15: the view shakes a little when shooting, more or less depending on the gun (its recoil and muzzle flash). In VR, moving the view on its own (not with the head) is the main cause of motion sickness, so this has to stay tiny and short:
- one jolt per shot, a fraction of a degree, gone within about 60 ms;
- scaled by the gun's recoil (the same per-round kick that drives the gun's recoil spring) and its muzzle flash;
- capped for automatic fire, so a long burst does not become a continuous wobble;
- never a sustained sway, never a camera translation;
- its own setting (strength, off), off or low by default.

A gentler alternative to test side by side is a brief blur or scale pulse of the image instead of moving it. Done when: the autotest measures the jolt per shot for a pistol, a rifle, a shotgun and a machine gun (it rises with recoil and stays inside the caps), and the user signs it off in the headset as noticeable but not sickening.
