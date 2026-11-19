using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using PMK.Launcher.UI;

namespace PMK.Launcher
{
    /// <summary>
    /// In-game updater. Checks the repository's latest GitHub release and, on
    /// request, downloads the zip for this platform and swaps the program
    /// files in place. Only the port is updated: the user's game data folder
    /// is never touched (it is re-patched automatically when needed).
    /// </summary>
    public sealed class Updater
    {
        private const string OldSuffix = ".pmk-old";

        private readonly string dataDir;
        private volatile string statusLine = "";
        private volatile ReleaseInfo available;
        private volatile bool busy;
        private volatile float progress;

        public Updater(string dataDir)
        {
            this.dataDir = dataDir;
        }

        public string StatusLine => busy && progress > 0 ? $"Downloading update... {progress * 100:F0}%" : statusLine;
        public string MenuLabel => available != null ? $"Update to v{available.Version}" : "Check for updates";

        public sealed class ReleaseInfo
        {
            public string Version;
            public string Tag;
            public string AssetUrl;
            public string AssetApiUrl;
            public string Sha256;
            public string Notes;
            public string PageUrl;
        }

        // ------------------------------------------------------------------ checking

        public void CheckInBackground(Settings settings, string settingsPath)
        {
            if ((DateTime.UtcNow - settings.LastUpdateCheckUtc).TotalHours < 6)
                return;
            _ = CheckAsync(settings, settingsPath, quiet: true);
        }

        public async Task CheckAsync(Settings settings, string settingsPath, bool quiet)
        {
            if (busy) return;
            busy = true;
            try
            {
                statusLine = "Checking for updates...";
                var info = await FetchLatestAsync();
                settings.LastUpdateCheckUtc = DateTime.UtcNow;
                settings.Save(settingsPath);
                if (info == null)
                {
                    statusLine = quiet ? "" : "No releases found";
                }
                else if (CompareVersions(info.Version, AppInfo.Version) > 0)
                {
                    if (quiet && info.Version == settings.SkippedVersion)
                    {
                        statusLine = "";
                        return;
                    }
                    available = info;
                    statusLine = $"Update available: v{info.Version}";
                }
                else
                {
                    statusLine = quiet ? "" : "You have the latest version";
                }
            }
            catch (Exception ex) when (ex is HttpRequestException || ex is TaskCanceledException || ex is JsonException)
            {
                statusLine = quiet ? "" : "Update check failed: " + ex.Message;
                Log.Write("Update check failed: " + ex.Message);
            }
            finally
            {
                busy = false;
            }
        }

        /// <summary>
        /// Optional GitHub token (data/github-token.txt) for private repositories,
        /// whose releases are invisible without authentication. A fine-grained
        /// token with read-only "Contents" access to the repository is enough.
        /// </summary>
        public static string Token { get; set; }

        public static void LoadToken(string dataDir)
        {
            try
            {
                string path = Path.Combine(dataDir, "github-token.txt");
                Token = File.Exists(path) ? File.ReadAllText(path).Trim() : null;
                if (string.IsNullOrEmpty(Token)) Token = Environment.GetEnvironmentVariable("PMK_GITHUB_TOKEN");
            }
            catch (IOException) { Token = null; }
        }

        public static async Task<ReleaseInfo> FetchLatestAsync()
        {
            string repo = AppInfo.UpdateRepository;
            if (string.IsNullOrWhiteSpace(repo)) return null;
            using var http = NewClient();
            using var resp = await http.GetAsync($"https://api.github.com/repos/{repo}/releases/latest");
            if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
                return null; // no release yet, or the repository is private
            resp.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            var root = doc.RootElement;
            string tag = root.GetProperty("tag_name").GetString() ?? "";
            var info = new ReleaseInfo
            {
                Tag = tag,
                Version = tag.TrimStart('v', 'V'),
                Notes = root.TryGetProperty("body", out var b) ? b.GetString() : "",
                PageUrl = root.TryGetProperty("html_url", out var h) ? h.GetString() : "",
            };
            foreach (var asset in root.GetProperty("assets").EnumerateArray())
            {
                if (asset.GetProperty("name").GetString() != AppInfo.AssetName) continue;
                info.AssetUrl = asset.GetProperty("browser_download_url").GetString();
                info.AssetApiUrl = asset.TryGetProperty("url", out var api) ? api.GetString() : null;
                if (asset.TryGetProperty("digest", out var d) && d.ValueKind == JsonValueKind.String && d.GetString().StartsWith("sha256:"))
                    info.Sha256 = d.GetString().Substring(7);
            }
            return info.AssetUrl != null ? info : null;
        }

