using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Xna.Framework.Audio;
using PMK.Compat.Audio.Xact;

namespace PMK.Compat.Audio
{
    // XNA 3.1 XACT API implemented over FNA SoundEffect.
    //
    // The Xbox banks hold XMA audio, which FNA cannot play, so the installer
    // decodes every wave to "<bank>.xwb.pcm/NNN.wav". Sound/cue structure,
    // loop counts and loop points still come from the original .xsb/.xwb.
    //
    // Playback state that the game can observe (Cue.IsPlaying) advances with
    // AudioEngine.Update(), which the game calls once per frame. That keeps
    // game logic identical on every machine during online play, whatever the
    // real audio device is doing.

    public enum AudioStopOptions { AsAuthored = 0, Immediate = 1 }

    public struct AudioCategory
    {
        private readonly AudioEngine engine;
        private readonly string name;

        internal AudioCategory(AudioEngine engine, string name)
        {
            this.engine = engine;
            this.name = name;
        }

        public string Name => name;
        public void SetVolume(float volume) => engine?.SetCategoryVolume(name, volume);
        public void Stop(AudioStopOptions options) => engine?.StopAll();
        public void Pause() => engine?.PauseAll(true);
        public void Resume() => engine?.PauseAll(false);
    }

    public class AudioEngine : IDisposable
    {
        internal const float FrameSeconds = 1f / 60f;

        private readonly Dictionary<string, float> categoryVolumes = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
        internal readonly List<Cue> Active = new List<Cue>();
        internal readonly List<WaveBank> WaveBanks = new List<WaveBank>();
        internal static bool AudioUnavailable;

        public AudioEngine(string settingsFile)
        {
            SettingsFile = settingsFile;
        }

        public string SettingsFile { get; }

        public AudioCategory GetCategory(string name) => new AudioCategory(this, name);

        internal void SetCategoryVolume(string name, float volume)
        {
            categoryVolumes[name] = Math.Max(0f, volume);
            foreach (var c in Active) c.ApplyVolume();
        }

        /// <summary>The game assigns every sound to "Default"; other names are tracked but unused.</summary>
        internal float CategoryVolume => categoryVolumes.TryGetValue("Default", out float v) ? v : 1f;

        internal bool IsMusic
        {
            get
            {
                foreach (var b in WaveBanks)
                    if (b.Data.Streaming) return true;
                return false;
            }
        }

        public void Update()
        {
            for (int i = Active.Count - 1; i >= 0; i--)
            {
                var cue = Active[i];
                cue.Tick();
                if (!cue.IsPlaying)
                    Active.RemoveAt(i);
            }
        }

        internal void StopAll()
        {
            foreach (var c in Active.ToArray()) c.Stop(AudioStopOptions.Immediate);
        }

        internal void PauseAll(bool pause)
        {
            foreach (var c in Active)
            {
                if (pause) c.Pause(); else c.Resume();
            }
        }

        internal WaveBank FindWaveBank(string name, int index)
        {
            foreach (var b in WaveBanks)
                if (string.Equals(b.Data.Name, name, StringComparison.OrdinalIgnoreCase))
                    return b;
            return index < WaveBanks.Count ? WaveBanks[index] : null;
        }

        public void Dispose()
        {
            StopAll();
            foreach (var b in WaveBanks) b.Dispose();
            WaveBanks.Clear();
        }
    }

    public class WaveBank : IDisposable
    {
        private readonly string pcmDir;
        private readonly Dictionary<int, SoundEffect> cache = new Dictionary<int, SoundEffect>();
        private readonly LinkedList<int> lru = new LinkedList<int>();
        internal readonly WaveBankData Data;

        public WaveBank(AudioEngine audioEngine, string nonStreamingWaveBankFilename)
            : this(audioEngine, nonStreamingWaveBankFilename, 0, 0) { }

