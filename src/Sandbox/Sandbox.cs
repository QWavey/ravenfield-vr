using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using HarmonyLib;
using RavenfieldVR.Core;
using UnityEngine;

namespace RavenfieldVR.Sandbox
{
    internal enum SoldierSide
    {
        Enemy,
        Friendly
    }

    /// <summary>What a spawned soldier does: as the game's own, never attacking, staying where put (and fighting), a statue, or following the player.</summary>
    internal enum SoldierBehaviour
    {
        Normal,
        Neutral,
        StandStill,
        Frozen,
        Follow
    }

    internal enum SoldierWeapons
    {
        Default,
        Random,
        None,
        Mine
    }

    internal enum SoldierHealth
    {
        Normal,
        Invulnerable,
        OneHit
    }

    /// <summary>The soldier the spawn tool makes next.</summary>
    internal sealed class SoldierSpec
    {
        public SoldierSide Side;
        public SoldierBehaviour Behaviour;
        public SoldierWeapons Weapons;
        public SoldierHealth Health;
        /// <summary>How many at once (side by side, 1.5 m apart).</summary>
        public int Count = 1;
    }

    /// <summary>
    /// The sandbox (the user, 2026-11-09: "we want a sandbox mode ... a sandbox gun like in boneworks to spawn stuff and also enemys
    /// ... set their behaivior like neutral, still standing ... giving them weapons or no weapon like loadouts or just random or with
    /// nothing ... or friendlys or stay still npcs ... to test stuff basically"): soldiers, vehicles and weapons made on the spot in any
    /// match, each soldier with a side, a behaviour, weapons and a health of the player's choosing; a quiet battlefield (none of the
    /// match's own soldiers) to test in peace. The game's own soldiers and ways throughout (ActorManager.CreateAIActor, Actor.SpawnAt,
    /// VehicleSpawner.SpawnVehicleAt): what is spawned fights, dies and is counted as any other. Three small patches, put on only
    /// when a sandbox feature is first used (a match without the sandbox runs none of this).
    /// </summary>
    internal static class Sandbox
    {
        internal enum ToolKind
        {
            None,
            Soldier,
            Vehicle,
            /// <summary>What it points at is taken off the field: a soldier (the sandbox's or the match's), a vehicle.</summary>
            Remove
        }

        internal static readonly SoldierSpec Soldier = new SoldierSpec();
        internal static VehicleSpawner.VehicleSpawnType Vehicle = VehicleSpawner.VehicleSpawnType.Jeep;
        internal static bool VehicleFriendly = true;

        /// <summary>What the spawn tool in the hand makes at a pull of its trigger (None: put away).</summary>
        internal static ToolKind Armed;

        /// <summary>None of the match's own soldiers: those alive are taken off the field, the game's spawn waves bring none.</summary>
        internal static bool Quiet { get; private set; }

        /// <summary>The player takes no damage (the game's own flag on the actor, set again after each spawn).</summary>
        internal static bool PlayerInvulnerable;

        private static readonly Dictionary<Actor, SoldierBehaviour> Behaviours = new Dictionary<Actor, SoldierBehaviour>();
        private static readonly List<UnityEngine.Object> Spawned = new List<UnityEngine.Object>();
        private static readonly List<Actor> Scratch = new List<Actor>();

        // A sandbox soldier is the sandbox's for good (the user, 2026-11-10: "if a bot is killed and the body timer has expired
        // ... the body and he himself also gets removed from the match and doesnt respawn"): the match's spawn waves never take
        // him, his body lies for BodySeconds and is then taken off the field with him. He is kept, hidden and switched off, for
        // the next spawn of his side: the game numbers every soldier it makes into tables that only grow (see-each-other,
        // distances), so a hundred spawns from a pool of a few cost the match nothing, a hundred new soldiers would.
        private static readonly HashSet<Actor> Made = new HashSet<Actor>();
        private static readonly Dictionary<Actor, float> DiedAt = new Dictionary<Actor, float>();
        private static readonly List<Actor> Pool = new List<Actor>();
        private static bool _wavePatched;

        /// <summary>Seconds a dead sandbox soldier's body lies before it is taken off the field (the panel's choice).</summary>
        internal static float BodySeconds = 20f;

        /// <summary>The check's: sandbox soldiers there are in this match (standing, lying dead, or put away), and how many are put away.</summary>
        internal static int MadeCount => Made.Count;
        internal static int PooledCount => Pool.Count;

        internal static bool IsSandboxSoldier(Actor a) => a != null && Made.Contains(a);

        /// <summary>One pull of the tool as it was made (what a saved set holds): a line of the set's file.</summary>
        private static readonly List<string> Placed = new List<string>();

        /// <summary>What each pull made, beside its line in <see cref="Placed"/> (a pull of four soldiers is one line and four soldiers).</summary>
        private static readonly List<List<UnityEngine.Object>> PlacedThings = new List<List<UnityEngine.Object>>();
        private static Harmony _harmony;
        private static bool _behaviourPatched;
        private static float _nextSweep;

        /// <summary>Any soldier of the sandbox with a behaviour of its own alive (the patches' first, cheapest question).</summary>
        private static bool _anyBehaviour;

        internal static int SpawnedCount => Spawned.Count(o => o != null && (!(o is Actor a) || !a.dead));

        private static Harmony Patcher => _harmony ?? (_harmony = new Harmony(PluginInfo.Guid + ".sandbox"));

