using System.Collections.Generic;
using RavenfieldVR.Core;
using RavenfieldVR.Rig;
using UnityEngine;

namespace RavenfieldVR.Weapons
{
    /// <summary>
    /// The muzzle flash on the lens (Tasks 209 A-C), as approved: on a shot where the weapon's own muzzle flash plays
    /// (Tasks 212: none for a weapon without one, a faint glint for a suppressed one), each eye gets the approved lens
    /// flash (<see cref="LensEffects"/> draws it). Everything here follows the approved simulation, flash_sim.py: the flash's
    /// centre a little ahead of the muzzle (the plume) with an uneven spread, a size varying shot to shot and a turning
    /// starburst; each eye's strength from the gun's flash size x how much the muzzle faces that eye x nearness x how much
    /// that eye looks along the gun; and a flash limiter per eye (on by default) that keeps it under three visible flashes
    /// a second (WCAG 2.3.1): six brightness steps in any 1.1 s, a flash costing two (a very bright one three); past
    /// the budget a soft flash below the flash threshold, and a steady glow for sustained fire instead of a strobe. The
    /// gun's flash size is its own (defined by the gun): its class (pistol, SMG, rifle, machine gun, shotgun) and its
    /// particles' real size.
    /// </summary>
    public sealed class LensFlash
    {
        public static LensFlash Instance { get; private set; }

        // 1.6 in flash_sim.py; at the strength it needs over a lit scene a rifle's flash washed out half the view.
        private const float MaxPower = 1.3f;

        private readonly VRRig _rig;
        private readonly Limiter[] _limiters = { new Limiter(), new Limiter() };
        // A shot this frame, decided before drawing (the game plays its particles during the frame).
        private Weapon _pendingWeapon;
        private HeldWeapon _pendingHeld;
        private int _pendingFrame = -1;
        // The fired gun's muzzle particle systems and how many particles each had at the shot: only particles this shot
        // put out count as its flash (not an earlier shot's still in the air, or another barrel's).
        private ParticleSystem[] _pendingSystems = new ParticleSystem[0];
        private int[] _pendingCounts = new int[0];
        private ParticleSystem[][] _pendingTrees = new ParticleSystem[0][];

        /// <summary>
        /// Particles alive in a muzzle's system and the systems under it: an RPG-7's top system emits nothing itself, its
        /// children make the flash and the smoke, and the shot was judged flash-less (no lens flash on the RPG-7 and RPG-7V).
        /// </summary>
        private static int TreeCount(ParticleSystem[] tree)
        {
            int n = 0;
            foreach (ParticleSystem p in tree)
            {
                if (p != null)
                {
                    n += p.particleCount;
                }
            }
            return n;
        }
        // The gun's flash plays on every shot (no burst chance): any of its particles out is this shot's flash.
        private bool _pendingEveryShot;
        /// <summary>The fired weapon's flash reading, taken once at the shot (its muzzle tree does not change within a shot).</summary>
        private MuzzleFlashInfo _pendingInfo;
        // The gun now (for the glow of sustained fire).
        private readonly List<float> _shots = new List<float>();
        private Weapon _gun;
        private float _gunSize;
        private bool _gunAuto;

        /// <summary>Autotest: one eye's flash as the limiter let it through (the steps it was charged; 0 for a soft one).</summary>
        internal struct TestFlash
        {
            public float Time, Asked, Shown, Visible;
            public int Eye, Steps;
        }

        /// <summary>Autotest: every flash is written here while it is set.</summary>
        internal List<TestFlash> TestLog;

        /// <summary>Self-test: flashes shown, and the last one's strength (the stronger eye).</summary>
        internal int Shown { get; private set; }

        /// <summary>Self-test: eyes whose picture flashed the whole view with a shot.</summary>
        internal int ViewFlashes { get; private set; }

        /// <summary>Self-test: the player's shots seen (each may or may not flash).</summary>
        internal int ShotsSeen { get; private set; }
        internal float LastPower { get; private set; }

