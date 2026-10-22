using System;
using System.IO;
using System.Reflection;
using RavenfieldVR.Core;
using UnityEngine;

namespace RavenfieldVR.Rig
{
    /// <summary>
    /// What a glove model is like, for the hand model to pose and close it: where each finger's joints are, how each
    /// joint turns as the finger curls (from open at 0 to a fist at 1), how far the skin reaches from the bones (the pads
    /// that touch what the hand holds), the palm's surface, and the mesh. Right hand, palm space (origin = palm centre,
    /// +Z toward the fingers, +Y toward the thumb, palm facing -X, model units before <see cref="Scale"/>); the left hand
    /// is mirrored by the hand model.
    /// <para>Two gloves: Valve's SteamVR glove (src/Rig/Glove, converted by tools/hands/convert_glove.py from
    /// assets/hands/steamvr-glove; BSD-3-Clause) with its finger motion from Valve's open-hand and fist poses, and the
    /// mod's earlier procedural glove (<see cref="GloveMesh"/>), kept as the fallback.</para>
    /// </summary>
    internal sealed class HandShape
    {
        public string Name;
        public float Scale;
        public readonly Vector3[] Knuckle = new Vector3[5];
        /// <summary>Joint rotations the mesh is bound in (the first joint's in palm space, the others in their parent's).</summary>
        public readonly Quaternion[,] Bind = new Quaternion[5, 3];
        /// <summary>Joint rotations at curl 0.</summary>
        public readonly Quaternion[,] Open = new Quaternion[5, 3];
        /// <summary>Offset to the next joint (or the fingertip), in the joint's own frame.</summary>
        public readonly Vector3[,] Child = new Vector3[5, 3];
        /// <summary>Curling turns a joint about this axis (its own frame), by up to <see cref="CurlAngle"/> degrees.</summary>
        public readonly Vector3[,] CurlAxis = new Vector3[5, 3];
        public readonly float[,] CurlAngle = new float[5, 3];
        /// <summary>The skin around the bone, on average.</summary>
        public readonly float[,] Radius = new float[5, 3];
        /// <summary>How far the skin reaches from the bone toward where the segment closes, and that direction (joint frame).</summary>
        public readonly float[,] Pad = new float[5, 3];
        public readonly Vector3[,] PadDir = new Vector3[5, 3];
        /// <summary>The thumb's first joint at rest and while holding a weapon's grip.</summary>
        public Quaternion ThumbRest, ThumbWeapon;
        /// <summary>The thumb's first joint in a fist (poses turn the thumb base between the rest and this).</summary>
        public Quaternion ThumbFist;
        /// <summary>Valve's own pointing hand, every joint (null for a glove without one).</summary>
        public Quaternion[,] Point;
        /// <summary>Points on the palm's skin (palm side).</summary>
        public Vector3[] PalmSkin;
        /// <summary>The palm and back of the hand as a box (the thumb and fingers excluded).</summary>
        public Vector3 PalmMin, PalmMax;
        /// <summary>Builds the skinned mesh in the hand root's space for joints posed at <see cref="Bind"/>; -1 mirrors it for a left hand.</summary>
        public Func<Transform, Transform[][], float, Mesh> BuildMesh;
        public Material Material;
        /// <summary>Bone weights per vertex the mesh uses (the renderer's skin quality).</summary>
        public SkinQuality Quality = SkinQuality.Bone2;

        private static HandShape _glove, _procedural;
        private static bool _gloveTried;

        /// <summary>The glove chosen in the settings; the procedural one when the model cannot be loaded.</summary>
        public static HandShape Current
        {
            get
            {
                if (VRConfig.Glove == null || VRConfig.Glove.Value == GloveStyle.Tactical)
                {
                    if (!_gloveTried)
                    {
                        _gloveTried = true;
                        try
                        {
                            _glove = LoadGlove();
                        }
                        catch (Exception e)
                        {
                            Log.Exception("Loading the glove model failed; using the simple glove", e);
                        }
                    }
                    if (_glove != null)
                    {
                        return _glove;
                    }
                }
                return _procedural ?? (_procedural = Procedural());
            }
        }

        // ---- the mod's earlier glove ---------------------------------------------------------------------------------

