using RavenfieldVR.Core;
using RavenfieldVR.Input;
using RavenfieldVR.Runtime;
using UnityEngine;
using UnityEngine.Rendering;

namespace RavenfieldVR.Rig
{
    /// <summary>
    /// The articulated glove: one skinned mesh (the glove model of <see cref="HandShape"/>) over a palm bone and five
    /// three-joint fingers, posed every frame from SteamVR skeletal curls or role-specific grips; each joint turns the way
    /// the model's own open-to-fist motion does. Built in palm space for a right hand (origin = palm centre, +Z = pointing
    /// direction, +Y = top/thumb side, palm facing -X) and mirrored on X for the left hand.
    /// </summary>
    public sealed class HandModel
    {
        private const int FpLayer = 18;

        private readonly VRHand _hand;
        private readonly HandShape _shape;
        private readonly float _scale;
        // The shape's joint data for this hand (mirrored for the left one).
        private readonly Vector3[,] _axis = new Vector3[5, 3];
        private readonly Vector3[,] _child = new Vector3[5, 3];
        private readonly Vector3[,] _padDir = new Vector3[5, 3];
        private readonly float[,] _padReach = new float[5, 3];
        private readonly Vector3[] _knuckle = new Vector3[5];
        private readonly Vector3[] _palmSkin;
        private readonly float _palmMiddleX;
        private readonly Vector3 _pinchPoint;
        private readonly Transform _root;
        private readonly Transform[][] _joints = new Transform[5][];
        private readonly Quaternion[][] _restRotations = new Quaternion[5][];
        private readonly float[] _curl = new float[5];
        private readonly Renderer _renderer;
        private readonly Quaternion _thumbRest, _thumbWeapon, _thumbFist;
        private Quaternion _thumbBase;
        private bool _visible = true;

        /// <summary>Self-test: the glove is drawn.</summary>
        internal bool Visible => _visible;
        private Vector3 _graspOffset;
        private readonly float[,] _joint = new float[5, 3];
        private readonly float[] _solved = new float[3];
        private readonly Vector3[] _chain = new Vector3[4];
        /// <summary>Per segment of the last <see cref="Chain"/>: from the bone line to where its skin closes on things (world).</summary>
        private readonly Vector3[] _chainPad = new Vector3[3];
        private float _palmPush;
        private float _slide, _slideGoal, _nextSlideSearch;

        /// <summary>
        /// Points just under the palm's skin (palm space, this hand), as deep as the held model's solid is grown: the skin
        /// touches the model exactly when one of them is in the solid.
        /// </summary>
        private Vector3[] PalmSkin => _palmSkin;

        /// <summary>
        /// The held weapon's solid (grown by a finger's radius, world space) while this hand holds it: the palm rests on it
        /// and every finger closes until it touches it. Null when the hand holds nothing measured.
        /// </summary>
        public System.Func<Vector3, bool> Solid;

        public HandModel(VRHand hand)
        {
            _hand = hand;
            _shape = HandShape.Current;
            _scale = _shape.Scale;
            _root = new GameObject("Hand Model").transform;
            _root.SetParent(hand.Root, false);
            _root.localScale = Vector3.one * _scale;

            float m = hand.Mirror;
            for (int f = 0; f < 5; f++)
            {
                _knuckle[f] = HandShape.MirrorPoint(_shape.Knuckle[f], m);
                for (int s = 0; s < 3; s++)
                {
                    _axis[f, s] = HandShape.MirrorAxis(_shape.CurlAxis[f, s], m);
                    _child[f, s] = HandShape.MirrorPoint(_shape.Child[f, s], m);
                    _padDir[f, s] = HandShape.MirrorPoint(_shape.PadDir[f, s], m);
                    // Contact is tested on the finger's axis against the solid grown by Grow: a pad reaching further than
                    // that from the bone is tested that much further out.
                    _padReach[f, s] = Mathf.Max(0f, _shape.Pad[f, s] - Weapons.GunVolume.Grow / _scale);
                }
            }
            _palmSkin = new Vector3[_shape.PalmSkin.Length];
            for (int i = 0; i < _palmSkin.Length; i++)
            {
                Vector3 p = _shape.PalmSkin[i];
                _palmSkin[i] = HandShape.MirrorPoint(new Vector3(p.x + Weapons.GunVolume.Grow / _scale, p.y, p.z), m);
            }
            _palmMiddleX = (_shape.PalmMin.x + _shape.PalmMax.x) * 0.5f;

            // Joints at the pose the mesh is bound in.
            for (int f = 0; f < 5; f++)
            {
                _joints[f] = new Transform[3];
                _restRotations[f] = new Quaternion[3];
                Transform parent = _root;
                for (int s = 0; s < 3; s++)
                {
                    var joint = new GameObject($"F{f}J{s}").transform;
                    joint.SetParent(parent, false);
                    joint.localPosition = s == 0 ? _knuckle[f] : _child[f, s - 1];
                    joint.localRotation = HandShape.MirrorRotation(_shape.Bind[f, s], m);
                    _joints[f][s] = joint;
                    parent = joint;
                }
            }

            // One skinned glove over the bones.
            var bones = new Transform[16];
            bones[0] = _root;
            for (int f = 0; f < 5; f++)
            {
                for (int s = 0; s < 3; s++)
                {
                    bones[1 + f * 3 + s] = _joints[f][s];
                }
            }
            Mesh mesh = _shape.BuildMesh(_root, _joints, m);
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
            smr.sharedMaterial = _shape.Material;
            smr.quality = _shape.Quality;
            smr.updateWhenOffscreen = false;
            smr.localBounds = new Bounds(new Vector3(-0.02f, 0f, 0.02f), new Vector3(0.24f, 0.24f, 0.34f));
            smr.shadowCastingMode = ShadowCastingMode.Off;
            smr.receiveShadows = true;
            smr.lightProbeUsage = LightProbeUsage.BlendProbes;
            smr.skinnedMotionVectors = false;
            _renderer = smr;

            // Rest: the open hand (curl 0); the thumb relaxed beside it.
            for (int f = 0; f < 5; f++)
            {
                for (int s = 0; s < 3; s++)
                {
                    _restRotations[f][s] = HandShape.MirrorRotation(_shape.Open[f, s], m);
                    _joints[f][s].localRotation = _restRotations[f][s];
                }
            }
            _thumbRest = HandShape.MirrorRotation(_shape.ThumbRest, m);
            _thumbWeapon = HandShape.MirrorRotation(_shape.ThumbWeapon, m);
            _thumbFist = HandShape.MirrorRotation(_shape.ThumbFist, m);
            _thumbBase = _thumbRest;
            _pinchPoint = PinchFromShape();
        }