        /// <summary>Self-test: how much of the last flash the eye that saw most of it saw past the gun (0..1).</summary>
        internal float LastVisible { get; private set; } = 1f;

        public LensFlash(VRRig rig)
        {
            Instance = this;
            _rig = rig;
            _fireball = new MuzzleFireball();
            // Without a shader for the core in the world it is drawn on the lens, as before (it was drawn nowhere).
            LensEffects.CoreInWorld = _fireball.Ready;
        }

        // The flash's hot core in the world, hidden by the gun where it covers it (Tasks 237).
        private readonly MuzzleFireball _fireball;
        private HeldWeapon _gunHeld;
        private float _gunAhead = 0.1f;
        private readonly float[] _glowVis = { 1f, 1f };
        private int _glowVisFrame = -10;

        /// <summary>The player's weapon fired (Weapon.Shoot): the flash is decided before the frame is drawn.</summary>
        public void OnShot(HeldWeapon held, Weapon weapon)
        {
            _shots.Add(Time.unscaledTime); // every shot counts for the glow's rate, two in one frame too
            ShotsSeen++;
            _pendingHeld = held;
            _pendingWeapon = weapon;
            _pendingFrame = Time.frameCount;
            // Read now, before the shot's particles are simulated (the game has only asked them to play).
            _pendingSystems = MuzzleFlashInfo.MuzzleSystems(weapon);
            _pendingInfo = MuzzleFlashInfo.Of(weapon); // read once: it walks the muzzle tree, and five or six reads followed per shot
            _pendingEveryShot = _pendingInfo.Chance >= 0.999f;
            _pendingCounts = new int[_pendingSystems.Length];
            _pendingTrees = new ParticleSystem[_pendingSystems.Length][];
            for (int i = 0; i < _pendingSystems.Length; i++)
            {
                _pendingTrees[i] = _pendingSystems[i] != null ? _pendingSystems[i].GetComponentsInChildren<ParticleSystem>(false) : new ParticleSystem[0];
                _pendingCounts[i] = TreeCount(_pendingTrees[i]);
            }
        }

        public void BeforeRender()
        {
            float now = Time.unscaledTime;
            if (_pendingWeapon != null && Time.frameCount - _pendingFrame <= 1)
            {
                // A shot fired after this frame's particle update shows its particles next frame: decided then.
                bool later = Time.frameCount == _pendingFrame && _pendingInfo.Particles && !ParticlesOut();
                if (!later)
                {
                    TryFlash(_pendingHeld, _pendingWeapon, _pendingInfo, now);
                    _pendingWeapon = null;
                    _pendingHeld = null;
                }
            }
            UpdateGlow(now, Time.unscaledDeltaTime);
            _fireball.BeforeRender(_rig.Camera != null ? _rig.Camera.transform : null);
        }

        /// <summary>
        /// How much of a flash at <paramref name="source"/> (about <paramref name="radius"/> across) the eye at
        /// <paramref name="eye"/> sees past the gun: the share of seven points over it whose line to the eye clears the
        /// drawn gun. Aiming, the front sight post hides part of it, the receiver all of it.
        /// </summary>
        internal static float Visibility(HeldWeapon held, Vector3 source, Vector3 eye, float radius)
        {
            if (held == null)
            {
                return 1f;
            }
            Vector3 toEye = eye - source;
            float d = toEye.magnitude;
            if (d < 1e-3f)
            {
                return 1f;
            }
            Vector3 dir = toEye / d;
            Vector3 a = Vector3.Cross(dir, Vector3.up);
            if (a.sqrMagnitude < 1e-4f)
            {
                a = Vector3.Cross(dir, Vector3.right);
            }
            a.Normalize();
            Vector3 b = Vector3.Cross(dir, a);
            int seen = 0;
            for (int k = 0; k < 7; k++)
            {
                Vector3 s = source;
                if (k > 0)
                {
                    float ang = k * Mathf.PI / 3f;
                    s += (a * Mathf.Cos(ang) + b * Mathf.Sin(ang)) * radius;
                }
                if (!Blocked(held, s, eye))
                {
                    seen++;
                }
            }
            return seen / 7f;
        }

