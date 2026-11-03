using System;
using System.Collections.Generic;
using System.Globalization;
using BepInEx.Configuration;
using RavenfieldVR.Core;
using RavenfieldVR.Input;
using RavenfieldVR.Rig;
using RavenfieldVR.Runtime;
using UnityEngine;
using UnityEngine.UI;

namespace RavenfieldVR.UI
{
    /// <summary>
    /// In-headset settings: turning, comfort, height, reloading, gun angle, rendering, handedness, plus recenter
    /// and the SteamVR binding UI; a second page ("More settings") for the controls, the gloves and the menus, which were only
    /// in the config file (sweep, 2026-10-29), and the first page's less used rows (aiming, display details: it was about 2.4 m
    /// tall; the pages are about 1.8 m each). Changes write straight to the BepInEx config and apply live (the few that do not say
    /// when they do).
    /// Visual language: gunmetal panel, off-white type, one signal-amber accent for the active choice, square
    /// flat controls; the game's own UI font when it can be found.
    /// </summary>
    public sealed class VRSettingsPanel : IDisposable
    {
        private const float CanvasWidth = 980f;
        private const float RowHeight = UiKit.RowHeight;

        private static readonly Color RowRule = UiKit.RowRule;
        private static readonly Color TextColor = UiKit.TextColor;
        private static readonly Color MutedText = UiKit.MutedText;
        private static readonly Color ButtonColor = UiKit.ButtonColor;
        private static readonly Color ButtonHover = UiKit.ButtonHover;
        private static readonly Color Accent = UiKit.Accent;
        private static readonly Color AccentText = UiKit.AccentText;

        private readonly VRRig _rig;
        private readonly GameObject _root;
        public readonly Canvas Canvas;
        private readonly RectTransform _content;
        /// <summary>The two pages (the first, and "More settings"), the one rows are built into, and the one shown.</summary>
        private readonly RectTransform[] _pages = new RectTransform[2];
        private RectTransform _page;
        private readonly List<Action> _refreshers = new List<Action>();
        private RigidPose _localPose;
        private float _height;

        public bool IsOpen => _root.activeSelf;

        public VRSettingsPanel(VRRig rig, Camera eventCamera)
        {
            _rig = rig;
            Canvas = UiKit.MakePanel("RavenfieldVR Settings", eventCamera, out _root, out _content);
            var rect = (RectTransform)_root.transform;

            Build();
            rect.sizeDelta = new Vector2(CanvasWidth, _height + 36f);
            _content.sizeDelta = new Vector2(0f, _height);
            _root.SetActive(false);
        }

        // ---- layout ---------------------------------------------------------------------------------