        /// <summary>
        /// Between the thumb and index tips with the hand pinching (the curls a hand takes on a bolt handle), palm space:
        /// where a pinched handle sits.
        /// </summary>
        private Vector3 PinchFromShape()
        {
            Vector3 thumb = ChainLocal(0, 0.45f, 0.45f, 0.45f, _thumbRest);
            Vector3 index = ChainLocal(1, 0.55f, 0.55f, 0.55f, _thumbRest);
            return (thumb + index) * 0.5f;
        }

        /// <summary>Fingertip of finger <paramref name="f"/> with these curls, palm space (no tracking needed).</summary>
        private Vector3 ChainLocal(int f, float c0, float c1, float c2, Quaternion thumbBase)
        {
            Vector3 p = _knuckle[f];
            Quaternion r = Quaternion.identity;
            for (int s = 0; s < 3; s++)
            {
                Quaternion rest = f == 0 && s == 0 ? thumbBase : _restRotations[f][s];
                float c = s == 0 ? c0 : s == 1 ? c1 : c2;
                r = r * rest * Quaternion.AngleAxis(_shape.CurlAngle[f, s] * c, _axis[f, s]);
                p += r * _child[f, s];
            }
            return p;
        }

        private Vector3 Mirror(Vector3 v) => new Vector3(v.x * _hand.Mirror, v.y, v.z);

        /// <summary>
        /// From the controller's grip point (the fist hollow, where <see cref="VRHand.Palm"/> lies in the glove) out along the
        /// palm's normal to the palm's skin, over the part of the palm an object covering <paramref name="zMin"/>..
        /// <paramref name="zMax"/> (toward the fingers) and <paramref name="yMin"/>..<paramref name="yMax"/> (toward the thumb)
        /// lies against; palm frame, metres from the palm centre, this glove at its size. The palm is curved (its heel stands
        /// out most): an object up by the fingers rests on the flatter part there. Something held at the grip point is moved
        /// out by this plus its own half thickness to rest on the palm instead of half inside it.
        /// </summary>
        internal static float PalmSurfaceFromGrip(float zMin, float zMax, float yMin, float yMax)
        {
            HandShape shape = HandShape.Current;
            float scale = shape.Scale, grow = Weapons.GunVolume.Grow;
            float covered = float.NegativeInfinity, any = 0f;
            foreach (Vector3 p in shape.PalmSkin)
            {
                float outward = -p.x * scale, y = p.y * scale, z = p.z * scale;
                any = Mathf.Max(any, outward);
                if (z >= zMin - grow && z <= zMax + grow && y >= yMin - grow && y <= yMax + grow)
                {
                    covered = Mathf.Max(covered, outward);
                }
            }
            float skin = float.IsNegativeInfinity(covered) ? any : covered;
            return Mathf.Max(0f, skin + VRHand.FistHollow.x); // the hollow lies -FistHollow.x out from the palm centre
        }

        /// <summary>The glove's palm frame (self-test).</summary>
        internal Transform Root => _root;

        /// <summary>Self-test: the glove posed the way it was before hands closed on the model (by their pose alone).</summary>
        internal static bool ClosingDisabled;
        /// <summary>Self-test: the pulled trigger finger the earlier way (all joints to the trigger curl, through what they touch).</summary>
        internal static bool LegacyTriggerFinger;

