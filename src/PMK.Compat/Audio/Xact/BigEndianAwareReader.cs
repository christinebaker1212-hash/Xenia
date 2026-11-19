using System;
using System.Buffers.Binary;

namespace PMK.Compat.Audio.Xact
{
    /// <summary>Random-access reader for XACT files, which are big-endian on Xbox 360.</summary>
    public sealed class XactReader
    {
        private readonly byte[] data;
        public bool BigEndian { get; }
        public int Position { get; set; }
        public int Length => data.Length;

        public XactReader(byte[] data, bool bigEndian)
        {
            this.data = data;
            BigEndian = bigEndian;
        }

        private ReadOnlySpan<byte> Take(int n)
        {
            if (Position < 0 || Position + n > data.Length)
                throw new FormatException($"XACT data truncated at 0x{Position:X}");
            var s = new ReadOnlySpan<byte>(data, Position, n);
            Position += n;
            return s;
        }

        public byte U8() => Take(1)[0];
        public ushort U16() => BigEndian ? BinaryPrimitives.ReadUInt16BigEndian(Take(2)) : BinaryPrimitives.ReadUInt16LittleEndian(Take(2));
        public short S16() => (short)U16();
        public uint U32() => BigEndian ? BinaryPrimitives.ReadUInt32BigEndian(Take(4)) : BinaryPrimitives.ReadUInt32LittleEndian(Take(4));
        public int S32() => (int)U32();
        public byte[] Bytes(int n) => Take(n).ToArray();

        public string CString(int offset, int max = 256)
        {
            int end = offset;
            while (end < data.Length && end - offset < max && data[end] != 0) end++;
            return System.Text.Encoding.ASCII.GetString(data, offset, end - offset);
        }

        public byte[] Raw => data;
    }

    public static class XactVolume
    {
        /// <summary>
        /// XACT stores volumes as a byte on a non-linear dB scale
        /// (0xB4 = 0 dB, 0xFF = +6 dB, 0x00 = -96 dB). Curve fitted to
        /// the XACT authoring tool's table.
        /// </summary>
        public static float ByteToDecibels(byte value)
        {
            const double a = -96.0, b = 0.432254984608615, c = 80.1748600297963, d = 67.7385212334047;
            return (float)(((a - d) / (1 + Math.Pow(value / c, b))) + d);
        }

        public static float ByteToAmplitude(byte value) => (float)Math.Pow(10, ByteToDecibels(value) / 20.0);
    }
}