        private void Build()
        {
            _page = _pages[0] = Page("Main");
            _height = 28f;
            Label("Ravenfield VR", 40, FontStyle.Bold, TextColor, 34f, 64f);
            Label("Settings apply immediately and are saved to the mod's config file.", 20, FontStyle.Normal, MutedText, 34f, 40f);
            _height += 10f;

            Section("Comfort");
            Choice("Turning", VRConfig.Turn, new[] { TurnMode.Snap, TurnMode.Smooth, TurnMode.Off }, new[] { "Snap", "Smooth", "Off" });
            Stepper("Snap angle", () => $"{VRConfig.SnapTurnAngle.Value:0}°", d => VRConfig.SnapTurnAngle.Value = Mathf.Clamp(VRConfig.SnapTurnAngle.Value + d * 15f, 10f, 90f)); // (the config's own floor: 15 moved a 10 up)
            Stepper("Smooth turn speed", () => $"{VRConfig.SmoothTurnSpeed.Value:0}°/s", d => VRConfig.SmoothTurnSpeed.Value = Mathf.Clamp(VRConfig.SmoothTurnSpeed.Value + d * 30f, 30f, 360f));
            Choice("Move with", VRConfig.MoveDirection, new[] { MoveReference.Head, MoveReference.OffHand }, new[] { "Head", "Off hand" });
            Stepper("View shake on shots", () => VRConfig.ViewShake.Value <= 0f ? "off" : $"{VRConfig.ViewShake.Value * 100f:0}%",
                d => VRConfig.ViewShake.Value = Mathf.Clamp01(Mathf.Round((VRConfig.ViewShake.Value + d * 0.1f) * 10f) / 10f));
            Stepper("Vignette", () => $"{VRConfig.Vignette.Value * 100f:0}%", d => VRConfig.Vignette.Value = Mathf.Clamp01((d > 0 ? Mathf.Floor(VRConfig.Vignette.Value * 10f + 1e-4f) + 1f : Mathf.Ceil(VRConfig.Vignette.Value * 10f - 1e-4f) - 1f) / 10f));
            TogglePair("Physical crouch", VRConfig.PhysicalCrouch, "Seated play", VRConfig.SeatedMode);
            ActionRow("Eye height", () => VRConfig.EyeHeight.Value > 0.3f ? $"{VRConfig.EyeHeight.Value:0.00} m" : "auto", "Calibrate now", () => _rig.CalibrateHeightNow());

            Section("Weapons");
            Choice("Reloading", VRConfig.Reload, new[] { ReloadMode.Manual, ReloadMode.Assisted }, new[] { "Manual", "Assisted" });
            Choice("Hands in slow motion", VRConfig.SlowHands, new[] { SlowMotionHands.Normal, SlowMotionHands.Slowed }, new[] { "Normal", "Slowed" });
            // Read when a weapon is drawn: the panel said it applied at once (review, 2026-10-20).
            Choice("Hands (next draw)", VRConfig.Hands, new[] { HandsMode.VRHands, HandsMode.WeaponArms }, new[] { "VR hands", "Weapon's arms" });
            Choice("Holsters", VRConfig.Holsters, new[] { HolsterLayout.Boneworks, HolsterLayout.Classic }, new[] { "Boneworks", "Classic" });
            // (All four places: one set in the file to the off-hand chest lit no button, and a press changed it; sweep.)
            Choice("Magazine pouch", VRConfig.Pouch, new[] { PouchLocation.OffHandHip, PouchLocation.OffHandChest, PouchLocation.Chest, PouchLocation.DominantHip },
                new[] { "Hip", "Off chest", "Chest", "Other hip" });
            TogglePair("Two-handed aiming", VRConfig.TwoHandedAiming, "Holster switching", VRConfig.HolsterSwitching);
            TogglePair("Spawn empty-handed", VRConfig.SpawnEmptyHanded, "Grip to hold", VRConfig.GripToHoldWeapon);
            TogglePair("MG belts by hand", VRConfig.BeltByHand, "Magazine palming", VRConfig.MagazinePalming);
            TogglePair("Gun stops at walls", VRConfig.WorldCollision, "Weapon weight", VRConfig.WeaponWeight);
            TogglePair("Show pouch", VRConfig.ShowPouch, "Reduce flashing", VRConfig.ReduceFlashing);
            Stepper("Gun pitch", () => $"{VRConfig.GunPitchTrim.Value:+0.#;-0.#;0}°", d => VRConfig.GunPitchTrim.Value = Mathf.Clamp(VRConfig.GunPitchTrim.Value + d * 2.5f, -45f, 45f));
            Stepper("Recoil", () => $"{VRConfig.RecoilStrength.Value * 100f:0}%", d => VRConfig.RecoilStrength.Value = Mathf.Clamp(VRConfig.RecoilStrength.Value + d * 0.25f, 0f, 3f));
            // How close a hand must be to take a slide, a foregrip or a magazine (only in the config file before: grabs that miss by
            // a few centimetres on another controller or hand size had no fix in the headset; sweep).
            Stepper("Grab reach", () => $"{VRConfig.GrabRadius.Value * 100f:0} cm", d => VRConfig.GrabRadius.Value = Mathf.Clamp(VRConfig.GrabRadius.Value + d * 0.01f, 0.03f, 0.25f));

            Section("Display");
            Choice("Graphics", VRConfig.Graphics, new[] { GraphicsPreset.Low, GraphicsPreset.Medium, GraphicsPreset.High, GraphicsPreset.Ultra, GraphicsPreset.Game },
                new[] { "Low", "Medium", "High", "Ultra", "Game" });
            Stepper("Render scale", () => $"{VRConfig.RenderScale.Value * 100f:0}%", d => VRConfig.RenderScale.Value = Mathf.Clamp(VRConfig.RenderScale.Value + d * 0.1f, 0.5f, 2f));
            Choice("Hold the frame rate", VRConfig.HoldFrameRate, new[] { false, true }, new[] { "Off", "Lower if slow" });
            Choice("Weapon hand", VRConfig.DominantHand, new[] { Handedness.Right, Handedness.Left }, new[] { "Right", "Left (restart)" });
            float mainHeight = _height + 18f;

            BuildMore();
            float pagesHeight = Mathf.Max(mainHeight, _height + 18f);
            // The footer at the same height on both pages (the shorter page's rows end above it).
            for (int i = 0; i < _pages.Length; i++)
            {
                _page = _pages[i];
                _height = pagesHeight;
                Footer(i);
                _pages[i].sizeDelta = new Vector2(0f, _height);
            }
            ShowPage(0);
        }

