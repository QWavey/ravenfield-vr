using System;
using System.IO;
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
                cam.Render();
                RenderTexture prev = RenderTexture.active;
                RenderTexture.active = rt;
                var tex = new Texture2D(rt.width, rt.height, TextureFormat.RGB24, false);
                tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
                tex.Apply();
                RenderTexture.active = prev;
                File.WriteAllBytes(path, tex.EncodeToPNG());
                UnityEngine.Object.Destroy(tex);
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
