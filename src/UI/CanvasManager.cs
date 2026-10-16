using System;
using System.Collections.Generic;
using HarmonyLib;
using RavenfieldVR.Core;
using RavenfieldVR.Rig;
using RavenfieldVR.Runtime;
using RavenfieldVR.Util;
using UnityEngine;
using UnityEngine.UI;
using Valve.VR;

namespace RavenfieldVR.UI
{
    public enum CanvasRole { Hud, Menu }

    /// <summary>
    /// Moves every screen-space canvas into the world. HUD canvases become a head-locked frame sized to match
    /// the game camera's frustum (the game camera is forced to that FOV), so screen-projected markers
    /// (objectives, squad orders, hit indicators) land exactly on their world targets. Interactive canvases
    /// (menus, loadout, squad map, dialogs) are placed in front of the player when they open and stay put
    /// relative to the play space. Everything is rendered by a dedicated UI camera on top of the world.
    /// </summary>
    public sealed class CanvasManager : IDisposable
    {
        private sealed class Tracked
        {
            public Canvas Canvas;
            public RectTransform Rect;
            public RenderMode OriginalMode;
            public Camera OriginalCamera;
            public Vector3 OriginalScale;
            public bool ScalerWasEnabled;
            public CanvasScaler Scaler;
            public Vector2 Size;
            public CanvasRole Role;
            public bool Visible;
            public bool Placed;
            public RigidPose LocalPose; // relative to the rig root
            public float NextRoleCheck;
            /// <summary>Parts of an open menu kept at no depth inside it every frame (its nested pages, anything set deep).</summary>
            public readonly List<Transform> Flat = new List<Transform>();
            public int Order;
            /// <summary>A vehicle seat's sight (gunner reticle): shown at optical infinity, not at HUD distance.</summary>
            public bool Collimated;
        }

        /// <summary>
        /// Distance for collimated sights. Far enough that both eyes see the reticle without disparity (no double
        /// image when focusing on the target), inside the UI camera's far plane; the angular size is kept.
        /// </summary>
        private const float CollimatedDistance = 80f;

        private readonly VRRig _rig;
        private readonly Camera _eventCamera;
        private readonly Dictionary<Canvas, Tracked> _tracked = new Dictionary<Canvas, Tracked>();
        private readonly HashSet<Canvas> _own = new HashSet<Canvas>();
        private float _nextScan;
        private readonly List<Canvas> _dead = new List<Canvas>();
        private bool _wasGameplay;

        public bool AnyMenuVisible { get; private set; }

        /// <summary>World pose shared by menus opened together (so they stack instead of scattering).</summary>
        private RigidPose? _menuAnchor;

        public CanvasManager(VRRig rig, Camera eventCamera)
        {
            _rig = rig;
            _eventCamera = eventCamera;
        }

        /// <summary>Canvases created by the mod itself; they place themselves.</summary>
        public void RegisterOwn(Canvas c) => _own.Add(c);

        /// <summary>
        /// Adds every visible converted canvas to <paramref name="into"/> (per frame, without allocating); only menus when
        /// <paramref name="menusOnly"/>: the laser hitting a HUD frame counted as pointing at a menu and held the gun's fire.
        /// </summary>
        public void CollectVisible(List<Canvas> into, bool menusOnly = false)
        {
            foreach (KeyValuePair<Canvas, Tracked> kv in _tracked)
            {
                if (kv.Value.Visible && kv.Value.Canvas != null && (!menusOnly || kv.Value.Role == CanvasRole.Menu))
                {
                    into.Add(kv.Value.Canvas);
                }
            }
        }

        /// <summary>Any converted canvas is visible (as of the last <see cref="Update"/>).</summary>
        public bool AnyVisible { get; private set; }

