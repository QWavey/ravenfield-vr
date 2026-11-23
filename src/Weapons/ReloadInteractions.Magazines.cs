using System.Collections;
using System.Collections.Generic;
using System.Linq;
using RavenfieldVR.Core;
using RavenfieldVR.Input;
using RavenfieldVR.Rig;
using RavenfieldVR.Runtime;
using UnityEngine;

namespace RavenfieldVR.Weapons
{
    // ReloadInteractions, part: magazines and shells in the hand: the magazine release, the pouch, and a held prop from
    // free to seated on its rail. The class, grabbing and the frame's visuals: ReloadInteractions.cs; the self-checks:
    // ReloadInteractions.SelfChecks.cs.
    public sealed partial class ReloadInteractions
    {
        // ---- magazine release ----------------------------------------------------------------------

        private void EjectMagazine()
        {
            if (_ejecting != null)
            {
                FinishEject();
            }
            // The rounds stay in the dropped magazine: pick it up again to use them (they return to the reserve if it
            // despawns first).
            int rounds = Ammo.EjectMagazine();
            _ejecting = MagazineProp.Create(Rig, PropKind.Magazine, rounds);
            _ejecting.NotFresh();
            Log.InfoOnce("eject:" + Data.Key, $"Magazine ejected from {Data.Key}: {rounds} rounds, {Rig.MagazineParts.Length} part(s) hidden on the gun, pull axis {Data.MagEjectAxis:F2}, rail {_ejecting.RailLength * 100f:0.0} cm.");
            _ejecting.Owner = Ammo;
            if (WellInGrip)
            {
                GripWellRail(_ejecting); // it drops clear of the grip, not let go half way up it (task 270)
            }
            _ejecting.RailDepth = _ejecting.RailLength;
            _ejectTime = 0f;
            _held.Sounds.Play(MechSound.MagOut, _ejecting.Go.transform.position);
            _sys.Rig.Dominant.Haptic(0.05f, 90f, 0.6f);
        }

        /// <summary>The off hand holds a fresh magazine near the well and has room for one more.</summary>
        private bool CanPalm(VRHand off)
        {
            if (!VRConfig.MagazinePalming.Value || !off.Tracked || off.Role != HandRole.Magazine || _palmed != null || _ejecting != null)
            {
                return false;
            }
            // This gun's kind of magazine only: one picked up from another weapon was taken as the fresh one.
            return off.Held is MagazineProp fresh && fresh.Go != null && FitsWell(fresh) && fresh.DistanceTo(SeatedMagBaseWorld) < 0.2f;
        }

        /// <summary>A magazine of this gun or of a sister gun (see <see cref="WeaponRigData.TakesMagazineOf"/>).</summary>
        private bool FitsWell(MagazineProp prop) => prop.Kind == PropKind.Magazine && (prop.WeaponKey == Data.Key || Data.TakesMagazineOf(prop.Data));

        /// <summary>This gun's prop of any kind, or a sister gun's magazine: its rounds are this gun's (stowed, a sister's were lost with no gun of its own to go to).</summary>
        private bool Ours(MagazineProp prop) => prop.WeaponKey == Data.Key || FitsWell(prop);

        /// <summary>Tactical reload: the released magazine goes into the off hand, beside the fresh one.</summary>
        private void TacticalEject(VRHand off)
        {
            int rounds = Ammo.EjectMagazine();
            MagazineProp prop = MagazineProp.Create(Rig, PropKind.Magazine, rounds);
            prop.Owner = Ammo;
            prop.AttachPalmed(off);
            _palmed = prop;
            // From the well into the hand over a moment (it was beside the fresh one the next frame).
            // (Against the rig: kept in the world, it trailed behind the walking player for its 0.18 s; MagazineProp.ToRigSpace.)
            _palmedFrom = prop.Go != null ? MagazineProp.ToRigSpace(RigidPose.Of(prop.Go.transform)) : default;
            _palmedAt = prop.Go != null ? Time.unscaledTime : -1f;
            _held.Sounds.Play(MechSound.MagOut, SeatPose.position);
            _sys.Rig.Dominant.Haptic(0.05f, 90f, 0.6f);
            off.Haptic(0.04f, 120f, 0.55f);
        }

        private float _refusedAt = -10f;
        private RigidPose _palmedFrom;

        /// <summary>
        /// A palmed magazine let go of as a life ends (WeaponSystem.LetGoForNewLife dropped only what each hand holds: the palmed one
        /// stayed the dead hand's and was taken up again the next frame, riding the hand through the death screen; scout).
        /// </summary>
        internal void DropPalmed(VRHand off)
        {
            MagazineProp palmed = _palmed;
            _palmed = null;
            if (palmed == null)
            {
                return;
            }
            if (palmed.Go == null)
            {
                palmed.Destroy();
            }
            else
            {
                palmed.Drop(off != null ? off.Velocity : Vector3.zero, Vector3.zero);
            }
        }
        private float _palmedAt = -1f;

        /// <summary>The fresh magazine is gone from the hand (seated or dropped): the palmed one becomes the one it holds.</summary>
        private void PromotePalmed(VRHand off)
        {
            MagazineProp palmed = _palmed;
            _palmed = null;
            if (palmed == null || palmed.Go == null)
            {
                return;
            }
            if (off.Held == null && off.GrabHeld)
            {
                palmed.Attach(off);
                palmed.FollowHand();
            }
            else
            {
                palmed.Drop(off.ThrowVelocity(), off.AngularVelocity);
            }
        }

        // ---- the magazine detent (idea 8) -------------------------------------------------------------------

        /// <summary>A magazine let go at its latch, waiting in the well to be tapped home (MagazineDetent); null for none.</summary>
        private MagazineProp _almostSeated;
        /// <summary>The held magazine stopped at its latch this push (its bump felt once).</summary>
        private bool _atDetent;
        /// <summary>A push in at least this fast (m/s) clicks it past the latch; so does the hand pushing on this far past the seat (m).</summary>
        private const float DetentSpeed = 0.3f, DetentPush = 0.01f;
        /// <summary>A tap on the base this fast (m/s, along the well) latches a waiting or loose magazine.</summary>
        private const float TapSpeed = 0.45f;

        /// <summary>
        /// The magazine detent applies: on, a box magazine into a well outside the grip (a pistol's is drawn home in the grip, where the
        /// hands meet), not a machine gun's box or a clip. Not in the autotest's scripted reloads (their pushes end at the seat).
        /// </summary>
        private bool DetentApplies(MagazineProp prop) =>
            VRConfig.MagazineDetent.Value && prop.Kind == PropKind.Magazine && !WellInGrip && !Data.IsMachineGun && !Data.EnBloc && !prop.BoxSized
            && !Core.AutoTest.Requested && VRConfig.MagazineInsertHelp.Value != MagazineHelp.Easy;

        /// <summary>How far short of its seat the latch holds a magazine: 8% of its way in, 4 to 8 mm.</summary>
        private static float DetentDepth(MagazineProp prop) => Mathf.Clamp(prop.RailLength * 0.08f, 0.004f, 0.008f);

        /// <summary>
        /// Each frame: a magazine waiting at its latch is tapped home by the free hand's palm (a quick push on its base), taken again by
        /// a squeeze on it, or dropped by the magazine release; a loose one in the gun is latched by the same tap.
        /// </summary>
        private void UpdateDetent(VRHand off, VRHand dom)
        {
            if (Ammo.MagLoose && Ammo.MagInserted && off.Tracked && off.Role == HandRole.Free && off.Held == null)
            {
                float push = Vector3.Dot(off.BodyThrowVelocity() - dom.BodyThrowVelocity(), SeatPose.TransformDirection(-Data.MagEjectAxis.normalized));
                if (push > TapSpeed && Vector3.Distance(off.PalmCentre, SeatedMagBaseWorld) < 0.08f)
                {
                    Ammo.MagLoose = false;
                    GameSounds.PlayPart(Data, MechSound.MagIn, SeatPose.position, 0.6f, 0.4f, 0.6f);
                    off.Haptic(0.04f, 110f, 0.7f);
                    _held.Nudge(SeatPose.TransformDirection(-Data.MagEjectAxis.normalized) * 0.08f);
                }
            }
            MagazineProp prop = _almostSeated;
            if (prop == null)
            {
                return;
            }
            if (prop.Go == null || prop.HeldBy != null)
            {
                _almostSeated = null;
                return;
            }
            if (Ammo.MagInserted || !AcceptsNow(prop))
            {
                // Another magazine went in past it (taken from the pouch with this one at its latch): it falls out. Forgotten, it hung in
                // the well for good, its rounds neither in the gun nor in the reserve (scout, 2026-11-03).
                DropAlmostSeated();
                return;
            }
            Vector3 insert = SeatPose.TransformDirection(prop.InsertDirLocal);
            if (VRInput.GetPressed(DigitalAction.MagRelease, dom.Side) && !VRGameInput.GunInputBlocked)
            {
                VRInput.Consume(DigitalAction.MagRelease, dom.Side);
                _almostSeated = null;
                prop.OnRail = false;
                prop.Drop(dom.Velocity - insert * 0.6f, Vector3.zero);
                return;
            }
            if (!off.Tracked || off.Role != HandRole.Free || off.Held != null)
            {
                return;
            }
            float tap = Vector3.Dot(off.BodyThrowVelocity() - dom.BodyThrowVelocity(), insert);
            if (tap > TapSpeed && Vector3.Distance(off.PalmCentre, prop.Go.transform.TransformPoint(prop.BaseLocal)) < 0.08f)
            {
                _almostSeated = null;
                _railInSpeed = tap; // (slapped home: as hard as a seating push, it drops a locked slide too)
                SeatProp(off, prop);
                return;
            }
            if (off.GrabPressed && prop.DistanceTo(off.Palm.position) < 0.08f)
            {
                VRInput.Consume(DigitalAction.Grab, off.Side);
                _almostSeated = null;
                prop.Attach(off); // (back on its line from where it is, next frame)
                prop.FollowHand();
            }
        }

