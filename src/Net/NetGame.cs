using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HarmonyLib;
using RavenfieldVR.Core;
using UnityEngine;

namespace RavenfieldVR.Net
{
    /// <summary>
    /// The match over a session. Each PC owns soldiers: its own player, and the host all of the match's soldiers. An owner tells
    /// the others where each of his is (15 times a second) and what it carries; every other PC shows it as a soldier of the game
    /// (a stand-in: ActorManager.CreateAIActor, moved, turned and made to fire by what was told, through four small patches on
    /// the AI's answers). A PC that joined runs the match with no soldiers of its own: all it sees are stand-ins of the host's.
    ///
    /// Hits: the shooter's PC decides. A hit by one of my soldiers on a stand-in is sent to the stand-in's owner, who takes the
    /// damage; a stand-in's own bullets here hurt nobody (its owner's PC sends what they hit there). So nobody is hit twice, and
    /// a kill is the shooter's on every PC.
    ///
    /// The host starts a match from the game's own menu; the same map, mode and night go to everyone (<see cref="MatchStart"/>),
    /// also to who joins later. A map is named by its file, so a workshop map works when both have it.
    /// Not carried yet: vehicles (a soldier in one is shown where his seat is), scores and flags (each PC counts its own).
    /// </summary>
    internal static class NetGame
    {
        internal const byte MatchStart = 16, ActorInfo = 17, ActorStates = 18, Hit = 19;
        private const float SendEvery = 1f / 15f;
        internal const int Alive = 1, TeamOne = 2, Crouched = 4, Proned = 8, Sprinting = 16, Firing = 32, Aimed = 64, Seated = 128;

        /// <summary>A soldier owned by another PC, and its stand-in here.</summary>
        internal sealed class Remote
        {
            public byte Owner;
            public ushort Id;
            public Actor Actor;
            public string Name = "", Weapon = "";
            public int Flags;
            public Vector3 Position, Velocity;
            public float Yaw, Pitch;
            public float Heard = -1f;
            public bool HasState, HasInfo, WeaponChanged;

            public bool Is(int flag) => (Flags & flag) != 0;
            public int Team => Is(TeamOne) ? 1 : 0;
            public Vector3 Facing => Quaternion.Euler(-Pitch, Yaw, 0f) * Vector3.forward;
        }

        private sealed class Own
        {
            public ushort Id;
            public string Weapon;
            public int Team = -1;
            public bool Told;
        }

        private static readonly Dictionary<int, Remote> Remotes = new Dictionary<int, Remote>();
        private static readonly Dictionary<Actor, Remote> ByActor = new Dictionary<Actor, Remote>();
        private static readonly Dictionary<Actor, Own> Owned = new Dictionary<Actor, Own>();
        private static readonly Dictionary<ushort, Actor> OwnedById = new Dictionary<ushort, Actor>();
        private static readonly List<Actor> Scratch = new List<Actor>();
        private static readonly NetWriter Out = new NetWriter(1400);
        private static NetSession _session;
        private static Harmony _harmony;
        private static bool _patched, _applying, _startingFromNet, _initialised;
        private static ushort _nextId = 1;
        private static float _nextSend;
        private static byte[] _lastStart;

        /// <summary>Stand-ins on the field (the patches' first, cheapest question).</summary>
        private static bool _anyRemote;

        /// <summary>The check's: how often a stand-in's speed was asked for, and the last answer.</summary>
        internal static int VelocityAsked;
        internal static Vector3 LastVelocity;

        internal static int RemoteCount => Remotes.Count;
        internal static IEnumerable<Remote> AllRemotes => Remotes.Values;

        /// <summary>Whether a soldier here is another PC's (its stand-in).</summary>
        internal static bool IsRemote(Actor a) => a != null && _anyRemote && ByActor.ContainsKey(a);

        internal static void Init()
        {
            if (_initialised)
            {
                return;
            }
            _initialised = true;
            Multiplayer.Started += Attach;
            Multiplayer.Ended += Detach;
        }

