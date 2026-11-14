using System.Collections.Generic;
using RavenfieldVR.Core;
using RavenfieldVR.Input;
using RavenfieldVR.Rig;
using RavenfieldVR.Runtime;
using UnityEngine;

namespace RavenfieldVR.Weapons
{
    // HeldWeapon, part: the held gun's measured shape. The solids the hands close on and keep out of, the magazine
    // line, the rack points on the drawn slide, and the self-checks of how a hand sits on it.
    // The class and the frame's work: HeldWeapon.cs.
    public sealed partial class HeldWeapon
    {
        /// <summary>
        /// Boneworks-style grips: the gun hand's glove takes the weapon's own hand pose on its grip, the support hand the
        /// weapon's support pose on the handguard (slid to wherever you hold it) or on the pistol grip it cups.
        /// </summary>
        private GunVolume _volume;
        private int _volumeTries;
        private float _volumeRetryAt;
        private bool _hullApplied;
        /// <summary>The frame the hull was last cooked in: <see cref="HandSolid"/> is asked twice in a frame while racking, one slice a frame.</summary>
        private int _hullCookFrame = -1;
        private System.Func<Vector3, bool> _supportSolid;
        private HandModel _supportGunHand;

        /// <summary>Self-test: the support hand on a pistol closes on the gun only, as before (to compare).</summary>
        internal static bool LegacySupportSolid;

        /// <summary>Self-test: the held gun's physics stand-in.</summary>
        internal GunCollider Collider => _gunCollider;
        private float _heldSince = -1f;
        private System.Func<Vector3, bool> _solid;

        /// <summary>The gun's solid the hands close on, as posed this frame (grown by a finger's radius); null until measured.</summary>
        internal System.Func<Vector3, bool> HandsSolid => _volume != null ? _solid : null; // (dropped to measure again: none meanwhile)

        /// <summary>Whether <see cref="HandsSolid"/> includes the seated magazine now (its shape changes with it).</summary>
        internal bool MagSolidShown => _magSolidShown;

        /// <summary>
        /// The seated magazine alone, as posed now (grown by a finger's radius); false while none is shown. A magazine in the
        /// hand that comes into the well must not pass through it: the well's opening is for the gun's body only.
        /// </summary>
        internal bool SeatedMagSolid(Vector3 p) => _magSolidShown && _magVolume.Solid(p);

        /// <summary>The gun's solid for a hand racking the action: recesses (an ejection port) closed over.</summary>
        private bool ClosedGunSolid(Vector3 p) => _volume.SolidClosed(p) || _magSolidShown && _magVolume.Solid(p);

        /// <summary>Self-test: the point is between the gun's two sides as seen from the side (inside a grip, not beside it).</summary>
        internal bool InsideGunFromSide(Vector3 world) => _volume != null && _volume.Ready && _volume.InsideSideView(world);

        /// <summary>The point is inside the drawn gun (its measured shape, not grown): what hides a muzzle flash from an eye.</summary>
        internal bool InsideDrawnGun(Vector3 world) => _volume != null && _volume.Ready && _volume.InsideModel(world);

        /// <summary>One of the points at i / <paramref name="steps"/> on the line between the two is inside the drawn gun (<see cref="GunVolume.LineInsideModel"/>).</summary>
        internal bool LineThroughDrawnGun(Vector3 from, Vector3 to, int steps) => _volume != null && _volume.Ready && _volume.LineInsideModel(from, to, steps);

        private int _handSolidFrame = -1;
        private System.Func<Vector3, bool> _handSolidNow;

        /// <summary>
        /// <see cref="HandSolidNow"/> once a frame: SnapHands asks it, and RackSolid asks it again in the same pass while the off
        /// hand racks, with nothing moved between (the volume posed, the seated magazine and slide tip looked at twice; review,
        /// 2026-10-23).
        /// </summary>
        internal System.Func<Vector3, bool> HandSolid()
        {
            if (_handSolidFrame != Time.frameCount)
            {
                _handSolidFrame = Time.frameCount;
                _handSolidNow = HandSolidNow();
            }
            return _handSolidNow;
        }

        /// <summary>
        /// The model's solid for the hands to close on, measured a moment after the weapon is in hand (its idle pose, every
        /// part in place) and kept per weapon type. Null until measured or when it cannot be.
        /// </summary>
        private System.Func<Vector3, bool> HandSolidNow()
        {
            if (_heldSince < 0f)
            {
                _heldSince = Time.unscaledTime;
            }
            Quaternion frame = OpenVRMath.SafeLookRotation(Rig.Data.AimAxis, Rig.Data.Up);
            // Measured with the weapon at rest (drawn, not reloading, slide home, hinge shut): whatever is displaced then
            // stays in its shape for the session. A gun that never comes to rest (empty, slide locked) is measured anyway.
            float held = Time.unscaledTime - _heldSince;
            bool atRest = Weapon.unholstered && !Weapon.reloading && SlideOffset < 0.002f && HingeOpenness < 0.01f;
            // Or stalled: a measuring whose views stopped is neither, and the hands had no solid for the rest of the hold (threads
            // hunt, 2026-10-21; the 15 s give-up was only in the cache's lookup, which this held volume never made again).
            if (_volume != null && (_volume.Failed || _volume.Stalled))
            {
                // Its read-back or measuring failed: measured again a second later (GunVolume keeps no failed entry). Latched
                // before, the hands never got a solid for the rest of the hold.
                _volume = null;
                _volumeRetryAt = Time.unscaledTime + 1f;
            }
            // An infinite-magazine gun (ammo -1) is analysed as NoAmmo but is a gun all the same: it gets a hand solid too.
            // A kit keeps its hidden glove, and a tool with no ammo stays unmeasured as before.
            bool measurable = !_isKit && (Rig.Data.Action != ActionType.NoAmmo || Weapon.configuration != null && Weapon.configuration.ammo == -1);
            if (_volume == null && _volumeTries < 3 && Time.unscaledTime >= _volumeRetryAt && held > 0.25f && (atRest || held > 5f) && measurable)
            {
                _volumeTries++;
                _volume = GunVolume.For(Rig, frame, Rig.Data.GripPoint);
                if (_volume == null)
                {
                    _volumeRetryAt = Time.unscaledTime + 1f; // not built (an exception, a degenerate box): tried again, three times at most
                }
            }
            if (_volume == null || !_volume.Ready || Rig.GunBody == null)
            {
                return null;
            }
            // The hull's pieces are cooked a slice a frame (task 263: all of them in one frame were a spike on the first hold
            // of a weapon type); the shape is chosen once they are all cooked, exactly as before.
            if (!_hullApplied && _volume.HullPieces != null && _volume.HullPieces.Length > 0 && _gunCollider != null && _hullCookFrame != Time.frameCount)
            {
                _hullCookFrame = Time.frameCount;
                // A rendered-model shape already decided against is never used (ChooseShape keeps the meshes): nothing to cook.
                bool hullUsed = _volume.Unreadable || !RenderedShapeBetter.TryGetValue(Rig.Data.Key, out bool known) || known;
                if (!hullUsed || CookHullWithin(_volume.HullPieces, 1.5f))
                {
                    _hullApplied = true;
                    // Apart: the choice already made for the type (every draw) and the first one's comparison (once a type).
                    string shapePart = _volume.Unreadable || RenderedShapeBetter.ContainsKey(Rig.Data.Key) ? "hands: shape chosen (known)" : "hands: shape chosen (compared)";
                    bool t = Core.PerfProbe.Part(shapePart);
                    ChooseShape();
                    if (t)
                    {
                        Core.PerfProbe.End(shapePart);
                    }
                }
            }
            _volume.SetPose(Rig.ToWorld(Rig.Data.GripPoint), Rig.GunBody.rotation * frame);
            // The steps once a draw timed apart in a profiled run: the hands' step had a 40 ms frame once, about a second
            // into a draw, where the measured shape becomes ready (2026-10-25).
            if (!_rackSnapped)
            {
                _rackSnapped = true;
                bool t = Core.PerfProbe.Part("hands: rack point snapped");
                SnapRackPoint();
                if (t)
                {
                    Core.PerfProbe.End("hands: rack point snapped");
                }
            }
            if (!_knobChecked && SlideOffset < 0.002f && !Weapon.reloading)
            {
                _knobChecked = true; // with the slide home: measured with it locked back, it read as seen (rule review)
                bool t = Core.PerfProbe.Part("hands: hidden slide checked");
                KnobOnHiddenSlide();
                if (t)
                {
                    Core.PerfProbe.End("hands: hidden slide checked");
                }
            }
            // (Each on its own: together they had a 13-14 ms frame once in a run, about a second into a draw; 2026-10-25.)
            bool timed = Core.PerfProbe.Part("hands: slide tip");
            RackOnDrawnSlideTip();
            if (timed)
            {
                Core.PerfProbe.End("hands: slide tip");
            }
            timed = Core.PerfProbe.Part("hands: seated magazine");
            UpdateSeatedMagSolid();
            if (timed)
            {
                Core.PerfProbe.End("hands: seated magazine");
            }
            timed = Core.PerfProbe.Part("hands: magazine line");
            FitMagazineLine();
            if (timed)
            {
                Core.PerfProbe.End("hands: magazine line");
            }
            return _solid ?? (_solid = p => _volume != null && _volume.Solid(p) || _magSolidShown && _magVolume != null && _magVolume.Solid(p));
        }