        /// <summary>
        /// Self-test: points along the middle of every finger bone and through the middle of the palm that lie inside the
        /// held model (a finger whose axis is in the gun is buried at least its own thickness), and how many of the four
        /// fingers touch it (their skin within reach of <paramref name="touching"/>).
        /// </summary>
        internal void Measure(System.Func<Vector3, bool> inside, System.Func<Vector3, bool> touching, out int buried, out int samples, out int fingersTouching)
        {
            buried = 0;
            samples = 0;
            fingersTouching = 0;
            for (int f = 0; f < 5; f++)
            {
                Chain(f, _joint[f, 0], _joint[f, 1], _joint[f, 2]);
                for (int s = 0; s < 3; s++)
                {
                    for (int i = 1; i <= 4; i++)
                    {
                        samples++;
                        if (inside(Vector3.Lerp(_chain[s], _chain[s + 1], i * 0.25f) + _chainPad[s]))
                        {
                            buried++;
                        }
                    }
                }
                // On it: closing the finger a little further (about 3 degrees at the knuckle) meets the model.
                Chain(f, Mathf.Min(1f, _joint[f, 0] + 0.04f), Mathf.Min(1f, _joint[f, 1] + 0.04f), Mathf.Min(1f, _joint[f, 2] + 0.04f));
                bool touches = false;
                for (int s = 0; s < 3 && !touches; s++)
                {
                    Vector3 a = _chain[s] + _chainPad[s], b = _chain[s + 1] + _chainPad[s];
                    touches = touching(Vector3.LerpUnclamped(a, b, 0.4f)) || touching(Vector3.LerpUnclamped(a, b, 0.75f)) || touching(Vector3.LerpUnclamped(a, b, s == 2 ? 1.12f : 1f));
                }
                if (f > 0 && touches)
                {
                    fingersTouching++;
                }
                else if (f == 0)
                {
                    ThumbTouching = touches;
                }
            }
            Matrix4x4 m = _root.localToWorldMatrix;
            foreach (Vector3 skin in PalmSkin)
            {
                // The palm's middle, half its thickness under the skin points.
                samples++;
                if (inside(m.MultiplyPoint3x4(new Vector3(_palmMiddleX * _hand.Mirror, skin.y, skin.z))))
                {
                    buried++;
                }
            }
        }

        /// <summary>Self-test: the thumb touched the model at the last <see cref="Measure"/>.</summary>
        internal bool ThumbTouching { get; private set; }

        /// <summary>Self-test: how far the palm was moved onto the held model and each finger's joint curls.</summary>
        internal string Describe()
        {
            var sb = new System.Text.StringBuilder();
            sb.Append($"palm moved {_palmPush * 100f:0.0} cm, slid {_slide * 100f:0.0} cm along the grip, joints");
            for (int f = 0; f < 5; f++)
            {
                sb.Append($" [{_joint[f, 0]:0.00} {_joint[f, 1]:0.00} {_joint[f, 2]:0.00}]");
            }
            return sb.ToString();
        }

        /// <summary>Tints all gloves, e.g. to the player's team colour.</summary>
        public static void SetGloveColor(Color c)
        {
            Material m = HandShape.Current.Material;
            if (m != null)
            {
                m.color = c;
            }
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
            SetVisible(!Suppressed && !HiddenByHeld);
            if (Suppressed || HiddenByHeld)
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
                Vector3 local = GraspPinch ? _pinchPoint * _scale : new Vector3(VRHand.FistHollow.x * _hand.Mirror, VRHand.FistHollow.y, VRHand.FistHollow.z);
                want = Vector3.ClampMagnitude(GraspAnchor.Value - target.TransformPoint(local), 0.05f);
            }
            _graspOffset = Vector3.Lerp(_graspOffset, want, 1f - Mathf.Exp(-(GraspAnchor.HasValue ? 30f : 18f) * dt));
            // Holding a weapon by a grip it was made for: the glove takes the weapon's own hand pose there (Boneworks style),
            // easing in when the grip is taken and back out to the tracked hand when it is let go.
            _snapBlend = Mathf.MoveTowards(_snapBlend, SnapPose.HasValue ? 1f : 0f, dt / 0.08f);
            if (SnapPose.HasValue)
            {
                _lastSnap = SnapPose.Value;
            }
            Vector3 position = target.position + _graspOffset;
            Quaternion rotation = target.rotation;
            if (_snapBlend > 0f)
            {
                float e = _snapBlend * _snapBlend * (3f - 2f * _snapBlend);
                position = Vector3.Lerp(position, _lastSnap.position, e);
                rotation = Quaternion.Slerp(rotation, _lastSnap.rotation, e);
            }
            _root.SetPositionAndRotation(position, rotation);

            bool closing = Closing;
            // Resting on the same spot of the held solid (the usual case once a weapon is held), the palm settle and every
            // finger solve give the same answer: they are only worked out again when something they depend on changed.
            bool moved = closing && GripMoved();
            if (closing)
            {
                if (_hand.Role == HandRole.Weapon)
                {
                    SlideOnGrip(dt, moved);
                }
                SettlePalm(dt, moved);
            }
            else
            {
                // Let go: the palm and the slide along the grip ease back to the tracked hand instead of popping.
                float back = 1f - Mathf.Exp(-20f * dt);
                _palmPush = Mathf.Lerp(_palmPush, 0f, back);
                _slide = Mathf.Lerp(_slide, 0f, back);
                _slideGoal = 0f;
                _nextSlideSearch = 0f;
                _slideAtSolid = null;
                _palmTargetValid = false;
                _settling = false;
                if (Mathf.Abs(_palmPush) > 1e-4f)
                {
                    _root.position += PalmNormal * _palmPush;
                }
                if (Mathf.Abs(_slide) > 1e-4f)
                {
                    _root.position += _root.rotation * Vector3.down * _slide;
                }
            }