        private static void Attach(NetSession session)
        {
            _session = session;
            session.Message += OnMessage;
            session.Joined += OnJoined;
            session.Left += OnLeft;
            Patch();
        }

        private static void Detach(NetSession session)
        {
            session.Message -= OnMessage;
            session.Joined -= OnJoined;
            session.Left -= OnLeft;
            _session = null;
            _lastStart = null;
            Clear(true);
        }

        private static void OnJoined(NetPlayer p)
        {
            // The newcomer knows none of my soldiers: each is told again. The host also tells him the match that is on.
            foreach (Own own in Owned.Values)
            {
                own.Told = false;
            }
            if (_session != null && _session.IsHost && _lastStart != null && InMatch)
            {
                Out.Reset();
                Out.Bytes(_lastStart, 0, _lastStart.Length);
                _session.SendTo(p.Id, MatchStart, Out, true);
            }
        }

        private static void OnLeft(NetPlayer p)
        {
            foreach (Remote r in Remotes.Values.Where(r => r.Owner == p.Id).ToArray())
            {
                Remove(r);
            }
        }

        private static bool InMatch => ActorManager.instance != null && LocalPlayer.actor != null && GameManager.instance != null && !GameManager.instance.isInNoGameplayScene;

        // ---- the patches --------------------------------------------------------------------------------------------

        private static void Patch()
        {
            if (_patched)
            {
                return;
            }
            _patched = true;
            _harmony = new Harmony(PluginInfo.Guid + ".net");
            Type ai = typeof(AiActorController);
            _harmony.Patch(AccessTools.Method(ai, nameof(AiActorController.Velocity)), postfix: new HarmonyMethod(typeof(NetGame), nameof(RemoteVelocity)));
            _harmony.Patch(AccessTools.Method(ai, nameof(AiActorController.FacingDirection)), postfix: new HarmonyMethod(typeof(NetGame), nameof(RemoteFacing)));
            _harmony.Patch(AccessTools.Method(ai, nameof(AiActorController.WantsToFire)), postfix: new HarmonyMethod(typeof(NetGame), nameof(RemoteFire)));
            _harmony.Patch(AccessTools.Method(ai, nameof(AiActorController.Crouch)), postfix: new HarmonyMethod(typeof(NetGame), nameof(RemoteCrouch)));
            _harmony.Patch(AccessTools.Method(ai, nameof(AiActorController.Prone)), postfix: new HarmonyMethod(typeof(NetGame), nameof(RemoteProne)));
            _harmony.Patch(AccessTools.Method(typeof(Actor), nameof(Actor.Damage), new[] { typeof(DamageInfo) }), prefix: new HarmonyMethod(typeof(NetGame), nameof(OnDamage)));
            _harmony.Patch(AccessTools.Method(typeof(GameModeBase), nameof(GameModeBase.SpawnActorListDeferred)), prefix: new HarmonyMethod(typeof(NetGame), nameof(HoldRemotes)));
            _harmony.Patch(AccessTools.Method(typeof(GameManager), nameof(GameManager.StartLevel)), postfix: new HarmonyMethod(typeof(NetGame), nameof(OnStartLevel)));
        }

        private static void RemoteVelocity(AiActorController __instance, ref Vector3 __result)
        {
            if (!_anyRemote || !ByActor.TryGetValue(__instance.actor, out Remote r))
            {
                return;
            }
            // Where he was told to be, carried on by his speed since; the stand-in walks there (the game's own legs and ground).
            float age = Mathf.Min(Time.unscaledTime - r.Heard, 0.3f);
            Vector3 to = r.Position + r.Velocity * age - __instance.actor.Position();
            to.y = 0f;
            Vector3 v = r.Velocity + to * 5f;
            v.y = 0f;
            __result = v.sqrMagnitude < 0.01f ? Vector3.zero : Vector3.ClampMagnitude(v, 9f);
            VelocityAsked++;
            LastVelocity = __result;
        }