        private static bool Passive(Actor a) => a != null && Behaviours.TryGetValue(a, out SoldierBehaviour b) && (b == SoldierBehaviour.Neutral || b == SoldierBehaviour.Frozen);

        private static bool Still(Actor a) => a != null && Behaviours.TryGetValue(a, out SoldierBehaviour b) && (b == SoldierBehaviour.StandStill || b == SoldierBehaviour.Frozen);

        // The patches (bodies kept to a flag test when the sandbox holds no such soldier).
        private static void HoldFire(AiActorController __instance, ref bool __result)
        {
            if (__result && (Paused || _anyBehaviour && Passive(__instance.actor)))
            {
                __result = false;
            }
        }

        private static void StandStill(AiActorController __instance, ref Vector3 __result)
        {
            if (Paused || _anyBehaviour && Still(__instance.actor))
            {
                __result = Vector3.zero;
            }
        }

        /// <summary>The game's spawn wave (a fresh list of the dead of one team): on a quiet battlefield its soldiers stay out, the player stays in.</summary>
        private static void HoldWave(List<Actor> actorsToSpawn)
        {
            if (actorsToSpawn != null && (Quiet || Made.Count > 0))
            {
                actorsToSpawn.RemoveAll(a => a != null && (Quiet ? a.aiControlled : Made.Contains(a)));
            }
        }

        private static void PatchBehaviours()
        {
            if (_behaviourPatched)
            {
                return;
            }
            _behaviourPatched = true;
            Patcher.Patch(AccessTools.Method(typeof(AiActorController), nameof(AiActorController.WantsToFire)), postfix: new HarmonyMethod(typeof(Sandbox), nameof(HoldFire)));
            Patcher.Patch(AccessTools.Method(typeof(AiActorController), nameof(AiActorController.Velocity)), postfix: new HarmonyMethod(typeof(Sandbox), nameof(StandStill)));
        }

        private static void PatchWave()
        {
            if (_wavePatched)
            {
                return;
            }
            _wavePatched = true;
            Patcher.Patch(AccessTools.Method(typeof(GameModeBase), nameof(GameModeBase.SpawnActorListDeferred)), prefix: new HarmonyMethod(typeof(Sandbox), nameof(HoldWave)));
        }

        /// <summary>The battlefield made quiet (its soldiers taken off, none coming) or given back to the match (the next wave brings them).</summary>
        /// <summary>In a shared match this PC is not the host's: the field and its soldiers are his to change.</summary>
        internal static bool HostsField => !Net.Multiplayer.Active || Net.Multiplayer.Hosting;

        internal static string SetQuiet(bool quiet)
        {
            if (!HostsField)
            {
                return "In a shared match the field is the host's to change.";
            }
            if (quiet)
            {
                PatchWave();
            }
            Quiet = quiet;
            if (!quiet || ActorManager.instance == null)
            {
                return quiet ? "quiet" : "the match's soldiers come back with the next wave";
            }
            int taken = 0;
            foreach (Actor a in ActorManager.instance.actors.ToArray())
            {
                if (a != null && a.aiControlled && !a.dead && !Made.Contains(a))
                {
                    if (a.IsSeated())
                    {
                        a.LeaveSeat(false);
                    }
                    a.KillSilently();
                    taken++;
                }
            }
            return $"quiet: {taken} soldier(s) taken off the field";
        }

        /// <summary>Where a spawn goes when the tool points at <paramref name="point"/>: the ground there, or under it when it is a wall.</summary>
        internal static Vector3 Ground(Vector3 point, Vector3 normal)
        {
            const int mask = ~Hitbox.ALL_HITBOX_LAYER_MASK & ~(1 << 2);
            if (normal.y < 0.6f && Physics.Raycast(point + normal * 0.6f + Vector3.up * 0.2f, Vector3.down, out RaycastHit below, 40f, mask, QueryTriggerInteraction.Ignore))
            {
                return below.point;
            }
            return point;
        }

        private static WeaponManager.WeaponEntry Pick(System.Random random, WeaponManager.WeaponSlot slot)
        {
            List<WeaponManager.WeaponEntry> fit = WeaponManager.instance.allWeapons.Where(e => e != null && e.prefab != null && !e.hidden && e.usableByAi && e.slot == slot).ToList();
            return fit.Count > 0 ? fit[random.Next(fit.Count)] : null;
        }

        private static WeaponManager.LoadoutSet Loadout(SoldierWeapons weapons)
        {
            switch (weapons)
            {
                case SoldierWeapons.None:
                    return new WeaponManager.LoadoutSet(); // (every slot empty: Actor.SpawnWeapon takes a null entry)
                case SoldierWeapons.Random:
                {
                    var random = new System.Random();
                    return new WeaponManager.LoadoutSet
                    {
                        primary = Pick(random, WeaponManager.WeaponSlot.Primary), secondary = Pick(random, WeaponManager.WeaponSlot.Secondary),
                        gear1 = Pick(random, WeaponManager.WeaponSlot.Gear)
                    };
                }
                case SoldierWeapons.Mine:
                {
                    Actor player = LocalPlayer.actor;
                    var set = new WeaponManager.LoadoutSet();
                    if (player != null && player.weapons != null)
                    {
                        WeaponManager.WeaponEntry At(int i) => i < player.weapons.Length && player.weapons[i] != null ? player.weapons[i].weaponEntry : null;
                        set.primary = At(0);
                        set.secondary = At(1);
                        set.gear1 = At(2);
                        set.gear2 = At(3);
                        set.gear3 = At(4);
                    }
                    return set;
                }
                default:
                    return null; // (the game's own pick for a soldier of that side)
            }
        }

