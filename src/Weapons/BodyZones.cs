using RavenfieldVR.Core;
using RavenfieldVR.Input;
using RavenfieldVR.Rig;
using UnityEngine;

namespace RavenfieldVR.Weapons
{
    /// <summary>
    /// Body-relative interaction points derived from the head pose and calibrated height: holsters
    /// (primary over the shoulder, sidearm on the hip, gear on the chest), the magazine pouch, the
    /// night-vision touch point beside the head and the look-at-your-wrist gesture.
    /// The torso yaw lags the head so glancing sideways doesn't swing the holsters away.
    /// </summary>
    public sealed class BodyZones
    {
        private readonly VRRig _rig;
        private float _torsoYaw;
        private bool _hasYaw;

        public Vector3 Chest, OffHip, DomHip, BackShoulder, OffChest, DomChest, Pouch, HeadSide;
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

            UpdateGestures();
            DrawDebug();
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

            if (off.GrabPressed && off.Role == HandRole.Free && Vector3.Distance(off.Palm.position, HeadSide) < 0.13f)
            {
                VRGameInput.GogglesGesturePressed = true;
                VRInput.Consume(DigitalAction.Grab, off.Side);
                off.Haptic(0.04f, 120f, 0.5f);
            }
        }

        /// <summary>Holster slot under <paramref name="point"/>, or -1. Slots: 0 primary, 1 sidearm, 2-4 gear.</summary>
        public int HolsterAt(Vector3 point)
        {
            float best = HolsterRadius;
            int slot = -1;
            Check(point, BackShoulder, 0, ref best, ref slot);
            Check(point, DomHip, 1, ref best, ref slot);
            Check(point, Chest, 2, ref best, ref slot);
            Check(point, OffChest, 3, ref best, ref slot);
            Check(point, DomChest, 4, ref best, ref slot);
            return slot;
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
            Vector3[] points = { BackShoulder, DomHip, Chest, OffChest, DomChest, Pouch, HeadSide };
            if (_debug == null)
            {
                _debug = new GameObject[points.Length];
                for (int i = 0; i < points.Length; i++)
                {
                    _debug[i] = DebugSphere(i == 5 ? new Color(1f, 0.7f, 0.2f, 0.35f) : new Color(0.3f, 0.7f, 1f, 0.3f), i == 5 ? PouchRadius : HolsterRadius);
                }
            }
            for (int i = 0; i < points.Length; i++)
            {
                _debug[i].transform.position = points[i];
            }
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
