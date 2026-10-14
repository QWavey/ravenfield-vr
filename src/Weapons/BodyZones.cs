using RavenfieldVR.Core;
using RavenfieldVR.Input;
using RavenfieldVR.Rig;
using RavenfieldVR.Util;
using UnityEngine;

namespace RavenfieldVR.Weapons
{
    /// <summary>
    /// Body-relative interaction points derived from the head pose and calibrated height: holsters (by what each weapon is
    /// rather than which loadout slot it came in), the magazine pouch, the night-vision touch point beside the head and the
    /// look-at-your-wrist gesture. Two layouts (<see cref="HolsterLayout"/>): the Boneworks one (long guns behind the
    /// shoulders, pistols under the arms, gear at the small of the back and on the belt, the pouch on the side of the hip)
    /// and the mod's earlier one (pistols on the hips, gear on the chest, the pouch at the front of the belt).
    /// The torso yaw lags the head so glancing sideways doesn't swing the holsters away.
    /// </summary>
    public sealed class BodyZones
    {
        private readonly VRRig _rig;
        private float _torsoYaw;
        private bool _hasYaw;

        public Vector3 Chest, OffHip, DomHip, BackShoulder, OffBackShoulder, OffChest, DomChest, Pouch, HeadSide, Belt;
        /// <summary>Boneworks layout: under each arm (pistols), the small of the back and the belt's front (gear).</summary>
        public Vector3 DomUnderarm, OffUnderarm, LowerBack, DomBeltFront, OffBeltFront;

        internal static bool Boneworks => VRConfig.Holsters == null || VRConfig.Holsters.Value == HolsterLayout.Boneworks;
        public Vector3 TorsoForward, TorsoRight;

        public const float HolsterRadius = 0.2f;
        public const float PouchRadius = 0.17f;

        private static GameObject[] _debug;

        public BodyZones(VRRig rig)
        {
            _rig = rig;
        }

        public void Update()
        {
            Transform head = _rig.Head;
            Vector3 f = head.forward;
            f.y = 0f;
            if (f.sqrMagnitude < 1e-4f)
            {
                f = head.up * -1f;
                f.y = 0f;
            }
            float headYaw = Quaternion.LookRotation(f.normalized).eulerAngles.y;
            if (!_hasYaw)
            {
                _torsoYaw = headYaw;
                _hasYaw = true;
            }
            // Torso follows the head only once it has turned past a comfortable neck angle.
            float delta = Mathf.DeltaAngle(_torsoYaw, headYaw);
            if (Mathf.Abs(delta) > 35f)
            {
                _torsoYaw += delta - Mathf.Sign(delta) * 35f;
            }
            _torsoYaw = Mathf.MoveTowardsAngle(_torsoYaw, headYaw, 25f * Time.unscaledDeltaTime);

            ComputeSpots();
            UpdateGestures();
            DrawDebug();
        }

