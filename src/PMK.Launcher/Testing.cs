using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;

namespace PMK.Launcher
{
    /// <summary>
    /// Scripted input for automated runs. One line per press:
    /// "&lt;frame&gt; &lt;duration&gt; &lt;Button[+Button...]&gt;", e.g. "300 5 Start".
    /// Button names are XNA <see cref="Buttons"/> values.
    /// </summary>
    public static class InputScript
    {
        public static Func<int, GamePadState?> Load(string path)
        {
            var presses = new List<(int from, int to, Buttons[] buttons)>();
            foreach (string raw in File.ReadAllLines(path))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#")) continue;
                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                int from = int.Parse(parts[0]);
                int dur = int.Parse(parts[1]);
                var names = parts.Length > 2 ? parts[2].Split('+') : Array.Empty<string>();
                var buttons = Array.ConvertAll(names, n => Enum.Parse<Buttons>(n, true));
                presses.Add((from, from + dur, buttons));
            }
            return frame =>
            {
                var down = new List<Buttons>();
                foreach (var p in presses)
                    if (frame >= p.from && frame < p.to) down.AddRange(p.buttons);
                return new GamePadState(Vector2.Zero, Vector2.Zero, 0f, 0f, down.ToArray());
            };
        }
    }

    public static class Screenshot
    {
        public static void Save(GraphicsDevice device, string path)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                var pp = device.PresentationParameters;
                var data = new Color[pp.BackBufferWidth * pp.BackBufferHeight];
                device.GetBackBufferData(data);
                using var tex = new Texture2D(device, pp.BackBufferWidth, pp.BackBufferHeight);
                tex.SetData(data);
                using var fs = File.Create(path);
                tex.SaveAsPng(fs, tex.Width, tex.Height);
            }
            catch (Exception ex)
            {
                Log.Write("Screenshot failed: " + ex.Message);
            }
        }
    }
}
