using System;
using System.IO;

namespace PMK.Compat.Content
{
    /// <summary>
    /// ContentManager that resolves assets inside the extracted game folder
    /// (CompatHost.GameDataDir) instead of the launcher's folder. FNA itself
    /// reads the Xbox 360 XNB files (LZX compression, byte-swapped textures).
    /// </summary>
    public class ContentManager : Microsoft.Xna.Framework.Content.ContentManager
    {
        public ContentManager(IServiceProvider serviceProvider) : base(serviceProvider, string.Empty) { }

        public ContentManager(IServiceProvider serviceProvider, string rootDirectory) : base(serviceProvider, rootDirectory) { }

        protected override Stream OpenStream(string assetName)
        {
            string path = Path.Combine(RootDirectory ?? string.Empty, assetName.Replace('\\', '/'));
            string full = CompatHost.ResolveGamePath(path + ".xnb");
            if (!File.Exists(full))
            {
                // Game paths use the original capitalisation; be lenient on case-sensitive file systems.
                string alt = CaseInsensitivePath.Find(full);
                if (alt != null)
                    full = alt;
                else
                    throw new Microsoft.Xna.Framework.Content.ContentLoadException("Missing asset: " + assetName);
            }
            return File.OpenRead(full);
        }
    }

    internal static class CaseInsensitivePath
    {
        public static string Find(string path)
        {
            string root = Path.GetPathRoot(path);
            string[] parts = path.Substring(root.Length).Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
            string current = root;
            foreach (string part in parts)
            {
                if (!Directory.Exists(current)) return null;
                string match = null;
                foreach (string entry in Directory.EnumerateFileSystemEntries(current))
                {
                    if (string.Equals(Path.GetFileName(entry), part, StringComparison.OrdinalIgnoreCase))
                    {
                        match = entry;
                        break;
                    }
                }
                if (match == null) return null;
                current = match;
            }
            return current;
        }
    }
}
