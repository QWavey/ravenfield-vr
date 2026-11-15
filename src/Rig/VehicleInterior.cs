using System.Collections.Generic;
using RavenfieldVR.Core;
using RavenfieldVR.Input;
using RavenfieldVR.Runtime;
using RavenfieldVR.Util;
using UnityEngine;

namespace RavenfieldVR.Rig
{
    /// <summary>
    /// An inside for closed ground vehicles (the user, 2026-11-15, from the vanilla APC's gunner seat: "you see its outside too and not
    /// just the thing, maybe render or create some insides for the APC, aswell as for tanks etc depending on their texture dynamically so
    /// players actually feel like in a tank"; "a button for shooting and a wheel to turn the turret and a back forward or left or right
    /// button for moving ... because he will feel like a actual gunner").
    /// <list type="bullet">
    /// <item>A cabin round the head (walls, roof, floor), coloured from the vehicle's own largest material; the view out is a slot in
    /// the front wall (the sight's or the hull's), a lamp over it.</item>
    /// <item>A gunner's seat (a gun to aim): a traverse handwheel on the left and an elevation handwheel on the right, cranked by a
    /// grip on the rim (a full turn of the crank is 90 degrees of turret, 30 of gun), and a red fire button, pressed by the hand.</item>
    /// <item>A driver's seat: four buttons for forward, back, left and right on a panel low in front. The sticks keep working.</item>
    /// </list>
    /// The gun of such a seat is never aimed by the head (looking round the inside moves nothing): the right thumbstick (the gun hand's)
    /// turns it, up and down and sideways, and the handwheels crank it (the user, 2026-11-15: "the turret shouldnt move with the sight of
    /// the player but like the ... joystick knob on the right controller ... but the player can also use the buttons inside"). While the
    /// vehicle burns or is wrecked, the inside flashes red like an alarm light (no sound: "just a red signal that is seen").
    /// Only in enclosed seats of ground vehicles and boats (helicopters and planes keep their own glass cockpits). Setting
    /// <c>Vehicles/Interiors</c>.
    /// </summary>
    public sealed class VehicleInterior
    {
        internal static VehicleInterior Instance;

        private readonly VRRig _rig;
        private Seat _seat;
        private GameObject _root;
        private Material _wall, _metal, _red, _lamp, _button, _paint;
        private Transform _traverse, _elevate, _fire, _steer;
        private float _steerTurned;

        /// <summary>
        /// A wheeled closed vehicle's driver (an APC, the mods' armoured cars) steers by a wheel in the middle too (the user: "make also
        /// sure all steering wheels are turneable"); a tank steers by its tracks and keeps the buttons.
        /// </summary>
        private static bool SteersByWheel(Seat seat) =>
            seat != null && seat.IsDriverSeat() && (seat.vehicle is Car || seat.vehicle is ArcadeCar) && !(seat.vehicle is Tank);

        /// <summary>For the vehicle check: the traverse handwheel (null: none).</summary>
        internal Transform TraverseWheel => _traverse;

        /// <summary>For the vehicle check: the fire button and the forward drive button (null: none).</summary>
        internal Transform FireButton => _fire;

        internal Transform ElevationWheel => _elevate;

        internal Transform CabinSteeringWheel => _steer;

        internal Transform ForwardButton => _driveButtons.Count > 0 ? _driveButtons[0].t : null;

        /// <summary>For the vehicle check: the alarm shown without setting the vehicle on fire (that burned it, the player with it).</summary>
        internal static bool TestAlarm;
        private readonly List<(Transform t, Vector2 drive)> _driveButtons = new List<(Transform, Vector2)>();
        private readonly int[] _onWheel = { -1, -1 }; // per hand: 0 traverse, 1 elevation, -1 none
        private readonly float[] _wheelAngle = new float[2];
        private readonly bool[] _pressed = new bool[8];

        /// <summary>The aim the handwheels set (world), or null: no hand on them.</summary>
        internal static Vector3? WheelAim;
        private Vector3 _aimLocal; // the wanted aim in the vehicle's frame while a wheel is held
        private bool _aimSet;
        /// <summary>The fire button is held.</summary>
        internal static bool Fire;

        public VehicleInterior(VRRig rig)
        {
            _rig = rig;
            Instance = this;
        }

        internal bool Shown => _root != null;

        private Seat _lastSeat;
        private float _nextLook;
        private bool _wanted;

        /// <summary>A shot of the inside's gun: a buzz in each hand on the fire button or a handwheel, harder for a big gun.</summary>
        internal static void Kick(Weapon w)
        {
            VehicleInterior me = Instance;
            if (me == null || me._seat == null || me._seat.activeWeapon != w)
            {
                return;
            }
            bool heavy = w.configuration != null && w.configuration.ammo <= 1; // (a cannon: one round a load)
            for (int h = 0; h < 2; h++)
            {
                VRHand hand = me._rig.Hands[h];
                if (hand == null || !hand.Tracked)
                {
                    continue;
                }
                bool onControls = me._onWheel[h] >= 0 || me._fire != null && Vector3.Distance(hand.PalmCentre, me._fire.position) < ButtonReach + 0.03f;
                if (onControls)
                {
                    hand.Haptic(heavy ? 0.12f : 0.03f, heavy ? 40f : 80f, heavy ? 1f : 0.6f);
                }
            }
        }

