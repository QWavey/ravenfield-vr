using System.Collections.Generic;
using RavenfieldVR.Core;
using RavenfieldVR.Rig;
using UnityEngine;
using UnityEngine.Rendering;

namespace RavenfieldVR.Weapons
{
    /// <summary>
    /// The hot core of the muzzle flash (flash_sim.py's core, fire and ember), in the world just in front of the muzzle,
    /// drawn by the head camera with the scene's depth: the gun and its sights hide what they cover, as they hide a real
    /// flash (Tasks 237). On the lens it was drawn over everything and showed through the gun when aiming. One billboard a
    /// flash (several at once on full auto), and one for the glow of sustained fire; the same size in view as the approved
    /// lens picture's core.
    /// </summary>
    internal sealed class MuzzleFireball
    {
        private const int Pool = 6, Size = 64;
        private static readonly int TintColorId = Shader.PropertyToID("_TintColor");

        private sealed class Ball
        {
            public GameObject Go;
            public Material Material;
            public Transform Muzzle;
            public float Ahead, Power, RadiusPx, Born = -1f;
        }

        private readonly Ball[] _balls = new Ball[Pool];
        private readonly Ball _glow;
        private readonly Dictionary<int, KeyValuePair<Texture2D, float>> _textures = new Dictionary<int, KeyValuePair<Texture2D, float>>();
        private readonly Mesh _quad;
        private readonly bool _ok;

        /// <summary>It can draw the core in the world (else the lens draws it).</summary>
        internal bool Ready => _ok;
        private int _next;

        /// <summary>
        /// Every fireball picture (16 strengths, 8 turns), worked out once on a pool thread (it is pure maths): the first shot of
        /// each new strength and turn worked out 4096 pixels on the main thread, in the frame of the shot (255.P5-2). Only
        /// the texture is made on the main thread.
        /// </summary>
        private static Color32[][] _pixels;
        private static float[] _radii;
        private static volatile bool _pixelsReady;
        private static bool _pixelsStarted;

