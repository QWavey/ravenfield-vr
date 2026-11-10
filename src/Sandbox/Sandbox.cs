using System;
using System.Collections.Generic;
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
            Vehicle
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
        private static Harmony _harmony;
        private static bool _behaviourPatched, _quietPatched;
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
            if (__result && _anyBehaviour && Passive(__instance.actor))
            {
                __result = false;
            }
        }

        private static void StandStill(AiActorController __instance, ref Vector3 __result)
        {
            if (_anyBehaviour && Still(__instance.actor))
            {
                __result = Vector3.zero;
            }
        }

        /// <summary>The game's spawn wave (a fresh list of the dead of one team): on a quiet battlefield its soldiers stay out, the player stays in.</summary>
        private static void HoldWave(List<Actor> actorsToSpawn)
        {
            if (Quiet && actorsToSpawn != null)
            {
                actorsToSpawn.RemoveAll(a => a != null && a.aiControlled);
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

        /// <summary>The battlefield made quiet (its soldiers taken off, none coming) or given back to the match (the next wave brings them).</summary>
        internal static string SetQuiet(bool quiet)
        {
            if (quiet && !_quietPatched)
            {
                _quietPatched = true;
                Patcher.Patch(AccessTools.Method(typeof(GameModeBase), nameof(GameModeBase.SpawnActorListDeferred)), prefix: new HarmonyMethod(typeof(Sandbox), nameof(HoldWave)));
            }
            Quiet = quiet;
            if (!quiet || ActorManager.instance == null)
            {
                return quiet ? "quiet" : "the match's soldiers come back with the next wave";
            }
            int taken = 0;
            foreach (Actor a in ActorManager.instance.actors.ToArray())
            {
                if (a != null && a.aiControlled && !a.dead && !Behaviours.ContainsKey(a) && !Spawned.Contains(a))
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
            facing = Vector3.ProjectOnPlane(facing, Vector3.up);
            facing = facing.sqrMagnitude > 1e-4f ? facing.normalized : Vector3.forward;
            Vector3 side = Vector3.Cross(Vector3.up, facing);
            int team = spec.Side == SoldierSide.Friendly ? player.team : 1 - player.team;
            if (spec.Behaviour != SoldierBehaviour.Normal && spec.Behaviour != SoldierBehaviour.Follow)
            {
                PatchBehaviours();
            }
            int count = Mathf.Clamp(spec.Count, 1, 12);
            for (int i = 0; i < count; i++)
            {
                Actor a;
                try
                {
                    a = ActorManager.instance.CreateAIActor(team);
                    Vector3 spot = at + side * ((i - (count - 1) * 0.5f) * 1.5f);
                    const int mask = ~Hitbox.ALL_HITBOX_LAYER_MASK & ~(1 << 2);
                    if (i > 0 && Physics.Raycast(spot + Vector3.up * 2f, Vector3.down, out RaycastHit g, 6f, mask, QueryTriggerInteraction.Ignore))
                    {
                        spot = g.point;
                    }
                    a.SpawnAt(spot, Quaternion.LookRotation(facing, Vector3.up), Loadout(spec.Weapons));
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
                    if (players != null)
                    {
                        ai.modifier.canJoinPlayerSquad = true;
                        ai.ChangeToSquad(players);
                    }
                    else if (ai.squad == null)
                    {
                        // (A soldier with no squad: the game's AI reads the missing squad every frame, autotest 2026-10-24.)
                        new Squad(new[] { a }, ActorManager.RandomSpawnPointForTeam(a.team), null, null, 0f);
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

        /// <summary>The last thing spawned taken away again (a soldier silently, a vehicle with its crew put out). False with nothing left.</summary>
        internal static bool RemoveLast()
        {
            for (int i = Spawned.Count - 1; i >= 0; i--)
            {
                UnityEngine.Object o = Spawned[i];
                Spawned.RemoveAt(i);
                if (o != null && (!(o is Actor a) || !a.dead))
                {
                    if (o is Vehicle v)
                    {
                        Eject(v);
                    }
                    Remove(o);
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
            Behaviours.Clear();
            _anyBehaviour = false;
            return n;
        }

        /// <summary>Every soldier of the other side dead, as shot (the match counts them).</summary>
        internal static int KillEnemies()
        {
            Actor player = LocalPlayer.actor;
            if (player == null || ActorManager.instance == null)
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
            if (a == null || a.dead || !a.aiControlled)
            {
                return;
            }
            if (behaviour != SoldierBehaviour.Frozen && Behaviours.TryGetValue(a, out SoldierBehaviour was) && was == SoldierBehaviour.Frozen)
            {
                a.Unfreeze();
            }
            if (behaviour == SoldierBehaviour.Normal)
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
        }

        /// <summary>Once a frame from the UI's update: the lists kept clean (twice a second), the player's own invulnerability held.</summary>
        internal static void Update()
        {
            if (Time.unscaledTime < _nextSweep)
            {
                return;
            }
            _nextSweep = Time.unscaledTime + 0.5f;
            if (Behaviours.Count > 0 || Spawned.Count > 0)
            {
                Sweep();
            }
            if (PlayerInvulnerable)
            {
                Actor player = LocalPlayer.actor;
                if (player != null && !player.dead && !player.isInvulnerable)
                {
                    player.isInvulnerable = true;
                }
            }
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

        /// <summary>A new scene: what was spawned went with the old one; the quiet battlefield and the tool are for one match.</summary>
        internal static void OnSceneChanged()
        {
            Behaviours.Clear();
            Spawned.Clear();
            _anyBehaviour = false;
            Quiet = false;
            Armed = ToolKind.None;
            PlayerInvulnerable = false;
        }
    }
}
