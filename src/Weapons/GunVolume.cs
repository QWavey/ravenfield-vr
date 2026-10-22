using System;
using System.Collections.Generic;
using System.Threading;
using RavenfieldVR.Core;
using UnityEngine;
using UnityEngine.Rendering;

namespace RavenfieldVR.Weapons
{
    /// <summary>
    /// The held weapon's solid shape exactly as it is drawn, for hands to close on: the visible model is rendered as depth
    /// from both ends of five directions (works for every mesh, readable or not, skinned or not). A point is solid when it
    /// lies between the nearest and the farthest surface in every view, so the opening of a trigger guard, the gap between
    /// a grip and its magazine and the space under a handguard stay open. The solid is grown by a finger's radius in
    /// advance, so a finger's (or the palm's) centre line can be tested directly: a hit means the skin touches the model.
    /// Built once per weapon type, in the collider frame (origin at the grip, z along the barrel, y up).
    /// </summary>
    internal sealed class GunVolume
    {
        /// <summary>How far the solid is grown: about the radius of a glove finger.</summary>
        public const float Grow = 0.0072f;
        private const float FineTexel = 0.0015f;
        private const int MaxTexels = 448;
        // A held weapon alone keeps three or four (itself, its seated magazine, its moving part, the magazine in the hand)
        // and every dropped weapon type one: with twelve, three weapons in turn measured each of them again on every switch
        // (about 12 ms on the main thread and 11 MB of garbage each; perf review 2026-10-16). Dropped weapons go first.
        private const int CacheSize = 24;

        private static readonly Dictionary<string, GunVolume> Cache = new Dictionary<string, GunVolume>();
        private static readonly List<string> CacheOrder = new List<string>();
        private static Shader _depthShader;
        private static bool _shaderLooked;

        private sealed class View
        {
            public Quaternion ToView; // frame -> view: x right, y up, z along the view direction
            public float MinX, MinY, Texel;
            public float InvTexel;
            public Quaternion WorldToView; // world -> view at the last SetPose (ToView after the frame's inverse)
            public int W, H;
            public float[] Near, Far; // per texel, along the view's z; empty where Near > Far
            public float[] BareNear, BareFar; // the same before growing: the model itself
            public float[] ClosedNear, ClosedFar; // the grown span with narrow recesses filled (through-holes kept)
        }

        /// <summary>
        /// Half-width of the recesses <see cref="SolidClosed"/> fills, in the grown shape: a groove up to twice this wide
        /// there (about 3.4 cm in the model: an ejection port, a lightening cut, a handguard slot) is closed over.
        /// </summary>
        public const float RecessFill = 0.010f;

        private readonly View[] _views;

        private static int _inFlight;

        /// <summary>Measurements rendered and not yet ready (each holds its read-back pictures until it is done).</summary>
        public static int InFlight => Volatile.Read(ref _inFlight);
        private readonly Bounds _box;
        // The box's corners, compared by hand in Test: Bounds.Contains is a native call in this Unity, one per point tested.
        private readonly Vector3 _boxMin, _boxMax;
        private volatile bool _ready;
        private Vector3 _pos;
        private Quaternion _inv = Quaternion.identity;

        /// <summary>Measuring is finished (the growing runs on a worker thread for a moment after the weapon is drawn).</summary>
        public bool Ready => _ready;
        public string Key { get; }
        public int Texels { get; private set; }
        public float BuildMs { get; private set; }
        /// <summary>
        /// For a weapon whose meshes cannot be read (its physics shape could only be built from the meshes' bounding boxes):
        /// the physics shape fitted to the model's rendered surface instead, made with the volume. Null otherwise or until
        /// ready.
        /// </summary>
        public ShapeFit.Piece[] HullPieces { get; private set; }
        private bool _wantHull;

        /// <summary>Some of the weapon's meshes cannot be read: its own physics shape could only be built from boxes.</summary>
        public bool Unreadable => _wantHull;

        /// <summary>
        /// A held weapon's volume also fits a physics shape (compared with the one from its meshes when they are
        /// readable); a magazine's does not need one.
        /// </summary>
        public bool WantsHull;
        /// <summary>Called on the worker thread once the volume (and its hull) is ready: keep only thread-safe work in it.</summary>
        internal volatile Action<GunVolume> OnReady; // (volatile: set on the main thread while the worker may be publishing Ready)
        /// <summary>Slice length and piece count for the hull (a gun's defaults; a magazine uses finer, fewer).</summary>
        public float HullSlice = 0.03f;
        public int HullMaxPieces = 16;

        /// <summary>Inside the model itself, in the frame (self-test).</summary>
        public bool InsideModelLocal(Vector3 p) => _ready && Test(p, false);

        /// <summary>
        /// Points on the model's surface as the views saw it (frame space): the first and last surface along every
        /// <paramref name="stride"/>-th texel of every view.
        /// </summary>
        public List<Vector3> SurfacePoints(int stride)
        {
            var points = new List<Vector3>();
            foreach (View v in _views)
            {
                if (v.BareNear == null)
                {
                    continue;
                }
                Quaternion toFrame = Runtime.RigidPose.Conjugate(v.ToView);
                for (int j = 0; j < v.H; j += stride)
                {
                    for (int i = 0; i < v.W; i += stride)
                    {
                        int k = j * v.W + i;
                        float a = v.BareNear[k], b = v.BareFar[k];
                        if (a > b)
                        {
                            continue;
                        }
                        float x = v.MinX + (i + 0.5f) * v.Texel, y = v.MinY + (j + 0.5f) * v.Texel;
                        points.Add(toFrame * new Vector3(x, y, a));
                        points.Add(toFrame * new Vector3(x, y, b));
                    }
                }
            }
            return points;
        }

        /// <summary>The part spent on the main thread (rendering and reading back the views), over all its frames.</summary>
        public float RenderMs { get; private set; }

        /// <summary>The longest single frame of it (the views are rendered one a frame): the hitch on drawing.</summary>
        public float RenderFrameMs { get; private set; }

        /// <summary>When its measuring started (realtime): one that never finished is measured again (see Cached).</summary>
        private readonly float _startedAt = Time.realtimeSinceStartup;

        /// <summary>Neither ready nor failed 15 s on: its measuring stopped (a view coroutine ended with the VR manager).</summary>
        internal bool Stalled => !Ready && !Failed && Time.realtimeSinceStartup - _startedAt > 15f;

        /// <summary>1 while this measuring holds one of the in-flight places (given back once, however it ends).</summary>
        private int _holdsPlace;

        /// <summary>
        /// Gives back this measuring's in-flight place, once: on its end, its failure, or when it is given up as stalled after 15 s
        /// (a read-back that never came back kept its place for the session, and the warm-up then waited its full 2 s on every
        /// weapon at four of them; lifecycle hunt, 2026-10-20).
        /// </summary>
        private void ReleasePlace()
        {
            if (Interlocked.Exchange(ref _holdsPlace, 0) == 1)
            {
                Interlocked.Decrement(ref _inFlight);
            }
        }

