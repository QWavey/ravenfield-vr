using HarmonyLib;
using RavenfieldVR.Core;
using UnityEngine;
using UnityEngine.Rendering;

namespace RavenfieldVR.Rig
{
    /// <summary>
    /// Getting hit, the VR way. The game shows damage as a flat full-screen vignette and a rotating arrow on a screen
    /// canvas, which in the headset is a red picture hanging in front of you. In VR those are hidden; instead, as approved
    /// (Tasks2 209 F, G; drawn by <see cref="LensEffects"/>): a red glow on the side the hit came from, in both eyes and
    /// stronger in the nearer one, for half a second, with a short buzz in the hand on that side; and at low health a red
    /// edge pulsing with a heartbeat while the colour drains from the view (a grey veil). All of it gone when healed.
    /// </summary>
    public sealed class DamageFeedback
    {
        public static DamageFeedback Instance { get; private set; }

        private const float Distance = 0.26f;

        private float _lowHealth;
        private float _vignetteAt = -10f, _directionAt = -10f;
        /// <summary>The grey veil that drains the colour from the view at low health (Tasks2 209 G).</summary>
        private readonly GameObject _veil;
        private readonly Material _veilMaterial;
        private float _beatPhase;

        public DamageFeedback(Transform head)
        {
            Instance = this;
            // A grey veil over the whole view: mixed toward grey, the world loses its colour.
            _veil = GameObject.CreatePrimitive(PrimitiveType.Quad);
            _veil.name = "Low Health Veil";
            Object.Destroy(_veil.GetComponent<Collider>());
            _veil.layer = 5;
            _veil.transform.SetParent(head, false);
            _veil.transform.localPosition = new Vector3(0f, 0f, Distance + 0.01f);
            // To 60 degrees out from the middle, so both eyes are covered to the view's edge (2.4 stopped at 46-52 degrees).
            _veil.transform.localScale = new Vector3(3.6f, 3.6f, 1f) * Distance;
            _veilMaterial = new Material(Shader.Find("Sprites/Default")) { renderQueue = 4000, color = new Color(0.32f, 0.32f, 0.33f, 0f) };
            var vr = _veil.GetComponent<MeshRenderer>();
            vr.sharedMaterial = _veilMaterial;
            vr.shadowCastingMode = ShadowCastingMode.Off;
            vr.receiveShadows = false;
            _veil.SetActive(false);
        }

        /// <summary>Autotest: the share of health to show (-1: the player's own).</summary>
        internal static float TestHealthShare = -1f;

        /// <summary>
        /// Set while the game handles damage that came from no direction (a damage zone, a script): its arrow then points
        /// "right" (atan2 of nothing), and the glow and the buzz would go to the right.
        /// </summary>
        internal static bool NoDirection;

        /// <summary>The game's damage vignette (a hit).</summary>
        public void OnVignette(float intensity, float duration)
        {
            _vignetteAt = Time.unscaledTime;
        }

        /// <summary>The game's hit-direction arrow: angle in the view (0 in front, 90 to the left).</summary>
        public void OnHitDirection(float angle, bool balanceOnly)
        {
            _directionAt = Time.unscaledTime;
            if (NoDirection)
            {
                LensEffects.Instance?.Hit(180f, balanceOnly); // from no side: glows from below, no hand buzzes alone
                return;
            }
            LensEffects.Instance?.Hit(angle, balanceOnly);
            // A short buzz in the hand on the side it came from (angle 90 = from the left).
            float side = -Mathf.Sin(angle * Mathf.Deg2Rad);
            VRRig rig = VRRig.Instance;
            if (rig != null && !balanceOnly && Mathf.Abs(side) > 0.3f)
            {
                rig.Hand(side > 0f ? Input.Hand.Right : Input.Hand.Left).Haptic(0.07f, 70f, Mathf.Clamp01(0.4f + Mathf.Abs(side) * 0.5f));
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
            // A vignette the game showed without its arrow (none known for the player: Actor.Damage shows both): from below.
            if (now - _vignetteAt > 0.1f && _vignetteAt > _directionAt + 0.25f)
            {
                _vignetteAt = -10f;
                LensEffects.Instance?.Hit(180f, false);
            }
            Actor actor = Util.GameContext.PlayerActor;
            float healthShare = actor != null && !actor.dead && actor.maxHealth > 0f ? actor.health / actor.maxHealth : 1f;
            if (TestHealthShare >= 0f)
            {
                healthShare = TestHealthShare;
            }
            // Below half health, fully at a quarter; eased in and out over a second or two.
            _lowHealth = Mathf.MoveTowards(_lowHealth, Mathf.Clamp01((0.5f - healthShare) * 4f), Time.unscaledDeltaTime);
            // The heartbeat: a double beat, faster the lower the health (70 to 110 a minute).
            _beatPhase = (_beatPhase + Time.unscaledDeltaTime * Mathf.Lerp(70f, 110f, _lowHealth) / 60f) % 1f;
            float beat = Mathf.Exp(-Mathf.Pow((_beatPhase - 0.08f) / 0.05f, 2f)) + 0.6f * Mathf.Exp(-Mathf.Pow((_beatPhase - 0.3f) / 0.05f, 2f));
            bool alive = actor != null && !actor.dead;
            // screen_effects.py G at the beat's peak, a little under it between beats.
            LensEffects.Instance?.SetLowHealth(alive ? _lowHealth * (0.6f + 0.4f * Mathf.Clamp01(beat)) : 0f);
            bool veil = _lowHealth > 0.01f && alive;
            if (_veil.activeSelf != veil)
            {
                _veil.SetActive(veil);
            }
            if (veil)
            {
                _veilMaterial.color = new Color(0.32f, 0.32f, 0.33f, 0.3f * _lowHealth * (0.9f + 0.1f * beat));
            }
        }

        public void Destroy()
        {
            if (_veil != null)
            {
                Object.Destroy(_veil);
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
                try
                {
                    DamageFeedback.Instance?.OnVignette(intensity, duration);
                }
                catch (System.Exception e)
                {
                    Log.ExceptionOnce("DamageUI.ShowVignette (VR)", e);
                }
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
                try
                {
                    DamageFeedback.Instance?.OnHitDirection(angle, onlyBalanceDamage);
                }
                catch (System.Exception e)
                {
                    Log.ExceptionOnce("DamageUI.ShowDamageIndicator (VR)", e);
                }
            }
        }
    }

    [HarmonyPatch(typeof(FpsActorController), nameof(FpsActorController.ReceivedDamage))]
    internal static class PlayerDamagePatch
    {
        private static void Prefix(Vector3 direction)
        {
            DamageFeedback.NoDirection = direction.sqrMagnitude < 1e-6f;
        }

        private static void Postfix(float damage)
        {
            DamageFeedback.NoDirection = false; // the arrow (inside the game's handler) has been shown
            if (VRManager.Active)
            {
                try
                {
                    DamageFeedback.Instance?.OnDamage(damage);
                }
                catch (System.Exception e)
                {
                    // Inside the game's own damage handling: a feedback error must not cut it short.
                    Log.ExceptionOnce("FpsActorController.ReceivedDamage (VR)", e);
                }
            }
        }
    }
}
