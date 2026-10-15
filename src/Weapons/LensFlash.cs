using System.Collections.Generic;
using RavenfieldVR.Core;
using RavenfieldVR.Rig;
using UnityEngine;
using UnityEngine.Rendering;

namespace RavenfieldVR.Weapons
{
    /// <summary>
    /// The muzzle flash as the eye sees it (Tasks2 209 A-C): a see-through flash on the lens, only on a shot where the
    /// weapon's own muzzle flash plays (Tasks2 212; none for a weapon without one, a faint glint for a suppressed one).
    /// At the muzzle, a little ahead of it: a white-hot core fading to orange with a ragged edge (the gas plume seen end
    /// on) and a short, faint starburst. On the lens (head-locked): soft ghosts on the line from the flash through the
    /// middle of the view, a thin halo ring, and a light warm wash from the gun's side. All of it added to the picture
    /// (never covering it), never above 94% white, one frame bright and gone within 60 ms, each eye by its own strength:
    /// flash size x how much the muzzle is in front of that eye x nearness, the eye further off sideways getting less.
    /// A flash limiter (on by default) keeps it under three visible flashes a second (WCAG 2.3.1): a budget of six
    /// brightness steps in any 1.1 s, a flash costing two (a very bright one three); past it, a soft flash and a steady
    /// glow while the firing goes on, instead of a strobe.
    /// </summary>
    public sealed class LensFlash
    {
        public static LensFlash Instance { get; private set; }

        private const float Life = 0.06f, HalfLife = 0.016f, MaxStrength = 1.6f, OverlayDistance = 0.25f;
        private const int Ghosts = 4;

        private readonly VRRig _rig;
        private readonly GameObject _world, _overlay;
        private readonly Transform _core, _rays, _halo, _wash;
        private readonly Transform[] _ghosts = new Transform[Ghosts];
        private readonly List<FlashPart> _parts = new List<FlashPart>();
        private static Texture2D _coreTex, _raysTex, _discTex, _ringTex, _washTex;
        private static Mesh _quad;

        // The flash now.
        private float _bornAt = -10f, _power, _rayAngle, _jitterSize = 1f;
        private Vector3 _muzzle, _muzzleForward, _gunSide;
        private Vector2 _jitter;
        // A shot this frame, decided before drawing (the game plays its particles during the frame).
        private Weapon _pendingWeapon;
        private HeldWeapon _pendingHeld;
        private int _pendingFrame = -1;
        // The limiter.
        private readonly List<KeyValuePair<float, int>> _steps = new List<KeyValuePair<float, int>>();
        private float _glow, _lastShotAt = -10f;

        /// <summary>Self-test: flashes shown, and the last one's strength.</summary>
        internal int Shown { get; private set; }
        internal float LastPower => _power;

        public LensFlash(VRRig rig)
        {
            Instance = this;
            _rig = rig;
            EnsureAssets();
            Shader additive = FindAdditive();
            _world = new GameObject("RavenfieldVR Lens Flash (muzzle)");
            Object.DontDestroyOnLoad(_world);
            _core = Part(_world.transform, "Core", _coreTex, additive, new Color(1f, 0.92f, 0.82f), 0, 4000);
            _rays = Part(_world.transform, "Rays", _raysTex, additive, new Color(1f, 0.85f, 0.7f) * 0.35f, 0, 4001);
            _overlay = new GameObject("RavenfieldVR Lens Flash (lens)") { layer = 5 };
            _overlay.transform.SetParent(rig.Camera.transform, false);
            _halo = Part(_overlay.transform, "Halo", _ringTex, additive, new Color(1f, 0.8f, 0.6f) * 0.12f, 5, 4002);
            for (int i = 0; i < Ghosts; i++)
            {
                _ghosts[i] = Part(_overlay.transform, "Ghost" + i, _discTex, additive, new Color(1f, 0.75f, 0.5f) * (0.09f - i * 0.015f), 5, 4002);
            }
            _wash = Part(_overlay.transform, "Wash", _washTex, additive, new Color(1f, 0.55f, 0.25f) * 0.07f, 5, 4001);
            _world.SetActive(false);
            _overlay.SetActive(false);
        }