        private static bool Blocked(HeldWeapon held, Vector3 from, Vector3 to)
        {
            Vector3 v = to - from;
            int steps = Mathf.Clamp(Mathf.CeilToInt(v.magnitude / 0.006f), 1, 160);
            for (int i = 1; i < steps; i++)
            {
                if (held.InsideDrawnGun(from + v * (i / (float)steps)))
                {
                    return true;
                }
            }
            return false;
        }

        // ---- a shot ------------------------------------------------------------------------------------------------

        private void TryFlash(HeldWeapon held, Weapon w, MuzzleFlashInfo info, float now)
        {
            LensEffects fx = LensEffects.Instance;
            if (!VRConfig.LensFlash.Value || fx == null || held == null || w == null || w.configuration == null)
            {
                return;
            }
            Transform muzzle = w.CurrentMuzzle();
            if (muzzle == null)
            {
                return;
            }
            // The gun's own flash size places the plume; the player's strength setting only scales how bright it is.
            float size = FlashSize(held, w, info, ParticlesOut());
            float strength = Mathf.Max(0.05f, VRConfig.LensFlashStrength.Value);
            _gun = w;
            // The glow of sustained fire is the gun's own flash size, whether or not this one shot flashed (a shot judged
            // flash-less set it to nothing, and the glow went out in the middle of a burst).
            float gunSize = info.Particles ? FlashSize(held, w, info, true) : size;
            _gunSize = gunSize * strength;
            _gunAuto = w.configuration.auto;
            if (size <= 0f)
            {
                if (info.Any) // a smoke grenade or a knife has no flash to miss
                {
                    Log.InfoOnce("lensnone-size:" + w.name, $"No lens flash for {w.name}: its flash particles did not come out on the shot "
                                                            + $"(muzzle '{muzzle.name}', particles {info.Particles}, light {info.Light}).");
                }
                return; // no flash on this shot (none on this weapon, or its particles skipped this one)
            }
            Transform head = _rig.Camera.transform;
            // flash_sim.py fire(): the plume's centre a little ahead of the muzzle, its uneven spread, its size and starburst.
            // The plume's bright part in front of the muzzle (10-25 cm for a rifle): at flash_sim.py's 2-7 cm an aimed rifle's
            // flash sat behind its front sight base and looked as if it came out of the gun.
            float ahead = Random.Range(0.08f, 0.16f) * Mathf.Sqrt(size);
            float jx = Gauss() * 0.8f * Mathf.Deg2Rad, jy = Gauss() * 0.8f * Mathf.Deg2Rad;
            float sizeK = Random.Range(0.85f, 1.15f);
            float phase = Random.Range(0f, 6.28f);
            Vector3 source = muzzle.position + muzzle.forward * ahead;
            Vector3 m = head.InverseTransformPoint(source);
            Vector3 dir = head.InverseTransformDirection(muzzle.forward);
            if (m.z < 0.02f)
            {
                Log.InfoOnce("lensnone-behind:" + w.name, $"No lens flash for {w.name}: its muzzle '{muzzle.name}' is {m.z * 100f:0} cm in front of the eyes "
                                                          + $"(behind them; muzzle points {(Vector3.Dot(muzzle.forward, head.forward) > 0f ? "forward" : "back")}).");
                return; // behind the eyes
            }
            _gunHeld = held;
            _gunAhead = ahead;
            float strongest = 0f, seenMost = 0f;
            // Now and then (about one shot in three) the whole view flashes with it, a loud gun's more: the same in both eyes,
            // only where the limiter gives this shot a full flash (Tasks 239).
            bool viewFlash = VRConfig.ShotViewFlash.Value && Random.value < 0.35f && info.Light;
            float viewAmount = Random.Range(0.10f, 0.22f) * Mathf.Clamp(size * strength / 1.7f, 0.4f, 1.3f);
            for (int e = 0; e < 2; e++)
            {
                float ex = fx.EyeX(e);
                float asked = EyePower(size * strength, m, dir, ex, fx.Ipd) * sizeK;
                float p = _limiters[e].Shot(now, asked, out int steps);
                // The eye's glare from it: as much of the flash as this eye sees past the gun.
                float vis = LensEffects.CoreInWorld ? Visibility(held, source, head.TransformPoint(new Vector3(ex, 0f, 0f)), 0.025f * Mathf.Sqrt(size)) : 1f;
                TestLog?.Add(new TestFlash { Time = now, Asked = asked, Shown = p, Eye = e, Steps = steps, Visible = vis });
                Vector2 at = LensEffects.Project(m, ex) + new Vector2(LensEffects.Focal * Mathf.Tan(jx), -LensEffects.Focal * Mathf.Tan(jy));
                float view = viewFlash && (steps > 0 || !VRConfig.ReduceFlashing.Value) ? viewAmount : 0f;
                fx.AddFlash(e, new LensEffects.Flash { X = at.x, Y = at.y, Power = p, Phase = phase, Born = now, Vis = vis, View = view });
                if (view > 0f)
                {
                    ViewFlashes++;
                }
                strongest = Mathf.Max(strongest, p);
                seenMost = Mathf.Max(seenMost, vis);
            }
            if (LensEffects.CoreInWorld)
            {
                _fireball.Add(muzzle, ahead, strongest, phase); // the core in the world, behind what covers it
            }
            LastPower = strongest;
            LastVisible = seenMost;
            Shown++;
        }

