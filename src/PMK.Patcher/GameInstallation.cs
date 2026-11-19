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
        public InstallManifest Manifest { get; private set; }

        public GameInstallation(string dataDir)
        {
            DataDir = dataDir;
            Manifest = LoadManifest();
        }

        public bool IsExtracted => Manifest != null && File.Exists(Path.Combine(GameDir, GameExe));
        public bool NeedsRelink => !IsExtracted || Manifest.PatchVersion != Relinker.PatchVersion
            || !File.Exists(Path.Combine(BinDir, "AncHell.dll"));
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

            string staging = Path.Combine(DataDir, "extract.tmp");
            if (Directory.Exists(staging)) Directory.Delete(staging, true);
            pkg.ExtractAll(staging, log);

            // The game lives in a sub-folder (584E07D1/); keep only that folder.
            string exeDir = Path.GetDirectoryName(Path.Combine(staging, exe.Path.Replace('/', Path.DirectorySeparatorChar)));
            if (Directory.Exists(GameDir)) Directory.Delete(GameDir, true);
            Directory.Move(exeDir, GameDir);
            Directory.Delete(staging, true);

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
            var relinker = new Relinker(GameDir);
            var inputs = Directory.GetFiles(GameDir).Where(f =>
                f.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));
            if (Directory.Exists(BinDir)) Directory.Delete(BinDir, true);
            relinker.Run(inputs, BinDir);
            foreach (var line in relinker.Log) log?.Invoke(line);
            Manifest.PatchVersion = Relinker.PatchVersion;
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

    public sealed class InstallManifest
    {
        public string PackageDisplayName { get; set; }
        public string GameExeSha256 { get; set; }
        public DateTime InstalledUtc { get; set; }
        public int PatchVersion { get; set; }
        public int AudioVersion { get; set; }
    }
}
