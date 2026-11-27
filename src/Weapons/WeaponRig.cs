using System.Collections.Generic;
using System.Text;
using RavenfieldVR.Runtime;
using UnityEngine;

namespace RavenfieldVR.Weapons
{
    /// <summary>How a weapon is fed and cycled, which decides the manual reload interactions.</summary>
    public enum ActionType
    {
        /// <summary>Detachable magazine, self-loading (pistols, rifles, SMGs, LMGs).</summary>
        SelfLoading,
        /// <summary>Detachable or internal magazine cycled by hand after every shot (bolt, pump, lever).</summary>
        Manual,
        /// <summary>Rounds/shells pushed in individually (tube shotguns, stripper-clip rifles).</summary>
        ShellFed,
        /// <summary>One round at a time loaded into the breech or muzzle (launchers).</summary>
        SingleShot,
        /// <summary>
        /// Opens to load and has no slide: break-action barrels or a revolver cylinder. Rounds go straight into the
        /// chambers while it is open; it fires only when closed.
        /// </summary>
        BreakOpen,
        Throwable,
        Melee,
        /// <summary>Infinite ammo or tools without ammo; no reload interaction.</summary>
        NoAmmo
    }

    /// <summary>
    /// The support hand on the magazine as the weapon's own reload animation holds it (WeaponAnalyzer.MagHoldOf): the hand bone's
    /// pose in the magazine part's frame (the frame the magazine in the VR hand is drawn in: MagSeat) and its fingers' local turns.
    /// </summary>
    public sealed class MagHoldPose
    {
        public RigidPose HandInMag;
        /// <summary>The finger bones under the hand bone (paths from it) and their local rotations there.</summary>
        public int[][] FingerPaths;
        public Quaternion[] FingerLocal;
        /// <summary>The reload step it was taken at and how far (m) the hand bone was from the magazine part then.</summary>
        public int Step;
        public float Distance;
    }

    /// <summary>One first-person arm of the weapon's rig, for IK: bones and the hand's grasp geometry.</summary>
    public sealed class ArmChain
    {
        public int[] Root;
        public int[] Upper;
        public int[] Lower;
        public int[] Hand;
        /// <summary>Hand-bone space: toward the fingers, back of the hand, and the middle of the palm.</summary>
        public Vector3 FingerDir;
        public Vector3 BackDir;
        public Vector3 PalmPoint;
    }

    /// <summary>One of a weapon's sights: where the eye goes and which way it looks (gun-body space).</summary>
    public sealed class SightLine
    {
        public Vector3 EyePoint;
        public Vector3 EyeDirection = Vector3.forward;
        /// <summary>The sight was lined up in this mode's own aim pose (else it copies the default line).</summary>
        public bool Measured;
        /// <summary>The mode narrows the view (a magnifier or a variable scope setting): it gets a lens of its own.</summary>
        public bool Magnified;
        public float Fov;
        public string Name = "";
    }

    public enum HingeKind
    {
        None,
        /// <summary>Barrels tip down on a cross pin (double barrels, break-action launchers).</summary>
        BreakAction,
        /// <summary>A revolver's cylinder swings out on its crane.</summary>
        Cylinder,
        /// <summary>A machine gun's top cover lifts to lay the belt.</summary>
        FeedCover
    }

    /// <summary>
    /// Path-based, instance-independent description of a weapon's rig, cached per prefab. All vectors are in
    /// gun-body space (the transform the gun's mesh rigidly follows), in world units.
    /// </summary>
    public sealed class WeaponRigData
    {
        public string Key;
        public int[] GunBodyPath;
        public int[] MuzzlePath;
        public int[] SlidePath;
        public int[] MagazinePath;
        /// <summary>The magazine part's name (for the logs: which part a machine gun's box is).</summary>
        public string MagazineName;
        /// <summary>Pieces that travel with the magazine (rounds, follower, base plate) when they are separate objects.</summary>
        public readonly List<int[]> MagazinePartPaths = new List<int[]>();
        /// <summary>
        /// The magazine's (or rocket's) renderers that show while it sits loaded, at rest: a launcher made for several
        /// warheads keeps all of them under its rocket and shows one; after the shot none shows, and a copy of all five
        /// was the rocket in the hand (RPG-7V variants; user, 2026-10-16).
        /// </summary>
        public readonly List<int[]> MagShownAtIdle = new List<int[]>();
        /// <summary>
        /// Bones of the gun's skinned mesh that draw a second magazine where the magazine sits (the ARS guns' "MagSwap",
        /// the one their own reload brings in): hidden with the magazine, never copied into the hand (Tasks 232: with the
        /// magazine out, the gun still drew one).
        /// </summary>
        public readonly List<int[]> MagHideOnlyPaths = new List<int[]>();
        /// <summary>True when the magazine is a bone of a skinned gun mesh rather than its own object.</summary>
        public bool MagazineIsSkinned;
        /// <summary>True when the reload animation hides and shows the magazine instead of moving it.</summary>
        public bool MagazineToggles;
        public readonly List<int[]> ArmRendererPaths = new List<int[]>();