        /// <summary>
        /// Soldiers by <paramref name="spec"/> at a point (several side by side), facing <paramref name="facing"/>. What was made; a
        /// reason in <paramref name="problem"/> when none could be.
        /// </summary>
        internal static List<Actor> SpawnSoldiers(SoldierSpec spec, Vector3 at, Vector3 facing, out string problem)
        {
            var made = new List<Actor>();
            problem = null;
            Actor player = LocalPlayer.actor;
            if (ActorManager.instance == null || player == null)
            {
                problem = "not in a match";
                return made;
            }
            // In a shared match the host's PC owns the soldiers: one made on a PC that joined would stand there only, shot at
            // by nobody and seen by nobody else. On the host's PC a sandbox soldier is told to everyone like any other.
            if (Net.Multiplayer.Active && !Net.Multiplayer.Hosting)
            {
                problem = "in a shared match only the host makes soldiers";
                return made;
            }
            facing = Vector3.ProjectOnPlane(facing, Vector3.up);
            facing = facing.sqrMagnitude > 1e-4f ? facing.normalized : Vector3.forward;
            Vector3 side = Vector3.Cross(Vector3.up, facing);
            int team = spec.Side == SoldierSide.Friendly ? player.team : 1 - player.team;
            if (spec.Behaviour != SoldierBehaviour.Normal && spec.Behaviour != SoldierBehaviour.Follow)
            {
                PatchBehaviours();
            }
            PatchWave();
            int count = Mathf.Clamp(spec.Count, 1, 12);
            for (int i = 0; i < count; i++)
            {
                Actor a;
                try
                {
                    a = TakeFromPool(team) ?? ActorManager.instance.CreateAIActor(team);
                    Vector3 spot = at + side * ((i - (count - 1) * 0.5f) * 1.5f);
                    const int mask = ~Hitbox.ALL_HITBOX_LAYER_MASK & ~(1 << 2);
                    if (i > 0 && Physics.Raycast(spot + Vector3.up * 2f, Vector3.down, out RaycastHit g, 6f, mask, QueryTriggerInteraction.Ignore))
                    {
                        spot = g.point;
                    }
                    a.SpawnAt(spot, Quaternion.LookRotation(facing, Vector3.up), Loadout(spec.Weapons));
                    Made.Add(a);
                    // (Said in the log for every soldier: "spawning enemys on a modded map ... doesnt spawn them", user 2026-11-10,
                    // and nothing in the log told what the pull had done.)
                    Log.Info($"Sandbox: a soldier made at {spot:F1} (asked {at:F1}), team {a.team} (the player's {player.team}), {spec.Behaviour}, {spec.Weapons}: "
                             + $"{(a.dead ? "DEAD" : "alive")}, {(a.gameObject.activeInHierarchy ? "shown" : "NOT SHOWN")}, stands at {a.Position():F1}.");
                }
                catch (Exception e)
                {
                    problem = "the game could not make a soldier: " + e.Message;
                    Log.WarnOnce("sandbox-soldier", "Sandbox: a soldier could not be made: " + e);
                    break;
                }
                var ai = a.controller as AiActorController;
                if (ai != null)
                {
                    Squad players = spec.Behaviour == SoldierBehaviour.Follow && spec.Side == SoldierSide.Friendly && FpsActorController.instance != null
                        ? FpsActorController.instance.playerSquad : null;
                    // (The player dead and alone: his squad is disbanded until he is back, and a soldier put into it followed nobody.)
                    if (players != null && !players.disbanded && !player.dead)
                    {
                        ai.modifier.canJoinPlayerSquad = true;
                        ai.ChangeToSquad(players);
                    }
                    else if (ai.squad == null)
                    {
                        // (A soldier with no squad: the game's AI reads the missing squad every frame, autotest 2026-10-24.)
                        // (Guarded: on a map where his side holds no spawn point the squad's making threw out of the whole pull.)
                        try
                        {
                            new Squad(new[] { a }, ActorManager.RandomSpawnPointForTeam(a.team), null, null, 0f);
                        }
                        catch (Exception e)
                        {
                            Log.WarnOnce("sandbox-squad", "Sandbox: a soldier's squad could not be made: " + e);
                        }
                    }
                    ai.facingDirectionVector = facing;
                }
                switch (spec.Health)
                {
                    case SoldierHealth.Invulnerable:
                        a.isInvulnerable = true;
                        break;
                    case SoldierHealth.OneHit:
                        a.health = 1f;
                        break;
                    case SoldierHealth.Normal:
                        break;
                }
                if (spec.Behaviour == SoldierBehaviour.Frozen)
                {
                    a.Freeze();
                }
                if (spec.Behaviour != SoldierBehaviour.Normal)
                {
                    Behaviours[a] = spec.Behaviour;
                    _anyBehaviour = true;
                }
                Spawned.Add(a);
                made.Add(a);
            }
            if (made.Count > 0)
            {
                PlacedThings.Add(made.Cast<UnityEngine.Object>().ToList());
                Placed.Add(string.Join(";", new[]
                {
                    "S", ((int)spec.Side).ToString(), ((int)spec.Behaviour).ToString(), ((int)spec.Weapons).ToString(), ((int)spec.Health).ToString(), made.Count.ToString(),
                    F(at.x), F(at.y), F(at.z), F(facing.x), F(facing.z)
                }));
            }
            return made;
        }

