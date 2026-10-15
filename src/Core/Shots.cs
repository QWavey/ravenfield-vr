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
        public static void Capture(string name, Vector3? position = null, Quaternion? rotation = null, float fov = 90f) =>
            CaptureFirstClear(name, new[] { (position, rotation) }, fov);

        /// <summary>
        /// Renders from each pose in turn and saves the first picture that shows something. A camera inside a mesh draws
        /// one flat colour (the akimbo Berettas' close-ups were all salmon: the other pistol's mesh stretched over the
        /// camera); the last pose is saved whatever it shows.
        /// </summary>
        public static void CaptureFirstClear(string name, (Vector3? position, Quaternion? rotation)[] poses, float fov)
        {
            VRRig rig = VRRig.Instance;
            Camera source = rig != null ? rig.Camera : Camera.main;
            if (source == null || poses.Length == 0)
            {
                return;
            }
            GameObject go = null;
            RenderTexture rt = null;
            Texture2D tex = null;
            RenderTexture prev = null;
            bool swapped = false;
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
                rt = new RenderTexture(1280, 800, 24);
                cam.targetTexture = rt;
                tex = new Texture2D(rt.width, rt.height, TextureFormat.RGB24, false);
                for (int i = 0; i < poses.Length; i++)
                {
                    go.transform.SetPositionAndRotation(poses[i].position ?? source.transform.position, poses[i].rotation ?? source.transform.rotation);
                    cam.Render();
                    prev = RenderTexture.active;
                    RenderTexture.active = rt;
                    swapped = true;
                    tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
                    tex.Apply();
                    RenderTexture.active = prev;
                    swapped = false;
                    if (i < poses.Length - 1 && Flat(tex))
                    {
                        Log.Info($"Shot '{name}': view {i + 1} shows one flat colour (the camera is inside something), the next one taken");
                        continue;
                    }
                    break;
                }
                string file = Path.Combine(Dir, Regex.Replace(name, @"[^A-Za-z0-9_.\-]+", "_") + ".png");
                File.WriteAllBytes(file, tex.EncodeToPNG());
                Log.Info("Shot: " + file);
            }
            catch (Exception e)
            {
                Log.Warn($"Shot '{name}' failed: {e.Message}");
            }
            finally
            {
                // Also when a shot failed half way (a full disk): the picture and the active target were left behind.
                if (swapped)
                {
                    RenderTexture.active = prev;
                }
                if (tex != null)
                {
                    UnityEngine.Object.Destroy(tex);
                }
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

        /// <summary>The picture is nearly one colour: its brightness varies by less than 2% over a grid of samples.</summary>
        private static bool Flat(Texture2D tex)
        {
            double sum = 0, sq = 0;
            int n = 0;
            for (int y = 8; y < tex.height; y += tex.height / 24)
            {
                for (int x = 8; x < tex.width; x += tex.width / 32)
                {
                    Color c = tex.GetPixel(x, y);
                    double l = 0.2126 * c.r + 0.7152 * c.g + 0.0722 * c.b;
                    sum += l;
                    sq += l * l;
                    n++;
                }
            }
            double mean = sum / n;
            return Math.Sqrt(Math.Max(0, sq / n - mean * mean)) < 0.02;
        }
    }
}
