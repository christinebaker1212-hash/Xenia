using System;
using System.IO;

namespace PMK.Launcher
{
    /// <summary>Plain-text log (data folder/log.txt), also echoed to stdout.</summary>
    public static class Log
    {
        private static readonly object Gate = new object();
        private static string path;

        public static void Init(string dataDir)
        {
            Directory.CreateDirectory(dataDir);
            path = Path.Combine(dataDir, "port-log.txt");
            try { File.WriteAllText(path, $"Protect Me Knight PC {AppInfo.Version} - {DateTime.Now:u}\n"); } catch (IOException) { }
        }

        public static void Write(string message)
        {
            string line = $"[{DateTime.Now:HH:mm:ss}] {message}";
            lock (Gate)
            {
                Console.WriteLine(line);
                if (path == null) return;
                try { File.AppendAllText(path, line + "\n"); } catch (IOException) { }
            }
        }
    }
}
