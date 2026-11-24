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
        // The gunner's station (the user, 2026-11-15: "the cockpit we made is kinda beta ... improve the cockpit for every tank"):
        // a ready lamp by the fire button, an ammunition readout, a cannon's breech; a tank driver's two steering levers.
        private Transform _fireReady, _breech, _breechBlock;
        private Material _guard, _ready;
        private TextMesh _ammoText;
        private float _nextReadout, _shotAt = -10f;
        private Vector3 _breechAt, _blockAt;
        private readonly Transform[] _levers = new Transform[2]; // left track, right track
        private readonly float[] _leverValue = new float[2];     // -1 pulled back .. 1 pushed forward
        private readonly int[] _onLever = { -1, -1 };            // per hand: the lever held, -1 none
        private bool _throttleOnly;                              // the right lever is a wheeled driver's throttle (no left one)

        /// <summary>For the vehicle check: the right lever is a throttle (forward and back only), not a track's.</summary>
        internal bool ThrottleOnly => _throttleOnly;

        /// <summary>
        /// A wheeled closed vehicle's driver (an APC, the mods' armoured cars) steers by a wheel in the middle too (the user: "make also
        /// sure all steering wheels are turneable"); a tank steers by its tracks and keeps the buttons.
        /// </summary>
        private static bool SteersByWheel(Seat seat) =>
            seat != null && seat.IsDriverSeat() && (seat.vehicle is Car || seat.vehicle is ArcadeCar) && !Tracked(seat.vehicle);

        /// <summary>A tracked vehicle: the game's Tank, or one with a tank's cannon (the mods' Leopards are cars to the game).</summary>
        private static bool Tracked(Vehicle v)
        {
            if (v == null)
            {
                return false;
            }
            if (!ReferenceEquals(v, _trackedOf))
            {
                // (Once a vehicle: asked every frame for the aim seat, the walk over its parts was not.)
                _trackedOf = v;
                _trackedIs = v is Tank || v.GetComponentInChildren<TankTurret>(true) != null;
            }
            return _trackedIs;
        }

        private static Vehicle _trackedOf;
        private static bool _trackedIs;

        /// <summary>For the vehicle check: the traverse handwheel (null: none).</summary>
        internal Transform TraverseWheel => _traverse;

        /// <summary>For the vehicle check: the fire button and the forward drive button (null: none).</summary>
        internal Transform FireButton => _fire;

        internal Transform ElevationWheel => _elevate;

        internal Transform CabinSteeringWheel => _steer;

        /// <summary>For the vehicle check: a tank driver's right steering lever (null: none), and the ammunition readout's text.</summary>
        internal Transform RightLever => _levers[1];

        internal string Readout => _ammoText != null ? _ammoText.text : "none";

        /// <summary>For the vehicle check: the ready lamp's state ("ready", "reloading", "empty"; "none" with no gun).</summary>
        internal string ReadyState { get; private set; } = "none";

        /// <summary>For the vehicle check: the rack's rounds shown and in all.</summary>
        internal string RackState => _rounds.Count == 0 ? "none" : $"{_roundsShown} of {_rounds.Count} shown";

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
        private float _lookUntil;

        /// <summary>A shot of the inside's gun: a buzz in each hand on the fire button or a handwheel, harder for a big gun.</summary>
        internal static void Kick(Weapon w)
        {
            VehicleInterior me = Instance;
            if (me == null || me._seat == null || me._seat.activeWeapon != w)
            {
                return;
            }
            bool heavy = w.configuration != null && w.configuration.ammo <= 1; // (a cannon: one round a load)
            if (heavy)
            {
                me._shotAt = Time.unscaledTime; // (the breech runs back and forward again: Breech)
            }
            for (int h = 0; h < 2; h++)
            {
                VRHand hand = me._rig.Hands[h];
                if (hand == null || !hand.Tracked)
                {
                    continue;
                }
                bool onControls = me._onWheel[h] >= 0 || me._onLever[h] >= 0 || me._fire != null && Vector3.Distance(hand.PalmCentre, me._fire.position) < ButtonReach + 0.03f;
                if (onControls)
                {
                    hand.Haptic(heavy ? 0.12f : 0.03f, heavy ? 40f : 80f, heavy ? 1f : 0.6f);
                }
            }
        }

        /// <summary>The seat's gun is aimed from the inside (stick and handwheels), not by the head; snap turning is off there.</summary>
        internal static bool AimsGun => Instance != null && Instance._aimSeat != null;

        // ---- loading the cannon by hand ----------------------------------------------------------------------------------------
        // (Manual reload, a cannon's seat: the gun waits empty for its loader. A shell taken from the rack by a grip and pushed into the
        // open breech loads it at once; let go anywhere else, it goes back in the rack.)
        private readonly List<Vector3> _rackCentres = new List<Vector3>(); // each shell's middle (cabin frame)
        private Transform _carried;
        private int _carryHand = -1;
        private static bool _loading;
        private const float ShellReach = 0.12f, BreechReach = 0.17f;
        private Weapon _cannon; // the gun the breech or the feed tray belongs to (null: neither)
        // A belt- or box-fed gun's feed tray on the left (an APC's gun, the mods' closed machine guns and autocannons): its cover open
        // while the gun waits for a box, the box set on it loads the gun.
        private Transform _feedLid;
        private Vector3 _feedLidAt;
        private const float FeedReach = 0.15f;

        /// <summary>
        /// The feed tray left of the handwheels: a tray, its cover hinged at the back (lifted 50 degrees while open), a guide rail into
        /// the gun.
        /// </summary>
        private void BuildFeed()
        {
            Vector3 at = new Vector3(-0.47f, -0.14f, 0.3f);
            Box("feed-tray", at, new Vector3(0.16f, 0.06f, 0.2f), _metal);
            Box("feed-guide", at + new Vector3(0f, 0.01f, 0.14f), new Vector3(0.06f, 0.03f, 0.1f), _paint);
            var hinge = new GameObject("feed-cover").transform;
            hinge.SetParent(_root.transform, false);
            _feedLidAt = at + new Vector3(0f, 0.035f, 0.1f); // (the cover's hinge: the tray's far top edge)
            hinge.localPosition = _feedLidAt;
            Transform cover = Box("feed-cover-plate", Vector3.zero, new Vector3(0.165f, 0.01f, 0.2f), _olive != null ? _olive : _paint);
            cover.SetParent(hinge, false);
            cover.localPosition = new Vector3(0f, 0f, -0.1f);
            _feedLid = hinge;
            Label("FEED", at + new Vector3(0f, 0.031f, -0.06f), Quaternion.Euler(90f, 0f, 0f), 0.0012f, new Color(1f, 0.85f, 0.3f));
        }

        /// <summary>Where a box goes: the tray's middle, over it (world; zero: no tray).</summary>
        private Vector3 FeedMouth => _feedLid != null ? _feedLid.parent.TransformPoint(_feedLidAt + new Vector3(0f, 0.04f, -0.1f)) : Vector3.zero;

        /// <summary>The feed tray's cover: up 50 degrees while the gun reloads or waits for a box, down once it is loaded.</summary>
        private void Feed()
        {
            if (_feedLid == null)
            {
                return;
            }
            Weapon gun = _cannon;
            bool open = gun != null && (gun.reloading || gun.ammo <= 0);
            Quaternion want = Quaternion.Euler(open ? 50f : 0f, 0f, 0f); // (+: the plate, toward the eyes from its hinge, swings up; scout)
            _feedLid.localRotation = Quaternion.RotateTowards(_feedLid.localRotation, want, Time.unscaledDeltaTime * 300f);
        }

        /// <summary>This seat's cannon is loaded by hand: Manual reload, a breech and a rack of shells in the inside.</summary>
        private bool LoadsByHand => (_breech != null || _feedLid != null) && _rounds.Count > 0 && VRConfig.ReloadNow == ReloadMode.Manual
                                    && (VRConfig.LoadCannonByHand == null || VRConfig.LoadCannonByHand.Value || TestLoadByHand);

        /// <summary>For the vehicle check: loading by hand checked whatever the setting.</summary>
        internal static bool TestLoadByHand;

        /// <summary>The game's own reload of the inside's cannon waits for a shell pushed into the breech (not its automatic one).</summary>
        internal static bool HoldsReload(Weapon gun)
        {
            VehicleInterior me = Instance;
            if (me == null || _loading || !me.LoadsByHand || me._seat == null || gun == null)
            {
                return false;
            }
            // (The cannon the breech was built for, whichever of the seat's guns is drawn: the coaxial machine gun is not loaded by hand.)
            Weapon mine = me._cannon;
            // (Only the cannon itself: its coaxial sub-weapon's own reload held too left the MG empty for good; scout.)
            return mine != null && ReferenceEquals(mine, gun) && ReferenceEquals(mine.GetActiveSubWeapon(), mine);
        }

        /// <summary>For the vehicle check: the first shell in the rack and the breech's mouth (world; zero: none).</summary>
        internal Vector3 FirstShell
        {
            get
            {
                for (int i = 0; i < _rounds.Count && i < _rackCentres.Count; i++)
                {
                    if (_rounds[i] != null && _rounds[i].activeSelf)
                    {
                        return _root.transform.TransformPoint(_rackCentres[i]);
                    }
                }
                return Vector3.zero;
            }
        }

        internal Vector3 BreechMouth => _breechBlock != null ? _breechBlock.position : FeedMouth;

        /// <summary>For the vehicle check: how far behind the mouth the carrying palm is when the load goes home (a shell's nose 30 cm
        /// ahead of the fist, a box's middle 5 cm).</summary>
        internal float CarryAhead => _breech != null ? 0.3f : 0.05f;

        /// <summary>For the vehicle check: a shell is in a hand.</summary>
        internal bool Carrying => _carried != null;

        /// <summary>A grip on a shell in the rack takes it (a copy in the hand, the rack one less meanwhile).</summary>
        private bool TakeShell(VRHand hand, int h, Vector3 palm)
        {
            if (!LoadsByHand || _carried != null)
            {
                return false;
            }
            for (int i = 0; i < _rounds.Count && i < _rackCentres.Count; i++)
            {
                if (_rounds[i] == null || !_rounds[i].activeSelf || Vector3.Distance(palm, _root.transform.TransformPoint(_rackCentres[i])) > ShellReach)
                {
                    continue;
                }
                _carried = new GameObject(_breech != null ? "carried shell" : "carried ammo box").transform;
                _carried.SetParent(_root.transform, false);
                // (The rack shell's parts round its middle: case, tip, nose along the shell's axis; a box with its lid and handle.)
                void Piece(PrimitiveType type, string n, float y, Vector3 size, Material m)
                {
                    Transform t = Part(type, n, Vector3.zero, size, Quaternion.identity, m);
                    t.SetParent(_carried, false);
                    t.localPosition = new Vector3(0f, y, 0f);
                }
                if (_breech != null)
                {
                    Piece(PrimitiveType.Cylinder, "shell-case", -0.095f, new Vector3(0.09f, 0.2f, 0.09f), _brass);
                    Piece(PrimitiveType.Cylinder, "shell-tip", 0.175f, new Vector3(0.075f, 0.07f, 0.075f), _metal);
                    Piece(PrimitiveType.Cylinder, "shell-nose", 0.265f, new Vector3(0.04f, 0.03f, 0.04f), _metal);
                }
                else
                {
                    // (Held by its handle: the box hangs under the fist. In the carried frame y runs along the hand: so the box's height
                    // is across it; as a box swings from a handle, it reads well enough.)
                    Piece(PrimitiveType.Cube, "ammo-box", 0f, new Vector3(0.12f, 0.17f, 0.16f), _olive);
                    Piece(PrimitiveType.Cube, "ammo-box-lid", 0f, new Vector3(0.125f, 0.175f, 0.015f), _olive);
                }
                // (The rack hides its last shown shell while one is carried and after it is rammed: the taken one is swapped into
                // that place, or shell 2 taken left shell 2 standing and shell 4 went; scout.)
                int last = i;
                for (int k = i + 1; k < _rounds.Count && k < _rackCentres.Count; k++)
                {
                    if (_rounds[k] != null && _rounds[k].activeSelf)
                    {
                        last = k;
                    }
                }
                if (last != i)
                {
                    (_rounds[i], _rounds[last]) = (_rounds[last], _rounds[i]);
                    (_rackCentres[i], _rackCentres[last]) = (_rackCentres[last], _rackCentres[i]);
                }
                _carryHand = h;
                hand.Role = HandRole.Cockpit;
                hand.Model.CockpitStick = true;
                VRInput.Consume(DigitalAction.Grab, hand.Side);
                hand.Haptic(0.04f, 90f, 0.6f);
                _roundsShown = -1; // (the rack shows one less while it is carried: Rounds)
                CarryTo(hand);
                return true;
            }
            return false;
        }

        /// <summary>The carried shell in the fist, nose forward along the hand.</summary>
        private void CarryTo(VRHand hand)
        {
            _carried.SetPositionAndRotation(hand.PalmCentre + hand.Raw.rotation * new Vector3(0f, 0f, 0.05f), hand.Raw.rotation * Quaternion.Euler(90f, 0f, 0f));
        }

        /// <summary>
        /// The shell in the hand: carried while gripped; at the open breech's mouth with the gun empty, rammed home (the gun loaded at
        /// once, a heavy thump in the hand, the block shut); let go elsewhere, back in the rack.
        /// </summary>
        private void Carry(VRHand hand)
        {
            if (_carried == null)
            {
                return;
            }
            Weapon gun = _cannon; // (not the drawn gun: a shell into the breech with the coaxial machine gun drawn loaded the machine gun)
            if (!hand.GrabHeld || gun == null)
            {
                DropShell();
                return;
            }
            CarryTo(hand);
            // (A shell's nose at the breech's mouth, its middle a hand's length behind it; a box's middle over the feed tray.)
            Vector3 nose = _breech != null ? _carried.TransformPoint(new Vector3(0f, 0.25f, 0f)) : _carried.position;
            float reach = _breech != null ? BreechReach : FeedReach;
            // (With its sub-weapon drawn the game's reload goes to the sub-weapon: no ram then; scout.)
            if (gun.ammo < gun.configuration.ammo && gun.HasSpareAmmo() && ReferenceEquals(gun.GetActiveSubWeapon(), gun)
                && Vector3.Distance(nose, BreechMouth) < reach)
            {
                int before = gun.ammo;
                _loading = true;
                try
                {
                    gun.InstantlyReload();
                }
                finally
                {
                    _loading = false;
                }
                if (gun.ammo <= before)
                {
                    return; // (nothing went in, an advanced-reload gun's: the shell stays in the hand)
                }
                hand.Haptic(0.09f, 50f, 1f);
                // (The gun's own reload sound, the game's: InstantlyReload plays none, and the breech shut in silence.)
                if (gun.reloadAudio != null && gun.reloadAudio.isActiveAndEnabled)
                {
                    gun.reloadAudio.Play();
                }
                _rammedAt = Time.unscaledTime;
                DropShell();
            }
        }

        private float _rammedAt = -10f;

        private void DropShell()
        {
            if (_carried != null)
            {
                Object.Destroy(_carried.gameObject);
                _carried = null;
                _roundsShown = -1;
            }
            if (_carryHand >= 0)
            {
                VRHand hand = _rig.Hands[_carryHand];
                if (hand != null)
                {
                    if (hand.Role == HandRole.Cockpit)
                    {
                        hand.Role = HandRole.Free;
                    }
                    hand.Model.CockpitStick = false;
                }
                _carryHand = -1;
            }
        }

        /// <summary>This turret is the aim seat's own gun (another player's turret on the same vehicle is not steered from here).</summary>
        internal static bool AimsThisGun(Weapon turret) => AimsGun && Gun(Instance._aimSeat) == turret;

        /// <summary>
        /// The seat whose gun is aimed by stick and handwheels: any enclosed seat with a turret not taken by hand, its cabin drawn or not.
        /// Head aim there chased itself: the view sits on the turret's own camera, turning the turret turned the head's direction, and the
        /// gun spun round (378 degrees in half a second in the vehicle check; the user's "spins 360 degrees").
        /// </summary>
        private Seat _aimSeat;

        // (Also a tracked vehicle's gun and a gun with its own sight camera, enclosed or not: the mods leave many tank gunners' seats
        // open, and there the head aimed the gun, the user's "the view still gets preferred", 2026-11-15.)
        private static bool AimSeat(Seat seat) => seat != null && Gun(seat) != null && !TurretGrip.Grabbable(seat, out _)
                                                  && (seat.enclosed || Tracked(seat.vehicle) || seat.activeWeapon.overrideCamera != null);

        private Light _alarm, _cabin;
        private Transform _lightSwitch, _hint;
        private Material _hintMat;
        private bool _combatLight;
        private static readonly Color DayLight = new Color(1f, 0.86f, 0.62f), CombatLight = new Color(1f, 0.12f, 0.06f);

        /// <summary>For the vehicle check: the cabin light's switch (null: none), whether the red combat light is on, the hint's place.</summary>
        internal Transform LightSwitch => _lightSwitch;

        internal bool CombatLightOn => _combatLight;

        internal Vector3 HintAt => _hint != null && _hint.gameObject.activeSelf ? _hint.position : Vector3.zero;

        /// <summary>The cabin light's toggle on the viewer's left: up for the warm light, down for a dim red one (a night's combat light).</summary>
        private void LightSwitchAt(Vector3 at)
        {
            Box("light-switch-plate", at + new Vector3(0f, 0f, 0.012f), new Vector3(0.05f, 0.07f, 0.01f), _metal);
            _lightSwitch = Box("light-switch", at, new Vector3(0.012f, 0.03f, 0.012f), _button);
            Label("LIGHT", at + new Vector3(0f, 0.05f, 0.005f), Quaternion.identity, 0.0014f, new Color(1f, 0.85f, 0.3f));
            SetCombatLight(false);
        }

        private float _lastHealth = -1f, _hitAt = -10f, _hitHard;

        /// <summary>For the vehicle check: when the inside last showed a hit (unscaled time).</summary>
        internal float LastHitShown => _hitAt;

        /// <summary>
        /// A hit on the hull seen inside: the health dropped since the last frame, the cabin light flickers and dips for a third of a
        /// second, harder for a bigger hit (no sound of its own: the hit's own is the game's). The alarm, when on, keeps its light.
        /// </summary>
        private void HitFlicker()
        {
            Vehicle v = _seat != null ? _seat.vehicle : null;
            if (v == null || _cabin == null)
            {
                return;
            }
            float health = v.GetHealthRatio();
            if (_lastHealth >= 0f && health < _lastHealth - 0.005f)
            {
                _hitAt = Time.unscaledTime;
                _hitHard = Mathf.Clamp01((_lastHealth - health) * 8f + 0.3f);
                Sparks(Mathf.RoundToInt(8 + 20 * _hitHard));
            }
            _lastHealth = health;
            // Badly hurt (under 35 %): thin smoke gathering under the roof behind the head (never before the viewer).
            bool smoking = (health < 0.35f || TestSmoke) && !v.dead;
            if (smoking && _smoke == null)
            {
                _smoke = Particles("smoke", new Vector3(0f, 0.28f, -0.2f), 60);
                ParticleSystem.MainModule main = _smoke.main;
                main.startLifetime = new ParticleSystem.MinMaxCurve(2f, 3.5f);
                main.startSpeed = new ParticleSystem.MinMaxCurve(0.02f, 0.08f);
                main.startSize = new ParticleSystem.MinMaxCurve(0.18f, 0.4f);
                main.startColor = new Color(0.22f, 0.21f, 0.2f, 0.22f);
                main.gravityModifier = -0.01f;
                ParticleSystem.EmissionModule em = _smoke.emission;
                em.rateOverTime = 6f;
                ParticleSystem.ShapeModule shape = _smoke.shape;
                shape.enabled = true;
                shape.shapeType = ParticleSystemShapeType.Sphere;
                shape.radius = 0.15f;
                ParticleSystem.ColorOverLifetimeModule fade = _smoke.colorOverLifetime;
                fade.enabled = true;
                var g = new Gradient();
                g.SetKeys(new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) },
                          new[] { new GradientAlphaKey(0f, 0f), new GradientAlphaKey(1f, 0.25f), new GradientAlphaKey(0f, 1f) });
                fade.color = g;
                _smoke.Play();
            }
            else if (!smoking && _smoke != null && _smoke.isEmitting)
            {
                _smoke.Stop(true, ParticleSystemStopBehavior.StopEmitting); // (repaired: the smoke thins out)
            }
            else if (smoking && _smoke != null && !_smoke.isEmitting)
            {
                _smoke.Play();
            }
            float since = Time.unscaledTime - _hitAt;
            float baseLight = _combatLight ? 0.7f : 1.1f;
            if (since >= 0f && since < 0.35f)
            {
                // (A flicker: off and on in steps, not a smooth fade; Perlin noise so each hit looks its own.)
                float n = Mathf.PerlinNoise(Time.unscaledTime * 40f, _hitAt);
                _cabin.intensity = baseLight * (n > 0.45f ? 1f - 0.85f * _hitHard * (1f - since / 0.35f) : 0.08f);
            }
            else if (!Mathf.Approximately(_cabin.intensity, baseLight))
            {
                _cabin.intensity = baseLight;
            }
        }

        /// <summary>
        /// Stowage, each where the hull has room for it: a fire extinguisher low on the left behind the hand, a first-aid kit (a white
        /// box, a red cross) behind the right shoulder.
        /// </summary>
        private void Stowage(float roomLeft, float roomRight, float floor)
        {
            Shader standard = Shader.Find("Standard");
            if (roomLeft >= 0.5f)
            {
                var red = new Material(standard) { color = new Color(0.7f, 0.06f, 0.04f) };
                red.SetFloat("_Glossiness", 0.6f);
                _extra.Add(red);
                float y = Mathf.Max(floor + 0.17f, -0.45f);
                Cylinder("extinguisher", new Vector3(-0.4f, y, -0.15f), new Vector3(0.1f, 0.15f, 0.1f), Quaternion.identity, red);
                Cylinder("extinguisher-head", new Vector3(-0.4f, y + 0.17f, -0.15f), new Vector3(0.04f, 0.025f, 0.04f), Quaternion.identity, _metal);
                Box("extinguisher-handle", new Vector3(-0.4f, y + 0.2f, -0.13f), new Vector3(0.015f, 0.012f, 0.07f), _metal);
                Box("extinguisher-strap", new Vector3(-0.4f, y + 0.05f, -0.15f), new Vector3(0.105f, 0.02f, 0.105f), _paint);
            }
            if (roomRight >= 0.5f)
            {
                var white = new Material(standard) { color = new Color(0.82f, 0.82f, 0.78f) };
                _extra.Add(white);
                Vector3 kit = new Vector3(0.38f, 0.05f, -0.3f);
                Box("first-aid", kit, new Vector3(0.14f, 0.1f, 0.06f), white);
                Box("first-aid-cross-a", kit + new Vector3(0f, 0f, 0.031f), new Vector3(0.05f, 0.015f, 0.002f), _red);
                Box("first-aid-cross-b", kit + new Vector3(0f, 0f, 0.031f), new Vector3(0.015f, 0.05f, 0.002f), _red);
            }
        }

        private readonly List<Material> _extra = new List<Material>();

        /// <summary>
        /// The seat itself under the player: a cushion where the game seats the soldier (0.85 m under the eyes) on a pedestal to the
        /// floor, and a backrest behind. The own body is not drawn in a cabin, and looking down showed the tread plate far below with
        /// nothing sat on. Only where the floor is under the cushion (a floor met lower than 0.95 m under the eyes).
        /// </summary>
        private void SeatPad(float floor)
        {
            if (floor > -0.95f || floor < -1.49f)
            {
                return;
            }
            var cushion = new Material(Shader.Find("Standard")) { color = new Color(0.13f, 0.12f, 0.1f), mainTexture = InteriorLook.Grime() };
            cushion.SetFloat("_Glossiness", 0.15f);
            _extra.Add(cushion);
            const float top = -0.83f;
            Box("seat-cushion", new Vector3(0f, top - 0.04f, -0.06f), new Vector3(0.4f, 0.08f, 0.38f), cushion);
            Box("seat-pan", new Vector3(0f, top - 0.095f, -0.06f), new Vector3(0.42f, 0.03f, 0.4f), _metal);
            float post = top - 0.11f - floor;
            if (post > 0.03f)
            {
                Cylinder("seat-post", new Vector3(0f, floor + post * 0.5f, -0.08f), new Vector3(0.07f, post * 0.5f, 0.07f), Quaternion.identity, _metal);
            }
            Box("seat-back", new Vector3(0f, top + 0.24f, -0.29f), new Vector3(0.38f, 0.42f, 0.06f), cushion);
            Box("seat-back-frame", new Vector3(0f, top + 0.24f, -0.325f), new Vector3(0.4f, 0.44f, 0.02f), _metal);
        }

        /// <summary>
        /// The hull's roof over the head fitted out where it is (the user, 2026-11-15/17: "the interiors are still bad", "still very
        /// weird in the vehicle"): the hatch handle hung at a fixed 35 cm over the eyes in the air under a roof that may be 0.8 m up.
        /// With a roof met 0.5-1.4 m over the eyes: the hatch's lid and rim overhead, two struts from the handle up to it (moving with
        /// the handle as it is pulled), quilted spall-liner pads either side of the hatch and a cable run along it. Nothing where no
        /// roof was met (1.5: Room's "nothing") or where it is too low for the hatch handle under it.
        /// </summary>
        private void RoofGear(float ceiling, float roomLeft, float roomRight)
        {
            if (ceiling < 0.5f || ceiling > 1.4f || _hatch == null)
            {
                return;
            }
            float top = ceiling - 0.012f;
            Vector3 lid = new Vector3(_hatchAt.x, top, _hatchAt.z);
            Cylinder("hatch-lid", lid, new Vector3(0.5f, 0.012f, 0.5f), Quaternion.identity, _metal);
            Cylinder("hatch-rim", lid + new Vector3(0f, -0.014f, 0f), new Vector3(0.56f, 0.008f, 0.56f), Quaternion.identity, _paint);
            // (The struts are the handle's: children of the hatch, so they come down with it when it is pulled.)
            float strut = top - 0.02f - (_hatchAt.y + 0.06f);
            if (strut > 0.04f)
            {
                foreach (float x in new[] { -0.1f, 0.1f })
                {
                    Transform t = Box("hatch-strut", new Vector3(_hatchAt.x + x, _hatchAt.y + 0.06f + strut * 0.5f, _hatchAt.z), new Vector3(0.016f, strut, 0.016f), _metal);
                    t.SetParent(_hatch, true);
                }
            }
            // Spall-liner pads: dark quilted panels in rows either side of the hatch, within the side walls.
            float half = Mathf.Min(Mathf.Min(roomLeft, roomRight), 1.2f) - 0.12f;
            if (half > 0.35f)
            {
                var liner = new Material(Shader.Find("Standard")) { color = new Color(0.2f, 0.21f, 0.18f), mainTexture = InteriorLook.Grime() };
                liner.SetFloat("_Glossiness", 0.08f);
                _extra.Add(liner);
                for (float x = -half + 0.12f; x <= half - 0.11f; x += 0.24f)
                {
                    for (float z = -0.55f; z <= 0.55f; z += 0.24f)
                    {
                        if (Mathf.Abs(x - lid.x) < 0.36f && Mathf.Abs(z - lid.z) < 0.36f)
                        {
                            continue; // (the hatch's opening)
                        }
                        Box("roof-liner", new Vector3(x, top - 0.012f, z), new Vector3(0.22f, 0.024f, 0.22f), liner);
                    }
                }
                // A cable run along the roof, on the right of the hatch, clipped every 30 cm.
                float cx = Mathf.Min(0.42f, half - 0.05f);
                Cylinder("roof-cable", new Vector3(cx, top - 0.035f, 0f), new Vector3(0.025f, 0.6f, 0.025f), Quaternion.Euler(90f, 0f, 0f), _metal);
                for (float z = -0.45f; z <= 0.46f; z += 0.3f)
                {
                    Box("roof-cable-clip", new Vector3(cx, top - 0.03f, z), new Vector3(0.04f, 0.02f, 0.015f), _paint);
                }
            }
        }

        /// <summary>
        /// The hull's walls fitted out where they are (the user, 2026-11-17: "its still very weird in the vehicle"; the RHINO's walls
        /// stand 1.2 m from the eyes, the APC's 1.45, and the cabin's own parts all sit within half a metre: a small set of controls
        /// floating in an empty painted shell). On each side wall within 0.7-1.6 m: a cable trunk down it, two electrical boxes with
        /// a lamp each, a pipe run along it; under the feet a dark tread-plate floor; over a tracked vehicle's gunner the turret ring.
        /// Each where the measured hull has it, none where it would stand in the air or in the cabin's own parts.
        /// </summary>
        private void WallGear(float roomLeft, float roomRight, float floor, bool breech, bool rack, bool turretRing)
        {
            foreach (float side in new[] { -1f, 1f })
            {
                float room = side < 0f ? roomLeft : roomRight;
                // (The breech reaches 0.70 m to the left: the wall gear only beyond it.)
                // (And the rack to 0.57 m on the right, 0.77 beside a steering wheel: the boxes ran into its shells.)
                float least = side < 0f ? (breech ? 0.85f : 0.7f) : rack ? (SteersByWheel(_seat) ? 1.0f : 0.8f) : 0.7f;
                // (1.5 is Room's "nothing met": no wall there, and the gear stood in open air; scout.)
                if (room < least || room > 1.49f)
                {
                    continue;
                }
                float x = side * (room - 0.08f);
                Box("wall-trunk", new Vector3(x, -0.25f, 0.35f), new Vector3(0.06f, 0.9f, 0.06f), _metal);
                for (int k = 0; k < 2; k++)
                {
                    float z = -0.05f - k * 0.3f;
                    Box("wall-box", new Vector3(x - side * 0.04f, 0.02f, z), new Vector3(0.1f, 0.18f, 0.24f), _olive != null ? _olive : _paint);
                    Box("wall-box-lid", new Vector3(x - side * 0.095f, 0.02f, z), new Vector3(0.01f, 0.15f, 0.2f), _metal);
                    Part(PrimitiveType.Sphere, "wall-box-lamp", new Vector3(x - side * 0.1f, 0.08f, z + 0.07f), Vector3.one * 0.014f, Quaternion.identity, k == 0 ? _ready : _lamp);
                }
                Cylinder("wall-pipe", new Vector3(x - side * 0.03f, 0.3f, -0.1f), new Vector3(0.035f, 0.55f, 0.035f), Quaternion.Euler(90f, 0f, 0f), _metal);
                Cylinder("wall-pipe-clamp", new Vector3(x - side * 0.03f, 0.3f, 0.25f), new Vector3(0.05f, 0.015f, 0.05f), Quaternion.Euler(90f, 0f, 0f), _paint);
                Cylinder("wall-pipe-clamp", new Vector3(x - side * 0.03f, 0.3f, -0.45f), new Vector3(0.05f, 0.015f, 0.05f), Quaternion.Euler(90f, 0f, 0f), _paint);
            }
            if (floor > -1.49f && floor < -0.4f) // (-1.5: no floor met)
            {
                var tread = new Material(Shader.Find("Standard")) { color = new Color(0.16f, 0.16f, 0.15f), mainTexture = InteriorLook.Grime() };
                tread.mainTextureScale = new Vector2(4f, 4f);
                tread.SetFloat("_Metallic", 0.55f);
                tread.SetFloat("_Glossiness", 0.3f);
                _extra.Add(tread);
                float w = Mathf.Clamp(Mathf.Min(roomLeft, roomRight) * 2f - 0.1f, 0.6f, 1.6f);
                Box("floor-plate", new Vector3(0f, floor + 0.01f, 0.05f), new Vector3(w, 0.02f, 1.1f), tread);
                // (Its tread: raised bars across the plate.)
                for (int k = 0; k < 7; k++)
                {
                    Box("floor-tread", new Vector3(0f, floor + 0.025f, -0.45f + k * 0.16f), new Vector3(w - 0.06f, 0.008f, 0.025f), tread);
                }
            }
            if (turretRing && Mathf.Min(roomLeft, roomRight) >= 0.78f) // (a narrower hull: the ring at its least 0.6 m ran through the wall; scout)
            {
                float r = Mathf.Clamp(Mathf.Min(roomLeft, roomRight) - 0.18f, 0.6f, 1.0f); // (in from the wall gear: at 0.12 it ran through the wall pipes)
                for (int k = 0; k < 16; k++)
                {
                    float a = k * 22.5f;
                    Vector3 at = Quaternion.Euler(0f, a, 0f) * new Vector3(0f, 0.4f, r); // (over the pipes at 0.3)
                    Transform seg = Box("turret-ring", at, new Vector3(2f * r * Mathf.Tan(11.25f * Mathf.Deg2Rad) + 0.01f, 0.05f, 0.06f), _metal);
                    seg.localRotation = Quaternion.Euler(0f, a, 0f);
                }
            }
        }
        private ParticleSystem _smoke, _sparks;

        /// <summary>For the vehicle check: the smoke shown at full health (its vehicles cannot be hurt), and a hit's sparks.</summary>
        internal static bool TestSmoke;

        internal void TestHit() => Sparks(30);
        private Material _puffMat;
        private static Texture2D _puff;

        /// <summary>A soft round particle (made once): the game's particle shaders may not be in the build, Sprites/Default is.</summary>
        private static Texture2D Puff()
        {
            if (_puff == null)
            {
                const int n = 32;
                _puff = new Texture2D(n, n, TextureFormat.RGBA32, false) { name = "RavenfieldVR puff", wrapMode = TextureWrapMode.Clamp };
                var px = new Color[n * n];
                for (int y = 0; y < n; y++)
                {
                    for (int x = 0; x < n; x++)
                    {
                        float dx = (x + 0.5f) / n * 2f - 1f, dy = (y + 0.5f) / n * 2f - 1f;
                        float a = Mathf.Clamp01(1f - Mathf.Sqrt(dx * dx + dy * dy));
                        px[y * n + x] = new Color(1f, 1f, 1f, a * a);
                    }
                }
                _puff.SetPixels(px);
                _puff.Apply();
                Object.DontDestroyOnLoad(_puff);
            }
            return _puff;
        }

        /// <summary>A particle system in the cabin (its frame, so it rides with the vehicle), drawn with the soft puff.</summary>
        private ParticleSystem Particles(string name, Vector3 at, int most)
        {
            var go = new GameObject(name);
            go.transform.SetParent(_root.transform, false);
            go.transform.localPosition = at;
            ParticleSystem ps = go.AddComponent<ParticleSystem>();
            ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            ParticleSystem.MainModule main = ps.main;
            main.playOnAwake = false;
            main.loop = true;
            main.maxParticles = most;
            main.simulationSpace = ParticleSystemSimulationSpace.Local;
            if (_puffMat == null)
            {
                _puffMat = new Material(Shader.Find("Sprites/Default")) { mainTexture = Puff() };
            }
            ParticleSystemRenderer r = go.GetComponent<ParticleSystemRenderer>();
            r.sharedMaterial = _puffMat;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
            return ps;
        }

        /// <summary>A hit's sparks: a burst from a wall beside or before the head, falling, gone in half a second.</summary>
        private void Sparks(int count)
        {
            if (_root == null)
            {
                return;
            }
            if (_sparks == null)
            {
                _sparks = Particles("sparks", Vector3.zero, 80);
                ParticleSystem.MainModule main = _sparks.main;
                main.loop = false;
                main.startLifetime = new ParticleSystem.MinMaxCurve(0.25f, 0.55f);
                main.startSpeed = new ParticleSystem.MinMaxCurve(0.8f, 2.2f);
                main.startSize = new ParticleSystem.MinMaxCurve(0.008f, 0.02f);
                main.startColor = new ParticleSystem.MinMaxGradient(new Color(1f, 0.85f, 0.4f), new Color(1f, 0.45f, 0.1f));
                main.gravityModifier = 1.2f;
                ParticleSystem.EmissionModule em = _sparks.emission;
                em.rateOverTime = 0f;
                ParticleSystem.ShapeModule shape = _sparks.shape;
                shape.enabled = true;
                shape.shapeType = ParticleSystemShapeType.Cone;
                shape.angle = 35f;
                shape.radius = 0.02f;
            }
            // (From one of the walls round the head, spraying inward.)
            float side = Random.value < 0.5f ? -1f : 1f;
            Vector3 from = new Vector3(side * 0.45f, Random.Range(-0.1f, 0.25f), Random.Range(-0.1f, 0.3f));
            _sparks.transform.localPosition = from;
            _sparks.transform.localRotation = Quaternion.LookRotation(new Vector3(-side, 0.2f, 0.1f));
            _sparks.Emit(count);
        }

        private void SetCombatLight(bool on)
        {
            _combatLight = on;
            if (_cabin != null)
            {
                _cabin.color = on ? CombatLight : DayLight;
                _cabin.intensity = on ? 0.7f : 1.1f;
            }
            if (_lightSwitch != null)
            {
                // (Tipped up or down on its plate: which way it is set reads at a glance.)
                _lightSwitch.localRotation = Quaternion.Euler(on ? -35f : 35f, 0f, 0f);
            }
            _lampColor = on ? new Color(0.9f, 0.08f, 0.04f) : new Color(1.2f, 0.95f, 0.6f);
            if (_lamp != null && (_alarm == null || !_alarm.enabled))
            {
                Emit(_lamp, _lampColor);
            }
        }

        /// <summary>
        /// Loading by hand shown where to: a pulsing glow on the next shell in the rack while the gun waits for one, on the breech's
        /// mouth while a shell is carried. Nothing otherwise.
        /// </summary>
        private void LoadHint()
        {
            Vector3 at = _carried != null ? BreechMouth : ReadyState == "load" ? FirstShell : Vector3.zero;
            bool show = at != Vector3.zero;
            if (_hint == null)
            {
                if (!show)
                {
                    return;
                }
                _hintMat = new Material(Shader.Find("Sprites/Default")) { renderQueue = 3100 };
                _hint = Part(PrimitiveType.Sphere, "load hint", Vector3.zero, Vector3.one * 0.06f, Quaternion.identity, _hintMat);
            }
            if (_hint.gameObject.activeSelf != show)
            {
                _hint.gameObject.SetActive(show);
            }
            if (show)
            {
                float k = 0.5f + 0.5f * Mathf.Sin(Time.unscaledTime * Mathf.PI * 2f);
                // (7 cm toward the eyes: at the shell's or the block's middle it was inside them, hidden.)
                _hint.position = at + (_root.transform.position - at).normalized * 0.07f;
                _hint.localScale = Vector3.one * (0.05f + 0.025f * k);
                _hintMat.color = new Color(0.55f, 0.85f, 1f, 0.15f + 0.35f * k);
            }
        }
        private readonly Transform[] _needles = new Transform[5];

        /// <summary>
        /// The gauges read the vehicle: speed (0-25 m/s), engine (its throttle, else its speed), hull (its health); a gun seat's two
        /// more read the gun: the turret's way against the hull's (the needle up: straight ahead, the whole round), its elevation (-30 to
        /// 60 degrees).
        /// </summary>
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
            Weapon gun = Gun(_seat);
            Vector3 aim = gun != null ? v.transform.InverseTransformDirection(TurretGrip.AimReference(gun).forward) : Vector3.forward;
            for (int g = 0; g < _needles.Length; g++)
            {
                float reading = g == 0 ? speed : g == 1 ? engine : g == 2 ? hull : g == 4 ? Mathf.Clamp01((Elevation(aim) + 30f) / 90f) : 0f;
                if (_needles[g] != null)
                {
                    // From 120 degrees left (nothing) to 120 right (full), eased so it swings, not jumps; the turret's dial the whole
                    // round, clockwise for a turret turned right.
                    Quaternion want = g == 3 ? Quaternion.Euler(0f, 0f, -Azimuth(aim)) : Quaternion.Euler(0f, 0f, 120f - 240f * reading);
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

        /// <summary>A direction in the vehicle's frame: its way round from the nose (degrees, right positive). Pure (offline tests).</summary>
        internal static float Azimuth(Vector3 local) => Mathf.Atan2(local.x, local.z) * Mathf.Rad2Deg;

        /// <summary>A direction in the vehicle's frame: its angle over level (degrees, up positive). Pure (offline tests).</summary>
        internal static float Elevation(Vector3 local) => local.sqrMagnitude < 1e-8f ? 0f : Mathf.Asin(Mathf.Clamp(local.normalized.y, -1f, 1f)) * Mathf.Rad2Deg;

        /// <summary>
        /// A tank driver's two steering levers, one a track (pushed forward: that track forward): both forward drives ahead, one alone
        /// turns toward the other side, opposite ways turns on the spot. Into the drive input: x steer right, y forward. Pure (offline tests).
        /// </summary>
        internal static Vector2 TillerDrive(float left, float right)
        {
            left = Mathf.Clamp(left, -1f, 1f);
            right = Mathf.Clamp(right, -1f, 1f);
            return new Vector2((left - right) * 0.5f, (left + right) * 0.5f);
        }

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
            if (_root != null && !VRConfig.SharperCabin.Value)
            {
                GraphicsPresets.InCabin(false); // (switched off in the cabin: at once; on again: the next cabin)
            }
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
                _hatchFrames = 0; // (the seat left at the first of the hatch's three frames: the rest threw him out of the next seat)
                _nextLook = Time.unscaledTime + 0.3f;
                _lookUntil = Time.unscaledTime + 3f;
            }
            // (Looked at for the first 3 s in the seat: the eyes have settled by then, and an open seat walked the vehicle's parts twice a
            // second for as long as it was sat in.)
            else if (!_wanted && Time.unscaledTime >= _nextLook && Time.unscaledTime < _lookUntil)
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
            if (_root != null && _builtNeutral != VRRig.NeutralVersion && ReferenceEquals(seat, _seat) && _seat != null)
            {
                // The seat's neutral pose taken again (the player settled, VRRig.SettleInSeat): the cabin built again round it.
                Seat same = _seat;
                Clear();
                _seat = same;
                Build();
            }
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
                if (_block != null)
                {
                    _block.enabled = false; // (nor the driver's block)
                }
                return;
            }
            if (_scope != null && !_scope.enabled)
            {
                _scope.enabled = true; // (every frame: at every other one its picture stepped at half the headset's rate; scout)
            }
            if (_root != null)
            {
                Hands();
                Gauges();
                Rounds();
                GunState();
                LoadHint();
                HitFlicker();
                Breech();
                Feed();
                AimMark();
            }
            Aim();
            PointScope();
        }

        /// <summary>
        /// The periscope looks where the gun points (the APC's view sits on the seat, and its periscope stayed with the hull while the
        /// turret turned: the user, 2026-11-15, "the view doesnt move but the turret does"), the world's up kept up (its picture came out
        /// upside down there). With no gun, along the cabin's forward.
        /// </summary>
        private Camera _block;
        private RenderTexture _blockTexture;
        private Material _blockScreen;

        /// <summary>For the vehicle check: the driver's vision block is there.</summary>
        internal bool HasDriverBlock => _block != null;

        /// <summary>
        /// A seat that both drives and aims (the RHINO's one seat) gets a driver's vision block left of the readout: a small screen
        /// looking along the hull's forward, its picture starting past the hull's front (the periscope follows the gun: with the
        /// turret turned the driver drove blind). 384 px, a quarter of the periscope's cost.
        /// </summary>
        private void DriverBlock(Vector3 at, Vector2 size)
        {
            _blockTexture = new RenderTexture(384, Mathf.RoundToInt(384 * size.y / size.x), 16) { name = "RavenfieldVR driver block", filterMode = FilterMode.Trilinear, useMipMap = true, autoGenerateMips = true, anisoLevel = 4 };
            var go = new GameObject("driver block camera");
            go.transform.SetParent(_root.transform, false);
            _block = go.AddComponent<Camera>();
            Camera eye = _rig.Camera;
            _block.CopyFrom(eye);
            _block.stereoTargetEye = StereoTargetEyeMask.None;
            _block.targetTexture = _blockTexture;
            _block.ResetAspect(); // (its picture's own shape: CopyFrom may carry the eyes' aspect, and a stretched picture's aim mark lies)
            _block.fieldOfView = 30f;
            _block.depth = eye.depth - 1;
            Lighter(_block, 800f);
            _rig.Suppressor?.Ignore(_block);
            // (Its near plane past the hull's box along the hull's forward from the eyes, as the periscope's was.)
            Bounds b = new Bounds(_seat.vehicle.transform.position, Vector3.zero);
            foreach (Renderer r in _seat.vehicle.GetComponentsInChildren<Renderer>())
            {
                if (r != null && r.GetComponentInParent<Actor>() == null && r.GetComponentInParent<Weapon>() == null)
                {
                    b.Encapsulate(r.bounds);
                }
            }
            // (Not the guns: a barrel laid forward stretched the box past its muzzle, and the block showed nothing nearer; scout.)
            Vector3 eyeAt = _root.transform.position, fwd = _seat.vehicle.transform.forward;
            float near = 1.2f;
            for (float d = 0.3f; d < 12f; d += 0.1f)
            {
                if (!b.Contains(eyeAt + fwd * d))
                {
                    near = Mathf.Max(d + 0.15f, 1.2f);
                    break;
                }
            }
            _block.nearClipPlane = near;
            Part(PrimitiveType.Cube, "driver-block-frame", at + new Vector3(0f, 0f, 0.006f), new Vector3(size.x + 0.02f, size.y + 0.02f, 0.01f), Quaternion.identity, _metal);
            Transform screen = Part(PrimitiveType.Cube, "driver block screen", at, new Vector3(size.x, size.y, 0.002f), Quaternion.identity, null);
            _blockScreen = new Material(Shader.Find("Unlit/Texture")) { mainTexture = _blockTexture };
            screen.GetComponent<Renderer>().sharedMaterial = _blockScreen;
            Label("DRIVER", at + new Vector3(0f, size.y * 0.5f + 0.014f, -0.002f), Quaternion.identity, 0.0011f, new Color(1f, 0.85f, 0.3f));
        }

        /// <summary>The vision block along the hull's forward, its up (it rides with the hull: a driver's block is fixed in it).</summary>
        private void PointBlock()
        {
            if (_block == null || _seat == null || _seat.vehicle == null)
            {
                return;
            }
            Transform v = _seat.vehicle.transform;
            _block.transform.SetPositionAndRotation(_root.transform.position, Quaternion.LookRotation(v.forward, v.up));
            // (Every third frame, and not behind a menu: a glance view, and each extra camera draws the whole scene again on the
            // CPU, which the user's play already spends most of a frame on: median 11.8 ms against the GPU's 3.5.)
            bool draw = !(GameContext.GameMenuOpen || GameManager.IsPaused()) && Time.frameCount % 3 == 0;
            if (_block.enabled != draw)
            {
                _block.enabled = draw;
            }
        }

        /// <summary>
        /// An extra camera of the cabin made cheaper on the CPU: no UI or first-person layers (they are the headset's), a nearer far
        /// plane, occlusion culling on. Each draws the whole scene again: the user's frames are CPU-bound.
        /// </summary>
        private static void Lighter(Camera c, float far)
        {
            c.cullingMask &= ~((1 << 5) | (1 << VRRig.FirstPersonLayer));
            // (Its own reach, under the preset's cap only: copied from the eyes it took their cabin reach, CabinFar, when built in a cabin
            // just left or rebuilt (a seat change, a settle), and the periscope saw nothing past the hull's 9 m.)
            c.farClipPlane = Mathf.Min(far, GraphicsPresets.FarClipCap);
            c.useOcclusionCulling = true;
        }

        private void PointScope()
        {
            PointBlock();
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
            if (gun != null)
            {
                // At the muzzle, 30 cm out, not the eyes: from there nothing of the vehicle stands in the picture (the user, 2026-11-15:
                // "some sights are still blocked with tank materials"; the barrel, its mantlet and the turret's parts were in front of
                // the eyes' camera along the gun).
                Transform muzzle = gun.CurrentMuzzle() != null ? gun.CurrentMuzzle() : TurretGrip.AimReference(gun);
                _scope.transform.position = muzzle.position + dir.normalized * 0.3f;
                if (_scope.nearClipPlane != 0.1f)
                {
                    _scope.nearClipPlane = 0.1f;
                }
            }
        }

        // ---- building ------------------------------------------------------------------------------------------------------

        private void Build()
        {
            Transform head = _rig.Head;
            _builtNeutral = VRRig.NeutralVersion;
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
            // A rubber brow pad over the viewer (as on a sight: the forehead rests on it) and rivets along the frame.
            var rubber = new Material(Shader.Find("Standard")) { color = new Color(0.06f, 0.06f, 0.06f) };
            rubber.SetFloat("_Glossiness", 0.15f);
            _extra.Add(rubber);
            Cylinder("brow-pad", new Vector3(0f, 0.152f, 0.33f), new Vector3(0.045f, 0.2f, 0.045f), Quaternion.Euler(0f, 0f, 90f), rubber);
            for (int k = 0; k < 7; k++)
            {
                float x = -0.27f + k * 0.09f;
                Part(PrimitiveType.Sphere, "rivet", new Vector3(x, 0.14f, 0.368f), Vector3.one * 0.011f, Quaternion.identity, _metal);
                Part(PrimitiveType.Sphere, "rivet", new Vector3(x, -0.14f, 0.368f), Vector3.one * 0.011f, Quaternion.identity, _metal);
            }
            Periscope(new Vector3(0f, 0f, 0.468f), new Vector2(0.56f, 0.2f));
            if (Gun(_seat) != null)
            {
                // A gun seat's periscope is its sight: a reticle on it and a zoom knob by it.
                Reticle(new Vector3(0f, 0f, 0.468f), new Vector2(0.56f, 0.2f));
                ZoomControl(new Vector3(0.35f, -0.05f, 0.4f));
            }
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
            // The channel closed behind the viewer: a plate wider and taller than it (every line from the eyes into it crosses the
            // plate), so no strip of it looks out past the hull (the eyes draw only to the hull's reach in here: CabinFar).
            Box("viewer-back", new Vector3(0f, 0f, 0.49f), new Vector3(0.6f, 0.44f, 0.02f), _metal);
            // Military fittings: a radio set on the left (two knobs, a dial window, a handset), the gauges over the viewer. (In a
            // cannon's seat the breech stands on the left, the radio behind the shoulder.)
            Weapon gunHere = Gun(_seat);
            // Fitted to this hull (mods' turrets go from roomy to tight): how far it reaches left and right at the hands' height and
            // down to the floor, and what does not fit is left out (a fitting through the wall showed from inside as cut off).
            float roomLeft = Room(new Vector3(0f, -0.15f, 0.2f), Vector3.left);
            float roomRight = Room(new Vector3(0f, -0.25f, 0.1f), Vector3.right);
            float floor = -Room(new Vector3(0f, 0f, 0.3f), Vector3.down);
            bool cannon = gunHere != null && gunHere.configuration != null && gunHere.configuration.ammo > 0 && gunHere.configuration.ammo <= 1; // (-1: an endless magazine)
            bool breech = cannon && roomLeft >= 0.73f;
            bool feed = gunHere != null && !cannon && gunHere.configuration != null && gunHere.configuration.ammo > 1 && roomLeft >= 0.58f;
            bool rack = gunHere != null && roomRight >= (SteersByWheel(_seat) ? 0.79f : 0.59f);
            Fit = $"room left {roomLeft:0.00} m, right {roomRight:0.00} m, floor {floor:0.00} m; {(breech ? "breech" : cannon ? "no room for the breech" : "no breech")}, {(rack ? "rack" : gunHere != null ? "no room for the rack" : "no rack")}{(roomLeft >= 0.55f ? ", radio" : ", no room for the radio")}";
            // (Behind the shoulder and up over the recoil guard's rails in a cannon's seat: at their height they ran through it.)
            Vector3 rz = breech || feed ? new Vector3(0f, 0.24f, -0.3f) : Vector3.zero;
            if (roomLeft >= 0.55f)
            {
                Box("radio", rz + new Vector3(-0.46f, -0.12f, 0.12f), new Vector3(0.12f, 0.22f, 0.3f), _paint);
                Cylinder("radio-knob-1", rz + new Vector3(-0.395f, -0.06f, 0.18f), new Vector3(0.035f, 0.01f, 0.035f), Quaternion.Euler(0f, 0f, 90f), _metal);
                Cylinder("radio-knob-2", rz + new Vector3(-0.395f, -0.06f, 0.06f), new Vector3(0.035f, 0.01f, 0.035f), Quaternion.Euler(0f, 0f, 90f), _metal);
                Box("radio-dial", rz + new Vector3(-0.398f, -0.16f, 0.12f), new Vector3(0.005f, 0.05f, 0.16f), _lamp);
                Box("radio-handset", rz + new Vector3(-0.42f, 0.03f, 0.2f), new Vector3(0.04f, 0.035f, 0.17f), _metal);
            }
            // A grab handle overhead (the hatch's) and a cable run along the right wall: the inside reads as fitted out, not bare.
            // (The hatch handle is the way out: gripped and pulled down 8 cm, the seat is left, as the trigger held for a second does.)
            _hatch = new GameObject("hatch").transform;
            _hatch.SetParent(_root.transform, false);
            _hatchAt = new Vector3(0f, roof + 0.02f, 0.05f);
            _hatch.localPosition = _hatchAt;
            foreach (Transform t in new[]
            {
                Box("hatch-handle", _hatchAt, new Vector3(0.22f, 0.02f, 0.02f), _guard),
                Box("hatch-handle-l", _hatchAt + new Vector3(-0.1f, 0.03f, 0f), new Vector3(0.02f, 0.06f, 0.02f), _metal),
                Box("hatch-handle-r", _hatchAt + new Vector3(0.1f, 0.03f, 0f), new Vector3(0.02f, 0.06f, 0.02f), _metal),
            })
            {
                t.SetParent(_hatch, true);
            }
            Label("HATCH", _hatchAt + new Vector3(0f, 0.065f, 0f), Quaternion.Euler(-90f, 0f, 0f), 0.0014f, new Color(1f, 0.85f, 0.3f));
            if (roomRight >= 0.48f)
            {
                Cylinder("cable-run", new Vector3(0.44f, 0.2f, 0.05f), new Vector3(0.025f, 0.3f, 0.025f), Quaternion.Euler(90f, 0f, 0f), _metal);
            }
            int dials = gunHere != null ? 5 : 3;
            for (int g = 0; g < dials; g++)
            {
                // (Speed, engine, hull in the middle; a gun seat's turret dial left of them, elevation right.)
                float x = g < 3 ? -0.12f + g * 0.12f : g == 3 ? -0.24f : 0.24f;
                Cylinder("gauge-" + g, new Vector3(x, 0.2f, 0.4f), new Vector3(0.075f, 0.012f, 0.075f), Quaternion.Euler(-80f, 0f, 0f), _metal);
                Cylinder("gauge-face-" + g, new Vector3(x, 0.2f, 0.393f), new Vector3(0.062f, 0.002f, 0.062f), Quaternion.Euler(-80f, 0f, 0f), _button);
                // (Its name over it: which dial reads what at a glance.)
                Label(g == 0 ? "SPEED" : g == 1 ? "ENGINE" : g == 2 ? "HULL" : g == 3 ? "TURRET" : "ELEV", new Vector3(x, 0.248f, 0.392f), Quaternion.Euler(-10f, 0f, 0f), 0.0011f, new Color(1f, 0.85f, 0.3f));
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
                if (g == 3)
                {
                    // The turret dial's hull: a little block in the middle pointing at the nose.
                    Transform hullMark = Box("gauge-hull", Vector3.zero, new Vector3(0.012f, 0.022f, 0.002f), _metal);
                    hullMark.SetParent(pivot, false);
                    hullMark.localPosition = new Vector3(0f, 0f, -0.001f);
                    hullMark.localRotation = Quaternion.identity;
                }
            }
            if (rack)
            {
                AmmoRack(); // (none: no shells to show nor to load by hand, the gun reloads the game's way)
            }
            // A dim warm light inside: the hull's inner walls face away from the sun and were black.
            var lamp = new GameObject("cabin light");
            lamp.transform.SetParent(_root.transform, false);
            lamp.transform.localPosition = new Vector3(0f, 0.25f, 0f);
            Light cabin = _cabin = lamp.AddComponent<Light>();
            cabin.type = LightType.Point;
            cabin.color = new Color(1f, 0.86f, 0.62f);
            cabin.range = 3f;
            cabin.intensity = 1.1f;
            LightSwitchAt(new Vector3(-0.36f, 0.2f, 0.38f));
            Stowage(roomLeft, roomRight, floor);
            WallGear(roomLeft, roomRight, floor, breech, rack, gunHere != null && Tracked(_seat.vehicle));
            float ceiling = Room(new Vector3(0f, 0f, 0.05f), Vector3.up);
            RoofGear(ceiling, roomLeft, roomRight);
            SeatPad(floor);
            Fit += $", roof {(ceiling > 1.49f ? "none met" : ceiling.ToString("0.00") + " m")}";
            // Grab handles on the side walls at eye height (over the rack and the radio, which ran through them lower), where the hull leaves room (to hold on to over rough ground).
            foreach (float side in new[] { -1f, 1f })
            {
                float room = side < 0f ? roomLeft : roomRight;
                if (room < 0.5f || room > 1.2f)
                {
                    continue; // (no wall near: a handle in the air reads wrong)
                }
                float x = side * (room - 0.04f);
                Box("wall-handle", new Vector3(x, 0.2f, 0.19f), new Vector3(0.02f, 0.02f, 0.18f), _metal);
                Box("wall-handle-a", new Vector3(x + side * 0.02f, 0.2f, 0.11f), new Vector3(0.04f, 0.02f, 0.02f), _metal);
                Box("wall-handle-b", new Vector3(x + side * 0.02f, 0.2f, 0.27f), new Vector3(0.04f, 0.02f, 0.02f), _metal);
            }
            _lastHealth = -1f;
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
                // (Named under each, tipped toward the eyes as the wheels are: which turns what reads at a glance.)
                Quaternion tipped = Quaternion.Euler(30f, 0f, 0f);
                Label("TRAVERSE", new Vector3(-apart, -0.505f, 0.32f), tipped, 0.0014f, new Color(1f, 0.85f, 0.3f));
                Label("ELEVATION", new Vector3(apart, -0.505f, 0.32f), tipped, 0.0014f, new Color(1f, 0.85f, 0.3f));
                // The fire button right by the hands (the user, 2026-11-15: "bring the red button a little bit nearer to the hands"; it sat
                // 20 cm over the right wheel): at the wheels' height beside the elevation wheel's inner rim, a palm's width from it; with
                // the steering wheel between the handwheels, outside the elevation wheel instead.
                FireControl(SteersByWheel(_seat) ? new Vector3(apart + 0.21f, -0.3f, 0.31f) : new Vector3(apart - 0.21f, -0.3f, 0.31f));
                AmmoReadout();
                RangeControl(new Vector3(0.15f, -0.205f, 0.405f));
                if (_seat.IsDriverSeat())
                {
                    DriverBlock(new Vector3(-0.2f, -0.205f, 0.41f), new Vector2(0.16f, 0.07f));
                }
                if (breech)
                {
                    BuildBreech();
                    _cannon = gunHere;
                }
                else if (feed && rack)
                {
                    BuildFeed();
                    _cannon = gunHere;
                }
            }
            if (SteersByWheel(_seat))
            {
                // (High enough that the drive panel's forward button is out of its rim's grip: a fist on it took the wheel.)
                _steer = Wheel("steering", new Vector3(0f, -0.3f, 0.33f));
            }
            if (_seat.IsDriverSeat() && Tracked(_seat.vehicle))
            {
                // A tank steers by its tracks: a lever for each, either side below the handwheels (the buttons stay too).
                // (On the floor: from it, 30 cm up to the knob.)
                float pivotY = Mathf.Clamp(floor, -0.85f, -0.45f);
                _levers[0] = Lever("lever-left", new Vector3(-0.42f, pivotY, 0.28f));
                _levers[1] = Lever("lever-right", new Vector3(0.42f, pivotY, 0.28f));
            }
            else if (SteersByWheel(_seat) && Gun(_seat) == null)
            {
                // A wheeled one's driver steers by the wheel: a throttle lever to its right (forward: gas, back: reverse), so both hands
                // drive (no handwheels there: the driver has no gun).
                _levers[1] = Lever("throttle", new Vector3(0.3f, Mathf.Clamp(floor, -0.85f, -0.45f), 0.28f));
                _throttleOnly = true;
            }
            if (_seat.IsDriverSeat())
            {
                // Low in the middle, in reach of either hand: forward above, back below, left and right either side.
                // (Over the floor of a low hull: under it, the panel was not seen and its buttons pressed blind.)
                float panelY = Mathf.Clamp(floor + 0.17f, -0.62f, -0.55f);
                // (26 cm wide: between the handwheels' rims; 36 ran its corners into them over a high floor.)
                Box("panel", new Vector3(0f, panelY, 0.36f), new Vector3(0.26f, 0.3f, 0.04f), _paint).localRotation = Quaternion.Euler(-35f, 0f, 0f);
                Vector3 c = new Vector3(0f, panelY, 0.33f);
                _driveButtons.Add((Button("forward", c + new Vector3(0f, 0.08f, -0.04f)), new Vector2(0f, 1f)));
                _driveButtons.Add((Button("back", c + new Vector3(0f, -0.08f, 0.04f)), new Vector2(0f, -1f)));
                _driveButtons.Add((Button("left", c + new Vector3(-0.1f, 0f, 0f)), new Vector2(-1f, 0f)));
                _driveButtons.Add((Button("right", c + new Vector3(0.1f, 0f, 0f)), new Vector2(1f, 0f)));
                if (Gun(_seat) == null)
                {
                    AmmoReadout(); // (a driver with no gun: the readout shows the speed)
                }
            }
            Crossings = Crossing();
            GraphicsPresets.InCabin(true);
            CabinFar = FarthestOfHull();
            int merged = _mergedParts = MergeFixed();
            Log.InfoOnce("vehicle-interior:" + _seat.vehicle.name, $"Vehicle inside for {_seat.vehicle.name} ({merged} fixed part(s) drawn as {_merged.Count} mesh(es); parts crossing: {Crossings}; {(Gun(_seat) != null ? "handwheels, fire control, ammunition readout" : "no gun")}{(_seat.IsDriverSeat() ? ", drive buttons" : "")}{(_levers[0] != null ? ", steering levers" : _throttleOnly ? ", throttle lever" : "")}; {Fit}; {cleared} small part(s) cleared from before the eyes).");
        }

        /// <summary>For the vehicle check and the log: the hull's room round the seat and what was fitted in it.</summary>
        internal string Fit { get; private set; } = "none";

        private readonly List<Mesh> _merged = new List<Mesh>();

        /// <summary>
        /// The eyes' far plane while a closed cabin is shown (infinity otherwise): just past the vehicle's farthest point. Inside the
        /// solid hull nothing of the world outside is seen (the periscope's and the driver's block's own cameras draw it), yet both eyes
        /// drew all of it, every tree, house and soldier twice over: the user's vehicle minutes were bound by the draws (63-66% of
        /// frames on time, GPU idle). VRRig.CopyCameraSettings takes the least of this and its other limits each frame.
        /// </summary>
        internal static float CabinFar = float.PositiveInfinity;

        /// <summary>The rig's neutral pose version the cabin was built round (VRRig.NeutralVersion).</summary>
        private int _builtNeutral = -1;

        /// <summary>
        /// A see-through part of the hull within 4 m of the eyes and over 20 cm across (glass: drawn after the opaque queue), not a
        /// particle or additive glow (muzzle flashes, dust quads).
        /// </summary>
        private static bool Window(Renderer r, Vector3 eyes)
        {
            if (r.bounds.size.magnitude < 0.2f || r.bounds.SqrDistance(eyes) > 16f)
            {
                return false;
            }
            foreach (Material m in r.sharedMaterials)
            {
                string sh = m != null && m.shader != null ? m.shader.name : "";
                if (m != null && m.renderQueue >= 2450 && m.renderQueue < 4000 && sh.IndexOf("Particle", System.StringComparison.OrdinalIgnoreCase) < 0
                    && sh.IndexOf("Additive", System.StringComparison.OrdinalIgnoreCase) < 0)
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>The farthest corner of the vehicle's drawn parts from the eyes, and 1.5 m more (at least 6 m).</summary>
        private float FarthestOfHull()
        {
            Vector3 eyes = _root.transform.position;
            float far = 0f;
            foreach (Renderer r in _seat.vehicle.GetComponentsInChildren<Renderer>())
            {
                if (r == null || r is ParticleSystemRenderer || r.GetComponentInParent<Actor>() != null)
                {
                    continue;
                }
                if (r.enabled && r.gameObject.activeInHierarchy && Window(r, eyes))
                {
                    // (A window near the eyes, a mod armoured car's windscreen: the world is seen through it, drawn as before.)
                    Log.InfoOnce("cabin-far-window:" + _seat.vehicle.name, $"Closed cabin in {_seat.vehicle.name}: a window ({r.name}) near the eyes, the world drawn as far as before.");
                    return float.PositiveInfinity;
                }
                Bounds b = r.bounds;
                if (b.size.magnitude > 60f)
                {
                    continue; // (a decal drawer or a stray manager parented in: not the hull)
                }
                for (int c = 0; c < 8; c++)
                {
                    Vector3 corner = b.center + Vector3.Scale(b.extents, new Vector3((c & 1) == 0 ? -1 : 1, (c & 2) == 0 ? -1 : 1, (c & 4) == 0 ? -1 : 1));
                    far = Mathf.Max(far, (corner - eyes).magnitude);
                }
            }
            float cabinFar = Mathf.Max(6f, far + 1.5f);
            Log.InfoOnce("cabin-far:" + _seat.vehicle.name, $"Closed cabin in {_seat.vehicle.name}: the eyes draw out to {cabinFar:0.0} m (the hull's farthest point {far:0.0} m); the world outside is seen through the periscope.");
            return cabinFar;
        }

        /// <summary>For the log and the vehicle check: cabin parts of different fittings running into each other ("none" when clear).</summary>
        internal string Crossings { get; private set; } = "none";

        /// <summary>
        /// Cabin parts of different fittings (their names' first word: "wall", "seat", "radio"...) whose boxes in the cabin's frame
        /// overlap by more than a third of the smaller one: a fitting through another, which read as broken from the seat. Taken
        /// before the merge, once a cabin; the five largest are said. The fittings are placed by measured room, and no game run checks
        /// each hull's layout: this does, in every seat taken.
        /// </summary>
        private static readonly char[] Dashes = { '-', ' ' };

        private string Crossing()
        {
            var boxes = new List<(string family, string name, Bounds box)>();
            Matrix4x4 toRoot = _root.transform.worldToLocalMatrix;
            foreach (MeshRenderer r in _root.GetComponentsInChildren<MeshRenderer>())
            {
                MeshFilter f = r.GetComponent<MeshFilter>();
                if (f == null || f.sharedMesh == null || r.GetComponent<TextMesh>() != null)
                {
                    continue;
                }
                Bounds local = f.sharedMesh.bounds;
                Matrix4x4 m = toRoot * r.transform.localToWorldMatrix;
                var b = new Bounds(m.MultiplyPoint3x4(local.center), Vector3.zero);
                for (int c = 0; c < 8; c++)
                {
                    Vector3 corner = local.center + Vector3.Scale(local.extents, new Vector3((c & 1) == 0 ? -1 : 1, (c & 2) == 0 ? -1 : 1, (c & 4) == 0 ? -1 : 1));
                    b.Encapsulate(m.MultiplyPoint3x4(corner));
                }
                string n = r.name;
                int dash = n.IndexOfAny(Dashes);
                string family = dash > 0 ? n.Substring(0, dash) : n;
                // (Fittings made of parts that touch by design though named apart: the brow pad on the viewer, the rack's straps round
                // its shells and boxes.)
                family = family == "brow" ? "viewer" : family == "shell" || family == "ammo" ? "rack" : family;
                boxes.Add((family, n, b));
            }
            var found = new List<(float share, string text)>();
            for (int i = 0; i < boxes.Count; i++)
            {
                for (int j = i + 1; j < boxes.Count; j++)
                {
                    if (boxes[i].family == boxes[j].family || !boxes[i].box.Intersects(boxes[j].box))
                    {
                        continue;
                    }
                    Vector3 lo = Vector3.Max(boxes[i].box.min, boxes[j].box.min), hi = Vector3.Min(boxes[i].box.max, boxes[j].box.max);
                    Vector3 d = hi - lo;
                    float shared = Mathf.Max(d.x, 0f) * Mathf.Max(d.y, 0f) * Mathf.Max(d.z, 0f);
                    Vector3 si = boxes[i].box.size, sj = boxes[j].box.size;
                    float smaller = Mathf.Min(si.x * si.y * si.z, sj.x * sj.y * sj.z);
                    // (Both at least a 4 cm cube's size: rivets, buttons, dials and lamps sit in their frames by design.)
                    if (smaller > 6.4e-5f && shared / smaller > 0.33f)
                    {
                        found.Add((shared / smaller, $"{boxes[i].name} x {boxes[j].name} {100f * shared / smaller:0}%"));
                    }
                }
            }
            if (found.Count == 0)
            {
                return "none";
            }
            found.Sort((a, b) => b.share.CompareTo(a.share));
            var said = new List<string>();
            for (int k = 0; k < found.Count && k < 5; k++)
            {
                said.Add(found[k].text);
            }
            return $"{found.Count} ({string.Join("; ", said)})";
        }
        private int _mergedParts;

        /// <summary>For the seat's frame line: what the cabin draws (its parts, the merged ones, the extra cameras).</summary>
        internal string DrawSummary
        {
            get
            {
                if (_root == null)
                {
                    return "no cabin";
                }
                int parts = _root.GetComponentsInChildren<Renderer>().Length;
                return $"cabin {parts} renderer(s) ({_mergedParts} fixed parts in {_merged.Count} mesh(es)), periscope {(_scope != null ? (_scopeTexture != null ? _scopeTexture.width + " px" : "on") : "none")}, "
                       + $"driver block {(_block != null ? "on" : "none")}, eyes drawn out to {(float.IsInfinity(CabinFar) ? "the game's distance" : CabinFar.ToString("0.0") + " m")}";
            }
        }

        /// <summary>
        /// The cabin's fixed parts (rivets, wall gear, the tread plate, stowage, frames: a hundred and more small boxes, each its own
        /// draw, twice for the two eyes and again for the cabin's light) drawn as one mesh a material. The user's 1.19.5 minutes in the
        /// RHINO and the RAPTOR kept to the headset's rate in 63-66% of frames (on foot 89%), the GPU idle at 3.5 ms and the frame's
        /// render-thread waits 7.4 ms against 5.6 on foot: the draws are the cost ("its still very weird in the vehicle", 2026-11-17).
        /// Left as they are: every part that moves, sinks, lights or is taken (the fields hold them), the screens, the labels, parts
        /// under a pivot, hidden parts. The materials stay the same, so a light switched or a flash shows on the merged ones too.
        /// Returns how many parts went into the meshes.
        /// </summary>
        private int MergeFixed()
        {
            if (_root == null)
            {
                return 0;
            }
            var keep = new HashSet<Transform> { _fire, _fireReady, _breech, _breechBlock, _carried, _feedLid, _lightSwitch, _hint, _hatch, _aimMark, _rangeButton, _zoomKnob };
            foreach (Transform t in _levers)
            {
                keep.Add(t);
            }
            foreach (Transform t in _needles)
            {
                keep.Add(t);
            }
            foreach ((Transform t, Vector2 _) in _driveButtons)
            {
                keep.Add(t);
            }
            foreach (GameObject g in _rounds)
            {
                if (g != null)
                {
                    keep.Add(g.transform);
                }
            }
            // (Also inside what turns as one: a handwheel's rim bars and spokes, the breech's body, the hatch's handle.)
            int parts = MergeIn(_root.transform, keep);
            foreach (Transform whole in new[] { _traverse, _elevate, _steer, _breech, _hatch })
            {
                if (whole != null)
                {
                    parts += MergeIn(whole, keep);
                }
            }
            return parts;
        }

        /// <summary>The fixed leaf parts directly under <paramref name="root"/> as one mesh a material (see <see cref="MergeFixed"/>).</summary>
        private int MergeIn(Transform root, HashSet<Transform> keep)
        {
            Matrix4x4 toRoot = root.worldToLocalMatrix;
            var groups = new Dictionary<Material, List<MeshFilter>>();
            for (int i = 0; i < root.childCount; i++)
            {
                Transform t = root.GetChild(i);
                if (keep.Contains(t) || t.childCount > 0 || !t.gameObject.activeSelf || t.name.EndsWith(" screen") || t.GetComponent<TextMesh>() != null)
                {
                    continue;
                }
                MeshRenderer r = t.GetComponent<MeshRenderer>();
                MeshFilter f = t.GetComponent<MeshFilter>();
                Material m = r != null ? r.sharedMaterial : null;
                if (r == null || !r.enabled || f == null || f.sharedMesh == null || f.sharedMesh.subMeshCount != 1 || m == null)
                {
                    continue;
                }
                if (!groups.TryGetValue(m, out List<MeshFilter> list))
                {
                    groups[m] = list = new List<MeshFilter>();
                }
                list.Add(f);
            }
            int parts = 0;
            foreach (KeyValuePair<Material, List<MeshFilter>> g in groups)
            {
                if (g.Value.Count < 2)
                {
                    continue; // (one part: nothing saved)
                }
                var combine = new CombineInstance[g.Value.Count];
                for (int k = 0; k < combine.Length; k++)
                {
                    combine[k] = new CombineInstance { mesh = g.Value[k].sharedMesh, transform = toRoot * g.Value[k].transform.localToWorldMatrix };
                }
                var mesh = new Mesh { name = "RavenfieldVR cabin " + g.Key.name, indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
                mesh.CombineMeshes(combine, true, true);
                _merged.Add(mesh);
                var go = new GameObject("cabin fixed (" + g.Key.name + ")");
                go.transform.SetParent(root, false);
                go.AddComponent<MeshFilter>().sharedMesh = mesh;
                MeshRenderer mr = go.AddComponent<MeshRenderer>();
                mr.sharedMaterial = g.Key;
                mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                mr.receiveShadows = false;
                foreach (MeshFilter f in g.Value)
                {
                    f.gameObject.SetActive(false); // (this frame already: Destroy waits for its end)
                    Object.Destroy(f.gameObject);
                }
                parts += g.Value.Count;
            }
            return parts;
        }

        /// <summary>
        /// How far the hull reaches from a point in the cabin along a direction (metres, up to 1.5): its own colliders' outer surface,
        /// cast in from outside (a ray from inside a collider meets nothing), the outermost of them. Nothing in the way: 1.5.
        /// </summary>
        private float Room(Vector3 from, Vector3 dir)
        {
            const float max = 1.5f;
            Vector3 a = _root.transform.TransformPoint(from), d = _root.transform.TransformDirection(dir).normalized;
            var ray = new Ray(a + d * max, -d);
            float room = -1f;
            foreach (Collider c in _seat.vehicle.GetComponentsInChildren<Collider>())
            {
                if (c == null || !c.enabled || c.isTrigger || c is WheelCollider || c.GetComponentInParent<Actor>() != null)
                {
                    continue;
                }
                if (c.Raycast(ray, out RaycastHit hit, max))
                {
                    room = Mathf.Max(room, max - hit.distance);
                }
            }
            return room < 0f ? max : room;
        }

        /// <summary>
        /// The fire control: a box facing the eyes as the wheels do, a yellow guard ring, the red button on it, a ready lamp beside it
        /// (green loaded, amber blinking while reloading, red empty) and "FIRE" on its far edge.
        /// </summary>
        private void FireControl(Vector3 at)
        {
            Quaternion tilt = Quaternion.Euler(-30f, 0f, 0f);
            Part(PrimitiveType.Cube, "fire-box", at + tilt * new Vector3(0f, -0.032f, 0.012f), new Vector3(0.1f, 0.05f, 0.115f), tilt, _paint);
            Part(PrimitiveType.Cylinder, "fire-guard", at + tilt * new Vector3(0f, -0.004f, 0f), new Vector3(0.086f, 0.006f, 0.086f), tilt, _guard);
            _fire = Part(PrimitiveType.Cylinder, "fire", at + tilt * new Vector3(0f, 0.006f, 0f), new Vector3(0.058f, 0.014f, 0.058f), tilt, _red);
            _fireReady = Part(PrimitiveType.Sphere, "fire-ready", at + tilt * new Vector3(0.036f, 0f, 0.05f), new Vector3(0.016f, 0.016f, 0.016f), Quaternion.identity, _ready);
            Label("FIRE", at + tilt * new Vector3(-0.008f, -0.006f, 0.054f), Quaternion.LookRotation(tilt * Vector3.down, tilt * Vector3.forward), 0.0016f, new Color(1f, 0.85f, 0.3f));
        }

        private Transform _hatch;
        private Vector3 _hatchAt;
        private int _hatchHand = -1;
        private float _hatchGripY;
        private static int _hatchFrames;
        private const float HatchPull = 0.08f, HatchReach = 0.08f;

        /// <summary>For the vehicle check: the hatch handle (null: none).</summary>
        internal Transform HatchHandle => _hatch;

        /// <summary>
        /// The hatch handle pulled down far enough: the seat's leave key is pressed for three frames (the game's own leave, as the
        /// trigger held for a second; GameInputBridge reads this).
        /// </summary>
        internal static bool HatchPulled()
        {
            if (_hatchFrames <= 0)
            {
                return false;
            }
            _hatchFrames--;
            return true;
        }

        /// <summary>The hatch handle: a grip within reach of its bar takes it; it follows the hand down; 8 cm down opens the hatch.</summary>
        private void Hatch(VRHand hand, int h, Vector3 palm)
        {
            if (_hatch == null)
            {
                return;
            }
            Vector3 local = _root.transform.InverseTransformPoint(palm);
            if (_hatchHand < 0)
            {
                bool onBar = Mathf.Abs(local.x - _hatchAt.x) < 0.13f && Mathf.Abs(local.y - _hatchAt.y) < HatchReach && Mathf.Abs(local.z - _hatchAt.z) < HatchReach;
                if (onBar && hand.Role == HandRole.Free && VRInput.GetHeldUnclaimed(DigitalAction.Grab, hand.Side))
                {
                    _hatchHand = h;
                    _hatchGripY = local.y;
                    hand.Role = HandRole.Cockpit;
                    hand.Model.CockpitStick = true;
                    VRInput.Consume(DigitalAction.Grab, hand.Side);
                    hand.Haptic(0.03f, 120f, 0.4f);
                }
                return;
            }
            if (_hatchHand != h)
            {
                return;
            }
            if (!hand.GrabHeld)
            {
                ReleaseHatch();
                return;
            }
            float down = Mathf.Clamp(_hatchGripY - local.y, 0f, HatchPull + 0.02f);
            _hatch.localPosition = _hatchAt + new Vector3(0f, -down, 0f);
            if (down >= HatchPull)
            {
                hand.Haptic(0.08f, 60f, 1f); // (the hatch thrown open)
                _hatchFrames = 3;
                ReleaseHatch();
            }
        }

        private void ReleaseHatch()
        {
            if (_hatchHand >= 0)
            {
                VRHand hand = _rig.Hands[_hatchHand];
                if (hand != null)
                {
                    if (hand.Role == HandRole.Cockpit)
                    {
                        hand.Role = HandRole.Free;
                    }
                    hand.Model.CockpitStick = false;
                }
                _hatchHand = -1;
            }
            if (_hatch != null)
            {
                _hatch.localPosition = _hatchAt;
            }
        }

        private Transform _aimMark;
        private Material _markMat;
        private float _markAngle = float.NaN, _markUntil = -10f, _scopeHeight = 0.2f;

        /// <summary>For the vehicle check: the aim mark's drop below the sight's centre (metres on the screen; NaN: not shown).</summary>
        internal float AimMarkDrop => _aimMark != null && _aimMark.gameObject.activeSelf ? -_aimMark.localPosition.y : float.NaN;

        /// <summary>
        /// How far over the line of sight a gun must point (degrees) for its round, leaving at <paramref name="speed"/> m/s and falling
        /// at <paramref name="gravity"/> m/s^2 with no drag (as the game's projectiles fly), to come down at <paramref name="range"/> m
        /// on the level: half the arc sine of g R / v^2. NaN past the round's reach. Pure (offline tests).
        /// </summary>
        internal static float AimDrop(float range, float speed, float gravity)
        {
            if (speed <= 0f || range <= 0f)
            {
                return float.NaN;
            }
            float k = gravity * range / (speed * speed);
            return k > 1f ? float.NaN : 0.5f * Mathf.Asin(Mathf.Max(k, 0f)) * Mathf.Rad2Deg;
        }

        /// <summary>
        /// An angle under the sight's centre as a distance down a screen <paramref name="height"/> tall showing <paramref name="fov"/>
        /// degrees top to bottom (the periscope's camera). Pure (offline tests).
        /// </summary>
        internal static float ScreenDrop(float degrees, float fov, float height) =>
            Mathf.Tan(degrees * Mathf.Deg2Rad) / Mathf.Tan(fov * 0.5f * Mathf.Deg2Rad) * height * 0.5f;

        /// <summary>
        /// The aim mark after a range is taken: the target put on it, the gun points high enough for the round to come down there
        /// (it moves with the zoom). Red at the bottom edge past the round's reach. Ten seconds, or until the next range.
        /// </summary>
        private void AimMark()
        {
            if (_aimMark == null || _scope == null)
            {
                return;
            }
            bool show = Time.unscaledTime < _markUntil;
            if (_aimMark.gameObject.activeSelf != show)
            {
                _aimMark.gameObject.SetActive(show);
            }
            if (!show)
            {
                return;
            }
            float half = _scopeHeight * 0.5f - 0.004f;
            bool reach = !float.IsNaN(_markAngle);
            float drop = reach ? Mathf.Min(ScreenDrop(_markAngle, _scope.fieldOfView, _scopeHeight), half) : half;
            _aimMark.localPosition = new Vector3(0f, -drop, _aimMark.localPosition.z);
            _markMat.color = reach && drop < half ? new Color(0.3f, 1f, 0.35f) : new Color(1f, 0.15f, 0.1f);
        }

        private Transform _rangeButton;
        private string _rangeText;
        private float _rangeUntil = -10f;

        /// <summary>For the vehicle check: the last range taken (null: none), whatever the readout shows meanwhile.</summary>
        internal string RangeText => _rangeText;

        /// <summary>For the vehicle check: the rangefinder's button (null: none).</summary>
        internal Transform RangeButton => _rangeButton;

        /// <summary>The rangefinder's button right of the readout: pressed, the range along the gun's line shows on it for 4 s.</summary>
        private void RangeControl(Vector3 at)
        {
            Quaternion face = Quaternion.Euler(20f, 0f, 0f);
            Part(PrimitiveType.Cube, "range-mount", at + face * new Vector3(0f, 0f, 0.008f), new Vector3(0.045f, 0.045f, 0.012f), face, _metal);
            _rangeButton = Part(PrimitiveType.Cylinder, "range", at, new Vector3(0.03f, 0.008f, 0.03f), face * Quaternion.Euler(-90f, 0f, 0f), _button);
            Label("RANGE", at + new Vector3(0f, 0.033f, 0f), face, 0.0012f, new Color(1f, 0.85f, 0.3f));
        }

        /// <summary>
        /// The range along the gun's line: the nearest thing hit within 3 km that is not this vehicle nor a soldier seated in it
        /// ("RANGE ---" with nothing), shown on the readout for 4 s.
        /// </summary>
        private void MeasureRange()
        {
            Weapon gun = Gun(_seat);
            if (gun == null)
            {
                return;
            }
            Transform aim = TurretGrip.AimReference(gun);
            // (Every layer the game casts against: layer 12 is the vehicles', and with it left out, as the pilot readout's height
            // does, an enemy tank read as the ground behind it; scout. RaycastAll on a press: a full buffer dropped the nearest.)
            RaycastHit[] hits = Physics.RaycastAll(aim.position, aim.forward, 3000f, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
            float nearest = float.MaxValue;
            Actor me = GameContext.PlayerActor;
            foreach (RaycastHit hit in hits)
            {
                Collider c = hit.collider;
                if (c == null || hit.distance >= nearest)
                {
                    continue;
                }
                Vehicle hitVehicle = c.GetComponentInParent<Vehicle>();
                Actor hitActor = c.GetComponentInParent<Actor>();
                if (ReferenceEquals(hitVehicle, _seat.vehicle) || ReferenceEquals(hitActor, me)
                    || hitActor != null && hitActor.IsSeated() && ReferenceEquals(hitActor.seat.vehicle, _seat.vehicle))
                {
                    continue;
                }
                nearest = hit.distance;
            }
            _rangeText = nearest < float.MaxValue ? $"RANGE {Mathf.RoundToInt(nearest / 5f) * 5} m" : "RANGE ---";
            // The fire control: the gun's own round (Weapon.projectileSpeed and projectileGravity, the game's from its prefab).
            if (nearest < float.MaxValue && gun.projectileSpeed > 1f)
            {
                _markAngle = AimDrop(nearest, gun.projectileSpeed, gun.projectileGravity.magnitude);
                _markUntil = Time.unscaledTime + 10f;
                if (float.IsNaN(_markAngle))
                {
                    _rangeText += "  OUT"; // (past the round's reach)
                }
            }
            else
            {
                _markUntil = -10f;
            }
            _rangeUntil = Time.unscaledTime + 4f;
            _nextReadout = 0f; // (shown now, not at the readout's next tick)
        }

        /// <summary>The ammunition readout under the viewer: rounds in the gun | spare (a lit display, "RELOADING" while it reloads).</summary>
        private void AmmoReadout()
        {
            Quaternion face = Quaternion.Euler(20f, 0f, 0f);
            Vector3 at = new Vector3(0f, -0.2f, 0.41f);
            Part(PrimitiveType.Cube, "readout-back", at, new Vector3(0.2f, 0.045f, 0.012f), face, _metal);
            _ammoText = Label("", at + face * new Vector3(0f, 0f, -0.008f), face, 0.0022f, new Color(1f, 0.55f, 0.15f));
            _nextReadout = 0f;
        }

        private TextMesh Label(string text, Vector3 at, Quaternion turn, float size, Color colour)
        {
            var go = new GameObject("label " + text);
            go.transform.SetParent(_root.transform, false);
            go.transform.localPosition = at;
            go.transform.localRotation = turn;
            TextMesh t = go.AddComponent<TextMesh>();
            Font font = Resources.GetBuiltinResource<Font>("Arial.ttf");
            t.font = font;
            go.GetComponent<MeshRenderer>().sharedMaterial = font.material;
            t.anchor = TextAnchor.MiddleCenter;
            t.alignment = TextAlignment.Center;
            t.fontSize = 48;
            t.characterSize = size;
            t.fontStyle = FontStyle.Bold;
            t.color = colour;
            t.text = text;
            return t;
        }

        /// <summary>
        /// A cannon's breech on the gunner's left, as in a turret: the block with its ring, the sliding breech block at its back (down
        /// while the gun reloads), a recoil guard along its side. It runs back at each shot (Breech).
        /// </summary>
        private void BuildBreech()
        {
            _breechAt = new Vector3(-0.6f, -0.1f, 0.3f);
            _breech = new GameObject("breech").transform;
            _breech.SetParent(_root.transform, false);
            _breech.localPosition = _breechAt;
            void On(Transform t) => t.SetParent(_breech, true);
            On(Box("breech-body", _breechAt, new Vector3(0.2f, 0.2f, 0.4f), _metal));
            On(Cylinder("breech-ring", _breechAt + new Vector3(0f, 0f, -0.17f), new Vector3(0.24f, 0.03f, 0.24f), Quaternion.Euler(90f, 0f, 0f), _paint));
            On(Cylinder("breech-tube", _breechAt + new Vector3(0f, 0f, 0.32f), new Vector3(0.11f, 0.14f, 0.11f), Quaternion.Euler(90f, 0f, 0f), _metal));
            _blockAt = _breechAt + new Vector3(0f, 0f, -0.205f);
            _breechBlock = Box("breech-block", _blockAt, new Vector3(0.14f, 0.12f, 0.03f), _paint);
            On(_breechBlock);
            On(Box("breech-handle", _breechAt + new Vector3(0.11f, 0.03f, -0.15f), new Vector3(0.02f, 0.02f, 0.12f), _button));
            // (The guard does not run back with the gun: it stands between the gunner and the recoil.)
            Box("recoil-guard-top", _breechAt + new Vector3(0.14f, 0.08f, -0.1f), new Vector3(0.02f, 0.02f, 0.5f), _guard);
            Box("recoil-guard-low", _breechAt + new Vector3(0.14f, -0.08f, -0.1f), new Vector3(0.02f, 0.02f, 0.5f), _guard);
            Box("recoil-guard-post", _breechAt + new Vector3(0.14f, 0f, -0.34f), new Vector3(0.02f, 0.18f, 0.02f), _guard);
        }

        /// <summary>
        /// The breech after a shot: back 14 cm in a twentieth of a second, forward again in half a second; its block down 6 cm while the
        /// gun reloads (the loader's open breech), up when it is loaded.
        /// </summary>
        private void Breech()
        {
            if (_breech == null)
            {
                return;
            }
            float since = Time.unscaledTime - _shotAt;
            float back = since < 0f ? 0f : since < 0.05f ? since / 0.05f : Mathf.Clamp01(1f - (since - 0.05f) / 0.5f);
            _breech.localPosition = _breechAt + new Vector3(0f, 0f, -0.14f * back * back * (3f - 2f * back));
            Weapon gun = _cannon;
            bool open = gun != null && (gun.reloading || gun.ammo <= 0);
            float rammed = Time.unscaledTime - _rammedAt;
            if (rammed >= 0f && rammed < 0.15f)
            {
                _breech.localPosition += new Vector3(0f, 0f, 0.02f * (1f - rammed / 0.15f)); // (the round rammed home)
            }
            Vector3 want = _blockAt + new Vector3(0f, open ? -0.06f : 0f, 0f) - _breechAt; // (in the breech's own frame)
            _breechBlock.localPosition = Vector3.MoveTowards(_breechBlock.localPosition, want, Time.unscaledDeltaTime * 0.3f);
        }

        /// <summary>The ready lamp and the ammunition readout follow the gun (the readout five times a second).</summary>
        private void GunState()
        {
            Weapon gun = _seat != null ? Gun(_seat) : null;
            if (gun == null)
            {
                // A driver's readout: the speed, five times a second.
                if (_ammoText != null && _seat != null && _seat.vehicle != null && Time.unscaledTime >= _nextReadout)
                {
                    _nextReadout = Time.unscaledTime + 0.2f;
                    string speed = $"{Mathf.RoundToInt(_seat.vehicle.Velocity().magnitude * 3.6f)} km/h";
                    if (_ammoText.text != speed)
                    {
                        _ammoText.text = speed;
                    }
                }
                return;
            }
            bool empty = gun.ammo <= 0 && !gun.HasInfiniteSpareAmmo() && gun.spareAmmo <= 0;
            string state = gun.reloading ? "reloading" : empty ? "empty" : gun.ammo <= 0 ? (HoldsReload(gun) ? "load" : "reloading") : "ready";
            if (_ready != null)
            {
                Color c = state == "ready" ? new Color(0.2f, 1.6f, 0.3f)
                        : state == "empty" ? new Color(1.6f, 0.08f, 0.05f)
                        : Color.Lerp(new Color(0.25f, 0.12f, 0f), new Color(1.8f, 0.9f, 0.1f), Mathf.PingPong(Time.unscaledTime * 3f, 1f));
                Emit(_ready, c);
                _ready.color = c * 0.4f;
            }
            ReadyState = state;
            if (_ammoText == null || Time.unscaledTime < _nextReadout)
            {
                return;
            }
            _nextReadout = Time.unscaledTime + 0.2f;
            string spare = gun.HasInfiniteSpareAmmo() ? "\u221E" : gun.spareAmmo.ToString();
            string text = state == "reloading" ? "RELOADING" : state == "load" ? (_breech != null ? "LOAD SHELL" : "LOAD AMMO") : state == "empty" ? "NO AMMO" : $"{gun.ammo}  |  {spare}";
            if (Time.unscaledTime < _rangeUntil && _rangeText != null && state == "ready")
            {
                text = _rangeText;
            }
            if (_ammoText.text != text)
            {
                _ammoText.text = text;
            }
        }

        /// <summary>A steering lever: a base plate on the floor, a shaft from its pivot, a knob 30 cm up; pushed forward or pulled back.</summary>
        private Transform Lever(string name, Vector3 pivot)
        {
            Box(name + "-base", pivot + new Vector3(0f, 0.01f, 0f), new Vector3(0.08f, 0.02f, 0.14f), _paint);
            // (Its name on the plate, lying flat, read from above: L TRACK, R TRACK, THROTTLE.)
            string tag = name == "lever-left" ? "L TRACK" : name == "lever-right" ? "R TRACK" : "THROTTLE";
            Label(tag, pivot + new Vector3(0f, 0.022f, -0.045f), Quaternion.Euler(90f, 0f, 0f), 0.0011f, new Color(1f, 0.85f, 0.3f));
            var p = new GameObject(name).transform;
            p.SetParent(_root.transform, false);
            p.localPosition = pivot;
            Transform shaft = Cylinder("shaft", Vector3.zero, new Vector3(0.022f, LeverLength / 2f, 0.022f), Quaternion.identity, _metal);
            shaft.SetParent(p, false);
            shaft.localPosition = new Vector3(0f, LeverLength / 2f, 0f);
            Transform knob = Part(PrimitiveType.Sphere, "knob", Vector3.zero, new Vector3(0.05f, 0.05f, 0.05f), Quaternion.identity, _button);
            knob.SetParent(p, false);
            knob.localPosition = new Vector3(0f, LeverLength, 0f);
            return p;
        }

        private const float LeverLength = 0.3f, LeverThrow = 30f, LeverReach = 0.08f;

        /// <summary>
        /// A lever's value from the palm relative to its pivot (cabin frame): the palm's angle round the pivot, forward positive, over
        /// the lever's 30-degree throw, -1..1. A palm at or under the pivot's height reads as just over it (no flip to the other end).
        /// Pure (offline tests).
        /// </summary>
        internal static float LeverValue(Vector3 fromPivot) =>
            Mathf.Clamp(Mathf.Atan2(fromPivot.z, Mathf.Max(fromPivot.y, 0.05f)) * Mathf.Rad2Deg / LeverThrow, -1f, 1f);

        private Vector3 LeverKnob(int l) => _levers[l].TransformPoint(new Vector3(0f, LeverLength, 0f));

        private Camera _scope;
        private RenderTexture _scopeTexture;
        private Material _scopeScreen, _reticle;
        private Transform _zoomKnob;
        private TextMesh _zoomText;
        private bool _zoomed;
        private const float WideView = 40f, ZoomedView = 13f;

        /// <summary>For the vehicle check: the sight's zoom knob (null: none) and whether it is zoomed in.</summary>
        internal Transform ZoomKnob => _zoomKnob;

        internal bool Zoomed => _zoomed;

        /// <summary>
        /// A gunner's sight on the periscope's screen: a gap in the middle (the gun's line), stadia lines out to each side with ticks,
        /// a range post below with its marks. Drawn just in front of the screen, unlit, in a sight's red-orange.
        /// </summary>
        private void Reticle(Vector3 screen, Vector2 size)
        {
            _reticle = new Material(Shader.Find("Unlit/Color")) { color = new Color(1f, 0.3f, 0.06f) };
            float z = screen.z - 0.0015f; // (between the eyes and the picture)
            const float t = 0.0016f;      // (a line's width)
            void Line(string n, float x, float y, float w, float h) => Part(PrimitiveType.Cube, "reticle-" + n, new Vector3(x, y, z), new Vector3(w, h, 0.0005f), Quaternion.identity, _reticle);
            float gap = 0.012f, reach = Mathf.Min(0.16f, size.x * 0.3f);
            Line("left", -(gap + reach / 2f), 0f, reach, t);
            Line("right", gap + reach / 2f, 0f, reach, t);
            for (int i = 1; i <= 4; i++)
            {
                float x = gap + i * reach / 4f;
                float h = i % 2 == 0 ? 0.012f : 0.007f;
                Line("tick-l" + i, -x, 0f, t, h);
                Line("tick-r" + i, x, 0f, t, h);
            }
            float post = Mathf.Min(0.07f, size.y * 0.38f);
            Line("post", 0f, -(gap + post / 2f), t, post);
            for (int i = 1; i <= 3; i++)
            {
                Line("range" + i, 0f, -(gap + i * post / 3.2f), 0.008f + i * 0.002f, t);
            }
            Line("top", 0f, gap + 0.006f, t, 0.012f);
            // The fire control's aim mark (shown after the range is taken: AimMark): a short green bar with a gap, where the target goes.
            _markMat = new Material(Shader.Find("Unlit/Color")) { color = new Color(0.3f, 1f, 0.35f) };
            _aimMark = new GameObject("aim mark").transform;
            _aimMark.SetParent(_root.transform, false);
            _aimMark.localPosition = new Vector3(0f, 0f, z - 0.0005f);
            foreach (float side in new[] { -1f, 1f })
            {
                Transform bar = Part(PrimitiveType.Cube, "aim-mark", Vector3.zero, new Vector3(0.014f, 0.0022f, 0.0005f), Quaternion.identity, _markMat);
                bar.SetParent(_aimMark, false);
                bar.localPosition = new Vector3(side * 0.012f, 0f, 0f);
            }
            _aimMark.gameObject.SetActive(false);
            _scopeHeight = size.y;
        }

        /// <summary>The sight's zoom knob on the viewer's right: pressed, x1 to x3 and back (the label beside it shows which).</summary>
        private void ZoomControl(Vector3 at)
        {
            Part(PrimitiveType.Cube, "zoom-mount", at + new Vector3(0f, 0f, 0.02f), new Vector3(0.05f, 0.05f, 0.03f), Quaternion.identity, _metal);
            _zoomKnob = Part(PrimitiveType.Cylinder, "zoom", at, new Vector3(0.036f, 0.01f, 0.036f), Quaternion.Euler(-90f, 0f, 0f), _button);
            _zoomText = Label("x1", at + new Vector3(0f, -0.045f, 0f), Quaternion.identity, 0.0018f, new Color(1f, 0.85f, 0.3f));
            _zoomed = false;
        }

        private void SetZoom(bool on)
        {
            _zoomed = on;
            if (_scope != null)
            {
                _scope.fieldOfView = on ? ZoomedView : WideView;
            }
            if (_zoomText != null)
            {
                _zoomText.text = on ? "x3" : "x1";
            }
        }

        /// <summary>
        /// The slot is a periscope's screen: a camera at the eyes looking the cabin's way renders the world past the hull (its near
        /// plane beyond the hull's front), shown on the slot. The hull is solid from inside now (HullInside), so the slot no longer looks
        /// at the world itself.
        /// </summary>
        private void Periscope(Vector3 at, Vector2 size)
        {
            // (1024 wide, 2x anti-aliased: at 384 the user found the tank's view "kinda low res", worse zoomed. A third of a megapixel
            // against the headset's five: cheap.)
            _scopeTexture = new RenderTexture(1024, Mathf.RoundToInt(1024 * size.y / size.x), 24) { name = "RavenfieldVR periscope", antiAliasing = 2, filterMode = FilterMode.Trilinear, useMipMap = true, autoGenerateMips = true, anisoLevel = 4 }; // (mips and anisotropy: seen at an angle and from arm's length the picture shimmered)
            var go = new GameObject("periscope camera");
            go.transform.SetParent(_root.transform, false);
            _scope = go.AddComponent<Camera>();
            Camera eye = _rig.Camera;
            _scope.CopyFrom(eye);
            _scope.stereoTargetEye = StereoTargetEyeMask.None;
            _scope.targetTexture = _scopeTexture;
            _scope.ResetAspect(); // (its picture's own shape: CopyFrom may carry the eyes' aspect, and a stretched picture's aim mark lies)
            _scope.fieldOfView = WideView;
            _scope.depth = eye.depth - 1;
            Lighter(_scope, 2000f);
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
            // (Worn: the interior's grime over the metal and the paint, not flat plastic colours.)
            _metal = new Material(standard) { color = tint * 0.45f, mainTexture = InteriorLook.Grime() };
            _paint = new Material(standard) { color = Color.Lerp(tint, new Color(0.55f, 0.56f, 0.5f), 0.6f), mainTexture = InteriorLook.Grime() };
            _metal.mainTextureScale = _paint.mainTextureScale = new Vector2(0.6f, 0.6f);
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
            _guard = new Material(standard) { color = new Color(0.85f, 0.62f, 0.1f) }; // (safety yellow: the guard round the button, the breech's)
            _guard.SetFloat("_Glossiness", 0.4f);
            Emit(_guard, new Color(0.12f, 0.08f, 0.01f));
            _ready = new Material(standard) { color = new Color(0.1f, 0.4f, 0.1f) };
            Emit(_ready, new Color(0.2f, 1.6f, 0.3f));
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
            return screen == null ? "NO SCREEN" : $"screen {(screen.enabled ? "on" : "OFF")}, shader {(screen.sharedMaterial != null && screen.sharedMaterial.shader != null ? screen.sharedMaterial.shader.name : "NONE")}, texture {(screen.sharedMaterial != null && screen.sharedMaterial.mainTexture == _scopeTexture ? "the periscope's" : "OTHER")}, camera {(_scope != null ? "present" : "NONE")}";
        }

        private Material _brass, _olive;
        private readonly List<GameObject> _rounds = new List<GameObject>();
        private int _roundsShown = -1;

        /// <summary>
        /// The rack shows what is left: a cannon's shells one by one with its spare rounds, a machine gun's boxes by halves of its spare
        /// ammunition; all of them with ammunition without end. Only when the count changes.
        /// </summary>
        private void Rounds()
        {
            Weapon gun = _cannon ?? (_seat != null ? Gun(_seat) : null); // (a cannon's rack counts its shells with the machine gun drawn too)
            if (gun == null || _rounds.Count == 0)
            {
                return;
            }
            int show;
            if (gun.HasInfiniteSpareAmmo())
            {
                show = _rounds.Count;
            }
            else if (gun.configuration.ammo <= 1)
            {
                show = Mathf.Clamp(gun.spareAmmo, 0, _rounds.Count);
            }
            else
            {
                int full = Mathf.Max(1, gun.configuration.spareAmmo);
                show = gun.spareAmmo <= 0 ? 0 : gun.spareAmmo * 2 > full ? 2 : 1;
            }
            if (_carried != null)
            {
                show = Mathf.Max(0, show - 1);
            }
            if (show == _roundsShown)
            {
                return;
            }
            _roundsShown = show;
            for (int i = 0; i < _rounds.Count; i++)
            {
                if (_rounds[i] != null)
                {
                    _rounds[i].SetActive(i < show);
                }
            }
        }

        /// <summary>
        /// The gun's rounds at the right, in reach: a cannon's (one round a load) as five shells standing in a rack (brass cases, dark
        /// tips, two straps), a machine gun's as two ammo boxes with handles. Nothing for a seat with no gun.
        /// </summary>
        private void AmmoRack()
        {
            Weapon gun = Gun(_seat);
            if (gun == null || gun.configuration == null)
            {
                return;
            }
            Shader standard = Shader.Find("Standard");
            if (_brass == null)
            {
                _brass = new Material(standard) { color = new Color(0.78f, 0.6f, 0.28f) };
                _brass.SetFloat("_Metallic", 0.8f);
                _brass.SetFloat("_Glossiness", 0.55f);
                _olive = new Material(standard) { color = new Color(0.27f, 0.3f, 0.18f), mainTexture = InteriorLook.Grime() };
                _olive.SetFloat("_Glossiness", 0.2f);
            }
            // (With the fire control outside the elevation wheel, a steering wheel between the handwheels, the rack stands further out.)
            Vector3 o = new Vector3(SteersByWheel(_seat) ? 0.2f : 0f, 0f, 0f);
            if (gun.configuration.ammo <= 1)
            {
                for (int i = 0; i < 5; i++)
                {
                    float z = 0.2f - i * 0.11f;
                    var shell = new GameObject("shell").transform;
                    shell.SetParent(_root.transform, false);
                    Cylinder("shell-case", o + new Vector3(0.52f, -0.32f, z), new Vector3(0.09f, 0.2f, 0.09f), Quaternion.identity, _brass).SetParent(shell, true);
                    Cylinder("shell-tip", o + new Vector3(0.52f, -0.05f, z), new Vector3(0.075f, 0.07f, 0.075f), Quaternion.identity, _metal).SetParent(shell, true);
                    Cylinder("shell-nose", o + new Vector3(0.52f, 0.04f, z), new Vector3(0.04f, 0.03f, 0.04f), Quaternion.identity, _metal).SetParent(shell, true);
                    _rounds.Add(shell.gameObject);
                    _rackCentres.Add(o + new Vector3(0.52f, -0.23f, z));
                }
                Box("rack-strap-low", o + new Vector3(0.52f, -0.42f, -0.02f), new Vector3(0.12f, 0.025f, 0.6f), _paint);
                Box("rack-strap-high", o + new Vector3(0.52f, -0.2f, -0.02f), new Vector3(0.12f, 0.025f, 0.6f), _paint);
            }
            else
            {
                for (int i = 0; i < 2; i++)
                {
                    float z = 0.12f - i * 0.2f;
                    var box = new GameObject("ammo box").transform;
                    box.SetParent(_root.transform, false);
                    Box("ammo-box", o + new Vector3(0.5f, -0.42f, z), new Vector3(0.12f, 0.16f, 0.17f), _olive).SetParent(box, true);
                    Box("ammo-box-lid", o + new Vector3(0.5f, -0.335f, z), new Vector3(0.125f, 0.015f, 0.175f), _olive).SetParent(box, true);
                    Box("ammo-box-handle", o + new Vector3(0.5f, -0.315f, z), new Vector3(0.02f, 0.012f, 0.08f), _metal).SetParent(box, true);
                    _rounds.Add(box.gameObject);
                    _rackCentres.Add(o + new Vector3(0.5f, -0.4f, z));
                }
            }
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
            Vector2 drive = Vector2.zero;
            bool fire = false;
            for (int h = 0; h < 2; h++)
            {
                VRHand hand = _rig.Hands[h];
                if (hand == null || !hand.Tracked)
                {
                    ReleaseWheel(h);
                    ReleaseLever(h);
                    if (_carryHand == h)
                    {
                        DropShell();
                    }
                    if (_hatchHand == h)
                    {
                        ReleaseHatch();
                    }
                    continue;
                }
                Vector3 palm = hand.PalmCentre;
                Hatch(hand, h, palm);
                // A steering lever: taken by a grip on its knob, pushed or pulled round its pivot.
                if (_onWheel[h] < 0 && _onLever[h] < 0 && hand.Role == HandRole.Free && VRInput.GetHeldUnclaimed(DigitalAction.Grab, hand.Side))
                {
                    for (int l = 0; l < 2; l++)
                    {
                        if (_levers[l] != null && _onLever[1 - h] != l && Vector3.Distance(palm, LeverKnob(l)) < LeverReach)
                        {
                            _onLever[h] = l;
                            hand.Role = HandRole.Cockpit;
                            hand.Model.CockpitStick = true;
                            VRInput.Consume(DigitalAction.Grab, hand.Side);
                            hand.Haptic(0.03f, 120f, 0.4f);
                            break;
                        }
                    }
                }
                if (_onLever[h] >= 0)
                {
                    if (!hand.GrabHeld)
                    {
                        ReleaseLever(h);
                    }
                    else
                    {
                        int l = _onLever[h];
                        float value = LeverValue(_root.transform.InverseTransformPoint(palm) - _levers[l].localPosition);
                        if (Mathf.FloorToInt(value * 4f) != Mathf.FloorToInt(_leverValue[l] * 4f))
                        {
                            hand.Haptic(0.008f, 200f, 0.25f); // (the lever's notches)
                        }
                        _leverValue[l] = value;
                    }
                }
                // A handwheel: taken by a grip at its rim, cranked round its axis (not by a hand on the fire button beside it).
                bool atFire = _fire != null && Vector3.Distance(palm, _fire.position) < ButtonReach;
                // A shell taken from the rack, or the one in this hand carried on.
                if (_carryHand == h)
                {
                    Carry(hand);
                }
                else if (_onWheel[h] < 0 && _onLever[h] < 0 && !atFire && hand.Role == HandRole.Free && VRInput.GetHeldUnclaimed(DigitalAction.Grab, hand.Side))
                {
                    TakeShell(hand, h, palm);
                }
                if (_onWheel[h] < 0 && _onLever[h] < 0 && !atFire && hand.Role == HandRole.Free && VRInput.GetHeldUnclaimed(DigitalAction.Grab, hand.Side))
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
                        if (_onWheel[1 - h] == _onWheel[h])
                        {
                            turned *= 0.5f; // (both hands on one wheel: each its half, or the wheel turned twice as far as either hand)
                        }
                        // (Against the angle's sign: RimAngle grows clockwise as the gunner sees it, from the hub's -z side, and a turn
                        // about the hub's z by +a is counterclockwise from there. With +a the wheel, its knob and spokes ran the other way
                        // round from the hand on it: "some things are in the wrong direction or turned", the user, 2026-11-15; scout.)
                        wheel.localRotation *= Quaternion.Euler(0f, 0f, -turned); // (the wheel turns with the hand)
                        if (Mathf.Abs(turned) > 3f)
                        {
                            hand.Haptic(0.008f, 200f, 0.2f); // (the gear's ticks)
                        }
                        if (_onWheel[h] == 2)
                        {
                            // The steering wheel: a third of a turn each way is full lock. Clockwise from the seat (its top to the right)
                            // grows the rim angle and steers right, the car input's positive.
                            _steerTurned = Mathf.Clamp(_steerTurned + turned, -120f, 120f);
                            VehicleInput.WheelSteer = _steerTurned / 120f;
                        }
                        else
                        {
                            Crank(_onWheel[h], turned);
                            // The trigger of a hand on a handwheel fires, as a gunner's handles do (either hand's).
                            if (VRInput.GetHeld(DigitalAction.Fire, hand.Side))
                            {
                                fire = true;
                            }
                        }
                    }
                }
                // Buttons: pressed while the hand is on them (not one holding a wheel).
                if (_onWheel[h] < 0 && _onLever[h] < 0 && _carryHand != h) // (a shell carried past the fire button fired the gun; scout)
                {
                    if (atFire)
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
            // A click as each is pressed, the button sunk in while held (the button pushed only; a trigger fires without it).
            Press(0, _fire, fire && PalmOnFire());
            if (_zoomKnob != null)
            {
                bool zoomDown = false;
                for (int h = 0; h < 2; h++)
                {
                    VRHand hand = _rig.Hands[h];
                    zoomDown |= hand != null && hand.Tracked && _onWheel[h] < 0 && _onLever[h] < 0 && _carryHand != h
                                && Vector3.Distance(hand.PalmCentre, _zoomKnob.position) < ButtonReach * 0.8f;
                }
                if (zoomDown && !_pressed[5])
                {
                    SetZoom(!_zoomed);
                }
                Press(5, _zoomKnob, zoomDown);
            }
            if (_rangeButton != null)
            {
                bool ranging = false;
                for (int h = 0; h < 2; h++)
                {
                    VRHand hand = _rig.Hands[h];
                    ranging |= hand != null && hand.Tracked && _onWheel[h] < 0 && _onLever[h] < 0 && _carryHand != h
                               && Vector3.Distance(hand.PalmCentre, _rangeButton.position) < ButtonReach * 0.7f;
                }
                if (ranging && !_pressed[7])
                {
                    MeasureRange();
                }
                Press(7, _rangeButton, ranging);
            }
            if (_lightSwitch != null)
            {
                bool flick = false;
                for (int h = 0; h < 2; h++)
                {
                    VRHand hand = _rig.Hands[h];
                    flick |= hand != null && hand.Tracked && _onWheel[h] < 0 && _onLever[h] < 0 && _carryHand != h
                             && Vector3.Distance(hand.PalmCentre, _lightSwitch.position) < ButtonReach * 0.8f;
                }
                if (flick && !_pressed[6])
                {
                    SetCombatLight(!_combatLight);
                    foreach (VRHand hand in _rig.Hands)
                    {
                        if (hand != null && hand.Tracked && Vector3.Distance(hand.PalmCentre, _lightSwitch.position) < ButtonReach)
                        {
                            hand.Haptic(0.012f, 220f, 0.5f); // (the toggle's snap)
                        }
                    }
                }
                _pressed[6] = flick;
            }
            for (int i = 0; i < _driveButtons.Count; i++)
            {
                (Transform t, Vector2 d) = _driveButtons[i];
                Press(1 + i, t, Vector2.Dot(drive, d) > 0.5f);
            }
            // The levers: a let-go one springs back to the middle; both into the drive input with the buttons.
            for (int l = 0; l < 2; l++)
            {
                if (_levers[l] == null)
                {
                    continue;
                }
                if (_onLever[0] != l && _onLever[1] != l)
                {
                    _leverValue[l] = Mathf.MoveTowards(_leverValue[l], 0f, Time.unscaledDeltaTime * 5f);
                }
                _levers[l].localRotation = Quaternion.Euler(_leverValue[l] * LeverThrow, 0f, 0f);
            }
            if (_levers[0] != null)
            {
                drive += TillerDrive(_leverValue[0], _leverValue[1]);
            }
            else if (_throttleOnly && _levers[1] != null)
            {
                drive += new Vector2(0f, _leverValue[1]); // (the throttle: forward and back only, the wheel steers)
            }
            Pins();
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
                    // (Zoomed in, a third as fast: the sight's picture moves three times as much.)
                    float fine = _zoomed ? ZoomedView / WideView : 1f;
                    Crank(0, stick.x * 70f * dt * 4f * fine);
                    Crank(1, stick.y * 35f * dt * 12f * fine); // (pushed up: the gun up)
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
                // (In its own frame: the world's up added to a position in the cabin's frame sank a button sideways in a tipped hull.)
                t.localPosition += t.localRotation * Vector3.up * (down ? -0.008f : 0.008f);
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

        private bool PalmOnFire()
        {
            foreach (VRHand hand in _rig.Hands)
            {
                if (hand != null && hand.Tracked && _fire != null && Vector3.Distance(hand.PalmCentre, _fire.position) < ButtonReach)
                {
                    return true;
                }
            }
            return false;
        }

        private void ReleaseLever(int h)
        {
            if (_onLever[h] < 0)
            {
                return;
            }
            _onLever[h] = -1;
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
            CockpitPin.ClearAll(this, _rig.Hands); // (a menu or a pause: the hands back on the controllers)
            ReleaseWheel(0);
            ReleaseWheel(1);
            ReleaseLever(0);
            ReleaseLever(1);
            ReleaseHatch();
            _leverValue[0] = _leverValue[1] = 0f;
            DropShell();
            Fire = false;
            VehicleInput.DriveButtons = Vector2.zero; // (the wanted aim stays: a pause or a rebuilt cabin left the gun with none)
        }

        /// <summary>
        /// The hands holding the cabin's controls drawn on them (the user, 2026-11-15: hands that "actually grab"): on a handwheel's or
        /// the steering wheel's rim where the palm closed, on a lever's knob, on the hatch handle's bar. A carried shell is in the fist
        /// already. Let go: back to the controller.
        /// </summary>
        private void Pins()
        {
            for (int h = 0; h < 2; h++)
            {
                VRHand hand = _rig.Hands[h];
                if (hand == null)
                {
                    continue;
                }
                Vector3? at = null, along = null;
                if (_onWheel[h] >= 0 && WheelOf(_onWheel[h]) != null)
                {
                    Transform wheel = WheelOf(_onWheel[h]);
                    Vector3 axis = wheel.parent.rotation * Quaternion.Euler(-30f, 0f, 0f) * Vector3.forward;
                    at = CockpitPin.OnRim(wheel.position, axis, WheelRadius, hand.PalmCentre);
                    along = CockpitPin.RimTangent(wheel.position, axis, at.Value);
                }
                else if (_onLever[h] >= 0 && _levers[_onLever[h]] != null)
                {
                    at = LeverKnob(_onLever[h]);
                }
                else if (_hatchHand == h && _hatch != null)
                {
                    // (On the bar, where along it the palm is: 11 cm either side of the middle.)
                    Vector3 local = _hatch.InverseTransformPoint(hand.PalmCentre);
                    at = _hatch.TransformPoint(new Vector3(Mathf.Clamp(local.x, -0.11f, 0.11f), 0f, 0f));
                    along = _hatch.right;
                }
                CockpitPin.Set(this, hand, at, along);
            }
        }

        private void Clear()
        {
            GraphicsPresets.InCabin(false);
            CabinFar = float.PositiveInfinity;
            CockpitPin.ClearAll(this, _rig.Hands);
            ReleaseHands();
            _driveButtons.Clear();
            _traverse = _elevate = _fire = _steer = null;
            _fireReady = _breech = _breechBlock = null;
            _cannon = null;
            _feedLid = null;
            _shotAt = _rammedAt = _hitAt = -10f; // (a seat changed straight after a shot: the new breech twitched)
            _zoomKnob = null;
            _rangeButton = null;
            _hatch = null;
            _aimMark = null;
            _markUntil = -10f;
            _markAngle = float.NaN;
            _hatchFrames = 0;
            _rangeText = null;
            _rangeUntil = -10f;
            _lightSwitch = _hint = null;
            foreach (Material m in _extra)
            {
                if (m != null)
                {
                    Object.Destroy(m);
                }
            }
            _extra.Clear();
            foreach (Mesh m in _merged)
            {
                if (m != null)
                {
                    Object.Destroy(m);
                }
            }
            _merged.Clear();
            _smoke = _sparks = null; // (their objects go with the root)
            if (_puffMat != null)
            {
                Object.Destroy(_puffMat);
                _puffMat = null;
            }
            _cabin = null;
            _zoomText = null;
            _zoomed = false;
            _ammoText = null;
            _levers[0] = _levers[1] = null;
            _throttleOnly = false;
            for (int g = 0; g < _needles.Length; g++)
            {
                _needles[g] = null;
            }
            ReadyState = "none";
            Fit = "none";
            _rounds.Clear();
            _rackCentres.Clear();
            _roundsShown = -1;
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
            if (_scope != null)
            {
                // (Off and untargeted before its texture goes: the root and so the camera are destroyed only at the frame's end, and it
                // rendered into the released texture meanwhile; as VRScope.Destroy does. Scope scout.)
                _scope.enabled = false;
                _scope.targetTexture = null;
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
            if (_block != null)
            {
                // (The driver's block view the same: it drew every third frame into its released texture until the frame's end; cabin scout.)
                _block.enabled = false;
                _block.targetTexture = null;
            }
            if (_blockTexture != null)
            {
                _blockTexture.Release();
                Object.Destroy(_blockTexture);
                _blockTexture = null;
            }
            if (_blockScreen != null)
            {
                Object.Destroy(_blockScreen);
                _blockScreen = null;
            }
            _block = null;
            foreach (Material m in new[] { _wall, _metal, _red, _lamp, _button, _paint, _brass, _olive, _guard, _ready, _reticle, _hintMat, _markMat })
            {
                if (m != null)
                {
                    Object.Destroy(m);
                }
            }
            _wall = _metal = _red = _lamp = _button = _paint = _brass = _olive = _guard = _ready = _reticle = _hintMat = _markMat = null;
        }

        public void Destroy()
        {
            Clear();
            _aimSeat = null;
            WheelAim = null;
            Fire = false;
            TestAlarm = false;
            TestSmoke = false;
            if (Instance == this)
            {
                Instance = null;
            }
        }
    }
}
