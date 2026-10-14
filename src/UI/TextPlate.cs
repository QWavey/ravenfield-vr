using UnityEngine;
using UnityEngine.Rendering;

namespace RavenfieldVR.UI
{
    /// <summary>
    /// A soft-edged dark rounded plate just behind a TextMesh, fitted to the text, so it reads against a bright sky as well
    /// as in a dark room (white text on an 80% plate keeps about 8:1 contrast over a white sky; bare, about 1.2:1).
    /// </summary>
    internal sealed class TextPlate
    {
        private static Texture2D _texture;
        private static Mesh _quad;

        private readonly Renderer _textRenderer;
        private readonly Transform _text;
        private readonly Transform _plate;
        private readonly Material _material;
        private readonly float _opacity;
        private readonly float _margin;

        /// <param name="opacity">How dark the plate is at full visibility.</param>
        /// <param name="margin">Room round the text (m).</param>
        public TextPlate(TextMesh text, Material textMaterial, float opacity, float margin)
        {
            EnsureAssets();
            _text = text.transform;
            _textRenderer = text.GetComponent<Renderer>();
            _opacity = opacity;
            _margin = margin;
            var plate = new GameObject("plate") { layer = text.gameObject.layer };
            plate.transform.SetParent(_text, false);
            plate.AddComponent<MeshFilter>().sharedMesh = _quad;
            // Drawn just before the text (the font's material is in the transparent queue).
            _material = new Material(Shader.Find("Sprites/Default")) { mainTexture = _texture, renderQueue = textMaterial.renderQueue - 1 };
            var r = plate.AddComponent<MeshRenderer>();
            r.sharedMaterial = _material;
            r.shadowCastingMode = ShadowCastingMode.Off;
            r.receiveShadows = false;
            _plate = plate.transform;
            SetAlpha(1f);
        }

        /// <summary>Fits the plate round the text as laid out now (measured facing straight ahead, then turned back).</summary>
        public void Fit()
        {
            Quaternion rot = _text.rotation;
            _text.rotation = Quaternion.identity;
            Bounds b = _textRenderer.bounds;
            Vector3 scale = _text.lossyScale;
            Vector3 position = _text.position;
            _text.rotation = rot;
            if (b.size.sqrMagnitude < 1e-10f)
            {
                return; // nothing laid out yet
            }
            Vector3 centre = b.center - position;
            float sx = Mathf.Max(1e-5f, Mathf.Abs(scale.x)), sy = Mathf.Max(1e-5f, Mathf.Abs(scale.y)), sz = Mathf.Max(1e-5f, Mathf.Abs(scale.z));
            // Just behind the text, seen from the eye (the text faces down its own -z).
            _plate.localPosition = new Vector3(centre.x / sx, centre.y / sy, 0.002f / sz);
            _plate.localScale = new Vector3((b.size.x + _margin * 2f) / sx, (b.size.y + _margin * 1.4f) / sy, 1f);
        }

        /// <summary>Fades the plate with its text (0..1).</summary>
        public void SetAlpha(float alpha) => _material.color = new Color(0.04f, 0.05f, 0.06f, _opacity * alpha);

        public void Destroy()
        {
            if (_plate != null)
            {
                Object.Destroy(_plate.gameObject);
            }
            if (_material != null)
            {
                Object.Destroy(_material);
            }
        }

        private static void EnsureAssets()
        {
            if (_quad == null)
            {
                _quad = new Mesh { name = "TextPlate" };
                _quad.vertices = new[] { new Vector3(-0.5f, -0.5f), new Vector3(0.5f, -0.5f), new Vector3(-0.5f, 0.5f), new Vector3(0.5f, 0.5f) };
                _quad.uv = new[] { new Vector2(0, 0), new Vector2(1, 0), new Vector2(0, 1), new Vector2(1, 1) };
                _quad.triangles = new[] { 0, 2, 1, 2, 3, 1 };
                _quad.RecalculateBounds();
            }
            if (_texture == null)
            {
                // A rounded rectangle with soft edges (alpha), twice as wide as high.
                const int w = 96, h = 48;
                _texture = new Texture2D(w, h, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, name = "TextPlate" };
                var px = new Color32[w * h];
                float r = h * 0.35f;
                for (int y = 0; y < h; y++)
                {
                    for (int x = 0; x < w; x++)
                    {
                        float dx = Mathf.Max(Mathf.Abs(x + 0.5f - w * 0.5f) - (w * 0.5f - r - 1f), 0f);
                        float dy = Mathf.Max(Mathf.Abs(y + 0.5f - h * 0.5f) - (h * 0.5f - r - 1f), 0f);
                        float a = Mathf.Clamp01(r - Mathf.Sqrt(dx * dx + dy * dy) + 0.5f);
                        px[y * w + x] = new Color32(255, 255, 255, (byte)(255f * a));
                    }
                }
                _texture.SetPixels32(px);
                _texture.Apply(false, true);
            }
        }
    }
}