        private static HttpClient NewClient()
        {
            var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("ProtectMeKnightPC-Updater/" + AppInfo.Version);
            http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            if (!string.IsNullOrEmpty(Token))
                http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", Token);
            return http;
        }

        /// <summary>Compares dotted versions numerically ("1.10.0" > "1.9.2"); pre-release tags sort lower.</summary>
        public static int CompareVersions(string a, string b)
        {
            static (int[] nums, string pre) Parse(string v)
            {
                v = (v ?? "0").Trim().TrimStart('v', 'V');
                string pre = "";
                int dash = v.IndexOf('-');
                if (dash >= 0) { pre = v.Substring(dash + 1); v = v.Substring(0, dash); }
                var nums = v.Split('.').Select(p => int.TryParse(p, out int n) ? n : 0).ToArray();
                return (nums, pre);
            }
            var (na, pa) = Parse(a);
            var (nb, pb) = Parse(b);
            for (int i = 0; i < Math.Max(na.Length, nb.Length); i++)
            {
                int x = i < na.Length ? na[i] : 0, y = i < nb.Length ? nb[i] : 0;
                if (x != y) return x.CompareTo(y);
            }
            if (pa.Length == 0 && pb.Length > 0) return 1;
            if (pa.Length > 0 && pb.Length == 0) return -1;
            return string.CompareOrdinal(pa, pb);
        }

        // ------------------------------------------------------------------ menu

        public void MenuAction(Settings settings, string settingsPath, LauncherGame game)
        {
            if (busy) return;
            var info = available;
            if (info == null)
            {
                _ = CheckAsync(settings, settingsPath, quiet: false);
                return;
            }
            var page = new MenuPage($"UPDATE TO v{info.Version}")
            {
                Footer = () => StatusLine + "\n" + FirstLines(info.Notes, 4),
                OnBack = game.PopPage,
            };
            page.Add(MenuItem.Button("Update now", () => _ = InstallAsync(info, game)));
            page.Add(MenuItem.Button("Skip this version", () =>
            {
                settings.SkippedVersion = info.Version;
                settings.Save(settingsPath);
                available = null;
                statusLine = "";
                game.PopPage();
            }));
            page.Add(MenuItem.Button("Later", game.PopPage));
            game.PushPage(page);
        }

        private static string FirstLines(string s, int n) =>
            string.Join("\n", (s ?? "").Replace("\r", "").Split('\n').Where(l => l.Trim().Length > 0).Take(n).Select(l => l.Length > 70 ? l.Substring(0, 70) : l));

        // ------------------------------------------------------------------ installing

