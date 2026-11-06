using System.Collections.Generic;
using RavenfieldVR.Core;
using RavenfieldVR.Input;
using RavenfieldVR.Rig;
using RavenfieldVR.Util;
using UnityEngine;
using UnityEngine.Rendering;

namespace RavenfieldVR.Weapons
{
    /// <summary>
    /// The weapons you carry, shown where they hang: rifles and launchers slung behind the shoulders (muzzle up), pistols
    /// muzzle-down on the hips, grenades and gear on the chest. Copies of each weapon's own third-person model (the same
    /// stripped models dropped weapons use), drawn only for the headset, placed on the holster layout of
    /// <see cref="BodyZones"/> so you can see what you reach for. The weapon in your hand is not shown, and a copy that
    /// would come within a few centimetres of the eyes (looking down over a shouldered rifle) is hidden.
    /// </summary>
    internal sealed class HolsterDisplay
    {
        private const int FpLayer = 18;

        private sealed class Shown
        {
            public WeaponManager.WeaponEntry Entry;
            public GameObject Go;
            public Vector3 Centre;   // model bounds centre in its own space
            public float Length;     // along the barrel
            /// <summary>The model's own frame: barrel and top in its local space, as a rotation (forward = barrel).</summary>
            public Quaternion Frame = Quaternion.identity;
            /// <summary>Its scale as built, how far a hand's reach has eased it out (0-1), and whether it is hidden near the eyes.</summary>
            public float Scale = 1f, Hover;
            public bool NearEyes;
            /// <summary>Built with its magazine hidden: the gun was put away with the magazine out.</summary>
            public bool MagOut;
        }

        private readonly WeaponSystem _sys;
        private readonly Shown[] _shown = new Shown[BodyZones.Spots];

        public HolsterDisplay(WeaponSystem sys)
        {
            _sys = sys;
        }

        public void BeforeRender()
        {
            Actor actor = GameContext.PlayerActor;
            bool show = VRConfig.ShowHolsters.Value && actor != null && !actor.dead && GameContext.PlayerOnFoot && !GameContext.GameMenuOpen
                        && actor.weapons != null;
            BodyZones zones = _sys.Zones;
            Transform head = _sys.Rig.Head;
            for (int spot = 0; spot < BodyZones.Spots; spot++)
            {
                int slot = show ? zones.SlotAtSpot(spot) : -1;
                Weapon w = slot >= 0 && slot < actor.weapons.Length ? actor.weapons[slot] : null;
                // (Nor the second weapon held in the free hand.)
                WeaponManager.WeaponEntry entry = w != null && w != actor.activeWeapon && (_sys.Second == null || w != _sys.Second.Weapon) && !Spent(w) ? w.weaponEntry : null;
                Shown s = _shown[spot];
                if (entry == null || entry.prefab == null)
                {
                    Hide(s);
                    continue;
                }
                // Its magazine as the gun has it: put away with the magazine out, the holster showed it in (the copy is of the prefab).
                AmmoState ammo = _sys.StateFor(w, create: false);
                bool magOut = ammo != null && ammo.UsesDetachableMag && !ammo.MagInserted;
                if (s == null || s.Entry != entry || s.Go == null || s.MagOut != magOut)
                {
                    Clear(spot);
                    s = Build(entry, w);
                    _shown[spot] = s;
                    if (s == null)
                    {
                        continue;
                    }
                    s.MagOut = magOut;
                    if (magOut)
                    {
                        WeaponDrops.HideMagazineOnModel(s.Go, entry);
                    }
                }
                // A hand reaching the holster eases its copy out a little (5% larger, 1.5 cm out along its barrel), as the pouch's copy
                // lifts: the one tick on reaching it was all a holster gave (sweep, improve 6).
                s.Hover = Mathf.MoveTowards(s.Hover, _sys.HolsterHovered(slot) ? 1f : 0f, Time.unscaledDeltaTime * 8f);
                Place(s, zones, spot);
                // Never in front of the eyes: a copy reaching into the face is hidden until you look away. With a band either side
                // (3 cm, and a tenth of the facing): one line for both ways flipped it every frame for a head held on it (a
                // shouldered rifle close to the eyes; sweep, improve 7).
                Vector3 centre = s.Go.transform.TransformPoint(s.Centre);
                float near = 0.2f + s.Length * 0.25f, distance = Vector3.Distance(centre, head.position);
                float facing = Vector3.Dot(head.forward, (centre - head.position).normalized);
                s.NearEyes = NearEyesNext(s.NearEyes, distance, near, facing);
                bool visible = !s.NearEyes;
                if (s.Go.activeSelf != visible)
                {
                    s.Go.SetActive(visible);
                }
            }
        }