        private Transform Part(Transform parent, string name, Texture2D tex, Shader shader, Color color, int layer, int queue)
        {
            var go = new GameObject(name) { layer = layer };
            go.transform.SetParent(parent, false);
            go.AddComponent<MeshFilter>().sharedMesh = _quad;
            var r = go.AddComponent<MeshRenderer>();
            r.sharedMaterial = new Material(shader) { mainTexture = tex, renderQueue = queue, name = "RavenfieldVR " + name };
            r.shadowCastingMode = ShadowCastingMode.Off;
            r.receiveShadows = false;
            r.lightProbeUsage = LightProbeUsage.Off;
            r.reflectionProbeUsage = ReflectionProbeUsage.Off;
            var part = go.AddComponent<FlashPart>();
            part.Owner = this;
            part.Renderer = r;
            part.Base = color;
            part.Property = r.sharedMaterial.HasProperty("_TintColor") ? "_TintColor" : "_Color";
            part.Halve = part.Property == "_TintColor"; // the legacy particle shaders double the tint
            _parts.Add(part);
            return go.transform;
        }

        private static Shader FindAdditive()
        {
            foreach (string n in new[] { "Legacy Shaders/Particles/Additive", "Particles/Additive", "Mobile/Particles/Additive" })
            {
                Shader s = Shader.Find(n);
                if (s != null)
                {
                    return s;
                }
            }
            Log.WarnOnce("lensflash-shader", "No additive particle shader found: the lens flash is drawn blended.");
            return Shader.Find("Sprites/Default");
        }

        /// <summary>The player's weapon fired (Weapon.Shoot): the flash is decided before the frame is drawn.</summary>
        public void OnShot(HeldWeapon held, Weapon weapon)
        {
            _pendingHeld = held;
            _pendingWeapon = weapon;
            _pendingFrame = Time.frameCount;
        }

        public void BeforeRender()
        {
            float now = Time.unscaledTime;
            if (_pendingWeapon != null && Time.frameCount - _pendingFrame <= 1)
            {
                TryFlash(_pendingHeld, _pendingWeapon, now);
                _pendingWeapon = null;
                _pendingHeld = null;
            }
            // A steady glow while a long burst goes on past the limiter: no strobe, and it fades when the firing stops.
            _glow = Mathf.MoveTowards(_glow, now - _lastShotAt < 0.15f && Budget(now) <= 0 ? 0.3f : 0f, Time.unscaledDeltaTime * 3f);
            float age = now - _bornAt;
            bool on = VRConfig.LensFlash.Value && (age < Life || _glow > 0.01f) && Util.GameContext.PlayerActor != null && !Util.GameContext.PlayerActor.dead;
            if (_world.activeSelf != on)
            {
                _world.SetActive(on);
                _overlay.SetActive(on);
            }
            if (!on)
            {
                return;
            }
            Place();
        }

        // ---- deciding a flash ---------------------------------------------------------------------------