        /// <summary>
        /// Parts that move with the slide without being its children: named after it, beside it in the model (the Vector's
        /// cocking knob). Their paths and rest places; moved by the slide's travel and put back with it.
        /// </summary>
        public readonly List<int[]> SlideCompanionPaths = new List<int[]>();
        public readonly List<Vector3> SlideCompanionRest = new List<Vector3>();

        // How each part was found, for the self-test report: motion, visibility, name, bones or virtual.
        public string MagMethod = "virtual";
        public string SlideMethod = "virtual";
        public string HandsMethod = "estimated";

        // Reload timeline keyframes (seconds from reload start, -1 = not seen) and fire keyframes.
        public float TMagOut = -1f, TMagIn = -1f, TSlideBack = -1f, TSlideForward = -1f, TCoverOpen = -1f, TCoverShut = -1f;
        public float TFireSlideBack = -1f, TFireSlideForward = -1f;
        public float ReloadDuration;

        /// <summary>The mod's own sounds for each manual action.</summary>
        public readonly Dictionary<MechSound, List<SoundCue>> Cues = new Dictionary<MechSound, List<SoundCue>>();
        /// <summary>Sound events the fire animation plays at the moment of the shot (kept when the fire animation is suppressed).</summary>
        public readonly List<SoundCue> ShotCues = new List<SoundCue>();
        public readonly List<string> Warnings = new List<string>();

        public Vector3 AimAxis = Vector3.forward;
        public Vector3 Up = Vector3.up;
        public Vector3 GripPoint;
        /// <summary>Down the grip along its rake (gun-body space), from the weapon's own hand: index knuckle to little finger's. Zero if unknown.</summary>
        public Vector3 GripDown;
        public Vector3 SupportPoint;
        public bool HasSupportPoint;
        public Vector3 EyePoint;
        public Vector3 EyeDirection = Vector3.forward;
        public bool HasEyePoint;
        /// <summary>The sight line is right but the eye distance was not measurable, so EyePoint sits on the line behind the gun.</summary>
        public bool EyeFromLine;
        /// <summary>
        /// Every sight mode of the weapon (index = the game's sight mode), when it has more than one: a magnifier
        /// flipped in behind a red dot, a canted or offset sight. Null when the weapon has a single sight.
        /// </summary>
        public SightLine[] Sights;
        /// <summary>
        /// Parts a sight mode moves (a flip magnifier swung in or out, a folding sight) and their pose in each sight mode
        /// (gun-body space, [part][mode]). The weapon's animation shows them in place only while aiming; the mod keeps them
        /// where the selected mode puts them all the time. Empty for weapons without such parts.
        /// </summary>
        public readonly List<int[]> SightPartPaths = new List<int[]>();
        /// <summary>
        /// A belt-fed gun's own belt in its model (links and rounds between the box and the feed, not part of the box): shown
        /// only while a box is in and its belt lies on the feed tray.
        /// </summary>
        public readonly List<int[]> BeltPartPaths = new List<int[]>();

        /// <summary>The model draws its belt itself, by the round count its animator is told (the MK46, the QJB201): no strip is laid over it.</summary>
        public bool BeltDrawnByAnimator;
        /// <summary>
        /// Spare magazines of the flat reload animation, drawn where its other hand waits (a G17's, an AK105's: half a
        /// metre from the gun). The flat view never shows them; in VR they floated in the air by the gun. Hidden while held.
        /// </summary>
        public readonly List<int[]> SparePartPaths = new List<int[]>();
        /// <summary>
        /// A break action's shells as they sit in its chambers at rest (the magazine's among them), one per chamber, and their
        /// rest poses (gun-body space, the barrels shut). Shown only while their chamber holds a shell, carried with the
        /// barrels when they open: the DOUBLE-BARREL's hang off the gun's root, and stayed in the air where the shut chambers
        /// were when the barrels swung down, never coming out (user, 2026-10-22). Empty for other guns.
        /// </summary>
        public readonly List<int[]> ChamberShellPaths = new List<int[]>();
        public readonly List<RigidPose> ChamberShellRest = new List<RigidPose>();

