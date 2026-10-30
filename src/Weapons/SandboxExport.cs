using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using BepInEx;
using RavenfieldVR.Core;
using RavenfieldVR.Runtime;
using UnityEngine;

namespace RavenfieldVR.Weapons
{
    /// <summary>
    /// -rfvr-export (all weapons) or -rfvr-export=name1,name2: every analysed weapon written for the sandbox (tools/sandbox),
    /// where hands, poses and reloads are tried without the game: its drawn model at rest in gun space as a .glb (each
    /// renderer a node named by its transform path, skinned meshes posed at idle, textures embedded) and what the analysis
    /// found as rig.json (grip, support, muzzle, rack point, slide / handle / magazine / hinge parts and their motion, the
    /// magazine's seat and line, finger curls). Unity's left-handed space is turned right-handed for both (z negated).
    /// Written to BepInEx/RavenfieldVR-export/&lt;weapon&gt;/ with an index.json of all of them.
    /// </summary>
    internal static class SandboxExport
    {
        public static string Dir => Path.Combine(Paths.BepInExRootPath, "RavenfieldVR-export");

        private static string[] _filters;

        public static bool Requested
        {
            get
            {
                Filters();
                return _filters != null;
            }
        }

        private static void Filters()
        {
            if (_filters != null)
            {
                return;
            }
            string arg = Environment.GetCommandLineArgs().FirstOrDefault(a => a.StartsWith("-rfvr-export", StringComparison.OrdinalIgnoreCase));
            if (arg == null)
            {
                return;
            }
            int eq = arg.IndexOf('=');
            _filters = eq < 0 ? new[] { "*" } : arg.Substring(eq + 1).Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).Select(f => f.Trim()).ToArray();
        }

