using System;
using System.Collections.Generic;
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
    /// In-headset settings, in tabs of a few rows each at one height (one page of it all was over 2 m tall and said little about what
    /// mattered; user, 2026-11-04: "sort the Settings tab ... its kinda messy"): Comfort, Weapons, Display. What is not super important
    /// is in four more tabs shown only with ShowAdvanced on in the config file, and what is better simply on (two-handed aiming, the
    /// detent, the counters) is only in the config file. Changes write straight to the BepInEx config and apply live (the few that do
    /// not say when they do).
    /// Visual language: gunmetal panel, off-white type, one blue accent for the active choice, square flat controls; the game's own UI
    /// font when it can be found.
    /// </summary>
    public sealed class VRSettingsPanel : IDisposable
    {
        private const float CanvasWidth = 980f;
        private const float RowHeight = UiKit.RowHeight;

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
        /// <summary>The tabs' pages, and the one rows are built into.</summary>
        private readonly List<RectTransform> _pages = new List<RectTransform>();
        private RectTransform _page;
        private readonly List<Action> _refreshers = new List<Action>();
        private RigidPose _localPose;
        private float _height;

        public bool IsOpen => _root.activeSelf;

        public VRSettingsPanel(VRRig rig, Camera eventCamera)
        {
            _rig = rig;
            Canvas = UiKit.MakePanel("RavenfieldVR Settings", eventCamera, out _root, out _content);
            Build();
            _root.SetActive(false);
        }

        // ---- layout ---------------------------------------------------------------------------------

        private void Build()
        {
            bool advanced = VRConfig.ShowAdvanced.Value;
            _page = Page("Header");
            _height = 24f;
            RectTransform title = Row(64f);
            Stretch(MakeText(title, "Ravenfield VR", 40, FontStyle.Bold, TextColor, TextAnchor.MiddleLeft).rectTransform, 34f, 0f);
            if (!advanced)
            {
                Stretch(MakeText(title, "More: ShowAdvanced in the config file", 18, FontStyle.Normal, MutedText, TextAnchor.MiddleRight).rectTransform, 34f, 34f);
            }
            RectTransform tabBar = Row(RowHeight);
            float top = _height + 10f;

            var tabs = new List<KeyValuePair<string, Action>>
            {
                new KeyValuePair<string, Action>("Comfort", BuildComfort),
                new KeyValuePair<string, Action>("Weapons", BuildWeapons),
                new KeyValuePair<string, Action>("Display", BuildDisplay)
            };
            if (advanced)
            {
                tabs.Add(new KeyValuePair<string, Action>("Controls", BuildControls));
                tabs.Add(new KeyValuePair<string, Action>("Gear", BuildGear));
                tabs.Add(new KeyValuePair<string, Action>("View", BuildView));
                tabs.Add(new KeyValuePair<string, Action>("Speed", BuildSpeed));
            }
            float bottom = top;
            foreach (KeyValuePair<string, Action> tab in tabs)
            {
                _page = Page(tab.Key);
                _pages.Add(_page);
                _height = top;
                tab.Value();
                bottom = Mathf.Max(bottom, _height);
            }

            // From the left, as wide as they fit; each lit while its page is shown.
            const float margin = 34f, gap = 8f;
            float width = Mathf.Min(210f, (CanvasWidth - 2f * margin - gap * (tabs.Count - 1)) / tabs.Count);
            for (int i = 0; i < tabs.Count; i++)
            {
                int page = i;
                Button button = MakeButton(tabBar, tabs[i].Key, CanvasWidth - margin - (i + 1) * width - i * gap, width, () => ShowPage(page), out Text text);
                _refreshers.Add(() => Paint(button, text, ShownPage == page));
            }

            // One height for all of them, the footer under the tallest page: it stays where it is from tab to tab, under the laser.
            _page = Page("Footer");
            _height = bottom + 14f;
            Footer();
            float height = _height + 18f;
            ((RectTransform)_root.transform).sizeDelta = new Vector2(CanvasWidth, height + 36f);
            _content.sizeDelta = new Vector2(0f, height);
            ShowPage(0);
        }

        private void BuildComfort()
        {
            Choice("Turning", VRConfig.Turn, new[] { TurnMode.Snap, TurnMode.Smooth, TurnMode.Off }, new[] { "Snap", "Smooth", "Off" });
            // One row for the one that is in use.
            Stepper("Turn amount",
                () => VRConfig.Turn.Value == TurnMode.Snap ? $"{VRConfig.SnapTurnAngle.Value:0}°" : VRConfig.Turn.Value == TurnMode.Smooth ? $"{VRConfig.SmoothTurnSpeed.Value:0}°/s" : "off",
                d =>
                {
                    if (VRConfig.Turn.Value == TurnMode.Snap)
                    {
                        VRConfig.SnapTurnAngle.Value = Mathf.Clamp(VRConfig.SnapTurnAngle.Value + d * 15f, 10f, 90f); // (the config's own floor: 15 moved a 10 up)
                    }
                    else if (VRConfig.Turn.Value == TurnMode.Smooth)
                    {
                        VRConfig.SmoothTurnSpeed.Value = Mathf.Clamp(VRConfig.SmoothTurnSpeed.Value + d * 30f, 30f, 360f);
                    }
                });
            Choice("Move with", VRConfig.MoveDirection, new[] { MoveReference.Head, MoveReference.OffHand }, new[] { "Head", "Off hand" });
            Stepper("Vignette", () => $"{VRConfig.Vignette.Value * 100f:0}%", d => VRConfig.Vignette.Value = Mathf.Clamp01((d > 0 ? Mathf.Floor(VRConfig.Vignette.Value * 10f + 1e-4f) + 1f : Mathf.Ceil(VRConfig.Vignette.Value * 10f - 1e-4f) - 1f) / 10f));
            Stepper("Vibration", () => VRConfig.HapticStrength.Value <= 0f ? "off" : $"{VRConfig.HapticStrength.Value * 100f:0}%",
                d => VRConfig.HapticStrength.Value = Mathf.Clamp(Mathf.Round((VRConfig.HapticStrength.Value + d * 0.25f) * 4f) / 4f, 0f, 2f));
            TogglePair("Physical crouch", VRConfig.PhysicalCrouch, "Seated play", VRConfig.SeatedMode);
            ActionRow("Eye height", () => VRConfig.EyeHeight.Value > 0.3f ? $"{VRConfig.EyeHeight.Value:0.00} m" : "auto", "Calibrate now", () => _rig.CalibrateHeightNow());
        }

        private void BuildWeapons()
        {
            Choice("Weapon hand", VRConfig.DominantHand, new[] { Handedness.Right, Handedness.Left }, new[] { "Right", "Left (restart)" });
            Choice("Reloading", VRConfig.Reload, new[] { ReloadMode.Manual, ReloadMode.Assisted }, new[] { "Manual", "Assisted" });
            // Read when a weapon is drawn: the panel said it applied at once (review, 2026-10-20).
            Choice("Hands (next draw)", VRConfig.Hands, new[] { HandsMode.VRHands, HandsMode.WeaponArms }, new[] { "VR hands", "Weapon's arms" });
            Choice("Holsters", VRConfig.Holsters, new[] { HolsterLayout.Boneworks, HolsterLayout.Classic }, new[] { "Boneworks", "Classic" });
            // (All four places: one set in the file to the off-hand chest lit no button, and a press changed it; sweep.)
            Choice("Magazine pouch", VRConfig.Pouch, new[] { PouchLocation.OffHandHip, PouchLocation.OffHandChest, PouchLocation.Chest, PouchLocation.DominantHip },
                new[] { "Hip", "Off chest", "Chest", "Other hip" });
            TogglePair("Grip to hold", VRConfig.GripToHoldWeapon, "Load magazines by hand", VRConfig.LoadMagazinesByHand);
            Stepper("Gun pitch", () => $"{VRConfig.GunPitchTrim.Value:+0.#;-0.#;0}°", d => VRConfig.GunPitchTrim.Value = Mathf.Clamp(VRConfig.GunPitchTrim.Value + d * 2.5f, -45f, 45f));
            Stepper("Recoil", () => $"{VRConfig.RecoilStrength.Value * 100f:0}%", d => VRConfig.RecoilStrength.Value = Mathf.Clamp(Mathf.Round((VRConfig.RecoilStrength.Value + d * 0.25f) * 100f) / 100f, 0f, 3f));
        }

        private void BuildDisplay()
        {
            Choice("Graphics", VRConfig.Graphics, new[] { GraphicsPreset.Low, GraphicsPreset.Medium, GraphicsPreset.High, GraphicsPreset.Ultra, GraphicsPreset.Game },
                new[] { "Low", "Medium", "High", "Ultra", "Game" });
            Stepper("Render scale", () => $"{VRConfig.RenderScale.Value * 100f:0}%", d => VRConfig.RenderScale.Value = Mathf.Clamp(Mathf.Round((VRConfig.RenderScale.Value + d * 0.1f) * 100f) / 100f, 0.5f, 2f)); // (rounded: added up, 1.2000001 went into the config)
            Choice("Hold the frame rate (not with Game)", VRConfig.HoldFrameRate, new[] { false, true }, new[] { "Off", "Lower if slow" });
            // (A safety setting: on this page whatever else is hidden.)
            Choice("Reduce flashing", VRConfig.ReduceFlashing, new[] { false, true }, new[] { "Off", "On" });
        }

        // ---- the tabs shown with ShowAdvanced on ---------------------------------------------------

        private void BuildControls()
        {
            Choice("Sprint button", VRConfig.Sprint, new[] { SprintMode.Toggle, SprintMode.Hold }, new[] { "Toggle", "Hold" });
            Choice("Mounted guns aim with", VRConfig.TurretAim, new[] { TurretAimMode.Head, TurretAimMode.Hand }, new[] { "Head", "Weapon hand" });
            Choice("Hands in slow motion", VRConfig.SlowHands, new[] { SlowMotionHands.Normal, SlowMotionHands.Slowed }, new[] { "Normal", "Slowed" });
            Stepper("Stick dead zone", () => $"{VRConfig.StickDeadzone.Value * 100f:0}%",
                d => VRConfig.StickDeadzone.Value = Mathf.Clamp(Mathf.Round((VRConfig.StickDeadzone.Value + d * 0.05f) * 20f) / 20f, 0f, 0.6f));
            Stepper("Crouch below (of eye height)", () => $"{VRConfig.PhysicalCrouchRatio.Value * 100f:0}%",
                d => VRConfig.PhysicalCrouchRatio.Value = Mathf.Clamp(Mathf.Round((VRConfig.PhysicalCrouchRatio.Value + d * 0.05f) * 20f) / 20f, 0.5f, 0.9f));
            // How close a hand must be to take a slide, a foregrip or a magazine (grabs that miss by a few centimetres on another
            // controller or hand size had no fix in the headset; sweep).
            Stepper("Grab reach", () => $"{VRConfig.GrabRadius.Value * 100f:0} cm", d => VRConfig.GrabRadius.Value = Mathf.Clamp(Mathf.Round((VRConfig.GrabRadius.Value + d * 0.01f) * 100f) / 100f, 0.03f, 0.25f));
            Stepper("Throw strength", () => $"{VRConfig.ThrowVelocityMultiplier.Value:0.0}×",
                d => VRConfig.ThrowVelocityMultiplier.Value = Mathf.Clamp(Mathf.Round((VRConfig.ThrowVelocityMultiplier.Value + d * 0.1f) * 10f) / 10f, 0.5f, 4f));
            TogglePair("Spawn empty-handed", VRConfig.SpawnEmptyHanded, "Level vehicle horizon", VRConfig.SmoothVehicleHorizon);
        }

        private void BuildGear()
        {
            Choice("Glove (restart)", VRConfig.Glove, new[] { GloveStyle.Tactical, GloveStyle.Simple }, new[] { "Tactical", "Simple" });
            Stepper("Glove size (restart)", () => $"{VRConfig.GloveSize.Value * 100f:0}%",
                d => VRConfig.GloveSize.Value = Mathf.Clamp(Mathf.Round((VRConfig.GloveSize.Value + d * 0.02f) * 50f) / 50f, 0.7f, 1.1f));
            Stepper("Scope steadying", () => VRConfig.ScopeSteadying.Value <= 0f ? "off" : $"{VRConfig.ScopeSteadying.Value * 100f:0}%",
                d => VRConfig.ScopeSteadying.Value = Mathf.Clamp01(Mathf.Round((VRConfig.ScopeSteadying.Value + d * 0.25f) * 4f) / 4f));
            Stepper("Aim steadying", () => VRConfig.AimSteadying.Value <= 0f ? "off" : $"{VRConfig.AimSteadying.Value * 100f:0}%",
                d => VRConfig.AimSteadying.Value = Mathf.Clamp01(Mathf.Round((VRConfig.AimSteadying.Value + d * 0.25f) * 4f) / 4f));
            Stepper("Scope picture (next scope)", () => $"{VRConfig.ScopeResolution.Value} px",
                d => VRConfig.ScopeResolution.Value = Mathf.Clamp(VRConfig.ScopeResolution.Value + d * 256, 256, 2048)); // next scope drawn
            TogglePair("Unlatched mag falls", VRConfig.UnderSeatedMagazineFalls, "AK magazines stay in", VRConfig.AkMagazinesStayIn);
        }

        private void BuildView()
        {
            Stepper("View shake on shots", () => VRConfig.ViewShake.Value <= 0f ? "off" : $"{VRConfig.ViewShake.Value * 100f:0}%",
                d => VRConfig.ViewShake.Value = Mathf.Clamp01(Mathf.Round((VRConfig.ViewShake.Value + d * 0.1f) * 10f) / 10f));
            Stepper("Lens flash strength", () => $"{VRConfig.LensFlashStrength.Value * 100f:0}%",
                d => VRConfig.LensFlashStrength.Value = Mathf.Clamp(Mathf.Round((VRConfig.LensFlashStrength.Value + d * 0.25f) * 4f) / 4f, 0f, 3f));
            Stepper("HUD distance", () => $"{VRConfig.HudDistance.Value:0.0} m", d => VRConfig.HudDistance.Value = Mathf.Clamp(VRConfig.HudDistance.Value + d * 0.5f, 1f, 20f));
            Stepper("Menu distance (next open)", () => $"{VRConfig.MenuDistance.Value:0.0} m",
                d => VRConfig.MenuDistance.Value = Mathf.Clamp(Mathf.Round((VRConfig.MenuDistance.Value + d * 0.1f) * 10f) / 10f, 0.6f, 4f));
            Stepper("Menu width", () => $"{VRConfig.MenuWidth.Value:0.0} m",
                d => VRConfig.MenuWidth.Value = Mathf.Clamp(Mathf.Round((VRConfig.MenuWidth.Value + d * 0.1f) * 10f) / 10f, 0.6f, 5f));
            // The body's shadow: a switch to see whether it was the shadow over the hands (task 336).
            TogglePair("Own body shadow", VRConfig.BodyShadow, "Water drops", VRConfig.WaterDrops);
        }

        private void BuildSpeed()
        {
            Choice("Game cameras into 1 px", VRConfig.GameCamerasOnePixel, new[] { false, true }, new[] { "Off", "On" });
            Choice("Scope lens every other frame", VRConfig.ScopeHalfRate, new[] { false, true }, new[] { "Off", "On" });
            Choice("Small objects without shadows", VRConfig.SmallObjectsNoShadows, new[] { false, true }, new[] { "Off", "On" });
            Choice("Tiny distant objects hidden", VRConfig.HideTinyDistantObjects, new[] { false, true }, new[] { "Off", "On" });
            Choice("Repeated objects instanced", VRConfig.InstanceRepeatedObjects, new[] { false, true }, new[] { "Off", "On" });
            Stepper("Garbage collection slice", () => $"{VRConfig.GcSliceMs.Value:0.0} ms",
                d => VRConfig.GcSliceMs.Value = Mathf.Clamp(Mathf.Round((VRConfig.GcSliceMs.Value + d * 0.5f) * 2f) / 2f, 0.5f, 3f));
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

        /// <summary>The tab shown, from the left.</summary>
        internal int ShownPage { get; private set; }

        /// <summary>How many tabs there are (three, seven with ShowAdvanced on).</summary>
        internal int PageCount => _pages.Count;

        /// <summary>Shows one tab's page.</summary>
        private void ShowPage(int page)
        {
            ShownPage = page;
            for (int i = 0; i < _pages.Count; i++)
            {
                _pages[i].gameObject.SetActive(i == page);
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

        /// <summary>A button as the active choice or not; the active one keeps the accent through hover and press.</summary>
        private static void Paint(Button button, Text text, bool on)
        {
            ColorBlock c = button.colors;
            c.normalColor = on ? Accent : ButtonColor;
            c.highlightedColor = on ? Accent : ButtonHover;
            c.selectedColor = c.normalColor;
            button.colors = c;
            text.color = on ? AccentText : TextColor;
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
                    Paint(buttons[i], texts[i], EqualityComparer<T>.Default.Equals(entry.Value, values[i]));
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
                    Paint(buttons[i], texts[i], entry.Value == (i == 0));
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

        private void Footer()
        {
            RectTransform row = Row(RowHeight + 10f);
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
            ShowPage(ShownPage); // (the tab it was left on)
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
