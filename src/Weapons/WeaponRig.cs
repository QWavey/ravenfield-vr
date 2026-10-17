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
        public float TMagOut = -1f, TMagIn = -1f, TSlideBack = -1f, TSlideForward = -1f;
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
        /// <summary>
        /// Spare magazines of the flat reload animation, drawn where its other hand waits (a G17's, an AK105's: half a
        /// metre from the gun). The flat view never shows them; in VR they floated in the air by the gun. Hidden while held.
        /// </summary>
        public readonly List<int[]> SparePartPaths = new List<int[]>();
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
        /// <summary>A part the reload swings open: break-action barrels, a cylinder crane or a feed cover.</summary>
        public HingeKind Hinge;
        public int[] HingePath;
        /// <summary>Hinge axis and pivot in gun-body space, and how far it opens (degrees, about the axis).</summary>
        public Vector3 HingeAxis;
        public Vector3 HingePivot;
        public float HingeOpenAngle;
        /// <summary>Where the off hand takes hold of a feed cover (gun-body space).</summary>
        public Vector3 HingeGrabPoint;
        /// <summary>The chambers (magazine seat) ride on the hinged part: they swing out with it.</summary>
        public bool SeatOnHinge;
        /// <summary>Automatic with a big box, drum or belt (or a feed cover): heavier handling, longer box insertion.</summary>
        public bool IsMachineGun;
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
        /// <summary>
        /// Share of the seated magazine that sticks out of the gun along its eject axis (1 = all outside, 0 = flush).
        /// A fresh magazine can only knock out one that sticks out; deep wells (pistol grips, KRISS Vector) cannot.
        /// </summary>
        public float MagProtrusion = 1f;
        public bool MagKickable = true;
        /// <summary>Direction (gun-body space) a magazine leaves the well.</summary>
        public Vector3 MagEjectAxis = Vector3.down;
        public Bounds MagBounds = new Bounds(Vector3.zero, new Vector3(0.025f, 0.12f, 0.06f));
        public Vector3 MuzzlePoint;

        public ActionType Action;
        public bool LockOnEmpty;
        /// <summary>
        /// Loaded with an en-bloc clip through the open action from the top (the M1 Garand): the clip goes in only while the
        /// action is open and the bolt then closes on its own; after the last round the empty clip flies out with its ping
        /// and the bolt stays open.
        /// </summary>
        public bool EnBloc;
        public bool IsPistol;

        /// <summary>
        /// A handgun by its slot. <see cref="IsPistol"/> also marks a long gun whose support hand cups the grip (held like a
        /// pistol, no handguard to steer by); what the gun is (its bolt travel, its handle rules, a revolver, a machine gun)
        /// goes by this: such a rifle got a pistol's 7 cm bolt travel and no AR or named charging handle (bug hunt 251).
        /// </summary>
        public bool Handgun;
        public bool Animated;
        public string Notes = "";

        /// <summary>Sandbox export only: where the magazine part waits at rest (its seat may be elsewhere: parked away).</summary>
        [System.NonSerialized] public Vector3 MagRestPos;

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
        public readonly Transform Magazine;
        /// <summary>The slide's companions (<see cref="WeaponRigData.SlideCompanionPaths"/>), same order.</summary>
        public readonly Transform[] SlideCompanions;
        public readonly Renderer[] ArmRenderers;
        public readonly Renderer[] MagazineRenderers;
        /// <summary>Magazine plus any co-moving pieces; all of them are hidden while the magazine is out.</summary>
        public readonly Transform[] MagazineParts;

        /// <summary>The magazine parts' own scales, recorded while the gun was fresh (the magazine is hidden by scaling while out).</summary>
        public readonly Vector3[] MagazinePartScales;

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
                MagazinePartScales[i] = MagazineParts[i].localScale;
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
        public Vector3 ToGun(Vector3 world) => Quaternion.Inverse(GunBody.rotation) * (world - GunBody.position);

        public Vector3 AimWorld => DirToWorld(Data.AimAxis);
        public Vector3 UpWorld => DirToWorld(Data.Up);
    }
}