        /// <summary>A gun-space pose carried along by the hinge opened <paramref name="openness"/> of the way.</summary>
        public RigidPose HingeRotated(RigidPose pose, float openness)
        {
            Quaternion r = Quaternion.AngleAxis(HingeOpenAngle * openness, HingeAxis);
            return new RigidPose(HingePivot + r * (pose.position - HingePivot), r * pose.rotation);
        }
        public RigidPose[][] SightPartPoses;
        /// <summary>Scoped weapons whose scope model was measured: the ocular lens centre (gun space) and its radius.</summary>
        public bool HasScopeModel;
        public Vector3 ScopeOcular;
        public float ScopeRadius;

        /// <summary>Slide/bolt travel vector at full rack (gun-body space).</summary>
        public Vector3 SlideTravel = new Vector3(0f, 0f, -0.05f);
        /// <summary>Where the off hand grabs to rack (gun-body space).</summary>
        public Vector3 RackPoint;
        /// <summary>Slide local position relative to its parent at rest.</summary>
        public Vector3 SlideRestLocal;
        /// <summary>A separate charging handle (AR style) that the hand works and the bolt follows; null if none.</summary>
        public int[] HandlePath;
        public Vector3 HandleRestLocal;
        /// <summary>Handle travel vector at full rack (gun-body space).</summary>
        public Vector3 HandleTravel;
        /// <summary>
        /// The handle moves with the bolt when the gun fires (an M1's operating rod, an AK's carrier handle): it rides with the bolt and
        /// stays back with it when the action locks open. An AR's charging handle does not move in the fire clip, and goes home alone.
        /// </summary>
        public bool HandleCycles;
        /// <summary>
        /// The bolt under a racked handle that carries it (the slide part is the handle: an M4's bolt under its charging
        /// handle, a PKM's under its 拉机柄). Locked open on an empty magazine only this stays back; the handle goes home.
        /// </summary>
        public int[] BoltPath;
        public Vector3 BoltRestLocal;
        /// <summary>
        /// No part of the model moves when the action is worked (bolt baked into the receiver): the rack point and
        /// travel come from where the mod's own hand racks in its reload animation.
        /// </summary>
        public bool RackFromHand;
        /// <summary>
        /// The rack point is estimated (from the reload hand, or virtual), not on a part: once the drawn model is measured it
        /// is put on the model's surface, where a hand can take it.
        /// </summary>
        public bool RackOnSurface;
        /// <summary>
        /// The part that moves when firing is inside the receiver (a bolt carrier) and the handle the reload hand takes is part
        /// of the receiver's mesh: that piece is cut out and moves with the pull.
        /// </summary>
        public bool CarveHandle;
        /// <summary>
        /// A handle is added at the rack point (a knob sticking out the side), not cut out of the receiver: for a model whose
        /// charging handle is not modelled at all (the CZ Bren 2's side handle). Like a cut-out handle it only comes back
        /// with the hand; the bolt inside moves with it.
        /// </summary>
        public bool AddedHandle;
        /// <summary>A side charging handle's knob found on the model (gun space; the CZ Bren 2's): where the hand takes it.</summary>
        public Vector3 SideKnob;
        public bool HasSideKnob;
        /// <summary>The part a cut-out handle is cut from (null: any mesh at the rack point): the handle's own mesh, not the receiver round it.</summary>
        public int[] CarveFromPath;
        /// <summary>The cut-out's radius (m) round the rack point; 0: the usual (3 cm, 2.2 on a handgun).</summary>
        public float CarveRadius;
        /// <summary>A part the reload swings open: break-action barrels, a cylinder crane or a feed cover.</summary>
        public HingeKind Hinge;
        public int[] HingePath;
        /// <summary>Hinge axis and pivot in gun-body space, and how far it opens (degrees, about the axis).</summary>
        public Vector3 HingeAxis;
        public Vector3 HingePivot;
        public float HingeOpenAngle;

        /// <summary>How far behind the barrel's end the game's muzzle transform sits (m), where it sits inside the gun: the QJB201's, 57 cm. 0 for nearly every gun.</summary>
        public float MuzzleInside;

