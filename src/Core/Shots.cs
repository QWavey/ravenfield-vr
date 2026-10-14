using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using BepInEx;
using RavenfieldVR.Rig;
using UnityEngine;

namespace RavenfieldVR.Core
{
    /// <summary>
    /// Pictures of what the headset shows, for checking the mod without wearing it: a mono render from the VR head (or
    /// any pose) with the head camera's settings plus the UI layer, written to BepInEx/RavenfieldVR-shots/. Used by the
    /// autotest (-rfvr-autotest -vr) and the intro staging (-rfvr-shots).
    /// </summary>
    internal static class Shots
    {
        private static readonly bool OnCommandLine = Environment.GetCommandLineArgs().Any(a => a.StartsWith("-rfvr-shots", StringComparison.OrdinalIgnoreCase));

        public static bool Enabled => OnCommandLine || AutoTest.Requested;

        public static string Dir => Path.Combine(Paths.BepInExRootPath, "RavenfieldVR-shots");

        private static bool _cleared;

        /// <summary>Renders from the VR head (or <paramref name="position"/> / <paramref name="rotation"/>) to a PNG named <paramref name="name"/>.</summary>
        public static void Capture(string name, Vector3? position = null, Quaternion? rotation = null, float fov = 90f)
        {
            VRRig rig = VRRig.Instance;
            Camera source = rig != null ? rig.Camera : Camera.main;
            if (source == null)
            {
                return;
            }
            GameObject go = null;
            RenderTexture rt = null;
            try
            {
                if (!_cleared)
                {
                    _cleared = true;
                    if (Directory.Exists(Dir))
                    {
                        foreach (string f in Directory.GetFiles(Dir, "*.png"))
                        {
                            File.Delete(f);
                        }
                    }
                }
                Directory.CreateDirectory(Dir);
                go = new GameObject("RavenfieldVR Shot");
                Camera cam = go.AddComponent<Camera>();
                cam.enabled = false;
                rig?.Suppressor.Ignore(cam);
                cam.CopyFrom(source);
                cam.stereoTargetEye = StereoTargetEyeMask.None;
                // What the headset shows: the world camera's layers and the UI camera's (the UI, or the loading screen).
                Camera ui = UI.UISystem.Instance != null ? UI.UISystem.Instance.UiCamera : null;
                cam.cullingMask = source.cullingMask | (ui != null ? ui.cullingMask : 1 << 5);
                cam.fieldOfView = fov;
                cam.nearClipPlane = Mathf.Min(source.nearClipPlane, 0.02f);
                go.transform.SetPositionAndRotation(position ?? source.transform.position, rotation ?? source.transform.rotation);
                rt = new RenderTexture(1280, 800, 24);
                cam.targetTexture = rt;
                cam.Render();
                RenderTexture prev = RenderTexture.active;
                RenderTexture.active = rt;
                var tex = new Texture2D(rt.width, rt.height, TextureFormat.RGB24, false);
                tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
                tex.Apply();
                RenderTexture.active = prev;
                string file = Path.Combine(Dir, Regex.Replace(name, @"[^A-Za-z0-9_.\-]+", "_") + ".png");
                File.WriteAllBytes(file, tex.EncodeToPNG());
                UnityEngine.Object.Destroy(tex);
                Log.Info("Shot: " + file);
            }
            catch (Exception e)
            {
                Log.Warn($"Shot '{name}' failed: {e.Message}");
            }
            finally
            {
                if (rt != null)
                {
                    rt.Release();
                    UnityEngine.Object.Destroy(rt);
                }
                if (go != null)
                {
                    UnityEngine.Object.Destroy(go);
                }
            }
        }
    }
}