        public void Update()
        {
            if (Time.unscaledTime >= _nextScan)
            {
                _nextScan = Time.unscaledTime + 0.4f;
                Scan();
            }
            bool menu = false, anyVisible = false;
            bool gameplay = GameContext.InGame && !GameContext.GameMenuOpen;
            bool gameplayChanged = gameplay != _wasGameplay;
            _wasGameplay = gameplay;
            List<Canvas> dead = _dead;
            dead.Clear();
            foreach (KeyValuePair<Canvas, Tracked> kv in _tracked)
            {
                Tracked t = kv.Value;
                if (t.Canvas == null)
                {
                    dead.Add(kv.Key);
                    continue;
                }
                bool visible = t.Canvas.isActiveAndEnabled && HasVisibleGraphics(t);
                // Worked out while it shows, and again the moment it opens: worked out while hidden (the game closes a
                // list by switching it off, so it holds nothing clickable), a menu came back as HUD, drawn head-locked
                // and then put where it stood at its last opening.
                bool opening = visible && !t.Visible;
                if (opening || visible && (Time.unscaledTime >= t.NextRoleCheck || gameplayChanged))
                {
                    t.NextRoleCheck = Time.unscaledTime + 0.5f;
                    t.Collimated = t.Canvas.GetComponentInParent<Vehicle>() != null;
                    // During gameplay with no game menu open everything is HUD, even canvases that happen to
                    // hold a clickable element; menus only exist while the game shows its cursor (or outside a match).
                    CanvasRole was = t.Role;
                    t.Role = gameplay ? CanvasRole.Hud : RoleOutsidePlay(t.Canvas);
                    if (!opening && t.Role != was && t.Role == CanvasRole.Menu)
                    {
                        // A canvas that turned back into a menu without reopening (its clickable parts came back, the game
                        // menu opened over it): re-place and re-flatten it like a fresh opening, or it stayed where it last
                        // stood relative to the body with its deep pages un-flattened.
                        t.Placed = false;
                        CollectFlat(t);
                    }
                }
                if (opening && t.Role == CanvasRole.Menu)
                {
                    t.Placed = false; // re-place menus each time they open
                    CollectFlat(t); // its pages kept flat while it is open: something puts them back deep every frame
                }
                t.Visible = visible;
                menu |= visible && t.Role == CanvasRole.Menu;
                anyVisible |= visible;
            }
            AnyVisible = anyVisible;
            foreach (Canvas c in dead)
            {
                _tracked.Remove(c);
            }
            if (!menu)
            {
                _menuAnchor = null;
            }
            AnyMenuVisible = menu;
        }

        private static bool HasVisibleGraphics(Tracked t)
        {
            CanvasGroup group = t.Canvas.GetComponent<CanvasGroup>();
            return group == null || group.alpha > 0.01f;
        }

        private void Scan()
        {
            foreach (Canvas c in UnityEngine.Object.FindObjectsOfType<Canvas>())
            {
                if (c == null || !c.isRootCanvas || _own.Contains(c) || _tracked.ContainsKey(c) || InsideAnotherCanvas(c))
                {
                    continue;
                }
                if (LoadingView.Active && LoadingView.IsLoadingScene(c.gameObject.scene))
                {
                    continue; // the loading view draws it into its picture (it looks for new ones four times a second)
                }
                if (c.renderMode == RenderMode.WorldSpace)
                {
                    continue; // already in the world (game nameplates etc.)
                }
                Convert(c);
            }
        }

        /// <summary>
        /// A page of a menu (the options' Game, Input and Video pages) switched off reports itself as a root canvas: it was
        /// converted as a panel of its own, scaled down to panel size (0.0008) and moved, and once switched on inside its
        /// menu it showed at 0.0008 of its size: the options' tab buttons over an empty page. It goes with its menu.
        /// </summary>
        private static bool InsideAnotherCanvas(Canvas c)
        {
            Transform parent = c.transform.parent;
            return parent != null && parent.GetComponentInParent<Canvas>() != null;
        }