            float[] goal = TargetCurls();
            float k = 1f - Mathf.Exp(-25f * dt);
            float kJoint = 1f - Mathf.Exp(-40f * dt);
            // The pose turns the thumb's base between its rest and the fist: along the frame of a held gun or the side of a
            // handguard (Valve's relaxed thumb stood up through it), wrapped round a knife's handle, up beside a pinch.
            // An empty hand keeps the rest (its tracked thumb moves from there), except the fist a controller without finger
            // tracking makes from grip and trigger: its thumb has nothing tracked and wraps over the fingers.
            bool restThumb = _hand.Role == HandRole.Free && _pose.Id != HandPoseId.Fist;
            Quaternion thumbGoal = restThumb ? _thumbRest : Quaternion.Slerp(_thumbRest, _thumbFist, _pose.Thumb);
            _thumbBase = Quaternion.Slerp(_thumbBase, thumbGoal, k);
            for (int f = 0; f < 5; f++)
            {
                _curl[f] = Mathf.Lerp(_curl[f], goal[f], k);
                float pullNow = f == 1 && _hand.Role == HandRole.Weapon ? VRInput.GetTrigger(_hand.Side) : 0f;
                bool reuse = closing && !moved && _fingerValid[f] && !_settling
                             && Mathf.Abs(_curl[f] - _fingerGoal[f]) < 0.004f && Mathf.Abs(pullNow - _fingerPull[f]) < 0.01f
                             && (f != 0 || Quaternion.Angle(_thumbBase, thumbGoal) < 0.5f); // the thumb's base eases in too
                if (reuse)
                {
                    _solved[0] = _fingerSolved[f, 0];
                    _solved[1] = _fingerSolved[f, 1];
                    _solved[2] = _fingerSolved[f, 2];
                }
                else if (closing)
                {
                    SolveFinger(f, _curl[f]);
                    if (f == 1 && _hand.Role == HandRole.Weapon)
                    {
                        // The trigger finger rests where it touches the frame; pulling, its knuckle stays on the frame and
                        // the two outer joints curl onto the trigger until they touch it.
                        float pull = VRInput.GetTrigger(_hand.Side);
                        if (pull > 0.02f && LegacyTriggerFinger)
                        {
                            for (int s = 0; s < 3; s++)
                            {
                                _solved[s] = Mathf.Lerp(_solved[s], Mathf.Max(_solved[s], _curl[f]), pull);
                            }
                        }
                        else if (pull > 0.02f)
                        {
                            float r0 = _solved[0], r1 = _solved[1], r2 = _solved[2];
                            SolveFinger(1, 1f, r0);
                            _solved[0] = r0;
                            _solved[1] = Mathf.Lerp(r1, Mathf.Max(r1, _solved[1]), pull);
                            _solved[2] = Mathf.Lerp(r2, Mathf.Max(r2, _solved[2]), pull);
                        }
                    }
                    _fingerValid[f] = true;
                    _fingerGoal[f] = _curl[f];
                    _fingerPull[f] = pullNow;
                    _fingerSolved[f, 0] = _solved[0];
                    _fingerSolved[f, 1] = _solved[1];
                    _fingerSolved[f, 2] = _solved[2];
                }
                if (!closing)
                {
                    _fingerValid[f] = false;
                }
                for (int s = 0; s < 3; s++)
                {
                    _joint[f, s] = Mathf.Lerp(_joint[f, s], closing ? _solved[s] : _curl[f], kJoint);
                    _joints[f][s].localRotation = JointRest(f, s) * Quaternion.AngleAxis(_shape.CurlAngle[f, s] * _joint[f, s], _axis[f, s]);
                }
            }
            UpdateSkin();
        }

        // The glove's surface as capsules along the finger bones plus the palm's box, for another hand to close on.
        private readonly Vector3[] _skinA = new Vector3[15], _skinB = new Vector3[15];
        private readonly float[] _skinR = new float[15];
        private bool _skinValid;

        private void UpdateSkin()
        {
            _skinValid = _visible;
            if (!_skinValid)
            {
                return;
            }
            int n = 0;
            for (int f = 0; f < 5; f++)
            {
                for (int s = 0; s < 3; s++)
                {
                    _skinA[n] = _joints[f][s].position;
                    _skinB[n] = s < 2 ? _joints[f][s + 1].position : _joints[f][2].TransformPoint(_child[f, 2]);
                    _skinR[n] = _shape.Radius[f, s] * _scale;
                    n++;
                }
            }
        }

