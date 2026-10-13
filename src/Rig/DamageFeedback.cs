using HarmonyLib;
using RavenfieldVR.Core;
using UnityEngine;
using UnityEngine.Rendering;

namespace RavenfieldVR.Rig
{
    /// <summary>
    /// Getting hit, the VR way. The game shows damage as a flat full-screen vignette and a rotating arrow on a screen
    /// canvas, which in the headset is a red picture hanging in front of you. In VR those are hidden and instead a ring
    /// around the edge of your view (head-locked, a little in front of the eyes) flashes red, brightest on the side the
    /// shot came from (top = in front, bottom = behind, as the game's arrow points), and both controllers buzz with the
    /// hit's strength. Below half health a faint red edge stays.
    /// </summary>
    public sealed class DamageFeedback
    {
        public static DamageFeedback Instance { get; private set; }

        private const float Distance = 0.26f;
        private const int Segments = 48;
        // View-cone angles (from the centre of view) of the ring's inner (clear), middle (full) and outer edge.
        private static readonly float[] RingAngles = { 34f, 52f, 80f };
        private static readonly float[] RingAlpha = { 0f, 1f, 1f };

        private readonly GameObject _go;
        private readonly Mesh _mesh;
        private readonly MeshRenderer _renderer;
        private readonly Color32[] _colors;
        private readonly float[] _vertexAngle;
        private readonly float[] _vertexRing;

        private float _flash;        // overall red, decays
        private float _flashPeak;
        private float _flashFrom;
        private float _flashStart = -10f;
        private float _flashDuration = 1.2f;
        private float _hitAngle;     // degrees, 0 = in front (top of the view), 90 = left
        private float _direction;    // strength of the directional cue, decays
        private bool _balanceOnly;
        private float _lowHealth;

        public DamageFeedback(Transform head)
        {
            Instance = this;
            _go = new GameObject("Damage Feedback") { layer = 5 };
            _go.transform.SetParent(head, false);
            _go.transform.localPosition = Vector3.zero;

            int rings = RingAngles.Length;
            var verts = new Vector3[rings * Segments];
            _colors = new Color32[verts.Length];
            _vertexAngle = new float[verts.Length];
            _vertexRing = new float[verts.Length];
            for (int r = 0; r < rings; r++)
            {
                float radius = Distance * Mathf.Tan(RingAngles[r] * Mathf.Deg2Rad);
                for (int i = 0; i < Segments; i++)
                {
                    // Angle around the view, counter-clockwise from the top (the game's indicator convention).
                    float a = i * 360f / Segments;
                    float rad = a * Mathf.Deg2Rad;
                    int v = r * Segments + i;
                    verts[v] = new Vector3(-Mathf.Sin(rad) * radius, Mathf.Cos(rad) * radius, Distance);
                    _vertexAngle[v] = a;
                    _vertexRing[v] = RingAlpha[r];
                }
            }
            var tris = new int[(rings - 1) * Segments * 6];
            int t = 0;
            for (int r = 0; r < rings - 1; r++)
            {
                for (int i = 0; i < Segments; i++)
                {
                    int a = r * Segments + i, b = r * Segments + (i + 1) % Segments;
                    int c = a + Segments, d = b + Segments;
                    tris[t++] = a; tris[t++] = c; tris[t++] = b;
                    tris[t++] = b; tris[t++] = c; tris[t++] = d;
                }
            }
            _mesh = new Mesh { name = "RavenfieldVR Damage Ring", vertices = verts, triangles = tris, colors32 = _colors };
            _mesh.bounds = new Bounds(Vector3.forward * Distance, Vector3.one * 10f);
            _mesh.MarkDynamic();
            _go.AddComponent<MeshFilter>().sharedMesh = _mesh;
            var mat = new Material(Shader.Find("Sprites/Default")) { renderQueue = 4001, color = Color.white };
            _renderer = _go.AddComponent<MeshRenderer>();
            _renderer.sharedMaterial = mat;
            _renderer.shadowCastingMode = ShadowCastingMode.Off;
            _renderer.receiveShadows = false;
            _renderer.enabled = false;
        }

        /// <summary>The game's damage vignette: how hard (0..1, more the lower your health) and for how long.</summary>
        public void OnVignette(float intensity, float duration)
        {
            _flashPeak = Mathf.Max(_flash, Mathf.Clamp01(intensity));
            _flashFrom = _flash;
            _flashStart = Time.unscaledTime;
            _flashDuration = Mathf.Clamp(duration * 0.25f, 0.6f, 1.8f);
        }