        /// <summary>The seat's gun is aimed from the inside (stick and handwheels), not by the head; snap turning is off there.</summary>
        internal static bool AimsGun => Instance != null && Instance._aimSeat != null;

        /// <summary>This turret is the aim seat's own gun (another player's turret on the same vehicle is not steered from here).</summary>
        internal static bool AimsThisGun(Weapon turret) => AimsGun && Gun(Instance._aimSeat) == turret;

        /// <summary>
        /// The seat whose gun is aimed by stick and handwheels: any enclosed seat with a turret not taken by hand, its cabin drawn or not.
        /// Head aim there chased itself: the view sits on the turret's own camera, turning the turret turned the head's direction, and the
        /// gun spun round (378 degrees in half a second in the vehicle check; the user's "spins 360 degrees").
        /// </summary>
        private Seat _aimSeat;

        private static bool AimSeat(Seat seat) => seat != null && seat.enclosed && Gun(seat) != null && !TurretGrip.Grabbable(seat, out _);

        private Light _alarm;
        private readonly Transform[] _needles = new Transform[3];

        /// <summary>The gauges read the vehicle: speed (0-25 m/s), engine (its throttle, else its speed), hull (its health).</summary>
        private void Gauges()
        {
            Vehicle v = _seat != null ? _seat.vehicle : null;
            if (v == null)
            {
                return;
            }
            float speed = Mathf.Clamp01(v.Velocity().magnitude / 25f);
            float engine = v.engine != null ? Mathf.Clamp01(Mathf.Max(Mathf.Abs(v.engine.targetThrottle), speed)) : speed;
            float hull = Mathf.Clamp01(v.GetHealthRatio());
            for (int g = 0; g < 3; g++)
            {
                float reading = g == 0 ? speed : g == 1 ? engine : hull;
                if (_needles[g] != null)
                {
                    // From 120 degrees left (nothing) to 120 right (full), eased so it swings, not jumps.
                    Quaternion want = Quaternion.Euler(0f, 0f, 120f - 240f * reading);
                    Transform n = _needles[g].childCount > 0 ? _needles[g].GetChild(0) : null;
                    if (n != null)
                    {
                        n.localRotation = Quaternion.Slerp(n.localRotation, want, Mathf.Clamp01(Time.unscaledDeltaTime * 6f));
                        n.localPosition = n.localRotation * new Vector3(0f, 0.012f, 0f) + new Vector3(0f, 0f, -0.002f);
                    }
                }
            }
        }
        private Color _wallEmit, _lampColor;

        internal static bool Wanted(Seat seat)
        {
            if (seat == null || !seat.enclosed || !VRConfig.Interiors.Value)
            {
                return false;
            }
            Vehicle v = seat.vehicle;
            // (Not a seat whose gun is taken by hand: that gunner stands out of a hatch, a tank's roof MG.)
            return !(v is Helicopter || v is Airplane || v is Plane) && !TurretGrip.Grabbable(seat, out _) && HullInside.EyesInside(v, seat);
        }

        private static Weapon Gun(Seat seat)
        {
            MountedWeapon w = seat.activeWeapon;
            return w is MountedTurret || w is MountedStabilizedTurret || w is TankTurret ? w : null;
        }

        public void Update()
        {
            Actor actor = GameContext.PlayerActor;
            Seat raw = actor != null && !actor.dead && actor.IsSeated() ? actor.seat : null;
            // (Once a seat has its inside it keeps it until the seat changes: looked at again, the eyes crossing a part's box edge as the
            // turret turned tore it down and built it again, twice a second.)
            if (!ReferenceEquals(raw, _lastSeat))
            {
                // (Judged 0.3 s after the seat is taken: in its first frame the eyes are still where they were, and the APC driver's view,
                // 2 m over his seat, was judged from inside the hull and kept so.)
                _lastSeat = raw;
                _wanted = false;
                _nextLook = Time.unscaledTime + 0.3f;
            }
            else if (!_wanted && Time.unscaledTime >= _nextLook)
            {
                _nextLook = Time.unscaledTime + 0.5f;
                _wanted = raw != null && Wanted(raw); // (not each frame: it walks the vehicle's parts)
            }
            Seat aim = raw != null && AimSeat(raw) ? raw : null;
            if (!ReferenceEquals(aim, _aimSeat))
            {
                _aimSeat = aim;
                _aimSet = aim != null;
                if (aim != null)
                {
                    _aimLocal = aim.vehicle.transform.InverseTransformDirection(TurretGrip.AimReference(Gun(aim)).forward);
                }
            }
            Seat seat = _wanted ? raw : null;
            // (By reference: a destroyed seat equals null to Unity, and a cabin whose vehicle was blown up stayed on: its fire button
            // fired the gun on foot; scout, 2026-11-15.)
            if (!ReferenceEquals(seat, _seat) || _seat == null && _root != null)
            {
                Clear();
                _seat = seat;
                if (_seat != null)
                {
                    Build();
                }
            }
            if (GameContext.GameMenuOpen || GameManager.IsPaused())
            {
                ReleaseHands();
                if (_scope != null)
                {
                    _scope.enabled = false; // (not drawn behind a menu)
                }
                return;
            }
            if (_scope != null && !_scope.enabled)
            {
                _scope.enabled = true;
            }
            if (_root != null)
            {
                Hands();
                Gauges();
            }
            Aim();
            PointScope();
        }

