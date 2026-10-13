using RavenfieldVR.Core;
using RavenfieldVR.Input;
using RavenfieldVR.Rig;
using RavenfieldVR.Util;
using UnityEngine;

namespace RavenfieldVR.Weapons
{
    /// <summary>
    /// Body-relative interaction points derived from the head pose and calibrated height: holsters
    /// (long guns over the shoulders, handguns on the hips, gear on the chest, by what each weapon is rather than
    /// which loadout slot it came in), the magazine pouch, the night-vision touch point beside the head and the
    /// look-at-your-wrist gesture.
    /// The torso yaw lags the head so glancing sideways doesn't swing the holsters away.
    /// </summary>
    public sealed class BodyZones
    {
        private readonly VRRig _rig;
        private float _torsoYaw;
        private bool _hasYaw;

        public Vector3 Chest, OffHip, DomHip, BackShoulder, OffBackShoulder, OffChest, DomChest, Pouch, HeadSide;
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

            Vector3 neck = head.position - Vector3.up * 0.12f - TorsoForward * 0.08f;
            Chest = neck - Vector3.up * 0.22f + TorsoForward * 0.12f;
            Vector3 waist = head.position - Vector3.up * (0.44f * height) + TorsoForward * 0.06f;
            DomHip = waist + TorsoRight * (0.18f * side);
            OffHip = waist - TorsoRight * (0.18f * side);
            BackShoulder = neck + Vector3.up * 0.05f - TorsoForward * 0.12f + TorsoRight * (0.17f * side);
            OffBackShoulder = neck + Vector3.up * 0.05f - TorsoForward * 0.12f - TorsoRight * (0.17f * side);
            OffChest = Chest - TorsoRight * (0.13f * side);
            DomChest = Chest + TorsoRight * (0.13f * side) - Vector3.up * 0.08f;
            HeadSide = head.position - head.right * (0.11f * side) + head.up * 0.02f;

            switch (VRConfig.Pouch.Value)
            {
                case PouchLocation.DominantHip: Pouch = DomHip + TorsoForward * 0.04f; break;
                case PouchLocation.Chest: Pouch = Chest - Vector3.up * 0.06f; break;
                case PouchLocation.OffHandChest: Pouch = OffChest; break;
                default: Pouch = OffHip + TorsoForward * 0.05f; break;
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

        private enum Spot { BackDominant, BackOff, DominantHip, OffHip, Chest, OffChest, DominantChest }

        private enum Carry { Long, Hand, Gear }

        private const int SpotCount = 7;

        // Where each kind of weapon is carried, best place first; a full place passes it on to the next.
        private static readonly Spot[] LongSpots = { Spot.BackDominant, Spot.BackOff, Spot.Chest, Spot.OffChest, Spot.DominantChest, Spot.DominantHip, Spot.OffHip };
        private static readonly Spot[] HandSpots = { Spot.DominantHip, Spot.OffHip, Spot.DominantChest, Spot.OffChest, Spot.Chest, Spot.BackOff, Spot.BackDominant };
        private static readonly Spot[] GearSpots = { Spot.Chest, Spot.OffChest, Spot.DominantChest, Spot.OffHip, Spot.DominantHip, Spot.BackOff, Spot.BackDominant };

        // Loadout slot carried at each spot (-1 = empty), rebuilt from the player's weapons when asked.
        private readonly int[] _spotSlot = new int[SpotCount];

        /// <summary>
        /// Loadout slot whose holster is under <paramref name="point"/>, or -1. Rifles, launchers and other long weapons
        /// hang over the shoulders (dominant side first), handguns sit on the hips, grenades and gear on the chest.
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
                Spot[] order = CarryOf(w) == Carry.Long ? LongSpots : CarryOf(w) == Carry.Hand ? HandSpots : GearSpots;
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

        private static bool IsChest(Spot s) => s == Spot.Chest || s == Spot.OffChest || s == Spot.DominantChest;

        public enum SpotKind { Back, Hip, Chest }

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
                case Spot.DominantHip:
                case Spot.OffHip:
                    return SpotKind.Hip;
                default:
                    return SpotKind.Chest;
            }
        }

        /// <summary>+1 on the dominant hand's side of the body, -1 on the other, 0 in the middle (in TorsoRight units).</summary>
        public float SideOf(int spot)
        {
            float dom = VRConfig.LeftHanded ? -1f : 1f;
            switch ((Spot)spot)
            {
                case Spot.BackDominant:
                case Spot.DominantHip:
                case Spot.DominantChest:
                    return dom;
                case Spot.BackOff:
                case Spot.OffHip:
                case Spot.OffChest:
                    return -dom;
                default:
                    return 0f;
            }
        }

        private Vector3 Position(Spot s)
        {
            switch (s)
            {
                case Spot.BackDominant: return BackShoulder;
                case Spot.BackOff: return OffBackShoulder;
                case Spot.DominantHip: return DomHip;
                case Spot.OffHip: return OffHip + TorsoForward * 0.06f; // cross-draw, ahead of the hip
                case Spot.Chest: return Chest;
                case Spot.OffChest: return OffChest;
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