        /// <summary>Before rendering: a magazine waiting at its latch is drawn there, with the gun.</summary>
        private void DrawAlmostSeated()
        {
            MagazineProp prop = _almostSeated;
            if (prop == null || prop.Go == null || prop.HeldBy != null)
            {
                return;
            }
            prop.RailDepth = prop.RailLength - DetentDepth(prop);
            prop.RailPose(SeatPose).ApplyTo(prop.Go.transform);
        }

        /// <summary>The gun let go of: a magazine waiting at its latch falls out of it.</summary>
        private void DropAlmostSeated()
        {
            MagazineProp prop = _almostSeated;
            _almostSeated = null;
            if (prop != null && prop.Go != null && prop.HeldBy == null)
            {
                prop.OnRail = false;
                prop.Drop(Vector3.zero, Vector3.zero);
            }
        }

        /// <summary>The ejected magazine leaves the well along its axis, then gravity takes it.</summary>
        private void UpdateEject(float dt)
        {
            if (_ejecting == null)
            {
                return;
            }
            if (_ejecting.Go == null || _ejecting.HeldBy != null)
            {
                _ejecting = null;
                return;
            }
            _ejectTime += dt;
            float u = Mathf.Clamp01(_ejectTime / EjectSeconds);
            _ejecting.RailDepth = _ejecting.RailLength * (1f - u * u);
            _ejecting.RailPose(SeatPose).ApplyTo(_ejecting.Go.transform);
            if (u >= 1f)
            {
                FinishEject();
            }
        }

        private void FinishEject()
        {
            MagazineProp prop = _ejecting;
            _ejecting = null;
            if (prop == null || prop.Go == null)
            {
                return;
            }
            // The weapon may already be gone (replaced or destroyed by the game): the prop's own axis then.
            Vector3 insert = Rig.GunBody != null ? SeatPose.TransformDirection(prop.InsertDirLocal) : prop.InsertDirWorld;
            prop.Drop(_sys.Rig.Dominant.Velocity - insert * 1.1f + Vector3.down * 0.25f, Random.insideUnitSphere * 1.5f);
        }

        /// <summary>Where magazines, shells and rockets go in; chambers on a break-open barrel or crane swing out with it.</summary>
        private RigidPose SeatPose => ChamberSeat(_seatChamber);

        /// <summary>
        /// The seat of chamber <paramref name="chamber"/> (a break action's; the magazine's own is chamber 0, and every other gun
        /// has only that one): the magazine's seat moved to where that chamber's shell sits, swung open with the barrels.
        /// </summary>
        private RigidPose ChamberSeat(int chamber)
        {
            RigidPose seat = Data.MagSeat;
            if (chamber > 0 && chamber < Data.ChamberShellRest.Count)
            {
                seat = new RigidPose(seat.position + Data.ChamberShellRest[chamber].position - Data.ChamberShellRest[0].position, seat.rotation);
            }
            if (Data.SeatOnHinge && _held.HingeOpenness > 0.001f)
            {
                seat = HingeRotate(seat, _held.HingeOpenness);
            }
            return Rig.GunBodyPose.Multiply(seat);
        }

        /// <summary>Which chamber the shell in the hand goes into (0 but on a break action of two or more).</summary>
        private int _seatChamber;

        /// <summary>The empty chamber nearest <paramref name="at"/> (world), or 0 when none is empty or the gun has one.</summary>
        private int NearestEmptyChamber(Vector3 at)
        {
            bool[] full = _held.ChamberFull;
            int best = 0;
            float bestDistance = float.MaxValue;
            for (int i = 0; i < Data.ChamberShellRest.Count && i < full.Length; i++)
            {
                float distance = (ChamberSeat(i).position - at).sqrMagnitude;
                if (!full[i] && distance < bestDistance)
                {
                    best = i;
                    bestDistance = distance;
                }
            }
            return best;
        }

        /// <summary>A gun-space pose carried along by the hinge opened <paramref name="openness"/> of the way.</summary>
        private RigidPose HingeRotate(RigidPose pose, float openness) => Data.HingeRotated(pose, openness);

        /// <summary>Base plate of the seated magazine, where a hand takes hold to pull it.</summary>
        private Vector3 SeatedMagBaseWorld
        {
            get
            {
                Quaternion seatInv = Runtime.RigidPose.Conjugate(Data.MagSeat.rotation);
                Vector3 insertLocal = (seatInv * -Data.MagEjectAxis).normalized;
                Bounds b = Data.MagBounds;
                float extent = Mathf.Abs(Vector3.Dot(b.extents, new Vector3(Mathf.Abs(insertLocal.x), Mathf.Abs(insertLocal.y), Mathf.Abs(insertLocal.z))));
                return SeatPose.TransformPoint(b.center - insertLocal * extent);
            }
        }

        /// <summary>The off hand takes the seated magazine: it unlatches and slides out along the well with the hand.</summary>
        private void PullSeatedMagazine(VRHand off)
        {
            Ammo.BeltState(out _beltWasLaid, out _beltWasLifted);
            int rounds = Ammo.EjectMagazine();
            MagazineProp prop = MagazineProp.Create(Rig, PropKind.Magazine, rounds);
            prop.Owner = Ammo;
            prop.Attach(off);
            if (WellInGrip)
            {
                // Out along the whole grip, as it goes in (task 270): on the short rail it came off with half of it still up
                // the grip, free there, where the keep-out now opens the well only to a magazine within 12 degrees.
                GripWellRail(prop);
            }
            prop.OnRail = true;
            prop.RailDepth = prop.RailLength;
            prop.LastTick = prop.RailDepth;
            // The hand took it wherever it grabbed (the grab zone is large): the magazine follows the hand's pull from
            // here. Taken from the palm's depth, a grip a little up the magazine read as pushed home and seated it again
            // on the same frame: pulling a magazine out by hand did nothing.
            prop.RailHandOffset = HandAlongRail(off, prop, out float lateral0) - prop.RailLength;
            // And wherever across it: the grab zone is large, so the hand is often several centimetres off the well's line.
            // Measured from no offset, a hand 7 cm off knocked it off the rail on the first frame and it jumped to the hand
            // through the receiver (user, 2026-10-16: "it always needs to exit how it entered").
            prop.RailLateral0 = lateral0;
            prop.FromSeat = true;
            prop.TuggedFromSeat = false;
            prop.MovingIn = false;
            prop.TurnDepth = prop.RailDepth;
            prop.PrevDepth = prop.RailDepth;
            // An en-bloc clip under a bolt shut on a round is not taken back, but it still leaves along its line (check-1017h:
            // the M1's clip was knocked off its rail on the first frame and jumped to the hand through the receiver).
            prop.RailOutOnly = !AcceptsNow(prop);
            prop.RailPose(SeatPose).ApplyTo(prop.Go.transform);
            _held.Sounds.Play(MechSound.MagOut, SeatPose.position);
            _held.Nudge(-SeatPose.TransformDirection(prop.InsertDirLocal) * 0.08f); // the latch gives with a tug
            off.Haptic(0.03f, 120f, 0.5f);
        }

        // ---- pouch -----------------------------------------------------------------------------------