        private static Shown Build(WeaponManager.WeaponEntry entry, Weapon live)
        {
            GameObject model = WeaponDrops.ModelFor(entry);
            if (model == null)
            {
                return null;
            }
            model.name = "RavenfieldVR Holstered " + entry.name;
            Object.DontDestroyOnLoad(model);
            // The copy already holds the model at the weapon's own third-person scale: only what its parent adds on top.
            Transform tpParent = live != null && live.thirdPersonTransform != null ? live.thirdPersonTransform.parent : null;
            Vector3 scale = tpParent != null ? tpParent.lossyScale : Vector3.one;
            float uniform = Mathf.Clamp((Mathf.Abs(scale.x) + Mathf.Abs(scale.y) + Mathf.Abs(scale.z)) / 3f, 0.5f, 2f);
            model.transform.localScale = Vector3.one * uniform;
            var bounds = new Bounds();
            bool any = false;
            foreach (Transform t in model.GetComponentsInChildren<Transform>(true))
            {
                t.gameObject.layer = FpLayer;
            }
            foreach (Renderer r in model.GetComponentsInChildren<Renderer>(true))
            {
                r.shadowCastingMode = ShadowCastingMode.Off;
                // (Nor a part the template switched off: a beam or a part parked away, which stretched the box the barrel, centre and
                // length are taken from; task 303.)
                if (r is ParticleSystemRenderer || !r.enabled)
                {
                    continue;
                }
                // A part hidden by scale (the parked ammunition, Task 268) has a box of nothing, but its pivot is where it waits:
                // taken in, that point would stretch the box the barrel and centre are taken from.
                Bounds wb = r.bounds;
                if (wb.size.sqrMagnitude < 1e-8f)
                {
                    continue;
                }
                Bounds local = new Bounds(model.transform.InverseTransformPoint(wb.center), Vector3.zero);
                local.Encapsulate(model.transform.InverseTransformPoint(wb.min));
                local.Encapsulate(model.transform.InverseTransformPoint(wb.max));
                if (!any)
                {
                    bounds = local;
                    any = true;
                }
                else
                {
                    bounds.Encapsulate(local);
                }
            }
            // Third-person models are not authored with the barrel on a fixed axis. The barrel runs along the longest
            // side; the muzzle is the end farther from the pivot (the hand holds the grip, behind the middle); the top
            // faces away from the hand (the grip hangs below the barrel).
            Quaternion frame = Quaternion.identity;
            float length = 0.5f;
            if (any)
            {
                Vector3 size = bounds.size;
                int axis = size.x >= size.y && size.x >= size.z ? 0 : size.y >= size.z ? 1 : 2;
                Vector3 dir = Vector3.zero;
                dir[axis] = bounds.max[axis] >= -bounds.min[axis] ? 1f : -1f;
                Vector3 top = Vector3.ProjectOnPlane(bounds.center, dir);
                if (top.sqrMagnitude < 1e-4f)
                {
                    top = Vector3.ProjectOnPlane(axis == 1 ? Vector3.forward : Vector3.up, dir);
                }
                frame = Quaternion.LookRotation(dir, top.normalized);
                length = Mathf.Clamp(size[axis] * uniform, 0.1f, 1.6f);
            }
            return new Shown
            {
                Entry = entry,
                Go = model,
                Centre = any ? bounds.center : Vector3.zero,
                Length = length,
                Frame = frame,
                Scale = uniform
            };
        }

        /// <summary>
        /// Boneworks layout: long guns hang down the back from the shoulder (grip up where the hand reaches over, muzzle
        /// low), pistols sit muzzle-down along the ribs under the arm with the grip forward, gear lies across the small of
        /// the back or hangs head-down from the belt. Classic layout: long guns muzzle up behind the shoulder, pistols
        /// muzzle-down on the hips, gear across the chest. Barrel and top come from the model's measured frame.
        /// </summary>
        /// <summary>The last grenade thrown: the slot stays the actor's (a resupply fills it), and showed a grenade on the belt that drew nothing to throw.</summary>
        internal static bool Spent(Weapon w) => w is ThrowableWeapon && !w.HasAnyAmmo();

