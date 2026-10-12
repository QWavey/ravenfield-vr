using UnityEngine;
using UnityEngine.Rendering;

namespace RavenfieldVR.Rig
{
    /// <summary>
    /// Tunnel vignette shown while moving with the stick or smooth turning. A head-locked quad with a
    /// procedurally generated radial alpha mask; its opacity follows locomotion speed.
    /// </summary>
    public sealed class ComfortVignette
    {
        private const float Distance = 0.25f;
        private readonly GameObject _quad;
        private readonly Material _material;
        private readonly MeshRenderer _renderer;
        private float _current;

        public ComfortVignette(Transform head)
        {
            _quad = new GameObject("Comfort Vignette");
            _quad.layer = 5;
            _quad.transform.SetParent(head, false);
            _quad.transform.localPosition = new Vector3(0f, 0f, Distance);
            float size = 2f * Distance * Mathf.Tan(70f * Mathf.Deg2Rad);
            _quad.transform.localScale = new Vector3(size, size, 1f);

            var mesh = new Mesh { name = "VignetteQuad" };
            mesh.vertices = new[] { new Vector3(-0.5f, -0.5f), new Vector3(0.5f, -0.5f), new Vector3(-0.5f, 0.5f), new Vector3(0.5f, 0.5f) };
            mesh.uv = new[] { new Vector2(0, 0), new Vector2(1, 0), new Vector2(0, 1), new Vector2(1, 1) };
            mesh.triangles = new[] { 0, 2, 1, 2, 3, 1 };
            mesh.RecalculateBounds();
            mesh.bounds = new Bounds(Vector3.zero, Vector3.one * 10f);
            _quad.AddComponent<MeshFilter>().sharedMesh = mesh;

            _material = new Material(Shader.Find("Sprites/Default")) { mainTexture = BuildMask(128), renderQueue = 4000 };
            _material.color = new Color(0f, 0f, 0f, 0f);
            _renderer = _quad.AddComponent<MeshRenderer>();
            _renderer.sharedMaterial = _material;
            _renderer.shadowCastingMode = ShadowCastingMode.Off;
            _renderer.receiveShadows = false;
            _renderer.enabled = false;
        }

        private static Texture2D BuildMask(int size)
        {
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, name = "VignetteMask" };
            var pixels = new Color32[size * size];
            float half = (size - 1) * 0.5f;
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float dx = (x - half) / half, dy = (y - half) / half;
                    float r = Mathf.Sqrt(dx * dx + dy * dy);
                    // Clear centre out to ~30% of the view, soft edge to fully dark at ~62%.
                    float a = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.3f, 0.62f, r));
                    pixels[y * size + x] = new Color32(0, 0, 0, (byte)(a * 255f));
                }
            }
            tex.SetPixels32(pixels);
            tex.Apply(false, true);
            return tex;
        }

        /// <param name="motion">0..1 locomotion intensity this frame.</param>
        /// <param name="strength">user setting 0..1.</param>
        public void Update(float motion, float strength)
        {
            float target = Mathf.Clamp01(motion) * strength;
            float rate = target > _current ? 6f : 3f;
            _current = Mathf.MoveTowards(_current, target, rate * Time.unscaledDeltaTime);
            bool visible = _current > 0.01f;
            if (_renderer.enabled != visible)
            {
                _renderer.enabled = visible;
            }
            if (visible)
            {
                _material.color = new Color(0f, 0f, 0f, _current);
            }
        }

        public void Destroy()
        {
            if (_quad != null)
            {
                Object.Destroy(_quad);
            }
        }
    }
}