        private GunVolume _slideTipVolume;
        private bool _slideTipDone;
        private bool? _slideTipWanted; // WeaponAnalyzer.WantsDrawnSlideTip for this weapon, asked once

        /// <summary>
        /// The rack point on the moving part's drawn tip, for a part the analysis could not read (WeaponAnalyzer.WantsDrawnSlideTip):
        /// the part alone is measured from three sides (shared per weapon type), and its surface point farthest from the bore
        /// on the receiver becomes where the hand takes it. The QJB 95 / 95-1's cocking hook (19 cm ahead of the old point,
        /// beyond the hand's reach) and the AK family's tab on its carrier (6-7 cm) offline (tools/export_checks/slide_tip_rule.py;
        /// task 305). Taken with the slide home; once per draw.
        /// </summary>
        private void RackOnDrawnSlideTip()
        {
            if (_slideTipDone)
            {
                return;
            }
            WeaponRigData d = Rig.Data;
            Transform slide = Rig.Slide;
            // Asked once: its answer is the weapon's own, and asked every frame until the tip was read (the slide locked back on
            // an empty gun, for as long as it stays so) it walked the slide's meshes into a new array and ran a regex each frame.
            if (_slideTipWanted == null)
            {
                _slideTipWanted = slide != null && WeaponAnalyzer.WantsDrawnSlideTip(d, slide);
            }
            if (!_slideTipWanted.Value)
            {
                _slideTipDone = true;
                return;
            }
            if (_slideTipVolume == null)
            {
                var renderers = new List<Renderer>();
                foreach (Renderer r in slide.GetComponentsInChildren<Renderer>(false))
                {
                    if (r.enabled && (r is MeshRenderer || r is SkinnedMeshRenderer) && System.Array.IndexOf(Rig.ArmRenderers, r) < 0)
                    {
                        renderers.Add(r);
                    }
                }
                _slideTipVolume = renderers.Count > 0 ? GunVolume.Measure("slidetip:" + d.Key, renderers, slide, 3) : null;
                if (_slideTipVolume == null)
                {
                    _slideTipDone = true;
                    return;
                }
            }
            if (_slideTipVolume.Failed || _slideTipVolume.Stalled) // (stalled: never Ready, it waited for the whole hold)
            {
                _slideTipDone = true;
                return;
            }
            if (!_slideTipVolume.Ready || SlideOffset > 0.002f || Weapon.reloading)
            {
                return; // measured over a few frames; read with the slide home
            }
            _slideTipDone = true;
            var gunPoints = new List<Vector3>();
            foreach (Vector3 p in _slideTipVolume.SurfacePoints(2))
            {
                gunPoints.Add(Rig.ToGun(slide.position + slide.rotation * p));
            }
            if (WeaponAnalyzer.TipOfSlide(d, gunPoints, RackPointGun, out Vector3 tip))
            {
                Log.InfoOnce("slidetip:" + d.Key, $"{d.Key}: rack point on its moving part's drawn tip ({Vector3.Distance(tip, RackPointGun) * 100f:0.0} cm from the analysed point, which its meshes could not show).");
                RackPointGun = tip;
            }
        }

        /// <summary>Analyses whose magazine line has been fitted to the measured shape (once each; a new analysis again).</summary>
        private static readonly HashSet<WeaponRigData> MagLineFitted = new HashSet<WeaponRigData>();

