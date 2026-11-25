using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace PMK.Stub
{
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            string root = AppDomain.CurrentDomain.BaseDirectory;
            string exe = Path.Combine(root, "app", "ProtectMeKnight.exe");
            if (!File.Exists(exe))
            {
                MessageBox.Show(
                    "The \"app\" folder is missing or incomplete:\n" + exe +
                    "\n\nUnzip the whole download again, keeping the \"app\" folder next to this program.",
                    "Protect Me Knight PC", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 1;
            }
            try
            {
                var psi = new ProcessStartInfo(exe, string.Join(" ", args.Select(Quote)))
                {
                    UseShellExecute = false,
                    WorkingDirectory = root,
                };
                Process.Start(psi);
                return 0;
            }
            catch (Exception ex)
            {
                MessageBox.Show("Could not start the game:\n" + ex.Message, "Protect Me Knight PC",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 1;
            }
        }

        private static string Quote(string a) =>
            a.Length > 0 && a.IndexOfAny(new[] { ' ', '\t', '"' }) < 0 ? a : "\"" + a.Replace("\"", "\\\"") + "\"";
    }
}