        /// <summary>
        /// The second page: the settings the first had no room for, which only the config file could change before (sweep: the
        /// controls, the comfort details, the gloves, the menus).
        /// </summary>
        private void BuildMore()
        {
            _page = _pages[1] = Page("More settings");
            _height = 28f;
            Label("More settings", 40, FontStyle.Bold, TextColor, 34f, 64f);
            Label("Controls, aiming, display details, gloves and menus. (restart): applies when the game starts again.", 20, FontStyle.Normal, MutedText, 34f, 40f);
            _height += 10f;

            Section("Controls and comfort");
            Choice("Sprint button", VRConfig.Sprint, new[] { SprintMode.Toggle, SprintMode.Hold }, new[] { "Toggle", "Hold" });
            Choice("Mounted guns aim with", VRConfig.TurretAim, new[] { TurretAimMode.Head, TurretAimMode.Hand }, new[] { "Head", "Weapon hand" });
            Stepper("Stick dead zone", () => $"{VRConfig.StickDeadzone.Value * 100f:0}%",
                d => VRConfig.StickDeadzone.Value = Mathf.Clamp(Mathf.Round((VRConfig.StickDeadzone.Value + d * 0.05f) * 20f) / 20f, 0f, 0.6f));
            Stepper("Vibration", () => VRConfig.HapticStrength.Value <= 0f ? "off" : $"{VRConfig.HapticStrength.Value * 100f:0}%",
                d => VRConfig.HapticStrength.Value = Mathf.Clamp(Mathf.Round((VRConfig.HapticStrength.Value + d * 0.25f) * 4f) / 4f, 0f, 2f));
            Stepper("Crouch below (of eye height)", () => $"{VRConfig.PhysicalCrouchRatio.Value * 100f:0}%",
                d => VRConfig.PhysicalCrouchRatio.Value = Mathf.Clamp(Mathf.Round((VRConfig.PhysicalCrouchRatio.Value + d * 0.05f) * 20f) / 20f, 0.5f, 0.9f));
            TogglePair("Roomscale walking", VRConfig.RoomscaleMovement, "Level vehicle horizon", VRConfig.SmoothVehicleHorizon);
            TogglePair("Stick jump / crouch", VRConfig.StickJumpCrouch, "Magazine round count", VRConfig.MagazineReadout);

            Section("Hands and weapons");
            Choice("Glove (restart)", VRConfig.Glove, new[] { GloveStyle.Tactical, GloveStyle.Simple }, new[] { "Tactical", "Simple" });
            Stepper("Glove size (restart)", () => $"{VRConfig.GloveSize.Value * 100f:0}%",
                d => VRConfig.GloveSize.Value = Mathf.Clamp(Mathf.Round((VRConfig.GloveSize.Value + d * 0.02f) * 50f) / 50f, 0.7f, 1.1f));
            TogglePair("Scope lenses (next draw)", VRConfig.VRScopes, "Shot view flash", VRConfig.ShotViewFlash);
            Choice("Load magazines by hand (experimental)", VRConfig.LoadMagazinesByHand, new[] { false, true }, new[] { "Off", "On" });

            // (From the first page, which was about 2.4 m tall with them: the two pages are about 1.8 m each now; review, 2026-10-30.)
            Section("Aiming and throwing");
            Stepper("Scope steadying", () => VRConfig.ScopeSteadying.Value <= 0f ? "off" : $"{VRConfig.ScopeSteadying.Value * 100f:0}%",
                d => VRConfig.ScopeSteadying.Value = Mathf.Clamp01(Mathf.Round((VRConfig.ScopeSteadying.Value + d * 0.25f) * 4f) / 4f));
            Stepper("Aim steadying", () => VRConfig.AimSteadying.Value <= 0f ? "off" : $"{VRConfig.AimSteadying.Value * 100f:0}%",
                d => VRConfig.AimSteadying.Value = Mathf.Clamp01(Mathf.Round((VRConfig.AimSteadying.Value + d * 0.25f) * 4f) / 4f));
            Stepper("Throw strength", () => $"{VRConfig.ThrowVelocityMultiplier.Value:0.0}×",
                d => VRConfig.ThrowVelocityMultiplier.Value = Mathf.Clamp(Mathf.Round((VRConfig.ThrowVelocityMultiplier.Value + d * 0.1f) * 10f) / 10f, 0.5f, 4f));

            Section("Display details");
            Stepper("HUD distance", () => $"{VRConfig.HudDistance.Value:0.0} m", d => VRConfig.HudDistance.Value = Mathf.Clamp(VRConfig.HudDistance.Value + d * 0.5f, 1f, 20f));
            TogglePair("Ammo counter on gun", VRConfig.GunAmmoCounter, "Grenade pins", VRConfig.GrenadePins);
            Choice("Frame rate on wrist", VRConfig.WristFrameRate, new[] { true, false }, new[] { "On", "Off" });
            TogglePair("Holstered weapons", VRConfig.ShowHolsters, "Explosion dirt", VRConfig.ExplosionDirt);
            // The body's shadow beside it: a switch to see whether it was the shadow over the hands (task 336).
            TogglePair("Wrist HP and ammo", VRConfig.WristHud, "Own body shadow", VRConfig.BodyShadow);
            TogglePair("Muzzle lens flash", VRConfig.LensFlash, "Water drops", VRConfig.WaterDrops);
            Stepper("Lens flash strength", () => $"{VRConfig.LensFlashStrength.Value * 100f:0}%",
                d => VRConfig.LensFlashStrength.Value = Mathf.Clamp(Mathf.Round((VRConfig.LensFlashStrength.Value + d * 0.25f) * 4f) / 4f, 0f, 3f));
            Stepper("Scope picture (next scope)", () => $"{VRConfig.ScopeResolution.Value} px",
                d => VRConfig.ScopeResolution.Value = Mathf.Clamp(VRConfig.ScopeResolution.Value + d * 256, 256, 2048)); // next scope drawn

            Section("Performance (try it)");
            Choice("Game cameras into 1 px", VRConfig.GameCamerasOnePixel, new[] { false, true }, new[] { "Off", "On" });
            Choice("Scope lens every other frame", VRConfig.ScopeHalfRate, new[] { false, true }, new[] { "Off", "On" });
            Choice("Small objects without shadows", VRConfig.SmallObjectsNoShadows, new[] { false, true }, new[] { "Off", "On" });
            Choice("Tiny distant objects hidden", VRConfig.HideTinyDistantObjects, new[] { false, true }, new[] { "Off", "On" });
            Choice("Repeated objects instanced", VRConfig.InstanceRepeatedObjects, new[] { false, true }, new[] { "Off", "On" });
            Stepper("Garbage collection slice", () => $"{VRConfig.GcSliceMs.Value:0.0} ms",
                d => VRConfig.GcSliceMs.Value = Mathf.Clamp(Mathf.Round((VRConfig.GcSliceMs.Value + d * 0.5f) * 2f) / 2f, 0.5f, 3f));

            Section("Menus");
            Stepper("Menu distance (next open)", () => $"{VRConfig.MenuDistance.Value:0.0} m",
                d => VRConfig.MenuDistance.Value = Mathf.Clamp(Mathf.Round((VRConfig.MenuDistance.Value + d * 0.1f) * 10f) / 10f, 0.6f, 4f));
            Stepper("Menu width", () => $"{VRConfig.MenuWidth.Value:0.0} m",
                d => VRConfig.MenuWidth.Value = Mathf.Clamp(Mathf.Round((VRConfig.MenuWidth.Value + d * 0.1f) * 10f) / 10f, 0.6f, 5f));
        }

