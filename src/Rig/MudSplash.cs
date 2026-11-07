using System.Collections.Generic;
using RavenfieldVR.Core;
using UnityEngine;
using UnityEngine.Rendering;

namespace RavenfieldVR.Rig
{
    /// <summary>
    /// A grenade or shell going off close by throws dirt on your view, like the lens dirt in Call of Duty: soft,
    /// out-of-focus spots of dirt (brown-grey, see-through, a denser rim like a dried drop) all over an invisible visor a
    /// hand's width in front of the eyes, more, bigger and denser the closer and larger the blast, leaning a little toward
    /// the side it came from. They stay a few seconds, the big ones run down slowly, and fade. Water drops (grey, clearer)
    /// for a blast in water. Nothing for a blast behind a wall, and nothing while dead. Listens to the game's explosion
    /// event (ActorManager.OnExplosion), so every explosion that deals damage counts. All spots are one mesh with one
    /// material (the four spot shapes side by side in one texture): one draw call however many there are.
    /// </summary>
    public sealed class MudSplash
    {
        public static MudSplash Instance { get; private set; }

        private const float Distance = 0.25f;
        private const int Pool = 48;
        private const int Variants = 4;
        // Half the visor's width and height that the eyes see at Distance: the headset's own (OpenVR's projection of the left eye,
        // read once), else the Quest 3's (about 110 x 96 degrees). It was the Quest 3's on every headset (sweep).
        private const float QuestHalfWidth = 0.34f, QuestHalfHeight = 0.27f;
        private static float _halfWidth = -1f, _halfHeight = -1f;

        private static float HalfWidth
        {
            get
            {
                Measure();
                return _halfWidth;
            }
        }

        private static float HalfHeight
        {
            get
            {
                Measure();
                return _halfHeight;
            }
        }

        private static void Measure()
        {
            if (_halfWidth > 0f)
            {
                return;
            }
            _halfWidth = QuestHalfWidth;
            _halfHeight = QuestHalfHeight;
            try
            {
                Valve.VR.CVRSystem system = Valve.VR.OpenVR.System;
                if (system == null)
                {
                    return;
                }
                float left = 0f, right = 0f, top = 0f, bottom = 0f;
                system.GetProjectionRaw(Valve.VR.EVREye.Eye_Left, ref left, ref right, ref top, ref bottom);
                float tanX = Mathf.Max(Mathf.Abs(left), Mathf.Abs(right)), tanY = Mathf.Max(Mathf.Abs(top), Mathf.Abs(bottom));
                if (tanX > 0.3f && tanX < 4f && tanY > 0.3f && tanY < 4f) // (a plausible eye: 33 to 152 degrees across)
                {
                    _halfWidth = Distance * tanX;
                    _halfHeight = Distance * tanY;
                }
            }
            catch (System.Exception)
            {
                // (no OpenVR: the Quest 3's)
            }
        }

        private sealed class Splat
        {
            public float Born, Life, Size, Fall, Stretch, Angle;
            public int Variant;
            public Vector2 Position;
            public Color Color;
            public bool Live;
        }

        private readonly Transform _head;
        private readonly GameObject _root;
        private readonly Splat[] _splats = new Splat[Pool];
        private int _liveCount;
        private readonly Texture2D _atlas;
        private readonly Material _material;
        private readonly Mesh _mesh;
        // Four corners per spot, rewritten in place each frame (no new arrays).
        private readonly Vector3[] _verts = new Vector3[Pool * 4];
        private readonly Color32[] _colors = new Color32[Pool * 4];
        private readonly Vector2[] _uvs = new Vector2[Pool * 4];
        private bool _shown;
        private ActorManager _subscribedTo;
        private int _subscribedScene = -1;

        public MudSplash(Transform head)
        {
            Instance = this;
            _head = head;
            _root = new GameObject("Mud Splash") { layer = 5 };
            _root.transform.SetParent(head, false);
            _atlas = MakeAtlas();
            _material = new Material(Shader.Find("Sprites/Default")) { renderQueue = 4000, mainTexture = _atlas };
            var tris = new int[Pool * 6];
            for (int i = 0; i < Pool; i++)
            {
                _splats[i] = new Splat();
                int v = i * 4, t = i * 6;
                // Corners as Unity's quad: (-,-) (+,-) (-,+) (+,+).
                tris[t] = v; tris[t + 1] = v + 3; tris[t + 2] = v + 1;
                tris[t + 3] = v + 3; tris[t + 4] = v; tris[t + 5] = v + 2;
            }
            _mesh = new Mesh { name = "RavenfieldVR Mud" };
            _mesh.MarkDynamic();
            _mesh.vertices = _verts;
            _mesh.colors32 = _colors;
            _mesh.uv = _uvs;
            _mesh.triangles = tris;
            // In front of the eyes, wherever the spots are: never culled.
            _mesh.bounds = new Bounds(new Vector3(0f, 0f, Distance), new Vector3(2f, 2f, 1f));
            _root.AddComponent<MeshFilter>().sharedMesh = _mesh;
            MeshRenderer r = _root.AddComponent<MeshRenderer>();
            r.sharedMaterial = _material;
            r.shadowCastingMode = ShadowCastingMode.Off;
            r.receiveShadows = false;
            _root.SetActive(false);
        }