        private void TakeFromPouch(VRHand off)
        {
            // With the hand's trigger held as it squeezes: one round for the chamber, not a magazine (Weapons/ChamberByHand; the
            // user, 2026-11-11). The squeeze alone stays what it was: after the last shot the slide is back with the empty magazine
            // still in, and that squeeze wants the next magazine.
            // (Only when it can go in now: the chamber open and empty, and the magazine not out. A finger resting on the trigger while
            // reaching for a magazine took a round instead, "randomly" (the user, 2026-11-14: "the player grabs a ... bullet from the
            // left side randomly ... while loading mags").)
            bool chamberWaits = (Ammo.SlideLocked || Ammo.BoltOpen) && !Ammo.Chambered && !Ammo.SpentInChamber
                                && (!Ammo.UsesDetachableMag || Ammo.MagInserted && Ammo.MagRounds > 0);
            if (HandLoading.ChamberEnabled && Ammo.Participates && Ammo.UsesChamber && Data.Action != ActionType.SingleShot && !Breaks
                && chamberWaits && VRInput.GetHeld(DigitalAction.Use, off.Side))
            {
                VRInput.Consume(DigitalAction.Use, off.Side);
                if (!Ammo.InfiniteSpare && Ammo.SpareAmmo <= 0)
                {
                    off.Haptic(0.15f, 35f, 0.7f);
                    _sys.FlashMessage("NO AMMO");
                    return;
                }
                Ammo.TakeSpare(1);
                Casings.Case round = Casings.RoundInHand(Data, Data.Key, Ammo, off);
                HandLoading.HoldRound(off, round);
                _held.Sounds.Play(MechSound.Pouch, off.Palm.position, 0.4f);
                off.Haptic(0.02f, 160f, 0.35f);
                return;
            }
            PropKind kind;
            int rounds;
            bool emptyToo = false;
            if (Ammo.UsesDetachableMag && HandLoading.Applies(Ammo))
            {
                // Loaded by hand: from the reserve as always, and once it gives none an empty one, to be filled by hand (HandLoading).
                kind = PropKind.Magazine;
                rounds = HandLoading.PouchMagazineRounds(Ammo.RoundsForFreshMagazine());
                emptyToo = true;
            }
            else if (Ammo.UsesDetachableMag)
            {
                kind = PropKind.Magazine;
                rounds = Ammo.RoundsForFreshMagazine();
            }
            else if (Data.Action == ActionType.SingleShot)
            {
                kind = PropKind.Rocket;
                rounds = Ammo.MagRounds == 0 && (Ammo.InfiniteSpare || Ammo.SpareAmmo > 0) ? 1 : 0;
            }
            else
            {
                kind = PropKind.Shell;
                // (A tube-fed shotgun: ShellsPerReach in the hand, pushed in one by one. Not in a test run: its hand loads one a reach.)
                int reach = Data.Action == ActionType.ShellFed && !Breaks && (!Core.AutoTest.Requested || TestShellsPerReach > 1)
                    ? Mathf.Clamp(Core.AutoTest.Requested ? TestShellsPerReach : VRConfig.ShellsPerReach.Value, 1, 4) : 1;
                rounds = Data.Action == ActionType.ShellFed || Breaks
                    ? Mathf.Min(reach, Mathf.Min(Ammo.TubeRoom, Ammo.InfiniteSpare ? reach : Ammo.SpareAmmo)) * (Ammo.ShellChunk() > 0 ? 1 : 0)
                    : Mathf.Min(Ammo.TubeRoom, Ammo.InfiniteSpare ? int.MaxValue : Ammo.SpareAmmo);
            }
            if (rounds <= 0 && !emptyToo)
            {
                if (kind == PropKind.Rocket)
                {
                    Log.InfoOnce("rocketnone:" + Data.Key, $"No rocket from the pouch for {Data.Key}: {(Ammo.MagRounds > 0 ? "it is still loaded" : "no rockets left")} (spare {Ammo.SpareAmmo}).");
                }
                off.Haptic(0.15f, 35f, 0.7f);
                _sys.FlashMessage(Ammo.SpareAmmo <= 0 && !Ammo.InfiniteSpare ? "NO AMMO" : "FULL");
                return;
            }
            MagazineProp prop = MagazineProp.Create(Rig, kind, rounds);
            if (prop == null || prop.Go == null)
            {
                return; // (nothing made: the reserve is not touched; it was taken first, and those rounds were lost; holster scout)
            }
            Ammo.TakeSpare(rounds); // (none for an empty one)
            if (emptyToo && rounds <= 0)
            {
                _sys.FlashMessage("EMPTY MAG"); // (the reserve is out: to be filled by hand, not a fault of the pouch)
            }
            prop.Owner = Ammo;
            prop.Attach(off);
            prop.FollowHand();
            prop.ShowShellCount(); // (two in the hand drawn as two)
            if (kind == PropKind.Rocket && prop.Go != null)
            {
                // What the hand holds, once per launcher (a rocket in the hand was reported invisible).
                int drawn = 0;
                float size = 0f;
                foreach (Renderer r in prop.Go.GetComponentsInChildren<Renderer>(false))
                {
                    if (r.enabled && !r.forceRenderingOff)
                    {
                        drawn++;
                        size = Mathf.Max(size, r.bounds.size.magnitude);
                    }
                }
                Log.InfoOnce("rocketheld:" + Data.Key, $"Rocket from the pouch for {Data.Key}: {drawn} drawn renderer(s), {size * 100f:0} cm across, "
                                                       + $"{Vector3.Distance(prop.Go.transform.position, off.Palm.position) * 100f:0} cm from the palm, layer {prop.Go.layer}.");
            }
            _held.Sounds.Play(MechSound.Pouch, off.Palm.position, 0.6f); // (the gun's own "get mag" where it has one)
            off.Haptic(0.03f, 140f, 0.45f);
        }

        // ---- held props: free, then on the rail --------------------------------------------------------

        /// <summary>Autotest: shells a reach for its two-shell step (0: one, as the test's hand loads).</summary>
        internal static int TestShellsPerReach;

        private bool AcceptsNow(MagazineProp prop)
        {
            if (!Ours(prop) || !Manual)
            {
                return false;
            }
            switch (prop.Kind)
            {
                case PropKind.Magazine:
                    // An en-bloc clip goes in only with rounds in it (until 2026-11-10 also only through the open action; see below): the
                    // empty one the last shot threw out, picked up off the ground and pressed home, left the bolt locked on nothing to feed, and no
                    // way took it out again, so no other clip went in (user, 2026-11-08: "i cant reload it rn").
                    // (2026-11-10: also at a shut bolt. The release throws the clip out with the bolt home on a round, and the next clip
                    // was refused until the rod was pulled, which nothing on the gun said: "i cant insert the mag".)
                    return Ammo.UsesDetachableMag && !Ammo.MagInserted && (!Data.EnBloc || prop.Rounds > 0);
                case PropKind.Rocket:
                    return Ammo.MagRounds == 0;
                default:
                    return Ammo.TubeRoom > 0 && (!Breaks || Ammo.Open);
            }
        }

