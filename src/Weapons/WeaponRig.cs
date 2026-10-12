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
        /// <summary>Pieces that travel with the magazine (rounds, follower, base plate) when they are separate objects.</summary>
        public readonly List<int[]> MagazinePartPaths = new List<int[]>();
        /// <summary>True when the magazine is a bone of a skinned gun mesh rather than its own object.</summary>
        public bool MagazineIsSkinned;
        /// <summary>True when the reload animation hides and shows the magazine instead of moving it.</summary>
        public bool MagazineToggles;
        public readonly List<int[]> ArmRendererPaths = new List<int[]>();

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
        public Vector3 SupportPoint;
        public bool HasSupportPoint;
        public Vector3 EyePoint;
        public Vector3 EyeDirection = Vector3.forward;
        public bool HasEyePoint;
        /// <summary>The sight line is right but the eye distance was not measurable, so EyePoint sits on the line behind the gun.</summary>
        public bool EyeFromLine;
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
        /// No part of the model moves when the action is worked (bolt baked into the receiver): the rack point and
        /// travel come from where the mod's own hand racks in its reload animation.
        /// </summary>
        public bool RackFromHand;
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
        public bool IsPistol;
        public bool Animated;
        public string Notes = "";

        public string Describe()
        {
            var sb = new StringBuilder();
            sb.Append($"[{Key}] action={Action} lockOnEmpty={LockOnEmpty} pistol={IsPistol} animated={Animated}");
            sb.Append($" grip={GripPoint:F3} support={(HasSupportPoint ? SupportPoint.ToString("F3") : "-")}");
            sb.Append($" aim={AimAxis:F2} eye={(HasEyePoint ? EyePoint.ToString("F3") : "-")}");
            sb.Append($" travel={SlideTravel.magnitude:0.000}");
            sb.Append($" mag={MagMethod}{(MagazinePartPaths.Count > 0 ? "+" + MagazinePartPaths.Count + " parts" : "")} eject={MagEjectAxis:F2}");
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
        public readonly Transform Hinge;
        public readonly Transform Magazine;
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
            Handle = Resolve(root, data.HandlePath);
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
            MagazineParts = parts.ToArray();
            MagazinePartScales = new Vector3[MagazineParts.Length];
            for (int i = 0; i < MagazineParts.Length; i++)
            {
                MagazinePartScales[i] = MagazineParts[i].localScale;
            }
        }

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
