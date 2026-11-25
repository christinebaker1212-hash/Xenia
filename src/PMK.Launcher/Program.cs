using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using PMK.Patcher;

namespace PMK.Launcher
{
    public static class Program
    {
        public static Options Options { get; private set; }

        [STAThread]
        public static int Main(string[] args)
        {
            // The game's debug logger asks for Shift-JIS, which .NET only has via this provider.
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

            Options = Options.Parse(args);
            if (Options.ApplyUpdateStaging != null)
                return Updater.ApplyStagedUpdate(Options.ApplyUpdateStaging, Options.ApplyUpdateParentPid);

            string dataDir = AppInfo.ResolveDataDir(Options.DataDir);
            Log.Init(dataDir);
            Updater.CleanupOldFiles();
            Updater.LoadToken(dataDir);

            if (Options.InstallPackage != null)
                return CommandLineInstall(dataDir, Options.InstallPackage);

            try
            {
                using var game = new LauncherGame(dataDir, Options);
                game.Run();
                return game.ExitCode;
            }
            catch (Exception ex)
            {
                Log.Write("Fatal: " + ex);
                Dialogs.Error("Protect Me Knight PC", "The game stopped because of an error:\n\n" + ex.Message
                    + "\n\nDetails are in " + Path.Combine(dataDir, "port-log.txt"));
                return 1;
            }
        }

        /// <summary>Non-interactive install: ProtectMeKnight --install &lt;package&gt;.</summary>
        private static int CommandLineInstall(string dataDir, string package)
        {
            try
            {
                var inst = new GameInstallation(dataDir);
                inst.Extract(package, Log.Write);
                inst.Relink(Log.Write);
                string ffmpeg = FfmpegLocator.Find(dataDir);
                if (ffmpeg == null && FfmpegLocator.CanDownload)
                    ffmpeg = Task.Run(() => FfmpegLocator.DownloadAsync(dataDir, null)).GetAwaiter().GetResult();
                if (ffmpeg == null)
                {
                    Log.Write("ffmpeg not found: install it and run again to convert the audio.");
                    return 2;
                }
                inst.ConvertAudio(ffmpeg, Log.Write);
                Log.Write("Install complete: " + dataDir);
                return 0;
            }
            catch (Exception ex)
            {
                Log.Write("Install failed: " + ex);
                return 1;
            }
        }
    }

    public sealed class Options
    {
        public string DataDir;
        public string InstallPackage;
        public string Host;      // "--host" (value unused) starts hosting immediately
        public string Join;      // "--join addr[:port]"
        public string ApplyUpdateStaging;
        public int ApplyUpdateParentPid;
        public int AutoFrames;   // run N frames then quit (testing)
        public string ScreenshotDir;
        public int ScreenshotEvery;
        public string InputScript; // scripted button presses (testing)
        public bool SkipMenu;
        public int Seed;
        public bool Windowed;
        public int AutoStartPlayers;
        public string Password;
        public int DropAtFrame = -1;

        public static Options Parse(string[] args)
        {
            var o = new Options();
            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i];
                string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException("Missing value for " + a);
                switch (a)
                {
                    case "--data": o.DataDir = Next(); break;
                    case "--install": o.InstallPackage = Next(); break;
                    case "--host": o.Host = "1"; break;
                    case "--join": o.Join = Next(); break;
                    case "--apply-update": o.ApplyUpdateStaging = Next(); o.ApplyUpdateParentPid = int.Parse(Next()); break;
                    case "--frames": o.AutoFrames = int.Parse(Next()); break;
                    case "--screenshots": o.ScreenshotDir = Next(); break;
                    case "--screenshot-every": o.ScreenshotEvery = int.Parse(Next()); break;
                    case "--input-script": o.InputScript = Next(); break;
                    case "--play": o.SkipMenu = true; break;
                    case "--seed": o.Seed = int.Parse(Next()); break;
                    case "--windowed": o.Windowed = true; break;
                    case "--autostart": o.AutoStartPlayers = int.Parse(Next()); break;
                    case "--password": o.Password = Next(); break;
                    case "--drop-at": o.DropAtFrame = int.Parse(Next()); break;
                }
            }
            return o;
        }
    }
}