        /// <summary>
        /// The periscope looks where the gun points (the APC's view sits on the seat, and its periscope stayed with the hull while the
        /// turret turned: the user, 2026-11-15, "the view doesnt move but the turret does"), the world's up kept up (its picture came out
        /// upside down there). With no gun, along the cabin's forward.
        /// </summary>
        private void PointScope()
        {
            if (_scope == null || _root == null)
            {
                return;
            }
            Weapon gun = _seat != null ? Gun(_seat) : null;
            Vector3 dir = gun != null ? TurretGrip.AimReference(gun).forward : _root.transform.forward;
            if (dir.sqrMagnitude < 1e-6f)
            {
                return;
            }
            _scope.transform.rotation = Quaternion.LookRotation(dir.normalized, Vector3.up);
        }

        // ---- building ------------------------------------------------------------------------------------------------------

        private void Build()
        {
            Transform head = _rig.Head;
            _root = new GameObject("RavenfieldVR Vehicle Interior");
            _root.transform.SetParent(_rig.Root, false);
            // At the head's neutral place and heading in the rig (the seat's or the sight's way), whatever way he looked as he sat.
            RigidPose neutral = _rig.NeutralHead;
            _root.transform.localPosition = neutral.position;
            _root.transform.localRotation = Quaternion.Euler(0f, neutral.rotation.eulerAngles.y, 0f);
            Materials(_seat.vehicle);
            // No drawn box: the vehicle's own hull is solid from inside now (HullInside) and is the cabin (the user found the drawn one
            // "very bad"; a custom cockpit comes later). In front of the eyes a periscope viewer: a frame and a screen showing the world.
            const float roof = 0.35f;
            Box("viewer-top", new Vector3(0f, 0.115f, 0.42f), new Vector3(0.64f, 0.05f, 0.1f), _metal);
            Box("viewer-bottom", new Vector3(0f, -0.115f, 0.42f), new Vector3(0.64f, 0.05f, 0.1f), _metal);
            Box("viewer-left", new Vector3(-0.305f, 0f, 0.42f), new Vector3(0.03f, 0.28f, 0.1f), _metal);
            Box("viewer-right", new Vector3(0.305f, 0f, 0.42f), new Vector3(0.03f, 0.28f, 0.1f), _metal);
            Box("viewer-back", new Vector3(0f, 0f, 0.475f), new Vector3(0.64f, 0.28f, 0.01f), _metal);
            Box("viewer-brow", new Vector3(0f, 0.15f, 0.36f), new Vector3(0.5f, 0.03f, 0.08f), _paint);
            Periscope(new Vector3(0f, 0f, 0.468f), new Vector2(0.56f, 0.2f));
            // The line from the eyes to the viewer kept clear of the vehicle's small parts (a breech, a sight box).
            int cleared = _rig.Hull != null ? _rig.Hull.ClearCorridor(_root.transform, new Vector3(0f, -0.1f, 0.2f), new Vector3(0.34f, 0.7f, 0.32f)) : 0;
            // And on along the view out of the hull: a gunner's eyes sit at his gun's sight, inside the barrel's and the turret's own
            // shapes, and solid from inside he saw the inside of the barrel (the user, 2026-11-15: "the view is viewing the insides and
            // not the outsides of the barrel"). The channel runs to past the hull's front, 50 cm wide and 40 cm high.
            float outOfHull = _scope != null ? Mathf.Max(_scope.nearClipPlane, 1f) : 3f;
            if (_rig.Hull != null)
            {
                cleared += _rig.Hull.ClearCorridor(_root.transform, new Vector3(0f, 0f, outOfHull / 2f), new Vector3(0.25f, 0.2f, outOfHull / 2f + 0.3f), first: false);
            }
            // Military fittings: a radio set on the left (two knobs, a dial window, a handset), three gauges over the viewer.
            Box("radio", new Vector3(-0.46f, -0.12f, 0.12f), new Vector3(0.12f, 0.22f, 0.3f), _paint);
            Cylinder("radio-knob-1", new Vector3(-0.395f, -0.06f, 0.18f), new Vector3(0.035f, 0.01f, 0.035f), Quaternion.Euler(0f, 0f, 90f), _metal);
            Cylinder("radio-knob-2", new Vector3(-0.395f, -0.06f, 0.06f), new Vector3(0.035f, 0.01f, 0.035f), Quaternion.Euler(0f, 0f, 90f), _metal);
            Box("radio-dial", new Vector3(-0.398f, -0.16f, 0.12f), new Vector3(0.005f, 0.05f, 0.16f), _lamp);
            Box("radio-handset", new Vector3(-0.42f, 0.03f, 0.2f), new Vector3(0.04f, 0.035f, 0.17f), _metal);
            for (int g = 0; g < 3; g++)
            {
                float x = -0.12f + g * 0.12f;
                Cylinder("gauge-" + g, new Vector3(x, 0.2f, 0.4f), new Vector3(0.075f, 0.012f, 0.075f), Quaternion.Euler(-80f, 0f, 0f), _metal);
                Cylinder("gauge-face-" + g, new Vector3(x, 0.2f, 0.393f), new Vector3(0.062f, 0.002f, 0.062f), Quaternion.Euler(-80f, 0f, 0f), _button);
                // (The needle on a pivot at the dial's middle: turned by its gauge's reading in Gauges.)
                var pivot = new GameObject("gauge-pivot-" + g).transform;
                pivot.SetParent(_root.transform, false);
                pivot.localPosition = new Vector3(x, 0.2f, 0.39f);
                pivot.localRotation = Quaternion.Euler(-80f + 90f, 0f, 0f);
                Transform needle = Box("gauge-needle-" + g, Vector3.zero, new Vector3(0.004f, 0.026f, 0.002f), _red);
                needle.SetParent(pivot, false);
                needle.localPosition = new Vector3(0f, 0.012f, -0.002f);
                needle.localRotation = Quaternion.identity;
                _needles[g] = pivot;
            }
            // A dim warm light inside: the hull's inner walls face away from the sun and were black.
            var lamp = new GameObject("cabin light");
            lamp.transform.SetParent(_root.transform, false);
            lamp.transform.localPosition = new Vector3(0f, 0.25f, 0f);
            Light cabin = lamp.AddComponent<Light>();
            cabin.type = LightType.Point;
            cabin.color = new Color(1f, 0.86f, 0.62f);
            cabin.range = 3f;
            cabin.intensity = 1.1f;
            cabin.shadows = LightShadows.None;
            var alarm = new GameObject("alarm");
            alarm.transform.SetParent(_root.transform, false);
            alarm.transform.localPosition = new Vector3(0f, roof - 0.1f, 0f);
            _alarm = alarm.AddComponent<Light>();
            _alarm.type = LightType.Point;
            _alarm.color = new Color(1f, 0.08f, 0.04f);
            _alarm.range = 2.5f;
            _alarm.intensity = 0f;
            _alarm.enabled = false;
            if (Gun(_seat) != null)
            {
                _aimLocal = _seat.vehicle.transform.InverseTransformDirection(TurretGrip.AimReference(Gun(_seat)).forward);
                _aimSet = true;
                // At the hands' height in front of the chest, where a gunner's hands rest: well in sight looking down.
                // (Further out when the driver's steering wheel sits between them.)
                float apart = SteersByWheel(_seat) ? 0.36f : 0.26f;
                _traverse = Wheel("traverse", new Vector3(-apart, -0.36f, 0.3f));
                _elevate = Wheel("elevation", new Vector3(apart, -0.36f, 0.3f));
                Box("fire-mount", new Vector3(0.26f, -0.2f, 0.4f), new Vector3(0.1f, 0.06f, 0.08f), _paint);
                _fire = Cylinder("fire", new Vector3(0.26f, -0.16f, 0.39f), new Vector3(0.06f, 0.015f, 0.06f), Quaternion.Euler(-60f, 0f, 0f), _red);
            }
            if (SteersByWheel(_seat))
            {
                // (High enough that the drive panel's forward button is out of its rim's grip: a fist on it took the wheel.)
                _steer = Wheel("steering", new Vector3(0f, -0.3f, 0.33f));
            }
            if (_seat.IsDriverSeat())
            {
                // Low in the middle, in reach of either hand: forward above, back below, left and right either side.
                Box("panel", new Vector3(0f, -0.62f, 0.36f), new Vector3(0.36f, 0.3f, 0.04f), _paint).localRotation = Quaternion.Euler(-35f, 0f, 0f);
                Vector3 c = new Vector3(0f, -0.62f, 0.33f);
                _driveButtons.Add((Button("forward", c + new Vector3(0f, 0.08f, -0.04f)), new Vector2(0f, 1f)));
                _driveButtons.Add((Button("back", c + new Vector3(0f, -0.08f, 0.04f)), new Vector2(0f, -1f)));
                _driveButtons.Add((Button("left", c + new Vector3(-0.1f, 0f, 0f)), new Vector2(-1f, 0f)));
                _driveButtons.Add((Button("right", c + new Vector3(0.1f, 0f, 0f)), new Vector2(1f, 0f)));
            }
            Log.InfoOnce("vehicle-interior:" + _seat.vehicle.name, $"Vehicle inside for {_seat.vehicle.name} ({(Gun(_seat) != null ? "handwheels and fire button" : "no gun")}{(_seat.IsDriverSeat() ? ", drive buttons" : "")}; {cleared} small part(s) cleared from before the eyes).");
        }