        /// <summary>
        /// Within <paramref name="grow"/> of this glove's skin (as last posed): for a support hand cupping this hand on a
        /// pistol, so its fingers wrap round these fingers instead of going through them.
        /// </summary>
        internal bool NearSkin(Vector3 p, float grow)
        {
            if (!_skinValid)
            {
                return false;
            }
            for (int i = 0; i < _skinA.Length; i++)
            {
                Vector3 a = _skinA[i], ab = _skinB[i] - a;
                float t = Mathf.Clamp01(Vector3.Dot(p - a, ab) / Mathf.Max(ab.sqrMagnitude, 1e-8f));
                float r = _skinR[i] + grow;
                if ((a + ab * t - p).sqrMagnitude <= r * r)
                {
                    return true;
                }
            }
            Vector3 l = _root.InverseTransformPoint(p); // palm space (the glove's scale undone)
            float g = grow / _scale;
            float x = l.x * _hand.Mirror; // as for the right hand
            Vector3 lo = _shape.PalmMin, hi = _shape.PalmMax;
            return l.z > lo.z - g && l.z < hi.z + g && l.y > lo.y - g && l.y < hi.y + g && x > lo.x - g && x < hi.x + g;
        }

        /// <summary>Holding a measured weapon by its grip or handguard: palm and fingers close on its model.</summary>
        private bool Closing => Solid != null && !ClosingDisabled && (_hand.Role == HandRole.Weapon || _hand.Role == HandRole.Foregrip || _hand.Role == HandRole.Throwable
                                                                       || _hand.Role == HandRole.Magazine || _hand.Role == HandRole.Slide);

        private Quaternion JointRest(int f, int s) => f == 0 && s == 0 ? _thumbBase : _restRotations[f][s];

        /// <summary>
        /// Rests the palm on what the hand holds: pushed out along the palm's normal while the skin would sink into the
        /// model, drawn in (up to 2 cm) while a gap is left, so the grip lies against the palm instead of in it or beside it.
        /// </summary>
        private readonly bool[] _fingerValid = new bool[5];
        private readonly float[] _fingerGoal = new float[5];
        private readonly float[] _fingerPull = new float[5];
        private readonly float[,] _fingerSolved = new float[5, 3];
        private float _palmTarget;
        private bool _palmTargetValid;
        /// <summary>The palm or the slide along the grip is still easing toward its place: the fingers follow it every frame.</summary>
        private bool _settling;

        private void SettlePalm(float dt, bool moved)
        {
            Vector3 n = PalmNormal;
            bool sliding = Mathf.Abs(_slide - _slideGoal) > 0.0005f;
            if (moved || sliding || !_palmTargetValid)
            {
                _palmTarget = PalmOffset(n);
                _palmTargetValid = true;
            }
            _palmPush = Mathf.Lerp(_palmPush, _palmTarget, 1f - Mathf.Exp(-30f * dt));
            _settling = sliding || Mathf.Abs(_palmPush - _palmTarget) > 0.0005f;
            _root.position += n * _palmPush;
        }

        private Vector3 PalmNormal => _root.rotation * new Vector3(-_hand.Mirror, 0f, 0f);

        /// <summary>How far along the palm's normal the palm has to move to just touch the model (negative: out of it).</summary>
        private float PalmOffset(Vector3 n)
        {
            if (PalmTouches(n, 0f))
            {
                for (float o = -0.002f; o >= -0.04f; o -= 0.002f)
                {
                    if (!PalmTouches(n, o))
                    {
                        return o;
                    }
                }
                return -0.04f;
            }
            for (float o = 0.002f; o <= 0.0201f; o += 0.002f)
            {
                if (PalmTouches(n, o))
                {
                    return o - 0.002f;
                }
            }
            return 0f;
        }

        /// <summary>
        /// Along the grip, toward the little finger (+) or the thumb (-), nearest first; the far steps toward the thumb are
        /// for a weapon's own hand pose that hangs under its grip (the fingers then closed on air).
        /// </summary>
        private static readonly float[] SlideSteps = { 0f, 0.005f, 0.01f, 0.015f, 0.02f, 0.025f, 0.03f, -0.005f, -0.01f, -0.015f, -0.02f, -0.03f, -0.04f };

        /// <summary>
        /// A weapon's own hand pose can sit a finger higher or lower on the grip than this glove's fingers fit (another hand
        /// size, a hand that never shows in the flat view): the middle finger then lies in the trigger guard instead of
        /// under it. A few times a second the nearest position along the grip where the middle finger can close round the
        /// grip is looked for, and the hand eases there.
        /// </summary>
        private bool _searchPending;

        private void SlideOnGrip(float dt, bool moved)
        {
            Vector3 down = _root.rotation * Vector3.down;
            _searchPending |= moved;
            if (_searchPending && Time.unscaledTime >= _nextSlideSearch)
            {
                _searchPending = false;
                _nextSlideSearch = Time.unscaledTime + 0.2f;
                Vector3 start = _root.position;
                Vector3 n = PalmNormal;
                _slideGoal = 0f;
                foreach (float step in SlideSteps)
                {
                    _root.position = start + down * step;
                    _root.position += n * PalmOffset(n);
                    SolveFinger(2, 1f);
                    // The middle finger has to close round the grip: touching it, not closing on air beside or under it.
                    if (_touched && _solved[0] >= 0.2f)
                    {
                        _slideGoal = step;
                        break;
                    }
                }
                _root.position = start;
            }
            _slide = Mathf.Lerp(_slide, _slideGoal, 1f - Mathf.Exp(-20f * dt));
            _root.position += down * _slide;
        }