        private void UpdateHeldProp(VRHand off)
        {
            if (ReferenceEquals(off.Held, SeatedHold))
            {
                UpdateSeatedHold(off);
                return;
            }
            if (off.Held is Casings.Case)
            {
                return; // a case or round in the fingers: HandLoading follows it and lets it go
            }
            var prop = off.Held as MagazineProp;
            if (prop == null)
            {
                off.Role = HandRole.Free;
                if (_palmed != null)
                {
                    PromotePalmed(off);
                }
                return;
            }
            if (prop.Go == null)
            {
                prop.Destroy(); // gone with a scene change: frees the hand (Drop threw on the destroyed object; prop physics hunt, 2026-10-21)
                return;
            }
            if (!off.GrabHeld)
            {
                ReleaseProp(off, prop);
                return;
            }
            if (prop.LostByHand)
            {
                // The hand went 45 cm on past the gun it could not pass through: it let go, the magazines fall where they were. The
                // palmed one too: left, it stayed drawn in the emptied hand, nothing releasing it (round-6 scout). Not promoted into
                // the hand: still 45 cm past the gun, it was lost again the next frame.
                prop.LostByHand = false;
                prop.Drop(Vector3.zero, Vector3.zero); // out of the hand (Drop frees it)
                MagazineProp palmed = _palmed;
                _palmed = null;
                if (palmed != null)
                {
                    if (palmed.Go != null)
                    {
                        palmed.Drop(Vector3.zero, Vector3.zero);
                    }
                    else
                    {
                        palmed.Destroy();
                    }
                }
                off.Haptic(0.03f, 90f, 0.4f);
                return;
            }
            if (TryMagKick(off, prop))
            {
                return;
            }
            bool refused = !AcceptsNow(prop);
            if (refused && _pullingIn == prop)
            {
                _pullingIn = null;
            }
            if (refused && !(prop.OnRail && prop.RailOutOnly))
            {
                prop.OnRail = false;
                if (_pullingIn == prop)
                {
                    _pullingIn = null;
                }
                // Pushed into a well that will not take it (a magazine still seated, a full tube, a loaded launcher): a short
                // blocked knock, at most once a second, instead of nothing at all (85-agent review).
                float refusedAlong = HandAlongRail(off, prop, out float refusedLateral);
                if (refusedLateral < 0.05f && refusedAlong > -0.02f && refusedAlong < prop.RailLength && Time.unscaledTime > _refusedAt + 1f)
                {
                    _refusedAt = Time.unscaledTime;
                    off.Haptic(0.04f, 70f, 0.55f); // felt only: no knock sound against the gun (user, 2026-10-16)
                    if (prop.Kind == PropKind.Magazine && Data.EnBloc && Ours(prop) && prop.Rounds == 0 && !Ammo.MagInserted)
                    {
                        _sys.FlashMessage("EMPTY CLIP"); // (the one the last shot threw out: a full one from the pouch goes in)
                    }
                    else if (prop.Kind == PropKind.Magazine && Data.EnBloc && Ours(prop) && Ammo.Chambered && !Ammo.SlideLocked && !Ammo.MagInserted)
                    {
                        // An M1's clip at a shut bolt: it goes in only through the open action (pull the rod back). It was a buzz and nothing more.
                        _sys.FlashMessage("OPEN IT");
                        Log.InfoOnce("enbloc-refused:" + Data.Key, $"{Data.Key}: a clip refused, the bolt shut on a round (it goes in through the open action).");
                    }
                    else if (prop.Kind == PropKind.Magazine && Data.EnBloc && Ours(prop) && Ammo.MagInserted)
                    {
                        Log.InfoOnce("enbloc-full:" + Data.Key, $"{Data.Key}: a clip refused, the gun counts one in already ({Ammo.MagRounds} rounds, bolt {(Ammo.SlideLocked ? "locked back" : "home")}).");
                    }
                    else if (prop.Kind == PropKind.Magazine && !Ours(prop))
                    {
                        _sys.FlashMessage("WRONG MAG"); // (taken up from another gun: it looks like one, and a buzz said nothing of why it did not go in)
                        Log.InfoOnce("wrongmag:" + prop.WeaponKey + ">" + Data.Key, $"{Data.Key}: refused a magazine of {prop.WeaponKey} (part '{prop.Data?.MagazineName}', this gun's '{Data.MagazineName}').");
                    }
                    else if (prop.Kind == PropKind.Shell && Breaks && !Ammo.Open)
                    {
                        _sys.FlashMessage("OPEN IT"); // (a shell at a shut break action or cylinder: B opens it)
                    }
                    else if (prop.Kind == PropKind.Rocket)
                    {
                        _sys.FlashMessage("LOADED"); // (a second rocket at the mouth: the pouch says FULL, here it was a buzz)
                    }
                }
                return;
            }

            if (!prop.OnRail && prop.Kind == PropKind.Shell && Data.ChamberShellRest.Count > 1 && prop.Go != null)
            {
                // A double barrel's shell goes into the empty chamber it is held by (both took it into the first one).
                _seatChamber = NearestEmptyChamber(prop.Go.transform.TransformPoint(prop.TipLocal));
            }
            RigidPose seat = SeatPose;
            Vector3 insert = seat.TransformDirection(prop.InsertDirLocal);
            RigidPose hold = prop.HoldPose(off);
            if (!prop.OnRail && prop.Kind == PropKind.Magazine && WellInGrip)
            {
                GripWellRail(prop);
            }
            if (!prop.OnRail && prop.Kind == PropKind.Magazine && Data.EnBloc)
            {
                // An en-bloc clip's way in starts above the receiver (8 cm of it): its 3 cm began inside the gun.
                prop.RailFromGripBottom(0.08f);
            }
            float along = HandAlongRail(off, prop, out float lateral);

            if (!prop.OnRail)
            {
                if (_pullingIn == prop)
                {
                    // (Off its way in again, pulled back out or let go: lined up once more it seated itself with no push, as if pushed
                    // home; magazine scout, 2026-11-12.)
                    _pullingIn = null;
                }
                prop.RailHandOffset = 0f;
                prop.RailLateral0 = 0f;
                prop.FromSeat = false;
                float angle = Vector3.Angle(hold.TransformDirection(prop.InsertDirLocal), insert);
                // A pistol's well is in the gun hand's grip: the controllers meet before the magazine is quite lined up, so
                // it is taken a little more loosely there.
                bool grip = prop.Kind == PropKind.Magazine && WellInGrip;
                // A rocket is lined up at the launcher's mouth, an arm's length out on a shouldered launcher: taken more
                // loosely too (it was close to impossible to start).
                // Only at the tube's mouth: a rocket goes in at the front, its tail first (user, 2026-10-16); held beside the
                // tube with its tail level with the middle, it was taken into the tube from the side.
                bool rocket = prop.Kind == PropKind.Rocket;
                // Taken loosely only at the well's mouth, where a magazine goes in (its tip within 2 cm of it). Higher up, off
                // the line, the magazine is beside the gun: taken there, it slid sideways onto its line through the grip's (or
                // the receiver's) wall in the 0.08 s switch, and sat in the well (user, 2026-10-19, headset: "the mag goes
                // through the handle", the 1911s most; task 270). Deeper than the mouth only on the line already (within the
                // well's play: the keep-out lets it no further) and nearly straight.
                // Judged where the magazine is drawn, not where the hand is: held back at the mouth by the gun with the hand
                // pushing on (a fast push passes the mouth's 2 cm in a frame), it is still at the mouth.
                // An en-bloc clip (the M1's) is not judged as a magazine here: it goes in from the top, pressed down, and the
                // hand holds it as a magazine (its way in pointing up out of the fist), so it was never within 35 degrees of
                // the way in and never taken onto it: "m1 garret cant be reloaded still ... putting a new one in doesnt work"
                // (user, 2026-11-10, twice). Taken by where the hand has it, over the open action, however it is turned; on
                // its way in it is drawn as it goes into the gun.
                bool enBloc = prop.Kind == PropKind.Magazine && Data.EnBloc;
                bool magazine = prop.Kind == PropKind.Magazine && !enBloc;
                float drawnLateral = lateral;
                float drawnAlong = magazine && prop.Go != null ? PoseAlongRail(prop, RigidPose.Of(prop.Go.transform), out drawnLateral) : along;
                bool atMouth = !magazine || drawnAlong < MouthReach + 0.005f;
                float lateralMax = rocket ? 0.06f : enBloc ? 0.06f : !atMouth ? MagazineProp.WellPlay * 0.75f : grip ? 0.045f : 0.035f;
                float angleMax = enBloc ? 181f : !atMouth ? MagazineProp.InWellTurn : grip || rocket ? 40f : 35f;
                if (magazine && atMouth)
                {
                    // How forgiving the well's mouth is (MagazineInsertHelp: other VR shooters' "easy magazine loading"). Only at
                    // the mouth: deeper in, the well's own play decides (a wider window there took it through the wall).
                    MagazineHelp help = VRConfig.MagazineInsertHelp.Value;
                    lateralMax *= help == MagazineHelp.Easy ? 1.6f : help == MagazineHelp.Exact ? 0.75f : 1f;
                    angleMax *= help == MagazineHelp.Easy ? 1.5f : help == MagazineHelp.Exact ? 0.8f : 1f;
                }
                bool lined = (magazine ? drawnLateral : lateral) < lateralMax && along > (rocket || enBloc ? -0.03f : -0.005f) && (rocket ? along < RocketTakenIn : (magazine ? drawnAlong : along) < prop.RailLength)
                             && angle < angleMax;
                if (rocket && !lined && LooselyLined(prop, hold, seat, insert, out float looseDepth))
                {
                    // A rocket held like a stick (along the fist, either end first) at the launcher's mouth: onto the line
                    // as it is, and from there it follows the hand along the tube. It had to be held with its tail out of
                    // the top of the fist within 40 degrees of the tube, and scoped RPGs "could not be reloaded" (Tasks 231).
                    // Where the hand has it: the line reaches out in front of the mouth. Taken onto the mouth itself, a rocket
                    // taken 10 cm out jumped 10 cm and stayed ahead of the hand, loaded at 80% of the push (rocket film).
                    lined = true;
                    prop.RailHandOffset = 0f;
                    prop.RailLateral0 = lateral;
                }
                else if (rocket && !lined && Time.unscaledTime > _refusedAt + 1f && NoseFirst(prop, hold, seat, insert))
                {
                    // Its warhead at the mouth: a rocket goes in tail first, and held the other way it stopped there with nothing said.
                    _refusedAt = Time.unscaledTime;
                    off.Haptic(0.04f, 70f, 0.55f);
                    _sys.FlashMessage("TAIL FIRST");
                }
                if (lined && magazine && Time.unscaledTime - prop.LeftRailAt < 0.25f && lateral > lateralMax)
                {
                    // Just taken off the line by the hand going aside, the hand still off it: not onto it again. It is judged by
                    // where the magazine is DRAWN, which for the 80 ms of the switch is still at the mouth: it went on again the
                    // next frame, off again the one after (the hand 7 cm aside), about 45 times a second, clicking each time, the
                    // glove pinned and free in turn (scout, 2026-11-11: "mags are glitching around").
                    lined = false;
                }
                if (prop.BackOutFirst)
                {
                    // (The hand has to come back out to the first third of the way in before the next shell starts.)
                    if (along < prop.RailLength * 0.35f)
                    {
                        prop.BackOutFirst = false;
                    }
                    else
                    {
                        lined = false;
                    }
                }
                if (enBloc && !lined && lateral < 0.15f && along > -0.12f && along < prop.RailLength + 0.05f && Log.DebugOn)
                {
                    // (Only with the debug log: built every frame the clip hovered there, though logged once.)
                    Log.InfoOnce("enbloc-near:" + Data.Key, $"{Data.Key}: a clip near the open action, not on its way in yet: {along * 100f:0.0} cm along its {prop.RailLength * 100f:0.0} cm way, {lateral * 100f:0.0} cm aside (6 at most), turned {angle:0} deg, {prop.Rounds} round(s).");
                }
                if (lined)
                {
                    if (enBloc)
                    {
                        Log.InfoOnce("enbloc-on:" + Data.Key, $"{Data.Key}: a clip taken onto its way in at {along * 100f:0.0} cm of {prop.RailLength * 100f:0.0} cm, {lateral * 100f:0.0} cm aside, turned {angle:0} deg in the hand.");
                    }
                    prop.OnRail = true;
                    _railInSpeed = 0f;
                    prop.RailDepth = Mathf.Clamp(along, rocket ? -RocketLineAhead : 0f, prop.RailLength);
                    if (magazine)
                    {
                        // On at the depth where it is drawn, the hand's way in counted from there: held back at the mouth it starts
                        // at the mouth; taken off the line there, at the mouth itself, so it moves onto the line under the well and
                        // not through its lowest centimetre (task 270).
                        prop.RailDepth = drawnLateral > MagazineProp.WellPlay * 0.75f ? 0f : Mathf.Clamp(drawnAlong, 0f, prop.RailLength);
                        prop.RailHandOffset = along - prop.RailDepth;
                    }
                    prop.LastTick = prop.RailDepth;
                    prop.MovingIn = false;
                    prop.TurnDepth = prop.RailDepth;
                    off.Haptic(0.015f, 180f, 0.3f);
                    if (prop.Kind == PropKind.Magazine)
                    {
                        _sys.Sounds.Play(MechSound.MagLip, SeatPose.position, 0.5f, 0f, Data); // onto the well's lip
                    }
                }
                return;
            }

            if (_pullingIn == prop)
            {
                // Started straight into a well inside the grip: the rail takes it home (see below), whatever the hand does.
                // A damped spring (about a third of a second, at most 0.6 m/s), not a constant 0.9 m/s: the hand turning
                // from holding it into pushing it home (PushPalm) was over in a tenth of a second and never seen.
                prop.RailDepth = Mathf.SmoothDamp(prop.RailDepth, prop.RailLength, ref prop.DepthVel, 0.09f, 0.6f, Mathf.Min(Time.unscaledDeltaTime, 0.05f));
                if (prop.RailDepth >= prop.RailLength - 0.001f)
                {
                    _pullingIn = null;
                    SeatProp(off, prop);
                    BeginSeatedHold(off, prop); // (pushed home in the hand: the hand stays on it while it holds)
                }
                return;
            }
            if (prop.AnchorOnRail)
            {
                prop.AnchorOnRail = false;
                prop.RailHandOffset = along; // (the shell before it just went in: this one starts at the mouth from the hand's place)
            }
            // On the rail: the hand pushes the prop along the axis only (from where it took it, when pulled out).
            along -= prop.RailHandOffset;
            // A rocket in the tube is held by the tube: it comes out only the way it went in, back out of the mouth. The hand
            // off its line leaves it on the line (it came off sideways through the tube's wall) until the hand is 35 cm off.
            bool inTube = prop.Kind == PropKind.Rocket && prop.RailDepth > 0.03f;
            // A magazine still in its well is held by the well the same way: it slides out along it, never sideways out of it.
            bool inWell = prop.Kind == PropKind.Magazine && prop.RailDepth > 0.01f;
            float offLine = prop.Kind == PropKind.Rocket ? -RocketLineAhead - 0.03f : -0.03f;
            if (along < offLine || lateral > (inTube ? 0.35f : inWell ? 0.2f : 0.07f) + prop.RailLateral0)
            {
                prop.OnRail = false;
                prop.LeftRailAt = Time.unscaledTime;
                prop.RailOutOnly = false;
                prop.RailHandOffset = 0f;
                prop.RailLateral0 = 0f;
                prop.FromSeat = false;
                _railInSpeed = 0f;
                return;
            }
            prop.PrevDepth = prop.RailDepth;
            prop.RailDepth = Mathf.Clamp(along, prop.Kind == PropKind.Rocket ? -RocketLineAhead : 0f, prop.RailLength);
            if (prop.Kind == PropKind.Magazine)
            {
                // How hard it is driven in, against the gun (the mag kick's measure: both hands share the body's motion).
                _railInSpeed = Mathf.Max(_railInSpeed, Vector3.Dot(off.BodyThrowVelocity() - _sys.Rig.Dominant.BodyThrowVelocity(),
                                                                  SeatPose.TransformDirection(prop.InsertDirLocal)));
            }
            // Which way it is going, with 3 mm of play: a slow push still turns the hand into the push and a slow pull lets
            // go of it (a millimetre a frame was a speed: slower than 9 cm/s neither ever happened; bug hunt 2026-10-16).
            if (prop.MovingIn ? prop.RailDepth > prop.TurnDepth : prop.RailDepth < prop.TurnDepth)
            {
                prop.TurnDepth = prop.RailDepth; // still going the same way: the turning point goes with it
            }
            else if (Mathf.Abs(prop.RailDepth - prop.TurnDepth) > 0.003f)
            {
                prop.MovingIn = !prop.MovingIn;
                prop.TurnDepth = prop.RailDepth;
            }
            if (prop.FromSeat && prop.RailDepth < prop.RailLength - 0.015f)
            {
                prop.FromSeat = false; // out a little: pushed back now, it seats again
            }
            bool goingIn = prop.RailDepth > prop.PrevDepth + 1e-4f;
            if (Mathf.Abs(prop.RailDepth - prop.LastTick) > 0.01f)
            {
                prop.LastTick = prop.RailDepth;
                off.Haptic(0.006f, 200f, 0.15f);
            }
            if (prop.FromSeat)
            {
                // Tugged 5 mm and pushed back home it seats (under the 1.5 cm above it never did, drawn fully in and not in the gun: the
                // let-go dropped it out of a full well; rail scout, 2026-11-06).
                prop.TuggedFromSeat |= prop.RailDepth < prop.RailLength - 0.005f;
                if (!prop.TuggedFromSeat || prop.RailDepth < prop.RailLength * 0.985f)
                {
                    return; // just pulled out of its seat: neither seated again nor drawn home until it comes out a little
                }
                prop.FromSeat = false;
            }
            if (refused)
            {
                // Out only: pushed back it stops short of its seat (the gun will not take it now).
                prop.RailDepth = Mathf.Min(prop.RailDepth, prop.RailLength * 0.9f);
                return;
            }
            prop.RailOutOnly = false;
            if (DetentApplies(prop))
            {
                // The latch (user, 2026-11-03, idea 8): the last few millimetres take a firm push or a push on past the seat; a gentle
                // one stops just short, felt as the magazine meets the latch.
                float latch = prop.RailLength - DetentDepth(prop);
                if (prop.RailDepth > latch)
                {
                    float pushSpeed = Vector3.Dot(off.BodyThrowVelocity() - _sys.Rig.Dominant.BodyThrowVelocity(), SeatPose.TransformDirection(prop.InsertDirLocal));
                    // (Held below 0.25 m/s once it holds, let through above 0.35: at one line of 0.3 a push near it went through and
                    // was held again frame by frame, the magazine jumping the last 8 mm in and out; scout, 2026-11-11.)
                    if (pushSpeed < (_atDetent ? DetentSpeed + 0.05f : DetentSpeed - 0.05f) && along - prop.RailLength < DetentPush)
                    {
                        prop.RailDepth = latch;
                        if (!_atDetent)
                        {
                            _atDetent = true;
                            off.Haptic(0.02f, 110f, 0.45f);
                        }
                    }
                }
                else if (prop.RailDepth < latch - 0.004f)
                {
                    _atDetent = false;
                }
            }
            // (98.5%, not 96: the prop gave way 3.6-5 mm short of the seat and the gun's magazine popped up there; review, 2026-10-20.)
            if (prop.RailDepth >= prop.RailLength * 0.985f)
            {
                SeatProp(off, prop);
                BeginSeatedHold(off, prop);
            }
            else if (goingIn && prop.Kind == PropKind.Magazine && prop.RailDepth >= prop.RailLength * 0.25f
                     && (WellInGrip || VRConfig.MagazineInsertHelp.Value == MagazineHelp.Easy && !Data.IsMachineGun && !Data.EnBloc))
            {
                // A pistol's well is in its grip, where the gun hand is: the two controllers meet before the magazine is all
                // the way in. A quarter of the way in and straight, it is drawn home the rest of the way. A rocket is not:
                // it slides into the tube with the hand the whole way, the hand never letting go of it (user, 2026-10-16).
                _pullingIn = prop;
                // On from the hand's speed, but no faster than the spring's own 0.6 m/s (a 2 m/s push seated it in a frame or
                // two; bug hunt 2026-10-16).
                prop.DepthVel = Mathf.Clamp((prop.RailDepth - prop.PrevDepth) / Mathf.Max(Time.unscaledDeltaTime, 1f / 120f), 0f, 0.6f);
                off.Haptic(0.012f, 160f, 0.35f);
            }
        }