        private static HandShape Procedural()
        {
            Vector3[] knuckles =
            {
                new Vector3(-0.016f, 0.028f, -0.022f),
                new Vector3(-0.004f, 0.030f, 0.046f),
                new Vector3(-0.002f, 0.010f, 0.050f),
                new Vector3(-0.002f, -0.010f, 0.047f),
                new Vector3(-0.004f, -0.029f, 0.041f)
            };
            float[][] lengths =
            {
                new[] { 0.036f, 0.031f, 0.026f },
                new[] { 0.042f, 0.025f, 0.021f },
                new[] { 0.046f, 0.029f, 0.022f },
                new[] { 0.043f, 0.027f, 0.021f },
                new[] { 0.034f, 0.020f, 0.019f }
            };
            float[] radii = { 0.0092f, 0.0078f, 0.0081f, 0.0078f, 0.0069f };
            float[] maxCurl = { 55f, 85f, 95f, 70f };
            var h = new HandShape { Name = "simple", Scale = 0.9f };
            for (int f = 0; f < 5; f++)
            {
                h.Knuckle[f] = knuckles[f];
                for (int s = 0; s < 3; s++)
                {
                    Quaternion rest = s > 0 ? Quaternion.identity
                        : f == 0 ? Quaternion.Euler(-28f, -32f, 0f) // thumb points forward and up along the top of the grip
                        : Quaternion.Euler(0f, (f - 2.5f) * 5f, 0f); // fingers fan out a little, so the hand reads as a hand
                    h.Bind[f, s] = rest;
                    h.Open[f, s] = rest;
                    h.Child[f, s] = new Vector3(0f, 0f, lengths[f][s]);
                    h.CurlAxis[f, s] = Vector3.down; // closing toward the palm (-X)
                    h.CurlAngle[f, s] = f == 0 ? maxCurl[0] * (s == 0 ? 0.6f : 1f) : maxCurl[s + 1];
                    h.Radius[f, s] = radii[f] * (1f - 0.12f * s);
                    h.Pad[f, s] = h.Radius[f, s];
                    h.PadDir[f, s] = Vector3.left;
                }
            }
            h.ThumbRest = h.Open[0, 0];
            h.ThumbWeapon = Quaternion.Euler(-6f, -16f, 0f); // along the frame of a held gun, beside the slide
            h.ThumbFist = Quaternion.SlerpUnclamped(h.ThumbRest, h.ThumbWeapon, 1f / 0.35f);
            var skin = new Vector3[12];
            int n = 0;
            foreach (float z in new[] { -0.03f, -0.005f, 0.02f, 0.036f })
            {
                foreach (float y in new[] { -0.026f, 0f, 0.026f })
                {
                    skin[n++] = new Vector3(-0.012f, y, z);
                }
            }
            h.PalmSkin = skin;
            h.PalmMin = new Vector3(-0.012f, -0.038f, -0.06f);
            h.PalmMax = new Vector3(0.015f, 0.038f, 0.05f);
            h.BuildMesh = (root, joints, mirror) => GloveMesh.Build(root, joints, lengths, radii, mirror);
            h.Material = SimpleMaterial();
            return h;
        }

        private static Material SimpleMaterial()
        {
            Shader shader = Shader.Find("Standard") ?? Shader.Find("Legacy Shaders/Bumped Diffuse") ?? Shader.Find("Legacy Shaders/Diffuse");
            var m = new Material(shader) { name = "RavenfieldVR Glove", color = new Color(0.16f, 0.15f, 0.13f) };
            // Matte fabric: barely any gloss, a fine weave in the normals.
            if (m.HasProperty("_Glossiness"))
            {
                m.SetFloat("_Glossiness", 0.12f);
            }
            if (m.HasProperty("_Metallic"))
            {
                m.SetFloat("_Metallic", 0f);
            }
            if (m.HasProperty("_BumpMap"))
            {
                m.SetTexture("_BumpMap", GloveMesh.Weave());
                m.SetTextureScale("_BumpMap", new Vector2(6f, 6f));
                m.SetFloat("_BumpScale", 0.6f);
                m.EnableKeyword("_NORMALMAP");
            }
            return m;
        }

        // ---- Valve's SteamVR glove ------------------------------------------------------------------------------------

        /// <summary>The glove a little under its modelled size: Ravenfield's guns are built for the weapons' own smaller hands.</summary>
        private const float GloveScale = 0.88f;

        private static byte[] Resource(string name)
        {
            Assembly asm = typeof(HandShape).Assembly;
            using (Stream s = asm.GetManifestResourceStream("RavenfieldVR.Rig.Glove." + name))
            {
                if (s == null)
                {
                    return null;
                }
                var buffer = new byte[s.Length];
                int read = 0;
                while (read < buffer.Length)
                {
                    int r = s.Read(buffer, read, buffer.Length - read);
                    if (r <= 0)
                    {
                        break;
                    }
                    read += r;
                }
                return buffer;
            }
        }

