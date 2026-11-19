using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace PMK.Compat.Audio.Xact
{
    public enum WaveCodec { Pcm = 0, Xma = 1, Adpcm = 2, Wma = 3 }

    public sealed class WaveEntry
    {
        public int Index;
        public WaveCodec Codec;
        public int Channels;
        public int SampleRate;
        public int BlockAlign;
        public int BitsFlag;
        /// <summary>Length in samples (per channel).</summary>
        public int Duration;
        public int PlayOffset;
        public int PlayLength;
        /// <summary>Loop region in samples; LoopLength 0 means no loop region.</summary>
        public int LoopStart;
        public int LoopLength;
    }

    /// <summary>Parser for XACT wave banks (.xwb), version 42-46, either endianness.</summary>
    public sealed class WaveBankData
    {
        public string Name { get; private set; }
        public bool BigEndian { get; private set; }
        public int Version { get; private set; }
        public bool Streaming { get; private set; }
        public int DataOffset { get; private set; }
        public int SeekTableOffset { get; private set; }
        public int SeekTableLength { get; private set; }
        public List<WaveEntry> Entries { get; } = new List<WaveEntry>();
        public byte[] Raw { get; private set; }

        public static WaveBankData Load(string path) => Parse(File.ReadAllBytes(path));

        public static WaveBankData Parse(byte[] raw)
        {
            string magic = Encoding.ASCII.GetString(raw, 0, 4);
            bool be = magic switch
            {
                "DNBW" => true,
                "WBND" => false,
                _ => throw new FormatException("Not an XACT wave bank"),
            };
            var r = new XactReader(raw, be);
            var bank = new WaveBankData { BigEndian = be, Raw = raw };
            r.Position = 4;
            bank.Version = (int)r.U32();
            if (bank.Version < 42 || bank.Version > 46)
                throw new NotSupportedException($"Wave bank version {bank.Version} is not supported");
            r.U32(); // header version
            var segOffset = new int[5];
            var segLength = new int[5];
            for (int i = 0; i < 5; i++)
            {
                segOffset[i] = (int)r.U32();
                segLength[i] = (int)r.U32();
            }

            r.Position = segOffset[0];
            uint flags = r.U32();
            int count = (int)r.U32();
            bank.Name = r.CString(r.Position, 64);
            r.Position += 64;
            int stride = (int)r.U32();
            r.U32(); // metadata element size
            r.U32(); // alignment
            bank.Streaming = (flags & 1) != 0;
            bool compact = (flags & 0x20000) != 0;
            if (compact)
                throw new NotSupportedException("Compact wave banks are not supported");

            bank.DataOffset = segOffset[4];
            bank.SeekTableOffset = segOffset[2];
            bank.SeekTableLength = segLength[2];

            for (int i = 0; i < count; i++)
            {
                r.Position = segOffset[1] + i * stride;
                uint flagsDuration = r.U32();
                uint format = r.U32();
                var e = new WaveEntry
                {
                    Index = i,
                    Duration = (int)(flagsDuration >> 4),
                    Codec = (WaveCodec)(format & 3),
                    Channels = (int)((format >> 2) & 7),
                    SampleRate = (int)((format >> 5) & 0x3FFFF),
                    BlockAlign = (int)((format >> 23) & 0xFF),
                    BitsFlag = (int)(format >> 31),
                    PlayOffset = (int)r.U32(),
                    PlayLength = (int)r.U32(),
                    LoopStart = (int)r.U32(),
                    LoopLength = (int)r.U32(),
                };
                // XMA banks store a slightly inaccurate rate (e.g. 44101): snap to standard rates.
                if (e.Codec == WaveCodec.Xma)
                    e.SampleRate = SnapRate(e.SampleRate);
                bank.Entries.Add(e);
            }
            return bank;
        }

        public static int SnapRate(int rate)
        {
            int[] std = { 8000, 11025, 16000, 22050, 24000, 32000, 44100, 48000 };
            foreach (int s in std)
                if (Math.Abs(rate - s) <= s / 100) return s;
            return rate;
        }

        public ReadOnlySpan<byte> GetData(WaveEntry e) => new ReadOnlySpan<byte>(Raw, DataOffset + e.PlayOffset, e.PlayLength);
    }
}
