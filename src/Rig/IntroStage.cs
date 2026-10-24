using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using RavenfieldVR.Core;
using RavenfieldVR.Runtime;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

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
    internal static class EyeHeightExtensions
    {
        /// <summary>The calibrated eye height, or a standing default before calibration.</summary>
        public static float ClampEye(this float h) => h > 0.3f ? h : 1.65f;
    }

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
                PlacedTitles.Clear();
                TitlesWithoutGround.Clear();
                _nextTitleScan = 0f;
                _hasGround = false;
                _isIntro = scene.buildIndex == 0 && UnityEngine.Object.FindObjectOfType<GotoMenu>() != null;
                _phase = _isIntro ? Phase.Idle : Phase.Done;
            }
            if (_isIntro && Time.unscaledTime >= _nextTitleScan)
            {
                // Text that switches on later in the intro too, for the whole intro: tied to the walkers' staging (done after
                // 0.65 s, or at once with no walkers), a title switched on later (the splash's text once the content has
                // loaded) was made a head-locked frame by the canvas manager instead (UI scout, 2026-10-23). Placed ones are
                // world space and skipped; a scene-wide search, so a few times a second.
                _nextTitleScan = Time.unscaledTime + 0.1f;
                PlaceTitleInWorld();
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
                    // Titles placed in the first moments, before the ground was measured, go to eye height now.
                    foreach (Canvas t in TitlesWithoutGround)
                    {
                        if (t != null)
                        {
                            Vector3 p = t.transform.position;
                            p.y = _ground + VRConfig.EyeHeight.Value.ClampEye();
                            t.transform.position = p;
                        }
                    }
                    TitlesWithoutGround.Clear();
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
            // Front first along their shared heading: a key per walker, so the comparison is consistent (measured along each
            // one's own heading, a < b and b < a could both hold, which a sort may answer with an exception; rig hunt, 2026-10-21).
            _along = Vector3.zero;
            foreach (Walker w in Walkers)
            {
                _along += Heading(w.T);
            }
            Walkers.Sort(FrontFirst); // (a static comparison: a lambda over a local made a closure every frame)
            for (int i = 0; i < Walkers.Count; i++)
            {
                Walker w = Walkers[i];
                float speed = Mathf.Max(0.3f, ((w.T.position - w.Start) / elapsed).magnitude);
                // Far enough to take most of the intro to arrive, near enough to be seen at once.
                // Close enough to reach you a little after the middle of the intro and walk on past before it cuts.
                float distance = Mathf.Clamp(speed * duration * 0.55f, 2.5f, 7f) + i * 0.6f;
                float side = (i % 2 == 0 ? 1f : -1f) * (1.1f + 0.4f * (i / 2)); // either side of the title, framing it
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
            if (Shots.Enabled && VRManager.Instance != null)
            {
                VRManager.Instance.StartCoroutine(IntroShots());
            }
        }

        private static IEnumerator IntroShots()
        {
            float waited = 0f;
            foreach (float at in new[] { 0.6f, 2.5f, 5f, 7.5f })
            {
                // Each picture at the time its name says (waited 2.5 s each after the first, they were 0.6 s late).
                yield return new WaitForSecondsRealtime(at - waited);
                waited = at;
                yield return new WaitForEndOfFrame();
                Shots.Capture($"intro-{at:0.0}s");
            }
        }

        private static Vector3 _along;
        private static readonly System.Comparison<Walker> FrontFirst = (a, b) => Vector3.Dot(b.T.position, _along).CompareTo(Vector3.Dot(a.T.position, _along));

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

        private const float TitleDistance = 12f;
        private const int WorldLayer = 0;
        private static readonly List<Canvas> PlacedTitles = new List<Canvas>();
        private static readonly List<Canvas> TitlesWithoutGround = new List<Canvas>();
        private static float _nextTitleScan;

        /// <summary>
        /// The intro's title and text are screen canvases. Left to the HUD conversion they would hang head-locked a few
        /// metres away and be drawn over everything, in front of soldiers who are further away. Here they become part of
        /// the scene instead: world-space, out behind where the walkers start, framed as the intro camera framed them, on
        /// a world layer so the soldiers walk in front of them. (The HUD conversion leaves world-space canvases alone.)
        /// </summary>
        private static void PlaceTitleInWorld()
        {
            Camera cam = GameCamera();
            if (cam == null)
            {
                return;
            }
            foreach (Canvas c in UnityEngine.Object.FindObjectsOfType<Canvas>())
            {
                if (c == null || !c.isRootCanvas || c.renderMode == RenderMode.WorldSpace || c.gameObject.scene.buildIndex != 0)
                {
                    continue;
                }
                var rect = (RectTransform)c.transform;
                Vector2 size = rect.rect.size;
                if (size.x < 10f || size.y < 10f)
                {
                    size = new Vector2(Screen.width, Screen.height) / Mathf.Max(0.01f, c.scaleFactor);
                }
                if (size.x < 10f || size.y < 10f)
                {
                    // A minimised window has no screen to size it by: placed at the next scan instead (as a world canvas it was
                    // never placed again, an empty title; scout, 2026-10-22).
                    continue;
                }
                Log.Info($"Intro: canvas '{c.name}' ({c.renderMode}, {size.x:0}x{size.y:0}, layer {c.gameObject.layer}) placed in the scene behind the walkers.");
                var scaler = c.GetComponent<CanvasScaler>();
                if (scaler != null)
                {
                    scaler.enabled = false;
                }
                c.renderMode = RenderMode.WorldSpace;
                rect.pivot = new Vector2(0.5f, 0.5f);
                rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 0.5f);
                rect.sizeDelta = size;
                Vector3 forward = Vector3.ProjectOnPlane(cam.transform.forward, Vector3.up);
                forward = forward.sqrMagnitude > 1e-6f ? forward.normalized : Vector3.forward; // (a camera looking straight down)
                float height = 2f * TitleDistance * Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad);
                Vector3 centre = cam.transform.position + forward * TitleDistance;
                centre.y = _hasGround ? _ground + VRConfig.EyeHeight.Value.ClampEye() : centre.y;
                rect.SetPositionAndRotation(centre, Quaternion.LookRotation(forward, Vector3.up));
                rect.localScale = Vector3.one * (height / Mathf.Max(1f, size.y)); // (a minimised window's zero screen: as CanvasManager)
                foreach (Transform t in c.GetComponentsInChildren<Transform>(true))
                {
                    t.gameObject.layer = WorldLayer;
                }
                PlacedTitles.Add(c);
                if (!_hasGround)
                {
                    TitlesWithoutGround.Add(c);
                }
            }
            // A 3D text drawn on the UI layer would be painted over the walkers the same way.
            foreach (TextMesh tm in UnityEngine.Object.FindObjectsOfType<TextMesh>())
            {
                if (tm.gameObject.layer == 5 && tm.gameObject.scene.buildIndex == 0)
                {
                    tm.gameObject.layer = WorldLayer;
                }
            }
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
