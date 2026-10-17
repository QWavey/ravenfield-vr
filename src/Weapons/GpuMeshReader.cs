using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using RavenfieldVR.Core;
using UnityEngine;
using UnityEngine.Rendering;

namespace RavenfieldVR.Weapons
{
    /// <summary>
    /// Reads a mesh the game does not let scripts read (its asset bundle dropped the CPU copy) back from the graphics card:
    /// the native Direct3D 11 vertex and index buffers are copied into a staging buffer and mapped, then decoded with the
    /// mesh's own vertex layout. Used by the sandbox export only (run with single-threaded rendering, so nothing else uses
    /// the device context meanwhile); anything unexpected returns false and the caller keeps its fallback.
    /// </summary>
    internal static class GpuMeshReader
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct BufferDesc
        {
            public uint ByteWidth;
            public int Usage;
            public uint BindFlags;
            public uint CpuAccessFlags;
            public uint MiscFlags;
            public uint StructureByteStride;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct Mapped
        {
            public IntPtr Data;
            public uint RowPitch;
            public uint DepthPitch;
        }

        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate uint ReleaseFn(IntPtr self);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void GetDeviceFn(IntPtr self, out IntPtr device);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void GetDescFn(IntPtr self, out BufferDesc desc);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int CreateBufferFn(IntPtr self, ref BufferDesc desc, IntPtr initial, out IntPtr buffer);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void GetContextFn(IntPtr self, out IntPtr context);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void CopyResourceFn(IntPtr self, IntPtr dst, IntPtr src);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int MapFn(IntPtr self, IntPtr resource, uint sub, int type, uint flags, out Mapped mapped);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void UnmapFn(IntPtr self, IntPtr resource, uint sub);

        // COM vtable slots (d3d11.h): IUnknown 0-2; ID3D11DeviceChild GetDevice 3; ID3D11Buffer GetDesc 10;
        // ID3D11Device CreateBuffer 3, GetImmediateContext 40; ID3D11DeviceContext Map 14, Unmap 15, CopyResource 47.
        private static T Fn<T>(IntPtr obj, int slot) where T : class
        {
            IntPtr vtable = Marshal.ReadIntPtr(obj);
            return Marshal.GetDelegateForFunctionPointer(Marshal.ReadIntPtr(vtable, slot * IntPtr.Size), typeof(T)) as T;
        }

        private static void Release(IntPtr obj)
        {
            if (obj != IntPtr.Zero)
            {
                Fn<ReleaseFn>(obj, 2)(obj);
            }
        }

        public static bool Supported => SystemInfo.graphicsDeviceType == GraphicsDeviceType.Direct3D11;

        /// <summary>The bytes of a native D3D11 buffer, or null.</summary>
        private static byte[] ReadBuffer(IntPtr buffer)
        {
            if (buffer == IntPtr.Zero)
            {
                return null;
            }
            Fn<GetDescFn>(buffer, 10)(buffer, out BufferDesc desc);
            if (desc.ByteWidth == 0 || desc.ByteWidth > 256u * 1024 * 1024)
            {
                return null;
            }
            Fn<GetDeviceFn>(buffer, 3)(buffer, out IntPtr device);
            IntPtr context = IntPtr.Zero, staging = IntPtr.Zero;
            try
            {
                if (device == IntPtr.Zero)
                {
                    return null;
                }
                Fn<GetContextFn>(device, 40)(device, out context);
                var sd = new BufferDesc { ByteWidth = desc.ByteWidth, Usage = 3 /* staging */, BindFlags = 0, CpuAccessFlags = 0x20000 /* read */ };
                if (Fn<CreateBufferFn>(device, 3)(device, ref sd, IntPtr.Zero, out staging) < 0 || staging == IntPtr.Zero)
                {
                    return null;
                }
                Fn<CopyResourceFn>(context, 47)(context, staging, buffer);
                if (Fn<MapFn>(context, 14)(context, staging, 0, 1 /* read */, 0, out Mapped mapped) < 0 || mapped.Data == IntPtr.Zero)
                {
                    return null;
                }
                var bytes = new byte[desc.ByteWidth];
                try
                {
                    Marshal.Copy(mapped.Data, bytes, 0, bytes.Length);
                }
                finally
                {
                    Fn<UnmapFn>(context, 15)(context, staging, 0);
                }
                return bytes;
            }
            finally
            {
                Release(staging);
                Release(context);
                Release(device);
            }
        }

        private static int Size(VertexAttributeFormat f)
        {
            switch (f)
            {
                case VertexAttributeFormat.Float32:
                case VertexAttributeFormat.UInt32:
                case VertexAttributeFormat.SInt32:
                    return 4;
                case VertexAttributeFormat.Float16:
                case VertexAttributeFormat.UNorm16:
                case VertexAttributeFormat.SNorm16:
                case VertexAttributeFormat.UInt16:
                case VertexAttributeFormat.SInt16:
                    return 2;
                default:
                    return 1;
            }
        }

        private static float Read(byte[] b, int o, VertexAttributeFormat f)
        {
            switch (f)
            {
                case VertexAttributeFormat.Float32: return BitConverter.ToSingle(b, o);
                case VertexAttributeFormat.Float16: return Mathf.HalfToFloat(BitConverter.ToUInt16(b, o));
                case VertexAttributeFormat.UNorm16: return BitConverter.ToUInt16(b, o) / 65535f;
                case VertexAttributeFormat.SNorm16: return Mathf.Max(-1f, BitConverter.ToInt16(b, o) / 32767f);
                case VertexAttributeFormat.UNorm8: return b[o] / 255f;
                case VertexAttributeFormat.SNorm8: return Mathf.Max(-1f, (sbyte)b[o] / 127f);
                case VertexAttributeFormat.UInt8: return b[o];
                case VertexAttributeFormat.SInt8: return (sbyte)b[o];
                case VertexAttributeFormat.UInt16: return BitConverter.ToUInt16(b, o);
                case VertexAttributeFormat.SInt16: return BitConverter.ToInt16(b, o);
                case VertexAttributeFormat.UInt32: return BitConverter.ToUInt32(b, o);
                case VertexAttributeFormat.SInt32: return BitConverter.ToInt32(b, o);
                default: return 0f;
            }
        }

        /// <summary>
        /// Positions, normals, first uvs, bone weights (null when not skinned) and each triangle submesh's indices (vertex
        /// numbers, base vertex added), read from the graphics card. False when it cannot be read this way.
        /// </summary>
        public static bool TryRead(Mesh mesh, out Vector3[] positions, out Vector3[] normals, out Vector2[] uvs, out BoneWeight[] weights, out List<int[]> submeshes)
        {
            positions = normals = null;
            uvs = null;
            weights = null;
            submeshes = null;
            if (!Supported || mesh == null || mesh.vertexCount == 0)
            {
                return false;
            }
            try
            {
                int n = mesh.vertexCount;
                VertexAttributeDescriptor[] attrs = mesh.GetVertexAttributes();
                var strides = new int[4];
                var offsets = new Dictionary<VertexAttribute, (int stream, int offset, VertexAttributeFormat format, int dim)>();
                foreach (VertexAttributeDescriptor a in attrs)
                {
                    offsets[a.attribute] = (a.stream, strides[a.stream], a.format, a.dimension);
                    strides[a.stream] += Size(a.format) * a.dimension;
                }
                var streams = new byte[4][];
                for (int s = 0; s < mesh.vertexBufferCount && s < 4; s++)
                {
                    if (strides[s] > 0)
                    {
                        streams[s] = ReadBuffer(mesh.GetNativeVertexBufferPtr(s));
                        if (streams[s] == null || streams[s].Length < strides[s] * n)
                        {
                            return false;
                        }
                    }
                }
                if (!offsets.TryGetValue(VertexAttribute.Position, out var pa))
                {
                    return false;
                }
                Vector3 V3(byte[] b, int at, VertexAttributeFormat f, int dim)
                {
                    int sz = Size(f);
                    return new Vector3(Read(b, at, f), dim > 1 ? Read(b, at + sz, f) : 0f, dim > 2 ? Read(b, at + 2 * sz, f) : 0f);
                }
                positions = new Vector3[n];
                for (int i = 0; i < n; i++)
                {
                    positions[i] = V3(streams[pa.stream], i * strides[pa.stream] + pa.offset, pa.format, pa.dim);
                }
                normals = new Vector3[n];
                if (offsets.TryGetValue(VertexAttribute.Normal, out var na))
                {
                    for (int i = 0; i < n; i++)
                    {
                        normals[i] = V3(streams[na.stream], i * strides[na.stream] + na.offset, na.format, na.dim).normalized;
                    }
                }
                if (offsets.TryGetValue(VertexAttribute.TexCoord0, out var ua))
                {
                    uvs = new Vector2[n];
                    int sz = Size(ua.format);
                    for (int i = 0; i < n; i++)
                    {
                        int at = i * strides[ua.stream] + ua.offset;
                        uvs[i] = new Vector2(Read(streams[ua.stream], at, ua.format), ua.dim > 1 ? Read(streams[ua.stream], at + sz, ua.format) : 0f);
                    }
                }
                if (offsets.TryGetValue(VertexAttribute.BlendIndices, out var bi) && offsets.TryGetValue(VertexAttribute.BlendWeight, out var bw))
                {
                    weights = new BoneWeight[n];
                    int si = Size(bi.format), sw = Size(bw.format);
                    for (int i = 0; i < n; i++)
                    {
                        int ai = i * strides[bi.stream] + bi.offset, aw = i * strides[bw.stream] + bw.offset;
                        float I(int k) => k < bi.dim ? Read(streams[bi.stream], ai + k * si, bi.format) : 0f;
                        float W(int k) => k < bw.dim ? Read(streams[bw.stream], aw + k * sw, bw.format) : 0f;
                        weights[i] = new BoneWeight
                        {
                            boneIndex0 = (int)I(0), boneIndex1 = (int)I(1), boneIndex2 = (int)I(2), boneIndex3 = (int)I(3),
                            weight0 = bw.dim == 1 ? 1f : W(0), weight1 = W(1), weight2 = W(2), weight3 = W(3)
                        };
                    }
                }
                byte[] ib = ReadBuffer(mesh.GetNativeIndexBufferPtr());
                if (ib == null)
                {
                    return false;
                }
                bool wide = mesh.indexFormat == IndexFormat.UInt32;
                submeshes = new List<int[]>();
                for (int s = 0; s < mesh.subMeshCount; s++)
                {
                    SubMeshDescriptor d = mesh.GetSubMesh(s);
                    if (d.topology != MeshTopology.Triangles)
                    {
                        submeshes.Add(new int[0]);
                        continue;
                    }
                    var tri = new int[d.indexCount];
                    for (int k = 0; k < d.indexCount; k++)
                    {
                        int at = (d.indexStart + k) * (wide ? 4 : 2);
                        if (at + (wide ? 4 : 2) > ib.Length)
                        {
                            return false;
                        }
                        int v = (wide ? (int)BitConverter.ToUInt32(ib, at) : BitConverter.ToUInt16(ib, at)) + d.baseVertex;
                        if (v < 0 || v >= n)
                        {
                            return false;
                        }
                        tri[k] = v;
                    }
                    submeshes.Add(tri);
                }
                return true;
            }
            catch (Exception e)
            {
                Log.WarnOnce("gpumesh:" + mesh.name, $"Reading '{mesh.name}' back from the graphics card failed: {e.Message}");
                return false;
            }
        }
    }
}
