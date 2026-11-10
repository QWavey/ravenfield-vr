using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using RavenfieldVR.Core;
using UnityEngine;

namespace RavenfieldVR.Net
{
    /// <summary>
    /// The joining side checked with one game only (after <see cref="NetGameCheck"/>, same run): a host made here (a session of
    /// its own on another UDP port of this PC) is joined by the game as by address. The host starts a match on the map that is
    /// running; measured: the game loads it with no soldiers of its own, the host's soldiers appear as told, stay off the field
    /// when told dead (over one of the game's own spawn waves), the match does not end for want of soldiers, a hit on a stand-in
    /// goes to the host, and the host leaving ends the game here in words.
    /// </summary>
    internal static class NetGameCheckClient
    {
        private const int Port = 7142;

        internal static IEnumerator Run(Action<string> report)
        {
            Actor player = LocalPlayer.actor;
            MapEntryData map = GameManager.instance != null ? GameManager.instance.lastMapEntry : null;
            GameModeParameters parameters = GameManager.instance != null ? GameManager.instance.gameModeParameters : null;
            if (player == null || map == null || parameters == null)
            {
                report("multiplayer joining side: skipped (no player or map)");
                yield break;
            }
            int team = player.team;
            UdpTransport line = null;
            NetSession host = null;
            string failed = null;
            int hits = 0, asked = 0;
            ushort hitVictim = 0;
            bool infoSent = true; // (as if told before the game here had loaded: it has to ask)
            try
            {
                line = new UdpTransport(Port);
                host = NetSession.Host(line, new NetPlayerInfo { Name = "Ghost host", GameBuild = Multiplayer.GameBuild, ModVersion = PluginInfo.Version });
                host.Message += (byte kind, byte sender, ref NetReader r) =>
                {
                    if (kind == NetGame.Hit)
                    {
                        hitVictim = r.UShort();
                        hits++;
                    }
                    else if (kind == NetGame.NeedInfo)
                    {
                        asked++;
                        infoSent = false; // (told again, as NetGame does on this message)
                    }
                };
            }
            catch (Exception e)
            {
                failed = e.Message;
            }
            if (failed != null)
            {
                report("multiplayer joining side: the second game NOT MADE (" + failed + ")");
                yield break;
            }

            // What the host's soldiers do (ids 1..3: two of the other side, one of the player's), told as a real host tells them.
            var w = new NetWriter(512);
            var soldiers = new List<(ushort id, int team, Vector3 at, bool alive)>();
            bool telling = false;
            float nextSend = 0f;
            string weapon = "";
            void Pump()
            {
                host.Update(Time.realtimeSinceStartupAsDouble);
                if (!telling || Time.unscaledTime < nextSend || host.PlayerCount < 2)
                {
                    return;
                }
                nextSend = Time.unscaledTime + 1f / 15f;
                if (!infoSent)
                {
                    infoSent = true;
                    foreach ((ushort id, int _, Vector3 _, bool _) in soldiers)
                    {
                        w.Reset();
                        w.UShort(id);
                        w.String("Host soldier " + id);
                        w.String(weapon);
                        host.Send(NetGame.ActorInfo, w, true);
                    }
                }
                w.Reset();
                w.Byte((byte)soldiers.Count);
                foreach ((ushort id, int side, Vector3 at, bool alive) in soldiers)
                {
                    NetGame.PutState(w, id, (alive ? NetGame.Alive : 0) | (side == 1 ? NetGame.TeamOne : 0), at, Vector3.zero, Vector3.forward);
                }
                host.Send(NetGame.ActorStates, w, false);
            }
            IEnumerator For(float seconds)
            {
                float until = Time.unscaledTime + seconds;
                while (Time.unscaledTime < until)
                {
                    if (Time.timeScale == 0f && LocalPlayer.actor != null && !LocalPlayer.actor.dead)
                    {
                        report("the game's time stood still under the check (" + AutoTest.WhyPaused() + "): set running again, NOT A RESULT OF THE CHECK");
                        GameManager.UnpauseGame();
                    }
                    Pump();
                    yield return null;
                }
            }

            // 1. Joined by address.
            Multiplayer.JoinAddress("127.0.0.1:" + Port);
            float by = Time.unscaledTime + 5f;
            while (Time.unscaledTime < by && !(Multiplayer.Active && Multiplayer.Session.State == NetState.Connected))
            {
                Pump();
                yield return null;
            }
            bool joined = Multiplayer.Active && Multiplayer.Session.State == NetState.Connected;
            report($"multiplayer joining a host by address: {(joined ? $"in as player {Multiplayer.Session.LocalId}" : "NOT JOINED (" + Multiplayer.Status + ")")}");
            if (!joined)
            {
                host.Dispose();
                line.Dispose();
                Multiplayer.Leave();
                yield break;
            }

            // 2. The host starts the match: this game loads the same map, with no soldiers of its own.
            Actor before = player; // (the new match has a new player; the actor manager is the same object in every match)
            string scene = map.isCustomMap ? Path.GetFileName(map.sceneName) : map.sceneName;
            string mode = parameters.gameMode != null ? parameters.gameMode.name : "";
            w.Reset();
            w.Bool(map.isCustomMap);
            w.String(scene);
            w.String(mode);
            w.Bool(false);
            w.Byte((byte)team);
            w.Float(5f);
            w.Byte(1);
            host.Send(NetGame.MatchStart, w, true);
            by = Time.unscaledTime + 90f;
            while (Time.unscaledTime < by && (ActorManager.instance == null || LocalPlayer.actor == null || ReferenceEquals(LocalPlayer.actor, before)))
            {
                Pump();
                yield return null;
            }
            bool loaded = ActorManager.instance != null && LocalPlayer.actor != null && !ReferenceEquals(LocalPlayer.actor, before);
            report($"multiplayer match started by the host ({scene}, {mode}): {(loaded ? "loaded here" : "NOT LOADED")}");
            if (!loaded)
            {
                host.Dispose();
                line.Dispose();
                Multiplayer.Leave();
                yield break;
            }
            yield return For(4f);
            IEnumerator deploy = AutoTest.DeployForCheck();
            while (deploy.MoveNext())
            {
                Pump();
                yield return deploy.Current;
            }
            player = LocalPlayer.actor;
            if (player == null || player.dead)
            {
                report("multiplayer joining side: the player NOT DEPLOYED in the host's match");
                host.Dispose();
                line.Dispose();
                Multiplayer.Leave();
                yield break;
            }
            int own = ActorManager.instance.actors.Count(a => a != null && a.aiControlled && !NetGame.IsRemote(a));
            report($"multiplayer joining side: soldiers of this game's own in the host's match: {own}{(own > 0 ? " NOT NONE" : "")}, the player on team {player.team} (told {team}){(player.team != team ? " NOT THE TOLD TEAM" : "")}");

            // 3. The host's soldiers appear as told.
            Vector3 forward = Vector3.ProjectOnPlane(player.transform.forward, Vector3.up).normalized;
            Vector3 side = Vector3.Cross(Vector3.up, forward);
            Vector3 Ground(Vector3 at) => Physics.Raycast(at + Vector3.up * 4f, Vector3.down, out RaycastHit g, 12f, ~Hitbox.ALL_HITBOX_LAYER_MASK & ~(1 << 2), QueryTriggerInteraction.Ignore) ? g.point : at;
            weapon = WeaponManager.instance.allWeapons.First(e => e != null && e.prefab != null && !e.hidden && e.slot == WeaponManager.WeaponSlot.Primary).name;
            soldiers.Add((1, 1 - team, Ground(player.Position() + forward * 9f - side * 2f), true));
            soldiers.Add((2, 1 - team, Ground(player.Position() + forward * 9f + side * 2f), true));
            soldiers.Add((3, team, Ground(player.Position() + forward * 5f + side * 4f), true));
            telling = true;
            yield return For(2.5f);
            List<NetGame.Remote> standins = NetGame.AllRemotes.Where(r => r.Actor != null && !r.Actor.dead).ToList();
            bool sides = standins.All(r => r.Actor.team == soldiers.First(s => s.id == r.Id).team);
            report($"multiplayer host's soldiers here: {standins.Count} of 3 on the field{(standins.Count != 3 ? " NOT ALL THERE" : "")}{(sides ? "" : " NOT ON THEIR SIDES")}"
                   + $" (told before this game knew them: it asked {asked} time(s){(asked == 0 ? " NOT ASKED" : "")})");

            // 4. One told dead: dead, and still dead after the game's own spawn wave; the match has not ended for want of soldiers.
            soldiers[1] = (2, 1 - team, soldiers[1].at, false);
            float wave = Mathf.Clamp(GameManager.GameParameters().GetRespawnTime(), 1f, 120f);
            yield return For(wave * 2f + 3f);
            NetGame.Remote second = NetGame.AllRemotes.FirstOrDefault(r => r.Id == 2);
            int alive = NetGame.AllRemotes.Count(r => r.Actor != null && !r.Actor.dead);
            own = ActorManager.instance.actors.Count(a => a != null && a.aiControlled && !NetGame.IsRemote(a) && !a.dead);
            report($"multiplayer host's soldier told dead: {(second != null && second.Actor != null && second.Actor.dead ? $"dead, and still after {wave * 2f + 3f:0} s (two spawn waves)" : "NOT DEAD")}; "
                   + $"{alive} stand-in(s) alive (2 told){(alive != 2 ? " NOT AS TOLD" : "")}, {own} soldier(s) of this game's own{(own > 0 ? " NOT NONE" : "")}, the match {(VictoryUi.GameHasEnded() ? "ended NOT RUNNING" : "running")}");

            // 5. A hit by the player on the host's soldier: sent to the host.
            NetGame.Remote first = NetGame.AllRemotes.FirstOrDefault(r => r.Id == 1);
            if (first != null && first.Actor != null && !first.Actor.dead)
            {
                first.Actor.Damage(new DamageInfo(DamageInfo.DamageSourceType.Projectile, player, player.activeWeapon) { healthDamage = 40f, point = first.Actor.CenterPosition(), direction = forward });
                yield return For(0.6f);
                report($"multiplayer hit on the host's soldier: {(hits == 1 && hitVictim == 1 ? "sent to the host" : $"NOT SENT ({hits} message(s), victim {hitVictim})")}");
            }

            // 6. The host leaves: the game here ends in words, his soldiers go.
            host.Leave();
            line.Dispose();
            by = Time.unscaledTime + 4f;
            while (Time.unscaledTime < by && Multiplayer.Active)
            {
                yield return null;
            }
            yield return null;
            report($"multiplayer host left: {(Multiplayer.Active ? "NOT ENDED here" : "ended here (\"" + Multiplayer.Status + "\")")}, stand-ins left {NetGame.RemoteCount}{(NetGame.RemoteCount > 0 ? " NOT REMOVED" : "")}");
            Multiplayer.Leave();
        }
    }
}