        /// <summary>A vehicle of the map's own kind for a side at a point (a metre up: it settles on its wheels). Null with the reason.</summary>
        internal static Vehicle SpawnVehicle(VehicleSpawner.VehicleSpawnType type, bool friendly, Vector3 at, Vector3 facing, out string problem)
        {
            problem = null;
            Actor player = LocalPlayer.actor;
            if (player == null || GameManager.instance == null)
            {
                problem = "not in a match";
                return null;
            }
            if (Net.Multiplayer.Active)
            {
                problem = "a shared match is on foot for now (vehicles are not shared yet)";
                return null;
            }
            facing = Vector3.ProjectOnPlane(facing, Vector3.up);
            facing = facing.sqrMagnitude > 1e-4f ? facing.normalized : Vector3.forward;
            int team = friendly ? player.team : 1 - player.team;
            try
            {
                bool air = type == VehicleSpawner.VehicleSpawnType.AttackHelicopter || type == VehicleSpawner.VehicleSpawnType.TransportHelicopter
                           || type == VehicleSpawner.VehicleSpawnType.AttackPlane || type == VehicleSpawner.VehicleSpawnType.BomberPlane;
                if (VehicleSpawner.RollPrefab(team, type, RarityTier.Common, RarityTierFallback.LowerThenHigher, true) == null)
                {
                    problem = $"this match has no {type} for that side";
                    return null;
                }
                Vehicle v = VehicleSpawner.SpawnVehicleAt(at + Vector3.up * (air ? 1.5f : 1f), Quaternion.LookRotation(facing, Vector3.up), team, type,
                                                          RarityTier.Common, RarityTierFallback.LowerThenHigher, true);
                if (v == null)
                {
                    problem = "the game made no vehicle";
                    return null;
                }
                Spawned.Add(v);
                {
                    PlacedThings.Add(new List<UnityEngine.Object> { v });
                    Placed.Add(string.Join(";", new[] { "V", ((int)type).ToString(), friendly ? "1" : "0", F(at.x), F(at.y), F(at.z), F(facing.x), F(facing.z) }));
                }
                return v;
            }
            catch (Exception e)
            {
                problem = "the game could not make the vehicle: " + e.Message;
                Log.WarnOnce("sandbox-vehicle", "Sandbox: a vehicle could not be made: " + e);
                return null;
            }
        }

        /// <summary>A weapon into the player's hands (its own slot; a large gear into the first gear slot). Its name, or null with the reason.</summary>
        internal static string Give(WeaponManager.WeaponEntry entry, out string problem)
        {
            problem = null;
            Actor player = LocalPlayer.actor;
            if (player == null || player.dead || entry == null || entry.prefab == null)
            {
                problem = player == null || player.dead ? "not on your feet" : "that weapon has no model";
                return null;
            }
            int slot = entry.slot == WeaponManager.WeaponSlot.Primary ? 0 : entry.slot == WeaponManager.WeaponSlot.Secondary ? 1 : 2;
            try
            {
                player.EquipNewWeaponEntry(entry, Mathf.Clamp(slot, 0, player.weapons.Length - 1), true);
                return entry.name;
            }
            catch (Exception e)
            {
                problem = "the game could not give it: " + e.Message;
                return null;
            }
        }

        private static Actor TakeFromPool(int team)
        {
            for (int i = Pool.Count - 1; i >= 0; i--)
            {
                Actor a = Pool[i];
                if (a == null)
                {
                    Pool.RemoveAt(i);
                }
                else if (a.team == team && a.dead)
                {
                    Pool.RemoveAt(i);
                    a.Activate();
                    a.isInvulnerable = false;
                    return a;
                }
            }
            return null;
        }

        /// <summary>A dead sandbox soldier off the field: his body hidden, himself switched off and kept for the next spawn.</summary>
        private static void PutAway(Actor a)
        {
            DiedAt.Remove(a);
            if (a == null || !a.dead)
            {
                return;
            }
            a.isScheduledToSpawn = true;
            if (Pool.Contains(a))
            {
                if (!a.isDeactivated)
                {
                    a.Deactivate(); // (brought back by something else and dead again: off the field as before)
                }
                return;
            }
            try
            {
                a.Deactivate();
            }
            catch (Exception e)
            {
                Log.WarnOnce("sandbox-put-away", "Sandbox: a dead soldier could not be put away: " + e.Message);
            }
            Pool.Add(a);
        }

        private static void Remove(UnityEngine.Object o)
        {
            if (o is Actor a)
            {
                Behaviours.Remove(a);
                if (a != null && !a.dead)
                {
                    if (a.IsSeated())
                    {
                        a.LeaveSeat(false);
                    }
                    a.Unfreeze();
                    a.isInvulnerable = false;
                    a.KillSilently();
                }
                if (a != null && Made.Contains(a))
                {
                    PutAway(a); // (removed by the panel: gone at once, body and all)
                }
            }
            else if (o is Vehicle v && v != null)
            {
                // Out of the game's own lists first, as a vehicle that died is (Vehicle.Die): a destroyed one left in them threw each
                // frame in the vehicle avoidance and the minimap (sandbox check, 2026-11-10: NullReferenceException in
                // VehicleParticleManager.UpdateMonitoredVehicles, "Could not blit vehicle").
                try
                {
                    if (ActorManager.instance != null)
                    {
                        ActorManager.DropVehicle(v);
                    }
                }
                catch (Exception e)
                {
                    Log.WarnOnce("sandbox-drop-vehicle", "Sandbox: the game's lists did not let a vehicle go: " + e.Message);
                }
                UnityEngine.Object.Destroy(v.gameObject);
            }
        }

