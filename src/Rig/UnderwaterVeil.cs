using UnityEngine;
using UnityEngine.Rendering;

namespace RavenfieldVR.Rig
{
    /// <summary>
    /// The water's colour with the eyes under it. The flat game draws it in <c>UnderwaterCamera.OnGUI</c>, a blue over the whole screen from the first
    /// person camera, which never reaches a headset: diving, the view stayed as clear as air (2026-11-07). A head-locked quad on the UI camera's layer,
    /// under the HUD, by the eyes' own height against the water and eased, as the surface's waves dip over a head held at it.
    /// </summary>
    public sealed class UnderwaterVeil
    {
        private const float Distance = 0.25f;
        /// <summary>The game's colour (0.2, 0.2, 0.5) a little thinner than its 0.9: all round the eyes that much left nothing to swim by.</summary>
        private const float Most = 0.8f;

        private readonly Transform _head;
        private readonly GameObject _quad;
        private readonly Mesh _mesh;
        private readonly Material _material;
        private readonly MeshRenderer _renderer;
        private float _alpha;

        public UnderwaterVeil(Transform head)
        {
            _head = head;
            _quad = new GameObject("Underwater Veil") { layer = 5 };
            _quad.transform.SetParent(head, false);
            _quad.transform.localPosition = new Vector3(0f, 0f, Distance);
            float size = 2f * Distance * Mathf.Tan(70f * Mathf.Deg2Rad);
            _quad.transform.localScale = new Vector3(size, size, 1f);
            _mesh = new Mesh { name = "UnderwaterVeilQuad" };
            _mesh.vertices = new[] { new Vector3(-0.5f, -0.5f), new Vector3(0.5f, -0.5f), new Vector3(-0.5f, 0.5f), new Vector3(0.5f, 0.5f) };
            _mesh.triangles = new[] { 0, 2, 1, 2, 3, 1 };
            _mesh.bounds = new Bounds(Vector3.zero, Vector3.one * 10f);
            _quad.AddComponent<MeshFilter>().sharedMesh = _mesh;
            // (Under the HUD's canvases, drawn at 3000: health and ammunition stay readable under water.)
            _material = new Material(Shader.Find("Sprites/Default")) { renderQueue = 2900 };
            _material.color = new Color(0.2f, 0.2f, 0.5f, 0f);
            _renderer = _quad.AddComponent<MeshRenderer>();
            _renderer.sharedMaterial = _material;
            _renderer.shadowCastingMode = ShadowCastingMode.Off;
            _renderer.receiveShadows = false;
            _renderer.enabled = false;
        }

        public void Update()
        {
            Actor me = Util.GameContext.PlayerActor;
            // (Not in a seat made for it: a submarine's crew sits dry, as the game's own immersion reads it.)
            bool under = me != null && !me.dead && !(me.IsSeated() && me.seat.allowUnderwater) && WaterLevel.IsInWater(_head.position);
            _alpha = Mathf.MoveTowards(_alpha, under ? Most : 0f, 5f * Time.unscaledDeltaTime);
            bool visible = _alpha > 0.005f;
            if (_renderer.enabled != visible)
            {
                _renderer.enabled = visible;
            }
            if (visible)
            {
                _material.color = new Color(0.2f, 0.2f, 0.5f, _alpha);
            }
        }

        public void Destroy()
        {
            if (_quad != null)
            {
                Object.Destroy(_quad);
            }
            if (_mesh != null)
            {
                Object.Destroy(_mesh);
            }
            if (_material != null)
            {
                Object.Destroy(_material);
            }
        }
    }
}
