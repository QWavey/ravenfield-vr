using System;
using RavenfieldVR.Core;
using RavenfieldVR.Util;
using Steamworks;
using UnityEngine;

namespace RavenfieldVR.Net
{
    /// <summary>
    /// What the player is doing, shown to Steam friends (rich presence "status"): VR or flat, alone, hosting or joined with how many players,
    /// in the menu or in a match (the map, the mode, which side leads and by how much), and the player's own state (waiting to deploy,
    /// in a vehicle, the weapon in hand). The user, 2026-11-14: "Steam needs to show that Im playing Ravenfield Multiplayer, Vr ... like very
    /// detailled and many statusses". Built every 3 seconds and sent only when it changed.
    /// Steam shows "status" in a friend's game info and chat; the friends list's one line uses tokens only the game's developer can define.
    /// </summary>
    internal static class SteamStatus
    {
        private static float _next;
        private static string _sent;
        private static bool _checked;

        /// <summary>The status last sent (self-test).</summary>
        internal static string Last => _sent;

        public static void Update()
        {
            if (Time.unscaledTime < _next)
            {
                return;
            }
            _next = Time.unscaledTime + 3f;
            if (!SteamLobbies.Available)
            {
                return;
            }
            string status;
            try
            {
                status = Build();
            }
            catch (Exception e)
            {
                Log.WarnOnce("steamstatus", "Steam status not made: " + e.Message);
                return;
            }
            if (status == _sent)
            {
                return;
            }
            try
            {
                SteamFriends.SetRichPresence("status", status);
                _sent = status;
                if (!_checked)
                {
                    _checked = true; // (once: what Steam holds now, read back, for the log and the checks)
                    string back = SteamFriends.GetFriendRichPresence(SteamUser.GetSteamID(), "status");
                    Log.Info($"Steam status shown to friends: \"{status}\" (Steam holds \"{back}\").");
                }
            }
            catch (Exception e)
            {
                Log.WarnOnce("steamstatus-set", "Steam status not sent: " + e.Message);
            }
        }

        /// <summary>The status taken off Steam (the game closes or VR stops nothing: the game's own presence is left).</summary>
        public static void Clear()
        {
            if (_sent == null || !SteamLobbies.Available)
            {
                return;
            }
            try
            {
                SteamFriends.ClearRichPresence();
            }
            catch (Exception)
            {
            }
            _sent = null;
        }

        internal static string Build()
        {
            string how = Runtime.OpenVRRuntime.Running ? "VR" : "Flat";
            string company = "";
            if (Multiplayer.Active)
            {
                int n = Multiplayer.Session.PlayerCount;
                company = Multiplayer.Hosting ? $"hosting multiplayer ({n} player{(n == 1 ? "" : "s")})" : $"in a multiplayer game ({n} players)";
            }
            if (Local.SplitPlayers.Running)
            {
                int n = Local.SplitPlayers.All.Count + 1;
                company = (company.Length > 0 ? company + ", " : "") + $"splitscreen with {n} players at one PC";
            }
            if (!GameContext.InGame)
            {
                return $"{how} · {(company.Length > 0 ? company + " · " : "")}in the main menu";
            }
            string where = MapAndMode();
            string score = Score();
            string me = Myself();
            string text = $"{how} · {(company.Length > 0 ? company : "playing alone")} · {where}";
            if (score.Length > 0)
            {
                text += " · " + score;
            }
            if (me.Length > 0)
            {
                text += " · " + me;
            }
            return text.Length > 250 ? text.Substring(0, 250) : text;
        }

        private static string MapAndMode()
        {
            GameManager gm = GameManager.instance;
            string map = "";
            if (gm != null && gm.lastMapEntry != null)
            {
                map = !string.IsNullOrEmpty(gm.lastMapEntry.metaData.displayName) ? gm.lastMapEntry.metaData.displayName
                    : System.IO.Path.GetFileNameWithoutExtension(gm.lastMapEntry.sceneName ?? "");
            }
            string mode = gm != null && gm.gameModeParameters != null && gm.gameModeParameters.gameMode != null ? gm.gameModeParameters.gameMode.name : "";
            mode = mode.Replace("(Clone)", "").Trim();
            return map.Length > 0 ? $"{mode} on {map}".Trim() : mode.Length > 0 ? mode : "in a match";
        }

        private static readonly string[] Sides = { "Eagles", "Ravens" };

        private static string Score()
        {
            if (GameManager.gameOver)
            {
                return "the match is over";
            }
            int a, b;
            string unit;
            if (GameModeBase.activeGameMode is BattleMode battle && battle.tickets != null && battle.tickets.Length >= 2)
            {
                a = battle.tickets[0];
                b = battle.tickets[1];
                unit = "";
            }
            else if (ActorManager.instance != null && ActorManager.instance.spawnPoints != null)
            {
                a = b = 0;
                foreach (SpawnPoint p in ActorManager.instance.spawnPoints)
                {
                    if (p == null)
                    {
                        continue;
                    }
                    a += p.owner == 0 ? 1 : 0;
                    b += p.owner == 1 ? 1 : 0;
                }
                unit = " flags";
            }
            else
            {
                return "";
            }
            if (a == b)
            {
                return $"even ({a} to {b}{unit})";
            }
            return a > b ? $"{Sides[0]} winning ({a} to {b}{unit})" : $"{Sides[1]} winning ({b} to {a}{unit})";
        }

        private static string Myself()
        {
            Actor me = GameContext.Player != null ? GameContext.Player.actor : null;
            if (me == null)
            {
                return "";
            }
            string side = me.team == 0 || me.team == 1 ? $"for the {Sides[me.team]}" : "";
            if (me.dead)
            {
                return $"{side}, waiting to deploy".Trim(' ', ',');
            }
            if (me.IsSeated() && me.seat != null && me.seat.vehicle != null)
            {
                string vehicle = me.seat.vehicle.name.Replace("(Clone)", "").Trim();
                return $"{side}, {(me.seat.IsDriverSeat() ? "driving" : "riding in")} a {vehicle}".Trim(' ', ',');
            }
            string gun = me.activeWeapon != null ? me.activeWeapon.name.Replace("(Clone)", "").Trim() : "";
            return gun.Length > 0 ? $"{side}, with a {gun}".Trim(' ', ',') : side;
        }
    }
}
