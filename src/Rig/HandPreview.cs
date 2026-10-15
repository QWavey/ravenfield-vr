using System;
using System.IO;
using System.Linq;
using RavenfieldVR.Core;
using UnityEngine;

namespace RavenfieldVR.Rig
{
    /// <summary>
    /// Self-test picture of the glove: both hands built exactly as in VR (open, half-closed, fist) are rendered off-screen
    /// to BepInEx/RavenfieldVR-hands.png, so the hand model can be checked without a headset.
    /// </summary>
    internal static class HandPreview
    {
        private static void Save(Camera cam, RenderTexture rt, string path)
        {
            cam.Render();
            RenderTexture prev = RenderTexture.active;
            RenderTexture.active = rt;
            var tex = new Texture2D(rt.width, rt.height, TextureFormat.RGB24, false);
            tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
            tex.Apply();
            RenderTexture.active = prev;
            File.WriteAllBytes(path, tex.EncodeToPNG());
            UnityEngine.Object.Destroy(tex);
        }

        /// <summary>-rfvr-handshots: glove close-ups (written from the menu, then the game quits).</summary>
        public static bool CloseupsRequested => Environment.GetCommandLineArgs().Any(a => string.Equals(a, "-rfvr-handshots", StringComparison.OrdinalIgnoreCase));

        /// <summary>
        /// Close-ups of the right glove: rows open, at rest, half closed, fist, holding a grip (weapon thumb), pinching,
        /// pointing (Valve's pose); columns from the back of the hand, the palm and the thumb's side. One picture, 3 x 7 tiles.
        /// </summary>
        public static string CaptureCloseups(string path)
        {
            var root = new GameObject("RavenfieldVR Hand Closeups");
            root.transform.position = new Vector3(0f, 2000f, 0f);
            RenderTexture rt = null;
            Texture2D sheet = null;
            try
            {
                var poses = new (float[] curls, bool weaponThumb)[]
                {
                    (new[] { 0f, 0f, 0f, 0f, 0f }, false),
                    (new[] { 0.1f, 0.12f, 0.16f, 0.2f, 0.24f }, false),
                    (new[] { 0.35f, 0.45f, 0.5f, 0.55f, 0.6f }, false),
                    (new[] { 1f, 1f, 1f, 1f, 1f }, false),
                    (new[] { 0.3f, 0.25f, 0.72f, 0.74f, 0.76f }, true),
                    (new[] { 0.45f, 0.55f, 0.7f, 0.75f, 0.78f }, false),
                    (new[] { 0.45f, 0.05f, 0.82f, 0.84f, 0.86f }, false) // last row: Valve's own pointing pose (below)
                };
                // One hand per pose, a metre apart: a skinned mesh is skinned once per frame, so re-posing one hand
                // between renders of the same frame would show its first pose every time.
                var hands = new VRHand[poses.Length];
                for (int p = 0; p < poses.Length; p++)
                {
                    hands[p] = new VRHand(Input.Hand.Right, root.transform);
                    hands[p].Root.localPosition = new Vector3(0f, 0f, p * 1f);
                    hands[p].Root.localRotation = Quaternion.identity;
                    hands[p].Model.PreviewPose(poses[p].curls, poses[p].weaponThumb);
                }
                hands[poses.Length - 1].Model.PreviewPointing();
                var light = new GameObject("light").AddComponent<Light>();
                light.transform.SetParent(root.transform, false);
                light.type = LightType.Directional;
                light.transform.rotation = Quaternion.Euler(40f, -30f, 0f);
                light.intensity = 1.15f;
                var fill = new GameObject("fill").AddComponent<Light>();
                fill.transform.SetParent(root.transform, false);
                fill.type = LightType.Directional;
                fill.transform.rotation = Quaternion.Euler(-20f, 150f, 0f);
                fill.intensity = 0.45f;

                var camGo = new GameObject("cam");
                camGo.transform.SetParent(root.transform, false);
                Camera cam = camGo.AddComponent<Camera>();
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = new Color(0.55f, 0.6f, 0.66f);
                cam.fieldOfView = 34f;
                cam.nearClipPlane = 0.01f;
                cam.farClipPlane = 5f;
                cam.cullingMask = 1 << 18;
                cam.enabled = false;
                const int Tile = 400;
                rt = new RenderTexture(Tile, Tile, 24);
                cam.targetTexture = rt;
                sheet = new Texture2D(Tile * 3, Tile * poses.Length, TextureFormat.RGB24, false);
                Vector3 centre = new Vector3(0f, 0f, 0.035f);
                var views = new[]
                {
                    (new Vector3(0.36f, 0.04f, 0.035f), Vector3.forward),   // back of the hand
                    (new Vector3(-0.36f, 0.04f, 0.035f), Vector3.forward),  // palm
                    (new Vector3(-0.05f, 0.36f, 0.035f), Vector3.forward)   // thumb side
                };
                for (int p = 0; p < poses.Length; p++)
                {
                    for (int v = 0; v < views.Length; v++)
                    {
                        Vector3 eye = hands[p].Root.TransformPoint(views[v].Item1);
                        Vector3 at = hands[p].Root.TransformPoint(centre);
                        camGo.transform.SetPositionAndRotation(eye, Quaternion.LookRotation(at - eye, views[v].Item2));
                        cam.Render();
                        RenderTexture prev = RenderTexture.active;
                        RenderTexture.active = rt;
                        // Rows from the top: pose 0 first.
                        sheet.ReadPixels(new Rect(0, 0, Tile, Tile), v * Tile, (poses.Length - 1 - p) * Tile);
                        RenderTexture.active = prev;
                    }
                }
                sheet.Apply();
                File.WriteAllBytes(path, sheet.EncodeToPNG());
                foreach (VRHand h in hands)
                {
                    h.Destroy();
                }
                return null;
            }
            catch (Exception e)
            {
                Log.Exception("Hand close-ups", e);
                return e.Message;
            }
            finally
            {
                if (rt != null)
                {
                    rt.Release();
                    UnityEngine.Object.Destroy(rt);
                }
                if (sheet != null)
                {
                    UnityEngine.Object.Destroy(sheet);
                }
                UnityEngine.Object.Destroy(root);
            }
        }

