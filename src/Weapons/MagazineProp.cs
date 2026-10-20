using System.Collections.Generic;
using RavenfieldVR.Core;
using RavenfieldVR.Rig;
using RavenfieldVR.Runtime;
using UnityEngine;
using UnityEngine.Rendering;

namespace RavenfieldVR.Weapons
{
    public enum PropKind { Magazine, Shell, Rocket }

    /// <summary>
    /// A magazine, shell or rocket you can hold, drop, catch and insert. Uses a copy of the weapon's own
    /// magazine meshes when available (skinned parts are baked), otherwise a sized procedural stand-in.
    /// Its local space matches the weapon's magazine space, so seating it is a straight pose match.
    /// </summary>
    public sealed class MagazineProp
    {
        private const int HeldLayer = 18;
        private const int WorldLayer = 0;
        private static readonly List<MagazineProp> Alive = new List<MagazineProp>();

        /// <summary>Every magazine, shell and rocket prop that exists (held, palmed, sliding or lying around).</summary>
        public static IReadOnlyList<MagazineProp> All => Alive;
        private static Material _metal, _brass, _olive;

        public readonly GameObject Go;
        public readonly PropKind Kind;
        public readonly string WeaponKey;
        public int Rounds;
        /// <summary>
        /// The ammo the rounds came from. A dropped magazine keeps its rounds (pick it up and seat it to use them);
        /// if it despawns first they go back to this reserve, so leaving a half-full magazine behind costs nothing.
        /// </summary>
        public AmmoState Owner;
        /// <summary>
        /// On the rail after being pulled out of its seat: where the hand was along the rail at the pull minus the
        /// magazine's depth then, so the magazine moves with the hand's pull from there (0 when it went onto the rail
        /// from outside).
        /// </summary>
        public float RailHandOffset;
        /// <summary>
        /// Pulled out of its seat and still on the rail: it is not seated again (nor drawn home into a pistol's grip) until
        /// it has come out a little. At the grab it sits at full depth, which counted as pushed home: it re-seated at once.
        /// </summary>
        public bool FromSeat;
        /// <summary>The rail depth last frame (whether it is going in or coming out).</summary>
        public float PrevDepth;
        /// <summary>
        /// It last moved into the well (set going in, cleared going out by more than a millimetre): the hand turns into a
        /// push only seating it, never pulling one out (it snapped into the push 1.5 cm out of a pistol's grip).
        /// </summary>
        public bool MovingIn;
        /// <summary>The depth it last turned at (the deepest while it goes in, the shallowest while it comes out).</summary>
        public float TurnDepth;
        public VRHand HeldBy { get; private set; }

        /// <summary>Insertion end (top of a magazine, nose of a shell/rocket) in prop space.</summary>
        public readonly Vector3 TipLocal;
        /// <summary>Unit vector from the prop's base toward its tip, in prop space.</summary>
        public readonly Vector3 InsertDirLocal;
        /// <summary>Unit vector pointing like the weapon barrel, in prop space.</summary>
        public readonly Vector3 FrontLocal;
        /// <summary>Bottom end (base plate of a magazine, rim of a shell) in prop space.</summary>
        public readonly Vector3 BaseLocal;
        /// <summary>Half the magazine's thickness across the hand (side to side), for holding a second one beside it.</summary>
        private float _sideHalf;
        /// <summary>Half the magazine's size front to back (along the hand's pointing direction when held).</summary>
        private float _frontHalf;
        /// <summary>Half the magazine's largest size across its insertion axis (front to back or side to side): how big its base plate is.</summary>
        public float CrossHalf { get; private set; }
        /// <summary>A slim magazine (under 4.4 cm across, or a pistol's own under 8 cm): held by its base in the fist too, a little nearer the base plate and with the pistol pose (Tasks 240).</summary>
        public bool PistolSized => Kind == PropKind.Magazine && (CrossHalf < 0.022f || _pistolGun && CrossHalf < 0.04f);

        /// <summary>A pistol's magazine: held and seated as one even when its base plate makes it wider (AUTOMATICO 4.8 cm, FNX-45 6.5).</summary>
        private bool _pistolGun;
        /// <summary>
        /// A machine gun's box or drum: wider across than a hand closes round (over 11 cm), or thicker than any stick
        /// magazine (over 5 cm). Held in the box pose, further out along the fingers.
        /// </summary>
        public bool BoxSized => Kind == PropKind.Magazine && (CrossHalf > 0.055f || _sideHalf > 0.025f);

        /// <summary>Self-test: its size across where the hand holds it, and how it is held.</summary>
        internal string SizeText => $"{_frontHalf * 200f:0.0} x {_sideHalf * 200f:0.0} cm across, held as {(BoxSized ? "a box" : PistolSized ? "a pistol magazine" : "a rifle magazine")}";
        private readonly Rigidbody _body;
        /// <summary>The magazine's own shape (convex pieces of its mesh: a curved or drum magazine is not a crate).</summary>
        private Collider[] _colliders;
        private readonly HashSet<Collider> _ignoredBodies = new HashSet<Collider>();
        private float _nextBodyCheck;
        private float _bodiesForgottenAt;
        private int _playerGeneration = -1;
        /// <summary>The colliders came from the meshes' boxes (unreadable meshes) and are replaced once the model is measured.</summary>
        private bool _boxShaped;
        /// <summary>Per magazine type: the shape fitted to the rendered model (for every later magazine of the type).</summary>
        private static readonly Dictionary<string, ShapeFit.Piece[]> RenderedShapes = new Dictionary<string, ShapeFit.Piece[]>();
        /// <summary>The pieces' bounds in prop space, for distances while the colliders are off (Unity reports an empty box then).</summary>
        private Bounds[] _pieceBounds;
        private float _dropTime = -1f;
        /// <summary>How fast it was let go, and by which path (the flung-magazine log line; task 315).</summary>
        private float _dropSpeed;
        private string _dropBy;
        private bool _flungLogged;
        private static int _flungLines;
        private static readonly Collider[] FlungNear = new Collider[8];

        /// <summary>Locked onto the weapon's insertion axis (sliding in or out of the well).</summary>
        public bool OnRail
        {
            get => _onRail;
            set
            {
                if (_onRail != value && Go != null)
                {
                    // Onto the rail or off it again: eased over a moment from where it is drawn. Lining up at the edge of the
                    // window it jumped up to 3.5 cm and 35 degrees in one frame (and back to the hand when pulled off).
                    _switchFrom = RigidPose.Of(Go.transform);
                    _switchAt = Time.unscaledTime;
                }
                _onRail = value;
            }
        }

        private bool _onRail;
        private RigidPose _switchFrom;
        private float _switchAt = -1f;
        private const float SwitchBlend = 0.08f;

        /// <summary>Draws the prop at <paramref name="target"/>, eased from where it was when it went onto or off the rail.</summary>
        public void ShowAt(RigidPose target)
        {
            if (Go == null)
            {
                return;
            }
            float t = _switchAt >= 0f ? (Time.unscaledTime - _switchAt) / SwitchBlend : 1f;
            if (t < 1f)
            {
                float k = t * t * (3f - 2f * t);
                target = new RigidPose(Vector3.Lerp(_switchFrom.position, target.position, k), Quaternion.Slerp(_switchFrom.rotation, target.rotation, k));
            }
            else
            {
                _switchAt = -1f;
            }
            target.ApplyTo(Go.transform);
        }
        /// <summary>How far along the rail (0 = at the well entry, RailLength = seated).</summary>
        public float RailDepth;
        /// <summary>How far off the line the held end was when it went onto the rail (a rocket taken loosely): the hand may keep it.</summary>
        public float RailLateral0;
        /// <summary>Speed along the rail while it is drawn home by itself (a damped spring, as Boneworks drives its insert).</summary>
        public float DepthVel;
        /// <summary>
        /// Pulled from its seat in a gun that will not take it back now (an en-bloc clip under a bolt shut on a round): it
        /// slides out along its rail all the same, and is not seated again from it.
        /// </summary>
        public bool RailOutOnly;
        /// <summary>Insertion travel: how far the prop slides before it seats.</summary>
        public float RailLength { get; private set; }

        /// <summary>A well in the grip deeper than the rail: the rail starts at the grip's bottom (ReloadInteractions, task 270).</summary>
        internal void RailFromGripBottom(float length) => RailLength = Mathf.Max(RailLength, length);
        public float LastTick;

        private MagazineProp(GameObject go, PropKind kind, string key, int rounds, Bounds bounds, Vector3 insertDir, Vector3 front, bool machineGun, string fitKey)
        {
            Go = go;
            Kind = kind;
            WeaponKey = key;
            Rounds = rounds;
            InsertDirLocal = insertDir.normalized;
            FrontLocal = Vector3.ProjectOnPlane(front, InsertDirLocal).sqrMagnitude > 1e-4f
                ? Vector3.ProjectOnPlane(front, InsertDirLocal).normalized
                : Vector3.Cross(InsertDirLocal, Vector3.right).normalized;
            float extent = Mathf.Abs(Vector3.Dot(bounds.extents, Abs(InsertDirLocal)));
            TipLocal = bounds.center + InsertDirLocal * extent;
            BaseLocal = bounds.center - InsertDirLocal * extent;
            Vector3 sideLocal = Vector3.Cross(InsertDirLocal, FrontLocal).normalized;
            float sideBox = Mathf.Abs(Vector3.Dot(bounds.extents, Abs(sideLocal))), frontBox = Mathf.Abs(Vector3.Dot(bounds.extents, Abs(FrontLocal)));
            _sectionKey = key + "|" + InsertDirLocal.ToString("F3");
            _boxHalf = new Vector2(frontBox, sideBox);
            if (kind == PropKind.Magazine && GripSection(go, key, InsertDirLocal, FrontLocal, sideLocal, out float frontGrip, out float sideGrip))
            {
                // Where the hand holds it, not its box: the box of a raked or curved magazine counted part of its length
                // as width (a 25 cm magazine tipped 20 degrees, an AK's bend), and most rifle and pistol magazines were
                // taken for a machine gun's box and held out on the fingers, on the back of the hand (91 guns' pictures).
                frontBox = frontGrip;
                sideBox = sideGrip;
            }
            _sideHalf = Mathf.Clamp(sideBox, 0.006f, 0.04f);
            _frontHalf = Mathf.Clamp(frontBox, 0.006f, 0.1f);
            CrossHalf = Mathf.Clamp(Mathf.Max(frontBox, sideBox), 0.006f, 0.1f);
            switch (kind)
            {
                case PropKind.Shell:
                    RailLength = Mathf.Clamp(extent * 1.4f, 0.025f, 0.06f);
                    break;
                case PropKind.Rocket:
                    RailLength = Mathf.Clamp(extent * 0.6f, 0.1f, 0.3f); // started into the tube, it slides home itself
                    break;
                default:
                    // Magazines slide in for roughly half their length before they latch; a machine gun's box or
                    // drum hooks on with a short, heavy slide along its rails.
                    RailLength = machineGun ? Mathf.Clamp(extent * 0.35f, 0.03f, 0.06f) : Mathf.Clamp(extent * 0.9f, 0.03f, 0.09f);
                    break;
            }

            Renderer[] shown = go.GetComponentsInChildren<Renderer>(true);
            foreach (Renderer r in shown)
            {
                _boxShaped |= !ShapeFit.Readable(r);
            }
            ShapeFit.Piece[] shape;
            if (_boxShaped && kind == PropKind.Magazine && RenderedShapes.TryGetValue(ShapeKey, out ShapeFit.Piece[] rendered))
            {
                shape = rendered; // this type was measured already: its real shape from the start
                _boxShaped = false;
            }
            else
            {
                shape = fitKey != null ? ShapeFit.Fit(fitKey, shown, go.transform.worldToLocalMatrix, 0.015f, 8) : new ShapeFit.Piece[0];
            }
            if (shape.Length == 0)
            {
                shape = new[] { ShapeFit.BoxPiece(bounds.center, Vector3.Max(bounds.size, new Vector3(0.01f, 0.01f, 0.01f))) };
                _boxShaped = true;
            }
            _colliders = ShapeFit.AddColliders(go, shape).ToArray();
            _pieceBounds = new Bounds[shape.Length];
            for (int i = 0; i < shape.Length; i++)
            {
                _pieceBounds[i] = shape[i].Bounds;
            }
            if (shape.Length > 0)
            {
                // What the physics shape spans against what the copy draws, both in its own space (task 260 b: the hitbox
                // of many magazines sat low and covered only about half of them).
                Bounds span = _pieceBounds[0];
                foreach (Bounds pb in _pieceBounds)
                {
                    span.Encapsulate(pb);
                }
                Log.InfoOnce("magshape:" + key + ":" + kind, $"Magazine shape of {key} ({kind}): {shape.Length} piece(s) spanning {span.min * 100f:F1}..{span.max * 100f:F1} cm, "
                                                        + $"the copy drawn over {bounds.min * 100f:F1}..{bounds.max * 100f:F1} cm, {(_boxShaped ? "boxes" : "fitted to its vertices")}, "
                                                        + $"{shown.Length} renderer(s) ({System.Linq.Enumerable.Count(shown, r => r != null && r.enabled)} on).");
            }
            _body = go.AddComponent<Rigidbody>();
            _body.mass = kind == PropKind.Rocket ? 2f : machineGun && kind == PropKind.Magazine ? 1.5f : 0.4f;
            _body.interpolation = RigidbodyInterpolation.Interpolate;
            _body.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
            _body.isKinematic = true;
            SetCollidersEnabled(false);
            Alive.Add(this);
        }

        private static Vector3 Abs(Vector3 v) => new Vector3(Mathf.Abs(v.x), Mathf.Abs(v.y), Mathf.Abs(v.z));

        private static readonly Dictionary<string, Vector2> GripSections = new Dictionary<string, Vector2>();
        private string _sectionKey;
        private Vector2 _boxHalf;
        private bool _sectionMeasured;