        private GunVolume(string key, View[] views, Bounds box)
        {
            Key = key;
            _views = views;
            _box = box;
            _boxMin = box.center - box.extents;
            _boxMax = box.center + box.extents;
        }

        /// <summary>Where the frame is in the world right now (the gun's grip and aim).</summary>
        public void SetPose(Vector3 position, Quaternion rotation)
        {
            _pos = position;
            _inv = Runtime.RigidPose.Conjugate(rotation);
            foreach (View v in _views)
            {
                v.WorldToView = v.ToView * _inv;
            }
        }

        /// <summary>Self-test: answer queries the earlier way (frame first, a box test, then each view) to compare speed.</summary>
        internal static bool LegacyQueries;

        /// <summary>Within a finger's radius of the model (world space, at the last <see cref="SetPose"/>).</summary>
        public bool Solid(Vector3 world)
        {
            if (!_ready)
            {
                return false;
            }
            if (LegacyQueries)
            {
                return SolidLocal(_inv * (world - _pos));
            }
            // One rotation per view straight from the world (no frame step, no box test: every view's picture already
            // rejects points beyond the model), multiplies instead of divides, out at the first view that says no.
            Vector3 d = world - _pos;
            View[] views = _views;
            for (int n = 0; n < views.Length; n++)
            {
                View v = views[n];
                Vector3 q = v.WorldToView * d;
                float fx = (q.x - v.MinX) * v.InvTexel, fy = (q.y - v.MinY) * v.InvTexel;
                if (fx < 0f || fy < 0f || fx >= v.W || fy >= v.H)
                {
                    return false;
                }
                int k = (int)fy * v.W + (int)fx;
                if (q.z < v.Near[k] || q.z > v.Far[k])
                {
                    return false;
                }
            }
            return true;
        }

        public bool SolidLocal(Vector3 p) => Test(p, true);

        /// <summary>
        /// Like <see cref="Solid"/>, with narrow recesses closed over: a hand racking the action rests on the receiver's
        /// skin instead of a fingertip sinking into an ejection port (the Vector family, Tasks 233). Holes that go through
        /// the model (a trigger guard) stay open.
        /// </summary>
        public bool SolidClosed(Vector3 world)
        {
            if (!_ready)
            {
                return false;
            }
            Vector3 d = world - _pos;
            View[] views = _views;
            for (int n = 0; n < views.Length; n++)
            {
                View v = views[n];
                float[] near = v.ClosedNear ?? v.Near, far = v.ClosedFar ?? v.Far;
                Vector3 q = v.WorldToView * d;
                float fx = (q.x - v.MinX) * v.InvTexel, fy = (q.y - v.MinY) * v.InvTexel;
                if (fx < 0f || fy < 0f || fx >= v.W || fy >= v.H)
                {
                    return false;
                }
                int k = (int)fy * v.W + (int)fx;
                if (q.z < near[k] || q.z > far[k])
                {
                    return false;
                }
            }
            return true;
        }

        /// <summary>
        /// Between the model's two sides as the side view sees it (the first view looks along the gun's right axis and keeps
        /// both of its surfaces): a magazine inside a grip is, one going up beside it or in front of it is not. The other views
        /// leave a magazine well open from below, so this asks the side view alone (self-test).
        /// </summary>
        public bool InsideSideView(Vector3 world)
        {
            if (!_ready || _views.Length == 0)
            {
                return false;
            }
            View v = _views[0];
            Vector3 q = v.ToView * (_inv * (world - _pos));
            int i = Mathf.FloorToInt((q.x - v.MinX) / v.Texel);
            int j = Mathf.FloorToInt((q.y - v.MinY) / v.Texel);
            if (i < 0 || j < 0 || i >= v.W || j >= v.H)
            {
                return false;
            }
            int k = j * v.W + i;
            return q.z >= v.BareNear[k] && q.z <= v.BareFar[k];
        }

        /// <summary>Inside the model itself, not grown (self-test: how deep a hand sinks into the gun).</summary>
        public bool InsideModel(Vector3 world) => _ready && Test(_inv * (world - _pos), false);

        /// <summary>Like <see cref="Solid"/> for one of several objects sharing this volume, at its own pose.</summary>
        public bool SolidAt(Vector3 world, Vector3 position, Quaternion inverseRotation) => _ready && Test(inverseRotation * (world - position), true);

        public bool InsideModelAt(Vector3 world, Vector3 position, Quaternion inverseRotation) => _ready && Test(inverseRotation * (world - position), false);

        /// <summary>Reading back or measuring failed: the volume never becomes ready (the next request measures again).</summary>
        public bool Failed { get; private set; }

        /// <summary>
        /// The side view (the first view, along the frame's right: the gun seen from its side): whether a world point lies within
        /// the gun's silhouette there, and <paramref name="withinThickness"/> whether it also lies between the gun's two sides
        /// along that view (not out beside a side panel). A magazine point hidden in the well when seated that, on its way in,
        /// leaves the silhouette stands out of the grip's front or back face into the air; one leaving the thickness pokes out
        /// through a side (HeldWeapon.FitMagazineLine, task 311).
        /// </summary>
        public bool InSideSilhouette(Vector3 world, out bool withinThickness)
        {
            withinThickness = false;
            if (!_ready || _views == null || _views.Length == 0)
            {
                return false;
            }
            View v = _views[0];
            if (v.BareNear == null)
            {
                return false;
            }
            Vector3 q = v.ToView * (_inv * (world - _pos));
            int i = Mathf.FloorToInt((q.x - v.MinX) / v.Texel);
            int j = Mathf.FloorToInt((q.y - v.MinY) / v.Texel);
            if (i < 0 || j < 0 || i >= v.W || j >= v.H)
            {
                return false;
            }
            int k = j * v.W + i;
            float a = v.BareNear[k], b = v.BareFar[k];
            if (a > b)
            {
                return false;
            }
            withinThickness = q.z >= a - 0.0005f && q.z <= b + 0.0005f;
            return true;
        }

        private bool Test(Vector3 p, bool grown)
        {
            // The box compared by hand (Bounds.Contains crossed into native code on every point: the glove tests held
            // magazines this way point by point each frame), then each view as Solid does it: multiplies instead of divides,
            // a cast instead of FloorToInt (the same for the non-negative values left after the check before it).
            if (!_ready || p.x < _boxMin.x || p.y < _boxMin.y || p.z < _boxMin.z || p.x > _boxMax.x || p.y > _boxMax.y || p.z > _boxMax.z)
            {
                return false;
            }
            View[] views = _views;
            for (int n = 0; n < views.Length; n++)
            {
                View v = views[n];
                Vector3 q = v.ToView * p;
                float fx = (q.x - v.MinX) * v.InvTexel, fy = (q.y - v.MinY) * v.InvTexel;
                if (fx < 0f || fy < 0f || fx >= v.W || fy >= v.H)
                {
                    return false;
                }
                int k = (int)fy * v.W + (int)fx;
                float[] near = grown ? v.Near : v.BareNear, far = grown ? v.Far : v.BareFar;
                if (q.z < near[k] || q.z > far[k])
                {
                    return false;
                }
            }
            return true;
        }

