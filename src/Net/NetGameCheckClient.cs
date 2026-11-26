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

        /// <summary>
        /// The steps run with a way out: one that threw (a soldier with no weapon to draw, a packet cut short) left the game hosted or
        /// joined, and every later run reported NOT HOSTING; now reported and the session left (check scout, 2026-11-26).
        /// </summary>
        internal static IEnumerator Run(Action<string> report)
        {
            IEnumerator steps = RunSteps(report);
            bool finished = false;
            try
            {
                while (true)
                {
                    object current;
                    try
                    {
                        if (!steps.MoveNext())
                        {
                            finished = true;
                            break;
                        }
                        current = steps.Current;
                    }
                    catch (Exception e)
                    {
                        report("multiplayer check: NOT FINISHED (" + e.GetType().Name + ": " + e.Message + ")");
                        break;
                    }
                    yield return current;
                }
            }
            finally
            {
                if (!finished)
                {
                    Log.Guard("Multiplayer check: leave", Multiplayer.Leave);
                }
            }
        }

        private static IEnumerator RunSteps(Action<string> report)
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
            int extraFlags = 0; // (flags told for soldier 1 besides his own: the reload step)
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
                    NetGame.PutState(w, id, (alive ? NetGame.Alive : 0) | (side == 1 ? NetGame.TeamOne : 0) | (id == 1 ? extraFlags : 0), at, Vector3.zero, Vector3.forward);
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

            // 3a. A soldier told reloading (2026-11-14): his stand-in reloads with him, and its gun is full again otherwise.
            {
                NetGame.Remote reloader = NetGame.AllRemotes.FirstOrDefault(r => r.Id == 1);
                Weapon gun = reloader != null && reloader.Actor != null ? reloader.Actor.activeWeapon : null;
                if (gun == null || gun.configuration == null || gun.configuration.ammo <= 0)
                {
                    report("multiplayer soldier's reload: skipped (no gun with a magazine in his stand-in's hands)");
                }
                else
                {
                    gun.ammo = 1; // (his copy nearly dry here: it is kept full while his owner is not reloading)
                    gun.OnAmmoChanged();
                    yield return For(0.4f);
                    bool refilled = gun.ammo == gun.configuration.ammo;
                    extraFlags = NetGame.Reloading;
                    bool reloaded = false;
                    for (float until = Time.unscaledTime + 1.5f; Time.unscaledTime < until && !reloaded;)
                    {
                        reloaded = gun.reloading;
                        Pump(); // (the word goes out only from Pump: a bare wait sent nothing, and the stand-in never heard it)
                        yield return null;
                    }
                    extraFlags = 0;
                    yield return For(0.3f);
                    report($"multiplayer soldier's reload: his copy kept full {(refilled ? "yes" : "NOT REFILLED")}; told reloading: "
                           + (reloaded ? "his stand-in reloaded with him" : gun.unholstered ? "NOT RELOADED" : "not reloaded (his gun not out here)"));
                }
            }

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

            // 5a. A vehicle of the host's (1.17.10): a copy made here from this PC's prefab of that name, kinematic, following where he
            // says it goes, gone when he says.
            if (NetVehicles.Enabled)
            {
                Vehicle prefab = Resources.FindObjectsOfTypeAll<Vehicle>()
                    .FirstOrDefault(v => v != null && !v.gameObject.scene.IsValid() && !v.isTurret && v.GetComponent<Rigidbody>() != null);
                Actor me = LocalPlayer.actor;
                if (prefab == null || me == null)
                {
                    report("multiplayer shared vehicle: skipped (no vehicle prefab loaded, or no player)");
                }
                else
                {
                    const ushort vid = 900;
                    Vector3 at = me.Position() + me.transform.forward * 15f + Vector3.up * 3f;
                    w.Reset();
                    w.UShort(vid);
                    w.String(prefab.gameObject.name, 128);
                    w.Byte(0);
                    w.Bool(false);
                    w.Vector(at);
                    w.Rotation(Quaternion.identity);
                    w.Bool(false);
                    host.Send(NetGame.VehicleInfo, w, true);
                    yield return For(0.5f);
                    host.Send(NetGame.VehicleInfo, w, true); // (told again, as the host does every 3 s: early in a match the first is waited out)
                    yield return For(0.8f);
                    Vehicle copy = NetVehicles.VehicleOf(vid);
                    bool made = copy != null;
                    bool kinematic = copy != null && copy.rigidbody != null && copy.rigidbody.isKinematic;
                    Vector3 to = at + me.transform.right * 6f;
                    for (int i = 0; i <= 15; i++)
                    {
                        w.Reset();
                        w.Byte(1);
                        w.UShort(vid);
                        w.Vector(Vector3.Lerp(at, to, i / 15f));
                        w.Rotation(Quaternion.identity);
                        w.SmallVector(i < 15 ? to - at : Vector3.zero); // (stopped at the end: carried on by its speed it would overshoot)
                        w.SmallVector(Vector3.zero);
                        w.Float(copy != null ? copy.maxHealth * 0.4f : 100f);
                        w.Byte(0);
                        w.Byte(0);
                        host.Send(NetGame.VehicleStates, w, false);
                        yield return For(1f / 15f);
                    }
                    yield return For(0.6f);
                    float off = copy != null ? Vector3.Distance(copy.transform.position, to) : -1f;
                    bool smoke = copy != null && (copy.smokeParticles == null || copy.smokeParticles.isPlaying);
                    w.Reset();
                    w.UShort(vid);
                    host.Send(NetGame.VehicleGone, w, true);
                    yield return For(0.6f);
                    report($"multiplayer shared vehicle '{prefab.gameObject.name}': {(made ? "copy made" : "NO COPY MADE")}, {(kinematic ? "kinematic" : "NOT KINEMATIC")}, "
                           + $"{(off >= 0f && off < 0.6f ? $"followed the host's word ({off * 100f:0} cm off)" : $"DID NOT FOLLOW ({off:0.00} m off)")}, smoke at 40% health {(smoke ? "shown" : "NOT SHOWN")}, "
                           + $"gone when told: {(!made ? "-" : copy == null ? "yes" : "NOT GONE")}");
                }
            }

            // 5a2. The host's spot and a magazine he hands over (2026-11-14): his mark shows here where he pointed, and the rounds go
            // into the reserve of this player's gun of that kind.
            {
                Actor me = LocalPlayer.actor;
                if (me == null)
                {
                    report("multiplayer spot and handed magazine: skipped (no player)");
                }
                else
                {
                    Vector3 spotAt = me.Position() + me.transform.forward * 12f;
                    w.Reset();
                    w.Vector(spotAt);
                    w.Bool(false);
                    host.Send(NetGame.Spot, w, true);
                    yield return For(0.6f);
                    bool marked = NetSpots.Showing(0, out Vector3 markAt) && (markAt - spotAt).sqrMagnitude < 0.01f;
                    Weapon gun = me.activeWeapon;
                    string given = "skipped (no gun with a reserve)";
                    if (gun != null && gun.weaponEntry != null && !gun.HasInfiniteSpareAmmo() && gun.configuration != null && gun.configuration.spareAmmo > 0)
                    {
                        gun.spareAmmo = Mathf.Max(0, gun.configuration.spareAmmo - 20);
                        int reserveBefore = gun.spareAmmo;
                        w.Reset();
                        w.String(gun.weaponEntry.name, 120);
                        w.VarInt(10);
                        w.Bool(false);
                        host.Send(NetGame.GiveRounds, w, true);
                        yield return For(0.6f);
                        given = gun.spareAmmo == reserveBefore + 10 ? $"10 rounds into the reserve ({reserveBefore} to {gun.spareAmmo})" : $"NOT ADDED ({reserveBefore} to {gun.spareAmmo})";
                    }
                    report($"multiplayer spot from the host: {(marked ? "shown where he pointed" : "NOT SHOWN")}; a magazine handed over: {given}");
                }
            }

            // 5b. The host's flags and the host's end: a flag held otherwise here is given to who holds it there; this game's own
            // victory is held back, the host's is shown.
            {
                SpawnPoint[] points = ActorManager.instance.spawnPoints;
                if (points != null && points.Length > 0)
                {
                    int flag = 0, was = points[0].owner, told = was == 0 ? 1 : 0;
                    void Tell(bool ended, int winner)
                    {
                        w.Reset();
                        w.Byte((byte)points.Length);
                        for (int i = 0; i < points.Length; i++)
                        {
                            w.Byte((byte)((i == flag ? told : points[i].owner) + 2));
                        }
                        w.Bool(ended);
                        w.Byte((byte)(winner + 2));
                        w.Byte(1); // (the Battle mode's score: two battalions and 41 tickets, three and 77)
                        w.Byte(2);
                        w.VarInt(41);
                        w.Byte(3);
                        w.VarInt(77);
                        host.Send(NetGame.MatchState, w, true);
                    }
                    Tell(false, -1);
                    yield return For(0.8f);
                    bool flagTaken = points[flag].owner == told;
                    var battle = GameModeBase.activeGameMode as BattleMode;
                    string score = battle == null ? "not a Battle match: no score told"
                        : battle.tickets[0] == 41 && battle.tickets[1] == 77 && battle.remainingBattalions[0] == 2 && battle.remainingBattalions[1] == 3 ? "the host's score shown (2 battalions and 41 tickets, 3 and 77)"
                        : $"SCORE NOT TAKEN ({battle.remainingBattalions[0]}/{battle.tickets[0]}, {battle.remainingBattalions[1]}/{battle.tickets[1]})";
                    VictoryUi.EndGame(1 - team); // (this game's own counting calls an end: held back on a PC that joined)
                    yield return For(0.3f);
                    bool held = !VictoryUi.GameHasEnded();
                    Tell(true, team);
                    yield return For(0.8f);
                    report($"multiplayer match state from the host: flag '{points[flag].name}' held by team {was} here, told {told}: {(flagTaken ? "given over" : "NOT GIVEN OVER")}; "
                           + $"this game's own end {(held ? "held back" : "NOT HELD BACK")}; the host's end {(VictoryUi.GameHasEnded() && NetGame.EndTaken ? "shown" : "NOT SHOWN")}; {score}");
                }
            }

            // 5c. The host goes back to the menu: so does this game, still in his game and following him there.
            {
                w.Reset();
                host.Send(NetGame.BackToMenu, w, true);
                float menuBy = Time.unscaledTime + 60f;
                while (Time.unscaledTime < menuBy && (MainMenu.instance == null || RavenfieldVR.Util.GameContext.InGame))
                {
                    Pump();
                    yield return null;
                }
                yield return For(2f);
                bool inMenu = MainMenu.instance != null && !RavenfieldVR.Util.GameContext.InGame;
                UI.UISystem menuUi = UI.UISystem.Instance;
                report($"multiplayer host back to the menu: {(inMenu ? "this game followed him there" : "NOT FOLLOWED")}, still in his game: {(Multiplayer.Active ? "yes" : "NOT IN IT")}, "
                       + $"the game's own menus {(menuUi != null && menuUi.Canvases.HideMenus ? "hidden (he chooses)" : "NOT HIDDEN")}");
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