        private static void RemoteFacing(AiActorController __instance, ref Vector3 __result)
        {
            if (_anyRemote && ByActor.TryGetValue(__instance.actor, out Remote r))
            {
                __result = r.Facing;
            }
        }

        private static void RemoteFire(AiActorController __instance, ref bool __result)
        {
            if (_anyRemote && ByActor.TryGetValue(__instance.actor, out Remote r))
            {
                __result = r.Is(Firing);
            }
        }

        private static void RemoteCrouch(AiActorController __instance, ref bool __result)
        {
            if (_anyRemote && ByActor.TryGetValue(__instance.actor, out Remote r))
            {
                __result = r.Is(Crouched);
            }
        }

        private static void RemoteProne(AiActorController __instance, ref bool __result)
        {
            if (_anyRemote && ByActor.TryGetValue(__instance.actor, out Remote r))
            {
                __result = r.Is(Proned);
            }
        }

        /// <summary>The shooter's PC decides (see the class's summary). False: the game's own damage is not run here.</summary>
        private static bool OnDamage(Actor __instance, DamageInfo info, ref bool __result)
        {
            if (!_anyRemote || _applying || _session == null)
            {
                return true;
            }
            Actor source = info.sourceActor;
            bool sourceRemote = source != null && ByActor.ContainsKey(source);
            if (ByActor.TryGetValue(__instance, out Remote victim))
            {
                if (source != null && !sourceRemote && Owned.TryGetValue(source, out Own shooter))
                {
                    SendHit(victim, shooter.Id, info);
                }
                __result = false;
                return false;
            }
            if (sourceRemote)
            {
                __result = false;
                return false;
            }
            return true;
        }

        // ---- the match's start ---------------------------------------------------------------------------------------

        private static void OnStartLevel(MapEntryData entry, GameModeParameters parameters)
        {
            if (_startingFromNet || _session == null || !_session.IsHost || entry == null || entry.isNoGameplayScene)
            {
                return;
            }
            try
            {
                GameModeParameters p = parameters ?? GameManager.instance.gameModeParameters;
                string scene = entry.isCustomMap ? Path.GetFileName(entry.sceneName) : entry.sceneName;
                string mode = p != null && p.gameMode != null ? p.gameMode.name : "";
                Out.Reset();
                Out.Bool(entry.isCustomMap);
                Out.String(scene);
                Out.String(mode);
                Out.Bool(p != null && p.nightMode);
                Out.Byte((byte)(p != null ? Mathf.Clamp(p.playerTeam, 0, 1) : 0));
                Out.Float(p != null ? p.GetRespawnTime() : 5f);
                Out.Byte((byte)(p != null ? Mathf.Clamp(p.GetGameLength(), 0, 255) : 1));
                _lastStart = Out.ToArray();
                _session.Send(MatchStart, Out, true);
                Multiplayer.Map = !string.IsNullOrEmpty(entry.metaData.displayName) ? entry.metaData.displayName : Path.GetFileNameWithoutExtension(scene);
                Multiplayer.Mode = mode;
                Clear(false);
            }
            catch (Exception e)
            {
                Log.WarnOnce("net-start", "Multiplayer: the match's start could not be told: " + e);
            }
        }

        /// <summary>The map on this PC by the name the host gave (a built-in scene; a workshop map's file name). Null: not here.</summary>
        internal static MapEntryData FindMap(bool custom, string scene)
        {
            foreach (MapEntryData m in custom ? ModManager.AllCustomMaps() : ModManager.AllBuiltInMaps())
            {
                if (m == null || string.IsNullOrEmpty(m.sceneName))
                {
                    continue;
                }
                string name = custom ? Path.GetFileName(m.sceneName) : m.sceneName;
                if (string.Equals(name, scene, StringComparison.OrdinalIgnoreCase))
                {
                    return m;
                }
            }
            return custom ? null : MapEntryData.CreateBasicBuiltIn(scene);
        }

