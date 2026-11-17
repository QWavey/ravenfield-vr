# Ravenfield VR

A VR mod for Ravenfield (Steam, Unity 2020.3 build) that runs through SteamVR. Built and tuned for a Quest 3 on SteamVR (Steam Link, Air Link / Link cable, or Virtual Desktop in SteamVR mode); Index, Vive and Reverb G2 controllers have default bindings too.

What you get:

- Stereo rendering through Valve's OpenVR XR plugin, roomscale tracking, snap or smooth turning, comfort vignette, graphics presets made for VR.
- Free hands. The gun sits in your hand and fires where its barrel points; nothing is glued to anything. Each weapon's own finger pose (from its mod's arm rig) shapes your hand around its grip, or switch to the weapon's own first-person arms, moved to your controllers by IK: the gun hand holds the grip as the gun's animation does, the other hand its foregrip and its magazines as the reload does, and off the gun its fingers follow your controller. With your hands empty the arms of the last gun you held stay on both hands.
- Manual reloading for every gun, modded ones included: magazines slide in and out along the well, slides, bolts, charging handles and pumps move with your hand, break-action shotguns and revolvers open, machine guns have feed covers. Your ammo pouch shows what the gun takes.
- The mod's own sounds for each action (magazine out, magazine in, rack back, slam home), cut out of its reload audio, so a reload sounds like what your hands are doing instead of the whole reload clip playing at once.
- A gun that kicks by how it is held: one hand, two hands, on the shoulder, or laid on something solid (a machine gun on a ledge, on its bipod, or fired lying down kicks half as hard). Its trigger moves with your finger.
- Semi/auto switching on guns that have it, and the fire-mode, flashlight, laser, bipod and inspect keys of weapon and mutator scripts reachable from the controllers.
- Fallen soldiers drop their weapons; pick one up and it replaces the one in that slot.
- Grenades with a pin you pull and an arm you throw with; melee weapons that hit when you swing them.
- Scopes you look through (magnified lens with a reticle) on weapons that only had a 2D overlay.
- Every menu on panels you point at with a laser; text fields open the SteamVR keyboard. The HUD as a frame in front of you that follows your view loosely (small head movements leave it still), with its markers lined up with the world. Vehicle gunner sights shown at infinity, so they never double.
- Cars and Humvees with a steering wheel you grab and turn, and your own weapon usable from the driver seat. Helicopters, planes and tanks keep stick controls. Open mounted guns (gun nests, a jeep's or technical's MG, a tank's roof MG, the mods' too) are taken by their handles and swung with your hands, and stay where you let go; turrets aimed through a sight aim where you look.

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
- **Settings panel:** double-click the left stick. Three tabs hold what matters most: Comfort, Weapons and Display; Close, SteamVR bindings, Mod keys and Recenter are at the bottom. Set `ShowAdvanced = true` in the config file and start the game again for four more tabs (Controls, Gear, View, Speed). What is better simply on, such as two-handed aiming, the magazine detent and the counters, is only in the config file.
- **Gun angle:** if guns feel tilted in your hand, use "Gun pitch" on the panel.

## Controls (Quest Touch, right-handed)