        private void Convert(Canvas c)
        {
            var rect = (RectTransform)c.transform;
            var t = new Tracked
            {
                Canvas = c,
                Rect = rect,
                OriginalMode = c.renderMode,
                OriginalCamera = c.worldCamera,
                OriginalScale = rect.localScale,
                Scaler = c.GetComponent<CanvasScaler>(),
                Size = rect.rect.size,
                Order = c.sortingOrder
            };
            if (t.Size.x < 10f || t.Size.y < 10f)
            {
                t.Size = new Vector2(Screen.width, Screen.height) / Mathf.Max(0.01f, c.scaleFactor);
            }
            t.ScalerWasEnabled = t.Scaler != null && t.Scaler.enabled;
            t.Role = RoleOutsidePlay(c);

            c.renderMode = RenderMode.WorldSpace;
            c.worldCamera = _eventCamera;
            if (t.Scaler != null)
            {
                t.Scaler.enabled = false;
            }
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.sizeDelta = t.Size;
            SetLayerRecursive(c.transform, 5);
            FlattenDepth(c.transform);
            _tracked[c] = t;
            Log.Debug($"Canvas '{c.name}' -> world ({t.Role}, {t.Size.x:0}x{t.Size.y:0})");
        }

        /// <summary>
        /// The nested canvases of a menu (its pages) and every part found deep inside it as it opens. The options' Game page
        /// sat 150 units deep and went back there every frame (flattened once, it was deep again), behind the panel's dark
        /// background: the options showed their tab buttons over an empty page.
        /// </summary>
        private static readonly List<Canvas> NestedBuffer = new List<Canvas>();

        private static void CollectFlat(Tracked t)
        {
            ClipWithoutStencil(t.Rect);
            t.Flat.Clear();
            t.Rect.GetComponentsInChildren(true, NestedBuffer);
            foreach (Canvas c in NestedBuffer)
            {
                if (c != null && c.transform != t.Rect)
                {
                    t.Flat.Add(c.transform);
                }
            }
            NestedBuffer.Clear();
            foreach (RectTransform r in t.Rect.GetComponentsInChildren<RectTransform>(true))
            {
                if (r != t.Rect && Mathf.Abs(r.localPosition.z) > 0.5f && !t.Flat.Contains(r))
                {
                    t.Flat.Add(r);
                }
            }
            KeepFlat(t);
        }

        /// <summary>
        /// Stencil masks (a scroll list's viewport) clip by a stencil the headset's picture may not keep: everything in the
        /// list vanished (the options' pages showed nothing under their tabs). Each becomes a rectangle clip, which needs
        /// no stencil; the mask's own image, drawn only as the stencil before, stays unseen (it still takes the laser).
        /// </summary>
        private static void ClipWithoutStencil(Transform root)
        {
            foreach (Mask m in root.GetComponentsInChildren<Mask>(true))
            {
                if (m == null || !m.enabled)
                {
                    continue;
                }
                if (m.GetComponent<RectMask2D>() == null)
                {
                    m.gameObject.AddComponent<RectMask2D>();
                }
                Graphic g = m.graphic;
                bool showGraphic = m.showMaskGraphic;
                m.enabled = false;
                if (g != null && !showGraphic)
                {
                    Color c = g.color;
                    g.color = new Color(c.r, c.g, c.b, 0f);
                }
            }
        }

        /// <summary>Right before drawing (after the game's animation has run): no depth inside the menu.</summary>
        private static void KeepFlat(Tracked t)
        {
            foreach (Transform f in t.Flat)
            {
                if (f == null)
                {
                    continue;
                }
                Vector3 p = f.localPosition;
                if (Mathf.Abs(p.z) > 0.01f)
                {
                    f.localPosition = new Vector3(p.x, p.y, 0f);
                }
            }
        }

