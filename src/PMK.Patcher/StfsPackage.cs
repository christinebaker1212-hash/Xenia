using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace PMK.Patcher
{
    /// <summary>
    /// Reader for Xbox 360 STFS packages (CON / LIVE / PIRS), the container
    /// Xbox Live Indie Games ship in. C# port of tools/stfs_extract.py.
    /// </summary>
    public sealed class StfsPackage
    {
        private const int Block = 0x1000;
        private const int DataPerL0 = 0xAA;
        private const int DataPerL1 = 0xAA * 0xAA;

        private readonly byte[] data;
        private readonly int shift;
        private readonly int baseOffset;
        private readonly int fileTableBlockCount;
        private readonly int fileTableBlock;

        public string Magic { get; }
        public uint TitleId { get; }
        public uint ContentType { get; }
        public string DisplayName { get; }
        public string TitleName { get; }
        public List<StfsEntry> Entries { get; } = new List<StfsEntry>();

        public StfsPackage(byte[] data)
        {
            this.data = data;
            if (data.Length < 0x1000)
                throw new InvalidDataException("File is too small to be an STFS package.");
            Magic = Encoding.ASCII.GetString(data, 0, 4);
            if (Magic != "CON " && Magic != "LIVE" && Magic != "PIRS")
                throw new InvalidDataException("Not an Xbox 360 STFS package.");
            int headerSize = (int)BE32(0x340);
            ContentType = BE32(0x344);
            TitleId = BE32(0x360);
            if (BE32(0x3A9) != 0)
                throw new InvalidDataException("Package uses SVOD, not STFS.");
            const int vd = 0x379;
            int blockSep = data[vd + 2];
            fileTableBlockCount = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(vd + 3));
            fileTableBlock = U24LE(vd + 5);
            shift = (blockSep & 1) != 0 ? 0 : 1;
            baseOffset = (headerSize + 0xFFF) & 0xF000;
            DisplayName = Utf16BE(0x411, 0x80);
            TitleName = Utf16BE(0x1691, 0x80);
            ReadFileTable();
        }

        public static StfsPackage Open(string path) => new StfsPackage(File.ReadAllBytes(path));

        private uint BE32(int o) => BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(o));
        private int U24LE(int o) => data[o] | (data[o + 1] << 8) | (data[o + 2] << 16);
        private int U24BE(int o) => (data[o] << 16) | (data[o + 1] << 8) | data[o + 2];

        private string Utf16BE(int o, int len)
        {
            string s = Encoding.BigEndianUnicode.GetString(data, o, len);
            int z = s.IndexOf('\0');
            return z >= 0 ? s.Substring(0, z) : s;
        }

        private int BackingBlock(int n)
        {
            int r = (((n + DataPerL0) / DataPerL0) << shift) + n;
            if (n < DataPerL0) return r;
            r += ((n + DataPerL1) / DataPerL1) << shift;
            if (n < DataPerL1) return r;
            return r + (1 << shift);
        }

        private int BlockOffset(int n) => baseOffset + BackingBlock(n) * Block;

        private int Level0HashBlock(int n)
        {
            int step0 = shift == 0 ? 0xAB : 0xAC;
            if (n < DataPerL0) return 0;
            int num = (n / DataPerL0) * step0;
            num += ((n / DataPerL1) + 1) << shift;
            if (n < DataPerL1) return num;
            return num + (1 << shift);
        }

        private int NextBlock(int n)
        {
            int off = baseOffset + Level0HashBlock(n) * Block;
            return U24BE(off + (n % DataPerL0) * 0x18 + 0x15);
        }

        private ReadOnlySpan<byte> ReadBlock(int n)
        {
            int o = BlockOffset(n);
            if (o < 0 || o + Block > data.Length)
            {
                int avail = Math.Max(0, data.Length - o);
                return o >= 0 && o < data.Length ? data.AsSpan(o, avail) : ReadOnlySpan<byte>.Empty;
            }
            return data.AsSpan(o, Block);
        }

        private void ReadFileTable()
        {
            int blk = fileTableBlock;
            for (int i = 0; i < fileTableBlockCount; i++)
            {
                var raw = ReadBlock(blk);
                for (int j = 0; j + 0x40 <= raw.Length; j += 0x40)
                {
                    var e = raw.Slice(j, 0x40);
                    int flags = e[0x28];
                    int nameLen = flags & 0x3F;
                    if (nameLen == 0) continue;
                    Entries.Add(new StfsEntry
                    {
                        Index = i * 0x40 + j / 0x40,
                        Name = Encoding.ASCII.GetString(e.Slice(0, nameLen)),
                        IsDirectory = (flags & 0x80) != 0,
                        Contiguous = (flags & 0x40) != 0,
                        Blocks = e[0x29] | (e[0x2A] << 8) | (e[0x2B] << 16),
                        StartBlock = e[0x2F] | (e[0x30] << 8) | (e[0x31] << 16),
                        Parent = BinaryPrimitives.ReadInt16BigEndian(e.Slice(0x32)),
                        Size = (int)BinaryPrimitives.ReadUInt32BigEndian(e.Slice(0x34)),
                    });
                }
                if (i + 1 < fileTableBlockCount)
                    blk = NextBlock(blk);
            }
            var byIndex = new Dictionary<int, StfsEntry>();
            foreach (var e in Entries) byIndex[e.Index] = e;
            foreach (var e in Entries)
            {
                var parts = new List<string> { e.Name };
                var seen = new HashSet<int>();
                int p = e.Parent;
                while (p != -1 && byIndex.TryGetValue(p, out var parent) && seen.Add(p))
                {
                    parts.Insert(0, parent.Name);
                    p = parent.Parent;
                }
                e.Path = string.Join("/", parts);
            }
        }

        public byte[] ReadFile(StfsEntry e)
        {
            var buf = new byte[e.Size];
            int written = 0;
            int blk = e.StartBlock;
            for (int i = 0; i < e.Blocks && written < e.Size; i++)
            {
                var chunk = ReadBlock(blk);
                int n = Math.Min(chunk.Length, e.Size - written);
                chunk.Slice(0, n).CopyTo(buf.AsSpan(written));
                written += n;
                if (written >= e.Size) break;
                blk = e.Contiguous ? blk + 1 : NextBlock(blk);
            }
            if (written != e.Size)
                throw new InvalidDataException($"{e.Path}: read {written} of {e.Size} bytes");
            return buf;
        }

        /// <summary>Extracts every file under <paramref name="outDir"/>, refusing unsafe paths.</summary>
        public void ExtractAll(string outDir, Action<string> log = null)
        {
            string root = Path.GetFullPath(outDir);
            foreach (var e in Entries)
            {
                string dest = Path.GetFullPath(Path.Combine(root, e.Path.Replace('/', Path.DirectorySeparatorChar)));
                if (!dest.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                {
                    log?.Invoke("Skipping unsafe path " + e.Path);
                    continue;
                }
                if (e.IsDirectory)
                {
                    Directory.CreateDirectory(dest);
                    continue;
                }
                Directory.CreateDirectory(Path.GetDirectoryName(dest));
                File.WriteAllBytes(dest, ReadFile(e));
            }
        }
    }

    public sealed class StfsEntry
    {
        public int Index;
        public string Name;
        public string Path;
        public bool IsDirectory;
        public bool Contiguous;
        public int Blocks;
        public int StartBlock;
        public int Parent;
        public int Size;
    }
}