        private static HandShape LoadGlove()
        {
            byte[] data = Resource("glove.bin");
            if (data == null)
            {
                Log.Warn("The glove model is not in this build; using the simple glove.");
                return null;
            }
            var r = new BinaryReader(new MemoryStream(data));
            if (new string(r.ReadChars(4)) != "RFVG")
            {
                throw new InvalidDataException("not a glove file");
            }
            int version = r.ReadInt32();
            if (version != 2)
            {
                throw new InvalidDataException("glove file version " + version);
            }
            int vertexCount = r.ReadInt32();
            int indexCount = r.ReadInt32();
            var positions = new Vector3[vertexCount];
            var normals = new Vector3[vertexCount];
            var uvs = new Vector2[vertexCount];
            var weights = new BoneWeight[vertexCount];
            for (int i = 0; i < vertexCount; i++)
            {
                positions[i] = ReadVector(r);
            }
            for (int i = 0; i < vertexCount; i++)
            {
                normals[i] = ReadVector(r);
            }
            for (int i = 0; i < vertexCount; i++)
            {
                uvs[i] = new Vector2(r.ReadSingle(), r.ReadSingle());
            }
            var bones = new byte[vertexCount * 4];
            r.Read(bones, 0, bones.Length);
            for (int i = 0; i < vertexCount; i++)
            {
                weights[i] = new BoneWeight
                {
                    boneIndex0 = bones[i * 4], boneIndex1 = bones[i * 4 + 1], boneIndex2 = bones[i * 4 + 2], boneIndex3 = bones[i * 4 + 3],
                    weight0 = r.ReadSingle(), weight1 = r.ReadSingle(), weight2 = r.ReadSingle(), weight3 = r.ReadSingle()
                };
            }
            var triangles = new int[indexCount];
            for (int i = 0; i < indexCount; i++)
            {
                triangles[i] = r.ReadUInt16();
            }

            float size = VRConfig.GloveSize != null ? Mathf.Clamp(VRConfig.GloveSize.Value, 0.7f, 1.1f) : GloveScale;
            var h = new HandShape { Name = "tactical (SteamVR glove)", Scale = size, Quality = SkinQuality.Bone4 };
            var relaxed = new Quaternion[5, 3];
            var fist = new Quaternion[5, 3];
            var point = new Quaternion[5, 3];
            for (int f = 0; f < 5; f++)
            {
                for (int s = 0; s < 3; s++)
                {
                    Vector3 pos = ReadVector(r);
                    if (s == 0)
                    {
                        h.Knuckle[f] = pos;
                    }
                    h.Bind[f, s] = ReadQuaternion(r);
                    h.Child[f, s] = ReadVector(r);
                    h.CurlAxis[f, s] = ReadVector(r).normalized;
                    h.CurlAngle[f, s] = r.ReadSingle();
                    h.Radius[f, s] = r.ReadSingle();
                    h.Pad[f, s] = r.ReadSingle();
                    h.PadDir[f, s] = ReadVector(r).normalized;
                    h.Open[f, s] = ReadQuaternion(r);
                    fist[f, s] = ReadQuaternion(r);
                    relaxed[f, s] = ReadQuaternion(r);
                    point[f, s] = ReadQuaternion(r);
                }
            }
            h.PalmSkin = new Vector3[12];
            for (int i = 0; i < 12; i++)
            {
                h.PalmSkin[i] = ReadVector(r);
            }
            h.PalmMin = new Vector3(r.ReadSingle(), 0f, 0f);
            h.PalmMax = new Vector3(r.ReadSingle(), 0f, 0f);
            h.PalmMin.y = r.ReadSingle();
            h.PalmMax.y = r.ReadSingle();
            h.PalmMin.z = r.ReadSingle();
            h.PalmMax.z = r.ReadSingle();
            // At rest the thumb lies relaxed beside the hand (Valve's open hand spreads it wide); on a grip it comes in a
            // third of the way to the fist, along the frame, and closes from there until it touches.
            h.ThumbRest = relaxed[0, 0];
            h.ThumbWeapon = Quaternion.Slerp(relaxed[0, 0], fist[0, 0], 0.35f);
            h.ThumbFist = fist[0, 0];
            h.Point = point;
            // Curl 0 is Valve's relaxed hand with its curl taken out, not the open hand: the open hand spreads the fingers
            // wide (index, ring and little finger 10-15 degrees out at the knuckle), which read as a stiff hand at rest.
            // Each joint keeps the relaxed pose's sideways set (the swing) and curls about the same axis as before, so a
            // fist and a grip close as they did. The thumb's base has its own rest and grip poses (above).
            for (int f = 0; f < 5; f++)
            {
                for (int s = f == 0 ? 1 : 0; s < 3; s++)
                {
                    h.Open[f, s] = WithoutCurl(h.Open[f, s], relaxed[f, s], h.CurlAxis[f, s]);
                }
            }

            h.BuildMesh = (root, joints, mirror) =>
            {
                var v = new Vector3[vertexCount];
                var n = new Vector3[vertexCount];
                for (int i = 0; i < vertexCount; i++)
                {
                    v[i] = new Vector3(positions[i].x * mirror, positions[i].y, positions[i].z);
                    n[i] = new Vector3(normals[i].x * mirror, normals[i].y, normals[i].z);
                }
                var t = (int[])triangles.Clone();
                if (mirror < 0f)
                {
                    for (int i = 0; i < t.Length; i += 3)
                    {
                        int swap = t[i + 1];
                        t[i + 1] = t[i + 2];
                        t[i + 2] = swap;
                    }
                }
                var mesh = new Mesh { name = mirror < 0f ? "RavenfieldVR Glove L" : "RavenfieldVR Glove R" };
                mesh.vertices = v;
                mesh.normals = n;
                mesh.uv = uvs;
                mesh.triangles = t;
                mesh.boneWeights = weights;
                mesh.RecalculateTangents();
                mesh.RecalculateBounds();
                return mesh;
            };
            h.Material = GloveMaterial();
            Log.Info($"Glove model: {vertexCount} vertices, {indexCount / 3} triangles.");
            return h;
        }

