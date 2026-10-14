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

        /// <summary>The ring on the wrist that fills while the look is held (see BodyZones.WristViewHold).</summary>
        private readonly LineRenderer _ring;
        private const int RingSegments = 48;
        private const float RingRadius = 0.022f;
        private readonly Vector3[] _ringPoints = new Vector3[RingSegments + 1];
        private float _ringShown = -1f;

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

            var ringGo = new GameObject("RavenfieldVR Wrist Ring");
            Object.DontDestroyOnLoad(ringGo);
            ringGo.layer = 5;
            _ring = ringGo.AddComponent<LineRenderer>();
            _ring.useWorldSpace = false;
            _ring.loop = false;
            _ring.widthMultiplier = 0.0035f;
            _ring.numCapVertices = 3;
            _ring.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            _ring.receiveShadows = false;
            _ring.material = new Material(Shader.Find("Sprites/Default"));
            _ring.startColor = _ring.endColor = new Color(0.86f, 0.9f, 0.84f, 0.9f);
            ringGo.SetActive(false);
        }

        /// <summary>The ring at the wrist, filled to <paramref name="progress"/> (clockwise from the top).</summary>
        private void UpdateRing(bool show, float progress, Vector3 pos, Quaternion rot)
        {
            GameObject g = _ring.gameObject;
            if (!show || progress <= 0.001f)
            {
                if (g.activeSelf)
                {
                    g.SetActive(false);
                }
                _ringShown = -1f;
                return;
            }
            if (!g.activeSelf)
            {
                g.SetActive(true);
            }
            g.transform.SetPositionAndRotation(pos, rot);
            if (Mathf.Abs(progress - _ringShown) < 0.004f)
            {
                return;
            }
            _ringShown = progress;
            int n = Mathf.Max(2, Mathf.CeilToInt(RingSegments * progress) + 1);
            for (int i = 0; i < n; i++)
            {
                float a = Mathf.PI * 0.5f - Mathf.PI * 2f * progress * i / (n - 1);
                _ringPoints[i] = new Vector3(Mathf.Cos(a) * RingRadius, Mathf.Sin(a) * RingRadius, 0f);
            }
            _ring.positionCount = n;
            _ring.SetPositions(_ringPoints);
        }

        public void BeforeRender()
        {
            VRHand off = _rig.Off; // the free hand's wrist (the gesture's hand)
            Actor actor = GameContext.PlayerActor;
            bool alive = off.Tracked && actor != null && !actor.dead && !GameContext.GameMenuOpen;
            if (alive)
            {
                // The ring fills where the display will appear, facing the eyes.
                Vector3 back0 = off.Palm.rotation * (off.IsLeft ? Vector3.left : Vector3.right);
                Vector3 at = off.PalmCentre + back0 * 0.035f - off.Palm.Forward * 0.07f;
                Vector3 eye = (_rig.Head.position - at).normalized;
                UpdateRing(VRGameInput.WristLooking && !VRGameInput.WristLookGesture, VRGameInput.WristLookProgress, at - eye * 0.002f, Quaternion.LookRotation(-eye, off.Palm.Forward));
            }
            else
            {
                UpdateRing(false, 0f, Vector3.zero, Quaternion.identity);
            }
            bool want = VRConfig.WristHud.Value && alive && VRGameInput.WristLookGesture;
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
            if (_ring != null)
            {
                Object.Destroy(_ring.material);
                Object.Destroy(_ring.gameObject);
            }
            _plate.Destroy();
        }
    }
}