        /// <summary>
        /// The line a magazine slides in on, fitted to the gun as it is drawn: from the analysed line, tilted up to 24 degrees
        /// either way in the gun's side plane, the one along which the least of the magazine (its own drawn shape, a third
        /// to a quarter of the way out down to the whole rail) passes through the gun outside its well (a handgun's well is
        /// its drawn shape, not its box: see <c>shapeWell</c> below). The analysed line
        /// came from the reload animation's pull or the magazine's box and was off on whole families: magazines went into
        /// pistol grips, the HK416-D's and CZ Bren 2's through the trigger guard (user, 2026-10-16: "could be for all
        /// weapons"). Kept only when clearly better: at most 60% of the points through the gun, and 3 fewer at least (the grid) or
        /// a fault of at least 1% of the skin's points to begin with (the skin).
        /// </summary>
        private void FitMagazineLine()
        {
            WeaponRigData d = Rig.Data;
            if (MagLineFitted.Contains(d) || _volume == null || !_volume.Ready || _magVolume == null || !_magVolume.Ready
                || !_magSolidShown || Ammo == null || !Ammo.UsesDetachableMag || !Ammo.MagInserted || d.Action == ActionType.SingleShot
                || d.IsMachineGun || Ammo.HasCover // a machine gun's box hooks on beside the gun, it does not slide up a well
                || d.MagBounds.size.sqrMagnitude < 1e-6f || d.MagEjectAxis.sqrMagnitude < 0.5f || SlideOffset > 0.002f || Weapon.reloading
                || !ReferenceEquals(ActiveRig, Rig))
            {
                return;
            }
            MagLineFitted.Add(d);
            if (MagLineTable.From(d))
            {
                return; // measured offline on the real meshes, which the in-game count can only approximate
            }
            RigidPose seat = Rig.GunBodyPose.Multiply(d.MagSeat);
            Bounds mb = d.MagBounds;
            var points = new List<Vector3>();
            const int n = 5; // (6 cubed, 17 tilts and 3 depths were some 11 000 volume tests in one frame; 255.P1.5)
            for (int i = 0; i < n; i++)
            {
                for (int j = 0; j < n; j++)
                {
                    for (int k = 0; k < n; k++)
                    {
                        Vector3 q = mb.min + Vector3.Scale(mb.size, new Vector3((i + 0.5f) / n, (j + 0.5f) / n, (k + 0.5f) / n));
                        if (_magVolume.InsideModel(seat.TransformPoint(q)))
                        {
                            points.Add(q);
                        }
                    }
                }
            }
            if (points.Count < 10)
            {
                Log.Info($"{d.Key}: magazine line not fitted (only {points.Count} points of the magazine's drawn shape).");
                return;
            }
            Bounds well = mb;
            well.Expand(MagazineProp.WellPlay);
            Quaternion seatInv = Runtime.RigidPose.Conjugate(d.MagSeat.rotation);
            Vector3 right = Vector3.Cross(d.Up, d.AimAxis).normalized;
            Vector3 in0 = -d.MagEjectAxis.normalized;
            // A handgun's well is its magazine's own drawn shape, not its box (task 270, user 2026-10-18: "mags go through the
            // handle"). The box is wider than the magazine by the rake of its shape (a pistol's 14 degrees), so the grip's walls
            // lay inside it and never counted: the 1911's box is 6.7 cm deep and covers its grip's walls, so the analysed line
            // counted 0 through and nothing was corrected (the log: 0/126, best tilt 0 deg) while the magazine went through the
            // handle. Other guns keep their box.
            bool shapeWell = d.Handgun;
            // And a handgun is measured by its magazine's SKIN, over the whole way in, against the hull of the magazine as it
            // sits (task 270, the user again, 2026-10-18 evening: "for the m1911 and other pistols the mag still goes through
            // the handle"). The grid above is inside the magazine, so its skin pressing into the grip's walls never counted, and
            // the magazine's own depth span has holes (a hollow magazine's core counted as "out of the well"): measured on the
            // 1911's export, 27-37 skin points sat in the grip's walls at every centimetre from 1 to 5 cm out on the analysed
            // line, while the log said "0/117 through". The rest hull: the skin's extremes along 26 directions.
            // Every gun, not only handguns (2026-10-19): the grid's 3-4 stray points of some 300 tilted the M4A1's line 6 degrees
            // and the M4A1 EOT's 3 (and the SpringField 1911's 3 the other way), and tilted, their magazines went in through the
            // gun (user: "this happens to some m4 versions too"); along the analysed line the M4A1 [SQUAD]'s skin passes
            // nothing at any depth, 6 degrees off it does.
            List<Vector3> skin = MagazineSkin(seat);
            // A handgun's magazine is straight: its drawn skin's long axis is its own line. Where the analysis could not read the
            // magazine's meshes (on the graphics card only) the line was a guess, along the gun hand's knuckles (or the box, or the
            // reload's pull), and on the P320s it lay 18 degrees off the magazine: half way out its front stood out of the grip's
            // front face (offline, the export: 115-220 points beside the grip's outline from half way out; along its own axis 5,
            // at the first centimetre only; user, 2026-10-19 night: "mag still glitches through the handle on the p320", task
            // 311). The tilt search below counts only points inside the gun's walls, so a magazine standing out of the grip into
            // the air was never corrected. The skin's axis is taken first, when it is clearly a long shape and clearly off the line.
            // The candidate only here; it is taken below, once both lines are counted, when it is clearly better (offline: the GLOCK
            // 43X 948 points out of place along its analysed line, 1 along its skin's; the P320 587 and 216; but the MWII G17s 16
            // and 52: their analysed line was the better one).
            Vector3 skinLine = Vector3.zero;
            float skinOff = 0f, skinElongation = 0f;
            if (d.Handgun && skin != null && MagazineUnreadable() && WeaponAnalyzer.AxisOf(skin, out Vector3 skinAxis, out skinElongation)
                && skinElongation >= 2.0f)
            {
                // The whole axis, its lean sideways too: the P320's magazine leans 10 degrees across the gun's computed side plane, and
                // offline its own axis beat the same axis put back into that plane (34 points out of place against 164).
                Vector3 axisGun = (d.MagSeat.rotation * skinAxis).normalized;
                if (Vector3.Dot(axisGun, d.MagEjectAxis) < 0f)
                {
                    axisGun = -axisGun;
                }
                skinOff = Vector3.Angle(axisGun, d.MagEjectAxis);
                if (skinOff > 4f && skinOff < 35f)
                {
                    skinLine = axisGun;
                }
            }
            Vector3[] hullDirs = null;
            float[] hullMax = null;
            if (skin != null)
            {
                hullDirs = HullDirections();
                hullMax = new float[hullDirs.Length];
                for (int h = 0; h < hullDirs.Length; h++)
                {
                    float m = float.NegativeInfinity;
                    foreach (Vector3 q in skin)
                    {
                        m = Mathf.Max(m, Vector3.Dot(hullDirs[h], q));
                    }
                    hullMax[h] = m + 0.003f; // (3 mm: the skin's own sampling, not a gap in the grip)
                }
            }
            bool InRestHull(Vector3 q)
            {
                for (int h = 0; h < hullDirs.Length; h++)
                {
                    if (Vector3.Dot(hullDirs[h], q) > hullMax[h])
                    {
                        return false;
                    }
                }
                return true;
            }
            // Which skin points the gun hides with the magazine seated (inside its side silhouette: in the well). Counted only inside
            // the gun's walls, a line along which the magazine stood out of the grip's front or back face into the air was never
            // corrected: the P320's, the M4A1's and HK416's (15-18 degrees back toward the trigger guard), the CZ Bren 2's (user,
            // 2026-10-19 night: "the mag still enters wrongly and leaves wrongly, glitching through the weapon", tasks 310, 311).
            // A hidden point still above the well's mouth on the way in must stay within the side silhouette and between the gun's
            // sides, as tools/export_checks/mag_line_scan.py counts it: offline that count is least at the well's own line (the
            // M4A1 199 points along the analysed line, 4 along the upright one).
            bool[] hidden = null;
            Vector3 inAnalysed = in0; // (the line as analysed, before the skin's axis or a tilt)
            if (skin != null)
            {
                hidden = new bool[skin.Count];
                for (int h = 0; h < skin.Count; h++)
                {
                    hidden[h] = _volume.InSideSilhouette(seat.TransformPoint(skin[h]), out _);
                }
            }
            int Through(Vector3 inGun)
            {
                Vector3 inLocal = (seatInv * inGun).normalized;
                if (skin != null)
                {
                    // The whole way in: the magazine's top travels its own length through the grip (beyond the rail, it is the
                    // hand that brings it up the line), at six depths.
                    float length = Mathf.Abs(Vector3.Dot(mb.size, new Vector3(Mathf.Abs(inLocal.x), Mathf.Abs(inLocal.y), Mathf.Abs(inLocal.z))));
                    int skinHits = 0;
                    // Which points are still in the well is measured along the analysed line for every candidate, as the offline
                    // scan does: measured along each candidate, a steep one let points out of the count sooner and looked cleaner
                    // for it (the QBZ192's offline sweep fell to nothing at 21 degrees either way).
                    Vector3 outLocal = (seatInv * -inAnalysed).normalized;
                    float mouth = float.NegativeInfinity; // the deepest a hidden point reaches out along that line: the well's mouth
                    for (int h = 0; h < skin.Count; h++)
                    {
                        if (hidden[h])
                        {
                            mouth = Mathf.Max(mouth, Vector3.Dot(skin[h], outLocal));
                        }
                    }
                    for (int s = 1; s <= 6; s++)
                    {
                        float back = Mathf.Min(length, 0.15f) * s / 6f;
                        for (int h = 0; h < skin.Count; h++)
                        {
                            Vector3 q = skin[h];
                            Vector3 p = q - inLocal * back;
                            Vector3 w = seat.TransformPoint(p);
                            if (!InRestHull(p) && _volume.InsideModel(w))
                            {
                                skinHits++;
                            }
                            else if (hidden[h] && Vector3.Dot(q, outLocal) + back <= mouth && (!_volume.InSideSilhouette(w, out bool between) || !between))
                            {
                                skinHits++; // still in the well, standing out of the grip's face or through a side
                            }
                        }
                    }
                    return skinHits;
                }
                float half = Mathf.Abs(Vector3.Dot(mb.extents, new Vector3(Mathf.Abs(inLocal.x), Mathf.Abs(inLocal.y), Mathf.Abs(inLocal.z))));
                float rail = Mathf.Clamp(half * 0.9f, 0.03f, 0.09f); // as MagazineProp's rail
                int hits = 0;
                for (int s = 1; s <= 3; s++)
                {
                    float back = rail * (0.25f + 0.375f * (s - 1)); // a quarter, five eighths and the whole rail out
                    foreach (Vector3 q in points)
                    {
                        Vector3 p = q - inLocal * back;
                        bool inWell = shapeWell ? _magVolume.InsideModel(seat.TransformPoint(p)) : Runtime.BoundsMath.Contains(well, p);
                        if (!inWell && _volume.InsideModel(seat.TransformPoint(p)))
                        {
                            hits++;
                        }
                    }
                }
                return hits;
            }
            if (skinLine.sqrMagnitude > 0.5f)
            {
                int alongAnalysed = Through(in0), alongSkin = Through(-skinLine);
                if (alongSkin <= alongAnalysed * 0.6f && alongAnalysed - alongSkin >= 6)
                {
                    Log.Info($"{d.Key}: magazine line taken from its drawn shape's long axis ({skinOff:0} deg off the analysed line, {skinElongation:0.0} times longer than wide; its meshes could not be read): {alongAnalysed} points out of place on the way in, now {alongSkin}.");
                    d.Notes += $"magazine line from its drawn shape ({skinOff:0} deg off) ";
                    d.MagEjectAxis = skinLine;
                    in0 = -skinLine;
                }
            }
            int before = Through(in0), best = before;
            float bestAngle = 0f;
            // Coarse to fine: every 6 degrees out to 24, then 3 degrees either side of the best.
            for (int step = 1; step <= 4; step++)
            {
                for (int sign = -1; sign <= 1; sign += 2)
                {
                    float angle = sign * step * 6f;
                    int hits = Through(Quaternion.AngleAxis(angle, right) * in0);
                    if (hits < best)
                    {
                        best = hits;
                        bestAngle = angle;
                    }
                }
            }
            float coarse = bestAngle;
            for (int sign = -1; sign <= 1; sign += 2)
            {
                float angle = coarse + sign * 3f;
                if (Mathf.Abs(angle) > 24.01f || Mathf.Abs(angle) < 0.01f)
                {
                    continue;
                }
                int hits = Through(Quaternion.AngleAxis(angle, right) * in0);
                if (hits < best)
                {
                    best = hits;
                    bestAngle = angle;
                }
            }
            int total = skin != null ? skin.Count * 6 : points.Count * 3;
            string wellBy = skin != null ? "its skin against its seated hull" : shapeWell ? "against its drawn shape" : "against its box";
            // (Also a small tilt that clears it completely: 2 points through the trigger guard and none at 3 degrees was kept on
            // the six CZ Bren 2 BRs and the Q1911, whose magazines the user saw go in through the gun; 2026-10-17.)
            // By the skin, only a clear fault is corrected (at least 1% of its points through, and 40% fewer after): a few skin
            // points are the sampling, not a wall.
            bool clear = skin != null
                ? before >= Mathf.Max(6, total / 100) && best <= before * 0.6f
                : best <= before * 0.6f && (before - best >= 3 || best == 0 && before >= 2 && Mathf.Abs(bestAngle) <= 6.01f);
            if (bestAngle != 0f && clear)
            {
                d.MagEjectAxis = -(Quaternion.AngleAxis(bestAngle, right) * in0).normalized;
                d.Notes += $"magazine line fitted to the drawn gun ({bestAngle:0} deg) ";
                Log.Info($"{d.Key}: magazine line fitted to the drawn gun, turned {bestAngle:0} deg in its side plane: {before}/{total} magazine points went through the gun on the way in, now {best} ({wellBy}).");
            }
            else
            {
                Log.Info($"{d.Key}: magazine line kept ({before}/{total} magazine points through the gun on the way in, {wellBy}; the best tilt, {bestAngle:0} deg, {best}).");
            }
        }

