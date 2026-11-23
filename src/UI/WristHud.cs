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
            _ring.sharedMaterial = new Material(Shader.Find("Sprites/Default")); // its own, destroyed with it (sharedMaterial: .material may copy)
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
            // Set only the n points in use, one at a time: the array holds the whole ring, so passing it whole would
            // hand the line renderer points past the ones it draws.
            for (int i = 0; i < n; i++)
            {
                _ring.SetPosition(i, _ringPoints[i]);
            }
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
            if (actor != null && actor.dead)
            {
                _alpha = 0f; // gone at once: fading out it was rebuilt, and flashed 0 HP
            }
            bool visible = _alpha > 0.01f;
            if (_go.activeSelf != visible)
            {
                _go.SetActive(visible);
            }
            if (!visible || actor == null)
            {
                _wasVisible = false; // so it is refit once when it next shows
                if (visible)
                {
                    // No player (a level loading): gone at once. Left to its fade it hung where the wrist last was, at full colour,
                    // the place and the fade being set below (bug hunt, 2026-10-22).
                    _alpha = 0f;
                    _go.SetActive(false);
                }
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
            int spare = a == null ? -2 : a.ShownSpare;
            int hpWhole = Mathf.RoundToInt(hp);
            bool becameVisible = !_wasVisible;
            _wasVisible = true;
            // The frame rate's share on time (task 336): changes every few seconds at most.
            int rate = VRConfig.WristFrameRate.Value && FrameBudget.RecentPercent >= 0 ? FrameBudget.RecentHz * 1000 + FrameBudget.RecentPercent : -1;
            // With the GPU's time beside it: over the refresh interval the preset is too heavy, under it the CPU is the limit.
            int gpu = rate < 0 ? -1 : FrameBudget.RecentGpuTenths;
            // And how far the frame rate hold has lowered the preset (HoldFrameRate): settings changed under the player are said.
            int holdSteps = GraphicsPresets.HoldLevel; // (also with the frame rate line off: the hold's own line is apart from it)
            // In a vehicle: its speed, its height over the ground in the air, and its health (the flat game's vehicle HUD, hidden in VR).
            // Rounded to 5 km/h and 5 m so the text is rebuilt only now and then.
            int vehicleInfo = VehicleInfo(actor, out string vehicleLine);
            // Rebuilt only when what it shows changes: no string garbage every frame.
            if (hpWhole != _shownHp || !ReferenceEquals(counter, _shownCounter) || spare != _shownSpare || rate != _shownRate || gpu != _shownGpu || holdSteps != _shownHeld
                || !ReferenceEquals(a, _shownAmmo) || vehicleInfo != _shownVehicle)
            {
                _shownVehicle = vehicleInfo;
                _shownHeld = holdSteps;
                _shownAmmo = a;
                _shownHp = hpWhole;
                _shownCounter = counter;
                _shownSpare = spare;
                _shownRate = rate;
                _shownGpu = gpu;
                string reserve = spare == -1 ? "\u221e" : spare.ToString();
                _text.text = (a == null ? hpWhole + " HP" : hpWhole + " HP\n" + counter + "  \u00b7  " + reserve + " reserve")
                             + (rate < 0 ? "" : "\n" + FrameBudget.RecentHz + " Hz  \u00b7  " + FrameBudget.RecentPercent + "% on time")
                             + (gpu < 0 ? "" : "  \u00b7  GPU " + (gpu / 10) + "." + (gpu % 10) + " ms")
                             // Frames late with the GPU well inside the interval: said in words (fewer bots helps, a lower preset hardly).
                             + (gpu >= 0 && FrameBudget.RecentPercent < 85 && FrameBudget.RecentHz > 0
                                && !GraphicsPresets.RenderScaleHelps(gpu / 10f, 1000f / FrameBudget.RecentHz, false) ? "  \u00b7  CPU-bound" : "")
                             + (vehicleLine != null ? "\n" + vehicleLine : "")
                             + (holdSteps > 0 ? "\nGraphics held down " + holdSteps + " step" + (holdSteps > 1 ? "s" : "") + " for the frame rate" : "");
                _refitFrames = 2; // this frame and the next: the text's size is known only once it has been laid out
            }
            if (becameVisible)
            {
                _refitFrames = Mathf.Max(_refitFrames, 2);
            }
            if (_refitFrames > 0)
            {
                _refitFrames--;
                _plate.Fit();
            }
            if (_alpha != _lastPlateAlpha)
            {
                _lastPlateAlpha = _alpha;
                _plate.SetAlpha(_alpha);
            }
            Color col = hp > 60f ? new Color(0.85f, 0.88f, 0.84f, _alpha) : hp > 30f ? new Color(0.95f, 0.72f, 0.25f, _alpha) : new Color(1f, 0.42f, 0.36f, _alpha);
            if (col.r != _lastColor.r || col.g != _lastColor.g || col.b != _lastColor.b || col.a != _lastColor.a)
            {
                _lastColor = col;
                _text.color = col; // baked into the text mesh, so set only when it actually changes
            }
        }

        private int _shownHp = int.MinValue;
        private int _shownVehicle = int.MinValue;
        private string _vehicleLine;

        /// <summary>The vehicle line's numbers packed into one int (-1: not in a vehicle) and its text (made again only when they change).</summary>
        private int _height;
        private float _heightAt;

        private int VehicleInfo(Actor actor, out string line)
        {
            line = null;
            Vehicle v = actor.IsSeated() && actor.seat != null ? actor.seat.vehicle : null;
            if (v == null)
            {
                return -1;
            }
            int speed = Mathf.RoundToInt(v.Velocity().magnitude * 3.6f / 5f) * 5;
            bool air = Util.GameContext.SeatKind == Util.VehicleKind.Airplane || Util.GameContext.SeatKind == Util.VehicleKind.Helicopter;
            // (The ground below cast five times a second, not every frame: shown in 5 m steps; scout.)
            if (!air)
            {
                _height = 0;
            }
            else if (Time.unscaledTime >= _heightAt)
            {
                _heightAt = Time.unscaledTime + 0.2f;
                _height = Physics.Raycast(v.transform.position, Vector3.down, out RaycastHit ground, 3000f, 1, QueryTriggerInteraction.Ignore)
                    ? Mathf.RoundToInt(ground.distance / 5f) * 5 : 0;
            }
            int height = _height;
            int health = v.maxHealth > 0f ? Mathf.Clamp(Mathf.RoundToInt(v.health / v.maxHealth * 100f), 0, 100) : 100;
            int packed = Mathf.Min(speed, 9995) / 5 * 1000000 + Mathf.Min(height, 4995) / 5 * 1000 + health;
            if (packed != _shownVehicle || _vehicleLine == null)
            {
                _vehicleLine = speed + " km/h" + (air ? "  \u00b7  " + height + " m up" : "") + "  \u00b7  vehicle " + health + "%";
            }
            line = _vehicleLine;
            return packed;
        }
        private string _shownCounter;
        private int _shownSpare = int.MinValue;
        private AmmoState _shownAmmo;
        private int _shownRate = int.MinValue;
        private int _shownGpu = int.MinValue;
        private int _shownHeld;
        private bool _wasVisible;
        private int _refitFrames;
        private float _lastPlateAlpha = -1f;
        private Color _lastColor = new Color(-1f, 0f, 0f, 0f);

        public void Destroy()
        {
            if (_go != null)
            {
                Object.Destroy(_go);
            }
            if (_ring != null)
            {
                Object.Destroy(_ring.sharedMaterial); // (read through .material, a copy could be made here and the original kept)
                Object.Destroy(_ring.gameObject);
            }
            _plate.Destroy();
        }
    }
}