        private Camera _scope;
        private RenderTexture _scopeTexture;
        private Material _scopeScreen;

        /// <summary>
        /// The slot is a periscope's screen: a camera at the eyes looking the cabin's way renders the world past the hull (its near
        /// plane beyond the hull's front), shown on the slot. The hull is solid from inside now (HullInside), so the slot no longer looks
        /// at the world itself.
        /// </summary>
        private void Periscope(Vector3 at, Vector2 size)
        {
            _scopeTexture = new RenderTexture(512, Mathf.RoundToInt(512 * size.y / size.x), 16) { name = "RavenfieldVR periscope" };
            var go = new GameObject("periscope camera");
            go.transform.SetParent(_root.transform, false);
            _scope = go.AddComponent<Camera>();
            Camera eye = _rig.Camera;
            _scope.CopyFrom(eye);
            _scope.stereoTargetEye = StereoTargetEyeMask.None;
            _scope.targetTexture = _scopeTexture;
            _scope.fieldOfView = 40f;
            _scope.depth = eye.depth - 1;
            _rig.Suppressor?.Ignore(_scope);
            // Past the hull in front: the vehicle's bounds along the cabin's forward from the eyes, plus a little.
            Bounds b = new Bounds(_seat.vehicle.transform.position, Vector3.zero);
            foreach (Renderer r in _seat.vehicle.GetComponentsInChildren<Renderer>())
            {
                if (r != null && r.GetComponentInParent<Actor>() == null)
                {
                    b.Encapsulate(r.bounds);
                }
            }
            Vector3 eyeAt = _root.transform.position, fwd = _root.transform.forward;
            float near = 0.3f;
            for (float d = 0.3f; d < 12f; d += 0.1f)
            {
                if (!b.Contains(eyeAt + fwd * d))
                {
                    near = d + 0.15f;
                    break;
                }
            }
            _scope.nearClipPlane = Mathf.Max(near, 1.2f); // (eyes outside the hull's box, a driver's on top: it looked at the hull at 0.3 m)
            go.transform.localPosition = Vector3.zero;
            go.transform.localRotation = Quaternion.identity;
            Transform screen = Part(PrimitiveType.Cube, "periscope screen", at, new Vector3(size.x, size.y, 0.002f), Quaternion.identity, null); // (a thin box: a quad faced away and the slot stayed dark)
            _scopeScreen = new Material(Shader.Find("Unlit/Texture")) { mainTexture = _scopeTexture };
            screen.GetComponent<Renderer>().sharedMaterial = _scopeScreen;
        }