        /// <summary>How many splats are on the view now (self-test).</summary>
        internal int VisibleCount
        {
            get
            {
                int n = 0;
                foreach (Splat s in _splats)
                {
                    n += s.Live ? 1 : 0;
                }
                return n;
            }
        }

        private bool _wasUnderwater;

        /// <summary>Seconds of play: what the dirt ages by.</summary>
        private float _clock;

        public void Update()
        {
            Subscribe();
            // Its own clock, stopped in a pause (not slowed in slow motion: the dirt is on the visor): on the wall clock a blast's dirt ran down and was
            // gone behind the menu.
            if (!GameManager.IsPaused())
            {
                _clock += Time.unscaledDeltaTime;
            }
            float now = _clock;
            bool dead = Util.GameContext.PlayerActor == null || Util.GameContext.PlayerActor.dead;
            // Coming up out of the water: drops on the view, as approved (Tasks 209 I, drawn by LensEffects; optional,
            // off by default).
            Actor me = Util.GameContext.PlayerActor;
            bool under = me != null && !me.dead && me.immersedInWater;
            if (_wasUnderwater && !under && !dead && VRConfig.WaterDrops.Value) // dying under water is no surfacing
            {
                LensEffects.Instance?.WaterDrops();
            }
            _wasUnderwater = under;
            bool any = false;
            // Nothing live and nothing on screen: skip the per-frame scan of all slots (the common case between blasts).
            if (_liveCount == 0 && !_shown)
            {
                return;
            }
            for (int i = 0; i < Pool; i++)
            {
                Splat s = _splats[i];
                int v = i * 4;
                float age = now - s.Born;
                if (s.Live && (age >= s.Life || dead))
                {
                    s.Live = false;
                    _liveCount--;
                }
                if (!s.Live)
                {
                    // Folded to a point: nothing drawn.
                    _verts[v] = _verts[v + 1] = _verts[v + 2] = _verts[v + 3] = new Vector3(0f, 0f, Distance);
                    continue;
                }
                any = true;
                float u = age / s.Life;
                // Wet dirt clings a moment, then runs down the visor, stretching downward as it goes, and fades.
                float fall = s.Fall * Mathf.Max(0f, age - 0.3f) * Mathf.Max(0f, age - 0.3f);
                float alpha = u < 0.45f ? 1f : 1f - (u - 0.45f) / 0.55f;
                Color c = s.Color;
                c.a *= alpha * alpha;
                Color32 c32 = c;
                float half = s.Size * 0.5f, stretch = 1f + s.Stretch * u;
                float cos = Mathf.Cos(s.Angle), sin = Mathf.Sin(s.Angle);
                var centre = new Vector2(s.Position.x, s.Position.y - fall);
                for (int k = 0; k < 4; k++)
                {
                    float cx = (k & 1) == 0 ? -half : half, cy = (k & 2) == 0 ? -half : half;
                    // Turned, then drawn out down the visor (it used to stretch along its own turned axis).
                    _verts[v + k] = new Vector3(centre.x + cx * cos - cy * sin, centre.y + (cx * sin + cy * cos) * stretch, Distance);
                    _colors[v + k] = c32;
                }
            }
            if (any || _shown)
            {
                _mesh.vertices = _verts;
                _mesh.colors32 = _colors;
            }
            if (any != _shown)
            {
                _shown = any;
                _root.SetActive(any);
            }
        }

        /// <summary>The game clears its explosion listeners when a level loads: (re)subscribe once a match runs.</summary>
        private void Subscribe()
        {
            ActorManager manager = ActorManager.instance;
            if (manager == null)
            {
                return;
            }
            int scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().handle;
            if (ReferenceEquals(manager, _subscribedTo) && scene == _subscribedScene)
            {
                return; // already subscribed for this manager and scene: no need to read InGame (IsSpectating) every frame
            }
            if (!Util.GameContext.InGame || manager.OnExplosion == null)
            {
                return;
            }
            manager.OnExplosion.RemoveListener(OnExplosion);
            manager.OnExplosion.AddListener(OnExplosion);
            _subscribedTo = manager;
            _subscribedScene = scene;
        }

        private void OnExplosion(ExplosionInfo info)
        {
            try
            {
                Dirty(info);
            }
            catch (System.Exception e)
            {
                // The game raises this event before it deals the explosion's damage, inside a catch that skips the rest:
                // a dirt error must never cost an explosion its damage.
                Log.ExceptionOnce("MudSplash.OnExplosion", e);
            }
        }