        /// <summary>
        /// The volume of this weapon type (measured now from the weapon as it is posed, or the one measured before), or
        /// null when it cannot be measured. <paramref name="posedByHand"/>: posed by a hand of the mod's (the free hand's second
        /// weapon), measured from the first frame, not only once the game calls it unholstered. Its draw time ran 1.2 s and
        /// more, the measurement started before it ended and failed ("put away before every view was taken"), and the glove
        /// had nothing to close on meanwhile.
        /// </summary>
        public static GunVolume For(WeaponRig rig, Quaternion frame, Vector3 origin, bool posedByHand = false)
        {
            if (rig.GunBody == null)
            {
                return null;
            }
            GunCollider.Envelope(rig.Data, frame, origin, out Vector3 envMin, out Vector3 envMax);
            return Cached(rig.Data.Key, () =>
            {
                Runtime.RigidPose? Now() => rig.GunBody != null && rig.Weapon != null && (rig.Weapon.unholstered || posedByHand && rig.Weapon.gameObject.activeInHierarchy)
                    ? new Runtime.RigidPose(rig.ToWorld(origin), rig.GunBody.rotation * frame)
                    : (Runtime.RigidPose?)null;
                return Build(rig.Data.Key, GunCollider.VisibleRenderers(rig), rig.ToWorld(origin), rig.GunBody.rotation * frame, envMin, envMax, 5, Now,
                    v => v.WantsHull = true);
            });
        }

        /// <summary>
        /// The volume of any set of renderers (a magazine on the gun or in the hand), in the frame of <paramref name="frame"/>
        /// as it is now; kept under <paramref name="key"/>.
        /// </summary>
        /// <param name="key">The cache key it is kept under.</param>
        /// <param name="renderers">What is measured.</param>
        /// <param name="frame">The transform whose space the volume is in.</param>
        /// <param name="views">How many depth views it is rendered from.</param>
        /// <param name="hullSlice">With a convex hull fitted too: the slice thickness (0: no hull).</param>
        /// <param name="hullPieces">With a hull: the most pieces it may have.</param>
        /// <param name="now">All views at once (the frame is gone at the end of this frame).</param>
        public static GunVolume Measure(string key, List<Renderer> renderers, Transform frame, int views = 5, float hullSlice = 0f, int hullPieces = 0, bool now = false)
        {
            if (frame == null || renderers.Count == 0)
            {
                return null;
            }
            return Cached(key, () =>
            {
                Runtime.RigidPose? Now() => frame != null ? new Runtime.RigidPose(frame.position, frame.rotation) : (Runtime.RigidPose?)null;
                // The hull settings set before any view is rendered: the worker fits the hull when it sees them.
                return Build(key, renderers, frame.position, frame.rotation, Vector3.one * -10f, Vector3.one * 10f, views, now ? null : (Func<Runtime.RigidPose?>)Now,
                    hullSlice > 0f ? v =>
                    {
                        v.HullSlice = hullSlice;
                        v.HullMaxPieces = hullPieces;
                        v.WantsHull = true;
                    } : (Action<GunVolume>)null);
            });
        }

        private static readonly Dictionary<int, Stack<Color32[]>> PixelPool = new Dictionary<int, Stack<Color32[]>>();

        /// <summary>A picture buffer of this many pixels: pooled (rented on the main thread, given back by the worker).</summary>
        private static Color32[] RentPixels(int length)
        {
            lock (PixelPool)
            {
                if (PixelPool.TryGetValue(length, out Stack<Color32[]> free) && free.Count > 0)
                {
                    return free.Pop();
                }
            }
            return new Color32[length];
        }

        private static void ReturnPixels(Color32[] pixels)
        {
            if (pixels == null)
            {
                return;
            }
            lock (PixelPool)
            {
                if (!PixelPool.TryGetValue(pixels.Length, out Stack<Color32[]> free))
                {
                    free = PixelPool[pixels.Length] = new Stack<Color32[]>();
                }
                if (free.Count < 12)
                {
                    free.Push(pixels);
                }
            }
        }

        /// <summary>Drops every measured shape (the weapon overrides changed the grip frames they were measured in).</summary>
        internal static void ClearCache()
        {
            // Their places too: a measuring that had stalled (a read-back never back) held one for the rest of the session, and
            // five of them kept the warm-up waiting its full 2 s at every step (bug hunt, 2026-10-20 night).
            foreach (GunVolume v in Cache.Values)
            {
                v.ReleasePlace();
            }
            Cache.Clear();
            CacheOrder.Clear();
        }

        private static GunVolume Cached(string key, Func<GunVolume> build)
        {
            if (Cache.TryGetValue(key, out GunVolume cached))
            {
                CacheOrder.Remove(key);
                // Not ready after 15 s is a measuring that stopped (its views' coroutine ended with the VR manager, an exception in a
                // view): kept "in progress", that weapon got no hand shape for the rest of the session (review, 2026-10-20).
                if (!cached.Failed && (cached.Ready || Time.realtimeSinceStartup - cached._startedAt < 15f))
                {
                    CacheOrder.Add(key);
                    return cached;
                }
                cached.ReleasePlace(); // given up: its place is free again
                Cache.Remove(key); // its read-back or measuring failed: measured again
            }
            GunVolume v = null;
            try
            {
                v = build();
            }
            catch (Exception e)
            {
                Log.WarnOnce("gunvolume:" + key, $"Could not measure the shape of {key} for the hands: {e.Message}");
            }
            if (v == null)
            {
                return null; // not kept: tried again the next time it is held
            }
            Cache[key] = v;
            CacheOrder.Add(key);
            while (CacheOrder.Count > CacheSize)
            {
                int oldest = CacheOrder.FindIndex(k => k.StartsWith("tp:", StringComparison.Ordinal));
                oldest = oldest >= 0 && oldest < CacheOrder.Count - 1 ? oldest : 0;
                if (Cache.TryGetValue(CacheOrder[oldest], out GunVolume evicted))
                {
                    evicted.ReleasePlace(); // (released once only: a measuring that finishes later releases nothing)
                }
                Cache.Remove(CacheOrder[oldest]);
                CacheOrder.RemoveAt(oldest);
            }
            return v;
        }

