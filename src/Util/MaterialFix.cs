using System.Collections.Generic;
using UnityEngine;

namespace RavenfieldVR.Util
{
    /// <summary>
    /// Repairs materials that would render magenta. Mod asset bundles can carry shaders compiled without the running
    /// graphics API, or materials whose shader was stripped, or empty material slots; some mods patch that up in a
    /// script on their live weapon, which copies built from the prefab (dropped weapons) or from mesh data (magazine
    /// props, the pouch) never get. A broken material is swapped for one using the game's own compiled shader of the
    /// same name (textures and colours kept), else Standard; an empty slot gets a neutral gunmetal. Working materials
    /// are never touched.
    /// </summary>
    internal static class MaterialFix
    {
        private static readonly Dictionary<Material, Material> Fixed = new Dictionary<Material, Material>();
        private static Material _gunmetal;

        public static bool IsBroken(Material m)
        {
            if (m == null)
            {
                return true;
            }
            Shader s = m.shader;
            return s == null || !s.isSupported || s.name == "Hidden/InternalErrorShader";
        }

        public static Material Fix(Material m)
        {
            if (!IsBroken(m))
            {
                return m;
            }
            if (m == null)
            {
                return Gunmetal();
            }
            if (Fixed.TryGetValue(m, out Material done) && done != null)
            {
                return done;
            }
            Shader shader = m.shader != null && m.shader.name != "Hidden/InternalErrorShader" ? Shader.Find(m.shader.name) : null;
            if (shader == null || !shader.isSupported)
            {
                shader = Usable("Standard") ?? Usable("Legacy Shaders/Diffuse");
            }
            if (shader == null)
            {
                return m;
            }
            // A copy keeps the serialized properties; those the new shader shares by name (_MainTex, _Color, _BumpMap,
            // _Metallic...) carry over.
            done = new Material(m) { shader = shader, name = m.name + " (repaired)" };
            if (Fixed.Count >= 128)
            {
                PruneGone(); // (sources from mod bundles unloaded since: their entries pinned both materials for good)
            }
            Fixed[m] = done;
            return done;
        }

        private static readonly List<Material> GoneSources = new List<Material>();

        private static void PruneGone()
        {
            GoneSources.Clear();
            foreach (KeyValuePair<Material, Material> e in Fixed)
            {
                if (e.Key == null || e.Value == null)
                {
                    GoneSources.Add(e.Key);
                }
            }
            foreach (Material k in GoneSources)
            {
                Fixed.Remove(k);
            }
            GoneSources.Clear();
        }

        /// <summary>The array with broken entries repaired, or the same array when nothing needed it.</summary>
        public static Material[] Fix(Material[] materials)
        {
            if (materials == null)
            {
                return null;
            }
            Material[] result = materials;
            for (int i = 0; i < materials.Length; i++)
            {
                Material repaired = IsBroken(materials[i]) ? Fix(materials[i]) : materials[i];
                if (!ReferenceEquals(repaired, materials[i])) // (unrepairable, it stays as it was and is not counted as repaired)
                {
                    if (ReferenceEquals(result, materials))
                    {
                        result = (Material[])materials.Clone();
                    }
                    result[i] = repaired;
                }
            }
            return result;
        }

        /// <summary>Repairs a renderer's materials in place; true when anything changed.</summary>
        public static bool Apply(Renderer r)
        {
            if (r == null || r is ParticleSystemRenderer || r is LineRenderer || r is TrailRenderer)
            {
                return false;
            }
            Material[] current = r.sharedMaterials;
            Material[] repaired = Fix(current);
            if (ReferenceEquals(repaired, current))
            {
                return false;
            }
            r.sharedMaterials = repaired;
            return true;
        }

        /// <summary>Repairs every renderer under <paramref name="root"/>; returns how many were changed.</summary>
        public static int ApplyAll(GameObject root)
        {
            int changed = 0;
            if (root == null)
            {
                return 0;
            }
            foreach (Renderer r in root.GetComponentsInChildren<Renderer>(true))
            {
                if (Apply(r))
                {
                    changed++;
                }
            }
            return changed;
        }

        private static Shader Usable(string name)
        {
            Shader s = Shader.Find(name);
            return s != null && s.isSupported ? s : null;
        }

        private static Material Gunmetal()
        {
            if (_gunmetal == null)
            {
                Shader s = Usable("Standard") ?? Usable("Legacy Shaders/Diffuse");
                if (s == null)
                {
                    return null; // (no usable shader: the slot stays empty, as it was; new Material(null) threw for every one)
                }
                _gunmetal = new Material(s) { name = "RavenfieldVR gunmetal", color = new Color(0.16f, 0.16f, 0.17f) };
                if (_gunmetal.HasProperty("_Metallic"))
                {
                    _gunmetal.SetFloat("_Metallic", 0.55f);
                    _gunmetal.SetFloat("_Glossiness", 0.45f);
                }
            }
            return _gunmetal;
        }
    }
}