        public WaveBank(AudioEngine audioEngine, string streamingWaveBankFilename, int offset, short packetsize)
        {
            string path = CompatHost.ResolveGamePath(streamingWaveBankFilename);
            Data = WaveBankData.Load(path);
            pcmDir = path + ".pcm";
            audioEngine.WaveBanks.Add(this);
        }

        public bool IsPrepared => true;
        public bool IsInUse => false;

        internal WaveEntry Entry(int track) => track >= 0 && track < Data.Entries.Count ? Data.Entries[track] : null;

        /// <summary>Duration of a track in samples after conversion (falls back to the bank's value).</summary>
        internal int DurationSamples(int track) => Entry(track)?.Duration ?? 0;

        internal SoundEffect GetSound(int track)
        {
            if (AudioEngine.AudioUnavailable)
                return null;
            if (cache.TryGetValue(track, out var fx))
            {
                lru.Remove(track);
                lru.AddFirst(track);
                return fx;
            }
            var entry = Entry(track);
            string file = Path.Combine(pcmDir, $"{track:D3}.wav");
            if (entry == null || !File.Exists(file))
                return null;
            try
            {
                fx = WavLoader.Load(file, entry);
            }
            catch (NoAudioHardwareException)
            {
                AudioEngine.AudioUnavailable = true;
                return null;
            }
            cache[track] = fx;
            lru.AddFirst(track);
            // Music tracks are large: keep only a few decoded at a time.
            int limit = Data.Streaming ? 3 : int.MaxValue;
            while (lru.Count > limit)
            {
                int old = lru.Last.Value;
                lru.RemoveLast();
                // Leave it to the GC if an instance may still be playing it.
                cache.Remove(old);
            }
            return fx;
        }

        public void Dispose()
        {
            foreach (var fx in cache.Values) fx.Dispose();
            cache.Clear();
            lru.Clear();
        }
    }

    public class SoundBank : IDisposable
    {
        private readonly AudioEngine engine;
        internal readonly SoundBankData Data;
        private readonly Dictionary<VariationData, VariationState> variationStates = new Dictionary<VariationData, VariationState>();
        // Seeded from the shared session seed so online peers pick the same tracks.
        private readonly Random random = DetRandom.Create();

        public SoundBank(AudioEngine audioEngine, string filename)
        {
            engine = audioEngine;
            Data = SoundBankData.Load(CompatHost.ResolveGamePath(filename));
        }

        public bool IsInUse => false;

        public Cue GetCue(string name)
        {
            var data = Data.FindCue(name) ?? throw new ArgumentException("Cue not found: " + name);
            return new Cue(engine, this, data);
        }

        public void PlayCue(string name) => GetCue(name).Play();

        internal SoundData Choose(CueData cue)
        {
            if (cue.Variation == null || cue.Variation.Entries.Count == 0)
                return cue.Sound;
            var v = cue.Variation;
            if (!variationStates.TryGetValue(v, out var st))
                variationStates[v] = st = new VariationState { Last = -1 };
            int n = v.Entries.Count;
            int pick;
            switch (v.Type)
            {
                case VariationType.Ordered:
                    pick = (st.Last + 1) % n;
                    break;
                case VariationType.OrderedFromRandom:
                    pick = st.Last < 0 ? random.Next(n) : (st.Last + 1) % n;
                    break;
                case VariationType.Shuffle:
                    if (st.Order == null || st.Cursor >= n)
                    {
                        st.Order = new int[n];
                        for (int i = 0; i < n; i++) st.Order[i] = i;
                        for (int i = n - 1; i > 0; i--)
                        {
                            int j = random.Next(i + 1);
                            (st.Order[i], st.Order[j]) = (st.Order[j], st.Order[i]);
                        }
                        st.Cursor = 0;
                    }
                    pick = st.Order[st.Cursor++];
                    break;
                case VariationType.RandomNoImmediateRepeats:
                    pick = random.Next(n);
                    if (n > 1 && pick == st.Last) pick = (pick + 1 + random.Next(n - 1)) % n;
                    break;
                default:
                    pick = WeightedPick(v);
                    break;
            }
            st.Last = pick;
            return v.Entries[pick].Sound;
        }