        private void TryFlash(HeldWeapon held, Weapon w, float now)
        {
            if (!VRConfig.LensFlash.Value || held == null || w == null || w.configuration == null)
            {
                return;
            }
            Transform muzzle = w.CurrentMuzzle();
            if (muzzle == null)
            {
                return;
            }
            MuzzleFlashInfo info = MuzzleFlashInfo.Of(w);
            // Did the game's own flash play on this shot? Its particles are out by now (a burst with a chance may skip).
            bool particlesOut = false;
            if (info.Particles)
            {
                foreach (Transform m in w.configuration.muzzles)
                {
                    ParticleSystem ps = m != null ? m.GetComponent<ParticleSystem>() ?? m.GetComponentInChildren<ParticleSystem>() : null;
                    if (ps != null && ps.particleCount > 0)
                    {
                        particlesOut = true;
                        break;
                    }
                }
            }
            float size;
            WeaponRigData d = held.Rig.Data;
            if (particlesOut)
            {
                float byClass = !info.Light ? 0.15f // suppressed: a faint glint
                    : d.IsPistol ? 1f : d.IsMachineGun ? 2f : w.configuration.projectilesPerShot > 1 ? 2.6f : 1.7f;
                float real = info.Size > 0.01f ? Mathf.Clamp(info.Size / 0.15f, 0.6f, 1.5f) : 1f;
                size = byClass * real;
            }
            else if (info.Light && !info.Particles)
            {
                size = 0.4f; // only the game's flash light: a small flash
            }
            else
            {
                return; // no flash on this shot (none on this weapon, or its particles skipped this one)
            }
            _lastShotAt = now;
            float power = size * Mathf.Max(0.05f, VRConfig.LensFlashStrength.Value);
            if (VRConfig.ReduceFlashing.Value)
            {
                int left = Budget(now);
                int cost = power > 1.2f ? 3 : 2;
                if (left >= cost)
                {
                    _steps.Add(new KeyValuePair<float, int>(now, cost));
                }
                else if (left >= 2)
                {
                    power = Mathf.Min(power, 1.2f); // the strongest that fades in one step
                    _steps.Add(new KeyValuePair<float, int>(now, 2));
                }
                else
                {
                    power = Mathf.Lerp(0.34f, 0.22f, _glow / 0.3f); // under the threshold; less over a glow already lit
                }
            }
            _power = Mathf.Min(power, MaxStrength);
            _bornAt = now;
            _muzzle = muzzle.position;
            _muzzleForward = muzzle.forward;
            Transform head = _rig.Camera.transform;
            _gunSide = Vector3.ProjectOnPlane(_muzzle - head.position, head.forward);
            // Never the same twice: size, place and starburst angle.
            _jitterSize = Random.Range(0.85f, 1.15f);
            _jitter = Random.insideUnitCircle * 0.8f;
            _rayAngle = Random.Range(0f, 360f);
            Shown++;
        }

        /// <summary>Brightness steps left in the limiter's 1.1 s window.</summary>
        private int Budget(float now)
        {
            int used = 0;
            for (int i = _steps.Count - 1; i >= 0; i--)
            {
                if (now - _steps[i].Key > 1.1f)
                {
                    _steps.RemoveAt(i);
                }
                else
                {
                    used += _steps[i].Value;
                }
            }
            return 6 - used;
        }

        // ---- drawing --------------------------------------------------------------------------------------

        private static float WorldSize(float degrees, float distance) => 2f * distance * Mathf.Tan(degrees * 0.5f * Mathf.Deg2Rad);

        private void Place()
        {
            Transform head = _rig.Camera.transform;
            float p = Mathf.Max(_power, _glow);
            // At the muzzle, a little ahead of it (the plume): a billboard toward the eyes.
            Vector3 at = _muzzle + _muzzleForward * (0.04f + 0.02f * p);
            at += head.rotation * new Vector3(_jitter.x, _jitter.y, 0f) * (Mathf.Deg2Rad * Vector3.Distance(head.position, at));
            float dist = Mathf.Max(0.1f, Vector3.Distance(head.position, at));
            Quaternion face = Quaternion.LookRotation(at - head.position, head.up);
            float coreDeg = (5.5f + 9f * p) * _jitterSize; // the ember glow's edge; core and body are inside the texture
            _core.SetPositionAndRotation(at, face);
            _core.localScale = Vector3.one * WorldSize(coreDeg, dist);
            _rays.SetPositionAndRotation(at, face * Quaternion.Euler(0f, 0f, _rayAngle));
            _rays.localScale = Vector3.one * WorldSize(coreDeg * 1.4f, dist);

            // On the lens: where the flash is seen from the head, on a plane a hand's width ahead.
            Vector3 local = head.InverseTransformPoint(at);
            Vector2 onLens = local.z > 0.05f ? new Vector2(local.x, local.y) / local.z * OverlayDistance : new Vector2(0f, -OverlayDistance);
            float halo = WorldSize(19f + 10f * p, OverlayDistance);
            _halo.localPosition = new Vector3(onLens.x, onLens.y, OverlayDistance);
            _halo.localRotation = Quaternion.identity;
            _halo.localScale = Vector3.one * halo;
            float[] along = { -0.45f, -0.9f, -1.35f, 0.5f };
            float[] sizes = { 3.5f, 6f, 2.5f, 4.5f };
            for (int i = 0; i < Ghosts; i++)
            {
                Vector2 g = onLens * along[i] * -1f; // through the middle of the view and out the other side
                _ghosts[i].localPosition = new Vector3(g.x, g.y, OverlayDistance);
                _ghosts[i].localRotation = Quaternion.identity;
                _ghosts[i].localScale = Vector3.one * WorldSize(sizes[i] * (0.7f + 0.3f * p), OverlayDistance);
            }
            Vector3 sideLocal = head.InverseTransformDirection(_gunSide);
            var washAt = new Vector2(sideLocal.x, sideLocal.y);
            washAt = washAt.sqrMagnitude > 1e-6f ? washAt.normalized * (OverlayDistance * 0.35f) : new Vector2(0f, -OverlayDistance * 0.35f);
            _wash.localPosition = new Vector3(washAt.x, washAt.y, OverlayDistance);
            _wash.localRotation = Quaternion.identity;
            _wash.localScale = Vector3.one * WorldSize(70f, OverlayDistance);
        }

