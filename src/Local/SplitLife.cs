using System;
using RavenfieldVR.Core;
using RavenfieldVR.Util;
using UnityEngine;

namespace RavenfieldVR.Local
{
    /// <summary>
    /// A splitscreen player's life in the match beyond moving and shooting (the user, 2026-11-14, playing player 2: "I died and cant
    /// respawn ... please add more features for the other players and improve it for the other players"):
    /// <list type="bullet">
    /// <item>respawn by its own button a moment after dying, beside the game's own player or at one of its team's flags (chosen with its
    /// next-weapon button while down), instead of waiting for the game's wave;</item>
    /// <item>a hit marker when its shots hit an enemy (larger for a kill), and its kills counted;</item>
    /// <item>a red flash at the edge of its view on the side a hit came from;</item>
    /// <item>its display: a health bar, the weapon's slots, rounds, kills and deaths, and what to press while down.</item>
    /// </list>
    /// </summary>
    internal static class SplitLife
    {
        public const float RespawnDelay = 2.5f;

        /// <summary>Once a frame while down: its next-weapon button picks where, its fire or jump brings it back.</summary>
        public static void WhileDown(SplitPlayer p)
        {
            if (p.NextWeapon)
            {
                p.SpawnAtFlag = !p.SpawnAtFlag;
            }
            if (Time.time - p.DiedAt >= RespawnDelay && (p.FirePressed || p.Jump))
            {
                Respawn(p);
            }
        }

        private static void Respawn(SplitPlayer p)
        {
            Actor a = p.Actor;
            Actor me = GameContext.Player != null ? GameContext.Player.actor : null;
            Vector3 at;
            float yaw = p.Yaw;
            string where;
            SpawnPoint flag = p.SpawnAtFlag || me == null || me.dead ? NearestFlag(a.team, me != null ? me.Position() : a.Position()) : null;
            if (flag != null)
            {
                at = flag.GetSpawnPosition();
                where = "at " + flag.shortName;
            }
            else if (me != null && !me.dead)
            {
                at = SplitPlayers.GroundBeside(me.Position(), yaw, p.Index, out where);
                where = "beside player 1, " + where;
            }
            else
            {
                return; // (no flag of its team and the game's own player down too: the game's wave brings it back)
            }
            try
            {
                a.SpawnAt(at, Quaternion.Euler(0f, yaw, 0f), a.controller.GetLoadout());
                p.Crouch = p.Sprint = false;
                Log.Info($"Splitscreen: player {p.Index + 1} back {where}.");
            }
            catch (Exception e)
            {
                Log.WarnOnce("split-respawn", $"Splitscreen: player {p.Index + 1} could not be brought back ({e.GetType().Name}: {e.Message}).");
            }
        }

        /// <summary>The team's flag nearest to a place (null: it holds none).</summary>
        private static SpawnPoint NearestFlag(int team, Vector3 near)
        {
            SpawnPoint best = null;
            float bestDist = float.MaxValue;
            SpawnPoint[] all = ActorManager.instance != null ? ActorManager.instance.spawnPoints : null;
            if (all == null)
            {
                return null;
            }
            foreach (SpawnPoint s in all)
            {
                if (s == null || s.owner != team)
                {
                    continue;
                }
                float d = (s.transform.position - near).sqrMagnitude;
                if (d < bestDist)
                {
                    bestDist = d;
                    best = s;
                }
            }
            return best;
        }

        /// <summary>
        /// After any soldier takes damage: a splitscreen player's hit on an enemy shows its marker (a kill a larger one, counted), and a
        /// splitscreen player hit flashes its view's edge on the side the hit came from.
        /// </summary>
        public static void OnDamage(Actor victim, DamageInfo info, bool wasAlive = true)
        {
            if (victim == null || info.healthDamage <= 0f)
            {
                return;
            }
            float now = Time.time;
            SplitPlayer shooter = info.sourceActor != null ? SplitPlayers.PlayerOf(info.sourceActor) : null;
            if (shooter != null && info.sourceActor != victim && victim.team != info.sourceActor.team)
            {
                if (victim.dead && !wasAlive)
                {
                    return; // (a hit on one already down: no second kill counted, splash or another's shot)
                }
                if (victim.dead)
                {
                    shooter.Kills++;
                    shooter.KillMark = true;
                    shooter.HitMarkUntil = now + 0.45f;
                }
                else
                {
                    shooter.KillMark = false;
                    shooter.HitMarkUntil = now + 0.15f;
                }
            }
            SplitPlayer hit = SplitPlayers.PlayerOf(victim);
            if (hit != null)
            {
                hit.HurtUntil = now + 0.6f;
                hit.HurtFrom = info.direction.sqrMagnitude > 1e-6f ? -info.direction.normalized : Vector3.zero;
            }
        }

        // ---- the display ------------------------------------------------------------------------------------------------------

        private static GUIStyle _big, _small, _center, _killMark, _hitMark, _bigRight, _smallRight;