        /// <summary>
        /// Parts of a screen canvas set at a depth inside it (the options' Game page sat 150 units deep) are drawn as they
        /// were on the flat screen, where depth never showed: in the world that page stood 15 cm behind the panel's dark
        /// background and the options came up empty.
        /// </summary>
        private static void FlattenDepth(Transform root)
        {
            foreach (RectTransform r in root.GetComponentsInChildren<RectTransform>(true))
            {
                if (r == root)
                {
                    continue;
                }
                Vector3 p = r.localPosition;
                if (Mathf.Abs(p.z) > 0.5f)
                {
                    r.localPosition = new Vector3(p.x, p.y, 0f);
                }
            }
        }

        private static void SetLayerRecursive(Transform t, int layer)
        {
            t.gameObject.layer = layer;
            foreach (Transform child in t)
            {
                SetLayerRecursive(child, layer);
            }
        }

        /// <summary>
        /// Before a match (the start-up and the main menu) every screen is a panel standing in front of you: the game's
        /// "loading mods" page has nothing to click and was taken for HUD, locked to the view and following every turn of
        /// the head. In a match, menus are the canvases with something to click.
        /// </summary>
        private static CanvasRole RoleOutsidePlay(Canvas c) =>
            UnityEngine.SceneManagement.SceneManager.GetActiveScene().buildIndex <= 1 ? CanvasRole.Menu : Classify(c);

        /// <summary>
        /// The match bar (the game mode's own canvas: battalions, tickets and flags across the top) shows only in the wrist
        /// view. Faded with a canvas group of the mod's own; the game switches the canvas itself on and off for its "hide
        /// UI" option, so that is left alone.
        /// </summary>
        public void UpdateMatchBar()
        {
            GameModeBase mode = GameModeBase.activeGameMode;
            Canvas c = mode != null ? mode.canvas : null;
            if (c == null)
            {
                _matchBar = null;
                return;
            }
            if (!ReferenceEquals(_matchBarFor, c))
            {
                _matchBarFor = c;
                CanvasGroup existing = c.GetComponent<CanvasGroup>();
                _matchBar = existing == null ? c.gameObject.AddComponent<CanvasGroup>() : null; // someone else's group: left alone
                if (_matchBar != null)
                {
                    _matchBar.alpha = 0f;
                }
            }
            if (_matchBar == null)
            {
                return;
            }
            float want = Input.VRGameInput.WristLookGesture ? 1f : 0f;
            _matchBar.alpha = Mathf.MoveTowards(_matchBar.alpha, want, Time.unscaledDeltaTime * 8f);
            _matchBar.blocksRaycasts = want > 0f;
        }

        private CanvasGroup _matchBar;
        private Canvas _matchBarFor;

        /// <summary>For tests: how visible the match bar is (its canvas group's alpha), -1 when the mod does not fade it.</summary>
        public float MatchBarAlpha => _matchBar != null ? _matchBar.alpha : -1f;

        /// <summary>Menus are canvases with something to click; everything else is HUD.</summary>
        private static readonly List<Selectable> SelectableBuffer = new List<Selectable>();

        private static CanvasRole Classify(Canvas c)
        {
            c.GetComponentsInChildren(false, SelectableBuffer);
            foreach (Selectable s in SelectableBuffer)
            {
                if (s.isActiveAndEnabled && s.interactable)
                {
                    return CanvasRole.Menu;
                }
            }
            return CanvasRole.Hud;
        }

        private bool _hadHead;
        private float _menuAwaySince = -1f;

        /// <summary>
        /// A menu the game just opened gets converted before this frame is drawn, not at the next scan (up to 0.4 s later):
        /// the loadout's weapon list showed flat in one eye, over an emptied panel, then jumped into place.
        /// </summary>
        public static void ScanNow() => _scanNow = true;

        private static bool _scanNow;