        /// <summary>How far past the tube's mouth a rocket's tail may be when it is taken onto the line (it goes in at the front).</summary>
        private const float RocketTakenIn = 0.08f;

        /// <summary>How far in front of the mouth a rocket's line reaches (where it is taken onto it, loosely held).</summary>
        private const float RocketLineAhead = 0.12f;

        /// <summary>
        /// A rocket at the launcher's mouth, loosely held: its tail (the end that goes in) within 12 cm of the tube's line
        /// at the muzzle, the rocket pointing into the tube within 60 degrees. Tail first only, as an RPG is loaded and as the
        /// rocket is drawn on the line: taken by its warhead end it was drawn a rocket's length away from the hand, or
        /// turned over (85-agent review). At the mouth only, no deeper than <see cref="RocketTakenIn"/>: held beside the tube
        /// it was taken in from the side. <paramref name="depth"/>: how far in its tail already is.
        /// </summary>
        private static bool LooselyLined(MagazineProp prop, RigidPose hold, RigidPose seat, Vector3 insert, out float depth)
        {
            Vector3 mouth = seat.TransformPoint(prop.TipLocal) - insert * prop.RailLength;
            Vector3 tail = hold.TransformPoint(prop.TipLocal), nose = hold.TransformPoint(prop.BaseLocal);
            Vector3 rel = tail - mouth;
            float along = Vector3.Dot(rel, insert);
            depth = Mathf.Max(0f, along);
            float lateral = Vector3.ProjectOnPlane(rel, insert).magnitude;
            float angle = Vector3.Angle(tail - nose, insert);
            return lateral < 0.12f && along > -0.12f && along < RocketTakenIn && angle < 60f;
        }

        /// <summary>A rocket at the launcher's mouth the wrong way round: its warhead end there, pointing into the tube.</summary>
        private static bool NoseFirst(MagazineProp prop, RigidPose hold, RigidPose seat, Vector3 insert)
        {
            Vector3 mouth = seat.TransformPoint(prop.TipLocal) - insert * prop.RailLength;
            Vector3 tail = hold.TransformPoint(prop.TipLocal), nose = hold.TransformPoint(prop.BaseLocal);
            Vector3 rel = nose - mouth;
            float along = Vector3.Dot(rel, insert);
            return Vector3.ProjectOnPlane(rel, insert).magnitude < 0.12f && along > -0.12f && along < RocketTakenIn && Vector3.Angle(tail - nose, insert) > 120f;
        }

