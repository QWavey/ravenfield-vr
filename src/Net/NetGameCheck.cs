using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using RavenfieldVR.Core;
using UnityEngine;

namespace RavenfieldVR.Net
{
    /// <summary>
    /// The multiplayer checked in a running match with one game only (-rfvr-netcheck with the autotest; tools/vrshots.py --netcheck):
    /// the game hosts on a UDP port, and a second player made here (a session of its own on another port of this PC, sending what
    /// a real one sends) joins it. Measured: the join, his soldier standing in the match and following what he tells, a hit on
    /// him passed to his PC and not taken here, a hit from him taken by the player, his death and return, the host's soldiers told
    /// to him, his leaving. A failure is worded in capitals (NOT ...), the autotest counts those.
    /// </summary>
    internal static class NetGameCheck
    {
        private const int Port = 7141;

        internal static IEnumerator Run(Action<string> report)
        {
            Actor player = LocalPlayer.actor;
            if (player == null || player.dead || ActorManager.instance == null)
            {
                report("multiplayer: skipped (no player on the field)");
                yield break;
            }
            if (Time.timeScale == 0f)
            {
                report("the game's time stood still as the check began (" + Core.AutoTest.WhyPaused() + "): set running again, NOT A RESULT OF THE CHECK");
                GameManager.UnpauseGame();
            }
            NetGame.Init();
            Multiplayer.HostLan(Port);
            if (!Multiplayer.Hosting)
            {
                report("multiplayer host on UDP: NOT HOSTING (" + Multiplayer.Status + ")");
                yield break;
            }

            UdpTransport line = null;
            NetSession ghost = null;
            int hitsGot = 0, statesGot = 0, othersHits = 0;
            float hitDamage = 0f;
            var told = new HashSet<ushort>();
            string failed = null;
            try
            {
                line = new UdpTransport(0);
                ghost = NetSession.Join(line, UdpTransport.Parse("127.0.0.1", Port), new NetPlayerInfo
                {
                    Name = "Ghost", GameBuild = Multiplayer.GameBuild, ModVersion = PluginInfo.Version, Vr = false
                });
                ghost.Message += (byte kind, byte sender, ref NetReader r) =>
                {
                    if (kind == NetGame.Hit)
                    {
                        // (The host's own soldiers shoot the stand-in too, and each of their hits is passed on as well: 6 messages
                        // in the first run with the match running. The player's is the one from soldier 0 with the check's damage.)
                        r.UShort();
                        ushort shooter = r.UShort();
                        float damage = r.Float();
                        othersHits++;
                        if (shooter == 0 && Mathf.Abs(damage - 30f) < 0.01f)
                        {
                            hitDamage = damage;
                            hitsGot++;
                            othersHits--;
                        }
                    }
                    else if (kind == NetGame.ActorStates)
                    {
                        int n = r.Byte();
                        for (int i = 0; i < n; i++)
                        {
                            told.Add(r.UShort());
                            r.Byte();
                            r.Vector();
                            r.UShort();
                            r.UShort();
                        }
                        statesGot++;
                    }
                };
            }
            catch (Exception e)
            {
                failed = e.Message;
            }
            if (failed != null)
            {
                report("multiplayer second player: NOT MADE (" + failed + ")");
                Multiplayer.Leave();
                yield break;
            }

            var w = new NetWriter(256);
            // What the ghost tells of himself (the same bytes a real player's game writes).
            Vector3 forward = Vector3.ProjectOnPlane(player.transform.forward, Vector3.up).normalized;
            Vector3 side = Vector3.Cross(Vector3.up, forward);
            Vector3 start = player.Position() + forward * 7f;
            if (Physics.Raycast(start + Vector3.up * 4f, Vector3.down, out RaycastHit ground, 12f, ~Hitbox.ALL_HITBOX_LAYER_MASK & ~(1 << 2), QueryTriggerInteraction.Ignore))
            {
                start = ground.point;
            }
            int enemyTeam = 1 - player.team;
            string weapon = player.activeWeapon != null && player.activeWeapon.weaponEntry != null ? player.activeWeapon.weaponEntry.name
                : WeaponManager.instance.allWeapons.First(e => e != null && e.prefab != null && !e.hidden && e.slot == WeaponManager.WeaponSlot.Primary).name;
            bool ghostAlive = true;
            Vector3 ghostAt = start, ghostVelocity = Vector3.zero;
            float nextSend = 0f;
            bool infoSent = false;
            void Pump()
            {
                ghost.Update(Time.realtimeSinceStartupAsDouble);
                if (ghost.State != NetState.Connected || Time.unscaledTime < nextSend)
                {
                    return;
                }
                nextSend = Time.unscaledTime + 1f / 15f;
                if (!infoSent)
                {
                    infoSent = true;
                    w.Reset();
                    w.UShort(0);
                    w.String("Ghost");
                    w.String(weapon);
                    ghost.Send(NetGame.ActorInfo, w, true);
                }
                w.Reset();
                w.Byte(1);
                NetGame.PutState(w, 0, (ghostAlive ? NetGame.Alive : 0) | (enemyTeam == 1 ? NetGame.TeamOne : 0), ghostAt, ghostVelocity, -forward);
                ghost.Send(NetGame.ActorStates, w, false);
            }
            IEnumerator For(float seconds, Action each = null)
            {
                float until = Time.unscaledTime + seconds;
                while (Time.unscaledTime < until)
                {
                    if (Time.timeScale == 0f)
                    {
                        report("the game's time stood still under the check (" + Core.AutoTest.WhyPaused() + "): set running again, NOT A RESULT OF THE CHECK");
                        GameManager.UnpauseGame();
                    }
                    each?.Invoke();
                    Pump();
                    yield return null;
                }
            }
            NetGame.Remote Standin() => NetGame.AllRemotes.FirstOrDefault(r => r.Id == 0);

            // 1. The join.
            float joinBy = Time.unscaledTime + 5f;
            while (ghost.State == NetState.Connecting && Time.unscaledTime < joinBy)
            {
                Pump();
                yield return null;
            }
            report($"multiplayer join over UDP: {(ghost.State == NetState.Connected ? $"joined as player {ghost.LocalId}, {Multiplayer.Session.PlayerCount} in the game" : "NOT JOINED (" + (ghost.CloseReason ?? "no answer") + ")")}");
            if (ghost.State != NetState.Connected)
            {
                ghost.Dispose();
                line.Dispose();
                Multiplayer.Leave();
                yield break;
            }

            // (A picture of the panel with the two players in it, to look its layout over: reports/shots/netcheck.)
            UI.MultiplayerPanel panel = UI.UISystem.Instance != null ? UI.UISystem.Instance.MultiplayerPanel : null;
            if (panel != null)
            {
                panel.Open();
                yield return For(0.5f);
                yield return new WaitForEndOfFrame();
                Core.Shots.Capture("multiplayer-panel-hosting");
                panel.Close();
            }

            // 2. His soldier here: made, of his side, with his weapon; then walking where he says (2 m/s sideways for 4 s).
            yield return For(1.5f);
            NetGame.Remote remote = Standin();
            Actor body = remote != null ? remote.Actor : null;
            string carried = body != null && body.activeWeapon != null && body.activeWeapon.weaponEntry != null ? body.activeWeapon.weaponEntry.name : "nothing";
            report($"multiplayer stand-in: {(body != null && !body.dead ? $"on the field, team {body.team} (told {enemyTeam}), carrying {carried} (told {weapon})" : "NOT ON THE FIELD")}"
                   + (body != null && body.team != enemyTeam ? " NOT HIS SIDE" : "") + (body != null && !body.dead && carried != weapon ? " NOT HIS WEAPON" : "")
                   + (remote != null && remote.Tag != null && remote.Tag.gameObject.activeSelf && remote.Tag.text == "Ghost" ? ", his name over him" : " HIS NAME NOT SHOWN"));
            if (body != null)
            {
                yield return new WaitForEndOfFrame();
                Core.Shots.Capture("multiplayer-other-player");
            }
            if (body == null)
            {
                ghost.Dispose();
                line.Dispose();
                Multiplayer.Leave();
                yield break;
            }
            ghostVelocity = side * 2f;
            float worst = 0f;
            bool knocked = false;
            int askedBefore = NetGame.VelocityAsked;
            float fastest = 0f;
            float walkStart = Time.unscaledTime;
            yield return For(4f, () =>
            {
                ghostAt = start + side * (2f * (Time.unscaledTime - walkStart));
                knocked |= body.fallenOver;
                if (Time.unscaledTime - walkStart > 1f && !knocked)
                {
                    Vector3 off = body.Position() - ghostAt;
                    off.y = 0f;
                    worst = Mathf.Max(worst, off.magnitude);
                    fastest = Mathf.Max(fastest, body.Velocity().magnitude);
                }
            });
            ghostVelocity = Vector3.zero;
            // (Knocked over on the way by the match's own soldiers, a blast or a blow: he lies 2.5 s, then stands where told.)
            yield return For(knocked ? 5f : 1f);
            Vector3 rest = body.Position() - ghostAt;
            rest.y = 0f;
            report($"multiplayer stand-in walking 8 m as told: {(knocked ? "knocked over on the way by the match (lies, then stands where told), before that " : "")}at most {worst:0.00} m behind{(worst > 1.5f ? " NOT FOLLOWING" : "")}, {rest.magnitude:0.00} m off at rest{(rest.magnitude > 0.5f ? " NOT THERE" : "")}"
                   + $" (his speed asked {NetGame.VelocityAsked - askedBefore} time(s), answered up to {fastest:0.0} m/s, last {NetGame.LastVelocity.magnitude:0.0}; moving {body.moving}, fallen {body.fallenOver}, frozen {body.isFrozen}, auto-move {body.autoMoveActor})");

            // 3. A hit on him by the player: passed to his PC, not taken here.
            float bodyHealth = body.health;
            body.Damage(new DamageInfo(DamageInfo.DamageSourceType.Projectile, player, player.activeWeapon) { healthDamage = 30f, point = body.CenterPosition(), direction = forward });
            yield return For(0.6f);
            report($"multiplayer hit on the stand-in: {(hitsGot == 1 ? $"passed to his PC ({hitDamage:0} damage)" : $"NOT PASSED ({hitsGot} message(s))")}, {othersHits} more by the host's soldiers, health here {bodyHealth:0} -> {body.health:0}{(body.health < bodyHealth - 0.1f ? " NOT LEFT TO HIS PC" : "")}");

            // 4. A hit by him on the player: taken.
            bool wasInvulnerable = player.isInvulnerable;
            player.isInvulnerable = false;
            Core.AutoTest.PlayerCanBeHurt = true; // (the autotest's own shield off for this one hit)
            float before = player.health;
            w.Reset();
            w.UShort(0);
            w.UShort(0);
            w.Float(25f);
            w.Float(0f);
            w.Byte(0);
            w.Byte((byte)DamageInfo.DamageSourceType.Projectile);
            w.Vector(player.CenterPosition());
            w.SmallVector(-forward);
            w.SmallVector(Vector3.zero);
            ghost.SendTo(0, NetGame.Hit, w, true);
            yield return For(0.6f);
            report($"multiplayer hit by the second player: the player's health {before:0} -> {player.health:0}{(player.health > before - 20f ? " NOT TAKEN" : "")}");
            player.isInvulnerable = wasInvulnerable;
            Core.AutoTest.PlayerCanBeHurt = false;

            // 5. He dies, and comes back.
            ghostAlive = false;
            yield return For(1f);
            bool died = body.dead;
            ghostAlive = true;
            ghostAt = start;
            yield return For(1.5f);
            report($"multiplayer stand-in: told dead: {(died ? "dead" : "NOT DEAD")}; told alive again: {(!body.dead ? "back" : "NOT BACK")}");

            // 6. The host's soldiers told to him.
            int mine = ActorManager.instance.actors.Count(a => a != null && a.aiControlled && !NetGame.IsRemote(a));
            report($"multiplayer host's soldiers told to the second player: {told.Count} of {mine + 1} (the player and {mine} soldier(s)), {statesGot} message(s){(told.Count < mine + 1 ? " NOT ALL TOLD" : "")}");

            int vehicles = ActorManager.instance.vehicles.Count(v => v != null && !v.isTurret);
            report($"multiplayer vehicles in the shared match: {vehicles} on the field ({NetGame.VehiclesRemoved} taken off){(vehicles > 0 ? " NOT TAKEN OFF" : "")}");

            // 7. He leaves: his soldier goes with him.
            ghost.Leave();
            line.Dispose();
            float goneBy = Time.unscaledTime + 3f;
            while (NetGame.RemoteCount > 0 && Time.unscaledTime < goneBy)
            {
                yield return null;
            }
            report($"multiplayer second player left: {(NetGame.RemoteCount == 0 && (body == null || body.dead) ? "his soldier went with him" : "HIS SOLDIER NOT REMOVED")}, {Multiplayer.Session.PlayerCount} in the game");
            Multiplayer.Leave();
            report($"multiplayer closed: {(Multiplayer.Active ? "NOT CLOSED" : "closed")}");
            if (panel != null)
            {
                panel.Open(); // (not in a game: host, the list, the address)
                yield return new WaitForSecondsRealtime(1.5f);
                yield return new WaitForEndOfFrame();
                Core.Shots.Capture("multiplayer-panel-list");
                panel.Close();
            }
        }
    }
}