        /// <summary>The lowest point of the gun's bipod, as the model draws it (gun space). Zero: none.</summary>
        public Vector3 BipodFoot;

        /// <summary>The gun's trigger, moved by the finger on the controller's: at a full pull it turns by this many degrees about the axis and moves by this much (gun space).</summary>
        public int[] TriggerPath;
        public Vector3 TriggerAxis;
        public float TriggerAngle;
        public Vector3 TriggerMove;

        /// <summary>A catch beside the feed cover that the reload turns as it opens: its part, axis and degrees (the QJB201's latch; most covers have none).</summary>
        public int[] HingeCatchPath;
        public Vector3 HingeCatchAxis;
        public float HingeCatchAngle;
        /// <summary>Where the off hand takes hold of a feed cover (gun-body space).</summary>
        public Vector3 HingeGrabPoint;
        /// <summary>The chambers (magazine seat) ride on the hinged part: they swing out with it.</summary>
        public bool SeatOnHinge;
        /// <summary>Automatic with a big box, drum or belt (or a feed cover): heavier handling, longer box insertion.</summary>
        public bool IsMachineGun;

        /// <summary>The weapon's pellets a shot (its configuration's projectilesPerShot): a shotgun's round is a shell, also where only this is known.</summary>
        public int Pellets;
        /// <summary>
        /// The action is worked by hand after every shot (bolt, lever, pump, including pump shotguns that load shell
        /// by shell); otherwise it cycles itself (self-loading rifles, pistols, semi-automatic shotguns).
        /// </summary>
        public bool CyclesByHand;
        /// <summary>The weapon's own arms for IK (null when the rig has no recognisable arm chain).</summary>
        public ArmChain ArmRight;
        public ArmChain ArmLeft;
        /// <summary>Finger curls (thumb, index, middle, ring, pinky; 0 open, 1 fist) of the mod's hands holding this gun.</summary>
        public float[] GripCurls;
        public float[] SupportCurls;

        public RigidPose MagSeat = RigidPose.Identity;

        /// <summary>The weapon's own support hand on the magazine, from its reload animation (null: the animation never holds it).</summary>
        public MagHoldPose MagHold;
        /// <summary>
        /// Share of the seated magazine that sticks out of the gun along its eject axis (1 = all outside, 0 = flush).
        /// A fresh magazine can only knock out one that sticks out; deep wells (pistol grips, KRISS Vector) cannot.
        /// </summary>
        public float MagProtrusion = 1f;
        public bool MagKickable = true;
        /// <summary>Direction (gun-body space) a magazine leaves the well.</summary>
        public Vector3 MagEjectAxis = Vector3.down;
        public Bounds MagBounds = new Bounds(Vector3.zero, new Vector3(0.025f, 0.12f, 0.06f));

        /// <summary>
        /// Runtime: the magazine's box was compared with the gun's own copy once (MagazineProp.AdoptCopyBounds, the first copy, fully
        /// drawn), and the analysed box as it was when the copy's replaced it (what the analysis's other readers, the casings, go by).
        /// </summary>
        public bool MagBoundsChecked, MagBoundsAdopted;
        public Bounds MagBoundsAnalysed;

        /// <summary>The magazine's box as the analysis measured it, whatever a copy's later replaced it with.</summary>
        public Bounds AnalysedMagBounds => MagBoundsAdopted ? MagBoundsAnalysed : MagBounds;

        /// <summary>
        /// This gun's well takes that gun's magazine: the same gun, or a sister gun with the same part of the same size. A magazine fitted only the
        /// exact variant it came from: one pulled from a soldier's HK416-B would not go into an HK416-A, with nothing to say why.
        /// </summary>
        internal bool TakesMagazineOf(WeaponRigData other) =>
            ReferenceEquals(other, this) || other != null && !string.IsNullOrEmpty(MagazineName) && MagazineName == other.MagazineName && IsPistol == other.IsPistol
            && (AnalysedMagBounds.size - other.AnalysedMagBounds.size).sqrMagnitude < 0.005f * 0.005f;