        private int WeightedPick(VariationData v)
        {
            int total = 0;
            foreach (var e in v.Entries) total += Math.Max(1, e.MaxWeight - e.MinWeight);
            int roll = random.Next(total);
            for (int i = 0; i < v.Entries.Count; i++)
            {
                roll -= Math.Max(1, v.Entries[i].MaxWeight - v.Entries[i].MinWeight);
                if (roll < 0) return i;
            }
            return v.Entries.Count - 1;
        }

        public void Dispose() { }

        private sealed class VariationState
        {
            public int Last;
            public int[] Order;
            public int Cursor;
        }
    }

    public class Cue : IDisposable
    {
        private enum State { Prepared, Playing, Paused, Stopped }

        private readonly AudioEngine engine;
        private readonly SoundBank bank;
        private readonly CueData data;
        private State state = State.Prepared;
        private readonly List<Voice> voices = new List<Voice>();
        private float soundVolume = 1f;

        internal Cue(AudioEngine engine, SoundBank bank, CueData data)
        {
            this.engine = engine;
            this.bank = bank;
            this.data = data;
        }

        public string Name => data.Name;
        public bool IsPrepared => state == State.Prepared;
        public bool IsPlaying => state == State.Playing || state == State.Paused;
        public bool IsPaused => state == State.Paused;
        public bool IsStopped => state == State.Stopped;
        public bool IsDisposed { get; private set; }

        public void Play()
        {
            if (state != State.Prepared)
                return;
            var sound = bank.Choose(data);
            if (sound == null)
            {
                state = State.Stopped;
                return;
            }
            soundVolume = sound.Volume;
            foreach (var (clipVolume, events) in sound.Clips)
            {
                foreach (var ev in events)
                {
                    var wb = engine.FindWaveBank(ev.WaveBank < bank.Data.WaveBankNames.Count ? bank.Data.WaveBankNames[ev.WaveBank] : null, ev.WaveBank);
                    if (wb == null) continue;
                    var voice = new Voice
                    {
                        Bank = wb,
                        Track = ev.Track,
                        ClipVolume = clipVolume,
                        Infinite = ev.LoopCount == 255,
                        PlaysLeft = ev.LoopCount == 255 ? int.MaxValue : ev.LoopCount + 1,
                        DelayFrames = (int)Math.Round(ev.TimestampMs / 1000f / AudioEngine.FrameSeconds),
                    };
                    var entry = wb.Entry(ev.Track);
                    int rate = entry != null && entry.SampleRate > 0 ? entry.SampleRate : 44100;
                    voice.LengthFrames = Math.Max(1, (int)Math.Ceiling(wb.DurationSamples(ev.Track) / (double)rate / AudioEngine.FrameSeconds));
                    voices.Add(voice);
                }
            }
            state = State.Playing;
            engine.Active.Add(this);
            foreach (var v in voices)
                if (v.DelayFrames == 0) StartVoice(v);
        }

        private void StartVoice(Voice v)
        {
            v.Started = true;
            v.FramesIntoPlay = 0;
            v.PlaysLeft--;
            var fx = v.Bank.GetSound(v.Track);
            if (fx == null) return;
            try
            {
                v.Instance?.Dispose();
                v.Instance = fx.CreateInstance();
                v.Instance.IsLooped = v.Infinite;
                v.Instance.Volume = Volume(v);
                v.Instance.Play();
            }
            catch (Exception)
            {
                v.Instance = null;
            }
        }

        private float Volume(Voice v)
        {
            float master = engine.IsMusic ? CompatHost.MusicVolume : CompatHost.SoundVolume;
            return Math.Clamp(soundVolume * v.ClipVolume * engine.CategoryVolume * master, 0f, 1f);
        }

