using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using RavenfieldVR.Core;
using UnityEngine;

namespace RavenfieldVR.Util
{
    /// <summary>
    /// A Wavefront OBJ model read at run time (the mod's own props from free CC0 packs: a closed vehicle's inside). Positions, UVs,
    /// normals, faces (triangulated fans), objects and groups, and MTL materials with their diffuse colour and diffuse texture
    /// (PNG or JPG beside the file). One mesh per material; OBJ's right-handed space flipped to Unity's (x mirrored, faces reversed).
    /// Read once per file and kept: <see cref="Load"/> gives a fresh copy each time.
    /// </summary>
    internal static class ObjModel
    {
        private sealed class Part
        {
            public string Material;
            public readonly List<Vector3> V = new List<Vector3>();
            public readonly List<Vector2> T = new List<Vector2>();
            public readonly List<Vector3> N = new List<Vector3>();
            public readonly List<int> I = new List<int>();
            public readonly Dictionary<long, int> Seen = new Dictionary<long, int>();
        }

        private sealed class Loaded
        {
            public readonly List<(Mesh mesh, Material material)> Parts = new List<(Mesh, Material)>();
            public Bounds Bounds;
        }

        private static readonly Dictionary<string, Loaded> Cache = new Dictionary<string, Loaded>(StringComparer.OrdinalIgnoreCase);

        /// <summary>The model as a new GameObject (one child per material), or null when the file cannot be read.</summary>
        internal static GameObject Load(string path, Transform parent = null)
        {
            if (!Cache.TryGetValue(path, out Loaded model))
            {
                try
                {
                    model = Read(path);
                }
                catch (Exception e)
                {
                    Log.WarnOnce("obj:" + path, $"OBJ model {Path.GetFileName(path)} could not be read: {e.Message}");
                    model = null;
                }
                Cache[path] = model;
            }
            if (model == null)
            {
                return null;
            }
            var root = new GameObject(Path.GetFileNameWithoutExtension(path));
            if (parent != null)
            {
                root.transform.SetParent(parent, false);
            }
            foreach ((Mesh mesh, Material material) in model.Parts)
            {
                var g = new GameObject(material != null ? material.name : "part");
                g.transform.SetParent(root.transform, false);
                g.AddComponent<MeshFilter>().sharedMesh = mesh;
                MeshRenderer r = g.AddComponent<MeshRenderer>();
                r.sharedMaterial = material;
                r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            }
            return root;
        }

        /// <summary>The model's bounds in its own space (after the flip), or an empty box.</summary>
        internal static Bounds BoundsOf(string path) => Cache.TryGetValue(path, out Loaded m) && m != null ? m.Bounds : new Bounds();

        private static float F(string s) => float.Parse(s, NumberStyles.Float, CultureInfo.InvariantCulture);

