using RavenfieldVR.Core;
using RavenfieldVR.UI;
using UnityEngine;

namespace RavenfieldVR.Weapons
{
    /// <summary>
    /// Small round counter floating at the rear-left of the receiver, facing the shooter's eye:
    /// "28+1", "RACK", "OPEN", "NO MAG", plus short flashed messages ("NO AMMO", "FULL").
    /// </summary>
    public sealed class AmmoCounter
    {
        private static Font _font;
        private readonly WeaponRig _rig;
        private readonly GameObject _go;
        private readonly TextMesh _text;
        private readonly TextPlate _plate;
        private string _flash;
        private float _flashUntil;

        private static readonly Color Normal = new Color(0.86f, 0.88f, 0.84f, 0.95f);
        private static readonly Color Warn = new Color(0.95f, 0.72f, 0.25f, 0.95f);
        private static readonly Color Empty = new Color(1f, 0.42f, 0.36f, 0.95f); // brightened to read on its plate

        public AmmoCounter(WeaponRig rig)
        {
            _rig = rig;
            if (_font == null)
            {
                _font = Resources.GetBuiltinResource<Font>("Arial.ttf");
            }
            _go = new GameObject("RavenfieldVR Ammo Counter");
            _go.layer = 18;
            WeaponRigData d = rig.Data;
            Vector3 right = Vector3.Cross(d.Up, d.AimAxis).normalized;
            rig.AttachInGunSpace(_go.transform, d.GripPoint + d.Up * 0.045f - right * 0.035f + d.AimAxis * 0.02f, Quaternion.LookRotation(d.AimAxis, d.Up));
            _text = _go.AddComponent<TextMesh>();
            _text.font = _font;
            _text.fontSize = 64;
            _text.characterSize = 0.0009f;
            _text.anchor = TextAnchor.LowerRight;
            _text.alignment = TextAlignment.Right;
            _text.color = Normal;
            var mr = _go.GetComponent<MeshRenderer>();
            mr.sharedMaterial = _font.material;
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            mr.receiveShadows = false;
            // A small dark backing like a digital readout: it reads against the gun, the sky and the ground alike.
            _plate = new TextPlate(_text, _font.material, 0.8f, 0.004f);
        }

        public void Flash(string message)
        {
            _flash = message;
            _flashUntil = Time.unscaledTime + 1.2f;
        }

        public void BeforeRender(AmmoState ammo)
        {
            // A message (NO AMMO, FULL, OPEN COVER) shows even with the gun counter off: it was the only way to tell them
            // apart, and with the option off none ever showed (85-agent review).
            bool flashing = _flash != null && Time.unscaledTime < _flashUntil;
            bool show = flashing || VRConfig.GunAmmoCounter.Value && ammo != null && ammo.Participates;
            if (_go.activeSelf != show)
            {
                _go.SetActive(show);
            }
            if (!show)
            {
                return;
            }
            if (_flash != null && Time.unscaledTime < _flashUntil)
            {
                Show(_flash);
                _text.color = Empty;
                _plate.Fit();
                return;
            }
            _flash = null;
            if (ammo == null || !ammo.Participates)
            {
                // A message shown on a gun without ammo of its own has ended: nothing to count (this read a missing
                // ammo state and stopped the rest of the frame's weapon update).
                _go.SetActive(false);
                return;
            }
            Show(ammo.CounterText());
            _text.color = ammo.Loaded == 0 ? Empty : (ammo.SlideLocked || !ammo.CanFire) ? Warn : Normal;
            _plate.Fit();
        }

        // TextMesh.text's getter builds a new string on every read, so the last value set is remembered instead.
        private string _shown;

        private void Show(string text)
        {
            if (!ReferenceEquals(text, _shown))
            {
                _text.text = text;
                _shown = text;
            }
        }

        public void Destroy()
        {
            _plate.Destroy();
            if (_go != null)
            {
                Object.Destroy(_go);
            }
        }
    }
}
