using System;
using System.Collections.Generic;
using RavenfieldVR.Core;
using RavenfieldVR.Input;
using RavenfieldVR.Rig;
using RavenfieldVR.Runtime;
using RavenfieldVR.Util;
using UnityEngine;
using UnityEngine.UI;

namespace RavenfieldVR.UI
{
    /// <summary>
    /// Every keyboard key that a script of the held weapon or of a running mutator / HUD mod is listening to right
    /// now, with what it is for, each pressable with the laser. The press goes to that script only.
    /// </summary>
    public sealed class ModKeysPanel : IDisposable
    {
        private const float CanvasWidth = 820f;
        private const float RebuildSeconds = 0.75f;

        private readonly VRRig _rig;
        private readonly GameObject _root;
        public readonly Canvas Canvas;
        private readonly RectTransform _content;
        private RigidPose _localPose;
        private float _height;
        private float _lastBuild;
        private string _lastSignature = "";

        public bool IsOpen => _root.activeSelf;

        public ModKeysPanel(VRRig rig, Camera eventCamera)
        {
            _rig = rig;
            Canvas = UiKit.MakePanel("RavenfieldVR Mod Keys", eventCamera, out _root, out _content);
            _root.SetActive(false);
        }

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
            Rebuild(force: true);
        }

        public void Close() => _root.SetActive(false);

        public void Update(bool pressing)
        {
            // Not while the laser holds a press: the rows rebuilt under it, its Press button was gone at the release and the tap
            // never went out (round-7 scout).
            if (IsOpen && !pressing && Time.unscaledTime - _lastBuild > RebuildSeconds)
            {
                Rebuild(force: false);
            }
        }

        private void Rebuild(bool force)
        {
            _lastBuild = Time.unscaledTime;
            Weapon weapon = GameContext.PlayerActor != null ? GameContext.PlayerActor.activeWeapon : null;
            List<ScriptKey> weaponKeys = ScriptKeys.KeysOn(weapon);
            List<ScriptKey> otherKeys = ScriptKeys.OtherKeys();
            string signature = Signature(weapon, weaponKeys, otherKeys);
            if (!force && signature == _lastSignature)
            {
                return;
            }
            _lastSignature = signature;

            for (int i = _content.childCount - 1; i >= 0; i--)
            {
                UnityEngine.Object.Destroy(_content.GetChild(i).gameObject);
            }
            _height = 24f;
            Label("Mod keys", 36, FontStyle.Bold, UiKit.TextColor, 52f);
            Label("Keys that weapon and mutator scripts are listening for. Press one to send it to that script.", 19, FontStyle.Normal, UiKit.MutedText, 56f); // (two lines: at 34 the second was cut off)

            Section(weapon != null ? "This weapon: " + StripTags(weapon.name) : "This weapon");
            if (weaponKeys.Count == 0)
            {
                Label("No script on this weapon reads a key.", 20, FontStyle.Italic, UiKit.MutedText, 46f);
            }
            foreach (ScriptKey k in weaponKeys)
            {
                KeyRow(k, k.Label, weapon);
            }

            Section("Mutators and other mods");
            if (otherKeys.Count == 0)
            {
                Label("No mutator or mod script reads a key right now.", 20, FontStyle.Italic, UiKit.MutedText, 46f);
            }
            int shown = 0;
            foreach (ScriptKey k in otherKeys)
            {
                // (The panel does not scroll: with no end to the rows it grew past the view, its Close button with it.)
                if (shown++ == MaxOtherRows)
                {
                    Label((otherKeys.Count - MaxOtherRows) + " more not shown.", 20, FontStyle.Italic, UiKit.MutedText, 46f);
                    break;
                }
                string owner = k.Script != null ? StripTags(k.Script.behaviour ?? k.Script.name) : "";
                KeyRow(k, k.Purpose == ScriptKeyPurpose.Other ? k.Label : $"{k.Label} · {owner}");
            }

            _height += 16f;
            RectTransform footer = Row(UiKit.RowHeight + 10f);
            UiKit.MakeButton(footer, "Close", 34f, 160f, Close, out _);

            var rect = (RectTransform)_root.transform;
            rect.sizeDelta = new Vector2(CanvasWidth, _height + 30f);
            _content.sizeDelta = new Vector2(0f, _height);
        }

