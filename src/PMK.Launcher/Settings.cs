using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Xna.Framework.Input;

namespace PMK.Launcher
{
    /// <summary>User settings, stored as settings.json in the data folder.</summary>
    public sealed class Settings
    {
        // Display
        public int WindowScale { get; set; } = 2;          // multiples of 640x360
        public bool Fullscreen { get; set; }
        public bool VSync { get; set; } = true;
        public bool SharpPixels { get; set; } = true;
        public bool IntegerScaling { get; set; } = true;

        // Audio (0..10)
        public int MusicVolume { get; set; } = 8;
        public int SoundVolume { get; set; } = 10;

        // Input
        public bool Vibration { get; set; } = true;
        /// <summary>Player slot (0-3) the keyboard controls, or -1 for none.</summary>
        public int KeyboardSlot { get; set; } = 0;
        public Dictionary<string, Keys> KeyBindings { get; set; } = DefaultBindings();

        // Online
        public string PlayerName { get; set; } = Environment.UserName;
        public string LastJoinAddress { get; set; } = "";
        public int Port { get; set; } = 47820;
        public int InputDelayFrames { get; set; } = 3;
        public int LocalPlayers { get; set; } = 1;
        public bool UseUpnp { get; set; } = true;

        // Updates
        public bool CheckForUpdates { get; set; } = true;
        public DateTime LastUpdateCheckUtc { get; set; }
        public string SkippedVersion { get; set; } = "";

        // Setup
        public string PackagePath { get; set; } = "";

        public static readonly string[] Actions =
        {
            "Up", "Down", "Left", "Right", "A", "B", "X", "Y", "Start", "Back", "LB", "RB", "LT", "RT",
        };

        public static Dictionary<string, Keys> DefaultBindings() => new Dictionary<string, Keys>
        {
            ["Up"] = Keys.Up,
            ["Down"] = Keys.Down,
            ["Left"] = Keys.Left,
            ["Right"] = Keys.Right,
            ["A"] = Keys.Z,
            ["B"] = Keys.X,
            ["X"] = Keys.A,
            ["Y"] = Keys.S,
            ["Start"] = Keys.Enter,
            ["Back"] = Keys.Back,
            ["LB"] = Keys.Q,
            ["RB"] = Keys.W,
            ["LT"] = Keys.LeftShift,
            ["RT"] = Keys.Space,
        };

        public Keys Binding(string action) =>
            KeyBindings != null && KeyBindings.TryGetValue(action, out var k) ? k : DefaultBindings()[action];

        private static readonly JsonSerializerOptions Json = new JsonSerializerOptions
        {
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter() },
        };

        public static Settings Load(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    var s = JsonSerializer.Deserialize<Settings>(File.ReadAllText(path), Json) ?? new Settings();
                    s.KeyBindings ??= DefaultBindings();
                    foreach (var kv in DefaultBindings())
                        if (!s.KeyBindings.ContainsKey(kv.Key)) s.KeyBindings[kv.Key] = kv.Value;
                    s.Clamp();
                    return s;
                }
            }
            catch (Exception ex) when (ex is JsonException || ex is IOException)
            {
                Log.Write("Settings unreadable, using defaults: " + ex.Message);
            }
            return new Settings();
        }

        public void Save(string path)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, JsonSerializer.Serialize(this, Json));
            }
            catch (IOException ex)
            {
                Log.Write("Could not save settings: " + ex.Message);
            }
        }

        public void Clamp()
        {
            WindowScale = Math.Clamp(WindowScale, 1, 8);
            MusicVolume = Math.Clamp(MusicVolume, 0, 10);
            SoundVolume = Math.Clamp(SoundVolume, 0, 10);
            KeyboardSlot = Math.Clamp(KeyboardSlot, -1, 3);
            InputDelayFrames = Math.Clamp(InputDelayFrames, 1, 10);
            LocalPlayers = Math.Clamp(LocalPlayers, 1, 4);
            Port = Math.Clamp(Port, 1024, 65535);
            PlayerName = string.IsNullOrWhiteSpace(PlayerName) ? "Player" : PlayerName.Trim();
            if (PlayerName.Length > 16) PlayerName = PlayerName.Substring(0, 16);
        }
    }
}