        /// <summary>
        /// The transform the held solid moves with (the gun body, the magazine), and a number its owner changes when
        /// the solid's shape changes (a magazine in or out): the grip is only searched again when the hand moved on it.
        /// </summary>
        public Transform SolidAnchor;
        public int SolidVersion;
        private Vector3 _slideAtPos;
        private Quaternion _slideAtRot;
        private object _slideAtSolid;
        private int _slideAtVersion = -1;

        /// <summary>The hand sits somewhere else on the held solid than at the last grip search (3 mm, 2 degrees), or the solid changed.</summary>
        private bool GripMoved()
        {
            if (SolidAnchor == null)
            {
                return true;
            }
            Quaternion inv = Quaternion.Inverse(SolidAnchor.rotation);
            Vector3 pos = inv * (_root.position - SolidAnchor.position);
            Quaternion rot = inv * _root.rotation;
            bool moved = !ReferenceEquals(_slideAtSolid, Solid) || _slideAtVersion != SolidVersion
                         || (pos - _slideAtPos).sqrMagnitude > 0.003f * 0.003f || Quaternion.Angle(rot, _slideAtRot) > 2f;
            if (moved)
            {
                _slideAtPos = pos;
                _slideAtRot = rot;
                _slideAtSolid = Solid;
                _slideAtVersion = SolidVersion;
            }
            return moved;
        }