        /// <summary>This eye's strength now (0..1.6): power x in front of it x nearness x the eye's side; fading.</summary>
        internal float EyeStrength(Camera cam)
        {
            float age = Time.unscaledTime - _bornAt;
            float flash = age < Life ? _power * Mathf.Pow(0.5f, age / HalfLife) : 0f;
            float level = Mathf.Max(flash, _glow);
            if (level <= 0.001f)
            {
                return 0f;
            }
            Transform head = _rig.Camera.transform;
            Vector3 eye = head.position;
            float ipd = cam != null && cam.stereoSeparation > 0.04f && cam.stereoSeparation < 0.09f ? cam.stereoSeparation : 0.064f;
            float sideSign = 0f;
            if (cam != null && cam.stereoActiveEye == Camera.MonoOrStereoscopicEye.Left)
            {
                sideSign = -1f;
            }
            else if (cam != null && cam.stereoActiveEye == Camera.MonoOrStereoscopicEye.Right)
            {
                sideSign = 1f;
            }
            eye += head.right * (ipd * 0.5f * sideSign);
            Vector3 toMuzzle = _muzzle - eye;
            float dist = toMuzzle.magnitude;
            float facing = 0.5f + 0.5f * Vector3.Dot(head.forward, toMuzzle / Mathf.Max(dist, 1e-3f));
            float near = Mathf.Clamp(0.75f / Mathf.Max(dist, 0.05f), 0.5f, 2.5f);
            // The eye on the far side from the muzzle (sideways) gets down to half.
            float lateral = Vector3.Dot(_muzzle - head.position, head.right);
            float side = sideSign == 0f || Mathf.Sign(lateral) == sideSign ? 1f : 1f - 0.5f * Mathf.Clamp01(Mathf.Abs(lateral) / 0.15f);
            return Mathf.Min(MaxStrength, level * facing * near * side);
        }

        public void Destroy()
        {
            if (_world != null)
            {
                Object.Destroy(_world);
            }
            if (_overlay != null)
            {
                Object.Destroy(_overlay);
            }
        }

        // ---- textures (made once) ---------------------------------------------------------------------------