        private static Material GloveMaterial()
        {
            Shader shader = Shader.Find("Standard") ?? Shader.Find("Legacy Shaders/Bumped Diffuse") ?? Shader.Find("Legacy Shaders/Diffuse");
            var m = new Material(shader) { name = "RavenfieldVR Tactical Glove", color = Color.white };
            byte[] color = Resource("glove_color.jpg");
            if (color != null)
            {
                var tex = new Texture2D(2, 2, TextureFormat.RGB24, true) { name = "RavenfieldVR Glove Color", anisoLevel = 4 };
                tex.LoadImage(color, false);
                tex.wrapMode = TextureWrapMode.Clamp;
                m.mainTexture = tex;
            }
            byte[] normal = Resource("glove_normal.jpg");
            if (normal != null && m.HasProperty("_BumpMap"))
            {
                // Normals are data, not colour: a linear texture (alpha 1, so the shader reads x from red).
                var tex = new Texture2D(2, 2, TextureFormat.RGBA32, true, true) { name = "RavenfieldVR Glove Normal", anisoLevel = 4 };
                tex.LoadImage(normal, false);
                tex.wrapMode = TextureWrapMode.Clamp;
                m.SetTexture("_BumpMap", tex);
                m.SetFloat("_BumpScale", 1f);
                m.EnableKeyword("_NORMALMAP");
            }
            if (m.HasProperty("_Glossiness"))
            {
                m.SetFloat("_Glossiness", 0.32f);
            }
            if (m.HasProperty("_Metallic"))
            {
                m.SetFloat("_Metallic", 0f);
            }
            return m;
        }

        /// <summary>
        /// <paramref name="pose"/> with its curl about <paramref name="axis"/> (joint frame) relative to <paramref name="open"/>
        /// taken out: the swing-twist split of open⁻¹·pose, keeping the swing.
        /// </summary>
        private static Quaternion WithoutCurl(Quaternion open, Quaternion pose, Vector3 axis)
        {
            Quaternion q = Runtime.RigidPose.Conjugate(open) * pose;
            float along = q.x * axis.x + q.y * axis.y + q.z * axis.z;
            var twist = new Quaternion(axis.x * along, axis.y * along, axis.z * along, q.w);
            float len = Mathf.Sqrt(twist.x * twist.x + twist.y * twist.y + twist.z * twist.z + twist.w * twist.w);
            if (len < 1e-6f)
            {
                return open; // a half turn about another axis: keep the open pose
            }
            twist = new Quaternion(twist.x / len, twist.y / len, twist.z / len, twist.w / len);
            return pose * Runtime.RigidPose.Conjugate(twist);
        }

        private static Vector3 ReadVector(BinaryReader r) => new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());

        private static Quaternion ReadQuaternion(BinaryReader r)
        {
            var q = new Quaternion(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
            float len = Mathf.Sqrt(q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w);
            return len > 1e-6f ? new Quaternion(q.x / len, q.y / len, q.z / len, q.w / len) : Quaternion.identity;
        }

        // ---- mirroring for the left hand -------------------------------------------------------------------------------

        /// <summary>A point or palm-space offset of the right hand, for a hand with this mirror (-1 left).</summary>
        public static Vector3 MirrorPoint(Vector3 v, float mirror) => new Vector3(v.x * mirror, v.y, v.z);

        /// <summary>A rotation of the right hand reflected across the palm's middle plane.</summary>
        public static Quaternion MirrorRotation(Quaternion q, float mirror) => mirror < 0f ? new Quaternion(q.x, -q.y, -q.z, q.w) : q;

        /// <summary>A rotation axis of the right hand, reflected (the angle stays).</summary>
        public static Vector3 MirrorAxis(Vector3 a, float mirror) => mirror < 0f ? new Vector3(a.x, -a.y, -a.z) : a;
    }
}