        /// <summary>
        /// Places every body spot from the head pose of this moment and the current torso yaw. Called again just before
        /// rendering, so what is drawn at a spot (holstered weapons, the pouch) does not trail the head by a frame.
        /// </summary>
        public void ComputeSpots()
        {
            Transform head = _rig.Head;
            Quaternion torso = Quaternion.Euler(0f, _torsoYaw, 0f);
            TorsoForward = torso * Vector3.forward;
            TorsoRight = torso * Vector3.right;
            float side = VRConfig.LeftHanded ? -1f : 1f;
            float height = VRConfig.EyeHeight.Value > 0.3f ? VRConfig.EyeHeight.Value : 1.65f;
            if (VRConfig.SeatedMode.Value)
            {
                height *= 1.35f; // seated calibration is lower than standing anatomy
            }

            // The neck pivot the head turns about (about 9 cm under and 8 cm behind the eyes): looking down at the pouch
            // or a holster swings the eyes forward and down, but not the body the spots hang from.
            Vector3 pivot = head.position + head.rotation * new Vector3(0f, -0.09f, -0.08f);
            Vector3 neck = pivot - Vector3.up * 0.03f;
            Chest = neck - Vector3.up * 0.22f + TorsoForward * 0.12f;
            Vector3 waist = neck - Vector3.up * (0.44f * height - 0.12f) + TorsoForward * 0.14f;
            // The belt line, where the pouch rides: about 62% of the eye height above the floor, just in front of the body.
            Vector3 belt = neck - Vector3.up * (0.38f * height - 0.12f) + TorsoForward * 0.14f;
            DomHip = waist + TorsoRight * (0.18f * side);
            OffHip = waist - TorsoRight * (0.18f * side);
            BackShoulder = neck + Vector3.up * 0.05f - TorsoForward * 0.12f + TorsoRight * (0.17f * side);
            OffBackShoulder = neck + Vector3.up * 0.05f - TorsoForward * 0.12f - TorsoRight * (0.17f * side);
            OffChest = Chest - TorsoRight * (0.13f * side);
            DomChest = Chest + TorsoRight * (0.13f * side) - Vector3.up * 0.08f;
            HeadSide = head.position - head.right * (0.11f * side) + head.up * 0.02f;
            Belt = belt;
            // Boneworks: pistols high under the arms (against the ribs, just below the armpit), gear at the small of the
            // back and at the front of the belt.
            Vector3 underarm = neck - Vector3.up * 0.28f + TorsoForward * 0.02f;
            DomUnderarm = underarm + TorsoRight * (0.17f * side);
            OffUnderarm = underarm - TorsoRight * (0.17f * side);
            LowerBack = belt - TorsoForward * 0.29f;
            DomBeltFront = belt + TorsoRight * (0.09f * side);
            OffBeltFront = belt - TorsoRight * (0.09f * side);
            // The side of the hip at belt height (the Boneworks belt pouch), a little ahead of the hip bone.
            Vector3 hipSide = belt - TorsoForward * 0.1f;

            switch (VRConfig.Pouch.Value)
            {
                // Each a hand's width from the nearest holster spot, so the holstered weapon and the pouch never overlap.
                case PouchLocation.DominantHip: Pouch = Boneworks ? hipSide + TorsoRight * (0.17f * side) : belt + TorsoRight * (0.1f * side); break;
                case PouchLocation.Chest: Pouch = Chest - Vector3.up * 0.13f; break;
                case PouchLocation.OffHandChest: Pouch = OffChest - Vector3.up * 0.1f + TorsoForward * 0.03f; break;
                default: Pouch = Boneworks ? hipSide - TorsoRight * (0.17f * side) : belt - TorsoRight * (0.1f * side); break; // off-hand side
            }
        }

        private void UpdateGestures()
        {
            VRHand off = _rig.Off;
            if (!off.Tracked)
            {
                VRGameInput.WristLookGesture = false;
                return;
            }
            // Wrist watch: back of the off hand turned toward the face, raised in front of the chest.
            Vector3 toHead = (_rig.Head.position - off.Raw.position);
            float dist = toHead.magnitude;
            Vector3 backOfHand = off.Palm.rotation * (off.IsLeft ? Vector3.left : Vector3.right);
            bool facing = dist < 0.55f && Vector3.Dot(backOfHand, toHead / Mathf.Max(dist, 1e-3f)) > 0.6f;
            bool inView = Vector3.Dot(_rig.Head.forward, -toHead.normalized) > 0.75f;
            VRGameInput.WristLookGesture = facing && inView && off.Role == HandRole.Free;

            if (off.GrabPressed && off.Role == HandRole.Free && Vector3.Distance(off.Palm.position, HeadSide) < 0.13f && BesideHead(off.Palm.position))
            {
                VRGameInput.GogglesGesturePressed = true;
                VRInput.Consume(DigitalAction.Grab, off.Side);
                off.Haptic(0.04f, 120f, 0.5f);
            }
        }

        // ---- holsters ---------------------------------------------------------------------------------

        /// <summary>
        /// Holster spots. Classic layout: the two shoulders, the two hips (handguns), the middle and sides of the chest
        /// (gear). Boneworks layout: the two shoulders, under the two arms (handguns), the small of the back and the two
        /// sides of the belt's front (gear).
        /// </summary>
        private enum Spot { BackDominant, BackOff, HandgunDominant, HandgunOff, Gear, GearOff, GearDominant }

        private enum Carry { Long, Hand, Gear }

        private const int SpotCount = 7;

        // Where each kind of weapon is carried, best place first; a full place passes it on to the next.
        private static readonly Spot[] LongSpots = { Spot.BackDominant, Spot.BackOff, Spot.Gear, Spot.GearOff, Spot.GearDominant, Spot.HandgunDominant, Spot.HandgunOff };
        private static readonly Spot[] HandSpots = { Spot.HandgunDominant, Spot.HandgunOff, Spot.GearDominant, Spot.GearOff, Spot.Gear, Spot.BackOff, Spot.BackDominant };
        private static readonly Spot[] GearSpots = { Spot.Gear, Spot.GearOff, Spot.GearDominant, Spot.HandgunOff, Spot.HandgunDominant, Spot.BackOff, Spot.BackDominant };
        // Boneworks: a second gear item goes on the belt at the weapon-hand side, away from the pouch on the other hip.
        private static readonly Spot[] GearSpotsBoneworks = { Spot.Gear, Spot.GearDominant, Spot.GearOff, Spot.HandgunOff, Spot.HandgunDominant, Spot.BackOff, Spot.BackDominant };

