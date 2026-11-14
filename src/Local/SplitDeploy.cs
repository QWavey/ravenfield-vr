using System;
using System.Linq;
using RavenfieldVR.Core;
using UnityEngine;

namespace RavenfieldVR.Local
{
    /// <summary>
    /// A splitscreen copy's player deploys, picks where and switches sides by keys: its window has no pointer (the host's copy has it), so
    /// the game's deploy screen and map clicks are keys here (the user, 2026-11-14, playing player 2's copy: "cant walk ... can also not shoot
    /// or reload or switch weapons, only look around and i dont take damage" (the deploy key hid the screen, but the game spawns a player only
    /// at a selected spawn point its team owns, so player 2 stayed down, looking around as the dead do); "add a key to switch teams in my
    /// copy").
    /// <list type="bullet">
    /// <item>down: fire, jump or A deploys at the selected point of its team (picked if none is); its next-weapon key (Q, Y) picks the next
    /// one; with no spawn wave taking it in 4 s it is put there as the wave would (as the autotest's deploy does);</item>
    /// <item>T or the D-pad's right: the other side (its soldier down first if alive, then the deploy screen of the new side).</item>
    /// </list>
    /// </summary>
    internal static class SplitDeploy
    {
        private static float _deployedAt = -1f;
        private static string _line = "";
        private static GUIStyle _style;

        public static void Update(Action<string> note)
        {
            Actor me = LocalPlayer.actor;
            if (me == null || GameManager.instance == null)
            {
                return;
            }
            if (FlatPlayerInput.WentDown(FlatPlayerInput.SwitchTeam))
            {
                SwitchTeam(me, note);
                return;
            }
            if (!me.dead)
            {
                _deployedAt = -1f;
                _line = "";
                return;
            }
            SpawnPoint at = OwnSelected();
            if (FlatPlayerInput.WentDown(SteelInput.KeyBinds.NextWeapon))
            {
                at = Next(at);
            }
            if (at == null)
            {
                at = Next(null);
            }
            _line = at == null ? "Your team holds no flag: wait for the next wave.   T: switch team"
                : _deployedAt > 0f ? $"Deploying at {at.shortName} ...   T: switch team"
                : $"Fire / Space / A: deploy at {at.shortName}    Q / Y: next flag    T / D-pad right: switch team";
            if (_deployedAt < 0f && at != null && (FlatPlayerInput.WentDown(SteelInput.KeyBinds.Fire) || FlatPlayerInput.WentDown(SteelInput.KeyBinds.Jump)))
            {
                if (LoadoutUi.instance != null && !LoadoutUi.IsOpen() && !LoadoutUi.HasBeenOpen())
                {
                    LoadoutUi.Show(false); // (never opened: the game spawns no one before it has been)
                }
                if (LoadoutUi.instance != null && LoadoutUi.IsOpen())
                {
                    LoadoutUi.instance.OnDeployClick();
                }
                _deployedAt = Time.unscaledTime;
                note($"Splitscreen: deploying at {at.shortName} (team {me.team}).");
            }
            else if (_deployedAt > 0f && Time.unscaledTime - _deployedAt > 4f && at != null)
            {
                // No spawn wave took the player: put at the point as the wave does (GameModeBase.SpawnActorListDeferred).
                if (LoadoutUi.instance != null && LoadoutUi.IsOpen())
                {
                    LoadoutUi.Hide();
                }
                me.SpawnAt(at.GetSpawnPosition(), Quaternion.identity);
                _deployedAt = -1f;
                note($"Splitscreen: put at {at.shortName} (no spawn wave took the player in 4 s).");
            }
        }

        /// <summary>The map's selected spawn point when the player's team owns it, else none.</summary>
        private static SpawnPoint OwnSelected()
        {
            SpawnPoint s = MinimapUi.instance != null ? MinimapUi.instance.selectedSpawnPoint : null;
            return s != null && s.owner == GameManager.PlayerTeam() ? s : null;
        }

        /// <summary>The team's next spawn point after this one (the first when none), selected on the map.</summary>
        private static SpawnPoint Next(SpawnPoint after)
        {
            SpawnPoint[] all = ActorManager.instance != null ? ActorManager.instance.spawnPoints : null;
            if (all == null)
            {
                return null;
            }
            int team = GameManager.PlayerTeam();
            var own = all.Where(s => s != null && s.owner == team).ToList();
            if (own.Count == 0)
            {
                return null;
            }
            int i = after != null ? own.IndexOf(after) : -1;
            SpawnPoint next = own[(i + 1) % own.Count];
            MinimapUi.SelectSpawnPoint(next);
            return next;
        }

        /// <summary>The other side: the player's team and its soldier's (with its hitboxes, as the multiplayer's stand-ins), then deploy.</summary>
        private static void SwitchTeam(Actor me, Action<string> note)
        {
            int team = 1 - Mathf.Clamp(GameManager.PlayerTeam(), 0, 1);
            if (!me.dead)
            {
                me.KillSilently();
            }
            GameManager.instance.gameModeParameters.playerTeam = team;
            me.SetTeam(team);
            foreach (Hitbox h in me.GetComponentsInChildren<Hitbox>(true))
            {
                h.team = team;
            }
            _deployedAt = -1f;
            Next(null);
            note($"Splitscreen: this player switched to team {team} ({(team == 0 ? "Eagles" : "Ravens")}).");
        }

        /// <summary>What to press, over the bottom of the copy's screen while down.</summary>
        public static void Draw()
        {
            if (string.IsNullOrEmpty(_line) || LocalPlayer.actor == null || !LocalPlayer.actor.dead)
            {
                return;
            }
            if (_style == null)
            {
                _style = new GUIStyle(GUI.skin.label) { fontSize = 20, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
                _style.normal.textColor = Color.white;
            }
            var r = new Rect(0f, Screen.height - 90f, Screen.width, 40f);
            GUI.color = new Color(0f, 0f, 0f, 0.55f);
            GUI.DrawTexture(r, Texture2D.whiteTexture);
            GUI.color = Color.white;
            GUI.Label(r, _line, _style);
        }
    }
}