        /// <summary>
        /// How far along the well's rail the held prop's tip is (from the rail's mouth; the rail's length = seated), and how
        /// far off the rail's line.
        /// </summary>
        private float HandAlongRail(VRHand off, MagazineProp prop, out float lateral)
        {
            // Against the gun as the gun hand carries it, not as drawn: it trails a swing by its weight (up to 8 cm, HeldWeapon.InertiaLag)
            // and the hand does not, so a swing moved a magazine in the well in or out by the lag, or took it off the line and on again,
            // each time flown round to the hand (user, 2026-11-03: "mags ... flying in the air trying to correct their positions").
            RigidPose hold = prop.HoldPose(off);
            // (An en-bloc clip by where it is, not by how the fist turns it: its way in is measured at its tip, and a clip turned
            // 120 degrees in the hand never reached the seat's depth: "taken onto its way in, NOT SEATED" in the check, and the
            // user's "putting a new one in doesnt work still".)
            Quaternion turn = prop.Kind == PropKind.Magazine && Data.EnBloc ? SeatPose.rotation : hold.rotation;
            // (And as its recoil and nudges have it: a shot or a nudge moved the drawn gun 1 to 3 cm, read here as the hand going in or
            // out by as much, a shell or rocket along the bore most; scout, 2026-11-11.)
            return PoseAlongRail(prop, new RigidPose(hold.position + _held.InertiaLag + _held.RecoilShift, turn), out lateral);
        }

        /// <summary>How deep the gun's well in its grip is from the side (its seated magazine inside the gun), -1 until measured.</summary>
        private float _gripWellDepth = -1f;
        /// <summary>No new measure before this (unscaled): the side view not ready, it was walked every frame of the hold; scout.</summary>
        private float _gripWellRetryAt;

        /// <summary>
        /// A magazine going into a well in the grip runs on its rail from the grip's bottom: its rail was at most 9 cm, and on
        /// the 1911s (12.6 cm in the grip seated) it began 3.6 cm up inside the grip, where the magazine was still free, turned
        /// up to 40 degrees and 4 mm off its line, and showed through the grip's walls on the way in (user, 2026-10-19: "the
        /// mag goes through the handle", the 1911s most; task 270). Measured once per hold, from the side, as the autotest does.
        /// </summary>
        private void GripWellRail(MagazineProp prop)
        {
            float length = Vector3.Dot(prop.TipLocal - prop.BaseLocal, prop.InsertDirLocal);
            if (_gripWellDepth < 0f && _held.HandsSolid != null && Time.unscaledTime >= _gripWellRetryAt)
            {
                RigidPose seated = SeatPose;
                float depth = 0f;
                for (float s = 0f; s <= length; s += 0.002f)
                {
                    if (!_held.InsideGunFromSide(seated.TransformPoint(prop.TipLocal - prop.InsertDirLocal * s)))
                    {
                        break;
                    }
                    depth = s;
                }
                _gripWellRetryAt = Time.unscaledTime + 0.25f;
                if (depth > 0.01f) // else the gun's side view is not ready yet: measured again in a moment
                {
                    _gripWellDepth = depth;
                    Log.InfoOnce("gripwell:" + Data.Key, $"{Data.Key}: the well in the grip is {depth * 100f:0.0} cm deep from the side; a magazine's rail starts at its bottom ({prop.RailLength * 100f:0.0} cm before).");
                }
            }
            if (_gripWellDepth > prop.RailLength)
            {
                prop.RailFromGripBottom(Mathf.Min(_gripWellDepth + 0.005f, length));
            }
        }

        /// <summary>How far along its rail a prop at <paramref name="pose"/> is (0: its tip at the entry), and how far off it.</summary>
        private float PoseAlongRail(MagazineProp prop, RigidPose pose, out float lateral)
        {
            RigidPose seat = SeatPose;
            Vector3 insert = seat.TransformDirection(prop.InsertDirLocal);
            Vector3 entryTip = seat.TransformPoint(prop.TipLocal) - insert * prop.RailLength;
            Vector3 rel = pose.TransformPoint(prop.TipLocal) - entryTip;
            lateral = Vector3.ProjectOnPlane(rel, insert).magnitude;
            return Vector3.Dot(rel, insert);
        }

        /// <summary>The held magazine is under its well and roughly lined up (6 cm aside, 40 degrees): about to go in.</summary>
        private bool LinedUpUnderWell(VRHand off, MagazineProp prop)
        {
            if (prop.Kind != PropKind.Magazine || !AcceptsNow(prop))
            {
                return false;
            }
            float along = HandAlongRail(off, prop, out float lateral);
            float angle = Vector3.Angle(prop.HoldPose(off).TransformDirection(prop.InsertDirLocal), SeatPose.TransformDirection(prop.InsertDirLocal));
            return lateral < 0.06f && angle < 40f && along > -0.06f && along < prop.RailLength;
        }

        /// <summary>
        /// How far into its well (from the entry: its tip at the mouth) a magazine off its line can still be taken onto it. Past
        /// that it is beside the gun, not under its well (task 270).
        /// </summary>
        internal const float MouthReach = 0.015f;

        /// <summary>How far the support hand may be pulled from the handguard with the grip closed before it lets go.</summary>
        private const float SupportLetGo = 0.6f;
        /// <summary>How far off a racked part's stroke line the hand may drift before it lets go of the part.</summary>
        private const float RackDrift = 0.35f;
        /// <summary>
        /// How far past either end of its stroke the hand may pull a racked part before it lets go (the part stops there; the
        /// real hand does not): Boneworks' grips break the same way once the hand has desynced from them.
        /// </summary>
        private const float RackOvershoot = 0.3f;
        /// <summary>How far the glove of a hand working a part stays on it: to the rack's let-go, drift and overshoot together.</summary>
        private const float HeldPartReach = 0.46f;
        private float _nextStretchBuzz;
        /// <summary>
        /// The support hand's distance from its own point (see <see cref="SupportAtOwnPoint"/>) where the hold took it: the stretch
        /// counts from there, so a palm a few centimetres off the point on the grab is not a stretch. -1 until the hold is measured.
        /// </summary>
        private float _supportRest = -1f;

        /// <summary>A magazine that is being drawn the last part of the way into a well inside the grip.</summary>
        private MagazineProp _pullingIn;

        /// <summary>The magazine well is inside the gun hand's grip (pistols, magazine-in-grip designs).</summary>
        private bool WellInGrip
        {
            get
            {
                WeaponRigData d = Data;
                Vector3 magCentre = d.MagSeat.TransformPoint(d.MagBounds.center);
                return d.IsPistol || Mathf.Abs(Vector3.Dot(magCentre - d.GripPoint, d.AimAxis)) < 0.05f;
            }
        }

        /// <summary>
        /// Tactical reload with one hand: driving a fresh magazine into the base of the seated one knocks the old
        /// one out of the well; the new one then slides in on the rail. Any magazine of the gun does it (an empty one
        /// too), struck fast or pushed slowly and firmly past the old one's base, held straight or tilted; the window is
        /// as big as the magazine. A magazine moving past beside the gun, clear of the base, does nothing.
        /// </summary>
        private bool TryMagKick(VRHand off, MagazineProp prop)
        {
            if (!FitsWell(prop) || !Manual || !Ammo.UsesDetachableMag || !Ammo.MagInserted
                || _ejecting != null || Ammo.BoxLocked)
            {
                _kickPrevAlong = float.NaN;
                _kickPushSince = -1f;
                return false;
            }
            RigidPose seat = SeatPose;
            Vector3 insert = seat.TransformDirection(prop.InsertDirLocal);
            float length = Vector3.Dot(prop.TipLocal - prop.BaseLocal, prop.InsertDirLocal);
            Vector3 oldBase = seat.TransformPoint(prop.TipLocal) - insert * length;
            RigidPose hold = prop.HoldPose(off);
            hold = new RigidPose(hold.position + _held.InertiaLag, hold.rotation); // (as the rail: against the gun as carried, or a swing struck the old one)
            Vector3 rel = hold.TransformPoint(prop.TipLocal) - oldBase;
            float along = Vector3.Dot(rel, insert);
            float lateral = Vector3.ProjectOnPlane(rel, insert).magnitude;
            float angle = Vector3.Angle(hold.TransformDirection(prop.InsertDirLocal), insert);
            // The speed that counts is the new magazine's speed relative to the gun: driving it up into the old one
            // and bringing the gun down onto it are the same strike.
            // Averaged over the last moment (raw controller speed is noisy around these thresholds).
            // Against the body: both hands share the rig's motion, but taken at each hand a turn moved them apart by its spin
            // times their gap (0.2-0.4 m/s at a smooth turn), around these thresholds (user, 2026-10-20: the mag kick worse).
            float speed = Vector3.Dot(off.BodyThrowVelocity() - _sys.Rig.Dominant.BodyThrowVelocity(), insert);
            // Sized by the magazine: a strike lands anywhere on the old base plate (as wide as the magazine) with any part
            // of the fresh one's top; a slow push has to be on the base itself.
            float strikeReach = Mathf.Clamp(prop.CrossHalf * 2f + 0.025f, 0.06f, 0.13f);
            float pushReach = Mathf.Clamp(prop.CrossHalf + 0.025f, 0.035f, 0.08f);
            float below = Mathf.Clamp(length * 0.25f, 0.04f, 0.08f);
            float into = Mathf.Clamp(length * 0.45f, 0.06f, 0.12f);
            bool near = along > -0.12f && along < into + 0.04f && lateral < strikeReach + 0.04f;
            if (near && Data.MagKickable && Log.DebugOn && Time.unscaledTime - _kickLogAt > 0.3f)
            {
                // Numbers for tuning in the headset: which condition holds a kick back (debug logging only: three lines a
                // second flooded the log while a magazine was near the well; 255.P2-5; and built only then: the line was made
                // three times a second during every reload with logging off, allocation hunt 2026-10-24).
                _kickLogAt = Time.unscaledTime;
                Log.Debug($"Mag kick test {Data.Key}: along {along * 100f:0.0} cm, lateral {lateral * 100f:0.0} cm (reach {strikeReach * 100f:0.0}), angle {angle:0} deg, speed {speed:0.00} m/s, kickable {Data.MagKickable}, rounds {prop.Rounds}");
            }
            // Passing the old magazine's base between two frames counts even when no single frame was inside the window.
            bool crossed = !float.IsNaN(_kickPrevAlong) && _kickPrevAlong < 0f && along >= 0f;
            _kickPrevAlong = near ? along : float.NaN;
            if (!Data.MagKickable)
            {
                return false; // deep wells (pistol grips, KRISS Vector) leave nothing to hit
            }
            if (lateral > strikeReach || angle > MaxKickAngle)
            {
                _kickPushSince = -1f;
                return false;
            }
            if (Time.unscaledTime - _seatedAt < 0.4f)
            {
                return false; // the push that just seated a magazine must not knock it straight back out with the next one
            }
            bool inWindow = along >= -below && along <= into;
            if (inWindow && !_kickTicked)
            {
                _kickTicked = true;
                off.Haptic(0.01f, 60f, 0.25f); // you are on the old magazine's base
            }
            else if (!inWindow)
            {
                _kickTicked = false;
            }
            // A slow, firm push: the fresh magazine's top pressed past the old one's base (where the old one would have to
            // give way), held there a moment or pushed on, and not being pulled back out.
            // Roughly in line (a strike may come in tilted, a push may not): a magazine held beside the old one, tilted 66 degrees and not
            // moving at all, knocked it out and sent it flying (log of 2026-11-03: 0.02 m/s, 4.9 cm aside).
            bool pressing = lateral <= pushReach && along >= 0.012f && along <= into && speed > -0.05f && angle <= MaxPushAngle;
            if (!pressing)
            {
                _kickPushSince = -1f;
            }
            else if (_kickPushSince < 0f)
            {
                _kickPushSince = Time.unscaledTime;
            }
            bool push = pressing && (along >= 0.03f || Time.unscaledTime - _kickPushSince >= 0.12f);
            // Crossing at 8 cm/s; compared a little under it (a push at exactly 0.08 m/s was a coin flip: 252 ae).
            // A strike coming in tilted has to mean it: 0.2 m/s straight, up to 0.5 at the most tilt (at 44 degrees a touch at 0.21 m/s knocked the
            // old one out, flying; log of 2026-11-03). The tilted test strike is at 0.6.
            float strikeSpeed = 0.2f + 0.3f * Mathf.Clamp01(angle / MaxKickAngle);
            bool strike = inWindow && speed >= strikeSpeed || crossed && speed >= (angle <= MaxPushAngle ? 0.075f : strikeSpeed);
            if (!strike && !push)
            {
                return false;
            }
            Log.Info($"Mag kick on {Data.Key} ({(strike ? "strike" : "push")}): along {along * 100f:0.0} cm, lateral {lateral * 100f:0.0} cm, angle {angle:0} deg, speed {speed:0.00} m/s");
            _kickPrevAlong = float.NaN;
            _kickPushSince = -1f;
            KickOut(off, prop);
            off.Haptic(0.04f, 80f, 0.8f);
            return true;
        }