        // Loadout slot carried at each spot (-1 = empty), rebuilt from the player's weapons when asked.
        private readonly int[] _spotSlot = new int[SpotCount];

        /// <summary>
        /// Loadout slot whose holster is under <paramref name="point"/>, or -1. Rifles, launchers and other long weapons
        /// hang behind the shoulders (dominant side first); handguns sit under the arms (Boneworks layout) or on the hips;
        /// grenades and gear at the small of the back and on the belt, or on the chest.
        /// </summary>
        public int HolsterAt(Vector3 point, bool includeChest = true)
        {
            AssignHolsters(GameContext.PlayerActor);
            float best = HolsterRadius;
            int slot = -1;
            for (int i = 0; i < SpotCount; i++)
            {
                if (_spotSlot[i] < 0 || !includeChest && IsChest((Spot)i))
                {
                    // The chest sits where a shouldered rifle's grip hand is: callers leave it out while aiming.
                    continue;
                }
                Check(point, Position((Spot)i), _spotSlot[i], ref best, ref slot);
            }
            return slot;
        }

        private void AssignHolsters(Actor actor)
        {
            for (int i = 0; i < SpotCount; i++)
            {
                _spotSlot[i] = -1;
            }
            if (actor == null || actor.weapons == null)
            {
                return;
            }
            for (int slot = 0; slot < actor.weapons.Length; slot++)
            {
                Weapon w = actor.weapons[slot];
                if (w == null)
                {
                    continue;
                }
                Carry carry = CarryOf(w);
                Spot[] order = carry == Carry.Long ? LongSpots : carry == Carry.Hand ? HandSpots : Boneworks ? GearSpotsBoneworks : GearSpots;
                foreach (Spot s in order)
                {
                    if (_spotSlot[(int)s] < 0)
                    {
                        _spotSlot[(int)s] = slot;
                        break;
                    }
                }
            }
        }

        /// <summary>What a weapon is, from its own slot type (0 primary, 1 secondary, 2 gear, 3 large gear) and class.</summary>
        private static Carry CarryOf(Weapon w)
        {
            if (w is ThrowableWeapon || w is ToggleableItem || w is SquadLeaderKit || w is FishingRod)
            {
                return Carry.Gear;
            }
            switch (w.slot)
            {
                case 0:
                case 3:
                    return Carry.Long;
                case 1:
                    return Carry.Hand;
                default:
                    // Small gear: a gun (machine pistol, flare gun) goes on the hip, a knife or tool on the chest.
                    return w is MeleeWeapon ? Carry.Gear : Carry.Hand;
            }
        }

        /// <summary>
        /// In front of the chest, where a shouldered rifle's grip hand is (the classic layout's gear spots): left out while
        /// aiming. The Boneworks layout has nothing there.
        /// </summary>
        private static bool IsChest(Spot s) => !Boneworks && (s == Spot.Gear || s == Spot.GearOff || s == Spot.GearDominant);

        public enum SpotKind { Back, Hip, Chest, Underarm, LowerBack, Belt }

        public const int Spots = SpotCount;

        /// <summary>Loadout slot carried at <paramref name="spot"/> (-1 empty), from the player's weapons right now.</summary>
        public int SlotAtSpot(int spot)
        {
            if (spot == 0)
            {
                AssignHolsters(GameContext.PlayerActor);
            }
            return _spotSlot[spot];
        }

        public Vector3 SpotPosition(int spot) => Position((Spot)spot);

        public SpotKind KindOf(int spot)
        {
            switch ((Spot)spot)
            {
                case Spot.BackDominant:
                case Spot.BackOff:
                    return SpotKind.Back;
                case Spot.HandgunDominant:
                case Spot.HandgunOff:
                    return Boneworks ? SpotKind.Underarm : SpotKind.Hip;
                case Spot.Gear:
                    return Boneworks ? SpotKind.LowerBack : SpotKind.Chest;
                default:
                    return Boneworks ? SpotKind.Belt : SpotKind.Chest;
            }
        }