        /// <summary>Colours from the vehicle's own largest material (its hull): the walls a darker shade of it, the metal darker still.</summary>
        private void Materials(Vehicle v)
        {
            Renderer biggest = null;
            float size = 0f;
            foreach (Renderer r in v.GetComponentsInChildren<Renderer>())
            {
                if (r == null || r is ParticleSystemRenderer || r.sharedMaterial == null)
                {
                    continue;
                }
                float s = r.bounds.size.sqrMagnitude;
                if (s > size)
                {
                    size = s;
                    biggest = r;
                }
            }
            Shader standard = Shader.Find("Standard");
            Color tint = new Color(0.32f, 0.36f, 0.3f);
            Texture tex = null;
            if (biggest != null)
            {
                Material m = biggest.sharedMaterial;
                tex = m.HasProperty("_MainTex") ? m.mainTexture : null;
                tint = m.HasProperty("_Color") ? m.color : tint;
            }
            // Halfway to a steel grey: a hull's material often carries its team's colour (an attack boat's cabin came out pink).
            float grey = tint.grayscale;
            tint = Color.Lerp(tint, new Color(grey, grey, grey), 0.5f);
            tint.a = 1f;
            _wall = new Material(standard) { color = tint * 0.6f, mainTexture = tex };
            _wall.mainTextureScale = new Vector2(1.5f, 1.5f);
            _wall.SetFloat("_Glossiness", 0.15f);
            _wallEmit = tint * 0.12f;
            Emit(_wall, _wallEmit);
            _metal = new Material(standard) { color = tint * 0.45f };
            _paint = new Material(standard) { color = Color.Lerp(tint, new Color(0.55f, 0.56f, 0.5f), 0.6f) };
            _paint.SetFloat("_Metallic", 0.4f);
            _paint.SetFloat("_Glossiness", 0.45f);
            Emit(_paint, new Color(0.07f, 0.07f, 0.06f));
            _metal.SetFloat("_Metallic", 0.6f);
            _metal.SetFloat("_Glossiness", 0.35f);
            Emit(_metal, tint * 0.05f);
            _red = new Material(standard) { color = new Color(0.75f, 0.08f, 0.06f) };
            Emit(_red, new Color(0.25f, 0.02f, 0.02f));
            _button = new Material(standard) { color = new Color(0.8f, 0.75f, 0.55f) };
            Emit(_button, new Color(0.12f, 0.11f, 0.08f));
            _lamp = new Material(standard) { color = new Color(1f, 0.85f, 0.6f) };
            _lampColor = new Color(1.2f, 0.95f, 0.6f);
            Emit(_lamp, _lampColor);
        }

        private static void Emit(Material m, Color c)
        {
            m.EnableKeyword("_EMISSION");
            m.SetColor("_EmissionColor", c);
        }

        /// <summary>For the vehicle check: how bright the periscope's picture is (0 black .. 1 white), -1 with none.</summary>
        internal float PeriscopeBrightness()
        {
            if (_scopeTexture == null)
            {
                return -1f;
            }
            RenderTexture prev = RenderTexture.active;
            RenderTexture.active = _scopeTexture;
            var t = new Texture2D(_scopeTexture.width, _scopeTexture.height, TextureFormat.RGB24, false);
            t.ReadPixels(new Rect(0, 0, _scopeTexture.width, _scopeTexture.height), 0, 0);
            t.Apply();
            RenderTexture.active = prev;
            float sum = 0f;
            Color[] px = t.GetPixels();
            for (int i = 0; i < px.Length; i += 7)
            {
                sum += px[i].grayscale;
            }
            Object.Destroy(t);
            return sum / Mathf.Max(1, px.Length / 7);
        }

