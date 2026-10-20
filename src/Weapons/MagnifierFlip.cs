using System.Text.RegularExpressions;
using RavenfieldVR.Core;
using RavenfieldVR.Input;
using RavenfieldVR.Rig;
using RavenfieldVR.Util;
using UnityEngine;

namespace RavenfieldVR.Weapons
{
    /// <summary>
    /// A flip-in magnifier on the held gun, switched by the free hand instead of a button. The off hand closes on the
    /// magnifier part within the grab radius and buzzes once. Sliding that hand sideways across the bore, or rolling it
    /// about the bore, past a small threshold taps the weapon's Magnifier script key once: the game's own script swings
    /// the lens in or out, and the hand gives a click. The hand has to come back toward where it took hold before the
    /// next flip, and letting go never flips. The part is found by its name, because the mod's own lens sits on the gun
    /// body rather than on a part of the model.
    /// </summary>
    public sealed class MagnifierFlip
    {
        /// <summary>Sideways travel across the bore (m) that flips the magnifier.</summary>
        private const float FlipSlide = 0.04f;
        /// <summary>Roll of the hand about the bore (degrees) that flips the magnifier.</summary>
        private const float FlipRoll = 35f;
        /// <summary>After a flip the hand has to come back this close (m) and this little rolled (degrees) to flip again.</summary>
        private const float RearmSlide = 0.02f;
        private const float RearmRoll = 15f;
        /// <summary>A shot kicks the gun under a held hand; for this long after one the grip follows the gun and nothing flips.</summary>
        private const float ShotSettle = 0.4f;

        /// <summary>Part names of a magnifier (a flip-in lens). G33, FTC and Unity are common model names.</summary>
        private static readonly Regex PartName = new Regex(@"magnif|flip|3x|g33|ftc|unity|mag-?nifier", RegexOptions.IgnoreCase);
        /// <summary>Names of the main optic (a red dot or a scope). Such a part is skipped unless its name also says magnifier.</summary>
        private static readonly Regex OpticName = new Regex(@"scope|optic|acog|eotech|holo|reflex|red ?dot", RegexOptions.IgnoreCase);
        private static readonly Regex MagnifierWords = new Regex(@"magnif|mag-?nifier|flip", RegexOptions.IgnoreCase);
        /// <summary>Iron sights (a flip-up rear or front sight): never the magnifier unless the name says magnifier (review, 2026-10-20).</summary>
        private static readonly Regex SightName = new Regex(@"iron|flip.?up|\bbus\b|(rear|front).?sight", RegexOptions.IgnoreCase);
        private static readonly Regex SaysMagnifier = new Regex(@"magnif|mag-?nifier|g33|ftc", RegexOptions.IgnoreCase);

        private readonly WeaponSystem _system;
        private readonly HeldWeapon _held;
        private Transform _part;
        private bool _searched;
        private bool _gripping;
        /// <summary>The magnifier was flipped and the hand has not come back toward the grab yet.</summary>
        private bool _latched;
        private float _shotAt = -10f;
        /// <summary>The palm's position and rotation in gun space when the grip closed on the magnifier.</summary>
        private Vector3 _grabGun;
        private Quaternion _grabRot = Quaternion.identity;

        public MagnifierFlip(WeaponSystem system, HeldWeapon held)
        {
            _system = system;
            _held = held;
        }

        /// <summary>The magnifier part of the held gun, or null when the model has no part named like one.</summary>
        public Transform Part
        {
            get
            {
                if (!_searched)
                {
                    _searched = true;
                    _part = Find(_held.Weapon, _held.Rig);
                }
                return _part;
            }
        }

        /// <summary>
        /// The hand flips this weapon's magnifier: the part is found and showing, and the weapon polls its Magnifier key.
        /// The Magnifier button leaves such a weapon alone. Without a part (or while the part is hidden, so the hand cannot
        /// reach it) the button keeps the switch, so switching is never impossible.
        /// </summary>
        public bool HandlesWeapon(Weapon weapon)
        {
            Transform part = Part;
            return weapon != null && weapon == _held.Weapon && part != null && part.gameObject.activeInHierarchy
                && ScriptKeys.Find(weapon, ScriptKeyPurpose.Magnifier) != null;
        }

        /// <summary>A shot went off: the gun kicks under the hand, so the grip is re-based rather than read as a hand movement.</summary>
        public void OnShot()
        {
            _shotAt = Time.unscaledTime;
        }

        /// <summary>Once per frame while a gun is held. Nothing happens while a menu is open.</summary>
        public void Update()
        {
            VRHand off = _system.Rig.Off;
            Transform part = Part;
            bool menu = GameContext.GameMenuOpen || !GameContext.InGame;
            if (menu || part == null || !part.gameObject.activeInHierarchy || !off.Tracked)
            {
                EndGrab(off);
                return;
            }
            if (!_gripping)
            {
                if (off.Role == HandRole.Free && (off.GrabPressed || off.GrabReaching) && InReach(off.Palm.position)
                    && ScriptKeys.Find(_held.Weapon, ScriptKeyPurpose.Magnifier) != null)
                {
                    BeginGrab(off);
                }
                return;
            }
            if (!off.GrabHeld || off.Role != HandRole.Magnifier)
            {
                EndGrab(off); // letting go never flips
                return;
            }
            Track(off);
        }

        /// <summary>The gun was put away or the active weapon changed: the hand lets go of the magnifier, with no flip.</summary>
        public void Release()
        {
            EndGrab(_system.Rig.Off);
        }