        public static bool Wants(string key)
        {
            Filters();
            if (_filters == null)
            {
                return false;
            }
            foreach (string f in _filters)
            {
                if (f == "*" || key.IndexOf(f, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
            }
            return false;
        }

        private static readonly List<string> Written = new List<string>();

        private static readonly Dictionary<string, string> WrittenKey = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> ExportedKeys = new HashSet<string>();

        /// <summary>This weapon was written in this run (or failed to be): not analysed again for the export.</summary>
        public static bool Exported(string key) => ExportedKeys.Contains(key);

        /// <summary>A file and folder name for a weapon (its key may hold '#', '/', colour tags).</summary>
        public static string SafeName(string key)
        {
            var sb = new StringBuilder();
            foreach (char c in key)
            {
                sb.Append(char.IsLetterOrDigit(c) || c == '-' || c == '_' || c == '.' ? c : '_');
            }
            return sb.ToString().Trim('_');
        }

        // ---- the model ----------------------------------------------------------------------------------------------

        /// <summary>The weapon's drawn model at rest in gun space, as a .glb; null when nothing could be read.</summary>
        public static byte[] BuildGlb(WeaponRigData d, Transform root, HashSet<Renderer> armRenderers, PoseSampler sampler, RigidPose gunIdle, out string note)
        {
            var w = new GlbWriter();
            RigidPose gunInv = gunIdle.Inverse();
            int unreadable = 0, hidden = 0, away = 0, fromGpu = 0;
            // The parts that move (slide, handle, bolt, magazine and its pieces, cover, the slide's companions): a skinned
            // mesh's triangles on their bones are split off as parts of their own.
            var roles = new List<Transform>();
            foreach (int[] p in new[] { d.SlidePath, d.HandlePath, d.BoltPath, d.MagazinePath, d.HingePath }.Concat(d.MagazinePartPaths).Concat(d.SlideCompanionPaths))
            {
                Transform rt = p != null ? WeaponRig.Resolve(root, p) : null;
                if (rt != null && !roles.Contains(rt))
                {
                    roles.Add(rt);
                }
            }
            // What the mod hides while the gun is held: spare magazines and the reload's rounds waiting beside the gun.
            var spare = new List<Transform>();
            foreach (int[] p in d.SparePartPaths)
            {
                Transform s = WeaponRig.Resolve(root, p);
                if (s != null)
                {
                    spare.Add(s);
                }
            }
            foreach (Renderer r in root.GetComponentsInChildren<Renderer>(true))
            {
                if (r == null || armRenderers.Contains(r) || r is ParticleSystemRenderer || !(r is MeshRenderer || r is SkinnedMeshRenderer))
                {
                    continue;
                }
                Transform t = r.transform;
                if (!sampler.IdleVisible(t) || !r.enabled || spare.Any(s => t.IsChildOf(s)))
                {
                    hidden++;
                    continue;
                }
                Mesh mesh = r is SkinnedMeshRenderer smr ? smr.sharedMesh : t.GetComponent<MeshFilter>()?.sharedMesh;
                if (mesh == null || mesh.vertexCount == 0)
                {
                    continue;
                }
                // Parked far from the gun (a lower level of detail kept 2 m away, a prop the animation hides off-screen): by its
                // mesh's middle at rest (a pivot at the gun's origin is common).
                Vector3 middle = gunInv.Multiply(sampler.Idle(t)).TransformPoint(Vector3.Scale(mesh.bounds.center, t.lossyScale));
                if ((middle - d.GripPoint).sqrMagnitude > 0.9f * 0.9f)
                {
                    away++;
                    continue;
                }
                Vector3[] v, n;
                Vector2[] uv;
                BoneWeight[] weights;
                List<int[]> subs;
                if (mesh.isReadable)
                {
                    v = mesh.vertices;
                    n = mesh.normals;
                    uv = mesh.uv;
                    weights = mesh.boneWeights;
                    subs = new List<int[]>();
                    for (int sm = 0; sm < mesh.subMeshCount; sm++)
                    {
                        subs.Add(mesh.GetTopology(sm) == MeshTopology.Triangles ? mesh.GetTriangles(sm) : new int[0]);
                    }
                }
                else if (GpuMeshReader.TryRead(mesh, out v, out n, out uv, out weights, out subs))
                {
                    fromGpu++; // its asset bundle dropped the CPU copy: read back from the graphics card
                    uv = uv ?? new Vector2[0];
                    weights = weights ?? new BoneWeight[0];
                }
                else
                {
                    // Its vertices cannot be read at all: a stand-in box of its bounds, at its place, so poses and coordinates
                    // still have the part.
                    unreadable++;
                    RigidPose bp = gunInv.Multiply(sampler.Idle(t));
                    Vector3 bs = t.lossyScale;
                    Bounds mb = mesh.bounds;
                    var corners = new Vector3[8];
                    var cn = new Vector3[8];
                    for (int c = 0; c < 8; c++)
                    {
                        Vector3 local = mb.center + Vector3.Scale(mb.extents, new Vector3((c & 1) == 0 ? -1 : 1, (c & 2) == 0 ? -1 : 1, (c & 4) == 0 ? -1 : 1));
                        corners[c] = bp.TransformPoint(Vector3.Scale(local, bs));
                        cn[c] = (corners[c] - bp.TransformPoint(Vector3.Scale(mb.center, bs))).normalized;
                    }
                    int[] box =
                    {
                        0, 2, 1, 1, 2, 3, 4, 5, 6, 5, 7, 6, 0, 1, 4, 1, 5, 4, 2, 6, 3, 3, 6, 7, 0, 4, 2, 2, 4, 6, 1, 3, 5, 3, 7, 5
                    };
                    w.AddNode("[stand-in] " + PathName(root, t), PathIndex(root, t), corners, cn, null,
                        new List<(int[] indices, Material mat)> { (box, StandIn) });
                    continue;
                }
                var pos = new Vector3[v.Length];
                var nor = new Vector3[v.Length];
                Matrix4x4[] bind = null;
                if (r is SkinnedMeshRenderer && weights.Length == v.Length)
                {
                    try
                    {
                        bind = mesh.bindposes;
                    }
                    catch (Exception)
                    {
                        bind = null; // not readable either: placed by its renderer's transform
                    }
                }
                if (r is SkinnedMeshRenderer skin && skin.bones != null && skin.bones.Length > 0 && bind != null && bind.Length > 0 && weights.Length == v.Length)
                {
                    // Skinned: each vertex by its bones at their idle poses.
                    Transform[] bones = skin.bones;
                    var mats = new Matrix4x4[bones.Length];
                    for (int b = 0; b < bones.Length; b++)
                    {
                        if (bones[b] == null || b >= bind.Length)
                        {
                            mats[b] = Matrix4x4.zero;
                            continue;
                        }
                        RigidPose p = gunInv.Multiply(sampler.Idle(bones[b]));
                        mats[b] = Matrix4x4.TRS(p.position, p.rotation, bones[b].lossyScale) * bind[b];
                    }
                    BoneWeight[] bw = weights;
                    for (int i = 0; i < v.Length; i++)
                    {
                        BoneWeight q = bw[i];
                        Vector3 p = Vector3.zero, nn = Vector3.zero;
                        Add(ref p, ref nn, mats, q.boneIndex0, q.weight0, v[i], n.Length > i ? n[i] : Vector3.up);
                        Add(ref p, ref nn, mats, q.boneIndex1, q.weight1, v[i], n.Length > i ? n[i] : Vector3.up);
                        Add(ref p, ref nn, mats, q.boneIndex2, q.weight2, v[i], n.Length > i ? n[i] : Vector3.up);
                        Add(ref p, ref nn, mats, q.boneIndex3, q.weight3, v[i], n.Length > i ? n[i] : Vector3.up);
                        pos[i] = p;
                        nor[i] = nn.sqrMagnitude > 1e-12f ? nn.normalized : Vector3.up;
                    }
                }
                else
                {
                    RigidPose p = gunInv.Multiply(sampler.Idle(t));
                    Vector3 s = t.lossyScale;
                    var inv = new Vector3(1f / NonZero(s.x), 1f / NonZero(s.y), 1f / NonZero(s.z));
                    for (int i = 0; i < v.Length; i++)
                    {
                        pos[i] = p.TransformPoint(Vector3.Scale(v[i], s));
                        // (A mesh read back from the graphics card without normals gives zeros: up, as for no normals.)
                        Vector3 nn = n.Length > i ? p.rotation * Vector3.Scale(n[i], inv) : Vector3.zero;
                        nor[i] = nn.sqrMagnitude > 1e-12f ? nn.normalized : Vector3.up;
                    }
                }
                var prims = new List<(int[] indices, Material mat)>();
                Material[] mats2 = r.sharedMaterials;
                for (int sm = 0; sm < subs.Count; sm++)
                {
                    if (subs[sm].Length >= 3)
                    {
                        prims.Add((subs[sm], mats2 != null && mats2.Length > 0 ? mats2[Mathf.Min(sm, mats2.Length - 1)] : null));
                    }
                }
                if (prims.Count == 0)
                {
                    continue;
                }
                Vector2[] uvOut = uv.Length == v.Length ? uv : null;
                if (r is SkinnedMeshRenderer sk && sk.bones != null && weights.Length == v.Length && roles.Count > 0)
                {
                    // A skinned gun's moving parts are bones of one mesh (the ARS family's 'Door' and 'Charger'): the triangles
                    // their bones carry become parts of their own, named by the bone, so the sandbox moves them with the part.
                    Transform[] bones = sk.bones;
                    var groups = new Dictionary<Transform, List<(int[] indices, Material mat)>>();
                    var rest = new List<(int[] indices, Material mat)>();
                    foreach (var (indices, mat) in prims)
                    {
                        var byRole = new Dictionary<Transform, List<int>>();
                        var others = new List<int>();
                        for (int k = 0; k + 2 < indices.Length; k += 3)
                        {
                            Transform role = RoleOf(weights[indices[k]], bones, roles);
                            if (role != null && RoleOf(weights[indices[k + 1]], bones, roles) == role && RoleOf(weights[indices[k + 2]], bones, roles) == role)
                            {
                                if (!byRole.TryGetValue(role, out List<int> list))
                                {
                                    byRole[role] = list = new List<int>();
                                }
                                list.Add(indices[k]);
                                list.Add(indices[k + 1]);
                                list.Add(indices[k + 2]);
                            }
                            else
                            {
                                others.Add(indices[k]);
                                others.Add(indices[k + 1]);
                                others.Add(indices[k + 2]);
                            }
                        }
                        if (others.Count > 0)
                        {
                            rest.Add((others.ToArray(), mat));
                        }
                        foreach (var kv in byRole)
                        {
                            if (!groups.TryGetValue(kv.Key, out var gl))
                            {
                                groups[kv.Key] = gl = new List<(int[] indices, Material mat)>();
                            }
                            gl.Add((kv.Value.ToArray(), mat));
                        }
                    }
                    if (rest.Count > 0)
                    {
                        w.AddNode(PathName(root, t), PathIndex(root, t), pos, nor, uvOut, rest);
                    }
                    foreach (var kv in groups)
                    {
                        w.AddNode(PathName(root, kv.Key) + " (skinned part of " + t.name + ")", PathIndex(root, kv.Key), pos, nor, uvOut, kv.Value);
                    }
                    continue;
                }
                w.AddNode(PathName(root, t), PathIndex(root, t), pos, nor, uvOut, prims);
            }
            note = $"{w.NodeCount} drawn part(s){(fromGpu > 0 ? $", {fromGpu} read back from the graphics card" : "")}{(unreadable > 0 ? $", {unreadable} unreadable (stand-in boxes)" : "")}{(hidden > 0 ? $", {hidden} hidden at rest or while held" : "")}{(away > 0 ? $", {away} parked far from the gun" : "")}";
            return w.NodeCount > 0 ? w.Build() : null;
        }

        private static float NonZero(float f) => Mathf.Abs(f) < 1e-6f ? 1e-6f : f;

        /// <summary>The moving part a vertex mostly belongs to (its heaviest bone at or under one), or null.</summary>
        private static Transform RoleOf(BoneWeight w, Transform[] bones, List<Transform> roles)
        {
            int b = w.boneIndex0;
            float best = w.weight0;
            if (w.weight1 > best) { b = w.boneIndex1; best = w.weight1; }
            if (w.weight2 > best) { b = w.boneIndex2; best = w.weight2; }
            if (w.weight3 > best) { b = w.boneIndex3; best = w.weight3; }
            if (best < 0.5f || b < 0 || b >= bones.Length || bones[b] == null)
            {
                return null;
            }
            Transform bone = bones[b];
            Transform found = null;
            foreach (Transform r in roles)
            {
                // The deepest part the bone is in (a handle under the slide is the handle).
                if ((bone == r || bone.IsChildOf(r)) && (found == null || r.IsChildOf(found)))
                {
                    found = r;
                }
            }
            return found != null ? bone : null;
        }

        private static Material _standIn;
        private static Material StandIn => _standIn ?? (_standIn = new Material(Shader.Find("Standard")) { name = "stand-in (unreadable mesh)", color = new Color(0.55f, 0.6f, 0.68f, 0.55f) });

        private static void Add(ref Vector3 p, ref Vector3 n, Matrix4x4[] mats, int bone, float weight, Vector3 v, Vector3 normal)
        {
            if (weight <= 0f || bone < 0 || bone >= mats.Length)
            {
                return;
            }
            p += mats[bone].MultiplyPoint3x4(v) * weight;
            n += mats[bone].MultiplyVector(normal) * weight;
        }

        private static string PathName(Transform root, Transform t)
        {
            var names = new List<string>();
            for (Transform p = t; p != null && p != root; p = p.parent)
            {
                names.Add(p.name);
            }
            names.Reverse();
            return names.Count == 0 ? root.name : string.Join("/", names.ToArray());
        }

        private static string PathIndex(Transform root, Transform t)
        {
            int[] path = WeaponRig.PathOf(root, t);
            return path == null ? "" : string.Join("/", path.Select(i => i.ToString(CultureInfo.InvariantCulture)).ToArray());
        }

        // ---- what the analysis found ------------------------------------------------------------------------------------

        /// <summary>Writes model.glb and rig.json for this weapon (the analysis is finished: every rule has run).</summary>
        public static void Write(WeaponRigData d, Transform root, Weapon weapon, byte[] glb, string note)
        {
            ExportedKeys.Add(d.Key);
            try
            {
                string name = SafeName(d.Key);
                string dir = Path.Combine(Dir, name);
                Directory.CreateDirectory(dir);
                File.WriteAllBytes(Path.Combine(dir, "model.glb"), glb);
                File.WriteAllText(Path.Combine(dir, "rig.json"), RigJson(d, root, weapon, note), new UTF8Encoding(false));
                lock (Written)
                {
                    if (!Written.Contains(name))
                    {
                        Written.Add(name);
                    }
                    if (WrittenKey.TryGetValue(name, out string other) && other != d.Key)
                    {
                        // Two keys with one folder name (characters made '_', or only the case differs): the later one wins.
                        // None among the 286 weapons of 2026-10-17; said, so it is not silent.
                        Log.Warn($"Sandbox export: {d.Key} and {other} share the folder {name}; {d.Key} is the one kept.");
                    }
                    WrittenKey[name] = d.Key;
                }
                Log.Info($"Sandbox export: {d.Key} -> {dir} ({note}).");
            }
            catch (Exception e)
            {
                Log.Warn($"Sandbox export of {d.Key} failed: {e.Message}");
            }
        }

        /// <summary>index.json: every weapon written in this run (with the ones from earlier runs still in the folder).</summary>
        public static void WriteIndex()
        {
            try
            {
                Directory.CreateDirectory(Dir);
                var all = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (string d in Directory.GetDirectories(Dir))
                {
                    if (File.Exists(Path.Combine(d, "rig.json")) && File.Exists(Path.Combine(d, "model.glb")))
                    {
                        all.Add(Path.GetFileName(d));
                    }
                }
                var sb = new StringBuilder("{\"written\":\"" + DateTime.Now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + "\",\"weapons\":[");
                bool first = true;
                foreach (string n in all)
                {
                    sb.Append(first ? "" : ",").Append(Str(n));
                    first = false;
                }
                sb.Append("]}");
                File.WriteAllText(Path.Combine(Dir, "index.json"), sb.ToString(), new UTF8Encoding(false));
                Log.Info($"Sandbox export: {all.Count} weapon(s) in {Dir}.");
            }
            catch (Exception e)
            {
                Log.Warn("Writing the sandbox index failed: " + e.Message);
            }
        }

        private static string RigJson(WeaponRigData d, Transform root, Weapon weapon, string note)
        {
            var j = new Json();
            j.Str("key", d.Key).Str("name", weapon != null ? weapon.name : d.Key).Str("action", d.Action.ToString())
                .Num("slot", d.Slot).Bool("isPistol", d.IsPistol).Bool("handgun", d.Handgun)
                .Bool("machineGun", d.IsMachineGun).Bool("enBloc", d.EnBloc).Bool("lockOnEmpty", d.LockOnEmpty).Bool("cyclesByHand", d.CyclesByHand)
                .Vec("aim", d.AimAxis).Vec("up", d.Up).Vec("grip", d.GripPoint).Vec("gripDown", d.GripDown)
                .Bool("hasSupport", d.HasSupportPoint).Vec("support", d.SupportPoint).Vec("muzzle", d.MuzzlePoint)
                .Vec("rackPoint", d.RackPoint).Vec("slideTravel", d.SlideTravel).Vec("handleTravel", d.HandleTravel)
                .Str("slideMethod", d.SlideMethod).Bool("addedHandle", d.AddedHandle).Bool("carveHandle", d.CarveHandle)
                .Path("slide", d.SlidePath).Path("handle", d.HandlePath).Path("bolt", d.BoltPath).Path("hinge", d.HingePath)
                .Path("magazine", d.MagazinePath).Paths("magazineParts", d.MagazinePartPaths).Paths("slideCompanions", d.SlideCompanionPaths)
                .Vec("magSeatPos", d.MagSeat.position).Vec("magRestPos", d.MagRestPos).Quat("magSeatRot", d.MagSeat.rotation).Vec("magEject", d.MagEjectAxis)
                .Vec("magBoundsCenter", d.MagBounds.center).Size("magBoundsSize", Abs(d.MagBounds.size)).Str("magMethod", d.MagMethod)
                .Str("hingeKind", d.Hinge.ToString()).Vec("hingePivot", d.HingePivot).Vec("hingeAxis", d.HingeAxis)
                .Num("hingeAngle", d.HingeOpenAngle).Vec("hingeGrab", d.HingeGrabPoint).Bool("seatOnHinge", d.SeatOnHinge)
                .Floats("gripCurls", d.GripCurls).Floats("supportCurls", d.SupportCurls)
                // By name: neither is in the model file (the arms are left out, a part hidden while held mostly hidden at rest), and the
                // export checks read the name rules against them (tools/export_checks/reload_helper_rule.py).
                .Strs("armRenderers", Names(root, d.ArmRendererPaths)).Strs("spareParts", Names(root, d.SparePartPaths))
                .Str("notes", d.Notes).Str("warnings", string.Join("; ", d.Warnings.ToArray())).Str("export", note);
            return j.Done();
        }

        private static Vector3 Abs(Vector3 v) => new Vector3(Mathf.Abs(v.x), Mathf.Abs(v.y), Mathf.Abs(v.z));

        /// <summary>The names of the parts at these paths under <paramref name="root"/> ("?" for one gone).</summary>
        private static IEnumerable<string> Names(Transform root, List<int[]> paths)
        {
            foreach (int[] p in paths)
            {
                Transform t = WeaponRig.Resolve(root, p);
                yield return t != null ? t.name : "?";
            }
        }

        private static string Str(string s)
        {
            var sb = new StringBuilder("\"");
            foreach (char c in s ?? "")
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20)
                        {
                            sb.Append("\\u").Append(((int)c).ToString("x4"));
                        }
                        else
                        {
                            sb.Append(c);
                        }
                        break;
                }
            }
            return sb.Append('"').ToString();
        }

        private static string F(float f) => float.IsNaN(f) || float.IsInfinity(f) ? "0" : f.ToString("0.######", CultureInfo.InvariantCulture);

        /// <summary>A small JSON object writer; vectors and rotations are turned right-handed (z negated).</summary>
        private sealed class Json
        {
            private readonly StringBuilder _sb = new StringBuilder("{");
            private bool _first = true;

            private Json Key(string k)
            {
                _sb.Append(_first ? "\n " : ",\n ").Append(SandboxExport.Str(k)).Append(':');
                _first = false;
                return this;
            }

            public Json Str(string k, string v)
            {
                Key(k)._sb.Append(SandboxExport.Str(v));
                return this;
            }

            public Json Num(string k, float v)
            {
                Key(k)._sb.Append(F(v));
                return this;
            }

            public Json Bool(string k, bool v)
            {
                Key(k)._sb.Append(v ? "true" : "false");
                return this;
            }

            /// <summary>A size (lengths along x, y, z): not turned right-handed like a point (Vec negated z, so every
            /// magBoundsSize had a negative z and the sandbox missed the magazine's length along z).</summary>
            public Json Size(string k, Vector3 v)
            {
                Key(k)._sb.Append('[').Append(F(v.x)).Append(',').Append(F(v.y)).Append(',').Append(F(v.z)).Append(']');
                return this;
            }

            public Json Vec(string k, Vector3 v)
            {
                Key(k)._sb.Append('[').Append(F(v.x)).Append(',').Append(F(v.y)).Append(',').Append(F(-v.z)).Append(']');
                return this;
            }

            public Json Quat(string k, Quaternion q)
            {
                Key(k)._sb.Append('[').Append(F(-q.x)).Append(',').Append(F(-q.y)).Append(',').Append(F(q.z)).Append(',').Append(F(q.w)).Append(']');
                return this;
            }

            public Json Floats(string k, float[] v)
            {
                Key(k)._sb.Append(v == null ? "null" : "[" + string.Join(",", v.Select(F).ToArray()) + "]");
                return this;
            }

            public Json Path(string k, int[] p)
            {
                Key(k)._sb.Append(p == null ? "null" : SandboxExport.Str(string.Join("/", p.Select(i => i.ToString(CultureInfo.InvariantCulture)).ToArray())));
                return this;
            }

            public Json Strs(string k, IEnumerable<string> vs)
            {
                Key(k)._sb.Append('[').Append(string.Join(",", vs.Select(SandboxExport.Str).ToArray())).Append(']');
                return this;
            }

            public Json Paths(string k, List<int[]> ps)
            {
                Key(k)._sb.Append('[').Append(string.Join(",", (ps ?? new List<int[]>()).Where(p => p != null)
                    .Select(p => SandboxExport.Str(string.Join("/", p.Select(i => i.ToString(CultureInfo.InvariantCulture)).ToArray()))).ToArray())).Append(']');
                return this;
            }

            public string Done() => _sb.Append("\n}\n").ToString();
        }

        // ---- a minimal glTF 2.0 binary writer ---------------------------------------------------------------------------

        private sealed class GlbWriter
        {
            private readonly MemoryStream _bin = new MemoryStream();
            private readonly StringBuilder _accessors = new StringBuilder();
            private readonly StringBuilder _views = new StringBuilder();
            private readonly StringBuilder _meshes = new StringBuilder();
            private readonly StringBuilder _nodes = new StringBuilder();
            private readonly List<string> _materials = new List<string>();
            private readonly List<string> _images = new List<string>();
            private readonly Dictionary<Material, int> _materialIndex = new Dictionary<Material, int>();
            private readonly Dictionary<Texture, int> _imageIndex = new Dictionary<Texture, int>();
            private int _accessorCount, _viewCount;
            public int NodeCount { get; private set; }

            private int View(byte[] data, int? target)
            {
                while (_bin.Length % 4 != 0)
                {
                    _bin.WriteByte(0);
                }
                long offset = _bin.Length;
                _bin.Write(data, 0, data.Length);
                _views.Append(_viewCount == 0 ? "" : ",").Append($"{{\"buffer\":0,\"byteOffset\":{offset},\"byteLength\":{data.Length}{(target.HasValue ? $",\"target\":{target.Value}" : "")}}}");
                return _viewCount++;
            }

            private int Accessor(int view, int componentType, int count, string type, string minMax)
            {
                _accessors.Append(_accessorCount == 0 ? "" : ",").Append($"{{\"bufferView\":{view},\"componentType\":{componentType},\"count\":{count},\"type\":\"{type}\"{minMax}}}");
                return _accessorCount++;
            }

            private static byte[] Floats(IEnumerable<float> fs)
            {
                var ms = new MemoryStream();
                var bw = new BinaryWriter(ms);
                foreach (float f in fs)
                {
                    bw.Write(f);
                }
                return ms.ToArray();
            }

            public void AddNode(string name, string pathIndex, Vector3[] pos, Vector3[] nor, Vector2[] uv, List<(int[] indices, Material mat)> prims)
            {
                // Right-handed: z negated, triangles turned the other way round.
                Vector3 min = Vector3.positiveInfinity, max = Vector3.negativeInfinity;
                var p = new List<float>(pos.Length * 3);
                foreach (Vector3 v in pos)
                {
                    var r = new Vector3(v.x, v.y, -v.z);
                    min = Vector3.Min(min, r);
                    max = Vector3.Max(max, r);
                    p.Add(r.x);
                    p.Add(r.y);
                    p.Add(r.z);
                }
                int posAcc = Accessor(View(Floats(p), 34962), 5126, pos.Length, "VEC3",
                    $",\"min\":[{F(min.x)},{F(min.y)},{F(min.z)}],\"max\":[{F(max.x)},{F(max.y)},{F(max.z)}]");
                int norAcc = Accessor(View(Floats(nor.SelectMany(n => new[] { n.x, n.y, -n.z })), 34962), 5126, nor.Length, "VEC3", "");
                int uvAcc = uv == null ? -1 : Accessor(View(Floats(uv.SelectMany(t => new[] { t.x, 1f - t.y })), 34962), 5126, uv.Length, "VEC2", "");
                var primJson = new List<string>();
                foreach (var (indices, mat) in prims)
                {
                    var ms = new MemoryStream();
                    var bw = new BinaryWriter(ms);
                    for (int i = 0; i + 2 < indices.Length; i += 3)
                    {
                        bw.Write((uint)indices[i]);
                        bw.Write((uint)indices[i + 2]);
                        bw.Write((uint)indices[i + 1]);
                    }
                    int idxAcc = Accessor(View(ms.ToArray(), 34963), 5125, indices.Length / 3 * 3, "SCALAR", "");
                    int m = MaterialOf(mat);
                    primJson.Add($"{{\"attributes\":{{\"POSITION\":{posAcc},\"NORMAL\":{norAcc}{(uvAcc >= 0 ? $",\"TEXCOORD_0\":{uvAcc}" : "")}}},\"indices\":{idxAcc},\"material\":{m}}}");
                }
                _meshes.Append(NodeCount == 0 ? "" : ",").Append($"{{\"name\":{SandboxExport.Str(name)},\"primitives\":[{string.Join(",", primJson.ToArray())}]}}");
                _nodes.Append(NodeCount == 0 ? "" : ",").Append($"{{\"name\":{SandboxExport.Str(name)},\"mesh\":{NodeCount},\"extras\":{{\"path\":{SandboxExport.Str(pathIndex)}}}}}");
                NodeCount++;
            }

            private int MaterialOf(Material mat)
            {
                if (mat == null)
                {
                    mat = DefaultMaterial;
                }
                if (_materialIndex.TryGetValue(mat, out int i))
                {
                    return i;
                }
                Color c = mat.HasProperty("_Color") ? mat.color : Color.white;
                int img = -1;
                Texture tex = mat.HasProperty("_MainTex") ? mat.mainTexture : null;
                if (tex != null)
                {
                    img = ImageOf(tex);
                }
                bool transparent = c.a < 0.99f || mat.renderQueue >= 3000;
                string json = $"{{\"name\":{SandboxExport.Str(mat.name)},\"pbrMetallicRoughness\":{{\"baseColorFactor\":[{F(c.r)},{F(c.g)},{F(c.b)},{F(c.a)}],\"metallicFactor\":0.1,\"roughnessFactor\":0.75"
                              + (img >= 0 ? $",\"baseColorTexture\":{{\"index\":{img}}}" : "") + "}"
                              + (transparent ? ",\"alphaMode\":\"BLEND\"" : "") + ",\"doubleSided\":" + (mat.name.StartsWith("stand-in", StringComparison.Ordinal) ? "true" : "false") + "}";
                _materials.Add(json);
                _materialIndex[mat] = _materials.Count - 1;
                return _materials.Count - 1;
            }

            private static Material _default;
            private static Material DefaultMaterial => _default ?? (_default = new Material(Shader.Find("Standard")) { name = "default", color = new Color(0.6f, 0.6f, 0.6f) });

            /// <summary>A texture as a PNG (read back through a render target: most game textures are not readable), at most 1024 wide.</summary>
            private int ImageOf(Texture tex)
            {
                if (_imageIndex.TryGetValue(tex, out int i))
                {
                    return i;
                }
                byte[] png = null;
                try
                {
                    int width = tex.width, height = tex.height;
                    float shrink = Mathf.Min(1f, 1024f / Mathf.Max(width, height));
                    width = Mathf.Max(1, Mathf.RoundToInt(width * shrink));
                    height = Mathf.Max(1, Mathf.RoundToInt(height * shrink));
                    RenderTexture rt = RenderTexture.GetTemporary(width, height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
                    RenderTexture prev = RenderTexture.active;
                    var read = new Texture2D(width, height, TextureFormat.RGBA32, false);
                    try
                    {
                        Graphics.Blit(tex, rt);
                        RenderTexture.active = rt;
                        read.ReadPixels(new Rect(0, 0, width, height), 0, 0, false);
                        read.Apply(false);
                        png = read.EncodeToPNG();
                    }
                    finally
                    {
                        RenderTexture.active = prev;
                        RenderTexture.ReleaseTemporary(rt);
                        UnityEngine.Object.Destroy(read);
                    }
                }
                catch (Exception)
                {
                    png = null;
                }
                if (png == null)
                {
                    _imageIndex[tex] = -1;
                    return -1;
                }
                int view = View(png, null);
                _images.Add($"{{\"bufferView\":{view},\"mimeType\":\"image/png\"}}");
                _imageIndex[tex] = _images.Count - 1;
                return _images.Count - 1;
            }

            public byte[] Build()
            {
                while (_bin.Length % 4 != 0)
                {
                    _bin.WriteByte(0);
                }
                var nodes = string.Join(",", Enumerable.Range(0, NodeCount).Select(n => n.ToString(CultureInfo.InvariantCulture)).ToArray());
                string textures = string.Join(",", Enumerable.Range(0, _images.Count).Select(n => $"{{\"source\":{n},\"sampler\":0}}").ToArray());
                var json = new StringBuilder();
                json.Append("{\"asset\":{\"version\":\"2.0\",\"generator\":\"RavenfieldVR sandbox export\"},\"scene\":0,");
                json.Append($"\"scenes\":[{{\"nodes\":[{nodes}]}}],\"nodes\":[{_nodes}],\"meshes\":[{_meshes}],");
                json.Append($"\"materials\":[{string.Join(",", _materials.ToArray())}],");
                if (_images.Count > 0)
                {
                    json.Append($"\"images\":[{string.Join(",", _images.ToArray())}],\"textures\":[{textures}],");
                    json.Append("\"samplers\":[{\"magFilter\":9729,\"minFilter\":9987,\"wrapS\":10497,\"wrapT\":10497}],");
                }
                json.Append($"\"accessors\":[{_accessors}],\"bufferViews\":[{_views}],\"buffers\":[{{\"byteLength\":{_bin.Length}}}]}}");
                byte[] jsonBytes = Encoding.UTF8.GetBytes(json.ToString());
                int jsonPad = (4 - jsonBytes.Length % 4) % 4;
                byte[] bin = _bin.ToArray();
                var outStream = new MemoryStream();
                var w = new BinaryWriter(outStream);
                w.Write(0x46546C67u); // glTF
                w.Write(2u);
                w.Write((uint)(12 + 8 + jsonBytes.Length + jsonPad + 8 + bin.Length));
                w.Write((uint)(jsonBytes.Length + jsonPad));
                w.Write(0x4E4F534Au); // JSON
                w.Write(jsonBytes);
                for (int i = 0; i < jsonPad; i++)
                {
                    w.Write((byte)0x20);
                }
                w.Write((uint)bin.Length);
                w.Write(0x004E4942u); // BIN
                w.Write(bin);
                return outStream.ToArray();
            }
        }
    }
}