        public Vector3 MuzzlePoint;
        /// <summary>
        /// Where the drawn barrel's front lies along the aim (gun-body space, metres from its origin), set only for a one-round
        /// launcher whose muzzle transform sits more than 30 cm inside the gun. The RPG-7V [PG-7V] family's sits at its tube's
        /// centre, 28 cm ahead of its grip (rig.json, 2026-10-18) while the tube runs on well past the grip (its rocket is 121 cm
        /// long), and the gun's length was taken from that muzzle. Negative infinity: not set.
        /// </summary>
        public float BarrelFrontAlong = float.NegativeInfinity;
        /// <summary>
        /// How far the drawn gun reaches ahead of the muzzle point (negative: a barrel past it) and behind it, along the aim, in
        /// metres (its meshes shown at rest, arms and a separate third-person model left out). 0 and 0 when not measured. Used to
        /// place the hands of an armless twin (<see cref="WeaponAnalyzer"/>'s GripFromTwin).
        /// </summary>
        public float ModelFrontFromMuzzle, ModelRearFromMuzzle;
        /// <summary>The gun's own idle animation holds the slide back (its rest was moved to the forward place): kept home while held.</summary>
        public bool SlideRestsBackInAnimation;
        /// <summary>
        /// How far the gun reaches ahead of its grip along the aim (metres): the muzzle's, or the drawn barrel's front where the
        /// muzzle transform sits well inside the gun (<see cref="BarrelFrontAlong"/>). The muzzle's for every other gun.
        /// </summary>
        public float LengthFromGrip => Mathf.Max(Vector3.Dot(MuzzlePoint - GripPoint, AimAxis), BarrelFrontAlong - Vector3.Dot(GripPoint, AimAxis));

        public ActionType Action;
        /// <summary>
        /// An infinite magazine (the game's ammo -1): classed <see cref="ActionType.NoAmmo"/> as a tool is, but a gun with a barrel
        /// and sights all the same (<see cref="IsTool"/>).
        /// </summary>
        public bool InfiniteAmmo;
        /// <summary>A blade, a throwable or a tool with no ammo: nothing to aim down, no barrel. An infinite-magazine gun is not one.</summary>
        public bool IsTool => Action == ActionType.Melee || Action == ActionType.Throwable || Action == ActionType.NoAmmo && !InfiniteAmmo;
        public bool LockOnEmpty;
        /// <summary>
        /// Loaded with an en-bloc clip through the open action from the top (the M1 Garand): the clip goes in only while the
        /// action is open and the bolt then closes on its own; after the last round the empty clip flies out with its ping
        /// and the bolt stays open.
        /// </summary>
        public bool EnBloc;
        /// <summary>
        /// Its magazine is curved (an AK's, a QBZ's: the analysis takes its line along its top): it rocks into the well, front lip
        /// first (MagazineProp.RockAngle), and with AkMagazinesStayIn the release only unlatches it.
        /// </summary>
        public bool MagCurved;
        public bool IsPistol;

        /// <summary>
        /// A handgun by its slot. <see cref="IsPistol"/> also marks a long gun whose support hand cups the grip (held like a
        /// pistol, no handguard to steer by); what the gun is (its bolt travel, its handle rules, a revolver, a machine gun)
        /// goes by this: such a rifle got a pistol's 7 cm bolt travel and no AR or named charging handle (bug hunt 251).
        /// </summary>
        public bool Handgun;
        /// <summary>The weapon's slot (0 primary, 1 secondary, 2 gear, 3 large gear; -1 unknown): from the weapon entry when the
        /// prefab has none yet.</summary>
        public int Slot = -1;
        public bool Animated;
        public string Notes = "";

        /// <summary>
        /// The gun hand's palm frame on the gun (gun space) when a sandbox pose set it; else the gun is held with the palm frame
        /// along the bore and up (LookRotation(AimAxis, Up)) at the grip point.
        /// </summary>
        public Quaternion GripRotation = Quaternion.identity;
        public bool HasGripRotation;

        /// <summary>
        /// The gun hand's thumb from a sandbox pose, when it was set there (-1: the mod's own thumb rule): its base turned this
        /// far to the fist, and its curl.
        /// </summary>
        public float GripThumbBase = -1f;
        public float GripThumbCurl = -1f;

        /// <summary>Sandbox export only: where the magazine part waits at rest (its seat may be elsewhere: parked away).</summary>
        [System.NonSerialized] public Vector3 MagRestPos;

        /// <summary>Sandbox export only: the model and its note, written once the weapon's overrides are applied.</summary>
        [System.NonSerialized] public byte[] ExportGlb;
        [System.NonSerialized] public string ExportNote;