        /// <summary>A magazine part whose meshes the game keeps only on the graphics card (the analysis could not read its shape).</summary>
        private bool MagazineUnreadable()
        {
            foreach (Transform part in Rig.MagazineParts)
            {
                if (part == null)
                {
                    continue;
                }
                foreach (Renderer r in part.GetComponentsInChildren<Renderer>(true))
                {
                    Mesh m = r is SkinnedMeshRenderer smr ? smr.sharedMesh : r.GetComponent<MeshFilter>()?.sharedMesh;
                    if (m != null && !m.isReadable)
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        /// <summary>
        /// The seated magazine's drawn surface (its measured volume's surface points, as the views saw it), in the seat's frame,
        /// at most 240 of them spread over it; null when there are too few to judge by (task 270).
        /// </summary>
        private List<Vector3> MagazineSkin(RigidPose seat)
        {
            if (_magFrame == null || _magVolume == null || !_magVolume.Ready)
            {
                return null;
            }
            List<Vector3> all = _magVolume.SurfacePoints(2);
            if (all.Count < 40)
            {
                return null;
            }
            var frame = new RigidPose(_magFrame.position, _magFrame.rotation); // (the volume is posed there, UpdateSeatedMagSolid)
            int step = Mathf.Max(1, all.Count / 240);
            var skin = new List<Vector3>(all.Count / step + 1);
            for (int i = 0; i < all.Count; i += step)
            {
                skin.Add(seat.InverseTransformPoint(frame.TransformPoint(all[i])));
            }
            return skin;
        }

        /// <summary>26 directions (a cube's faces, edges and corners) for a convex hull by its extremes.</summary>
        private static Vector3[] HullDirections()
        {
            var dirs = new List<Vector3>(26);
            for (int x = -1; x <= 1; x++)
            {
                for (int y = -1; y <= 1; y++)
                {
                    for (int z = -1; z <= 1; z++)
                    {
                        if (x != 0 || y != 0 || z != 0)
                        {
                            dirs.Add(new Vector3(x, y, z).normalized);
                        }
                    }
                }
            }
            return dirs.ToArray();
        }

        /// <summary>Per weapon type: how much of its slide is seen from outside at rest (-1: not measurable).</summary>
        private static readonly Dictionary<string, float> SlideSeen = new Dictionary<string, float>();
        /// <summary>Weapon types whose slide is being measured now: its pictures come back from the GPU a frame or two later.</summary>
        private static readonly HashSet<string> SlideMeasuring = new HashSet<string>();
        /// <summary>
        /// Drops what was measured by weapon name before a content reload (2026-10-19): the slide's visible share and the
        /// render-or-fit choice are measured again on the reloaded weapon's next hold.
        /// </summary>
        internal static void ClearNameCaches()
        {
            SlideSeen.Clear();
            SlideMeasuring.Clear();
            RenderedShapeBetter.Clear();
        }
        private bool _knobChecked;
        /// <summary>Set by <see cref="Release"/>: a measurement that comes back after it adds nothing to this hold.</summary>
        private bool _releasedHold;
        /// <summary>A knob was added on a slide hidden in the receiver: the racking hand closes on the knob, not the slide.</summary>
        private bool _knobOnHidden;

        /// <summary>
        /// A long gun or pistol whose slide (found by its fire motion) moves hidden in the receiver, with nothing else of it moving
        /// with the hand, gets a slim latch (BoltPiece's T; it was a cube, task 299) where the hand takes it, as a bolt baked into
        /// the receiver does: GREASER's bolt was 0% seen and PPSh-41's 3% (check-1017f), and racked, nothing visible moved.
        /// Measured once per weapon type, from four sides with and without the slide (a few small pictures). A slide seen at
        /// least 20% from outside gets nothing: the hand takes the slide itself.
        /// </summary>
        private void KnobOnHiddenSlide()
        {
            WeaponRigData d = Rig.Data;
            if (Rig.Slide == null || _boltPiece != null || d.HandlePath != null || d.CarveHandle || d.AddedHandle
                || Rig.SlideCompanions.Length > 0 || d.SlideMethod == null || !d.SlideMethod.StartsWith("fire motion", System.StringComparison.Ordinal))
            {
                return;
            }
            if (!SlideSeen.TryGetValue(d.Key, out float seen))
            {
                var mine = new List<Renderer>();
                foreach (Renderer r in Rig.Slide.GetComponentsInChildren<Renderer>(false))
                {
                    if (r.enabled && (r is MeshRenderer || r is SkinnedMeshRenderer))
                    {
                        mine.Add(r);
                    }
                }
                if (mine.Count == 0)
                {
                    return; // a bone of the gun's skinned mesh: not measured this way
                }
                var arms = new HashSet<Renderer>(Rig.ArmRenderers);
                var gun = new List<Renderer>();
                foreach (Renderer r in Weapon.GetComponentsInChildren<Renderer>(false))
                {
                    if (r.enabled && !arms.Contains(r) && (r is MeshRenderer || r is SkinnedMeshRenderer))
                    {
                        gun.Add(r);
                    }
                }
                // Measured with the pictures read back as the GPU finishes them, a frame or two later (task 263, 2026-10-18:
                // read back at once, the first hold of each such gun type stalled on 12 pictures and made a few MB of garbage).
                // The knob comes when they are back, on this same hold.
                if (!SlideMeasuring.Add(d.Key))
                {
                    return; // (being measured for another hold of this type: its result serves the next hold)
                }
                string key = d.Key, slideName = Rig.Slide.name;
                try
                {
                    // Its views a frame apart, each with the gun as it is then; given up (NaN) once the gun is let go, or the slide
                    // leaves home or a reload starts in between (measured with the slide home only, as above).
                    WeaponRig rig = Rig;
                    (Vector3, Vector3)? AxesNow() => !_releasedHold && rig.GunBody != null && SlideOffset < 0.002f && !Weapon.reloading
                        ? (rig.AimWorld, rig.DirToWorld(d.Up))
                        : ((Vector3, Vector3)?)null;
                    GunVolume.VisibleShareAsync(gun, mine, Rig.AimWorld, Rig.DirToWorld(d.Up), (share, views) =>
                    {
                        SlideMeasuring.Remove(key);
                        if (float.IsNaN(share))
                        {
                            return; // given up between its views: measured again on a later hold
                        }
                        SlideSeen[key] = share;
                        Log.Info($"{key}: its slide '{slideName}' is {(share < 0f ? "not measurable" : $"{share * 100f:0}% seen from outside ({views})")}.");
                        if (!_releasedHold && Rig != null && Rig.Data.Key == key)
                        {
                            AddKnobOnHiddenSlide(share);
                        }
                    }, 0.004f, AxesNow);
                }
                catch (System.Exception e)
                {
                    SlideMeasuring.Remove(key); // (its callback never comes: measured again on the next hold)
                    Log.WarnOnce("slideseen:" + key, $"Could not measure how much of {key}'s slide is seen: {e.Message}");
                }
                return;
            }
            AddKnobOnHiddenSlide(seen);
        }

        /// <summary>
        /// The knob on a slide seen less than 20% from outside (see <see cref="KnobOnHiddenSlide"/>). Was 5%: the GREASER's bolt, 18%
        /// seen only through its ejection port, got none, and the hand closed round the receiver tube with no finger on the bolt
        /// (0/4 in the check): the user's "the pulling back doesnt even work" (2026-11-14).
        /// </summary>
        private void AddKnobOnHiddenSlide(float seen)
        {
            WeaponRigData d = Rig.Data;
            if (seen < 0f || seen >= 0.2f || _boltPiece != null)
            {
                return;
            }
            try
            {
                _boltPiece = BoltPiece.CreateKnob(Rig, RackPointGun);
                _knobOnHidden = true;
                _rackSolid = null;
                Log.InfoOnce("hiddenknob:" + d.Key, $"{d.Key}: its slide moves hidden in the receiver: a knob added where the hand takes it.");
            }
            catch (System.Exception e)
            {
                _boltPiece = null;
                Log.WarnOnce("hiddenknob:" + d.Key, $"Could not add a knob for {d.Key}: {e.Message}");
            }
        }

        /// <summary>
        /// An estimated rack point (from the reload hand, or virtual) can lie inside the receiver or float beside it: it is
        /// moved straight out from the bore onto the drawn model's surface, where a hand can take it and a handle can be cut
        /// out or added. A handle cut at the old point is cut again at the new one. A stroke of the reload hand that starts
        /// well off the model held no handle (an AS Val's left hand passed 10 cm over the front sight): the estimate above
        /// the grip is used instead.
        /// </summary>
        private void SnapRackPoint()
        {
            WeaponRigData d = Rig.Data;
            if (!d.RackOnSurface || _volume == null || !_volume.Ready)
            {
                return;
            }
            Vector3 p = d.RackPoint;
            float off = ToSurface(p, out Vector3 snapped);
            string how = off > 0f ? "onto the receiver" : "out of the receiver";
            if (d.RackFromHand && off > 0.05f)
            {
                p = WeaponAnalyzer.RackEstimate(d);
                ToSurface(p, out snapped);
                how = (float.IsPositiveInfinity(off) ? "the reload hand's stroke started where no model lies toward the bore"
                          : $"the reload hand's stroke started {off * 100f:0.0} cm off the model") + ", no handle there: the estimate above the grip instead";
            }
            float moved = Vector3.Distance(snapped, d.RackPoint);
            if (moved < 0.003f)
            {
                return;
            }
            RackPointGun = snapped;
            SnappedRackPoints.Put(d, snapped); // (the warm-up cuts the handle here next time: BoltPiece.Prewarm)
            Log.InfoOnce("racksnap:" + d.Key, $"{d.Key}: rack point put on the model's surface ({moved * 100f:0.0} cm, {how}).");
            if (_boltPiece != null)
            {
                _boltPiece.Destroy();
                try
                {
                    _boltPiece = BoltPiece.Create(Rig, RackPointGun);
                }
                catch (System.Exception e)
                {
                    _boltPiece = null;
                    Log.WarnOnce("boltpiece2:" + d.Key, $"Could not build a moving handle for {d.Key}: {e.Message}");
                }
            }
        }

        /// <summary>
        /// <paramref name="p"/> (gun space) moved straight out from the bore onto the model's surface. Returns how far
        /// outside the model it was (0 when it was inside and is moved out; infinity when no model lies between it and the bore).
        /// </summary>
        private float ToSurface(Vector3 p, out Vector3 snapped)
        {
            WeaponRigData d = Rig.Data;
            Vector3 bore = d.MuzzlePoint + d.AimAxis * Vector3.Dot(p - d.MuzzlePoint, d.AimAxis);
            Vector3 outward = p - bore;
            outward = outward.sqrMagnitude > 1e-6f ? outward.normalized : d.Up;
            bool Inside(Vector3 gun) => _volume.InsideModel(Rig.ToWorld(gun));
            snapped = p;
            if (Inside(p))
            {
                for (float s = 0.002f; s <= 0.08f; s += 0.002f)
                {
                    if (!Inside(p + outward * s))
                    {
                        snapped = p + outward * s;
                        break;
                    }
                }
                return 0f;
            }
            // In toward the bore until the model: a reload hand taken over the top put the point 8 to 15 cm above the
            // receiver (an MK46's), past a search of 6 cm, and the racking hand hung in the air there.
            float reach = Mathf.Min(0.2f, (p - bore).magnitude);
            for (float s = 0.002f; s <= reach; s += 0.002f)
            {
                if (Inside(p - outward * s))
                {
                    snapped = p - outward * (s - 0.002f);
                    return s - 0.002f;
                }
            }
            return float.PositiveInfinity; // nothing toward the bore: off the model however near it is
        }

        private static readonly System.Text.RegularExpressions.Regex LaserPart =
            new System.Text.RegularExpressions.Regex(@"laser|beam", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        /// <summary>
        /// Self-test: where everything drawn on the weapon is, in gun space: the middle of each mesh drawn (not the
        /// weapon's own arms) and each bone a drawn skinned mesh hangs on. Two of these, before and after a pull, show
        /// what visibly moved.
        /// </summary>
        internal Dictionary<Object, Vector3> DrawnLayout()
        {
            var at = new Dictionary<Object, Vector3>();
            if (Rig.GunBody == null || Weapon == null)
            {
                return at;
            }
            var arms = new HashSet<Renderer>(Rig.ArmRenderers);
            // (Only what the headset's camera draws: the ARS guns carry their third-person body inside the first-person gun on a
            // layer the view leaves out, and its bones moving read as "THE SLIDE RAN FORWARD ON ITS OWN" on the MP9; 2026-11-14.)
            Camera eye = _system != null && _system.Rig != null ? _system.Rig.Camera : null;
            int seen = eye != null ? eye.cullingMask : ~0;
            foreach (Renderer r in Weapon.GetComponentsInChildren<Renderer>(false))
            {
                if (!r.enabled || r.forceRenderingOff || arms.Contains(r) || r.transform.lossyScale.sqrMagnitude < 1e-6f || (seen & (1 << r.gameObject.layer)) == 0)
                {
                    continue;
                }
                if (LaserPart.IsMatch(r.name))
                {
                    continue; // a laser's dot and beam go where the beam hits, not with the gun (check-1017h: the QBZ192-C's "slide" 190 mm back)
                }
                if (_sightParts != null && System.Array.Exists(_sightParts, s => s != null && r.transform.IsChildOf(s)))
                {
                    continue; // a part the sight mode moves (the CZ Bren 2 BR Raid's 'Red Dot Sight' read as its slide 93 mm back; check-1018e)
                }
                if (r is MeshRenderer)
                {
                    at[r] = Rig.ToGun(r.bounds.center);
                }
                else if (r is SkinnedMeshRenderer smr && smr.bones != null)
                {
                    bool[] weighted = WeightedBones(smr.sharedMesh, smr.bones.Length);
                    for (int bi = 0; bi < smr.bones.Length; bi++)
                    {
                        Transform b = smr.bones[bi];
                        if (b != null && (weighted == null || weighted[bi]) && !at.ContainsKey(b))
                        {
                            at[b] = Rig.ToGun(b.position);
                        }
                    }
                }
            }
            return at;
        }

        private static readonly Dictionary<Mesh, bool[]> WeightedBoneCache = new Dictionary<Mesh, bool[]>();

        /// <summary>
        /// Which of a skinned mesh's bones move any vertex (null: not readable, all count). A bone nothing hangs on is not drawn:
        /// the ARS MP9's 'Body_TP' moved 16 mm at a fresh magazine and 572 mm at a half rack and read as "THE SLIDE RAN FORWARD
        /// ON ITS OWN" (2026-11-14).
        /// </summary>
        private static bool[] WeightedBones(Mesh mesh, int count)
        {
            if (mesh == null || !mesh.isReadable)
            {
                return null;
            }
            mesh = WholeMesh(mesh);
            if (WeightedBoneCache.TryGetValue(mesh, out bool[] known) && known.Length == count)
            {
                return known;
            }
            var used = new bool[count];
            foreach (BoneWeight w in mesh.boneWeights)
            {
                if (w.weight0 > 0f && w.boneIndex0 >= 0 && w.boneIndex0 < count) used[w.boneIndex0] = true;
                if (w.weight1 > 0f && w.boneIndex1 >= 0 && w.boneIndex1 < count) used[w.boneIndex1] = true;
                if (w.weight2 > 0f && w.boneIndex2 >= 0 && w.boneIndex2 < count) used[w.boneIndex2] = true;
                if (w.weight3 > 0f && w.boneIndex3 >= 0 && w.boneIndex3 < count) used[w.boneIndex3] = true;
            }
            WeightedBoneCache[mesh] = used;
            return used;
        }

        /// <summary>
        /// Self-test (Task 232): with the magazine out, the gun's skinned meshes as drawn now, and their vertices that lie in
        /// the seated magazine's box (grown 1 cm): how many, by the bone that carries most of each. The leftover magazine
        /// the ARS MP9 / HK416 still draw is among them. A bone the mod hid (scaled to nothing) folds its vertices onto its
        /// pivot: counted apart. Empty when nothing is drawn there.
        /// </summary>
        internal string SkinnedInMagazineSpace()
        {
            if (Rig.GunBody == null || Weapon == null)
            {
                return "";
            }
            RigidPose seat = Rig.GunBodyPose.Multiply(Rig.Data.MagSeat);
            Bounds box = Rig.Data.MagBounds;
            box.Expand(0.02f);
            var arms = new HashSet<Renderer>(Rig.ArmRenderers);
            var shown = new Dictionary<string, int>();
            int folded = 0;
            foreach (SkinnedMeshRenderer smr in Weapon.GetComponentsInChildren<SkinnedMeshRenderer>(false))
            {
                if (!smr.enabled || arms.Contains(smr) || smr.sharedMesh == null || !smr.sharedMesh.isReadable)
                {
                    continue;
                }
                var baked = new Mesh();
                try
                {
                    smr.BakeMesh(baked); // with the renderer's scale: only its position and rotation are left to apply
                    Vector3[] v = baked.vertices;
                    // (Only vertices a triangle draws: with the magazine out a copy without it is drawn, its vertices all kept.)
                    var drawn = new bool[v.Length];
                    for (int sub = 0; sub < smr.sharedMesh.subMeshCount; sub++)
                    {
                        foreach (int ti in smr.sharedMesh.GetTriangles(sub))
                        {
                            if (ti >= 0 && ti < drawn.Length)
                            {
                                drawn[ti] = true;
                            }
                        }
                    }
                    BoneWeight[] w = smr.sharedMesh.boneWeights;
                    Transform[] bones = smr.bones;
                    Vector3 at = smr.transform.position;
                    Quaternion rot = smr.transform.rotation;
                    for (int i = 0; i < v.Length; i++)
                    {
                        if (!drawn[i] || !Runtime.BoundsMath.Contains(box, seat.InverseTransformPoint(at + rot * v[i])))
                        {
                            continue;
                        }
                        Transform bone = i < w.Length && w[i].boneIndex0 >= 0 && w[i].boneIndex0 < bones.Length ? bones[w[i].boneIndex0] : null;
                        if (bone != null && bone.lossyScale.sqrMagnitude < 1e-6f)
                        {
                            folded++;
                            continue;
                        }
                        string key = $"'{smr.name}' bone '{(bone != null ? bone.name : "?")}'";
                        shown[key] = shown.TryGetValue(key, out int c) ? c + 1 : 1;
                    }
                }
                finally
                {
                    Object.Destroy(baked);
                }
            }
            if (shown.Count == 0)
            {
                return folded > 0 ? $"nothing drawn ({folded} vertices of hidden bones folded there)" : "";
            }
            var list = new List<KeyValuePair<string, int>>(shown);
            list.Sort((a, b) => b.Value.CompareTo(a.Value));
            var parts = new List<string>();
            for (int i = 0; i < list.Count && i < 4; i++)
            {
                parts.Add($"{list[i].Value} vertices of {list[i].Key}");
            }
            return string.Join(", ", parts.ToArray()) + (folded > 0 ? $" ({folded} of hidden bones folded there)" : "");
        }

        /// <summary>Self-test: what the hand sees move when it works the action.</summary>
        internal string VisibleHandle =>
            Rig.Handle != null ? "the charging handle"
            : _boltPiece != null && _boltPiece.CarrierOnly && Rig.Slide != null ? "the gun's own carrier with its handle (nothing cut out, no knob added)"
            : _boltPiece != null && _boltPiece.ShownPieces == 0 ? (Rig.Slide != null ? "the carrier inside only (THE HANDLE IS NOT SHOWN)" : "NOTHING VISIBLE (the handle's pieces are gone)")
            : _boltPiece != null && _boltPiece.Carved ? "a piece cut out of the receiver" + (Rig.Slide != null ? " (and the carrier inside)" : "")
            : _boltPiece != null ? "an added handle" + (Rig.Slide != null ? " (and the carrier inside)" : "")
            : Rig.Slide != null ? "the slide / bolt"
            : "NOTHING VISIBLE";

        /// <summary>Self-test: how long choosing the physics shape took on the main thread (-1 when no choice was made).</summary>
        internal float ShapeChoiceMs { get; private set; } = -1f;

        /// <summary>Per weapon type: the shape fitted to the rendered model matched it better than the one from the meshes.</summary>
        private static readonly Dictionary<string, bool> RenderedShapeBetter = new Dictionary<string, bool>();

        /// <summary>
        /// The physics stand-in becomes the shape that matches the visible model best. Unreadable meshes only gave boxes:
        /// the rendered-model fit replaces them. Readable meshes are compared once per weapon type (share of the model
        /// covered minus share of the shape outside it) and the better fit is kept.
        /// </summary>
        private void ChooseShape()
        {
            string key = Rig.Data.Key;
            if (_volume.Unreadable)
            {
                _gunCollider.UseShape(_volume.HullPieces, _volume);
                return;
            }
            if (RenderedShapeBetter.TryGetValue(key, out bool known))
            {
                if (known)
                {
                    _gunCollider.UseShape(_volume.HullPieces, null);
                }
                return;
            }
            ShapeFit.Piece[] meshes = _gunCollider.Pieces;
            var watch = System.Diagnostics.Stopwatch.StartNew();
            // Coarse sampling (4³ per piece, ~150 surface points): enough to tell the fits apart without a hitch.
            string before = _gunCollider.Match(_volume, 4, 150, out float excess0, out float covered0);
            _gunCollider.UseShape(_volume.HullPieces, null);
            string after = _gunCollider.Match(_volume, 4, 150, out float excess1, out float covered1);
            ShapeChoiceMs = (float)watch.Elapsed.TotalMilliseconds;
            bool better = covered1 - excess1 > covered0 - excess0 + 5f; // a clear margin, not noise
            RenderedShapeBetter[key] = better;
            if (better)
            {
                _gunCollider.ShapeBefore = before;
                _gunCollider.ShapeBeforeExcess = excess0;
                _gunCollider.ShapeBeforeCovered = covered0;
            }
            else
            {
                _gunCollider.UseShape(meshes, null);
                _gunCollider.ShapeRejected = after;
            }
            Log.Info($"{key}: physics shape from the {(better ? "rendered model" : "meshes")} (meshes: {before}; rendered model: {after}).");
        }

        /// <summary>
        /// Cooks the hulls of a rendered-model shape for about <paramref name="budgetMs"/> on the main thread, the rest on the
        /// next calls: PhysX cooks each piece here, and the sixteen of a gun in one frame were a spike on its first hold (task
        /// 263, 2026-10-18). The same checks as <see cref="ShapeFit.Bake"/>: a piece PhysX cannot cook is marked and becomes a
        /// box. The first piece of a call is always cooked, so every call makes progress. True once no piece is left.
        /// </summary>
        private static bool CookHullWithin(ShapeFit.Piece[] pieces, float budgetMs)
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            int cooked = 0;
            foreach (ShapeFit.Piece p in pieces)
            {
                if (p.Points.Length < 4 || p.CookFailed || p.Baked)
                {
                    continue;
                }
                if (cooked > 0 && watch.Elapsed.TotalMilliseconds >= budgetMs)
                {
                    return false;
                }
                cooked++;
                try
                {
                    Physics.BakeMesh(p.Mesh.GetInstanceID(), true);
                    p.Baked = true;
                }
                catch (System.Exception)
                {
                    p.CookFailed = true;
                }
            }
            return true;
        }

        private GunVolume _magVolume;
        private bool _magVolumeTried, _magSolidShown;
        private float _magVolumeRetryAt, _partRetryAt; // 0: no retry pending
        private int _magVolumeTries, _partTries;
        private Transform _magFrame;

        /// <summary>
        /// The seated magazine is measured on its own (it is a prop of its own once out, so it is not part of the gun's
        /// solid): while it is in the gun and shown, the hands close on it too (a magazine-held gun, a hand on the well).
        /// </summary>
        private void UpdateSeatedMagSolid()
        {
            _magSolidShown = false;
            if (_magFrame == null)
            {
                foreach (Transform part in Rig.MagazineParts)
                {
                    if (part != null)
                    {
                        _magFrame = part;
                        break;
                    }
                }
                if (_magFrame == null)
                {
                    return;
                }
            }
            if (_magHidden || !_magFrame.gameObject.activeInHierarchy)
            {
                return;
            }
            if (_magVolume != null && (_magVolume.Failed || _magVolume.Stalled) && _magVolumeTries < 3)
            {
                // A failed measure (a read-back error, a renderer gone mid-way) is tried again a second later, three times at
                // most: latched, the seated magazine had no solid and the line fit never ran for the rest of the hold (review,
                // 2026-10-20); retried with no wait and no limit, a measure that kept failing rendered and read back its views
                // every second of the hold (bug hunt, 2026-10-23), as HandSolid's own volume is limited.
                if (_magVolumeRetryAt <= 0f)
                {
                    _magVolumeRetryAt = Time.unscaledTime + 1f;
                }
                else if (Time.unscaledTime >= _magVolumeRetryAt)
                {
                    _magVolume = null;
                    _magVolumeTried = false;
                    _magVolumeRetryAt = 0f;
                }
            }
            if (_magVolume == null && !_magVolumeTried && !Weapon.reloading) // (a reload parks the part, shrunk: measured so it stood for the type's seated magazine)
            {
                _magVolumeTried = true;
                _magVolumeTries++;
                var renderers = new List<Renderer>();
                foreach (Transform part in Rig.MagazineParts)
                {
                    if (part != null)
                    {
                        foreach (Renderer r in part.GetComponentsInChildren<Renderer>(false))
                        {
                            if (r.enabled && (r is MeshRenderer || r is SkinnedMeshRenderer))
                            {
                                renderers.Add(r);
                            }
                        }
                    }
                }
                _magVolume = GunVolume.Measure("seated:" + Rig.Data.Key, renderers, _magFrame, 3);
            }
            if (_magVolume != null && _magVolume.Ready)
            {
                _magVolume.SetPose(_magFrame.position, _magFrame.rotation);
                _magSolidShown = true;
            }
        }

        /// <summary>Self-test: inside the gun or its seated magazine (not grown).</summary>
        private bool InsideModel(Vector3 p) => _volume.InsideModel(p) || _magSolidShown && _magVolume.InsideModel(p);

        /// <summary>Self-test: the weapon's own arms (arm-IK mode), or null.</summary>
        internal ArmIK Arms => _armIK;

        /// <summary>The weapon's own arms are shown as the hands (the arm-IK mode), not hidden.</summary>
        internal bool ArmsShownByMod => _armIK != null && _armIK.Active;

        /// <summary>Self-test: the measured solid (null until measured).</summary>
        internal GunVolume Volume => _volume;

        private GunVolume _partVolume;
        private bool _partTried;
        private System.Func<Vector3, bool> _partSolid;

        /// <summary>The part a racking hand holds: a separate charging handle, else the slide, bolt or pump.</summary>
        private Transform WorkedPart => Rig.Data.HandlePath != null && Rig.Handle != null ? Rig.Handle : Rig.Slide;

        /// <summary>
        /// The hand working the action (racking a slide, pulling a bolt or charging handle, pumping) closes on the part it
        /// holds, measured on its own the first time it is taken and moving with it.
        /// </summary>
        private System.Func<Vector3, bool> PartSolid()
        {
            Transform part = WorkedPart;
            if (part == null)
            {
                return null;
            }
            if (_partVolume != null && (_partVolume.Failed || _partVolume.Stalled) && _partTries < 3)
            {
                // Failed: tried again a second later (the racking hand closed on nothing for the rest of the hold), three times
                // at most (see the seated magazine's volume).
                if (_partRetryAt <= 0f)
                {
                    _partRetryAt = Time.unscaledTime + 1f;
                }
                else if (Time.unscaledTime >= _partRetryAt)
                {
                    _partVolume = null;
                    _partSolid = null;
                    _partTried = false;
                    _partRetryAt = 0f;
                }
            }
            if (_partVolume == null && !_partTried)
            {
                _partTried = true;
                _partTries++;
                var renderers = new List<Renderer>();
                foreach (Renderer r in part.GetComponentsInChildren<Renderer>(false))
                {
                    if (r.enabled && (r is MeshRenderer || r is SkinnedMeshRenderer) && !GunCollider.AddedByMod(r.transform, Rig.Weapon.transform))
                    {
                        renderers.Add(r);
                    }
                }
                if (part == Rig.Slide)
                {
                    // The slide's companions (the Vector's knob beside its bolt) move with it: the hand closes on them too.
                    foreach (Transform c in Rig.SlideCompanions)
                    {
                        if (c != null)
                        {
                            foreach (Renderer r in c.GetComponentsInChildren<Renderer>(false))
                            {
                                if (r.enabled && (r is MeshRenderer || r is SkinnedMeshRenderer))
                                {
                                    renderers.Add(r);
                                }
                            }
                        }
                    }
                }
                _partVolume = GunVolume.Measure("part:" + Rig.Data.Key + ":" + part.name, renderers, part, 3);
            }
            if (_partVolume == null || !_partVolume.Ready)
            {
                return null;
            }
            _partVolume.SetPose(part.position, part.rotation);
            return _partSolid ?? (_partSolid = _partVolume.Solid);
        }

        private System.Func<Vector3, bool> _rackSolid, _drawnHandleSolid;
        private Vector3 _drawnHandleAt, _drawnHandleAcross;

        /// <summary>On the drawn handle: a bar 4 cm across the gun, 9 mm round (grown by a finger's radius like the solids).</summary>
        private bool OnDrawnHandle(Vector3 p)
        {
            Vector3 v = p - _drawnHandleAt;
            float t = Mathf.Clamp(Vector3.Dot(v, _drawnHandleAcross), -0.02f, 0.02f);
            float r = 0.009f + GunVolume.Grow;
            return (v - _drawnHandleAcross * t).sqrMagnitude < r * r;
        }
        private Vector3 _partRestPos;
        private Quaternion _partRestInv;
        private HandModel _rackGunHand;

        /// <summary>
        /// What a hand working the action closes on: the part where it is now, the rest of the gun round it (the receiver
        /// under a bolt knob, the stock behind a charging handle, the frame under a slide: the gun's solid with the part's
        /// rest place taken out, since the part has moved), and the gun hand's fingers. On the part alone the fingers went
        /// on into the receiver: 16 points of a hand over an AK's top, 15 of one on an AR's charging handle from above
        /// (tools/hands/grip_check.py). The part alone when the gun is not measured.
        /// </summary>
        private System.Func<Vector3, bool> RackSolid(VRHand main)
        {
            System.Func<Vector3, bool> part = PartSolid();
            System.Func<Vector3, bool> gun = HandSolid();
            // (Also a knob cut out or added on a moving part with no handle of its own, which the hand takes and not the part: the
            // CZ Bren 2's knob, 10 cm ahead of the bolt inside the receiver, was in no solid, and the fingers closed on the bolt's
            // place inside the gun; Bren hunt, 2026-10-19, task 302.)
            bool madeKnob = (Rig.Data.CarveHandle || Rig.Data.AddedHandle) && Rig.Handle == null;
            if ((part == null || _knobOnHidden || madeKnob) && gun != null && _boltPiece != null)
            {
                // A handle the mod draws (cut out of the receiver, or a knob added): no part of the model to measure, so
                // the hand closed on nothing and curled through it. A bar across the gun where the handle is on its stroke.
                WeaponRigData dh = Rig.Data;
                Vector3 dir = dh.SlideTravel.sqrMagnitude > 1e-6f ? dh.SlideTravel.normalized : -dh.AimAxis;
                _drawnHandleAt = Rig.ToWorld(RackPointGun + dir * SlideOffset);
                _drawnHandleAcross = Rig.DirToWorld(Vector3.Cross(dh.Up, dh.AimAxis).normalized);
                _rackGunHand = main.Model != null && main.Role == HandRole.Weapon ? main.Model : null;
                return _drawnHandleSolid ?? (_drawnHandleSolid = p => OnDrawnHandle(p) || ClosedGunSolid(p)
                                                                     || _rackGunHand != null && _rackGunHand.NearSkin(p, GunVolume.Grow));
            }
            if (part == null || gun == null)
            {
                return part;
            }
            WeaponRigData d = Rig.Data;
            Transform worked = WorkedPart;
            Vector3 travel = Vector3.zero;
            if (SlideOffset > 0.0005f)
            {
                float fraction = SlideOffset / Mathf.Max(d.SlideTravel.magnitude, 1e-4f);
                travel = worked == Rig.Handle ? d.HandleTravel * Mathf.Clamp01(fraction) : d.SlideTravel * fraction;
            }
            _partRestPos = worked.position - Rig.DirToWorld(travel);
            _partRestInv = Runtime.RigidPose.Conjugate(worked.rotation);
            _rackGunHand = main.Model != null && main.Role == HandRole.Weapon ? main.Model : null;
            return _rackSolid ?? (_rackSolid = p => _partSolid(p)
                                                     || ClosedGunSolid(p) && !_partVolume.SolidAt(p, _partRestPos, _partRestInv)
                                                     || _rackGunHand != null && _rackGunHand.NearSkin(p, GunVolume.Grow));
        }

        /// <summary>Self-test: the worked part's shape has been measured (or there is none to measure).</summary>
        internal bool PartShapeReady => WorkedPart == null || _partTried && (_partVolume == null || _partVolume.Ready);

        /// <summary>Self-test: how much of a hand working the action is buried in the part it holds.</summary>
        internal string PartClipping(VRHand hand, out int buried, out int touching)
        {
            buried = touching = -1;
            if (_partTried && _partVolume == null)
            {
                return "not measured (the part draws no mesh of its own: a bone of the gun's skinned mesh, or built by the mod)";
            }
            if (_partVolume == null || !_partVolume.Ready || hand.Model == null)
            {
                return "not measured (the part's shape is not ready)";
            }
            hand.Model.Measure(_partVolume.InsideModel, _partVolume.Solid, out buried, out int samples, out touching);
            return $"{buried}/{samples} points inside the part, {touching}/4 fingers on it";
        }

        /// <summary>Self-test: how much of a hand on this weapon is buried in its model, and how many fingers touch it.</summary>
        internal string HandClipping(VRHand hand) => HandClipping(hand, out _, out _, out _);

        internal string HandClipping(VRHand hand, out int buried, out int samples, out int touching)
        {
            buried = samples = touching = -1;
            if (_volume == null || !_volume.Ready || hand.Model == null)
            {
                return "not measured";
            }
            hand.Model.Measure(InsideModel, _solid ?? _volume.Solid, out buried, out samples, out touching);
            return $"{buried}/{samples} points inside the gun, {touching}/4 fingers on it";
        }
    }
}