        /// <summary>+1 on the dominant hand's side of the body, -1 on the other, 0 in the middle (in TorsoRight units).</summary>
        public float SideOf(int spot)
        {
            float dom = VRConfig.LeftHanded ? -1f : 1f;
            switch ((Spot)spot)
            {
                case Spot.BackDominant:
                case Spot.HandgunDominant:
                case Spot.GearDominant:
                    return dom;
                case Spot.BackOff:
                case Spot.HandgunOff:
                case Spot.GearOff:
                    return -dom;
                default:
                    return 0f;
            }
        }

        private Vector3 Position(Spot s)
        {
            if (Boneworks)
            {
                switch (s)
                {
                    case Spot.BackDominant: return BackShoulder;
                    case Spot.BackOff: return OffBackShoulder;
                    case Spot.HandgunDominant: return DomUnderarm;
                    case Spot.HandgunOff: return OffUnderarm;
                    case Spot.Gear: return LowerBack;
                    case Spot.GearOff: return OffBeltFront;
                    default: return DomBeltFront;
                }
            }
            switch (s)
            {
                case Spot.BackDominant: return BackShoulder;
                case Spot.BackOff: return OffBackShoulder;
                case Spot.HandgunDominant: return DomHip;
                case Spot.HandgunOff: return OffHip + TorsoForward * 0.06f; // cross-draw, ahead of the hip
                case Spot.Gear: return Chest;
                case Spot.GearOff: return OffChest;
                default: return DomChest;
            }
        }

        private static void Check(Vector3 p, Vector3 zone, int id, ref float best, ref int slot)
        {
            float d = Vector3.Distance(p, zone);
            if (d < best)
            {
                best = d;
                slot = id;
            }
        }

        public bool InPouch(Vector3 point) => Vector3.Distance(point, Pouch) < PouchRadius;

        /// <summary>
        /// At the body: on a holster, or against the torso (within a forearm of the line from the neck to the hips). Letting
        /// go of a weapon here puts it away instead of dropping it.
        /// </summary>
        public bool AtBody(Vector3 point)
        {
            if (HolsterAt(point) >= 0)
            {
                return true;
            }
            Transform head = _rig.Head;
            Vector3 flat = point - head.position;
            float below = -flat.y;
            flat.y = 0f;
            float waistDrop = head.position.y - (DomHip.y - 0.12f);
            return below > 0.12f && below < waistDrop && flat.magnitude < 0.3f;
        }

        /// <summary>
        /// Out to the off-hand side of the head, at or above eye level and not in front of the face: the night-vision
        /// gesture must not fire while the hand works a charging handle just under the eye of a shouldered rifle.
        /// </summary>
        private bool BesideHead(Vector3 point)
        {
            Transform head = _rig.Head;
            Vector3 local = head.InverseTransformPoint(point);
            float side = VRConfig.LeftHanded ? 1f : -1f; // the off hand's side of the head
            return local.x * side > 0.06f && local.y > -0.04f && local.z < 0.08f;
        }

        private void DrawDebug()
        {
            bool show = VRConfig.ShowGrabZones.Value;
            if (!show)
            {
                if (_debug != null)
                {
                    foreach (GameObject g in _debug)
                    {
                        Object.Destroy(g);
                    }
                    _debug = null;
                }
                return;
            }
            // Holsters in use (blue), then the pouch (amber) and the night-vision point.
            AssignHolsters(GameContext.PlayerActor);
            if (_debug == null)
            {
                _debug = new GameObject[SpotCount + 2];
                for (int i = 0; i < _debug.Length; i++)
                {
                    bool pouch = i == SpotCount;
                    _debug[i] = DebugSphere(pouch ? new Color(1f, 0.7f, 0.2f, 0.35f) : new Color(0.3f, 0.7f, 1f, 0.3f), pouch ? PouchRadius : HolsterRadius);
                }
            }
            for (int i = 0; i < SpotCount; i++)
            {
                _debug[i].SetActive(_spotSlot[i] >= 0);
                _debug[i].transform.position = Position((Spot)i);
            }
            _debug[SpotCount].transform.position = Pouch;
            _debug[SpotCount + 1].transform.position = HeadSide;
        }

        public static GameObject DebugSphere(Color c, float radius)
        {
            GameObject g = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            Object.Destroy(g.GetComponent<Collider>());
            g.layer = 5;
            g.transform.localScale = Vector3.one * radius * 2f;
            var mat = new Material(Shader.Find("Sprites/Default")) { color = c };
            g.GetComponent<Renderer>().sharedMaterial = mat;
            return g;
        }

        public void Destroy()
        {
            if (_debug != null)
            {
                foreach (GameObject g in _debug)
                {
                    Object.Destroy(g);
                }
                _debug = null;
            }
        }
    }
}