        private static void Styles()
        {
            if (_big != null)
            {
                return;
            }
            _big = new GUIStyle(GUI.skin.label) { fontSize = 22, fontStyle = FontStyle.Bold };
            _small = new GUIStyle(GUI.skin.label) { fontSize = 14 };
            _center = new GUIStyle(GUI.skin.label) { fontSize = 20, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
            // (Made once with the rest: new ones for each player each frame were garbage the whole match; split scout, 2026-11-25.)
            _killMark = new GUIStyle(_center) { fontSize = 30 };
            _hitMark = new GUIStyle(_center) { fontSize = 22 };
            _bigRight = new GUIStyle(_big) { alignment = TextAnchor.MiddleRight };
            _smallRight = new GUIStyle(_small) { alignment = TextAnchor.MiddleRight };
        }

        private static void Box(Rect r, Color c)
        {
            Color was = GUI.color;
            GUI.color = c;
            GUI.DrawTexture(r, Texture2D.whiteTexture);
            GUI.color = was;
        }

        /// <summary>One player's display in its part of the window (r: in GUI coordinates, from the top).</summary>
        public static void Draw(SplitPlayer p, Rect r)
        {
            Styles();
            Actor a = p.Actor;
            float now = Time.time;
            // The hurt flash: the edge the hit came from, fading.
            if (now < p.HurtUntil && p.Cam != null)
            {
                float k = Mathf.Clamp01((p.HurtUntil - now) / 0.6f) * 0.45f;
                Vector3 local = p.HurtFrom.sqrMagnitude > 0f ? p.Cam.transform.InverseTransformDirection(p.HurtFrom) : Vector3.zero;
                float t = Mathf.Min(r.width, r.height) * 0.06f;
                var red = new Color(0.85f, 0.05f, 0.05f, k);
                var faint = new Color(0.85f, 0.05f, 0.05f, k * 0.35f);
                Box(new Rect(r.x, r.y, t, r.height), local.x < -0.3f ? red : faint);
                Box(new Rect(r.xMax - t, r.y, t, r.height), local.x > 0.3f ? red : faint);
                Box(new Rect(r.x, r.y, r.width, t), local.z > 0.3f ? red : faint);
                Box(new Rect(r.x, r.yMax - t, r.width, t), local.z < -0.3f ? red : faint);
            }
            GUI.color = Color.white;
            string name = $"Player {p.Index + 1}";
            if (a.dead || a.fallenOver)
            {
                string line = a.fallenOver && !a.dead ? $"{name}: knocked over, getting up..."
                    : now - p.DiedAt < RespawnDelay ? $"{name}: down"
                    : $"{name}: down.   Fire / Space / A: back in   ({(p.SpawnAtFlag ? "at the nearest flag" : "beside player 1")}; Q / Y to change)";
                GUI.Label(new Rect(r.x + 20f, r.center.y - 14f, r.width - 40f, 30f), line, _small);
                Stats(p, r);
                return;
            }
            // The crosshair, and the hit marker over it.
            GUI.Label(new Rect(r.center.x - 10f, r.center.y - 14f, 20f, 28f), "+", _center);
            if (now < p.HitMarkUntil)
            {
                GUI.color = p.KillMark ? new Color(1f, 0.25f, 0.2f) : Color.white;
                float s = p.KillMark ? 34f : 24f;
                GUI.Label(new Rect(r.center.x - s, r.center.y - s, s * 2f, s * 2f), "X", p.KillMark ? _killMark : _hitMark);
                GUI.color = Color.white;
            }
            if (p.KeysPair >= 0 && Runtime.OpenVRRuntime.Running)
            {
                GUI.Label(new Rect(r.x + 20f, r.center.y + 24f, r.width - 40f, 40f), $"{name}: connect a controller (number {SplitPlayers.PadOf(p) + 1}).", _small);
            }
            // Health: a bar, green to red.
            float hp = Mathf.Clamp01(a.health / Mathf.Max(1f, a.maxHealth));
            var bar = new Rect(r.x + 16f, r.yMax - 64f, Mathf.Min(220f, r.width * 0.35f), 12f);
            Box(bar, new Color(0f, 0f, 0f, 0.45f));
            Box(new Rect(bar.x, bar.y, bar.width * hp, bar.height), Color.Lerp(new Color(0.9f, 0.15f, 0.1f), new Color(0.25f, 0.85f, 0.3f), hp));
            GUI.Label(new Rect(bar.xMax + 8f, bar.y - 5f, 120f, 22f), $"{Mathf.CeilToInt(a.health)}", _small);
            // The weapon, its rounds and its slots.
            Weapon w = a.activeWeapon;
            if (w != null)
            {
                string rounds = w.reloading ? "reloading" : w.ammo < 0 ? "inf" : $"{w.ammo}";
                string spare = w.spareAmmo < 0 ? "" : $" / {w.spareAmmo}";
                GUI.Label(new Rect(r.xMax - 236f, r.yMax - 72f, 220f, 30f), rounds + spare, _bigRight);
                GUI.Label(new Rect(r.xMax - 336f, r.yMax - 44f, 320f, 22f), w.name.Replace("(Clone)", "").Trim(), _smallRight);
            }
            if (a.weapons != null)
            {
                var slots = new System.Text.StringBuilder();
                for (int i = 0; i < a.weapons.Length; i++)
                {
                    if (a.weapons[i] != null)
                    {
                        slots.Append(i == a.activeWeaponSlot ? $"[{i + 1}] " : $" {i + 1}  ");
                    }
                }
                GUI.Label(new Rect(r.x + 16f, r.yMax - 44f, 260f, 22f), slots.ToString(), _small);
            }
            Stats(p, r);
        }

        private static void Stats(SplitPlayer p, Rect r)
        {
            GUI.Label(new Rect(r.x + 16f, r.y + 10f, 300f, 22f), $"Player {p.Index + 1}    Kills {p.Kills}   Deaths {p.Deaths}", _small);
        }
    }
}