        /// <summary>
        /// Knocked out by a fresh magazine: the old one comes off at the well's mouth, set aside by its own width (never out
        /// of the inside of the gun or through the fresh one coming up), and flies clear forward, out in front of the gun
        /// with the strike's push and a little sideways away from the gun hand's side, turning end over end with its top
        /// leading. It used to drop straight down, spinning about the barrel (user, 2026-10-18: "drops straight down instead
        /// of flipping out forward", task 260 a). (It was made at its seat, slid straight out along the well through the fresh
        /// one, and dropped from inside the gun.) The release button still drops it straight out.
        /// </summary>
        private void KickOut(VRHand off, MagazineProp fresh)
        {
            if (_ejecting != null)
            {
                FinishEject();
            }
            int rounds = Ammo.EjectMagazine();
            MagazineProp old = MagazineProp.Create(Rig, PropKind.Magazine, rounds);
            old.Owner = Ammo;
            RigidPose seat = SeatPose;
            Vector3 insert = seat.TransformDirection(old.InsertDirLocal);
            Vector3 up = Rig.UpWorld, aim = Rig.AimWorld;
            Vector3 gunRight = Vector3.Cross(up.y >= 0f ? up : -up, aim).normalized; // (the player's right of the gun: rolled over, its own right sent the magazine at the gun hand)
            // Away from the gun hand: to the left of a right hand's gun, to the right of a left hand's.
            Vector3 side = _sys.Rig.Dominant.IsLeft ? gunRight : -gunRight;
            float width = Mathf.Max(0.02f, old.CrossHalf * 2f);
            old.RailDepth = old.RailLength * 0.15f; // at the mouth
            RigidPose mouth = old.RailPose(seat);
            mouth = new RigidPose(mouth.position + side * width - insert * 0.01f, mouth.rotation);
            mouth.ApplyTo(old.Go.transform);
            Vector3 push = Vector3.ClampMagnitude(off.BodyThrowVelocity() - _sys.Rig.Dominant.BodyThrowVelocity(), 2.5f);
            // Forward along the barrel with the strike and the gun hand's motion; out of the well, a little away from the gun
            // hand and only a touch down. (Sideways and down alone, it fell straight down.)
            Vector3 velocity = _sys.Rig.Dominant.Velocity + push * 0.5f + aim * Random.Range(1.4f, 1.9f) + side * 0.6f - insert * 0.6f + Vector3.down * 0.2f;
            // End over end with the top (the insertion end) leading forward: the spin is about the axis across the gun, insert
            // x aim, which carries the top along the barrel. A spin about the barrel itself only rolled it.
            Vector3 tumble = Vector3.Cross(insert, aim).normalized * Random.Range(6f, 9f) + Random.insideUnitSphere * 1.5f;
            old.Drop(velocity, tumble);
            _held.Sounds.Play(MechSound.MagOut, SeatPose.position);
            _held.Sounds.Play(MechSound.MagKnock, mouth.position, 0.9f); // (the gun's own where it has one: the C8's MAG KICK, the QJB201's Box_Hip)
            _held.Nudge(-insert * 0.06f);
        }

        /// <summary>How far (degrees) a fresh magazine may be tilted off the well's axis and still knock the old one out.</summary>
        private const float MaxKickAngle = 75f;
        /// <summary>The same for a slow push: it has to bear on the old one's base.</summary>
        private const float MaxPushAngle = 35f;

        private bool _kickTicked;
        /// <summary>When the fresh magazine started pressing past the old one's base (-1: not pressing).</summary>
        private float _kickPushSince = -1f;

        /// <summary>The fastest the magazine on the rail has been driven in so far (m/s, against the gun), for the slap release at its seat.</summary>
        private float _railInSpeed;
        /// <summary>A magazine driven home at least this fast (m/s) releases a locked slide (<see cref="VRConfig.MagSlapReleasesSlide"/>).</summary>
        private const float SlapSpeed = 0.9f;

        /// <summary>The next seating is of a magazine that never left its seat (taken and let go at once): no sound, no push.</summary>
        private bool _quietSeat;
        private bool _beltWasLaid, _beltWasLifted;

        private void SeatProp(VRHand off, MagazineProp prop)
        {
            _seatedAt = Time.unscaledTime;
            Vector3 at = SeatPose.position;
            switch (prop.Kind)
            {
                case PropKind.Magazine:
                    if (prop.WeaponKey != Data.Key)
                    {
                        Log.InfoOnce("sistermag:" + prop.WeaponKey + ">" + Data.Key, $"{Data.Key}: took a sister gun's magazine ({prop.WeaponKey}).");
                    }
                    Ammo.InsertMagazine(prop.Rounds);
                    if (!_quietSeat)
                    {
                        _held.Sounds.Play(MechSound.MagIn, at);
                        _held.Nudge(SeatPose.TransformDirection(prop.InsertDirLocal) * 0.14f); // seated with a push into the gun
                    }
                    if (Data.EnBloc && prop.Rounds > 0 && (Ammo.SlideLocked || !Ammo.Chambered))
                    {
                        // The clip pressed home releases the bolt: it slams forward on its own and chambers the first round.
                        ReleaseSlide();
                        _held.Nudge(Rig.AimWorld * 0.12f, 12f);
                        off.Haptic(0.06f, 100f, 0.9f);
                    }
                    else if (Ammo.SlideLocked)
                    {
                        // Slammed home, the locked slide drops and chambers the first round (Boneworks' Gun.isSlideReleaseOnCollision);
                        // a gentle push leaves it locked for the release or a rack. The speed is logged for tuning in the headset.
                        bool slam = VRConfig.MagSlapReleasesSlide.Value && _railInSpeed >= SlapSpeed && Ammo.FeedReady && Ammo.MagRounds > 0;
                        Log.Info($"{Data.Key}: magazine seated at {_railInSpeed:0.00} m/s into the locked gun ({(slam ? "slammed: the slide drops" : "the slide stays locked")}).");
                        if (slam)
                        {
                            ReleaseSlide();
                            _held.Nudge(Rig.AimWorld * 0.12f, 12f);
                            off.Haptic(0.06f, 100f, 0.9f);
                        }
                    }
                    _railInSpeed = 0f;
                    break;
                case PropKind.Shell:
                {
                    if (_seatChamber < _held.ChamberFull.Length)
                    {
                        _held.ChamberFull[_seatChamber] = true; // drawn in the chamber it went into, not the first empty one
                    }
                    if (prop.Rounds > 1 && Ammo.TubeRoom > 1 && ReferenceEquals(off.Held, prop))
                    {
                        // More than one in the hand (ShellsPerReach): this one goes in, the hand keeps the rest and pushes the next
                        // after drawing back a little.
                        Ammo.LoadRounds(1);
                        prop.Rounds--;
                        prop.ShowShellCount();
                        // The next one waits at the port's mouth on its line, its way in counted from where the hand is: a little back
                        // and in again, it goes in. Taken off the line until the hand came back to a third of the way, it was drawn in
                        // the hand, inside the port, and looked loaded while it was not (the user, 2026-11-14: "the shell gets inserted
                        // into the shotgun but im still holding it (and it doesnt get insert)").
                        prop.BackOutFirst = false;
                        prop.OnRail = true;
                        prop.AnchorOnRail = true;
                        prop.RailDepth = 0f;
                        prop.PrevDepth = 0f;
                        prop.TurnDepth = 0f;
                        prop.MovingIn = false;
                        prop.FromSeat = false;
                        _held.Sounds.Play(MechSound.ShellIn, at);
                        off.Haptic(0.05f, 110f, 0.8f);
                        _sys.Rig.Dominant.Haptic(0.05f, 110f, 0.6f);
                        return;
                    }
                    int accepted = Ammo.LoadRounds(prop.Rounds);
                    Ammo.ReturnSpare(prop.Rounds - accepted);
                    _held.Sounds.Play(MechSound.ShellIn, at);
                    break;
                }
                case PropKind.Rocket:
                    Ammo.LoadRounds(1);
                    _held.Sounds.Play(MechSound.MagIn, at);
                    _held.Nudge(SeatPose.TransformDirection(prop.InsertDirLocal) * 0.1f);
                    break;
            }
            prop.Destroy();
            off.Haptic(0.05f, 110f, 0.8f);
            _sys.Rig.Dominant.Haptic(0.05f, 110f, 0.6f);
            if (_palmed != null)
            {
                PromotePalmed(off); // the old magazine stays in the hand
            }
        }