        private static void StartFromHost(ref NetReader r)
        {
            bool custom = r.Bool();
            string scene = r.String(512);
            string modeName = r.String(128);
            bool night = r.Bool();
            int team = r.Byte();
            float respawn = r.Float();
            int length = r.Byte();
            MapEntryData map = FindMap(custom, scene);
            if (map == null)
            {
                UI.UISystem.Instance?.Notice?.Show($"The host started on a map this PC does not have: {Path.GetFileNameWithoutExtension(scene)}. Subscribe to it and join again.", 12f);
                Log.Warn("Multiplayer: the host's map is not on this PC: " + scene);
                return;
            }
            GameModeInfo mode = GameManager.instance.officialGameModes.FirstOrDefault(m => m != null && string.Equals(m.name, modeName, StringComparison.OrdinalIgnoreCase));
            GameModeParameters p = mode != null ? GameModeParameters.CreateForGameMode(mode) : GameModeParameters.GetDefault();
            // None of this PC's own soldiers: the host's are shown here.
            p.configuration.SetInt("countTeam0", 0);
            p.configuration.SetInt("countTeam1", 0);
            p.configuration.SetFloat("respawnTime", Mathf.Clamp(respawn, 1f, 120f));
            p.configuration.SetDropdown("gameLength", length);
            p.nightMode = night;
            p.playerTeam = team;
            Clear(false);
            _startingFromNet = true;
            try
            {
                Log.Info($"Multiplayer: the host starts {scene} ({modeName}{(night ? ", night" : "")}).");
                GameManager.StartLevel(map, p);
            }
            finally
            {
                _startingFromNet = false;
            }
        }

        // ---- what is told ---------------------------------------------------------------------------------------------

        private static void OnMessage(byte kind, byte sender, ref NetReader r)
        {
            try
            {
                switch (kind)
                {
                    case MatchStart:
                        if (sender == 0 && _session != null && !_session.IsHost)
                        {
                            StartFromHost(ref r);
                        }
                        break;
                    case ActorInfo:
                    {
                        Remote remote = Get(sender, r.UShort());
                        remote.Name = r.String(64);
                        string weapon = r.String(128);
                        remote.WeaponChanged |= remote.HasInfo && weapon != remote.Weapon;
                        remote.Weapon = weapon;
                        remote.HasInfo = true;
                        break;
                    }
                    case ActorStates:
                    {
                        int count = r.Byte();
                        for (int i = 0; i < count; i++)
                        {
                            Remote remote = Get(sender, r.UShort());
                            remote.Flags = r.Byte();
                            remote.Position = r.Vector();
                            remote.Velocity = new Vector3((sbyte)r.Byte() / 10f, 0f, (sbyte)r.Byte() / 10f);
                            remote.Yaw = r.Byte() / 256f * 360f;
                            remote.Pitch = (sbyte)r.Byte() / 127f * 90f;
                            remote.Heard = Time.unscaledTime;
                            remote.HasState = true;
                        }
                        break;
                    }
                    case Hit:
                        TakeHit(sender, ref r);
                        break;
                }
            }
            catch (NetFormatException e)
            {
                Log.WarnOnce("net-format-" + kind, $"Multiplayer: a message of kind {kind} from player {sender} could not be read: {e.Message}");
            }
        }

        private static Remote Get(byte owner, ushort id)
        {
            int key = owner << 16 | id;
            if (!Remotes.TryGetValue(key, out Remote r))
            {
                r = new Remote { Owner = owner, Id = id };
                Remotes[key] = r;
            }
            return r;
        }

        private static void SendHit(Remote victim, ushort shooter, DamageInfo info)
        {
            Out.Reset();
            Out.UShort(victim.Id);
            Out.UShort(shooter);
            Out.Float(info.healthDamage);
            Out.Float(info.balanceDamage);
            Out.Byte((byte)((info.isSplashDamage ? 1 : 0) | (info.isPiercing ? 2 : 0) | (info.isCriticalHit ? 4 : 0) | (info.isKick ? 8 : 0)));
            Out.Byte((byte)info.type);
            Out.Vector(info.point);
            Out.SmallVector(info.direction);
            Out.SmallVector(info.impactForce);
            _session.SendTo(victim.Owner, Hit, Out, true);
        }