        /// <summary>A page of rows under the panel's content, from its top.</summary>
        private RectTransform Page(string name)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.layer = 5;
            var rt = (RectTransform)go.transform;
            rt.SetParent(_content, false);
            rt.anchorMin = new Vector2(0f, 1f);
            rt.anchorMax = new Vector2(1f, 1f);
            rt.pivot = new Vector2(0.5f, 1f);
            rt.anchoredPosition = Vector2.zero;
            rt.sizeDelta = Vector2.zero;
            return rt;
        }

        /// <summary>The page shown (0: the first, 1: More settings).</summary>
        internal int ShownPage { get; private set; }

        /// <summary>Shows one page (0: the first, 1: More settings).</summary>
        private void ShowPage(int page)
        {
            ShownPage = page;
            for (int i = 0; i < _pages.Length; i++)
            {
                if (_pages[i] != null)
                {
                    _pages[i].gameObject.SetActive(i == page);
                }
            }
            Changed = true;
        }

        private RectTransform Row(float height)
        {
            var go = new GameObject("Row", typeof(RectTransform));
            go.layer = 5;
            var rt = (RectTransform)go.transform;
            rt.SetParent(_page, false);
            rt.anchorMin = new Vector2(0f, 1f);
            rt.anchorMax = new Vector2(1f, 1f);
            rt.pivot = new Vector2(0.5f, 1f);
            rt.anchoredPosition = new Vector2(0f, -_height);
            rt.sizeDelta = new Vector2(0f, height);
            _height += height;
            return rt;
        }