        internal void ApplyVolume()
        {
            foreach (var v in voices)
                if (v.Instance != null && !v.Instance.IsDisposed)
                    v.Instance.Volume = Volume(v);
        }

        /// <summary>Advances the cue by one game frame.</summary>
        internal void Tick()
        {
            if (state != State.Playing)
                return;
            bool anyAlive = false;
            foreach (var v in voices)
            {
                if (!v.Started)
                {
                    if (--v.DelayFrames <= 0) StartVoice(v);
                    anyAlive = true;
                    continue;
                }
                if (v.Done) continue;
                if (v.Infinite)
                {
                    anyAlive = true;
                    continue;
                }
                v.FramesIntoPlay++;
                if (v.FramesIntoPlay >= v.LengthFrames)
                {
                    if (v.PlaysLeft > 0)
                    {
                        StartVoice(v);
                        anyAlive = true;
                    }
                    else
                    {
                        v.Done = true;
                        v.Instance?.Stop();
                    }
                }
                else
                {
                    anyAlive = true;
                }
            }
            ApplyVolume();
            if (!anyAlive)
                Stop(AudioStopOptions.Immediate);
        }

        public void Pause()
        {
            if (state != State.Playing) return;
            state = State.Paused;
            foreach (var v in voices) v.Instance?.Pause();
        }

        public void Resume()
        {
            if (state != State.Paused) return;
            state = State.Playing;
            foreach (var v in voices) v.Instance?.Resume();
        }

        public void Stop(AudioStopOptions options)
        {
            if (state == State.Stopped) return;
            state = State.Stopped;
            foreach (var v in voices)
            {
                try { v.Instance?.Stop(); v.Instance?.Dispose(); } catch (Exception) { }
                v.Instance = null;
            }
            voices.Clear();
        }

        public void Dispose()
        {
            Stop(AudioStopOptions.Immediate);
            IsDisposed = true;
        }

        private sealed class Voice
        {
            public WaveBank Bank;
            public int Track;
            public float ClipVolume;
            public bool Infinite;
            public int PlaysLeft;
            public int DelayFrames;
            public int LengthFrames;
            public int FramesIntoPlay;
            public bool Started;
            public bool Done;
            public SoundEffectInstance Instance;
        }
    }

    /// <summary>Loads a converted 16-bit PCM WAV and applies the bank's loop region.</summary>
    internal static class WavLoader
    {
        public static SoundEffect Load(string path, WaveEntry entry)
        {
            byte[] file = File.ReadAllBytes(path);
            int pos = 12, channels = 0, rate = 0, bits = 0, dataOffset = -1, dataLength = 0;
            while (pos + 8 <= file.Length)
            {
                string id = System.Text.Encoding.ASCII.GetString(file, pos, 4);
                int len = BitConverter.ToInt32(file, pos + 4);
                if (id == "fmt ")
                {
                    channels = BitConverter.ToInt16(file, pos + 10);
                    rate = BitConverter.ToInt32(file, pos + 12);
                    bits = BitConverter.ToInt16(file, pos + 22);
                }
                else if (id == "data")
                {
                    dataOffset = pos + 8;
                    dataLength = Math.Min(len, file.Length - dataOffset);
                    break;
                }
                pos += 8 + len + (len & 1);
            }
            if (dataOffset < 0 || bits != 16 || channels < 1 || channels > 2)
                throw new InvalidDataException("Unsupported WAV: " + path);
            int frames = dataLength / (2 * channels);
            int loopStart = 0, loopLength = 0;
            if (entry.LoopLength > 0 && entry.LoopStart < frames)
            {
                loopStart = entry.LoopStart;
                loopLength = Math.Min(entry.LoopLength, frames - loopStart);
            }
            return new SoundEffect(file, dataOffset, dataLength, rate,
                channels == 2 ? AudioChannels.Stereo : AudioChannels.Mono, loopStart, loopLength);
        }
    }
}
