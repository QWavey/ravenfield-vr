using System.Collections.Generic;
using RavenfieldVR.Core;
using RavenfieldVR.Util;
using UnityEngine;

namespace RavenfieldVR.Rig
{
    /// <summary>
    /// A closed vehicle's own body drawn from inside too (the user, 2026-11-15, from a mod's armoured vehicle: "still see through parts
    /// in modded tanks etc ... first we need to make sure that the parts are not see through anymore"). A game model's faces are drawn
    /// from their front only, so from a seat inside the hull every wall was a window. While the player sits in an enclosed seat, each of
    /// the vehicle's meshes gets a twin facing inward (its triangles turned round, its normals reversed), with the same materials: the
    /// hull's real shape and texture from inside. Glass and other see-through materials stay as they are; parts of a lower detail
    /// level are left out (their twins would be drawn over the full ones). Twins are made once per mesh and taken away on leaving.
    /// </summary>
    internal sealed class HullInside
    {
        private static readonly Dictionary<Mesh, Mesh> Inward = new Dictionary<Mesh, Mesh>();
        private readonly List<GameObject> _twins = new List<GameObject>();
        private Vehicle _vehicle;
        private readonly Camera _camera;
        private UnityEngine.Rendering.CommandBuffer _backs;
        private int _drawnBack;

        public HullInside(Camera camera)
        {
            _camera = camera;
        }

        internal int Twins => _twins.Count;

        public void Update()
        {
            Actor actor = GameContext.PlayerActor;
            Seat seat = actor != null && !actor.dead && actor.IsSeated() ? actor.seat : null;
            Vehicle v = seat != null && seat.enclosed && VRConfig.Interiors.Value ? seat.vehicle : null;
            if (v == _vehicle)
            {
                return;
            }
            Clear();
            _vehicle = v;
            if (v != null)
            {
                Build(v);
            }
        }

        private Material _steel;
        private Color _tint = new Color(0.35f, 0.37f, 0.33f);

        private void Build(Vehicle v)
        {
            Renderer biggest = null;
            float size = 0f;
            foreach (Renderer r in v.GetComponentsInChildren<Renderer>())
            {
                if (r != null && r.sharedMaterial != null && r.bounds.size.sqrMagnitude > size && r.sharedMaterial.HasProperty("_Color"))
                {
                    size = r.bounds.size.sqrMagnitude;
                    biggest = r;
                }
            }
            if (biggest != null)
            {
                Color c = biggest.sharedMaterial.color;
                float g = c.grayscale;
                _tint = Color.Lerp(c, new Color(g, g, g), 0.5f);
                _tint.a = 1f;
            }
            var lowerDetail = new HashSet<Renderer>();
            foreach (LODGroup g in v.GetComponentsInChildren<LODGroup>(true))
            {
                LOD[] lods = g.GetLODs();
                for (int i = 1; i < lods.Length; i++)
                {
                    foreach (Renderer r in lods[i].renderers)
                    {
                        if (r != null)
                        {
                            lowerDetail.Add(r);
                        }
                    }
                }
            }
            int made = 0, skipped = 0;
            foreach (MeshRenderer r in v.GetComponentsInChildren<MeshRenderer>(true))
            {
                // (Not the seated soldiers' bodies and guns: they hang under the vehicle while seated.)
                if (r == null || lowerDetail.Contains(r) || r.sharedMaterials == null || r.GetComponentInParent<Actor>() != null)
                {
                    continue;
                }
                MeshFilter f = r.GetComponent<MeshFilter>();
                Mesh mesh = f != null ? f.sharedMesh : null;
                if (mesh == null || !Opaque(r.sharedMaterials))
                {
                    skipped++;
                    continue;
                }
                Mesh inward = InwardOf(mesh);
                if (inward == null)
                {
                    // Not readable (most game meshes): drawn again with the faces' culling turned round, by the headset's camera.
                    DrawBack(r);
                    continue;
                }
                var twin = new GameObject(r.name + " (inside)");
                twin.layer = r.gameObject.layer;
                twin.transform.SetParent(r.transform, false);
                twin.AddComponent<MeshFilter>().sharedMesh = inward;
                MeshRenderer tr = twin.AddComponent<MeshRenderer>();
                tr.sharedMaterials = r.sharedMaterials;
                tr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                tr.receiveShadows = false; // (the hull's own shadow lay on every inner face: black)
                tr.enabled = r.enabled;
                _twins.Add(twin);
                made++;
            }
            foreach (SkinnedMeshRenderer r in v.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                if (r != null && !lowerDetail.Contains(r) && r.sharedMaterials != null && Opaque(r.sharedMaterials) && r.GetComponentInParent<Actor>() == null)
                {
                    DrawBack(r);
                }
            }
            if (_backs != null && _camera != null)
            {
                _camera.AddCommandBuffer(UnityEngine.Rendering.CameraEvent.AfterForwardOpaque, _backs);
            }
            Log.InfoOnce("hull-inside:" + v.name, $"Vehicle {v.name} drawn from inside: {made} part(s) given an inward twin, {_drawnBack} drawn back side out, {skipped} left as they are (see-through).");
        }