        /// <summary>Rig dump only (-rfvr-dumprig): lines an analysis step adds to the gun's section.</summary>
        [System.NonSerialized] public string DumpLines = "";

        public string Describe()
        {
            var sb = new StringBuilder();
            sb.Append($"[{Key}] action={Action} lockOnEmpty={LockOnEmpty} pistol={IsPistol} animated={Animated}");
            sb.Append($" grip={GripPoint:F3} support={(HasSupportPoint ? SupportPoint.ToString("F3") : "-")}");
            sb.Append($" aim={AimAxis:F2} eye={(HasEyePoint ? EyePoint.ToString("F3") : "-")}");
            sb.Append($" travel={SlideTravel.magnitude:0.000}");
            sb.Append($" mag={MagMethod}{(MagazinePartPaths.Count > 0 ? "+" + MagazinePartPaths.Count + " parts" : "")} eject={MagEjectAxis:F2}");
            // Which part the magazine (a machine gun's box) is, and the belt and cover with it (252 n: which part was taken for
            // the box was nowhere in the logs).
            if (MagazineName != null)
            {
                sb.Append($" magPart='{MagazineName}'");
            }
            if (BeltPartPaths.Count > 0 || Hinge != HingeKind.None)
            {
                sb.Append($" belt={BeltPartPaths.Count} hinge={Hinge}");
            }
            sb.Append($" slideFound={SlideMethod} hands={HandsMethod}");
            foreach (KeyValuePair<MechSound, List<SoundCue>> kv in Cues)
            {
                sb.Append($" {kv.Key}:{kv.Value.Count}");
            }
            if (Notes.Length > 0)
            {
                sb.Append(" notes: ").Append(Notes);
            }
            return sb.ToString();
        }
    }

    /// <summary>A <see cref="WeaponRigData"/> resolved against one live weapon instance.</summary>
    public sealed class WeaponRig
    {
        public readonly Weapon Weapon;
        public readonly WeaponRigData Data;
        public readonly Transform GunBody;
        public readonly Transform Muzzle;
        public readonly Transform Slide;
        public readonly Transform Handle;
        /// <summary>The bolt a racked handle carries (<see cref="WeaponRigData.BoltPath"/>); null if none.</summary>
        public readonly Transform Bolt;
        public readonly Transform Hinge;
        public readonly Transform HingeCatch;
        public readonly Transform Trigger;
        public readonly Transform Magazine;
        /// <summary>The slide's companions (<see cref="WeaponRigData.SlideCompanionPaths"/>), same order.</summary>
        public readonly Transform[] SlideCompanions;
        public readonly Renderer[] ArmRenderers;
        public readonly Renderer[] MagazineRenderers;
        /// <summary>Magazine plus any co-moving pieces; all of them are hidden while the magazine is out.</summary>
        public readonly Transform[] MagazineParts;

        /// <summary>The magazine parts' own scales, recorded while the gun was fresh (the magazine is hidden by scaling while out).</summary>
        public readonly Vector3[] MagazinePartScales;

        /// <summary>
        /// The parked ammunition (<see cref="WeaponRigData.SparePartPaths"/>) and the scales it had when this rig was made,
        /// before anything hid it. The held gun and the second weapon both hide it while they are in a hand, and both put it
        /// back from these: a second weapon that became the held gun first would otherwise keep the hidden scale as its own
        /// (Task 268).
        /// </summary>
        public readonly Transform[] SpareParts;
        public readonly Vector3[] SpareScales;

        /// <summary>The gun model's own belt (<see cref="WeaponRigData.BeltPartPaths"/>): on the feed tray, never part of a box copied for the hand.</summary>
        public readonly Transform[] BeltParts;

        /// <summary>A break action's chamber shells (<see cref="WeaponRigData.ChamberShellPaths"/>, same order) and their own scales.</summary>
        public readonly Transform[] ChamberShells;
        public readonly Vector3[] ChamberShellScales;

        /// <summary>
        /// A part's scale to give back: one already shrunk away by a rig of the held gun (1e-4) counts as its full size, or the new
        /// rig gave the tiny scale back for good (a rig made on a weapon in the hand; rig scout).
        /// </summary>
        private static Vector3 RestScale(Vector3 s) => s == Vector3.one * 1e-4f ? Vector3.one : s; // (only the mod's own mark: a part a mod scaled to 0 stays so)