        public void BeforeRender()
        {
            if (_scanNow)
            {
                _scanNow = false;
                Scan();
                Update(); // visible and placed this frame
            }
            Transform head = _rig.Head;
            FpsActorController player = GameContext.Player;
            RigidPose hud = _rig.HudPose;
            if (_rig.HeadTracked && !_hadHead)
            {
                _hadHead = true;
                RecenterMenus(); // menus opened before the headset's first pose were placed from nowhere
            }
            KeepMenusInView(head);
            float hfov = VRConfig.HudHorizontalFov.Value;
            float distance = VRConfig.HudDistance.Value;
            float aspect = 16f / 9f;
            float aspectArea = 0f;

            foreach (Tracked t in _tracked.Values)
            {
                if (t.Canvas == null || !t.Visible)
                {
                    continue;
                }
                if (t.Role == CanvasRole.Hud)
                {
                    if (t.Size.x > 1f && t.Size.y > 1f && t.Size.x * t.Size.y > aspectArea)
                    {
                        // The game camera's frustum follows the main HUD (the largest HUD canvas), not whichever canvas is
                        // enumerated last: a small HUD panel or a 4:3 canvas showing or hiding used to jerk the camera's
                        // vertical FOV and shift the screen-projected markers (objectives, squad orders, hit indicators).
                        aspectArea = t.Size.x * t.Size.y;
                        aspect = t.Size.x / t.Size.y;
                    }
                    // Head-locked frame matching the game camera's frustum (vehicle sights far out, same angular size).
                    float d = t.Collimated ? CollimatedDistance : distance;
                    float width = 2f * d * Mathf.Tan(hfov * 0.5f * Mathf.Deg2Rad);
                    float scale = width / t.Size.x;
                    float depth = d - 0.001f * d / distance * Mathf.Clamp(t.Order, -100, 100);
                    t.Rect.SetPositionAndRotation(hud.position + hud.Forward * depth, hud.rotation);
                    SetScale(t.Rect, Vector3.one * scale);
                }
                else
                {
                    KeepFlat(t);
                    if (!t.Placed)
                    {
                        PlaceMenu(t);
                    }
                    RigidPose world = RigidPose.Of(_rig.Root).Multiply(t.LocalPose);
                    world.ApplyTo(t.Rect);
                    SetScale(t.Rect, Vector3.one * (VRConfig.MenuWidth.Value / Mathf.Max(1f, t.Size.x)));
                }
            }

            // Keep the game camera's projection identical to the HUD frame so markers line up.
            if (player != null && player.fpCamera != null)
            {
                float vfov = 2f * Mathf.Atan(Mathf.Tan(hfov * 0.5f * Mathf.Deg2Rad) / aspect) * Mathf.Rad2Deg;
                player.fpCamera.fieldOfView = vfov;
                player.fpCamera.aspect = aspect;
            }
        }

        /// <summary>
        /// Written only when it differs: the same every frame for each shown canvas (its size and the settings rarely
        /// change), while anything that does change it (the game, a setting) is still put right the next frame.
        /// </summary>
        private static void SetScale(Transform t, Vector3 scale)
        {
            if (t.localScale != scale)
            {
                t.localScale = scale;
            }
        }

        private void PlaceMenu(Tracked t)
        {
            if (_menuAnchor == null)
            {
                Transform head = _rig.Head;
                Vector3 forward = head.forward;
                forward.y = 0f;
                if (forward.sqrMagnitude < 1e-4f)
                {
                    forward = _rig.Root.forward;
                }
                forward.Normalize();
                Vector3 pos = head.position + forward * VRConfig.MenuDistance.Value - Vector3.up * 0.08f;
                _menuAnchor = RigidPose.Of(_rig.Root).Inverse().Multiply(new RigidPose(pos, Quaternion.LookRotation(forward, Vector3.up)));
                _menuLocalAnchor = _rig.Root.InverseTransformPoint(pos);
            }
            // Kept relative to the rig: a menu opened later (the loadout's weapon list in a moving car) went where the rig
            // had been when the first one opened.
            RigidPose anchor = RigidPose.Of(_rig.Root).Multiply(_menuAnchor.Value);
            // Stack overlapping menus by sorting order, a few millimetres apart.
            anchor.position -= anchor.Forward * 0.004f * Mathf.Clamp(t.Order, -50, 50);
            t.LocalPose = RigidPose.Of(_rig.Root).Inverse().Multiply(anchor);
            t.Placed = true;
        }

