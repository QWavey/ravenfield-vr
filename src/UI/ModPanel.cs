using System;
using RavenfieldVR.Core;
using RavenfieldVR.Rig;
using RavenfieldVR.Runtime;
using UnityEngine;
using UnityEngine.UI;

namespace RavenfieldVR.UI
{
    /// <summary>
    /// A panel of the mod's own in the world, worked with the laser (the sandbox's, the multiplayer's): rows built top down, rebuilt
    /// whole when what they show changes, placed in front of the player and brought back when looked away from, as the mod-keys
    /// panel is (its rows and placement, shared). A press only marks the panel changed; it is rebuilt once the press is over (rebuilt
    /// under the laser, the pressed button was gone at its release and the press never counted: round-7 scout).
    /// </summary>
    public abstract class ModPanel : IDisposable
    {
        protected readonly VRRig Rig;
        private readonly GameObject _root;
        public readonly Canvas Canvas;
        private readonly RectTransform _content;
        private RigidPose _localPose;
        private float _height;
        private float _awaySince = -1f;
        private float _lastBuild;

        /// <summary>The canvas's width in its own units, and how wide that is in the world (m).</summary>
        protected virtual float CanvasWidth => 900f;
        protected virtual float WorldWidth => 0.9f;

        /// <summary>Seconds between rebuilds while open with nothing pressed (0: only when marked changed).</summary>
        protected virtual float RefreshSeconds => 0f;

        /// <summary>While set by a page's Build, the buttons it makes are greyed out (shown, not to be pressed).</summary>
        protected bool Greyed;

        /// <summary>Whether the panel has its own Close at the bottom (not one that is a choice to make, or that the game keeps open).</summary>
        protected virtual bool HasClose => true;

        private bool _placeWhenTracked;

        /// <summary>Set by every button of the panel: rebuilt at the next update without a press held.</summary>
        protected bool Changed;

        public bool IsOpen => _root.activeSelf;

