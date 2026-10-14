using System.Collections.Generic;
using RavenfieldVR.Core;
using UnityEngine;
using UnityEngine.Rendering;

namespace RavenfieldVR.Rig
{
    /// <summary>
    /// A grenade or shell going off close by throws dirt on your view (Call of Duty style): dark brown splats and droplets
    /// on an invisible visor a hand's width in front of the eyes, more and bigger the closer and larger the blast, on the
    /// side it came from, that slide down and fade over two to four seconds. Water splashes (grey and see-through) for a
    /// blast in water. Nothing for a blast behind a wall, and nothing while dead. Listens to the game's explosion event
    /// (ActorManager.OnExplosion), so every explosion that deals damage counts.
    /// </summary>
    public sealed class MudSplash
    {
        public static MudSplash Instance { get; private set; }

        private const float Distance = 0.25f;
        private const int Pool = 24;
        private const int Variants = 4;

        private sealed class Splat
        {
            public GameObject Go;
            public Material Material;
            public float Born, Life, Size, Fall, Stretch;
            public Vector2 Position;
            public Color Color;
            public bool Live;
        }

        private readonly Transform _head;
        private readonly GameObject _root;
        private readonly Splat[] _splats = new Splat[Pool];
        private readonly Texture2D[] _textures = new Texture2D[Variants];
        private ActorManager _subscribedTo;
        private int _subscribedScene = -1;

