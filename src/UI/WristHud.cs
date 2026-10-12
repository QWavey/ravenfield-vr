using RavenfieldVR.Core;
using RavenfieldVR.Input;
using RavenfieldVR.Rig;
using RavenfieldVR.Util;
using RavenfieldVR.Weapons;
using UnityEngine;

namespace RavenfieldVR.UI
{
    /// <summary>Health, reserve ammo and the weapon's state on the back of the off-hand wrist, shown while you look at it.</summary>
    public sealed class WristHud
    {
        private readonly VRRig _rig;
        private readonly GameObject _go;
        private readonly TextMesh _text;
        private float _alpha;

        public WristHud(VRRig rig)
        {
            _rig = rig;
            Font font = Resources.GetBuiltinResource<Font>("Arial.ttf");
            _go = new GameObject("RavenfieldVR Wrist HUD");
            Object.DontDestroyOnLoad(_go);
            _go.layer = 5;
            _text = _go.AddComponent<TextMesh>();
            _text.font = font;
            _text.fontSize = 64;
            _text.characterSize = 0.0011f;
            _text.anchor = TextAnchor.MiddleCenter;
            _text.alignment = TextAlignment.Center;
            _text.lineSpacing = 0.95f;
            var mr = _go.GetComponent<MeshRenderer>();
            mr.sharedMaterial = font.material;
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            _go.SetActive(false);
        }

        public void BeforeRender()
        {
            VRHand off = _rig.Off;
            Actor actor = GameContext.PlayerActor;
            bool want = VRConfig.WristHud.Value && off.Tracked && actor != null && !actor.dead && VRGameInput.WristLookGesture;
            _alpha = Mathf.MoveTowards(_alpha, want ? 1f : 0f, Time.unscaledDeltaTime * 6f);
            bool visible = _alpha > 0.01f;
            if (_go.activeSelf != visible)
            {
                _go.SetActive(visible);
            }
            if (!visible || actor == null)
            {
                return;
            }

            // Back of the wrist: behind the palm toward the forearm, on the back-of-hand side.
            Vector3 back = off.Palm.rotation * (off.IsLeft ? Vector3.left : Vector3.right);
            Vector3 pos = off.Palm.position + back * 0.035f - off.Palm.Forward * 0.07f;
            Vector3 toHead = (_rig.Head.position - pos).normalized;
            _go.transform.SetPositionAndRotation(pos, Quaternion.LookRotation(-toHead, off.Palm.Forward));

            string ammo = "";
            WeaponSystem ws = WeaponSystem.Instance;
            HeldWeapon held = ws?.Held;
            if (held != null && held.ActiveAmmo != null && held.ActiveAmmo.Participates)
            {
                AmmoState a = held.ActiveAmmo;
                string reserve = a.InfiniteSpare ? "∞" : a.SpareAmmo.ToString();
                ammo = $"\n{a.CounterText()}  ·  {reserve} reserve";
            }
            float hp = Mathf.Max(0f, actor.health);
            Color hpColor = hp > 60f ? new Color(0.85f, 0.88f, 0.84f, _alpha) : hp > 30f ? new Color(0.95f, 0.72f, 0.25f, _alpha) : new Color(0.92f, 0.32f, 0.26f, _alpha);
            _text.text = $"{hp:0} HP{ammo}";
            _text.color = hpColor;
        }

        public void Destroy()
        {
            if (_go != null)
            {
                Object.Destroy(_go);
            }
        }
    }
}