        internal float PeriscopeNear => _scope != null ? _scope.nearClipPlane : -1f;

        /// <summary>For the vehicle check: the periscope's picture saved as a PNG, and whether its screen has the picture on it.</summary>
        internal string SavePeriscope(string file)
        {
            if (_scopeTexture == null)
            {
                return "none";
            }
            RenderTexture prev = RenderTexture.active;
            RenderTexture.active = _scopeTexture;
            var t = new Texture2D(_scopeTexture.width, _scopeTexture.height, TextureFormat.RGB24, false);
            t.ReadPixels(new Rect(0, 0, _scopeTexture.width, _scopeTexture.height), 0, 0);
            t.Apply();
            RenderTexture.active = prev;
            System.IO.File.WriteAllBytes(file, ImageConversion.EncodeToPNG(t));
            Object.Destroy(t);
            Renderer screen = _root != null ? _root.transform.Find("periscope screen")?.GetComponent<Renderer>() : null;
            return screen == null ? "NO SCREEN" : $"screen {(screen.enabled ? "on" : "OFF")}, shader {(screen.sharedMaterial != null && screen.sharedMaterial.shader != null ? screen.sharedMaterial.shader.name : "NONE")}, texture {(screen.sharedMaterial != null && screen.sharedMaterial.mainTexture == _scopeTexture ? "the periscope's" : "OTHER")}, camera {(_scope != null && _scope.enabled ? "on" : "OFF")}";
        }

        private Transform Box(string name, Vector3 at, Vector3 size, Material m) => Part(PrimitiveType.Cube, name, at, size, Quaternion.identity, m);

        private Transform Cylinder(string name, Vector3 at, Vector3 size, Quaternion turn, Material m) => Part(PrimitiveType.Cylinder, name, at, size, turn, m);

        private Transform Part(PrimitiveType type, string name, Vector3 at, Vector3 size, Quaternion turn, Material m)
        {
            GameObject g = GameObject.CreatePrimitive(type);
            Object.Destroy(g.GetComponent<Collider>());
            g.name = name;
            g.transform.SetParent(_root.transform, false);
            g.transform.localPosition = at;
            g.transform.localRotation = turn;
            g.transform.localScale = size;
            Renderer r = g.GetComponent<Renderer>();
            r.sharedMaterial = m;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
            return g.transform;
        }

        /// <summary>A handwheel facing the gunner (its axis toward him): a rim of eight bars, three spokes, a crank knob.</summary>
        private Transform Wheel(string name, Vector3 at)
        {
            var hub = new GameObject(name).transform;
            hub.SetParent(_root.transform, false);
            hub.localPosition = at;
            hub.localRotation = Quaternion.Euler(-30f, 0f, 0f); // (tipped toward the eyes)
            const float r = 0.11f;
            for (int i = 0; i < 8; i++)
            {
                float a = i * 45f;
                Transform bar = Cylinder("rim", Vector3.zero, new Vector3(0.022f, r * 0.42f, 0.022f), Quaternion.identity, _paint);
                bar.SetParent(hub, false);
                bar.localPosition = Quaternion.Euler(0f, 0f, a) * new Vector3(0f, r, 0f);
                bar.localRotation = Quaternion.Euler(0f, 0f, a + 90f);
            }
            for (int i = 0; i < 3; i++)
            {
                Transform spoke = Cylinder("spoke", Vector3.zero, new Vector3(0.014f, r * 0.5f, 0.014f), Quaternion.identity, _paint);
                spoke.SetParent(hub, false);
                spoke.localPosition = Quaternion.Euler(0f, 0f, i * 120f) * new Vector3(0f, r * 0.5f, 0f);
                spoke.localRotation = Quaternion.Euler(0f, 0f, i * 120f);
            }
            Transform knob = Cylinder("knob", Vector3.zero, new Vector3(0.03f, 0.035f, 0.03f), Quaternion.identity, _button);
            knob.SetParent(hub, false);
            knob.localPosition = new Vector3(0f, r, -0.035f);
            knob.localRotation = Quaternion.Euler(90f, 0f, 0f);
            return hub;
        }

        private Transform Button(string name, Vector3 at) =>
            Cylinder(name, at, new Vector3(0.05f, 0.012f, 0.05f), Quaternion.Euler(-35f - 90f, 0f, 0f), name == "forward" || name == "back" ? _button : _button);

        // ---- the hands -----------------------------------------------------------------------------------------------------

        private const float WheelReach = 0.08f, WheelRadius = 0.11f, ButtonReach = 0.06f;

