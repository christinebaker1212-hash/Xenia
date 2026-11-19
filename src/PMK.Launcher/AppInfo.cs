using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;

namespace PMK.Launcher
{
    public static class AppInfo
    {
        public static string Version
        {
            get
            {
                string v = typeof(AppInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";
                int plus = v.IndexOf('+');
                return plus >= 0 ? v.Substring(0, plus) : v;
            }
        }

        public static string UpdateRepository =>
            typeof(AppInfo).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
                .FirstOrDefault(a => a.Key == "UpdateRepository")?.Value ?? "";

        /// <summary>Folder holding the program files (ProtectMeKnight.exe and its DLLs).</summary>
        public static string InstallDir => AppContext.BaseDirectory;

        /// <summary>Name of the sub-folder releases keep the program files in.</summary>
        public const string AppFolderName = "app";

        /// <summary>
        /// Top-level folder the user unzipped: holds the "Protect Me Knight"
        /// launcher, "app" and "data". When run from a build folder (no "app"
        /// parent) it is the program folder itself.
        /// </summary>
        public static string RootDir
        {
            get
            {
                string dir = Path.TrimEndingDirectorySeparator(InstallDir);
                return string.Equals(Path.GetFileName(dir), AppFolderName, StringComparison.OrdinalIgnoreCase)
                    ? Path.GetDirectoryName(dir)
                    : dir;
            }
        }

        /// <summary>Release asset name for this platform.</summary>
        public static string AssetName =>
            RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "ProtectMeKnightPC-win-x64.zip" : "ProtectMeKnightPC-linux-x64.zip";

        /// <summary>
        /// Data folder: "data" next to the program when that is writable
        /// (portable install), otherwise the user's local app data.
        /// </summary>
        public static string ResolveDataDir(string overrideDir)
        {
            if (!string.IsNullOrEmpty(overrideDir))
                return Path.GetFullPath(overrideDir);
            string portable = Path.Combine(RootDir, "data");
            try
            {
                Directory.CreateDirectory(portable);
                string probe = Path.Combine(portable, ".write-test");
                File.WriteAllText(probe, "");
                File.Delete(probe);
                return portable;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ProtectMeKnightPC");
            }
        }
    }
}
