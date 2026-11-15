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
        private Transform _traverse, _elevate, _fire;

        /// <summary>For the vehicle check: the traverse handwheel (null: none).</summary>
        internal Transform TraverseWheel => _traverse;

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

        /// <summary>The seat's gun is aimed from the inside (stick and handwheels), not by the head; snap turning is off there.</summary>
        internal static bool AimsGun => Instance != null && Instance._root != null && Instance._seat != null && Gun(Instance._seat) != null;

        private Light _alarm;
        private Color _wallEmit, _lampColor;

        internal static bool Wanted(Seat seat)
        {
            if (seat == null || !seat.enclosed || !VRConfig.Interiors.Value)
            {
                return false;
            }
            Vehicle v = seat.vehicle;
            // (Not a seat whose gun is taken by hand: that gunner stands out of a hatch, a tank's roof MG.)
            return !(v is Helicopter || v is Airplane || v is Plane) && !TurretGrip.Grabbable(seat, out _);
        }

        private static Weapon Gun(Seat seat)
        {
            MountedWeapon w = seat.activeWeapon;
            return w is MountedTurret || w is MountedStabilizedTurret || w is TankTurret ? w : null;
        }

        public void Update()
        {
            Actor actor = GameContext.PlayerActor;
            Seat seat = actor != null && !actor.dead && actor.IsSeated() && Wanted(actor.seat) ? actor.seat : null;
            if (seat != _seat)
            {
                Clear();
                _seat = seat;
                if (_seat != null)
                {
                    Build();
                }
            }
            if (_root == null)
            {
                return;
            }
            if (GameContext.GameMenuOpen || GameManager.IsPaused())
            {
                ReleaseHands();
                return;
            }
            Hands();
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
                _traverse = Wheel("traverse", new Vector3(-0.26f, -0.36f, 0.3f));
                _elevate = Wheel("elevation", new Vector3(0.26f, -0.36f, 0.3f));
                Box("fire-mount", new Vector3(0.26f, -0.2f, 0.4f), new Vector3(0.1f, 0.06f, 0.08f), _paint);
                _fire = Cylinder("fire", new Vector3(0.26f, -0.16f, 0.39f), new Vector3(0.06f, 0.015f, 0.06f), Quaternion.Euler(-60f, 0f, 0f), _red);
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
            Log.InfoOnce("vehicle-interior:" + _seat.vehicle.name, $"Vehicle inside for {_seat.vehicle.name} ({(Gun(_seat) != null ? "handwheels and fire button" : "no gun")}{(_seat.IsDriverSeat() ? ", drive buttons" : "")}).");
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
            _scope.nearClipPlane = near;
            go.transform.localPosition = Vector3.zero;
            go.transform.localRotation = Quaternion.identity;
            Transform screen = Part(PrimitiveType.Quad, "periscope screen", at, new Vector3(size.x, size.y, 1f), Quaternion.identity, null);
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
                if (_onWheel[h] < 0 && gun != null && hand.Role == HandRole.Free && VRInput.GetHeldUnclaimed(DigitalAction.Grab, hand.Side))
                {
                    for (int w = 0; w < 2; w++)
                    {
                        Transform wheel = w == 0 ? _traverse : _elevate;
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
                        Transform wheel = _onWheel[h] == 0 ? _traverse : _elevate;
                        float angle = RimAngle(wheel, palm);
                        float turned = Mathf.DeltaAngle(_wheelAngle[h], angle);
                        _wheelAngle[h] = angle;
                        wheel.localRotation *= Quaternion.Euler(0f, 0f, turned); // (the wheel turns with the hand)
                        if (Mathf.Abs(turned) > 3f)
                        {
                            hand.Haptic(0.008f, 200f, 0.2f); // (the gear's ticks)
                        }
                        Crank(_onWheel[h], turned);
                        anyWheel = true;
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
                Vector3 actual = _seat.vehicle.transform.InverseTransformDirection(TurretGrip.AimReference(gun).forward);
                if (Vector3.Angle(actual, _aimLocal) > 20f)
                {
                    _aimLocal = Vector3.RotateTowards(actual, _aimLocal, 20f * Mathf.Deg2Rad, 0f);
                }
                WheelAim = _seat.vehicle.transform.TransformDirection(_aimLocal);
            }
            else
            {
                WheelAim = null;
            }
            Alarm();
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
        private void Crank(int wheel, float degrees)
        {
            Vector3 up = Vector3.up;
            if (wheel == 0)
            {
                _aimLocal = Quaternion.AngleAxis(degrees * 0.25f, up) * _aimLocal;
            }
            else
            {
                Vector3 across = Vector3.Cross(up, _aimLocal).normalized;
                Vector3 next = Quaternion.AngleAxis(-degrees * (30f / 360f), across) * _aimLocal;
                if (Vector3.Angle(Vector3.ProjectOnPlane(next, up), next) < 60f)
                {
                    _aimLocal = next;
                }
            }
            _aimLocal.Normalize();
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

        private void ReleaseWheel(int h)
        {
            if (_onWheel[h] < 0)
            {
                return;
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
            WheelAim = null;
            _aimSet = false;
            VehicleInput.DriveButtons = Vector2.zero;
        }

        private void Clear()
        {
            ReleaseHands();
            _driveButtons.Clear();
            _traverse = _elevate = _fire = null;
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

        public void Destroy() => Clear();
    }
}