        /// <summary>
        /// The hand that pushed a magazine home is still on it (its grip still closed): the hand stays on the seated magazine until
        /// it opens (Tasks 347, user: "dont release the hand on the mag till the user lets go"). Let go at the seat before, the
        /// glove jumped off the magazine to the tracked hand as the gun's own magazine popped in. While it stays, the magazine is
        /// not pulled out again: that takes a new squeeze, after letting go.
        /// </summary>
        internal static readonly object SeatedHold = new object();

        /// <summary>Where the glove was on the magazine as it seated, in the gun body's space (it moves with the gun).</summary>
        private Runtime.RigidPose _seatedGloveInGun;

        /// <summary>Only from a magazine the hand pushed home (the rail and a pistol well's draw), not from the self-checks' seating.</summary>
        private void BeginSeatedHold(VRHand off, MagazineProp seated)
        {
            if (seated == null || seated.Kind != PropKind.Magazine || off == null || !off.Tracked || !off.GrabHeld || off.Role != HandRole.Free
                || off.Held != null || off.Model == null || Rig.GunBody == null || !Ammo.MagInserted)
            {
                return; // (a palmed magazine promoted into the hand holds that one instead)
            }
            off.Role = HandRole.Magazine;
            off.Held = SeatedHold;
            _seatedGloveInGun = Rig.GunBodyPose.Inverse().Multiply(Runtime.RigidPose.Of(off.Model.Root));
        }

        /// <summary>While the hand stays on the seated magazine: let go (or the magazine gone, the hand lost), it is free again.</summary>
        private void UpdateSeatedHold(VRHand off)
        {
            if (!off.GrabHeld || !off.Tracked || !Ammo.MagInserted || Rig.GunBody == null)
            {
                EndSeatedHold(off);
            }
        }

        /// <summary>Last in the frame (after the gun's own hand poses): the glove on the seated magazine, closing on the gun's model there.</summary>
        private void PoseSeatedHold(VRHand off)
        {
            if (off == null || off.Model == null || !ReferenceEquals(off.Held, SeatedHold) || Rig.GunBody == null)
            {
                return;
            }
            if (!off.GrabHeld || !Ammo.MagInserted)
            {
                // Let go or ejected while the hand had no tracking (the update returns before the hand's role then): not pinned on.
                EndSeatedHold(off);
                return;
            }
            off.Model.SnapPose = Rig.GunBodyPose.Multiply(_seatedGloveInGun);
            System.Func<Vector3, bool> solid = _held.HandSolid();
            if (solid != null)
            {
                off.Model.Solid = solid;
                off.Model.SolidAnchor = Rig.GunBody;
            }
        }

        private void EndSeatedHold(VRHand off)
        {
            if (off == null || !ReferenceEquals(off.Held, SeatedHold))
            {
                return;
            }
            off.Held = null;
            off.Role = HandRole.Free;
            if (off.Model != null)
            {
                off.Model.SnapPose = null;
                if (off.Model.SolidAnchor == Rig.GunBody)
                {
                    off.Model.Solid = null; // (only what the seated hold set)
                    off.Model.SolidAnchor = null;
                }
            }
        }

        /// <summary>Let go at the pouch, gently: stowed. Flicked away from the hip it flies.</summary>
        // Gentle against the body: running, the hand's world speed is the running speed, and a magazine put back while
        // moving was thrown off the hip instead of stowed.
        private bool Stows(VRHand off) => _sys.Zones.InPouch(off.Palm.position) && off.BodyThrowVelocity().magnitude < 1.2f;

        private void ReleaseProp(VRHand off, MagazineProp prop)
        {
            _kickPrevAlong = float.NaN; // the next magazine's kick starts afresh (a stale crossing fired a kick; review, 2026-10-20)
            _kickPushSince = -1f;
            if (_pullingIn == prop)
            {
                // Let go while it was being drawn in: it finishes going in rather than falling out of the grip.
                _pullingIn = null;
                if (AcceptsNow(prop))
                {
                    SeatProp(off, prop);
                    return;
                }
            }
            if (_palmed != null)
            {
                // Letting go drops (or stows) everything the hand holds.
                MagazineProp palmed = _palmed;
                _palmed = null;
                if (palmed.Go == null)
                {
                    palmed.Destroy(); // gone with a scene change: Drop throws on the destroyed object (as the held prop is guarded above)
                }
                else if (Stows(off))
                {
                    _sys.ReturnRounds(palmed, palmed.Rounds, Ours(palmed) ? Ammo : null);
                    palmed.Rounds = 0;
                    palmed.Destroy();
                }
                else
                {
                    palmed.Drop(off.ThrowVelocity(), off.AngularVelocity);
                }
            }
            if (prop.OnRail && prop.FromSeat && prop.RailDepth >= prop.RailLength - 0.008f && AcceptsNow(prop))
            {
                // Taken by the seated magazine and let go before it came out (a hand on the well, a squeeze that meant the gun): it stays in.
                // It fell out of a full well, or waited at the latch, with nothing pulled. (Pulled a centimetre on purpose, it goes as it did.)
                // (Never tugged: it never left its seat, so it goes back without the seating's sound and push. A hand on the well
                // and off again played the magazine out and in; scout, 2026-11-10.)
                _quietSeat = !prop.TuggedFromSeat;
                SeatProp(off, prop);
                if (_quietSeat && Ammo.HasCover)
                {
                    // (A machine gun's box under an open cover: taking it un-laid the belt, and put back the gun said LAY BELT
                    // again though nothing had moved; scout, 2026-11-10.)
                    Ammo.RestoreBelt(_beltWasLaid, _beltWasLifted);
                }
                _quietSeat = false;
                return;
            }
            if (Stows(off))
            {
                // Stowed: the rounds go back to this weapon's reserve, or to the weapon the magazine belongs to.
                _sys.ReturnRounds(prop, prop.Rounds, Ours(prop) ? Ammo : null);
                prop.Rounds = 0;
                prop.Destroy();
                _sys.Sounds.Play(MechSound.Pouch, off.Palm.position, 0.5f);
                off.Haptic(0.02f, 120f, 0.3f);
                return;
            }
            if (prop.Owner == null && Ours(prop))
            {
                prop.Owner = Ammo;
            }
            if (prop.OnRail && DetentApplies(prop) && prop.RailDepth >= prop.RailLength - DetentDepth(prop) - 0.002f && AcceptsNow(prop))
            {
                // Let go at the latch: it waits there in the well to be tapped home; or, with UnderSeatedMagazineFalls, it goes in
                // unlatched and drops out at the next shot (idea 8).
                if (VRConfig.UnderSeatedMagazineFalls.Value)
                {
                    SeatProp(off, prop);
                    Ammo.MagLoose = true;
                }
                else
                {
                    DropAlmostSeated(); // (one waiting there already: the same)
                    prop.LetGoOnRail();
                    _almostSeated = prop;
                    // It looks seated, 4 to 8 mm short, and the gun has no magazine: said, or a reload ended on a gun that will not fire.
                    _sys.FlashMessage("TAP IT HOME");
                    Log.InfoOnce("taphome:" + Data.Key, $"{Data.Key}: a magazine let go at its latch ({prop.RailDepth * 100f:0.0} of {prop.RailLength * 100f:0.0} cm in).");
                }
                _atDetent = false;
                return;
            }
            if (prop.OnRail)
            {
                // Let go half way in: it slides back out of the well, with the off hand's pull out along it (a push in is not carried:
                // the gun hand's speed alone lost a flick out of the well; scout, 2026-10-27).
                Vector3 insert = SeatPose.TransformDirection(prop.InsertDirLocal);
                float pulledOut = Mathf.Min(0f, Vector3.Dot(off.ThrowVelocity() - _sys.Rig.Dominant.ThrowVelocity(), insert));
                prop.Drop(_sys.Rig.Dominant.Velocity + insert * pulledOut - insert * 0.6f, Vector3.zero);
                return;
            }
            prop.Drop(off.ThrowVelocity(), off.AngularVelocity);
        }
    }
}
