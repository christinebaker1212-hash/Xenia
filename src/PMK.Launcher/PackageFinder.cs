using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace PMK.Launcher
{
    /// <summary>
    /// Looks for the game's STFS package in the usual Xenia content folders:
    /// .../content/&lt;profile&gt;/584E07D2/00000002/&lt;42-char name&gt;.
    /// </summary>
    public static class PackageFinder
    {
        private const string LauncherTitle = "584E07D2";
        private const string MarketplaceContent = "00000002";

        public static string Search(Action<string> log)
        {
            foreach (string root in CandidateRoots())
            {
                try
                {
                    if (!Directory.Exists(root)) continue;
                    foreach (string titleDir in Directory.EnumerateDirectories(root, LauncherTitle, new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 3, IgnoreInaccessible = true }))
                    {
                        string contentDir = Path.Combine(titleDir, MarketplaceContent);
                        if (!Directory.Exists(contentDir)) continue;
                        foreach (string file in Directory.EnumerateFiles(contentDir))
                        {
                            if (IsGamePackage(file))
                            {
                                log?.Invoke("Found game package: " + file);
                                return file;
                            }
                        }
                    }
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                }
            }
            return null;
        }

        private static IEnumerable<string> CandidateRoots()
        {
            string docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var roots = new List<string>
            {
                Path.Combine(docs, "Xenia", "content"),
                Path.Combine(home, "OneDrive", "Documents", "Xenia", "content"),
                Path.Combine(appData, "Xenia", "content"),
                Path.Combine(home, ".local", "share", "Xenia", "content"),
            };
            return roots.Where(r => !string.IsNullOrEmpty(r)).Distinct();
        }

        /// <summary>A Protect Me Knight package: STFS "LIVE" file that contains AncHell.exe.</summary>
        public static bool IsGamePackage(string file)
        {
            try
            {
                var info = new FileInfo(file);
                if (info.Length < 0x10000 || info.Length > 512L * 1024 * 1024) return false;
                using (var fs = File.OpenRead(file))
                {
                    var magic = new byte[4];
                    if (fs.Read(magic, 0, 4) != 4) return false;
                    string m = System.Text.Encoding.ASCII.GetString(magic);
                    if (m != "LIVE" && m != "PIRS" && m != "CON ") return false;
                }
                var pkg = Patcher.StfsPackage.Open(file);
                return pkg.Entries.Any(e => e.Name.Equals(Patcher.GameInstallation.GameExe, StringComparison.OrdinalIgnoreCase));
            }
            catch (Exception)
            {
                return false;
            }
        }
    }
}
