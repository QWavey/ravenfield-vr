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
    /// and the SteamVR binding UI. Changes write straight to the BepInEx config and apply live.
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
            _height = 28f;
            Label("Ravenfield VR", 40, FontStyle.Bold, TextColor, 34f, 64f);
            Label("Settings apply immediately and are saved to the mod's config file.", 20, FontStyle.Normal, MutedText, 34f, 40f);
            _height += 10f;

            Section("Comfort");
            Choice("Turning", VRConfig.Turn, new[] { TurnMode.Snap, TurnMode.Smooth, TurnMode.Off }, new[] { "Snap", "Smooth", "Off" });
            Stepper("Snap angle", () => $"{VRConfig.SnapTurnAngle.Value:0}°", d => VRConfig.SnapTurnAngle.Value = Mathf.Clamp(VRConfig.SnapTurnAngle.Value + d * 15f, 15f, 90f));
            Stepper("Smooth turn speed", () => $"{VRConfig.SmoothTurnSpeed.Value:0}°/s", d => VRConfig.SmoothTurnSpeed.Value = Mathf.Clamp(VRConfig.SmoothTurnSpeed.Value + d * 30f, 30f, 360f));
            Choice("Move with", VRConfig.MoveDirection, new[] { MoveReference.Head, MoveReference.OffHand }, new[] { "Head", "Off hand" });
            Stepper("Vignette", () => $"{VRConfig.Vignette.Value * 100f:0}%", d => VRConfig.Vignette.Value = Mathf.Clamp01(Mathf.Round((VRConfig.Vignette.Value + d * 0.1f) * 10f) / 10f));
            Toggle("Physical crouch", VRConfig.PhysicalCrouch);
            Toggle("Seated play", VRConfig.SeatedMode);
            ActionRow("Eye height", () => VRConfig.EyeHeight.Value > 0.3f ? $"{VRConfig.EyeHeight.Value:0.00} m" : "auto", "Calibrate now", () => _rig.CalibrateHeightNow());

            Section("Weapons");
            Choice("Reloading", VRConfig.Reload, new[] { ReloadMode.Manual, ReloadMode.Assisted }, new[] { "Manual", "Assisted" });
            Toggle("Two-handed aiming", VRConfig.TwoHandedAiming);
            Choice("Hands", VRConfig.Hands, new[] { HandsMode.VRHands, HandsMode.WeaponArms }, new[] { "VR hands", "Weapon's arms" });
            Toggle("Holster switching", VRConfig.HolsterSwitching);
            Choice("Magazine pouch", VRConfig.Pouch, new[] { PouchLocation.OffHandHip, PouchLocation.Chest, PouchLocation.DominantHip }, new[] { "Hip", "Chest", "Other hip" });
            Toggle("Grenade pins", VRConfig.GrenadePins);
            Toggle("Lay MG belts by hand", VRConfig.BeltByHand);
            Stepper("Gun pitch", () => $"{VRConfig.GunPitchTrim.Value:+0;-0;0}°", d => VRConfig.GunPitchTrim.Value = Mathf.Clamp(VRConfig.GunPitchTrim.Value + d * 2.5f, -45f, 45f));
            Stepper("Recoil", () => $"{VRConfig.RecoilStrength.Value * 100f:0}%", d => VRConfig.RecoilStrength.Value = Mathf.Clamp(VRConfig.RecoilStrength.Value + d * 0.25f, 0f, 3f));

            Section("Display");
            Choice("Graphics", VRConfig.Graphics, new[] { GraphicsPreset.Low, GraphicsPreset.Medium, GraphicsPreset.High, GraphicsPreset.Ultra, GraphicsPreset.Game },
                new[] { "Low", "Medium", "High", "Ultra", "Game" });
            Stepper("Render scale", () => $"{VRConfig.RenderScale.Value * 100f:0}%", d => VRConfig.RenderScale.Value = Mathf.Clamp(VRConfig.RenderScale.Value + d * 0.1f, 0.5f, 2f));
            Stepper("HUD distance", () => $"{VRConfig.HudDistance.Value:0.0} m", d => VRConfig.HudDistance.Value = Mathf.Clamp(VRConfig.HudDistance.Value + d * 0.5f, 1f, 20f));
            Toggle("Ammo counter on gun", VRConfig.GunAmmoCounter);
            Choice("Weapon hand", VRConfig.DominantHand, new[] { Handedness.Right, Handedness.Left }, new[] { "Right", "Left (restart)" });

            _height += 18f;
            Footer();
        }

        private RectTransform Row(float height)
        {
            var go = new GameObject("Row", typeof(RectTransform));
            go.layer = 5;
            var rt = (RectTransform)go.transform;
            rt.SetParent(_content, false);
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

        private static Button MakeButton(RectTransform parent, string label, float x, float width, Action onClick, out Text text) =>
            UiKit.MakeButton(parent, label, x, width, onClick, out text);

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

        private void Toggle(string label, ConfigEntry<bool> entry) =>
            Choice(label, entry, new[] { true, false }, new[] { "On", "Off" });

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
            MakeButton(row, "Mod keys", 428f, 150f, () => UISystem.Instance?.ModKeys.Open(), out _);
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

        public void BeforeRender()
        {
            if (!IsOpen)
            {
                return;
            }
            RigidPose.Of(_rig.Root).Multiply(_localPose).ApplyTo(_root.transform);
            _root.transform.localScale = Vector3.one * (0.95f / CanvasWidth);
        }

        public void Refresh()
        {
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
