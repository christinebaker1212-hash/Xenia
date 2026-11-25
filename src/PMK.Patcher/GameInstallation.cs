using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;

namespace PMK.Patcher
{
    /// <summary>
    /// The user's local copy of the game, built from their own STFS package:
    /// extracted files, relinked assemblies and converted audio. Nothing in
    /// here is shipped with the port.
    /// </summary>
    public sealed class GameInstallation
    {
        public const string GameExe = "AncHell.exe";
        public const string ProtectMeKnightTitleId = "585504F8";

        public string DataDir { get; }
        public string GameDir => Path.Combine(DataDir, "game");
        public string BinDir => Path.Combine(DataDir, "bin");
        public string ManifestPath => Path.Combine(DataDir, "install.json");

        /// <summary>Where the launcher ships PMK.Mods (see Mods.targets); null to install without mods.</summary>
        public string ModsDir { get; set; } = DefaultModsDir;

        public static string DefaultModsDir
        {
            get
            {
                string dir = Path.Combine(AppContext.BaseDirectory, "mods");
                return File.Exists(Path.Combine(dir, ModLinker.ModsAssemblyName + ".dll")) ? dir : null;
            }
        }
        public InstallManifest Manifest { get; private set; }

        public GameInstallation(string dataDir)
        {
            DataDir = dataDir;
            Manifest = LoadManifest();
        }

        public bool IsExtracted => Manifest != null && File.Exists(Path.Combine(GameDir, GameExe));
        public bool NeedsRelink => !IsExtracted || Manifest.PatchVersion != Relinker.PatchVersion
            || !File.Exists(Path.Combine(BinDir, "AncHell.dll"))
            || (Manifest.ModsSha256 ?? "") != ModsHash();

        /// <summary>Changes whenever the shipped mods change, so they are re-applied after an update.</summary>
        private string ModsHash()
        {
            if (ModsDir == null) return "";
            var files = new[] { ModLinker.ModsAssemblyName, ModLinker.GameApiAssemblyName }
                .Select(n => Path.Combine(ModsDir, n + ".dll")).Where(File.Exists);
            return string.Join("+", files.Select(Sha256));
        }
        public bool NeedsAudio => !IsExtracted || Manifest.AudioVersion != AudioConverter.AudioVersion;
        public bool IsReady => IsExtracted && !NeedsRelink && !NeedsAudio;

        private InstallManifest LoadManifest()
        {
            try
            {
                return File.Exists(ManifestPath)
                    ? JsonSerializer.Deserialize<InstallManifest>(File.ReadAllText(ManifestPath))
                    : null;
            }
            catch (JsonException)
            {
                return null;
            }
        }

        private void SaveManifest()
        {
            Directory.CreateDirectory(DataDir);
            File.WriteAllText(ManifestPath, JsonSerializer.Serialize(Manifest, new JsonSerializerOptions { WriteIndented = true }));
        }

        /// <summary>Extracts the game from the user's package (the 75B16C4F... file).</summary>
        public void Extract(string packagePath, Action<string> log)
        {
            var pkg = StfsPackage.Open(packagePath);
            log?.Invoke($"Package: {pkg.Magic.Trim()} \"{pkg.DisplayName}\", {pkg.Entries.Count(e => !e.IsDirectory)} files");
            var exe = pkg.Entries.FirstOrDefault(e => !e.IsDirectory && e.Name.Equals(GameExe, StringComparison.OrdinalIgnoreCase));
            if (exe == null)
                throw new InvalidDataException("This package does not contain Protect Me Knight (AncHell.exe not found). "
                    + "Pick the game's own package from the 584E07D2\\00000002 folder, not the XNA launcher update.");

            // Extract the game folder (584E07D1/) straight into its final place.
            // Never extract-then-move: Windows Defender scans new .exe/.dll files
            // and holds them open, which makes a folder move fail with
            // "Access denied" even for administrators.
            if (File.Exists(ManifestPath)) FileUtil.DeleteFile(ManifestPath);
            Manifest = null;
            FileUtil.DeleteDirectory(Path.Combine(DataDir, "extract.tmp")); // leftover from older versions
            FileUtil.DeleteDirectory(GameDir);
            string prefix = exe.Path.Contains('/') ? exe.Path.Substring(0, exe.Path.LastIndexOf('/') + 1) : "";
            pkg.ExtractSubtree(prefix, GameDir, log);

            Manifest = new InstallManifest
            {
                PackageDisplayName = pkg.DisplayName,
                GameExeSha256 = Sha256(Path.Combine(GameDir, GameExe)),
                InstalledUtc = DateTime.UtcNow,
            };
            SaveManifest();
        }

        public void Relink(Action<string> log)
        {
            var relinker = new Relinker(GameDir) { ModsDir = ModsDir };
            var inputs = Directory.GetFiles(GameDir).Where(f =>
                f.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));
            FileUtil.DeleteDirectory(BinDir);
            relinker.Run(inputs, BinDir);
            foreach (var line in relinker.Log) log?.Invoke(line);
            Manifest.PatchVersion = Relinker.PatchVersion;
            Manifest.ModsSha256 = ModsHash();
            SaveManifest();
        }

        public void ConvertAudio(string ffmpegPath, Action<string> log, Action<int, int> progress = null)
        {
            var converter = new AudioConverter(ffmpegPath);
            var banks = Directory.GetFiles(GameDir, "*.xwb", SearchOption.AllDirectories);
            int total = 0, done = 0;
            foreach (var b in banks) total += Compat.Audio.Xact.WaveBankData.Load(b).Entries.Count;
            foreach (var b in banks)
            {
                int before = done;
                converter.ConvertBank(b, log, (i, n) => progress?.Invoke(before + i, total));
                done += Compat.Audio.Xact.WaveBankData.Load(b).Entries.Count;
            }
            Manifest.AudioVersion = AudioConverter.AudioVersion;
            SaveManifest();
        }

        private static string Sha256(string path)
        {
            using var s = File.OpenRead(path);
            return Convert.ToHexString(SHA256.HashData(s));
        }
    }

    /// <summary>
    /// File operations that tolerate Windows briefly locking freshly written
    /// files (antivirus scans, search indexing, OneDrive sync).
    /// </summary>
    public static class FileUtil
    {
        public static void Retry(Action action, string what)
        {
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    action();
                    return;
                }
                catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException) && attempt < 20)
                {
                    System.Threading.Thread.Sleep(250 + attempt * 100);
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    throw new IOException($"Could not {what}. Close any program using it (or pause antivirus/OneDrive sync) and try again. ({ex.Message})", ex);
                }
            }
        }

        public static void DeleteDirectory(string dir)
        {
            if (!Directory.Exists(dir)) return;
            Retry(() =>
            {
                foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                {
                    var attr = File.GetAttributes(f);
                    if ((attr & FileAttributes.ReadOnly) != 0) File.SetAttributes(f, attr & ~FileAttributes.ReadOnly);
                }
                Directory.Delete(dir, true);
            }, "delete " + dir);
        }

        public static void DeleteFile(string path)
        {
            if (File.Exists(path)) Retry(() => File.Delete(path), "delete " + path);
        }

        public static void WriteAllBytes(string path, byte[] data) =>
            Retry(() => File.WriteAllBytes(path, data), "write " + path);
    }

    public sealed class InstallManifest
    {
        public string PackageDisplayName { get; set; }
        public string GameExeSha256 { get; set; }
        public DateTime InstalledUtc { get; set; }
        public int PatchVersion { get; set; }
        public string ModsSha256 { get; set; }
        public int AudioVersion { get; set; }
    }
}
