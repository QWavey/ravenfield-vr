using RavenfieldVR.Core;
using RavenfieldVR.Input;
using RavenfieldVR.Runtime;
using UnityEngine;
using UnityEngine.Rendering;

namespace RavenfieldVR.Rig
{
    /// <summary>
    /// Procedural articulated glove: palm, cuff and five three-segment fingers built from Unity's primitive
    /// meshes, posed every frame from SteamVR skeletal curls or role-specific grips.
    /// Built in palm space for a right hand (origin = palm centre, +Z = pointing direction, +Y = top/thumb side,
    /// palm facing -X) and mirrored on X for the left hand.
    /// </summary>
    public sealed class HandModel
    {
        private const int FpLayer = 18;

        private static Material _sharedMaterial;
        private static Mesh _capsule, _cube, _sphere;

        private readonly VRHand _hand;
        private readonly Transform _root;
        private readonly Transform[][] _joints = new Transform[5][];
        private readonly Quaternion[][] _restRotations = new Quaternion[5][];
        private readonly float[] _curl = new float[5];
        private readonly Renderer[] _renderers;
        private bool _visible = true;

        // Knuckle positions (right hand, palm space) and segment lengths in metres; index 0 = thumb.
        private static readonly Vector3[] Knuckles =
        {
            new Vector3(-0.016f, 0.028f, -0.022f),
            new Vector3(-0.004f, 0.030f, 0.046f),
            new Vector3(-0.002f, 0.010f, 0.050f),
            new Vector3(-0.002f, -0.010f, 0.047f),
            new Vector3(-0.004f, -0.029f, 0.041f)
        };

        private static readonly float[][] Lengths =
        {
            new[] { 0.036f, 0.031f, 0.026f },
            new[] { 0.042f, 0.025f, 0.021f },
            new[] { 0.046f, 0.029f, 0.022f },
            new[] { 0.043f, 0.027f, 0.021f },
            new[] { 0.034f, 0.020f, 0.019f }
        };

        private static readonly float[] Radii = { 0.0105f, 0.0090f, 0.0094f, 0.0090f, 0.0080f };
        private static readonly float[] MaxCurl = { 55f, 85f, 95f, 70f };

        public HandModel(VRHand hand)
        {
            _hand = hand;
            _root = new GameObject("Hand Model").transform;
            _root.SetParent(hand.Root, false);
            EnsureAssets();

            float m = hand.Mirror;
            Part(_root, _cube, new Vector3(-0.002f * m, 0f, 0.002f), Quaternion.identity, new Vector3(0.026f, 0.072f, 0.086f));
            Part(_root, _sphere, new Vector3(0.006f * m, 0f, -0.004f), Quaternion.identity, new Vector3(0.024f, 0.074f, 0.09f));
            Part(_root, _capsule, new Vector3(0.002f * m, 0f, -0.07f), Quaternion.Euler(90f, 0f, 0f), new Vector3(0.05f, 0.032f, 0.06f));

            for (int f = 0; f < 5; f++)
            {
                _joints[f] = new Transform[3];
                _restRotations[f] = new Quaternion[3];
                Transform parent = _root;
                Vector3 local = Mirror(Knuckles[f]);
                Quaternion baseRot = f == 0
                    ? Quaternion.Euler(-28f, -32f * m, 0f) // thumb points forward and up along the top of the grip
                    : Quaternion.Euler(0f, (f - 2.5f) * 2.5f * m, 0f);
                for (int s = 0; s < 3; s++)
                {
                    var joint = new GameObject($"F{f}J{s}").transform;
                    joint.SetParent(parent, false);
                    joint.localPosition = local;
                    joint.localRotation = s == 0 ? baseRot : Quaternion.identity;
                    _restRotations[f][s] = joint.localRotation;
                    float len = Lengths[f][s];
                    float r = Radii[f] * (1f - 0.12f * s);
                    Part(joint, _capsule, new Vector3(0f, 0f, len * 0.5f), Quaternion.Euler(90f, 0f, 0f), new Vector3(r * 2f, len * 0.5f + r, r * 2f));
                    _joints[f][s] = joint;
                    parent = joint;
                    local = new Vector3(0f, 0f, len);
                }
            }
            _renderers = _root.GetComponentsInChildren<Renderer>(true);
        }

