using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using RavenfieldVR.Core;
using RavenfieldVR.Runtime;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace RavenfieldVR.Rig
{
    /// <summary>
    /// The start-up intro (scene "Splash": a soldier or two walking across a dark stage in front of the title) is staged
    /// for a flat camera, so in the headset they slide past from one side to the other. In VR the walkers are turned
    /// around to come at you instead: each starts a few metres in front of the view and walks toward it, passing just
    /// beside you, with the title still behind them. They walk by their own animation (root motion along their facing),
    /// so turning them is all it takes; a walker that does not follow its new facing is put back as it was.
    /// The view is raised to standing eye height above their floor (the flat camera sits at about knee height).
    /// <c>-rfvr-introdump</c> logs the scene and what moves in it, also in flat play.
    /// </summary>
    public static class IntroStage
    {
        private static readonly bool Dump = Environment.GetCommandLineArgs().Any(a => a.Equals("-rfvr-introdump", StringComparison.OrdinalIgnoreCase));

        private enum Phase { Idle, Measuring, Verifying, Done }

        private sealed class Walker
        {
            public Transform T;
            public Vector3 Start;
            public Vector3 OriginalPosition;
            public Quaternion OriginalRotation;
            public Vector3 Heading;
        }

        private static Phase _phase = Phase.Idle;
        private static bool _isIntro;
        private static int _sceneHandle = -1;
        private static float _phaseStart;
        private static float _ground;
        private static bool _hasGround;
        private static readonly List<Walker> Walkers = new List<Walker>();

        public static void Install(MonoBehaviour host)
        {
            if (Dump && host != null)
            {
                host.StartCoroutine(Probe()); // the intro may already be loading when the plugin starts
            }
        }

        /// <summary>Per frame in VR (before the rig is placed).</summary>
        public static void Update()
        {
            Scene scene = SceneManager.GetActiveScene();
            if (scene.handle != _sceneHandle)
            {
                _sceneHandle = scene.handle;
                Walkers.Clear();
                _hasGround = false;
                _isIntro = scene.buildIndex == 0 && UnityEngine.Object.FindObjectOfType<GotoMenu>() != null;
                _phase = _isIntro ? Phase.Idle : Phase.Done;
            }
            if (!_isIntro || _phase == Phase.Done)
            {
                return;
            }
            try
            {
                Step(scene);
            }
            catch (Exception e)
            {
                _phase = Phase.Done;
                Log.Exception("Staging the intro for VR", e);
            }
        }

        private static void Step(Scene scene)
        {
            float now = Time.realtimeSinceStartup;
            switch (_phase)
            {
                case Phase.Idle:
                    // Everything animated that stands on the stage; who actually walks shows in a quarter of a second.
                    float groundSum = 0f;
                    foreach (GameObject root in scene.GetRootGameObjects())
                    {
                        if (!root.activeInHierarchy || root.GetComponentInChildren<Animator>() == null)
                        {
                            continue;
                        }
                        Transform t = root.transform;
                        Walkers.Add(new Walker { T = t, Start = t.position, OriginalPosition = t.position, OriginalRotation = t.rotation });
                        groundSum += t.position.y;
                    }
                    if (Walkers.Count == 0)
                    {
                        _phase = Phase.Done;
                        return;
                    }
                    _ground = groundSum / Walkers.Count;
                    _hasGround = true;
                    _phase = Phase.Measuring;
                    _phaseStart = now;
                    return;

                case Phase.Measuring:
                    if (now - _phaseStart < 0.25f)
                    {
                        return;
                    }
                    Turn(now - _phaseStart);
                    _phase = Walkers.Count > 0 ? Phase.Verifying : Phase.Done;
                    _phaseStart = now;
                    return;

                case Phase.Verifying:
                    if (now - _phaseStart < 0.4f)
                    {
                        return;
                    }
                    foreach (Walker w in Walkers)
                    {
                        if (w.T == null)
                        {
                            continue;
                        }
                        Vector3 moved = w.T.position - w.Start;
                        moved.y = 0f;
                        if (moved.magnitude > 0.05f && Vector3.Dot(moved.normalized, w.Heading) < 0.7f)
                        {
                            // Moved by something other than its facing: back to where the intro wanted it.
                            w.T.SetPositionAndRotation(w.OriginalPosition + moved, w.OriginalRotation);
                            Log.Info($"Intro: '{w.T.name}' does not walk along its facing; left as it was.");
                        }
                    }
                    _phase = Phase.Done;
                    return;
            }
        }

        /// <summary>
        /// The walkers that moved get a new start in front of the view and face it, side by side as they were in
        /// line, each passing a little beside the eye.
        /// </summary>
        private static void Turn(float elapsed)
        {
            Camera cam = GameCamera();
            if (cam == null)
            {
                Walkers.Clear();
                return;
            }
            Vector3 eye = cam.transform.position;
            Vector3 forward = cam.transform.forward;
            forward.y = 0f;
            if (forward.sqrMagnitude < 1e-4f)
            {
                Walkers.Clear();
                return;
            }
            forward.Normalize();
            Vector3 right = Vector3.Cross(Vector3.up, forward);
            float duration = IntroDuration();

            Walkers.RemoveAll(w =>
            {
                if (w.T == null)
                {
                    return true;
                }
                Vector3 v = (w.T.position - w.Start) / elapsed;
                v.y = 0f;
                return v.magnitude < 0.15f || v.magnitude > 15f; // standing props, or something flying through
            });
            if (Walkers.Count == 0)
            {
                return;
            }
            // Keep their order across the old path: the leader stays the leader, a little ahead of the others.
            Walkers.Sort((a, b) => Vector3.Dot(b.T.position - a.T.position, Heading(a.T)).CompareTo(0f));
            for (int i = 0; i < Walkers.Count; i++)
            {
                Walker w = Walkers[i];
                float speed = Mathf.Max(0.3f, ((w.T.position - w.Start) / elapsed).magnitude);
                // Far enough to take most of the intro to arrive, near enough to be seen at once.
                float distance = Mathf.Clamp(speed * duration * 0.85f, 3.5f, 11f) + i * 0.9f;
                float side = (i % 2 == 0 ? 1f : -1f) * (0.75f + 0.35f * (i / 2));
                Vector3 start = eye + forward * distance + right * side;
                start.y = w.T.position.y;
                Vector3 target = eye + right * side;
                target.y = start.y;
                Vector3 heading = (target - start).normalized;
                w.T.SetPositionAndRotation(start, Quaternion.LookRotation(heading, Vector3.up));
                w.Start = start;
                w.Heading = heading;
            }
            VRRig.Instance?.FadeCut();
            Log.Info($"Intro staged for VR: {Walkers.Count} walker(s) turned toward the view ({duration:0} s intro).");
        }

        private static Vector3 Heading(Transform t)
        {
            Vector3 f = t.forward;
            f.y = 0f;
            return f.sqrMagnitude > 1e-4f ? f.normalized : Vector3.forward;
        }

        private static float IntroDuration()
        {
            GotoMenu goTo = UnityEngine.Object.FindObjectOfType<GotoMenu>();
            return goTo != null ? Mathf.Clamp(goTo.duration, 4f, 25f) : 10f;
        }

        /// <summary>The intro's own camera (the enabled one that is not the mod's).</summary>
        private static Camera GameCamera()
        {
            VRRig rig = VRRig.Instance;
            foreach (Camera c in Camera.allCameras)
            {
                if (c.targetTexture == null && (rig == null || !c.transform.IsChildOf(rig.Root)))
                {
                    return c;
                }
            }
            return null;
        }

        /// <summary>
        /// While the intro plays, the view stands where its camera is, at standing eye height above the walkers'
        /// floor, facing the way the camera faces.
        /// </summary>
        public static bool TryGetAnchor(Camera following, out RigidPose anchor)
        {
            anchor = default;
            if (!_isIntro || !_hasGround || following == null)
            {
                return false;
            }
            float eyeHeight = VRConfig.EyeHeight.Value > 0.3f ? VRConfig.EyeHeight.Value : 1.65f;
            Vector3 p = following.transform.position;
            p.y = _ground + eyeHeight;
            anchor = new RigidPose(p, Quaternion.Euler(0f, following.transform.eulerAngles.y, 0f));
            return true;
        }

        // ---- diagnostics ----------------------------------------------------------------------------------

        /// <summary>Logs each scene of the first 25 seconds (cameras, root objects) and every animated object that moves.</summary>
        private static IEnumerator Probe()
        {
            float until = Time.realtimeSinceStartup + 25f;
            var last = new Dictionary<Transform, Vector3>();
            string lastScene = null;
            while (Time.realtimeSinceStartup < until)
            {
                Scene active = SceneManager.GetActiveScene();
                var sb = new StringBuilder();
                if (active.name != lastScene)
                {
                    lastScene = active.name;
                    sb.AppendLine($"Intro probe: scene '{active.name}' (build index {active.buildIndex}) at {Time.realtimeSinceStartup:0.0}s");
                    foreach (Camera c in Camera.allCameras)
                    {
                        sb.AppendLine($"  camera '{PathOf(c.transform)}' at {c.transform.position:F2} euler {c.transform.eulerAngles:F0} fov {c.fieldOfView:0}");
                    }
                    foreach (GameObject root in active.GetRootGameObjects())
                    {
                        if (root.activeSelf)
                        {
                            sb.AppendLine($"  root '{root.name}' at {root.transform.position:F1} children {root.transform.childCount}");
                        }
                    }
                }
                foreach (Animator a in UnityEngine.Object.FindObjectsOfType<Animator>())
                {
                    Transform t = a.transform;
                    Vector3 p = t.position;
                    if (last.TryGetValue(t, out Vector3 before) && (p - before).sqrMagnitude > 0.04f)
                    {
                        sb.AppendLine($"  moving '{PathOf(t)}' {before:F1} -> {p:F1} (forward {t.forward:F2})");
                    }
                    last[t] = p;
                }
                if (sb.Length > 0)
                {
                    Log.Info(sb.ToString());
                }
                yield return new WaitForSecondsRealtime(1f);
            }
        }

        private static string PathOf(Transform t)
        {
            string p = t.name;
            for (Transform q = t.parent; q != null; q = q.parent)
            {
                p = q.name + "/" + p;
            }
            return p;
        }
    }
}