        private static Shader DepthShader()
        {
            if (!_shaderLooked)
            {
                _shaderLooked = true;
                _depthShader = Shader.Find("Hidden/Internal-DepthNormalsTexture");
                if (_depthShader == null)
                {
                    Log.Warn("The depth shader is missing: hands close on held weapons by their pose alone.");
                }
            }
            return _depthShader;
        }

        private static int _layer = -1;
        private static int _layerScene = -1;

        /// <summary>
        /// A layer nothing in the scene draws on (unnamed and holding no renderer), so the shape camera sees only the
        /// renderers moved onto it. Checked once per scene.
        /// </summary>
        /// <summary>A new scene: its free layer found now, in the load (on the first hold it scanned every renderer of the map; 255.P3-3).</summary>
        internal static void OnSceneLoaded() => FreeLayer();

        private static int FreeLayer()
        {
            int scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().handle;
            if (_layer >= 0 && _layerScene == scene)
            {
                return _layer;
            }
            _layerScene = scene;
            var used = new bool[32];
            foreach (Renderer r in UnityEngine.Object.FindObjectsOfType<Renderer>())
            {
                used[r.gameObject.layer] = true;
            }
            _layer = 31;
            for (int l = 31; l >= 20; l--)
            {
                if (!used[l] && string.IsNullOrEmpty(LayerMask.LayerToName(l)))
                {
                    _layer = l;
                    break;
                }
            }
            return _layer;
        }

        /// <param name="key">The cache key it is kept under.</param>
        /// <param name="renderers">What is measured.</param>
        /// <param name="fp">The frame's position the volume is measured in.</param>
        /// <param name="fr">The frame's rotation.</param>
        /// <param name="envMin">The volume's box is cut to this corner (frame space).</param>
        /// <param name="envMax">And to this one.</param>
        /// <param name="viewCount">How many depth views it is rendered from.</param>
        /// <param name="poseNow">Where the frame is now (null: gone), asked once a frame while the views are rendered one a
        /// frame; null to render them all at once from (<paramref name="fp"/>, <paramref name="fr"/>).</param>
        /// <param name="setUp">Run on the new volume before any view is rendered. Where a read-back is synchronous (no async
        /// read-back on the GPU), the decode is queued inside the render, and hull settings set after Build came too late for the
        /// worker (threads hunt, 2026-10-21).</param>
        private static GunVolume Build(string key, List<Renderer> renderers, Vector3 fp, Quaternion fr, Vector3 envMin, Vector3 envMax, int viewCount = 5,
            Func<Runtime.RigidPose?> poseNow = null, Action<GunVolume> setUp = null)
        {
            // Timed on its own in a profiled run (its main-thread part: the views drawn and the read-backs queued).
            if (!Core.PerfProbe.Enabled)
            {
                return BuildNow(key, renderers, fp, fr, envMin, envMax, viewCount, poseNow, setUp);
            }
            Core.PerfProbe.Begin("shape measuring started");
            try
            {
                return BuildNow(key, renderers, fp, fr, envMin, envMax, viewCount, poseNow, setUp);
            }
            finally
            {
                Core.PerfProbe.End("shape measuring started");
            }
        }

        private static GunVolume BuildNow(string key, List<Renderer> renderers, Vector3 fp, Quaternion fr, Vector3 envMin, Vector3 envMax, int viewCount,
            Func<Runtime.RigidPose?> poseNow, Action<GunVolume> setUp)
        {
            Shader shader = DepthShader();
            if (shader == null || renderers.Count == 0)
            {
                return null;
            }
            var watch = System.Diagnostics.Stopwatch.StartNew();
            Quaternion finv = Runtime.RigidPose.Conjugate(fr);

            // The model's extent in the frame, kept within the envelope a held weapon can sensibly have.
            var box = new Bounds();
            bool any = false;
            foreach (Renderer r in renderers)
            {
                Bounds b = r.bounds;
                for (int c = 0; c < 8; c++)
                {
                    Vector3 corner = b.center + Vector3.Scale(b.extents, new Vector3((c & 1) == 0 ? -1 : 1, (c & 2) == 0 ? -1 : 1, (c & 4) == 0 ? -1 : 1));
                    Vector3 local = finv * (corner - fp);
                    if (!any)
                    {
                        box = new Bounds(local, Vector3.zero);
                        any = true;
                    }
                    else
                    {
                        box.Encapsulate(local);
                    }
                }
            }
            box.SetMinMax(Vector3.Max(box.min, envMin), Vector3.Min(box.max, envMax));
            if (box.size.x <= 0f || box.size.y <= 0f || box.size.z <= 0f)
            {
                return null;
            }
            box.Expand(2f * Grow + 0.004f);

            var allDirs = new[]
            {
                (Vector3.right, Vector3.up),
                (Vector3.up, Vector3.forward),
                (Vector3.forward, Vector3.up),
                // Diagonals round the barrel: they cut the corners off round handguards and grips.
                ((Vector3.right + Vector3.up).normalized, Vector3.forward),
                ((Vector3.right - Vector3.up).normalized, Vector3.forward)
            };
            var dirs = new (Vector3, Vector3)[Mathf.Clamp(viewCount, 3, allDirs.Length)];
            System.Array.Copy(allDirs, dirs, dirs.Length);
            var views = new View[dirs.Length];
            var job = new Job { Views = views, MinZ = new float[dirs.Length], MaxZ = new float[dirs.Length], Far = new float[dirs.Length], Front = new Color32[dirs.Length][], Back = new Color32[dirs.Length][] };

            // Every view's place first (pure arithmetic); the pictures after.
            var looks = new Quaternion[dirs.Length];
            var mids = new Vector3[dirs.Length];
            int texels = 0;
            for (int d = 0; d < dirs.Length; d++)
            {
                Quaternion look = Quaternion.LookRotation(dirs[d].Item1, dirs[d].Item2);
                Quaternion toView = Runtime.RigidPose.Conjugate(look);
                Vector3 min = Vector3.positiveInfinity, max = Vector3.negativeInfinity;
                for (int c = 0; c < 8; c++)
                {
                    Vector3 corner = box.center + Vector3.Scale(box.extents, new Vector3((c & 1) == 0 ? -1 : 1, (c & 2) == 0 ? -1 : 1, (c & 4) == 0 ? -1 : 1));
                    Vector3 q = toView * corner;
                    min = Vector3.Min(min, q);
                    max = Vector3.Max(max, q);
                }
                Vector3 size = max - min;
                float texel = Mathf.Max(FineTexel, Mathf.Max(size.x, size.y) / MaxTexels);
                int w = Mathf.Max(8, Mathf.CeilToInt(size.x / texel));
                int h = Mathf.Max(8, Mathf.CeilToInt(size.y / texel));
                Vector3 mid = (min + max) * 0.5f;
                views[d] = new View
                {
                    ToView = toView,
                    WorldToView = toView,
                    InvTexel = 1f / texel,
                    Texel = texel,
                    W = w,
                    H = h,
                    MinX = mid.x - w * texel * 0.5f,
                    MinY = mid.y - h * texel * 0.5f
                };
                texels += w * h;
                job.MinZ[d] = min.z;
                job.MaxZ[d] = max.z;
                job.Far[d] = size.z + 2f * Gap;
                looks[d] = look;
                mids[d] = mid;
            }
            GunVolume volume = new GunVolume(key, views, box) { Texels = texels };
            setUp?.Invoke(volume);
            volume._holdsPlace = 1;
            Interlocked.Increment(ref _inFlight);
            foreach (Renderer r in renderers)
            {
                volume._wantHull |= !ShapeFit.Readable(r);
            }
            job.Volume = volume;
            job.Left = dirs.Length * 2;
            var staged = new Staged
            {
                Job = job, Renderers = renderers, Dirs = dirs, Looks = looks, Mids = mids, Shader = shader, PoseNow = poseNow
            };
            if (poseNow == null || VRManager.Instance == null)
            {
                // All at once, from the pose given (a model laid out for the warm-up, gone at the end of this frame).
                try
                {
                    RenderViews(staged, 0, dirs.Length, fp, fr);
                }
                catch (Exception e)
                {
                    // Failed as a whole, which also gives back its place among the measures in flight (it leaked, and the warm-up
                    // then waited its full two seconds on every weapon; review, 2026-10-20).
                    Log.WarnOnce("gunvolume-views:" + key, $"Rendering the views of {key} failed: {e.Message}");
                    while (job.Left > 0)
                    {
                        job.Arrived(0, null);
                    }
                    return volume;
                }
                volume.RenderMs = (float)watch.Elapsed.TotalMilliseconds;
                volume.RenderFrameMs = volume.RenderMs;
            }
            else
            {
                // One view a frame, each from where the weapon is then: all ten pictures in the frame the weapon was first held
                // took 11 ms on average and up to 17 (a dropped frame or two on every first hold; 255.P1.1, P6-4).
                volume.RenderMs = (float)watch.Elapsed.TotalMilliseconds;
                volume.RenderFrameMs = volume.RenderMs;
                VRManager.Instance.StartCoroutine(RenderStaged(staged));
            }
            return volume;
        }