        /// <summary>A menu left behind (turned away for two seconds) comes back in front of you.</summary>
        private void KeepMenusInView(Transform head)
        {
            if (_menuAnchor == null)
            {
                _menuAwaySince = -1f;
                return;
            }
            Vector3 toMenu = RigidPose.Of(_rig.Root).Multiply(new RigidPose(_menuLocalAnchor, Quaternion.identity)).position - head.position;
            Vector3 flatForward = Vector3.ProjectOnPlane(head.forward, Vector3.up);
            Vector3 flatTo = Vector3.ProjectOnPlane(toMenu, Vector3.up);
            bool away = flatForward.sqrMagnitude > 1e-4f && flatTo.sqrMagnitude > 1e-4f && Vector3.Angle(flatForward, flatTo) > 75f;
            if (!away)
            {
                _menuAwaySince = -1f;
                return;
            }
            if (_menuAwaySince < 0f)
            {
                _menuAwaySince = Time.unscaledTime;
            }
            else if (Time.unscaledTime - _menuAwaySince > 2f)
            {
                _menuAwaySince = -1f;
                RecenterMenus();
            }
        }

        private Vector3 _menuLocalAnchor;

        /// <summary>Re-centres open menus in front of the player.</summary>
        public void RecenterMenus()
        {
            _menuAnchor = null;
            foreach (Tracked t in _tracked.Values)
            {
                t.Placed = false;
            }
        }

        public bool IsMenuCanvas(Canvas c) => _tracked.TryGetValue(c, out Tracked t) && t.Role == CanvasRole.Menu;

        public void OnSceneChanged()
        {
            _own.RemoveWhere(c => c == null); // the loading scene's canvases are registered on every map load
            // Only the canvases that went with the old scene are dropped. One that lives on (the game's Options panel)
            // stays converted: forgotten, it kept the old scene's world position, the scan passes over canvases already
            // in world space, and in a match the pause menu's Options opened out of sight ("clicking does nothing").
            _dead.Clear();
            foreach (KeyValuePair<Canvas, Tracked> kv in _tracked)
            {
                if (kv.Value.Canvas == null)
                {
                    _dead.Add(kv.Key);
                }
                else
                {
                    kv.Value.Placed = false;
                    kv.Value.Visible = false;
                }
            }
            foreach (Canvas c in _dead)
            {
                _tracked.Remove(c);
            }
            _menuAnchor = null;
            _nextScan = 0f;
        }

        /// <summary>Self-test: whether this canvas is converted, its role, and where it is from the head.</summary>
        internal string Describe(Canvas c)
        {
            if (c == null)
            {
                return "no canvas";
            }
            Canvas root = c.rootCanvas;
            string where = "";
            Transform head = _rig.Head;
            if (head != null)
            {
                Vector3 to = c.transform.position - head.position;
                where = $", {to.magnitude:0.00} m from the head, {Vector3.Angle(head.forward, to):0} deg off the view";
            }
            string state = _tracked.TryGetValue(root, out Tracked t)
                ? $"converted ({t.Role}, {(t.Visible ? "visible" : "hidden")}, {(t.Placed ? "placed" : "not placed")})"
                : "NOT CONVERTED";
            return $"'{root.name}' {root.renderMode}, {(c.enabled ? "on" : "off")}, {state}{where}";
        }

        /// <summary>Self-test: the canvas is drawn within this many degrees of the view, at most this far away.</summary>
        internal bool InView(Canvas c, float maxAngle, float maxDistance)
        {
            Transform head = _rig.Head;
            if (c == null || head == null || !_tracked.TryGetValue(c.rootCanvas, out Tracked t) || !t.Visible)
            {
                return false;
            }
            Vector3 to = c.transform.position - head.position;
            return to.magnitude <= maxDistance && Vector3.Angle(head.forward, to) <= maxAngle;
        }

