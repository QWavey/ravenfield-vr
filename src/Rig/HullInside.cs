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
            // (Looked at on a seat change and twice a second after: the eyes settle into the seat a moment after it is taken.)
            if (seat != _lastSeat || Time.unscaledTime >= _nextLook)
            {
                _lastSeat = seat;
                _nextLook = Time.unscaledTime + 0.5f;
                _wanted = seat != null && VehicleInterior.Wanted(seat); // (the same rule as the inside: a solid hull with no viewer and no clear view out happened otherwise)
            }
            Vehicle v = _wanted && seat != null ? seat.vehicle : null;
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

        /// <summary>
        /// The eyes are inside the vehicle's body (its biggest part's box): an enclosed seat whose view the game puts on top of the vehicle
        /// (the APC's driver, 2 m over his seat) looks from outside, and an inside drawn there showed the hull from above in a black void.
        /// </summary>
        internal static bool EyesInside(Vehicle v)
        {
            VRRig rig = VRRig.Instance;
            if (v == null || rig == null || rig.Head == null)
            {
                return false;
            }
            // The three biggest parts' boxes together (the hull, the turret...): a gunner's eyes sit in the turret, over the hull's box,
            // and the Leopards' gunners got no inside in the full check.
            var parts = new List<Renderer>();
            foreach (Renderer r in v.GetComponentsInChildren<Renderer>())
            {
                if (r != null && !(r is ParticleSystemRenderer) && r.GetComponentInParent<Actor>() == null)
                {
                    parts.Add(r);
                }
            }
            if (parts.Count == 0)
            {
                return false;
            }
            parts.Sort((x, y) => y.bounds.size.sqrMagnitude.CompareTo(x.bounds.size.sqrMagnitude));
            for (int i = 0; i < parts.Count && i < 3; i++)
            {
                Bounds pb = parts[i].bounds;
                pb.Expand(0.2f);
                if (pb.Contains(rig.Head.position))
                {
                    return true;
                }
            }
            return false;
        }

        private Seat _lastSeat;
        private float _nextLook;
        private bool _wanted;
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
            _backDrawn.Add(r);
            _drawnBack++;
        }

        internal int DrawnBack => _drawnBack;

        /// <summary>For the vehicle check: the back-drawn parts and the twins taken off for a picture and put back.</summary>
        internal void ShowParts(bool backs, bool twins)
        {
            if (_backs != null && _camera != null)
            {
                _camera.RemoveCommandBuffer(UnityEngine.Rendering.CameraEvent.AfterForwardOpaque, _backs);
                if (backs)
                {
                    _camera.AddCommandBuffer(UnityEngine.Rendering.CameraEvent.AfterForwardOpaque, _backs);
                }
            }
            foreach (GameObject t in _twins)
            {
                if (t != null)
                {
                    t.SetActive(twins);
                }
            }
        }

        private readonly List<Renderer> _hidden = new List<Renderer>();

        /// <summary>
        /// Small parts in the way between the eyes and the periscope viewer (a tank's gun breech stood as a pillar before the eyes),
        /// hidden on this PC while seated, with their twins; parts over 1.5 m across (the hull's walls) are never hidden.
        /// </summary>
        internal int ClearCorridor(Transform frame, Vector3 centre, Vector3 halfSize)
        {
            if (_vehicle == null)
            {
                return 0;
            }
            int n = 0;
            foreach (Renderer r in _vehicle.GetComponentsInChildren<Renderer>())
            {
                if (r == null || !r.enabled || r.GetComponentInParent<Actor>() != null || r.bounds.size.magnitude > 1.5f)
                {
                    continue;
                }
                Vector3 local = frame.InverseTransformPoint(r.bounds.center);
                Vector3 ext = r.bounds.extents;
                if (Mathf.Abs(local.x - centre.x) < halfSize.x + ext.magnitude * 0.5f && Mathf.Abs(local.y - centre.y) < halfSize.y + ext.magnitude * 0.5f
                    && Mathf.Abs(local.z - centre.z) < halfSize.z + ext.magnitude * 0.5f)
                {
                    r.enabled = false;
                    _hidden.Add(r);
                    n++;
                }
            }
            // The back-drawn parts (unreadable meshes, not cut): one crossing the corridor is left out (a tank's gun stood as a pillar
            // before the eyes, its sight's arrows beside it).
            if (_backs != null && _backDrawn.Count > 0)
            {
                Bounds corridor = new Bounds(frame.TransformPoint(centre), Vector3.zero);
                for (int k = 0; k < 8; k++)
                {
                    corridor.Encapsulate(frame.TransformPoint(centre + Vector3.Scale(halfSize, new Vector3((k & 1) == 0 ? -1 : 1, (k & 2) == 0 ? -1 : 1, (k & 4) == 0 ? -1 : 1))));
                }
                var keep = new List<Renderer>();
                foreach (Renderer r in _backDrawn)
                {
                    if (r != null && !r.bounds.Intersects(corridor))
                    {
                        keep.Add(r);
                    }
                    else
                    {
                        n++;
                    }
                }
                if (_camera != null)
                {
                    _camera.RemoveCommandBuffer(UnityEngine.Rendering.CameraEvent.AfterForwardOpaque, _backs);
                }
                _backs.Release();
                _backs = null;
                _backDrawn.Clear();
                _drawnBack = 0;
                foreach (Renderer r in keep)
                {
                    DrawBack(r);
                }
                if (_backs != null && _camera != null)
                {
                    _camera.AddCommandBuffer(UnityEngine.Rendering.CameraEvent.AfterForwardOpaque, _backs);
                }
            }
            // The big parts' inward twins: the triangles in the corridor cut out (a gunner's eyes sit at his gun's sight, inside the gun's
            // and the turret's own shapes, and their inside stood as a pillar before the eyes in the tank). The vehicle's own mesh is
            // left whole: from inside, only the twin is drawn.
            foreach (GameObject twin in _twins)
            {
                MeshFilter f = twin != null ? twin.GetComponent<MeshFilter>() : null;
                Mesh m = f != null ? f.sharedMesh : null;
                if (m == null || !m.isReadable)
                {
                    continue;
                }
                Vector3[] v = m.vertices;
                bool touched = false;
                var kept = new List<int>[m.subMeshCount];
                for (int sub = 0; sub < m.subMeshCount; sub++)
                {
                    int[] t = m.GetTriangles(sub);
                    kept[sub] = new List<int>(t.Length);
                    for (int i = 0; i + 2 < t.Length; i += 3)
                    {
                        // (Any corner or the middle inside: long thin faces with their middle outside the column stood on as a pillar.)
                        bool inside = false;
                        for (int k = 0; k < 4 && !inside; k++)
                        {
                            Vector3 p = k < 3 ? v[t[i + k]] : (v[t[i]] + v[t[i + 1]] + v[t[i + 2]]) / 3f;
                            Vector3 local = frame.InverseTransformPoint(twin.transform.TransformPoint(p));
                            inside = Mathf.Abs(local.x - centre.x) < halfSize.x && Mathf.Abs(local.y - centre.y) < halfSize.y && Mathf.Abs(local.z - centre.z) < halfSize.z;
                        }
                        if (inside)
                        {
                            touched = true;
                            continue;
                        }
                        kept[sub].Add(t[i]);
                        kept[sub].Add(t[i + 1]);
                        kept[sub].Add(t[i + 2]);
                    }
                }
                if (!touched)
                {
                    continue;
                }
                Mesh cut = Object.Instantiate(m);
                cut.name = m.name + " (cut)";
                for (int sub = 0; sub < m.subMeshCount; sub++)
                {
                    cut.SetTriangles(kept[sub], sub);
                }
                f.sharedMesh = cut;
                _cut.Add(cut);
                n++;
            }
            return n;
        }

        private readonly List<Mesh> _cut = new List<Mesh>();
        private readonly List<Renderer> _backDrawn = new List<Renderer>();

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
            foreach (Renderer r in _hidden)
            {
                if (r != null)
                {
                    r.enabled = true;
                }
            }
            _hidden.Clear();
            foreach (Mesh m in _cut)
            {
                if (m != null)
                {
                    Object.Destroy(m);
                }
            }
            _cut.Clear();
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
            _backDrawn.Clear();
            if (_steel != null)
            {
                Object.Destroy(_steel);
                _steel = null;
            }
        }

        public void Destroy() => Clear();
    }
}