        private Vector3 Mirror(Vector3 v) => new Vector3(v.x * _hand.Mirror, v.y, v.z);

        private static void Part(Transform parent, Mesh mesh, Vector3 pos, Quaternion rot, Vector3 scale)
        {
            var go = new GameObject("part");
            go.layer = FpLayer;
            go.transform.SetParent(parent, false);
            go.transform.localPosition = pos;
            go.transform.localRotation = rot;
            go.transform.localScale = scale;
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterial = _sharedMaterial;
            mr.shadowCastingMode = ShadowCastingMode.Off;
            mr.receiveShadows = true;
            mr.lightProbeUsage = LightProbeUsage.BlendProbes;
        }

        private static void EnsureAssets()
        {
            if (_capsule == null)
            {
                _capsule = PrimitiveMesh(PrimitiveType.Capsule);
                _cube = PrimitiveMesh(PrimitiveType.Cube);
                _sphere = PrimitiveMesh(PrimitiveType.Sphere);
            }
            if (_sharedMaterial == null)
            {
                Shader shader = Shader.Find("Standard") ?? Shader.Find("Legacy Shaders/Diffuse") ?? Shader.Find("Sprites/Default");
                _sharedMaterial = new Material(shader) { name = "RavenfieldVR Glove", color = new Color(0.16f, 0.15f, 0.13f) };
                if (_sharedMaterial.HasProperty("_Glossiness"))
                {
                    _sharedMaterial.SetFloat("_Glossiness", 0.18f);
                }
            }
        }

        private static Mesh PrimitiveMesh(PrimitiveType type)
        {
            GameObject tmp = GameObject.CreatePrimitive(type);
            Mesh mesh = tmp.GetComponent<MeshFilter>().sharedMesh;
            Object.DestroyImmediate(tmp);
            return mesh;
        }

        /// <summary>Tints all gloves, e.g. to the player's team colour.</summary>
        public static void SetGloveColor(Color c)
        {
            EnsureAssets();
            _sharedMaterial.color = c;
        }

        public void SetVisible(bool visible)
        {
            if (_visible == visible)
            {
                return;
            }
            _visible = visible;
            foreach (Renderer r in _renderers)
            {
                r.enabled = visible;
            }
        }

        /// <summary>Applies placement and finger curls. Call after the hand pose and any override are set.</summary>
        public void Apply()
        {
            // Briefly out of the cameras' view the hand holds its last pose relative to the body (as the gun in it
            // does), so the model keeps being placed there instead of freezing in the world.
            if (!_hand.HasPose)
            {
                return;
            }
            SetVisible(!Suppressed);
            if (Suppressed)
            {
                return;
            }

            Vector3 posOffset = VRConfig.ParseVector(VRConfig.HandModelPosition, Vector3.zero);
            posOffset.x *= _hand.Mirror;
            Quaternion rotOffset = Quaternion.Euler(_hand.MirrorEuler(VRConfig.ParseVector(VRConfig.HandModelRotation, Vector3.zero)));
            // Always the tracked hand: held objects follow the hand, never the reverse.
            RigidPose target = _hand.Palm.Multiply(new RigidPose(posOffset, rotOffset));
            _root.SetPositionAndRotation(target.position, target.rotation);

            float[] goal = TargetCurls();
            float k = 1f - Mathf.Exp(-25f * Time.unscaledDeltaTime);
            for (int f = 0; f < 5; f++)
            {
                _curl[f] = Mathf.Lerp(_curl[f], goal[f], k);
                float sign = -_hand.Mirror; // fingers curl toward the palm (-X on the right hand)
                for (int s = 0; s < 3; s++)
                {
                    float maxAngle = f == 0 ? MaxCurl[0] * (s == 0 ? 0.6f : 1f) : MaxCurl[s + 1];
                    _joints[f][s].localRotation = _restRotations[f][s] * Quaternion.Euler(0f, sign * maxAngle * _curl[f], 0f);
                }
            }
        }

        private readonly float[] _goal = new float[5];