        public WeaponRig(Weapon weapon, WeaponRigData data)
        {
            Weapon = weapon;
            Data = data;
            Transform root = weapon.transform;
            GunBody = Resolve(root, data.GunBodyPath) ?? root;
            Muzzle = Resolve(root, data.MuzzlePath);
            Slide = Resolve(root, data.SlidePath);
            SlideCompanions = new Transform[data.SlideCompanionPaths.Count];
            for (int i = 0; i < SlideCompanions.Length; i++)
            {
                SlideCompanions[i] = Resolve(root, data.SlideCompanionPaths[i]);
            }
            Handle = Resolve(root, data.HandlePath);
            Bolt = Resolve(root, data.BoltPath);
            Hinge = Resolve(root, data.HingePath);
            HingeCatch = Resolve(root, data.HingeCatchPath);
            Trigger = Resolve(root, data.TriggerPath);
            Magazine = Resolve(root, data.MagazinePath);
            var arms = new List<Renderer>();
            foreach (int[] p in data.ArmRendererPaths)
            {
                Transform t = Resolve(root, p);
                Renderer r = t != null ? t.GetComponent<Renderer>() : null;
                if (r != null)
                {
                    arms.Add(r);
                }
            }
            if (weapon.arms != null && !arms.Contains(weapon.arms))
            {
                arms.Add(weapon.arms);
            }
            ArmRenderers = arms.ToArray();
            MagazineRenderers = Magazine != null ? Magazine.GetComponentsInChildren<Renderer>(true) : new Renderer[0];
            var parts = new List<Transform>();
            if (Magazine != null)
            {
                parts.Add(Magazine);
            }
            foreach (int[] p in data.MagazinePartPaths)
            {
                Transform t = Resolve(root, p);
                if (t != null && !parts.Contains(t))
                {
                    parts.Add(t);
                }
            }
            var hideOnly = new HashSet<Transform>();
            foreach (int[] p in data.MagHideOnlyPaths)
            {
                Transform t = Resolve(root, p);
                if (t != null && !parts.Contains(t))
                {
                    parts.Add(t);
                    hideOnly.Add(t);
                }
            }
            MagazineHideOnly = hideOnly;
            MagazineParts = parts.ToArray();
            var shown = new HashSet<Renderer>();
            foreach (int[] p in data.MagShownAtIdle)
            {
                Transform t = Resolve(root, p);
                Renderer r = t != null ? t.GetComponent<Renderer>() : null;
                if (r != null)
                {
                    shown.Add(r);
                }
            }
            MagShownLoaded = shown;
            MagazinePartScales = new Vector3[MagazineParts.Length];
            for (int i = 0; i < MagazineParts.Length; i++)
            {
                MagazinePartScales[i] = RestScale(MagazineParts[i].localScale);
            }
            SpareParts = new Transform[data.SparePartPaths.Count];
            SpareScales = new Vector3[SpareParts.Length];
            for (int i = 0; i < SpareParts.Length; i++)
            {
                SpareParts[i] = Resolve(root, data.SparePartPaths[i]);
                SpareScales[i] = SpareParts[i] != null ? RestScale(SpareParts[i].localScale) : Vector3.one;
            }
            BeltParts = new Transform[data.BeltPartPaths.Count];
            for (int i = 0; i < BeltParts.Length; i++)
            {
                BeltParts[i] = Resolve(root, data.BeltPartPaths[i]);
            }
            ChamberShells = new Transform[data.ChamberShellPaths.Count];
            ChamberShellScales = new Vector3[ChamberShells.Length];
            for (int i = 0; i < ChamberShells.Length; i++)
            {
                ChamberShells[i] = Resolve(root, data.ChamberShellPaths[i]);
                ChamberShellScales[i] = ChamberShells[i] != null ? ChamberShells[i].localScale : Vector3.one;
                if (ChamberShells[i] == Magazine && Magazine != null)
                {
                    // The magazine's own shell, hidden while its chamber is empty: its scale as the rig recorded it.
                    int m = System.Array.IndexOf(MagazineParts, Magazine);
                    if (m >= 0)
                    {
                        ChamberShellScales[i] = MagazinePartScales[m];
                    }
                }
            }
        }

        /// <summary>
        /// Hides the parked ammunition: the spare magazines, drums and rounds the reload keeps beside the gun
        /// (<see cref="WeaponRigData.SparePartPaths"/>), which the flat reload draws where they wait and which floated in the air
        /// by a held gun (Tasks 257, 268). Called every frame the gun is in a hand, so a part an animation scaled back up is hidden
        /// again.
        /// </summary>
        public void HideSpareParts()
        {
            Vector3 tiny = Vector3.one * 1e-4f;
            foreach (Transform t in SpareParts)
            {
                if (t != null && t.localScale != tiny) // re-hidden only when an animation has scaled it back up
                {
                    t.localScale = tiny;
                }
            }
        }