        private static void StartPixels()
        {
            if (_pixelsStarted)
            {
                return;
            }
            _pixelsStarted = true;
            // Once here first: its tables are made by the first call, on this thread.
            LensEffects.Fireball(1f, 0f, Size, out _);
            var pixels = new Color32[17 * 8][];
            var radii = new float[17 * 8];
            System.Threading.ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    for (int pb = 1; pb <= 16; pb++)
                    {
                        for (int ph = 0; ph < 8; ph++)
                        {
                            int key = pb * 8 + ph;
                            pixels[key] = LensEffects.Fireball(pb / 10f, ph * (2f * Mathf.PI / 8f), Size, out radii[key]);
                        }
                    }
                    _pixels = pixels;
                    _radii = radii;
                    _pixelsReady = true;
                }
                catch (System.Exception)
                {
                    // left to the main thread, picture by picture, as before
                }
            });
        }

        public MuzzleFireball()
        {
            StartPixels();
            Shader shader = Shader.Find("Legacy Shaders/Particles/Additive") ?? Shader.Find("Particles/Additive")
                            ?? Shader.Find("Mobile/Particles/Additive");
            if (shader == null)
            {
                Log.WarnOnce("fireball-shader", "No additive particle shader: the muzzle flash's core stays on the lens.");
                return;
            }
            _quad = new Mesh { name = "RavenfieldVR fireball quad" };
            _quad.vertices = new[] { new Vector3(-0.5f, -0.5f, 0f), new Vector3(0.5f, -0.5f, 0f), new Vector3(-0.5f, 0.5f, 0f), new Vector3(0.5f, 0.5f, 0f) };
            _quad.uv = new[] { new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(0f, 1f), new Vector2(1f, 1f) };
            _quad.triangles = new[] { 0, 2, 1, 2, 3, 1 };
            _quad.RecalculateBounds();
            for (int i = 0; i < Pool; i++)
            {
                _balls[i] = Make(shader, "RavenfieldVR Muzzle Fireball " + i);
            }
            _glow = Make(shader, "RavenfieldVR Muzzle Glow");
            _ok = true;
        }

        private Ball Make(Shader shader, string name)
        {
            var go = new GameObject(name);
            Object.DontDestroyOnLoad(go);
            go.AddComponent<MeshFilter>().sharedMesh = _quad;
            var r = go.AddComponent<MeshRenderer>();
            var m = new Material(shader) { renderQueue = 3100, name = name };
            r.sharedMaterial = m;
            r.shadowCastingMode = ShadowCastingMode.Off;
            r.receiveShadows = false;
            r.lightProbeUsage = LightProbeUsage.Off;
            r.reflectionProbeUsage = ReflectionProbeUsage.Off;
            go.SetActive(false);
            return new Ball { Go = go, Material = m };
        }

        /// <summary>A flash of strength <paramref name="power"/> (the limiter's, the stronger eye's), <paramref name="ahead"/> metres in front of the muzzle.</summary>
        public void Add(Transform muzzle, float ahead, float power, float phase)
        {
            if (!_ok || muzzle == null || power <= 0.01f)
            {
                return;
            }
            Ball b = _balls[_next];
            _next = (_next + 1) % Pool;
            KeyValuePair<Texture2D, float> t = TextureFor(power, phase);
            b.Material.mainTexture = t.Key;
            b.RadiusPx = t.Value;
            b.Muzzle = muzzle;
            b.Ahead = ahead;
            b.Power = power;
            b.Born = Time.unscaledTime;
        }

        /// <summary>The steady glow of sustained fire at <paramref name="muzzle"/> (0: none).</summary>
        public void SetGlow(Transform muzzle, float ahead, float power)
        {
            if (!_ok)
            {
                return;
            }
            if (muzzle == null || power <= 0.01f)
            {
                _glow.Born = -1f;
                return;
            }
            KeyValuePair<Texture2D, float> t = TextureFor(power, 0f);
            if (_glow.Material.mainTexture != t.Key)
            {
                _glow.Material.mainTexture = t.Key;
            }
            _glow.RadiusPx = t.Value;
            _glow.Muzzle = muzzle;
            _glow.Ahead = ahead;
            _glow.Power = power;
            _glow.Born = Time.unscaledTime;
        }

        /// <summary>Each frame before drawing: places what shows at its muzzle, facing the eyes, fading as the lens flash does.</summary>
        public void BeforeRender(Transform head)
        {
            if (!_ok || head == null)
            {
                return;
            }
            float now = Time.unscaledTime;
            foreach (Ball b in _balls)
            {
                float age = now - b.Born;
                Place(b, head, b.Born < 0f || age > LensEffects.Life ? 0f : Mathf.Exp(-age / LensEffects.Decay));
            }
            Place(_glow, head, _glow.Born >= 0f && now - _glow.Born < 0.1f ? 1f : 0f);
        }

        private static void Place(Ball b, Transform head, float k)
        {
            bool show = k > 0.01f && b.Muzzle != null && b.Muzzle.gameObject.activeInHierarchy;
            if (b.Go.activeSelf != show)
            {
                b.Go.SetActive(show);
            }
            if (!show)
            {
                return;
            }
            Vector3 at = b.Muzzle.position + b.Muzzle.forward * b.Ahead;
            Vector3 toward = at - head.position;
            float dist = toward.magnitude;
            if (dist < 0.05f)
            {
                b.Go.SetActive(false);
                return;
            }
            // The same angular size as the approved lens picture's core: its pixels over the eye view's focal length.
            float radius = b.RadiusPx / LensEffects.Focal * dist;
            int layer = b.Muzzle.gameObject.layer; // drawn with the gun, in the head camera's pass (the gun's layer can change)
            if (b.Go.layer != layer)
            {
                b.Go.layer = layer;
            }
            b.Go.transform.SetPositionAndRotation(at, Quaternion.LookRotation(toward / dist, head.up));
            b.Go.transform.localScale = new Vector3(2f * radius, 2f * radius, 1f);
            // The legacy particle shaders double the tint: 0.5 is the picture as it is, faded by k.
            b.Material.SetColor(TintColorId, new Color(0.5f * k, 0.5f * k, 0.5f * k, 0.5f));
        }

        private KeyValuePair<Texture2D, float> TextureFor(float power, float phase)
        {
            // Strength in tenths up to the strongest flash, the starburst's turn in eighths: a few dozen pictures, each made once.
            int pb = Mathf.Clamp(Mathf.RoundToInt(power * 10f), 1, 16);
            int ph = Mathf.RoundToInt(phase / (2f * Mathf.PI) * 8f) & 7;
            int key = pb * 8 + ph;
            if (!_textures.TryGetValue(key, out KeyValuePair<Texture2D, float> t))
            {
                Color32[] pixels;
                float radiusPx;
                if (_pixelsReady)
                {
                    pixels = _pixels[key];
                    radiusPx = _radii[key];
                }
                else
                {
                    pixels = LensEffects.Fireball(pb / 10f, ph * (2f * Mathf.PI / 8f), Size, out radiusPx);
                }
                var tex = new Texture2D(Size, Size, TextureFormat.RGBA32, false)
                {
                    wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear, name = "RavenfieldVR fireball " + key
                };
                tex.SetPixels32(pixels);
                tex.Apply(false, true);
                t = new KeyValuePair<Texture2D, float>(tex, radiusPx);
                _textures[key] = t;
            }
            return t;
        }

        public void Destroy()
        {
            foreach (Ball b in _balls)
            {
                Free(b);
            }
            Free(_glow);
            foreach (KeyValuePair<Texture2D, float> t in _textures.Values)
            {
                Object.Destroy(t.Key);
            }
            _textures.Clear();
            if (_quad != null)
            {
                Object.Destroy(_quad);
            }
        }

        private static void Free(Ball b)
        {
            if (b == null)
            {
                return;
            }
            if (b.Material != null)
            {
                Object.Destroy(b.Material);
            }
            if (b.Go != null)
            {
                Object.Destroy(b.Go);
            }
        }
    }
}
