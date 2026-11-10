using System;
using System.Text;
using UnityEngine;

namespace RavenfieldVR.Net
{
    /// <summary>A message that does not read as its kind says (cut short, a length past its end): dropped, never trusted.</summary>
    internal sealed class NetFormatException : Exception
    {
        public NetFormatException(string message) : base(message)
        {
        }
    }

    /// <summary>A float and its four bytes in one place (the wire's floats without unsafe code).</summary>
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Explicit)]
    internal struct FloatBits
    {
        [System.Runtime.InteropServices.FieldOffset(0)] public float Value;
        [System.Runtime.InteropServices.FieldOffset(0)] public uint Bits;
    }

    /// <summary>
    /// A message being written: bytes in a buffer that grows, small numbers in few bytes (varints), positions as floats, turns as
    /// the three smallest parts of the quaternion in 10 bits each. Plain C# (it is checked without the game: tools/tests/NetTests.cs).
    /// </summary>
    internal sealed class NetWriter
    {
        private byte[] _buf;

        public NetWriter(int capacity = 256)
        {
            _buf = new byte[Math.Max(16, capacity)];
        }

        public int Length { get; private set; }

        /// <summary>The buffer itself (valid up to <see cref="Length"/>): sent as it is, no copy.</summary>
        public byte[] Buffer => _buf;

        public void Reset() => Length = 0;

        public byte[] ToArray()
        {
            var copy = new byte[Length];
            System.Buffer.BlockCopy(_buf, 0, copy, 0, Length);
            return copy;
        }

        private void Room(int more)
        {
            if (Length + more > _buf.Length)
            {
                Array.Resize(ref _buf, Math.Max(_buf.Length * 2, Length + more));
            }
        }

        public void Byte(byte v)
        {
            Room(1);
            _buf[Length++] = v;
        }

        public void Bool(bool v) => Byte(v ? (byte)1 : (byte)0);

        public void UShort(ushort v)
        {
            Room(2);
            _buf[Length++] = (byte)v;
            _buf[Length++] = (byte)(v >> 8);
        }

        public void UInt32(uint v)
        {
            Room(4);
            _buf[Length++] = (byte)v;
            _buf[Length++] = (byte)(v >> 8);
            _buf[Length++] = (byte)(v >> 16);
            _buf[Length++] = (byte)(v >> 24);
        }

        public void ULong(ulong v)
        {
            UInt32((uint)v);
            UInt32((uint)(v >> 32));
        }

        /// <summary>A count or an id: 7 bits a byte, the low ones first (under 128 in one byte).</summary>
        public void VarUInt(uint v)
        {
            while (v >= 0x80)
            {
                Byte((byte)(v | 0x80));
                v >>= 7;
            }
            Byte((byte)v);
        }

        /// <summary>A signed number near zero in few bytes (zigzag: -1 is 1, 1 is 2).</summary>
        public void VarInt(int v) => VarUInt((uint)((v << 1) ^ (v >> 31)));

        public void Float(float v) => UInt32(new FloatBits { Value = v }.Bits);

        /// <summary>A value known to lie in a range, in 16 bits (an angle, a health, a throttle).</summary>
        public void Ranged(float v, float min, float max)
        {
            float t = max > min ? (v - min) / (max - min) : 0f;
            UShort((ushort)Math.Round((t < 0f ? 0f : t > 1f ? 1f : t) * 65535f));
        }

        public void Vector(Vector3 v)
        {
            Float(v.x);
            Float(v.y);
            Float(v.z);
        }

        /// <summary>A small offset (a hand from the head, a speed) in millimetres, three varints: a few bytes for what is near.</summary>
        public void SmallVector(Vector3 v)
        {
            VarInt((int)Math.Round(v.x * 1000f));
            VarInt((int)Math.Round(v.y * 1000f));
            VarInt((int)Math.Round(v.z * 1000f));
        }

        /// <summary>
        /// A rotation in 4 bytes: which part is largest (2 bits) and the other three in 10 bits each (each within 0.707 of zero):
        /// under a fifth of a degree off, a quarter of four floats.
        /// </summary>
        public void Rotation(Quaternion q)
        {
            float x = q.x, y = q.y, z = q.z, w = q.w;
            float n = (float)Math.Sqrt(x * x + y * y + z * z + w * w);
            if (n < 1e-6f)
            {
                x = y = z = 0f;
                w = 1f;
                n = 1f;
            }
            x /= n;
            y /= n;
            z /= n;
            w /= n;
            float ax = Math.Abs(x), ay = Math.Abs(y), az = Math.Abs(z), aw = Math.Abs(w);
            int largest = ax >= ay && ax >= az && ax >= aw ? 0 : ay >= az && ay >= aw ? 1 : az >= aw ? 2 : 3;
            float sign = (largest == 0 ? x : largest == 1 ? y : largest == 2 ? z : w) < 0f ? -1f : 1f;
            float a = largest == 0 ? y : x, b = largest <= 1 ? z : y, c = largest == 3 ? z : w;
            uint packed = (uint)largest << 30 | Pack10(a * sign) << 20 | Pack10(b * sign) << 10 | Pack10(c * sign);
            UInt32(packed);
        }

        private const float Root2Over2 = 0.70710678f;

        private static uint Pack10(float v)
        {
            float t = (v + Root2Over2) / (2f * Root2Over2);
            return (uint)Math.Round((t < 0f ? 0f : t > 1f ? 1f : t) * 1023f);
        }

        public void String(string s)
        {
            if (string.IsNullOrEmpty(s))
            {
                VarUInt(0);
                return;
            }
            byte[] utf8 = Encoding.UTF8.GetBytes(s);
            VarUInt((uint)utf8.Length);
            Bytes(utf8, 0, utf8.Length);
        }

        public void Bytes(byte[] data, int offset, int count)
        {
            Room(count);
            System.Buffer.BlockCopy(data, offset, _buf, Length, count);
            Length += count;
        }
    }

    /// <summary>A message being read: the writer's kinds back in the same order; anything past the end is a <see cref="NetFormatException"/>.</summary>
    internal struct NetReader
    {
        private readonly byte[] _buf;
        private readonly int _end;
        private int _at;

        public NetReader(byte[] buffer, int offset, int length)
        {
            _buf = buffer;
            _at = offset;
            _end = offset + length;
        }

        public int Remaining => _end - _at;

        private void Need(int count)
        {
            if (count < 0 || _at + count > _end)
            {
                throw new NetFormatException($"the message ends {_at + count - _end} byte(s) short");
            }
        }

        public byte Byte()
        {
            Need(1);
            return _buf[_at++];
        }

        public bool Bool() => Byte() != 0;

        public ushort UShort()
        {
            Need(2);
            ushort v = (ushort)(_buf[_at] | _buf[_at + 1] << 8);
            _at += 2;
            return v;
        }

        public uint UInt32()
        {
            Need(4);
            uint v = (uint)(_buf[_at] | _buf[_at + 1] << 8 | _buf[_at + 2] << 16 | _buf[_at + 3] << 24);
            _at += 4;
            return v;
        }

        public ulong ULong()
        {
            ulong low = UInt32();
            return low | (ulong)UInt32() << 32;
        }

        public uint VarUInt()
        {
            uint v = 0;
            for (int shift = 0; shift < 35; shift += 7)
            {
                byte b = Byte();
                v |= (uint)(b & 0x7F) << shift;
                if ((b & 0x80) == 0)
                {
                    return v;
                }
            }
            throw new NetFormatException("a number of more than five bytes");
        }

        public int VarInt()
        {
            uint v = VarUInt();
            return (int)(v >> 1) ^ -(int)(v & 1);
        }

        public float Float() => new FloatBits { Bits = UInt32() }.Value;

        public float Ranged(float min, float max) => min + UShort() / 65535f * (max - min);

        public Vector3 Vector() => new Vector3(Float(), Float(), Float());

        public Vector3 SmallVector() => new Vector3(VarInt() / 1000f, VarInt() / 1000f, VarInt() / 1000f);

        public Quaternion Rotation()
        {
            uint packed = UInt32();
            int largest = (int)(packed >> 30);
            float a = Unpack10(packed >> 20), b = Unpack10(packed >> 10), c = Unpack10(packed);
            float rest = 1f - a * a - b * b - c * c;
            float d = rest > 0f ? (float)Math.Sqrt(rest) : 0f;
            switch (largest)
            {
                case 0: return new Quaternion(d, a, b, c);
                case 1: return new Quaternion(a, d, b, c);
                case 2: return new Quaternion(a, b, d, c);
                default: return new Quaternion(a, b, c, d);
            }
        }

        private static float Unpack10(uint v) => (v & 1023u) / 1023f * (2f * 0.70710678f) - 0.70710678f;

        public string String(int maxBytes = 4096)
        {
            int count = (int)VarUInt();
            if (count == 0)
            {
                return "";
            }
            if (count > maxBytes)
            {
                throw new NetFormatException($"a text of {count} bytes (at most {maxBytes})");
            }
            Need(count);
            string s = Encoding.UTF8.GetString(_buf, _at, count);
            _at += count;
            return s;
        }

        /// <summary>The rest of the message as its own array (a relayed message, a payload).</summary>
        public byte[] Rest()
        {
            var rest = new byte[_end - _at];
            System.Buffer.BlockCopy(_buf, _at, rest, 0, rest.Length);
            _at = _end;
            return rest;
        }
    }
}
