using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace PMK.Compat.Audio.Xact
{
    public sealed class PlayWaveEvent
    {
        public int Track;
        public int WaveBank;
        /// <summary>0 = play once, 255 = loop forever, n = play n+1 times.</summary>
        public int LoopCount;
        public int TimestampMs;
        /// <summary>Alternative tracks when the event picks a track variation.</summary>
        public List<int> TrackVariations;
    }

    public sealed class SoundData
    {
        public int Offset;
        public int Category;
        public float Volume = 1f;
        public List<(float Volume, List<PlayWaveEvent> Events)> Clips = new List<(float, List<PlayWaveEvent>)>();
    }

    public enum VariationType { Ordered = 0, OrderedFromRandom = 1, Random = 2, RandomNoImmediateRepeats = 3, Shuffle = 4 }

    public sealed class VariationData
    {
        public VariationType Type;
        public List<(SoundData Sound, int MinWeight, int MaxWeight)> Entries = new List<(SoundData, int, int)>();
    }

    public sealed class CueData
    {
        public string Name;
        public SoundData Sound;
        public VariationData Variation;
    }

    /// <summary>
    /// Parser for XACT sound banks (.xsb, content version 43-46, either
    /// endianness). Covers the features XNA games use: simple and complex
    /// sounds, play-wave events (with loop counts) and cue variation tables.
    /// </summary>
    public sealed class SoundBankData
    {
        public string Name { get; private set; }
        public List<string> WaveBankNames { get; } = new List<string>();
        public List<CueData> Cues { get; } = new List<CueData>();
        public List<string> Warnings { get; } = new List<string>();

        private readonly Dictionary<int, SoundData> soundCache = new Dictionary<int, SoundData>();
        private XactReader r;

        public CueData FindCue(string name)
        {
            foreach (var c in Cues)
                if (string.Equals(c.Name, name, StringComparison.Ordinal))
                    return c;
            return null;
        }

        public static SoundBankData Load(string path) => Parse(File.ReadAllBytes(path));

        public static SoundBankData Parse(byte[] raw)
        {
            string magic = Encoding.ASCII.GetString(raw, 0, 4);
            bool be = magic switch
            {
                "KBDS" => true,
                "SDBK" => false,
                _ => throw new FormatException("Not an XACT sound bank"),
            };
            var bank = new SoundBankData { r = new XactReader(raw, be) };
            bank.ParseHeader();
            return bank;
        }

        private void ParseHeader()
        {
            r.Position = 4;
            int contentVersion = r.U16();
            if (contentVersion < 43 || contentVersion > 46)
                throw new NotSupportedException($"Sound bank content version {contentVersion} is not supported");
            r.U16(); // tool version
            r.U16(); // crc
            r.U32(); r.U32(); // last modified
            r.U8(); // platform
            int simpleCues = r.U16();
            int complexCues = r.U16();
            r.U16(); // unknown
            r.U16(); // total cues (aligned)
            int waveBankCount = r.U8();
            r.U16(); // sound count
            r.U16(); // unknown
            r.U16(); // cue name table length
            int simpleCueOffset = r.S32();
            int complexCueOffset = r.S32();
            int cueNameOffset = r.S32();
            r.S32(); // unknown
            r.S32(); // variation table offset (tables are reached through cues)
            r.S32(); // transition table offset
            int waveBankNameOffset = r.S32();
            r.S32(); // cue name hash table
            int cueNameIndexOffset = r.S32();
            r.S32(); // sound table offset
            Name = r.CString(r.Position, 64);

            for (int i = 0; i < waveBankCount; i++)
                WaveBankNames.Add(r.CString(waveBankNameOffset + i * 64, 64));

            int total = simpleCues + complexCues;
            var names = new string[total];
            if (cueNameIndexOffset > 0)
            {
                for (int i = 0; i < total; i++)
                {
                    r.Position = cueNameIndexOffset + i * 6;
                    int nameOff = r.S32();
                    names[i] = nameOff > 0 ? r.CString(nameOff) : $"cue{i}";
                }
            }
            else if (cueNameOffset > 0)
            {
                int p = cueNameOffset;
                for (int i = 0; i < total; i++)
                {
                    names[i] = r.CString(p);
                    p += names[i].Length + 1;
                }
            }

            for (int i = 0; i < simpleCues; i++)
            {
                r.Position = simpleCueOffset + i * 5;
                r.U8(); // flags
                int soundOffset = r.S32();
                Cues.Add(new CueData { Name = names[i], Sound = ReadSound(soundOffset) });
            }

            for (int i = 0; i < complexCues; i++)
            {
                r.Position = complexCueOffset + i * 15;
                int flags = r.U8();
                var cue = new CueData { Name = names[simpleCues + i] };
                if ((flags & 0x04) != 0)
                {
                    int soundOffset = r.S32();
                    r.U32();
                    cue.Sound = ReadSound(soundOffset);
                }
                else
                {
                    int variationOffset = r.S32();
                    r.U32(); // transition table
                    cue.Variation = ReadVariation(variationOffset);
                }
                Cues.Add(cue);
            }
        }

        private VariationData ReadVariation(int offset)
        {
            r.Position = offset;
            uint info = r.U32();
            int count = (int)(info & 0xFFFF);
            int flags = (int)(info >> 16);
            r.U16(); // unknown
            r.U16(); // interactive variable index
            var v = new VariationData { Type = (VariationType)(flags & 0x07) };
            int entryType = (flags >> 3) & 0x07;
            var entries = new List<(int soundOffset, int min, int max, int track, int bank)>();
            for (int i = 0; i < count; i++)
            {
                switch (entryType)
                {
                    case 0: // wave with weights
                        {
                            int track = r.U16(); int wb = r.U8(); int min = r.U8(); int max = r.U8();
                            entries.Add((-1, min, max, track, wb));
                            break;
                        }
                    case 1: // sound with weights
                        {
                            int so = r.S32(); int min = r.U8(); int max = r.U8();
                            entries.Add((so, min, max, 0, 0));
                            break;
                        }
                    case 3: // interactive sound
                        {
                            int so = r.S32(); r.U32(); r.U32();
                            entries.Add((so, 0, 255, 0, 0));
                            break;
                        }
                    case 4: // compact wave
                        {
                            int track = r.U16(); int wb = r.U8();
                            entries.Add((-1, 0, 255, track, wb));
                            break;
                        }
                    default:
                        throw new NotSupportedException($"Variation entry type {entryType} is not supported");
                }
            }
            foreach (var e in entries)
            {
                SoundData s = e.soundOffset >= 0
                    ? ReadSound(e.soundOffset)
                    : WaveOnlySound(e.track, e.bank);
                v.Entries.Add((s, e.min, e.max));
            }
            return v;
        }

        private static SoundData WaveOnlySound(int track, int bank)
        {
            var s = new SoundData { Offset = -1 };
            s.Clips.Add((1f, new List<PlayWaveEvent> { new PlayWaveEvent { Track = track, WaveBank = bank } }));
            return s;
        }

        private SoundData ReadSound(int offset)
        {
            if (soundCache.TryGetValue(offset, out var cached))
                return cached;
            int save = r.Position;
            r.Position = offset;
            var s = new SoundData { Offset = offset };
            int flags = r.U8();
            s.Category = r.U16();
            s.Volume = XactVolume.ByteToAmplitude(r.U8());
            r.S16(); // pitch
            r.U8(); // priority
            r.U16(); // entry length
            int clipCount = (flags & 0x01) != 0 ? r.U8() : 1;

            if ((flags & 0x0E) != 0)
            {
                int start = r.Position;
                int len = r.U16();
                r.Position = start + len;
            }
            if ((flags & 0x10) != 0)
            {
                int start = r.Position;
                int len = r.U16();
                r.Position = start + len;
            }

            if ((flags & 0x01) != 0)
            {
                var clipHeaders = new List<(float vol, int eventOffset)>();
                for (int i = 0; i < clipCount; i++)
                {
                    float vol = XactVolume.ByteToAmplitude(r.U8());
                    int eventOffset = r.S32();
                    r.U8(); r.U8(); r.U16(); // filter
                    clipHeaders.Add((vol, eventOffset));
                }
                foreach (var (vol, eventOffset) in clipHeaders)
                    s.Clips.Add((vol, ReadEvents(eventOffset, offset)));
            }
            else
            {
                int track = r.U16();
                int wb = r.U8();
                s.Clips.Add((1f, new List<PlayWaveEvent> { new PlayWaveEvent { Track = track, WaveBank = wb } }));
            }
            soundCache[offset] = s;
            r.Position = save;
            return s;
        }

        private List<PlayWaveEvent> ReadEvents(int offset, int soundOffset)
        {
            var list = new List<PlayWaveEvent>();
            r.Position = offset;
            int count = r.U8();
            for (int i = 0; i < count; i++)
            {
                uint info = r.U32();
                r.U16(); // random offset
                int type = (int)(info & 0x1F);
                int timestamp = (int)((info >> 5) & 0xFFFF);
                r.U8(); // separator (0xFF)
                switch (type)
                {
                    case 1: // play wave
                        {
                            r.U8(); // track variation flags
                            int track = r.U16();
                            int wb = r.U8();
                            int loop = r.U8();
                            r.U16(); r.U16(); // pan angle / arc
                            list.Add(new PlayWaveEvent { Track = track, WaveBank = wb, LoopCount = loop, TimestampMs = timestamp });
                            break;
                        }
                    case 3: // play wave from track variation
                        {
                            r.U8();
                            int loop = r.U8();
                            r.U16(); r.U16();
                            int tracks = r.U16();
                            int vflags = r.U16();
                            r.U32();
                            var ev = new PlayWaveEvent { LoopCount = loop, TimestampMs = timestamp, TrackVariations = new List<int>() };
                            for (int t = 0; t < tracks; t++)
                            {
                                int track = r.U16(); int wb = r.U8(); r.U8(); r.U8();
                                ev.TrackVariations.Add(track);
                                ev.WaveBank = wb;
                            }
                            ev.Track = ev.TrackVariations.Count > 0 ? ev.TrackVariations[0] : 0;
                            list.Add(ev);
                            break;
                        }
                    default:
                        // Other events (pitch/volume/marker...) have variable layouts; the
                        // remaining events of this clip are skipped rather than misparsed.
                        Warnings.Add($"Sound 0x{soundOffset:X}: event type {type} not supported, skipped");
                        return list;
                }
            }
            return list;
        }
    }
}
