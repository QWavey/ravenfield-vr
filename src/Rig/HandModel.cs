using RavenfieldVR.Core;
using RavenfieldVR.Input;
using RavenfieldVR.Runtime;
using UnityEngine;
using UnityEngine.Rendering;

namespace RavenfieldVR.Rig
{
    /// <summary>
    /// Procedural articulated glove: one skinned mesh (<see cref="GloveMesh"/>) over a palm bone and five three-joint
    /// fingers, posed every frame from SteamVR skeletal curls or role-specific grips.
    /// Built in palm space for a right hand (origin = palm centre, +Z = pointing direction, +Y = top/thumb side,
    /// palm facing -X) and mirrored on X for the left hand.
    /// </summary>
    public sealed class HandModel
    {
        private const int FpLayer = 18;

        private static Material _sharedMaterial;

        private readonly VRHand _hand;
        private readonly Transform _root;
        private readonly Transform[][] _joints = new Transform[5][];
        private readonly Quaternion[][] _restRotations = new Quaternion[5][];
        private readonly float[] _curl = new float[5];
        private readonly Renderer _renderer;
        private readonly Quaternion _thumbRest;
        private Quaternion _thumbBase;
        private bool _visible = true;
        private Vector3 _graspOffset;

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

        /// <summary>Between the thumb and index tips (right hand, palm space): where a pinched bolt handle sits.</summary>
        private static readonly Vector3 PinchPoint = new Vector3(-0.028f, 0.024f, 0.062f);

        public HandModel(VRHand hand)
        {
            _hand = hand;
            _root = new GameObject("Hand Model").transform;
            _root.SetParent(hand.Root, false);
            EnsureMaterial();

            float m = hand.Mirror;
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
                    _joints[f][s] = joint;
                    parent = joint;
                    local = new Vector3(0f, 0f, Lengths[f][s]);
                }
            }
            _thumbRest = _restRotations[0][0];
            _thumbBase = _thumbRest;

