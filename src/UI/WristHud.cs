using RavenfieldVR.Core;
using RavenfieldVR.Input;
using RavenfieldVR.Rig;
using RavenfieldVR.Util;
using RavenfieldVR.Weapons;
using UnityEngine;

namespace RavenfieldVR.UI
{
    /// <summary>
    /// Health, reserve ammo and the weapon's state on the back of the off-hand wrist, shown while you look at it, on a dark
    /// rounded plate so it reads against a bright sky as well as in a dark room.
    /// </summary>
    public sealed class WristHud
    {
        private readonly VRRig _rig;
        private readonly GameObject _go;
        private readonly TextMesh _text;
        private readonly TextPlate _plate;
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
            _plate = new TextPlate(_text, font.material, 0.8f, 0.012f);
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
            Vector3 pos = off.PalmCentre + back * 0.035f - off.Palm.Forward * 0.07f;
            Vector3 toHead = (_rig.Head.position - pos).normalized;
            _go.transform.SetPositionAndRotation(pos, Quaternion.LookRotation(-toHead, off.Palm.Forward));

            WeaponSystem ws = WeaponSystem.Instance;
            HeldWeapon held = ws?.Held;
            AmmoState a = held != null && held.ActiveAmmo != null && held.ActiveAmmo.Participates ? held.ActiveAmmo : null;
            float hp = Mathf.Max(0f, actor.health);
            string counter = a != null ? a.CounterText() : null;
            int spare = a == null ? -2 : a.InfiniteSpare ? -1 : a.SpareAmmo;
            int hpWhole = Mathf.RoundToInt(hp);
            // Rebuilt only when what it shows changes: no string garbage every frame.
            if (hpWhole != _shownHp || !ReferenceEquals(counter, _shownCounter) || spare != _shownSpare)
            {
                _shownHp = hpWhole;
                _shownCounter = counter;
                _shownSpare = spare;
                string reserve = spare == -1 ? "\u221e" : spare.ToString();
                _text.text = a == null ? hpWhole + " HP" : hpWhole + " HP\n" + counter + "  \u00b7  " + reserve + " reserve";
            }
            _plate.Fit(); // every frame it shows: the text's size is known only once it has been laid out
            _plate.SetAlpha(_alpha);
            _text.color = hp > 60f ? new Color(0.85f, 0.88f, 0.84f, _alpha) : hp > 30f ? new Color(0.95f, 0.72f, 0.25f, _alpha) : new Color(1f, 0.42f, 0.36f, _alpha);
        }

        private int _shownHp = int.MinValue;
        private string _shownCounter;
        private int _shownSpare = int.MinValue;

        public void Destroy()
        {
            if (_go != null)
            {
                Object.Destroy(_go);
            }
            _plate.Destroy();
        }
    }
}