        private void Dirty(ExplosionInfo info)
        {
            if (!VRConfig.ExplosionDirt.Value)
            {
                return;
            }
            float range = 4f;
            if (info.configuration != null)
            {
                range = Mathf.Max(3f, info.configuration.damageRange, info.configuration.balanceRange * 0.6f);
            }
            bool water = false;
            try
            {
                water = WaterLevel.instance != null && WaterLevel.IsInWater(info.point + Vector3.down * 0.2f);
            }
            catch (System.Exception)
            {
                // a map without water
            }
            Splash(info.point, range, water);
        }

        /// <summary>Throws dirt (or water) on the view from a blast at <paramref name="point"/> reaching <paramref name="range"/> metres.</summary>
        public void Splash(Vector3 point, float range, bool water)
        {
            Actor actor = Util.GameContext.PlayerActor;
            if (actor == null || actor.dead || _head == null)
            {
                return;
            }
            Vector3 eyes = _head.position;
            Vector3 to = point - eyes;
            float distance = to.magnitude;
            float strength = Mathf.Clamp01(1f - distance / (range * 1.4f));
            if (strength < 0.08f || Blocked(eyes, point, distance))
            {
                return;
            }
            float big = Mathf.Clamp(range / 6f, 0.7f, 1.5f);
            int count = Mathf.Clamp(Mathf.RoundToInt(8f + 34f * strength * big), 6, Pool);
            // Over the whole view, a third of the spots leaning toward the side the blast came from.
            Vector3 local = _head.InverseTransformDirection(to.normalized);
            var lean = new Vector2(local.x, local.y);
            lean = lean.sqrMagnitude > 1e-4f ? lean.normalized : Vector2.zero;
            var rng = new System.Random(Time.frameCount * 31 + Mathf.RoundToInt(distance * 100f));
            float R() => (float)rng.NextDouble();
            for (int i = 0; i < count; i++)
            {
                int idx = FreeIndex();
                Splat s = _splats[idx];
                bool wasLive = s.Live;
                var p = new Vector2((R() * 2f - 1f) * HalfWidth, (R() * 2f - 1f) * HalfHeight);
                if (i % 3 == 0)
                {
                    p = Vector2.Lerp(p, new Vector2(lean.x * HalfWidth, lean.y * HalfHeight), 0.5f);
                }
                s.Position = p;
                // Mostly small and middling spots, a few big blurry ones; closer blasts throw bigger and more.
                float r = R();
                float size = r < 0.15f ? Mathf.Lerp(0.06f, 0.11f, R()) : r < 0.55f ? Mathf.Lerp(0.03f, 0.06f, R()) : Mathf.Lerp(0.012f, 0.03f, R());
                s.Size = size * Mathf.Lerp(0.8f, 1.2f, strength) * (water ? 0.75f : 1f);
                s.Life = Mathf.Lerp(3f, 6f, R()) * (water ? 0.6f : 1f);
                // Big wet spots run down slowly; specks stay where they landed.
                s.Fall = size > 0.05f ? Mathf.Lerp(0.002f, 0.006f, R()) * (water ? 3f : 1f) : water ? 0.004f : 0f;
                s.Stretch = size > 0.05f ? Mathf.Lerp(0.05f, 0.3f, R()) : 0f;
                s.Born = _clock;
                float shade = Mathf.Lerp(0.8f, 1.2f, R());
                // Dirt: brown-grey and see-through (small specks denser), not a glow.
                float alpha = Mathf.Lerp(0.75f, 0.4f, Mathf.Clamp01(size / 0.11f)) * Mathf.Lerp(0.7f, 1f, strength);
                s.Color = water ? new Color(0.72f, 0.78f, 0.84f, alpha * 0.55f) : new Color(0.33f * shade, 0.27f * shade, 0.2f * shade, alpha);
                s.Variant = rng.Next(Variants);
                s.Angle = R() * Mathf.PI * 2f;
                s.Live = true;
                if (!wasLive)
                {
                    _liveCount++;
                }
                SetUvs(idx, s.Variant); // Free already knew the slot: no IndexOf scan of the pool
            }
            _mesh.uv = _uvs;
        }

        /// <summary>The spot's shape: one quarter of the texture (a small margin in, so neighbours never bleed in).</summary>
        private void SetUvs(int index, int variant)
        {
            const float margin = 0.004f;
            float x0 = (variant & 1) * 0.5f + margin, y0 = (variant >> 1) * 0.5f + margin, x1 = x0 + 0.5f - 2f * margin, y1 = y0 + 0.5f - 2f * margin;
            int v = index * 4;
            _uvs[v] = new Vector2(x0, y0);
            _uvs[v + 1] = new Vector2(x1, y0);
            _uvs[v + 2] = new Vector2(x0, y1);
            _uvs[v + 3] = new Vector2(x1, y1);
        }

