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
        }

        private static readonly Dictionary<GraphicsPreset, Profile> Profiles = new Dictionary<GraphicsPreset, Profile>
        {
            [GraphicsPreset.Low] = new Profile
            {
                RenderScale = 0.8f, Msaa = 0, ShadowDistance = 35f, Cascades = 1, ShadowResolution = ShadowResolution.Low, Shadows = ShadowQuality.HardOnly,
                LodBias = 0.6f, PixelLights = 1, TextureLimit = 1, Anisotropic = AnisotropicFiltering.Disable, SoftParticles = false, RealtimeReflections = false,
                DetailDensity = 0.25f, DetailDistance = 35f, TreeDistance = 350f, MinPixelError = 12f, FarClip = 1200f
            },
            [GraphicsPreset.Medium] = new Profile
            {
                RenderScale = 1.0f, Msaa = 2, ShadowDistance = 60f, Cascades = 2, ShadowResolution = ShadowResolution.Medium, Shadows = ShadowQuality.All,
                LodBias = 1.0f, PixelLights = 2, TextureLimit = 0, Anisotropic = AnisotropicFiltering.Enable, SoftParticles = false, RealtimeReflections = false,
                DetailDensity = 0.55f, DetailDistance = 60f, TreeDistance = 700f, MinPixelError = 7f, FarClip = 2000f
            },
            [GraphicsPreset.High] = new Profile
            {
                RenderScale = 1.2f, Msaa = 4, ShadowDistance = 100f, Cascades = 2, ShadowResolution = ShadowResolution.High, Shadows = ShadowQuality.All,
                LodBias = 1.5f, PixelLights = 4, TextureLimit = 0, Anisotropic = AnisotropicFiltering.ForceEnable, SoftParticles = true, RealtimeReflections = true,
                DetailDensity = 0.85f, DetailDistance = 100f, TreeDistance = 1200f, MinPixelError = 4f, FarClip = 3000f
            },
            [GraphicsPreset.Ultra] = new Profile
            {
                RenderScale = 1.4f, Msaa = 4, ShadowDistance = 150f, Cascades = 4, ShadowResolution = ShadowResolution.VeryHigh, Shadows = ShadowQuality.All,
                LodBias = 2.0f, PixelLights = 6, TextureLimit = 0, Anisotropic = AnisotropicFiltering.ForceEnable, SoftParticles = true, RealtimeReflections = true,
                DetailDensity = 1f, DetailDistance = 160f, TreeDistance = 2000f, MinPixelError = 2f, FarClip = 4000f
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
                return;
            }
            QualitySettings.antiAliasing = p.Msaa;
            QualitySettings.shadows = p.Shadows;
            QualitySettings.shadowDistance = p.ShadowDistance;
            QualitySettings.shadowCascades = p.Cascades;
            QualitySettings.shadowResolution = p.ShadowResolution;
            QualitySettings.lodBias = p.LodBias;
            QualitySettings.pixelLightCount = p.PixelLights;
            QualitySettings.masterTextureLimit = p.TextureLimit;
            QualitySettings.anisotropicFiltering = p.Anisotropic;
            QualitySettings.softParticles = p.SoftParticles;
            QualitySettings.realtimeReflectionProbes = p.RealtimeReflections;
            FarClipCap = p.FarClip;
            ForceMsaa = p.Msaa > 0;
            ApplyTerrains(p);
            Log.Info($"Graphics preset {preset}: render scale {VRConfig.RenderScale.Value:0.0#}, MSAA {p.Msaa}x, shadows {p.ShadowDistance:0} m, " +
                     $"LOD bias {p.LodBias:0.0#}, view distance {p.FarClip:0} m.");
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
