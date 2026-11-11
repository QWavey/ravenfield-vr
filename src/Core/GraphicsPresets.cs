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
            /// <summary>HDR colour targets (false: 8-bit ones, half the bandwidth; the same picture with post-processing off).</summary>
            public bool Hdr = true;

            /// <summary>
            /// The terrain drawn instanced (Terrain.drawInstanced): its patches in a few draws instead of one each, in every pass of
            /// both eyes. The game itself turns it on at its own top terrain quality, for every map's terrain (Options.Apply), so its
            /// terrain shaders take it; below that the terrain drew patch by patch. Never turned off where the game turned it on.
            /// </summary>
            public bool TerrainInstanced = true;

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
                DetailDensity = 0.2f, DetailDistance = 30f, TreeDistance = 300f, MinPixelError = 12f, FarClip = 1200f, Hdr = false
            },
            [GraphicsPreset.Medium] = new Profile
            {
                RenderScale = 0.9f, Msaa = 2, ShadowDistance = 30f, Cascades = 1, ShadowResolution = ShadowResolution.Low, Shadows = ShadowQuality.HardOnly,
                LodBias = 0.7f, PixelLights = 1, TextureLimit = 0, Anisotropic = AnisotropicFiltering.Enable, SoftParticles = false, RealtimeReflections = false,
                DetailDensity = 0.45f, DetailDistance = 50f, TreeDistance = 600f, MinPixelError = 8f, FarClip = 2000f, Hdr = false
            },
            [GraphicsPreset.High] = new Profile
            {
                RenderScale = 1.0f, Msaa = 2, ShadowDistance = 60f, Cascades = 2, ShadowResolution = ShadowResolution.Medium, Shadows = ShadowQuality.All,
                LodBias = 1.0f, PixelLights = 2, TextureLimit = 0, Anisotropic = AnisotropicFiltering.Enable, SoftParticles = false, RealtimeReflections = false,
                DetailDensity = 0.7f, DetailDistance = 80f, TreeDistance = 1000f, MinPixelError = 5f, FarClip = 3000f
            },
            // Ultra's textures filtered as each asks (Enable), not forced to 16x: forced, the filter alone took 2.9 of Ultra's 8.9 ms
            // of GPU a frame (the frozen bench, 2026-10-24: 5.99 ms with High's filtering; High's grass, trees and view distance,
            // reflections off and soft particles off each saved nothing measurable).
            [GraphicsPreset.Ultra] = new Profile
            {
                RenderScale = 1.2f, Msaa = 4, ShadowDistance = 100f, Cascades = 2, ShadowResolution = ShadowResolution.High, Shadows = ShadowQuality.All,
                LodBias = 1.5f, PixelLights = 3, TextureLimit = 0, Anisotropic = AnisotropicFiltering.Enable, SoftParticles = true, RealtimeReflections = true,
                DetailDensity = 1f, DetailDistance = 120f, TreeDistance = 1600f, MinPixelError = 3f, FarClip = 4000f
            },
        };

        private struct TerrainOriginal
        {
            public float DetailDensity, DetailDistance, TreeDistance, PixelError;
            public bool Instanced;
        }

        // What the game itself set for each terrain (after its own options), so presets can go up again as well as down.
        private static readonly Dictionary<Terrain, TerrainOriginal> Originals = new Dictionary<Terrain, TerrainOriginal>();

        // ---- holding the frame rate (VRConfig.HoldFrameRate) ------------------------------------------------------------------

        /// <summary>How many steps the preset is held below itself for the frame rate (0: as set; up to <see cref="HoldSteps"/>).</summary>
        internal static int HoldLevel { get; private set; }
        private const int HoldSteps = 6;

        /// <summary>
        /// The eye resolution to draw at: the setting's, and at the hold's last step 15% less (never below 60% unless the setting
        /// itself is): the GPU's own lever, where the steps before it are mostly the CPU's (the user's Ultra had the GPU at 11 ms).
        /// </summary>
        internal static float EffectiveRenderScale()
        {
            float scale = VRConfig.RenderScale.Value;
            if (HoldLevel >= 6)
            {
                scale = Mathf.Max(Mathf.Min(scale, 0.6f), scale * 0.85f);
            }
            return Mathf.Clamp(scale, 0.5f, 2f);
        }
        private static int _missedWindows, _roomyWindows, _upWait = 10;
        private static float _lastStepDown = -100f, _lastStepUp = -100f;

        // The last step (the render scale) only after the GPU was heavy for _scaleNeeds windows in a row, as the late windows are counted;
        // each time it had to be given back (the GPU light again: the CPU the cause), the next needs twice as many. One heavy window (a
        // blast, a load) took it, the next gave it back: the user's Ultra session of 2026-11-07 went 5 -> 6 -> 5 ten times in ten
        // minutes, each change a new pair of eye textures (a hitch), with the GPU at 4.6 of 11.1 ms.
        private static int _gpuHeavyWindows, _scaleNeeds = 2;
        private static float _lastScaleBack = -1000f;
        private static bool _gpuEverTimed;

        /// <summary>
        /// The preset held down <see cref="HoldLevel"/> steps, the costliest for the CPU in multi-pass first (each is paid once per eye;
        /// the user's Medium ran at half rate with the GPU at 7 ms, Tasks 348): the shadow distance shortened and the level's tiny distant
        /// objects hidden (<see cref="TinyObjects"/>, which looks at the hold itself), then shadows off, the LOD
        /// bias lowered toward the game's own 0.3, the grass thinned, no pixel lights; last the render scale (<see cref="EffectiveRenderScale"/>).
        /// </summary>
        private static Profile Adapted(Profile p)
        {
            if (HoldLevel <= 0)
            {
                return p;
            }
            Profile a = p.MemberwiseCloneProfile();
            if (HoldLevel >= 1)
            {
                a.ShadowDistance = Mathf.Max(10f, a.ShadowDistance * 0.6f);
            }
            if (HoldLevel >= 2)
            {
                a.Shadows = ShadowQuality.Disable;
            }
            if (HoldLevel >= 3)
            {
                a.LodBias = Mathf.Max(0.3f, a.LodBias - 0.3f);
            }
            if (HoldLevel >= 4)
            {
                a.DetailDensity *= 0.5f;
                a.DetailDistance *= 0.7f;
            }
            if (HoldLevel >= 5)
            {
                a.PixelLights = 0;
            }
            return a;
        }

        /// <summary>A VR start begins with the preset as set: a hold from an earlier VR session kept its steps (and the smaller eyes).</summary>
        internal static void ResetHold()
        {
            HoldLevel = 0;
            _upWait = 10;
            _missedWindows = _roomyWindows = 0;
            _gpuHeavyWindows = 0;
            _scaleNeeds = 2;
            _gpuEverTimed = false;
        }

        /// <summary>
        /// Whether the last step (the render scale, the GPU's lever) helps: with the GPU timed well inside the refresh interval the
        /// frames are late for the CPU, and fewer pixels only blur the view (the user's Medium: the GPU at 3.3 ms of 11.1 ms, frames
        /// at half rate). Taken at 70% of the interval or more, given back under 45% once taken (about 62% at the full scale: no
        /// see-saw). The GPU not timed (-1): it may help, as before.
        /// </summary>
        internal static bool RenderScaleHelps(float gpuMs, float budgetMs, bool taken)
        {
            if (gpuMs < 0f || budgetMs <= 0f)
            {
                return true;
            }
            return gpuMs >= budgetMs * (taken ? 0.45f : 0.7f);
        }

        /// <summary>
        /// The last few seconds' share of frames on time (FrameBudget, every 3 s of play). Two windows in a row under 85%: a step down.
        /// Thirty seconds at 98% or more: a step back up, and if that step is taken back within 15 s, the next waits twice as long (up
        /// to two minutes), so it does not see-saw on the edge.
        /// </summary>
        internal static void OnRecentFrames(int percentOnTime, float gpuMs = -1f, float budgetMs = 0f)
        {
            bool on = VRConfig.HoldFrameRate.Value && Profiles.ContainsKey(VRConfig.Graphics.Value);
            if (!on)
            {
                if (HoldLevel != 0)
                {
                    HoldLevel = 0;
                    _upWait = 10;
                    ApplyRenderScaleIfHeld();
                    Apply();
                }
                _missedWindows = _roomyWindows = 0;
                _gpuHeavyWindows = 0;
                _scaleNeeds = 2;
                return;
            }
            float now = Time.unscaledTime;
            _missedWindows = percentOnTime < 85 ? _missedWindows + 1 : 0;
            // (One window a little short takes two back off the count, not all of it: with half a minute of windows at 98% asked for in
            // a row, a single one at 96%, a few late frames at a spawn or a collection, began the count again each time, and a
            // session that ran well stayed held down to its end; scout, 2026-11-11. Under 93% it starts again as before.)
            _roomyWindows = percentOnTime >= 98 ? _roomyWindows + 1 : percentOnTime >= 93 ? Mathf.Max(0, _roomyWindows - 2) : 0;
            // (A window with no GPU time counts as heavy only where the GPU was never timed: then the scale may help, as before.)
            _gpuEverTimed |= gpuMs >= 0f;
            bool heavy = gpuMs >= 0f ? RenderScaleHelps(gpuMs, budgetMs, false) : !_gpuEverTimed;
            _gpuHeavyWindows = heavy ? _gpuHeavyWindows + 1 : 0;
            if (_scaleNeeds > 2 && now - _lastScaleBack > 300f)
            {
                _scaleNeeds = 2; // five minutes with the scale left alone: as at the start
            }
            if (HoldLevel == HoldSteps && !RenderScaleHelps(gpuMs, budgetMs, true))
            {
                HoldLevel--;
                _lastStepUp = now;
                _lastScaleBack = now;
                _scaleNeeds = Mathf.Min(16, _scaleNeeds * 2);
                _gpuHeavyWindows = 0;
                ApplyRenderScaleIfHeld();
                _missedWindows = _roomyWindows = 0;
                Log.Info($"Frame rate: the GPU at {gpuMs:0.0} ms of {budgetMs:0.0} ms, the frames late for the CPU: the render scale back (step {HoldLevel} of {HoldSteps}).");
                Apply();
                return;
            }
            int deepest = HoldLevel == HoldSteps - 1 && _gpuHeavyWindows < _scaleNeeds ? HoldSteps - 1 : HoldSteps;
            if (_missedWindows >= 2 && HoldLevel < deepest)
            {
                if (now - _lastStepUp < 15f)
                {
                    _upWait = Mathf.Min(40, _upWait * 2); // the step up did not hold
                }
                HoldLevel++;
                _lastStepDown = now;
                ApplyRenderScaleIfHeld();
                _missedWindows = _roomyWindows = 0;
                Log.Info($"Frame rate: {percentOnTime}% of frames on time, the preset held down to step {HoldLevel} of {HoldSteps}.");
                Apply();
            }
            else if (_roomyWindows >= _upWait && HoldLevel > 0)
            {
                HoldLevel--;
                _lastStepUp = now;
                ApplyRenderScaleIfHeld();
                _missedWindows = _roomyWindows = 0;
                Log.Info($"Frame rate: room again, the preset back up to step {HoldLevel} of {HoldSteps}.");
                Apply();
            }
        }

        /// <summary>The eyes' resolution as the hold leaves it (only a change: a new scale reallocates the eye targets).</summary>
        private static void ApplyRenderScaleIfHeld()
        {
            float scale = EffectiveRenderScale();
            if (!Mathf.Approximately(UnityEngine.XR.XRSettings.eyeTextureResolutionScale, scale))
            {
                UnityEngine.XR.XRSettings.eyeTextureResolutionScale = scale;
            }
        }

        /// <summary>Far plane limit for the VR camera, or infinity when the game decides.</summary>
        public static float FarClipCap { get; private set; } = float.PositiveInfinity;

        /// <summary>MSAA the VR camera should allow (null: as the game's camera).</summary>
        public static bool? ForceMsaa { get; private set; }

        /// <summary>
        /// HDR the VR camera may use (null: as the game's camera). With post-processing off nothing reads past 1 (no bloom, no
        /// tonemapping), so 8-bit targets give the same picture at half the colour bandwidth, paid per eye and per MSAA sample.
        /// </summary>
        public static bool? AllowHdr { get; private set; }

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
                AllowHdr = null;
                RestoreTerrains();
                // Switched to Game while running: the last preset's quality settings stayed. Once, on the change: restored on
                // every options save it put back the start-up quality over a Quality level the player had just picked. The copy is
                // of the game's level as it is now: taken again whenever the game applies another level (OnGameOptionsApplied).
                // (Re-applying the level itself does nothing: Unity's SetQualityLevel with the level already set is no change, and
                // the Game preset kept Medium's shadows, MSAA and LOD; the user's session of 2026-10-28, 1% of frames on time.)
                RestoreGameQuality();
                _gameQuality = null; // the next preset takes the game's quality as it is then
                return;
            }
            ApplyProfile(Adapted(p));
            Log.Info($"Graphics preset {preset}: render scale {VRConfig.RenderScale.Value:0.0#}, MSAA {p.Msaa}x, shadows {p.ShadowDistance:0} m, " +
                     $"LOD bias {p.LodBias:0.0#}, view distance {p.FarClip:0} m{(HoldLevel > 0 ? $"; held down {HoldLevel} step(s) for the frame rate" : "")}.");
        }

        /// <summary>
        /// Perf bench: a preset with some of its settings changed, applied for the moment only (the saved settings are left
        /// alone; <see cref="Apply"/> and the live settings put the player's back). Returns what was applied.
        /// </summary>
        internal static string ApplyForBench(GraphicsPreset preset, float? renderScale = null, int? msaa = null, float? shadowDistance = null,
            int? cascades = null, bool? reflections = null, int? pixelLights = null, float? lodBias = null, ShadowResolution? shadowResolution = null,
            ShadowQuality? shadows = null, bool? softParticles = null, bool? hdr = null, GraphicsPreset? terrainOf = null,
            AnisotropicFiltering? anisotropic = null, bool? terrainInstanced = null)
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
            p.Shadows = shadows ?? p.Shadows;
            p.SoftParticles = softParticles ?? p.SoftParticles;
            p.Hdr = hdr ?? p.Hdr;
            p.Anisotropic = anisotropic ?? p.Anisotropic;
            p.TerrainInstanced = terrainInstanced ?? p.TerrainInstanced;
            if (terrainOf.HasValue && Profiles.TryGetValue(terrainOf.Value, out Profile t))
            {
                // Grass, trees, terrain detail and view distance of another preset.
                p.DetailDensity = t.DetailDensity;
                p.DetailDistance = t.DetailDistance;
                p.TreeDistance = t.TreeDistance;
                p.MinPixelError = t.MinPixelError;
                p.FarClip = t.FarClip;
            }
            ApplyProfile(p);
            UnityEngine.XR.XRSettings.eyeTextureResolutionScale = Mathf.Clamp(p.RenderScale, 0.5f, 2f);
            string shadowText = p.Shadows == ShadowQuality.Disable ? "off" : $"{p.ShadowDistance:0} m {p.Cascades} cascades {p.ShadowResolution}";
            return $"{preset}: scale {p.RenderScale:0.0#}, MSAA {p.Msaa}x, shadows {shadowText}, reflections {(p.RealtimeReflections ? "on" : "off")}, "
                   + $"pixel lights {p.PixelLights}, LOD bias {p.LodBias:0.0#}, soft particles {(p.SoftParticles ? "on" : "off")}, HDR {(p.Hdr ? "as the game" : "off")}, "
                   + $"grass x{p.DetailDensity:0.0#} to {p.DetailDistance:0} m, trees {p.TreeDistance:0} m, terrain error {p.MinPixelError:0}, anisotropic {p.Anisotropic}";
        }

        /// <summary>
        /// Perf bench: the game's own quality, as the Game preset leaves it (the user's 'Fastest' ran smoothly while every preset
        /// ghosted), at the given render scale. Put back by <see cref="Apply"/> as the other bench steps.
        /// </summary>
        internal static string ApplyGameForBench(float renderScale)
        {
            FarClipCap = float.PositiveInfinity;
            ForceMsaa = null;
            AllowHdr = null;
            RestoreTerrains();
            RestoreGameQuality();
            UnityEngine.XR.XRSettings.eyeTextureResolutionScale = Mathf.Clamp(renderScale, 0.5f, 2f);
            int level = QualitySettings.GetQualityLevel();
            string name = level >= 0 && level < QualitySettings.names.Length ? QualitySettings.names[level] : "?";
            return $"the game's own quality '{name}': scale {renderScale:0.0#}, MSAA {QualitySettings.antiAliasing}x, shadows "
                   + $"{(QualitySettings.shadows == ShadowQuality.Disable ? "off" : $"{QualitySettings.shadowDistance:0} m")}, pixel lights {QualitySettings.pixelLightCount}, "
                   + $"LOD bias {QualitySettings.lodBias:0.0#}";
        }

        /// <summary>The game's own quality settings, from before the first preset was applied (of the level in <see cref="_gameQualityLevel"/>).</summary>
        private static Profile _gameQuality;
        private static int _gameQualityLevel = -1;

        /// <summary>
        /// The game applied its options (the main menu's Save): with another Quality level than the one the copy of its settings is
        /// of, that level's own settings are what is set now (before a preset goes over them), so the copy is taken again. Kept,
        /// switching to Game put back the level from VR start over the one the player had saved since (round-4 scout).
        /// </summary>
        internal static void OnGameOptionsApplied()
        {
            if (_gameQuality != null && QualitySettings.GetQualityLevel() != _gameQualityLevel)
            {
                _gameQuality = null; // (taken again by the preset applied next, from the level's settings as they are now)
            }
        }

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
                _gameQualityLevel = QualitySettings.GetQualityLevel();
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
            AllowHdr = p.Hdr ? (bool?)null : false;
            ApplyTerrains(p);
        }

        /// <summary>The preset was picked (launcher, settings panel): it also sets the render scale, which stays adjustable.</summary>
        public static void OnPresetChanged()
        {
            HoldLevel = 0; // (a preset picked starts as itself: the frame rate hold steps down from it again if it must)
            _missedWindows = _roomyWindows = 0; // (a bad window of the last preset is no count against this one)
            _upWait = 10;
            if (Profiles.TryGetValue(VRConfig.Graphics.Value, out Profile p))
            {
                VRConfig.RenderScale.Value = p.RenderScale;
            }
            // The eyes back at the setting's own scale: the hold's last step lowers them, and only a changed render scale or a hold
            // step writes them, so picking Game (no render scale of its own) from a held preset left them 15% under the setting
            // for the rest of the session (graphics scout, 2026-11-03).
            ApplyRenderScaleIfHeld();
            Apply();
        }

        private static void SetIfChanged(float now, float want, System.Action<float> set)
        {
            if (!Mathf.Approximately(now, want))
            {
                set(want);
            }
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
                        TreeDistance = t.treeDistance, PixelError = t.heightmapPixelError, Instanced = t.drawInstanced
                    };
                    Originals[t] = o;
                }
                // Only what differs, as the quality settings above: the grass density's setter rebuilds the detail patches, and this runs
                // on every step of the frame rate hold (the render-scale steps leave the terrain as it is) and at every level start.
                SetIfChanged(t.detailObjectDensity, o.DetailDensity * p.DetailDensity, v => t.detailObjectDensity = v);
                SetIfChanged(t.detailObjectDistance, Mathf.Min(o.DetailDistance, p.DetailDistance), v => t.detailObjectDistance = v);
                SetIfChanged(t.treeDistance, Mathf.Min(o.TreeDistance, p.TreeDistance), v => t.treeDistance = v);
                SetIfChanged(t.heightmapPixelError, Mathf.Max(o.PixelError, p.MinPixelError), v => t.heightmapPixelError = v);
                bool instanced = o.Instanced || p.TerrainInstanced && !OlderMap();
                if (t.drawInstanced != instanced)
                {
                    t.drawInstanced = instanced;
                }
            }
        }

        /// <summary>
        /// A map the game gives its fallback terrain material (Options.Apply's own test, the same one: an engine version under 2020, or
        /// any with a minor version under 3, as 2021.1 too) and
        /// draws them instanced only at its own top terrain quality; the mod leaves them as the game set them (review, 2026-10-31).
        /// </summary>
        internal static bool OlderMap()
        {
            try
            {
                ModManager.EngineVersionInfo v = GameManager.instance != null ? GameManager.instance.sceneContentInfo.versionInfo : default;
                return GameManager.instance != null && (v.majorVersion < 2020 || v.minorVersion < 3);
            }
            catch (System.Exception)
            {
                return true; // (unknown: as the game set it)
            }
        }

        /// <summary>
        /// VR stopping: the level's terrains and the game's quality settings as the game had them (a preset's stayed for the rest of
        /// the level in flat play: the terrain's detail, trees, pixel error and instancing, the shadows, MSAA and LOD; review).
        /// </summary>
        internal static void OnVrStopped()
        {
            RestoreTerrains();
            RestoreGameQuality();
            _gameQuality = null;
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
                kv.Key.drawInstanced = kv.Value.Instanced;
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
                GraphicsPresets.OnGameOptionsApplied(); // (before the preset goes over the level the game just set)
                GraphicsPresets.Apply();
            }
        }
    }
}