        private async Task InstallAsync(ReleaseInfo info, LauncherGame game)
        {
            busy = true;
            progress = 0.01f;
            string staging = Path.Combine(dataDir, "update-staging");
            try
            {
                if (Directory.Exists(staging)) Directory.Delete(staging, true);
                Directory.CreateDirectory(staging);
                string zip = Path.Combine(dataDir, "update.zip");
                // Private repositories: assets must be fetched through the API with the token.
                bool viaApi = !string.IsNullOrEmpty(Token) && info.AssetApiUrl != null;
                await DownloadAsync(viaApi ? info.AssetApiUrl : info.AssetUrl, zip, p => progress = (float)p, viaApi);
                if (!string.IsNullOrEmpty(info.Sha256))
                {
                    using var fs = File.OpenRead(zip);
                    string actual = Convert.ToHexString(SHA256.HashData(fs));
                    if (!actual.Equals(info.Sha256, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("The download is corrupted (checksum mismatch).");
                }
                ZipFile.ExtractToDirectory(zip, staging, true);
                File.Delete(zip);
                // Some zips wrap everything in one folder.
                var entries = Directory.GetFileSystemEntries(staging);
                string root = entries.Length == 1 && Directory.Exists(entries[0]) ? entries[0] : staging;
                if (!File.Exists(Path.Combine(root, Path.GetFileName(Environment.ProcessPath))))
                    throw new InvalidDataException("The update does not contain " + Path.GetFileName(Environment.ProcessPath));

                ApplyInPlace(root, AppInfo.InstallDir);
                Directory.Delete(staging, true);
                Log.Write($"Updated to v{info.Version}");
                statusLine = "Update installed - restarting";
                game.Restart(null);
            }
            catch (Exception ex)
            {
                Log.Write("Update failed: " + ex);
                busy = false;
                progress = 0;
                statusLine = "Update failed: " + ex.Message;
                if (ex is UnauthorizedAccessException)
                    Dialogs.Error("Update failed", "The game folder is not writable. Download the update from:\n" + info.PageUrl);
            }
            finally
            {
                busy = false;
            }
        }

        private static async Task DownloadAsync(string url, string path, Action<double> report, bool binaryFromApi)
        {
            using var http = NewClient();
            if (binaryFromApi)
            {
                http.DefaultRequestHeaders.Accept.Clear();
                http.DefaultRequestHeaders.Accept.ParseAdd("application/octet-stream");
            }
            using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
            resp.EnsureSuccessStatusCode();
            long total = resp.Content.Headers.ContentLength ?? -1;
            await using var src = await resp.Content.ReadAsStreamAsync();
            await using var dst = File.Create(path);
            var buf = new byte[1 << 16];
            long read = 0;
            int n;
            while ((n = await src.ReadAsync(buf)) > 0)
            {
                await dst.WriteAsync(buf.AsMemory(0, n));
                read += n;
                if (total > 0) report(read / (double)total);
            }
        }

        /// <summary>
        /// Moves the new files over the install folder. Files in use (the
        /// running exe and its DLLs) are renamed aside first, which Windows
        /// allows; the leftovers are deleted at the next start.
        /// </summary>
        public static void ApplyInPlace(string sourceDir, string installDir)
        {
            foreach (string src in Directory.GetFiles(sourceDir, "*", SearchOption.AllDirectories))
            {
                string rel = Path.GetRelativePath(sourceDir, src);
                if (rel.StartsWith("data" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    continue; // never overwrite user data
                string dest = Path.Combine(installDir, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(dest));
                if (File.Exists(dest))
                {
                    string aside = dest + OldSuffix;
                    if (File.Exists(aside)) File.Delete(aside);
                    File.Move(dest, aside);
                }
                File.Move(src, dest);
            }
        }

        /// <summary>Fallback path: a copy of the launcher applies the update after this process exits.</summary>
        public static int ApplyStagedUpdate(string staging, int parentPid)
        {
            try
            {
                try { Process.GetProcessById(parentPid).WaitForExit(30000); } catch (ArgumentException) { }
                Thread.Sleep(500);
                ApplyInPlace(staging, AppInfo.InstallDir);
                Process.Start(new ProcessStartInfo(Path.Combine(AppInfo.InstallDir, Path.GetFileName(Environment.ProcessPath))) { UseShellExecute = false });
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("Update failed: " + ex);
                return 1;
            }
        }

        /// <summary>Deletes files renamed aside by a previous update.</summary>
        public static void CleanupOldFiles()
        {
            try
            {
                foreach (string f in Directory.GetFiles(AppInfo.InstallDir, "*" + OldSuffix, SearchOption.AllDirectories))
                {
                    try { File.Delete(f); } catch (Exception) { }
                }
            }
            catch (Exception) { }
        }
    }
}