        /// <summary>
        /// Every pose of the pose library (<see cref="HandPoses"/>), one right glove each with its name under it, seen from
        /// the palm side at three quarters: a sheet of 6 x 5 tiles in one picture.
        /// </summary>
        public static string CapturePoseSheet(string path)
        {
            var root = new GameObject("RavenfieldVR Hand Pose Sheet");
            root.transform.position = new Vector3(0f, 2100f, 0f);
            RenderTexture rt = null;
            Texture2D sheet = null;
            try
            {
                GripPose[] poses = HandPoses.Library;
                var hands = new VRHand[poses.Length];
                Font font = Resources.GetBuiltinResource<Font>("Arial.ttf");
                for (int p = 0; p < poses.Length; p++)
                {
                    hands[p] = new VRHand(Input.Hand.Right, root.transform);
                    hands[p].Root.localPosition = new Vector3(0f, 0f, p * 1f);
                    hands[p].Root.localRotation = Quaternion.identity;
                    hands[p].Model.PreviewPose(poses[p].Curls, poses[p].Thumb);
                    var label = new GameObject("label") { layer = 18 };
                    label.transform.SetParent(hands[p].Root, false);
                    label.transform.localPosition = new Vector3(-0.02f, -0.075f, 0.03f);
                    var text = label.AddComponent<TextMesh>();
                    text.font = font;
                    label.GetComponent<MeshRenderer>().sharedMaterial = font.material;
                    text.text = poses[p].Id.ToString();
                    text.fontSize = 48;
                    text.characterSize = 0.0022f;
                    text.anchor = TextAnchor.MiddleCenter;
                    text.color = Color.black;
                }
                var light = new GameObject("light").AddComponent<Light>();
                light.transform.SetParent(root.transform, false);
                light.type = LightType.Directional;
                light.transform.rotation = Quaternion.Euler(40f, 60f, 0f);
                light.intensity = 1.15f;
                light.cullingMask = 1 << 18; // it lives until the end of the frame: the game's own view must not be lit by it
                var camGo = new GameObject("cam");
                camGo.transform.SetParent(root.transform, false);
                Camera cam = camGo.AddComponent<Camera>();
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = new Color(0.62f, 0.66f, 0.7f);
                cam.fieldOfView = 36f;
                cam.nearClipPlane = 0.01f;
                cam.farClipPlane = 5f;
                cam.cullingMask = 1 << 18;
                cam.enabled = false;
                const int Tile = 300, Columns = 6;
                int rows = (poses.Length + Columns - 1) / Columns;
                rt = new RenderTexture(Tile, Tile, 24);
                cam.targetTexture = rt;
                sheet = new Texture2D(Tile * Columns, Tile * rows, TextureFormat.RGB24, false);
                for (int p = 0; p < poses.Length; p++)
                {
                    // From the palm side and a little below the thumb, where a held grip shows best.
                    Vector3 eye = hands[p].Root.TransformPoint(new Vector3(-0.3f, 0.12f, 0.09f));
                    Vector3 at = hands[p].Root.TransformPoint(new Vector3(0f, -0.01f, 0.035f));
                    camGo.transform.SetPositionAndRotation(eye, Quaternion.LookRotation(at - eye, Vector3.forward));
                    cam.Render();
                    RenderTexture prev = RenderTexture.active;
                    RenderTexture.active = rt;
                    sheet.ReadPixels(new Rect(0, 0, Tile, Tile), p % Columns * Tile, (rows - 1 - p / Columns) * Tile);
                    RenderTexture.active = prev;
                }
                sheet.Apply();
                File.WriteAllBytes(path, sheet.EncodeToPNG());
                foreach (VRHand h in hands)
                {
                    h.Destroy();
                }
                return null;
            }
            catch (Exception e)
            {
                Log.Exception("Hand pose sheet", e);
                return e.Message;
            }
            finally
            {
                if (rt != null)
                {
                    rt.Release();
                    UnityEngine.Object.Destroy(rt);
                }
                if (sheet != null)
                {
                    UnityEngine.Object.Destroy(sheet);
                }
                UnityEngine.Object.Destroy(root);
            }
        }