        /// <summary>
        /// The held weapon's own finger pose for this hand's role (from the mod's arm rig), or null for the generic
        /// pose. Used as how far each finger may open while holding, so the hand wraps this gun the way its author
        /// animated it while the tracked fingers keep moving on top.
        /// </summary>
        public float[] WeaponGripCurls;
        /// <summary>The weapon's own arm is shown for this hand instead (Hands = weapon's arms).</summary>
        public bool Suppressed;
        public float[] WeaponSupportCurls;

        /// <summary>
        /// Finger targets: the tracked fingers (SteamVR skeletal curls, or trigger/grip/thumb input) always drive the
        /// pose; a held object only sets how far each finger can open, so fingers never freeze around a weapon.
        /// </summary>
        private float[] TargetCurls()
        {
            Hand side = _hand.Side;
            float trigger = VRInput.GetTrigger(side);
            float grip = VRInput.GetGrip(side);
            if (VRInput.HasSkeleton(side))
            {
                for (int f = 0; f < 5; f++)
                {
                    _goal[f] = VRInput.GetCurl(side, f);
                }
            }
            else
            {
                Fill(grip * 0.6f, trigger, grip, grip, grip);
            }

            switch (_hand.Role)
            {
                case HandRole.Weapon:
                    // Trigger finger follows the trigger; the other fingers wrap the grip but still move.
                    if (WeaponGripCurls != null)
                    {
                        float[] c = WeaponGripCurls;
                        _goal[1] = Mathf.Lerp(Pose(c[1], 0.3f) * 0.85f, 0.85f, trigger);
                        Minimum(Pose(c[0], 0.35f), -1f, Pose(c[2], 0.7f), Pose(c[3], 0.72f), Pose(c[4], 0.74f));
                    }
                    else
                    {
                        _goal[1] = Mathf.Lerp(0.3f, 0.82f, trigger);
                        Minimum(0.35f, -1f, 0.7f, 0.72f, 0.74f);
                    }
                    break;
                case HandRole.Foregrip:
                    if (WeaponSupportCurls != null)
                    {
                        float[] c = WeaponSupportCurls;
                        Minimum(Pose(c[0], 0.3f), Pose(c[1], 0.55f), Pose(c[2], 0.6f), Pose(c[3], 0.62f), Pose(c[4], 0.65f));
                    }
                    else
                    {
                        Minimum(0.3f, 0.55f, 0.6f, 0.62f, 0.65f);
                    }
                    break;
                case HandRole.Cockpit:
                    Minimum(0.3f, 0.55f, 0.6f, 0.62f, 0.65f);
                    break;
                case HandRole.Magazine:
                case HandRole.Throwable:
                    Minimum(0.3f, 0.45f, 0.5f, 0.52f, 0.55f);
                    break;
                case HandRole.Slide:
                case HandRole.Hinge:
                case HandRole.Pin:
                case HandRole.Belt:
                    Minimum(0.35f, 0.4f, 0.55f, 0.6f, 0.62f);
                    break;
                case HandRole.Pointer:
                    _goal[1] = Mathf.Min(_goal[1], 0.1f);
                    Minimum(-1f, -1f, 0.8f, 0.82f, 0.84f);
                    break;
            }
            return _goal;
        }

        /// <summary>A measured curl, kept within what still reads as holding something; the fallback when unmeasured.</summary>
        private static float Pose(float measured, float fallback) => measured < 0f ? fallback : Mathf.Clamp(measured, 0.15f, 0.92f);

        private void Minimum(float thumb, float index, float middle, float ring, float pinky)
        {
            _goal[0] = Mathf.Max(_goal[0], thumb);
            _goal[1] = Mathf.Max(_goal[1], index);
            _goal[2] = Mathf.Max(_goal[2], middle);
            _goal[3] = Mathf.Max(_goal[3], ring);
            _goal[4] = Mathf.Max(_goal[4], pinky);
        }

        private void Fill(float thumb, float index, float middle, float ring, float pinky)
        {
            _goal[0] = thumb;
            _goal[1] = index;
            _goal[2] = middle;
            _goal[3] = ring;
            _goal[4] = pinky;
        }

        public void Destroy()
        {
            if (_root != null)
            {
                Object.Destroy(_root.gameObject);
            }
        }
    }
}