        private static void TakeHit(byte sender, ref NetReader r)
        {
            ushort victimId = r.UShort();
            ushort shooterId = r.UShort();
            float health = r.Float(), balance = r.Float();
            int flags = r.Byte();
            int type = r.Byte();
            Vector3 point = r.Vector(), direction = r.SmallVector(), force = r.SmallVector();
            Actor victim = victimId == 0 ? LocalPlayer.actor : OwnedById.TryGetValue(victimId, out Actor a) ? a : null;
            if (victim == null || victim.dead || float.IsNaN(health) || health < 0f || health > 100000f)
            {
                return;
            }
            Remotes.TryGetValue(sender << 16 | shooterId, out Remote shooter);
            var info = new DamageInfo(Enum.IsDefined(typeof(DamageInfo.DamageSourceType), type) ? (DamageInfo.DamageSourceType)type : DamageInfo.DamageSourceType.Unknown,
                                      shooter != null ? shooter.Actor : null, null)
            {
                healthDamage = health, balanceDamage = balance, isSplashDamage = (flags & 1) != 0, isPiercing = (flags & 2) != 0,
                isCriticalHit = (flags & 4) != 0, isKick = (flags & 8) != 0, point = point, direction = direction, impactForce = force
            };
            _applying = true;
            try
            {
                victim.Damage(info);
            }
            finally
            {
                _applying = false;
            }
        }

        // ---- each frame -----------------------------------------------------------------------------------------------

        /// <summary>Once a frame (after the session's own update): my soldiers told, the others' stand-ins kept.</summary>
        internal static void Update()
        {
            NetSession session = _session;
            if (session == null || session.State != NetState.Connected)
            {
                return;
            }
            Multiplayer.InMatch = InMatch;
            if (!InMatch)
            {
                if (Remotes.Count > 0 || Owned.Count > 0)
                {
                    Clear(false);
                }
                return;
            }
            if (Time.unscaledTime >= _nextSend)
            {
                _nextSend = Time.unscaledTime + SendEvery;
                if (session.PlayerCount > 1)
                {
                    TellMine(session);
                }
            }
            KeepRemotes();
        }

        private static Own OwnOf(Actor a, bool player)
        {
            if (!Owned.TryGetValue(a, out Own own))
            {
                own = new Own { Id = player ? (ushort)0 : _nextId++ };
                if (_nextId == 0)
                {
                    _nextId = 1;
                }
                Owned[a] = own;
                OwnedById[own.Id] = a;
            }
            return own;
        }