        private static void Place(Shown s, BodyZones zones, int spot)
        {
            Vector3 fwd = zones.TorsoForward, right = zones.TorsoRight, up = Vector3.up;
            float side = zones.SideOf(spot);
            float dom = VRInput.GunHand == Hand.Left ? -1f : 1f; // (the hand the gun is in now: it can be passed over)
            Vector3 at = zones.SpotPosition(spot);
            Vector3 barrel, top, centre;
            Vector3 drawn = Vector3.zero; // which way a reached copy eases out of its holster (set per kind: its grip end)
            switch (zones.KindOf(spot))
            {
                case BodyZones.SpotKind.Back when BodyZones.Boneworks:
                    barrel = (-up * 0.93f + right * (0.22f * side) - fwd * 0.12f).normalized;
                    top = fwd; // sights against the back, the grip sticking out behind the shoulder for the hand reaching over
                    // The grip at the shoulder, the rest of the gun hanging down the back.
                    centre = at + barrel * Mathf.Min(0.2f, s.Length * 0.22f) - fwd * 0.04f;
                    break;
                case BodyZones.SpotKind.Back:
                    barrel = (up * 0.93f + right * (0.28f * side) - fwd * 0.12f).normalized;
                    top = -fwd;
                    // The middle of the gun a hand's length below and behind the shoulder: the stock and grip are where you
                    // reach over the shoulder, the muzzle sticks up beside the head.
                    centre = at - up * Mathf.Min(0.22f, s.Length * 0.3f) - fwd * 0.06f + right * (0.04f * side);
                    break;
                case BodyZones.SpotKind.Hip:
                    barrel = (-up + fwd * 0.12f).normalized;
                    top = fwd;
                    // A long gun in the hip holster (a mod's "[Sec]" rifle) hangs lower, its top at most 20 cm above the hip:
                    // centred on the hip, an 80 cm rifle stood up past the shoulder into the view (user's screenshot, 2026-10-17).
                    centre = at + right * (0.05f * side) - up * (0.04f + Mathf.Max(0f, s.Length * 0.5f - 0.2f));
                    break;
                case BodyZones.SpotKind.Underarm:
                    // A shoulder holster: muzzle down and back along the ribs, grip forward under the arm.
                    barrel = (-up * 0.8f - fwd * 0.6f).normalized;
                    top = (up * 0.6f - fwd * 0.8f).normalized;
                    centre = at + right * (0.035f * side);
                    break;
                case BodyZones.SpotKind.LowerBack:
                    // Across the small of the back, the handle toward the weapon hand.
                    barrel = -right * dom;
                    top = up;
                    centre = at - fwd * 0.02f;
                    break;
                case BodyZones.SpotKind.Belt:
                    // Hanging from the belt, handle up, head (or the grenade's body) down.
                    barrel = (-up + fwd * 0.15f).normalized;
                    top = fwd;
                    centre = at - up * Mathf.Min(0.12f, s.Length * 0.3f) + fwd * 0.03f;
                    break;
                default:
                    // Across the chest (classic layout), head toward the weapon-hand shoulder, kept low: a tool standing
                    // upright there reached up into the line of sight.
                    barrel = (up * 0.45f + right * (0.89f * (side != 0f ? side : dom))).normalized;
                    top = -fwd;
                    centre = at + fwd * 0.06f - up * 0.07f;
                    drawn = fwd; // out from the chest, toward the hand in front of it (along the barrel was sideways, across it)
                    break;
            }
            if (drawn == Vector3.zero)
            {
                drawn = -barrel; // the grip end: up out of a back or hip holster, forward out of the others
            }
            Quaternion rot = Quaternion.LookRotation(barrel, top) * Runtime.RigidPose.Conjugate(s.Frame);
            Transform t = s.Go.transform;
            Vector3 scale = Vector3.one * (s.Scale * (1f + 0.05f * s.Hover));
            if (t.localScale != scale)
            {
                t.localScale = scale;
            }
            centre += drawn * (0.015f * s.Hover); // drawn out a little toward the reaching hand
            t.rotation = rot;
            t.position = centre - rot * Vector3.Scale(s.Centre, t.localScale);
        }

        /// <summary>
        /// Whether a copy is hidden near the eyes, from whether it was: hidden within <paramref name="near"/> while faced (0.2 of the
        /// view or more), shown again only 3 cm farther or a tenth less faced (one line both ways flipped it every frame).
        /// </summary>
        internal static bool NearEyesNext(bool was, float distance, float near, float facing) =>
            was ? distance < near + 0.03f && facing > 0.1f : distance <= near && facing >= 0.2f;

        private static void Hide(Shown s)
        {
            if (s == null)
            {
                return;
            }
            // Shown again at rest (a weapon back in another holster came back eased out and judged by its old place near the eyes).
            s.Hover = 0f;
            s.NearEyes = false;
            if (s.Go != null && s.Go.activeSelf)
            {
                s.Go.SetActive(false);
            }
        }

        private void Clear(int spot)
        {
            Shown s = _shown[spot];
            if (s != null && s.Go != null)
            {
                Object.Destroy(s.Go);
            }
            _shown[spot] = null;
        }

        public void Destroy()
        {
            for (int i = 0; i < _shown.Length; i++)
            {
                Clear(i);
            }
        }
    }
}