        /// <summary>
        /// The palm is within the grab radius of the magnifier, and not nearer the gun's foregrip or its rack point (they win
        /// there): on an AR the magnifier sits a few centimetres in front of the charging handle, and this grip is taken before
        /// the reload's, so a hand reaching to rack took the magnifier instead.
        /// </summary>
        private bool InReach(Vector3 palm)
        {
            Transform part = Part;
            Renderer r = part.GetComponentInChildren<Renderer>();
            Vector3 centre = r != null ? r.bounds.center : part.position;
            float d = Vector3.Distance(palm, centre);
            if (d >= VRConfig.GrabRadius.Value)
            {
                return false;
            }
            WeaponRigData data = _held.Rig.Data;
            if (data.HasSupportPoint && Vector3.Distance(palm, _held.Rig.ToWorld(data.SupportPoint)) < d)
            {
                return false;
            }
            // Weighed against the reload's own rack zone (where the knob is drawn now: its snapped point, moved with the stroke) as
            // TryGrab weighs them: the analysed point lay 3 cm off the CZ Bren 2 Tac's knob and the grip took palms in its zone.
            return _held.Reload == null || _held.Reload.RackScore(palm) >= d / VRConfig.GrabRadius.Value;
        }

        private void BeginGrab(VRHand off)
        {
            _gripping = true;
            _latched = false;
            Rebase(off);
            off.Role = HandRole.Magnifier;
            VRInput.Consume(DigitalAction.Grab, off.Side);
            off.Haptic(0.015f, 180f, 0.3f);
        }

        private void Track(VRHand off)
        {
            if (Time.unscaledTime - _shotAt < ShotSettle)
            {
                Rebase(off);
                return;
            }
            Vector3 axis = _held.Rig.Data.AimAxis.normalized;
            Vector3 now = _held.Rig.ToGun(off.Palm.position);
            // How far the hand has slid across the bore since it took hold, and how far it has rolled about the bore.
            float slide = Vector3.ProjectOnPlane(now - _grabGun, axis).magnitude;
            float roll = Mathf.Abs(Twist(GunRotation(off.Palm.rotation) * Quaternion.Inverse(_grabRot), axis));
            if (!_latched)
            {
                if (slide > FlipSlide || roll > FlipRoll)
                {
                    _latched = true;
                    Flip(off);
                }
            }
            else if (slide < RearmSlide && roll < RearmRoll)
            {
                _latched = false; // back near where it was taken: the next flip is armed
            }
        }

        private void Flip(VRHand off)
        {
            ScriptKey key = ScriptKeys.Find(_held.Weapon, ScriptKeyPurpose.Magnifier);
            if (key == null)
            {
                return; // the script stopped polling its key: nothing to tap
            }
            ScriptKeys.Tap(key.Key, key.Script);
            Flips++;
            off.Haptic(0.03f, 160f, 0.55f);
        }

        /// <summary>Autotest: the taps made by the hand since the gun was drawn.</summary>
        internal int Flips { get; private set; }

        /// <summary>Takes the hand's current pose as the reference for the next slide and roll.</summary>
        private void Rebase(VRHand off)
        {
            _grabGun = _held.Rig.ToGun(off.Palm.position);
            _grabRot = GunRotation(off.Palm.rotation);
        }

        private void EndGrab(VRHand off)
        {
            _gripping = false;
            _latched = false;
            if (off.Role == HandRole.Magnifier)
            {
                off.Role = HandRole.Free;
            }
        }

        /// <summary>A world rotation seen from the gun body, so the gun's own motion cancels out.</summary>
        private Quaternion GunRotation(Quaternion world) => Quaternion.Inverse(_held.Rig.GunBody.rotation) * world;

        /// <summary>The part of a rotation that turns about the axis (degrees), leaving out any tilt of the hand.</summary>
        private static float Twist(Quaternion q, Vector3 axis)
        {
            float w = q.w;
            float along = Vector3.Dot(new Vector3(q.x, q.y, q.z), axis);
            if (w < 0f)
            {
                w = -w;
                along = -along;
            }
            return 2f * Mathf.Rad2Deg * Mathf.Atan2(along, w);
        }

        /// <summary>
        /// The magnifier part: the shallowest transform of the model with a magnifier-like name, that is not the main optic
        /// (the scope object, or an optic name without a magnifier word) and has something to grab. Null when there is none.
        /// </summary>
        private static Transform Find(Weapon weapon, WeaponRig rig)
        {
            Transform root = weapon.transform;
            Transform scope = weapon.configuration.scopeAimObject != null ? weapon.configuration.scopeAimObject.transform : null;
            Transform best = null;
            int bestDepth = int.MaxValue;
            foreach (Transform t in root.GetComponentsInChildren<Transform>(true))
            {
                if (t == root || t == rig.GunBody || !PartName.IsMatch(t.name))
                {
                    continue;
                }
                if (scope != null && (t == scope || t.IsChildOf(scope)))
                {
                    continue;
                }
                if (OpticName.IsMatch(t.name) && !MagnifierWords.IsMatch(t.name) || SightName.IsMatch(t.name) && !SaysMagnifier.IsMatch(t.name))
                {
                    continue;
                }
                if (t.GetComponentInChildren<Renderer>(true) == null)
                {
                    continue; // a bare pivot with nothing to take hold of
                }
                int depth = Depth(t, root);
                if (depth < bestDepth)
                {
                    best = t;
                    bestDepth = depth;
                }
            }
            return best;
        }

        private static int Depth(Transform t, Transform root)
        {
            int depth = 0;
            for (Transform p = t; p != null && p != root; p = p.parent)
            {
                depth++;
            }
            return depth;
        }
    }
}