        private int FreeIndex()
        {
            int oldest = 0;
            for (int i = 0; i < _splats.Length; i++)
            {
                if (!_splats[i].Live)
                {
                    return i;
                }
                if (_splats[i].Born < _splats[oldest].Born)
                {
                    oldest = i;
                }
            }
            return oldest;
        }

        /// <summary>A wall or the ground between the blast and your head keeps the dirt off (a blast just behind cover still splashes a little over it).</summary>
        private static bool Blocked(Vector3 eyes, Vector3 point, float distance)
        {
            // Level geometry only: not the soldiers, ragdolls, the first-person weapon or loose props.
            int mask = Physics.DefaultRaycastLayers & ~((1 << 2) | (1 << 5) | (1 << 8) | (1 << 10) | (1 << 16) | (1 << 17) | (1 << 18));
            int props = Weapons.PropPhysics.Layer;
            if (props >= 0)
            {
                mask &= ~(1 << props);
            }
            Vector3 target = point + Vector3.up * 0.3f;
            return Physics.Linecast(eyes, target, out RaycastHit hit, mask, QueryTriggerInteraction.Ignore) && hit.distance < distance - 0.8f;
        }

        /// <summary>The four spot shapes in the quarters of one texture.</summary>
        private static Texture2D MakeAtlas()
        {
            const int N = 128;
            var pixels = new Color32[4 * N * N];
            for (int i = 0; i < Variants; i++)
            {
                DrawSpot(pixels, 2 * N, (i & 1) * N, (i >> 1) * N, N, 1234 + i * 77);
            }
            var tex = new Texture2D(2 * N, 2 * N, TextureFormat.RGBA32, true) { name = "RavenfieldVR Mud", wrapMode = TextureWrapMode.Clamp };
            tex.SetPixels32(pixels);
            tex.Apply(true, true);
            return tex;
        }

        /// <summary>
        /// A spot of dirt on the visor, out of focus this close to the eye: a soft-edged, slightly irregular disc, denser at
        /// its rim like a dried drop, grainy inside (alpha; the colour comes from the vertex colour), drawn into an N x N
        /// block of <paramref name="pixels"/> (rows <paramref name="stride"/> wide) at (<paramref name="ox"/>, <paramref name="oy"/>).
        /// </summary>
        private static void DrawSpot(Color32[] pixels, int stride, int ox, int oy, int N, int seed)
        {
            var rng = new System.Random(seed);
            var freq = new float[4];
            var phase = new float[4];
            for (int k = 0; k < 4; k++)
            {
                freq[k] = 2 + k * 2 + rng.Next(2);
                phase[k] = (float)rng.NextDouble() * 6.283f;
            }
            float rimStrength = 0.15f + 0.25f * (float)rng.NextDouble();
            for (int y = 0; y < N; y++)
            {
                for (int x = 0; x < N; x++)
                {
                    float px = (x + 0.5f) / N * 2f - 1f, py = (y + 0.5f) / N * 2f - 1f;
                    float r = Mathf.Sqrt(px * px + py * py);
                    float theta = Mathf.Atan2(py, px);
                    float edge = 0.82f;
                    for (int k = 0; k < 4; k++)
                    {
                        edge += 0.035f / (k + 1) * Mathf.Sin(freq[k] * theta + phase[k]);
                    }
                    // Soft, defocused edge over the outer quarter; a denser rim just inside it.
                    float body = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01((edge - r) / 0.25f));
                    float rim = 1f - rimStrength + rimStrength * Mathf.SmoothStep(0f, 1f, Mathf.Clamp01((r - (edge - 0.4f)) / 0.3f));
                    float grain = 0.72f + 0.28f * Mathf.PerlinNoise(px * 7f + seed * 0.37f, py * 7f + seed * 0.11f);
                    byte v = (byte)(255 * Mathf.Lerp(0.85f, 1f, grain));
                    pixels[(oy + y) * stride + ox + x] = new Color32(v, v, v, (byte)(255 * Mathf.Clamp01(body * rim * grain)));
                }
            }
        }

        public void Destroy()
        {
            if (_subscribedTo != null && _subscribedTo.OnExplosion != null)
            {
                _subscribedTo.OnExplosion.RemoveListener(OnExplosion);
            }
            if (_root != null)
            {
                Object.Destroy(_root);
            }
            if (_mesh != null)
            {
                Object.Destroy(_mesh);
            }
            if (_material != null)
            {
                Object.Destroy(_material);
            }
            if (_atlas != null)
            {
                Object.Destroy(_atlas);
            }
            if (Instance == this)
            {
                Instance = null;
            }
        }
    }
}