        private static Text MakeText(RectTransform parent, string text, int size, FontStyle style, Color color, TextAnchor anchor) =>
            UiKit.MakeText(parent, text, size, style, color, anchor);

        private void Label(string text, int size, FontStyle style, Color color, float indent, float height)
        {
            RectTransform row = Row(height);
            Text t = MakeText(row, text, size, style, color, TextAnchor.MiddleLeft);
            Stretch(t.rectTransform, indent, 0f);
        }

        private void Section(string title)
        {
            _height += 14f;
            RectTransform row = Row(46f);
            Text t = MakeText(row, title, 26, FontStyle.Bold, TextColor, TextAnchor.LowerLeft);
            Stretch(t.rectTransform, 34f, 0f);
            RectTransform rule = Row(2f);
            Image line = rule.gameObject.AddComponent<Image>();
            line.color = RowRule;
            line.raycastTarget = false;
            rule.offsetMin = new Vector2(34f, rule.offsetMin.y);
            rule.offsetMax = new Vector2(-34f, rule.offsetMax.y);
            _height += 6f;
        }

        private static void Stretch(RectTransform rt, float left, float right) => UiKit.Stretch(rt, left, right);

        // Every button marks the panel for a refresh: its values change by hand (see Changed).
        private Button MakeButton(RectTransform parent, string label, float x, float width, Action onClick, out Text text) =>
            UiKit.MakeButton(parent, label, x, width, () =>
            {
                onClick();
                Changed = true;
            }, out text);

        /// <summary>
        /// A button was pressed since the last <see cref="Refresh"/>: refreshed at once then, and only every two seconds
        /// otherwise (for values changed elsewhere: a preset's render scale, the measured eye height). Five times a second,
        /// it built its fourteen value strings each time while open (allocation hunt, 2026-10-24).
        /// </summary>
        public bool Changed { get; private set; } = true;