        public void Dispose()
        {
            foreach (Tracked t in _tracked.Values)
            {
                if (t.Canvas == null)
                {
                    continue;
                }
                t.Canvas.renderMode = t.OriginalMode;
                t.Canvas.worldCamera = t.OriginalCamera;
                t.Rect.localScale = t.OriginalScale;
                if (t.Scaler != null)
                {
                    t.Scaler.enabled = t.ScalerWasEnabled;
                }
            }
            _tracked.Clear();
        }
    }

    /// <summary>The game's screen fades become SteamVR compositor fades (a flat black panel would not cover your view).</summary>
    [HarmonyPatch(typeof(EffectUi))]
    internal static class EffectUiPatches
    {
        [HarmonyPostfix, HarmonyPatch(nameof(EffectUi.FadeOut))]
        private static void FadeOut(float duration, Color color)
        {
            if (VRManager.Active)
            {
                OpenVR.Compositor?.FadeToColor(Mathf.Max(0f, duration), color.r, color.g, color.b, 1f, false);
            }
        }

        [HarmonyPostfix, HarmonyPatch(nameof(EffectUi.FadeIn))]
        private static void FadeIn(float duration, Color color)
        {
            if (VRManager.Active)
            {
                CVRCompositor comp = OpenVR.Compositor;
                comp?.FadeToColor(0f, color.r, color.g, color.b, 1f, false);
                comp?.FadeToColor(Mathf.Max(0f, duration), color.r, color.g, color.b, 0f, false);
            }
        }

        [HarmonyPostfix, HarmonyPatch("LateUpdate")]
        private static void HideFlatEffects(EffectUi __instance)
        {
            if (!VRManager.Active)
            {
                return;
            }
            if (__instance.fadeImage != null)
            {
                __instance.fadeImage.enabled = false;
            }
            if (__instance.eyeLidParent != null && __instance.eyeLidParent.gameObject.activeSelf)
            {
                __instance.eyeLidParent.gameObject.SetActive(false);
            }
        }
    }

    /// <summary>The loadout's weapon list is a canvas of its own, shown when a slot is clicked: converted at once.</summary>
    [HarmonyPatch(typeof(WeaponSelectionUi), nameof(WeaponSelectionUi.Show))]
    internal static class WeaponListShowPatch
    {
        private static void Postfix()
        {
            if (VRManager.Active)
            {
                CanvasManager.ScanNow();
            }
        }
    }

    /// <summary>PlayerFpParent re-applies its FOV (and aim zoom) every physics step; keep the HUD-matched FOV in VR.</summary>
    [HarmonyPatch(typeof(PlayerFpParent), "FixedUpdate")]
    internal static class FpFovPatch
    {
        private static float _fov = 60f;
        private static void Prefix(PlayerFpParent __instance) => _fov = __instance.fpCamera != null ? __instance.fpCamera.fieldOfView : 60f;

        private static void Postfix(PlayerFpParent __instance)
        {
            if (VRManager.Active && __instance.fpCamera != null)
            {
                __instance.fpCamera.fieldOfView = _fov;
            }
        }
    }

    /// <summary>The game's weapon panel (name, ammo, slot icons) has no job in VR; hidden unless the setting says otherwise.</summary>
    [HarmonyPatch(typeof(IngameUI), nameof(IngameUI.ElementIsVisible))]
    internal static class HideWeaponHudPatch
    {
        private static void Postfix(IngameUI.UIElement element, ref bool __result)
        {
            if (__result && element == IngameUI.UIElement.WeaponInfo && VRManager.Active && VRConfig.HideGameWeaponHud.Value)
            {
                __result = false;
            }
        }
    }
}
