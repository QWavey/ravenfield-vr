using System.Collections.Generic;
using RavenfieldVR.Core;
using RavenfieldVR.Rig;
using UnityEngine;

namespace RavenfieldVR.Weapons
{
    /// <summary>
    /// The muzzle flash on the lens (Tasks2 209 A-C), as approved: on a shot where the weapon's own muzzle flash plays
    /// (Tasks2 212: none for a weapon without one, a faint glint for a suppressed one), each eye gets the approved lens
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
        // The gun now (for the glow of sustained fire).
        private readonly List<float> _shots = new List<float>();
        private Weapon _gun;
        private float _gunSize;
        private bool _gunAuto;

        /// <summary>Autotest: one eye's flash as the limiter let it through (the steps it was charged; 0 for a soft one).</summary>
        internal struct TestFlash
        {
            public float Time, Asked, Shown;
            public int Eye, Steps;
        }

        /// <summary>Autotest: every flash is written here while it is set.</summary>
        internal List<TestFlash> TestLog;

        /// <summary>Self-test: flashes shown, and the last one's strength (the stronger eye).</summary>
        internal int Shown { get; private set; }
        internal float LastPower { get; private set; }

        public LensFlash(VRRig rig)
        {
            Instance = this;
            _rig = rig;
        }

        /// <summary>The player's weapon fired (Weapon.Shoot): the flash is decided before the frame is drawn.</summary>
        public void OnShot(HeldWeapon held, Weapon weapon)
        {
            _shots.Add(Time.unscaledTime); // every shot counts for the glow's rate, two in one frame too
            _pendingHeld = held;
            _pendingWeapon = weapon;
            _pendingFrame = Time.frameCount;
            // Read now, before the shot's particles are simulated (the game has only asked them to play).
            _pendingSystems = MuzzleFlashInfo.MuzzleSystems(weapon);
            _pendingCounts = new int[_pendingSystems.Length];
            for (int i = 0; i < _pendingSystems.Length; i++)
            {
                _pendingCounts[i] = _pendingSystems[i] != null ? _pendingSystems[i].particleCount : 0;
            }
        }

        public void BeforeRender()
        {
            float now = Time.unscaledTime;
            if (_pendingWeapon != null && Time.frameCount - _pendingFrame <= 1)
            {
                // A shot fired after this frame's particle update shows its particles next frame: decided then.
                if (Time.frameCount == _pendingFrame && MuzzleFlashInfo.Of(_pendingWeapon).Particles && !ParticlesOut())
                {
                    UpdateGlow(now, Time.unscaledDeltaTime);
                    return;
                }
                TryFlash(_pendingHeld, _pendingWeapon, now);
                _pendingWeapon = null;
                _pendingHeld = null;
            }
            UpdateGlow(now, Time.unscaledDeltaTime);
        }

        // ---- a shot ------------------------------------------------------------------------------------------------

        private void TryFlash(HeldWeapon held, Weapon w, float now)
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
            float size = FlashSize(held, w, ParticlesOut());
            float strength = Mathf.Max(0.05f, VRConfig.LensFlashStrength.Value);
            _gun = w;
            _gunSize = size * strength;
            _gunAuto = w.configuration.auto;
            if (size <= 0f)
            {
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
            Vector3 m = head.InverseTransformPoint(muzzle.position + muzzle.forward * ahead);
            Vector3 dir = head.InverseTransformDirection(muzzle.forward);
            if (m.z < 0.02f)
            {
                return; // behind the eyes
            }
            float strongest = 0f;
            for (int e = 0; e < 2; e++)
            {
                float ex = fx.EyeX(e);
                float asked = EyePower(size * strength, m, dir, ex, fx.Ipd) * sizeK;
                float p = _limiters[e].Shot(now, asked, out int steps);
                TestLog?.Add(new TestFlash { Time = now, Asked = asked, Shown = p, Eye = e, Steps = steps });
                Vector2 at = LensEffects.Project(m, ex) + new Vector2(LensEffects.Focal * Mathf.Tan(jx), -LensEffects.Focal * Mathf.Tan(jy));
                fx.AddFlash(e, new LensEffects.Flash { X = at.x, Y = at.y, Power = p, Phase = phase, Born = now });
                strongest = Mathf.Max(strongest, p);
            }
            LastPower = strongest;
            Shown++;
        }

        /// <summary>
        /// The gun's flash size on this shot, as flash_sim.py's guns (pistol 1.0, SMG 1.1, rifle 1.7, machine gun 2.0,
        /// shotgun 2.6, suppressed 0.15), times its particles' real size; 0 when it does not flash on this shot.
        /// </summary>
        private static float FlashSize(HeldWeapon held, Weapon w, bool particlesOut)
        {
            MuzzleFlashInfo info = MuzzleFlashInfo.Of(w);
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
                if (_pendingSystems[i] != null && _pendingSystems[i].particleCount > _pendingCounts[i])
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
            LensEffects fx = LensEffects.Instance;
            if (fx == null)
            {
                return;
            }
            for (int i = _shots.Count - 1; i >= 0; i--)
            {
                if (now - _shots[i] > 0.5f)
                {
                    _shots.RemoveAt(i);
                }
            }
            Transform muzzle = _gun != null && VRConfig.LensFlash.Value ? _gun.CurrentMuzzle() : null;
            Transform head = _rig.Camera.transform;
            Vector3 m = muzzle != null ? head.InverseTransformPoint(muzzle.position) : Vector3.forward;
            Vector3 dir = muzzle != null ? head.InverseTransformDirection(muzzle.forward) : Vector3.forward;
            // Only while the trigger is held on an automatic (flash_sim.py: full auto): taps of an automatic left a faint glow
            // sitting at the muzzle between the shots.
            float rate = _gunAuto && _gun != null && _gun.holdingFire ? _shots.Count / 0.5f : 0f;
            for (int e = 0; e < 2; e++)
            {
                float ex = fx.EyeX(e);
                float power = muzzle != null && m.z > 0.02f ? EyePower(_gunSize, m, dir, ex, fx.Ipd) : 0f;
                if (power <= 0f)
                {
                    _limiters[e].ClearGlow(); // no gun to glow at
                }
                float glow = _limiters[e].Step(dt, rate, power);
                Vector2 at = m.z > 0.02f ? LensEffects.Project(m, ex) : new Vector2(LensEffects.W / 2f, LensEffects.H);
                fx.SetGlow(e, muzzle != null && m.z > 0.02f ? glow : 0f, at, at.x >= LensEffects.W / 2f ? 1f : -1f);
            }
        }

        private static float Gauss()
        {
            float u1 = 1f - Random.value, u2 = Random.value;
            return Mathf.Sqrt(-2f * Mathf.Log(u1)) * Mathf.Cos(2f * Mathf.PI * u2);
        }

        public void Destroy()
        {
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
                _full.RemoveAll(s => t - s.Key >= Window);
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
