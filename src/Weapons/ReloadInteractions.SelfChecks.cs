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
    /// <summary>
    /// The in-game self-checks of the manual reloading (-rfvr-selftest, -rfvr-autotest, -rfvr-deep): scripted hands drive each
    /// reload the way a player's would and report what they measure. Kept apart from the reloading itself
    /// (ReloadInteractions.cs) so that file reads as the game code it is.
    /// </summary>
    public sealed partial class ReloadInteractions
    {
        /// <summary>
        /// Autotest: the belt against the box as it is drawn (user, 2026-11-08: "The MG bullets as a lane ... are still clipping through the mags
        /// and have no collision with the MG mags (discovered on MG32 for example)"). The strip keeps out of <c>Data.MagBounds</c> at the seat;
        /// this measures where the box's own renderers really are in that frame (an animator can carry the box part off its seat, and a box
        /// copy can be drawn at another size), and how many of the strip's rounds lie in the box as drawn and as kept out.
        /// </summary>
        private string BeltAgainstDrawnBox(string when)
        {
            if (_belt == null)
            {
                return $"belt {when}: no strip";
            }
            RigidPose seat = SeatPose;
            Bounds drawn = default(Bounds);
            bool any = false;
            int renderers = 0;
            Transform mag = Rig.Magazine;
            if (mag != null)
            {
                foreach (Renderer r in mag.GetComponentsInChildren<Renderer>())
                {
                    // (Only what is at the box: the PKM's magazine part took in 46 renderers over 25 m.)
                    if (!r.enabled || r.forceRenderingOff || !r.gameObject.activeInHierarchy || r is ParticleSystemRenderer
                        || r.bounds.size.magnitude > 1f || (r.bounds.center - seat.position).magnitude > 0.5f)
                    {
                        continue;
                    }
                    Mesh m = r is SkinnedMeshRenderer ? null : r.GetComponent<MeshFilter>()?.sharedMesh;
                    Bounds lb = m != null ? m.bounds : default(Bounds);
                    for (int c = 0; c < 8; c++)
                    {
                        Vector3 sign = new Vector3((c & 1) == 0 ? -1f : 1f, (c & 2) == 0 ? -1f : 1f, (c & 4) == 0 ? -1f : 1f);
                        // (A skinned box by its world box: its mesh's own box is in its root bone's frame.)
                        Vector3 world = m != null ? r.transform.TransformPoint(lb.center + Vector3.Scale(lb.extents, sign)) : r.bounds.center + Vector3.Scale(r.bounds.extents, sign);
                        Vector3 inSeat = seat.InverseTransformPoint(world);
                        if (!any)
                        {
                            drawn = new Bounds(inSeat, Vector3.zero);
                            any = true;
                        }
                        else
                        {
                            drawn.Encapsulate(inSeat);
                        }
                    }
                    renderers++;
                }
            }
            Bounds kept = Data.MagBounds;
            int inKept = _belt.RoundsIn(seat, kept, 0.004f, out int shown, out int reachKept);
            string s = $"belt {when}: {shown} round(s) drawn, {inKept} with their middle and {reachKept} with an end 4 mm into the keep-out box";
            if (!any)
            {
                return s + $"; the box part draws nothing ({(mag != null ? $"scale {mag.lossyScale.magnitude:0.0000}" : "no part")})";
            }
            int inDrawn = _belt.RoundsIn(seat, drawn, 0.004f, out _, out int reachDrawn);
            Vector3 off = (drawn.center - kept.center) * 100f, grow = (drawn.size - kept.size) * 100f;
            bool apart = off.magnitude > 1.5f || Mathf.Abs(grow.x) > 2f || Mathf.Abs(grow.y) > 2f || Mathf.Abs(grow.z) > 2f;
            return s + $", {inDrawn} and {reachDrawn} into the box as drawn ({renderers} renderer(s)); the drawn box {off.ToString("0.0")} cm off the keep-out box's middle, "
                     + $"{grow.ToString("0.0")} cm larger{(apart ? " (NOT KEPT OUT OF THE BOX AS DRAWN)" : "")}{(inDrawn > 0 || reachDrawn > 1 ? " (THE BELT PASSES THROUGH THE BOX)" : "")}";
        }

        /// <summary>
        /// Autotest: a machine gun's whole reload by hand, through the hand's own grabs: every step is a squeeze where a
        /// player's hand would be, nothing is called directly (user, 2026-10-16: "reloading is sooo buggy and doesnt even
        /// work manually"). The cover opened at its latch, the box pulled out along its line, a fresh one taken from the
        /// pouch and seated along its line, the belt taken at the box's mouth and laid on the tray, the cover pushed shut;
        /// then it must be ready to fire. Each step says what took the squeeze when it went wrong.
        /// </summary>
        internal IEnumerator MgHandReloadSelfCheck(System.Action<string> report, System.Action<string, Vector3, float> sideShot = null)
        {
            VRHand off = _sys.Rig.Off;
            if (!Manual || Ammo == null || !Ammo.HasCover || Data.Hinge != HingeKind.FeedCover || !Ammo.UsesDetachableMag)
            {
                yield break;
            }
            string busy = OffHandBusy(off) ?? (Ammo.CoverOpen ? "the cover is open" : !Ammo.MagInserted ? "no box in"
                : !Ammo.InfiniteSpare && Ammo.SpareAmmo <= 0 ? "no spare box" : null);
            if (busy != null)
            {
                report($"machine gun reload by hand: skipped ({busy})");
                yield break;
            }
            int side = (int)off.Side;
            RigidPose rawFromPalm = off.Palm.Inverse().Multiply(off.Raw);
            Quaternion palmDown = Quaternion.LookRotation(Rig.AimWorld, -Rig.UpWorld);
            void PalmTo(Vector3 p)
            {
                VRHand.Scripted[side] = new RigidPose(p, palmDown).Multiply(rawFromPalm);
                VRHand.ScriptedVelocity[side] = Vector3.zero;
            }
            Vector3 CoverLatchAt(float openness) => Rig.ToWorld(HingeRotate(new RigidPose(Data.HingeGrabPoint + Data.Up * 0.02f, Quaternion.identity), openness).position);
            var steps = new List<string>();
            bool ok = false;
            // The cover settled shut first (the check before can leave it swinging down).
            if (_coverHeld)
            {
                EndCover(off);
            }
            for (float until = Time.unscaledTime + 1f; _held.HingeOpenness > 0.01f && Time.unscaledTime < until;)
            {
                yield return null;
            }
            try
            {
                // 1. The cover: the hand at its latch, a squeeze, lifted.
                PalmTo(CoverLatchAt(0f));
                yield return null;
                yield return null;
                VRInput.SimulateGrabPress(off.Side);
                yield return null;
                yield return null;
                bool tookCover = off.Role == HandRole.Hinge;
                string coverTaker = $"{off.Role}, the palm {CoverZoneDistance(off.Palm.position) * 100f:0.0} cm from the latch, the cover {_held.HingeOpenness:0.00} open";
                for (int i = 1; i <= 15; i++)
                {
                    PalmTo(CoverLatchAt(0.8f * i / 15f));
                    yield return null;
                }
                VRInput.SimulatedGrab[side] = false;
                yield return new WaitForSecondsRealtime(0.4f);
                bool opened = Ammo.CoverOpen;
                steps.Add(tookCover ? opened ? "cover opened at its latch" : "cover taken but DID NOT OPEN" : $"COVER NOT TAKEN at its latch (the squeeze: {coverTaker})");
                if (!opened)
                {
                    yield break;
                }

                // 2. The box out: a squeeze on it, pulled out along its line, let go away from the gun.
                Vector3 outW = Rig.DirToWorld(Data.MagEjectAxis).normalized;
                Vector3 boxAt = SeatedMagBaseWorld;
                PalmTo(boxAt);
                yield return null;
                yield return null;
                VRInput.SimulateGrabPress(off.Side);
                yield return null;
                yield return null;
                bool tookBox = off.Held is MagazineProp;
                HandRole boxTaker = off.Role;
                for (int i = 1; i <= 15; i++)
                {
                    PalmTo(boxAt + outW * (0.35f * i / 15f));
                    yield return null;
                }
                bool boxOut = !Ammo.MagInserted;
                VRInput.SimulatedGrab[side] = false;
                yield return new WaitForSecondsRealtime(0.3f);
                steps.Add(tookBox ? boxOut ? "box pulled out" : "box taken but NOT OUT of the gun" : $"BOX NOT TAKEN (the squeeze: {boxTaker})");
                if (Ammo.MagInserted)
                {
                    yield break;
                }

                // 3. A fresh box: a squeeze in the pouch.
                PalmTo(_sys.Zones.Pouch);
                yield return null;
                yield return null;
                VRInput.SimulateGrabPress(off.Side);
                for (int i = 0; i < 6 && off.Held == null; i++)
                {
                    yield return null;
                }
                MagazineProp box = off.Held as MagazineProp;
                steps.Add(box != null && box.Rounds > 0 ? $"fresh box from the pouch ({box.Rounds} rounds)" : $"NO FRESH BOX from the pouch (the squeeze: {off.Role})");
                if (box == null || box.Rounds <= 0)
                {
                    yield break;
                }

                // 4. Seated along its line, pushed in as a hand does.
                RigidPose holdInRaw = off.Raw.Inverse().Multiply(box.HoldPose(off));
                float length = Vector3.Dot(box.TipLocal - box.BaseLocal, box.InsertDirLocal);
                for (float below = 0.06f; below > -length - 0.03f && ReferenceEquals(off.Held, box) && !Ammo.MagInserted;
                     below -= 0.3f * Mathf.Clamp(Time.unscaledDeltaTime, 0.005f, 0.02f))
                {
                    PlaceFreshMag(off, box, holdInRaw, below, 0f, 0.3f);
                    yield return null;
                }
                for (int wait = 0; wait < 30 && !Ammo.MagInserted; wait++)
                {
                    yield return null;
                }
                bool seated = Ammo.MagInserted;
                VRInput.SimulatedGrab[side] = false;
                VRHand.ScriptedVelocity[side] = Vector3.zero;
                yield return null;
                steps.Add(seated ? "fresh box seated along its line" : "FRESH BOX NOT SEATED");
                if (!seated)
                {
                    yield break;
                }
                // The belt hanging from the fresh box, the gun level and then rolled each way (a belt through the box "only if the mg is turned a
                // little", 2026-11-04): measured against the box as drawn, and pictured.
                if (VRConfig.BeltByHand.Value && !Ammo.BeltLaid)
                {
                    VRHand dom = _sys.Rig.Dominant;
                    int domSide = (int)dom.Side;
                    RigidPose? domWas = VRHand.Scripted[domSide];
                    RigidPose domRaw = dom.Raw;
                    try
                    {
                        foreach (float roll in new[] { 0f, 25f, -25f, 50f })
                        {
                            VRHand.Scripted[domSide] = roll == 0f ? domWas : new RigidPose(domRaw.position, Quaternion.AngleAxis(roll, Rig.AimWorld) * domRaw.rotation);
                            // (Settled: a machine gun follows the hand with its weight's lag, and after four frames it had not turned at all.)
                            yield return new WaitForSecondsRealtime(0.8f);
                            float rolled = Vector3.SignedAngle(Vector3.ProjectOnPlane(Vector3.up, Rig.AimWorld), Rig.UpWorld, Rig.AimWorld);
                            report(BeltAgainstDrawnBox(roll == 0f ? "hanging, the gun level" : $"hanging, the gun rolled {roll:0} deg (measured {rolled:0})"));
                            sideShot?.Invoke(roll == 0f ? "belt-1-hanging" : $"belt-1-hanging-roll{roll:0}", BeltStartWorld, 0.4f);
                        }
                    }
                    finally
                    {
                        VRHand.Scripted[domSide] = domWas;
                    }
                    yield return null;
                }

                // 5. The belt: taken at the box's mouth, brought onto the tray.
                if (VRConfig.BeltByHand.Value && !Ammo.BeltLaid)
                {
                    Vector3 beltAt = BeltStartWorld;
                    PalmTo(beltAt);
                    yield return null;
                    yield return null;
                    VRInput.SimulateGrabPress(off.Side);
                    yield return null;
                    yield return null;
                    bool tookBelt = off.Role == HandRole.Belt;
                    HandRole beltTaker = off.Role;
                    for (int i = 1; i <= 20 && !Ammo.BeltLaid; i++)
                    {
                        PalmTo(Vector3.Lerp(beltAt, FeedTrayWorld, i / 20f));
                        yield return null;
                        if (i == 10 && tookBelt)
                        {
                            report(BeltAgainstDrawnBox("held halfway to the tray"));
                            sideShot?.Invoke("belt-2-held", BeltStartWorld, 0.4f);
                        }
                    }
                    bool laid = Ammo.BeltLaid;
                    VRInput.SimulatedGrab[side] = false;
                    yield return null;
                    steps.Add(tookBelt ? laid ? "belt taken at the box and laid on the tray" : "belt taken but NOT LAID on the tray" : $"BELT NOT TAKEN at the box (the squeeze: {beltTaker})");
                    if (!laid)
                    {
                        yield break;
                    }
                }

                // 6. The cover pushed shut.
                PalmTo(CoverLatchAt(_held.HingeOpenness));
                yield return null;
                yield return null;
                VRInput.SimulateGrabPress(off.Side);
                yield return null;
                yield return null;
                bool tookShut = off.Role == HandRole.Hinge;
                HandRole shutTaker = off.Role;
                float from = _held.HingeOpenness;
                for (int i = 1; i <= 15; i++)
                {
                    PalmTo(CoverLatchAt(Mathf.Lerp(from, 0f, i / 15f)));
                    yield return null;
                }
                VRInput.SimulatedGrab[side] = false;
                yield return new WaitForSecondsRealtime(0.3f);
                bool shut = !Ammo.CoverOpen;
                steps.Add(tookShut ? shut ? "cover pushed shut" : "cover taken but NOT SHUT" : $"COVER NOT TAKEN to shut it (the squeeze: {shutTaker})");
                ok = shut;
            }
            finally
            {
                VRInput.SimulatedGrab[side] = false;
                VRHand.Scripted[side] = null;
                VRHand.ScriptedVelocity[side] = Vector3.zero;
                if (off.Held is MagazineProp leftover)
                {
                    Ammo.ReturnSpare(leftover.Rounds);
                    leftover.Rounds = 0;
                    leftover.Destroy();
                }
                report($"machine gun reload by hand: {string.Join(", ", steps)}{(ok ? Ammo.FeedReady ? "; ready to fire" : "; NOT READY TO FIRE" : "")}");
                // Left as a player would leave it: box in, belt laid, cover shut (a failed step is put right here, also after
                // a step that gave up, for the checks after this one).
                if (!Ammo.MagInserted)
                {
                    if (!Ammo.CoverOpen)
                    {
                        Ammo.SetCoverOpen(true);
                    }
                    Ammo.InsertMagazine(Ammo.RoundsForFreshMagazine());
                }
                if (Ammo.CoverOpen && !Ammo.BeltLaid)
                {
                    Ammo.LayBelt();
                }
                if (Ammo.CoverOpen)
                {
                    Ammo.SetCoverOpen(false);
                }
            }
            yield return new WaitForSecondsRealtime(0.2f);
        }

        /// <summary>
        /// Autotest: the simulated off hand takes a feed cover at its latch and lifts it round its hinge, as a hand would, to
        /// 60% of its swing; reports whether it opened and stays open when let go, then shuts it again by hand.
        /// </summary>
        internal IEnumerator CoverSelfCheck(System.Action<string> report)
        {
            VRHand off = _sys.Rig.Off;
            if (!Manual || Data.Hinge != HingeKind.FeedCover)
            {
                yield break;
            }
            string busy = OffHandBusy(off) ?? (Ammo.CoverOpen ? "the cover is already open" : null);
            if (busy != null)
            {
                report($"feed cover by hand: skipped ({busy})");
                yield break;
            }
            int side = (int)off.Side;
            RigidPose rawFromPalm = off.Palm.Inverse().Multiply(off.Raw);
            Quaternion palmDown = Quaternion.LookRotation(Rig.AimWorld, -Rig.UpWorld);
            RigidPose At(float openness) => new RigidPose(
                Rig.ToWorld(HingeRotate(new RigidPose(Data.HingeGrabPoint + Data.Up * 0.02f, Quaternion.identity), openness).position), palmDown).Multiply(rawFromPalm);
            bool opened = false, stayed = false, shut = false;
            try
            {
                // The hand onto the cover first, then the grip closes there (closed before it got there, the grab was taken
                // wherever the hand was and the cover "could not be taken").
                VRHand.Scripted[side] = At(0f);
                yield return null;
                yield return null;
                VRInput.SimulateGrabPress(off.Side); // a press: a grip only held was never taken (the cover check read NOT TAKEN)
                yield return null;
                yield return null; // the grab is taken (the press)
                bool took = off.Role == HandRole.Hinge;
                for (int i = 1; i <= 15; i++)
                {
                    VRHand.Scripted[side] = At(0.6f * i / 15f);
                    yield return null;
                }
                opened = Ammo.CoverOpen;
                VRInput.SimulatedGrab[side] = false;
                yield return new WaitForSecondsRealtime(0.4f);
                stayed = Ammo.CoverOpen && _held.HingeOpenness > 0.9f;
                VRHand.Scripted[side] = At(_held.HingeOpenness);
                yield return null;
                VRInput.SimulateGrabPress(off.Side); // a press: a grip only held was never taken (the cover check read NOT TAKEN)
                yield return null;
                yield return null;
                for (int i = 1; i <= 15; i++)
                {
                    VRHand.Scripted[side] = At(Mathf.Lerp(1f, 0f, i / 15f));
                    yield return null;
                }
                shut = !Ammo.CoverOpen;
                // Let go where it is before the hand is handed back (see the tracking note at EndCover's caller).
                VRInput.SimulatedGrab[side] = false;
                yield return null;
                yield return null;
                report($"feed cover by hand: {(took ? "taken at its latch" : "NOT TAKEN at its latch")}, lifted to 60%: {(opened ? "open" : "DID NOT OPEN")}, let go: {(stayed ? "falls open" : "DID NOT STAY OPEN")}, pushed down: {(shut ? "shut" : "DID NOT SHUT")}");
            }
            finally
            {
                VRHand.Scripted[side] = null;
                VRInput.SimulatedGrab[side] = false;
            }
            yield return new WaitForSecondsRealtime(0.3f);
            if (Ammo.CoverOpen)
            {
                Ammo.SetCoverOpen(false); // left as it was found
            }
        }

        /// <summary>Autotest (-rfvr-deep): the launcher emptied and reloaded by hand, as the rocket film does, without filming.</summary>
        internal IEnumerator RocketCheck(System.Action<string> report)
        {
            if (Ammo.MagRounds > 0 && Manual)
            {
                Ammo.ReturnSpare(Ammo.MagRounds); // as if fired: the rocket goes back to the pouch
                Ammo.MagRounds = 0;
                yield return null;
            }
            yield return RocketFilm(report, _ => { });
        }

        /// <summary>
        /// Autotest film of a launcher's reload by hand: a rocket from the pouch, carried to the launcher's mouth, lined up
        /// 25 cm in front of it and pushed in, the way a player does. Reports whether the rocket in the hand is drawn, whether
        /// it went onto the line and whether it loaded (Tasks 231: scoped RPGs could not be reloaded, the rocket invisible).
        /// </summary>
        private IEnumerator RocketFilm(System.Action<string> report, System.Action<string> frame)
        {
            VRHand off = _sys.Rig.Off;
            if (!Manual || off.Held != null || off.Role != HandRole.Free || Rig.GunBody == null)
            {
                report("rocket film: skipped (manual reloading off, or the hand is busy)");
                yield break;
            }
            if (Ammo.MagRounds > 0)
            {
                // Not fired in the film (a lock-on launcher with nothing to lock on): unloaded, as if fired, rather than
                // skipped (HYDRA, SCALPEL and AT-1K were never loaded by hand).
                Ammo.ReturnSpare(Ammo.MagRounds);
                Ammo.MagRounds = 0;
                yield return null;
            }
            int side = (int)off.Side;
            // The launcher's measured shape is what the rocket is kept out of: up to 6 s for it (measured a moment after the
            // launcher is held at rest), or the side probes below measure nothing.
            for (float waited = 0f; _held.HandsSolid == null && waited < 6f; waited += Time.unscaledDeltaTime)
            {
                yield return null;
            }
            bool solidReady = _held.HandsSolid != null;
            RigidPose rawFromPalm = off.Palm.Inverse().Multiply(off.Raw);
            VRInput.SimulatedGrab[side] = true;
            TakeFromPouch(off);
            if (!(off.Held is MagazineProp rocket) || rocket.Go == null)
            {
                VRInput.SimulatedGrab[side] = false;
                report("rocket film: NO ROCKET FROM THE POUCH");
                yield break;
            }
            float drawnSize = 0f;
            foreach (Renderer r in rocket.Go.GetComponentsInChildren<Renderer>(false))
            {
                if (r.enabled && !r.forceRenderingOff)
                {
                    drawnSize = Mathf.Max(drawnSize, r.bounds.size.magnitude);
                }
            }
            // The rocket in the hand is the launcher's own: its meshes are those the launcher shows loaded (an RPG-7V
            // variant's held all five warheads, the scoped one a made stand-in; user, 2026-10-16).
            string model = RocketModel(rocket);
            // The palm that holds the rocket lined up at the mouth (depth 0 on the line), as the hand holds it.
            float depth0 = rocket.RailDepth;
            rocket.RailDepth = 0f;
            RigidPose entry = rocket.RailPose(SeatPose);
            rocket.RailDepth = depth0;
            RigidPose palmAtEntry = entry.Multiply(rocket.HoldPose(off).Inverse().Multiply(off.Palm));
            Vector3 insert = SeatPose.TransformDirection(rocket.InsertDirLocal);
            // As a real hand holds it: 45 degrees off the tube's line, turned about the rocket's tail at the mouth (the
            // strict line-up wanted 40 degrees at most; the loose one takes it).
            Vector3 tailAtMouth = entry.TransformPoint(rocket.TipLocal);
            Quaternion tilt = Quaternion.AngleAxis(45f, Vector3.Cross(insert, Vector3.up).sqrMagnitude > 1e-4f ? Vector3.Cross(insert, Vector3.up).normalized : Vector3.right);
            palmAtEntry = new RigidPose(tailAtMouth + tilt * (palmAtEntry.position - tailAtMouth), tilt * palmAtEntry.rotation);
            float rail = rocket.RailLength;
            System.Func<float, RigidPose> along = d => new RigidPose(palmAtEntry.position + insert * d, palmAtEntry.rotation);
            // The palm that holds the rocket at a pose of its own (the hand holds it as HoldPose puts it).
            RigidPose holdToPalm = rocket.HoldPose(off).Inverse().Multiply(off.Palm);
            RigidPose seatedPose = SeatPose;
            Vector3 aimW = Rig.AimWorld, upW = Rig.UpWorld, rightW = Vector3.Cross(upW, aimW).normalized;
            Vector3 midLocal = (rocket.TipLocal + rocket.BaseLocal) * 0.5f;
            Quaternion toLocalFrame = Runtime.RigidPose.Conjugate(Quaternion.LookRotation(rocket.InsertDirLocal, rocket.FrontLocal));
            System.Func<Vector3, Vector3, Vector3, RigidPose> palmHolding = (mid, axisW, frontW) =>
            {
                Quaternion r = Quaternion.LookRotation(axisW, frontW) * toLocalFrame;
                return new RigidPose(mid - r * midLocal, r).Multiply(holdToPalm);
            };
            Vector3 seatedTail = seatedPose.TransformPoint(rocket.TipLocal); // inside the tube, behind its mouth: where the probe crosses it
            RigidPose start = off.Palm;
            bool watchBack = false, linedSeen = false;
            // Into the launcher's side (beside the tube, then across it from above): how much of the drawn rocket is inside the
            // launcher, and whether it was taken in from there (user, 2026-10-16: it went through the launcher).
            bool watchSide = false, fromSide = false, pushing = false, loadedWhilePushing = false;
            int sideInside = 0, sideSamples = 0;
            float lead = 0f, handAtLoad = -1f;
            float linedAhead = float.NaN;
            Vector3 aimG = Data.AimAxis.normalized;
            float tubeFrontG = MagazineProp.TubeFront(Rig, aimG, out float tfG) ? tfG : Vector3.Dot(Data.MuzzlePoint, aimG);
            bool fromBehind = false;
            void Watch()
            {
                if (!(off.Held is MagazineProp held))
                {
                    return;
                }
                if (watchBack && (held.OnRail || Ammo.MagRounds > 0))
                {
                    fromBehind = true; // every frame of the probe at the back, not only its end
                }
                if (watchSide)
                {
                    fromSide |= held.OnRail || Ammo.MagRounds > 0;
                    int inside = 0;
                    const int k = 24;
                    for (int i = 0; i <= k; i++)
                    {
                        inside += _held.InsideDrawnGun(held.Go.transform.TransformPoint(Vector3.Lerp(held.BaseLocal, held.TipLocal, i / (float)k))) ? 1 : 0;
                    }
                    sideInside = Mathf.Max(sideInside, inside);
                    sideSamples = k + 1;
                }
                if (pushing && held.OnRail)
                {
                    // The rocket goes in with the hand: never ahead of where the hand has pushed it.
                    float hand = Mathf.Clamp(HandAlongRail(off, held, out _) - held.RailHandOffset, 0f, held.RailLength);
                    lead = Mathf.Max(lead, held.RailDepth - hand);
                }
                if (!watchBack && !linedSeen && held.OnRail)
                {
                    // Where the hand's rocket tail was when it went onto the line, against the tube's front end.
                    linedSeen = true;
                    linedAhead = Vector3.Dot(Rig.ToGun(held.HoldPose(off).TransformPoint(held.TipLocal)), aimG) - tubeFrontG;
                }
            }
            IEnumerator Move(System.Func<float, RigidPose> pose, float seconds, string note)
            {
                float t0 = Time.unscaledTime;
                for (float k = 0f; k < 1f; k = (Time.unscaledTime - t0) / seconds)
                {
                    float s = k * k * (3f - 2f * k);
                    VRHand.Scripted[side] = pose(s).Multiply(rawFromPalm);
                    yield return new WaitForEndOfFrame();
                    Watch();
                    frame(note);
                    yield return null;
                }
                VRHand.Scripted[side] = pose(1f).Multiply(rawFromPalm);
            }
            bool onLine = false, loaded = false;
            // Where the rocket is lined up, against the tube's front end: an RPG is loaded from the front (user, 2026-10-16).
            Vector3 aimGun = Data.AimAxis.normalized;
            float tubeFront = MagazineProp.TubeFront(Rig, aimGun, out float tf) ? tf : Vector3.Dot(Data.MuzzlePoint, aimGun);
            float mouthAhead = Vector3.Dot(Rig.ToGun(tailAtMouth), aimGun) - tubeFront;
            try
            {
                // At the back first: the rocket's tail held 40 cm behind where it sits seated must not be taken.
                RigidPose behind = along(rail + 0.4f);
                watchBack = true;
                yield return Move(s => new RigidPose(Vector3.Lerp(start.position, behind.position, s), Quaternion.Slerp(start.rotation, behind.rotation, s)), 0.6f, "at the back");
                yield return Move(s => along(rail + 0.4f - 0.1f * s), 0.3f, "at the back");
                watchBack = false;
                if (off.Held is MagazineProp offRail)
                {
                    offRail.OnRail = false;
                }
                // Beside the tube, lying along it as it sits seated, pushed sideways into it; then held across the tube and
                // brought down onto it from above.
                start = off.Palm;
                RigidPose besideOut = seatedPose.Multiply(new RigidPose(Runtime.RigidPose.Conjugate(seatedPose.rotation) * (rightW * 0.3f), Quaternion.identity)).Multiply(holdToPalm);
                yield return Move(s => new RigidPose(Vector3.Lerp(start.position, besideOut.position, s), Quaternion.Slerp(start.rotation, besideOut.rotation, s)), 0.6f, "beside the tube");
                watchSide = true;
                yield return Move(s =>
                {
                    RigidPose p = seatedPose.Multiply(new RigidPose(Runtime.RigidPose.Conjugate(seatedPose.rotation) * (rightW * (0.3f * (1f - s))), Quaternion.identity));
                    return p.Multiply(holdToPalm);
                }, 0.7f, "into the tube's side");
                watchSide = false;
                if (off.Held is MagazineProp offRail2)
                {
                    offRail2.OnRail = false;
                }
                start = off.Palm;
                RigidPose above = palmHolding(seatedTail + upW * 0.35f, rightW, aimW);
                yield return Move(s => new RigidPose(Vector3.Lerp(start.position, above.position, s), Quaternion.Slerp(start.rotation, above.rotation, s)), 0.5f, "across the tube");
                watchSide = true;
                yield return Move(s => palmHolding(seatedTail + upW * (0.35f * (1f - s)), rightW, aimW), 0.6f, "down across the tube");
                watchSide = false;
                if (off.Held is MagazineProp offRail3)
                {
                    offRail3.OnRail = false;
                }
                start = off.Palm;
                RigidPose before = along(-0.25f);
                yield return Move(s => new RigidPose(Vector3.Lerp(start.position, before.position, s), Quaternion.Slerp(start.rotation, before.rotation, s)), 0.8f, "to the launcher");
                yield return Move(s => along(-0.25f + 0.25f * s), 0.45f, "lining up");
                onLine = off.Held is MagazineProp lined && lined.OnRail;
                pushing = true;
                float pushT0 = Time.unscaledTime;
                for (float k = 0f; k < 1f && !loadedWhilePushing; k = (Time.unscaledTime - pushT0) / 1.0f)
                {
                    float s = k * k * (3f - 2f * k);
                    VRHand.Scripted[side] = along(rail * s + 0.01f).Multiply(rawFromPalm);
                    yield return new WaitForEndOfFrame();
                    Watch();
                    frame("pushing in");
                    yield return null;
                    if (Ammo.MagRounds > 0)
                    {
                        loadedWhilePushing = true; // loaded with the grip still closed: where the hand was then
                        handAtLoad = s;
                    }
                }
                pushing = false;
                if (!loadedWhilePushing)
                {
                    yield return Move(s => along(rail + 0.01f), 0.2f, "pushing in");
                    loadedWhilePushing = Ammo.MagRounds > 0;
                    handAtLoad = loadedWhilePushing ? 1f : -1f;
                }
                onLine |= Ammo.MagRounds > 0;
                loaded = Ammo.MagRounds > 0;
                yield return Move(s => along(rail + 0.01f), 0.25f, "loaded, still held");
                VRInput.SimulatedGrab[side] = false;
                yield return Move(s => along(rail - 0.15f * s), 0.35f, "hand away");
            }
            finally
            {
                VRHand.Scripted[side] = null;
                VRInput.SimulatedGrab[side] = false;
            }
            if (off.Held is MagazineProp left)
            {
                Ammo.ReturnSpare(left.Rounds);
                left.Rounds = 0;
                left.Destroy();
            }
            string atHand = float.IsNaN(linedAhead) ? "" : $" (the hand's rocket {Mathf.Abs(linedAhead) * 100f:0} cm {(linedAhead >= 0f ? "in front of" : "inside")} it as it went on)";
            report($"rocket film: rocket in the hand {(drawnSize > 0.12f ? $"drawn ({drawnSize * 100f:0} cm)" : drawnSize > 0f ? $"TOO SMALL ({drawnSize * 100f:0} cm)" : "NOT DRAWN")}, {model}, "
                   + $"lined up {Mathf.Abs(mouthAhead) * 100f:0} cm {(mouthAhead >= 0f ? "in front of" : "inside")} the tube's front{(mouthAhead < -0.05f ? " (NOT AT THE MUZZLE)" : "")}{atHand}, "
                   + $"{(fromBehind ? "TAKEN FROM THE BACK" : "not taken at the back")}, "
                   + $"pushed into the launcher's side{(solidReady ? "" : " (NO SOLID: the launcher was never measured)")}: {sideInside}/{sideSamples} points of it inside the launcher{(sideInside > 2 ? " (PASSES THROUGH THE LAUNCHER)" : "")}"
                   + $"{(fromSide ? ", TAKEN IN FROM THE SIDE" : ", not taken in from the side")}, "
                   + $"{(onLine ? "went onto the launcher's line" : "NEVER LINED UP at the mouth")}, "
                   + $"slid in {(lead > 0.03f ? $"AHEAD OF THE HAND by up to {lead * 100f:0} cm" : $"with the hand (at most {lead * 100f:0.0} cm ahead)")}, "
                   + $"{(loaded ? loadedWhilePushing ? $"loaded with the hand on it ({handAtLoad * 100f:0}% of the push)" : "loaded" : "NOT LOADED")}");
            Core.AutoTest.Metric("rocket: points of a rocket pushed into the launcher's side that are inside it", sideInside);
            Core.AutoTest.Metric("rocket: how far ahead of the hand it slid in (cm)", lead * 100f);
        }

        /// <summary>
        /// A magazine on its way into the well, measured: how far the hand has turned into the push on its base plate, how
        /// much of the free glove is inside the gun hand, and how much of what has gone in lies outside the gun's own shape
        /// (a magazine going up beside the grip instead of into it: drawn over the grip's outside on the 1911, GLOCK 43X,
        /// P320s; user, 2026-10-16; the line check could not see it: it measured the magazine against its own rail).
        /// </summary>
        private string SeatingState(VRHand off, MagazineProp prop)
        {
            LogCopyPieces(prop);
            var sb = new System.Text.StringBuilder();
            float frac = prop.OnRail ? prop.RailDepth / Mathf.Max(prop.RailLength, 1e-4f) : 0f;
            sb.Append($"rail {frac * 100f:0}%, push {(off.Model != null ? off.Model.PushWeight : 0f):0.00}");
            HandModel gunHand = _sys.Rig.Dominant != null ? _sys.Rig.Dominant.Model : null;
            if (off.Model != null && gunHand != null)
            {
                off.Model.Measure(p => gunHand.NearSkin(p, 0f), p => false, out int inHand, out int samples, out _);
                if (inHand >= 0)
                {
                    sb.Append($", glove in the gun hand {inHand}/{samples}{(inHand > 4 ? " (GLOVES OVERLAP)" : "")}");
                    Core.AutoTest.Metric("magazine (deep): free glove points inside the gun hand while seating", inHand);
                }
            }
            if (prop.Go != null && prop.OnRail && prop.RailDepth > 0.01f)
            {
                // Which way it comes in: its line tipped out of the gun's side plane comes up beside the grip, not into it.
                Vector3 insertW = SeatPose.TransformDirection(prop.InsertDirLocal).normalized;
                Vector3 rightW = Vector3.Cross(Rig.UpWorld, Rig.AimWorld).normalized;
                float tilt = Mathf.Asin(Mathf.Clamp01(Mathf.Abs(Vector3.Dot(insertW, rightW)))) * Mathf.Rad2Deg;
                sb.Append($", its line {tilt:0} deg out of the gun's side plane{(tilt > 8f ? " (COMES IN FROM THE SIDE)" : "")}");
                // What has gone in: the part past the well's mouth. Where the mouth is comes from the magazine seated (how
                // far down from its top it is still inside the gun from the side); the part of it that is in now should be
                // inside the gun's drawn shape. Counted as all the rail had gone in, a rifle magazine's top still under the
                // well read as beside it (100% on rifles whose magazines went in straight).
                RigidPose seated = SeatPose;
                float length = Vector3.Dot(prop.TipLocal - prop.BaseLocal, prop.InsertDirLocal);
                float inSeated = 0f;
                for (float s = 0f; s <= length; s += 0.002f)
                {
                    if (!_held.InsideGunFromSide(seated.TransformPoint(prop.TipLocal - prop.InsertDirLocal * s)))
                    {
                        break;
                    }
                    inSeated = s;
                }
                float inDepth = inSeated - (prop.RailLength - prop.RailDepth);
                if (inSeated < 0.005f)
                {
                    sb.Append(", seated it is not inside the gun from the side (SIDE TEST OR SEAT WRONG)");
                }
                else if (inDepth > 0.005f)
                {
                    Transform t = prop.Go.transform;
                    Vector3 side = Vector3.Cross(prop.InsertDirLocal, prop.FrontLocal).normalized;
                    Bounds box = MagBoxLocal(prop);
                    int inside = 0, total = 0, insideSeated = 0;
                    for (int a = 0; a < 5; a++)
                    {
                        for (int f = -1; f <= 1; f++)
                        {
                            for (int s = -1; s <= 1; s++)
                            {
                                Vector3 local = prop.TipLocal - prop.InsertDirLocal * (inDepth * (0.1f + 0.8f * a / 4f))
                                                + prop.FrontLocal * (f * Mathf.Abs(Vector3.Dot(box.extents, Abs(prop.FrontLocal))) * 0.6f)
                                                + side * (s * Mathf.Abs(Vector3.Dot(box.extents, Abs(side))) * 0.6f);
                                total++;
                                inside += _held.InsideGunFromSide(t.TransformPoint(local)) ? 1 : 0;
                                // Compared with the seated magazine at the same place in the gun (the point slid down the line by
                                // the part still out), not the same point of the magazine seated, which sits up in the frame: the
                                // grip's bottom (a cutout, a flared well) showed more of any magazine there, and a clean line was
                                // flagged as going in beside the well on the 1911s and the Glock (task 270; offline, the line was clean).
                                insideSeated += _held.InsideGunFromSide(seated.TransformPoint(local - prop.InsertDirLocal * (prop.RailLength - prop.RailDepth))) ? 1 : 0;
                            }
                        }
                    }
                    float outside = total > 0 ? 1f - inside / (float)total : 0f;
                    float outsideSeated = total > 0 ? 1f - insideSeated / (float)total : 0f;
                    Core.AutoTest.Metric("magazine (deep): share of what has gone in that shows beside the gun's sides", outside * 100f);
                    sb.Append($", {inDepth * 100f:0.0} cm in past the well's mouth ({inSeated * 100f:0.0} cm seated): {outside * 100f:0}% of it shows beside the gun's sides "
                              + $"(the seated magazine there: {outsideSeated * 100f:0}%){(inDepth > 0.025f && outside > 0.25f && outside > outsideSeated + 0.15f ? " (GOES IN BESIDE THE WELL)" : "")}");
                }
                else
                {
                    sb.Append($", not past the well's mouth yet ({inSeated * 100f:0.0} cm of it goes in)");
                }
            }
            return sb.ToString();
        }

        /// <summary>
        /// Once per weapon: each piece the magazine in the hand is drawn with, its size and how far its middle sits to the side
        /// of the bore's plane when seated, against the gun's own magazine pieces (the 1911's copy showed over the outside of
        /// its grip panel while the gun's own magazine did not; its analysed box was 7 x 6 cm across).
        /// </summary>
        private void LogCopyPieces(MagazineProp prop)
        {
            if (prop.Go == null || !_copyLogged.Add(Data.Key))
            {
                return;
            }
            Vector3 aimW = Rig.AimWorld, rightW = Vector3.Cross(Rig.UpWorld, aimW).normalized;
            Vector3 bore = Rig.ToWorld(Data.MuzzlePoint);
            var sb = new System.Text.StringBuilder($"Magazine pieces of {Data.Key} (sideways from the bore's plane, cm):");
            foreach (Renderer r in prop.Go.GetComponentsInChildren<Renderer>(false))
            {
                Bounds b = r.bounds;
                sb.Append($" copy '{r.name}' {Vector3.Dot(b.center - bore, rightW) * 100f:0.0} ({b.size.magnitude * 100f:0} cm);");
            }
            foreach (Transform part in Rig.MagazineParts)
            {
                foreach (Renderer r in part != null ? part.GetComponentsInChildren<Renderer>(true) : new Renderer[0])
                {
                    Bounds b = r.bounds;
                    sb.Append($" gun '{r.name}' {(r.enabled && r.gameObject.activeInHierarchy ? "" : "hidden ")}{Vector3.Dot(b.center - bore, rightW) * 100f:0.0} ({b.size.magnitude * 100f:0} cm);");
                }
            }
            Log.Info(sb.ToString());
        }

        private readonly HashSet<string> _copyLogged = new HashSet<string>();

        private static Vector3 Abs(Vector3 v) => new Vector3(Mathf.Abs(v.x), Mathf.Abs(v.y), Mathf.Abs(v.z));

        private static Bounds MagBoxLocal(MagazineProp prop)
        {
            Vector3 mid = (prop.TipLocal + prop.BaseLocal) * 0.5f;
            Vector3 size = new Vector3(0.03f, 0.03f, 0.03f);
            if (prop.Go != null)
            {
                Matrix4x4 toRoot = prop.Go.transform.worldToLocalMatrix;
                bool any = false;
                var b = new Bounds(mid, Vector3.zero);
                foreach (MeshFilter mf in prop.Go.GetComponentsInChildren<MeshFilter>())
                {
                    if (mf.sharedMesh == null)
                    {
                        continue;
                    }
                    Bounds mb = mf.sharedMesh.bounds;
                    Matrix4x4 m = toRoot * mf.transform.localToWorldMatrix;
                    for (int i = 0; i < 8; i++)
                    {
                        Vector3 c = mb.center + Vector3.Scale(mb.extents, new Vector3((i & 1) == 0 ? -1f : 1f, (i & 2) == 0 ? -1f : 1f, (i & 4) == 0 ? -1f : 1f));
                        b.Encapsulate(m.MultiplyPoint3x4(c));
                        any = true;
                    }
                }
                if (any)
                {
                    return b;
                }
            }
            return new Bounds(mid, size);
        }

        /// <summary>
        /// Whether the rocket in the hand is drawn with the launcher's own meshes (those it shows loaded): "the launcher's own
        /// rocket", or NOT THE LAUNCHER'S ROCKET with what it is instead.
        /// </summary>
        private string RocketModel(MagazineProp rocket)
        {
            var own = new HashSet<string>();
            IEnumerable<Renderer> loaded = Rig.MagShownLoaded != null && Rig.MagShownLoaded.Count > 0
                ? (IEnumerable<Renderer>)Rig.MagShownLoaded
                : Rig.MagazineParts.Where(p => p != null).SelectMany(p => p.GetComponentsInChildren<Renderer>(true));
            foreach (Renderer r in loaded)
            {
                MeshFilter mf = r != null ? r.GetComponent<MeshFilter>() : null;
                string mesh = mf != null && mf.sharedMesh != null ? mf.sharedMesh.name : r is SkinnedMeshRenderer smr && smr.sharedMesh != null ? smr.sharedMesh.name : null;
                if (mesh != null)
                {
                    own.Add(mesh);
                }
            }
            string ownIs = "the launcher's own rocket";
            if (own.Count == 0)
            {
                // A missile tube shows nothing loaded: its rocket is the projectile it fires.
                GameObject projectile = Rig.Weapon != null && Rig.Weapon.configuration != null ? Rig.Weapon.configuration.projectilePrefab : null;
                if (projectile != null)
                {
                    foreach (MeshFilter mf in projectile.GetComponentsInChildren<MeshFilter>(true))
                    {
                        if (mf.sharedMesh != null)
                        {
                            own.Add(mf.sharedMesh.name);
                        }
                    }
                    ownIs = "the launcher's own projectile";
                }
            }
            var held = new List<string>();
            foreach (MeshFilter mf in rocket.Go.GetComponentsInChildren<MeshFilter>(true))
            {
                if (mf.sharedMesh != null)
                {
                    held.Add(mf.sharedMesh.name.Replace(" (baked)", ""));
                }
            }
            if (held.Count == 0)
            {
                return "NOT THE LAUNCHER'S ROCKET (nothing drawn)";
            }
            bool match = held.Any(own.Contains);
            bool extra = held.Any(h => !own.Contains(h) && !h.StartsWith("Capsule", System.StringComparison.Ordinal));
            if (match && !extra)
            {
                return $"{ownIs} ({string.Join(", ", held.Distinct().ToArray())})";
            }
            return match ? $"NOT THE LAUNCHER'S ROCKET alone (also {string.Join(", ", held.Where(h => !own.Contains(h)).Distinct().ToArray())})"
                         : $"NOT THE LAUNCHER'S ROCKET ({string.Join(", ", held.Distinct().ToArray())}; it shows {string.Join(", ", own.ToArray())})";
        }

        /// <summary>
        /// Autotest film (-rfvr-flashfilm): the off hand pulls the seated magazine out of the well by hand, holds it, and
        /// pushes it back in, the way a player does: the hand a little turned and off the well's line, as a real hand is.
        /// <paramref name="frame"/> is called on every frame (it films the close-up). <paramref name="clock"/>: the time its moves are
        /// timed by (real time unless given: the showcase's frame-locked picture pass gives game time, which steps 1/60 s a frame);
        /// <paramref name="startRaw"/>: where the hand's controller pose starts (the showcase's hand on the handguard), eased into the reach
        /// instead of jumping to it.
        /// </summary>
        internal IEnumerator ReloadFilm(System.Action<string> report, System.Action<string> frame, System.Func<float> clock = null, RigidPose? startRaw = null)
        {
            if (Data.Action == ActionType.SingleShot && !Ammo.UsesDetachableMag)
            {
                yield return RocketFilm(report, frame);
                yield break;
            }
            VRHand off = _sys.Rig.Off;
            if (!Manual || !Ammo.UsesDetachableMag || Ammo.BoxLocked || off.Held != null || off.Role != HandRole.Free || Rig.GunBody == null)
            {
                report("reload film: skipped (no detachable magazine by hand, or the hand is busy)");
                yield break;
            }
            if (!Ammo.MagInserted)
            {
                TakeFromPouch(off);
                if (off.Held is MagazineProp m0 && AcceptsNow(m0))
                {
                    SeatProp(off, m0);
                }
                yield return null;
            }
            if (!Ammo.MagInserted || off.Held != null)
            {
                report("reload film: NO MAGAZINE SEATED");
                yield break;
            }
            int side = (int)off.Side;
            RigidPose palm0 = off.Palm, raw0 = off.Raw;
            RigidPose rawFromPalm = palm0.Inverse().Multiply(raw0);
            // Where a palm holds this magazine seated (as the hand holds one), from a stand-in made like the pulled one.
            MagazineProp probe = MagazineProp.Create(Rig, PropKind.Magazine, 0);
            probe.RailDepth = probe.RailLength;
            RigidPose seated = probe.RailPose(SeatPose);
            RigidPose holding = seated.Multiply(probe.HoldPose(off).Inverse().Multiply(palm0));
            float railLength = probe.RailLength;
            Vector3 insert = SeatPose.TransformDirection(probe.InsertDirLocal);
            probe.Destroy();
            // A real hand: 18 degrees turned about the well's line and 1.5 cm aside.
            Vector3 aside = Vector3.Cross(insert, Rig.AimWorld).normalized;
            RigidPose hand = new RigidPose(holding.position + aside * 0.015f, Quaternion.AngleAxis(18f, insert) * holding.rotation);
            // Out of the gun, as a real hand is: a seated pistol magazine is held inside the grip (only its base shows), and
            // the hand pushing it home stays under the grip.
            // The drawn gun only (not the seated magazine's own solid: a rifle's hand round its magazine was pushed 20 cm
            // under it, the magazine held there never lined up again).
            for (int k = 0; k < 25 && _held.InsideDrawnGun(hand.position); k++)
            {
                hand = new RigidPose(hand.position - insert * 0.01f, hand.rotation);
            }
            float outBy = railLength + 0.12f;
            System.Func<float, RigidPose> along = d => new RigidPose(hand.position - insert * d, hand.rotation);
            float Now() => clock != null ? clock() : Time.unscaledTime;
            IEnumerator Move(System.Func<float, RigidPose> pose, float seconds, string note, RigidPose? start = null)
            {
                float t0 = Now();
                for (float k = 0f; k < 1f; k = (Now() - t0) / seconds)
                {
                    float s = k * k * (3f - 2f * k);
                    RigidPose to = pose(s).Multiply(rawFromPalm);
                    VRHand.Scripted[side] = start.HasValue ? RigidPose.Lerp(start.Value, to, s) : to;
                    yield return new WaitForEndOfFrame();
                    frame(note);
                    yield return null;
                }
                VRHand.Scripted[side] = pose(1f).Multiply(rawFromPalm);
            }
            bool pulled = false, seatedAgain = false;
            try
            {
                yield return Move(s => along(0.12f * (1f - s)), 0.45f, "reaching", startRaw);
                VRInput.SimulatedGrab[side] = true;
                if (off.Held == null && Ammo.MagInserted)
                {
                    PullSeatedMagazine(off); // what a squeeze at the magazine's base does (the simulated grab is held, not pressed)
                }
                yield return Move(s => along(0f), 0.15f, "grab");
                pulled = off.Held is MagazineProp;
                yield return Move(s => along(outBy * s), 0.8f, "pulling out");
                yield return Move(s => along(outBy), 0.4f, "held");
                yield return Move(s => along(outBy * (1f - s)), 0.8f, "pushing in");
                yield return Move(s => along(-0.005f), 0.3f, "seated");
                seatedAgain = Ammo.MagInserted;
                VRInput.SimulatedGrab[side] = false;
                yield return Move(s => along(0.12f * s), 0.35f, "let go");
            }
            finally
            {
                VRHand.Scripted[side] = null;
                VRInput.SimulatedGrab[side] = false;
            }
            if (off.Held is MagazineProp left)
            {
                // Still in the hand (the real grip held it): back into the pouch. One let go unseated has fallen, as in play.
                Ammo.ReturnSpare(left.Rounds);
                left.Rounds = 0;
                left.Destroy();
            }
            report($"reload film: {(pulled ? "pulled out by hand" : "NOT PULLED OUT (the grab did not take it)")}, {(seatedAgain ? "seated again" : "NOT SEATED AGAIN")}");
        }

        /// <summary>
        /// Showcase film (-rfvr-showcase): the reload as a VR player does it, in one flowing move (the user, on the first reload clip:
        /// "it kindoff stutters and isnt smooth ... and also doenst look so natural because vr players would have done it diffrent"):
        /// the magazine dropped free (the eject button) as the free hand leaves the handguard, the hand straight down to the pouch,
        /// a fresh magazine taken on the way, up under the well, in with a short slap, let go and away. One smooth curve through
        /// those points (Hermite, its speed carried through each, stopping only at the seat), not ReloadFilm's seven moves that each
        /// stop dead. <paramref name="frame"/> on every frame; <paramref name="clock"/> times it; 1.36 s at <paramref name="speed"/> 1.
        /// </summary>
        internal IEnumerator SwapFilm(System.Action<string> report, System.Action<string> frame, System.Func<float> clock = null, RigidPose? startRaw = null, float speed = 1f)
        {
            VRHand off = _sys.Rig.Off;
            if (!Manual || !Ammo.UsesDetachableMag || Ammo.BoxLocked || off.Held != null || off.Role != HandRole.Free || Rig.GunBody == null)
            {
                report("swap film: skipped (no detachable magazine by hand, or the hand is busy)");
                yield break;
            }
            int side = (int)off.Side;
            RigidPose palm0 = off.Palm, raw0 = off.Raw;
            RigidPose rawFromPalm = palm0.Inverse().Multiply(raw0);
            MagazineProp probe = MagazineProp.Create(Rig, PropKind.Magazine, 0);
            probe.RailDepth = probe.RailLength;
            RigidPose seated = probe.RailPose(SeatPose);
            RigidPose holding = seated.Multiply(probe.HoldPose(off).Inverse().Multiply(palm0));
            Vector3 insert = SeatPose.TransformDirection(probe.InsertDirLocal);
            probe.Destroy();
            Vector3 aside = Vector3.Cross(insert, Rig.AimWorld).normalized;
            RigidPose hand = new RigidPose(holding.position + aside * 0.012f, Quaternion.AngleAxis(12f, insert) * holding.rotation);
            for (int k = 0; k < 25 && _held.InsideDrawnGun(hand.position); k++)
            {
                hand = new RigidPose(hand.position - insert * 0.01f, hand.rotation);
            }
            RigidPose startPalm = startRaw.HasValue ? startRaw.Value.Multiply(rawFromPalm.Inverse()) : palm0;
            Vector3 pouch = _sys.Zones.Pouch;
            Quaternion atPouch = Quaternion.Slerp(startPalm.rotation, hand.rotation, 0.5f);
            // The knots (palm poses at seconds of speed 1); the seat is a stop (no speed through it), the rest carry it.
            // Lined up 17 cm under the well, entering at 6 cm, home at 0.4 m/s (pushed at 1 m/s in one go, the XM8's was not taken: montage 2).
            var at = new[] { startPalm.position, pouch + Vector3.up * 0.03f, hand.position - insert * 0.17f, hand.position - insert * 0.06f, hand.position + insert * 0.006f,
                             hand.position - insert * 0.02f + aside * 0.05f - Vector3.up * 0.08f };
            var turn = new[] { startPalm.rotation, atPouch, hand.rotation, hand.rotation, hand.rotation, hand.rotation };
            var when = new[] { 0f, 0.38f, 0.72f, 0.86f, 1.02f, 1.36f };
            var stop = new[] { true, false, false, false, true, true };
            Vector3 Tangent(int i) => stop[i] || i == 0 || i == at.Length - 1 ? Vector3.zero : (at[i + 1] - at[i - 1]) / (when[i + 1] - when[i - 1]);
            RigidPose PalmAt(float t)
            {
                int i = 0;
                while (i < when.Length - 2 && t > when[i + 1])
                {
                    i++;
                }
                float dt = when[i + 1] - when[i];
                float u = Mathf.Clamp01((t - when[i]) / dt), u2 = u * u, u3 = u2 * u;
                Vector3 p = (2f * u3 - 3f * u2 + 1f) * at[i] + (u3 - 2f * u2 + u) * dt * Tangent(i) + (-2f * u3 + 3f * u2) * at[i + 1] + (u3 - u2) * dt * Tangent(i + 1);
                return new RigidPose(p, Quaternion.Slerp(turn[i], turn[i + 1], u * u * (3f - 2f * u)));
            }
            float Now() => clock != null ? clock() : Time.unscaledTime;
            float t0 = Now(), end = when[when.Length - 1];
            bool ejected = false, took = false, seatedNow = false, let = false;
            try
            {
                for (float t = 0f; ; t = (Now() - t0) * speed)
                {
                    if (!ejected)
                    {
                        ejected = true;
                        if (Ammo.MagInserted)
                        {
                            EjectMagazine(); // (the eject button: the magazine drops out of the well)
                        }
                    }
                    if (!took && t >= when[1])
                    {
                        took = true;
                        VRInput.SimulatedGrab[side] = true;
                        TakeFromPouch(off);
                    }
                    if (!let && t >= when[4] + 0.06f)
                    {
                        let = true;
                        seatedNow = Ammo.MagInserted;
                        VRInput.SimulatedGrab[side] = false;
                    }
                    VRHand.Scripted[side] = PalmAt(Mathf.Min(t, end)).Multiply(rawFromPalm);
                    yield return new WaitForEndOfFrame();
                    frame(t < when[1] ? "dropping" : t < when[2] ? "from the pouch" : t < when[4] + 0.06f ? "inserting" : "let go");
                    if (t >= end)
                    {
                        break;
                    }
                    yield return null;
                }
            }
            finally
            {
                VRHand.Scripted[side] = null;
                VRInput.SimulatedGrab[side] = false;
            }
            if (off.Held is MagazineProp left)
            {
                Ammo.ReturnSpare(left.Rounds); // (not seated: back into the pouch, as ReloadFilm does)
                left.Rounds = 0;
                left.Destroy();
            }
            report($"swap film: {(ejected ? "the magazine dropped" : "NOTHING DROPPED")}, {(took ? "a fresh one from the pouch" : "NOTHING TAKEN from the pouch")}, "
                   + $"{(seatedNow ? "seated" : "NOT SEATED")}");
        }

        /// <summary>
        /// Showcase film (-rfvr-showcase): the free hand racks the gun as a player does: reaches the rack part (slide, bolt, charging
        /// handle; from <paramref name="startRaw"/>, eased), takes it, pulls the whole stroke back and lets go at the back, so it goes
        /// home on its spring (a hand-driven bolt is pushed home by the hand), and the hand comes away. <paramref name="frame"/> is
        /// called on every frame; <paramref name="clock"/> times the moves (real time unless given). 0.35 + 0.08 + 0.2 + 0.3 s.
        /// </summary>
        internal IEnumerator RackFilm(System.Action<string> report, System.Action<string> frame, System.Func<float> clock = null, RigidPose? startRaw = null)
        {
            VRHand off = _sys.Rig.Off;
            if (!Manual || !Ammo.UsesChamber)
            {
                report("rack film: skipped (no rack by hand)");
                yield break;
            }
            string busy = OffHandBusy(off);
            if (busy != null)
            {
                report($"rack film: skipped ({busy})");
                yield break;
            }
            int side = (int)off.Side;
            System.Func<Vector3, RigidPose> at = RackHandPose(off);
            Vector3 restGun = Rig.ToGun(RackPointWorld), strokeGun = StrokeDir * StrokeLength;
            Vector3 Point(float back) => Rig.ToWorld(restGun + strokeGun * back);
            Vector3 away = Rig.DirToWorld(Data.Up).normalized * 0.06f - Rig.AimWorld * 0.04f;
            float Now() => clock != null ? clock() : Time.unscaledTime;
            IEnumerator Move(System.Func<float, RigidPose> pose, float seconds, string note, RigidPose? start = null)
            {
                float t0 = Now();
                for (float k = 0f; k < 1f; k = (Now() - t0) / seconds)
                {
                    float s = k * k * (3f - 2f * k);
                    RigidPose to = pose(s);
                    VRHand.Scripted[side] = start.HasValue ? RigidPose.Lerp(start.Value, to, s) : to;
                    yield return new WaitForEndOfFrame();
                    frame(note);
                    yield return null;
                }
                VRHand.Scripted[side] = pose(1f);
            }
            bool took = false, chamberedAfter = false;
            try
            {
                yield return Move(s => at(Point(0f) - away * (1f - s)), 0.35f, "reaching", startRaw);
                VRInput.SimulatedGrab[side] = true;
                BeginRack(off);
                took = off.Role == HandRole.Slide;
                yield return Move(s => at(Point(0f)), 0.08f, "taking");
                // Pulled back fast (a rack is a snap, not a slide), let go at the back.
                yield return Move(s => at(Point(s)), 0.2f, "pulling back");
                if (HandDriven)
                {
                    yield return Move(s => at(Point(1f - s)), 0.18f, "pushing home");
                }
                VRInput.SimulatedGrab[side] = false;
                yield return Move(s => at(Point(HandDriven ? 0f : 1f) + away * s), 0.3f, "let go");
                chamberedAfter = Ammo.Chambered;
            }
            finally
            {
                VRHand.Scripted[side] = null;
                VRInput.SimulatedGrab[side] = false;
            }
            report($"rack film: {(took ? "the hand took the rack part" : "THE HAND DID NOT TAKE THE RACK PART")}, pulled {StrokeLength * 100f:0.0} cm and let go, "
                   + $"{(chamberedAfter ? "a round chambered" : "NO ROUND CHAMBERED")}");
        }

        /// <summary>
        /// Autotest: runs this weapon's whole manual reload through the real code paths without hand tracking —
        /// open / eject, take from the pouch, seat, pull a seated magazine by hand and seat it again, feed cover,
        /// rack — and reports what happened.
        /// </summary>
        internal IEnumerator SelfCheck(System.Action<string> report, System.Action<string, Vector3> picture = null)
        {
            if (!Manual)
            {
                report("manual reload not active for this weapon");
                yield break;
            }
            VRHand off = _sys.Rig.Off;
            if (off.Held != null)
            {
                // Said, as the other checks say a busy hand: this check left no line, and a gun's reload results were simply
                // missing from the report (autotest scout, 2026-10-22).
                report($"reload checks NOT RUN: the free hand holds {off.Held.GetType().Name}");
                yield break;
            }
            // The simulated hand keeps its grip closed while it handles magazines, as a player's would.
            VRInput.SimulatedGrab[(int)off.Side] = true;
            try
            {
                foreach (object step in SelfCheckSteps(report, picture))
                {
                    yield return step;
                }
            }
            finally
            {
                VRInput.SimulatedGrab[(int)off.Side] = false;
            }
        }

        /// <summary>The steps of <see cref="SelfCheck"/>; <paramref name="picture"/> pictures a shell going in (a name and the world point to look at), when the run takes pictures.</summary>
        private IEnumerable SelfCheckSteps(System.Action<string> report, System.Action<string, Vector3> picture)
        {
            VRHand off = _sys.Rig.Off;
            if (Breaks)
            {
                // A shot, then opened twice: the fired case falls out the first time only (every opening dropped
                // "capacity minus loaded" cases once, out of nothing). Shut first: left open (break actions stay open),
                // opening again did nothing and the check read "0 cases" as a pass (255.I6-8). A break-action shotgun's live
                // shells come out with it (2026-10-22).
                SetBreakOpen(false);
                yield return new UnityEngine.WaitForSecondsRealtime(0.1f);
                bool fired = Ammo.MagRounds > 0;
                int liveAfter = fired && Data.Hinge == HingeKind.BreakAction ? Ammo.MagRounds - 1 : 0;
                if (fired)
                {
                    Ammo.OnShot();
                }
                int cases = Casings.Spawned;
                SetBreakOpen(true);
                int first = Casings.Spawned - cases;
                SetBreakOpen(false);
                yield return new UnityEngine.WaitForSecondsRealtime(0.2f);
                cases = Casings.Spawned;
                SetBreakOpen(true);
                int again = Casings.Spawned - cases;
                int expected = (fired ? 1 : 0) + liveAfter;
                report($"break-open: opened after a shot, {first} shell(s) came out{(fired && first == 0 ? " (NO CASE FELL OUT OF THE FIRED ONE)" : first < expected ? $" (NOT ALL OUT: {expected} were in)" : fired ? "" : " (nothing loaded to fire)")}; opened again, {again}{(again > 0 ? " (NOT ONLY ONCE: cases out of nothing)" : "")}");
                yield return new UnityEngine.WaitForSecondsRealtime(0.2f);
                bool refused = false;
                for (int n = 0; n < Ammo.Capacity + 1 && Ammo.TubeRoom > 0 && !refused; n++)
                {
                    bool seated = false;
                    foreach (object step in LoadShellByHand(off, ok => seated = ok, n == 0 ? picture : null))
                    {
                        yield return step;
                    }
                    refused = !seated;
                }
                report($"break-open: loaded by hand, one shell at a time, to {Ammo.MagRounds}/{Ammo.Capacity}{(refused ? " (A SHELL PUSHED IN ALONG ITS LINE WAS NOT TAKEN)" : "")}; {ChamberShellsReport()}");
                if (picture != null)
                {
                    yield return new WaitForEndOfFrame();
                    picture("break-open-loaded", ChamberSeat(0).position);
                }
                SetBreakOpen(false);
            }
            else if (Ammo.UsesDetachableMag)
            {
                if (Data.EnBloc)
                {
                    // An en-bloc rifle takes a clip only through the open action: as after its last round, the bolt locked
                    // back over an empty chamber (with a round chambered and the bolt shut the clip cannot go in).
                    Ammo.Chambered = false;
                    Ammo.SlideLocked = true;
                    Ammo.Sync();
                }
                if (Data.Hinge == HingeKind.FeedCover)
                {
                    Ammo.SetCoverOpen(true);
                    _held.HingeOpenness = 1f;
                }
                if (Ammo.MagInserted)
                {
                    int before = Ammo.MagRounds;
                    EjectMagazine();
                    MagazineProp ejected = _ejecting;
                    yield return new UnityEngine.WaitForSecondsRealtime(0.3f);
                    // It must be findable where it fell, by the same search a reaching hand uses.
                    bool grabbable = ejected != null && ejected.Go != null && ejected.IsDropped
                                     && MagazineProp.NearestDropped(ejected.Go.transform.position, 0.13f, Data.Key) == ejected;
                    if (!grabbable)
                    {
                        report("DROPPED MAGAZINE NOT GRABBABLE");
                    }
                    else if (off.Held == null)
                    {
                        ejected.Attach(off);
                        ejected.FollowHand();
                        if (AcceptsNow(ejected))
                        {
                            SeatProp(off, ejected);
                            // (An en-bloc clip pressed home sends the bolt forward, which chambers its first round: counted.)
                            int kept = Ammo.MagRounds + (Data.EnBloc && Ammo.Chambered ? 1 : 0);
                            report(kept == before ? $"dropped magazine picked up and seated, {before} rounds kept" : $"PICKED-UP MAGAZINE LOST ROUNDS ({kept}/{before})");
                        }
                    }
                    if (Ammo.MagInserted)
                    {
                        EjectMagazine();
                        yield return new UnityEngine.WaitForSecondsRealtime(0.2f);
                    }
                }
                if (Data.EnBloc)
                {
                    // Open again for the pouch's clip (the seated one sent the bolt home on a round, and a clip cannot go in
                    // then: "NOT SEATED, 0 rounds").
                    Ammo.Chambered = false;
                    Ammo.SlideLocked = true;
                    Ammo.Sync();
                }
                TakeFromPouch(off);
                if (off.Held is MagazineProp mag && AcceptsNow(mag))
                {
                    SeatProp(off, mag);
                }
                if (Ammo.MagInserted)
                {
                    PullSeatedMagazine(off);
                    yield return null;
                    yield return null;
                    // Taken out of its seat by hand, it stays in the hand (it went straight back in on the grab frame).
                    report(!Ammo.MagInserted && off.Held is MagazineProp ? "pulled out by hand: stays in the hand" : "pulled out by hand: NOT KEPT IN THE HAND (seated again at once)");
                    if (Data.EnBloc)
                    {
                        // The clip seated sent the bolt home on a round: open again for it to go back in.
                        Ammo.Chambered = false;
                        Ammo.SlideLocked = true;
                        Ammo.Sync();
                    }
                    if (off.Held is MagazineProp pulled && AcceptsNow(pulled))
                    {
                        SeatProp(off, pulled);
                    }
                }
                if (Data.Hinge == HingeKind.FeedCover)
                {
                    if (VRConfig.BeltByHand.Value && Ammo.MagInserted && !Ammo.BeltLaid)
                    {
                        // Pulled far past its length the belt slips from the hand and hangs from the box (it used to vanish).
                        BeginBelt(off);
                        RigidPose raw = off.Raw;
                        Vector3 palmFromRaw = raw.InverseTransformPoint(off.Palm.position);
                        Vector3 far = BeltStartWorld + Vector3.down * 0.25f - Rig.AimWorld * 0.55f;
                        VRHand.Scripted[(int)off.Side] = new RigidPose(far - raw.rotation * palmFromRaw, raw.rotation);
                        yield return null;
                        yield return null;
                        bool hanging = _beltHanging && !_beltHeld;
                        VRHand.Scripted[(int)off.Side] = null;
                        yield return null;
                        report($"belt pulled too far: {(hanging ? "slipped from the hand and hangs from the box" : "NOT HANGING from the box")}");
                        BeginBelt(off);
                        yield return null;
                        CompleteBelt(off);
                        report($"belt laid: {(_held.BeltPartCount > 0 ? $"the model's own belt ({_held.BeltPartCount} part(s)) shows" : Ammo.BeltLaid ? "the strip stays on the feed tray" : "NOTHING SHOWS on the tray")}");
                    }
                    Ammo.SetCoverOpen(false);
                }
                report($"magazine: {(Ammo.MagInserted ? "seated" : "NOT SEATED")}, {Ammo.MagRounds} rounds{(Ammo.HasCover ? Ammo.BeltLaid ? ", belt laid" : ", BELT NOT LAID" : "")}");
            }
            else if (Data.Action == ActionType.ShellFed)
            {
                // Emptied first, the shells back into the reserve: a full tube takes nothing, and the check passed untried.
                Ammo.ReturnSpare(Ammo.MagRounds);
                Ammo.MagRounds = 0;
                Ammo.Sync();
                bool refused = false;
                for (int n = 0; n < Ammo.Capacity + 1 && Ammo.TubeRoom > 0 && !refused; n++)
                {
                    bool seated = false;
                    foreach (object step in LoadShellByHand(off, ok => seated = ok, n == 0 ? picture : null))
                    {
                        yield return step;
                    }
                    refused = !seated;
                }
                Vector3 port = Rig.ToGun(SeatPose.position);
                report($"loaded by hand, one shell at a time, to {Ammo.MagRounds}/{Ammo.Capacity}{(refused ? " (A SHELL PUSHED IN ALONG ITS LINE WAS NOT TAKEN)" : "")}; "
                       + $"shells go in {Vector3.Dot(port - Data.GripPoint, Data.AimAxis) * 100f:0} cm ahead of the grip, {-Vector3.Dot(port - Data.MuzzlePoint, Data.Up) * 100f:0.0} cm under the bore"
                       + $"{(Vector3.ProjectOnPlane(port - Data.MuzzlePoint, Data.AimAxis).magnitude > 0.12f ? " (SHELL SEAT NOT ON THE GUN)" : "")}");
            }
            else if (Data.Action == ActionType.SingleShot)
            {
                TakeFromPouch(off);
                if (off.Held is MagazineProp shell && AcceptsNow(shell))
                {
                    SeatProp(off, shell);
                }
                report($"loaded to {Ammo.MagRounds}/{Ammo.Capacity}");
            }
            if (off.Held is MagazineProp leftover)
            {
                Ammo.ReturnSpare(leftover.Rounds);
                leftover.Destroy();
            }
            if (Ammo.UsesChamber)
            {
                Ammo.RackBack();
                _held.Sounds.Play(MechSound.SlideBack, RackPointWorld);
                Ammo.BoltOpen = false;
                Ammo.RackForward();
                _held.Sounds.Play(MechSound.SlideForward, RackPointWorld);
                report($"racked: {(Ammo.Chambered ? "chambered" : "NOTHING CHAMBERED")}");
            }
            yield return null;
        }

        /// <summary>
        /// The world bounds of what is drawn under <paramref name="t"/> (enabled meshes not scaled away); false if nothing.
        /// Pieces more than a metre from the middle of the rest are left out: a BRN 180 parks spent rounds 25 m away, which
        /// put its magazine's middle 12 m off and the autotest's close-up camera in a wall.
        /// </summary>
        internal static bool DrawnBounds(Transform t, out Bounds bounds)
        {
            bounds = default;
            if (t == null)
            {
                return false;
            }
            var parts = new System.Collections.Generic.List<Bounds>();
            foreach (Renderer r in t.GetComponentsInChildren<Renderer>(false))
            {
                // Drawn at a size that shows: the part's own scale said nothing on a model scaled up from centimetres (a
                // magazine shrunk to 1/10000 still had a scale of 1/100 there, and the m4 pack's M4A1s read "STILL SHOWS"
                // with nothing in the well in the picture).
                if (r.enabled && (r is MeshRenderer || r is SkinnedMeshRenderer) && r.transform.lossyScale.sqrMagnitude >= 1e-6f
                    && r.bounds.size.sqrMagnitude >= 0.002f * 0.002f)
                {
                    parts.Add(r.bounds);
                }
            }
            if (parts.Count == 0)
            {
                return false;
            }
            float Median(int axis)
            {
                var v = new float[parts.Count];
                for (int i = 0; i < v.Length; i++)
                {
                    v[i] = parts[i].center[axis];
                }
                System.Array.Sort(v);
                return v[v.Length / 2];
            }
            var middle = new Vector3(Median(0), Median(1), Median(2));
            if (parts.Count <= 2)
            {
                // Two pieces have no middle: the one nearest the part's own origin is the part.
                middle = parts[0].center;
                foreach (Bounds b in parts)
                {
                    if ((b.center - t.position).sqrMagnitude < (middle - t.position).sqrMagnitude)
                    {
                        middle = b.center;
                    }
                }
            }
            bool any = false;
            foreach (Bounds b in parts)
            {
                if ((b.center - middle).sqrMagnitude > 1f)
                {
                    continue;
                }
                if (!any)
                {
                    bounds = b;
                    any = true;
                }
                else
                {
                    bounds.Encapsulate(b);
                }
            }
            return any;
        }

        /// <summary>
        /// World box of the magazine as analysed, at its seat: for a magazine drawn by the gun's skinned mesh (a bone of it,
        /// nothing of its own to measure) or one the mod makes (no magazine part).
        /// </summary>
        private Bounds AnalysedMagWorld()
        {
            RigidPose seat = SeatPose;
            Bounds mb = Data.MagBounds;
            var b = new Bounds(seat.TransformPoint(mb.center), Vector3.zero);
            for (int i = 0; i < 8; i++)
            {
                b.Encapsulate(seat.TransformPoint(mb.center + Vector3.Scale(mb.extents, new Vector3((i & 1) == 0 ? -1f : 1f, (i & 2) == 0 ? -1f : 1f, (i & 4) == 0 ? -1f : 1f))));
            }
            return b;
        }

        /// <summary>
        /// Autotest (-rfvr-deep): one magazine all the way round, with pictures from the gun's side and numbers. Dropped: does
        /// the copy that leaves the well start where the gun's own magazine was, and is the gun's own one hidden? Taken from
        /// the pouch: is it in the hand or floating beside it? Slid in: does it travel up the magwell or in front of it?
        /// Seated: does the gun's magazine show where it was?
        /// </summary>
        internal IEnumerator MagazineDeepCheck(System.Action<string> report, System.Action<string, Vector3, float> sideShot)
        {
            VRHand off = _sys.Rig.Off;
            if (Manual && Ammo.BoxLocked && Ammo.HasCover)
            {
                // A machine gun's box is locked under its shut cover: opened first, as a player does, so the box is taken out,
                // held and seated in the pictures too (none of the belt-fed guns had one; Tasks 252 n).
                Ammo.SetCoverOpen(true);
                _held.HingeOpenness = 1f;
                yield return new WaitForSecondsRealtime(0.2f);
                report("magazine (deep): feed cover opened first (the box was locked under it)");
            }
            if (!Manual || !Ammo.UsesDetachableMag || Ammo.BoxLocked || off.Held != null || off.Role != HandRole.Free || Rig.GunBody == null)
            {
                report("magazine (deep): skipped (no detachable magazine, or the hand is busy)");
                yield break;
            }
            if (Data.EnBloc && !(Ammo.SlideLocked || !Ammo.Chambered))
            {
                // An en-bloc clip goes in only through the open action: as after its last round, the bolt locked back over an
                // empty chamber (with the bolt shut on a round the fresh clip was refused: "NOT ON THE RAIL", "NOT SEATED").
                Ammo.Chambered = false;
                Ammo.SlideLocked = true;
                Ammo.Sync();
            }
            if (!Ammo.MagInserted)
            {
                TakeFromPouch(off);
                if (off.Held is MagazineProp first && AcceptsNow(first))
                {
                    SeatProp(off, first);
                }
                yield return null;
            }
            yield return new WaitForSecondsRealtime(0.5f); // the gun settles in the hand (the check before may have moved it)
            var lines = new System.Collections.Generic.List<string>();
            // A magazine drawn by the gun's skinned mesh (a bone of it) or made by the mod (no magazine part) has nothing of
            // its own to measure: its analysed box at the seat stands in, and the pictures are still taken (ARS guns had none).
            bool ownDrawn = DrawnBounds(Rig.Magazine, out Bounds own);
            if (!ownDrawn)
            {
                own = AnalysedMagWorld();
                lines.Add(Rig.Magazine != null ? "the gun's magazine is drawn by its skinned mesh: its analysed box measured" : "no magazine part (a made one): its analysed box measured");
            }
            // Measured in gun space: the gun may move in the hand during the check.
            Vector3 ownGun = Rig.ToGun(own.center);
            Vector3 outGun = Data.MagEjectAxis.normalized;
            Vector3 outWorld = Rig.DirToWorld(outGun);
            // The free hand out of the side camera's way for the first pictures (it stood right in front of it).
            RigidPose offRaw = off.Raw;
            VRHand.Scripted[(int)off.Side] = new RigidPose(offRaw.position + Vector3.down * 0.6f - Rig.AimWorld * 0.3f, offRaw.rotation);
            yield return null;
            yield return null;
            yield return new WaitForEndOfFrame();
            sideShot("mag-1-seated", Rig.ToWorld(ownGun), 0.32f);

            // 1. Dropped.
            EjectMagazine();
            MagazineProp leaving = _ejecting;
            if (Data.EnBloc)
            {
                // The fresh clip goes in through the open action (seated, the first one sent the bolt home on a round).
                Ammo.Chambered = false;
                Ammo.SlideLocked = true;
                Ammo.Sync();
            }
            yield return null;
            yield return new WaitForEndOfFrame();
            if (leaving != null && leaving.Go != null && DrawnBounds(leaving.Go.transform, out Bounds copy))
            {
                Vector3 delta = Rig.ToGun(copy.center) - ownGun;
                float across = Vector3.ProjectOnPlane(delta, outGun).magnitude;
                float size = copy.size.magnitude / Mathf.Max(own.size.magnitude, 1e-4f);
                Core.AutoTest.Metric("magazine (deep): cm the dropped copy starts across the pull from the gun's magazine", across * 100f);
                lines.Add($"dropped copy {across * 100f:0.0} cm across the pull from the gun's magazine, {Vector3.Dot(delta, outGun) * 100f:0.0} cm out, size x{size:0.00}"
                          + (ownDrawn && (across > 0.015f || size > 1.5f || size < 0.67f) ? " (THE DROPPED MAGAZINE IS NOT THE ONE IN THE GUN)" : ""));
            }
            else
            {
                lines.Add("dropped copy: DRAWS NOTHING");
            }
            bool ownShows = ownDrawn && DrawnBounds(Rig.Magazine, out _);
            if (ownShows)
            {
                lines.Add("the gun's own magazine STILL SHOWS after the drop");
            }
            sideShot("mag-2-leaving", Rig.ToWorld(ownGun), 0.32f);
            yield return new WaitForSecondsRealtime(0.6f);
            yield return new WaitForEndOfFrame();
            sideShot("mag-3-out", Rig.ToWorld(ownGun), 0.32f);
            string skinnedLeft = _held.SkinnedInMagazineSpace();
            if (skinnedLeft.Length > 0)
            {
                lines.Add($"while out, the gun's skinned meshes in the magazine's place: {skinnedLeft}");
            }
            VRHand.Scripted[(int)off.Side] = null;

            // 2. A fresh one from the pouch, in the hand.
            VRInput.SimulatedGrab[(int)off.Side] = true;
            try
            {
                TakeFromPouch(off);
                if (!(off.Held is MagazineProp fresh))
                {
                    lines.Add("NO FRESH MAGAZINE from the pouch");
                    report("magazine (deep): " + string.Join("; ", lines));
                    yield break;
                }
                // The fingers close and the palm settles over about a fifth of a second (two frames after the grab the
                // picture showed an open hand with the magazine above its knuckles on 91 guns).
                yield return new WaitForSecondsRealtime(0.35f);
                yield return new WaitForEndOfFrame();
                if (DrawnBounds(fresh.Go.transform, out Bounds inHand))
                {
                    float gap = Mathf.Sqrt(inHand.SqrDistance(off.PalmCentre));
                    Core.AutoTest.Metric("magazine (deep): cm from the palm to the magazine in the hand", gap * 100f);
                    lines.Add($"in the hand: {gap * 100f:0.0} cm from the palm{(gap > 0.04f ? " (FLOATS BESIDE THE HAND)" : "")}");
                }
                // How the fingers hold it (the review saw an open hand with the magazine on its knuckles on 91 guns).
                string held = MagazineProp.HandClipping(off, out _, out _, out int fingersOn);
                if (fingersOn >= 0)
                {
                    Core.AutoTest.Metric("magazine (deep): fingers on the magazine in the hand (of 4)", fingersOn);
                }
                lines.Add($"held: {held}; {fresh.SizeText}; {(off.Model != null ? off.Model.CurlsText : "no hand model")}{(fingersOn == 0 ? " (NO FINGER ON THE MAGAZINE)" : "")}");
                sideShot("mag-4-in-hand", off.PalmCentre, 0.32f);

                RigidPose holdInRaw = off.Raw.Inverse().Multiply(fresh.HoldPose(off));
                float length = Vector3.Dot(fresh.TipLocal - fresh.BaseLocal, fresh.InsertDirLocal);
                if (WellInGrip && _held.HandsSolid != null)
                {
                    // 2b. Into the grip from the side and turned (task 270; user, 2026-10-19: "the mag goes through the handle", the
                    // 1911s most): it must not go into the grip through its walls.
                    yield return SideEntry(off, fresh, holdInRaw, length, lines, sideShot);
                }

                // 3. Lined up under the well, half way in on the rail, home.
                PlaceFreshMag(off, fresh, holdInRaw, 0.03f, 0f, 0.1f);
                yield return null;
                yield return null;
                yield return new WaitForEndOfFrame();
                // It waits a magazine's length under the well: framed from further back, on its top (a shot at the well
                // showed only the tip of it at the frame's bottom edge).
                Vector3 freshTip = fresh.Go != null ? fresh.Go.transform.TransformPoint(fresh.TipLocal) : Rig.ToWorld(ownGun + outGun * 0.05f);
                sideShot("mag-5-lined-up", freshTip - outWorld * 0.07f, 0.5f);
                // In until it is half way along its rail (a long magazine only meets the rail near the well).
                float halfIn = 0.03f;
                bool shot30 = false, shot60 = false, shot85 = false;
                for (float below = 0.03f; below > -length - 0.03f && ReferenceEquals(off.Held, fresh) && !Ammo.MagInserted
                                          && !(fresh.OnRail && fresh.RailDepth >= fresh.RailLength * 0.5f); below -= 0.3f * Mathf.Clamp(Time.unscaledDeltaTime, 0.005f, 0.02f))
                {
                    PlaceFreshMag(off, fresh, holdInRaw, below, 0f, 0.3f);
                    halfIn = below;
                    yield return null;
                    if (!shot30 && fresh.OnRail && fresh.RailDepth >= fresh.RailLength * 0.3f)
                    {
                        shot30 = true;
                        yield return new WaitForEndOfFrame();
                        lines.Add("30% in: " + SeatingState(off, fresh));
                        sideShot("mag-6a-30-in", fresh.Go.transform.TransformPoint(fresh.TipLocal), 0.22f);
                    }
                }
                yield return new WaitForEndOfFrame();
                if (fresh.Go != null && !Ammo.MagInserted && DrawnBounds(fresh.Go.transform, out Bounds half))
                {
                    // Where the gun's own magazine would be, slid out by what is left of the rail.
                    Vector3 halfGun = Rig.ToGun(half.center);
                    float left = fresh.OnRail ? fresh.RailLength - fresh.RailDepth : Vector3.Dot(halfGun - ownGun, outGun);
                    Vector3 expected = ownGun + outGun * left;
                    float off1 = Vector3.ProjectOnPlane(halfGun - expected, outGun).magnitude;
                    Core.AutoTest.Metric("magazine (deep): cm off the magwell line half way in", off1 * 100f);
                    lines.Add($"half way in ({(fresh.OnRail ? "on the rail" : "NOT ON THE RAIL")}): {off1 * 100f:0.0} cm off the magwell line{(ownDrawn && off1 > 0.012f ? " (SLIDES IN BESIDE THE WELL)" : "")}");
                }
                lines.Add("half in: " + SeatingState(off, fresh));
                sideShot("mag-6-half-in", Rig.ToWorld(ownGun), 0.32f);
                for (float below = halfIn; below > -length - 0.03f && ReferenceEquals(off.Held, fresh) && fresh.Go != null && !Ammo.MagInserted; below -= 0.3f * Mathf.Clamp(Time.unscaledDeltaTime, 0.005f, 0.02f))
                {
                    PlaceFreshMag(off, fresh, holdInRaw, below, 0f, 0.3f);
                    yield return null;
                    if (fresh.Go == null || Ammo.MagInserted)
                    {
                        break;
                    }
                    float frac = fresh.OnRail ? fresh.RailDepth / Mathf.Max(fresh.RailLength, 1e-4f) : 0f;
                    if (!shot60 && frac >= 0.6f || !shot85 && frac >= 0.85f)
                    {
                        string which = !shot60 && frac < 0.85f ? "60" : "85";
                        shot60 = true;
                        shot85 |= which == "85";
                        yield return new WaitForEndOfFrame();
                        if (fresh.Go == null)
                        {
                            break;
                        }
                        lines.Add(which + "% in: " + SeatingState(off, fresh));
                        // Between the base plate and the gun hand's grip, wide enough for the gun: centred on the base plate the
                        // pictures showed the free hand alone and nothing of where the magazine went.
                        Vector3 basePlate = fresh.Go.transform.TransformPoint(fresh.BaseLocal);
                        sideShot($"mag-6{(which == "60" ? "b" : "c")}-{which}-in", Vector3.Lerp(basePlate, Rig.ToWorld(Data.GripPoint), 0.5f), 0.32f);
                    }
                }
                for (int wait = 0; wait < 30 && !Ammo.MagInserted; wait++)
                {
                    yield return null;
                }
                yield return new WaitForEndOfFrame();
                bool seated = Ammo.MagInserted;
                string back = !ownDrawn ? "(not measured: nothing of its own)"
                    : DrawnBounds(Rig.Magazine, out Bounds again) ? $"{(Rig.ToGun(again.center) - ownGun).magnitude * 100f:0.0} cm from where it was" : "NOT SHOWN";
                lines.Add(seated ? $"seated: the gun's magazine shows {back}" : "NOT SEATED");
                sideShot("mag-7-seated-again", Rig.ToWorld(ownGun), 0.32f);
            }
            finally
            {
                VRHand.Scripted[(int)off.Side] = null;
                VRHand.ScriptedVelocity[(int)off.Side] = Vector3.zero;
                VRInput.SimulatedGrab[(int)off.Side] = false;
                if (off.Held is MagazineProp leftover)
                {
                    Ammo.ReturnSpare(leftover.Rounds);
                    leftover.Rounds = 0;
                    leftover.Destroy();
                }
            }
            report("magazine (deep): " + string.Join("; ", lines));
            if (Ammo.HasCover && Ammo.CoverOpen)
            {
                // Finished as a player finishes a belt reload: the belt on the tray, the cover shut. Left open with the belt not
                // laid, every machine gun's fire test after this check read LAY BELT and did not fire (weapons-1017).
                if (Ammo.MagInserted && !Ammo.BeltLaid)
                {
                    Ammo.LayBelt();
                }
                Ammo.SetCoverOpen(false);
            }
            yield return new WaitForSecondsRealtime(0.2f);
        }

        /// <summary>
        /// Autotest: H3VR-style tactical reload through the real code. A fresh magazine from the pouch in the off hand,
        /// the release pressed (the old one goes into the same hand), the fresh one seated, the old one still held, then
        /// both let go. Pictures at each step; rounds must add up.
        /// </summary>
        internal IEnumerator TacticalSelfCheck(System.Action<string> report, System.Action<string> shot)
        {
            VRHand off = _sys.Rig.Off;
            if (!Manual || !Ammo.UsesDetachableMag || Ammo.BoxLocked)
            {
                yield break; // (not this gun's kind of reload)
            }
            if (off.Held != null)
            {
                report($"tactical reload NOT RUN: the free hand holds {off.Held.GetType().Name}"); // (it left no line; scout, 2026-10-22)
                yield break;
            }
            if (!Ammo.MagInserted)
            {
                TakeFromPouch(off);
                if (off.Held is MagazineProp first && AcceptsNow(first))
                {
                    SeatProp(off, first);
                }
            }
            // Counted without the reserve: the game tops it up near a supply crate at any moment (the test stands at the
            // spawn's crate; an M1's "40 -> 48" was one refill of 8). What was loaded, plus the fresh magazine.
            int before = Ammo.MagRounds + (Ammo.Chambered ? 1 : 0);
            // The simulated hand keeps its grip closed for the whole check, as a player holding the magazines would.
            VRInput.SimulatedGrab[(int)off.Side] = true;
            TakeFromPouch(off);
            if (off.Held is MagazineProp taken)
            {
                before += taken.Rounds;
            }
            if (!(off.Held is MagazineProp fresh))
            {
                VRInput.SimulatedGrab[(int)off.Side] = false;
                report("tactical reload: NO FRESH MAGAZINE from the pouch"); // (in lower case it never counted as a problem)
                yield break;
            }
            MagReadout.TestTarget = fresh;
            yield return new WaitForSecondsRealtime(0.4f); // the readout fades in
            yield return new WaitForEndOfFrame();
            shot("magazine-in-hand");
            bool palmable = CanPalm(off);
            TacticalEject(off);
            MagazineProp old = _palmed;
            yield return new WaitForEndOfFrame();
            shot("tactical-two-magazines");
            if (AcceptsNow(fresh))
            {
                SeatProp(off, fresh);
            }
            bool kept = old != null && ReferenceEquals(off.Held, old);
            yield return new WaitForEndOfFrame();
            shot("tactical-old-magazine-kept");
            int inHand = off.Held is MagazineProp held ? held.Rounds : 0;
            int after = Ammo.MagRounds + (Ammo.Chambered ? 1 : 0) + inHand;
            MagReadout.TestTarget = null;
            if (off.Held is MagazineProp leftover)
            {
                Ammo.ReturnSpare(leftover.Rounds);
                leftover.Rounds = 0;
                leftover.Destroy();
            }
            VRInput.SimulatedGrab[(int)off.Side] = false;
            report($"tactical reload: {(palmable ? "fresh magazine at the well" : "fresh magazine away from the well (simulated hand)")}, old magazine {(kept ? "kept in the hand" : "NOT KEPT")}, rounds {(Ammo.InfiniteSpare || after == before ? "add up" : $"DO NOT ADD UP ({before} -> {after}: magazine {Ammo.MagRounds}, chamber {(Ammo.Chambered ? 1 : 0)}, spare {Ammo.SpareAmmo}, in hand {inHand}; weapon ammo {Ammo.Weapon.ammo})")}");
        }

        /// <summary>
        /// Autotest (task 165): a magazine in the hand swept through the receiver above its well, sideways and from above,
        /// slowly and fast (2.5 m/s, a hard shove). Where it is drawn it must stay out of the gun's body the whole way
        /// (only the magwell is open to it), and end on the side it came from.
        /// </summary>
        internal IEnumerator MagazineKeepOutCheck(System.Action<string> report, System.Action<string> shot)
        {
            VRHand off = _sys.Rig.Off;
            if (!Manual || !Ammo.UsesDetachableMag || off.Held != null || off.Role != HandRole.Free || Rig.GunBody == null)
            {
                report("magazine keep-out: skipped (no detachable magazine, or the hand is busy)");
                yield break;
            }
            float until = Time.unscaledTime + 4f;
            while (_held.HandsSolid == null && Time.unscaledTime < until)
            {
                yield return null;
            }
            if (_held.HandsSolid == null)
            {
                report("magazine keep-out: the gun's body was not measured: not checked");
                yield break;
            }
            int side = (int)off.Side;
            VRInput.SimulatedGrab[side] = true;
            var lines = new System.Collections.Generic.List<string>();
            try
            {
                // The deep check before it takes fresh magazines from the pouch: the MG3's spare boxes ran out there, and this
                // check reported NO MAGAZINE in every run. Topped up to the weapon's own spare, as the kick check does.
                Ammo.SpareAmmo = Mathf.Max(Ammo.SpareAmmo, Ammo.Weapon.configuration.spareAmmo);
                TakeFromPouch(off);
                if (!(off.Held is MagazineProp mag))
                {
                    report($"magazine keep-out: NO MAGAZINE from the pouch (spare {Ammo.SpareAmmo}, a fresh one would hold {Ammo.RoundsForFreshMagazine()})");
                    yield break;
                }
                yield return null;
                yield return null;
                RigidPose holdInRaw = off.Raw.Inverse().Multiply(mag.HoldPose(off));
                Vector3 aim = Data.AimAxis.normalized, up = Data.Up.normalized, right = Vector3.Cross(up, aim).normalized;
                Vector3 seatGun = Rig.ToGun(SeatPose.position);
                // In the receiver over the well: the bore's line, a little ahead of the magazine's middle.
                Vector3 centre = Data.MuzzlePoint + aim * (Vector3.Dot(seatGun - Data.MuzzlePoint, aim) + 0.03f);
                var passes = new[] { ("sideways slowly", right, 0.5f), ("sideways hard", right, 2.5f), ("from above hard", -up, 2.5f) };
                int worstFrames = 0, worstPoints = 0;
                // What the keep-out works with (252 ae reviews: measured before the keep-out is changed): the magazine's box,
                // the well's opening, where the seat lies from the sweep's centre, how far the centre is from the grip
                // (the keep-out is off past 0.7 m).
                Vector3 seatOff = seatGun - centre;
                lines.Add($"probe: magazine {mag.KeepOutBox}, well opening {Data.MagBounds.size * 100f:F1} cm, insert {Rig.ToGun(SeatPose.position + SeatPose.rotation * mag.InsertDirLocal) - seatGun:F2}, "
                          + $"seat {Vector3.Dot(seatOff, right) * 100f:0.0} right {Vector3.Dot(seatOff, up) * 100f:0.0} up {Vector3.Dot(seatOff, -aim) * 100f:0.0} back of the sweep's centre, "
                          + $"centre {Vector3.Distance(Rig.ToWorld(centre), _held.GripWorld.position) * 100f:0} cm from the grip");
                // Which of the gun's parts the measured body covers (the HK416s let magazines through their receivers, the probe
                // finding only a few solid points where the receiver is: task 328 d): the parts measured, those left out as the
                // magazine's, and the parts whose middle the body leaves mostly empty.
                var bodyParts = GunCollider.VisibleRenderers(Rig);
                var emptyParts = new System.Collections.Generic.List<string>();
                foreach (Renderer part in bodyParts)
                {
                    if (part == null)
                    {
                        continue;
                    }
                    Bounds pb = part.bounds;
                    int hits = 0, tests = 0;
                    for (int i = -1; i <= 1; i++)
                    {
                        for (int j = -1; j <= 1; j++)
                        {
                            for (int k = -1; k <= 1; k++)
                            {
                                tests++;
                                if (_held.HandsSolid(pb.center + Vector3.Scale(pb.extents * 0.5f, new Vector3(i, j, k))))
                                {
                                    hits++;
                                }
                            }
                        }
                    }
                    if (hits * 2 < tests)
                    {
                        emptyParts.Add($"{part.name} {hits}/{tests}");
                    }
                }
                var magParts = new System.Collections.Generic.List<string>();
                foreach (Transform mp in Rig.MagazineParts)
                {
                    if (mp != null)
                    {
                        magParts.Add(mp.name);
                    }
                }
                lines.Add($"body: {bodyParts.Count} part(s) measured, left out as the magazine's: {(magParts.Count > 0 ? string.Join(", ", magParts) : "none")}; "
                          + $"mostly empty in the body: {(emptyParts.Count > 0 ? string.Join(", ", emptyParts) : "none")}");
                foreach (var (name, dirGun, speed) in passes)
                {
                    int frames = 0, maxPoints = 0, probeFramesOut = 0, probeMaxOut = 0, probeMaxIn = 0, probePoints = 0;
                    float travel = 0f, firstIn = float.MaxValue, lastIn = float.MinValue;
                    string whereIn = "";
                    // Start on the pass's own side, round the gun well clear of it (50 cm under it, or beside it for the pass from
                    // above): the magazine from the pouch, or left by the last pass, could be on the far side, where the keep-out held
                    // it against the gun while the hand started across, and the pass read "past the gun (went through)" with the
                    // magazine never in it (the HK416s, task 328 d).
                    Vector3 detour = Mathf.Abs(Vector3.Dot(dirGun, up)) > 0.5f ? right : -up;
                    if (mag.Go == null || !ReferenceEquals(off.Held, mag))
                    {
                        // Gone from the hand in an earlier pass (seated, or destroyed): read here, its position threw and ended the
                        // whole autotest run; with no magazine a pass would only report "on its side" (bug hunt, 2026-10-22).
                        lines.Add($"{name}: not run (the magazine left the hand in an earlier pass)");
                        break;
                    }
                    Vector3 legFrom = Rig.ToGun(mag.Go.transform.position);
                    Vector3[] legs =
                    {
                        centre + dirGun * Vector3.Dot(legFrom - centre, dirGun) + detour * 0.5f,
                        centre - dirGun * 0.25f + detour * 0.5f,
                        centre - dirGun * 0.25f,
                    };
                    foreach (Vector3 legTo in legs)
                    {
                        for (int k = 1; k <= 8 && ReferenceEquals(off.Held, mag) && mag.Go != null; k++)
                        {
                            VRHand.Scripted[side] = new RigidPose(Rig.ToWorld(Vector3.Lerp(legFrom, legTo, k / 8f)), SeatPose.rotation).Multiply(holdInRaw.Inverse());
                            VRHand.ScriptedVelocity[side] = Vector3.zero;
                            yield return null;
                        }
                        legFrom = legTo;
                    }
                    float startSide = mag.Go != null ? Vector3.Dot(Rig.ToGun(mag.Go.transform.position) - centre, dirGun) : 0f;
                    while (travel <= 0.5f && ReferenceEquals(off.Held, mag) && mag.Go != null && !mag.OnRail)
                    {
                        Vector3 at = Rig.ToWorld(centre + dirGun * (travel - 0.25f));
                        RigidPose hold = new RigidPose(at, SeatPose.rotation);
                        VRHand.Scripted[side] = hold.Multiply(holdInRaw.Inverse());
                        VRHand.ScriptedVelocity[side] = Rig.DirToWorld(dirGun) * speed;
                        yield return null;
                        int inside = mag.PointsInsideGun(_held);
                        if (inside > 0)
                        {
                            frames++;
                            maxPoints = Mathf.Max(maxPoints, inside);
                        }
                        mag.ProbeInside(_held, out probePoints, out int pIn, out int pOut);
                        if (pOut > 0)
                        {
                            probeFramesOut++;
                            // Where (task 328 d): the travel these frames span, where the probe's points in the body are and where
                            // the magazine itself is, from the sweep's centre (right, up, back; cm), at the frame with the most.
                            firstIn = Mathf.Min(firstIn, travel);
                            lastIn = Mathf.Max(lastIn, travel);
                            if (pOut > probeMaxOut)
                            {
                                Vector3 pc = Rig.ToGun(mag.ProbeOutCentre) - centre, mc = Rig.ToGun(mag.Go.transform.position) - centre;
                                whereIn = $"points at {Vector3.Dot(pc, right) * 100f:0.0}/{Vector3.Dot(pc, up) * 100f:0.0}/{Vector3.Dot(pc, -aim) * 100f:0.0}, "
                                          + $"magazine at {Vector3.Dot(mc, right) * 100f:0.0}/{Vector3.Dot(mc, up) * 100f:0.0}/{Vector3.Dot(mc, -aim) * 100f:0.0} cm right/up/back";
                            }
                        }
                        probeMaxOut = Mathf.Max(probeMaxOut, pOut);
                        probeMaxIn = Mathf.Max(probeMaxIn, pIn);
                        if (Mathf.Abs(travel - 0.25f) < speed * 0.012f)
                        {
                            yield return new WaitForEndOfFrame();
                            shot($"keepout-{name.Replace(' ', '-')}");
                        }
                        travel += speed * Mathf.Clamp(Time.unscaledDeltaTime, 0.005f, 0.03f);
                    }
                    float endSide = mag.Go != null ? Vector3.Dot(Rig.ToGun(mag.Go.transform.position) - centre, dirGun) : 0f;
                    Core.AutoTest.Metric($"magazine keep-out ({name}): frames with the magazine drawn inside the gun", frames);
                    // Through only from the start side to the far one: a magazine already past the middle at the start never crossed.
                    string ended = endSide <= 0.02f ? "on its side"
                        : startSide > -0.05f ? $"past the gun, but it started {startSide * 100f:0.0} cm in (not a crossing)"
                        : probeFramesOut + frames == 0 ? "past the gun, over or around it (never in it)" : "PAST THE GUN (WENT THROUGH)";
                    lines.Add($"{name}: {frames} frame(s) drawn inside the gun (at most {maxPoints}/{mag.KeepOutPoints} points), ended {endSide * 100f:0.0} cm {ended}"
                              + $" [probe of {probePoints} points: {probeFramesOut} frame(s) with points in the solid outside the well's opening (at most {probeMaxOut}), at most {probeMaxIn} in the solid inside the opening"
                              + (probeFramesOut > 0 ? $"; from {(firstIn - 0.25f) * 100f:0} to {(lastIn - 0.25f) * 100f:0} cm of the sweep, {whereIn}" : "") + "]");
                    worstFrames = Mathf.Max(worstFrames, frames);
                    worstPoints = Mathf.Max(worstPoints, maxPoints);
                    // Back out the way it came before the next pass.
                    VRHand.Scripted[side] = new RigidPose(Rig.ToWorld(centre - dirGun * 0.3f), SeatPose.rotation).Multiply(holdInRaw.Inverse());
                    yield return null;
                    yield return null;
                }
                report("magazine keep-out: " + string.Join("; ", lines));
                if (worstFrames > 2 || worstPoints > 3)
                {
                    report("magazine keep-out: THE MAGAZINE WENT INTO THE GUN");
                }
                if (off.Held is MagazineProp leftover)
                {
                    Ammo.ReturnSpare(leftover.Rounds);
                    leftover.Rounds = 0;
                    leftover.Destroy();
                }
            }
            finally
            {
                VRInput.SimulatedGrab[side] = false;
                VRHand.Scripted[side] = null;
                VRHand.ScriptedVelocity[side] = Vector3.zero;
            }
        }

        /// <summary>
        /// Autotest (task 270): the fresh magazine (the gun empty) (a) held level with the grip at half its rail, 5 cm to the
        /// side, and brought onto its line from there slowly: it must stay out of the grip, kept out at its wall and not taken
        /// onto the rail beside it (taken there, it slid in through the wall); (b) turned 25 degrees and pushed up into the
        /// well from below: taken at the mouth, with none of it through the grip's walls on the way. Then back out under the
        /// well for the straight reload that follows.
        /// </summary>
        private IEnumerator SideEntry(VRHand off, MagazineProp fresh, RigidPose holdInRaw, float length, System.Collections.Generic.List<string> lines,
                                      System.Action<string, Vector3, float> sideShot)
        {
            float halfway = fresh.RailLength - length - fresh.RailLength * 0.5f; // "below" for the tip at half the rail
            int framesIn = 0, maxIn = 0, maxOut = 0;
            bool takenBeside = false, shotBeside = false;
            for (float aside = 0.05f; aside >= 0f && ReferenceEquals(off.Held, fresh) && !Ammo.MagInserted; aside -= 0.2f * Mathf.Clamp(Time.unscaledDeltaTime, 0.005f, 0.02f))
            {
                PlaceFreshMag(off, fresh, holdInRaw, halfway, aside, 0f);
                VRHand.ScriptedVelocity[(int)off.Side] = Vector3.zero;
                yield return null;
                if (fresh.Go == null)
                {
                    break;
                }
                PoseAlongRail(fresh, RigidPose.Of(fresh.Go.transform), out float drawnLateral);
                takenBeside |= fresh.OnRail && drawnLateral > MagazineProp.WellPlay;
                int inside = fresh.PointsInsideGun(_held);
                if (inside > 0)
                {
                    framesIn++;
                    maxIn = Mathf.Max(maxIn, inside);
                }
                fresh.ProbeInside(_held, out _, out _, out int outWell);
                maxOut = Mathf.Max(maxOut, outWell);
                if (!shotBeside && aside < 0.004f)
                {
                    shotBeside = true;
                    yield return new WaitForEndOfFrame();
                    sideShot("mag-side-entry", fresh.Go.transform.TransformPoint(Vector3.Lerp(fresh.TipLocal, fresh.BaseLocal, 0.5f)), 0.3f);
                }
            }
            Core.AutoTest.Metric("magazine side entry: points through the grip's walls (probe, at most)", maxOut);
            lines.Add($"side entry (beside the grip at half depth, onto the line from 5 cm): {(takenBeside ? "TAKEN ONTO THE RAIL BESIDE THE GRIP" : fresh.OnRail ? "taken on the line" : "kept out at the grip's wall")}, "
                      + $"{framesIn} frame(s) drawn inside (at most {maxIn}/45 points), at most {maxOut} probe points through the walls{(maxOut > 6 ? " (THROUGH THE GRIP)" : "")}");
            // Out sideways, down under the well, back onto the line below it.
            for (int k = 0; k < 3; k++)
            {
                float[] asides = { 0.06f, 0.06f, 0f };
                float[] belows = { halfway, 0.06f, 0.06f };
                PlaceFreshMag(off, fresh, holdInRaw, belows[k], asides[k], 0f);
                yield return null;
                yield return null;
            }
            if (Ammo.MagInserted || !ReferenceEquals(off.Held, fresh))
            {
                yield break;
            }
            // (b) Turned 25 degrees, pushed up from under the well until the rail takes it (or 4 cm past the mouth).
            int tiltOut = 0;
            bool taken = false;
            float takenDepth = -1f;
            float mouthBelow = fresh.RailLength - length; // "below" with the tip at the rail's entry
            for (float below = 0.04f; below > mouthBelow - 0.04f && ReferenceEquals(off.Held, fresh) && !Ammo.MagInserted; below -= 0.2f * Mathf.Clamp(Time.unscaledDeltaTime, 0.005f, 0.02f))
            {
                PlaceFreshMag(off, fresh, holdInRaw, below, 0f, 0.2f, 25f);
                yield return null;
                if (fresh.Go == null)
                {
                    break;
                }
                if (!fresh.OnRail)
                {
                    fresh.ProbeInside(_held, out _, out _, out int outWell);
                    tiltOut = Mathf.Max(tiltOut, outWell);
                }
                if (fresh.OnRail)
                {
                    taken = true;
                    takenDepth = fresh.RailDepth;
                    break;
                }
            }
            lines.Add($"turned 25 deg, up from below: {(taken ? $"taken at the mouth ({takenDepth * 100f:0.0} cm in)" : "NOT TAKEN")}, at most {tiltOut} probe points through the walls before{(tiltOut > 6 ? " (THROUGH THE GRIP)" : "")}");
            // Back down out of the well for the straight reload.
            for (int k = 0; k < 3; k++)
            {
                PlaceFreshMag(off, fresh, holdInRaw, 0.08f, 0f, -0.3f);
                yield return null;
            }
        }

        /// <summary>
        /// Autotest: one shell from the pouch, pushed into the gun along its line by the simulated hand from 4 cm out, the way
        /// a player loads it; not seated by the test. The shells' line ran to where the reload's shell waits (46 cm behind an
        /// R870's grip), and the test, which seated them itself, said "loaded to 8/8" (user, 2026-10-22: no shotgun could be
        /// reloaded). <paramref name="done"/> gets whether it went in; <paramref name="picture"/>, when given, pictures it half way in.
        /// </summary>
        private IEnumerable LoadShellByHand(VRHand off, System.Action<bool> done, System.Action<string, Vector3> picture = null)
        {
            if (!Ammo.InfiniteSpare)
            {
                Ammo.SpareAmmo = Mathf.Max(Ammo.SpareAmmo, Ammo.Capacity); // a test of the hands, not of the reserve
            }
            TakeFromPouch(off);
            if (!(off.Held is MagazineProp shell) || shell.Kind != PropKind.Shell)
            {
                done(false);
                yield break;
            }
            yield return null;
            // Lined up on the empty chamber's line (a double barrel's), as a hand holding it there would be.
            _seatChamber = NearestEmptyChamber(SeatPose.position);
            RigidPose holdInRaw = off.Raw.Inverse().Multiply(shell.HoldPose(off));
            float length = Vector3.Dot(shell.TipLocal - shell.BaseLocal, shell.InsertDirLocal);
            int before = Ammo.MagRounds;
            for (float below = 0.04f; below > -(length + 0.02f) && Ammo.MagRounds == before && off.Held == shell;
                 below -= 0.25f * Mathf.Clamp(Time.unscaledDeltaTime, 0.005f, 0.02f))
            {
                PlaceFreshMag(off, shell, holdInRaw, below, 0f, 0.25f);
                yield return null;
                if (picture != null && below < -length * 0.5f && shell.Go != null)
                {
                    // Half way in, held still for the picture (the null driver draws a frame or two a step).
                    VRHand.ScriptedVelocity[(int)off.Side] = Vector3.zero;
                    yield return new WaitForEndOfFrame();
                    picture(Breaks ? "shell-into-chamber" : "shell-into-port", shell.Go.transform.TransformPoint((shell.BaseLocal + shell.TipLocal) * 0.5f));
                    picture = null;
                }
            }
            VRHand.ScriptedVelocity[(int)off.Side] = Vector3.zero;
            VRHand.Scripted[(int)off.Side] = null;
            bool seated = Ammo.MagRounds > before;
            if (!seated && off.Held is MagazineProp left)
            {
                Ammo.ReturnSpare(left.Rounds);
                left.Rounds = 0;
                left.Destroy();
            }
            yield return null;
            done(seated);
        }

        /// <summary>Autotest: a break action's chamber shells, drawn where their chambers are (open, carried with the barrels).</summary>
        private string ChamberShellsReport()
        {
            Transform[] shells = Rig.ChamberShells;
            if (shells.Length == 0)
            {
                return "no chamber shells of its own (a made one is drawn in the hand)";
            }
            float worst = 0f;
            int drawn = 0;
            for (int i = 0; i < shells.Length && i < Data.ChamberShellRest.Count; i++)
            {
                if (shells[i] == null || shells[i].lossyScale.sqrMagnitude < 1e-6f)
                {
                    continue;
                }
                drawn++;
                Vector3 chamber = Rig.GunBodyPose.Multiply(Data.HingeRotated(Data.ChamberShellRest[i], _held.HingeOpenness)).position;
                worst = Mathf.Max(worst, Vector3.Distance(shells[i].position, chamber));
            }
            return $"{drawn}/{shells.Length} chamber shell(s) drawn, at most {worst * 100f:0.0} cm from their chambers{(worst > 0.01f ? " (NOT IN THEIR CHAMBERS)" : "")}";
        }

        /// <summary>
        /// Autotest: moves the simulated off hand so the magazine it holds stands lined up below the well, its tip
        /// <paramref name="below"/> under the seated magazine's base (sideways by <paramref name="aside"/>), moving at
        /// <paramref name="speed"/> into the well.
        /// </summary>
        private void PlaceFreshMag(VRHand off, MagazineProp fresh, RigidPose holdInRaw, float below, float aside, float speed, float tilt = 0f)
        {
            RigidPose seat = SeatPose;
            Vector3 insert = seat.TransformDirection(fresh.InsertDirLocal);
            Vector3 side = seat.TransformDirection(Vector3.Cross(fresh.InsertDirLocal, fresh.FrontLocal).normalized);
            float length = Vector3.Dot(fresh.TipLocal - fresh.BaseLocal, fresh.InsertDirLocal);
            var hold = new RigidPose(seat.position - insert * (length + below) + side * aside, seat.rotation);
            if (tilt != 0f)
            {
                // Tipped forward or back about its own top, which stays where the straight magazine's would be.
                Vector3 tip = hold.TransformPoint(fresh.TipLocal);
                Quaternion q = Quaternion.AngleAxis(tilt, side);
                hold = new RigidPose(tip + q * (hold.position - tip), q * hold.rotation);
            }
            VRHand.Scripted[(int)off.Side] = hold.Multiply(holdInRaw.Inverse());
            VRHand.ScriptedVelocity[(int)off.Side] = insert * speed;
        }

        /// <summary>Autotest: one way of driving a fresh magazine at the seated one.</summary>
        private sealed class KickPass
        {
            public string Name;
            public float Speed;
            public float Tilt;
            public float Aside;
            public bool Knocks = true;
        }

        /// <summary>
        /// The mag kick must work struck fast, pushed slowly and tilted, and must not fire for a magazine that only passes
        /// beside the gun.
        /// </summary>
        private static readonly KickPass[] KickPasses =
        {
            new KickPass { Name = "fast strike", Speed = 0.9f },
            new KickPass { Name = "slow push", Speed = 0.08f },
            new KickPass { Name = "tilted 45 deg", Speed = 0.6f, Tilt = 45f },
            new KickPass { Name = "passing beside the gun", Speed = 0.9f, Aside = 0.2f, Knocks = false }
        };

        /// <summary>
        /// Autotest: the two H3VR reloads through the real hand path, the simulated off hand moved by script. Mag kick: a
        /// fresh magazine driven up into the seated one knocks it out and slides in after it. Tactical: the fresh
        /// magazine waits under the well while the gun hand presses the release; the old one lands in the same hand,
        /// the fresh one goes in.
        /// </summary>
        internal IEnumerator H3vrReloadSelfCheck(System.Action<string> report, System.Action<string> shot)
        {
            VRHand off = _sys.Rig.Off;
            VRHand dom = _sys.Rig.Dominant;
            if (!Manual || !Ammo.UsesDetachableMag || Ammo.BoxLocked || off.Held != null || off.Role != HandRole.Free)
            {
                report("h3vr reloads: skipped (no detachable magazine or the hand is busy)");
                yield break;
            }
            VRInput.SimulatedGrab[(int)off.Side] = true;
            try
            {
                for (int pass = 0; pass <= KickPasses.Length; pass++)
                {
                    bool kick = pass < KickPasses.Length;
                    KickPass how = kick ? KickPasses[pass] : null;
                    if (kick && !Data.MagKickable)
                    {
                        if (pass == 0)
                        {
                            report("mag kick: this magazine sits too deep in the gun to be knocked out (not kickable)");
                        }
                        continue;
                    }
                    if (!Ammo.MagInserted)
                    {
                        TakeFromPouch(off);
                        if (off.Held is MagazineProp seatFirst && AcceptsNow(seatFirst))
                        {
                            SeatProp(off, seatFirst);
                        }
                    }
                    int oldRounds = Ammo.MagRounds;
                    // Each pass starts with a full reserve: a gun with a few spare magazines (a sniper's four) ran the pouch dry
                    // by the fourth pass, the knocked-out magazines lying on the ground with their rounds.
                    if (!Ammo.InfiniteSpare && Ammo.Weapon.configuration.spareAmmo > 0)
                    {
                        Ammo.SpareAmmo = Mathf.Max(Ammo.SpareAmmo, Ammo.Weapon.configuration.spareAmmo);
                    }
                    // Counted without the pouch (a mutator may refill it at any moment): what is loaded, plus the fresh one.
                    int before = Ammo.MagRounds + (Ammo.Chambered ? 1 : 0);
                    TakeFromPouch(off);
                    if (!(off.Held is MagazineProp fresh))
                    {
                        report((kick ? "mag kick" : "tactical reload (h3vr)") + ": NO FRESH MAGAZINE from the pouch");
                        continue;
                    }
                    int freshRounds = fresh.Rounds;
                    before += freshRounds;
                    yield return null;
                    RigidPose holdInRaw = off.Raw.Inverse().Multiply(fresh.HoldPose(off));
                    MagazineProp old = null;
                    bool knocked = false, palmed = false;
                    // Magazines lying around before this pass (earlier passes' knocked-out ones) are not this pass's.
                    var lyingBefore = new System.Collections.Generic.HashSet<MagazineProp>();
                    foreach (MagazineProp m in MagazineProp.All)
                    {
                        if (m.IsDropped)
                        {
                            lyingBefore.Add(m);
                        }
                    }
                    if (kick)
                    {
                        // From 8 cm below, up into (or past) the old magazine at this pass's speed and angle.
                        _seatedAt = -10f; // the seat just above is the test's set-up, not a push the kick must wait out
                        // Stepped by at most 20 ms of travel a frame: right after a picture the first frame is long, and the
                        // hand jumped straight past the well (a player's hand moves continuously).
                        for (float below = 0.08f; below > -0.06f; below -= how.Speed * Mathf.Clamp(Time.unscaledDeltaTime, 0.005f, 0.02f))
                        {
                            PlaceFreshMag(off, fresh, holdInRaw, below, how.Aside, how.Speed, how.Tilt);
                            yield return null;
                            if (!Ammo.MagInserted)
                            {
                                knocked = true;
                                break;
                            }
                        }
                        yield return new WaitForEndOfFrame();
                        shot(pass == 0 ? "mag-kick" : "mag-kick-" + pass);
                        if (!how.Knocks)
                        {
                            // Only passing by: nothing may have come out. The fresh magazine goes back into the pouch.
                            VRHand.ScriptedVelocity[(int)off.Side] = Vector3.zero;
                            report($"mag kick, {how.Name}: {(knocked ? "old magazine NOT LEFT IN THE GUN (knocked out by a magazine that missed its base)" : "nothing knocked out")}");
                            if (off.Held is MagazineProp passed)
                            {
                                Ammo.ReturnSpare(passed.Rounds);
                                passed.Rounds = 0;
                                passed.Destroy();
                            }
                            VRHand.Scripted[(int)off.Side] = null;
                            yield return null;
                            continue;
                        }
                    }
                    else
                    {
                        // Lined up 5 cm under the well, a little aside so it does not strike the old one; the gun hand
                        // presses the release.
                        PlaceFreshMag(off, fresh, holdInRaw, 0.05f, 0.03f, 0f);
                        yield return null;
                        yield return null;
                        VRInput.SimulatePress(DigitalAction.MagRelease, dom.Side);
                        yield return null;
                        yield return null;
                        old = _palmed;
                        palmed = old != null && ReferenceEquals(old.HeldBy, off);
                        knocked = !Ammo.MagInserted;
                        yield return new WaitForEndOfFrame();
                        shot("h3vr-tactical-two-magazines");
                    }
                    // Then up the well until it seats.
                    float length = Vector3.Dot(fresh.TipLocal - fresh.BaseLocal, fresh.InsertDirLocal);
                    float start = kick ? -0.02f : 0.04f;
                    for (float below = start; below > -length - 0.03f && fresh.Go != null && ReferenceEquals(off.Held, fresh); below -= 0.6f * Mathf.Clamp(Time.unscaledDeltaTime, 0.005f, 0.02f))
                    {
                        PlaceFreshMag(off, fresh, holdInRaw, below, 0f, 0.6f);
                        yield return null;
                    }
                    VRHand.ScriptedVelocity[(int)off.Side] = Vector3.zero;
                    yield return null;
                    // The knocked-out magazine slides out of the well for a moment before it falls (and counts as dropped).
                    for (int wait = 0; wait < 30 && _ejecting != null; wait++)
                    {
                        yield return null;
                    }
                    bool seated = Ammo.MagInserted && Ammo.MagRounds == freshRounds;
                    int inHand = off.Held is MagazineProp h ? h.Rounds : 0;
                    int dropped = 0;
                    if (kick)
                    {
                        foreach (MagazineProp m in MagazineProp.All)
                        {
                            if (m.IsDropped && m.WeaponKey == Data.Key && m.Rounds == oldRounds && !lyingBefore.Contains(m))
                            {
                                dropped = m.Rounds;
                                break;
                            }
                        }
                    }
                    int after = Ammo.MagRounds + (Ammo.Chambered ? 1 : 0) + inHand + dropped;
                    knocked |= kick && dropped > 0; // the kick can land a frame after the strike (the hand pose lags a frame)
                    yield return new WaitForEndOfFrame();
                    shot(kick ? (pass == 0 ? "mag-kick-seated" : $"mag-kick-{pass}-seated") : "h3vr-tactical-seated");
                    string sum = after == before ? "rounds add up" : $"ROUNDS DO NOT ADD UP ({before} -> {after}: magazine {Ammo.MagRounds}, chamber {(Ammo.Chambered ? 1 : 0)}, in hand {inHand}, dropped {dropped})";
                    if (kick)
                    {
                        Core.AutoTest.Metric($"mag kick ({how.Name}): knocked out (0/1)", knocked ? 1 : 0);
                        // (Not seated: where the fresh one was left, for finding why: the slow push's NOT SEATED from 2026-11-08 had no word on it.)
                        string why = seated || fresh.Go == null ? "" : $" [it {(fresh.OnRail ? $"on the rail {fresh.RailDepth * 100f:0.0} of {fresh.RailLength * 100f:0.0} cm" : "off the rail")}, "
                                     + $"{(ReferenceEquals(off.Held, fresh) ? "in the hand" : "not in the hand")}, the gun {(AcceptsNow(fresh) ? "would take it" : "would not take it")}, magazine in {Ammo.MagInserted}]";
                        report($"mag kick, {how.Name}: old magazine {(knocked ? $"knocked out ({oldRounds} rounds)" : "NOT KNOCKED OUT")}, fresh one {(seated ? $"seated ({freshRounds} rounds)" : "NOT SEATED" + why)}, {sum}");
                    }
                    else
                    {
                        report($"tactical reload (h3vr): old magazine {(palmed ? $"in the same hand as the fresh one ({oldRounds} rounds)" : knocked ? "released but NOT IN THE HAND" : "NOT RELEASED")}, fresh one {(seated ? $"seated ({freshRounds} rounds)" : "NOT SEATED")}, then {(inHand > 0 ? $"the old one held ({inHand} rounds)" : "nothing in the hand")}, {sum}");
                    }
                    // Clean up: whatever is left in the hand back into the pouch.
                    if (off.Held is MagazineProp leftover)
                    {
                        Ammo.ReturnSpare(leftover.Rounds);
                        leftover.Rounds = 0;
                        leftover.Destroy();
                    }
                    VRHand.Scripted[(int)off.Side] = null;
                    yield return null;
                }
            }
            finally
            {
                VRHand.Scripted[(int)off.Side] = null;
                VRHand.ScriptedVelocity[(int)off.Side] = Vector3.zero;
                VRInput.SimulatedGrab[(int)off.Side] = false;
            }
        }

        /// <summary>
        /// Autotest: the scripted off hand's pose for a palm at a point on the rack part (slide, bolt, charging handle or
        /// pump), held as a player holds it. Shared by the rack checks.
        /// </summary>
        private System.Func<Vector3, RigidPose> RackHandPose(VRHand off)
        {
            RigidPose rawFromPalm = off.Palm.Inverse().Multiply(off.Raw);
            // The hand as a player holds the part (the controller's fixed test pose put the fist sideways beside it, and the
            // picture review saw the hand off the part on 207 guns): the palm against the part's outer side; on a part on top,
            // the fingers across it toward the far side, on a part on the side, up and over it.
            Vector3 aimW = Rig.AimWorld, upW = Rig.DirToWorld(Data.Up).normalized, rightW = Vector3.Cross(upW, aimW).normalized;
            Vector3 boreAt = Rig.ToWorld(Data.MuzzlePoint);
            // A handle clearly off to the side (an AK's bolt handle, a side charging handle) is taken from that side; any
            // other part over the top (a pistol slide, a bolt whose rack point lay a few millimetres under the bore put the
            // hand under the gun, at the trigger guard: P320, the PKM family).
            // From the side the part faces, once it is clearly off the bore (1.5 cm): the PKM's lever 2.3 cm to the right at
            // the bore's height was taken over the top, the glove 2.5 cm above it (check-1017f); a knob up and to the side
            // (M1014) is taken from that diagonal.
            Vector3 offBore = Vector3.ProjectOnPlane(RackPointWorld - boreAt, aimW);
            Vector3 outward = offBore.magnitude > 0.015f ? offBore.normalized : upW;
            Vector3 intoPart = -outward;
            Vector3 fingers = Vector3.Dot(outward, upW) > 0.7f ? (off.IsLeft ? rightW : -rightW) : upW;
            Vector3 palmX = -intoPart * off.Mirror;
            fingers = Vector3.ProjectOnPlane(fingers, palmX);
            fingers = fingers.sqrMagnitude > 1e-6f ? fingers.normalized : aimW;
            Quaternion palmRot = Quaternion.LookRotation(fingers, Vector3.Cross(fingers, palmX));
            return palm => new RigidPose(palm - intoPart * 0.02f, palmRot).Multiply(rawFromPalm);
        }

        /// <summary>
        /// Autotest: the simulated off hand is put on the rack point (slide, bolt, charging handle or pump) and takes it the
        /// way a grab there does; pictured and measured on the part at rest and pulled half way, then let go.
        /// </summary>
        internal IEnumerator RackGripSelfCheck(System.Action<string> report, System.Action<string> shot)
        {
            VRHand off = _sys.Rig.Off;
            if (!Manual || !Ammo.UsesChamber)
            {
                yield break;
            }
            string busy = OffHandBusy(off);
            if (busy != null)
            {
                report($"rack grip: skipped ({busy})");
                yield break;
            }
            // The rack point is put on the model's surface once the gun is measured (HeldWeapon.SnapRackPoint): waited
            // for, or the hand was put at the analysis's point and the pictures showed it where the point had been.
            for (float until = Time.unscaledTime + 2f; _held.HandsSolid == null && Time.unscaledTime < until;)
            {
                yield return null;
            }
            int side = (int)off.Side;
            System.Func<Vector3, RigidPose> At = RackHandPose(off);
            VRInput.SimulatedGrab[side] = true;
            try
            {
                VRHand.Scripted[side] = At(RackPointWorld);
                yield return null;
                yield return null;
                BeginRack(off);
                yield return new WaitForSecondsRealtime(0.5f); // the part is measured, the hand closes on it
                // The part's shape is measured from pictures over a few frames: waited for (up to 2 s more), or the hand
                // was measured against nothing ("the part's shape is not ready").
                for (float until = Time.realtimeSinceStartup + 2f; !_held.PartShapeReady && Time.realtimeSinceStartup < until;)
                {
                    yield return null;
                }
                yield return new WaitForEndOfFrame();
                shot("rack-grip");
                string atRest = _held.PartClipping(off, out int restBuried, out int restTouching);
                var layoutRest = _held.DrawnLayout();
                // Half way back along the stroke.
                Vector3 start = RackPointWorld;
                Vector3 pull = Rig.DirToWorld(StrokeDir) * (StrokeLength * 0.5f);
                _held.ResetKick();
                for (int i = 1; i <= 10; i++)
                {
                    VRHand.Scripted[side] = At(start + pull * (i / 10f));
                    yield return null;
                }
                float tug = _held.PeakShift;
                yield return new WaitForSecondsRealtime(0.2f);
                yield return new WaitForEndOfFrame();
                shot("rack-grip-pulled");
                string pulled = _held.PartClipping(off, out int pulledBuried, out _);
                // What visibly moved with the half pull (in gun space), and whether the part under the hand is among it.
                var layoutPulled = _held.DrawnLayout();
                float most = 0f, nearestMoving = float.MaxValue, halfStroke = StrokeLength * 0.5f;
                int movedCount = 0;
                // Which parts moved, by name, how far, and how far from the rack point (252 ae reviews: the next run must
                // say which part moved, not only that something did).
                var movers = new List<(string name, float moved, float fromPoint)>();
                var movingRenderers = new List<Renderer>();
                foreach (var kv in layoutRest)
                {
                    if (!layoutPulled.TryGetValue(kv.Key, out Vector3 now))
                    {
                        continue;
                    }
                    float moved = Vector3.Distance(kv.Value, now);
                    most = Mathf.Max(most, moved);
                    if (moved >= Mathf.Max(0.004f, halfStroke * 0.4f))
                    {
                        // How far it is from the hand now (to a mesh's surface, a bone's point): a part that came back
                        // with the hand is still right there.
                        movedCount++;
                        // From the hand itself (the rack point lies on the part by definition: every gun read 0.0 cm).
                        Vector3 handAt = off.Model != null ? off.Model.GraspPointWorld : RackPointWorld;
                        float gap = kv.Key is Renderer r ? Mathf.Sqrt(r.bounds.SqrDistance(handAt))
                            : kv.Key is Transform b ? Vector3.Distance(b.position, handAt) : float.MaxValue;
                        nearestMoving = Mathf.Min(nearestMoving, gap);
                        float fromPoint = kv.Key is Renderer rr ? Mathf.Sqrt(rr.bounds.SqrDistance(RackPointWorld))
                            : kv.Key is Transform bb ? Vector3.Distance(bb.position, RackPointWorld) : float.MaxValue;
                        movers.Add(((kv.Key is Renderer ? "" : "bone ") + kv.Key.name, moved, fromPoint));
                        if (kv.Key is Renderer mr)
                        {
                            movingRenderers.Add(mr);
                        }
                    }
                }
                movers.Sort((a, b) => b.moved.CompareTo(a.moved));
                var moverText = new List<string>();
                for (int i = 0; i < movers.Count && i < 5; i++)
                {
                    moverText.Add($"'{movers[i].name}' {movers[i].moved * 1000f:0} mm ({movers[i].fromPoint * 100f:0.0} cm from the rack point)");
                }
                if (movers.Count > 5)
                {
                    moverText.Add($"{movers.Count - 5} more");
                }
                // Whether the moving part is seen at all from outside the gun (a carrier in the receiver moves, unseen).
                string seenText = "";
                if (movingRenderers.Count > 0)
                {
                    var gunRenderers = new List<Renderer>();
                    var arms = new HashSet<Renderer>(Rig.ArmRenderers);
                    foreach (Renderer r in _held.Weapon.GetComponentsInChildren<Renderer>(false))
                    {
                        if (r.enabled && !arms.Contains(r) && (r is MeshRenderer || r is SkinnedMeshRenderer))
                        {
                            gunRenderers.Add(r);
                        }
                    }
                    float share = GunVolume.VisibleShare(gunRenderers, movingRenderers, Rig.AimWorld, Rig.DirToWorld(Data.Up), out string views);
                    if (share >= 0f)
                    {
                        Core.AutoTest.Metric("rack: % of the moving parts seen from outside the gun at half pull", share * 100f);
                        seenText = $"; seen from outside: {share * 100f:0}% of the moving parts ({views}){(share < 0.05f ? " (HIDDEN IN THE GUN)" : "")}";
                    }
                }
                Core.AutoTest.Metric("rack: mm the drawn gun moves at half pull (most)", most * 1000f);
                string drawnMoves = movedCount == 0
                    ? $"NOTHING DRAWN MOVES (most {most * 1000f:0} mm for a {halfStroke * 1000f:0} mm half pull)"
                    : $"{movedCount} drawn part(s) move, most {most * 1000f:0} mm; the nearest is {nearestMoving * 100f:0.0} cm from the hand"
                      + (nearestMoving > 0.03f ? " (THE PART UNDER THE HAND DOES NOT MOVE)" : "")
                      + $" [{string.Join(", ", moverText.ToArray())}]{seenText}";
                Vector3 rackGun = Rig.ToGun(RackPointWorld) - Data.MuzzlePoint;
                Vector3 rightGun = Vector3.Cross(Data.Up, Data.AimAxis).normalized;
                drawnMoves += $"; rack point back {Vector3.Dot(rackGun, -Data.AimAxis) * 100f:0.0} up {Vector3.Dot(rackGun, Data.Up) * 100f:0.0} right {Vector3.Dot(rackGun, rightGun) * 100f:0.0} cm from the muzzle";
                // All the way back, then let go: a spring-driven slide or bolt slams home and jolts the gun.
                for (int i = 11; i <= 20; i++)
                {
                    VRHand.Scripted[side] = At(start + pull * (i / 10f));
                    yield return null;
                }
                _held.ResetKick();
                VRInput.SimulatedGrab[side] = false;
                yield return new WaitForSecondsRealtime(0.35f);
                float jolt = _held.PeakShift;
                Core.AutoTest.Metric("racking: mm the gun moves with the pull", tug * 1000f);
                if (!HandDriven)
                {
                    Core.AutoTest.Metric("racking: mm the gun moves when the slide goes home", jolt * 1000f);
                }
                Core.AutoTest.Metric("rack grip: points inside the part", restBuried);
                Core.AutoTest.Metric("rack grip: fingers on the part (of 4)", restTouching);
                Core.AutoTest.Metric("rack grip pulled half way: points inside the part", pulledBuried);
                string moves = _held.VisibleHandle;
                Core.AutoTest.Metric("rack grip: something visible moves with the pull (0/1)", moves.StartsWith("NOTHING", System.StringComparison.Ordinal) ? 0 : 1);
                report($"rack grip ({(IsPump ? "pump" : HandleStroke ? "charging handle" : "slide/bolt")}, {Data.SlideMethod}): {atRest}; pulled half way: {pulled}; moving with the hand: {moves}; drawn: {drawnMoves}; "
                       + $"the gun moved {tug * 1000f:0.0} mm with the pull{(HandDriven ? " (hand-driven: stays open)" : $", {jolt * 1000f:0.0} mm when it went home")}"); 
            }
            finally
            {
                VRHand.Scripted[side] = null;
                VRInput.SimulatedGrab[side] = false;
            }
            yield return new WaitForSecondsRealtime(0.4f); // the grip opens: the part is let go and returns
            if (HandDriven && Ammo.BoltOpen)
            {
                // A bolt or pump stays open where the hand left it: closed again as a hand pushing it home would, or the
                // shots after this check met an open bolt (an MRAD "did not fire").
                _rackedBack = false;
                _held.SlideOffset = 0f;
                Ammo.BoltOpen = false;
                Ammo.RackForward();
            }
        }

        /// <summary>
        /// Autotest: the support hand holding the handguard slides back along the gun onto the charging handle with its grip still
        /// closed, and must take the handle (Tasks 322: it stayed on the handguard, which lets go only at 60 cm, and the rack, which
        /// needs a fresh squeeze, never started: "i cannot pull back the bolt thingy").
        /// </summary>
        internal IEnumerator SupportSlideSelfCheck(System.Action<string> report)
        {
            VRHand off = _sys.Rig.Off;
            if (!Manual || !Ammo.UsesChamber || IsPump || GripData.IsPistol || !GripData.HasSupportPoint)
            {
                yield break;
            }
            string busy = OffHandBusy(off);
            if (busy != null)
            {
                report($"support hand slid onto the charging handle: skipped ({busy})");
                yield break;
            }
            int side = (int)off.Side;
            System.Func<Vector3, RigidPose> At = RackHandPose(off);
            Vector3 from = GripRig.ToWorld(GripData.SupportPoint);
            bool took = false;
            string role = "";
            // The handover's two tests where the palm came nearest the handle: a failure names which one held it back.
            float nearRack = float.MaxValue, nearGuard = 0f;
            VRInput.SimulatedGrab[side] = true;
            try
            {
                VRHand.Scripted[side] = At(from);
                yield return null;
                yield return null;
                off.Role = HandRole.Foregrip;
                _held.SetTwoHanded(VRConfig.TwoHandedAiming.Value);
                yield return null;
                for (int i = 1; i <= 20 && !took; i++)
                {
                    // Toward the handle where it sits on the gun as the gun hand holds it: the live one swings with the two-hand
                    // turn as the palm moves, and a palm sent after it each frame chased it.
                    Vector3 rackOnGun = UnsteeredGunPoint(GripRig.ToGun(RackPointWorld));
                    VRHand.Scripted[side] = At(Vector3.Lerp(from, rackOnGun, i / 20f));
                    yield return null;
                    took = off.Role == HandRole.Slide;
                    Vector3 palm = _held.TwoHanded ? PalmOnLiveGun(off.Palm.position) : off.Palm.position; // (as the handover measures it)
                    float dRack = RackZoneDistance(palm);
                    if (dRack < nearRack)
                    {
                        nearRack = dRack;
                        nearGuard = ForegripDistance(palm);
                    }
                }
                role = off.Role.ToString();
            }
            finally
            {
                if (_racking)
                {
                    EndRack(off, chamber: false);
                }
                if (off.Role == HandRole.Foregrip)
                {
                    _held.SetTwoHanded(false);
                    off.Role = HandRole.Free;
                }
                VRHand.Scripted[side] = null;
                VRInput.SimulatedGrab[side] = false;
            }
            Core.AutoTest.Metric("support hand slid onto the charging handle: taken (1) or not (0)", took ? 1 : 0);
            report($"support hand slid back onto the charging handle, grip held: {(took ? "took the handle" : $"DID NOT TAKE IT (the hand: {role}; nearest {nearRack * 100f:0.0} cm from the handle, grab radius {VRConfig.GrabRadius.Value * 100f:0.0} cm, {nearGuard * 100f:0.0} cm from the handguard: needs under half)")}");
            yield return new WaitForSecondsRealtime(0.3f);
            if (HandDriven && Ammo.BoltOpen)
            {
                // A bolt left open by the take is closed again, as the rack-grip check does (the shots after it met an open bolt).
                _rackedBack = false;
                _held.SlideOffset = 0f;
                Ammo.BoltOpen = false;
                Ammo.RackForward();
            }
        }

        /// <summary>
        /// Autotest: the off hand takes the handguard (as a grab there would) for a picture of both hands on the gun, then
        /// lets go. Reports whether the glove took the weapon's own support pose.
        /// </summary>
        internal IEnumerator TwoHandSelfCheck(System.Action<string> report, System.Action<string> shot)
        {
            VRHand off = _sys.Rig.Off;
            if (!GripData.HasSupportPoint)
            {
                yield break;
            }
            if (off.Role != HandRole.Free)
            {
                report($"two hands: skipped (the off hand is busy: {off.Role})");
                yield break;
            }
            VRInput.SimulatedGrab[(int)off.Side] = true;
            off.Role = HandRole.Foregrip;
            _held.SetTwoHanded(VRConfig.TwoHandedAiming.Value);
            yield return new WaitForSecondsRealtime(0.3f);
            yield return new WaitForEndOfFrame();
            shot("two-handed");
            bool snapped = off.Model != null && off.Model.SnapPose.HasValue;
            HandModel gunHand = _sys.Rig.Dominant.Model;
            if (GripData.IsPistol && off.Model != null && gunHand != null)
            {
                // Cupping a pistol: how much of the support hand is inside the gun hand, closing on it and (as before) not.
                off.Model.Measure(p => gunHand.NearSkin(p, 0f), p => false, out int inside, out _, out _);
                HeldWeapon.LegacySupportSolid = true;
                yield return new WaitForSecondsRealtime(0.35f);
                off.Model.Measure(p => gunHand.NearSkin(p, 0f), p => false, out int before, out _, out _);
                HeldWeapon.LegacySupportSolid = false;
                Core.AutoTest.Metric("support hand on a pistol: points inside the gun hand", inside);
                Core.AutoTest.Metric("support hand on a pistol (closing on the gun only): points inside the gun hand", before);
                report($"support hand round the gun hand: {inside}/72 points inside it (closing on the gun only: {before})");
                yield return new WaitForSecondsRealtime(0.2f);
            }
            VRInput.SimulatedGrab[(int)off.Side] = false;
            yield return null; // the grip opens: the foregrip is let go
            report($"two hands: support hand {(snapped ? "on the weapon's own handguard pose" : "on the tracked hand (no support pose)")}");
        }

        /// <summary>Autotest: the gun is worked by a pump the support hand's trigger takes (<see cref="PumpTriggerSelfCheck"/>).</summary>
        internal bool WorksByPump => Ammo != null && Manual && Ammo.UsesChamber && IsPump;

        /// <summary>
        /// Autotest: the pump worked by the support hand's trigger, as the player does it (user, 2026-10-16: "only rackable
        /// if the trigger finger on the rack was pressed and held"; 255.I6-2): the support hand holds the pump, the trigger
        /// squeezed takes it, a full stroke back and home cycles a round, the trigger let go leaves the hand holding the gun
        /// by the pump again. Also: the grip alone (no trigger) does not work the pump.
        /// </summary>
        internal IEnumerator PumpTriggerSelfCheck(System.Action<string> report)
        {
            VRHand off = _sys.Rig.Off;
            if (!WorksByPump || off.Role != HandRole.Free || off.Held != null || Rig.GunBody == null || _racking)
            {
                yield break;
            }
            int side = (int)off.Side;
            bool chambered = Ammo.Chambered, spent = Ammo.SpentInChamber;
            int rounds = Ammo.MagRounds;
            System.Func<Vector3, RigidPose> at = RackHandPose(off);
            Vector3 start = RackPointWorld;
            Vector3 pull = Rig.DirToWorld(StrokeDir) * StrokeLength;
            bool held = false, taken = false, backOnGrip = false, freeAfter = false;
            float gripOnly = 0f, back = 0f, drawnMost = 0f;
            int cycledFrom = -1;
            try
            {
                // The support hand on the pump (a grab there is the foregrip, as for any handguard).
                VRHand.Scripted[side] = at(start);
                yield return null;
                yield return null;
                VRInput.SimulatedGrab[side] = true;
                off.Role = HandRole.Foregrip;
                _held.SetTwoHanded(VRConfig.TwoHandedAiming.Value);
                yield return new WaitForSecondsRealtime(0.3f);
                held = off.Role == HandRole.Foregrip;
                // The grip alone, pulled back: the pump must not move (it was worked by any grab).
                float before = _held.SlideOffset;
                for (int i = 1; i <= 8; i++)
                {
                    VRHand.Scripted[side] = at(start + pull * (0.5f * i / 8f));
                    yield return null;
                }
                gripOnly = Mathf.Abs(_held.SlideOffset - before);
                VRHand.Scripted[side] = at(start);
                yield return new WaitForSecondsRealtime(0.2f);
                // Make the chamber's state known: a live round in it, the tube with rounds behind it.
                Ammo.Chambered = true;
                Ammo.SpentInChamber = false;
                if (Ammo.MagRounds == 0)
                {
                    Ammo.MagRounds = Mathf.Max(1, Mathf.Min(2, Ammo.Capacity));
                }
                cycledFrom = Ammo.MagRounds;
                // The trigger squeezed: the hand takes the pump.
                VRInput.SimulatedUse[side] = true;
                VRInput.SimulatePress(DigitalAction.Use, off.Side);
                yield return null;
                yield return null;
                taken = _racking && _rackByTrigger && off.Role == HandRole.Slide;
                var drawnHome = _held.DrawnLayout();
                // All the way back, then home.
                for (int i = 1; i <= 12; i++)
                {
                    VRHand.Scripted[side] = at(start + pull * (i / 12f));
                    yield return null;
                }
                back = Travel > 1e-4f ? _held.SlideOffset / Travel : 0f;
                // What the eye sees move (the stroke above is the mod's number): 76 EAGLE's pump block moved 15 mm in the
                // grip-only rack check while the stroke read 80% (252 ae).
                yield return new WaitForEndOfFrame();
                foreach (var kv in drawnHome)
                {
                    if (_held.DrawnLayout().TryGetValue(kv.Key, out Vector3 now))
                    {
                        drawnMost = Mathf.Max(drawnMost, Vector3.Distance(kv.Value, now));
                    }
                }
                for (int i = 11; i >= 0; i--)
                {
                    VRHand.Scripted[side] = at(start + pull * (i / 12f));
                    yield return null;
                }
                yield return new WaitForSecondsRealtime(0.1f);
                // The trigger let go: the hand holds the gun by the pump again.
                VRInput.SimulatedUse[side] = false;
                yield return null;
                yield return null;
                backOnGrip = !_racking && off.Role == HandRole.Foregrip;
                VRInput.SimulatedGrab[side] = false;
                yield return new WaitForSecondsRealtime(0.3f);
                freeAfter = off.Role == HandRole.Free;
            }
            finally
            {
                VRInput.SimulatedUse[side] = false;
                VRInput.SimulatedGrab[side] = false;
                VRHand.Scripted[side] = null;
                if (_racking)
                {
                    EndRack(off, chamber: false);
                }
                if (off.Role != HandRole.Free)
                {
                    _held.SetTwoHanded(false);
                    off.Role = HandRole.Free;
                }
            }
            // A full stroke ejects the round and feeds the next: one round fewer in the tube, one chambered.
            bool cycled = cycledFrom > 0 && Ammo.Chambered && Ammo.MagRounds == cycledFrom - 1;
            Core.AutoTest.Metric("pump by trigger: share of the stroke at the full pull", back);
            Core.AutoTest.Metric("pump by trigger: mm the drawn pump moves at the full pull", drawnMost * 1000f);
            report($"pump by trigger: {(held ? "held by the pump" : "NOT HELD BY THE PUMP")}, the grip alone moved it {gripOnly * 1000f:0} mm{(gripOnly > 0.005f ? " (THE GRIP ALONE WORKS THE PUMP)" : "")}; "
                   + $"{(taken ? "the trigger took the pump" : "THE TRIGGER DID NOT TAKE THE PUMP")}, full pull {back * 100f:0}% of the stroke{(taken && back < 0.8f ? " (NOT ALL THE WAY BACK)" : "")}"
                   + $" (the drawn gun moved {drawnMost * 1000f:0} mm{(taken && drawnMost < Travel * 0.5f ? ": THE PUMP IS NOT SEEN MOVING" : "")}), "
                   + $"{(cycled ? "cycled a round" : taken ? "DID NOT CYCLE A ROUND" : "not cycled")}; "
                   + $"trigger let go: {(backOnGrip ? "the hand holds the pump again" : "NOT BACK ON THE PUMP")}, grip let go: {(freeAfter ? "free" : "NOT FREE")}");
            // Back as it was.
            _rackedBack = false;
            _held.SlideOffset = 0f;
            Ammo.BoltOpen = false;
            Ammo.Chambered = chambered;
            Ammo.SpentInChamber = spent;
            Ammo.MagRounds = rounds;
            Ammo.Sync();
        }

        /// <summary>
        /// Autotest: an en-bloc rifle as the player loads and fires it (255.I6-5): with the bolt shut on a round a clip is
        /// refused; through the open action a full clip goes in, the bolt slams home on its own and chambers; the last round
        /// fired throws the empty clip and leaves the bolt locked open, ready for the next clip.
        /// </summary>
        internal IEnumerator EnBlocSelfCheck(System.Action<string> report)
        {
            if (!Manual || Ammo == null || !Data.EnBloc || !Ammo.UsesDetachableMag)
            {
                yield break;
            }
            VRHand off = _sys.Rig.Off;
            string busy = OffHandBusy(off);
            if (busy != null)
            {
                report($"en-bloc: skipped ({busy})");
                yield break;
            }
            bool chambered = Ammo.Chambered, inserted = Ammo.MagInserted, locked = Ammo.SlideLocked;
            int rounds = Ammo.MagRounds;
            if (Ammo.MagInserted)
            {
                Ammo.EjectMagazine();
            }
            // The bolt shut on a round, no clip: a clip cannot go in.
            Ammo.Chambered = true;
            Ammo.SlideLocked = false;
            Ammo.Sync();
            MagazineProp clip = MagazineProp.Create(Rig, PropKind.Magazine, Ammo.Capacity);
            clip.Owner = Ammo;
            clip.Attach(off);
            clip.FollowHand();
            bool refusedShut = !AcceptsNow(clip);
            // As after the last round: the bolt locked back over an empty chamber. The clip goes in and releases the bolt.
            Ammo.Chambered = false;
            Ammo.SlideLocked = true;
            Ammo.Sync();
            yield return new WaitForSecondsRealtime(0.2f);
            bool accepted = AcceptsNow(clip);
            if (accepted)
            {
                SeatProp(off, clip);
            }
            else
            {
                clip.Destroy();
            }
            yield return new WaitForSecondsRealtime(0.4f);
            bool seated = Ammo.MagInserted, released = !Ammo.SlideLocked, fed = Ammo.Chambered;
            // Fired to the last round: the empty clip flies out, the bolt stays open.
            int shots = 0;
            while (Ammo.Chambered && shots < Ammo.Capacity + 2)
            {
                Ammo.OnShot();
                OnShot();
                shots++;
                yield return null;
            }
            yield return new WaitForSecondsRealtime(0.3f);
            bool thrown = !Ammo.MagInserted, lockedOpen = Ammo.SlideLocked;
            // The empty clip it threw out, pushed at the open action: refused (taken, it left the bolt back and no full clip went in after it;
            // user, 2026-11-08: "i cant reload it rn").
            MagazineProp empty = MagazineProp.Create(Rig, PropKind.Magazine, 0);
            empty.Owner = Ammo;
            bool emptyRefused = !AcceptsNow(empty);
            empty.Destroy();
            report($"en-bloc: bolt shut on a round: {(refusedShut ? "clip refused" : "CLIP TAKEN WITH THE BOLT SHUT")}; through the open action: "
                   + $"{(accepted && seated ? "clip in" : "CLIP NOT SEATED")}, {(released ? "bolt home on its own" : "BOLT NOT RELEASED")}, {(fed ? "chambered" : "NOTHING CHAMBERED")}; "
                   + $"an empty clip at the open action: {(emptyRefused ? "refused" : "EMPTY CLIP NOT REFUSED")}; "
                   + $"{shots} shot(s): {(thrown ? "empty clip thrown out" : "EMPTY CLIP NOT THROWN OUT")}, {(lockedOpen ? "bolt locked open" : "BOLT NOT LOCKED OPEN")}{(_pingSource != null ? $", ping '{_pingSource.clip.name}'" : "")}");
            // Back as it was.
            Ammo.SlideLocked = locked;
            Ammo.Chambered = chambered;
            Ammo.MagInserted = inserted;
            Ammo.MagRounds = rounds;
            Ammo.Sync();
        }

        /// <summary>
        /// Autotest: an emptied magazine dropped at the hip is not handed back by a squeeze in the pouch (user, 2026-10-16: "I
        /// grab an empty mag from the pouch"; 255.I6-6): the squeeze goes through the hand's own grab (TryGrab) and must
        /// bring a full one.
        /// </summary>
        internal IEnumerator EmptyAtPouchSelfCheck(System.Action<string> report)
        {
            if (!Manual || Ammo == null || !Ammo.UsesDetachableMag || !Ammo.MagInserted || Ammo.BoxLocked)
            {
                yield break;
            }
            VRHand off = _sys.Rig.Off;
            string busy = OffHandBusy(off) ?? (_ejecting != null ? "a magazine is coming out" : !Ammo.InfiniteSpare && Ammo.SpareAmmo <= 0 ? "no spare rounds" : null);
            if (busy != null)
            {
                report($"empty magazine at the pouch: skipped ({busy})");
                yield break;
            }
            int side = (int)off.Side;
            bool chambered = Ammo.Chambered;
            int rounds = Ammo.MagRounds, spare = Ammo.SpareAmmo;
            Ammo.MagRounds = 0;
            EjectMagazine();
            MagazineProp empty = _ejecting;
            FinishEject();
            Vector3 pouch = _sys.Zones.Pouch;
            if (empty != null && empty.Go != null)
            {
                // Lying in the pouch zone, held still there for the squeeze.
                empty.Go.transform.position = pouch + Vector3.down * 0.03f;
                if (empty.Body != null)
                {
                    empty.Body.velocity = Vector3.zero;
                    empty.Body.isKinematic = true;
                }
            }
            RigidPose rawFromPalm = off.Palm.Inverse().Multiply(off.Raw);
            MagazineProp got = null;
            try
            {
                VRHand.Scripted[side] = new RigidPose(pouch, off.Palm.rotation).Multiply(rawFromPalm);
                yield return null;
                yield return null;
                VRInput.SimulateGrabPress(off.Side);
                for (int i = 0; i < 6 && off.Held == null; i++)
                {
                    yield return null;
                }
                got = off.Held as MagazineProp;
            }
            finally
            {
                VRInput.SimulatedGrab[side] = false;
                VRHand.Scripted[side] = null;
            }
            bool emptyBack = got != null && got == empty;
            report($"empty magazine at the pouch: {(empty == null ? "NO MAGAZINE CAME OUT" : emptyBack ? "THE EMPTY MAGAZINE WAS HANDED BACK" : got != null && got.Rounds > 0 ? $"a full one from the pouch ({got.Rounds} rounds)" : got != null ? "AN EMPTY MAGAZINE FROM THE POUCH" : "NOTHING TAKEN (the pouch did not answer the squeeze)")}");
            // Back as it was.
            if (got != null && got != empty)
            {
                Ammo.ReturnSpare(got.Rounds);
                got.Rounds = 0;
                got.Destroy();
            }
            if (empty != null)
            {
                empty.Rounds = 0;
                empty.Destroy();
            }
            Ammo.MagInserted = true;
            Ammo.MagRounds = rounds;
            Ammo.Chambered = chambered;
            Ammo.Sync();
            if (!Ammo.InfiniteSpare && Ammo.SpareAmmo < spare)
            {
                Ammo.ReturnSpare(spare - Ammo.SpareAmmo); // the reserve as before the check
            }
        }

        /// <summary>
        /// Autotest: the seated magazine taken 7 cm off the well's line and pulled (255.I6-7): it must stay on its rail and
        /// slide out along the well, not jump to the hand through the receiver (the hand's offset is kept from the grab:
        /// RailLateral0). The reload film only pulls 1.5 cm aside.
        /// </summary>
        internal IEnumerator OffLinePullSelfCheck(System.Action<string> report)
        {
            if (!Manual || Ammo == null || !Ammo.UsesDetachableMag || !Ammo.MagInserted || Ammo.BoxLocked || Rig.GunBody == null)
            {
                yield break;
            }
            VRHand off = _sys.Rig.Off;
            string busy = OffHandBusy(off) ?? (_ejecting != null ? "a magazine is coming out" : null);
            if (busy != null)
            {
                report($"magazine pulled 7 cm off the line: skipped ({busy})");
                yield break;
            }
            int side = (int)off.Side;
            int rounds = Ammo.MagRounds;
            RigidPose palm0 = off.Palm, rawFromPalm = palm0.Inverse().Multiply(off.Raw);
            MagazineProp probe = MagazineProp.Create(Rig, PropKind.Magazine, 0);
            probe.RailDepth = probe.RailLength;
            RigidPose holding = probe.RailPose(SeatPose).Multiply(probe.HoldPose(off).Inverse().Multiply(palm0));
            float railLength = probe.RailLength;
            Vector3 insert = SeatPose.TransformDirection(probe.InsertDirLocal);
            probe.Destroy();
            Vector3 aside = Vector3.Cross(insert, Rig.AimWorld).normalized;
            Vector3 hand = holding.position + aside * 0.07f;
            bool firstFrame = false, halfWay = false;
            try
            {
                VRHand.Scripted[side] = new RigidPose(hand, holding.rotation).Multiply(rawFromPalm);
                yield return null;
                yield return null;
                VRInput.SimulatedGrab[side] = true;
                PullSeatedMagazine(off);
                yield return null;
                firstFrame = off.Held is MagazineProp m1 && m1.OnRail;
                for (int i = 1; i <= 8; i++)
                {
                    VRHand.Scripted[side] = new RigidPose(hand - insert * (railLength * 0.5f * i / 8f), holding.rotation).Multiply(rawFromPalm);
                    yield return null;
                }
                halfWay = off.Held is MagazineProp m2 && m2.OnRail;
                // Pushed home again.
                for (int i = 7; i >= -1; i--)
                {
                    VRHand.Scripted[side] = new RigidPose(hand - insert * (railLength * 0.5f * i / 8f), holding.rotation).Multiply(rawFromPalm);
                    yield return null;
                }
            }
            finally
            {
                VRInput.SimulatedGrab[side] = false;
                VRHand.Scripted[side] = null;
            }
            yield return null;
            if (off.Held is MagazineProp left)
            {
                if (AcceptsNow(left))
                {
                    SeatProp(off, left);
                }
                else
                {
                    Ammo.ReturnSpare(left.Rounds);
                    left.Rounds = 0;
                    left.Destroy();
                }
            }
            if (!Ammo.MagInserted)
            {
                Ammo.MagInserted = true;
                Ammo.MagRounds = rounds;
                Ammo.Sync();
            }
            report($"magazine pulled 7 cm off the well's line: {(firstFrame ? "on its rail on the first frame" : "NOT ON THE RAIL ON THE FIRST FRAME")}, "
                   + $"{(halfWay ? "still on it half way out" : "NOT ON THE RAIL HALF WAY OUT")}");
        }

        /// <summary>
        /// Autotest: an empty reload as the player does it (Tasks 252 c/d/i, 254): the last round fired leaves the slide or
        /// bolt locked back; a fresh magazine goes in and the drawn slide must stay back (it ran forward on its own on the
        /// mwii-g17); released, it must come home (it stayed back until the first shot on the FNX-45 and many rifles); then a
        /// hand rack must visibly move it (a broken block once reset it every frame). Measured on the drawn model, in gun space.
        /// </summary>
        internal IEnumerator EmptyReloadSelfCheck(System.Action<string> report)
        {
            if (!Manual || Ammo == null || !Ammo.UsesDetachableMag || !Ammo.UsesChamber || !Data.LockOnEmpty || Data.EnBloc || Ammo.HasCover)
            {
                yield break; // (a belt-fed gun's fresh box comes with its belt unlaid: it could not fire after this check)
            }
            if (_racking || Rig.GunBody == null || !ReferenceEquals(_held.ActiveRig, _held.Rig))
            {
                report($"empty reload: skipped ({(_racking ? "a rack under way" : Rig.GunBody == null ? "no gun body" : "a sub weapon is active")})");
                yield break;
            }
            bool chambered = Ammo.Chambered, inserted = Ammo.MagInserted, locked = Ammo.SlideLocked;
            int rounds = Ammo.MagRounds;
            float Moved(Dictionary<Object, Vector3> a, Dictionary<Object, Vector3> b)
            {
                float most = 0f;
                foreach (var kv in a)
                {
                    if (b.TryGetValue(kv.Key, out Vector3 at))
                    {
                        most = Mathf.Max(most, Vector3.Distance(kv.Value, at));
                    }
                }
                return most;
            }
            // Which drawn parts are away from home, by name, and how far back (+) or forward (-) along the bore (ARS MP9: a
            // part 44 mm off after the release, not named; 252 ae reviews: named before anything is changed).
            string Away(Dictionary<Object, Vector3> a, Dictionary<Object, Vector3> b)
            {
                var away = new List<(string name, float moved, float back)>();
                foreach (var kv in a)
                {
                    if (b.TryGetValue(kv.Key, out Vector3 at) && Vector3.Distance(kv.Value, at) >= 0.003f)
                    {
                        away.Add(((kv.Key is Renderer ? "" : "bone ") + kv.Key.name, Vector3.Distance(kv.Value, at), Vector3.Dot(at - kv.Value, -Data.AimAxis)));
                    }
                }
                away.Sort((x, y) => y.moved.CompareTo(x.moved));
                var parts = new List<string>();
                for (int i = 0; i < away.Count && i < 3; i++)
                {
                    parts.Add($"'{away[i].name}' {away[i].moved * 1000f:0} mm ({away[i].back * 1000f:0} mm back)");
                }
                return parts.Count == 0 ? "nothing" : string.Join(", ", parts.ToArray()) + (away.Count > 3 ? $" and {away.Count - 3} more" : "");
            }
            yield return new WaitForSecondsRealtime(0.3f);
            // The loaded look is copied only once the gun has settled loaded (HeldWeapon.SettledLoaded): a moment given for it.
            for (float waited = 0f; waited < 1.5f && inserted && rounds >= Mathf.Max(1, Ammo.Capacity - 1) && !MagazineProp.KeepsLoadedLook(Data.Key); waited += 0.1f)
            {
                yield return new WaitForSecondsRealtime(0.1f);
            }
            var home = _held.DrawnLayout();
            string homeState = _held.AnimatorStateText;
            // The last round fired: nothing chambered, the magazine empty, the slide caught back.
            Ammo.Chambered = false;
            Ammo.MagRounds = 0;
            Ammo.SlideLocked = true;
            Ammo.Sync();
            yield return new WaitForSecondsRealtime(0.5f);
            var lockedBack = _held.DrawnLayout();
            float lockTravel = Moved(home, lockedBack);
            // Locked with a charging handle of its own drawn home (an M4's T): the grab point must be on it, not where the carrier
            // took it (it sat 7.5 cm behind the T; user, 2026-10-20: "some m4a1 racks got unrackable again").
            if (_held.HandleLockedAlone && Rig.Handle != null)
            {
                Vector3 grab = RackPointWorld;
                float gap = float.MaxValue;
                foreach (Renderer r in Rig.Handle.GetComponentsInChildren<Renderer>())
                {
                    gap = Mathf.Min(gap, Mathf.Sqrt(r.bounds.SqrDistance(grab)));
                }
                if (gap < float.MaxValue)
                {
                    Core.AutoTest.Metric("locked empty: cm from the grab point to the drawn charging handle", gap * 100f);
                    string handleLine = $"locked empty: the grab point {gap * 100f:0.0} cm from the drawn charging handle";
                    if (gap > 0.03f)
                    {
                        handleLine += " (THE HANDLE IS OUT OF THE GRAB)";
                    }
                    report(handleLine);
                }
            }
            // A full magazine as the pouch makes it while the gun's own sits empty: drawn as it looks loaded, not as the empty one
            // shows (user, 2026-10-25: "it shows an empty mag, but it's full").
            MagazineProp full = MagazineProp.Create(Rig, PropKind.Magazine, Ammo.Capacity), empty = MagazineProp.Create(Rig, PropKind.Magazine, 0);
            int fullDrawn = MagazineProp.DrawnCount(full), emptyDrawn = MagazineProp.DrawnCount(empty);
            report($"empty reload: a full magazine from the pouch with the gun's empty draws {fullDrawn} renderer(s) (an empty one {emptyDrawn}; "
                   + $"{(MagazineProp.KeepsLoadedLook(Data.Key) ? "the loaded look kept" : "NO LOADED LOOK KEPT")}){(fullDrawn < emptyDrawn ? " (THE FULL ONE DRAWS LESS THAN AN EMPTY ONE)" : "")}");
            full?.Destroy();
            empty?.Destroy();
            // A fresh magazine in.
            Ammo.InsertMagazine(Ammo.Capacity);
            yield return new WaitForSecondsRealtime(0.6f);
            var afterInsert = _held.DrawnLayout();
            float ranForward = Moved(lockedBack, afterInsert);
            string ranWhat = Away(lockedBack, afterInsert); // (which part: the P220 T S's 60 mm was not named, and may be no slide at all; 2026-11-08)
            bool stillLocked = Ammo.SlideLocked;
            // Released (the slide release, as the A button does).
            ReleaseSlide();
            yield return new WaitForSecondsRealtime(0.8f);
            var released = _held.DrawnLayout();
            float leftBack = Moved(home, released);
            string releasedAway = Away(home, released), releasedState = _held.AnimatorStateText;
            bool fed = Ammo.Chambered;
            // A hand rack: the off hand takes the part and pulls it half way back; the drawn part must follow. Through the
            // hand's own path (BeginRack, UpdateRack, EndRack): the offset set directly left a reset in that path unseen
            // (255.I6-1).
            float racked = -1f;
            VRHand off = _sys.Rig.Off;
            if (off.Role == HandRole.Free && off.Held == null)
            {
                int side = (int)off.Side;
                System.Func<Vector3, RigidPose> at = RackHandPose(off);
                Vector3 start = RackPointWorld;
                Vector3 pull = Rig.DirToWorld(StrokeDir) * (StrokeLength * 0.5f);
                VRInput.SimulatedGrab[side] = true;
                try
                {
                    VRHand.Scripted[side] = at(start);
                    yield return null;
                    yield return null;
                    BeginRack(off);
                    yield return new WaitForSecondsRealtime(0.2f);
                    for (int i = 1; i <= 10; i++)
                    {
                        VRHand.Scripted[side] = at(start + pull * (i / 10f));
                        yield return null;
                    }
                    yield return new WaitForSecondsRealtime(0.1f);
                    yield return new WaitForEndOfFrame();
                    racked = _racking ? Moved(home, _held.DrawnLayout()) : -2f;
                    VRInput.SimulatedGrab[side] = false;
                    yield return new WaitForSecondsRealtime(0.5f); // let go: it goes home
                }
                finally
                {
                    VRHand.Scripted[side] = null;
                    VRInput.SimulatedGrab[side] = false;
                }
                if (_racking)
                {
                    EndRack(off, chamber: false);
                }
            }
            Core.AutoTest.Metric("empty reload: mm the drawn slide ran forward on a fresh magazine", ranForward * 1000f);
            Core.AutoTest.Metric("empty reload: mm the drawn slide stayed back after the release", leftBack * 1000f);
            if (racked >= 0f)
            {
                Core.AutoTest.Metric("empty reload: mm the drawn slide moved at half a hand rack", racked * 1000f);
            }
            float tol = Mathf.Max(0.003f, lockTravel * 0.25f);
            string verdict = lockTravel < 0.003f ? "THE DRAWN SLIDE DOES NOT SHOW THE LOCK (nothing moved when locked)"
                : ranForward > tol ? $"THE SLIDE RAN FORWARD ON ITS OWN when the magazine went in ({ranForward * 1000f:0} mm: {ranWhat})"
                : leftBack > tol ? $"THE SLIDE STAYED BACK after the release ({leftBack * 1000f:0} mm from home: {releasedAway}; at home {homeState}, released {releasedState})"
                : "stayed back with the fresh magazine, home when released";
            report($"empty reload: locked back {lockTravel * 1000f:0} mm, {(stillLocked ? "still locked" : "NOT LOCKED")} with a fresh magazine, {verdict}, {(fed ? "chambered" : "NOTHING CHAMBERED")}; "
                   + (racked == -1f ? "hand rack not checked (the off hand was busy)"
                       : racked == -2f ? "THE HAND LET GO OF THE RACK during the pull"
                       : $"half a hand rack moves the drawn gun {racked * 1000f:0} mm{(racked < Mathf.Max(0.002f, Travel * 0.15f) ? " (THE HAND RACK MOVES NOTHING)" : "")}"));
            // Back as it was.
            Ammo.SlideLocked = locked;
            Ammo.Chambered = chambered;
            Ammo.MagInserted = inserted;
            Ammo.MagRounds = rounds;
            Ammo.Sync();
        }

        /// <summary>Autotest: one full manual cycle (back and home), as a hand on the bolt or pump would do.</summary>
        internal void CycleForTest()
        {
            if (Ammo == null || !Ammo.UsesChamber)
            {
                return;
            }
            Ammo.BoltOpen = false;
            bool spent = Ammo.SpentInChamber;
            bool live = Ammo.RackBack();
            if (spent || live)
            {
                Casings.Eject(_held, live);
            }
            Ammo.RackForward();
        }
    }
}