| Input | On foot | In a vehicle |
| --- | --- | --- |
| Right trigger | Fire | Fire mounted weapon |
| Left trigger | Use: enter vehicle, ladder, interact | Hold 1 s (the hand on your seat's side): leave vehicle |
| Grips | Grab: foregrip, magazine, slide, bolt, cover, grenade pin, magnifier, holsters, pouch, anything lying on the ground, a soldier. Closed on an empty hand: a fist. Hold half a second on a vehicle: get in | Grab: steering wheel (one or both hands), flight stick, a plane's throttle lever |
| Left stick | Move. Click: sprint (you can shoot while sprinting). Double-click: VR settings. Hold: scoreboard | Steer and throttle (car), collective and yaw (helicopter), rudder and throttle (plane). Click: next seat |
| Right stick | Turn. Up: jump. Flick down: crouch. Hold down: prone. Click: next weapon. Hold click: kick | Cyclic (helicopter, plane; a plane's with a gentler middle). Hold click: next seat (let go of the controls first at speed). Click: next seat where the seat has no second weapon to cycle |
| B | Drop the magazine / open a break-action or revolver | Reload a mounted gun |
| Grip (free hand) | | Aim the seat's gun: its zoom or sight camera, and a lock-on that needs aiming. At a mortar the right stick up and down sets its range |
| A | Slide or bolt release; pulls a grenade's pin; sets off the charges a C4 in the hand threw. With the slide forward: fire mode (semi/auto, or the weapon's own mode switch; a switch that cannot be undone waits for a hold). Pressed twice: slow motion (also in menus and while dead; your hands keep your speed, or slow with the world with `SlowMotionHands` = Slowed) | Countermeasures. Hold: auto-hover (helicopter) |
| X | Squad order (hold and point, release to send; the objectives' flags show with their names while you point; point at an enemy you can see to have the squad attack them). Pressed twice: pause menu | |
| Y | Squad map. Hold: pause menu. Double: loadout | |
| Menu (≡) | SteamVR's own dashboard (the match pauses behind it); not bound by the mod | |

Change anything in SteamVR: **Settings → Controllers → Manage controller bindings → Ravenfield**, or the "SteamVR bindings" button on the settings panel. Left-handed players: set "Weapon hand: Left" and restart; the default bindings mirror automatically.

Other gestures:

- **In the game's menus:** the trigger is the left mouse button and **B the right one** (a weapon entry's options, the squad map's context menu). On the tactics map press a spawn point and drag to another to send an attack; hold B and sweep the laser over an arrow to take it off.
- **Holsters** (laid out like Boneworks): weapons are carried by what they are. Rifles, launchers and other long weapons hang down your back from the shoulders, grip up (weapon side first, then the other shoulder); pistols sit under your arms; grenades, kits and tools at the small of your back, then on the front of your belt. Reach there with your weapon hand and grip; a short buzz tells you your hand is at a holster with something in it, and the weapon there eases out a little. Put a gun back where it lives and let go to have both hands free. `HolsterLayout = Classic` (or "Holsters: Classic" on the panel) brings back the earlier layout: pistols on the hips, gear on the chest.
- **Night vision:** grip with your off hand at the side of your head; a short buzz says it took. The view turns the game's green, with its grain and bloom.
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

A magazine fits its own gun and that gun's sister variants (the same part of the same size): one pulled from a soldier's HK416-B goes into your HK416-A. Let go just short of its latch, it says `TAP IT HOME`.

**The M1 GARRET (en-bloc clip):** after its last round the empty clip pings out of the top and the bolt stays back. Push a full clip from the pouch down into the open action: the bolt slams forward on its own and chambers the first round. With the bolt shut on a round a clip will not go in (`OPEN IT`: pull the rod back first), nor will the empty clip it threw out (`EMPTY CLIP`).

**Bolt-action, lever and pump guns** move only by hand: pull the bolt or pump back and push it forward again to chamber the next round. Let go half way and it stays there.

**Shotguns with a tube** take shells one at a time at the loading port; semi-automatic ones cycle themselves after each shot, pump guns need the pump.

**Break-action shotguns, revolvers and break-action launchers:** press B to open (the barrels tip down, the cylinder swings out, fired cases fall out). Push shells into the chambers from your pouch (a revolver with a speedloader takes all six at once). Flick your wrist up or press B to close, or close it by hand (lift the barrels, push the cylinder in).

**Machine guns** with a feed cover: grab the cover's rear latch and lift it (with the cover shut and the belt in the feed the box will not come off). Drop the old box (B), slide a new box onto its rails (a short, heavy slide), then take the belt end at the mouth of the box and lay it on the feed tray under the cover; it clicks in. Press the cover down over the belt and rack if the bolt is forward. The counter walks you through it: `LAY BELT`, `CLOSE COVER`, `RACK`.

**Rocket and grenade launchers:** grip at the pouch for a round and push it in at the front, tail first (held the other way it says `TAIL FIRST`).

**Grenades:** pull the pin with your other hand (grip the ring at the top and pull it away), or press A to flick it out one-handed. Nothing happens while you keep holding the grenade; hold the trigger, throw with your arm and let go of the trigger at the end of the throw, and the fuse starts as it leaves your hand. Thrown with the pin still in, it is a dud: it bounces and lies there, and picking it up puts it back in your pouch. Put the grenade away with the pin out and the pin goes back in. Smoke grenades work the same; ammo bags, medic bags and charges have no pin. **Melee weapons** hit what you swing them through. So does any gun, anywhere from its butt to its muzzle, and an empty hand closed into a fist (grip pressed, nothing held): punch.

**Soldiers:** reach for a soldier with an empty hand and grip. They go limp and hang from your hand; drag them, slam them into things, or swing and let go to throw them. A hard impact hurts them and any soldier they hit, by the speed squared as in Boneworks (a shove does nothing, a slam a fifth of their health, at most a third in one impact), so it takes a few slams to finish one off. Your own team's soldiers are hurt like anyone else, as by your bullets and knife. Your hand sits on the body where it took hold (chest, arm, hand, leg), its fingers round it. Only an empty hand takes hold: the hand on your gun, a grenade or a magazine never grabs a soldier, the other, empty hand does.

- **Human shield:** hold a soldier up in front of you; bullets hit him, his own friends' too.
- **His gun:** swing a hand into a soldier's gun to knock it aside (his aim goes off and he holds his fire for a moment). Grip under the middle of his gun to pull its magazine out: he has to reload, and the magazine is in your hand. Grip it anywhere else to take it from him: he draws his other weapon (one with no other weapon keeps it, pushed off you and silent for as long as you hold it). Gripping his hand or arm takes him, not the gun.
- **From behind:** reach for an enemy's head from behind and grip: he is held still. Turn your wrist hard (a quarter turn) to break his neck; let go and he is free again. It works on your own team too.
- **Strapped grenade:** holding a soldier, draw a grenade in the other hand, pull the pin (or press A) and let it go slowly on his body (walking is fine): it sticks to him. Throw him away before it goes off: he dies in the blast, everyone near takes the grenade's normal explosion damage. It kills one of your own team the same way.

Loose magazines, shells and duds are physical: they bounce off your gun while they fall, lie still once they have landed, and never catch on your own body. Bullets pass through them. Your gun pushes a magazine it meets (lying or falling), never faster than the gun moves; a magazine leaving the well passes out through the gun. An empty hand tapped or swept into a loose magazine pushes it, and a slow reach to take it does not. Fired cases fly out of the ejection port and lie on the ground for a while.

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

Get in by holding your grip on a vehicle for half a second with an empty hand (or the left trigger as before); get out by holding the trigger of the hand on your seat's side for a second; click the left stick to move to the next seat. Grab the steering wheel of a car or Humvee with one or both hands and turn it; the car steers with it. Let go and the left stick steers again. While driving, reach to a holster to draw that weapon and shoot with one hand while steering with the other; put it back to get the horn again. Shooting your own gun from a seat, hold on to the vehicle with your free hand (close it on the frame, a door or a handle) and the gun kicks less. Open mounted guns within reach (gun nests, a jeep's MG, a technical's, the mods' standing and vehicle guns) are taken by their handles with your grip: the gun swings round its pivot with your hands as a real one does (handles to the right, muzzle to the left) and stays where you let go; the trigger of either hand on it fires it, and each shot kicks in your hands. With manual reloading, an empty gun is reloaded by its cocking handle on the receiver's right side: grip it and pull back (a gun with no magazine never needs it). Setting `GrabMountedGuns` (on); off, they aim where you look. A steering wheel a mod left linked to no seat is turned by the driver too. In closed vehicles (tanks, APCs, the mods' armour) you sit inside the real hull, solid around you, with a periscope viewer before your eyes showing the outside; turn the gun with the right thumbstick or crank its handwheels (left: sideways, right: up and down), press the red button beside the right handwheel to fire (or pull the trigger of a hand on a handwheel); its lamp shows green when loaded, amber while reloading, red when empty, and the readout under the viewer counts rounds and spares. The periscope is your sight, with a reticle; press the knob on its right for x3 zoom (the stick aims finer then). With manual reloading a tank's cannon waits for you to load it: grip a shell from the rack on your right and push its nose into the open breech on your left; an APC's or armoured car's gun waits the same way for an ammo box from the rack set on its feed tray on your left (a glow shows where; setting `LoadCannonByHand` (Vehicles, on), off and those guns reload themselves while your own stay manual). As the driver press the forward, back, left and right buttons, and in a tank pull and push its two steering levers, one for each track; in an APC push the throttle lever beside the steering wheel (the sticks work too). Hits throw sparks inside, and a badly damaged hull fills with thin smoke. A tank whose one seat drives and aims has a small DRIVER screen looking straight ahead along the hull, since the periscope follows the gun. Whatever you hold in a vehicle (a steering wheel, a gun's handles, a flight stick, a lever, the hatch handle) your drawn hand closes on it and stays there while you hold it, and the vehicle you sit in is drawn smoothly at the headset's frame rate. Press RANGE beside the readout to measure the range along the gun's line: a green mark then shows on the sight where to put the target so the round drops onto it (red at the edge when the target is out of the gun's reach), and grip the yellow hatch handle overhead and pull it down to get out. The dials show speed, engine, hull, the turret's way against the hull and the gun's elevation; a switch by the viewer turns the red combat light on and off, and hits make the cabin light flicker. Each hull's room is measured, and fittings that would not fit inside are left out. Looking around inside never moves the gun. While the vehicle burns, the inside flashes red (an open vehicle pulses in your hands instead). Setting `Interiors` (Vehicles). Helicopters have a collective lever where your off hand rests, like a plane's throttle: grab it and push forward to climb, pull back to descend; it stays where you let go until you push the left stick. The steering rim, the flight stick, the levers and a mounted gun's handles glow faintly as a free hand comes near; a vehicle you can get into shows a glow at your palm that fills green while you hold. Changing seats tells you which seat you are in now, and a helicopter's auto-hover clicks as it turns on or off. A helicopter's door guns and a bomber's tail gun are taken by their handles too. Tanks, helicopters and planes use the sticks (helicopters and planes also have a grabbable flight stick: tilt your wrist, or move your hand). A plane also has a throttle lever where your off hand rests by the seat: grab it and push forward for more, pull back for less (a click at cruise; pulled past the second click, the airbrake). It stays where you let go; pushing the left stick takes the throttle back. A plane's grabbed flight stick also stays where you let go of it (it holds the attitude, like a trimmed stick); grab it again to go on from there, or push the right thumbstick to take over (setting `StickHoldsTrim`). While a vehicle turns fast, a light tunnel darkens the edge of the view (Settings, Comfort, "Tunnel in vehicles"). In a seat whose vehicle has its own outside camera (planes) you sit at the seat's eyes in the cockpit; a turret's view stays put while its gun turns. Hits, crashes and hard landings rumble in your hands, and the engine hums faintly in a hand on the wheel or stick.

## Sandbox

**Sandbox is a game mode:** choose it where the game's modes are (next to Point Match, Battle ...). It starts with no soldier on either side, only you, and has no end by score. The spawn tool is in your hand from the first life, and the menu button opens the sandbox panel. (Inside any other match the panel opens from the VR settings' **Sandbox** button.)

