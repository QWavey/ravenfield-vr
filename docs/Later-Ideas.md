# Later ideas

Ideas the user wants built later, written down as they gave them. Not started.

## 1. Multiplayer (the user, 2026-11-09)

> "Ravenm is outdated, we have no multiplayer. please add multiplayer mod where players can just easily join over steam or see lobbys
> over the program (which pulls data from steam) and join to lobbys directly like raven m does, you can analyze ravenm on github etc.
> the only problem with it is that it gets outdated after updates. but our mod should be optimized."

What is wanted:
- Players join each other easily over Steam: invite / join a friend through Steam.
- A lobby browser inside the game (in VR) that pulls the open lobbies from Steam, and joins a lobby directly from that list, as RavenM
  does.
- Built into this mod and kept working: RavenM breaks after game updates, ours must not (or must break as little as possible and be quick
  to fix).

To look at first:
- RavenM's source on GitHub (the Ravenfield multiplayer mod: Steam lobbies, BepInEx). Read how it makes lobbies, lists them, joins, and
  what it syncs (actors, vehicles, weapons, the match), and which of its patches broke on game updates and why.
- Steamworks lobbies (matchmaking: create, list with filters, join, lobby data such as map, mode, mod version) and Steam networking
  (P2P / relay), so no ports or servers are needed.

How ours should differ (the user: "our mod should be optimized"):
- Survive game updates: patch few, stable methods; find members by name with fallbacks rather than by IL position; check the game's
  version at start and turn off only the parts that no longer fit, with a clear message, instead of failing whole.
- A protocol version in the lobby data: players on different mod versions see it in the list and cannot join a game that would desync.
- Send little: deltas and interpolation, the game's own simulation where both sides can run it.
- VR first: sync what VR players do that flat players do not: both hands, held guns and their poses, the reload by hand (magazines,
  racking), grabbing soldiers and objects, thrown things, the body pose from the headset. A flat (non-VR) player should be able to play
  with a VR one.

## 2. A sandbox mode with a spawn gun (the user, 2026-11-09)

> "we want a sandbox mode rn we have point match and some other stuff, do like a sandbox gun like in boneworks to spawn stuff and also
> enemys etc etc and set their behaivior lioke neutral, still standing etc etc many detials like giving them weapons or no weapon like
> loadouts or just random or with nothing etc or friendlys or stay still npcs etc. to test stuff basically."

What is wanted: a mode (beside point match and the game's other modes) to test things freely, with a spawn tool held in the hand like
Boneworks' spawn gun:
- Spawn things: weapons (vanilla and workshop), magazines and ammo, grenades, props, vehicles, and soldiers.
- Soldiers with settings chosen when spawned (and changeable after, by pointing at one):
  - side: enemy, friendly, neutral;
  - behaviour: normal AI, stand still, do not shoot, patrol, follow the player, frozen (no AI at all);
  - weapons: none, a chosen loadout, random, the team's default;
  - health: normal, invulnerable, one hit.
- Remove what was spawned (one, or all), freeze it, and save a set of spawns as a preset to load again.
- Many details, for testing: everything the mod does (reloads, grabs, throws, melee, scopes, vehicles) should be testable here without a
  match going on around it.

Already in the mod to build on: the autotest's soldier bring (`AutoTest.Bring`: respawn at a spot, out of seats, a squad of its own, AI
paused), the showcase's `BringEnemies`, `StandBefore` (a soldier stood in front with his AI paused) and the quieting of nearby soldiers,
`GiveRifle` and `ResupplyAmmo`, the second weapon in the free hand.