        /// <summary>
        /// The gun's flash size on this shot, as flash_sim.py's guns (pistol 1.0, SMG 1.1, rifle 1.7, machine gun 2.0,
        /// shotgun 2.6, suppressed 0.15), times its particles' real size; 0 when it does not flash on this shot.
        /// </summary>
        private static float FlashSize(HeldWeapon held, Weapon w, MuzzleFlashInfo info, bool particlesOut)
        {
            float size;
            // The gun that fired: an under-barrel shotgun on a pistol is a shotgun's flash, not the pistol's.
            WeaponRigData d = (w == held.Weapon ? held.Rig : held.ActiveRig ?? held.Rig).Data;
            if (particlesOut)
            {
                // A short automatic that is no pistol nor machine gun is an SMG (a gun under 45 cm from grip to muzzle).
                bool smg = w.configuration.auto && !d.IsPistol && !d.IsMachineGun && Vector3.Distance(d.GripPoint, d.MuzzlePoint) < 0.45f;
                float byClass = !info.Light ? 0.15f // suppressed: a faint glint
                    : d.IsPistol ? 1f : d.IsMachineGun ? 2f : w.configuration.projectilesPerShot > 1 ? 2.6f : smg ? 1.1f : 1.7f;
                float real = info.Size > 0.01f ? Mathf.Clamp(info.Size / 0.15f, 0.6f, 1.5f) : 1f;
                size = byClass * real;
            }
            else if (info.Light && !info.Particles)
            {
                size = 0.4f; // only the game's flash light: a small flash
            }
            else
            {
                return 0f;
            }
            return size;
        }

