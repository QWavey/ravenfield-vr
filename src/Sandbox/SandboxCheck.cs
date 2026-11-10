using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using RavenfieldVR.UI;
using UnityEngine;

namespace RavenfieldVR.Sandbox
{
    /// <summary>
    /// The sandbox checked in a running match (-rfvr-sandboxcheck with the autotest; tools/vrshots.py --sandboxcheck): each kind of
    /// soldier made and measured for what it was set to (frozen: no move; neutral: no shot; stand still: no step; no weapons, mine,
    /// random; no damage, one hit; a friendly following), the quiet battlefield over a spawn wave, a vehicle, a weapon into the
    /// hands, the clearing, and the panel's four pages built. A failure is worded in capitals (NOT ...), the autotest counts those.
    /// </summary>
    internal static class SandboxCheck
    {
        private const int Mask = ~Hitbox.ALL_HITBOX_LAYER_MASK & ~(1 << 2);

        private static Vector3 Spot(Actor player, float ahead, float aside)
        {
            Transform t = player.transform;
            Vector3 forward = Vector3.ProjectOnPlane(t.forward, Vector3.up).normalized;
            Vector3 at = player.Position() + forward * ahead + Vector3.Cross(Vector3.up, forward) * aside;
            return Physics.Raycast(at + Vector3.up * 4f, Vector3.down, out RaycastHit g, 12f, Mask, QueryTriggerInteraction.Ignore) ? g.point : at;
        }

        private static List<Actor> Make(Actor player, SoldierSide side, SoldierBehaviour behaviour, SoldierWeapons weapons, SoldierHealth health, int count,
                                        float ahead, float aside, out string problem)
        {
            var spec = new SoldierSpec { Side = side, Behaviour = behaviour, Weapons = weapons, Health = health, Count = count };
            Vector3 at = Spot(player, ahead, aside);
            return Sandbox.SpawnSoldiers(spec, at, player.Position() - at, out problem);
        }

        private static int MatchSoldiersAlive(ICollection<Actor> own)
        {
            int n = 0;
            foreach (Actor a in ActorManager.instance.actors)
            {
                if (a != null && a.aiControlled && !a.dead && !own.Contains(a))
                {
                    n++;
                }
            }
            return n;
        }