        private static Loaded Read(string path)
        {
            var pos = new List<Vector3>();
            var uv = new List<Vector2>();
            var nrm = new List<Vector3>();
            var parts = new Dictionary<string, Part>();
            Dictionary<string, Material> materials = new Dictionary<string, Material>();
            Part current = null;
            string dir = Path.GetDirectoryName(path);
            Part PartFor(string mat)
            {
                mat = mat ?? "";
                if (!parts.TryGetValue(mat, out Part p))
                {
                    p = new Part { Material = mat };
                    parts[mat] = p;
                }
                return p;
            }
            foreach (string raw in File.ReadLines(path))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line[0] == '#')
                {
                    continue;
                }
                string[] w = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                switch (w[0])
                {
                    case "v":
                        pos.Add(new Vector3(-F(w[1]), F(w[2]), F(w[3]))); // (x mirrored: OBJ is right-handed)
                        break;
                    case "vt":
                        uv.Add(new Vector2(F(w[1]), w.Length > 2 ? F(w[2]) : 0f));
                        break;
                    case "vn":
                        nrm.Add(new Vector3(-F(w[1]), F(w[2]), F(w[3])));
                        break;
                    case "mtllib":
                        foreach (KeyValuePair<string, Material> m in ReadMtl(Path.Combine(dir, line.Substring(7).Trim())))
                        {
                            materials[m.Key] = m.Value;
                        }
                        break;
                    case "usemtl":
                        current = PartFor(w.Length > 1 ? w[1] : "");
                        break;
                    case "f":
                        current = current ?? PartFor("");
                        var corner = new int[w.Length - 1];
                        for (int i = 1; i < w.Length; i++)
                        {
                            corner[i - 1] = Vertex(current, w[i], pos, uv, nrm);
                        }
                        for (int i = 1; i + 1 < corner.Length; i++)
                        {
                            // (Wound the other way round: the mirror turned the faces inside out.)
                            current.I.Add(corner[0]);
                            current.I.Add(corner[i + 1]);
                            current.I.Add(corner[i]);
                        }
                        break;
                }
            }
            var model = new Loaded();
            bool first = true;
            foreach (Part p in parts.Values)
            {
                if (p.I.Count == 0)
                {
                    continue;
                }
                var mesh = new Mesh { name = p.Material, indexFormat = p.V.Count > 65000 ? UnityEngine.Rendering.IndexFormat.UInt32 : UnityEngine.Rendering.IndexFormat.UInt16 };
                mesh.SetVertices(p.V);
                if (p.T.Count == p.V.Count)
                {
                    mesh.SetUVs(0, p.T);
                }
                mesh.SetTriangles(p.I, 0);
                if (p.N.Count == p.V.Count)
                {
                    mesh.SetNormals(p.N);
                }
                else
                {
                    mesh.RecalculateNormals();
                }
                mesh.RecalculateBounds();
                materials.TryGetValue(p.Material, out Material mat);
                model.Parts.Add((mesh, mat ?? new Material(Shader.Find("Standard")) { name = p.Material }));
                if (first)
                {
                    model.Bounds = mesh.bounds;
                    first = false;
                }
                else
                {
                    model.Bounds.Encapsulate(mesh.bounds);
                }
            }
            return model;
        }

        /// <summary>One corner "v/vt/vn" (indices from 1, or negative from the end) as a vertex of the part, shared when repeated.</summary>
        private static int Vertex(Part p, string token, List<Vector3> pos, List<Vector2> uv, List<Vector3> nrm)
        {
            string[] k = token.Split('/');
            int Index(int i, int count) => i > 0 ? i - 1 : count + i;
            int vi = Index(int.Parse(k[0], CultureInfo.InvariantCulture), pos.Count);
            int ti = k.Length > 1 && k[1].Length > 0 ? Index(int.Parse(k[1], CultureInfo.InvariantCulture), uv.Count) : -1;
            int ni = k.Length > 2 && k[2].Length > 0 ? Index(int.Parse(k[2], CultureInfo.InvariantCulture), nrm.Count) : -1;
            long key = ((long)vi << 40) ^ ((long)(ti + 1) << 20) ^ (ni + 1);
            if (p.Seen.TryGetValue(key, out int at))
            {
                return at;
            }
            at = p.V.Count;
            p.V.Add(pos[vi]);
            p.T.Add(ti >= 0 ? uv[ti] : Vector2.zero);
            if (ni >= 0)
            {
                p.N.Add(nrm[ni]);
            }
            p.Seen[key] = at;
            return at;
        }

        private static Dictionary<string, Material> ReadMtl(string path)
        {
            var result = new Dictionary<string, Material>();
            if (!File.Exists(path))
            {
                return result;
            }
            Shader standard = Shader.Find("Standard");
            Material m = null;
            string dir = Path.GetDirectoryName(path);
            foreach (string raw in File.ReadLines(path))
            {
                string line = raw.Trim();
                string[] w = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (w.Length == 0)
                {
                    continue;
                }
                switch (w[0])
                {
                    case "newmtl":
                        m = new Material(standard) { name = w.Length > 1 ? w[1] : "material" };
                        m.SetFloat("_Glossiness", 0.2f);
                        result[m.name] = m;
                        break;
                    case "Kd":
                        if (m != null && w.Length >= 4)
                        {
                            m.color = new Color(F(w[1]), F(w[2]), F(w[3]));
                        }
                        break;
                    case "map_Kd":
                        if (m != null)
                        {
                            string file = Path.Combine(dir, line.Substring(6).Trim());
                            if (File.Exists(file))
                            {
                                var tex = new Texture2D(2, 2, TextureFormat.RGBA32, true);
                                if (ImageConversion.LoadImage(tex, File.ReadAllBytes(file)))
                                {
                                    tex.wrapMode = TextureWrapMode.Repeat;
                                    m.mainTexture = tex;
                                }
                            }
                        }
                        break;
                }
            }
            return result;
        }
    }
}