        public MudSplash(Transform head)
        {
            Instance = this;
            _head = head;
            _root = new GameObject("Mud Splash") { layer = 5 };
            _root.transform.SetParent(head, false);
            for (int i = 0; i < Variants; i++)
            {
                _textures[i] = MakeTexture(1234 + i * 77);
            }
            Shader shader = Shader.Find("Sprites/Default");
            for (int i = 0; i < Pool; i++)
            {
                GameObject quad = GameObject.CreatePrimitive(PrimitiveType.Quad);
                quad.name = "Splat";
                Object.Destroy(quad.GetComponent<Collider>());
                quad.layer = 5;
                quad.transform.SetParent(_root.transform, false);
                var mat = new Material(shader) { renderQueue = 4000, mainTexture = _textures[i % Variants] };
                MeshRenderer r = quad.GetComponent<MeshRenderer>();
                r.sharedMaterial = mat;
                r.shadowCastingMode = ShadowCastingMode.Off;
                r.receiveShadows = false;
                quad.SetActive(false);
                _splats[i] = new Splat { Go = quad, Material = mat };
            }
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

        public void Update()
        {
            Subscribe();
            float now = Time.unscaledTime;
            bool dead = Util.GameContext.PlayerActor == null || Util.GameContext.PlayerActor.dead;
            foreach (Splat s in _splats)
            {
                if (!s.Live)
                {
                    continue;
                }
                float age = now - s.Born;
                if (age >= s.Life || dead)
                {
                    s.Live = false;
                    s.Go.SetActive(false);
                    continue;
                }
                float u = age / s.Life;
                // Wet dirt clings a moment, then runs down the visor, stretching as it goes, and fades.
                float fall = s.Fall * Mathf.Max(0f, age - 0.3f) * Mathf.Max(0f, age - 0.3f);
                float alpha = u < 0.45f ? 1f : 1f - (u - 0.45f) / 0.55f;
                Color c = s.Color;
                c.a *= alpha * alpha;
                s.Material.color = c;
                Transform t = s.Go.transform;
                t.localPosition = new Vector3(s.Position.x, s.Position.y - fall, Distance);
                t.localScale = new Vector3(s.Size, s.Size * (1f + s.Stretch * u), 1f);
            }
        }

        /// <summary>The game clears its explosion listeners when a level loads: (re)subscribe once a match runs.</summary>
        private void Subscribe()
        {
            ActorManager manager = ActorManager.instance;
            int scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().handle;
            if (manager == null || !Util.GameContext.InGame || ReferenceEquals(manager, _subscribedTo) && scene == _subscribedScene)
            {
                return;
            }
            if (manager.OnExplosion == null)
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
            int count = Mathf.Clamp(Mathf.RoundToInt((water ? 3f : 2f) + (water ? 12f : 10f) * strength * big), 1, Pool);
            // Toward the side the blast came from (behind you: the edges of the view).
            Vector3 local = _head.InverseTransformDirection(to.normalized);
            var bias = new Vector2(local.x, local.y);
            bias = bias.sqrMagnitude > 1e-4f ? bias.normalized * Mathf.Lerp(0.04f, 0.16f, Mathf.Clamp01(1f - local.z)) : Vector2.zero;
            var rng = new System.Random(Time.frameCount * 31 + Mathf.RoundToInt(distance * 100f));
            for (int i = 0; i < count; i++)
            {
                Splat s = Free();
                float r = (float)rng.NextDouble();
                float angle = (float)rng.NextDouble() * Mathf.PI * 2f;
                float spread = 0.2f * Mathf.Sqrt((float)rng.NextDouble());
                s.Position = bias + new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * spread;
                // A few big splats and many droplets; closer and bigger blasts throw bigger ones.
                float size = r < 0.3f ? Mathf.Lerp(0.07f, 0.16f, strength) : Mathf.Lerp(0.02f, 0.06f, (float)rng.NextDouble());
                s.Size = size * big * (water ? 0.8f : 1f);
                s.Life = Mathf.Lerp(2f, 4f, Mathf.Clamp01(size / 0.16f)) * (water ? 0.6f : 1f);
                s.Fall = Mathf.Lerp(0.004f, 0.014f, (float)rng.NextDouble()) * (water ? 2.5f : 1f);
                s.Stretch = Mathf.Lerp(0.1f, 0.5f, (float)rng.NextDouble());
                s.Born = Time.unscaledTime;
                float shade = Mathf.Lerp(0.75f, 1.15f, (float)rng.NextDouble());
                s.Color = water ? new Color(0.78f, 0.84f, 0.9f, 0.4f) : new Color(0.27f * shade, 0.19f * shade, 0.11f * shade, 0.97f);
                s.Material.mainTexture = _textures[rng.Next(Variants)];
                s.Go.transform.localRotation = Quaternion.Euler(0f, 0f, (float)rng.NextDouble() * 360f);
                s.Live = true;
                s.Go.SetActive(true);
            }
        }

        private Splat Free()
        {
            Splat oldest = _splats[0];
            foreach (Splat s in _splats)
            {
                if (!s.Live)
                {
                    return s;
                }
                if (s.Born < oldest.Born)
                {
                    oldest = s;
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

        /// <summary>A splat: an irregular wet blob with a few droplets and streaks thrown off it (alpha), drawn once per variant.</summary>
        private static Texture2D MakeTexture(int seed)
        {
            const int N = 128;
            var rng = new System.Random(seed);
            var pixels = new Color32[N * N];
            var freq = new float[5];
            var phase = new float[5];
            for (int k = 0; k < 5; k++)
            {
                freq[k] = 2 + k * 2 + rng.Next(2);
                phase[k] = (float)rng.NextDouble() * 6.283f;
            }
            var drops = new List<Vector3>(); // x, y, radius (texture units -1..1)
            int dropCount = 6 + rng.Next(8);
            for (int i = 0; i < dropCount; i++)
            {
                float a = (float)rng.NextDouble() * 6.283f;
                float d = 0.45f + 0.45f * (float)rng.NextDouble();
                drops.Add(new Vector3(Mathf.Cos(a) * d, Mathf.Sin(a) * d, 0.03f + 0.08f * (float)rng.NextDouble()));
            }
            for (int y = 0; y < N; y++)
            {
                for (int x = 0; x < N; x++)
                {
                    float px = (x + 0.5f) / N * 2f - 1f, py = (y + 0.5f) / N * 2f - 1f;
                    float r = Mathf.Sqrt(px * px + py * py);
                    float theta = Mathf.Atan2(py, px);
                    float edge = 0.38f;
                    for (int k = 0; k < 5; k++)
                    {
                        edge += 0.06f / (k + 1) * Mathf.Sin(freq[k] * theta + phase[k]);
                    }
                    float alpha = Mathf.Clamp01((edge - r) / 0.03f);
                    foreach (Vector3 d in drops)
                    {
                        float dx = px - d.x, dy = py - d.y;
                        alpha = Mathf.Max(alpha, Mathf.Clamp01((d.z - Mathf.Sqrt(dx * dx + dy * dy)) / 0.02f));
                    }
                    // Wet and uneven: a little lighter and thinner in places.
                    float grain = 0.85f + 0.15f * Mathf.PerlinNoise(px * 6f + seed, py * 6f);
                    byte v = (byte)(255 * grain);
                    pixels[y * N + x] = new Color32(v, v, v, (byte)(255 * alpha * Mathf.Lerp(0.8f, 1f, grain)));
                }
            }
            var tex = new Texture2D(N, N, TextureFormat.RGBA32, true) { name = "RavenfieldVR Mud", wrapMode = TextureWrapMode.Clamp };
            tex.SetPixels32(pixels);
            tex.Apply(true, true);
            return tex;
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
            foreach (Splat s in _splats)
            {
                if (s?.Material != null)
                {
                    Object.Destroy(s.Material);
                }
            }
            foreach (Texture2D t in _textures)
            {
                if (t != null)
                {
                    Object.Destroy(t);
                }
            }
            if (Instance == this)
            {
                Instance = null;
            }
        }
    }
}
