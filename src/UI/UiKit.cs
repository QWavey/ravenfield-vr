using System;
using UnityEngine;
using UnityEngine.UI;

namespace RavenfieldVR.UI
{
    /// <summary>
    /// Shared look of the mod's own panels: gunmetal panel, off-white type, one signal-amber accent for the active
    /// choice, square flat controls, the game's own UI font when it can be found.
    /// </summary>
    internal static class UiKit
    {
        public const float RowHeight = 58f;

        public static readonly Color PanelColor = new Color32(27, 30, 33, 240);
        public static readonly Color RowRule = new Color32(58, 63, 68, 255);
        public static readonly Color TextColor = new Color32(226, 228, 223, 255);
        public static readonly Color MutedText = new Color32(150, 156, 160, 255);
        public static readonly Color ButtonColor = new Color32(42, 46, 51, 255);
        public static readonly Color ButtonHover = new Color32(56, 61, 67, 255);
        public static readonly Color Accent = new Color32(201, 160, 74, 255);
        public static readonly Color AccentText = new Color32(24, 22, 18, 255);

        private static Font _font;

        public static Font Font
        {
            get
            {
                if (_font == null)
                {
                    _font = FindGameFont();
                }
                return _font;
            }
        }

        private static Font FindGameFont()
        {
            try
            {
                if (IngameMenuUi.instance != null)
                {
                    Text t = IngameMenuUi.instance.GetComponentInChildren<Text>(true);
                    if (t != null && t.font != null)
                    {
                        return t.font;
                    }
                }
                foreach (Text t in Resources.FindObjectsOfTypeAll<Text>())
                {
                    if (t.font != null && t.font.name != "Arial")
                    {
                        return t.font;
                    }
                }
            }
            catch (Exception)
            {
                // Fall through to the built-in font.
            }
            return Resources.GetBuiltinResource<Font>("Arial.ttf");
        }

        public static Text MakeText(RectTransform parent, string text, int size, FontStyle style, Color color, TextAnchor anchor)
        {
            var go = new GameObject("Text", typeof(RectTransform));
            go.layer = 5;
            var rt = (RectTransform)go.transform;
            rt.SetParent(parent, false);
            Text t = go.AddComponent<Text>();
            t.font = Font;
            t.text = text;
            t.fontSize = size;
            t.fontStyle = style;
            t.color = color;
            t.alignment = anchor;
            t.raycastTarget = false;
            t.horizontalOverflow = HorizontalWrapMode.Overflow;
            return t;
        }

        /// <summary>A button anchored to the right edge of <paramref name="parent"/>, <paramref name="x"/> from it.</summary>
        public static Button MakeButton(RectTransform parent, string label, float x, float width, Action onClick, out Text text)
        {
            var go = new GameObject("Button", typeof(RectTransform));
            go.layer = 5;
            var rt = (RectTransform)go.transform;
            rt.SetParent(parent, false);
            rt.anchorMin = new Vector2(1f, 0.5f);
            rt.anchorMax = new Vector2(1f, 0.5f);
            rt.pivot = new Vector2(1f, 0.5f);
            rt.anchoredPosition = new Vector2(-x, 0f);
            rt.sizeDelta = new Vector2(width, RowHeight - 14f);
            Image img = go.AddComponent<Image>();
            img.color = Color.white;
            Button b = go.AddComponent<Button>();
            ColorBlock colors = b.colors;
            colors.normalColor = ButtonColor;
            colors.highlightedColor = ButtonHover;
            colors.pressedColor = Accent;
            colors.selectedColor = ButtonColor;
            colors.fadeDuration = 0.05f;
            b.colors = colors;
            b.navigation = new Navigation { mode = Navigation.Mode.None };
            b.onClick.AddListener(() => onClick());
            text = MakeText(rt, label, 21, FontStyle.Normal, TextColor, TextAnchor.MiddleCenter);
            Stretch(text.rectTransform, 6f, 6f);
            return b;
        }

        public static void Stretch(RectTransform rt, float left, float right)
        {
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = new Vector2(left, 0f);
            rt.offsetMax = new Vector2(-right, 0f);
        }

        /// <summary>A world-space canvas root with the panel background, on the UI layer.</summary>
        public static Canvas MakePanel(string name, Camera eventCamera, out GameObject root, out RectTransform content)
        {
            root = new GameObject(name);
            UnityEngine.Object.DontDestroyOnLoad(root);
            root.layer = 5;
            Canvas canvas = root.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            canvas.worldCamera = eventCamera;
            canvas.sortingOrder = 500;
            root.AddComponent<GraphicRaycaster>();
            var rect = (RectTransform)root.transform;
            rect.pivot = new Vector2(0.5f, 0.5f);
            root.AddComponent<Image>().color = PanelColor;

            var contentGo = new GameObject("Content", typeof(RectTransform));
            contentGo.layer = 5;
            content = (RectTransform)contentGo.transform;
            content.SetParent(rect, false);
            content.anchorMin = new Vector2(0f, 1f);
            content.anchorMax = new Vector2(1f, 1f);
            content.pivot = new Vector2(0.5f, 1f);
            content.anchoredPosition = Vector2.zero;
            return canvas;
        }
    }
}