        private void Hands()
        {
            Weapon gun = Gun(_seat);
            Vector2 drive = Vector2.zero;
            bool fire = false;
            bool anyWheel = false;
            for (int h = 0; h < 2; h++)
            {
                VRHand hand = _rig.Hands[h];
                if (hand == null || !hand.Tracked)
                {
                    ReleaseWheel(h);
                    continue;
                }
                Vector3 palm = hand.PalmCentre;
                // A handwheel: taken by a grip at its rim, cranked round its axis.
                if (_onWheel[h] < 0 && hand.Role == HandRole.Free && VRInput.GetHeldUnclaimed(DigitalAction.Grab, hand.Side))
                {
                    for (int w = 0; w < 3; w++)
                    {
                        Transform wheel = WheelOf(w);
                        if (wheel != null && OnRim(wheel, palm))
                        {
                            _onWheel[h] = w;
                            _wheelAngle[h] = RimAngle(wheel, palm);
                            hand.Role = HandRole.Cockpit;
                            hand.Model.CockpitStick = true;
                            VRInput.Consume(DigitalAction.Grab, hand.Side);
                            hand.Haptic(0.03f, 120f, 0.4f);
                            break;
                        }
                    }
                }
                if (_onWheel[h] >= 0)
                {
                    if (!hand.GrabHeld)
                    {
                        ReleaseWheel(h);
                    }
                    else
                    {
                        Transform wheel = WheelOf(_onWheel[h]);
                        float angle = RimAngle(wheel, palm);
                        float turned = Mathf.DeltaAngle(_wheelAngle[h], angle);
                        _wheelAngle[h] = angle;
                        if (_onWheel[h] == 2)
                        {
                            // (The steering wheel turns only as far as the lock: past it the drawn wheel and the steering disagreed.)
                            float before = _steerTurned;
                            turned = Mathf.Clamp(before + turned, -120f, 120f) - before;
                        }
                        wheel.localRotation *= Quaternion.Euler(0f, 0f, turned); // (the wheel turns with the hand)
                        if (Mathf.Abs(turned) > 3f)
                        {
                            hand.Haptic(0.008f, 200f, 0.2f); // (the gear's ticks)
                        }
                        if (_onWheel[h] == 2)
                        {
                            // The steering wheel: a third of a turn each way is full lock (turned clockwise steers right).
                            _steerTurned = Mathf.Clamp(_steerTurned + turned, -120f, 120f);
                            VehicleInput.WheelSteer = -_steerTurned / 120f;
                        }
                        else
                        {
                            Crank(_onWheel[h], turned);
                            anyWheel = true;
                        }
                    }
                }
                // Buttons: pressed while the hand is on them (not one holding a wheel).
                if (_onWheel[h] < 0)
                {
                    if (_fire != null && Vector3.Distance(palm, _fire.position) < ButtonReach)
                    {
                        fire = true;
                    }
                    foreach ((Transform t, Vector2 d) in _driveButtons)
                    {
                        if (Vector3.Distance(palm, t.position) < ButtonReach)
                        {
                            drive += d;
                        }
                    }
                }
            }
            // A click as each is pressed, the button sunk in while held.
            Press(0, _fire, fire);
            for (int i = 0; i < _driveButtons.Count; i++)
            {
                (Transform t, Vector2 d) = _driveButtons[i];
                Press(1 + i, t, Vector2.Dot(drive, d) > 0.5f);
            }
            Fire = fire;
            VehicleInput.DriveButtons = Vector2.ClampMagnitude(drive, 1f);
            Alarm();
        }

        /// <summary>The aim seat's gun: the right stick turns the wanted aim (the wheels too, in Hands), kept near the gun.</summary>
        private void Aim()
        {
            Weapon gun = _aimSeat != null ? Gun(_aimSeat) : null;
            if (gun != null && _aimSet)
            {
                // The right thumbstick (the gun hand's) turns it too: 70 degrees a second sideways, 35 up and down at full push.
                Vector2 stick = VRGameInput.ApplyDeadzone(VRGameInput.TurnStickRaw, VRConfig.StickDeadzone.Value);
                float dt = Mathf.Min(Time.unscaledDeltaTime, 0.05f);
                if (stick.sqrMagnitude > 0f)
                {
                    Crank(0, stick.x * 70f * dt * 4f);
                    Crank(1, stick.y * 35f * dt * 12f); // (pushed up: the gun up)
                }
                // Never far ahead of the gun: at a stop of its turn (or held by the game), the wanted aim waits there (no wind-up
                // to turn back through).
                Vector3 actual = _aimSeat.vehicle.transform.InverseTransformDirection(TurretGrip.AimReference(gun).forward);
                if (Vector3.Angle(actual, _aimLocal) > 20f)
                {
                    _aimLocal = Vector3.RotateTowards(actual, _aimLocal, 20f * Mathf.Deg2Rad, 0f);
                }
                WheelAim = _aimSeat.vehicle.transform.TransformDirection(_aimLocal);
            }
            else
            {
                WheelAim = null;
            }
        }

        /// <summary>Burning or wrecked: a red light flashing twice a second, the lamp and the walls red with it (no sound).</summary>
        private void Alarm()
        {
            Vehicle v = _seat != null ? _seat.vehicle : null;
            bool on = v != null && (v.burning || v.dead) || TestAlarm;
            if (_alarm == null)
            {
                return;
            }
            if (!on)
            {
                if (_alarm.enabled)
                {
                    _alarm.enabled = false;
                    Emit(_lamp, _lampColor);
                    Emit(_wall, _wallEmit);
                }
                return;
            }
            float k = Mathf.Pow(0.5f + 0.5f * Mathf.Sin(Time.unscaledTime * Mathf.PI * 4f), 2f);
            _alarm.enabled = true;
            _alarm.intensity = 3f * k;
            Emit(_lamp, Color.Lerp(new Color(0.3f, 0.02f, 0.02f), new Color(2.5f, 0.1f, 0.05f), k));
            Emit(_wall, Color.Lerp(_wallEmit, new Color(0.5f, 0.03f, 0.02f), k));
        }

