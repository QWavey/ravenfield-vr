using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RavenfieldVR.Core;
using UnityEngine;

namespace RavenfieldVR.Net
{
    /// <summary>
    /// Vehicles in a shared match (cars, tanks, boats, helicopters, planes; not the fixed turrets, each PC keeps the map's own).
    /// Asked for by the user after playing 1.17.8 with a friend: "cars and other stuff like tanks arent synced ... can you please sync
    /// the cars etc" (Tasks 383.11). They were taken off every PC before (Tasks 373).
    ///
    /// The host's vehicles are the match's: his spawners make them, his game numbers each and tells the others which vehicle it is
    /// (its prefab's name), and every other PC makes a copy of it from its own copy of that prefab (its own vehicles and spawners are
    /// stopped). Each vehicle has ONE owner PC whose physics drive it: the host, or the PC whose player sits in its driver's seat
    /// (he takes it on entering, gives it back to the host on leaving). The owner tells where it is, how fast, its health and its
    /// death 15 times a second; on every other PC its rigidbody is kinematic and follows. Who sits where is told by each soldier's own
    /// PC, and a stand-in is seated only by that word (its own AI does not take seats). A hit on a vehicle is decided by the shooter's
    /// PC and sent to the vehicle's owner, as a hit on a soldier is.
    /// </summary>
    internal static class NetVehicles
    {
        /// <summary>Vehicles shared (off: taken off the field in a shared match, as before 1.17.10).</summary>
        internal static bool Enabled = true;

        private const float StateEvery = 1f / 15f, InfoEvery = 3f, SeatEvery = 0.2f, SeatResendEvery = 3f;
        private const int StatesPerMessage = 24;

        private sealed class Shared
        {
            public ushort Id;
            public Vehicle V;
            public string Key = "";
            public byte Owner;
            public bool Copy, WasKinematic, HasState;
            /// <summary>When it was last brought back here (a revive the game left dead was tried again at every state, 15 a second; scout).</summary>
            public float ReviveTriedAt = -10f;
            public Vector3 Pos, Vel, AngVel;
            public Quaternion Rot = Quaternion.identity;
            public float Heard = -10f, Health = -1f, OwnerSince;
            /// <summary>Where it was shown against where the newest word puts it, faded out (see Follow).</summary>
            public Vector3 Offset;
            /// <summary>Its told acceleration, averaged (a jolt is the told one against it: VehicleFeel.OnJolt).</summary>
            public Vector3 AccelAverage;
            public Quaternion TurnOffset = Quaternion.identity;
            public bool ToldDead;
            public int Team = -1;
        }

