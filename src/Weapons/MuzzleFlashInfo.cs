using UnityEngine;

namespace RavenfieldVR.Weapons
{
    /// <summary>
    /// What the game shows as a weapon's muzzle flash (Tasks2 212). On every shot the game plays the particle system on the
    /// muzzle that fired (on the muzzle itself or the first one under it: Weapon.FindMuzzleFlashParticles), and for a
    /// "loud" weapon it lights the scene for a frame (MuzzleFlashManager). A muzzle without particles shows no flash;
    /// a suppressed weapon is not loud. A mod's particle system may emit on only some shots (a burst with a probability).
    /// </summary>
    public struct MuzzleFlashInfo
    {
        /// <summary>A particle system plays at the muzzle on a shot.</summary>
        public bool Particles;
        /// <summary>The game's flash light comes on with a shot (a loud weapon).</summary>
        public bool Light;
        /// <summary>The flash's size across in metres (the largest particle at its start size, in the world).</summary>
        public float Size;
        /// <summary>The share of shots that emit anything (1 = every shot).</summary>
        public float Chance;
        /// <summary>The particle system's name (for the report).</summary>
        public string Name;

        public bool Any => Particles && Chance > 0f || Light;

        public static MuzzleFlashInfo Of(Weapon w)
        {
            var info = new MuzzleFlashInfo { Light = w != null && w.configuration != null && w.configuration.loud };
            Transform[] muzzles = w != null && w.configuration != null ? w.configuration.muzzles : null;
            if (muzzles == null)
            {
                return info;
            }
            foreach (Transform m in muzzles)
            {
                if (m == null)
                {
                    continue;
                }
                ParticleSystem ps = m.GetComponent<ParticleSystem>() ?? m.GetComponentInChildren<ParticleSystem>(true);
                if (ps == null)
                {
                    continue;
                }
                info.Particles = true;
                info.Name = info.Name ?? ps.name;
                foreach (ParticleSystem p in ps.GetComponentsInChildren<ParticleSystem>(true))
                {
                    ParticleSystem.MainModule main = p.main;
                    Vector3 s = p.transform.lossyScale;
                    float scale = Mathf.Max(Mathf.Abs(s.x), Mathf.Max(Mathf.Abs(s.y), Mathf.Abs(s.z)));
                    float size = main.startSize.constantMax * (main.scalingMode == ParticleSystemScalingMode.Shape ? 1f : scale);
                    if (main.startSize3D)
                    {
                        size = Mathf.Max(main.startSizeX.constantMax, Mathf.Max(main.startSizeY.constantMax, main.startSizeZ.constantMax)) * scale;
                    }
                    info.Size = Mathf.Max(info.Size, size);
                    info.Chance = Mathf.Max(info.Chance, EmitChance(p));
                }
            }
            return info;
        }

        /// <summary>The share of plays on which the system emits at all.</summary>
        private static float EmitChance(ParticleSystem p)
        {
            ParticleSystem.EmissionModule e = p.emission;
            if (!e.enabled)
            {
                return 0f;
            }
            if (e.rateOverTime.constantMax > 0f)
            {
                return 1f;
            }
            float chance = 0f;
            for (int i = 0; i < e.burstCount; i++)
            {
                ParticleSystem.Burst b = e.GetBurst(i);
                if (b.count.constantMax > 0f)
                {
                    chance = Mathf.Max(chance, b.probability);
                }
            }
            return chance;
        }

        public string Describe()
        {
            if (!Any)
            {
                return "none";
            }
            string parts = Particles
                ? $"particles '{Name}', {Size * 100f:0} cm, {(Chance >= 0.999f ? "every shot" : Chance <= 0f ? "never emits" : $"{Chance * 100f:0}% of shots")}"
                : "no particles";
            return parts + (Light ? ", the flash light" : ", no flash light (not loud)");
        }
    }
}