        /// <summary>
        /// One soldier taken off the field by the remover (pointed at): the sandbox's own is put away at once; one of the match's
        /// is taken off silently and comes back with the match's next wave, as any of its dead.
        /// </summary>
        internal static string RemoveSoldier(Actor a)
        {
            if (!HostsField)
            {
                return "In a shared match the field is the host's to change.";
            }
            if (a == null || a.dead || !a.aiControlled)
            {
                return "Nothing to remove there.";
            }
            if (Net.NetGame.IsRemote(a))
            {
                return "That one is another PC's: he cannot be removed here.";
            }
            string name = a.name;
            bool mine = Made.Contains(a);
            Spawned.Remove(a);
            Remove(a);
            if (!mine && !a.dead)
            {
                a.KillSilently();
            }
            Sweep();
            return mine ? "Removed." : $"{name} taken off the field (the match brings him back).";
        }

        /// <summary>One vehicle taken off the field by the remover, its crew put out first.</summary>
        internal static string RemoveVehicle(Vehicle v)
        {
            if (!HostsField)
            {
                return "In a shared match the field is the host's to change.";
            }
            if (v == null)
            {
                return "Nothing to remove there.";
            }
            Eject(v);
            Spawned.Remove(v);
            Remove(v);
            return "Vehicle removed.";
        }

        /// <summary>
        /// The last pull of the tool taken away again, all of what it made (soldiers silently, a vehicle with its crew put out),
        /// and its line of the set with it. False with nothing left. (One soldier of a pull of four had taken the whole pull's line
        /// out of the set, and the next ones the lines of earlier pulls: review of 2026-11-10.)
        /// </summary>
        internal static bool RemoveLast()
        {
            for (int i = PlacedThings.Count - 1; i >= 0; i--)
            {
                List<UnityEngine.Object> things = PlacedThings[i];
                PlacedThings.RemoveAt(i);
                Placed.RemoveAt(i);
                bool any = false;
                foreach (UnityEngine.Object o in things)
                {
                    if (o != null && (!(o is Actor a) || !a.dead))
                    {
                        if (o is Vehicle v)
                        {
                            Eject(v);
                        }
                        Spawned.Remove(o);
                        Remove(o);
                        any = true;
                    }
                }
                if (any)
                {
                    Sweep();
                    return true;
                }
            }
            return false;
        }

        private static void Eject(Vehicle v)
        {
            if (v == null || v.seats == null)
            {
                return;
            }
            foreach (Seat seat in v.seats)
            {
                if (seat != null && seat.occupant != null)
                {
                    seat.occupant.LeaveSeat(false);
                }
            }
        }

        internal static int RemoveAll()
        {
            int n = 0;
            foreach (UnityEngine.Object o in Spawned.ToArray())
            {
                if (o != null && (!(o is Actor a) || !a.dead))
                {
                    if (o is Vehicle v)
                    {
                        Eject(v);
                    }
                    Remove(o);
                    n++;
                }
            }
            Spawned.Clear();
            Placed.Clear();
            PlacedThings.Clear();
            Behaviours.Clear();
            _anyBehaviour = false;
            return n;
        }

        /// <summary>Every soldier of the other side dead, as shot (the match counts them).</summary>
        internal static int KillEnemies()
        {
            Actor player = LocalPlayer.actor;
            if (player == null || ActorManager.instance == null || !HostsField)
            {
                return 0;
            }
            int n = 0;
            foreach (Actor a in ActorManager.instance.actors.ToArray())
            {
                if (a != null && !a.dead && a.team != player.team && a.aiControlled)
                {
                    a.isInvulnerable = false;
                    a.Kill(new DamageInfo(DamageInfo.DamageSourceType.Unknown, player, null) { healthDamage = 1000f, point = a.CenterPosition() });
                    n++;
                }
            }
            return n;
        }

        /// <summary>A behaviour for a soldier already there (pointed at with the tool): any soldier of the match, not only the sandbox's.</summary>
        internal static void SetBehaviour(Actor a, SoldierBehaviour behaviour)
        {
            if (a == null || a.dead || !a.aiControlled || Net.NetGame.IsRemote(a))
            {
                return; // (another PC's soldier is told what to do by that PC)
            }
            if (behaviour != SoldierBehaviour.Frozen && Behaviours.TryGetValue(a, out SoldierBehaviour was) && was == SoldierBehaviour.Frozen)
            {
                a.Unfreeze();
            }
            if (behaviour == SoldierBehaviour.Follow)
            {
                // A friendly joins the player's squad now; an enemy (or with no squad to join) fights on as he was.
                Behaviours.Remove(a);
                Actor player = LocalPlayer.actor;
                Squad players = FpsActorController.instance != null ? FpsActorController.instance.playerSquad : null;
                if (player != null && !player.dead && a.team == player.team && players != null && !players.disbanded && a.controller is AiActorController follower)
                {
                    follower.modifier.canJoinPlayerSquad = true;
                    follower.ChangeToSquad(players);
                }
            }
            else if (behaviour == SoldierBehaviour.Normal)
            {
                Behaviours.Remove(a);
            }
            else
            {
                PatchBehaviours();
                Behaviours[a] = behaviour;
                if (behaviour == SoldierBehaviour.Frozen)
                {
                    a.Freeze();
                }
            }
            _anyBehaviour = Behaviours.Count > 0;
        }