        private static readonly Dictionary<ushort, Shared> ById = new Dictionary<ushort, Shared>();
        private static readonly Dictionary<Vehicle, Shared> ByVehicle = new Dictionary<Vehicle, Shared>();
        private static readonly HashSet<ushort> Gone = new HashSet<ushort>();
        private static readonly Dictionary<Actor, int> SeatTold = new Dictionary<Actor, int>();
        private static readonly List<Shared> Scratch = new List<Shared>();
        private static readonly NetWriter Out = new NetWriter(1400);
        private static readonly Dictionary<string, GameObject> Catalog = new Dictionary<string, GameObject>(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> Missing = new HashSet<string>();
        private static ushort _nextId = 1;
        private static float _nextState, _nextInfo, _nextSeat, _nextSeatResend, _catalogAt = -100f;
        private static bool _patched, _applyingHit;
        private static float _matchSeenAt = -1f, _nextFirstSeenPrune;
        private static readonly List<Vehicle> Scratch2 = new List<Vehicle>();

        /// <summary>A stand-in is being seated or unseated by its owner's word (the patches let it through).</summary>
        internal static bool Seating;

        /// <summary>The check's and the log's: copies made here, hits passed to an owner, owners taken.</summary>
        internal static int CopiesMade, HitsSent, OwnersTaken;

        internal static bool IsShared(Vehicle v) => v != null && ByVehicle.ContainsKey(v);

        /// <summary>The check's: the vehicle shown here under the host's number (null: none).</summary>
        internal static Vehicle VehicleOf(ushort id) => ById.TryGetValue(id, out Shared s) ? s.V : null;

        internal static int Count => ById.Count;

        // ---- the patches --------------------------------------------------------------------------------------------

        internal static void Patch(Harmony harmony)
        {
            if (_patched || harmony == null)
            {
                return;
            }
            _patched = true;
            harmony.Patch(AccessTools.Method(typeof(Vehicle), nameof(Vehicle.Damage)), prefix: new HarmonyMethod(typeof(NetVehicles), nameof(OnVehicleDamage)));
            harmony.Patch(AccessTools.Method(typeof(Actor), nameof(Actor.EnterSeat)), prefix: new HarmonyMethod(typeof(NetVehicles), nameof(OnEnterSeat)),
                          finalizer: new HarmonyMethod(typeof(NetVehicles), nameof(AfterEnterSeat)));
            harmony.Patch(AccessTools.Method(typeof(Actor), nameof(Actor.LeaveSeat)), prefix: new HarmonyMethod(typeof(NetVehicles), nameof(OnLeaveSeat)));
            harmony.Patch(AccessTools.Method(typeof(Vehicle), "FixedUpdate"), postfix: new HarmonyMethod(typeof(NetVehicles), nameof(AfterFixedUpdate)));
            harmony.Patch(AccessTools.Method(typeof(Actor), nameof(Actor.SwitchSeat)), prefix: new HarmonyMethod(typeof(NetVehicles), nameof(OnSwitchSeat)));
            harmony.Patch(AccessTools.Method(typeof(Actor), nameof(Actor.OnGotVehicleKill)), prefix: new HarmonyMethod(typeof(NetVehicles), nameof(OnGotVehicleKill)));
            harmony.Patch(AccessTools.Method(typeof(Vehicle), nameof(Vehicle.Die)), prefix: new HarmonyMethod(typeof(NetVehicles), nameof(OnVehicleDie)));
        }

        /// <summary>
        /// A vehicle killed here by another PC's player (his stand-in the damage's source): not this PC's to show or count. A player's
        /// stand-in is not AI, so the game put "DESTROYED ..." on this player's screen for a kill he did not make (vehicle scout,
        /// 2026-11-12); the killer's own PC credits it (<see cref="NetGame.VehicleKilled"/>) and the shared scoreboard carries it.
        /// </summary>
        private static bool OnGotVehicleKill(Actor __instance) => !NetGame.IsRemote(__instance);

        /// <summary>Whether the owner's player, as his PC last told, sits in its driver's seat.</summary>
        private static bool OwnerDrives(Shared s)
        {
            NetGame.Remote taker = NetGame.RemoteOf(s.Owner, 0);
            return taker != null && taker.SeatVehicle == s.Id && taker.SeatIndex == 0;
        }

        private static readonly AccessTools.FieldRef<Vehicle, Vector3> CachedVelocity = AccessTools.FieldRefAccess<Vehicle, Vector3>("cachedVelocity");
        private static readonly AccessTools.FieldRef<Vehicle, Vector3> CachedLocalVelocity = AccessTools.FieldRefAccess<Vehicle, Vector3>("cachedLocalVelocity");

        /// <summary>
        /// A vehicle another PC drives reads its told speed here: kinematic, its rigidbody's speed is none, and its wheels, engine
        /// sound and animations stood still while it drove along.
        /// </summary>
        private static void AfterFixedUpdate(Vehicle __instance)
        {
            if (!Enabled || ById.Count == 0 || !ByVehicle.TryGetValue(__instance, out Shared s) || !s.HasState || __instance.dead)
            {
                return;
            }
            NetSession session = NetGame.SessionNow;
            if (session == null || s.Owner == session.LocalId)
            {
                return;
            }
            CachedVelocity(__instance) = s.Vel;
            CachedLocalVelocity(__instance) = __instance.transform.InverseTransformDirection(s.Vel);
            // (Its acceleration as told: the game took it from a kinematic body's zero speed against the told one, about -speed/dt, a
            // thousand m/s2 at 20 m/s, which the seat cameras' springs read; multiplayer scout.)
            __instance.acceleration = s.AccelAverage;
        }

        /// <summary>A stand-in takes a seat only by its owner's word: its own AI walked it into a vehicle nobody else saw it in.</summary>
        private static bool OnEnterSeat(Actor __instance, Seat seat, bool kickOutOccupant, ref bool __result, out bool __state)
        {
            __state = false;
            if (!Enabled)
            {
                return true;
            }
            if (!Seating && NetGame.IsRemote(__instance))
            {
                __result = false;
                return false;
            }
            // A seat another PC's soldier sits in (his player, or one of the host's soldiers) is his: this PC's player does not take it.
            // The game puts the one sitting there out, but only here: on his own PC he still sat there, and two players sat in one seat
            // (the user, 2026-11-11: "A player can go into another players seat without kicking him out"). The game's own seat change is refused before
            // it moves anything (OnSwitchSeat); a free seat is taken as ever.
            if (!Seating && seat != null && seat.occupant != null && seat.occupant != __instance && NetGame.IsRemote(seat.occupant))
            {
                if (__instance == LocalPlayer.actor)
                {
                    UI.UISystem.Instance?.Notice?.Show(NetGame.RemoteOfActor(seat.occupant)?.Id == 0 ? "That seat is taken by another player." : "That seat is taken.", 2f);
                }
                __result = false;
                return false;
            }
            return true;
        }

        /// <summary>
        /// The game's own seat change (its seat keys) into a seat another PC's soldier holds: refused before anything moves. The game
        /// leaves the seat first and puts the one sitting there out by its own way, past EnterSeat: the player sat there and the other
        /// soldier stood stranded here while his PC still seated him (vehicle scout, 2026-11-11).
        /// </summary>
        private static bool OnSwitchSeat(Actor __instance, int seatIndex, ref bool __result)
        {
            if (!Enabled || Seating || __instance != LocalPlayer.actor || __instance.seat == null || __instance.seat.vehicle == null)
            {
                return true;
            }
            List<Seat> seats = __instance.seat.vehicle.seats;
            Seat target = seatIndex >= 0 && seats != null && seatIndex < seats.Count ? seats[seatIndex] : null;
            if (target == null || target == __instance.seat || target.occupant == null || !NetGame.IsRemote(target.occupant))
            {
                return true;
            }
            UI.UISystem.Instance?.Notice?.Show(NetGame.RemoteOfActor(target.occupant)?.Id == 0 ? "That seat is taken by another player." : "That seat is taken.", 2f);
            __result = false;
            return false;
        }

        private static System.Exception AfterEnterSeat(System.Exception __exception, bool __state)
        {
            if (__state)
            {
                Seating = false;
            }
            return __exception;
        }

        /// <summary>Nor leaves one on its own (its AI's own wish to get out); a fall, a death or a wreck still put it out.</summary>
        private static bool OnLeaveSeat(Actor __instance, bool forcedByFallingOver)
        {
            if (!Enabled || Seating || forcedByFallingOver || NetGame.Applying || !NetGame.IsRemote(__instance) || __instance.dead)
            {
                return true;
            }
            Seat seat = __instance.seat;
            return seat == null || seat.vehicle == null || seat.vehicle.dead || !ByVehicle.ContainsKey(seat.vehicle);
        }

        /// <summary>
        /// A hit on a shared vehicle: applied by its owner only. A hit from this PC's own soldier on another PC's vehicle is sent to
        /// that owner (with the hit marker here); a stand-in's own hit is its owner PC's to tell; a crash or a fire on a copy is the owner's.
        /// </summary>
        private static bool OnVehicleDamage(Vehicle __instance, DamageInfo info)
        {
            if (!Enabled || _applyingHit || !ByVehicle.TryGetValue(__instance, out Shared s))
            {
                return true;
            }
            NetSession session = NetGame.SessionNow;
            if (session == null)
            {
                return true;
            }
            Actor source = info.sourceActor;
            if (source != null && NetGame.IsRemote(source))
            {
                return false;
            }
            if (s.Owner == session.LocalId)
            {
                return true;
            }
            if (source != null && NetGame.OwnedId(source, out ushort shooter) && !__instance.dead)
            {
                Out.Reset();
                Out.UShort(s.Id);
                Out.Byte(session.LocalId); // (the shooter's PC: a hit passed on by an owner who has just handed the vehicle on keeps it)
                Out.UShort(shooter);
                Out.Float(info.healthDamage);
                Out.Float(info.balanceDamage);
                Out.Byte((byte)((info.isSplashDamage ? 1 : 0) | (info.isPiercing ? 2 : 0) | (info.isCriticalHit ? 4 : 0)));
                Out.Byte((byte)info.type);
                Out.Vector(info.point);
                Out.SmallVector(info.direction);
                session.SendTo(s.Owner, NetGame.VehicleHit, Out, true);
                HitsSent++;
                if (source == LocalPlayer.actor)
                {
                    try
                    {
                        IngameUI.OnDamageDealt(info, new HitInfo(__instance), showHitIndicator: true);
                    }
                    catch (Exception)
                    {
                        // (the game's UI not up: no marker)
                    }
                }
            }
            return false;
        }

        // ---- each frame -------------------------------------------------------------------------------------------------

        /// <summary>In a match, once a frame (NetGame.Update).</summary>
        internal static void Update(NetSession session)
        {
            if (!Enabled || session == null || ActorManager.instance == null)
            {
                return;
            }
            byte me = session.LocalId;
            if (_matchSeenAt < 0f)
            {
                _matchSeenAt = Time.unscaledTime;
            }
            if (FirstSeen.Count > 0 && Time.unscaledTime >= _nextFirstSeenPrune)
            {
                // (Destroyed ones out: kept, each held its vehicle for the scene; scout.)
                _nextFirstSeenPrune = Time.unscaledTime + 10f;
                Scratch2.Clear();
                foreach (Vehicle v in FirstSeen.Keys)
                {
                    if (v == null)
                    {
                        Scratch2.Add(v);
                    }
                }
                foreach (Vehicle v in Scratch2)
                {
                    FirstSeen.Remove(v);
                }
            }
            if (session.IsHost)
            {
                Register(session);
            }
            // Gone: destroyed here (cleaned up after its death, or taken off by a script).
            Scratch.Clear();
            foreach (Shared s in ById.Values)
            {
                if (s.V == null)
                {
                    Scratch.Add(s);
                }
            }
            foreach (Shared s in Scratch)
            {
                // (By the host, or by the PC that drove it: gone on its owner's PC, the host kept a frozen ghost that fell back in
                // as his own 5 s later; vehicle scout.)
                bool tell = session.IsHost || s.Owner == session.LocalId;
                Forget(s);
                if (tell)
                {
                    Out.Reset();
                    Out.UShort(s.Id);
                    session.Send(NetGame.VehicleGone, Out, true);
                }
            }
            TakeOrGive(session, me);
            float now = Time.unscaledTime;
            foreach (Shared s in ById.Values)
            {
                Rigidbody rb = s.V.rigidbody;
                bool mine = s.Owner == me;
                if (rb != null && !s.V.dead)
                {
                    bool kinematic = mine ? s.WasKinematic : true;
                    if (rb.isKinematic != kinematic)
                    {
                        rb.isKinematic = kinematic;
                        if (!kinematic && s.HasState)
                        {
                            rb.velocity = s.Vel; // (taken over moving: it goes on as it went)
                            rb.angularVelocity = s.AngVel;
                        }
                    }
                }
                if (!mine && s.HasState)
                {
                    Follow(s, now);
                }
            }
            if (Time.unscaledTime >= _nextState && session.PlayerCount > 1)
            {
                _nextState = Time.unscaledTime + StateEvery;
                TellStates(session, me);
            }
            if (Time.unscaledTime >= _nextSeat && session.PlayerCount > 1)
            {
                _nextSeat = Time.unscaledTime + SeatEvery;
                bool all = Time.unscaledTime >= _nextSeatResend;
                if (all)
                {
                    _nextSeatResend = Time.unscaledTime + SeatResendEvery;
                }
                TellSeats(session, all);
            }
            SeatRemotes(session);
        }

        /// <summary>The host numbers each vehicle of his match as it comes (spawned, or the map's own), and tells everyone which it is.</summary>
        private static void Register(NetSession session)
        {
            List<Vehicle> list = ActorManager.instance.vehicles;
            if (list != null)
            {
                for (int i = 0; i < list.Count; i++)
                {
                    Vehicle v = list[i];
                    if (v == null || ByVehicle.ContainsKey(v) || !v.gameObject.activeInHierarchy)
                    {
                        continue;
                    }
                    var s = new Shared { Id = _nextId++, V = v, Key = KeyOf(v), Owner = 0, WasKinematic = v.rigidbody != null && v.rigidbody.isKinematic };
                    if (_nextId == 0)
                    {
                        _nextId = 1;
                    }
                    ById[s.Id] = s;
                    ByVehicle[v] = s;
                    TellInfo(session, s, NetSession.Everyone);
                }
            }
            if (Time.unscaledTime >= _nextInfo && session.PlayerCount > 1)
            {
                // Again every few seconds: who joined or loaded since is told too (the ones he knows are passed over).
                _nextInfo = Time.unscaledTime + InfoEvery;
                foreach (Shared s in ById.Values)
                {
                    if (s.V != null && !s.V.dead)
                    {
                        TellInfo(session, s, NetSession.Everyone);
                    }
                }
            }
        }

        private static void TellInfo(NetSession session, Shared s, byte to)
        {
            Out.Reset();
            Out.UShort(s.Id);
            Out.String(s.Key, 128);
            Out.Byte(s.Owner);
            Out.Bool(s.V.dead);
            Out.Vector(s.V.transform.position);
            Out.Rotation(s.V.transform.rotation);
            Out.Bool(s.V.isTurret);
            session.SendTo(to, NetGame.VehicleInfo, Out, true);
        }

        /// <summary>
        /// This PC's player in a driver's seat takes the vehicle (his physics drive it while he does); out of it again, a vehicle this
        /// PC took goes back to the host.
        /// </summary>
        private static void TakeOrGive(NetSession session, byte me)
        {
            Actor player = LocalPlayer.actor;
            Seat seat = player != null && !player.dead ? player.seat : null;
            Vehicle driven = seat != null && seat.IsDriverSeat() ? seat.vehicle : null;
            // (A joined PC takes it only from the host: two joined PCs' players each in the driver's seat of their own copy took it
            // from each other every frame. The host takes it from anyone: his word holds.)
            // (Not within a second of the host's taking it back: both players in the one driver's seat at once, the two took it from each
            // other at every word until the refusal arrived; multiplayer scout.)
            if (driven != null && !driven.dead && ByVehicle.TryGetValue(driven, out Shared take) && take.Owner != me
                && (session.IsHost || take.Owner == 0 && Time.unscaledTime - take.OwnerSince > 1f))
            {
                SetOwner(session, take, me, tell: true);
                OwnersTaken++;
            }
            if (session.IsHost)
            {
                // A vehicle another took and nobody of his drives here for a while (his word of giving it back lost, his game stalled):
                // the host's again.
                float now = Time.unscaledTime;
                Scratch.Clear();
                foreach (Shared s in ById.Values)
                {
                    if (s.Owner != me && now - s.OwnerSince > 5f && s.V != null)
                    {
                        Actor driver = s.V.seats.Count > 0 ? s.V.seats[0].occupant : null;
                        NetGame.Remote taker = NetGame.RemoteOf(s.Owner, 0);
                        bool saysDriving = taker != null && taker.SeatVehicle == s.Id && taker.SeatIndex == 0;
                        if ((driver == null || !NetGame.IsRemote(driver)) && !saysDriving)
                        {
                            Scratch.Add(s);
                        }
                    }
                }
                foreach (Shared s in Scratch)
                {
                    SetOwner(session, s, me, tell: true);
                }
                return;
            }
            Scratch.Clear();
            foreach (Shared s in ById.Values)
            {
                if (s.Owner == me && s.V != driven)
                {
                    Scratch.Add(s);
                }
            }
            foreach (Shared s in Scratch)
            {
                SetOwner(session, s, 0, tell: true);
            }
        }

        private static void SetOwner(NetSession session, Shared s, byte owner, bool tell, bool force = false)
        {
            if (s.Owner == owner && !force)
            {
                return;
            }
            s.Owner = owner;
            s.OwnerSince = Time.unscaledTime;
            if (tell)
            {
                Out.Reset();
                Out.UShort(s.Id);
                Out.Byte(owner);
                // (Where it is and how fast as it changes hands: the new owner goes on from there, not from a state a moment old.)
                Rigidbody rb = s.V != null ? s.V.rigidbody : null;
                Out.Vector(s.V != null ? s.V.transform.position : s.Pos);
                Out.Rotation(s.V != null ? s.V.transform.rotation : s.Rot);
                Out.SmallVector(rb != null && !rb.isKinematic ? rb.velocity : s.Vel);
                session.Send(NetGame.VehicleOwner, Out, true);
            }
        }

        /// <summary>A vehicle another PC drives: where it was told to be, carried on by its speed since, eased (the tells come 15 times a second).</summary>
        private static void Follow(Shared s, float now)
        {
            Transform t = s.V.transform;
            float age = Mathf.Min(now - s.Heard, 0.25f);
            Vector3 want = s.Pos + s.Vel * age;
            Quaternion turn = s.AngVel.sqrMagnitude > 1e-6f ? Quaternion.AngleAxis(s.AngVel.magnitude * Mathf.Rad2Deg * age, s.AngVel.normalized) * s.Rot : s.Rot;
            // Where the word puts it NOW (its told place carried on by its told speed), with what it was shown at against the newest
            // word fading out in a tenth of a second. Eased toward the word before, it trailed behind by its speed over 14: at a plane's
            // 100 m/s seven metres, a rider's view lagging with it.
            float fade = Mathf.Exp(-10f * Time.deltaTime);
            s.Offset *= fade;
            s.TurnOffset = Quaternion.Slerp(Quaternion.identity, s.TurnOffset, fade);
            Vector3 at = want + s.Offset;
            Quaternion rot = s.TurnOffset * turn;
            t.SetPositionAndRotation(at, rot);
            if (s.V.rigidbody != null)
            {
                s.V.rigidbody.position = at;
                s.V.rigidbody.rotation = rot;
            }
        }

        private static int _stateTick;

        private static bool Idle(Vehicle v)
        {
            if (v.AnySeatsTaken() || v.burning)
            {
                return false;
            }
            Rigidbody rb = v.rigidbody;
            return v.isTurret || rb == null || rb.isKinematic || rb.velocity.sqrMagnitude < 0.05f && rb.angularVelocity.sqrMagnitude < 0.01f;
        }

        private static void TellStates(NetSession session, byte me)
        {
            Scratch.Clear();
            _stateTick++;
            foreach (Shared s in ById.Values)
            {
                // (A turret or a parked vehicle nobody sits in stands still: told three times a second, not fifteen; a map has a score.)
                if (s.Owner == me && s.V != null && (!Idle(s.V) || _stateTick % 5 == 0))
                {
                    Scratch.Add(s);
                }
            }
            int at = 0;
            while (at < Scratch.Count)
            {
                int n = Mathf.Min(StatesPerMessage, Scratch.Count - at);
                Out.Reset();
                Out.Byte((byte)n);
                for (int i = 0; i < n; i++)
                {
                    Shared s = Scratch[at + i];
                    Vehicle v = s.V;
                    Rigidbody rb = v.rigidbody;
                    Out.UShort(s.Id);
                    Out.Vector(v.transform.position);
                    Out.Rotation(v.transform.rotation);
                    Out.SmallVector(rb != null && !rb.isKinematic ? Vector3.ClampMagnitude(rb.velocity, 400f) : Vector3.zero);
                    Out.SmallVector(rb != null && !rb.isKinematic ? Vector3.ClampMagnitude(rb.angularVelocity, 50f) : Vector3.zero);
                    Out.Float(v.health);
                    Out.Byte((byte)((v.dead ? 1 : 0) | (v.burning ? 2 : 0)));
                    Out.Byte((byte)(sbyte)Mathf.Clamp(v.ownerTeam, -1, 1));
                }
                session.Send(NetGame.VehicleStates, Out, false);
                at += n;
            }
        }

        /// <summary>Where each of this PC's soldiers sits (a shared vehicle and its seat), told when it changes and every few seconds.</summary>
        private static readonly List<Actor> SeatGone = new List<Actor>();

        private static void TellSeats(NetSession session, bool all)
        {
            if (all && SeatTold.Count > 0)
            {
                SeatGone.Clear();
                foreach (Actor told in SeatTold.Keys)
                {
                    if (told == null)
                    {
                        SeatGone.Add(told);
                    }
                }
                foreach (Actor gone in SeatGone)
                {
                    SeatTold.Remove(gone);
                }
            }
            foreach (KeyValuePair<Actor, ushort> kv in NetGame.OwnedIds())
            {
                Actor a = kv.Key;
                int now = -1;
                if (a != null && !a.dead && a.seat != null && a.seat.vehicle != null && ByVehicle.TryGetValue(a.seat.vehicle, out Shared s))
                {
                    int index = s.V.seats.IndexOf(a.seat);
                    if (index >= 0 && index < 255)
                    {
                        now = s.Id << 8 | index;
                    }
                }
                bool known = SeatTold.TryGetValue(a, out int told);
                if (known && told == now && !(all && now >= 0))
                {
                    continue;
                }
                if (!known && now < 0)
                {
                    SeatTold[a] = now;
                    continue;
                }
                SeatTold[a] = now;
                Out.Reset();
                Out.UShort(kv.Value);
                Out.UShort(now >= 0 ? (ushort)(now >> 8) : (ushort)0);
                Out.Byte(now >= 0 ? (byte)(now & 255) : (byte)255);
                session.Send(NetGame.VehicleSeat, Out, true);
            }
        }

        /// <summary>Every stand-in in the seat its owner says (or out of a shared vehicle's seat when he says none).</summary>
        private static void SeatRemotes(NetSession session)
        {
            foreach (NetGame.Remote r in NetGame.RemoteValues)
            {
                Actor a = r.Actor;
                if (a == null || a.dead || !r.HasState)
                {
                    continue;
                }
                if (a.seat != null && a.seat.occupant == null)
                {
                    // His seat emptied under him (a kick of 1.17.10 refused halfway): it still names him, so his leaving threw in the
                    // game's own code (the user's log, 2026-11-11: "a stand-in could not be seated: NullReferenceException at
                    // Vehicle.OccupantLeft"). The seat is his again for the leave to run.
                    a.seat.occupant = a;
                }
                Seat want = null;
                if (r.SeatVehicle != 0 && ById.TryGetValue(r.SeatVehicle, out Shared s) && s.V != null && !s.V.dead && r.SeatIndex < s.V.seats.Count)
                {
                    want = s.V.seats[r.SeatIndex];
                }
                if (want != null && a.seat != want)
                {
                    if (Time.unscaledTime < r.SeatRetryAt || !want.gameObject.activeInHierarchy)
                    {
                        continue;
                    }
                    if (want.occupant != null && (want.occupant == LocalPlayer.actor || !want.occupant.aiControlled))
                    {
                        continue; // (a player sits there here: not thrown out by another's word; the next tell settles it)
                    }
                    NetGame.Remote holder = NetGame.RemoteOfActor(want.occupant);
                    if (holder != null && holder.SeatVehicle == r.SeatVehicle && holder.SeatIndex == r.SeatIndex && holder.Owner <= r.Owner)
                    {
                        continue; // (two say the same seat: the one of the lower player number keeps it; they kicked each other every frame)
                    }
                    if (want.vehicle.ownerTeam >= 0 && want.vehicle.ownerTeam != a.team)
                    {
                        want.vehicle.ownerTeam = a.team; // (claimed by his side on his PC: the copy's claim was the last owner's)
                    }
                    Seating = true;
                    try
                    {
                        if (a.seat != null && a.seat.occupant == a)
                        {
                            a.LeaveSeat(false);
                        }
                        if (!a.EnterSeat(want, kickOutOccupant: true, ignoreLock: true))
                        {
                            r.SeatRetryAt = Time.unscaledTime + 2f;
                            Log.InfoOnce("net-seat-refused:" + want.vehicle.name, $"Multiplayer: a seat of {want.vehicle.name} refused a stand-in (tried again every 2 s).");
                        }
                    }
                    catch (Exception e)
                    {
                        Log.WarnOnce("net-seat", "Multiplayer: a stand-in could not be seated: " + e);
                        r.SeatRetryAt = Time.unscaledTime + 2f; // (as a refused seat: it threw and was tried again every frame; scout)
                    }
                    finally
                    {
                        Seating = false;
                    }
                }
                else if (want == null && a.seat != null && a.seat.vehicle != null && ByVehicle.ContainsKey(a.seat.vehicle) && a.seat.occupant == a)
                {
                    Seating = true;
                    try
                    {
                        a.LeaveSeat(false);
                    }
                    catch (Exception e)
                    {
                        Log.WarnOnce("net-unseat", "Multiplayer: a stand-in could not be unseated: " + e);
                    }
                    finally
                    {
                        Seating = false;
                    }
                }
            }
        }

        // ---- what is told -----------------------------------------------------------------------------------------------

        internal static void OnMessage(byte kind, byte sender, ref NetReader r)
        {
            NetSession session = NetGame.SessionNow;
            if (!Enabled || session == null)
            {
                return;
            }
            switch (kind)
            {
                case NetGame.VehicleInfo:
                    TakeInfo(session, sender, ref r);
                    break;
                case NetGame.VehicleStates:
                {
                    int n = r.Byte();
                    for (int i = 0; i < n; i++)
                    {
                        ushort id = r.UShort();
                        Vector3 pos = r.Vector();
                        Quaternion rot = r.Rotation();
                        Vector3 vel = r.SmallVector(), ang = r.SmallVector();
                        float health = r.Float();
                        int flags = r.Byte();
                        int team = (sbyte)r.Byte();
                        if (!ById.TryGetValue(id, out Shared s) || s.Owner != sender || s.V == null || !Finite(pos) || !Finite(vel) || !Finite(ang))
                        {
                            continue;
                        }
                        // (Held to what an owner sends, 400 m/s and 50 rad/s: a larger told speed went into the copy's lead and, taken
                        // over, into its rigidbody; scout.)
                        vel = Vector3.ClampMagnitude(vel, 400f);
                        ang = Vector3.ClampMagnitude(ang, 50f);
                        // (A passenger feels its crashes and landings from the told speeds: its own rigidbody stands still here.)
                        float told = Time.unscaledTime - s.Heard;
                        if (s.HasState && told > 0.03f && told < 0.3f && Finite(vel))
                        {
                            Vector3 accel = (vel - s.Vel) / told;
                            Rig.VehicleFeel.OnJolt(s.V, (accel - s.AccelAverage).magnitude);
                            s.AccelAverage = Vector3.Lerp(s.AccelAverage, accel, 1f - Mathf.Exp(-told / 0.3f));
                        }
                        Transform shown = s.V.transform;
                        Vector3 offset = shown.position - pos;
                        bool keep = s.HasState && offset.sqrMagnitude < 12f * 12f; // (far off, or the first word: put there)
                        s.Offset = keep ? offset : Vector3.zero;
                        s.TurnOffset = keep ? shown.rotation * Quaternion.Inverse(rot) : Quaternion.identity;
                        s.Pos = pos;
                        s.Rot = rot;
                        s.Vel = vel;
                        s.AngVel = ang;
                        s.Heard = Time.unscaledTime;
                        s.HasState = true;
                        s.Team = team;
                        if (!float.IsNaN(health) && !float.IsInfinity(health))
                        {
                            if (s.V.health - health > 1f)
                            {
                                Rig.VehicleFeel.OnHit(s.V, s.V.health - health); // (felt by a rider of a vehicle another PC drives too)
                            }
                            s.Health = health;
                            s.V.health = Mathf.Clamp(health, 0f, s.V.maxHealth);
                        }
                        if (LocalPlayer.actor == null || LocalPlayer.actor.seat == null || LocalPlayer.actor.seat.vehicle != s.V)
                        {
                            s.V.ownerTeam = Mathf.Clamp(team, -1, 1); // (not under this PC's own player in it: the game's own claim holds)
                        }
                        if ((flags & 1) != 0 && !s.V.dead)
                        {
                            s.ToldDead = true;
                            DieHere(s);
                        }
                        else if ((flags & 1) == 0 && s.V.dead)
                        {
                            ReviveHere(s); // (repaired on its owner's PC: a wrench, a squad's repair; it stayed dead here, its seats off)
                        }
                        else if (!s.V.dead)
                        {
                            // Its smoke below half health and its fire while it burns, as on the owner's (the damage that starts them is
                            // the owner's alone: a copy hit hard showed nothing until it blew up).
                            if (s.V.smokeParticles != null && s.V.health < 0.5f * s.V.maxHealth && !s.V.smokeParticles.isPlaying)
                            {
                                s.V.smokeParticles.Play();
                            }
                            if (s.V.fireParticles != null && (flags & 2) != 0 && !s.V.fireParticles.isPlaying)
                            {
                                s.V.fireParticles.Play();
                            }
                        }
                    }
                    break;
                }
                case NetGame.VehicleGone:
                {
                    ushort id = r.UShort();
                    Gone.Add(id);
                    if (ById.TryGetValue(id, out Shared s))
                    {
                        // This player out first if he sits in it (the host's sandbox removed it under him): destroyed with him in its
                        // seat, he sat on in nothing (the game's own death of a vehicle puts its riders out; sandbox scout, 2026-11-12).
                        Actor me = LocalPlayer.actor;
                        if (s.V != null && me != null && me.IsSeated() && me.seat != null && me.seat.vehicle == s.V)
                        {
                            try
                            {
                                me.LeaveSeat(false);
                            }
                            catch (Exception e)
                            {
                                Log.WarnOnce("net-vehicle-gone-seat", "Multiplayer: getting out of a vehicle that went threw: " + e.Message);
                            }
                        }
                        Forget(s);
                        if (s.Copy && s.V != null)
                        {
                            try
                            {
                                // (Out of the game's own lists first: destroyed in them, its vehicle loop and particles threw on it every frame;
                                // the first in-game net check, 2026-11-14.)
                                ActorManager.DropVehicle(s.V);
                            }
                            catch (Exception)
                            {
                            }
                            UnityEngine.Object.Destroy(s.V.gameObject);
                        }
                    }
                    break;
                }
                case NetGame.VehicleOwner:
                {
                    ushort id = r.UShort();
                    byte owner = r.Byte();
                    Vector3 pos = r.Vector();
                    Quaternion rot = r.Rotation();
                    Vector3 vel = r.SmallVector();
                    if (owner != sender && owner != 0 && sender != 0)
                    {
                        break; // (one takes a vehicle for himself, or gives it to the host: no other word; the host names anyone)
                    }
                    if (ById.TryGetValue(id, out Shared s) && Finite(pos) && Finite(vel))
                    {
                        if (owner == 0 && sender != 0 && sender != s.Owner)
                        {
                            break; // (a give-back from one who is no longer its owner, come late: the new owner's word stands; vehicle scout)
                        }
                        // Two joined PCs taking it at once: the host's table holds the first, the second is told who has it (each took
                        // the other's word and nobody drove it, frozen for both until one got out; vehicle scout, 2026-11-12).
                        if (session.IsHost && sender != 0 && owner == sender && s.Owner != 0 && s.Owner != sender && s.Owner != session.LocalId
                            && (Time.unscaledTime - s.OwnerSince < 5f || OwnerDrives(s)))
                        {
                            SetOwner(session, s, s.Owner, tell: true, force: true);
                            break;
                        }
                        // (Taken by another while this PC's player drives it too, both at once: the host's word holds, the other gives way.)
                        if (s.Owner == session.LocalId && owner != session.LocalId && sender != 0 && session.IsHost && DrivenHere(s))
                        {
                            // (Told even though nothing changes here: the taker believed it his, and both drove it apart; scout.)
                            SetOwner(session, s, session.LocalId, tell: true, force: true);
                            break;
                        }
                        s.Pos = pos;
                        s.Rot = rot;
                        s.Vel = vel;
                        s.AngVel = Vector3.zero;
                        s.Heard = Time.unscaledTime;
                        s.HasState = true;
                        s.Owner = owner;
                        s.OwnerSince = Time.unscaledTime;
                        if (owner == session.LocalId && s.V.dead && s.V.rigidbody != null && s.V.rigidbody.isKinematic != s.WasKinematic)
                        {
                            // (A wreck handed to this PC falls as its own: the per-frame switch skips the dead, and it hung where it was; scout.)
                            s.V.rigidbody.isKinematic = s.WasKinematic;
                        }
                    }
                    break;
                }
                case NetGame.VehicleSeat:
                {
                    ushort actor = r.UShort();
                    ushort vehicle = r.UShort();
                    int seat = r.Byte();
                    NetGame.Remote remote = NetGame.RemoteOf(sender, actor);
                    if (remote != null)
                    {
                        remote.SeatVehicle = seat == 255 ? (ushort)0 : vehicle;
                        remote.SeatIndex = seat == 255 ? 0 : seat;
                    }
                    // Two players sitting down in one seat at the same moment, each on his own PC before the other's word came: both
                    // stayed, each seated on his own PC (vehicle scout, 2026-11-12). The host decides: a player's claim on a seat another
                    // player already has in the host's game is refused, and that PC puts its player out.
                    if (session.IsHost && actor == 0 && seat != 255 && ById.TryGetValue(vehicle, out Shared claimed) && claimed.V != null
                        && seat < claimed.V.seats.Count)
                    {
                        Actor occupant = claimed.V.seats[seat].occupant;
                        Actor claimant = remote != null ? remote.Actor : null;
                        bool otherPlayer = occupant != null && occupant != claimant
                                           && (occupant == LocalPlayer.actor || NetGame.RemoteOfActor(occupant)?.Id == 0);
                        if (otherPlayer)
                        {
                            Out.Reset();
                            Out.UShort(vehicle);
                            Out.Byte((byte)seat);
                            session.SendTo(sender, NetGame.VehicleSeatRefused, Out, true);
                        }
                    }
                    break;
                }
                case NetGame.VehicleSeatRefused:
                {
                    ushort vehicle = r.UShort();
                    int seat = r.Byte();
                    Actor me = LocalPlayer.actor;
                    if (sender == 0 && me != null && me.seat != null && ById.TryGetValue(vehicle, out Shared refused) && refused.V != null
                        && me.seat.vehicle == refused.V && seat < refused.V.seats.Count && refused.V.seats[seat] == me.seat)
                    {
                        try
                        {
                            me.LeaveSeat(false);
                        }
                        catch (Exception e)
                        {
                            Log.WarnOnce("net-seat-refused", "Multiplayer: getting out of a seat the host refused threw: " + e.Message);
                        }
                        UI.UISystem.Instance?.Notice?.Show("That seat was just taken by another player.", 2.5f);
                    }
                    break;
                }
                case NetGame.VehicleHit:
                    TakeHit(session, sender, ref r);
                    break;
                case NetGame.VehicleKilled:
                {
                    // This PC's player disabled or destroyed a vehicle another PC drove: his credit, as for one of his own match
                    // (the game's "DISABLED"/"DESTROYED" line and the scoreboard's vehicle count).
                    ushort id = r.UShort();
                    ushort shooter = r.UShort();
                    bool disabled = r.Bool();
                    Actor player = LocalPlayer.actor;
                    if (shooter == 0 && player != null && ById.TryGetValue(id, out Shared s) && s.V != null)
                    {
                        try
                        {
                            player.OnGotVehicleKill(disabled, s.V);
                        }
                        catch (Exception e)
                        {
                            Log.WarnOnce("net-vehicle-kill", "Multiplayer: a vehicle kill could not be counted: " + e.Message);
                        }
                    }
                    break;
                }
            }
        }

        private static bool DrivenHere(Shared s)
        {
            Actor player = LocalPlayer.actor;
            return player != null && player.seat != null && player.seat.vehicle == s.V && player.seat.IsDriverSeat();
        }

        private static void TakeInfo(NetSession session, byte sender, ref NetReader r)
        {
            ushort id = r.UShort();
            string key = r.String(128);
            byte owner = r.Byte();
            bool dead = r.Bool();
            Vector3 pos = r.Vector();
            Quaternion rot = r.Rotation();
            bool turret = r.Remaining > 0 && r.Bool();
            if (sender != 0 || session.IsHost || Gone.Contains(id) || ById.ContainsKey(id) || dead || !Finite(pos) || NetGame.LoadingNow || !NetGame.InMatchNow)
            {
                return;
            }
            {
                // The same vehicle standing on this PC too, at the same place under the same name (a turret, the map's own car or tank,
                // one a spawner made at the match's start): that one is taken as the host's, not a second one made beside it. A map's
                // scripts on this PC know their own vehicles: taken off and made anew, those scripts lost them.
                Vehicle local = LocalMatch(key, pos, turret);
                if (local != null)
                {
                    // (Not a turret: gone with the host's when he says so, as a copy is. A turret stays: the map's own, never cleaned up.)
                    var adopted = new Shared { Id = id, V = local, Key = key, Owner = owner, Copy = !turret, WasKinematic = local.rigidbody != null && local.rigidbody.isKinematic, Pos = local.transform.position, Rot = local.transform.rotation, Heard = Time.unscaledTime, HasState = true };
                    local.spawner = null;
                    ById[id] = adopted;
                    ByVehicle[local] = adopted;
                    TurretTries.Remove(id);
                    Log.InfoOnce("net-adopt:" + key, $"Multiplayer: the host's {(turret ? "turret" : "vehicle")} '{key}' is this PC's own at the same place (number {id}).");
                    return;
                }
            }
            // (In a match's first seconds a vehicle too waits a telling when one of its name stands near on this PC, not yet listed as
            // it can be taken; with none near it is made at once: it waited 3 s for nothing; scout.)
            if (turret || Time.unscaledTime - _matchSeenAt < 10f && SameNameNear(key, pos))
            {
                TurretTries.TryGetValue(id, out int tries);
                TurretTries[id] = tries + 1;
                if (tries < (turret ? 3 : 1))
                {
                    return; // (made only when none has come after three tellings: its spawner may bring this PC's own yet)
                }
            }
            GameObject prefab = PrefabOf(key);
            if (prefab == null)
            {
                if (Missing.Add(key))
                {
                    Log.Warn($"Multiplayer: the host's vehicle '{key}' is not on this PC (a mod's vehicle?): it is not shown here.");
                    UI.UISystem.Instance?.Notice?.Show($"The host has a vehicle this PC lacks: {key}. Get the host's mods.", 6f);
                }
                return;
            }
            GameObject go;
            try
            {
                go = UnityEngine.Object.Instantiate(prefab, pos, rot);
            }
            catch (Exception e)
            {
                Log.WarnOnce("net-vehicle-make:" + key, $"Multiplayer: the host's vehicle '{key}' could not be made here: {e}");
                return;
            }
            Vehicle v = go != null ? go.GetComponent<Vehicle>() : null;
            if (v == null)
            {
                if (go != null)
                {
                    UnityEngine.Object.Destroy(go);
                }
                return;
            }
            v.spawner = null;
            if (turret)
            {
                v.isTurret = true;
            }
            var s = new Shared { Id = id, V = v, Key = key, Owner = owner, Copy = true, WasKinematic = v.rigidbody != null && v.rigidbody.isKinematic, Pos = pos, Rot = rot, Heard = Time.unscaledTime, HasState = true };
            if (v.rigidbody != null)
            {
                v.rigidbody.isKinematic = true;
            }
            ById[id] = s;
            ByVehicle[v] = s;
            CopiesMade++;
            Log.InfoOnce("net-vehicle-copy:" + key, $"Multiplayer: the host's vehicle '{key}' shown here (number {id}).");
        }

        private static readonly Dictionary<ushort, int> TurretTries = new Dictionary<ushort, int>();

        private static bool SameNameNear(string key, Vector3 pos)
        {
            List<Vehicle> list = ActorManager.instance != null ? ActorManager.instance.vehicles : null;
            if (list == null)
            {
                return false;
            }
            foreach (Vehicle v in list)
            {
                if (v != null && !ByVehicle.ContainsKey(v) && (v.transform.position - pos).sqrMagnitude < 30f * 30f
                    && string.Equals(KeyOf(v), key, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>This PC's own turret of that name standing within 2.5 m of the told place, not yet taken as one of the host's.</summary>
        private static Vehicle LocalMatch(string key, Vector3 pos, bool turret)
        {
            Vehicle best = null;
            float bestDistance = 2.5f * 2.5f;
            List<Vehicle> list = ActorManager.instance != null ? ActorManager.instance.vehicles : null;
            if (list == null)
            {
                return null;
            }
            foreach (Vehicle v in list)
            {
                if (v == null || v.isTurret != turret || v.dead || ByVehicle.ContainsKey(v) || !v.gameObject.activeInHierarchy || !string.Equals(KeyOf(v), key, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                float d = (v.transform.position - pos).sqrMagnitude;
                if (d < bestDistance)
                {
                    bestDistance = d;
                    best = v;
                }
            }
            return best;
        }

        private static void TakeHit(NetSession session, byte sender, ref NetReader r)
        {
            ushort id = r.UShort();
            byte shooterPc = r.Byte();
            ushort shooter = r.UShort();
            float health = r.Float(), balance = r.Float();
            int flags = r.Byte();
            int type = r.Byte();
            Vector3 point = r.Vector();
            Vector3 direction = r.SmallVector();
            if (!ById.TryGetValue(id, out Shared s) || s.V == null || s.V.dead || float.IsNaN(health) || float.IsInfinity(health) || !Finite(point))
            {
                return;
            }
            if (s.Owner != session.LocalId)
            {
                // Handed on since it was sent (its driver got in or out): passed to the owner now, once (it was lost; vehicle scout).
                if (sender == shooterPc)
                {
                    // (Also when the shooter has taken it since: he did not apply his own shot, not owning it then; vehicle scout.)
                    Out.Reset();
                    Out.UShort(id);
                    Out.Byte(shooterPc);
                    Out.UShort(shooter);
                    Out.Float(health);
                    Out.Float(balance);
                    Out.Byte((byte)flags);
                    Out.Byte((byte)type);
                    Out.Vector(point);
                    Out.SmallVector(direction);
                    session.SendTo(s.Owner, NetGame.VehicleHit, Out, true);
                }
                return;
            }
            NetGame.Remote by = NetGame.RemoteOf(shooterPc, shooter);
            var info = new DamageInfo(Enum.IsDefined(typeof(DamageInfo.DamageSourceType), type) ? (DamageInfo.DamageSourceType)type : DamageInfo.DamageSourceType.Unknown, by != null ? by.Actor : null, null)
            {
                healthDamage = Mathf.Clamp(health, 0f, 100000f),
                balanceDamage = float.IsNaN(balance) || float.IsInfinity(balance) ? 0f : balance,
                isSplashDamage = (flags & 1) != 0,
                isPiercing = (flags & 2) != 0,
                isCriticalHit = (flags & 4) != 0,
                point = point,
                direction = Finite(direction) ? direction : Vector3.zero,
            };
            bool wasDead = s.V.dead, wasBurning = s.V.burning;
            _applyingHit = true;
            try
            {
                s.V.Damage(info);
            }
            finally
            {
                _applyingHit = false;
            }
            if (!wasDead && s.V.dead || !wasBurning && s.V.burning)
            {
                Out.Reset();
                Out.UShort(s.Id);
                Out.UShort(shooter);
                Out.Bool(!s.V.dead);
                session.SendTo(shooterPc, NetGame.VehicleKilled, Out, true);
            }
        }

        /// <summary>Its owner told it destroyed: so here too (the fire, the explosion, its riders out; the wreck follows the owner's).</summary>
        private static readonly System.Reflection.MethodInfo Ressurect = AccessTools.Method(typeof(Vehicle), "Ressurect");

        /// <summary>
        /// Its owner says it lives again (repaired after its death there, Vehicle.Repair): so here too, its seats back, and not the wreck's
        /// own clean-up 24 s after its death (that took the copy away while the owner's drove on; multiplayer scout, 2026-11-18).
        /// </summary>
        private static void ReviveHere(Shared s)
        {
            if (Ressurect == null || Time.unscaledTime - s.ReviveTriedAt < 1f)
            {
                return;
            }
            s.ReviveTriedAt = Time.unscaledTime;
            _applyingHit = true;
            try
            {
                s.ToldDead = false;
                s.V.health = Mathf.Max(s.V.health, 0.5f * s.V.maxHealth);
                Ressurect.Invoke(s.V, null);
                s.V.CancelInvoke("Cleanup");
                Log.InfoOnce("net-vehicle-revive:" + s.V.name, $"Multiplayer: {s.V.name} repaired on its owner's PC: alive here again.");
            }
            catch (Exception e)
            {
                Log.WarnOnce("net-vehicle-revive", "Multiplayer: a vehicle told repaired could not be brought back here: " + e);
            }
            finally
            {
                _applyingHit = false;
            }
        }

        /// <summary>
        /// A vehicle this PC owns dying not by a hit (its fire burning out) whose fire another PC's player started: his kill, told to his
        /// PC (only a killing hit told it, and a burn-out was nobody's; multiplayer scout, 2026-11-18).
        /// </summary>
        private static void OnVehicleDie(Vehicle __instance, DamageInfo info)
        {
            if (!Enabled || _applyingHit || __instance == null || __instance.dead || !ByVehicle.TryGetValue(__instance, out Shared s))
            {
                return;
            }
            NetSession session = NetGame.SessionNow;
            NetGame.Remote by = NetGame.RemoteOfActor(info.sourceActor);
            if (session == null || s.Owner != session.LocalId || by == null)
            {
                return;
            }
            Out.Reset();
            Out.UShort(s.Id);
            Out.UShort(by.Id);
            Out.Bool(false);
            session.SendTo(by.Owner, NetGame.VehicleKilled, Out, true);
        }

        private static void DieHere(Shared s)
        {
            _applyingHit = true;
            try
            {
                s.V.health = 0f;
                s.V.Die(new DamageInfo(DamageInfo.DamageSourceType.Unknown, null, null));
            }
            catch (Exception e)
            {
                Log.WarnOnce("net-vehicle-die", "Multiplayer: a vehicle told destroyed could not be destroyed here: " + e);
            }
            finally
            {
                _applyingHit = false;
            }
        }

        // ---- the vehicles' prefabs ----------------------------------------------------------------------------------------

        /// <summary>A vehicle as it is told: its prefab's name (what a spawner, a mod's slot or the map made it from).</summary>
        private static string KeyOf(Vehicle v)
        {
            GameObject prefab = v.vehicleInfo != null ? v.vehicleInfo.prefab : null;
            string name = prefab != null ? prefab.name : v.gameObject.name;
            return (name ?? "").Replace("(Clone)", "").Trim();
        }

        /// <summary>This PC's prefab of that name: every vehicle prefab loaded (the game's, the mods', the map's spawners').</summary>
        private static GameObject PrefabOf(string key)
        {
            if (string.IsNullOrEmpty(key))
            {
                return null;
            }
            if (Catalog.TryGetValue(key, out GameObject known) && known != null)
            {
                return known;
            }
            if (Missing.Contains(key))
            {
                return null; // (looked for once a match: the scene's search every few seconds was a hitch in the headset)
            }
            if (Time.unscaledTime - _catalogAt < 5f && Catalog.Count > 0)
            {
                return null;
            }
            _catalogAt = Time.unscaledTime;
            Catalog.Clear();
            try
            {
                foreach (Vehicle v in Resources.FindObjectsOfTypeAll<Vehicle>())
                {
                    if (v != null && !v.gameObject.scene.IsValid() && !string.IsNullOrEmpty(v.gameObject.name) && !Catalog.ContainsKey(v.gameObject.name))
                    {
                        Catalog[v.gameObject.name] = v.gameObject;
                    }
                }
                foreach (VehicleSpawner spawner in UnityEngine.Object.FindObjectsOfType<VehicleSpawner>())
                {
                    if (spawner != null && spawner.prefab != null && !Catalog.ContainsKey(spawner.prefab.name))
                    {
                        Catalog[spawner.prefab.name] = spawner.prefab;
                    }
                }
            }
            catch (Exception e)
            {
                Log.WarnOnce("net-vehicle-catalog", "Multiplayer: the vehicle prefabs could not be listed: " + e);
            }
            return Catalog.TryGetValue(key, out GameObject found) ? found : null;
        }

        // ---- the session ----------------------------------------------------------------------------------------------------

        /// <summary>A player left: the vehicles he drove are the host's again.</summary>
        internal static void OnLeft(byte player)
        {
            foreach (Shared s in ById.Values)
            {
                if (s.Owner == player)
                {
                    s.Owner = 0;
                }
            }
        }

        /// <summary>A copy of a vehicle that is not one of the host's: taken off on a joined PC (its own spawners are stopped).</summary>
        internal static bool KeepOnJoined(Vehicle v)
        {
            if (!Enabled)
            {
                return false;
            }
            if (IsShared(v))
            {
                return true;
            }
            // A few seconds for the host's word to take it as his (LocalMatch): the host tells his vehicles every 3 s.
            if (!FirstSeen.TryGetValue(v, out float seen))
            {
                FirstSeen[v] = Time.unscaledTime;
                return true;
            }
            return Time.unscaledTime - seen < 8f;
        }

        private static readonly Dictionary<Vehicle, float> FirstSeen = new Dictionary<Vehicle, float>();

        private static void Forget(Shared s)
        {
            ById.Remove(s.Id);
            if (!ReferenceEquals(s.V, null))
            {
                ByVehicle.Remove(s.V);
            }
        }

        /// <summary>The match or the session over: everything forgotten; a copy left in a match that goes on alone moves by its own physics again.</summary>
        internal static void Clear()
        {
            foreach (Shared s in ById.Values)
            {
                if (s.V != null && s.V.rigidbody != null && !s.V.dead)
                {
                    s.V.rigidbody.isKinematic = s.WasKinematic;
                }
            }
            ById.Clear();
            ByVehicle.Clear();
            Gone.Clear();
            SeatTold.Clear();
            TurretTries.Clear();
            FirstSeen.Clear();
            _matchSeenAt = -1f;
            Missing.Clear();
            Catalog.Clear();
            _catalogAt = -100f;
            _nextId = 1;
        }

        private static bool Finite(Vector3 v) =>
            !(float.IsNaN(v.x) || float.IsNaN(v.y) || float.IsNaN(v.z) || float.IsInfinity(v.x) || float.IsInfinity(v.y) || float.IsInfinity(v.z));
    }
}