        /// <summary>
        /// A magazine whose meshes cannot be read had its grip thickness from its box (a raked or curved one too wide): once
        /// its shape is measured (rendered), the band where the hand holds it is measured on that, and kept for its type.
        /// </summary>
        private void MeasureSectionFromVolume()
        {
            if (_sectionMeasured || Kind != PropKind.Magazine || _volume == null || !_volume.Ready || Go == null)
            {
                return;
            }
            _sectionMeasured = true;
            if (GripSections.TryGetValue(_sectionKey, out Vector2 known) && known.x > 0f)
            {
                // From its vertices, or measured on an earlier one: this one takes it too (one made before that kept its box's
                // widths for good, and a palmed one never measured: a pistol magazine could be held as a box; bug hunt).
                ApplySection(known.x, known.y);
                return;
            }
            Vector3 insert = InsertDirLocal, front = FrontLocal, side = Vector3.Cross(InsertDirLocal, FrontLocal).normalized;
            float length = Vector3.Dot(TipLocal - BaseLocal, insert);
            if (length < 0.03f)
            {
                return;
            }
            Transform t = Go.transform;
            Quaternion inv = Quaternion.Inverse(t.rotation);
            float reachF = Mathf.Clamp(_boxHalf.x + 0.01f, 0.02f, 0.12f), reachS = Mathf.Clamp(_boxHalf.y + 0.01f, 0.02f, 0.06f);
            const float step = 0.004f;
            float fMin = float.MaxValue, fMax = float.MinValue, sMin = float.MaxValue, sMax = float.MinValue;
            int hits = 0;
            for (float u = 0.12f; u <= 0.3f; u += 0.03f)
            {
                Vector3 c = BaseLocal + insert * (length * u);
                for (float f = -reachF; f <= reachF; f += step)
                {
                    for (float s = -reachS; s <= reachS; s += step)
                    {
                        Vector3 local = c + front * f + side * s;
                        Vector3 frame = inv * (t.TransformPoint(local) - t.position);
                        if (!_volume.InsideModelLocal(frame))
                        {
                            continue;
                        }
                        hits++;
                        fMin = Mathf.Min(fMin, f);
                        fMax = Mathf.Max(fMax, f);
                        sMin = Mathf.Min(sMin, s);
                        sMax = Mathf.Max(sMax, s);
                    }
                }
            }
            if (hits < 8)
            {
                return;
            }
            float frontHalf = (fMax - fMin) * 0.5f + step * 0.5f, sideHalf = (sMax - sMin) * 0.5f + step * 0.5f;
            GripSections[_sectionKey] = new Vector2(frontHalf, sideHalf);
            ApplySection(frontHalf, sideHalf);
        }

        private Vector3 _sectionGoal, _sectionFrom; // front half, side half, cross half
        private float _sectionAt;
        private int _sectionFrame = -1;
        private bool _sectionEasing;

        /// <summary>
        /// Takes the measured thickness where the hand holds it. In the hand it eases there over a tenth of a second: the
        /// measurement lands a few frames after the grab, and the magazine jumped in the palm (bug hunt 2026-10-16).
        /// </summary>
        private void ApplySection(float frontHalf, float sideHalf)
        {
            var goal = new Vector3(Mathf.Clamp(frontHalf, 0.006f, 0.1f), Mathf.Clamp(sideHalf, 0.006f, 0.04f), Mathf.Clamp(Mathf.Max(frontHalf, sideHalf), 0.006f, 0.1f));
            bool wasBox = BoxSized, wasPistol = PistolSized;
            if (HeldBy != null)
            {
                _sectionFrom = new Vector3(_frontHalf, _sideHalf, CrossHalf);
                _sectionGoal = goal;
                _sectionAt = Time.unscaledTime;
                _sectionEasing = true;
            }
            else
            {
                _frontHalf = goal.x;
                _sideHalf = goal.y;
                CrossHalf = goal.z;
            }
            bool box = goal.z > 0.055f || goal.y > 0.025f, pistol = goal.z < 0.022f || _pistolGun && goal.z < 0.04f;
            if (Kind == PropKind.Magazine && (wasBox != box || wasPistol != pistol))
            {
                Log.InfoOnce("magsection:" + WeaponKey, $"Magazine of {WeaponKey}: {goal.x * 200f:0.0} x {goal.y * 200f:0.0} cm where the hand holds it, held as "
                                                        + $"{(box ? "a box" : pistol ? "a pistol magazine" : "a rifle magazine")} (its box said {_boxHalf.x * 200f:0.0} x {_boxHalf.y * 200f:0.0} cm).");
            }
        }

        /// <summary>
        /// Once a frame (the hand's shape is updated once per magazine it holds), linear over 0.1 s; at once when it is not
        /// held (let go half way it kept the in-between widths until it was picked up again).
        /// </summary>
        private void EaseSection()
        {
            if (!_sectionEasing || _sectionFrame == Time.frameCount)
            {
                return;
            }
            _sectionFrame = Time.frameCount;
            float k = HeldBy != null ? Mathf.Clamp01((Time.unscaledTime - _sectionAt) / 0.1f) : 1f;
            Vector3 now = Vector3.Lerp(_sectionFrom, _sectionGoal, k);
            _frontHalf = now.x;
            _sideHalf = now.y;
            CrossHalf = now.z;
            _sectionEasing = k < 1f;
        }

        /// <summary>
        /// The magazine's half thickness front to back and side to side where a hand holds it (a band 12-30% of its length
        /// up from the base plate), from its vertices, across its insertion axis. False when its meshes cannot be read (the
        /// box is used then). Kept per weapon.
        /// </summary>
        private static bool GripSection(GameObject go, string key, Vector3 insert, Vector3 front, Vector3 side, out float frontHalf, out float sideHalf)
        {
            frontHalf = sideHalf = 0f;
            string cacheKey = key + "|" + insert.ToString("F3");
            if (key != null && GripSections.TryGetValue(cacheKey, out Vector2 known))
            {
                frontHalf = known.x;
                sideHalf = known.y;
                return known.x > 0f;
            }
            var pts = new List<Vector3>();
            Matrix4x4 toRoot = go.transform.worldToLocalMatrix;
            foreach (MeshFilter mf in go.GetComponentsInChildren<MeshFilter>(true))
            {
                Mesh m = mf.sharedMesh;
                if (m == null || !m.isReadable || m.vertexCount == 0)
                {
                    continue;
                }
                Matrix4x4 toProp = toRoot * mf.transform.localToWorldMatrix;
                Vector3[] v = m.vertices;
                int step = Mathf.Max(1, v.Length / 3000);
                for (int i = 0; i < v.Length; i += step)
                {
                    pts.Add(toProp.MultiplyPoint3x4(v[i]));
                }
            }
            bool ok = false;
            if (pts.Count >= 16)
            {
                float lo = float.MaxValue, hi = float.MinValue;
                foreach (Vector3 p in pts)
                {
                    float t = Vector3.Dot(p, insert);
                    lo = Mathf.Min(lo, t);
                    hi = Mathf.Max(hi, t);
                }
                float length = hi - lo;
                if (length > 0.02f)
                {
                    float a = lo + length * 0.12f, b = lo + length * 0.3f;
                    float fMin = float.MaxValue, fMax = float.MinValue, sMin = float.MaxValue, sMax = float.MinValue;
                    int n = 0;
                    foreach (Vector3 p in pts)
                    {
                        float t = Vector3.Dot(p, insert);
                        if (t < a || t > b)
                        {
                            continue;
                        }
                        float f = Vector3.Dot(p, front), s = Vector3.Dot(p, side);
                        fMin = Mathf.Min(fMin, f);
                        fMax = Mathf.Max(fMax, f);
                        sMin = Mathf.Min(sMin, s);
                        sMax = Mathf.Max(sMax, s);
                        n++;
                    }
                    if (n >= 8)
                    {
                        frontHalf = (fMax - fMin) * 0.5f;
                        sideHalf = (sMax - sMin) * 0.5f;
                        ok = frontHalf > 0.003f && sideHalf > 0.003f;
                    }
                }
            }
            if (key != null)
            {
                GripSections[cacheKey] = ok ? new Vector2(frontHalf, sideHalf) : Vector2.zero;
            }
            return ok;
        }

        /// <summary>
        /// The way a prop goes in and its front (gun-body space), for the prop in the hand and the one shown on the pouch
        /// alike. A rocket goes into the launcher's muzzle, tail first, back along the bore (an RPG is always loaded from
        /// the front; user, 2026-10-16: the reload animation's first motion is not that on every launcher), and its front
        /// is the launcher's up (the bore, its old front, lay along its insert and left its roll in the hand to chance).
        /// </summary>
        private static void Axes(WeaponRigData d, PropKind kind, out Vector3 insertGun, out Vector3 frontGun)
        {
            insertGun = -d.MagEjectAxis;
            frontGun = d.AimAxis;
            if (kind == PropKind.Rocket)
            {
                // Along the bore (a slightly oblique animated axis kept a rail whose depth is measured along the bore), or
                // better along the rocket's own length as it sits loaded, which is the tube's axis: the muzzle's aim can be a
                // few degrees off the tube, and over a 50-70 cm rail the booster rode along the top of an RPG-7's tube instead
                // of going into it (the user's picture, 2026-10-16: "the same bug as with pistol mags").
                insertGun = -d.AimAxis.normalized;
                frontGun = d.Up;
                Bounds mb = d.MagBounds;
                Vector3 size = mb.size;
                if (size.sqrMagnitude > 1e-6f)
                {
                    Vector3 longLocal = size.x >= size.y && size.x >= size.z ? Vector3.right : size.y >= size.z ? Vector3.up : Vector3.forward;
                    float longest = Vector3.Dot(size, longLocal), next = Mathf.Max(Vector3.Dot(size, Vector3.one - longLocal) - Mathf.Min(size.x, Mathf.Min(size.y, size.z)), 1e-4f);
                    Vector3 longGun = (d.MagSeat.rotation * longLocal).normalized;
                    if (Vector3.Dot(longGun, insertGun) < 0f)
                    {
                        longGun = -longGun;
                    }
                    // A rocket clearly longer than wide, its length within 15 degrees of the bore: that is the line it goes in on.
                    if (longest > next * 3f && Vector3.Angle(longGun, insertGun) < 15f)
                    {
                        insertGun = longGun;
                        frontGun = Vector3.ProjectOnPlane(d.Up, longGun).normalized;
                    }
                }
            }
        }

        /// <summary>Creates a prop for <paramref name="rig"/>'s ammunition at the magazine's seated pose.</summary>
        public static MagazineProp Create(WeaponRig rig, PropKind kind, int rounds)
        {
            WeaponRigData d = rig.Data;
            Axes(d, kind, out Vector3 insertGun, out Vector3 frontGun);
            Quaternion seatInv = Quaternion.Inverse(d.MagSeat.rotation);
            Vector3 insertLocal = seatInv * insertGun;
            Vector3 frontLocal = seatInv * frontGun;

            GameObject go = null;
            Bounds bounds = d.MagBounds;
            string fitKey = null;
            if (CopiesModel(rig, kind))
            {
                go = CheckedCopy(rig, kind, out bounds);
                fitKey = go != null ? "mag:" + d.Key : null;
                if (go == null)
                {
                    bounds = d.MagBounds;
                }
            }
            if (go == null && kind == PropKind.Rocket)
            {
                go = ProjectileCopy(rig, insertLocal, frontLocal, out bounds);
            }
            bool standIn = go == null;
            if (go == null)
            {
                go = new GameObject("RavenfieldVR " + kind);
                bounds = BuildProcedural(go, kind, insertLocal, frontLocal, d.MagBounds, ShotgunShells(rig));
            }
            // What the hand holds, once per weapon and kind: the gun's own part copied or a made stand-in, and which part
            // (a machine gun's box was invisible in the hand, or only its belt came out; Tasks 252 n).
            Log.InfoOnce("propkind:" + kind + ":" + d.Key, $"{kind} of {d.Key} in the hand: {(standIn ? "a made stand-in" : "a copy of the gun's own")} "
                + $"(part '{(rig.Magazine != null ? rig.Magazine.name : "none")}', {rig.MagazineParts.Length} part(s) with it), {bounds.size.x * 100f:0} x {bounds.size.y * 100f:0} x {bounds.size.z * 100f:0} cm"
                + $"{(standIn ? "" : $", {go.GetComponentsInChildren<Renderer>(true).Length} renderer(s)")}.");
            go.name = "RavenfieldVR " + kind;
            SetLayer(go, HeldLayer);
            var prop = new MagazineProp(go, kind, d.Key, rounds, bounds, insertLocal, frontLocal, d.IsMachineGun, fitKey);
            prop._pistolGun = d.IsPistol;
            if (kind == PropKind.Rocket)
            {
                // Its rail runs from the launcher's mouth to where its tail sits seated: lined up at the muzzle, in front of
                // the launcher, where a rocket is put in. It began a few tenths of the rocket's length back from the seat,
                // inside the tube, and a rocket held at the muzzle was never taken.
                // The mouth is the tube's front end (the launcher's own meshes, not its rocket): its "muzzle" point is where
                // the projectile starts, 1.1 m in front of the grip on an RPG-7, past the warhead.
                Vector3 tailSeated = d.MagSeat.TransformPoint(prop.TipLocal);
                Vector3 aim = d.AimAxis.normalized;
                bool tube = TubeFront(rig, aim, out float front);
                float mouth = tube ? front : Vector3.Dot(d.MuzzlePoint, aim);
                float depth = mouth - Vector3.Dot(tailSeated, aim);
                if (depth > 0.05f && depth < 1.8f)
                {
                    prop.RailLength = depth + 0.01f;
                }
                Log.InfoOnce("rocketrail:" + d.Key, $"Rocket rail of {d.Key}: {prop.RailLength * 100f:0} cm from the {(tube ? "tube's front" : "muzzle point")} to the seated tail "
                                                    + $"({depth * 100f:0} cm measured), along the rocket's length {Vector3.Angle(insertGun, -aim):0.0} deg off the aim.");
            }
            RigidPose seat = rig.GunBodyPose.Multiply(d.MagSeat);
            seat.ApplyTo(go.transform);
            Cull();
            return prop;
        }

        /// <summary>Visual-only copy of what the weapon feeds from, in magazine space (for the pouch display).</summary>
        internal static GameObject BuildDisplay(WeaponRig rig, PropKind kind, out Bounds bounds, out Vector3 insertLocal, out Vector3 frontLocal)
        {
            WeaponRigData d = rig.Data;
            Quaternion seatInv = Quaternion.Inverse(d.MagSeat.rotation);
            Axes(d, kind, out Vector3 insertGun, out Vector3 frontGun);
            insertLocal = (seatInv * insertGun).normalized;
            frontLocal = seatInv * frontGun;
            GameObject go = null;
            bounds = d.MagBounds;
            if (CopiesModel(rig, kind))
            {
                go = CheckedCopy(rig, kind, out bounds);
                if (go == null)
                {
                    bounds = d.MagBounds;
                }
            }
            if (go == null && kind == PropKind.Rocket)
            {
                go = ProjectileCopy(rig, insertLocal, frontLocal, out bounds);
            }
            if (go == null)
            {
                go = new GameObject("RavenfieldVR Pouch " + kind);
                bounds = BuildProcedural(go, kind, insertLocal, frontLocal, d.MagBounds, ShotgunShells(rig));
            }
            go.name = "RavenfieldVR Pouch " + kind;
            SetLayer(go, HeldLayer);
            foreach (Collider c in go.GetComponentsInChildren<Collider>(true))
            {
                Object.Destroy(c);
            }
            return go;
        }

        internal static int DisplayLayer => HeldLayer;