        /// <summary>A soldier's behaviour as the sandbox has it (Normal for the match's own).</summary>
        internal static SoldierBehaviour BehaviourOf(Actor a) => a != null && Behaviours.TryGetValue(a, out SoldierBehaviour b) ? b : SoldierBehaviour.Normal;

        /// <summary>The dead leave the sandbox's lists: a soldier the game respawns is the match's own again.</summary>
        private static void Sweep()
        {
            Scratch.Clear();
            foreach (KeyValuePair<Actor, SoldierBehaviour> kv in Behaviours)
            {
                if (kv.Key == null || kv.Key.dead)
                {
                    Scratch.Add(kv.Key);
                }
            }
            foreach (Actor a in Scratch)
            {
                Behaviours.Remove(a);
            }
            // (Unity's own null for a destroyed vehicle or actor: the list's entries are compared as objects.)
            Spawned.RemoveAll(o => o == null || o is Actor a && a.dead);
            _anyBehaviour = Behaviours.Count > 0;
            // The dead of the sandbox: their body lies for its time, then they are put away.
            if (Made.Count > 0)
            {
                Scratch.Clear();
                foreach (Actor a in Made)
                {
                    if (a == null)
                    {
                        Scratch.Add(a);
                    }
                    else if (!a.dead)
                    {
                        DiedAt.Remove(a);
                    }
                    else if (!DiedAt.TryGetValue(a, out float died))
                    {
                        // (Marked as waiting for a spawn: a map's own scripts take any dead soldier that is not, and brought
                        // sandbox soldiers back, also ones put away; Actor.SpawnAt clears the mark. Review of 2026-11-10.)
                        a.isScheduledToSpawn = true;
                        if (!a.isDeactivated)
                        {
                            DiedAt[a] = Time.time;
                        }
                    }
                    else if (Time.time - died >= BodySeconds)
                    {
                        Scratch.Add(a);
                    }
                }
                foreach (Actor a in Scratch)
                {
                    if (a == null)
                    {
                        Made.Remove(a);
                        DiedAt.Remove(a);
                    }
                    else
                    {
                        PutAway(a);
                    }
                }
            }
        }

        /// <summary>Once a frame from the UI's update: the lists kept clean (twice a second), the player's own invulnerability held.</summary>
        internal static void Update()
        {
            if (!_modeGreeted && InMode)
            {
                Actor me = LocalPlayer.actor;
                if (me != null && !me.dead)
                {
                    // In the mode the tool is in the hand from the first life, and the menu button is the sandbox's panel.
                    _modeGreeted = true;
                    PatchWave();
                    if (Armed == ToolKind.None)
                    {
                        Armed = ToolKind.Soldier;
                    }
                    UI.UISystem.Instance?.Notice?.Show("Sandbox: the tool is in your free hand. Point, pull its trigger. Menu button: the sandbox panel.", 7f);
                }
            }
            if (PlayerInvulnerable)
            {
                // (Every frame: the game's spawn clears it, and half a second of a fresh life could be hurt.)
                Actor player = LocalPlayer.actor;
                if (player != null && !player.dead && !player.isInvulnerable)
                {
                    player.isInvulnerable = true;
                }
            }
            if (Time.unscaledTime < _nextSweep)
            {
                return;
            }
            _nextSweep = Time.unscaledTime + 0.5f;
            if (Behaviours.Count > 0 || Spawned.Count > 0 || Made.Count > 0)
            {
                Sweep();
            }
        }

        private static string F(float v) => v.ToString("0.###", CultureInfo.InvariantCulture);

        /// <summary>A number of a set's line; a line with one that is no number, or far off any map, is not made (it throws: counted as failed).</summary>
        private static float P(string s)
        {
            float v = float.Parse(s, CultureInfo.InvariantCulture);
            if (float.IsNaN(v) || float.IsInfinity(v) || Mathf.Abs(v) > 100000f)
            {
                throw new FormatException("a place off the map");
            }
            return v;
        }

        /// <summary>A choice of a set's line, held to the choices there are (a file edited by hand, or of a later version).</summary>
        private static int C(string s, int count) => Mathf.Clamp(int.Parse(s), 0, count - 1);

        /// <summary>The saved set's file for the map that is running (a set is places on one map).</summary>
        private static string SetFile()
        {
            string map = GameManager.instance != null && GameManager.instance.lastMapEntry != null ? Path.GetFileNameWithoutExtension(GameManager.instance.lastMapEntry.sceneName) : "map";
            foreach (char c in Path.GetInvalidFileNameChars())
            {
                map = map.Replace(c, '_');
            }
            return Path.Combine(BepInEx.Paths.ConfigPath, "RavenfieldVR-sandbox-" + map + ".txt");
        }

        /// <summary>How many pulls of the tool this match's set holds so far (what Save would write).</summary>
        internal static int PlacedCount => Placed.Count;