        /// <summary>A volume's pictures still to be rendered, one view a frame.</summary>
        private sealed class Staged
        {
            public Job Job;
            public List<Renderer> Renderers;
            public (Vector3, Vector3)[] Dirs;
            public Quaternion[] Looks;
            public Vector3[] Mids;
            public Shader Shader;
            public Func<Runtime.RigidPose?> PoseNow;
        }

        private static System.Collections.IEnumerator RenderStaged(Staged s)
        {
            GunVolume volume = s.Job.Volume;
            for (int d = 0; d < s.Dirs.Length; d++)
            {
                yield return null;
                Runtime.RigidPose? pose = s.PoseNow();
                if (pose == null || s.Renderers.Exists(r => r == null))
                {
                    // The weapon or magazine went away (holstered, dropped, destroyed) before every view was taken: failed, and
                    // measured again the next time it is held.
                    for (int t = 2 * d; t < 2 * s.Dirs.Length; t++)
                    {
                        s.Job.Arrived(t, null);
                    }
                    yield break;
                }
                var watch = System.Diagnostics.Stopwatch.StartNew();
                try
                {
                    RenderViews(s, d, d + 1, pose.Value.position, pose.Value.rotation);
                }
                catch (Exception e)
                {
                    Log.WarnOnce("gunvolume-staged:" + volume.Key, $"Rendering a view of {volume.Key} failed: {e.Message}");
                    for (int t = 2 * d; t < 2 * s.Dirs.Length; t++)
                    {
                        s.Job.Arrived(t, null);
                    }
                    yield break;
                }
                float ms = (float)watch.Elapsed.TotalMilliseconds;
                volume.RenderMs += ms;
                volume.RenderFrameMs = Mathf.Max(volume.RenderFrameMs, ms);
            }
        }

        /// <summary>Renders views [from, to) of a volume from the frame at (fp, fr) and asks for their pictures back.</summary>
        private static void RenderViews(Staged s, int from, int to, Vector3 fp, Quaternion fr)
        {
            Job job = s.Job;
            List<Renderer> renderers = s.Renderers;
            int layer = FreeLayer();
            var oldLayers = new int[renderers.Count];
            for (int i = 0; i < renderers.Count; i++)
            {
                oldLayers[i] = renderers[i].gameObject.layer;
            }
            var go = new GameObject("RavenfieldVR Shape Camera");
            Camera cam = go.AddComponent<Camera>();
            cam.enabled = false;
            cam.orthographic = true;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0f, 0f, 0f, 0f);
            cam.cullingMask = 1 << layer;
            cam.stereoTargetEye = StereoTargetEyeMask.None;
            cam.useOcclusionCulling = false;
            cam.allowHDR = false;
            cam.allowMSAA = false;
            cam.renderingPath = RenderingPath.Forward;
            var targets = new RenderTexture[(to - from) * 2];
            try
            {
                for (int i = 0; i < renderers.Count; i++)
                {
                    renderers[i].gameObject.layer = layer;
                }
                for (int d = from; d < to; d++)
                {
                    View v = job.Views[d];
                    Quaternion look = s.Looks[d];
                    Vector3 mid = s.Mids[d];
                    cam.orthographicSize = v.H * v.Texel * 0.5f;
                    cam.aspect = (float)v.W / v.H;
                    cam.nearClipPlane = 0.005f;
                    cam.farClipPlane = job.Far[d];
                    // From the near end looking along the direction, then from the far end looking back.
                    targets[2 * (d - from)] = Render(cam, fp + fr * (look * new Vector3(mid.x, mid.y, job.MinZ[d] - Gap)), fr * look, v.W, v.H, s.Shader);
                    targets[2 * (d - from) + 1] = Render(cam, fp + fr * (look * new Vector3(mid.x, mid.y, job.MaxZ[d] + Gap)),
                        fr * Quaternion.LookRotation(-s.Dirs[d].Item1, s.Dirs[d].Item2), v.W, v.H, s.Shader);
                }
            }
            finally
            {
                for (int i = 0; i < renderers.Count; i++)
                {
                    if (renderers[i] != null)
                    {
                        renderers[i].gameObject.layer = oldLayers[i];
                    }
                }
                UnityEngine.Object.Destroy(go);
            }