        private bool PalmTouches(Vector3 normal, float offset)
        {
            Matrix4x4 m = _root.localToWorldMatrix;
            Vector3 shift = normal * offset;
            foreach (Vector3 p in PalmSkin)
            {
                if (Solid(m.MultiplyPoint3x4(p) + shift))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Closes finger <paramref name="f"/> toward <paramref name="goal"/> one joint after another, as a real finger wraps
        /// a grip: the joints up to a segment that touches the model stop at the touch (found to a fraction of a degree),
        /// the joints beyond it keep closing. A segment already in the model with the finger open (the hand overlaps the
        /// model there anyway) does not stop it. Per-joint curls in <see cref="_solved"/>.
        /// </summary>
        private void SolveFinger(int f, float goal, float lockedKnuckle = -1f)
        {
            const int Steps = 10;
            // A thumb (or a support hand's finger) that starts inside what the hand holds is first opened out of it, a
            // little past straight, and closes from there: otherwise it stayed inside (the support thumb stood up through a
            // handguard) because segments that start inside never stop a finger.
            float start = 0f;
            // On a held magazine every finger does: a curved one (an AK's) can start inside a finger, which then passed
            // straight through it.
            if (lockedKnuckle < 0f && (f == 0 || _hand.Role == HandRole.Foregrip || _hand.Role == HandRole.Magazine))
            {
                Chain(f, 0f, 0f, 0f);
                if (SegmentTouches(0) || SegmentTouches(1))
                {
                    for (float c = -0.05f; c >= -0.351f; c -= 0.05f)
                    {
                        Chain(f, c, c, c);
                        if (!SegmentTouches(0) && !SegmentTouches(1))
                        {
                            start = c;
                            break;
                        }
                    }
                }
            }
            // With a locked knuckle only the two outer joints close (a trigger finger whose base lies on the frame).
            float c0 = lockedKnuckle >= 0f ? lockedKnuckle : start, c1 = start, c2 = start;
            bool l0 = lockedKnuckle >= 0f, l1 = false, l2 = false;
            Chain(f, c0, c1, c2);
            bool in0 = SegmentTouches(0), in1 = SegmentTouches(1), in2 = SegmentTouches(2);
            int guard = 0;
            for (int step = 1; step <= Steps && !l2 && guard++ < Steps + 4; step++)
            {
                float t = start + (goal - start) * step / Steps;
                float n0 = l0 ? c0 : t, n1 = l1 ? c1 : t, n2 = l2 ? c2 : t;
                if (FirstTouch(f, n0, n1, n2, in0, in1, in2) < 0)
                {
                    c0 = n0;
                    c1 = n1;
                    c2 = n2;
                    continue;
                }
                // Somewhere in this step a segment touched: find where, and close the moving joints to just before it.
                float lo = 0f, hi = 1f;
                for (int i = 0; i < 5; i++)
                {
                    float mid = (lo + hi) * 0.5f;
                    if (FirstTouch(f, c0 + (n0 - c0) * mid, c1 + (n1 - c1) * mid, c2 + (n2 - c2) * mid, in0, in1, in2) < 0)
                    {
                        lo = mid;
                    }
                    else
                    {
                        hi = mid;
                    }
                }
                int hit = FirstTouch(f, c0 + (n0 - c0) * hi, c1 + (n1 - c1) * hi, c2 + (n2 - c2) * hi, in0, in1, in2);
                c0 += (n0 - c0) * lo;
                c1 += (n1 - c1) * lo;
                c2 += (n2 - c2) * lo;
                // The joints up to the touching segment stay; the ones past it go on closing from here.
                l0 |= hit >= 0;
                l1 |= hit >= 1;
                l2 |= hit >= 2;
                step--; // the free joints still have this step to close
            }
            _solved[0] = c0;
            _solved[1] = c1;
            _solved[2] = c2;
            _touched = l0 && lockedKnuckle < 0f || l1 || l2;
        }

        /// <summary>The last <see cref="SolveFinger"/> stopped a joint on the model (the finger met it before closing all the way).</summary>
        private bool _touched;

        /// <summary>The first segment (0 = nearest the palm) touching the model with these curls, or -1.</summary>
        private int FirstTouch(int f, float c0, float c1, float c2, bool in0, bool in1, bool in2)
        {
            Chain(f, c0, c1, c2);
            if (!in0 && SegmentTouches(0))
            {
                return 0;
            }
            if (!in1 && SegmentTouches(1))
            {
                return 1;
            }
            if (!in2 && SegmentTouches(2))
            {
                return 2;
            }
            return -1;
        }

        /// <summary>Joint and tip positions of finger <paramref name="f"/> (world) for these curls, into <see cref="_chain"/>.</summary>
        private void Chain(int f, float c0, float c1, float c2)
        {
            Matrix4x4 m = _root.localToWorldMatrix;
            Vector3 p = _knuckle[f];
            Quaternion r = Quaternion.identity;
            _chain[0] = m.MultiplyPoint3x4(p);
            for (int s = 0; s < 3; s++)
            {
                float c = s == 0 ? c0 : s == 1 ? c1 : c2;
                r = r * JointRest(f, s) * Quaternion.AngleAxis(_shape.CurlAngle[f, s] * c, _axis[f, s]);
                p += r * _child[f, s];
                _chain[s + 1] = m.MultiplyPoint3x4(p);
                _chainPad[s] = _padReach[f, s] > 0f ? m.MultiplyVector(r * _padDir[f, s] * _padReach[f, s]) : Vector3.zero;
            }
        }

        private bool SegmentTouches(int s)
        {
            Vector3 a = _chain[s] + _chainPad[s], b = _chain[s + 1] + _chainPad[s];
            // The last segment reaches on into the rounded fingertip.
            float end = s == 2 ? 1.12f : 1f;
            return Solid(Vector3.LerpUnclamped(a, b, 0.4f)) || Solid(Vector3.LerpUnclamped(a, b, 0.75f)) || Solid(Vector3.LerpUnclamped(a, b, end));
        }

        /// <summary>Self-test picture: the glove with these finger curls (thumb..pinky), no tracking needed.</summary>
        internal void PreviewPose(float[] curls, bool weaponThumb = false) => PreviewPose(curls, weaponThumb ? 0.35f : 0f);

        /// <summary>Self-test picture: these curls and the thumb's base turned <paramref name="thumbBase"/> of the way to the fist.</summary>
        internal void PreviewPose(float[] curls, float thumbBase)
        {
            SetVisible(true);
            Quaternion thumb = Quaternion.Slerp(_thumbRest, _thumbFist, thumbBase);
            for (int f = 0; f < 5; f++)
            {
                for (int s = 0; s < 3; s++)
                {
                    Quaternion rest = f == 0 && s == 0 ? thumb : _restRotations[f][s];
                    _joints[f][s].localRotation = rest * Quaternion.AngleAxis(_shape.CurlAngle[f, s] * curls[f], _axis[f, s]);
                }
            }
        }

        /// <summary>What the hand holds, for its pose (set by whoever gives the hand its role).</summary>
        public WeaponClass WeaponClass;
        public WorkedPart WorkedPart;
        /// <summary>The hand holds a second magazine beside the first (H3VR palming).</summary>
        public bool Palming;
        /// <summary>The cockpit control in the hand is a flight stick (else a wheel).</summary>
        public bool CockpitStick;

        private GripPose _pose = HandPoses.Get(HandPoseId.Relaxed);

        /// <summary>The pose the hand takes now (self-test and logs).</summary>
        public HandPoseId CurrentPose => _pose.Id;

        private readonly float[] _goal = new float[5];

        /// <summary>
        /// The held weapon's own finger pose for this hand's role (from the mod's arm rig), or null for the generic
        /// pose. Used as how far each finger may open while holding, so the hand wraps this gun the way its author
        /// animated it while the tracked fingers keep moving on top.
        /// </summary>
        public float[] WeaponGripCurls;
        /// <summary>The held weapon has a pistol grip held in one hand (the thumb lies along the frame).</summary>
        public bool WeaponIsPistol;

        /// <summary>
        /// Where the glove goes while it holds a weapon by a grip that has an authored hand pose: palm centre and frame
        /// (+Z toward the fingers, +Y toward the thumb) of the weapon's own hand. Null for the tracked pose.
        /// </summary>
        public RigidPose? SnapPose;
        private RigidPose _lastSnap;
        private float _snapBlend;
        /// <summary>The weapon's own arm is shown for this hand instead (Hands = weapon's arms).</summary>
        public bool Suppressed;
        /// <summary>What the hand holds is carried without a visible hand (an ammo or medic bag).</summary>
        public bool HiddenByHeld;
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
            // A hand at rest is never ruler-straight: a slight natural curl, more toward the little finger.
            Minimum(0.1f, 0.12f, 0.16f, 0.2f, 0.24f);
            _pose = HandPoses.Get(HandPoses.For(_hand.Role, WeaponClass, WorkedPart, _hand.Held as Weapons.MagazineProp, Palming, CockpitStick));
            if (_hand.Role == HandRole.Free && !VRInput.HasSkeleton(side) && grip > 0.9f && trigger > 0.9f)
            {
                _pose = HandPoses.Get(HandPoseId.Fist);
            }
            float[] p = _pose.Curls;

            switch (_hand.Role)
            {
                case HandRole.Weapon:
                    // Trigger finger follows the trigger; the other fingers wrap the grip but still move.
                    if (WeaponGripCurls != null && SnapPose.HasValue)
                    {
                        // On the weapon's own grip pose: the fingers close as its own hand does; only the trigger
                        // finger moves with the trigger.
                        float[] c = WeaponGripCurls;
                        _goal[1] = Mathf.Lerp(Pose(c[1], 0.3f) * 0.85f, 0.85f, trigger);
                        _goal[2] = Pose(c[2], 0.7f);
                        _goal[3] = Pose(c[3], 0.72f);
                        _goal[4] = Pose(c[4], 0.74f);
                    }
                    else if (WeaponGripCurls != null)
                    {
                        float[] c = WeaponGripCurls;
                        _goal[1] = Mathf.Lerp(Pose(c[1], 0.3f) * 0.85f, 0.85f, trigger);
                        Minimum(-1f, -1f, Pose(c[2], 0.7f), Pose(c[3], 0.72f), Pose(c[4], 0.74f));
                    }
                    else
                    {
                        _goal[1] = Mathf.Lerp(p[1], 0.82f, trigger);
                        Minimum(-1f, -1f, p[2], p[3], p[4]);
                    }
                    // The thumb rides along the frame on a pistol and wraps a rifle's grip only a little; the mod's own
                    // thumb curl is measured on its own thumb rest pose and would push ours across the slide. Other grips
                    // (a launcher, a knife, a tool) by their pose.
                    _goal[0] = WeaponIsPistol ? Mathf.Lerp(0.06f, 0.2f, _goal[0])
                        : _pose.Id == HandPoseId.RifleGrip ? Mathf.Lerp(0.25f, 0.45f, _goal[0])
                        : Mathf.Lerp(p[0], Mathf.Min(1f, p[0] + 0.2f), _goal[0]);
                    break;
                case HandRole.Foregrip:
                    if (WeaponSupportCurls != null && SnapPose.HasValue)
                    {
                        float[] c = WeaponSupportCurls;
                        Fill(Pose(c[0], 0.3f), Pose(c[1], 0.55f), Pose(c[2], 0.6f), Pose(c[3], 0.62f), Pose(c[4], 0.65f));
                    }
                    else if (WeaponSupportCurls != null)
                    {
                        float[] c = WeaponSupportCurls;
                        Minimum(Pose(c[0], 0.3f), Pose(c[1], 0.55f), Pose(c[2], 0.6f), Pose(c[3], 0.62f), Pose(c[4], 0.65f));
                    }
                    else
                    {
                        Minimum(p[0], p[1], p[2], p[3], p[4]);
                    }
                    break;
                case HandRole.Pointer:
                    _goal[1] = Mathf.Min(_goal[1], p[1] + 0.05f);
                    Minimum(-1f, -1f, p[2], p[3], p[4]);
                    break;
                case HandRole.Free:
                    if (_pose.Id == HandPoseId.Fist)
                    {
                        Minimum(p[0], p[1], p[2], p[3], p[4]);
                    }
                    break;
                default:
                    // Holding or working something (a magazine, a grenade, a bolt, a pin, a wheel...): the fingers close at
                    // least as far as the pose, whatever the grip trigger reads; a firm pose is taken as it is.
                    if (_pose.Exact)
                    {
                        Fill(p[0], p[1], p[2], p[3], p[4]);
                    }
                    else
                    {
                        Minimum(p[0], p[1], p[2], p[3], p[4]);
                    }
                    break;
            }

            if (Closing)
            {
                // The model itself stops each finger where it touches: a held grip is wrapped all the way round.
                if (_hand.Role == HandRole.Weapon)
                {
                    // A rifle's thumb wraps over the grip until it meets it; a pistol's lies along the frame.
                    Minimum(WeaponIsPistol ? 0.35f : 0.95f, -1f, 0.95f, 0.95f, 0.95f);
                }
                else
                {
                    // A magazine's thumb closes on round it too (it stopped half way, short of a slim magazine).
                    Minimum(_hand.Role == HandRole.Magazine ? 0.9f : 0.55f, 0.9f, 0.95f, 0.95f, 0.95f);
                }
                return _goal;
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