        private static void EnsureAssets()
        {
            if (_quad != null)
            {
                return;
            }
            _quad = new Mesh { name = "RavenfieldVR flash quad" };
            _quad.vertices = new[] { new Vector3(-0.5f, -0.5f, 0f), new Vector3(0.5f, -0.5f, 0f), new Vector3(-0.5f, 0.5f, 0f), new Vector3(0.5f, 0.5f, 0f) };
            _quad.uv = new[] { new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(0f, 1f), new Vector2(1f, 1f) };
            _quad.triangles = new[] { 0, 2, 1, 2, 3, 1 };
            _quad.RecalculateBounds();
            _quad.bounds = new Bounds(Vector3.zero, Vector3.one * 2f);
            var rng = new System.Random(4127);
            float[] edge = new float[64];
            for (int i = 0; i < edge.Length; i++)
            {
                edge[i] = 0.75f + 0.5f * (float)rng.NextDouble();
            }
            _coreTex = Make(128, (x, y, r, a) =>
            {
                // Ragged plume edge: a wobble round the circle (smoothed), white-hot middle, orange body, dim ember glow.
                float t = (a / (2f * Mathf.PI) + 1f) % 1f * edge.Length;
                int i0 = (int)t % edge.Length, i1 = (i0 + 1) % edge.Length;
                float wob = Mathf.Lerp(edge[i0], edge[i1], t - Mathf.Floor(t));
                float body = Mathf.Clamp01(1f - r / (0.42f * wob));
                float core = Mathf.Clamp01(1f - r / 0.1f);
                float ember = Mathf.Clamp01(1f - r) * 0.35f;
                body = body * body;
                Color c = new Color(1f, 0.55f, 0.15f) * body + new Color(1f, 0.98f, 0.92f) * core * core + new Color(0.9f, 0.35f, 0.08f) * ember * ember;
                return new Color(Mathf.Min(c.r, 0.94f), Mathf.Min(c.g, 0.94f), Mathf.Min(c.b, 0.94f), 1f);
            });
            _raysTex = Make(128, (x, y, r, a) =>
            {
                float v = 0f;
                for (int k = 0; k < 6; k++)
                {
                    float da = Mathf.DeltaAngle(a * Mathf.Rad2Deg, k * 60f) * Mathf.Deg2Rad;
                    v += Mathf.Exp(-da * da / 0.0025f);
                }
                v *= Mathf.Clamp01(1f - r) * Mathf.Clamp01(1f - r) * Mathf.Clamp01(r * 6f);
                return new Color(v, v * 0.9f, v * 0.75f, 1f);
            });
            _discTex = Make(64, (x, y, r, a) =>
            {
                float v = Mathf.Clamp01(1f - r);
                v = v * v * (3f - 2f * v);
                return new Color(v, v, v, 1f);
            });
            _ringTex = Make(128, (x, y, r, a) =>
            {
                float v = Mathf.Exp(-(r - 0.85f) * (r - 0.85f) / 0.0012f) * Mathf.Clamp01((1f - r) * 20f);
                return new Color(v, v, v, 1f);
            });
            _washTex = Make(64, (x, y, r, a) =>
            {
                float v = Mathf.Clamp01(1f - r);
                v *= v;
                return new Color(v, v, v, 1f);
            });
        }

        private static Texture2D Make(int n, System.Func<float, float, float, float, Color> f)
        {
            var tex = new Texture2D(n, n, TextureFormat.RGBA32, true) { wrapMode = TextureWrapMode.Clamp, name = "RavenfieldVR flash" };
            var px = new Color[n * n];
            for (int j = 0; j < n; j++)
            {
                for (int i = 0; i < n; i++)
                {
                    float x = (i + 0.5f) / n * 2f - 1f, y = (j + 0.5f) / n * 2f - 1f;
                    float r = Mathf.Sqrt(x * x + y * y);
                    px[j * n + i] = r >= 1f ? Color.black : f(x, y, r, Mathf.Atan2(y, x));
                }
            }
            tex.SetPixels(px);
            tex.Apply(true, true);
            return tex;
        }
    }

    /// <summary>One part of the lens flash: tinted for the eye it is drawn into (each eye has its own strength).</summary>
    internal sealed class FlashPart : MonoBehaviour
    {
        public LensFlash Owner;
        public MeshRenderer Renderer;
        public Color Base;
        public string Property;
        public bool Halve;
        private MaterialPropertyBlock _block;

        private void OnWillRenderObject()
        {
            if (Owner == null || Renderer == null)
            {
                return;
            }
            _block = _block ?? new MaterialPropertyBlock();
            float k = Owner.EyeStrength(Camera.current) * (Halve ? 0.5f : 1f);
            Color c = Base * k;
            c.a = 1f;
            _block.SetColor(Property, c);
            Renderer.SetPropertyBlock(_block);
        }
    }
}