        private RectTransform LabeledRow(string label)
        {
            RectTransform row = Row(RowHeight);
            Text t = MakeText(row, label, 22, FontStyle.Normal, TextColor, TextAnchor.MiddleLeft);
            Stretch(t.rectTransform, 34f, 420f);
            return row;
        }

        private void Choice<T>(string label, ConfigEntry<T> entry, T[] values, string[] names)
        {
            RectTransform row = LabeledRow(label);
            float width = Mathf.Min(150f, 450f / values.Length - 8f);
            var buttons = new Button[values.Length];
            var texts = new Text[values.Length];
            for (int i = values.Length - 1, slot = 0; i >= 0; i--, slot++)
            {
                T value = values[i];
                buttons[i] = MakeButton(row, names[i], 34f + slot * (width + 8f), width, () => entry.Value = value, out Text text);
                texts[i] = text;
            }
            _refreshers.Add(() =>
            {
                for (int i = 0; i < values.Length; i++)
                {
                    bool on = EqualityComparer<T>.Default.Equals(entry.Value, values[i]);
                    // The active choice keeps the accent through hover/press transitions.
                    ColorBlock c = buttons[i].colors;
                    c.normalColor = on ? Accent : ButtonColor;
                    c.highlightedColor = on ? Accent : ButtonHover;
                    c.selectedColor = c.normalColor;
                    buttons[i].colors = c;
                    texts[i].color = on ? AccentText : TextColor;
                }
            });
        }

        /// <summary>Two on/off settings side by side in one row (the panel is tall enough as it is).</summary>
        private void TogglePair(string left, ConfigEntry<bool> leftEntry, string right, ConfigEntry<bool> rightEntry)
        {
            RectTransform row = Row(RowHeight);
            ToggleHalf(row, left, leftEntry, CanvasWidth * 0.5f);
            ToggleHalf(row, right, rightEntry, 0f);
        }

        /// <summary>One on/off setting in half a row; <paramref name="fromRight"/> is where its half ends, from the row's right edge.</summary>
        private void ToggleHalf(RectTransform row, string label, ConfigEntry<bool> entry, float fromRight)
        {
            const float button = 64f, gap = 8f;
            float offX = fromRight + (fromRight > 0f ? 12f : 34f);
            float onX = offX + button + gap;
            Text t = MakeText(row, label, 22, FontStyle.Normal, TextColor, TextAnchor.MiddleLeft);
            Stretch(t.rectTransform, CanvasWidth - fromRight - CanvasWidth * 0.5f + 34f, onX + button + 10f);
            var buttons = new Button[2];
            var texts = new Text[2];
            buttons[0] = MakeButton(row, "On", onX, button, () => entry.Value = true, out texts[0]);
            buttons[1] = MakeButton(row, "Off", offX, button, () => entry.Value = false, out texts[1]);
            _refreshers.Add(() =>
            {
                for (int i = 0; i < 2; i++)
                {
                    bool on = entry.Value == (i == 0);
                    ColorBlock c = buttons[i].colors;
                    c.normalColor = on ? Accent : ButtonColor;
                    c.highlightedColor = on ? Accent : ButtonHover;
                    c.selectedColor = c.normalColor;
                    buttons[i].colors = c;
                    texts[i].color = on ? AccentText : TextColor;
                }
            });
        }

        private void Stepper(string label, Func<string> value, Action<int> step)
        {
            RectTransform row = LabeledRow(label);
            MakeButton(row, "+", 34f, 56f, () => step(1), out _);
            Text v = MakeText(row, value(), 22, FontStyle.Normal, TextColor, TextAnchor.MiddleCenter);
            RectTransform vr = v.rectTransform;
            vr.anchorMin = vr.anchorMax = new Vector2(1f, 0.5f);
            vr.pivot = new Vector2(1f, 0.5f);
            vr.anchoredPosition = new Vector2(-98f, 0f);
            vr.sizeDelta = new Vector2(130f, RowHeight);
            MakeButton(row, "−", 236f, 56f, () => step(-1), out _);
            _refreshers.Add(() => v.text = value());
        }