        protected ModPanel(VRRig rig, Camera eventCamera, string name)
        {
            Rig = rig;
            Canvas = UiKit.MakePanel(name, eventCamera, out _root, out _content);
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

        public virtual void Open()
        {
            _root.SetActive(true);
            Place();
            Rebuild();
        }

        public virtual void Close() => _root.SetActive(false);

        public virtual void Update(bool pressing)
        {
            if (!IsOpen || pressing)
            {
                return;
            }
            if (Changed)
            {
                Rebuild();
            }
            else if (RefreshSeconds > 0f && Time.unscaledTime - _lastBuild > RefreshSeconds)
            {
                // Looked at again, rebuilt only when what it shows is different: rebuilt every second the button under the laser
                // was a new one each time (its highlight blinked) and forty objects were thrown away a second for nothing.
                _lastBuild = Time.unscaledTime;
                if (Shown() != _shown)
                {
                    Rebuild();
                }
            }
        }

        /// <summary>A number that changes when what the panel shows from outside changes (counts, states): the periodic look compares it.</summary>
        protected virtual int Shown() => 0;

        private int _shown;

        /// <summary>The rows, top down (Title, Section, Label, Choice, ActionRow ...).</summary>
        protected abstract void Build();

        protected void Rebuild()
        {
            Changed = false;
            _lastBuild = Time.unscaledTime;
            _shown = Shown();
            for (int i = _content.childCount - 1; i >= 0; i--)
            {
                UnityEngine.Object.Destroy(_content.GetChild(i).gameObject);
            }
            _height = 24f;
            Greyed = false;
            Build();
            Greyed = false;
            _height += 16f;
            if (HasClose)
            {
                RectTransform footer = Row(UiKit.RowHeight + 10f);
                UiKit.MakeButton(footer, "Close", 34f, 160f, Close, out _);
            }
            else
            {
                _height += 10f;
            }
            var rect = (RectTransform)_root.transform;
            rect.sizeDelta = new Vector2(CanvasWidth, _height + 30f);
            _content.sizeDelta = new Vector2(0f, _height);
        }

        protected RectTransform Row(float height)
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

        protected void Gap(float height) => _height += height;

        protected Text Label(string text, int size = 20, FontStyle style = FontStyle.Normal, Color? color = null, float height = 46f)
        {
            RectTransform row = Row(height);
            Text t = UiKit.MakeText(row, text, size, style, color ?? UiKit.MutedText, TextAnchor.MiddleLeft);
            t.horizontalOverflow = HorizontalWrapMode.Wrap;
            UiKit.Stretch(t.rectTransform, 34f, 34f);
            return t;
        }

        protected void Title(string title, string under = null)
        {
            Label(title, 36, FontStyle.Bold, UiKit.TextColor, 52f);
            if (under != null)
            {
                Label(under, 19, FontStyle.Normal, UiKit.MutedText, 56f);
            }
        }

        protected void Section(string title)
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

        /// <summary>A button in a row, <paramref name="x"/> from the row's right edge; a press marks the panel changed after its own work.</summary>
        protected Button ButtonIn(RectTransform row, string label, float x, float width, Action onClick, bool chosen = false)
        {
            float pressedAt = -10f;
            Button b = UiKit.MakeButton(row, label, x, width, () =>
            {
                // (The same button pressed again within a quarter second, before the panel was built anew: a second host or join that
                // ended the first one; panel scout, 2026-11-25.)
                if (Time.unscaledTime - pressedAt < 0.25f)
                {
                    return;
                }
                pressedAt = Time.unscaledTime;
                onClick();
                Changed = true; // (the laser's hand has its own buzz for the press: a second one here went to the gun hand)
            }, out Text text);
            if (chosen)
            {
                ColorBlock colors = b.colors;
                colors.normalColor = colors.selectedColor = UiKit.Accent;
                b.colors = colors;
                text.color = UiKit.AccentText;
            }
            if (Greyed)
            {
                // Shown, not to be pressed (what is not this player's to change in a shared match): dim, no answer to the laser.
                b.interactable = false;
                Color dim = text.color;
                dim.a = 0.35f;
                text.color = dim;
            }
            return b;
        }

        /// <summary>A row with its name on the left and one button for each choice on the right, the chosen one in the accent colour.</summary>
        protected void Choice(string label, string[] names, int chosen, Action<int> pick, float buttonWidth = 0f)
        {
            RectTransform row = Row(UiKit.RowHeight);
            const float labelWidth = 190f, gap = 8f;
            Text t = UiKit.MakeText(row, label, 22, FontStyle.Normal, UiKit.TextColor, TextAnchor.MiddleLeft);
            UiKit.Stretch(t.rectTransform, 34f, CanvasWidth - 34f - labelWidth);
            float width = buttonWidth > 0f ? buttonWidth : Mathf.Floor((CanvasWidth - 34f - labelWidth - 34f - gap * (names.Length - 1)) / names.Length);
            for (int i = 0; i < names.Length; i++)
            {
                int index = i;
                // (From the right edge: the last name at the margin, the first one leftmost.)
                float x = 34f + (names.Length - 1 - i) * (width + gap);
                ButtonIn(row, names[i], x, width, () => pick(index), i == chosen);
            }
        }

        /// <summary>A row with a text on the left and one or two buttons on the right.</summary>
        protected RectTransform ActionRow(string label, string button, Action onClick, string button2 = null, Action onClick2 = null, float width = 200f)
        {
            RectTransform row = Row(UiKit.RowHeight);
            Text t = UiKit.MakeText(row, label, 22, FontStyle.Normal, UiKit.TextColor, TextAnchor.MiddleLeft);
            UiKit.Stretch(t.rectTransform, 34f, 34f + (button2 != null ? 2f : 1f) * (width + 8f));
            if (button2 != null)
            {
                ButtonIn(row, button2, 34f, width, onClick2);
                ButtonIn(row, button, 34f + width + 8f, width, onClick);
            }
            else
            {
                ButtonIn(row, button, 34f, width, onClick);
            }
            return row;
        }

        private void Place()
        {
            // (Opened before the headset's first pose, at the game's start: placed for a head at the floor's origin, and brought
            // back only by looking away from it. Placed again once the head is tracked.)
            _placeWhenTracked = !Rig.HeadTracked;
            Transform head = Rig.Head;
            Vector3 forward = head.forward;
            forward.y = 0f;
            forward = forward.sqrMagnitude > 1e-4f ? forward.normalized : Rig.Root.forward;
            Vector3 right = Vector3.Cross(Vector3.up, forward);
            Vector3 pos = head.position + forward * (VRConfig.MenuDistance.Value * 0.8f) - right * 0.2f - Vector3.up * 0.12f;
            var world = new RigidPose(pos, Quaternion.LookRotation(forward, Vector3.up));
            _localPose = RigidPose.Of(Rig.Root).Inverse().Multiply(world);
        }

        public virtual void BeforeRender()
        {
            if (!IsOpen)
            {
                _awaySince = -1f;
                return;
            }
            if (_placeWhenTracked && Rig.HeadTracked)
            {
                Place();
            }
            // Looked away from it (more than 75 degrees) for 2 s: it comes back in front, as the game's menus do.
            Vector3 toPanel = _root.transform.position - Rig.Head.position;
            toPanel.y = 0f;
            Vector3 look = Rig.Head.forward;
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
            RigidPose.Of(Rig.Root).Multiply(_localPose).ApplyTo(_root.transform);
            Vector3 scale = Vector3.one * (WorldWidth / CanvasWidth);
            if (_root.transform.localScale != scale)
            {
                _root.transform.localScale = scale;
            }
        }

        public virtual void Dispose()
        {
            if (_root != null)
            {
                UnityEngine.Object.Destroy(_root);
            }
        }
    }
}