        /// <summary>The game's hit-direction arrow: angle in the view (0 in front, 90 to the left).</summary>
        public void OnHitDirection(float angle, bool balanceOnly)
        {
            _hitAngle = angle;
            _direction = 1f;
            _balanceOnly = balanceOnly;
            if (!balanceOnly && _flash < 0.35f)
            {
                OnVignette(0.35f, 2f); // a hit the game did not vignette (in a vehicle) still flashes a little
            }
        }

        public void OnDamage(float damage)
        {
            if (damage <= 0.5f)
            {
                return;
            }
            float amp = Mathf.Clamp(0.25f + damage / 60f, 0.25f, 1f);
            VRRig rig = VRRig.Instance;
            if (rig != null)
            {
                foreach (VRHand h in rig.Hands)
                {
                    h.Haptic(0.06f + amp * 0.08f, 55f, amp);
                }
            }
        }

        public void Update()
        {
            float now = Time.unscaledTime;
            float age = now - _flashStart;
            // Snaps in within a few hundredths of a second, fades over the duration.
            _flash = age < 0.05f ? Mathf.Lerp(_flashFrom, _flashPeak, age / 0.05f) : _flashPeak * Mathf.Clamp01(1f - (age - 0.05f) / _flashDuration);
            _direction = Mathf.MoveTowards(_direction, 0f, Time.unscaledDeltaTime / 1.5f);

            Actor actor = Util.GameContext.PlayerActor;
            float healthShare = actor != null && !actor.dead && actor.maxHealth > 0f ? actor.health / actor.maxHealth : 1f;
            _lowHealth = Mathf.MoveTowards(_lowHealth, Mathf.Clamp01((0.5f - healthShare) * 2f) * 0.45f, Time.unscaledDeltaTime);

            bool visible = (_flash > 0.01f || _direction > 0.01f || _lowHealth > 0.01f) && actor != null && !actor.dead;
            if (_renderer.enabled != visible)
            {
                _renderer.enabled = visible;
            }
            if (!visible)
            {
                return;
            }
            Color tint = _balanceOnly && _flash < 0.05f ? new Color(1f, 0.85f, 0.2f) : new Color(0.75f, 0.02f, 0.02f);
            for (int v = 0; v < _colors.Length; v++)
            {
                float toward = Mathf.Max(0f, Mathf.Cos((_vertexAngle[v] - _hitAngle) * Mathf.Deg2Rad));
                float a = _vertexRing[v] * Mathf.Clamp01(_flash * 0.75f + _lowHealth + _direction * toward * toward * 0.85f);
                _colors[v] = new Color(tint.r, tint.g, tint.b, a);
            }
            _mesh.colors32 = _colors;
        }

        public void Destroy()
        {
            if (_go != null)
            {
                Object.Destroy(_go);
            }
            if (Instance == this)
            {
                Instance = null;
            }
        }
    }

    /// <summary>In VR the flat damage vignette and arrow are hidden; their triggers drive <see cref="DamageFeedback"/>.</summary>
    [HarmonyPatch(typeof(DamageUI), "LateUpdate")]
    internal static class DamageUiHidePatch
    {
        private static void Postfix(DamageUI __instance)
        {
            if (!VRManager.Active || DamageFeedback.Instance == null)
            {
                return;
            }
            if (__instance.damageVignette != null)
            {
                __instance.damageVignette.enabled = false;
            }
            if (__instance.damageIndicator != null)
            {
                __instance.damageIndicator.enabled = false;
            }
        }
    }

    [HarmonyPatch(typeof(DamageUI), nameof(DamageUI.ShowVignette))]
    internal static class DamageUiVignettePatch
    {
        private static void Postfix(float intensity, float duration)
        {
            if (VRManager.Active)
            {
                DamageFeedback.Instance?.OnVignette(intensity, duration);
            }
        }
    }

    [HarmonyPatch(typeof(DamageUI), nameof(DamageUI.ShowDamageIndicator))]
    internal static class DamageUiIndicatorPatch
    {
        private static void Postfix(float angle, bool onlyBalanceDamage)
        {
            if (VRManager.Active)
            {
                DamageFeedback.Instance?.OnHitDirection(angle, onlyBalanceDamage);
            }
        }
    }

    [HarmonyPatch(typeof(FpsActorController), nameof(FpsActorController.ReceivedDamage))]
    internal static class PlayerDamagePatch
    {
        private static void Postfix(float damage)
        {
            if (VRManager.Active)
            {
                DamageFeedback.Instance?.OnDamage(damage);
            }
        }
    }
}