            // One skinned glove over the bones, built at rest.
            var bones = new Transform[16];
            bones[0] = _root;
            for (int f = 0; f < 5; f++)
            {
                for (int s = 0; s < 3; s++)
                {
                    bones[1 + f * 3 + s] = _joints[f][s];
                }
            }
            Mesh mesh = GloveMesh.Build(_root, _joints, Lengths, Radii, m);
            var bind = new Matrix4x4[bones.Length];
            for (int i = 0; i < bones.Length; i++)
            {
                bind[i] = bones[i].worldToLocalMatrix * _root.localToWorldMatrix;
            }
            mesh.bindposes = bind;
            var go = new GameObject("Glove");
            go.layer = FpLayer;
            go.transform.SetParent(_root, false);
            var smr = go.AddComponent<SkinnedMeshRenderer>();
            smr.sharedMesh = mesh;
            smr.bones = bones;
            smr.rootBone = _root;
            smr.sharedMaterial = _sharedMaterial;
            smr.quality = SkinQuality.Bone2;
            smr.updateWhenOffscreen = false;
            smr.localBounds = new Bounds(new Vector3(0f, 0f, 0.02f), new Vector3(0.2f, 0.2f, 0.32f));
            smr.shadowCastingMode = ShadowCastingMode.Off;
            smr.receiveShadows = true;
            smr.lightProbeUsage = LightProbeUsage.BlendProbes;
            smr.skinnedMotionVectors = false;
            _renderer = smr;
        }

        private Vector3 Mirror(Vector3 v) => new Vector3(v.x * _hand.Mirror, v.y, v.z);

        private static void EnsureMaterial()
        {
            if (_sharedMaterial != null)
            {
                return;
            }
            Shader shader = Shader.Find("Standard") ?? Shader.Find("Legacy Shaders/Bumped Diffuse") ?? Shader.Find("Legacy Shaders/Diffuse");
            _sharedMaterial = new Material(shader) { name = "RavenfieldVR Glove", color = new Color(0.16f, 0.15f, 0.13f) };
            // Matte fabric: barely any gloss, a fine weave in the normals.
            if (_sharedMaterial.HasProperty("_Glossiness"))
            {
                _sharedMaterial.SetFloat("_Glossiness", 0.12f);
            }
            if (_sharedMaterial.HasProperty("_Metallic"))
            {
                _sharedMaterial.SetFloat("_Metallic", 0f);
            }
            if (_sharedMaterial.HasProperty("_BumpMap"))
            {
                _sharedMaterial.SetTexture("_BumpMap", GloveMesh.Weave());
                _sharedMaterial.SetTextureScale("_BumpMap", new Vector2(6f, 6f));
                _sharedMaterial.SetFloat("_BumpScale", 0.6f);
                _sharedMaterial.EnableKeyword("_NORMALMAP");
            }
        }

        /// <summary>Tints all gloves, e.g. to the player's team colour.</summary>
        public static void SetGloveColor(Color c)
        {
            EnsureMaterial();
            _sharedMaterial.color = c;
        }

        public void SetVisible(bool visible)
        {
            if (_visible == visible)
            {
                return;
            }
            _visible = visible;
            _renderer.enabled = visible;
        }

        /// <summary>Renderers and draw calls of one hand (for the measurements in the docs and self-test).</summary>
        public int RendererCount => 1;

        /// <summary>
        /// While the hand works a part (racks a bolt, holds a handguard or a cover) the glove settles onto it: the point it
        /// grips with lands on <see cref="GraspAnchor"/> (within a few centimetres; the tracked hand still decides
        /// where it is, nothing pins it). Null when the hand holds nothing it should settle on.
        /// </summary>
        public Vector3? GraspAnchor;
        /// <summary>True: the anchor is pinched between thumb and index (a bolt handle); false: it runs through the fist.</summary>
        public bool GraspPinch;

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
            float dt = Time.unscaledDeltaTime;

            // The fist closes around the controller's handle (the grip pose), so the palm sits beside it.
            Vector3 posOffset = VRConfig.ParseVector(VRConfig.HandModelPosition, Vector3.zero) - VRHand.FistHollow;
            posOffset.x *= _hand.Mirror;
            Quaternion rotOffset = Quaternion.Euler(_hand.MirrorEuler(VRConfig.ParseVector(VRConfig.HandModelRotation, Vector3.zero)));
            // Always the tracked hand: held objects follow the hand, never the reverse.
            RigidPose target = _hand.Palm.Multiply(new RigidPose(posOffset, rotOffset));

            // Settling onto a held part: a small, smoothed offset, released as soon as the hand lets go.
            Vector3 want = Vector3.zero;
            if (GraspAnchor.HasValue)
            {
                Vector3 local = GraspPinch ? PinchPoint : new Vector3(VRHand.FistHollow.x, VRHand.FistHollow.y, VRHand.FistHollow.z);
                local.x *= _hand.Mirror;
                want = Vector3.ClampMagnitude(GraspAnchor.Value - target.TransformPoint(local), 0.05f);
            }
            _graspOffset = Vector3.Lerp(_graspOffset, want, 1f - Mathf.Exp(-(GraspAnchor.HasValue ? 30f : 18f) * dt));
            _root.SetPositionAndRotation(target.position + _graspOffset, target.rotation);

            float[] goal = TargetCurls();
            float k = 1f - Mathf.Exp(-25f * dt);
            // The thumb lies along the frame of a held gun (beside the slide, not across it); elsewhere it rests up.
            Quaternion thumbGoal = _hand.Role == HandRole.Weapon ? Quaternion.Euler(-6f, -16f * _hand.Mirror, 0f) : _thumbRest;
            _thumbBase = Quaternion.Slerp(_thumbBase, thumbGoal, k);
            for (int f = 0; f < 5; f++)
            {
                _curl[f] = Mathf.Lerp(_curl[f], goal[f], k);
                float sign = -_hand.Mirror; // fingers curl toward the palm (-X on the right hand)
                for (int s = 0; s < 3; s++)
                {
                    float maxAngle = f == 0 ? MaxCurl[0] * (s == 0 ? 0.6f : 1f) : MaxCurl[s + 1];
                    Quaternion rest = f == 0 && s == 0 ? _thumbBase : _restRotations[f][s];
                    _joints[f][s].localRotation = rest * Quaternion.Euler(0f, sign * maxAngle * _curl[f], 0f);
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
        /// <summary>The held weapon has a pistol grip held in one hand (the thumb lies along the frame).</summary>
        public bool WeaponIsPistol;
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
                        Minimum(-1f, -1f, Pose(c[2], 0.7f), Pose(c[3], 0.72f), Pose(c[4], 0.74f));
                    }
                    else
                    {
                        _goal[1] = Mathf.Lerp(0.3f, 0.82f, trigger);
                        Minimum(-1f, -1f, 0.7f, 0.72f, 0.74f);
                    }
                    // The thumb rides along the frame on a pistol and wraps a rifle's grip only a little; the mod's own
                    // thumb curl is measured on its own thumb rest pose and would push ours across the slide.
                    _goal[0] = WeaponIsPistol ? Mathf.Lerp(0.06f, 0.2f, _goal[0]) : Mathf.Lerp(0.25f, 0.45f, _goal[0]);
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
                case HandRole.Optic:
                    // Pinching a bolt or handle: thumb and index close on it, the rest curl out of the way.
                    Minimum(0.45f, 0.55f, 0.7f, 0.75f, 0.78f);
                    break;
                case HandRole.Pointer:
                    _goal[1] = Mathf.Min(_goal[1], 0.1f);
                    Minimum(-1f, -1f, 0.8f, 0.82f, 0.84f);
                    break;
            }

            // Wrapping fingers stop on the held object instead of closing through it: at 0.85 the fist leaves about the
            // 3 cm of a pistol grip, magazine or wheel rim, at 0.75 the 4 cm of a handguard.
            float cap = HoldCap(_hand.Role);
            for (int f = 2; f < 5; f++)
            {
                _goal[f] = Mathf.Min(_goal[f], cap);
            }
            return _goal;
        }

        private static float HoldCap(HandRole role)
        {
            switch (role)
            {
                case HandRole.Weapon:
                case HandRole.Magazine:
                case HandRole.Throwable:
                case HandRole.Cockpit:
                    return 0.85f;
                case HandRole.Foregrip:
                    return 0.75f;
                default:
                    return 1f;
            }
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