        private static void TellMine(NetSession session)
        {
            Scratch.Clear();
            Actor player = LocalPlayer.actor;
            if (player != null)
            {
                Scratch.Add(player);
            }
            if (session.IsHost)
            {
                foreach (Actor a in ActorManager.instance.actors)
                {
                    if (a != null && a.aiControlled && !ByActor.ContainsKey(a))
                    {
                        Scratch.Add(a);
                    }
                }
            }
            // What each carries and is called, when new or changed (reliable, one message each).
            foreach (Actor a in Scratch)
            {
                Own own = OwnOf(a, a == player);
                string weapon = !a.dead && a.activeWeapon != null && a.activeWeapon.weaponEntry != null ? a.activeWeapon.weaponEntry.name : "";
                if (!own.Told || (weapon != own.Weapon && !a.dead))
                {
                    own.Told = true;
                    own.Weapon = weapon;
                    Out.Reset();
                    Out.UShort(own.Id);
                    Out.String(a == player ? session.LocalInfo.Name : a.name ?? "");
                    Out.String(weapon);
                    session.Send(ActorInfo, Out, true);
                }
            }
            // Where each is: as many to a message as fit one packet.
            const int most = 60; // (19 bytes each)
            int at = 0;
            while (at < Scratch.Count)
            {
                int n = Mathf.Min(most, Scratch.Count - at);
                Out.Reset();
                Out.Byte((byte)n);
                for (int i = 0; i < n; i++)
                {
                    Actor a = Scratch[at + i];
                    ActorController c = a.controller;
                    bool alive = !a.dead;
                    int flags = (alive ? Alive : 0) | (a.team == 1 ? TeamOne : 0);
                    Vector3 velocity = Vector3.zero, facing = a.transform.forward;
                    if (alive && c != null)
                    {
                        flags |= (c.Crouch() || a.stance == Actor.Stance.Crouch ? Crouched : 0) | (a.stance == Actor.Stance.Prone ? Proned : 0) | (c.IsSprinting() ? Sprinting : 0)
                                 | (c.WantsToFire() ? Firing : 0) | (c.Aiming() ? Aimed : 0) | (a.IsSeated() ? Seated : 0);
                        velocity = a.Velocity();
                        facing = c.FacingDirection();
                    }
                    PutState(Out, Owned[a].Id, flags, a.Position(), velocity, facing);
                }
                session.Send(ActorStates, Out, false);
                at += n;
            }
            // Soldiers of mine that are gone from the game (a list rebuilt by the match): forgotten.
            if (Owned.Count > Scratch.Count + 8)
            {
                foreach (Actor gone in Owned.Keys.Where(k => k == null).ToArray())
                {
                    OwnedById.Remove(Owned[gone].Id);
                    Owned.Remove(gone);
                }
            }
        }

        /// <summary>One soldier's state as it goes over the line (19 bytes).</summary>
        internal static void PutState(NetWriter w, ushort id, int flags, Vector3 position, Vector3 velocity, Vector3 facing)
        {
            w.UShort(id);
            w.Byte((byte)flags);
            w.Vector(position);
            w.Byte((byte)(sbyte)Mathf.Clamp(Mathf.RoundToInt(velocity.x * 10f), -127, 127));
            w.Byte((byte)(sbyte)Mathf.Clamp(Mathf.RoundToInt(velocity.z * 10f), -127, 127));
            float yaw = Mathf.Atan2(facing.x, facing.z) * Mathf.Rad2Deg;
            float pitch = Mathf.Asin(Mathf.Clamp(facing.normalized.y, -1f, 1f)) * Mathf.Rad2Deg;
            w.Byte((byte)(Mathf.RoundToInt(Mathf.Repeat(yaw, 360f) / 360f * 256f) & 255));
            w.Byte((byte)(sbyte)Mathf.Clamp(Mathf.RoundToInt(pitch / 90f * 127f), -127, 127));
        }

        /// <summary>The game's spawn wave (a fresh list of one team's dead): stand-ins stay out, their owners say when they are back.</summary>
        private static void HoldRemotes(List<Actor> actorsToSpawn)
        {
            if (ByActor.Count > 0 && actorsToSpawn != null)
            {
                actorsToSpawn.RemoveAll(a => a != null && ByActor.ContainsKey(a));
            }
        }

        private static WeaponManager.LoadoutSet LoadoutOf(Remote r)
        {
            if (string.IsNullOrEmpty(r.Weapon) || WeaponManager.instance == null)
            {
                return string.IsNullOrEmpty(r.Weapon) && r.HasInfo ? new WeaponManager.LoadoutSet() : null;
            }
            WeaponManager.WeaponEntry entry = WeaponManager.instance.allWeapons.FirstOrDefault(e => e != null && e.prefab != null && e.name == r.Weapon);
            if (entry == null)
            {
                return null; // (a weapon of a mod this PC does not have: the game's own pick stands in)
            }
            var set = new WeaponManager.LoadoutSet();
            switch (entry.slot)
            {
                case WeaponManager.WeaponSlot.Primary:
                    set.primary = entry;
                    break;
                case WeaponManager.WeaponSlot.Secondary:
                    set.secondary = entry;
                    break;
                default:
                    set.gear1 = entry;
                    break;
            }
            return set;
        }