        public static string Capture(string path)
        {
            var root = new GameObject("RavenfieldVR Hand Preview");
            root.transform.position = new Vector3(0f, 2000f, 0f);
            RenderTexture rt = null;
            try
            {
                float[][] poses =
                {
                    new[] { 0f, 0f, 0f, 0f, 0f },
                    new[] { 0.35f, 0.45f, 0.5f, 0.55f, 0.6f },
                    new[] { 0.8f, 0.9f, 1f, 1f, 1f }
                };
                var hands = new VRHand[6];
                for (int i = 0; i < 6; i++)
                {
                    Input.Hand side = i % 2 == 0 ? Input.Hand.Left : Input.Hand.Right;
                    var h = new VRHand(side, root.transform);
                    hands[i] = h;
                    // Palms facing each other a little, fingers pointing away from the camera and up.
                    float x = (i / 2 - 1) * 0.3f + (i % 2 == 0 ? -0.07f : 0.07f);
                    h.Root.localPosition = new Vector3(x, 0f, 0.5f);
                    h.Root.localRotation = Quaternion.Euler(-60f, i % 2 == 0 ? 25f : -25f, i % 2 == 0 ? -70f : 70f);
                    h.Model.PreviewPose(poses[i / 2]);
                }
                var light = new GameObject("light").AddComponent<Light>();
                light.transform.SetParent(root.transform, false);
                light.type = LightType.Directional;
                light.transform.rotation = Quaternion.Euler(35f, 20f, 0f);
                light.intensity = 1.1f;

                var camGo = new GameObject("cam");
                camGo.transform.SetParent(root.transform, false);
                camGo.transform.localPosition = new Vector3(0f, 0.05f, 0f);
                Camera cam = camGo.AddComponent<Camera>();
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = new Color(0.55f, 0.6f, 0.66f);
                cam.fieldOfView = 70f;
                cam.nearClipPlane = 0.01f;
                cam.farClipPlane = 5f;
                cam.cullingMask = 1 << 18;
                cam.enabled = false;
                rt = new RenderTexture(1200, 600, 24);
                cam.targetTexture = rt;
                Save(cam, rt, path);
                // From the other side: the backs of the hands and the knuckles.
                camGo.transform.localPosition = new Vector3(0f, 0.05f, 1.0f);
                camGo.transform.localRotation = Quaternion.Euler(0f, 180f, 0f);
                Save(cam, rt, path.Replace(".png", "-back.png"));
                foreach (VRHand h in hands)
                {
                    h.Destroy();
                }
                return null;
            }
            catch (Exception e)
            {
                Log.Exception("Hand preview", e);
                return e.Message;
            }
            finally
            {
                if (rt != null)
                {
                    rt.Release();
                    UnityEngine.Object.Destroy(rt);
                }
                UnityEngine.Object.Destroy(root);
            }
        }
    }
}