        /// <summary>
        /// The copy of the gun's own magazine (or rocket), checked against the analysis. A copy taken while the game had
        /// the part parked and shrunk for its reload (a BRN 180's magazine waits 25 m away at 1%) came out tiny and showed as
        /// nothing in the hand: scaled back to the analysed size, it is kept if it then sits where the magazine was
        /// measured; otherwise (or with nothing in it drawn, or a "rocket" too short to be one) the made one is used.
        /// </summary>
        private static GameObject CheckedCopy(WeaponRig rig, PropKind kind, out Bounds bounds)
        {
            WeaponRigData d = rig.Data;
            bounds = d.MagBounds;
            GameObject go = kind == PropKind.Rocket ? LoadedLook(d.Key) : null;
            if (go != null)
            {
                if (DrawsAnything(go))
                {
                    bounds = RenderedBounds(go, d.MagBounds, d.Key);
                    return go;
                }
                // A kept copy that draws nothing is not handed out: the hand held an invisible rocket (Tasks 205). The gun's
                // own parts below, or the made one, are the choices after it.
                Log.InfoOnce("rocketlook-hidden:" + d.Key, $"Rocket of {d.Key}: the copy kept as it sits loaded draws nothing in the hand; the gun's own part or the made one is used.");
                Object.Destroy(go);
                go = null;
            }
            go = CopyVisuals(rig);
            if (go == null)
            {
                return null;
            }
            bool drawn = false;
            foreach (Renderer r in go.GetComponentsInChildren<Renderer>(true))
            {
                drawn |= r.enabled && r.transform.lossyScale.sqrMagnitude > 1e-8f;
            }
            float expected = d.MagBounds.size.magnitude;
            Bounds raw = MeasureCopy(go, out bool any);
            if (drawn && any && expected > 0.02f && raw.size.magnitude > 1e-4f
                && (raw.size.magnitude < expected / 3f || raw.size.magnitude > expected * 3f))
            {
                float k = expected / raw.size.magnitude;
                foreach (Transform child in go.transform)
                {
                    child.localPosition *= k;
                    child.localScale *= k;
                }
                raw = MeasureCopy(go, out any);
                bool placed = (raw.center - d.MagBounds.center).magnitude < 0.03f;
                Log.InfoOnce("magrescale:" + d.Key, $"Magazine copy of {d.Key} came out x{1f / k:0.###} of the analysed size (the part was parked or shrunk): "
                                                     + $"scaled back, {(placed ? "kept" : $"but {((raw.center - d.MagBounds.center).magnitude * 100f):0.0} cm off its place: the made one is used")}.");
                if (!placed)
                {
                    drawn = false;
                }
            }
            if (!drawn || !any)
            {
                Object.Destroy(go);
                return null;
            }
            bounds = RenderedBounds(go, d.MagBounds, d.Key);
            if (kind == PropKind.Rocket && Longest(bounds) < 0.12f)
            {
                Object.Destroy(go); // not a rocket (a fuse, a cap): the made one instead
                return null;
            }
            return go;
        }