        private static string Signature(Weapon weapon, List<ScriptKey> a, List<ScriptKey> b)
        {
            var sb = new System.Text.StringBuilder(weapon != null ? weapon.GetInstanceID().ToString() : "-");
            foreach (ScriptKey k in a)
            {
                sb.Append('|').Append(k.Script.GetInstanceID()).Append(k.Key);
            }
            sb.Append('#');
            foreach (ScriptKey k in b)
            {
                sb.Append('|').Append(k.Script.GetInstanceID()).Append(k.Key);
            }
            return sb.ToString();
        }

        private static string StripTags(string s) => System.Text.RegularExpressions.Regex.Replace(s ?? "", "<[^>]+>", "");

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

        private const int MaxOtherRows = 12;

        private void Label(string text, int size, FontStyle style, Color color, float height)
        {
            RectTransform row = Row(height);
            Text t = UiKit.MakeText(row, text, size, style, color, TextAnchor.MiddleLeft);
            t.horizontalOverflow = HorizontalWrapMode.Wrap;
            UiKit.Stretch(t.rectTransform, 34f, 34f);
        }

        private void Section(string title)
        {
            _height += 12f;
            RectTransform row = Row(44f);
            Text t = UiKit.MakeText(row, title, 24, FontStyle.Bold, UiKit.TextColor, TextAnchor.LowerLeft);
            UiKit.Stretch(t.rectTransform, 34f, 34f);
            RectTransform rule = Row(2f);
            Image line = rule.gameObject.AddComponent<Image>();
            line.color = UiKit.RowRule;
            line.raycastTarget = false;
            rule.offsetMin = new Vector2(34f, rule.offsetMin.y);
            rule.offsetMax = new Vector2(-34f, rule.offsetMax.y);
            _height += 4f;
        }

        /// <summary>A key's row with its Press button; <paramref name="onWeapon"/> is the weapon a weapon's own key is on (null for the others).</summary>
        private void KeyRow(ScriptKey key, string label, Weapon onWeapon = null)
        {
            RectTransform row = Row(UiKit.RowHeight);
            Text t = UiKit.MakeText(row, label, 22, FontStyle.Normal, UiKit.TextColor, TextAnchor.MiddleLeft);
            UiKit.Stretch(t.rectTransform, 34f, 330f);
            Text k = UiKit.MakeText(row, key.KeyLabel, 22, FontStyle.Bold, UiKit.Accent, TextAnchor.MiddleRight);
            RectTransform kr = k.rectTransform;
            kr.anchorMin = kr.anchorMax = new Vector2(1f, 0.5f);
            kr.pivot = new Vector2(1f, 0.5f);
            kr.anchoredPosition = new Vector2(-196f, 0f);
            kr.sizeDelta = new Vector2(120f, UiKit.RowHeight);
            ScriptKey target = key;
            UiKit.MakeButton(row, "Press", 34f, 150f, () =>
            {
                if (target.Script == null)
                {
                    return; // the script went away (weapon switched) before the list refreshed: never broadcast
                }
                Weapon now = GameContext.PlayerActor != null ? GameContext.PlayerActor.activeWeapon : null;
                if (onWeapon != null && now != onWeapon)
                {
                    // A row of the weapon held before the switch (the list refreshes every 0.75 s): its key went to the holstered
                    // weapon's script, with the press felt (squad UI scout, 2026-10-22). The list is the current weapon's now.
                    Rebuild(force: true);
                    return;
                }
                ScriptKeys.Tap(target.Key, target.Script);
                _rig.Hand(VRInput.Dominant).Haptic(0.015f, 180f, 0.35f);
            }, out _);
        }

        private void Place()
        {
            Transform head = _rig.Head;
            Vector3 forward = head.forward;
            forward.y = 0f;
            forward = forward.sqrMagnitude > 1e-4f ? forward.normalized : _rig.Root.forward;
            Vector3 right = Vector3.Cross(Vector3.up, forward);
            Vector3 pos = head.position + forward * (VRConfig.MenuDistance.Value * 0.8f) - right * 0.2f - Vector3.up * 0.12f;
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
            Vector3 scale = Vector3.one * (0.8f / CanvasWidth);
            if (_root.transform.localScale != scale) // a constant: written once, not every frame the panel is open
            {
                _root.transform.localScale = scale;
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
