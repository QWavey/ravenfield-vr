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

        /// <summary>Adds every visible converted canvas to <paramref name="into"/> (per frame, without allocating).</summary>
        public void CollectVisible(List<Canvas> into)
        {
            foreach (KeyValuePair<Canvas, Tracked> kv in _tracked)
            {
                if (kv.Value.Visible && kv.Value.Canvas != null)
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
                if (Time.unscaledTime >= t.NextRoleCheck || gameplayChanged)
                {
                    t.NextRoleCheck = Time.unscaledTime + 0.5f;
                    t.Collimated = t.Canvas.GetComponentInParent<Vehicle>() != null;
                    // During gameplay with no game menu open everything is HUD, even canvases that happen to
                    // hold a clickable element; menus only exist while the game shows its cursor (or outside a match).
                    t.Role = gameplay ? CanvasRole.Hud : Classify(t.Canvas);
                }
                if (visible && !t.Visible && t.Role == CanvasRole.Menu)
                {
                    t.Placed = false; // re-place menus each time they open
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
                if (c == null || !c.isRootCanvas || _own.Contains(c) || _tracked.ContainsKey(c))
                {
                    continue;
                }
                if (c.renderMode == RenderMode.WorldSpace)
                {
                    continue; // already in the world (game nameplates etc.)
                }
                Convert(c);
            }
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
            t.Role = Classify(c);

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
            _tracked[c] = t;
            Log.Debug($"Canvas '{c.name}' -> world ({t.Role}, {t.Size.x:0}x{t.Size.y:0})");
        }

        private static void SetLayerRecursive(Transform t, int layer)
        {
            t.gameObject.layer = layer;
            foreach (Transform child in t)
            {
                SetLayerRecursive(child, layer);
            }
        }

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

        public void BeforeRender()
        {
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

            foreach (Tracked t in _tracked.Values)
            {
                if (t.Canvas == null || !t.Visible)
                {
                    continue;
                }
                if (t.Size.x > 1f && t.Size.y > 1f)
                {
                    aspect = t.Size.x / t.Size.y;
                }
                if (t.Role == CanvasRole.Hud)
                {
                    // Head-locked frame matching the game camera's frustum (vehicle sights far out, same angular size).
                    float d = t.Collimated ? CollimatedDistance : distance;
                    float width = 2f * d * Mathf.Tan(hfov * 0.5f * Mathf.Deg2Rad);
                    float scale = width / t.Size.x;
                    float depth = d - 0.001f * d / distance * Mathf.Clamp(t.Order, -100, 100);
                    t.Rect.SetPositionAndRotation(hud.position + hud.Forward * depth, hud.rotation);
                    t.Rect.localScale = Vector3.one * scale;
                }
                else
                {
                    if (!t.Placed)
                    {
                        PlaceMenu(t);
                    }
                    RigidPose world = RigidPose.Of(_rig.Root).Multiply(t.LocalPose);
                    world.ApplyTo(t.Rect);
                    t.Rect.localScale = Vector3.one * (VRConfig.MenuWidth.Value / Mathf.Max(1f, t.Size.x));
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
                _menuAnchor = new RigidPose(pos, Quaternion.LookRotation(forward, Vector3.up));
                _menuLocalAnchor = _rig.Root.InverseTransformPoint(pos);
            }
            RigidPose anchor = _menuAnchor.Value;
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
            _tracked.Clear();
            _menuAnchor = null;
            _nextScan = 0f;
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