        /// <summary>Whether this map has a saved set.</summary>
        internal static bool HasSavedSet
        {
            get
            {
                try
                {
                    return File.Exists(SetFile());
                }
                catch (Exception)
                {
                    return false;
                }
            }
        }

        /// <summary>Everything placed in this match written down for this map (soldiers with their settings, vehicles), to set up again.</summary>
        internal static string SaveSet()
        {
            if (Placed.Count == 0)
            {
                return "Nothing placed yet: nothing saved.";
            }
            try
            {
                File.WriteAllLines(SetFile(), Placed.ToArray());
                return $"Saved: {Placed.Count} placement(s) for this map.";
            }
            catch (Exception e)
            {
                return "Not saved: " + e.Message;
            }
        }

        /// <summary>This map's saved set made again where it stood.</summary>
        internal static string LoadSet()
        {
            string[] lines;
            try
            {
                string file = SetFile();
                if (!File.Exists(file))
                {
                    return "No saved set for this map.";
                }
                lines = File.ReadAllLines(file);
            }
            catch (Exception e)
            {
                return "Not loaded: " + e.Message;
            }
            int made = 0, failed = 0;
            {
                foreach (string line in lines)
                {
                    string[] p = line.Split(';');
                    try
                    {
                        if (p[0] == "S" && p.Length >= 11)
                        {
                            var spec = new SoldierSpec
                            {
                                Side = (SoldierSide)C(p[1], 2), Behaviour = (SoldierBehaviour)C(p[2], 5), Weapons = (SoldierWeapons)C(p[3], 4),
                                Health = (SoldierHealth)C(p[4], 3), Count = Mathf.Clamp(int.Parse(p[5]), 1, 12)
                            };
                            int n = SpawnSoldiers(spec, new Vector3(P(p[6]), P(p[7]), P(p[8])), new Vector3(P(p[9]), 0f, P(p[10])), out _).Count;
                            made += n > 0 ? 1 : 0;
                            failed += n > 0 ? 0 : 1;
                        }
                        else if (p[0] == "V" && p.Length >= 8)
                        {
                            Vehicle v = SpawnVehicle((VehicleSpawner.VehicleSpawnType)C(p[1], Enum.GetValues(typeof(VehicleSpawner.VehicleSpawnType)).Length), p[2] == "1", new Vector3(P(p[3]), P(p[4]), P(p[5])), new Vector3(P(p[6]), 0f, P(p[7])), out _);
                            made += v != null ? 1 : 0;
                            failed += v != null ? 0 : 1;
                        }
                    }
                    catch (Exception)
                    {
                        failed++; // (a line from another version of the mod, or edited by hand)
                    }
                }
            }
            // (What was loaded is part of this match's set, each as the pull it was: saved again, it is kept.)
            return $"Loaded: {made} placement(s)" + (failed > 0 ? $", {failed} could not be made here." : ".");
        }

        /// <summary>The player's health full, every weapon's ammunition filled (the game's own resupply). False when not on the feet.</summary>
        internal static bool Refill()
        {
            Actor player = LocalPlayer.actor;
            if (player == null || player.dead)
            {
                return false;
            }
            player.ResupplyAmmo();
            for (int i = 0; i < 4 && player.health < player.maxHealth; i++)
            {
                player.ResupplyHealth(); // (30 a time, as at a crate)
            }
            return true;
        }

        internal static void SetPlayerInvulnerable(bool on)
        {
            PlayerInvulnerable = on;
            Actor player = LocalPlayer.actor;
            if (player != null && !player.dead)
            {
                player.isInvulnerable = on;
            }
        }

        // ---- pause and play ----------------------------------------------------------------------------------------

        /// <summary>Every soldier of the match holds still and holds fire (the user, 2026-11-10: "add pause and play buttons so i can pause npc behavior").</summary>
        internal static bool Paused { get; private set; }

        internal static string SetPaused(bool paused)
        {
            if (!HostsField)
            {
                return "In a shared match the field is the host's to change.";
            }
            if (paused)
            {
                PatchBehaviours();
            }
            Paused = paused;
            return paused ? "The soldiers are paused: no step, no shot." : "The soldiers go on.";
        }

        // ---- flying ------------------------------------------------------------------------------------------------

        /// <summary>The player flies: no fall, the walking stick moves him along his look (the user, 2026-11-10: "a fly tool to fly around").</summary>
        internal static bool Fly { get; private set; }
        private static bool _flyHolds;
        private const float FlySpeed = 9f;

        internal static string SetFly(bool on)
        {
            Fly = on;
            return on ? "Flying: the walking stick moves you where you look. Look up to rise." : "On your feet again.";
        }

        /// <summary>Each frame (from the tool's update, which has the head): the game's own walking is off while flying, and the stick moves the body.</summary>
        internal static void FlyTick(Transform head)
        {
            FpsActorController fps = FpsActorController.instance;
            Actor player = LocalPlayer.actor;
            if (Fly && player != null && player.dead)
            {
                Fly = false; // (a new life starts on its feet; review, 2026-11-10)
            }
            // (Not in a pause or under the game's menu: the stick moved the body through them.)
            bool can = Fly && !GameManager.IsPaused() && !Input.VRGameInput.GunInputBlocked && fps != null && fps.controller != null && player != null && !player.dead && !player.IsSeated() && !player.fallenOver && Util.GameContext.InGame;
            if (_flyHolds != can && fps != null && fps.controller != null)
            {
                fps.controller.enabled = !can; // (the game's walking and its gravity: off while flying, back at once after)
                _flyHolds = can;
            }
            if (!can || head == null)
            {
                return;
            }
            CharacterController body = fps.GetComponent<CharacterController>();
            if (body == null || !body.enabled)
            {
                return;
            }
            Vector2 stick = Input.VRGameInput.MoveStick;
            if (stick.sqrMagnitude < 0.01f)
            {
                return;
            }
            Vector3 right = Vector3.ProjectOnPlane(head.right, Vector3.up).normalized;
            body.Move((head.forward * stick.y + right * stick.x) * (FlySpeed * Time.deltaTime));
        }

