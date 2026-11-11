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
        internal const byte MatchStart = 16, ActorInfo = 17, ActorStates = 18, Hit = 19, NeedInfo = 20, LobbyState = 21, Knock = 22, VrPose = 23, MatchState = 24, BackToMenu = 25, Thrown = 26, Dropped = 28, DropGone = 29, KilledBy = 30;
        private const float SendEvery = 1f / 15f;
        internal const int Alive = 1, TeamOne = 2, Crouched = 4, Proned = 8, Sprinting = 16, Firing = 32, Aimed = 64, Seated = 128;
        /// <summary>A second byte of a soldier's state (the first was full): on a ladder, off the ground (a jump, a fall), swimming.</summary>
        internal const int OnLadder = 256, Airborne = 512, Swimming = 1024;

        /// <summary>A soldier owned by another PC, and its stand-in here.</summary>
        internal sealed class Remote
        {
            public byte Owner;
            public ushort Id;
            public Actor Actor;
            public string Name = "", Weapon = "";

            /// <summary>Everything he carries, by slot (null: only the weapon in his hands was told), and the slot in his hands.</summary>
            public string[] Loadout;
            public int Slot = -1;
            public float NextSlotCheck, NextCarriedCheck;
            public int Flags;
            public Vector3 Position, Velocity;
            /// <summary>The stand-in is walking toward its told place (it stands within 12 cm then, not 25: see RemoteVelocity).</summary>
            public bool Walking;
            public float Yaw, Pitch;
            public float Heard = -1f;

            /// <summary>Since when his stand-in here lies knocked over while he is told on his feet (-1: it stands).</summary>
            public float FallenSince = -1f;
            public bool HasState, HasInfo, WeaponChanged;

            /// <summary>A VR player's head, hands and gun as his game tells them (null: none told; NetAvatar).</summary>
            public NetAvatar.Pose Pose;

            /// <summary>Another player's name over his soldier (players only: id 0 of each owner).</summary>
            public TextMesh Tag;

            public bool Is(int flag) => (Flags & flag) != 0;
            public int Team => Is(TeamOne) ? 1 : 0;
            public Vector3 Facing => Quaternion.Euler(-Pitch, Yaw, 0f) * Vector3.forward;
        }

        private sealed class Own
        {
            public ushort Id;
            public string Weapon;
            public int Carried;
            public int Team = -1;
            public bool Told;
            /// <summary>Tellings of his place since he died (the first second in full, then one in thirty).</summary>
            public int DeadTicks;
        }

        // Who is told how often (the user, 2026-11-10: "make sure we optimize multiplayer playing"). Every soldier's place went out
        // fifteen times a second, the dead and the far ones too: a host with sixty soldiers sent 17 kB a second to every player,
        // most of it about soldiers nobody could see. Now: a dead one for a second after his death, then every two seconds (so he
        // stays known); a living one farther than 150 m from every player, and not firing, five times a second; the rest as before.
        private static readonly List<Actor> ToTell = new List<Actor>();
        private static readonly List<Vector3> Eyes = new List<Vector3>();
        /// <summary>Whether <see cref="Eyes"/> begins with this PC's own player (the others' follow).</summary>
        private static bool _eyesOwnFirst;

        /// <summary>
        /// How far the nearest other player is from <paramref name="from"/> (metres), by the places gathered when the soldiers
        /// were last told (15 times a second); 0 when none is known yet (taken as near).
        /// </summary>
        internal static float NearestOtherPlayer(Vector3 from)
        {
            float best = float.MaxValue;
            for (int i = _eyesOwnFirst ? 1 : 0; i < Eyes.Count; i++)
            {
                best = Mathf.Min(best, (Eyes[i] - from).sqrMagnitude);
            }
            return best == float.MaxValue ? 0f : Mathf.Sqrt(best);
        }
        private static int _tellTick;
        private const float FarMetres = 150f;
        /// <summary>The check's: soldiers' places told in the last telling, of how many owned.</summary>
        internal static int LastTold, LastOwned;

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

        /// <summary>A match is being started here because the host started it (read by the test's own match-size patch).</summary>
        internal static bool StartingFromNet => _startingFromNet;

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
            // (Here too, not only from the VR side's scene handler: a flat player has none, his "loading" never ended and nothing
            // of the match was told or shown after the first level; scout, 2026-11-10. Run twice in VR, it only clears again.)
            UnityEngine.SceneManagement.SceneManager.activeSceneChanged += (from, to) => OnSceneChanged();
        }

        private static void Attach(NetSession session)
        {
            _session = session;
            _backPending = false;
            NetMods.Clear();
            _hostWasInMatch = false; // (left over from a game hosted before, it sent a joined player's own match back to the menu; scout)
            session.Message += OnMessage;
            session.Joined += OnJoined;
            session.Left += OnLeft;
            // (What every player tells of his soldiers goes on unreliably through the host too: relayed reliably, one lost packet
            // held every later hit and match start back on that line. Review of 2026-11-10.)
            session.ReliableKind = k => k != ActorStates && k != VrPose;
            Patch();
            // A game hosted from inside a running match: who joins is told that match (it was only told at a level's start).
            if (session.IsHost && InMatch && GameManager.instance.lastMapEntry != null)
            {
                RememberStart(GameManager.instance.lastMapEntry, GameManager.instance.gameModeParameters);
            }
        }

        private static void Detach(NetSession session)
        {
            session.Message -= OnMessage;
            session.Joined -= OnJoined;
            session.Left -= OnLeft;
            _session = null;
            Log.Guard("Multiplayer: own rules back", GiveRulesBack);
            _lastStart = null;
            _loading = false;
            _lobbyTold = "";
            Clear(true);
            // The match goes on alone: its vehicles come again (their spawners were stopped for the shared match).
            foreach (VehicleSpawner spawner in StoppedSpawners)
            {
                if (spawner != null)
                {
                    spawner.enabled = true;
                    spawner.SpawnVehicleWhenClear();
                }
            }
            StoppedSpawners.Clear();
            foreach (CapturePoint capture in HeldFlags)
            {
                if (capture != null)
                {
                    capture.canCapture = true; // (the match goes on alone: its flags are its own again)
                }
            }
            HeldFlags.Clear();
            _ended = false;
            Multiplayer.Map = "";
            Multiplayer.Mode = "";
        }

        private static readonly List<VehicleSpawner> StoppedSpawners = new List<VehicleSpawner>();
        private static bool _loading;
        /// <summary>Since when a level is said to be loading (Time.unscaledTime; -1: none).</summary>
        private static float _loadingSince = -1f;
        /// <summary>The host's "back to the menu" came while this PC was loading his match: followed once it is loaded.</summary>
        private static bool _backPending;

        /// <summary>The match that is on, as it is told to who joins (and what the list shows of it).</summary>
        private static void RememberStart(MapEntryData entry, GameModeParameters p)
        {
            // (A workshop map's file name with its mod's number: two workshop maps may share a file name, and the joiner loaded
            // the first of them it had.)
            string scene = entry.isCustomMap ? Path.GetFileName(entry.sceneName) : entry.sceneName;
            if (entry.isCustomMap && entry.sourceMod != null && entry.sourceMod.IsWorkshopItem())
            {
                scene += KeySeparator.ToString() + entry.sourceMod.workshopItemId.m_PublishedFileId;
            }
            string mode = p != null && p.gameMode != null ? p.gameMode.name : "";
            Out.Reset();
            Out.Bool(entry.isCustomMap);
            Out.String(scene, 512);
            Out.String(mode, 128);
            Out.Bool(p != null && p.nightMode);
            Out.Byte((byte)(p != null ? Mathf.Clamp(p.playerTeam, 0, 1) : 0));
            Out.Float(p != null ? p.GetRespawnTime() : 5f);
            Out.Byte((byte)(p != null ? Mathf.Clamp(p.GetGameLength(), 0, 255) : 1));
            PutRules(Out);
            _lastStart = Out.ToArray();
            Multiplayer.Map = !string.IsNullOrEmpty(entry.metaData.displayName) ? entry.metaData.displayName : Path.GetFileNameWithoutExtension(Path.GetFileName(entry.sceneName));
            Multiplayer.Mode = mode;
        }

        // ---- the host's rules: mutators and team skins ----------------------------------------------------------------------
        // Each PC started its own mutators and team skins (the host's infinite ammo or no knockdown on his PC only, a joiner's left over
        // from his own menu; the host's soldiers in another skin on each PC; modded multiplayer hunt, 2026-11-11). The host tells his with
        // the match; a joined PC plays with them (those it has: the others are named in a notice), and gets its own back when the session
        // ends. Each mutator runs with this PC's own settings for it.

        private const int MaxRules = 32;

        private static string ModKey(string name, ModInformation mod) =>
            mod != null && mod.IsWorkshopItem() ? name + KeySeparator + mod.workshopItemId.m_PublishedFileId : name ?? "";

        private static void PutRules(NetWriter w)
        {
            GameInfoContainer info = GameManager.instance != null ? GameManager.instance.gameInfo : null;
            var mutators = info != null && info.activeMutators != null ? info.activeMutators.Where(m => m != null).Take(MaxRules).ToList() : new List<MutatorEntryData>();
            w.Byte((byte)mutators.Count);
            foreach (MutatorEntryData m in mutators)
            {
                w.String(ModKey(m.name, m.sourceMod), 160);
            }
            for (int team = 0; team < 2; team++)
            {
                ActorSkin skin = info != null && info.team != null && team < info.team.Length && info.team[team] != null ? info.team[team].skin : null;
                w.String(skin != null ? ModKey(skin.name, skin.sourceMod) : "", 160);
            }
        }

        /// <summary>This PC's own rules from before the first shared match, given back when the session ends (null: none taken).</summary>
        private static List<MutatorEntryData> _ownMutators;
        private static ActorSkin[] _ownSkins;

        private static bool SameMod(ModInformation mod, ulong id) =>
            id == 0ul ? mod == null || !mod.IsWorkshopItem() : mod != null && mod.IsWorkshopItem() && mod.workshopItemId.m_PublishedFileId == id;

        private static void SplitKey(string key, out string name, out ulong mod)
        {
            int cut = key.IndexOf(KeySeparator);
            name = cut >= 0 ? key.Substring(0, cut) : key;
            mod = cut >= 0 && ulong.TryParse(key.Substring(cut + 1), out ulong id) ? id : 0ul;
        }

        private static void TakeRules(ref NetReader r)
        {
            if (r.Remaining <= 0)
            {
                return; // (a host of before 1.17.7 tells none)
            }
            int n = Mathf.Min(r.Byte(), MaxRules);
            var keys = new List<string>();
            for (int i = 0; i < n; i++)
            {
                keys.Add(r.String(160));
            }
            string[] skins = { r.String(160), r.String(160) };
            GameInfoContainer info = GameManager.instance != null ? GameManager.instance.gameInfo : null;
            ModManager mods = ModManager.instance;
            if (info == null || mods == null)
            {
                return;
            }
            if (_ownMutators == null)
            {
                _ownMutators = info.activeMutators != null ? info.activeMutators.ToList() : new List<MutatorEntryData>();
                _ownSkins = info.team != null ? info.team.Select(t => t != null ? t.skin : null).ToArray() : new ActorSkin[0];
            }
            var lacking = new List<string>();
            info.activeMutators.Clear();
            foreach (string key in keys)
            {
                SplitKey(key, out string name, out ulong mod);
                IEnumerable<MutatorEntryData> all = (mods.loadedMutators ?? new List<MutatorEntryData>()).Where(m => m != null && m.name == name);
                MutatorEntryData found = all.FirstOrDefault(m => SameMod(m.sourceMod, mod)) ?? all.FirstOrDefault();
                if (found != null)
                {
                    info.activeMutators.Add(found);
                }
                else
                {
                    lacking.Add(name);
                }
            }
            for (int team = 0; team < 2 && info.team != null && team < info.team.Length; team++)
            {
                if (info.team[team] == null || string.IsNullOrEmpty(skins[team]))
                {
                    continue;
                }
                SplitKey(skins[team], out string name, out ulong mod);
                IEnumerable<ActorSkin> all = (mods.actorSkins ?? new List<ActorSkin>()).Where(k => k != null && string.Equals(k.name, name, StringComparison.OrdinalIgnoreCase));
                ActorSkin found = all.FirstOrDefault(k => SameMod(k.sourceMod, mod)) ?? all.FirstOrDefault();
                if (found != null)
                {
                    info.team[team].skin = found;
                }
                else
                {
                    lacking.Add(name + " (skin)");
                }
            }
            Log.Info($"Multiplayer: the host's rules: {keys.Count} mutator(s), {info.activeMutators.Count} on here; skins '{skins[0]}', '{skins[1]}'"
                     + (lacking.Count > 0 ? "; not on this PC: " + string.Join(", ", lacking) : "") + ".");
            if (lacking.Count > 0)
            {
                UI.UISystem.Instance?.Notice?.Show($"The host plays with {string.Join(", ", lacking.Take(4))}{(lacking.Count > 4 ? $" and {lacking.Count - 4} more" : "")}, "
                                                   + "which this PC does not have: the match goes on without. Get the host's mods.", 12f);
            }
        }

        /// <summary>The session ended: this PC's own mutators and skins back (the host's were its only while it played with him).</summary>
        private static void GiveRulesBack()
        {
            GameInfoContainer info = GameManager.instance != null ? GameManager.instance.gameInfo : null;
            if (_ownMutators == null || info == null)
            {
                _ownMutators = null;
                return;
            }
            info.activeMutators.Clear();
            foreach (MutatorEntryData m in _ownMutators)
            {
                info.activeMutators.Add(m);
            }
            for (int team = 0; team < _ownSkins.Length && info.team != null && team < info.team.Length; team++)
            {
                if (info.team[team] != null)
                {
                    info.team[team].skin = _ownSkins[team];
                }
            }
            _ownMutators = null;
            _ownSkins = null;
        }

        private static void OnJoined(NetPlayer p)
        {
            // The newcomer knows none of my soldiers: each is told again. The host also tells him the match that is on.
            foreach (Own own in Owned.Values)
            {
                own.Told = false;
            }
            _lobbyResend = true;
            _nextLobbyTell = 0f;
            _matchTold = 0; // (the newcomer is told the flags and the end at the next telling)
            if (_session != null && _session.IsHost)
            {
                NetMods.Tell(_session, p.Id); // (the workshop mods he may lack: he is asked whether to fetch them)
            }
            if (_session != null && _session.IsHost && _lastStart != null && InMatch)
            {
                Out.Reset();
                Out.Bytes(_lastStart, 0, _lastStart.Length);
                _session.SendTo(p.Id, MatchStart, Out, true);
                StartTold.Add(p.Id);
            }
        }

        /// <summary>
        /// On the host: who has been told the match that is on. One who joined while the host's map was still loading was told nothing
        /// (the match was not in yet) and nothing told him later: he sat on "Starting ..." for the whole match (scout, 2026-11-11). He is
        /// told once the match is in.
        /// </summary>
        private static readonly HashSet<byte> StartTold = new HashSet<byte>();

        private static void OnLeft(NetPlayer p)
        {
            foreach (Remote r in Remotes.Values.Where(r => r.Owner == p.Id).ToArray())
            {
                Remove(r);
            }
            StartTold.Remove(p.Id); // (his number may be the next newcomer's: that one is told the match afresh)
            // (His dropped guns shown here go with him: he can no longer pick them up.)
            foreach (int key in ShownDrops.Keys.Where(k => k >> 16 == p.Id).ToArray())
            {
                Weapons.WeaponSystem.Instance?.Drops?.RemoveShown(ShownDrops[key]);
                ShownDrops.Remove(key);
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
            _harmony.Patch(AccessTools.Method(typeof(BattleMode), nameof(BattleMode.DrainTicket)), prefix: new HarmonyMethod(typeof(NetGame), nameof(OnDrainTicket)));
            _harmony.Patch(AccessTools.Method(typeof(Actor), nameof(Actor.SwitchWeapon)), prefix: new HarmonyMethod(typeof(NetGame), nameof(OnSwitchWeapon)));
            _harmony.Patch(AccessTools.Method(typeof(VictoryUi), nameof(VictoryUi.EndGame)), prefix: new HarmonyMethod(typeof(NetGame), nameof(OnEndGame)));
            _harmony.Patch(AccessTools.Method(typeof(ActorIk), "OnAnimatorIK"), prefix: new HarmonyMethod(typeof(NetAvatar), nameof(NetAvatar.BeforeIk)));
            _harmony.Patch(AccessTools.Method(typeof(Actor), nameof(Actor.KnockOver)), postfix: new HarmonyMethod(typeof(NetGame), nameof(OnKnockOver)));
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
            // Standing still where he was told to be: no walk. The stand-in seldom reaches the told place to the centimetre (its own
            // capsule and ground, the told speed's 0.1 m/s steps), and the pull of a few centimetres toward it read as a slow walk, so
            // a player standing still walked in place on the other PC (the user's friend, 2026-11-11). Within 25 cm while its owner
            // stands, and 12 cm once started (no flicker at the line), it stands.
            bool owner = r.Velocity.sqrMagnitude < 0.2f * 0.2f;
            float near = r.Walking ? 0.12f : 0.25f;
            if (owner && to.sqrMagnitude < near * near)
            {
                r.Walking = false;
                __result = Vector3.zero;
                VelocityAsked++;
                LastVelocity = __result;
                return;
            }
            r.Walking = true;
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

        /// <summary>
        /// A stand-in knocked over here (a blow with the stock, a thrown body, a blast: the user, 2026-11-10, "all functions should
        /// work the same same as with npcs like when I hit the other player with my stock"): its owner is told and his soldier
        /// goes down there too, with the same push.
        /// </summary>
        private static void OnKnockOver(Actor __instance, Vector3 force)
        {
            if (!_anyRemote || _applying || _session == null || !ByActor.TryGetValue(__instance, out Remote r))
            {
                return;
            }
            Out.Reset();
            Out.UShort(r.Id);
            Out.Vector(Vector3.ClampMagnitude(force, 5000f));
            _session.SendTo(r.Owner, Knock, Out, true);
        }

        /// <summary>The check's: thrown things of other players shown here.</summary>
        internal static int ThrowsShown;

        /// <summary>
        /// A thing thrown by this game's own player with his hand (a grenade, an ammo bag, a medic kit): the others are told which
        /// of his slots it came from, from where and how fast, and see it fly from his soldier's hand. What it hits is decided here,
        /// on the thrower's PC, as for every shot.
        /// </summary>
        // ---- dropped guns ---------------------------------------------------------------------------------------------
        // The player's own gun let go of in a shared match (the user's friend, 2026-11-11: "when you drop your gun there is nothing"):
        // the others are told the gun, where it is and how fast; each shows a copy that falls there and cannot be picked up (it is
        // the dropper's). When his drop goes (picked up again, back to its holster), they take theirs away.

        private static int _nextDropId;

        /// <summary>The copies shown of other players' dropped guns (key: the sender's number and the drop's).</summary>
        private static readonly Dictionary<int, Weapons.DroppedWeapon> ShownDrops = new Dictionary<int, Weapons.DroppedWeapon>();

        internal static void OnOwnDrop(Weapons.DroppedWeapon drop, Vector3 velocity)
        {
            NetSession session = _session;
            if (session == null || session.State != NetState.Connected || session.PlayerCount < 2 || drop == null || drop.Go == null || drop.Entry == null)
            {
                return;
            }
            _nextDropId = _nextDropId % 65000 + 1;
            drop.NetId = _nextDropId;
            Transform t = drop.Go.transform;
            Out.Reset();
            Out.UShort((ushort)drop.NetId);
            Out.String(WeaponKey(drop.Entry), 160);
            Out.Vector(t.position);
            Out.Rotation(t.rotation);
            Out.SmallVector(Vector3.ClampMagnitude(velocity, 30f));
            session.Send(Dropped, Out, true);
        }

        internal static void OnOwnDropGone(int id)
        {
            NetSession session = _session;
            if (session == null || session.State != NetState.Connected || session.PlayerCount < 2 || id <= 0)
            {
                return;
            }
            Out.Reset();
            Out.UShort((ushort)id);
            session.Send(DropGone, Out, true);
        }

        private static void ShowDrop(byte sender, ref NetReader r)
        {
            int key = sender << 16 | r.UShort();
            string weapon = r.String(160);
            Vector3 at = r.Vector();
            Quaternion turn = r.Rotation();
            Vector3 velocity = r.SmallVector();
            Weapons.WeaponSystem system = Weapons.WeaponSystem.Instance;
            if (!InMatch || _loading || system == null || system.Drops == null || !Finite(at) || !Finite(velocity) || ShownDrops.Count > 64)
            {
                return;
            }
            WeaponManager.WeaponEntry entry = EntryNamed(weapon);
            if (entry == null)
            {
                return; // (a weapon this PC lacks: logged once by EntryNamed)
            }
            if (ShownDrops.TryGetValue(key, out Weapons.DroppedWeapon old))
            {
                system.Drops.RemoveShown(old);
            }
            Weapons.DroppedWeapon shown = system.Drops.Show(entry, new Runtime.RigidPose(at, turn), velocity);
            if (shown != null)
            {
                ShownDrops[key] = shown;
            }
        }

        internal static void OnThrown(Projectile p, Vector3 velocity)
        {
            NetSession session = _session;
            Actor player = LocalPlayer.actor;
            if (session == null || session.State != NetState.Connected || session.PlayerCount < 2 || p == null || player == null || p.sourceWeapon == null
                || player.weapons == null || _applying)
            {
                return;
            }
            int slot = Array.IndexOf(player.weapons, p.sourceWeapon);
            if (slot < 0)
            {
                return;
            }
            Out.Reset();
            Out.Byte((byte)slot);
            Out.Vector(p.transform.position);
            Out.SmallVector(Vector3.ClampMagnitude(velocity, 60f));
            session.Send(Thrown, Out, true);
        }

        private static void ShowThrow(byte sender, ref NetReader r)
        {
            int slot = r.Byte();
            Vector3 from = r.Vector(), velocity = r.SmallVector();
            if (!Remotes.TryGetValue(sender << 16, out Remote thrower) || thrower.Actor == null || thrower.Actor.dead || !InMatch
                || float.IsNaN(velocity.x) || float.IsNaN(from.x) || velocity.sqrMagnitude > 61f * 61f)
            {
                return;
            }
            Actor a = thrower.Actor;
            // (From near his soldier, wherever the told place is: a made-up message does not drop grenades across the map.)
            if ((from - a.CenterPosition()).sqrMagnitude > 3f * 3f)
            {
                from = a.CenterPosition() + Vector3.up * 0.4f;
            }
            Weapon weapon = a.weapons != null && slot < a.weapons.Length ? a.weapons[slot] : null;
            if (weapon == null || weapon.configuration == null || weapon.configuration.projectilePrefab == null)
            {
                return;
            }
            Vector3 dir = velocity.sqrMagnitude > 1e-6f ? velocity.normalized : a.transform.forward;
            // (The weapon knows its thrower before the projectile starts: the projectile takes its side and what it may hit
            // from the weapon as it starts travelling, and a stand-in's weapon with no thrower set made one of nobody's side
            // that could hit its own; scout, 2026-11-10.)
            Actor creditBefore = weapon.killCredit;
            weapon.killCredit = a;
            Projectile p;
            try
            {
                p = weapon.SpawnProjectile(dir, from, false);
            }
            finally
            {
                weapon.killCredit = creditBefore;
            }
            if (p == null)
            {
                return;
            }
            p.killCredit = a; // (his: what it does to this PC's soldiers is dropped here and decided on his PC)
            p.velocity = velocity;
            p.transform.rotation = Quaternion.LookRotation(dir);
            if (p is RigidbodyProjectile)
            {
                Rigidbody body = p.GetComponent<Rigidbody>();
                if (body != null && !body.isKinematic)
                {
                    body.velocity = velocity;
                }
            }
            ThrowsShown++;
        }

        private static bool _switching;

        /// <summary>
        /// A stand-in holds the weapon its owner holds: its own AI's choice of another (for what it sees here) is not taken. Both
        /// switched it back and forth twice a second, and which gun was in its hands at a given moment was chance (net check,
        /// 2026-11-10: "he drew his sidearm: in the stand-in's hands 76 EAGLE" in two runs of five).
        /// </summary>
        private static bool OnSwitchWeapon(Actor __instance)
        {
            return _switching || !_anyRemote || !ByActor.TryGetValue(__instance, out Remote r) || r.Slot < 0;
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
                    if (source == LocalPlayer.actor && !__instance.dead)
                    {
                        // His hit marker, as on any soldier: the game's own Damage, which shows it, does not run on a stand-in (the
                        // hit is his owner's to apply), and a hit on another PC's soldier showed nothing (scout, 2026-11-11).
                        try
                        {
                            IngameUI.OnDamageDealt(info, new HitInfo(__instance), showHitIndicator: true);
                        }
                        catch (Exception)
                        {
                            // (the game's UI not up: no marker, nothing else)
                        }
                    }
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
                RememberStart(entry, parameters ?? GameManager.instance.gameModeParameters);
                _session.Send(MatchStart, Out, true);
                StartTold.Clear();
                foreach (NetPlayer told in _session.Players)
                {
                    StartTold.Add(told.Id);
                }
                Clear(false);
                // (Until the new scene is there the old one's soldiers are still in the game's lists: not told as the new match's.)
                _loading = true;
            }
            catch (Exception e)
            {
                Log.WarnOnce("net-start", "Multiplayer: the match's start could not be told: " + e);
            }
        }

        /// <summary>The map on this PC by the name the host gave (a built-in scene; a workshop map's file name). Null: not here.</summary>
        internal static MapEntryData FindMap(bool custom, string scene)
        {
            int cut = scene != null ? scene.IndexOf(KeySeparator) : -1;
            ulong mod = cut >= 0 && ulong.TryParse(scene.Substring(cut + 1), out ulong id) ? id : 0ul;
            if (cut >= 0)
            {
                scene = scene.Substring(0, cut);
            }
            MapEntryData first = null;
            foreach (MapEntryData m in custom ? ModManager.AllCustomMaps() : ModManager.AllBuiltInMaps())
            {
                if (m == null || string.IsNullOrEmpty(m.sceneName))
                {
                    continue;
                }
                string name = custom ? Path.GetFileName(m.sceneName) : m.sceneName;
                if (!string.Equals(name, scene, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                if (mod == 0ul || m.sourceMod != null && m.sourceMod.IsWorkshopItem() && m.sourceMod.workshopItemId.m_PublishedFileId == mod)
                {
                    return m;
                }
                first = first ?? m;
            }
            if (first != null)
            {
                Log.WarnOnce("net-othermap:" + scene, $"Multiplayer: the host's map '{scene}' of workshop {mod} is not on this PC; another mod's map of that file name is loaded.");
                return first;
            }
            return custom ? null : MapEntryData.CreateBasicBuiltIn(scene);
        }

        private static void StartFromHost(ref NetReader r)
        {
            _backPending = false; // (a new start from him: the earlier leaving is no longer the last word)
            bool custom = r.Bool();
            string scene = r.String(512);
            string modeName = r.String(128);
            bool night = r.Bool();
            int team = r.Byte();
            float respawn = r.Float();
            int length = r.Byte();
            TakeRules(ref r); // (before the level starts: the game spawns the mutators and skins the teams as it starts)
            MapEntryData map = FindMap(custom, scene);
            if (map == null)
            {
                int cut = scene.IndexOf(KeySeparator);
                string file = Path.GetFileNameWithoutExtension(cut >= 0 ? scene.Substring(0, cut) : scene);
                string where = cut >= 0 ? $" (workshop item {scene.Substring(cut + 1)})" : "";
                UI.UISystem.Instance?.Notice?.Show($"The host started on a map this PC does not have: {file}{where}. Subscribe to it and join again.", 12f);
                Log.Warn("Multiplayer: the host's map is not on this PC: " + scene);
                return;
            }
            GameModeInfo mode = Sandbox.Sandbox.ModeByName(modeName)
                                ?? GameManager.instance.officialGameModes.FirstOrDefault(m => m != null && string.Equals(m.name, modeName, StringComparison.OrdinalIgnoreCase));
            GameModeParameters p = mode != null ? GameModeParameters.CreateForGameMode(mode) : GameModeParameters.GetDefault();
            if (mode == null && !string.IsNullOrEmpty(modeName))
            {
                // (A mod's game mode this PC lacks: the game's default mode was taken with nothing said, and the two PCs played two
                // modes. Modded multiplayer hunt, 2026-11-11.)
                UI.UISystem.Instance?.Notice?.Show($"The host plays the game mode '{modeName}', which this PC does not have: the default mode is played here. Get the host's mods.", 12f);
                Log.Warn($"Multiplayer: the host's game mode '{modeName}' is not on this PC; the default mode is played here.");
            }
            // None of this PC's own soldiers: the host's are shown here.
            p.configuration.SetInt("countTeam0", 0);
            p.configuration.SetInt("countTeam1", 0);
            p.configuration.SetFloat("respawnTime", Mathf.Clamp(respawn, 1f, 120f));
            p.configuration.SetDropdown("gameLength", length);
            p.nightMode = night;
            p.playerTeam = team;
            Clear(false);
            _loading = true;
            _startingFromNet = true;
            try
            {
                Log.Info($"Multiplayer: the host starts {scene} ({modeName}{(night ? ", night" : "")}).");
                GameManager.StartLevel(map, p);
            }
            catch (Exception)
            {
                _loading = false; // (no level is loading after all: left on, nothing of the session was told or kept again until it ended)
                throw;
            }
            finally
            {
                _startingFromNet = false;
            }
        }

        // ---- what is told ---------------------------------------------------------------------------------------------

        private static void OnMessage(byte kind, byte sender, ref NetReader r)
        {
            // From a player of the session only: a state on the unreliable way may arrive after its sender's leaving was told on
            // the reliable one, and made his stand-ins anew for six seconds, a body left behind (scout, 2026-11-11).
            if (_session != null && _session.Player(sender) == null)
            {
                return;
            }
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
                        if (r.Remaining > 0)
                        {
                            // All he carries (the user, 2026-11-10: "gun hands with then gun models etc and inventory ... should be seen").
                            int slot = r.Byte();
                            int n = Mathf.Min(r.Byte(), 5);
                            var carried = new string[5];
                            for (int i = 0; i < n; i++)
                            {
                                carried[i] = r.String(128);
                            }
                            remote.WeaponChanged |= remote.HasInfo && (remote.Loadout == null || slot != remote.Slot || !carried.SequenceEqual(remote.Loadout));
                            remote.Loadout = carried;
                            remote.Slot = slot < 5 ? slot : -1;
                        }
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
                            remote.Flags |= r.Byte() << 8;
                            remote.Position = r.Vector();
                            float vx = (sbyte)r.Byte() / 10f, vz = (sbyte)r.Byte() / 10f, vy = (sbyte)r.Byte() / 5f;
                            remote.Velocity = new Vector3(vx, vy, vz);
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
                    case Thrown:
                        ShowThrow(sender, ref r);
                        break;
                    case Dropped:
                        ShowDrop(sender, ref r);
                        break;
                    case KilledBy:
                        TakeKill(sender, ref r);
                        break;
                    case DropGone:
                    {
                        int key = sender << 16 | r.UShort();
                        if (ShownDrops.TryGetValue(key, out Weapons.DroppedWeapon gone))
                        {
                            ShownDrops.Remove(key);
                            Weapons.WeaponSystem.Instance?.Drops?.RemoveShown(gone);
                        }
                        break;
                    }
                    case NetMods.Kind:
                        NetMods.Take(sender, ref r, sender == 0 && _session != null && !_session.IsHost);
                        break;
                    case BackToMenu:
                        // The host left the match for the menu: so does everyone ("just follow what he does").
                        if (sender == 0 && _session != null && !_session.IsHost && InMatch && !_loading)
                        {
                            Log.Info("Multiplayer: the host went back to the menu.");
                            UI.UISystem.Instance?.Notice?.Show("The host went back to the menu.", 4f);
                            GameManager.ReturnToMenu();
                        }
                        else if (sender == 0 && _session != null && !_session.IsHost && _loading)
                        {
                            // (Still loading the match he just left: taken once it is loaded. It was dropped, and the joined
                            // player stood in the match alone; scout, 2026-11-10.)
                            _backPending = true;
                        }
                        break;
                    case MatchState:
                        if (sender == 0 && _session != null && !_session.IsHost)
                        {
                            TakeMatchState(ref r);
                        }
                        break;
                    case VrPose:
                        NetAvatar.Read(Get(sender, 0), ref r);
                        break;
                    case Knock:
                    {
                        ushort id = r.UShort();
                        Vector3 force = r.Vector();
                        Actor mine = id == 0 ? LocalPlayer.actor : OwnedById.TryGetValue(id, out Actor own) ? own : null;
                        bool sane = !float.IsNaN(force.x) && !float.IsNaN(force.y) && !float.IsNaN(force.z) && force.sqrMagnitude <= 5000f * 5000f + 1f;
                        if (mine != null && !mine.dead && !mine.fallenOver && sane)
                        {
                            _applying = true;
                            try
                            {
                                mine.KnockOver(force);
                            }
                            finally
                            {
                                _applying = false;
                            }
                        }
                        break;
                    }
                    case LobbyState:
                        if (sender == 0 && _session != null && !_session.IsHost)
                        {
                            int n = Mathf.Min(r.Byte(), 12);
                            var lines = new string[n];
                            for (int i = 0; i < n; i++)
                            {
                                lines[i] = r.String(200);
                            }
                            Multiplayer.SetHostLines(lines);
                        }
                        break;
                    case NeedInfo:
                        // Someone sees my soldiers move and does not know what they are (he loaded the map after I told it,
                        // and a new scene forgets the old one's soldiers): each is told again.
                        foreach (Own own in Owned.Values)
                        {
                            own.Told = false;
                        }
                        break;
                }
            }
            catch (NetFormatException e)
            {
                Log.WarnOnce("net-format-" + kind, $"Multiplayer: a message of kind {kind} from player {sender} could not be read: {e.Message}");
            }
            catch (Exception e)
            {
                // (The game's own code under a message, a hit's damage or a level's start: its throw ended the session's update
                // for the frame and, over Steam, lost the rest of the batch. Review of 2026-11-10.)
                Log.WarnOnce("net-handler-" + kind, $"Multiplayer: acting on a message of kind {kind} from player {sender} threw: {e}");
            }
        }

        /// <summary>The most soldiers of one player's that are stood in for (the game's largest matches have some hundreds in all).</summary>
        private const int MaxRemotesOfOne = 300;

        private static Remote Get(byte owner, ushort id)
        {
            int key = owner << 16 | id;
            if (!Remotes.TryGetValue(key, out Remote r))
            {
                // No more of one player's soldiers than a match has: every number a message names became a stand-in, a soldier
                // made for each, a thousand in one frame from one message after another (scout, 2026-11-11). What is over the
                // count is read into a spare that is kept nowhere.
                int ofOwner = 0;
                foreach (Remote other in Remotes.Values)
                {
                    ofOwner += other.Owner == owner ? 1 : 0;
                }
                if (ofOwner >= MaxRemotesOfOne)
                {
                    Log.WarnOnce("net-remotes-" + owner, $"Multiplayer: player {owner} tells of more than {MaxRemotesOfOne} soldiers; the rest are left out.");
                    return new Remote { Owner = owner, Id = id };
                }
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

        private static bool Finite(Vector3 v) =>
            !(float.IsNaN(v.x) || float.IsNaN(v.y) || float.IsNaN(v.z) || float.IsInfinity(v.x) || float.IsInfinity(v.y) || float.IsInfinity(v.z));

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
            // (Numbers that are none: a hit's push or place that is not a number went into the ragdoll's physics as it came.)
            if (float.IsNaN(balance) || float.IsInfinity(balance))
            {
                balance = 0f;
            }
            if (!Finite(point))
            {
                point = victim.CenterPosition();
            }
            direction = Finite(direction) ? direction : Vector3.zero;
            force = Finite(force) ? Vector3.ClampMagnitude(force, 100000f) : Vector3.zero;
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
            if (victim.dead && _session != null)
            {
                // Killed by this hit: his PC is told, so the kill is counted there (the kills, the streak, the end of match's numbers;
                // the stand-in only died "of nothing" there, and a player never got a kill of another PC's soldier; scout, 2026-11-11).
                Out.Reset();
                Out.UShort(shooterId);
                Out.UShort(victimId);
                _session.SendTo(sender, KilledBy, Out, true);
            }
        }

        /// <summary>The PC of a soldier I hit says my hit killed him: counted as my kill, as the game counts its own.</summary>
        private static void TakeKill(byte sender, ref NetReader r)
        {
            ushort shooterId = r.UShort();
            ushort victimId = r.UShort();
            Actor player = LocalPlayer.actor;
            if (shooterId != 0 || player == null || GameManager.gameResultsTracker == null)
            {
                return; // (only the player's own kills are counted by the game; his soldiers' are theirs)
            }
            Remotes.TryGetValue(sender << 16 | victimId, out Remote dead);
            Actor hit = dead != null ? dead.Actor : null;
            if (hit == null)
            {
                return;
            }
            var info = new DamageInfo(DamageInfo.DamageSourceType.Projectile, player, player.activeWeapon);
            try
            {
                GameManager.gameResultsTracker.AddPlayerKill(info, hit);
            }
            catch (Exception e)
            {
                Log.WarnOnce("net-kill", "Multiplayer: a kill could not be counted: " + e.Message);
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
            // The match runs at one speed for everyone: no slow motion, no pause (a pause on one PC left its soldiers standing
            // still on all the others). (The user, 2026-11-10: "Slowmotion shouldnt work in multiplayer.")
            if (Time.timeScale != 1f && session.PlayerCount > 0)
            {
                GameManager.UnpauseGame();
                GameManager.SetTimeScale(1f);
            }
            if (session.IsHost && Time.unscaledTime >= _nextLobbyTell)
            {
                _nextLobbyTell = Time.unscaledTime + 0.5f;
                TellLobby(session);
            }
            // The host out of a match he was in (back in the menu, not loading the next one): the others are told to come along.
            // A level that never came (the start refused inside the game, the same scene started again with no change of scene):
            // loading for more than a minute and a half is over. Left on, nothing was told or kept for the rest of the session.
            if (_loading && _loadingSince < 0f)
            {
                _loadingSince = Time.unscaledTime;
            }
            else if (!_loading)
            {
                _loadingSince = -1f;
            }
            else if (Time.unscaledTime - _loadingSince > 90f)
            {
                _loading = false;
                _loadingSince = -1f;
                Log.WarnOnce("net-loading-long", "Multiplayer: a level said to be loading for 90 s; taken as loaded.");
            }
            bool inMatchNow = InMatch && !_loading;
            if (session.IsHost && _hostWasInMatch && !inMatchNow && !_loading && session.PlayerCount > 1)
            {
                Out.Reset();
                session.Send(BackToMenu, Out, true);
                _lastStart = null;
                Multiplayer.Map = "";
                Multiplayer.Mode = "";
            }
            if (session.IsHost && inMatchNow && _lastStart != null)
            {
                foreach (NetPlayer late in session.Players)
                {
                    if (late.Id != session.LocalId && StartTold.Add(late.Id))
                    {
                        Out.Reset();
                        Out.Bytes(_lastStart, 0, _lastStart.Length);
                        session.SendTo(late.Id, MatchStart, Out, true);
                    }
                }
            }
            _hostWasInMatch = inMatchNow;
            Multiplayer.InMatch = InMatch && !_loading;
            if (!InMatch || _loading)
            {
                if (Remotes.Count > 0 || Owned.Count > 0)
                {
                    Clear(false);
                }
                return;
            }
            if (_backPending)
            {
                _backPending = false;
                if (_session != null && !_session.IsHost)
                {
                    Log.Info("Multiplayer: the host went back to the menu while this PC was loading; following now.");
                    UI.UISystem.Instance?.Notice?.Show("The host went back to the menu.", 4f);
                    GameManager.ReturnToMenu();
                    return;
                }
            }
            // (A new match is told by its new player: the actor manager is the same object in every match.)
            bool newMatch = !ReferenceEquals(_vehiclesOffFor, LocalPlayer.actor);
            if (newMatch || Time.unscaledTime >= _nextVehicleSweep)
            {
                _vehiclesOffFor = LocalPlayer.actor;
                _nextVehicleSweep = Time.unscaledTime + 5f;
                NoVehicles(newMatch);
            }
            if (Time.unscaledTime >= _nextSend)
            {
                _nextSend = Time.unscaledTime + SendEvery;
                if (session.PlayerCount > 1)
                {
                    TellMine(session);
                }
            }
            NetAvatar.Tell(session);
            if (session.IsHost && Time.unscaledTime >= _nextMatchTell)
            {
                _nextMatchTell = Time.unscaledTime + 1f;
                TellMatchState(session);
            }
            KeepRemotes();
        }

        // ---- the match's state: the host's flags and the host's end --------------------------------------------------

        private static readonly List<CapturePoint> HeldFlags = new List<CapturePoint>();
        private static float _nextMatchTell, _matchToldAt;
        private static int _matchTold;
        private static bool _ended, _takingEnd;
        private static int _winner;

        /// <summary>The check's: how often the host's flags were put on this PC's, and whether the host's end was taken.</summary>
        internal static int FlagsTaken, ScoreTaken;

        /// <summary>On a PC that joined, the fallen are counted by the host: its own count of tickets is held back (the host's is shown).</summary>
        private static bool OnDrainTicket() => _session == null || _session.IsHost;
        internal static bool EndTaken;

        /// <summary>
        /// The match's end is the host's (each PC counted its own flags and losses, and could call a victory nobody else saw):
        /// on a PC that joined, the game's own end is held back; the host's is told and shown here.
        /// </summary>
        private static bool OnEndGame(int winner)
        {
            if (_session == null || _takingEnd)
            {
                return true;
            }
            if (!_session.IsHost)
            {
                return false;
            }
            _ended = true;
            _winner = winner;
            _nextMatchTell = 0f;
            return true;
        }

        /// <summary>Who holds each flag and whether the match is over, once a second from the host; sent when changed, and every five seconds.</summary>
        private static void TellMatchState(NetSession session)
        {
            SpawnPoint[] points = ActorManager.instance.spawnPoints;
            if (points == null || session.PlayerCount < 2)
            {
                return;
            }
            int hash = _ended ? 7 + _winner : 3;
            if (GameModeBase.activeGameMode is BattleMode scored && scored.tickets != null)
            {
                hash = hash * 31 + scored.tickets[0] * 7 + scored.tickets[1] * 13 + scored.remainingBattalions[0] * 101 + scored.remainingBattalions[1] * 1009;
            }
            foreach (SpawnPoint point in points)
            {
                hash = hash * 31 + (point != null ? point.owner + 2 : 0);
            }
            if (hash == _matchTold && Time.unscaledTime - _matchToldAt < 5f)
            {
                return;
            }
            _matchTold = hash;
            _matchToldAt = Time.unscaledTime;
            Out.Reset();
            Out.Byte((byte)Mathf.Min(points.Length, 255));
            for (int i = 0; i < points.Length && i < 255; i++)
            {
                Out.Byte((byte)(points[i] != null ? points[i].owner + 2 : 0));
            }
            Out.Bool(_ended);
            Out.Byte((byte)Mathf.Clamp(_winner + 2, 0, 255));
            // The score, for the mode that has one known here (Battle: battalions and tickets a side).
            if (GameModeBase.activeGameMode is BattleMode battle && battle.tickets != null)
            {
                Out.Byte(1);
                for (int team = 0; team < 2; team++)
                {
                    Out.Byte((byte)Mathf.Clamp(battle.remainingBattalions[team], 0, 255));
                    Out.VarInt(battle.tickets[team]);
                }
            }
            else
            {
                Out.Byte(0);
            }
            session.Send(MatchState, Out, true);
        }

        private static void TakeMatchState(ref NetReader r)
        {
            int n = r.Byte();
            SpawnPoint[] points = InMatch && !_loading ? ActorManager.instance.spawnPoints : null;
            for (int i = 0; i < n; i++)
            {
                int owner = r.Byte() - 2;
                // (The same map on both PCs has the same flags in the same order. A flag held differently here is given to who
                // holds it on the host's PC: this PC's own counting is only a guess between two tellings.)
                if (points != null && i < points.Length && points[i] is CapturePoint capture && capture.canCapture)
                {
                    // (This PC's own counting of who stands at a flag took it neutral and back between two tellings, with the
                    // "captured" banner each time: flags change hands here only when the host says so.)
                    capture.canCapture = false;
                    HeldFlags.Add(capture);
                }
                if (points != null && i < points.Length && points[i] != null && owner >= -1 && owner <= 1 && points[i].owner != owner)
                {
                    points[i].SetOwner(owner);
                    FlagsTaken++;
                }
            }
            bool ended = r.Bool();
            int winner = r.Byte() - 2;
            if (r.Remaining > 0 && r.Byte() == 1)
            {
                int[] battalions = new int[2], tickets = new int[2];
                for (int team = 0; team < 2; team++)
                {
                    battalions[team] = r.Byte();
                    tickets[team] = r.VarInt();
                }
                // The host's score shown here: this PC's own counting of the fallen is held back (OnDrainTicket).
                if (points != null && GameModeBase.activeGameMode is BattleMode battle && battle.tickets != null)
                {
                    for (int team = 0; team < 2; team++)
                    {
                        bool newWave = battle.remainingBattalions[team] != Mathf.Clamp(battalions[team], 0, 3);
                        battle.remainingBattalions[team] = Mathf.Clamp(battalions[team], 0, 3);
                        battle.tickets[team] = tickets[team];
                        if (newWave)
                        {
                            battle.UpdateInfantryPanels(team);
                        }
                        if (battle.remainingBattalions[team] > 0)
                        {
                            battle.UpdateTicketLabel(team);
                        }
                    }
                    ScoreTaken++;
                }
            }
            if (ended && points != null && !VictoryUi.GameHasEnded())
            {
                _takingEnd = true;
                try
                {
                    VictoryUi.EndGame(winner);
                    EndTaken = true;
                }
                finally
                {
                    _takingEnd = false;
                }
            }
        }

        private static bool _hostWasInMatch;
        private static float _nextAsk, _nextLobbyTell;
        private static string _lobbyTold = "";

        /// <summary>
        /// What the host is doing, in a few lines, for the players who joined (they see it in their panel and cannot choose
        /// themselves; the user, 2026-11-10: "only lobby host etc can change stuff ... they ... see what the lobby host changes
        /// but not tip anything (just follow what he does)"). Read from the game's own match menu twice a second, sent when changed.
        /// </summary>
        private static void TellLobby(NetSession session)
        {
            var lines = new List<string>();
            try
            {
                if (InMatch && !_loading)
                {
                    lines.Add("In a match: " + Multiplayer.Map + (string.IsNullOrEmpty(Multiplayer.Mode) ? "" : ", " + Multiplayer.Mode));
                }
                else if (_loading)
                {
                    lines.Add("Starting: " + Multiplayer.Map + (string.IsNullOrEmpty(Multiplayer.Mode) ? "" : ", " + Multiplayer.Mode));
                }
                else
                {
                    InstantActionConfigMenu menu = InstantActionConfigMenu.instance;
                    if (menu != null && menu.isActiveAndEnabled && menu.selectedMap != null)
                    {
                        MapEntryData map = menu.selectedMap;
                        string name = !string.IsNullOrEmpty(map.metaData.displayName) ? map.metaData.displayName : Path.GetFileNameWithoutExtension(map.sceneName);
                        lines.Add("Map: " + name + (map.isCustomMap ? "   (a workshop map: you need it too)" : ""));
                        lines.Add("Mode: " + (menu.selectedGameMode != null ? menu.selectedGameMode.name : "?"));
                        string eagle = menu.botAmountEagleIF != null ? menu.botAmountEagleIF.text : "?", raven = menu.botAmountRavenIF != null ? menu.botAmountRavenIF.text : "?";
                        lines.Add($"Soldiers: {eagle} against {raven}");
                        lines.Add("Time of day: " + (PreviewTerrain.instance != null && PreviewTerrain.instance.isNight ? "night" : "day"));
                        if (menu.respawnTimeIF != null)
                        {
                            lines.Add("Respawn: " + menu.respawnTimeIF.text + " s");
                        }
                    }
                    else
                    {
                        lines.Add("In the main menu, not choosing a match yet.");
                    }
                }
            }
            catch (Exception e)
            {
                Log.WarnOnce("net-lobby", "Multiplayer: the host's menu could not be read: " + e.Message);
                lines.Add("In the menu.");
            }
            string all = string.Join("\n", lines.ToArray());
            if (all == _lobbyTold && !_lobbyResend)
            {
                return;
            }
            _lobbyTold = all;
            _lobbyResend = false;
            Out.Reset();
            Out.Byte((byte)lines.Count);
            foreach (string line in lines)
            {
                Out.String(line, 200); // (by its bytes, as it is read: 180 characters of a map's name in another script were more than the reader's 200 bytes, and the whole lobby state was refused)
            }
            session.Send(LobbyState, Out, true);
            Multiplayer.SetHostLines(lines.ToArray());
        }

        private static bool _lobbyResend;
        private static Actor _vehiclesOffFor;
        private static float _nextVehicleSweep;

        /// <summary>Vehicles taken off the field in this match by the multiplayer (the check's and the log's count).</summary>
        internal static int VehiclesRemoved;

        /// <summary>
        /// A shared match is on foot for now: vehicles are not carried between the PCs, and a host's soldier driving one was a
        /// soldier sliding over the ground on every other PC, shot at from a tank nobody else saw. So in a session the map's
        /// vehicles are taken away on every PC and their spawners stopped (the fixed guns stay: a soldier at one stands at it).
        /// At the match's start, when a session begins in a running match, and every five seconds for what a script brings.
        /// The spawners are looked for once a match (a search of the whole scene: not every five seconds in the headset);
        /// the later looks only read the game's own list of vehicles.
        /// </summary>
        private static void NoVehicles(bool newMatch)
        {
            try
            {
                if (!newMatch && !ActorManager.instance.vehicles.Any(v => v != null && !v.isTurret))
                {
                    return;
                }
                foreach (VehicleSpawner spawner in UnityEngine.Object.FindObjectsOfType<VehicleSpawner>())
                {
                    if (spawner.enabled)
                    {
                        spawner.CancelInvoke();
                        spawner.StopAllCoroutines();
                        spawner.enabled = false;
                        StoppedSpawners.Add(spawner);
                    }
                }
                int removed = 0;
                foreach (Vehicle v in ActorManager.instance.vehicles.ToArray())
                {
                    if (v == null || v.isTurret)
                    {
                        continue;
                    }
                    if (v.seats != null)
                    {
                        foreach (Seat seat in v.seats)
                        {
                            if (seat != null && seat.occupant != null)
                            {
                                seat.occupant.LeaveSeat(false);
                            }
                        }
                    }
                    ActorManager.DropVehicle(v);
                    UnityEngine.Object.Destroy(v.gameObject);
                    removed++;
                }
                foreach (VehicleSpawner spawner in UnityEngine.Object.FindObjectsOfType<VehicleSpawner>())
                {
                    spawner.CancelInvoke(); // (a removed vehicle's spawner counts down to the next one)
                }
                if (removed > 0)
                {
                    VehiclesRemoved += removed;
                    Log.Info($"Multiplayer: {removed} vehicle(s) taken off the field (a shared match is on foot for now).");
                }
            }
            catch (Exception e)
            {
                Log.WarnOnce("net-vehicles", "Multiplayer: the vehicles could not be taken off the field: " + e);
            }
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
                string weapon = !a.dead && a.activeWeapon != null ? WeaponKey(a.activeWeapon.weaponEntry) : "";
                // What he carries in each slot and which is in his hands: one number for "changed" (no string built each time).
                int slot = 255, carriedHash = 17;
                if (!a.dead && a.weapons != null)
                {
                    for (int i = 0; i < a.weapons.Length && i < 5; i++)
                    {
                        Weapon w = a.weapons[i];
                        carriedHash = carriedHash * 31 + (w != null && w.weaponEntry != null && w.weaponEntry.name != null ? w.weaponEntry.name.GetHashCode() : 0);
                        if (w != null && w == a.activeWeapon)
                        {
                            slot = i;
                        }
                    }
                }
                carriedHash = carriedHash * 31 + slot;
                if (!own.Told || (!a.dead && (weapon != own.Weapon || carriedHash != own.Carried)))
                {
                    own.Told = true;
                    own.Weapon = weapon;
                    own.Carried = carriedHash;
                    Out.Reset();
                    Out.UShort(own.Id);
                    Out.String(a == player ? session.LocalInfo.Name : a.name ?? "", 64);
                    Out.String(weapon, 128);
                    Out.Byte((byte)slot);
                    Out.Byte(5);
                    for (int i = 0; i < 5; i++)
                    {
                        Weapon w = !a.dead && a.weapons != null && i < a.weapons.Length ? a.weapons[i] : null;
                        Out.String(w != null ? WeaponKey(w.weaponEntry) : "", 128);
                    }
                    session.Send(ActorInfo, Out, true);
                }
            }
            // Whose place is told this time (see ToTell).
            _tellTick++;
            Eyes.Clear();
            _eyesOwnFirst = player != null;
            if (player != null)
            {
                Eyes.Add(player.Position());
            }
            foreach (Remote r in Remotes.Values)
            {
                if (r.Id == 0 && r.Actor != null)
                {
                    Eyes.Add(r.Actor.Position());
                }
            }
            ToTell.Clear();
            foreach (Actor a in Scratch)
            {
                Own own = Owned[a];
                if (a.dead)
                {
                    own.DeadTicks++;
                    if (own.DeadTicks > 15 && own.DeadTicks % 30 != 0)
                    {
                        continue;
                    }
                }
                else
                {
                    own.DeadTicks = 0;
                    if (a != player && (_tellTick + own.Id) % 3 != 0)
                    {
                        Vector3 p = a.Position();
                        bool near = false;
                        for (int e = 0; e < Eyes.Count && !near; e++)
                        {
                            near = (Eyes[e] - p).sqrMagnitude < FarMetres * FarMetres;
                        }
                        if (!near && !FiredJustNow(a))
                        {
                            continue;
                        }
                    }
                }
                ToTell.Add(a);
            }
            LastTold = ToTell.Count;
            LastOwned = Scratch.Count;
            // Where each is: as many to a message as fit one packet.
            const int most = 60; // (19 bytes each)
            int at = 0;
            while (at < ToTell.Count)
            {
                int n = Mathf.Min(most, ToTell.Count - at);
                Out.Reset();
                Out.Byte((byte)n);
                for (int i = 0; i < n; i++)
                {
                    Actor a = ToTell[at + i];
                    ActorController c = a.controller;
                    bool alive = !a.dead;
                    int flags = (alive ? Alive : 0) | (a.team == 1 ? TeamOne : 0);
                    Vector3 velocity = Vector3.zero, facing = a.transform.forward;
                    if (alive && c != null)
                    {
                        flags |= (c.Crouch() || a.stance == Actor.Stance.Crouch ? Crouched : 0) | (a.stance == Actor.Stance.Prone ? Proned : 0) | (c.IsSprinting() ? Sprinting : 0)
                                 | (c.WantsToFire() || FiredJustNow(a) ? Firing : 0) | (c.Aiming() ? Aimed : 0) | (a.IsSeated() ? Seated : 0)
                                 | (a.IsOnLadder() ? OnLadder : 0) | (!a.IsSeated() && !a.IsOnLadder() && !c.OnGround() ? Airborne : 0) | (a.IsSwimming() ? Swimming : 0);
                        velocity = a.Velocity();
                        facing = c.FacingDirection();
                        // A VR player aims with his hand, not with his head: his soldier on the other PCs points the gun where
                        // the gun hand points it (he looked where the head looked, and shot "sideways" out of a level gun).
                        if (a == player && VRManager.Active && a.activeWeapon != null && Rig.VRRig.Instance != null && Rig.VRRig.Instance.Dominant.Tracked)
                        {
                            facing = Rig.VRRig.Instance.Dominant.TrackedPointer.Forward;
                        }
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

        /// <summary>A shot within the last fifth of a second (the gun's own clock: however the trigger was pulled, a VR hand included).</summary>
        private static bool FiredJustNow(Actor a) => a.activeWeapon != null && Time.time - a.activeWeapon.lastFiredTimestamp < 0.2f;

        /// <summary>One soldier's state as it goes over the line (19 bytes).</summary>
        internal static void PutState(NetWriter w, ushort id, int flags, Vector3 position, Vector3 velocity, Vector3 facing)
        {
            w.UShort(id);
            w.Byte((byte)flags);
            w.Byte((byte)(flags >> 8));
            w.Vector(position);
            w.Byte((byte)(sbyte)Mathf.Clamp(Mathf.RoundToInt(velocity.x * 10f), -127, 127));
            w.Byte((byte)(sbyte)Mathf.Clamp(Mathf.RoundToInt(velocity.z * 10f), -127, 127));
            w.Byte((byte)(sbyte)Mathf.Clamp(Mathf.RoundToInt(velocity.y * 5f), -127, 127)); // (up and down, 0.2 m/s steps: a jump, a fall, a ladder)
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

        /// <summary>
        /// A weapon as it is told to the other PCs: its name, and for a workshop mod's weapon the mod's number after a separator. Told by
        /// its name alone, two mods' weapons of one name ("AK-47", "M4A1", "Glock 17": common among workshop packs) were the same to the
        /// other PC, which took the first it had, often the other mod's gun (2026-11-11, modded multiplayer hunt).
        /// </summary>
        internal static string WeaponKey(WeaponManager.WeaponEntry e)
        {
            if (e == null || e.name == null)
            {
                return "";
            }
            ModInformation mod = e.sourceMod;
            return mod != null && mod.IsWorkshopItem() ? e.name + KeySeparator + mod.workshopItemId.m_PublishedFileId : e.name;
        }

        private const char KeySeparator = '\u001F';

        /// <summary>
        /// The weapon told by <see cref="WeaponKey"/>: of that mod when this PC has it, else the first of that name (another mod's gun of the
        /// same name is closer than none), else null. Asked a few times a second at most; the answer is kept by the told text.
        /// </summary>
        private static WeaponManager.WeaponEntry EntryNamed(string key)
        {
            if (string.IsNullOrEmpty(key) || WeaponManager.instance == null)
            {
                return null;
            }
            if (EntryByKey.TryGetValue(key, out WeaponManager.WeaponEntry known) && known != null && known.prefab != null)
            {
                return known;
            }
            int cut = key.IndexOf(KeySeparator);
            string name = cut >= 0 ? key.Substring(0, cut) : key;
            ulong mod = cut >= 0 && ulong.TryParse(key.Substring(cut + 1), out ulong id) ? id : 0ul;
            WeaponManager.WeaponEntry first = null, exact = null;
            foreach (WeaponManager.WeaponEntry e in WeaponManager.instance.allWeapons)
            {
                if (e == null || e.prefab == null || e.name != name)
                {
                    continue;
                }
                first = first ?? e;
                if (mod != 0ul && e.sourceMod != null && e.sourceMod.IsWorkshopItem() && e.sourceMod.workshopItemId.m_PublishedFileId == mod)
                {
                    exact = e;
                    break;
                }
            }
            WeaponManager.WeaponEntry found = exact ?? first;
            if (found == null)
            {
                Log.InfoOnce("net-noweapon:" + key, $"Multiplayer: the weapon '{name}'{(mod != 0ul ? $" (workshop {mod})" : "")} is not on this PC: its holder is shown with another.");
            }
            else if (exact == null && mod != 0ul)
            {
                Log.InfoOnce("net-otherweapon:" + key, $"Multiplayer: the weapon '{name}' of workshop {mod} is not on this PC: shown with another mod's '{name}'.");
            }
            if (EntryByKey.Count > 512)
            {
                EntryByKey.Clear();
            }
            EntryByKey[key] = found;
            return found;
        }

        /// <summary>The weapons found by their told text (cleared with the scene: its weapons are the scene's).</summary>
        private static readonly Dictionary<string, WeaponManager.WeaponEntry> EntryByKey = new Dictionary<string, WeaponManager.WeaponEntry>();

        private static WeaponManager.LoadoutSet LoadoutOf(Remote r)
        {
            if (r.Loadout != null)
            {
                // (A weapon of a mod this PC does not have leaves its slot empty; with nothing known at all the game picks.)
                var all = new WeaponManager.LoadoutSet
                {
                    primary = EntryNamed(r.Loadout[0]), secondary = EntryNamed(r.Loadout[1]), gear1 = EntryNamed(r.Loadout[2]),
                    gear2 = EntryNamed(r.Loadout[3]), gear3 = EntryNamed(r.Loadout[4])
                };
                bool told = r.Loadout.Any(n => !string.IsNullOrEmpty(n));
                bool any = all.primary != null || all.secondary != null || all.gear1 != null || all.gear2 != null || all.gear3 != null;
                if (any || !told)
                {
                    return all;
                }
                return null;
            }
            if (string.IsNullOrEmpty(r.Weapon) || WeaponManager.instance == null)
            {
                return string.IsNullOrEmpty(r.Weapon) && r.HasInfo ? new WeaponManager.LoadoutSet() : null;
            }
            WeaponManager.WeaponEntry entry = EntryNamed(r.Weapon);
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
                    if (alive && !r.HasInfo && now >= _nextAsk && _session != null)
                    {
                        _nextAsk = now + 2f;
                        Out.Reset();
                        _session.SendTo(r.Owner, NeedInfo, Out, true);
                    }
                    if (!alive || !r.HasInfo)
                    {
                        continue;
                    }
                    try
                    {
                        if (!ReferenceEquals(r.Actor, null))
                        {
                            ByActor.Remove(r.Actor); // (destroyed under us: its key goes with it)
                        }
                        a = ActorManager.instance.CreateAIActor(r.Team);
                        r.Actor = a;
                        ByActor[a] = r;
                        if (r.Id == 0)
                        {
                            NetAvatar.Register(a, r);
                        }
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
                    r.NextSlotCheck = 0f;
                    if (a.controller is AiActorController ai && ai.squad == null)
                    {
                        new Squad(new[] { a }, ActorManager.RandomSpawnPointForTeam(a.team), null, null, 0f);
                    }
                }
                else if (!alive && a.dead && !a.isScheduledToSpawn)
                {
                    // (Marked as waiting for a spawn: a map's own scripts take any dead soldier that is not, and would bring a
                    // stand-in back as their own. Actor.SpawnAt clears the mark when its owner has it alive again.)
                    a.isScheduledToSpawn = true;
                }
                else if (!alive && !a.dead)
                {
                    _applying = true;
                    try
                    {
                        if (r.Is(Alive))
                        {
                            // Alive by his owner's word, only not heard for six seconds (his PC stalled, a loss of line): taken off quietly.
                            // A kill drained the host's tickets once a soldier and left bodies for deaths that never were (scout, 2026-11-11).
                            a.KillSilently();
                        }
                        else
                        {
                            a.Kill(new DamageInfo(DamageInfo.DamageSourceType.Unknown, null, null) { healthDamage = 1000f, point = a.CenterPosition(), direction = -r.Facing });
                        }
                    }
                    finally
                    {
                        _applying = false;
                    }
                }
                if (r.Id == 0)
                {
                    KeepTag(r, a, alive);
                    if (now >= r.NextCarriedCheck)
                    {
                        r.NextCarriedCheck = now + 0.5f;
                        NetAvatar.KeepCarried(r, a, alive && !a.dead);
                    }
                }
                if (!alive || a.dead)
                {
                    continue;
                }
                if (a.team != r.Team)
                {
                    // (Switched side alive, by a map's or mod's script on its owner's PC: taken on at once, not at his next death; this PC's
                    // own soldiers took him for the other side meanwhile. Modded multiplayer hunt, 2026-11-11.)
                    a.SetTeam(r.Team);
                }
                {
                    if (r.WeaponChanged)
                    {
                        r.WeaponChanged = false;
                        r.NextSlotCheck = 0f;
                        if (r.Loadout != null)
                        {
                            for (int i = 0; i < 5 && i < a.weapons.Length; i++)
                            {
                                WeaponManager.WeaponEntry want = EntryNamed(r.Loadout[i]);
                                WeaponManager.WeaponEntry has = a.weapons[i] != null ? a.weapons[i].weaponEntry : null;
                                if (want != null && want != has)
                                {
                                    a.EquipNewWeaponEntry(want, i, false);
                                }
                            }
                        }
                        else
                        {
                            WeaponManager.WeaponEntry entry = EntryNamed(r.Weapon);
                            if (entry != null)
                            {
                                a.EquipNewWeaponEntry(entry, 0, true);
                            }
                        }
                    }
                    // The weapon in his hands is the one his owner has in his (the stand-in's own AI would pick another for
                    // what it sees): looked at twice a second.
                    if (r.Slot >= 0 && now >= r.NextSlotCheck && !a.fallenOver)
                    {
                        r.NextSlotCheck = now + 0.5f;
                        if (r.Slot < a.weapons.Length && a.weapons[r.Slot] != null && a.activeWeapon != a.weapons[r.Slot])
                        {
                            _switching = true;
                            try
                            {
                                a.SwitchWeapon(r.Slot);
                            }
                            finally
                            {
                                _switching = false;
                            }
                        }
                    }
                    // Knocked over here only (a blast, a blow, a hand: none of that is told yet, his owner has him on his feet):
                    // he lies for two and a half seconds, then stands where his owner has him. Without it he lay behind, 1.8 m
                    // off and getting up in the game's own time, while his owner's soldier walked on (net check, 2026-11-10).
                    if (a.fallenOver)
                    {
                        if (r.FallenSince < 0f)
                        {
                            r.FallenSince = now;
                        }
                        else if (now - r.FallenSince > 2.5f)
                        {
                            r.FallenSince = -1f;
                            // (Onto the ground under where he was told: stood up in the air over a slope, the game found no ground
                            // within its reach under him and dropped him again, for as long as he was told there.)
                            Vector3 stand = r.Position;
                            if (Physics.Raycast(stand + Vector3.up * 1.5f, Vector3.down, out RaycastHit under, 8f, 2232321, QueryTriggerInteraction.Ignore))
                            {
                                stand = under.point;
                            }
                            a.InstantGetUpAtPosition(stand);
                        }
                        continue;
                    }
                    r.FallenSince = -1f;
                    // A player's soldier stands turned the way the player does (his head's way when he is in VR, else his aim's): the
                    // game turns a soldier's body only into his walk, so one who had walked sideways stood sideways to his own gun,
                    // and the gun hand could not reach its grip across the chest (10 cm short in the check, 2026-11-10).
                    // A player's soldier is animated also where no camera sees him (the game stops an unseen soldier's
                    // animation: his arms were then set twice in two seconds instead of every frame and a weapon he drew stayed
                    // undrawn, until he stepped into view with the wrong gun and his arms catching up; the game's spawn resets it).
                    if (r.Id == 0)
                    {
                        a.forceAnimatorNeverCull = true;
                    }
                    if (r.Id == 0 && !a.IsSeated() && !a.IsOnLadder())
                    {
                        Vector3 way = r.Pose != null && r.Pose.Fresh ? r.Pose.HeadForward : r.Facing;
                        way.y = 0f;
                        if (way.sqrMagnitude > 1e-4f)
                        {
                            Quaternion turned = Quaternion.RotateTowards(a.transform.rotation, Quaternion.LookRotation(way, Vector3.up), 540f * Time.deltaTime);
                            a.transform.rotation = turned;
                            if (a.rigidbody != null)
                            {
                                a.rigidbody.rotation = turned;
                            }
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
                    else if ((r.Is(OnLadder) || r.Is(Airborne) || r.Is(Swimming) && Mathf.Abs(off.y) > 0.3f) && !a.fallenOver && !a.IsSeated())
                    {
                        // Up a ladder, in a jump or a fall: carried where he is told to be, his height too (his legs walk on the ground
                        // only: a climber walked beside the ladder and a jumper dropped back down in steps of 3 m; scout, 2026-11-11).
                        float age = Mathf.Min(Time.unscaledTime - r.Heard, 0.3f);
                        Vector3 want = r.Position + r.Velocity * age;
                        Vector3 at = Vector3.Lerp(a.Position(), want, 1f - Mathf.Exp(-12f * Time.deltaTime));
                        a.SetPositionAndRotation(at, a.transform.rotation);
                    }
                }
            }
        }

        /// <summary>
        /// The other player's name over his soldier's head, turned to the eyes, the size held as seen (so it reads at 5 m and at
        /// 80): among thirty soldiers of the same side he could not be told apart. Hidden while he is dead.
        /// </summary>
        private static void KeepTag(Remote r, Actor a, bool alive)
        {
            bool show = alive && !a.dead && !string.IsNullOrEmpty(r.Name);
            if (r.Tag == null)
            {
                if (!show)
                {
                    return;
                }
                var go = new GameObject("RavenfieldVR Player Name");
                r.Tag = go.AddComponent<TextMesh>();
                Font font = Resources.GetBuiltinResource<Font>("Arial.ttf");
                r.Tag.font = font;
                go.GetComponent<MeshRenderer>().sharedMaterial = font.material;
                r.Tag.anchor = TextAnchor.LowerCenter;
                r.Tag.alignment = TextAlignment.Center;
                r.Tag.fontSize = 64;
                r.Tag.characterSize = 0.1f;
                r.Tag.color = new Color(1f, 1f, 1f, 0.9f);
            }
            GameObject tag = r.Tag.gameObject;
            if (tag.activeSelf != show)
            {
                tag.SetActive(show);
            }
            if (!show)
            {
                return;
            }
            if (r.Tag.text.Length != r.Name.Length || r.Tag.text != r.Name)
            {
                r.Tag.text = r.Name;
            }
            Camera eyes = Camera.main;
            Vector3 at = a.CenterPosition() + Vector3.up * 1.15f;
            Transform t = tag.transform;
            t.position = at;
            if (eyes != null)
            {
                Vector3 away = at - eyes.transform.position;
                float far = away.magnitude;
                if (far > 0.01f)
                {
                    t.rotation = Quaternion.LookRotation(away, Vector3.up);
                }
                // (About 1.1 degrees tall at any distance, never smaller than at 4 m. The letters are 0.64 units tall unscaled:
                // character size 0.1 at font size 64. At 0.0045 a unit of distance the name was 2.6 cm tall at 9 m and not to be
                // found in the check's picture, 2026-11-10.)
                t.localScale = Vector3.one * (Mathf.Max(far, 4f) * 0.05f); // (0.03 read 0.4 degrees in the next picture: small letters are half the line)
            }
        }

        private static void Remove(Remote r)
        {
            if (r.Tag != null)
            {
                UnityEngine.Object.Destroy(r.Tag.gameObject);
                r.Tag = null;
            }
            NetAvatar.Forget(r);
            Remotes.Remove(r.Owner << 16 | r.Id);
            Actor a = r.Actor;
            if (!ReferenceEquals(a, null))
            {
                ByActor.Remove(a);
                if (a != null && a.dead)
                {
                    a.isScheduledToSpawn = true;
                }
                if (a != null && !a.dead)
                {
                    _applying = true;
                    try
                    {
                        a.KillSilently();
                        // (And never a soldier of the match again: the next spawn wave took a leaver's soldier for one of the
                        // match's dead and brought him back as a plain bot, which the host then told to everyone as his own.)
                        a.isScheduledToSpawn = true;
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
            foreach (Remote r in Remotes.Values)
            {
                if (r.Tag != null)
                {
                    UnityEngine.Object.Destroy(r.Tag.gameObject);
                    r.Tag = null;
                }
            }
            NetAvatar.Clear();
            Remotes.Clear();
            ByActor.Clear();
            Owned.Clear();
            OwnedById.Clear();
            _anyRemote = false;
            _nextId = 1;
        }

        internal static void OnSceneChanged()
        {
            _loading = false;
            EntryByKey.Clear(); // (the weapons found by name are the last scene's)
            ShownDrops.Clear(); // (their copies went with the scene)
            _ended = false;
            _matchTold = 0;
            HeldFlags.Clear(); // (they went with the old scene)
            StoppedSpawners.Clear(); // (they went with the old scene)
            Clear(false);
        }
    }
}