        /// <summary>Puts the parked ammunition back as the rig was made: the gun is holstered, dropped or released.</summary>
        public void ShowSpareParts()
        {
            for (int i = 0; i < SpareParts.Length; i++)
            {
                if (SpareParts[i] != null)
                {
                    SpareParts[i].localScale = SpareScales[i];
                }
            }
        }

        /// <summary>Magazine parts only hidden with the magazine, never copied (<see cref="WeaponRigData.MagHideOnlyPaths"/>).</summary>
        public HashSet<Transform> MagazineHideOnly { get; private set; } = new HashSet<Transform>();

        /// <summary>The magazine's renderers that show while it sits loaded (<see cref="WeaponRigData.MagShownAtIdle"/>).</summary>
        public HashSet<Renderer> MagShownLoaded { get; private set; } = new HashSet<Renderer>();

        /// <summary>
        /// Shows the magazine at its real size for a moment (it is scaled to nothing while out of the gun) so it can be
        /// copied; returns the scales to put back with <see cref="RestoreMagazineScales"/>.
        /// </summary>
        public Vector3[] ShowMagazineForCopy()
        {
            var current = new Vector3[MagazineParts.Length];
            for (int i = 0; i < MagazineParts.Length; i++)
            {
                if (MagazineParts[i] != null)
                {
                    current[i] = MagazineParts[i].localScale;
                    MagazineParts[i].localScale = MagazinePartScales[i];
                }
            }
            return current;
        }

        public void RestoreMagazineScales(Vector3[] scales)
        {
            for (int i = 0; i < MagazineParts.Length && i < scales.Length; i++)
            {
                if (MagazineParts[i] != null)
                {
                    MagazineParts[i].localScale = scales[i];
                }
            }
        }

        public static Transform Resolve(Transform root, int[] path)
        {
            if (path == null)
            {
                return null;
            }
            Transform t = root;
            foreach (int i in path)
            {
                if (t == null || i < 0 || i >= t.childCount)
                {
                    return null;
                }
                t = t.GetChild(i);
            }
            return t;
        }

        public static int[] PathOf(Transform root, Transform t)
        {
            if (t == null)
            {
                return null;
            }
            var path = new List<int>();
            while (t != null && t != root)
            {
                path.Add(t.GetSiblingIndex());
                t = t.parent;
            }
            if (t != root)
            {
                return null;
            }
            path.Reverse();
            return path.ToArray();
        }

        /// <summary>World pose of the gun body right now.</summary>
        public RigidPose GunBodyPose => RigidPose.Of(GunBody);

        public Vector3 ToWorld(Vector3 gunPoint) => GunBody.position + GunBody.rotation * gunPoint;

        /// <summary>
        /// Parents <paramref name="t"/> to the gun body at a gun-space pose in metres, with unit world scale. Gun
        /// bones often carry an import scale (0.01, 100); a plain localPosition would be scaled by it and land the
        /// object (and size it) far from where it belongs.
        /// </summary>
        public void AttachInGunSpace(Transform t, Vector3 gunPoint, Quaternion gunRotation)
        {
            t.SetParent(GunBody, false);
            t.SetPositionAndRotation(ToWorld(gunPoint), GunBody.rotation * gunRotation);
            Vector3 s = GunBody.lossyScale;
            t.localScale = new Vector3(1f / Mathf.Max(1e-5f, Mathf.Abs(s.x)), 1f / Mathf.Max(1e-5f, Mathf.Abs(s.y)), 1f / Mathf.Max(1e-5f, Mathf.Abs(s.z)));
        }
        public Vector3 DirToWorld(Vector3 gunDir) => GunBody.rotation * gunDir;
        // (The rotation's conjugate, not Quaternion.Inverse: a native call in this Unity, and the keep-out and the hands ask this per point.)
        public Vector3 ToGun(Vector3 world) => Runtime.RigidPose.Conjugate(GunBody.rotation) * (world - GunBody.position);

        public Vector3 AimWorld => DirToWorld(Data.AimAxis);
        public Vector3 UpWorld => DirToWorld(Data.Up);
    }
}