        /// <summary>The renderer's back faces drawn by the headset's camera after its opaque pass (each material's forward pass).</summary>
        private void DrawBack(Renderer r)
        {
            if (_camera == null)
            {
                return;
            }
            if (_backs == null)
            {
                _backs = new UnityEngine.Rendering.CommandBuffer { name = "RavenfieldVR hull inside" };
                _backs.SetInvertCulling(true);
            }
            // (Drawn here the game's own pass got no light and came out black: one steel material of the vehicle's tint, glowing a little.)
            if (_steel == null)
            {
                _steel = new Material(Shader.Find("Standard")) { color = _tint * 0.8f };
                _steel.EnableKeyword("_EMISSION");
                _steel.SetColor("_EmissionColor", _tint * 0.55f);
                _steel.SetFloat("_Glossiness", 0.1f);
            }
            int subs = r.sharedMaterials.Length;
            for (int i = 0; i < subs; i++)
            {
                _backs.DrawRenderer(r, _steel, i, 0);
            }
            _drawnBack++;
        }

        internal int DrawnBack => _drawnBack;

        /// <summary>No see-through material on it (glass, a sight's lens, a decal): those stay windows.</summary>
        private static bool Opaque(Material[] materials)
        {
            foreach (Material m in materials)
            {
                if (m == null || m.renderQueue >= 2450)
                {
                    return false;
                }
            }
            return true;
        }

        /// <summary>The mesh turned inside out: every triangle wound the other way, normals reversed (made once per mesh).</summary>
        private static Mesh InwardOf(Mesh mesh)
        {
            if (Inward.TryGetValue(mesh, out Mesh done))
            {
                return done;
            }
            Mesh inward = null;
            if (mesh.isReadable)
            {
                inward = Object.Instantiate(mesh);
                inward.name = mesh.name + " (inward)";
                for (int s = 0; s < inward.subMeshCount; s++)
                {
                    int[] t = inward.GetTriangles(s);
                    for (int i = 0; i + 2 < t.Length; i += 3)
                    {
                        int a = t[i];
                        t[i] = t[i + 1];
                        t[i + 1] = a;
                    }
                    inward.SetTriangles(t, s);
                }
                Vector3[] n = inward.normals;
                if (n != null && n.Length > 0)
                {
                    for (int i = 0; i < n.Length; i++)
                    {
                        n[i] = -n[i];
                    }
                    inward.normals = n;
                }
            }
            Inward[mesh] = inward;
            return inward;
        }

        private void Clear()
        {
            foreach (GameObject g in _twins)
            {
                if (g != null)
                {
                    Object.Destroy(g);
                }
            }
            _twins.Clear();
            _vehicle = null;
            if (_backs != null)
            {
                if (_camera != null)
                {
                    _camera.RemoveCommandBuffer(UnityEngine.Rendering.CameraEvent.AfterForwardOpaque, _backs);
                }
                _backs.Release();
                _backs = null;
            }
            _drawnBack = 0;
            if (_steel != null)
            {
                Object.Destroy(_steel);
                _steel = null;
            }
        }

        public void Destroy() => Clear();
    }
}
