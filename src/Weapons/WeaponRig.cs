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
        Throwable,
        Melee,
        /// <summary>Infinite ammo or tools without ammo; no reload interaction.</summary>
        NoAmmo
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
        public readonly List<int[]> ArmRendererPaths = new List<int[]>();

        public Vector3 AimAxis = Vector3.forward;
        public Vector3 Up = Vector3.up;
        public Vector3 GripPoint;
        public Vector3 SupportPoint;
        public bool HasSupportPoint;
        public Vector3 EyePoint;
        public Vector3 EyeDirection = Vector3.forward;
        public bool HasEyePoint;

        /// <summary>Slide/bolt travel vector at full rack (gun-body space).</summary>
        public Vector3 SlideTravel = new Vector3(0f, 0f, -0.05f);
        /// <summary>Where the off hand grabs to rack (gun-body space).</summary>
        public Vector3 RackPoint;
        /// <summary>Slide local position relative to its parent at rest.</summary>
        public Vector3 SlideRestLocal;

        public RigidPose MagSeat = RigidPose.Identity;
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
            sb.Append($" slide={(SlidePath != null ? "yes" : "virtual")} travel={SlideTravel.magnitude:0.000}");
            sb.Append($" mag={(MagazinePath != null ? "yes" : "virtual")} eject={MagEjectAxis:F2}");
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
        public readonly Transform Magazine;
        public readonly Renderer[] ArmRenderers;
        public readonly Renderer[] MagazineRenderers;

        public WeaponRig(Weapon weapon, WeaponRigData data)
        {
            Weapon = weapon;
            Data = data;
            Transform root = weapon.transform;
            GunBody = Resolve(root, data.GunBodyPath) ?? root;
            Muzzle = Resolve(root, data.MuzzlePath);
            Slide = Resolve(root, data.SlidePath);
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
        public Vector3 DirToWorld(Vector3 gunDir) => GunBody.rotation * gunDir;
        public Vector3 ToGun(Vector3 world) => Quaternion.Inverse(GunBody.rotation) * (world - GunBody.position);

        public Vector3 AimWorld => DirToWorld(Data.AimAxis);
        public Vector3 UpWorld => DirToWorld(Data.Up);
    }
}