        private static void KeepRemotes()
        {
            if (Remotes.Count == 0)
            {
                _anyRemote = false;
                return;
            }
            float now = Time.unscaledTime;
            foreach (Remote r in Remotes.Values)
            {
                if (!r.HasState)
                {
                    continue;
                }
                Actor a = r.Actor;
                bool alive = r.Is(Alive) && now - r.Heard < 6f; // (silent for six seconds: off the field until heard again)
                if (a == null)
                {
                    if (!alive || !r.HasInfo)
                    {
                        continue;
                    }
                    try
                    {
                        a = ActorManager.instance.CreateAIActor(r.Team);
                        r.Actor = a;
                        ByActor[a] = r;
                        _anyRemote = true;
                        if (!string.IsNullOrEmpty(r.Name))
                        {
                            a.name = r.Name;
                        }
                    }
                    catch (Exception e)
                    {
                        Log.WarnOnce("net-standin", "Multiplayer: a stand-in could not be made: " + e);
                        continue;
                    }
                }
                if (alive && a.dead)
                {
                    if (a.team != r.Team)
                    {
                        a.SetTeam(r.Team);
                    }
                    a.SpawnAt(r.Position, Quaternion.Euler(0f, r.Yaw, 0f), LoadoutOf(r));
                    r.WeaponChanged = false;
                    if (a.controller is AiActorController ai && ai.squad == null)
                    {
                        new Squad(new[] { a }, ActorManager.RandomSpawnPointForTeam(a.team), null, null, 0f);
                    }
                }
                else if (!alive && !a.dead)
                {
                    _applying = true;
                    try
                    {
                        a.Kill(new DamageInfo(DamageInfo.DamageSourceType.Unknown, null, null) { healthDamage = 1000f, point = a.CenterPosition(), direction = -r.Facing });
                    }
                    finally
                    {
                        _applying = false;
                    }
                }
                else if (alive)
                {
                    if (r.WeaponChanged)
                    {
                        r.WeaponChanged = false;
                        WeaponManager.WeaponEntry entry = WeaponManager.instance.allWeapons.FirstOrDefault(e => e != null && e.prefab != null && e.name == r.Weapon);
                        if (entry != null)
                        {
                            a.EquipNewWeaponEntry(entry, 0, true);
                        }
                    }
                    // Far from where he was told to be (a jump, a vehicle, a fall): put there. Near: he walks it himself.
                    Vector3 off = r.Position - a.Position();
                    float flat = new Vector2(off.x, off.z).magnitude;
                    if (r.Is(Seated) || flat > 4f || Mathf.Abs(off.y) > 3f)
                    {
                        if (!a.fallenOver && !a.IsSeated())
                        {
                            a.SetPositionAndRotation(r.Position, Quaternion.Euler(0f, r.Yaw, 0f));
                        }
                    }
                }
            }
        }

        private static void Remove(Remote r)
        {
            Remotes.Remove(r.Owner << 16 | r.Id);
            Actor a = r.Actor;
            if (!ReferenceEquals(a, null))
            {
                ByActor.Remove(a);
                if (a != null && !a.dead)
                {
                    _applying = true;
                    try
                    {
                        a.KillSilently();
                    }
                    catch (Exception)
                    {
                        // (a scene on its way out)
                    }
                    finally
                    {
                        _applying = false;
                    }
                }
            }
            _anyRemote = ByActor.Count > 0;
        }

        /// <summary>Everything of the match forgotten (a new scene: the actors went with the old one; the session's end).</summary>
        private static void Clear(bool removeActors)
        {
            if (removeActors)
            {
                foreach (Remote r in Remotes.Values.ToArray())
                {
                    Remove(r);
                }
            }
            Remotes.Clear();
            ByActor.Clear();
            Owned.Clear();
            OwnedById.Clear();
            _anyRemote = false;
            _nextId = 1;
        }

        internal static void OnSceneChanged() => Clear(false);
    }
}