        private void Press(int i, Transform t, bool down)
        {
            if (t == null)
            {
                return;
            }
            if (down != _pressed[i])
            {
                _pressed[i] = down;
                t.localPosition += t.up * (down ? -0.008f : 0.008f);
                foreach (VRHand hand in _rig.Hands)
                {
                    if (hand != null && hand.Tracked && Vector3.Distance(hand.PalmCentre, t.position) < ButtonReach + 0.02f)
                    {
                        hand.Haptic(0.015f, 180f, down ? 0.6f : 0.3f);
                    }
                }
            }
        }

        /// <summary>The crank's turn into the wanted aim: a full turn is 90 degrees sideways (traverse) or 30 up and down (elevation).</summary>
        private void Crank(int wheel, float degrees) => _aimLocal = Cranked(_aimLocal, wheel, degrees);

        /// <summary>
        /// The wanted aim (the vehicle's frame, up is up) turned by a crank: the traverse wheel (0) a quarter of its turn sideways (a
        /// full turn 90 degrees), the elevation wheel (1) a twelfth up or down (30), never past 60 degrees from level. Pure (offline tests).
        /// </summary>
        internal static Vector3 Cranked(Vector3 aim, int wheel, float degrees)
        {
            Vector3 up = Vector3.up;
            if (wheel == 0)
            {
                aim = TurretGrip.Rotate(aim, up, degrees * 0.25f);
            }
            else
            {
                Vector3 across = Vector3.Cross(up, aim).normalized;
                Vector3 next = TurretGrip.Rotate(aim, across, -degrees * (30f / 360f));
                if (Vector3.Angle(Vector3.ProjectOnPlane(next, up), next) < 60f)
                {
                    aim = next;
                }
            }
            return aim.normalized;
        }

        /// <summary>
        /// A point in the wheel's mount (where it sits, as built: not its turn, which follows the hand). Read against the turning wheel
        /// itself, each crank's own turn came back as the hand's and the turret swung 90 to 180 degrees for half a turn.
        /// </summary>
        private static Vector3 InMount(Transform wheel, Vector3 world)
        {
            Quaternion mount = wheel.parent.rotation * Quaternion.Euler(-30f, 0f, 0f);
            return Quaternion.Inverse(mount) * (world - wheel.position);
        }

        private static bool OnRim(Transform wheel, Vector3 palm)
        {
            Vector3 local = InMount(wheel, palm);
            float r = new Vector2(local.x, local.y).magnitude;
            return Mathf.Abs(local.z) < WheelReach && r > WheelRadius - WheelReach && r < WheelRadius + WheelReach;
        }

        private static float RimAngle(Transform wheel, Vector3 palm)
        {
            Vector3 local = InMount(wheel, palm);
            return Mathf.Atan2(local.x, local.y) * Mathf.Rad2Deg;
        }

        private Transform WheelOf(int w) => w == 0 ? _traverse : w == 1 ? _elevate : _steer;

        private void ReleaseWheel(int h)
        {
            if (_onWheel[h] < 0)
            {
                return;
            }
            if (_onWheel[h] == 2 && _onWheel[1 - h] != 2)
            {
                // Let go of the steering wheel: back to straight (as the cars' wheels), the stick steers again.
                _steerTurned = 0f;
                VehicleInput.WheelSteer = null;
                if (_steer != null)
                {
                    _steer.localRotation = Quaternion.Euler(-30f, 0f, 0f);
                }
            }
            _onWheel[h] = -1;
            VRHand hand = _rig.Hands[h];
            if (hand != null)
            {
                if (hand.Role == HandRole.Cockpit)
                {
                    hand.Role = HandRole.Free;
                }
                hand.Model.CockpitStick = false;
            }
        }

        private void ReleaseHands()
        {
            ReleaseWheel(0);
            ReleaseWheel(1);
            Fire = false;
            VehicleInput.DriveButtons = Vector2.zero; // (the wanted aim stays: a pause or a rebuilt cabin left the gun with none)
        }

        private void Clear()
        {
            ReleaseHands();
            _driveButtons.Clear();
            _traverse = _elevate = _fire = _steer = null;
            _steerTurned = 0f;
            for (int i = 0; i < _pressed.Length; i++)
            {
                _pressed[i] = false;
            }
            if (_root != null)
            {
                Object.Destroy(_root);
                _root = null;
            }
            if (_scopeTexture != null)
            {
                _scopeTexture.Release();
                Object.Destroy(_scopeTexture);
                _scopeTexture = null;
            }
            if (_scopeScreen != null)
            {
                Object.Destroy(_scopeScreen);
                _scopeScreen = null;
            }
            _scope = null;
            foreach (Material m in new[] { _wall, _metal, _red, _lamp, _button, _paint })
            {
                if (m != null)
                {
                    Object.Destroy(m);
                }
            }
            _wall = _metal = _red = _lamp = _button = _paint = null;
        }

        public void Destroy()
        {
            Clear();
            _aimSeat = null;
            WheelAim = null;
            Fire = false;
            TestAlarm = false;
            if (Instance == this)
            {
                Instance = null;
            }
        }
    }
}