        /// <summary>
        /// True when some renderer of the copy is on, in an active object, and has a size (a copy scaled to nothing, or
        /// switched off, draws nothing).
        /// </summary>
        private static bool DrawsAnything(GameObject go)
        {
            foreach (Renderer r in go.GetComponentsInChildren<Renderer>(true))
            {
                if (r.enabled && r.gameObject.activeInHierarchy && r.transform.lossyScale.sqrMagnitude > 1e-8f)
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Each launcher's rocket as it looks seated and loaded (key: the weapon), copied while it shows so. A rocket from the
        /// pouch comes after the shot, and by then the game has shrunk the launcher's rocket to a few percent and moved it
        /// half a metre (the five RPG-7V variants, the scoped RPG-7V): that copy failed its check and the hand held the made
        /// olive stand-in instead of the launcher's own warhead (user, 2026-10-16).
        /// </summary>
        private static readonly Dictionary<string, GameObject> LoadedLooks = new Dictionary<string, GameObject>();
        private static readonly Dictionary<string, float> LoadedLookTried = new Dictionary<string, float>();
        private static readonly Dictionary<string, int> LoadedLookTries = new Dictionary<string, int>();
        private static GameObject _loadedLooksHolder;

        /// <summary>The last look at each launcher's loaded rocket (gun space) while its far seat waits to be taken over.</summary>
        private static readonly Dictionary<string, (RigidPose pose, float at)> SeatLooks = new Dictionary<string, (RigidPose pose, float at)>();

        /// <summary>
        /// While a loaded launcher is held: a copy of its rocket as it shows now, kept when it measures as the analysis did
        /// (its size within half again, its middle within 3 cm; rocket-sized when the analysis found no box). Tried at most
        /// twice a second for the first five tries, then every ten seconds, until one is kept; thirty tries at most (a copy
        /// is meshes made and thrown away: a launcher whose rocket never measures right would make them for as long as it is
        /// held; ten tries half a second apart were all spent in the first five seconds).
        /// </summary>
        internal static void KeepLoadedLook(WeaponRig rig)
        {
            string key = rig.Data.Key;
            WeaponRigData d = rig.Data;
            if (rig.Magazine != null && rig.GunBody != null && (d.MagSeat.position - d.GripPoint).sqrMagnitude > 1.5f * 1.5f)
            {
                // The analysed rest pose had the rocket parked away (the Panzerfaust 3's warhead waits 30 m off in its empty
                // idle, and its rocket was "seated" there: taken from the back, a 30 m rail). Loaded, it sits on the gun now.
                RigidPose live = rig.GunBodyPose.Inverse().Multiply(RigidPose.Of(rig.Magazine));
                // Taken only from a still pose outside the reload: two looks a third of a second apart within 1 cm and 1
                // degree. One frame mid-animation went into the weapon's shared data for every rig of it (review 253.13).
                bool still = false;
                if (!rig.Weapon.reloading && (live.position - d.GripPoint).sqrMagnitude < 1.5f * 1.5f)
                {
                    if (SeatLooks.TryGetValue(key, out (RigidPose pose, float at) seen) && Time.unscaledTime - seen.at >= 0.3f)
                    {
                        still = Vector3.Distance(seen.pose.position, live.position) < 0.01f && Quaternion.Angle(seen.pose.rotation, live.rotation) < 1f;
                        SeatLooks[key] = (live, Time.unscaledTime);
                    }
                    else if (!SeatLooks.ContainsKey(key))
                    {
                        SeatLooks[key] = (live, Time.unscaledTime);
                    }
                }
                if (still)
                {
                    Log.InfoOnce("rocketseat:" + key, $"Rocket of {key}: seated where it sits loaded, {(live.position - d.GripPoint).magnitude * 100f:0} cm from the grip "
                                                      + $"(the analysis had it {(d.MagSeat.position - d.GripPoint).magnitude:0.0} m off).");
                    d.MagSeat = live;
                }
            }
            if (LoadedLooks.ContainsKey(key) || !CopiesModel(rig, PropKind.Rocket)
                || LoadedLookTries.TryGetValue(key, out int tries) && tries >= 30
                || LoadedLookTried.TryGetValue(key, out float tried) && Time.unscaledTime < tried + (tries < 5 ? 0.5f : 10f))
            {
                return;
            }
            LoadedLookTried[key] = Time.unscaledTime;
            LoadedLookTries[key] = tries + 1;
            GameObject go = CopyVisuals(rig);
            if (go == null)
            {
                return;
            }
            Bounds analysed = rig.Data.MagBounds;
            Bounds b = MeasureCopy(go, out bool any);
            float size = b.size.magnitude, expected = analysed.size.magnitude;
            // Only a copy shrunk or moved away is refused (after the shot the game left it at 3% and up to 60 cm off):
            // within half again and 3 cm, the RPG-7V [PG-7VR]'s and the Panzerfaust's were never kept.
            bool fits = any && Longest(b) >= 0.12f && Longest(b) < 2f
                        && (expected < 0.02f || size > expected / 2f && size < expected * 2f && (b.center - analysed.center).magnitude < 0.08f);
            if (!fits)
            {
                Log.InfoOnce("rocketlook-no:" + key, $"Rocket of {key} not copied as it sits loaded (try {tries + 1}): {(any ? $"{Longest(b) * 100f:0} cm long, {size * 100f:0} cm across against {expected * 100f:0} analysed, middle {(b.center - analysed.center).magnitude * 100f:0.0} cm off" : "nothing drawn")}.");
                Object.Destroy(go);
                return;
            }
            if (_loadedLooksHolder == null)
            {
                _loadedLooksHolder = new GameObject("RavenfieldVR loaded rockets");
                _loadedLooksHolder.SetActive(false); // kept, never drawn
                Object.DontDestroyOnLoad(_loadedLooksHolder);
            }
            go.transform.SetParent(_loadedLooksHolder.transform, false);
            go.name = "rocket " + key;
            LoadedLooks[key] = go;
            Log.InfoOnce("rocketlook:" + key, $"Rocket of {key} copied as it sits loaded: {go.GetComponentsInChildren<MeshFilter>(true).Length} mesh(es), {Longest(b) * 100f:0} cm long.");
        }

        /// <summary>
        /// A launcher that shows no rocket of its own (a missile tube: HYDRA, SCALPEL, a signal launcher): the rocket in the
        /// hand is the projectile it fires, its own meshes, seated inside the tube with its nose at the mouth. It was a
        /// made-up 75 cm olive capsule. Null when the projectile has no mesh to show or is not rocket-sized.
        /// </summary>
        private static GameObject ProjectileCopy(WeaponRig rig, Vector3 insertLocal, Vector3 frontLocal, out Bounds bounds)
        {
            WeaponRigData d = rig.Data;
            bounds = d.MagBounds;
            GameObject prefab = rig.Weapon != null && rig.Weapon.configuration != null ? rig.Weapon.configuration.projectilePrefab : null;
            if (prefab == null)
            {
                return null;
            }
            // Its meshes in its own space (it flies along its forward), without trails, flames or glow.
            Matrix4x4 toRoot = prefab.transform.worldToLocalMatrix;
            var parts = new List<(MeshRenderer r, Mesh mesh, Matrix4x4 m)>();
            bool any = false;
            var b = new Bounds();
            foreach (MeshRenderer r in prefab.GetComponentsInChildren<MeshRenderer>(true))
            {
                MeshFilter mf = r.GetComponent<MeshFilter>();
                if (!r.enabled || mf == null || mf.sharedMesh == null || GunCollider.SeeThrough(r) || !WeaponAnalyzer.ActiveUpTo(r.transform, prefab.transform))
                {
                    continue; // (switched off in the prefab: a flame or a glow it turns on in flight)
                }
                Matrix4x4 m = toRoot * r.transform.localToWorldMatrix;
                parts.Add((r, mf.sharedMesh, m));
                Bounds mb = mf.sharedMesh.bounds;
                for (int i = 0; i < 8; i++)
                {
                    Vector3 c = m.MultiplyPoint3x4(mb.center + Vector3.Scale(mb.extents, new Vector3((i & 1) == 0 ? -1f : 1f, (i & 2) == 0 ? -1f : 1f, (i & 4) == 0 ? -1f : 1f)));
                    if (!any)
                    {
                        b = new Bounds(c, Vector3.zero);
                        any = true;
                    }
                    else
                    {
                        b.Encapsulate(c);
                    }
                }
            }
            float length = b.size.z;
            if (!any || length < 0.12f || length > 2f || Mathf.Max(b.size.x, b.size.y) > length)
            {
                Log.InfoOnce("rocketprojectile:" + d.Key, $"Rocket of {d.Key}: its projectile {prefab.name} has {(any ? $"a {b.size.x * 100f:0} x {b.size.y * 100f:0} x {length * 100f:0} cm model, not a rocket's" : "no model")}: the made one is used.");
                return null;
            }
            // Into seat space: its forward along the bore (out of the tube), its up the launcher's up, its nose at the tube's
            // front on the bore's line through the seat.
            Vector3 aimLocal = -insertLocal.normalized;
            Quaternion q = Quaternion.LookRotation(aimLocal, frontLocal);
            Vector3 aimGun = d.AimAxis.normalized;
            float seatAlong = Vector3.Dot(d.MagSeat.position, aimGun);
            bool tube = TubeFront(rig, aimGun, out float front);
            float noseAlong = tube ? front - seatAlong : Vector3.Dot(d.MuzzlePoint, aimGun) - seatAlong;
            Vector3 nosePoint = new Vector3(b.center.x, b.center.y, b.max.z);
            // On the tube's own axis when the muzzle point is off it (within 15 cm).
            Vector3 across = Vector3.zero;
            if (tube && TubeCentre(rig, aimGun, front, out Vector3 tubeCentre))
            {
                Vector3 offGun = Vector3.ProjectOnPlane(tubeCentre - d.MagSeat.position, aimGun);
                if (offGun.magnitude < 0.15f)
                {
                    across = Quaternion.Inverse(d.MagSeat.rotation) * offGun;
                    if (offGun.magnitude > 0.01f)
                    {
                        Log.InfoOnce("rocketcentre:" + d.Key, $"Rocket of {d.Key}: on the tube's axis, {offGun.magnitude * 100f:0.0} cm off the muzzle point's line.");
                    }
                }
            }
            var root = new GameObject("rocket");
            foreach ((MeshRenderer r, Mesh mesh, Matrix4x4 m) in parts)
            {
                Vector3 p = aimLocal * noseAlong + across + q * (m.MultiplyPoint3x4(Vector3.zero) - nosePoint);
                Quaternion rot = q * m.rotation;
                AddPart(root.transform, r.name, mesh, r.sharedMaterials, p, rot, m.lossyScale);
            }
            bounds = MeasureCopy(root, out _); // its own size (the analysis had a 14 cm stand-in box for a missile tube)
            Log.InfoOnce("rocketprojectile:" + d.Key, $"Rocket of {d.Key}: its projectile's own model ({prefab.name}, {parts.Count} mesh(es), {length * 100f:0} cm), seated in the tube.");
            return root;
        }

        /// <summary>
        /// Drops the looks, seats and shapes kept by weapon name before a content reload (2026-10-19): the reloaded launcher is
        /// new, so its rocket is copied and its seat measured again. The kept rocket copies are destroyed with their keys.
        /// </summary>
        internal static void ClearLooks()
        {
            foreach (GameObject look in LoadedLooks.Values)
            {
                if (look != null)
                {
                    Object.Destroy(look);
                }
            }
            LoadedLooks.Clear();
            LoadedLookTried.Clear();
            LoadedLookTries.Clear();
            SeatLooks.Clear();
            GripSections.Clear();
            RenderedShapes.Clear();
        }

        /// <summary>A new copy of the launcher's rocket as it looked loaded; null when none was kept (or its meshes are gone).</summary>
        private static GameObject LoadedLook(string key)
        {
            if (!LoadedLooks.TryGetValue(key, out GameObject look))
            {
                return null;
            }
            bool whole = look != null;
            if (whole)
            {
                foreach (MeshFilter mf in look.GetComponentsInChildren<MeshFilter>(true))
                {
                    whole &= mf.sharedMesh != null;
                }
            }
            if (!whole)
            {
                // Its meshes unloaded with their content pack: copied again the next time it shows loaded.
                if (look != null)
                {
                    Object.Destroy(look);
                }
                LoadedLooks.Remove(key);
                return null;
            }
            GameObject go = Object.Instantiate(look);
            foreach (OwnedMesh owned in go.GetComponentsInChildren<OwnedMesh>(true))
            {
                if (owned.Mesh != null)
                {
                    // A baked mesh is the kept copy's own: this one gets its own, or dropping it would take the kept one's.
                    Mesh own = Object.Instantiate(owned.Mesh);
                    own.name = owned.Mesh.name;
                    MeshFilter mf = owned.GetComponent<MeshFilter>();
                    if (mf != null)
                    {
                        mf.sharedMesh = own;
                    }
                    owned.Mesh = own;
                }
            }
            return go;
        }

        /// <summary>The copy's meshes' bounds in its own space.</summary>
        private static Bounds MeasureCopy(GameObject root, out bool any)
        {
            Matrix4x4 toRoot = root.transform.worldToLocalMatrix;
            any = false;
            var b = new Bounds();
            foreach (MeshFilter mf in root.GetComponentsInChildren<MeshFilter>(true))
            {
                if (mf.sharedMesh == null)
                {
                    continue;
                }
                Bounds mb = mf.sharedMesh.bounds;
                Matrix4x4 m = toRoot * mf.transform.localToWorldMatrix;
                for (int i = 0; i < 8; i++)
                {
                    Vector3 corner = mb.center + Vector3.Scale(mb.extents, new Vector3((i & 1) == 0 ? -1f : 1f, (i & 2) == 0 ? -1f : 1f, (i & 4) == 0 ? -1f : 1f));
                    Vector3 p = m.MultiplyPoint3x4(corner);
                    if (!any)
                    {
                        b = new Bounds(p, Vector3.zero);
                        any = true;
                    }
                    else
                    {
                        b.Encapsulate(p);
                    }
                }
            }
            return b;
        }

        /// <summary>
        /// The bounds of what a copy actually shows, in its root's space. The analysis measures the magazine part alone at
        /// its idle pose; the copy also carries the pieces that travel with it (rounds, a follower) and skinned triangles
        /// baked from the gun's mesh, and a magazine held or displayed by the analysis box sat off its real middle (pistol
        /// magazines hung low in the pouch and in the hand). A copy measuring far from the analysis keeps the analysis.
        /// </summary>
        private static Bounds RenderedBounds(GameObject root, Bounds analysed, string key)
        {
            Matrix4x4 toRoot = root.transform.worldToLocalMatrix;
            bool any = false;
            var b = new Bounds();
            foreach (MeshFilter mf in root.GetComponentsInChildren<MeshFilter>(true))
            {
                if (mf.sharedMesh == null)
                {
                    continue;
                }
                Bounds mb = mf.sharedMesh.bounds;
                Matrix4x4 m = toRoot * mf.transform.localToWorldMatrix;
                for (int i = 0; i < 8; i++)
                {
                    Vector3 corner = mb.center + Vector3.Scale(mb.extents, new Vector3((i & 1) == 0 ? -1f : 1f, (i & 2) == 0 ? -1f : 1f, (i & 4) == 0 ? -1f : 1f));
                    Vector3 p = m.MultiplyPoint3x4(corner);
                    if (!any)
                    {
                        b = new Bounds(p, Vector3.zero);
                        any = true;
                    }
                    else
                    {
                        b.Encapsulate(p);
                    }
                }
            }
            float size = b.size.magnitude, expected = analysed.size.magnitude;
            if (!any || size < 0.02f || size > 1.5f || expected > 0.02f && (size > expected * 3f || size < expected / 3f))
            {
                Log.InfoOnce("magbounds:" + key, $"Magazine copy of {key} measures {size * 100f:0.0} cm against {expected * 100f:0.0} cm analysed; the analysed size is used.");
                return analysed;
            }
            if ((b.center - analysed.center).magnitude > 0.01f || Mathf.Abs(size - expected) > 0.01f)
            {
                Log.InfoOnce("magbounds:" + key, $"Magazine copy of {key}: shown bounds centre {b.center * 100f} cm, size {size * 100f:0.0} cm (analysed {analysed.center * 100f} cm, {expected * 100f:0.0} cm).");
            }
            return b;
        }

        /// <summary>
        /// Copies the magazine's look: its own meshes, the pieces that travel with it, and for skinned guns the
        /// triangles of the gun mesh that the magazine bones drive. Everything is expressed in magazine space.
        /// </summary>
        private static GameObject CopyVisuals(WeaponRig rig)
        {
            // While the magazine is out of the gun its parts are scaled to nothing: copy them at their real size.
            Vector3[] hidden = rig.ShowMagazineForCopy();
            try
            {
                return CopyVisualsShown(rig);
            }
            finally
            {
                rig.RestoreMagazineScales(hidden);
            }
        }

        /// <summary>A piece further than this from the magazine is parked out of sight, not part of it.</summary>
        private const float ParkedAway = 1.5f;

        private static GameObject CopyVisualsShown(WeaponRig rig)
        {
            Transform source = rig.Magazine;
            var root = new GameObject("mag");
            bool any = false;
            Quaternion inv = Quaternion.Inverse(source.rotation);
            var magBones = new HashSet<Transform>();
            foreach (Transform part in rig.MagazineParts)
            {
                if (rig.MagazineHideOnly.Contains(part))
                {
                    continue; // a second magazine drawn in its place: hidden with it, not copied (it doubled the one in the hand)
                }
                foreach (Transform t in part.GetComponentsInChildren<Transform>(true))
                {
                    magBones.Add(t);
                }
            }

            // Only what shows on the gun: a rig's hidden spare magazine (the one its reload animation brings in) must not
            // turn up in your hand next to the real one.
            bool anyShown = false;
            foreach (Transform part in rig.MagazineParts)
            {
                foreach (Renderer r in part.GetComponentsInChildren<Renderer>(true))
                {
                    anyShown |= r.enabled && r.gameObject.activeInHierarchy && !(r is ParticleSystemRenderer);
                }
            }
            // Nothing shows (a launcher's rocket after the shot): what showed while it sat loaded, not every model under
            // it (an RPG-7V's rocket holds five warheads and shows one; the copy of all five was the rocket in the hand).
            HashSet<Renderer> loadedLook = !anyShown && rig.MagShownLoaded != null && rig.MagShownLoaded.Count > 0 ? rig.MagShownLoaded : null;
            foreach (Transform part in rig.MagazineParts)
            {
                if (rig.MagazineHideOnly.Contains(part))
                {
                    continue;
                }
                foreach (Renderer r in part.GetComponentsInChildren<Renderer>(true))
                {
                    if (anyShown && (!r.enabled || !r.gameObject.activeInHierarchy) || loadedLook != null && !loadedLook.Contains(r))
                    {
                        continue;
                    }
                    if ((r.transform.position - source.position).sqrMagnitude > ParkedAway * ParkedAway)
                    {
                        continue; // parked out of sight by the mod (a PKM box's belt links wait 25 m away): not on the magazine
                    }
                    Mesh mesh = null;
                    if (r is MeshRenderer)
                    {
                        MeshFilter mf = r.GetComponent<MeshFilter>();
                        mesh = mf != null ? mf.sharedMesh : null;
                    }
                    else if (r is SkinnedMeshRenderer smr && smr.sharedMesh != null)
                    {
                        mesh = new Mesh { name = smr.sharedMesh.name + " (baked)" };
                        smr.BakeMesh(mesh);
                    }
                    if (mesh == null)
                    {
                        continue;
                    }
                    AddPart(root.transform, r.name, mesh, r.sharedMaterials, inv * (r.transform.position - source.position), inv * r.transform.rotation,
                        r is SkinnedMeshRenderer ? Vector3.one : r.transform.lossyScale, owned: r is SkinnedMeshRenderer);
                    any = true;
                }
            }

            // Skinned guns: the magazine is a bone; cut its triangles out of the gun mesh.
            if (!any && rig.Data.MagazineIsSkinned)
            {
                foreach (SkinnedMeshRenderer smr in rig.Weapon.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                {
                    Mesh cut = ExtractBoneTriangles(smr, magBones);
                    if (cut != null)
                    {
                        AddPart(root.transform, smr.name + " (magazine)", cut, smr.sharedMaterials,
                            inv * (smr.transform.position - source.position), inv * smr.transform.rotation, Vector3.one, owned: true);
                        any = true;
                    }
                }
            }

            if (!any)
            {
                Object.Destroy(root);
                return null;
            }
            return root;
        }

        /// <param name="root">The copy the part goes under.</param>
        /// <param name="name">The part's name.</param>
        /// <param name="mesh">Its mesh.</param>
        /// <param name="materials">Its materials (fixed for the mod's shaders).</param>
        /// <param name="localPos">Where it sits under the root.</param>
        /// <param name="localRot">How it is turned under the root.</param>
        /// <param name="scale">Its scale.</param>
        /// <param name="owned">The mesh was made for this copy (baked, cut out): it goes with the copy.</param>
        private static void AddPart(Transform root, string name, Mesh mesh, Material[] materials, Vector3 localPos, Quaternion localRot, Vector3 scale, bool owned = false)
        {
            var part = new GameObject(name);
            part.transform.SetParent(root, false);
            part.transform.localPosition = localPos;
            part.transform.localRotation = localRot;
            part.transform.localScale = scale;
            part.AddComponent<MeshFilter>().sharedMesh = mesh;
            var mr = part.AddComponent<MeshRenderer>();
            mr.sharedMaterials = Util.MaterialFix.Fix(materials);
            mr.shadowCastingMode = ShadowCastingMode.On;
            if (owned)
            {
                part.AddComponent<OwnedMesh>().Mesh = mesh;
            }
        }

        /// <summary>
        /// Which of a skinned mesh's vertices and triangles a magazine's bones drive (per mesh and bone set): found once. Every
        /// magazine let go, pulled, taken from the pouch or kicked out on such a gun walked the whole gun mesh's triangles and
        /// bone weights again (255.P2-1); only the bake (the magazine as posed now) is done each time.
        /// </summary>
        private sealed class BoneCut
        {
            public int[] Sources;
            public int[][] Triangles;
            public Vector2[] Uv;
        }

        private static readonly Dictionary<string, BoneCut> BoneCuts = new Dictionary<string, BoneCut>();

        /// <summary>Triangles whose vertices are driven mostly by the given bones, baked in the renderer's space.</summary>
        private static Mesh ExtractBoneTriangles(SkinnedMeshRenderer smr, HashSet<Transform> bones)
        {
            Mesh shared = smr.sharedMesh;
            if (shared == null || !shared.isReadable || smr.bones == null)
            {
                return null;
            }
            Transform[] smrBones = smr.bones;
            var boneIsMag = new bool[smrBones.Length];
            bool anyBone = false;
            var key = new System.Text.StringBuilder().Append(shared.GetInstanceID()).Append(':');
            for (int i = 0; i < smrBones.Length; i++)
            {
                boneIsMag[i] = smrBones[i] != null && bones.Contains(smrBones[i]);
                anyBone |= boneIsMag[i];
                if (boneIsMag[i])
                {
                    key.Append(i).Append(',');
                }
            }
            if (!anyBone)
            {
                return null;
            }
            if (!BoneCuts.TryGetValue(key.ToString(), out BoneCut cut))
            {
                cut = FindBoneCut(shared, boneIsMag);
                BoneCuts[key.ToString()] = cut; // a null (nothing driven by them) is kept too
            }
            if (cut == null)
            {
                return null;
            }
            var baked = new Mesh();
            smr.BakeMesh(baked);
            Vector3[] verts = baked.vertices;
            Vector3[] normals = baked.normals;
            Object.Destroy(baked);
            int n = cut.Sources.Length;
            var newVerts = new Vector3[n];
            var newNormals = new Vector3[n];
            for (int i = 0; i < n; i++)
            {
                int source = cut.Sources[i];
                newVerts[i] = verts[source];
                newNormals[i] = normals != null && normals.Length > source ? normals[source] : Vector3.up;
            }
            var result = new Mesh { name = shared.name + " (magazine)" };
            result.indexFormat = n > 65000 ? IndexFormat.UInt32 : IndexFormat.UInt16;
            result.vertices = newVerts;
            result.normals = newNormals;
            result.uv = cut.Uv;
            result.subMeshCount = cut.Triangles.Length;
            for (int sub = 0; sub < cut.Triangles.Length; sub++)
            {
                result.SetTriangles(cut.Triangles[sub], sub);
            }
            result.RecalculateBounds();
            return result;
        }

        private static BoneCut FindBoneCut(Mesh shared, bool[] boneIsMag)
        {
            BoneWeight[] weights = shared.boneWeights;
            if (weights == null || weights.Length != shared.vertexCount)
            {
                return null;
            }
            Vector2[] uv = shared.uv;
            var map = new Dictionary<int, int>();
            var sources = new List<int>();
            var newUv = new List<Vector2>();
            var subMeshes = new List<int[]>();
            for (int sub = 0; sub < shared.subMeshCount; sub++)
            {
                int[] tris = shared.GetTriangles(sub);
                var keep = new List<int>();
                for (int t = 0; t + 2 < tris.Length; t += 3)
                {
                    if (!DrivenBy(weights[tris[t]], boneIsMag) || !DrivenBy(weights[tris[t + 1]], boneIsMag) || !DrivenBy(weights[tris[t + 2]], boneIsMag))
                    {
                        continue;
                    }
                    for (int k = 0; k < 3; k++)
                    {
                        int source = tris[t + k];
                        if (!map.TryGetValue(source, out int idx))
                        {
                            idx = sources.Count;
                            map[source] = idx;
                            sources.Add(source);
                            newUv.Add(uv != null && uv.Length > source ? uv[source] : Vector2.zero);
                        }
                        keep.Add(idx);
                    }
                }
                subMeshes.Add(keep.ToArray());
            }
            return sources.Count == 0 ? null : new BoneCut { Sources = sources.ToArray(), Triangles = subMeshes.ToArray(), Uv = newUv.ToArray() };
        }

        private static bool DrivenBy(BoneWeight w, bool[] boneIsMag)
        {
            float share = 0f;
            if (w.boneIndex0 < boneIsMag.Length && boneIsMag[w.boneIndex0]) share += w.weight0;
            if (w.boneIndex1 < boneIsMag.Length && boneIsMag[w.boneIndex1]) share += w.weight1;
            if (w.boneIndex2 < boneIsMag.Length && boneIsMag[w.boneIndex2]) share += w.weight2;
            if (w.boneIndex3 < boneIsMag.Length && boneIsMag[w.boneIndex3]) share += w.weight3;
            return share >= 0.5f;
        }

        /// <summary>
        /// A magazine, and a launcher's rocket, are copied from the weapon's own model. The rocket only when the analysis
        /// found one of a rocket's size (a launcher showed a made-up capsule instead of its rocket).
        /// </summary>
        private static bool CopiesModel(WeaponRig rig, PropKind kind)
        {
            if (rig.Magazine == null)
            {
                return false;
            }
            if (kind == PropKind.Magazine)
            {
                return true;
            }
            // A rocket whose analysed box came out empty (the scoped RPG-7V's sits under a material-less container) is copied
            // too: the copy measures itself, and one that is not rocket-sized still falls back to the made one.
            float length = Longest(rig.Data.MagBounds);
            // Up to 2 m: the RPG-7V's tandem PG-7VR is 1.7 m with its booster, and at 1.4 m it got the flying projectile.
            return kind == PropKind.Rocket && (length < 0.001f || length > 0.15f && length < 2f);
        }

        private static float Longest(Bounds b) => Mathf.Max(b.size.x, Mathf.Max(b.size.y, b.size.z));

        /// <summary>
        /// How far forward (gun-body space, along <paramref name="aim"/>) the launcher's own meshes reach: its tube's front
        /// end. Not its rocket (the magazine parts), not see-through lenses or sight overlays (queue 3000 and up), nothing
        /// over 2.5 m (a sight's far plane). False when it has no mesh of its own to measure (a skinned tube).
        /// </summary>
        internal static bool TubeFront(WeaponRig rig, Vector3 aim, out float front)
        {
            front = float.MinValue;
            if (rig.GunBody == null)
            {
                return false;
            }
            bool any = false;
            foreach (MeshFilter mf in rig.GunBody.GetComponentsInChildren<MeshFilter>(false))
            {
                Renderer r = mf.GetComponent<Renderer>();
                if (mf.sharedMesh == null || r == null || !r.enabled || r.sharedMaterial != null && r.sharedMaterial.renderQueue >= 3000)
                {
                    continue;
                }
                bool rocket = false;
                foreach (Transform part in rig.MagazineParts)
                {
                    rocket |= part != null && mf.transform.IsChildOf(part);
                }
                if (rocket || Vector3.Scale(mf.sharedMesh.bounds.size, mf.transform.lossyScale).magnitude > 2.5f)
                {
                    continue;
                }
                Bounds mb = mf.sharedMesh.bounds;
                for (int i = 0; i < 8; i++)
                {
                    Vector3 corner = mb.center + Vector3.Scale(mb.extents, new Vector3((i & 1) == 0 ? -1f : 1f, (i & 2) == 0 ? -1f : 1f, (i & 4) == 0 ? -1f : 1f));
                    front = Mathf.Max(front, Vector3.Dot(rig.ToGun(mf.transform.TransformPoint(corner)), aim));
                    any = true;
                }
            }
            return any;
        }

        /// <summary>
        /// The middle, across the bore, of the launcher's own meshes that reach its front end (within 12 cm of it), in gun-body
        /// space: where a missile goes into a tube whose muzzle point is not on the tube's axis (the HYDRA's missile slid in on
        /// the muzzle point's line, over the side of its box). False when nothing reaches the front.
        /// </summary>
        internal static bool TubeCentre(WeaponRig rig, Vector3 aim, float front, out Vector3 centre)
        {
            centre = Vector3.zero;
            if (rig.GunBody == null)
            {
                return false;
            }
            Vector3 up = Vector3.ProjectOnPlane(rig.Data.Up, aim).normalized, right = Vector3.Cross(up, aim).normalized;
            float upMin = float.MaxValue, upMax = float.MinValue, rightMin = float.MaxValue, rightMax = float.MinValue;
            var corners = new Vector3[8];
            foreach (MeshFilter mf in rig.GunBody.GetComponentsInChildren<MeshFilter>(false))
            {
                Renderer r = mf.GetComponent<Renderer>();
                if (mf.sharedMesh == null || r == null || !r.enabled || r.sharedMaterial != null && r.sharedMaterial.renderQueue >= 3000)
                {
                    continue;
                }
                bool rocket = false;
                foreach (Transform part in rig.MagazineParts)
                {
                    rocket |= part != null && mf.transform.IsChildOf(part);
                }
                if (rocket || Vector3.Scale(mf.sharedMesh.bounds.size, mf.transform.lossyScale).magnitude > 2.5f)
                {
                    continue;
                }
                Bounds mb = mf.sharedMesh.bounds;
                float reach = float.MinValue, back = float.MaxValue;
                for (int i = 0; i < 8; i++)
                {
                    Vector3 corner = mb.center + Vector3.Scale(mb.extents, new Vector3((i & 1) == 0 ? -1f : 1f, (i & 2) == 0 ? -1f : 1f, (i & 4) == 0 ? -1f : 1f));
                    corners[i] = rig.ToGun(mf.transform.TransformPoint(corner));
                    float along = Vector3.Dot(corners[i], aim);
                    reach = Mathf.Max(reach, along);
                    back = Mathf.Min(back, along);
                }
                // A tube is long along the bore: a sight post, a grip or a handle that reaches the mouth (shorter than 20 cm)
                // pulled the middle toward itself, and the missile with it (review 253.11).
                if (reach < front - 0.12f || reach - back < 0.2f || rig.Handle != null && mf.transform.IsChildOf(rig.Handle))
                {
                    continue;
                }
                foreach (Vector3 c in corners)
                {
                    upMin = Mathf.Min(upMin, Vector3.Dot(c, up));
                    upMax = Mathf.Max(upMax, Vector3.Dot(c, up));
                    rightMin = Mathf.Min(rightMin, Vector3.Dot(c, right));
                    rightMax = Mathf.Max(rightMax, Vector3.Dot(c, right));
                }
            }
            if (upMin > upMax)
            {
                return false;
            }
            centre = up * ((upMin + upMax) * 0.5f) + right * ((rightMin + rightMax) * 0.5f) + aim * front;
            return true;
        }

        /// <summary>A shotgun's rounds are shells; any other gun loaded round by round takes rifle rounds.</summary>
        private static bool ShotgunShells(WeaponRig rig) =>
            rig.Weapon == null || rig.Weapon.configuration == null || rig.Weapon.configuration.projectilesPerShot > 1 || rig.Data.Action == ActionType.BreakOpen;

        private static Bounds BuildProcedural(GameObject go, PropKind kind, Vector3 insertLocal, Vector3 frontLocal, Bounds magBounds, bool shotgunShell)
        {
            EnsureMaterials();
            if (kind == PropKind.Shell)
            {
                // The round itself (a shell with its brass head, or a live rifle round), tip first into the gun. It was a
                // brass capsule: "a yellow thing" instead of a shell.
                GameObject round = Casings.MakeRound(shotgunShell, go.transform, out float length, out float radius);
                Vector3 up = Mathf.Abs(Vector3.Dot(insertLocal.normalized, frontLocal.normalized)) > 0.95f ? Vector3.up : frontLocal;
                Quaternion along = Quaternion.LookRotation(insertLocal, up);
                round.transform.localRotation = along;
                round.transform.localPosition = -(along * new Vector3(0f, 0f, length * 0.5f));
                return new Bounds(Vector3.zero, Abs(along * new Vector3(radius * 2f, radius * 2f, length)));
            }
            Vector3 size;
            Material mat;
            PrimitiveType shape;
            switch (kind)
            {
                case PropKind.Shell:
                    size = new Vector3(0.019f, 0.065f, 0.019f);
                    mat = _brass;
                    shape = PrimitiveType.Capsule;
                    break;
                case PropKind.Rocket:
                    size = new Vector3(0.085f, 0.75f, 0.085f);
                    mat = _olive;
                    shape = PrimitiveType.Capsule;
                    break;
                default:
                    size = magBounds.size.sqrMagnitude > 1e-6f ? magBounds.size : new Vector3(0.025f, 0.12f, 0.06f);
                    mat = _metal;
                    shape = PrimitiveType.Cube;
                    break;
            }
            GameObject tmp = GameObject.CreatePrimitive(shape);
            Object.DestroyImmediate(tmp.GetComponent<Collider>());
            tmp.transform.SetParent(go.transform, false);
            // Primitive's long axis (Y) follows the insertion direction; Z follows the barrel.
            Quaternion orient = Quaternion.LookRotation(frontLocal, insertLocal);
            tmp.transform.localRotation = orient;
            tmp.transform.localPosition = kind == PropKind.Magazine ? magBounds.center : Vector3.zero;
            tmp.transform.localScale = kind == PropKind.Magazine ? new Vector3(size.x, size.y, size.z) : new Vector3(size.x, size.y * 0.5f, size.z);
            tmp.GetComponent<Renderer>().sharedMaterial = mat;
            Vector3 worldishSize = orient * new Vector3(size.x, size.y, size.z);
            return new Bounds(tmp.transform.localPosition, Abs(worldishSize));
        }

        private static void EnsureMaterials()
        {
            if (_metal != null)
            {
                return;
            }
            Shader s = Shader.Find("Standard") ?? Shader.Find("Legacy Shaders/Diffuse");
            _metal = new Material(s) { color = new Color(0.12f, 0.12f, 0.13f) };
            _brass = new Material(s) { color = new Color(0.72f, 0.55f, 0.22f) };
            _olive = new Material(s) { color = new Color(0.28f, 0.31f, 0.2f) };
            if (_metal.HasProperty("_Metallic"))
            {
                _metal.SetFloat("_Metallic", 0.6f);
                _brass.SetFloat("_Metallic", 0.9f);
            }
        }

        private static void SetLayer(GameObject go, int layer)
        {
            foreach (Transform t in go.GetComponentsInChildren<Transform>(true))
            {
                t.gameObject.layer = layer;
            }
        }

        // ---- holding ------------------------------------------------------------------------------

        // A grabbed prop eases into the hand over a few frames instead of jumping there.
        private RigidPose _attachFrom;
        private bool _fresh = true;
        private float _attachAt = -1f;
        private const float AttachBlend = 0.09f;

        public void Attach(VRHand hand)
        {
            // A prop made for this grab (from the pouch, pulled from the well) starts where it belongs; one picked up,
            // caught or moved from the other fingers eases in from where it was.
            if (Go != null && !_fresh)
            {
                _attachFrom = RigidPose.Of(Go.transform);
                _attachAt = Time.unscaledTime;
            }
            _fresh = false;
            HeldBy = hand;
            _keptOut = Vector3.zero; // a fresh grip: no offset left over from the last time it met the gun
            LostByHand = false; // (a stale flag let go of the next grip at once; 255.B3-1)
            _drawnFor = null;
            hand.Role = HandRole.Magazine;
            hand.Held = this;
            _body.isKinematic = true;
            SetCollidersEnabled(false);
            _dropTime = -1f;
            OnRail = false;
            RailOutOnly = false;
            SetLayer(Go, HeldLayer);
        }

        /// <summary>
        /// World pose the prop takes in the hand: the fist closed around its lower body (just up from the base plate, as you
        /// hold a magazine to seat it), tip up-forward out between the thumb and the index, front forward. A pistol magazine
        /// the same way, its base plate in the fist (the user's picture; it was held index-point, the index along its front).
        /// </summary>
        public RigidPose HoldPose(VRHand hand)
        {
            RigidPose palm = hand.Palm;
            if (Kind == PropKind.Shell && hand.Model != null)
            {
                return ShellPinchPose(hand, palm);
            }
            Vector3 insertTarget = (palm.Up * 0.75f + palm.Forward * 0.65f).normalized;
            Quaternion rot = Quaternion.LookRotation(palm.Forward, insertTarget) * Quaternion.Inverse(Quaternion.LookRotation(FrontLocal, InsertDirLocal));
            // A magazine by its lower end (15% up from the base plate): held a third of the way up, its side ran past the
            // pinky, which lay flat along it and stuck out straight (the "pinky bug"); now the pinky curls toward the base
            // plate in line with the others (tools/hands/grip_check.py). Shells are pinched in the middle. A rocket is held
            // by its front part, 6 cm out past what goes into the tube: the hand pushes it in up to the mouth and stays out
            // (it slides in with the hand now, and by its middle the glove went 11 cm into an RPG-7's tube).
            float length = Vector3.Distance(BaseLocal, TipLocal);
            float gripAt = Kind == PropKind.Magazine ? (PistolSized ? 0.12f : 0.15f)
                           : Kind == PropKind.Rocket && length > 0.05f ? Mathf.Clamp(1f - (RailLength + 0.06f) / length, 0.12f, 0.5f) : 0.5f;
            Vector3 held = Vector3.Lerp(BaseLocal, TipLocal, gripAt);
            Vector3 pos = palm.position - rot * held;
            // Resting against the palm, not half inside it. The grip point (the fist hollow) lies at the palm's skin, so a
            // prop centred on it was half in the hand, and the glove, settling onto it, left the real hand: 1.8 cm for a
            // rifle magazine, 2.0 for an AK's, the full 4 cm for a machine gun's box (tools/hands/grip_check.py). Out along
            // the palm's normal by the palm's skin over the part it covers, plus its own half thickness.
            float zHold = VRHand.FistHollow.z, yHold = VRHand.FistHollow.y;
            float rest = Rig.HandModel.PalmSurfaceFromGrip(zHold - _frontHalf, zHold + _frontHalf, yHold - gripAt * length, yHold + (1f - gripAt) * length) + _sideHalf;
            pos += palm.rotation * new Vector3(-hand.Mirror, 0f, 0f) * rest;
            if (Kind == PropKind.Magazine)
            {
                // A centimetre toward the fingers: further back its back edge lay in the root of the thumb, which went
                // through it (rifle and AK magazines, grip_check.py); here the thumb wraps it and nothing is buried. A
                // machine gun's box or drum (as wide as the box pose takes) three: its 8 cm side buried the thumb 14 mm
                // at one (10 of 72 hand points inside it; at three none, every finger and the thumb on it).
                pos += palm.rotation * Vector3.forward * (BoxSized ? 0.03f : 0.01f);
            }
            return new RigidPose(pos, rot);
        }

        // Shell pinch (tools/hands/grip_check.py, "shell (pinch)"): along the fingers, tipped 22 degrees toward the palm's
        // heel, the pinch point (between the thumb and index tips) 2.5 cm up from the rim.
        private static readonly Vector3 ShellPinchAxis = new Vector3(0f, -0.3714f, 0.9285f);
        private const float ShellPinchFromRim = 0.025f;

        /// <summary>
        /// A shell the way it is pushed into a tube: pinched near its rim between the thumb and the index and middle
        /// fingers, its front out past the fingertips. Held at its middle it lay in a closed fist.
        /// </summary>
        private RigidPose ShellPinchPose(VRHand hand, RigidPose palm)
        {
            Vector3 axis = palm.rotation * ShellPinchAxis;
            Vector3 front = Vector3.ProjectOnPlane(palm.Up, axis);
            front = front.sqrMagnitude > 1e-6f ? front.normalized : palm.Forward;
            Quaternion rot = Quaternion.LookRotation(front, axis) * Quaternion.Inverse(Quaternion.LookRotation(FrontLocal, InsertDirLocal));
            Vector3 pinch = palm.TransformPoint(hand.Model.PinchInPalm);
            return new RigidPose(pinch - axis * ShellPinchFromRim - rot * BaseLocal, rot);
        }

        /// <summary>
        /// Held as a second magazine in a hand that already holds one (H3VR-style palming): no hand role of its own, no
        /// physics; <see cref="PalmedPose"/> places it beside the first.
        /// </summary>
        public void AttachPalmed(VRHand hand)
        {
            _fresh = false;
            HeldBy = hand;
            _body.isKinematic = true;
            SetCollidersEnabled(false);
            _dropTime = -1f;
            OnRail = false;
            RailOutOnly = false;
            SetLayer(Go, HeldLayer);
        }

        /// <summary>
        /// The palmed magazine's pose: parallel to the one the hand holds, beside it on the finger side of the fist and
        /// a little lower, the way a second magazine is clamped between the fingers.
        /// </summary>
        public RigidPose PalmedPose(VRHand hand, MagazineProp primary)
        {
            RigidPose p = HoldPose(hand);
            Vector3 towardFingers = -hand.Palm.Right * hand.Mirror; // the palm side of the fist
            // Both rest on the palm in HoldPose: this one goes the first one's whole thickness further out, and 4 mm. Its
            // thickness that way, both its width and its depth (a pistol magazine is held in the fist by its base too, Tasks 240).
            MagazineProp first = primary ?? this;
            Vector3 frontW = p.rotation * FrontLocal, sideW = p.rotation * Vector3.Cross(InsertDirLocal, FrontLocal);
            float half = Mathf.Abs(Vector3.Dot(towardFingers, sideW)) * first._sideHalf + Mathf.Abs(Vector3.Dot(towardFingers, frontW)) * first._frontHalf;
            float spacing = 2f * half + 0.004f;
            Vector3 insert = p.TransformDirection(InsertDirLocal);
            return new RigidPose(p.position + towardFingers * spacing - insert * 0.025f, p.rotation);
        }

        private GunVolume _volume;
        private bool _volumeTried;
        private static readonly System.Func<Vector3, bool>[] HandSolids = new System.Func<Vector3, bool>[2];
        private static readonly VRHand[] HandSolidOwners = new VRHand[2];

        private float _volumeRetryAt;

        /// <summary>Self-test: this magazine's measured solid (null until measured).</summary>
        internal GunVolume Volume => _volume;

        private void EnsureVolume()
        {
            if (_volume != null && !_volume.Failed || Go == null)
            {
                return;
            }
            if (_volumeTried && Time.unscaledTime < _volumeRetryAt)
            {
                // A read-back that failed, or no volume at all (its build threw): measured again once a second, not every
                // frame, and not never (a null on the first try left the magazine without a hand shape for good; bug hunt).
                return;
            }
            _volumeRetryAt = Time.unscaledTime + 1f;
            _volumeTried = true;
            var renderers = new List<Renderer>();
            foreach (Renderer r in Go.GetComponentsInChildren<Renderer>(false))
            {
                if (r.enabled && (r is MeshRenderer || r is SkinnedMeshRenderer))
                {
                    renderers.Add(r);
                }
            }
            // Box-like: the three axis views see it whole. With unreadable meshes its physics shape comes from it too.
            _volume = GunVolume.Measure(ShapeKey, renderers, Go.transform, 3, 0.015f, 8);
        }

        /// <summary>
        /// The fingers of the hand holding magazines close on them: the solid of every magazine that hand holds (the one
        /// in the fist and a palmed second one), each measured once per magazine type from its own model.
        /// </summary>
        private void ShapeHand()
        {
            VRHand hand = HeldBy;
            if (hand == null || hand.Model == null || Kind != PropKind.Magazine)
            {
                return;
            }
            foreach (MagazineProp m in Alive)
            {
                if (m.HeldBy == hand && m.Kind == PropKind.Magazine)
                {
                    m.EnsureVolume(); // the palmed second magazine too: the fingers must not go through it either
                    m.MeasureSectionFromVolume();
                    m.EaseSection();
                }
            }
            if (_volume == null || !_volume.Ready)
            {
                return; // until it is measured the fingers keep their usual grip instead of closing on nothing
            }
            int side = (int)hand.Side;
            if (HandSolids[side] == null || !ReferenceEquals(HandSolidOwners[side], hand))
            {
                // A new rig (VR restarted) has new hands: the closure must test this one.
                HandSolidOwners[side] = hand;
                HandSolids[side] = p => HeldSolid(hand, p);
            }
            hand.Model.Solid = HandSolids[side];
            hand.Model.SolidAnchor = Go.transform;
            hand.Model.SolidVersion = HeldShapesOf(hand).Count;
        }

        /// <summary>One held magazine's shape at its pose this frame (magazines of one type share a volume).</summary>
        private struct HeldShape
        {
            public GunVolume Volume;
            public Vector3 Position;
            public Quaternion Inverse;
        }

        /// <summary>Per hand: the measured magazines it holds this frame, gathered once per frame (not per tested point).</summary>
        private static readonly List<HeldShape>[] HeldShapes = { new List<HeldShape>(2), new List<HeldShape>(2) };
        private static readonly int[] HeldShapesFrame = { -1, -1 };

        private static List<HeldShape> HeldShapesOf(VRHand hand)
        {
            int side = (int)hand.Side;
            List<HeldShape> list = HeldShapes[side];
            if (HeldShapesFrame[side] == Time.frameCount)
            {
                return list;
            }
            HeldShapesFrame[side] = Time.frameCount;
            list.Clear();
            foreach (MagazineProp m in Alive)
            {
                if (m.HeldBy == hand && m._volume != null && m._volume.Ready && m.Go != null)
                {
                    Transform t = m.Go.transform;
                    list.Add(new HeldShape { Volume = m._volume, Position = t.position, Inverse = Quaternion.Inverse(t.rotation) });
                }
            }
            return list;
        }

        private static bool HeldSolid(VRHand hand, Vector3 p)
        {
            List<HeldShape> held = HeldShapesOf(hand);
            for (int i = 0; i < held.Count; i++)
            {
                if (held[i].Volume.SolidAt(p, held[i].Position, held[i].Inverse))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>Self-test: how much of this hand is buried in the magazines it holds.</summary>
        internal static string HandClipping(VRHand hand) => HandClipping(hand, out _, out _, out _);

        internal static string HandClipping(VRHand hand, out int buried, out int samples, out int touching)
        {
            buried = samples = touching = -1;
            if (hand.Model == null)
            {
                return "not measured";
            }
            bool measured = false;
            foreach (MagazineProp m in Alive)
            {
                measured |= m.HeldBy == hand && m._volume != null && m._volume.Ready;
            }
            if (!measured)
            {
                return "not measured (magazine shape not ready)";
            }
            hand.Model.Measure(p => InsideHeld(hand, p), p => HeldSolid(hand, p), out buried, out samples, out touching);
            return $"{buried}/{samples} points inside the magazines, {touching}/4 fingers on them";
        }

        private static bool InsideHeld(VRHand hand, Vector3 p)
        {
            List<HeldShape> held = HeldShapesOf(hand);
            for (int i = 0; i < held.Count; i++)
            {
                if (held[i].Volume.InsideModelAt(p, held[i].Position, held[i].Inverse))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>A hand let go of its magazines: it no longer closes on them.</summary>
        private static void ReleaseHand(VRHand hand)
        {
            if (hand != null && hand.Model != null && hand.Model.Solid == HandSolids[(int)hand.Side])
            {
                hand.Model.Solid = null;
            }
        }

        private string ShapeKey => "prop:" + WeaponKey + ":" + Kind;

        /// <summary>
        /// A magazine whose meshes could not be read had box colliders: once its model is measured they become the
        /// shape fitted to what is drawn (and every later magazine of the type starts with it).
        /// </summary>
        private void TakeRenderedShape()
        {
            if (!_boxShaped || _volume == null || !_volume.Ready || _volume.HullPieces == null || _volume.HullPieces.Length == 0 || Go == null)
            {
                return;
            }
            _boxShaped = false;
            ShapeFit.Piece[] pieces = _volume.HullPieces;
            RenderedShapes[ShapeKey] = pieces;
            bool on = _colliders.Length > 0 && _colliders[0] != null && _colliders[0].enabled;
            foreach (Collider c in _colliders)
            {
                if (c != null)
                {
                    UnityEngine.Object.Destroy(c);
                }
            }
            ShapeFit.Bake(pieces);
            _colliders = ShapeFit.AddColliders(Go, pieces).ToArray();
            _pieceBounds = new Bounds[pieces.Length];
            for (int i = 0; i < pieces.Length; i++)
            {
                _pieceBounds[i] = pieces[i].Bounds;
            }
            SetCollidersEnabled(on);
            if (on)
            {
                PropPhysics.IgnorePlayer(_colliders);
            }
        }

        /// <summary>Self-test: how this magazine's colliders match its drawn model (null until it is measured).</summary>
        internal string ShapeMatch(out float excess, out float covered)
        {
            excess = covered = -1f;
            if (_volume == null || !_volume.Ready || Go == null || _colliders.Length == 0 || !_colliders[0].enabled)
            {
                return null;
            }
            return GunCollider.MatchShape(_colliders, Go.transform, _volume, 6, 300, out excess, out covered);
        }

        /// <summary>Self-test: the colliders were still the meshes' boxes when matched.</summary>
        internal bool BoxShaped => _boxShaped;

        /// <summary>
        /// On the well's line (pulled out or pushed in): drawn there, and the hand's glove moved to hold it there as it holds
        /// one in the free hand (<see cref="Rig.HandModel.HeldPalm"/>), the fingers closing on it where it is.
        /// </summary>
        public void ShowOnRail(RigidPose railPose)
        {
            ShapeHand();
            TakeRenderedShape();
            ShowAt(railPose);
            // Off the line again it goes from here toward the hand (from where it was drawn before it went on, a rocket
            // pulled out of the tube's mouth went back through the launcher to its side).
            HeldWeapon gunNow = WeaponSystem.Instance != null ? WeaponSystem.Instance.Held : null;
            if (gunNow != null && gunNow.Rig.GunBody != null)
            {
                _drawnInGun = gunNow.Rig.ToGun(railPose.position);
                _drawnFor = gunNow;
            }
            if (HeldBy != null && HeldBy.Model != null)
            {
                // The palm such that this magazine, at its pose on the line, sits in the hand as HoldPose puts it.
                RigidPose palm = HeldBy.Palm;
                RigidPose hold = HoldPose(HeldBy);
                RigidPose heldPalm = RigidPose.Of(Go.transform).Multiply(hold.Inverse().Multiply(palm));
                // A pistol magazine going in: as it slides up the grip the hand lets go of it and turns into a push on its
                // base plate, the thumb coming away (the user's pictures: more satisfying than a fist all the way in).
                float push = PistolSized && !FromSeat && MovingIn && RailLength > 1e-4f ? Mathf.InverseLerp(0.2f, 0.75f, RailDepth / RailLength) : 0f;
                if (Kind == PropKind.Rocket && GoesAllTheWayIn && !FromSeat && MovingIn)
                {
                    // A missile that goes all the way into its tube: the hand cannot follow it in, so for the last of it the
                    // hand turns into a push on its nose, as on a pistol magazine's base plate.
                    push = Mathf.InverseLerp(0.45f, 0.8f, RailDepth / RailLength);
                }
                if (push > 0f)
                {
                    push = push * push * (3f - 2f * push);
                    RigidPose pushPalm = PushPalm(HeldBy);
                    heldPalm = new RigidPose(Vector3.Lerp(heldPalm.position, pushPalm.position, push), Quaternion.Slerp(heldPalm.rotation, pushPalm.rotation, push));
                    HeldBy.Model.PushBlend = push;
                    HeldBy.Model.PushBlendFrame = Time.frameCount;
                }
                HeldBy.Model.HeldPalm = heldPalm;
                HeldBy.Model.HeldPalmFrame = Time.frameCount;
                HeldShapesFrame[(int)HeldBy.Side] = -1; // moved: the fingers' solid is measured again from where it is now
            }
        }

        /// <summary>A rocket whose whole length goes into the tube (within 10 cm): nothing of it is left for the hand at the end.</summary>
        internal bool GoesAllTheWayIn => Kind == PropKind.Rocket && RailLength > Vector3.Dot(TipLocal - BaseLocal, InsertDirLocal) - 0.1f;

        /// <summary>
        /// The palm pushing this magazine's base plate home: the palm flat under it, facing up the magazine, its heel on the
        /// base plate and the fingers forward toward the muzzle, under the gun hand's.
        /// </summary>
        private RigidPose PushPalm(VRHand hand)
        {
            Transform t = Go.transform;
            Vector3 up = t.TransformDirection(InsertDirLocal).normalized;
            Vector3 front = Vector3.ProjectOnPlane(t.TransformDirection(FrontLocal), up).normalized;
            Vector3 right = -hand.Mirror * up; // the palm's normal (-Mirror * right) points up the magazine
            Vector3 handUp = Vector3.Cross(front, right);
            Quaternion rot = Quaternion.LookRotation(front, handUp);
            // The palm's skin about 1.7 cm out from its centre; the heel (2.5 cm toward the wrist) under the base plate.
            Vector3 basePlate = t.TransformPoint(BaseLocal);
            return new RigidPose(basePlate - up * 0.017f + front * 0.025f, rot);
        }

        public void FollowHand()
        {
            ShapeHand();
            TakeRenderedShape();
            if (HeldBy != null && !OnRail)
            {
                RigidPose target = HoldPose(HeldBy);
                float t = _attachAt >= 0f ? (Time.unscaledTime - _attachAt) / AttachBlend : 1f;
                if (t < 1f)
                {
                    float k = t * t * (3f - 2f * t);
                    target = new RigidPose(Vector3.Lerp(_attachFrom.position, target.position, k), Quaternion.Slerp(_attachFrom.rotation, target.rotation, k));
                }
                else
                {
                    _attachAt = -1f;
                }
                ShowAt(KeepOffGun(target));
            }
            if (HeldBy != null)
            {
                HeldShapesFrame[(int)HeldBy.Side] = -1; // moved: the fingers' solid is measured again from where it is now
            }
        }

        /// <summary>The hand went on far past the gun this magazine is kept out of: it is let go of (ReloadInteractions).</summary>
        internal bool LostByHand;

        /// <summary>How far the magazine in the hand is drawn away from the hand to stay out of the gun now.</summary>
        private Vector3 _keptOut;
        // On the rail nothing keeps it out of the gun: the last keep-out eases away with the switch onto the line (the palmed
        // magazine beside it kept the stale offset for as long as this one sat on the rail; bug hunt 251.3).
        internal Vector3 KeptOut => OnRail && _switchAt >= 0f ? _keptOut * (1f - Mathf.Clamp01((Time.unscaledTime - _switchAt) / SwitchBlend)) : _keptOut;
        /// <summary>Rows of points along a magazine for the keep-out (five, 4-5.5 cm apart on a rifle magazine, let a plate of
        /// the receiver between them: the dense probe found up to 300 points of a PATRIOT magazine in the gun; check-1017f).</summary>
        internal const int MagRows = 9;
        private static readonly Vector3[] Samples = new Vector3[MagRows * 5];

        /// <summary>
        /// The well lets a magazine in only on its own line: within the rail's reach of it sideways (4.5 cm) and turned less
        /// than 40 degrees from it. Off the line, the well's opening is solid like the rest of the gun: a PKM's box swept
        /// sideways 13 cm over its seat passed through the receiver, all its points inside the opening's box (check-1017f).
        /// </summary>
        private bool OnWellLine(RigidPose seat, Vector3 at, Quaternion rot)
        {
            Vector3 local = seat.InverseTransformPoint(at);
            // Past the mouth the well is open only to a magazine nearly straight on its line and within its play: turned in it,
            // its corners went out through a pistol grip's walls, and 4.5 cm aside, the half of it over the seated magazine
            // was let in and its far side stood in the grip's wall (user, 2026-10-19, the 1911s; task 270; check-1019k: 23-102
            // points in the wall pushed in from the side). At the mouth it may come in turned and aside, as the rail takes it
            // on there.
            float depth = Vector3.Dot(local, InsertDirLocal) + RailLength; // 0: its tip at the well's mouth, RailLength: seated
            bool past = depth > ReloadInteractions.MouthReach;
            if (Vector3.ProjectOnPlane(local, InsertDirLocal).magnitude > (past ? WellPlay * 0.75f : 0.045f))
            {
                return false;
            }
            return Quaternion.Angle(rot, seat.rotation) < (past ? InWellTurn : 40f);
        }

        /// <summary>How far a magazine may be turned off its line inside its well, past the mouth (task 270).</summary>
        internal const float InWellTurn = 12f;
        /// <summary>How much bigger than the magazine its well is left open (the whole size: half of it a side).</summary>
        internal const float WellPlay = 0.008f;
        private static readonly Vector3[] RocketSamples = new Vector3[5 * 24];
        private static readonly Vector3[] SlideAxes = new Vector3[3];
        /// <summary>Where the magazine was last drawn, in the space of the gun it was kept out of (it moves with that gun).</summary>
        private Vector3 _drawnInGun;
        private HeldWeapon _drawnFor;
        private Vector3 _keptTargetInGun;
        private Quaternion _keptRotInGun = Quaternion.identity;
        private System.Func<Vector3, bool> _keptSolid;
        private bool _keptMagShown;

        /// <summary>
        /// A magazine in the hand does not pass through the gun. It goes from where it was drawn toward the hand, stops
        /// where it meets the gun's body and slides along it (the glove holding it stays with it); its own magwell stays
        /// open to it (the rail takes it from there, and a fresh one pushed into the seated one kicks it out: those go by
        /// the hand). Only checking where the hand is now let a hard or fast push through: a magazine past the middle of
        /// the receiver came out the far side, and one buried deeper than the search reached went straight through.
        /// A rocket the same against its launcher (user, 2026-10-16: it went through the tube): it goes in only at the
        /// tube's mouth, on its line, where it is no longer kept out.
        /// </summary>
        /// <summary>
        /// The magazine held beside the one in the hand (palmed): kept out of the gun the same way, on its own (it borrowed
        /// the first one's keep-out only, and went through the gun wherever the first one was clear; user, 2026-10-16).
        /// With the first one on its rail the glove is left to that one (<paramref name="moveHand"/> false).
        /// </summary>
        internal RigidPose KeptOffGun(RigidPose target, bool moveHand)
        {
            _keepHand = moveHand;
            try
            {
                return KeepOffGun(target);
            }
            finally
            {
                _keepHand = true;
            }
        }

        private bool _keepHand = true;

        /// <summary>The rig whose well a held prop goes into: the active sub weapon's when it has a body, else the gun's own.</summary>
        private static WeaponRig WellRig(HeldWeapon gun) => gun.ActiveRig != null && gun.ActiveRig.GunBody != null ? gun.ActiveRig : gun.Rig;

        private RigidPose KeepOffGun(RigidPose target)
        {
            HeldWeapon gun = WeaponSystem.Instance != null ? WeaponSystem.Instance.Held : null;
            System.Func<Vector3, bool> solid = gun != null ? gun.HandsSolid : null;
            Vector3 drawn = target.position;
            bool rocket = Kind == PropKind.Rocket;
            if (solid != null && HeldBy != null && (Kind == PropKind.Magazine || rocket) && gun.Rig.GunBody != null
                && (target.position - gun.GripWorld.position).sqrMagnitude < (rocket ? 1.6f * 1.6f : 0.7f * 0.7f))
            {
                WeaponRigData d = gun.Rig.Data;
                // The well of the weapon in use: a sub weapon's magazine or rocket goes into its own well, and was kept out
                // of it as the parent's body (255.B3-3).
                WeaponRig wellRig = WellRig(gun);
                RigidPose seat = wellRig.GunBodyPose.Multiply(wellRig.Data.MagSeat);
                Bounds well = wellRig.Data.MagBounds;
                // Open only on the well's own line, 4 mm round the magazine: at 1.5 cm a side a magazine held off the line went
                // into a pistol's grip through its 4 mm wall and showed through its sides (user, 2026-10-16, many pistols).
                well.Expand(WellPlay);
                // Points over the magazine: five along it, each at its middle and four edges. A rocket every 6 cm along it
                // (a launcher's tube is a few centimetres thick: five points over a metre of rocket would pass it between
                // them), half way out to its edges (its box is the warhead's: the thin tail would stop short of the tube).
                Vector3 side = Vector3.Cross(InsertDirLocal, FrontLocal).normalized;
                Vector3[] points = rocket ? RocketSamples : Samples;
                int along = rocket ? Mathf.Clamp(Mathf.CeilToInt(Vector3.Distance(BaseLocal, TipLocal) / 0.06f) + 1, 5, RocketSamples.Length / 5) : MagRows;
                // A pistol magazine to its edges: its box is its shape, and at 80% its outer 1.5-3 mm went into a 3-5 mm grip wall
                // unseen (task 270). Others at 80%: a rifle magazine's box is taken where the hand holds it, and a curved one's
                // edges along its length would meet the receiver before the magazine does.
                float edge = rocket ? 0.5f : _pistolGun ? 1f : 0.8f;
                int n = 0;
                for (int a = 0; a < along; a++)
                {
                    Vector3 c = Vector3.Lerp(BaseLocal, TipLocal, a / (float)(along - 1));
                    points[n++] = c;
                    points[n++] = c + FrontLocal * (_frontHalf * edge);
                    points[n++] = c - FrontLocal * (_frontHalf * edge);
                    points[n++] = c + side * (_sideHalf * edge);
                    points[n++] = c - side * (_sideHalf * edge);
                }
                int count = n;
                Quaternion rot = target.rotation;
                bool BlockedAt(Vector3 at)
                {
                    // A rocket has no well open to it from the side: where it sits seated is the tube's front itself, and
                    // left open, a rocket held across the launcher went straight through the tube there.
                    bool open = !rocket && OnWellLine(seat, at, rot);
                    for (int i = 0; i < count; i++)
                    {
                        Vector3 p = at + rot * points[i];
                        if (!solid(p))
                        {
                            continue;
                        }
                        // The well opens to the gun's body only: a magazine seated in it stays solid, so a fresh one stops at its
                        // base, where a firm push or strike knocks the seated one out, instead of passing through it (user,
                        // 2026-10-18, task 260 c).
                        if (open && well.Contains(seat.InverseTransformPoint(p)) && !gun.SeatedMagSolid(p))
                        {
                            continue;
                        }
                        return true;
                    }
                    return false;
                }
                // From where it is toward where it is going, in steps of half a centimetre: the last free step.
                Vector3 Advance(Vector3 from, Vector3 delta)
                {
                    int steps = Mathf.Clamp(Mathf.CeilToInt(delta.magnitude / 0.005f), 1, 80);
                    Vector3 last = from;
                    for (int i = 1; i <= steps; i++)
                    {
                        Vector3 p = from + delta * ((float)i / steps);
                        if (BlockedAt(p))
                        {
                            return last;
                        }
                        last = p;
                    }
                    return last;
                }

                Vector3 from = ReferenceEquals(_drawnFor, gun) ? gun.Rig.ToWorld(_drawnInGun) : target.position;
                // Held against the gun with the hand still against it (within a millimetre and half a degree, in gun space):
                // last frame's place, without the search's up to a few thousand solid tests a frame (255.P2-3). Only for a
                // blocked one: a free one is drawn exactly at the hand.
                Vector3 targetInGun = gun.Rig.ToGun(target.position);
                Quaternion rotInGun = Quaternion.Inverse(gun.Rig.GunBody.rotation) * target.rotation;
                if (ReferenceEquals(_drawnFor, gun) && ReferenceEquals(_keptSolid, solid) && _keptMagShown == gun.MagSolidShown && _keptOut.sqrMagnitude > 1e-6f
                    && (targetInGun - _keptTargetInGun).sqrMagnitude < 1e-6f && Quaternion.Angle(rotInGun, _keptRotInGun) < 0.5f)
                {
                    drawn = from;
                }
                else
                {
                    _keptTargetInGun = targetInGun;
                    _keptRotInGun = rotInGun;
                    _keptSolid = solid;
                    _keptMagShown = gun.MagSolidShown;
                    if (BlockedAt(from))
                    {
                        // Already in the gun (taken out of the pouch inside it, the gun swung onto it): out the shortest way,
                        // from the bore outward first, then up, down and to either side.
                        Vector3 bore = gun.Rig.ToWorld(d.MuzzlePoint);
                        Vector3 away = Vector3.ProjectOnPlane(from - bore, gun.Rig.AimWorld);
                        Vector3 up = gun.Rig.UpWorld, right = Vector3.Cross(up, gun.Rig.AimWorld).normalized;
                        Vector3[] dirs = { away.sqrMagnitude > 1e-6f ? away.normalized : -up, -up, up, right, -right, -gun.Rig.AimWorld, gun.Rig.AimWorld };
                        float best = float.MaxValue;
                        Vector3 freed = target.position;
                        foreach (Vector3 dir in dirs)
                        {
                            for (float step = 0.005f; step <= 0.25f && step < best; step += 0.005f)
                            {
                                if (!BlockedAt(from + dir * step))
                                {
                                    best = step;
                                    freed = from + dir * step;
                                    break;
                                }
                            }
                        }
                        from = freed;
                    }
                    drawn = Advance(from, target.position - from);
                    Vector3 left = target.position - drawn;
                    if (left.sqrMagnitude > 1e-6f)
                    {
                        // Met the gun: what is left of the move slides along it, one of the gun's axes at a time.
                        Vector3 upW = gun.Rig.UpWorld, aimW = gun.Rig.AimWorld, rightW = Vector3.Cross(upW, aimW).normalized;
                        SlideAxes[0] = aimW;
                        SlideAxes[1] = upW;
                        SlideAxes[2] = rightW;
                        foreach (Vector3 axis in SlideAxes)
                        {
                            Vector3 part = axis * Vector3.Dot(target.position - drawn, axis);
                            if (part.sqrMagnitude > 1e-8f)
                            {
                                drawn = Advance(drawn, part);
                            }
                        }
                    }
                    // The hand far through or past the gun: the magazine stays outside it, the glove with it (it jumped through the
                    // gun to the hand 30 cm on; user, 2026-10-16). Pulled more than 45 cm from it, the hand lets go of it.
                    LostByHand = (drawn - target.position).sqrMagnitude > 0.45f * 0.45f;
                    _drawnInGun = gun.Rig.ToGun(drawn);
                    _drawnFor = gun;
                }
            }
            else
            {
                _drawnFor = null;
                LostByHand = false;
            }
            _keptOut = drawn - target.position;
            ContactSounds(gun, drawn, _keptOut.sqrMagnitude > 1e-6f);
            if (_keptOut.sqrMagnitude < 1e-8f)
            {
                return target;
            }
            if (_keepHand && HeldBy != null && HeldBy.Model != null)
            {
                Rig.HandModel model = HeldBy.Model;
                bool setThisFrame = model.KeepOutFrame == Time.frameCount;
                // Both shifts keep the glove out (backed out of the well, and to where the magazine is drawn): added, not the
                // longer one kept, which dropped the magazine's sideways offset (255.I2-3).
                model.KeepOut = setThisFrame ? model.KeepOut + _keptOut : _keptOut;
                model.KeepOutFrame = Time.frameCount;
            }
            return new RigidPose(drawn, target.rotation);
        }

        /// <summary>
        /// Autotest: how many of the magazine's 25 check points are inside <paramref name="gun"/>'s body where it is drawn
        /// now (its magwell does not count, a magazine seated in it does: task 260 c). -1 when the gun's solid is not measured.
        /// </summary>
        internal int PointsInsideGun(HeldWeapon gun)
        {
            System.Func<Vector3, bool> solid = gun != null ? gun.HandsSolid : null;
            if (solid == null || Go == null || gun.Rig.GunBody == null)
            {
                return -1;
            }
            WeaponRig wellRig = WellRig(gun);
            RigidPose seat = wellRig.GunBodyPose.Multiply(wellRig.Data.MagSeat);
            Bounds well = wellRig.Data.MagBounds;
            well.Expand(WellPlay);
            Vector3 side = Vector3.Cross(InsertDirLocal, FrontLocal).normalized;
            Transform t = Go.transform;
            bool open = OnWellLine(seat, t.position, t.rotation);
            int inside = 0;
            for (int a = 0; a < MagRows; a++)
            {
                Vector3 c = Vector3.Lerp(BaseLocal, TipLocal, a / (float)(MagRows - 1));
                foreach (Vector3 local in new[] { c, c + FrontLocal * (_frontHalf * 0.8f), c - FrontLocal * (_frontHalf * 0.8f), c + side * (_sideHalf * 0.8f), c - side * (_sideHalf * 0.8f) })
                {
                    Vector3 p = t.TransformPoint(local);
                    // The same rule as KeepOffGun: the well is open to the gun's body only, not to a magazine seated in it.
                    if (solid(p) && !(open && well.Contains(seat.InverseTransformPoint(p)) && !gun.SeatedMagSolid(p)))
                    {
                        inside++;
                    }
                }
            }
            return inside;
        }

        /// <summary>
        /// Self-test: the magazine as drawn now, sampled every 5 mm over its whole box (the keep-out tests 25 points): how
        /// many lie in the gun's solid inside the well's opening (let through by design, not a seated magazine) and how many
        /// outside it. A sideways pass that crosses the receiver through the opening, or between the keep-out's rows, shows
        /// up here (252 ae).
        /// </summary>
        internal void ProbeInside(HeldWeapon gun, out int points, out int inWell, out int outWell)
        {
            points = inWell = outWell = 0;
            System.Func<Vector3, bool> solid = gun != null ? gun.HandsSolid : null;
            if (solid == null || Go == null || gun.Rig.GunBody == null)
            {
                return;
            }
            WeaponRig wellRig = WellRig(gun);
            RigidPose seat = wellRig.GunBodyPose.Multiply(wellRig.Data.MagSeat);
            Bounds well = wellRig.Data.MagBounds;
            well.Expand(WellPlay);
            Vector3 side = Vector3.Cross(InsertDirLocal, FrontLocal).normalized;
            Transform t = Go.transform;
            bool open = OnWellLine(seat, t.position, t.rotation);
            int na = Mathf.Clamp(Mathf.CeilToInt(Vector3.Distance(BaseLocal, TipLocal) / 0.005f) + 1, 2, 80);
            int nf = Mathf.Clamp(Mathf.CeilToInt(2f * _frontHalf / 0.005f) + 1, 2, 20);
            int ns = Mathf.Clamp(Mathf.CeilToInt(2f * _sideHalf / 0.005f) + 1, 2, 20);
            for (int a = 0; a < na; a++)
            {
                Vector3 c = Vector3.Lerp(BaseLocal, TipLocal, a / (float)(na - 1));
                for (int f = 0; f < nf; f++)
                {
                    for (int s = 0; s < ns; s++)
                    {
                        Vector3 p = t.TransformPoint(c + FrontLocal * (_frontHalf * (2f * f / (nf - 1) - 1f)) + side * (_sideHalf * (2f * s / (ns - 1) - 1f)));
                        points++;
                        if (!solid(p))
                        {
                            continue;
                        }
                        // A magazine seated in the well is not let through (task 260 c): its points are outside the opening.
                        if (open && well.Contains(seat.InverseTransformPoint(p)) && !gun.SeatedMagSolid(p))
                        {
                            inWell++;
                        }
                        else
                        {
                            outWell++;
                        }
                    }
                }
            }
        }

        /// <summary>Self-test: the keep-out's own box of the magazine (front x side x length, cm).</summary>
        internal string KeepOutBox => $"{_frontHalf * 200f:0.0} x {_sideHalf * 200f:0.0} x {Vector3.Distance(BaseLocal, TipLocal) * 100f:0.0} cm";

        private bool _touchingGun;
        private Vector3 _lastScrapeAt;
        // Each magazine's own: shared, the palmed one's knock silenced the fresh one's in the same frame (255.I1-6).
        private float _lastContactSound = -1f;

        /// <summary>
        /// A held magazine meeting the gun buzzes the hand, harder the faster it came in, and more lightly while it scrapes
        /// along the gun. Felt only: no knock or scrape sound (user, 2026-10-16, 252 r). At most four buzzes a second (a
        /// magazine resting against the gun does not buzz the hand over and over).
        /// </summary>
        private void ContactSounds(HeldWeapon gun, Vector3 drawn, bool touching)
        {
            WeaponSystem sys = WeaponSystem.Instance;
            if (sys == null || HeldBy == null || gun == null)
            {
                _touchingGun = false;
                return;
            }
            float now = Time.unscaledTime;
            if (touching && now - _lastContactSound >= 0.25f)
            {
                VRHand dom = sys.Rig.Dominant;
                float speed = (HeldBy.Velocity - (dom != null ? dom.Velocity : Vector3.zero)).magnitude;
                // Felt, not heard: the knock and scrape sounds of a magazine meeting the gun were too goofy (user, 2026-10-16).
                if (!_touchingGun)
                {
                    float loud = Mathf.Clamp01(0.2f + speed / 1.5f);
                    HeldBy.Haptic(0.02f + 0.03f * loud, 150f, 0.3f + 0.5f * loud);
                    dom?.Haptic(0.015f, 150f, 0.2f * loud);
                    _lastContactSound = now;
                    _lastScrapeAt = drawn;
                }
                else if ((drawn - _lastScrapeAt).sqrMagnitude > 0.015f * 0.015f && speed > 0.1f)
                {
                    HeldBy.Haptic(0.012f, 220f, 0.15f);
                    _lastContactSound = now;
                    _lastScrapeAt = drawn;
                }
            }
            _touchingGun = touching;
        }

        /// <summary>World pose at the current rail depth, given the seated pose of the magazine/shell/rocket.</summary>
        public RigidPose RailPose(RigidPose seat)
        {
            Vector3 insert = seat.TransformDirection(InsertDirLocal);
            return new RigidPose(seat.position - insert * (RailLength - RailDepth), seat.rotation);
        }

        public Vector3 TipWorld => Go.transform.TransformPoint(TipLocal);
        public Vector3 InsertDirWorld => Go.transform.TransformDirection(InsertDirLocal);

        public void Drop(Vector3 velocity, Vector3 angularVelocity, [System.Runtime.CompilerServices.CallerMemberName] string by = "")
        {
            if (Core.AutoTest.Requested && HeldBy != null && Rounds > 0)
            {
                // Autotest: which branch takes a loaded magazine out of a hand (RECON LRR's fresh magazine left the hand
                // with its 14 rounds during a slow push, and no branch could be named from the code; 252 ae).
                Log.Info($"Magazine ({Rounds} rounds) dropped from the {HeldBy.Side} hand by {by}{(LostByHand ? " (lost by the hand)" : "")}.");
            }
            LostByHand = false;
            if (_sectionEasing)
            {
                _frontHalf = _sectionGoal.x;
                _sideHalf = _sectionGoal.y;
                CrossHalf = _sectionGoal.z;
                _sectionEasing = false;
            }
            if (HeldBy != null)
            {
                if (ReferenceEquals(HeldBy.Held, this))
                {
                    HeldBy.Held = null;
                    HeldBy.Role = HandRole.Free;
                }
                ReleaseHand(HeldBy);
                HeldBy = null;
            }
            OnRail = false;
            RailOutOnly = false;
            int layer = PropPhysics.Layer;
            SetLayer(Go, layer >= 0 ? layer : WorldLayer);
            SetCollidersEnabled(true);
            _body.isKinematic = false;
            _body.velocity = velocity;
            _body.angularVelocity = angularVelocity;
            _dropTime = Time.time;
            _dropSpeed = velocity.magnitude;
            _dropBy = by;
            _flungLogged = false;
            // The newest drop goes last: the cap on lying props counts from the end, so a magazine picked up and dropped
            // again counts as new (counted by when it was made, it vanished at the feet the moment it was let go).
            if (Alive.Remove(this))
            {
                Alive.Add(this);
            }
            PropPhysics.IgnorePlayer(_colliders);
            // Bodies around it from the first physics step on (not from the next clean-up pass): a magazine falling next to
            // a soldier or into a ragdoll is not shoved away.
            PropPhysics.IgnoreBodiesNear(_colliders, Go.transform.position, 1f, _ignoredBodies);
            foreach (Collider c in _colliders)
            {
                GunCollider.Current?.OnPropDropped(c);
            }
        }

        private void SetCollidersEnabled(bool on)
        {
            foreach (Collider c in _colliders)
            {
                c.enabled = on;
            }
        }

        public bool IsDropped => _dropTime >= 0f;

        /// <summary>Seconds since it was let go (-1 while held).</summary>
        internal float DroppedFor => _dropTime >= 0f ? Time.time - _dropTime : -1f;
        internal Collider[] Colliders => _colliders;
        internal Rigidbody Body => _body;

        /// <summary>
        /// Distance from a point to the prop's own shape. Its transform pivot can sit far from the mesh (many mods
        /// pivot a magazine at the gun's origin), so grabbing is always judged against the box around the mesh.
        /// </summary>
        public float DistanceTo(Vector3 point)
        {
            if (Go == null || _colliders.Length == 0)
            {
                return float.MaxValue;
            }
            float best = float.MaxValue;
            if (_colliders[0].enabled && Go.activeInHierarchy)
            {
                foreach (Collider c in _colliders)
                {
                    best = Mathf.Min(best, Vector3.Distance(c.ClosestPoint(point), point));
                }
                return best;
            }
            // Held or sliding in the well the colliders are off and report no bounds at all: measure against the pieces'
            // boxes in the prop's own space (it is never scaled).
            Vector3 local = Go.transform.InverseTransformPoint(point);
            foreach (Bounds b in _pieceBounds)
            {
                best = Mathf.Min(best, Mathf.Sqrt(b.SqrDistance(local)));
            }
            return best;
        }

        /// <summary>Removed by the clean-up: rounds still inside go back to the reserve they came from.</summary>
        public void Despawn()
        {
            if (Rounds > 0 && VRConfig.ReturnRoundsOnMagDrop.Value && WeaponSystem.Instance != null)
            {
                WeaponSystem.Instance.ReturnRounds(this, Rounds);
            }
            Rounds = 0;
            Destroy();
        }

        public void Destroy([System.Runtime.CompilerServices.CallerMemberName] string by = "")
        {
            if (Core.AutoTest.Requested && HeldBy != null && Rounds > 0)
            {
                Log.Info($"Magazine ({Rounds} rounds) in the {HeldBy.Side} hand removed by {by}.");
            }
            if (HeldBy != null && ReferenceEquals(HeldBy.Held, this))
            {
                HeldBy.Held = null;
                HeldBy.Role = HandRole.Free;
            }
            ReleaseHand(HeldBy);
            HeldBy = null;
            Alive.Remove(this);
            if (Go != null)
            {
                Object.Destroy(Go);
            }
        }

        /// <summary>
        /// Nearest dropped prop within <paramref name="radius"/>, measured to its collider (a long magazine can be taken
        /// by either end); only props of <paramref name="weaponKey"/> unless it is null, and only those <paramref name="accept"/>
        /// takes when given.
        /// </summary>
        public static MagazineProp NearestDropped(Vector3 point, float radius, string weaponKey, System.Func<MagazineProp, bool> accept = null)
        {
            MagazineProp best = null;
            float bestDist = radius;
            foreach (MagazineProp p in Alive)
            {
                if (!p.IsDropped || p.Go == null || weaponKey != null && p.WeaponKey != weaponKey || accept != null && !accept(p))
                {
                    continue;
                }
                float d = p.DistanceTo(point);
                if (d < bestDist)
                {
                    bestDist = d;
                    best = p;
                }
            }
            return best;
        }

        /// <summary>Removes props past their lifetime, beyond the cap, or fallen out of the world.</summary>
        public static void Cull()
        {
            float lifetime = VRConfig.MagPropLifetime.Value;
            int max = Mathf.Max(1, VRConfig.MaxMagProps.Value);
            int dropped = 0;
            for (int i = Alive.Count - 1; i >= 0; i--)
            {
                MagazineProp p = Alive[i];
                if (p.Go == null)
                {
                    // Its object went with the scene: the hand holding it is freed too (it kept pointing at the dead prop,
                    // threw every frame and held nothing else again).
                    p.Destroy();
                    continue;
                }
                if (!p.IsDropped)
                {
                    continue;
                }
                dropped++;
                bool expired = Time.time - p._dropTime > lifetime || p.Go.transform.position.y < -500f || dropped > max;
                if (expired)
                {
                    p.Despawn();
                    continue;
                }
                p.LogIfFlung();
                // Lying in the world, bodies do not push it about (a magazine on the ground stays where it fell).
                if (Time.time >= p._nextBodyCheck)
                {
                    p._nextBodyCheck = Time.time + (Time.time - p._dropTime < 0.5f ? 0f : 0.25f);
                    if (Time.time - p._bodiesForgottenAt > 2f)
                    {
                        p._bodiesForgottenAt = Time.time; // bodies switched off and on collide again: ignore them afresh
                        p._ignoredBodies.Clear();
                    }
                    PropPhysics.IgnoreBodiesNear(p._colliders, p.Go.transform.position, 1f, p._ignoredBodies);
                    if (p._playerGeneration != PropPhysics.PlayerGeneration)
                    {
                        p._playerGeneration = PropPhysics.PlayerGeneration;
                        PropPhysics.IgnorePlayer(p._colliders);
                    }
                }
            }
        }

        /// <summary>
        /// A magazine flying off much faster than it was let go, in its first seconds on its own: pushed out of something it was
        /// let go inside (the gun's stand-in, a hand, a body), the "mags flying around" the user saw on pistols (2026-10-20, task
        /// 315). Logged once per drop, with what it was let go by and what is around it, at most 20 times a session.
        /// </summary>
        private void LogIfFlung()
        {
            if (_flungLogged || _flungLines >= 20 || _body == null || _body.isKinematic || Time.time - _dropTime > 3f)
            {
                return;
            }
            float speed = _body.velocity.magnitude;
            // Faster than it was let go plus what falling since has given it (a magazine dropped off a roof is fast and not flung).
            float fallen = Physics.gravity.magnitude * (Time.time - _dropTime);
            if (speed < 6f || speed < _dropSpeed + fallen + 3f)
            {
                return;
            }
            _flungLogged = true;
            _flungLines++;
            Vector3 at = Go.transform.position;
            int n = Physics.OverlapSphereNonAlloc(at, 0.15f, FlungNear, ~0, QueryTriggerInteraction.Ignore);
            var near = new System.Text.StringBuilder();
            for (int i = 0; i < n; i++)
            {
                Collider c = FlungNear[i];
                if (c == null || System.Array.IndexOf(_colliders, c) >= 0)
                {
                    continue;
                }
                near.Append(near.Length > 0 ? ", " : "").Append(c.name).Append(" (layer ").Append(c.gameObject.layer).Append(')');
            }
            Log.Info($"Magazine of {WeaponKey} flung: {speed:0.0} m/s {Time.time - _dropTime:0.00} s after it was let go at {_dropSpeed:0.0} m/s by {_dropBy}; "
                     + $"near it: {(near.Length > 0 ? near.ToString() : "nothing")}.");
        }

        public static void DestroyAll()
        {
            for (int i = Alive.Count - 1; i >= 0; i--)
            {
                Alive[i].Destroy();
            }
            Alive.Clear();
        }
    }
}