            // The pictures come back from the GPU a frame or two later without stalling the game; where that is not
            // supported, they are read at once.
            bool async = SystemInfo.supportsAsyncGPUReadback;
            for (int t = 0; t < targets.Length; t++)
            {
                int index = 2 * from + t;
                RenderTexture rt = targets[t];
                if (rt == null)
                {
                    job.Arrived(index, null);
                    continue;
                }
                if (async)
                {
                    AsyncGPUReadback.Request(rt, 0, TextureFormat.RGBA32, request =>
                    {
                        Color32[] pixels = null;
                        if (!request.hasError)
                        {
                            // Into a pooled buffer (2 MB a picture, ten a weapon: new arrays every measure were large-object
                            // garbage); back to the pool once decoded.
                            Unity.Collections.NativeArray<Color32> data = request.GetData<Color32>();
                            pixels = RentPixels(data.Length);
                            data.CopyTo(pixels);
                        }
                        RenderTexture.ReleaseTemporary(rt);
                        job.Arrived(index, pixels);
                    });
                }
                else
                {
                    job.Arrived(index, ReadNow(rt));
                    RenderTexture.ReleaseTemporary(rt);
                }
            }
        }

        private const float Gap = 0.05f;

        /// <summary>One volume's pictures on their way back from the GPU, then decoded and grown on a worker thread.</summary>
        private sealed class Job
        {
            public GunVolume Volume;
            public View[] Views;
            public float[] MinZ, MaxZ, Far;
            public Color32[][] Front, Back;
            public int Left;
            public bool Failed;
            private readonly System.Diagnostics.Stopwatch _since = System.Diagnostics.Stopwatch.StartNew();

