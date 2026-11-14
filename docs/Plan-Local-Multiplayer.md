# Plan: Local play, splitscreen, Steam statuses, flat multiplayer (Tasks 395)

The user's request (2026-11-14 night, during the work-until-limit 85 run), sorted into an order where each step stands on the one before.

## 1. Steam statuses (rich presence)

Friends see what the player is doing, in detail: main menu, in the loadout, in a match (map, mode, which side is winning and by how much),
in a vehicle, dead and waiting to deploy, in multiplayer (hosting / joined, how many players), in VR or flat, in splitscreen locally.

- `SteamFriends.SetRichPresence("status", ...)` through Steamworks.NET (the game's own `GameManager.steamworks`), updated a few times a minute
  and on every change (not every frame).
- `steam_player_group` / `steam_player_group_size` while in a multiplayer match, so friends see the party together.
- Limit: Steam shows `status` in the friends list only for games that define rich-presence tokens. Ravenfield's developer defines those, not
  a mod. The `status` string is still visible in the friends list's game info and in Steam chat on most clients. The plan sets it in every
  state and checks it in a test against `SteamFriends.GetFriendRichPresence(own id, "status")`.

## 2. The multiplayer menu: two choices

- Multiplayer → **Local** or **Online** ("over the internet" said shorter).
- Local → **LAN** (players on the same network) or **Splitscreen** (players at this PC).
- Online → **IP** (by address) or **Steam** (lobbies, friends).
- In the VR panel and in the flat window (F8) alike.

## 3. LAN

- The host announces itself on the local network: a small UDP broadcast every second on a fixed port, with the game's name, the map, players
  and the version.
- The LAN list shows hosts heard in the last few seconds; picking one joins it by its address (the existing UDP join).
- Test: a host and a joiner on one PC through the broadcast (loopback), the list finds the host, the join works.

## 4. Flat players (no VR)

- A player without a headset can host and join (the flat window, F8, exists). Checked end to end: the netcheck has a flat mode
  (`vrshots --netcheck-flat`).
- Delivered as its own package at the end: "Ravenfield Multiplayer", the same mod with VR off by default, for friends without a headset.

## 5. Splitscreen, 2 to 4 players at one PC

- Players 2 to 4 are soldiers of their own steered by their own input, as the other PC's players are today (the multiplayer's stand-ins: an
  AI soldier the mod drives with a facing, a move, fire and reload). The game itself knows only one player.
- Each gets a camera at its soldier's eyes, with its own part of the screen:
  - 2 players: side by side or one above the other (a setting);
  - 3 or 4 players: quarters.
- Input per player:
  - a controller: Unity's joysticks by number;
  - keyboard and mouse: the first is the game's own. A second keyboard and mouse needs Windows Raw Input to tell the devices apart (the game
    sees one merged mouse and keyboard). Written as its own reader: WM_INPUT through a message hook.
  - Any mix: one on keyboard and mouse, others on controllers.
- A small HUD per player: health, ammo, the weapon's name, a crosshair.
- Spawning, death and respawn through the game's own spawn waves, as the multiplayer's stand-ins do.
- Tests: an automated check with two and four simulated players (input fed by the check), their cameras and viewports, moving, firing,
  dying and respawning. Then pictures of the split screen.

## Order of work

1, then 2, then 3 (small and testable alone), then 4's check, then 5 (the largest), and 4's package at the end with everything in it.