- **The tool** looks like one of the game's pistols with a lamp at its mouth (red: the next pull makes an enemy, blue: a friendly or a vehicle, yellow: it points at a soldier). It is in your gun hand while that hand holds nothing, else in your other hand. Point it at the ground and pull that hand's trigger. Pointed at a soldier who is already there, the pull gives him the chosen behaviour instead.
- **Soldiers:** the side (enemy, friendly), the behaviour (normal, neutral = never shoots, stand still, frozen, follow me), the weapons (their own, random, none, yours), the health (normal, no damage, one hit) and how many. "Here" makes them three metres in front of you without the tool.
- **Weapons:** every weapon of the game and of your loaded mods as a tile with its picture; a press puts it in your hands.
- **Vehicles:** any of the map's vehicle kinds, for either side.
- **World:** pause and play (every soldier holds still and holds fire), quiet battlefield (none of the match's own soldiers), kill every enemy, remove what you made (the last, or all; or take the remover and point at one soldier or vehicle), and save what you placed on this map to set it up again later. A soldier you made never comes back by himself: once he is dead his body lies for the time you choose here (5 seconds to 5 minutes), then it is taken off the field.
- **Me:** no damage for you, flying (the walking stick moves you where you look; look up to rise), fill up health and ammunition, put the tool away or take it, and the way to the VR settings.

## Multiplayer (early)

Each time the game starts, a choice stands over the main menu: **Single player** or **Multiplayer**. The multiplayer panel can also be opened later from the VR settings.

- **Host:** Public (your game shows in everyone's list), Friends (only friends see it; "Invite" opens Steam's friends list), or LAN. Then start a match from the game's own menu: everyone in your game loads the same map and mode.
- **Join:** press Join on a game in the list, accept a friend's invitation in Steam, or type a LAN address. Once you are in, the host chooses: your own game menu is hidden, your panel shows the map, mode and numbers he is picking, and the match starts for you when he starts it.
- No slow motion and no pause in a shared match.
- The host can kick a player (Kick, pressed twice). A joined player can play against the host ("Play against" in his panel, from the next match). Without a headset, press **F8** for a multiplayer window.
- Everyone needs this mod in the same version, and for a workshop map the map itself. A game on another version is shown in the list with the reason and cannot be joined.
- You see the other players and the host's soldiers; everyone fights the same soldiers. Vehicles and turrets are shared: whoever sits in the driver's seat drives it for everyone, the others ride along; hits on a vehicle count for everyone. A seat the other player sits in cannot be taken. The flags, the Battle mode's battalions and tickets and the end of the match are the host's for everyone (other modes' scores are each PC's own; the scoreboard's kills, deaths, flags and vehicles are shared), and the other player is a soldier with his name over him who moves as he does in VR: his arms follow his hands, his gun is where he really holds it (lifted, sideways, tipped up for a reload), his head turns where he looks, his free hand turns with his wrist and closes as he squeezes his grip, his gun's own magazine shows in his free hand while he reloads, and he carries what the player carries (the gun he is not holding on his back, the sidearm at his thigh). Hitting him, knocking him over or throwing things at him works as on any soldier, and he feels it on his PC; soldiers of the other PC (him and the host's soldiers on a joined PC) cannot be grabbed, held or disarmed by hand, since his PC decides where they are.
- Point with the squad button to show your friend a place: an order you give that way, or with no squad of your own a plain SPOT, puts your name, the distance and a marker over that place on his screen for 10 seconds (red for an enemy).
- Hand your friend a magazine: let go of a loaded magazine at his hand while he holds the same gun, and its rounds go into his reserve. A gun one of you puts down can be picked up by the other, with the rounds it had.
- In the sandbox the host places what the joined player asks for: his vehicle and soldier tool works too, and what it makes is shared (the field settings stay the host's).
- Voice chat (off by default): turn it on in Settings, Display ("Voice chat (multiplayer)") and pick your microphone there; you then hear each other from your soldiers' heads, louder when near. It sends only while you speak. Both players need it on.
- Played over Steam it needs no open port. It has been checked with a second player simulated on one PC; a real session between two PCs is its first full test.

## Local play: LAN and splitscreen

Multiplayer first asks **Local** or **Online**. Online is Steam (friends, open games) or an IP address, as above. Local is:

- **LAN:** host a game on this network, or join one from the list (games on the network show up within a second; no address to type).
- **Splitscreen:** 2 to 4 players at one PC, each a whole player: the same view, display, deploy screen, loadout, weapons, vehicles and every feature as the first. Set the players and the screen (two side by side or one above the other; three or four in quarters), turn it on, then start a match. The screen reads "Loading players" while the others' games load (each extra player runs its own copy of the game, out of sight, joined to a LAN game this PC hosts); when all are in, everyone is shown and deployed together, each in their part of one screen. Players see and can fight each other (switch team with T, or the controller's D-pad right).
  - **Controls:** your own Ravenfield controls (Options, Controls) for every keyboard and mouse; Xbox-style controllers the game's controller layout (left stick moves, right stick looks, right trigger fires, left trigger aims, A jump, B crouch, X reload, Y next weapon, RB use, Start the menu).
  - **Which keyboard is whose:** the first keyboard and mouse touched are player 1's, the next ones player 2's, and so on; player n takes controller n.
  - **Players 2 to 4** have their own Esc menu (the game goes on; only player 1 pauses it): resume, switch team, respawn, the weapons for the next spawn, mouse sensitivity, invert up and down, leave. Each player's mouse sensitivity and invert are remembered for the next game. Down, fire or jump deploys and the next-weapon key picks the flag.
  - **Speed:** the other players' games draw at the screen's rate (up to 144 frames a second) and come down by themselves (120, 90, 60) if the graphics card cannot hold it with everyone playing.
  - With VR the headset is player 1 and the others share the monitor. The lighter old way (the other players as soldiers drawn in player 1's game) stays as an option: `FullGames = false` under Splitscreen in the settings file.
  - The settings are kept (Splitscreen in the settings file: Players, AboveBelow, FirstController, FullGames).

**Steam status:** friends see what you are doing: VR or flat, alone, hosting or in a multiplayer game, splitscreen, the map and mode, who is winning and by how much, and you (waiting to deploy, driving a vehicle, the weapon in hand). Steam shows it in your game info and in chat.

**Without a headset:** the release also has `RavenfieldMultiplayer-<version>.zip`, the same mod with VR off for friends without one (F8 opens the multiplayer window).

## Graphics

Pick **Low, Medium, High or Ultra** in the launcher or on the settings panel ("Graphics"). A preset sets the render resolution, MSAA, shadow distance and quality, level of detail, per-pixel lights, texture size, vegetation and view distance, layered on top of the game's own options. "Game" leaves graphics to the game's options menu. Render scale stays adjustable after picking a preset. The presets are tuned for VR, where shadows, MSAA and per-pixel lights are paid once per eye: Low has no shadows (about the game's "Fast" quality), Medium hard shadows to 30 m ("Simple"), High soft shadows to 60 m ("Good"), Ultra to 100 m with 4x MSAA ("Beautiful"). If you see ghosting or things lagging in front of you, the frame rate is below the headset's: pick a lower preset (or "Game" with a low quality in the game's options).

Every shadow, per-pixel light and level of detail is drawn once per eye, so in a busy match the presets above Low can cost more processor time than the 11 ms a 90 Hz frame has; SteamVR then drops to half rate (45 fps) even when the graphics card has room. If the wrist display shows few frames on time with a low GPU time, pick a lower preset or turn on **Hold the frame rate** on the settings panel: it lowers the preset's costliest settings while frames are late and brings them back when there is room. Each minute the game's log (`BepInEx/LogOutput.log`) gets a line saying where the frame's time went; `python tools/frame_report.py` turns those lines into a table, and `python tools/bench_report.py` the autotest's graphics bench into what each variant saves or costs against its own preset.

The settings panel's **Speed** tab (shown with `ShowAdvanced`) has settings to try, all off by default: game cameras into 1 px, scope lens every other frame, small objects without shadows, tiny distant objects hidden, repeated objects instanced and the garbage collection slice. Each one the frame rate line names (with a count where it has one) while it is on, so turn one on, play a few minutes and compare its minutes with the ones before in `frame_report`'s `try` column; or run `python tools/vrshots.py --fast --bench` (the game must be closed; it measures each of them against Medium with the world frozen, twice) and read the result with `bench_report.py`. Keep one on only if it saves time and you see no difference. With the mod's own presets (Low to Ultra) the level's terrain is drawn instanced, as the game itself does at its top terrain quality (not on maps the game gives its fallback terrain material, older engine versions by its own test); the Game preset leaves the terrain as the game set it.

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
| `FallInFirstPerson` | on | Knocked over, your view stays at your soldier's head as he falls and lies (the horizon kept level, his head hidden), not the game's camera behind him |
| `ScopeHalfRate` | off | To try: the magnified scope lens draws its picture every other frame (about half of its 0.5 ms a frame while aiming through it); its picture may lag a fast pan. On the panel's Speed tab (with `ShowAdvanced` on) ("Scope lens every other frame") |
| `GarbageCollectionSliceMs` | 3 | To try: how much of a frame the game's garbage collector may take while it collects (Unity's own is 3 ms); lower spreads the same work over more frames. The frame rate line says the slice. On the panel's Speed tab (with `ShowAdvanced` on) ("Garbage collection slice") |
| `InstanceRepeatedObjects` | off | To try: the level's materials drawn by several objects are drawn instanced, so copies of one mesh go in one draw in each eye's passes. Not for static-batched or lightmapped objects. On the panel's Speed tab (with `ShowAdvanced` on) ("Repeated objects instanced") |
| `HideTinyDistantObjects` | off | To try: the level's objects that would be only about three pixels of an eye across (1 m corner to corner past about 250 m on Medium) are not drawn, so each eye's passes have fewer objects. Standing props only; everything is drawn while a magnified scope is. On the panel's Speed tab (with `ShowAdvanced` on) ("Tiny distant objects hidden") |
| `SmallObjectsNoShadows` | off | To try: the level's small objects (under 40 cm corner to corner) cast no shadow, so the shadow passes drawn once per eye have fewer objects (with shadows on). On the panel's Speed tab (with `ShowAdvanced` on) ("Small objects without shadows") |
| `GameCamerasOnePixel` | off | To try: the game's own screen cameras (drawing nothing in VR, kept on for its logic) render into one pixel instead of the whole screen, which may save about 0.7 ms of processor time a frame. Applies at once; on the panel's Speed tab (with `ShowAdvanced` on) ("Game cameras into 1 px") |
| `HoldFrameRate` | off | While frames keep missing the headset's rate, lower the preset's costliest settings a step at a time (shadow distance, shadows, level of detail, grass, per-pixel lights, last 15% of the resolution, that one only when the GPU is the cause: with the CPU the limit it would only blur the view) and bring them back when there is room. The wrist display says how many steps. Also on the panel ("Hold the frame rate") |
| `PostProcessing` | off | Copy the game's colour grading, bloom and AO to the headset (costs frame rate) |
| `GogglesTube` | 0.5 | How dark the edge of the view is through night vision goggles. 0: none |
| `WristFrameRate` | on | The wrist display also shows the share of frames at the headset's rate and the GPU's time a frame (last few seconds) |
| `BodyShadow` | off | Your own soldier's shadow on foot and in first-person seats. Its arms and gun move in the game's animation, not yours, so the shadow wanders across your hands |
| `Hands` | VRHands | VRHands, or WeaponArms for the weapon's own first-person arms (with no weapon in the hand, the arms of the last one you held) |
| `TurnMode` / `SnapTurnAngle` | Snap / 30 | |
| `HolsterLayout` | Boneworks | Boneworks, or Classic (pistols on the hips, gear on the chest) |
| `GripToHoldWeapon` | on | Hold the grip to keep holding a weapon; letting go puts it away. Off: it stays in your hand (either hand) until you put it back at its holster with a grip press there |
| `MagazinePouch` | OffHandHip | Also Chest, DominantHip, OffHandChest |
| `ShowPouch` | on | Show the gun's ammo at the pouch, the last gun's while it is put away |
| `WeaponWorldCollision` | on | The gun in your hand is stopped by walls, crates and the ground |
| `GloveSize` | 0.88 | Size of the tactical glove (applies at the next start) |
| `ExplosionDirt` | on | Dirt on your view from close explosions |
| `TwoHandRecoilReduction` | 0.4 | How much a second hand on the gun tames recoil |
| `WeaponWeight` | on | Long guns held in one hand droop and trail the wrist |
| `SlowMotionHands` | Normal | In slow motion. Normal: your hands move at your real speed and what you let go of flies as in real time until it touches something (your gun, hands and body don't count). Slowed: your hands are slowed with the world, as in Boneworks (they trail a fast move and catch up when you stop; what you hold, throw or drop moves at the world's pace). Your head is never slowed, nor the menu laser and your walking direction; a fast swing drags the hand along at most 1.5 m behind. Also on the panel's Controls tab, with `ShowAdvanced` on ("Hands in slow motion") |
| `FireSelector` | on | Semi/auto switching on automatic weapons |
| `WeaponsWhileDriving` | on | Draw your weapon from the driver seat of cars |
| `EnemyWeaponDrops` | on | Fallen soldiers drop their weapon |
| `ModWeaponSounds` | on | Use each mod's own sounds for manual actions (a vanilla weapon's when the mod has none for an action) |
| `ShowAdvanced` | off | The settings panel's advanced tabs (Controls, Gear, View, Speed), read when the game starts |
| `GrenadePins` | on | Grenades need their pin pulled; off makes every throw live |
| `BeltByHand` | on | Lay a machine gun's belt on the feed tray yourself; off lays it when the cover closes |
| `HideGameWeaponHud` | on | Hide the game's weapon panel (name, ammo, slots); the gun, pouch and wrist show ammo |
| `SkipDrawAnimation` | on | Skip the weapon's draw animation in VR (you draw it with your hand) |
| `ReturnRoundsOnMagDrop` | on | Rounds in magazines that get cleared away return to your reserve |
| `MagazineInsertHelp` | Normal | How forgiving the magazine well is: Exact (a quarter less room), Normal, Easy (60% more room beside the well and half as much again in the turn; the magazine is drawn home by itself once a quarter in, no last push). VR settings, Weapons: "Magazine well" |
| `ShellsPerReach` | 2 | Shells one reach into the pouch takes for a tube-fed shotgun; they still go in one at a time (push one in, draw the hand back a little, push the next) |
| `GripLetGoBelow` | 0.3 | (Input) Once a hand holds something, a grip that eases off for a moment (up to 0.15 s) keeps its hold while the squeeze stays above this (0 to 1). A hard push no longer drops and re-takes what you hold. 0 = the controller's own click only |
| `ChamberByHand` | on | A single round into the open chamber by hand: hold your free hand's trigger and squeeze its grip at the pouch for one round, then hold it to the ejection port with the slide caught back or the bolt open. Let the slide go or push the bolt home and the gun holds that one round. Manual reloading only |
| `MagazineReach` | 3 | A dropped magazine of the gun in your hand (with rounds in it) comes to your empty hand from this many metres: point at it and squeeze the grip. 0 = off |
| `MagazineDetent` | on | A magazine's last few millimetres take a firm push or a tap on its base, with a click as it latches; pushed gently it stops just short and waits to be tapped home (not pistols, machine gun boxes or clips) |
| `UnderSeatedMagazineFalls` | off | With the detent: a magazine let go before it clicks home goes in unlatched and drops out at the next shot unless tapped home |
| `AkMagazinesStayIn` | off | On rifles with a curved magazine (AK style) the release only unlatches it; your hand rocks it out |
| `LoadMagazinesByHand` | off | Experimental: rounds without end, loaded by hand. Magazines come from the reserve as usual; once it is out the pouch hands out empty ones, as many as you like: gun put away, a squeeze at the pouch for a magazine, then with it in one hand a squeeze at the pouch with the other for a round and touch it to the magazine's top; a magazine's trigger lets its rounds out one by one; cases and rounds can be picked up and are pushed by bullets |
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
- **The game opens on the monitor only.** A box at the top of the screen says why: with **Try again (F6)** when starting SteamVR or connecting the headset can fix it (no restart needed), with only **OK** when it needs a restart or a reinstall (the VR runtime files missing from `ravenfield_Data`, or a game update the mod cannot hook into yet). For more, check `BepInEx/LogOutput.log` for lines starting with `[Ravenfield VR]`. "Not starting VR: SteamVR is not running" means SteamVR was not open and the game was not started through the launcher's Play in VR. "OpenVR failed to initialise" includes SteamVR's reason (for example `Init_HmdNotFound` when the headset is not streaming).
- **"Unity did not register the 'OpenVR Display' subsystem".** The runtime files under `ravenfield_Data` are missing. Run Setup again; a game update or "Verify integrity of game files" can remove them.
- **Controls do nothing in the headset.** If SteamVR's input could not start for the mod, a note in front of you (and on the monitor) says so and why: restart SteamVR, then the game. Otherwise open the SteamVR binding UI for Ravenfield and select "Ravenfield VR default".
- **Low frame rate, ghosting, things lagging or doubled in front of you.** The frame rate is below the headset's, and SteamVR fills in the missing frames. Look at your wrist: its last line says how many frames of the last few seconds kept the headset's rate and how long the GPU took for each ("90 Hz · 97% on time · GPU 6.2 ms"), so you can switch presets on the settings panel and see the effect. GPU time over the headset's frame time (11.1 ms at 90 Hz) means the preset is too heavy: pick a lower one (Low has no shadows) or a smaller render scale. GPU time well under it with frames still late means the CPU is the limit (the wrist then says "CPU-bound"): reduce the bot count (Ravenfield is CPU-heavy with many bots); the frame rate hold then leaves the render scale alone, which would only blur the view. Once a minute of play the log has a line "Frame rate over the last minute of play: N% of frames at the headset's 90 Hz" with the GPU's median, the eye size and the settings in use, and the CPU's speed against the session's first minute: if the frame rate falls as you play while that speed falls too (under about 80%), the processor is slowing down (heat, or a power plan other than High performance), not the game getting heavier. Under 75% a note in the headset says so once. Keep `PostProcessing` off.
- **A weapon sits wrong in the hand.** Use "Gun pitch" for all guns, or a per-weapon override (above).
- **Windows Defender flags Setup as `Program:Win32/Wacapew.A!ml` (versions before 1.10.1).** A false positive. `!ml` is Defender's machine-learning guess, and **releases up to and including 1.10.0** were built as an unsigned installer **with no version metadata and no icon** (`FileVersion 0.0.0.0`, empty product/company) — exactly what that heuristic flags on a fresh, low-reputation download. **Fixed in 1.10.1 and later:** the setup and launcher are now compiled with full assembly metadata (product, company, version, description) and a product icon, which is what tells those models a file is normal software; on current Defender the rebuilt binaries scan clean. **Use the latest release.** If a build still flags (reputation builds only after a release has been downloaded a few times), allow the file in Windows Security and — the authoritative fix — report it to Microsoft at <https://www.microsoft.com/en-us/wdsi/filesubmission> (pick "Software developer"; usually cleared within a day). A code-signing certificate removes it for good, but costs money and is optional for a free mod.

## Known limitations

- The game's night vision shows in the headset (its green, grain and bloom, while the goggles are on). Image effects a mod puts straight on the game's camera are not copied to the eyes.
  mod attaches to the game's own camera (scripts drawing over its picture) are not copied to the headset camera.
- Third-person vehicle cameras and the death camera follow the game's camera position, turning with it only around the vertical axis.
- Ravenscript mods that draw their own screen-space UI get the same head-locked HUD treatment as the game's HUD.
- Weapons without any animator get their parts by name only; a few of those keep an estimated handle position.
- Materials a mod ships with a shader the graphics card cannot run (magenta) are swapped for the game's own version of that shader, or a plain one, so they may look a little different from what the modder intended.
