using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using PMK.Compat.Audio.Xact;

namespace PMK.Patcher
{
    /// <summary>
    /// Decodes the Xbox wave banks (XMA) to 16-bit PCM WAV files that FNA can
    /// play: "&lt;bank&gt;.xwb" becomes "&lt;bank&gt;.xwb.pcm/NNN.wav". XMA is
    /// decoded by ffmpeg; each entry is wrapped in a RIFF/XMA2 header first.
    /// </summary>
    public sealed class AudioConverter
    {
        /// <summary>Bump when the output format changes.</summary>
        public const int AudioVersion = 1;

        private readonly string ffmpeg;

        public AudioConverter(string ffmpegPath)
        {
            ffmpeg = ffmpegPath;
        }

        public void ConvertBank(string xwbPath, Action<string> log = null, Action<int, int> progress = null)
        {
            var bank = WaveBankData.Load(xwbPath);
            string outDir = xwbPath + ".pcm";
            Directory.CreateDirectory(outDir);
            string tmpDir = Path.Combine(Path.GetTempPath(), "pmk-audio-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tmpDir);
            try
            {
                for (int i = 0; i < bank.Entries.Count; i++)
                {
                    var e = bank.Entries[i];
                    string outFile = Path.Combine(outDir, $"{i:D3}.wav");
                    byte[] payload = bank.GetData(e).ToArray();
                    switch (e.Codec)
                    {
                        case WaveCodec.Pcm:
                            FileUtil.WriteAllBytes(outFile, BuildPcmWav(e, payload, bank.BigEndian));
                            break;
                        case WaveCodec.Xma:
                            {
                                string inFile = Path.Combine(tmpDir, $"{i:D3}.xma.wav");
                                File.WriteAllBytes(inFile, BuildXma2Riff(e, payload));
                                RunFfmpeg(inFile, outFile);
                                break;
                            }
                        case WaveCodec.Adpcm:
                            {
                                string inFile = Path.Combine(tmpDir, $"{i:D3}.adpcm.wav");
                                File.WriteAllBytes(inFile, BuildAdpcmRiff(e, payload));
                                RunFfmpeg(inFile, outFile);
                                break;
                            }
                        default:
                            throw new NotSupportedException($"{Path.GetFileName(xwbPath)} entry {i}: codec {e.Codec} not supported");
                    }
                    progress?.Invoke(i + 1, bank.Entries.Count);
                }
                log?.Invoke($"Converted {bank.Entries.Count} sounds from {Path.GetFileName(xwbPath)}");
            }
            finally
            {
                try { Directory.Delete(tmpDir, true); } catch (IOException) { }
            }
        }

        private void RunFfmpeg(string input, string output)
        {
            var psi = new ProcessStartInfo(ffmpeg)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
            };
            foreach (var a in new[] { "-hide_banner", "-loglevel", "error", "-y", "-i", input, "-c:a", "pcm_s16le", "-f", "wav", output })
                psi.ArgumentList.Add(a);
            using var p = Process.Start(psi) ?? throw new InvalidOperationException("Could not start ffmpeg");
            string err = p.StandardError.ReadToEnd();
            p.StandardOutput.ReadToEnd();
            p.WaitForExit();
            if (p.ExitCode != 0 || !File.Exists(output))
                throw new InvalidOperationException($"ffmpeg failed on {Path.GetFileName(input)}: {err.Trim()}");
        }

        /// <summary>RIFF wrapper with an XMA2WAVEFORMATEX header (format tag 0x166).</summary>
        public static byte[] BuildXma2Riff(WaveEntry e, byte[] data)
        {
            var fmt = new MemoryStream();
            var w = new BinaryWriter(fmt);
            int channels = e.Channels;
            w.Write((ushort)0x166);
            w.Write((ushort)channels);
            w.Write(e.SampleRate);
            w.Write(0);                 // average bytes per second (unused by decoders)
            w.Write((ushort)0);         // block align
            w.Write((ushort)0);         // bits per sample
            w.Write((ushort)34);        // extra size
            w.Write((ushort)((channels + 1) / 2)); // streams
            w.Write(channels == 1 ? 4 : channels == 2 ? 3 : 0);
            w.Write(e.Duration);        // samples encoded
            w.Write(data.Length);       // bytes per block (single block)
            w.Write(0);                 // play begin
            w.Write(e.Duration);        // play length
            w.Write(e.LoopStart);
            w.Write(e.LoopLength);
            w.Write((byte)(e.LoopLength > 0 ? 255 : 0));
            w.Write((byte)4);           // encoder version
            w.Write((ushort)1);         // block count
            return Riff(fmt.ToArray(), data);
        }

        private static byte[] BuildAdpcmRiff(WaveEntry e, byte[] data)
        {
            var fmt = new MemoryStream();
            var w = new BinaryWriter(fmt);
            int blockAlign = (e.BlockAlign + 22) * e.Channels;
            int samplesPerBlock = (blockAlign / e.Channels - 7) * 2 + 2;
            w.Write((ushort)2);
            w.Write((ushort)e.Channels);
            w.Write(e.SampleRate);
            w.Write(e.SampleRate * blockAlign / samplesPerBlock);
            w.Write((ushort)blockAlign);
            w.Write((ushort)4);
            w.Write((ushort)32);
            w.Write((ushort)samplesPerBlock);
            w.Write((ushort)7);
            short[] coefs = { 256, 0, 512, -256, 0, 0, 192, 64, 240, 0, 460, -208, 392, -232 };
            foreach (short c in coefs) w.Write(c);
            return Riff(fmt.ToArray(), data);
        }

        private static byte[] BuildPcmWav(WaveEntry e, byte[] data, bool bigEndian)
        {
            int bits = e.BitsFlag == 1 ? 16 : 8;
            if (bits == 16 && bigEndian)
            {
                data = (byte[])data.Clone();
                for (int i = 0; i + 1 < data.Length; i += 2) (data[i], data[i + 1]) = (data[i + 1], data[i]);
            }
            if (bits == 8)
            {
                // Expand to 16-bit so every converted file has the same format.
                var wide = new byte[data.Length * 2];
                for (int i = 0; i < data.Length; i++)
                {
                    short s = (short)((data[i] - 128) << 8);
                    wide[i * 2] = (byte)s;
                    wide[i * 2 + 1] = (byte)(s >> 8);
                }
                data = wide;
            }
            var fmt = new MemoryStream();
            var w = new BinaryWriter(fmt);
            w.Write((ushort)1);
            w.Write((ushort)e.Channels);
            w.Write(e.SampleRate);
            w.Write(e.SampleRate * e.Channels * 2);
            w.Write((ushort)(e.Channels * 2));
            w.Write((ushort)16);
            return Riff(fmt.ToArray(), data);
        }

        private static byte[] Riff(byte[] fmt, byte[] data)
        {
            var ms = new MemoryStream();
            var w = new BinaryWriter(ms);
            w.Write(Encoding.ASCII.GetBytes("RIFF"));
            w.Write(4 + 8 + fmt.Length + 8 + data.Length);
            w.Write(Encoding.ASCII.GetBytes("WAVE"));
            w.Write(Encoding.ASCII.GetBytes("fmt "));
            w.Write(fmt.Length);
            w.Write(fmt);
            w.Write(Encoding.ASCII.GetBytes("data"));
            w.Write(data.Length);
            w.Write(data);
            return ms.ToArray();
        }
    }
}
