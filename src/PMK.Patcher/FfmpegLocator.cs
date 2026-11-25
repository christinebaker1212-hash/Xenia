using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace PMK.Patcher
{
    /// <summary>
    /// Finds ffmpeg (used once, to decode the Xbox XMA audio). On Windows it
    /// can download an LGPL build when none is installed.
    /// </summary>
    public static class FfmpegLocator
    {
        public const string WindowsDownloadUrl =
            "https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-win64-lgpl.zip";

        private static string ExeName => RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "ffmpeg.exe" : "ffmpeg";

        public static string Find(string dataDir)
        {
            var candidates = new[]
            {
                Path.Combine(AppContext.BaseDirectory, ExeName),
                Path.Combine(AppContext.BaseDirectory, "tools", ExeName),
                Path.Combine(dataDir, "tools", ExeName),
            };
            foreach (var c in candidates)
                if (File.Exists(c)) return c;
            string path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
            foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                try
                {
                    string c = Path.Combine(dir.Trim('"'), ExeName);
                    if (File.Exists(c)) return c;
                }
                catch (ArgumentException) { }
            }
            return null;
        }

        public static bool CanDownload => RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            && RuntimeInformation.OSArchitecture == Architecture.X64;

        /// <summary>Downloads ffmpeg.exe into &lt;dataDir&gt;/tools. Windows x64 only.</summary>
        public static async Task<string> DownloadAsync(string dataDir, IProgress<double> progress)
        {
            if (!CanDownload)
                throw new PlatformNotSupportedException("Install ffmpeg with your package manager, then start the game again.");
            string toolsDir = Path.Combine(dataDir, "tools");
            Directory.CreateDirectory(toolsDir);
            string zipPath = Path.Combine(toolsDir, "ffmpeg.zip");
            using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(20) })
            {
                http.DefaultRequestHeaders.UserAgent.ParseAdd("ProtectMeKnightPC-Installer");
                using var resp = await http.GetAsync(WindowsDownloadUrl, HttpCompletionOption.ResponseHeadersRead);
                resp.EnsureSuccessStatusCode();
                long total = resp.Content.Headers.ContentLength ?? -1;
                await using var src = await resp.Content.ReadAsStreamAsync();
                await using var dst = File.Create(zipPath);
                var buf = new byte[1 << 16];
                long read = 0;
                int n;
                while ((n = await src.ReadAsync(buf)) > 0)
                {
                    await dst.WriteAsync(buf.AsMemory(0, n));
                    read += n;
                    if (total > 0) progress?.Report((double)read / total);
                }
            }
            string target = Path.Combine(toolsDir, "ffmpeg.exe");
            using (var zip = ZipFile.OpenRead(zipPath))
            {
                var entry = zip.Entries.FirstOrDefault(e => e.FullName.EndsWith("bin/ffmpeg.exe", StringComparison.OrdinalIgnoreCase))
                    ?? throw new InvalidDataException("ffmpeg.exe not found in the download");
                entry.ExtractToFile(target, true);
            }
            File.Delete(zipPath);
            return target;
        }
    }
}
