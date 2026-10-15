using UnityEngine;

namespace RavenfieldVR.Weapons
{
    /// <summary>
    /// What the game shows as a weapon's muzzle flash (Tasks 212). On every shot the game plays the particle system on the
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
                // Only systems the game can play (Weapon.FindMuzzleFlashParticles looks among the active ones): a switched-off
                // child counted as a flash, and a loud weapon's light-only flash was skipped for it.
                ParticleSystem ps = Playable(m.GetComponent<ParticleSystem>(), w) ?? FirstPlayable(m, w);
                if (ps == null)
                {
                    continue;
                }
                info.Particles = true;
                info.Name = info.Name ?? ps.name;
                foreach (ParticleSystem p in ps.GetComponentsInChildren<ParticleSystem>(true))
                {
                    if (Playable(p, w) == null)
                    {
                        continue;
                    }
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

        /// <summary>
        /// The muzzle's particle systems the game plays on a shot (the first active one on it or under it), as the game
        /// finds them; active counted up to the weapon, so a weapon outside a scene reads the same.
        /// </summary>
        internal static ParticleSystem[] MuzzleSystems(Weapon w)
        {
            Transform[] muzzles = w != null && w.configuration != null ? w.configuration.muzzles : null;
            if (muzzles == null)
            {
                return new ParticleSystem[0];
            }
            var list = new System.Collections.Generic.List<ParticleSystem>();
            foreach (Transform m in muzzles)
            {
                ParticleSystem ps = m == null ? null : Playable(m.GetComponent<ParticleSystem>(), w) ?? FirstPlayable(m, w);
                if (ps != null && !list.Contains(ps))
                {
                    list.Add(ps);
                }
            }
            return list.ToArray();
        }

        private static ParticleSystem Playable(ParticleSystem p, Weapon w) =>
            p != null && WeaponAnalyzer.ActiveUpTo(p.transform, w.transform.parent) ? p : null;

        private static ParticleSystem FirstPlayable(Transform m, Weapon w)
        {
            foreach (ParticleSystem p in m.GetComponentsInChildren<ParticleSystem>(true))
            {
                if (Playable(p, w) != null)
                {
                    return p;
                }
            }
            return null;
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