        // ---- the game mode -----------------------------------------------------------------------------------------

        // Sandbox is one of the game's modes (the user, 2026-11-10: "sandbox should be a mode in game mode not in the UI. when sandbox
        // mdoe is launched we start on both sides with 0 bots except me"): an entry in the game's own mode picker, made from Point
        // Match (its rules and its flags), started with no soldier on either side and never ended by its score.
        private static GameModeInfo _modeInfo;
        private static bool _modePatched, _modeGreeted;

        /// <summary>This match was started as the Sandbox mode.</summary>
        internal static bool InMode => _modeInfo != null && Util.GameContext.InGame && GameManager.instance != null && GameManager.instance.gameModeParameters != null
                                       && GameManager.instance.gameModeParameters.gameMode == _modeInfo;

        internal static void InitMode()
        {
            if (_modePatched)
            {
                return;
            }
            _modePatched = true;
            Patcher.Patch(AccessTools.Method(typeof(GameManager), nameof(GameManager.AvailableGameModes)), postfix: new HarmonyMethod(typeof(Sandbox), nameof(AddMode)));
            Patcher.Patch(AccessTools.Method(typeof(GameManager), nameof(GameManager.StartLevel)), prefix: new HarmonyMethod(typeof(Sandbox), nameof(OnStartLevel)));
            Patcher.Patch(AccessTools.Method(typeof(VictoryUi), nameof(VictoryUi.EndGame)), prefix: new HarmonyMethod(typeof(Sandbox), nameof(HoldEnd)));
            Patcher.Patch(AccessTools.Method(typeof(GameManager), nameof(GameManager.GetBuiltInGameModeByName)), postfix: new HarmonyMethod(typeof(Sandbox), nameof(FindMode)));
        }

        /// <summary>The game looks a mode up by its name (a saved game configuration that names ours): ours is found too.</summary>
        private static void FindMode(string name, ref GameModeInfo __result)
        {
            if (__result == null && _modeInfo != null && string.Equals(name, _modeInfo.name, StringComparison.InvariantCultureIgnoreCase))
            {
                __result = _modeInfo;
            }
        }

        private static void AddMode(ref IEnumerable<GameModeInfo> __result)
        {
            try
            {
                if (_modeInfo == null)
                {
                    GameModeInfo basis = GameManager.GetBuiltInGameModeByName("Point Match") ?? GameManager.GetDefaultGameMode();
                    if (basis == null)
                    {
                        return;
                    }
                    _modeInfo = UnityEngine.Object.Instantiate(basis);
                    _modeInfo.hideFlags = HideFlags.HideAndDontSave;
                    _modeInfo.name = "Sandbox";
                    _modeInfo.sourceMod = basis.sourceMod;
                    Log.Info($"Sandbox: added to the game's modes (made from {basis.name}).");
                }
                if (__result != null && !__result.Contains(_modeInfo))
                {
                    __result = __result.Concat(new[] { _modeInfo }).ToList();
                }
            }
            catch (Exception e)
            {
                Log.WarnOnce("sandbox-mode", "Sandbox: the mode could not be added to the game's list: " + e);
            }
        }

        private static void OnStartLevel(GameModeParameters parameters)
        {
            // (A restart passes none: the game then takes the ones it holds.)
            parameters = parameters ?? (GameManager.instance != null ? GameManager.instance.gameModeParameters : null);
            if (parameters != null && _modeInfo != null && parameters.gameMode == _modeInfo && parameters.configuration != null)
            {
                parameters.configuration.SetInt("countTeam0", 0);
                parameters.configuration.SetInt("countTeam1", 0);
                Log.Info("Sandbox: the mode starts, no soldier on either side.");
            }
        }

        /// <summary>The sandbox has no end: its score is nobody's to win.</summary>
        private static bool HoldEnd() => !InMode;

        /// <summary>The game's mode by name, ours among them (a joined PC looks the host's mode up).</summary>
        internal static GameModeInfo ModeByName(string name) =>
            _modeInfo != null && string.Equals(name, _modeInfo.name, StringComparison.OrdinalIgnoreCase) ? _modeInfo : null;

        /// <summary>A new scene: what was spawned went with the old one; the quiet battlefield and the tool are for one match.</summary>
        internal static void OnSceneChanged()
        {
            Paused = false;
            Fly = false;
            _flyHolds = false;
            _modeGreeted = false;
            Behaviours.Clear();
            Spawned.Clear();
            Placed.Clear();
            PlacedThings.Clear();
            Made.Clear();
            DiedAt.Clear();
            Pool.Clear();
            _anyBehaviour = false;
            Quiet = false;
            Armed = ToolKind.None;
            PlayerInvulnerable = false;
        }
    }
}
