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
        private const int CacheSize = 12;

        private static readonly Dictionary<string, GunVolume> Cache = new Dictionary<string, GunVolume>();
        private static readonly List<string> CacheOrder = new List<string>();
        private static Shader _depthShader;
        private static bool _shaderLooked;

        private sealed class View
        {
            public Quaternion ToView; // frame -> view: x right, y up, z along the view direction
            public float MinX, MinY, Texel;
            public int W, H;
            public float[] Near, Far; // per texel, along the view's z; empty where Near > Far
            public float[] BareNear, BareFar; // the same before growing: the model itself
        }

        private readonly View[] _views;
        private readonly Bounds _box;
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
                Quaternion toFrame = Quaternion.Inverse(v.ToView);
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

        /// <summary>The part spent on the main thread (rendering and reading back the views): a one-off hitch on drawing.</summary>
        public float RenderMs { get; private set; }

        private GunVolume(string key, View[] views, Bounds box)
        {
            Key = key;
            _views = views;
            _box = box;
        }

        /// <summary>Where the frame is in the world right now (the gun's grip and aim).</summary>
        public void SetPose(Vector3 position, Quaternion rotation)
        {
            _pos = position;
            _inv = Quaternion.Inverse(rotation);
        }

        /// <summary>Within a finger's radius of the model (world space, at the last <see cref="SetPose"/>).</summary>
        public bool Solid(Vector3 world) => _ready && SolidLocal(_inv * (world - _pos));

        public bool SolidLocal(Vector3 p) => Test(p, true);

        /// <summary>Inside the model itself, not grown (self-test: how deep a hand sinks into the gun).</summary>
        public bool InsideModel(Vector3 world) => _ready && Test(_inv * (world - _pos), false);

        private bool Test(Vector3 p, bool grown)
        {
            if (!_ready || !_box.Contains(p))
            {
                return false;
            }
            foreach (View v in _views)
            {
                Vector3 q = v.ToView * p;
                int i = Mathf.FloorToInt((q.x - v.MinX) / v.Texel);
                int j = Mathf.FloorToInt((q.y - v.MinY) / v.Texel);
                if (i < 0 || j < 0 || i >= v.W || j >= v.H)
                {
                    return false;
                }
                int k = j * v.W + i;
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
        /// null when it cannot be measured.
        /// </summary>
        public static GunVolume For(WeaponRig rig, Quaternion frame, Vector3 origin)
        {
            if (rig.GunBody == null)
            {
                return null;
            }
            GunCollider.Envelope(rig.Data, frame, origin, out Vector3 envMin, out Vector3 envMax);
            return Cached(rig.Data.Key, () =>
            {
                GunVolume v = Build(rig.Data.Key, GunCollider.VisibleRenderers(rig), rig.ToWorld(origin), rig.GunBody.rotation * frame, envMin, envMax);
                if (v != null)
                {
                    v.WantsHull = true;
                }
                return v;
            });
        }

        /// <summary>
        /// The volume of any set of renderers (a magazine on the gun or in the hand), in the frame of <paramref name="frame"/>
        /// as it is now; kept under <paramref name="key"/>.
        /// </summary>
        public static GunVolume Measure(string key, List<Renderer> renderers, Transform frame)
        {
            if (frame == null || renderers.Count == 0)
            {
                return null;
            }
            return Cached(key, () => Build(key, renderers, frame.position, frame.rotation, Vector3.one * -10f, Vector3.one * 10f));
        }

        private static GunVolume Cached(string key, Func<GunVolume> build)
        {
            if (Cache.TryGetValue(key, out GunVolume cached))
            {
                CacheOrder.Remove(key);
                CacheOrder.Add(key);
                return cached;
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
                Cache.Remove(CacheOrder[0]);
                CacheOrder.RemoveAt(0);
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

        private static int FreeLayer()
        {
            for (int l = 31; l >= 20; l--)
            {
                if (string.IsNullOrEmpty(LayerMask.LayerToName(l)))
                {
                    return l;
                }
            }
            return 31;
        }

        private static GunVolume Build(string key, List<Renderer> renderers, Vector3 fp, Quaternion fr, Vector3 envMin, Vector3 envMax)
        {
            Shader shader = DepthShader();
            if (shader == null || renderers.Count == 0)
            {
                return null;
            }
            var watch = System.Diagnostics.Stopwatch.StartNew();
            Quaternion finv = Quaternion.Inverse(fr);

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

            var dirs = new[]
            {
                (Vector3.right, Vector3.up),
                (Vector3.up, Vector3.forward),
                (Vector3.forward, Vector3.up),
                ((Vector3.right + Vector3.up).normalized, Vector3.forward),
                ((Vector3.right - Vector3.up).normalized, Vector3.forward)
            };
            var views = new View[dirs.Length];
            var job = new Job { Views = views, MinZ = new float[dirs.Length], MaxZ = new float[dirs.Length], Far = new float[dirs.Length], Front = new Color32[dirs.Length][], Back = new Color32[dirs.Length][] };

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
            var targets = new RenderTexture[dirs.Length * 2];
            GunVolume volume;
            try
            {
                for (int i = 0; i < renderers.Count; i++)
                {
                    renderers[i].gameObject.layer = layer;
                }
                int texels = 0;
                for (int d = 0; d < dirs.Length; d++)
                {
                    Quaternion look = Quaternion.LookRotation(dirs[d].Item1, dirs[d].Item2);
                    Quaternion toView = Quaternion.Inverse(look);
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
                        Texel = texel,
                        W = w,
                        H = h,
                        MinX = mid.x - w * texel * 0.5f,
                        MinY = mid.y - h * texel * 0.5f
                    };
                    texels += w * h;
                    float far = size.z + 2f * Gap;
                    job.MinZ[d] = min.z;
                    job.MaxZ[d] = max.z;
                    job.Far[d] = far;
                    cam.orthographicSize = h * texel * 0.5f;
                    cam.aspect = (float)w / h;
                    cam.nearClipPlane = 0.005f;
                    cam.farClipPlane = far;
                    // From the near end looking along the direction, then from the far end looking back.
                    targets[2 * d] = Render(cam, fp + fr * (look * new Vector3(mid.x, mid.y, min.z - Gap)), fr * look, w, h, shader);
                    targets[2 * d + 1] = Render(cam, fp + fr * (look * new Vector3(mid.x, mid.y, max.z + Gap)), fr * Quaternion.LookRotation(-dirs[d].Item1, dirs[d].Item2), w, h, shader);
                }
                volume = new GunVolume(key, views, box) { Texels = texels };
                foreach (Renderer r in renderers)
                {
                    volume._wantHull |= !ShapeFit.Readable(r);
                }
                job.Volume = volume;
                job.Left = targets.Length;
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
                int index = t;
                RenderTexture rt = targets[t];
                if (async)
                {
                    AsyncGPUReadback.Request(rt, 0, TextureFormat.RGBA32, request =>
                    {
                        Color32[] pixels = request.hasError ? null : request.GetData<Color32>().ToArray();
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
            volume.RenderMs = (float)watch.Elapsed.TotalMilliseconds;
            return volume;
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
                    Log.WarnOnce("gunvolume-read:" + Volume.Key, $"Reading back the shape of {Volume.Key} failed: hands close on it by their pose alone.");
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
                        }
                        Front = Back = null;
                        Volume._ready = true;
                        if (Volume.WantsHull)
                        {
                            // Same slicing as a readable model gets from its vertices, from the rendered surface.
                            Volume.HullPieces = ShapeFit.FitCloud(Volume.SurfacePoints(2));
                        }
                        Volume.BuildMs = (float)_since.Elapsed.TotalMilliseconds;
                    }
                    catch (Exception e)
                    {
                        Log.WarnOnce("gunvolume-grow:" + Volume.Key, $"Measuring {Volume.Key} for the hands failed: {e.Message}");
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
        /// Grows one view's depth span by <see cref="Grow"/> as a sphere would: a texel d away from a surface texel takes
        /// its span widened by sqrt(r² - d²).
        /// </summary>
        private static void GrowView(View v, float[] near, float[] far)
        {
            float r = Grow;
            int reach = Mathf.CeilToInt(r / v.Texel);
            var di = new List<int>();
            var dj = new List<int>();
            var dh = new List<float>();
            for (int y = -reach; y <= reach; y++)
            {
                for (int x = -reach; x <= reach; x++)
                {
                    float d = Mathf.Sqrt(x * x + y * y) * v.Texel;
                    if (d <= r)
                    {
                        di.Add(x);
                        dj.Add(y);
                        dh.Add(Mathf.Sqrt(r * r - d * d));
                    }
                }
            }
            int w = v.W, h = v.H, count = di.Count;
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