        internal static IEnumerator Run(Action<string> report)
        {
            Actor player = LocalPlayer.actor;
            if (player == null || player.dead || ActorManager.instance == null)
            {
                report("sandbox: skipped (no player on the field)");
                yield break;
            }
            var own = new HashSet<Actor>();
            bool wasInvulnerable = player.isInvulnerable;
            Sandbox.SetPlayerInvulnerable(true);
            report($"sandbox: no damage for the player: {(player.isInvulnerable ? "on" : "NOT ON")}");

            Core.Log.Info("[sandbox check] step: quiet");
            // 1. The quiet battlefield, over more than one of the game's spawn waves.
            int before = MatchSoldiersAlive(own);
            string said = Sandbox.SetQuiet(true);
            yield return null;
            int after = MatchSoldiersAlive(own);
            float wave = Mathf.Clamp(GameManager.GameParameters().GetRespawnTime(), 1f, 120f);
            float wait = Mathf.Min(wave * 2f + 2f, 40f);
            yield return Core.AutoTest.GameSeconds(wait, report);
            int later = MatchSoldiersAlive(own);
            report($"sandbox quiet: {before} soldier(s) of the match before, {after} right after ({said}), {later} after {wait:0} s (spawn wave {wave:0} s)"
                   + (after > 0 ? " NOT QUIET AT ONCE" : "") + (later > 0 ? " NOT QUIET AFTER A WAVE" : ""));

            Core.Log.Info("[sandbox check] step: soldiers");
            // 2. A frozen enemy: there, of the other side, not moving, not shooting.
            List<Actor> frozen = Make(player, SoldierSide.Enemy, SoldierBehaviour.Frozen, SoldierWeapons.Default, SoldierHealth.Normal, 1, 7f, -4f, out string problem);
            // 3. Two neutral enemies in plain view, and two that stand still.
            List<Actor> neutral = Make(player, SoldierSide.Enemy, SoldierBehaviour.Neutral, SoldierWeapons.Default, SoldierHealth.Normal, 2, 9f, 0f, out string problem2);
            List<Actor> still = Make(player, SoldierSide.Enemy, SoldierBehaviour.StandStill, SoldierWeapons.None, SoldierHealth.Normal, 2, 9f, 5f, out string problem3);
            foreach (Actor a in frozen.Concat(neutral).Concat(still))
            {
                own.Add(a);
            }
            if (frozen.Count != 1 || neutral.Count != 2 || still.Count != 2)
            {
                report($"sandbox soldiers: NOT MADE ({frozen.Count}/1 frozen: {problem}; {neutral.Count}/2 neutral: {problem2}; {still.Count}/2 still: {problem3})");
            }
            else
            {
                yield return Core.AutoTest.GameSeconds(1.5f, report); // (landed on their feet)
                Vector3 frozenAt = frozen[0].Position();
                Vector3[] stillAt = still.Select(a => a.Position()).ToArray();
                float started = Time.time;
                Sandbox.SetPlayerInvulnerable(false);
                float health = player.health;
                yield return Core.AutoTest.GameSeconds(8f, report);
                float moved = Vector3.Distance(frozenAt, frozen[0].Position());
                float stepped = still.Select((a, i) => Vector3.Distance(stillAt[i], a.Position())).Max();
                bool shot = frozen.Concat(neutral).Any(a => a.weapons.Any(w => w != null && w.lastFiredTimestamp > started));
                bool wrongSide = frozen.Concat(neutral).Concat(still).Any(a => a.team == player.team);
                bool unarmed = still.All(a => a.weapons.All(w => w == null));
                report($"sandbox frozen enemy: moved {moved:0.00} m in 8 s{(moved > 0.1f ? " NOT STILL" : "")}{(frozen[0].dead ? " NOT ALIVE" : "")}");
                report($"sandbox neutral enemies: shots fired {(shot ? "yes NOT HOLDING FIRE" : "none")}, the player's health {health:0} -> {player.health:0}"
                       + (player.health < health - 0.5f ? " NOT UNHARMED" : "") + (wrongSide ? " NOT OF THE OTHER SIDE" : ""));
                report($"sandbox stand-still enemies without weapons: stepped {stepped:0.00} m in 8 s{(stepped > 0.6f ? " NOT STANDING STILL" : "")}, weapons {(unarmed ? "none" : "NOT NONE")}");
                Sandbox.SetPlayerInvulnerable(true);
            }

            Core.Log.Info("[sandbox check] step: weapons");
            // 4. Weapons: mine, random.
            List<Actor> mine = Make(player, SoldierSide.Friendly, SoldierBehaviour.Frozen, SoldierWeapons.Mine, SoldierHealth.Invulnerable, 1, 6f, 8f, out problem);
            List<Actor> random = Make(player, SoldierSide.Friendly, SoldierBehaviour.Frozen, SoldierWeapons.Random, SoldierHealth.OneHit, 1, 6f, 10f, out problem2);
            foreach (Actor a in mine.Concat(random))
            {
                own.Add(a);
            }
            yield return Core.AutoTest.GameSeconds(0.5f, report);
            if (mine.Count == 1 && random.Count == 1)
            {
                Actor m = mine[0], r = random[0];
                string Names(Actor a) => string.Join(", ", a.weapons.Where(w => w != null).Select(w => w.weaponEntry != null ? w.weaponEntry.name : w.name).ToArray());
                bool same = true;
                for (int i = 0; i < 5; i++)
                {
                    WeaponManager.WeaponEntry want = player.weapons[i] != null ? player.weapons[i].weaponEntry : null;
                    WeaponManager.WeaponEntry got = m.weapons[i] != null ? m.weapons[i].weaponEntry : null;
                    same &= want == got;
                }
                report($"sandbox friendly with my weapons: [{Names(m)}] against mine [{Names(player)}]{(same ? "" : " NOT THE SAME")}{(m.team != player.team ? " NOT FRIENDLY" : "")}");
                report($"sandbox friendly with random weapons: [{Names(r)}]{(r.weapons.Any(w => w != null) ? "" : " NOT ARMED")}");
                // 5. Health: no damage, one hit.
                m.Damage(new DamageInfo(DamageInfo.DamageSourceType.Unknown, player, null) { healthDamage = 500f, point = m.CenterPosition() });
                r.Damage(new DamageInfo(DamageInfo.DamageSourceType.Unknown, player, null) { healthDamage = 5f, point = r.CenterPosition() });
                yield return null;
                report($"sandbox health: 500 damage on the no-damage soldier: {(m.dead ? "dead NOT INVULNERABLE" : "alive")}; 5 damage on the one-hit soldier: {(r.dead ? "dead" : "alive NOT DEAD")}");
            }
            else
            {
                report($"sandbox friendlies: NOT MADE (mine: {problem}; random: {problem2})");
            }

            Core.Log.Info("[sandbox check] step: follower");
            // 5b. A dead sandbox soldier: his body lies for its time, then he is off the field, and no spawn wave brings him back.
            if (random.Count == 1 && random[0].dead)
            {
                Actor dead = random[0];
                float was = Sandbox.BodySeconds;
                Sandbox.BodySeconds = 3f;
                bool lying = dead.isRendered && !dead.isDeactivated;
                float waveTime = Mathf.Clamp(GameManager.GameParameters().GetRespawnTime(), 1f, 120f);
                Sandbox.SetQuiet(false); // (the match's waves run: they must not take him)
                yield return Core.AutoTest.GameSeconds(Mathf.Max(5f, waveTime * 2f + 2f), report);
                Sandbox.SetQuiet(true);
                Sandbox.BodySeconds = was;
                report($"sandbox dead soldier: body {(lying ? "lay" : "NOT LYING")} at first, after its time {(dead.isDeactivated && !dead.isRendered ? "taken off the field" : "NOT TAKEN OFF")}, "
                       + $"{(dead.dead ? "not brought back by two spawn waves" : "NOT DEAD ANY MORE (a spawn wave took him)")}; put away: {Sandbox.PooledCount}");
                // The next friendly of the sandbox is the same soldier again, not a new one.
                int madeBefore = Sandbox.MadeCount;
                List<Actor> anew = Make(player, SoldierSide.Friendly, SoldierBehaviour.Frozen, SoldierWeapons.Default, SoldierHealth.Normal, 1, 6f, 12f, out _);
                foreach (Actor a in anew)
                {
                    own.Add(a);
                }
                yield return Core.AutoTest.GameSeconds(0.5f, report);
                bool reused = anew.Count == 1 && ReferenceEquals(anew[0], dead) && Sandbox.MadeCount == madeBefore;
                report($"sandbox soldier made again from the one put away: {(reused && !dead.dead && dead.isRendered && !dead.isDeactivated ? "the same soldier, standing and seen" : "NOT REUSED")}");
            }

            // 6. A friendly that follows: in the player's squad.
            List<Actor> follow = Make(player, SoldierSide.Friendly, SoldierBehaviour.Follow, SoldierWeapons.Default, SoldierHealth.Normal, 1, 4f, -3f, out problem);
            foreach (Actor a in follow)
            {
                own.Add(a);
            }
            yield return Core.AutoTest.GameSeconds(0.5f, report);
            if (follow.Count == 1)
            {
                var ai = follow[0].controller as AiActorController;
                Squad players = FpsActorController.instance != null ? FpsActorController.instance.playerSquad : null;
                report($"sandbox follower: {(ai != null && players != null && ai.squad == players ? "in the player's squad" : "NOT IN THE SQUAD")}");
            }
            else
            {
                report($"sandbox follower: NOT MADE ({problem})");
            }

            // 7. A soldier of the match's kind pointed at: the tool's change of behaviour, and back.
            if (neutral.Count > 0 && !neutral[0].dead)
            {
                Sandbox.SetBehaviour(neutral[0], SoldierBehaviour.Frozen);
                bool set = Sandbox.BehaviourOf(neutral[0]) == SoldierBehaviour.Frozen;
                Sandbox.SetBehaviour(neutral[0], SoldierBehaviour.Normal);
                bool back = Sandbox.BehaviourOf(neutral[0]) == SoldierBehaviour.Normal;
                report($"sandbox behaviour changed on a standing soldier: {(set && back ? "frozen and back to normal" : "NOT CHANGED")}");
            }

            Core.Log.Info("[sandbox check] step: vehicle");
            // 8. A vehicle (whichever kind this map's sides have), and taken away again.
            Vehicle vehicle = null;
            string vehicleProblem = null;
            foreach (VehicleSpawner.VehicleSpawnType type in (VehicleSpawner.VehicleSpawnType[])Enum.GetValues(typeof(VehicleSpawner.VehicleSpawnType)))
            {
                Vector3 at = Spot(player, 14f, -8f);
                vehicle = Sandbox.SpawnVehicle(type, true, at, player.Position() - at, out vehicleProblem);
                if (vehicle != null)
                {
                    break;
                }
            }
            if (vehicle == null)
            {
                report($"sandbox vehicle: NOT MADE ({vehicleProblem})");
            }
            else
            {
                string name = vehicle.name;
                yield return Core.AutoTest.GameSeconds(1.5f, report);
                bool removed = Sandbox.RemoveLast();
                yield return null;
                yield return null;
                report($"sandbox vehicle: {name} made, {(removed && vehicle == null ? "taken away again" : "NOT TAKEN AWAY")}");
            }

            Core.Log.Info("[sandbox check] step: give");
            // 9. A weapon into the hands.
            WeaponManager.WeaponEntry give = WeaponManager.instance.allWeapons.FirstOrDefault(e => e != null && e.prefab != null && !e.hidden
                && e.slot == WeaponManager.WeaponSlot.Primary && (player.weapons[0] == null || player.weapons[0].weaponEntry != e));
            if (give != null)
            {
                string given = Sandbox.Give(give, out problem);
                yield return Core.AutoTest.GameSeconds(1f, report);
                bool has = player.weapons[0] != null && player.weapons[0].weaponEntry == give;
                report($"sandbox weapon into the hands: {give.name}: {(given != null && has ? "in the primary slot" : "NOT GIVEN (" + problem + ")")}");
            }

            Core.Log.Info("[sandbox check] step: panel");
            // 9b. The spawn gun itself: taken, the free hand held out and pointed at the ground ahead, its trigger pulled.
            Core.Log.Info("[sandbox check] step: tool");
            Rig.VRRig rig = Rig.VRRig.Instance;
            SandboxTool tool = UISystem.Instance != null ? UISystem.Instance.SandboxTool : null;
            if (rig != null && tool != null)
            {
                int side = (int)rig.Off.Side;
                Transform head = rig.Head;
                Vector3 ahead = Vector3.ProjectOnPlane(head.forward, Vector3.up).normalized;
                Vector3 hold = head.position + ahead * 0.38f - Vector3.up * 0.28f + Vector3.Cross(Vector3.up, ahead) * (rig.Off.IsLeft ? -0.16f : 0.16f);
                Vector3 target = Spot(player, 6f, rig.Off.IsLeft ? -1.5f : 1.5f);
                Sandbox.Soldier.Side = SoldierSide.Enemy;
                Sandbox.Soldier.Behaviour = SoldierBehaviour.Frozen;
                Sandbox.Soldier.Weapons = SoldierWeapons.Default;
                Sandbox.Soldier.Health = SoldierHealth.Normal;
                Sandbox.Soldier.Count = 1;
                Sandbox.Armed = Sandbox.ToolKind.Soldier;
                int madeBefore = Sandbox.SpawnedCount;
                bool shown = false;
                float gunFromHand = -1f;
                try
                {
                    Rig.VRHand.Scripted[side] = new Runtime.RigidPose(hold, Quaternion.LookRotation(target - hold, Vector3.up));
                    yield return new WaitForSecondsRealtime(0.8f);
                    shown = tool.GunShown;
                    gunFromHand = Vector3.Distance(tool.Muzzle, rig.Off.TrackedPointer.position);
                    yield return new WaitForEndOfFrame();
                    Core.Shots.Capture("sandbox-spawn-gun");
                    Input.VRInput.SimulatedTrigger[side] = 1f;
                    yield return new WaitForSecondsRealtime(0.4f);
                    Input.VRInput.SimulatedTrigger[side] = 0f;
                    yield return new WaitForSecondsRealtime(0.6f);
                    yield return new WaitForEndOfFrame();
                    Core.Shots.Capture("sandbox-spawn-gun-after");
                }
                finally
                {
                    Input.VRInput.SimulatedTrigger[side] = 0f;
                    Rig.VRHand.Scripted[side] = null;
                    Sandbox.Armed = Sandbox.ToolKind.None;
                }
                int madeByPull = Sandbox.SpawnedCount - madeBefore;
                // The remover: the soldier the pull made, pointed at by its own word (the hand's beam is not aimed at him here).
                Actor justMade = ActorManager.instance.actors.Where(a => Sandbox.IsSandboxSoldier(a) && !a.dead && Sandbox.BehaviourOf(a) == SoldierBehaviour.Frozen && a.team != player.team)
                    .OrderBy(a => (a.Position() - target).sqrMagnitude).FirstOrDefault(); // (the frozen enemy nearest where the gun pointed)
                string removed = justMade != null ? Sandbox.RemoveSoldier(justMade) : "no soldier";
                string removerNote = justMade != null && justMade.dead && justMade.isDeactivated ? "the remover took him off at once" : "THE REMOVER DID NOT TAKE HIM (" + removed + ")";
                foreach (Actor a in ActorManager.instance.actors)
                {
                    if (Sandbox.IsSandboxSoldier(a))
                    {
                        own.Add(a);
                    }
                }
                report($"sandbox spawn gun: {(shown ? $"in the free hand (its mouth {gunFromHand * 100f:0} cm from the hand's pointer)" : "NOT IN THE HAND")}, "
                       + $"a pull of its trigger made {madeByPull} soldier(s){(madeByPull != 1 ? " NOT ONE" : "")}; {removerNote}");
            }

            // 10. The panel: every page built, with buttons on it.
            SandboxPanel panel = UISystem.Instance != null ? UISystem.Instance.SandboxPanel : null;
            if (panel != null)
            {
                var pages = new List<string>();
                bool empty = false;
                string threw = null;
                for (int tab = 0; tab < 4; tab++)
                {
                    try
                    {
                        int buttons = panel.BuildForCheck(tab);
                        pages.Add(buttons.ToString());
                        empty |= buttons < 4;
                    }
                    catch (Exception e)
                    {
                        threw = e.GetType().Name + ": " + e.Message;
                    }
                    // (A picture of each page, to look its layout over: reports/shots/sandboxcheck.)
                    yield return new WaitForSecondsRealtime(0.4f);
                    yield return new WaitForEndOfFrame();
                    Core.Shots.Capture("sandbox-panel-" + (tab + 1));
                    yield return null;
                }
                panel.Close();
                report($"sandbox panel: buttons on its four pages: {string.Join(", ", pages.ToArray())}{(empty ? " NOT BUILT" : "")}{(threw != null ? " NOT BUILT (" + threw + ")" : "")}");
            }

            // 10b. The set saved, cleared, loaded: the same number of soldiers stand again.
            Core.Log.Info("[sandbox check] step: set");
            int standing = own.Count(a => a != null && !a.dead);
            int placed = Sandbox.PlacedCount;
            string saved = Sandbox.SaveSet();
            Sandbox.RemoveAll();
            yield return null;
            string loadedSet = Sandbox.LoadSet();
            yield return Core.AutoTest.GameSeconds(1f, report);
            int again = Sandbox.SpawnedCount;
            report($"sandbox set: {placed} placement(s), {saved} {loadedSet} {again} standing after the load{(placed == 0 || again < placed ? " NOT SET UP AGAIN" : "")} ({standing} stood before)");
            Core.Log.Info("[sandbox check] step: clear");
            // 11. Cleared: nothing of the sandbox's left standing; the match's soldiers back with the next wave.
            int removedAll = Sandbox.RemoveAll();
            yield return null;
            int left = own.Count(a => a != null && !a.dead) + Sandbox.SpawnedCount;
            report($"sandbox cleared: {removedAll} removed, {left} still standing{(left > 0 ? " NOT CLEARED" : "")}");
            Sandbox.SetQuiet(false);
            yield return Core.AutoTest.GameSeconds(wave + 3f, report);
            int back2 = MatchSoldiersAlive(own);
            report($"sandbox quiet off: {back2} soldier(s) of the match back after a wave{(back2 == 0 ? " NOT BACK" : "")}");
            Sandbox.SetPlayerInvulnerable(wasInvulnerable);
        }
    }
}