        /// <summary>
        /// Did the game's own flash play on this shot? This shot's particles are out once simulated (a burst with a chance
        /// may emit nothing): more particles than at the shot on one of the fired gun's muzzles.
        /// </summary>
        private bool ParticlesOut()
        {
            for (int i = 0; i < _pendingSystems.Length; i++)
            {
                ParticleSystem ps = _pendingSystems[i];
                // More particles than at the shot; or, for a flash that plays on every shot, any: a machine gun's system is
                // full and playing through sustained fire, its count never rises, and only the first of 120 rounds flashed.
                int now = ps != null && i < _pendingTrees.Length ? TreeCount(_pendingTrees[i]) : 0;
                if (ps != null && (now > _pendingCounts[i] || _pendingEveryShot && now > 0))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// flash_sim.py eye_power: flash size x facing (seen from behind: half, toward the face: full) x nearness x how
        /// much this eye looks along the gun (the other eye down to half). <paramref name="m"/> and <paramref name="dir"/>
        /// are in head space (x right, y up, z forward).
        /// </summary>
        private static float EyePower(float size, Vector3 m, Vector3 dir, float eyeX, float ipd)
        {
            Vector3 toEye = new Vector3(eyeX, 0f, 0f) - m;
            float d = Mathf.Max(toEye.magnitude, 1e-3f);
            float facing = 0.5f + 0.5f * Mathf.Max(0f, Vector3.Dot(dir.normalized, toEye / d));
            float dist = Mathf.Clamp(0.75f / d, 0.5f, 2.5f);
            float near = Mathf.Min(Mathf.Abs(m.x + ipd / 2f), Mathf.Abs(m.x - ipd / 2f));
            float side = 1f - 0.5f * Mathf.Clamp01((Mathf.Abs(m.x - eyeX) - near) / ipd);
            return Mathf.Min(MaxPower, size * facing * dist * side);
        }

        // ---- the glow of sustained fire ------------------------------------------------------------------------------

        private void UpdateGlow(float now, float dt)
        {
            // Prune the shot times first, before the no-effects return: OnShot keeps adding to them, so with the lens off
            // (no additive shader) the list would otherwise grow all session.
            for (int i = _shots.Count - 1; i >= 0; i--)
            {
                if (now - _shots[i] > 0.5f)
                {
                    _shots.RemoveAt(i);
                }
            }
            LensEffects fx = LensEffects.Instance;
            if (fx == null)
            {
                return;
            }
            Transform muzzle = _gun != null && VRConfig.LensFlash.Value ? _gun.CurrentMuzzle() : null;
            Transform head = _rig.Camera.transform;
            Vector3 m = muzzle != null ? head.InverseTransformPoint(muzzle.position) : Vector3.forward;
            Vector3 dir = muzzle != null ? head.InverseTransformDirection(muzzle.forward) : Vector3.forward;
            // Only while the trigger is held on an automatic (flash_sim.py: full auto): taps of an automatic left a faint glow
            // sitting at the muzzle between the shots.
            float rate = _gunAuto && _gun != null && _gun.holdingFire ? _shots.Count / 0.5f : 0f;
            float glowMost = 0f;
            for (int e = 0; e < 2; e++)
            {
                float ex = fx.EyeX(e);
                float power = muzzle != null && m.z > 0.02f ? EyePower(_gunSize, m, dir, ex, fx.Ipd) : 0f;
                if (power <= 0f)
                {
                    _limiters[e].ClearGlow(); // no gun to glow at
                }
                float glow = _limiters[e].Step(dt, rate, power);
                float shown = muzzle != null && m.z > 0.02f ? glow : 0f;
                if (shown > 0.001f && LensEffects.CoreInWorld && Time.frameCount - _glowVisFrame >= 3)
                {
                    // Seen past the gun, measured every third frame while it glows.
                    _glowVis[e] = Visibility(_gunHeld, muzzle.position + muzzle.forward * _gunAhead, head.TransformPoint(new Vector3(ex, 0f, 0f)), 0.025f * Mathf.Sqrt(Mathf.Max(0.1f, _gunSize)));
                    if (e == 1)
                    {
                        _glowVisFrame = Time.frameCount;
                    }
                }
                Vector2 at = m.z > 0.02f ? LensEffects.Project(m, ex) : new Vector2(LensEffects.W / 2f, LensEffects.H);
                fx.SetGlow(e, shown, at, at.x >= LensEffects.W / 2f ? 1f : -1f, _glowVis[e]);
                glowMost = Mathf.Max(glowMost, shown);
            }
            if (LensEffects.CoreInWorld)
            {
                // Gone out when the player is dead, like the lens glow (Present zeroes j.Glow on death). The world fireball
                // used to keep glowing at the muzzle of a gun left visible after death until the last shot left the window.
                _fireball.SetGlow(muzzle, _gunAhead, Util.GameContext.PlayerAlive ? glowMost : 0f);
            }
        }

        private static float Gauss()
        {
            float u1 = 1f - Random.value, u2 = Random.value;
            return Mathf.Sqrt(-2f * Mathf.Log(u1)) * Mathf.Cos(2f * Mathf.PI * u2);
        }

        public void Destroy()
        {
            _fireball.Destroy();
            if (Instance == this)
            {
                Instance = null;
            }
        }

        /// <summary>
        /// flash_sim.py Limiter, per eye: at most 3 full flashes in any second; later shots get a soft flash below the
        /// flash threshold, and sustained fire a steady glow that rises and falls slowly. The soft and one-step levels
        /// were calibrated by flash_sim.py calibrate_soft() for each glow under them (on a lit base the same step in the
        /// picture is a bigger change in light).
        /// </summary>
        private sealed class Limiter
        {
            private static readonly float[] Glows = { 0f, 0.1f, 0.2f, 0.3f, 0.4f, 0.6f };
            private static readonly float[] Soft = { 0.3408f, 0.3216f, 0.2947f, 0.267f, 0.2447f, 0.2215f };
            private static readonly float[] OneStep = { 0.8583f, 0.8338f, 0.8042f, 0.7737f, 0.7526f, 0.8012f };
            private const float Window = 1.1f;
            private const int Budget = 6;

            private readonly List<KeyValuePair<float, int>> _full = new List<KeyValuePair<float, int>>();
            private float _glow;

            public float Shot(float t, float power, out int steps)
            {
                steps = 2;
                if (!VRConfig.ReduceFlashing.Value)
                {
                    return power;
                }
                for (int i = _full.Count - 1; i >= 0; i--) // drop entries older than the window in place (no per-call closure)
                {
                    if (t - _full[i].Key >= Window)
                    {
                        _full.RemoveAt(i);
                    }
                }
                int left = Budget;
                foreach (var s in _full)
                {
                    left -= s.Value;
                }
                // A flash costs 2 transitions (on, off); a very bright one fades in two counted steps and costs 3.
                float one = Interp(_glow, OneStep);
                int cost = power <= one ? 2 : 3;
                if (left >= cost)
                {
                    _full.Add(new KeyValuePair<float, int>(t, cost));
                    steps = cost;
                    return power;
                }
                if (left >= 2)
                {
                    _full.Add(new KeyValuePair<float, int>(t, 2));
                    return Mathf.Min(power, one);
                }
                steps = 0; // a soft flash, under the flash threshold
                return Mathf.Min(power, Interp(_glow, Soft));
            }

            /// <summary>The steady glow while firing fast: follows the rate, eased (never more than 4% a frame at 90 fps).</summary>
            public float Step(float dt, float rate, float power)
            {
                float want = VRConfig.ReduceFlashing.Value ? Mathf.Min(0.35f, 0.025f * rate) * power : 0f;
                float most = 0.04f * power + 1e-4f;
                _glow += Mathf.Clamp(want - _glow, -most, most) * Mathf.Min(1f, dt * 90f);
                return _glow;
            }

            public void ClearGlow()
            {
                _glow = 0f;
            }

            private static float Interp(float x, float[] ys)
            {
                if (x <= Glows[0])
                {
                    return ys[0];
                }
                for (int i = 1; i < Glows.Length; i++)
                {
                    if (x <= Glows[i])
                    {
                        return Mathf.Lerp(ys[i - 1], ys[i], (x - Glows[i - 1]) / (Glows[i] - Glows[i - 1]));
                    }
                }
                return ys[ys.Length - 1];
            }
        }
    }
}