            public void Arrived(int index, Color32[] pixels)
            {
                if (pixels == null)
                {
                    Failed = true;
                }
                else if (index % 2 == 0)
                {
                    Front[index / 2] = pixels;
                }
                else
                {
                    Back[index / 2] = pixels;
                }
                if (--Left > 0)
                {
                    return;
                }
                if (Failed)
                {
                    Volume.ReleasePlace();
                    Volume.Failed = true;
                    Log.WarnOnce("gunvolume-read:" + Volume.Key, $"Measuring the shape of {Volume.Key} failed (put away before every view was taken, or a read-back failed): measured again when held.");
                    return;
                }
                // Decoding and growing by a finger's radius is pure arithmetic: off the main thread.
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    try
                    {
                        for (int d = 0; d < Views.Length; d++)
                        {
                            Decode(Views[d], Front[d], Back[d], MinZ[d], MaxZ[d], Far[d], out float[] near, out float[] far);
                            GrowView(Views[d], near, far);
                            CloseRecesses(Views[d]);
                        }
                        for (int d = 0; d < Views.Length; d++)
                        {
                            ReturnPixels(Front[d]);
                            ReturnPixels(Back[d]);
                        }
                        Front = Back = null;
                        if (Volume.WantsHull)
                        {
                            // Same slicing as a readable model gets from its vertices, from the rendered surface.
                            Volume.HullPieces = ShapeFit.FitCloud(Volume.SurfacePoints(2), Volume.HullSlice, Volume.HullMaxPieces);
                        }
                        Volume.BuildMs = (float)_since.Elapsed.TotalMilliseconds;
                        Volume._ready = true; // last: everything above is published with it
                        Volume.OnReady?.Invoke(Volume);
                    }
                    catch (Exception e)
                    {
                        Volume.Failed = true;
                        Log.WarnOnce("gunvolume-grow:" + Volume.Key, $"Measuring {Volume.Key} for the hands failed: {e.Message}");
                    }
                    finally
                    {
                        Volume.ReleasePlace();
                    }
                });
            }
        }

        /// <summary>
        /// Near and far surface per texel along the view (frame metres) from the two depth pictures; empty texels get an
        /// empty span. A surface seen from one side only (a single-sided sheet) becomes a sheet.
        /// </summary>
        private static void Decode(View v, Color32[] front, Color32[] back, float minZ, float maxZ, float far, out float[] near, out float[] farSpan)
        {
            int w = v.W, h = v.H;
            near = new float[w * h];
            farSpan = new float[w * h];
            for (int j = 0; j < h; j++)
            {
                for (int i = 0; i < w; i++)
                {
                    int k = j * w + i;
                    float dn = Depth(front[k], far);
                    float db = Depth(back[j * w + (w - 1 - i)], far); // the back view is mirrored left to right
                    float zn = dn > 0f ? minZ - Gap + dn * far : float.PositiveInfinity;
                    float zf = db > 0f ? maxZ + Gap - db * far : float.NegativeInfinity;
                    if (dn > 0f && db <= 0f)
                    {
                        zf = zn;
                    }
                    else if (db > 0f && dn <= 0f)
                    {
                        zn = zf;
                    }
                    near[k] = zn;
                    farSpan[k] = zf;
                }
            }
        }

        /// <summary>Linear depth as a fraction of the far plane (EncodeDepthNormal keeps it in the last two channels); 0 where nothing was drawn.</summary>
        private static float Depth(Color32 c, float far)
        {
            float d = c.b / 255f + c.a / 65025f;
            return d * far > 0.02f ? d : 0f;
        }

        /// <summary>
        /// How much of <paramref name="part"/> is seen when the gun is looked at from its left, right, top and bottom: of the
        /// pixels the part covers alone, the share where it is the gun's nearest surface (the picture changes when it is left
        /// out). A carrier buried in the receiver reads 0, a slide or a knob standing out most of it. The arms are not drawn.
        /// Rendered and read back now, in the frame it is called (12 small pictures, a few GPU stalls): the rack self-test needs
        /// the value in the same call. A hold in play should use <see cref="VisibleShareAsync"/>, which takes the same pictures
        /// a frame or two later.
        /// </summary>
        internal static float VisibleShare(List<Renderer> gun, ICollection<Renderer> part, Vector3 aimWorld, Vector3 upWorld, out string perView, float texel = 0.002f)
        {
            float share = -1f;
            string text = "";
            MeasureShare(gun, part, aimWorld, upWorld, texel, false, (s, t) => { share = s; text = t; });
            perView = text;
            return share;
        }

        /// <summary>
        /// <see cref="VisibleShare"/> for a hold in play (task 263, 2026-10-18: the first hold of a hidden-slide gun read its 12
        /// pictures back in the hold frame, with 12 GPU stalls and a few MB of garbage). The same renders and the same pictures,
        /// but they come back from the GPU a frame or two later, and <paramref name="done"/> runs then on the main thread with the
        /// share and the per-view text (-1 when not measurable). It runs before this returns when there is nothing to measure or
        /// when asynchronous read-back is not supported. If this throws, <paramref name="done"/> is never called.
        /// </summary>
        internal static void VisibleShareAsync(List<Renderer> gun, ICollection<Renderer> part, Vector3 aimWorld, Vector3 upWorld, Action<float, string> done, float texel = 0.002f)
        {
            MeasureShare(gun, part, aimWorld, upWorld, texel, true, done);
        }

        /// <summary>
        /// The measurement behind both. With <paramref name="deferred"/> the pictures are read back as the GPU finishes them;
        /// otherwise, or where asynchronous read-back is not supported, each is read at once and <paramref name="done"/> runs
        /// before this returns.
        /// </summary>
        private static void MeasureShare(List<Renderer> gun, ICollection<Renderer> part, Vector3 aimWorld, Vector3 upWorld, float texel, bool deferred, Action<float, string> done)
        {
            Shader shader = DepthShader();
            if (shader == null || part.Count == 0)
            {
                done(-1f, "");
                return;
            }
            var all = new List<Renderer>();
            foreach (Renderer r in gun)
            {
                if (r != null && !part.Contains(r))
                {
                    all.Add(r);
                }
            }
            var mine = new List<Renderer>();
            foreach (Renderer r in part)
            {
                if (r != null)
                {
                    mine.Add(r);
                }
            }
            if (mine.Count == 0)
            {
                done(-1f, "");
                return;
            }
            Bounds wb = mine[0].bounds;
            foreach (Renderer r in all)
            {
                wb.Encapsulate(r.bounds);
            }
            foreach (Renderer r in mine)
            {
                wb.Encapsulate(r.bounds);
            }
            float radius = Mathf.Max(0.05f, wb.extents.magnitude);
            int size = Mathf.Clamp(Mathf.CeilToInt(2f * radius / texel), 64, 512);
            Vector3 aim = aimWorld.normalized, up = upWorld.normalized, right = Vector3.Cross(up, aim).normalized;
            var views = new[] { (right, up, "left"), (-right, up, "right"), (-up, aim, "top"), (up, aim, "bottom") };
            int layer = FreeLayer();
            var owners = new List<GameObject>();
            foreach (Renderer r in all)
            {
                owners.Add(r.gameObject);
            }
            foreach (Renderer r in mine)
            {
                owners.Add(r.gameObject);
            }
            var oldLayers = new int[owners.Count];
            for (int i = 0; i < owners.Count; i++)
            {
                oldLayers[i] = owners[i].layer;
            }
            var go = new GameObject("RavenfieldVR Visibility Camera");
            Camera cam = go.AddComponent<Camera>();
            cam.enabled = false;
            cam.orthographic = true;
            cam.orthographicSize = radius;
            cam.aspect = 1f;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0f, 0f, 0f, 0f);
            cam.cullingMask = 1 << layer;
            cam.stereoTargetEye = StereoTargetEyeMask.None;
            cam.useOcclusionCulling = false;
            cam.allowHDR = false;
            cam.allowMSAA = false;
            cam.renderingPath = RenderingPath.Forward;
            cam.nearClipPlane = 0.005f;
            cam.farClipPlane = 2f * radius + 0.2f;
            void SetLayers(bool rest, bool own)
            {
                for (int i = 0; i < all.Count; i++)
                {
                    all[i].gameObject.layer = rest ? layer : oldLayers[i];
                }
                for (int i = 0; i < mine.Count; i++)
                {
                    mine[i].gameObject.layer = own ? layer : oldLayers[all.Count + i];
                }
            }
            // The pictures in order, three a view: the rest of the gun, the gun with the part, the part alone. Each is read back as
            // soon as it is rendered, or, when deferred, asked for and taken when it arrives; the comparison runs once all of them
            // are in, on the same pixels either way.
            int shots = views.Length * 3;
            var pictures = new Color32[shots][];
            int left = shots;
            // Asynchronous read-back is queued behind the frame's GPU work; where it is not supported, each picture is read at once.
            bool readLater = deferred && SystemInfo.supportsAsyncGPUReadback;
            void Finish()
            {
                if (Array.Exists(pictures, p => p == null))
                {
                    done(-1f, ""); // a picture missing (its read-back failed): not measured
                    return;
                }
                long partPx = 0, seenPx = 0;
                var parts = new List<string>();
                for (int v = 0; v < views.Length; v++)
                {
                    Color32[] without = pictures[3 * v], with = pictures[3 * v + 1], alone = pictures[3 * v + 2];
                    long own = 0, seen = 0;
                    for (int i = 0; i < alone.Length; i++)
                    {
                        Color32 a = alone[i];
                        if (a.r == 0 && a.g == 0 && a.b == 0 && a.a == 0)
                        {
                            continue;
                        }
                        own++;
                        Color32 x = with[i], y = without[i];
                        if (x.r != y.r || x.g != y.g || x.b != y.b || x.a != y.a)
                        {
                            seen++;
                        }
                    }
                    partPx += own;
                    seenPx += seen;
                    parts.Add($"{views[v].Item3} {(own == 0 ? 0f : 100f * seen / own):0}%");
                    ReturnPixels(without);
                    ReturnPixels(with);
                    ReturnPixels(alone);
                }
                done(partPx == 0 ? -1f : (float)seenPx / partPx, string.Join(", ", parts.ToArray()));
            }
            void Take(int shot, RenderTexture rt)
            {
                if (!readLater)
                {
                    try
                    {
                        pictures[shot] = ReadNow(rt);
                    }
                    finally
                    {
                        RenderTexture.ReleaseTemporary(rt);
                    }
                    return;
                }
                AsyncGPUReadback.Request(rt, 0, TextureFormat.RGBA32, request =>
                {
                    Color32[] pixels = null;
                    try
                    {
                        if (request.hasError)
                        {
                            pixels = ReadNow(rt); // read again at once, so the value stays exact
                        }
                        else
                        {
                            // Into a pooled buffer, as the views of a measured shape are: a new array of a picture each time was garbage.
                            Unity.Collections.NativeArray<Color32> data = request.GetData<Color32>();
                            pixels = RentPixels(data.Length);
                            data.CopyTo(pixels);
                        }
                    }
                    catch (Exception e)
                    {
                        // Counted down all the same (thrown out of here, it never reached zero and the slide stayed "being measured"
                        // for the session; review, 2026-10-20): a missing picture fails the measurement in Finish.
                        Log.WarnOnce("slidemeasure-read", "A slide measurement's read-back failed: " + e.Message);
                        pixels = null;
                    }
                    finally
                    {
                        RenderTexture.ReleaseTemporary(rt);
                    }
                    pictures[shot] = pixels;
                    if (--left == 0)
                    {
                        Finish();
                    }
                });
            }
            void Shoot(int shot, Vector3 dir, Vector3 camUp)
            {
                Take(shot, Render(cam, wb.center - dir * (radius + 0.1f), Quaternion.LookRotation(dir, camUp), size, size, shader));
            }
            try
            {
                for (int v = 0; v < views.Length; v++)
                {
                    Vector3 dir = views[v].Item1, camUp = views[v].Item2;
                    SetLayers(true, false);
                    Shoot(3 * v, dir, camUp);
                    SetLayers(true, true);
                    Shoot(3 * v + 1, dir, camUp);
                    SetLayers(false, true);
                    Shoot(3 * v + 2, dir, camUp);
                }
            }
            finally
            {
                for (int i = 0; i < owners.Count; i++)
                {
                    if (owners[i] != null)
                    {
                        owners[i].layer = oldLayers[i];
                    }
                }
                UnityEngine.Object.Destroy(go);
            }
            if (!readLater)
            {
                Finish();
            }
        }

        private static RenderTexture Render(Camera cam, Vector3 position, Quaternion rotation, int w, int h, Shader shader)
        {
            cam.transform.SetPositionAndRotation(position, rotation);
            RenderTexture rt = RenderTexture.GetTemporary(w, h, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
            cam.targetTexture = rt;
            cam.RenderWithShader(shader, "");
            cam.targetTexture = null;
            return rt;
        }

        private static Color32[] ReadNow(RenderTexture rt)
        {
            RenderTexture prev = RenderTexture.active;
            var tex = new Texture2D(rt.width, rt.height, TextureFormat.RGBA32, false, true);
            try
            {
                RenderTexture.active = rt;
                tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0, false);
                return tex.GetPixels32();
            }
            finally
            {
                RenderTexture.active = prev;
                UnityEngine.Object.Destroy(tex);
            }
        }

        /// <summary>
        /// The grown span with recesses narrower than twice <see cref="RecessFill"/> filled: a grey-scale closing of the
        /// near and far depth (the nearest surface spread over a square, then the farthest of that over the same square),
        /// kept only where the grown span has a surface (a hole through the model stays a hole) and only ever moving a
        /// surface outward.
        /// </summary>
        private static void CloseRecesses(View v)
        {
            int r = Mathf.Max(1, Mathf.RoundToInt(RecessFill / v.Texel));
            int w = v.W, h = v.H;
            float[] near = v.Near, far = v.Far;
            // Two scratch pictures per worker thread, reused (four new ones per view, each over the large-object limit, fed the
            // collector that pauses the game; 255.P6-3). Only the first w*h of each are used.
            float[] a = Scratch(ref _scratchA, w * h), b = Scratch(ref _scratchB, w * h);
            // Near: dilate the surface toward the viewer (minimum), then erode it back (maximum); empty texels are +inf.
            Filter(near, a, w, h, r, true, false);
            Filter(a, b, w, h, r, true, true);
            Filter(b, a, w, h, r, false, false);
            Filter(a, b, w, h, r, false, true);
            var closedNear = new float[w * h];
            for (int k = 0; k < closedNear.Length; k++)
            {
                closedNear[k] = near[k] > far[k] ? near[k] : Mathf.Min(near[k], b[k]);
            }
            // Far: the same mirrored (maximum, then minimum; empty texels are -inf), in the same two scratch pictures.
            float[] fa = a, fb = b;
            for (int k = 0; k < far.Length; k++)
            {
                fa[k] = -far[k];
            }
            Filter(fa, fb, w, h, r, true, false);
            Filter(fb, fa, w, h, r, true, true);
            Filter(fa, fb, w, h, r, false, false);
            Filter(fb, fa, w, h, r, false, true);
            var closedFar = new float[w * h];
            for (int k = 0; k < closedFar.Length; k++)
            {
                closedFar[k] = near[k] > far[k] ? far[k] : Mathf.Max(far[k], -fa[k]);
            }
            v.ClosedNear = closedNear;
            v.ClosedFar = closedFar;
        }

        [ThreadStatic] private static float[] _scratchA, _scratchB;

        private static float[] Scratch(ref float[] buffer, int length)
        {
            if (buffer == null || buffer.Length < length)
            {
                buffer = new float[length];
            }
            return buffer;
        }

        /// <summary>
        /// One pass of a separable square filter: the minimum (or maximum) over 2r+1 texels along the rows (or the columns).
        /// </summary>
        private static void Filter(float[] src, float[] dst, int w, int h, int r, bool min, bool columns)
        {
            int outer = columns ? w : h, inner = columns ? h : w;
            for (int o = 0; o < outer; o++)
            {
                for (int i = 0; i < inner; i++)
                {
                    float best = min ? float.PositiveInfinity : float.NegativeInfinity;
                    int lo = Mathf.Max(0, i - r), hi = Mathf.Min(inner - 1, i + r);
                    for (int t = lo; t <= hi; t++)
                    {
                        float x = src[columns ? t * w + o : o * w + t];
                        if (min ? x < best : x > best)
                        {
                            best = x;
                        }
                    }
                    // Past the picture's edge counts as empty for the erosion (the maximum): a surface at the border is
                    // not filled toward it.
                    if (!min && (i - r < 0 || i + r > inner - 1))
                    {
                        best = float.PositiveInfinity;
                    }
                    dst[columns ? i * w + o : o * w + i] = best;
                }
            }
        }

        /// <summary>
        /// Grows one view's depth span by <see cref="Grow"/> as a sphere would: a texel d away from a surface texel takes
        /// its span widened by sqrt(r² - d²).
        /// </summary>
        private static void GrowView(View v, float[] near, float[] far)
        {
            float r = Grow;
            int reach = Mathf.CeilToInt(r / v.Texel);
            // The disc's offsets in plain arrays (the inner loop below runs some hundred times per texel; 255.P6-3).
            int side = 2 * reach + 1;
            var di = new int[side * side];
            var dj = new int[side * side];
            var dh = new float[side * side];
            int count = 0;
            for (int y = -reach; y <= reach; y++)
            {
                for (int x = -reach; x <= reach; x++)
                {
                    float d = Mathf.Sqrt(x * x + y * y) * v.Texel;
                    if (d <= r)
                    {
                        di[count] = x;
                        dj[count] = y;
                        dh[count] = Mathf.Sqrt(r * r - d * d);
                        count++;
                    }
                }
            }
            int w = v.W, h = v.H;
            var n = new float[w * h];
            var f = new float[w * h];
            for (int j = 0; j < h; j++)
            {
                for (int i = 0; i < w; i++)
                {
                    float bestN = float.PositiveInfinity, bestF = float.NegativeInfinity;
                    for (int o = 0; o < count; o++)
                    {
                        int ii = i + di[o], jj = j + dj[o];
                        if (ii < 0 || jj < 0 || ii >= w || jj >= h)
                        {
                            continue;
                        }
                        int k2 = jj * w + ii;
                        float a = near[k2], b = far[k2];
                        if (a > b)
                        {
                            continue;
                        }
                        float hh = dh[o];
                        if (a - hh < bestN)
                        {
                            bestN = a - hh;
                        }
                        if (b + hh > bestF)
                        {
                            bestF = b + hh;
                        }
                    }
                    n[j * w + i] = bestN;
                    f[j * w + i] = bestF;
                }
            }
            v.Near = n;
            v.Far = f;
            v.BareNear = near;
            v.BareFar = far;
        }
    }
}
