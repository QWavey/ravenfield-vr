using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace RavenfieldVR.Core
{
    public enum GraphicsPreset { Game, Low, Medium, High, Ultra }

    /// <summary>
    /// VR graphics presets. Rendering twice at headset resolution and 90 Hz costs far more than the flat game, so a
    /// preset caps what matters most for GPU time — eye resolution, MSAA, shadow distance/cascades/resolution, LOD
    /// bias, per-pixel lights, texture size, vegetation density/distance, terrain detail and view distance — on top
    /// of the game's own options. "Game" leaves everything to the game's options menu. Applied when VR starts,
    /// whenever the game re-applies its options (level start, Options > Save) and live when the preset changes.
    /// </summary>
    public static class GraphicsPresets
    {
        private sealed class Profile
        {
            public float RenderScale;
            public int Msaa;
            public float ShadowDistance;
            public int Cascades;
            public ShadowResolution ShadowResolution;
            public ShadowQuality Shadows;
            public float LodBias;
            public int PixelLights;
            public int TextureLimit;
            public AnisotropicFiltering Anisotropic;
            public bool SoftParticles;
            public bool RealtimeReflections;
            public float DetailDensity;
            public float DetailDistance;
            public float TreeDistance;
            public float MinPixelError;
            public float FarClip;

            public Profile MemberwiseCloneProfile() => (Profile)MemberwiseClone();
        }

        private static readonly Dictionary<GraphicsPreset, Profile> Profiles = new Dictionary<GraphicsPreset, Profile>
        {
            // Tuned for multi-pass VR (2026-10-24): every shadow pass, MSAA sample and pixel light is paid once per eye, and
            // directional shadows add a depth pass of the whole scene per eye. The old presets cost far more than the game's own
            // levels: the user's 'Game' ran Ravenfield's 'Fastest' (no shadows, no pixel lights, LOD bias 0.3) smoothly, while
            // even Low added shadows and a doubled LOD and dropped the frame rate into the headset's reprojection (ghosting,
            // things lagging in front of the view); Ultra (scale 1.4, MSAA 4x, 4 cascades) ran 31-46 fps in play. Low is now about
            // the game's 'Fast', Medium its 'Simple', High its 'Good' and Ultra its 'Beautiful', each at most 2 cascades.
            [GraphicsPreset.Low] = new Profile
            {
                RenderScale = 0.8f, Msaa = 0, ShadowDistance = 15f, Cascades = 1, ShadowResolution = ShadowResolution.Low, Shadows = ShadowQuality.Disable,
                LodBias = 0.4f, PixelLights = 0, TextureLimit = 1, Anisotropic = AnisotropicFiltering.Disable, SoftParticles = false, RealtimeReflections = false,
                DetailDensity = 0.2f, DetailDistance = 30f, TreeDistance = 300f, MinPixelError = 12f, FarClip = 1200f
            },
            [GraphicsPreset.Medium] = new Profile
            {
                RenderScale = 0.9f, Msaa = 2, ShadowDistance = 30f, Cascades = 1, ShadowResolution = ShadowResolution.Low, Shadows = ShadowQuality.HardOnly,
                LodBias = 0.7f, PixelLights = 1, TextureLimit = 0, Anisotropic = AnisotropicFiltering.Enable, SoftParticles = false, RealtimeReflections = false,
                DetailDensity = 0.45f, DetailDistance = 50f, TreeDistance = 600f, MinPixelError = 8f, FarClip = 2000f
            },
            [GraphicsPreset.High] = new Profile
            {
                RenderScale = 1.0f, Msaa = 2, ShadowDistance = 60f, Cascades = 2, ShadowResolution = ShadowResolution.Medium, Shadows = ShadowQuality.All,
                LodBias = 1.0f, PixelLights = 2, TextureLimit = 0, Anisotropic = AnisotropicFiltering.Enable, SoftParticles = true, RealtimeReflections = false,
                DetailDensity = 0.7f, DetailDistance = 80f, TreeDistance = 1000f, MinPixelError = 5f, FarClip = 3000f
            },
            [GraphicsPreset.Ultra] = new Profile
            {
                RenderScale = 1.2f, Msaa = 4, ShadowDistance = 100f, Cascades = 2, ShadowResolution = ShadowResolution.High, Shadows = ShadowQuality.All,
                LodBias = 1.5f, PixelLights = 3, TextureLimit = 0, Anisotropic = AnisotropicFiltering.ForceEnable, SoftParticles = true, RealtimeReflections = true,
                DetailDensity = 1f, DetailDistance = 120f, TreeDistance = 1600f, MinPixelError = 3f, FarClip = 4000f
            },
        };

        private struct TerrainOriginal
        {
            public float DetailDensity, DetailDistance, TreeDistance, PixelError;
        }

        // What the game itself set for each terrain (after its own options), so presets can go up again as well as down.
        private static readonly Dictionary<Terrain, TerrainOriginal> Originals = new Dictionary<Terrain, TerrainOriginal>();

        /// <summary>Far plane limit for the VR camera, or infinity when the game decides.</summary>
        public static float FarClipCap { get; private set; } = float.PositiveInfinity;

        /// <summary>MSAA the VR camera should allow (null: as the game's camera).</summary>
        public static bool? ForceMsaa { get; private set; }

        public static void Apply()
        {
            if (!VRManager.Active)
            {
                return;
            }
            GraphicsPreset preset = VRConfig.Graphics.Value;
            if (!Profiles.TryGetValue(preset, out Profile p))
            {
                FarClipCap = float.PositiveInfinity;
                ForceMsaa = null;
                RestoreTerrains();
                // Switched to Game while running: the last preset's quality settings stayed. Once, on the change: restored on
                // every options save it put back the start-up quality over a Quality level the player had just picked.
                RestoreGameQuality();
                _gameQuality = null; // the next preset takes the game's quality as it is then
                return;
            }
            ApplyProfile(p);
            Log.Info($"Graphics preset {preset}: render scale {VRConfig.RenderScale.Value:0.0#}, MSAA {p.Msaa}x, shadows {p.ShadowDistance:0} m, " +
                     $"LOD bias {p.LodBias:0.0#}, view distance {p.FarClip:0} m.");
        }

        /// <summary>
        /// Perf bench: a preset with some of its settings changed, applied for the moment only (the saved settings are left
        /// alone; <see cref="Apply"/> and the live settings put the player's back). Returns what was applied.
        /// </summary>
        internal static string ApplyForBench(GraphicsPreset preset, float? renderScale = null, int? msaa = null, float? shadowDistance = null,
            int? cascades = null, bool? reflections = null, int? pixelLights = null, float? lodBias = null, ShadowResolution? shadowResolution = null)
        {
            if (!Profiles.TryGetValue(preset, out Profile src))
            {
                return "no such preset";
            }
            var p = (Profile)src.MemberwiseCloneProfile();
            p.RenderScale = renderScale ?? p.RenderScale;
            p.Msaa = msaa ?? p.Msaa;
            p.ShadowDistance = shadowDistance ?? p.ShadowDistance;
            p.Cascades = cascades ?? p.Cascades;
            p.RealtimeReflections = reflections ?? p.RealtimeReflections;
            p.PixelLights = pixelLights ?? p.PixelLights;
            p.LodBias = lodBias ?? p.LodBias;
            p.ShadowResolution = shadowResolution ?? p.ShadowResolution;
            ApplyProfile(p);
            UnityEngine.XR.XRSettings.eyeTextureResolutionScale = Mathf.Clamp(p.RenderScale, 0.5f, 2f);
            return $"{preset}: scale {p.RenderScale:0.0#}, MSAA {p.Msaa}x, shadows {p.ShadowDistance:0} m {p.Cascades} cascades {p.ShadowResolution}, "
                   + $"reflections {(p.RealtimeReflections ? "on" : "off")}, pixel lights {p.PixelLights}, LOD bias {p.LodBias:0.0#}";
        }

        /// <summary>The game's own quality settings, from before the first preset was applied.</summary>
        private static Profile _gameQuality;

        private static void RestoreGameQuality()
        {
            Profile g = _gameQuality;
            if (g == null)
            {
                return;
            }
            QualitySettings.antiAliasing = g.Msaa;
            QualitySettings.shadows = g.Shadows;
            QualitySettings.shadowDistance = g.ShadowDistance;
            QualitySettings.shadowCascades = g.Cascades;
            QualitySettings.shadowResolution = g.ShadowResolution;
            QualitySettings.lodBias = g.LodBias;
            QualitySettings.pixelLightCount = g.PixelLights;
            QualitySettings.masterTextureLimit = g.TextureLimit;
            QualitySettings.anisotropicFiltering = g.Anisotropic;
            QualitySettings.softParticles = g.SoftParticles;
            QualitySettings.realtimeReflectionProbes = g.RealtimeReflections;
        }

        private static void ApplyProfile(Profile p)
        {
            if (_gameQuality == null)
            {
                _gameQuality = new Profile
                {
                    Msaa = QualitySettings.antiAliasing, Shadows = QualitySettings.shadows, ShadowDistance = QualitySettings.shadowDistance,
                    Cascades = QualitySettings.shadowCascades, ShadowResolution = QualitySettings.shadowResolution, LodBias = QualitySettings.lodBias,
                    PixelLights = QualitySettings.pixelLightCount, TextureLimit = QualitySettings.masterTextureLimit,
                    Anisotropic = QualitySettings.anisotropicFiltering, SoftParticles = QualitySettings.softParticles,
                    RealtimeReflections = QualitySettings.realtimeReflectionProbes
                };
            }
            // Only what differs: this runs several times at start-up and again at every level start, and some of
            // these setters are not free (MSAA touches the headset's eye targets, the texture limit reloads textures).
            if (QualitySettings.antiAliasing != p.Msaa) QualitySettings.antiAliasing = p.Msaa;
            if (QualitySettings.shadows != p.Shadows) QualitySettings.shadows = p.Shadows;
            if (!Mathf.Approximately(QualitySettings.shadowDistance, p.ShadowDistance)) QualitySettings.shadowDistance = p.ShadowDistance;
            if (QualitySettings.shadowCascades != p.Cascades) QualitySettings.shadowCascades = p.Cascades;
            if (QualitySettings.shadowResolution != p.ShadowResolution) QualitySettings.shadowResolution = p.ShadowResolution;
            if (!Mathf.Approximately(QualitySettings.lodBias, p.LodBias)) QualitySettings.lodBias = p.LodBias;
            if (QualitySettings.pixelLightCount != p.PixelLights) QualitySettings.pixelLightCount = p.PixelLights;
            if (QualitySettings.masterTextureLimit != p.TextureLimit) QualitySettings.masterTextureLimit = p.TextureLimit;
            if (QualitySettings.anisotropicFiltering != p.Anisotropic) QualitySettings.anisotropicFiltering = p.Anisotropic;
            if (QualitySettings.softParticles != p.SoftParticles) QualitySettings.softParticles = p.SoftParticles;
            if (QualitySettings.realtimeReflectionProbes != p.RealtimeReflections) QualitySettings.realtimeReflectionProbes = p.RealtimeReflections;
            FarClipCap = p.FarClip;
            ForceMsaa = p.Msaa > 0;
            ApplyTerrains(p);
        }

        /// <summary>The preset was picked (launcher, settings panel): it also sets the render scale, which stays adjustable.</summary>
        public static void OnPresetChanged()
        {
            if (Profiles.TryGetValue(VRConfig.Graphics.Value, out Profile p))
            {
                VRConfig.RenderScale.Value = p.RenderScale;
            }
            Apply();
        }

        private static void ApplyTerrains(Profile p)
        {
            foreach (Terrain t in Terrain.activeTerrains)
            {
                if (t == null)
                {
                    continue;
                }
                if (!Originals.TryGetValue(t, out TerrainOriginal o))
                {
                    o = new TerrainOriginal
                    {
                        DetailDensity = t.detailObjectDensity, DetailDistance = t.detailObjectDistance,
                        TreeDistance = t.treeDistance, PixelError = t.heightmapPixelError
                    };
                    Originals[t] = o;
                }
                t.detailObjectDensity = o.DetailDensity * p.DetailDensity;
                t.detailObjectDistance = Mathf.Min(o.DetailDistance, p.DetailDistance);
                t.treeDistance = Mathf.Min(o.TreeDistance, p.TreeDistance);
                t.heightmapPixelError = Mathf.Max(o.PixelError, p.MinPixelError);
            }
        }

        private static void RestoreTerrains()
        {
            foreach (KeyValuePair<Terrain, TerrainOriginal> kv in Originals)
            {
                if (kv.Key == null)
                {
                    continue;
                }
                kv.Key.detailObjectDensity = kv.Value.DetailDensity;
                kv.Key.detailObjectDistance = kv.Value.DetailDistance;
                kv.Key.treeDistance = kv.Value.TreeDistance;
                kv.Key.heightmapPixelError = kv.Value.PixelError;
            }
        }

        /// <summary>A new level: the game re-applied its options to fresh terrains.</summary>
        public static void OnLevelOptionsApplied()
        {
            var stale = new List<Terrain>();
            foreach (Terrain t in Originals.Keys)
            {
                if (t == null)
                {
                    stale.Add(t);
                }
            }
            foreach (Terrain t in stale)
            {
                Originals.Remove(t);
            }
            Apply();
        }
    }

    [HarmonyPatch(typeof(Options), "Apply")]
    internal static class OptionsApplyPatch
    {
        private static void Postfix(bool isLevelStart)
        {
            if (isLevelStart)
            {
                GraphicsPresets.OnLevelOptionsApplied();
            }
            else
            {
                GraphicsPresets.Apply();
            }
        }
    }
}