        private void ActionRow(string label, Func<string> value, string buttonLabel, Action onClick)
        {
            RectTransform row = LabeledRow(label);
            MakeButton(row, buttonLabel, 34f, 200f, onClick, out _);
            Text v = MakeText(row, value(), 22, FontStyle.Normal, MutedText, TextAnchor.MiddleRight);
            RectTransform vr = v.rectTransform;
            vr.anchorMin = vr.anchorMax = new Vector2(1f, 0.5f);
            vr.pivot = new Vector2(1f, 0.5f);
            vr.anchoredPosition = new Vector2(-250f, 0f);
            vr.sizeDelta = new Vector2(150f, RowHeight);
            _refreshers.Add(() => v.text = value());
        }

        private void Footer(int page)
        {
            RectTransform row = Row(RowHeight + 10f);
            // The other page, at the footer's left end.
            MakeButton(row, page == 0 ? "More settings" : "Back", 752f, 194f, () => ShowPage(page == 0 ? 1 : 0), out _);
            MakeButton(row, "Close", 34f, 140f, Close, out _);
            MakeButton(row, "SteamVR bindings", 186f, 230f, VRInput.OpenBindingUI, out _);
            MakeButton(row, "Mod keys", 428f, 150f, () =>
            {
                Close(); // in its place, not half over it
                UISystem.Instance?.ModKeys.Open();
            }, out _);
            MakeButton(row, "Recenter", 590f, 150f, () =>
            {
                _rig.Recenter();
                UISystem.Instance?.Canvases.RecenterMenus();
                Place();
            }, out _);
        }

        // ---- open / close / placement -----------------------------------------------------------

        public void Toggle()
        {
            if (IsOpen)
            {
                Close();
            }
            else
            {
                Open();
            }
        }

        public void Open()
        {
            _root.SetActive(true);
            ShowPage(0);
            Place();
            Refresh();
        }

        public void Close() => _root.SetActive(false);

        private void Place()
        {
            Transform head = _rig.Head;
            Vector3 forward = head.forward;
            forward.y = 0f;
            forward = forward.sqrMagnitude > 1e-4f ? forward.normalized : _rig.Root.forward;
            // Slightly to the side of centre so it does not cover a game menu opened at the same time.
            Vector3 right = Vector3.Cross(Vector3.up, forward);
            Vector3 pos = head.position + forward * (VRConfig.MenuDistance.Value * 0.85f) + right * 0.18f - Vector3.up * 0.1f;
            var world = new RigidPose(pos, Quaternion.LookRotation(forward, Vector3.up));
            _localPose = RigidPose.Of(_rig.Root).Inverse().Multiply(world);
        }

        private float _awaySince = -1f;

        public void BeforeRender()
        {
            if (!IsOpen)
            {
                _awaySince = -1f;
                return;
            }
            // Looked away from it (more than 75 degrees) for 2 s, turned round in the room: it comes back in front, as the game's
            // menus do (CanvasManager.KeepMenusInView). It stayed behind the player (review, 2026-10-20).
            Vector3 toPanel = _root.transform.position - _rig.Head.position;
            toPanel.y = 0f;
            Vector3 look = _rig.Head.forward;
            look.y = 0f;
            if (toPanel.sqrMagnitude > 1e-4f && look.sqrMagnitude > 1e-4f && Vector3.Angle(toPanel, look) > 75f)
            {
                if (_awaySince < 0f)
                {
                    _awaySince = Time.unscaledTime;
                }
                else if (Time.unscaledTime - _awaySince > 2f)
                {
                    _awaySince = -1f;
                    Place();
                }
            }
            else
            {
                _awaySince = -1f;
            }
            RigidPose.Of(_rig.Root).Multiply(_localPose).ApplyTo(_root.transform);
            Vector3 scale = Vector3.one * (0.95f / CanvasWidth);
            if (_root.transform.localScale != scale) // a constant: written once, not every frame the panel is open
            {
                _root.transform.localScale = scale;
            }
        }

        public void Refresh()
        {
            Changed = false;
            foreach (Action r in _refreshers)
            {
                r();
            }
        }

        public void Dispose()
        {
            if (_root != null)
            {
                UnityEngine.Object.Destroy(_root);
            }
        }
    }
}
