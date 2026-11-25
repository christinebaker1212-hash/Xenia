using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;

namespace PMK.Launcher.UI
{
    /// <summary>Single-line text entry (SDL text input, Ctrl+V pastes).</summary>
    public sealed class TextPrompt
    {
        public string Title { get; }
        public string Value { get; private set; }
        public Action<string> Done { get; }

        public TextPrompt(string title, string initial, Action<string> done)
        {
            Title = title;
            Value = initial ?? "";
            Done = done;
        }

        public void OnChar(char c)
        {
            if (c == '\b')
            {
                if (Value.Length > 0) Value = Value.Substring(0, Value.Length - 1);
            }
            else if (c >= 32 && c <= 126 && Value.Length < 64)
            {
                Value += c;
            }
        }

        public void Paste()
        {
            try
            {
                string clip = SDL3.SDL.SDL_GetClipboardText();
                if (!string.IsNullOrEmpty(clip))
                    foreach (char c in clip.Trim()) OnChar(c);
            }
            catch (Exception ex) when (ex is DllNotFoundException || ex is EntryPointNotFoundException) { }
        }

        public void Draw(Ui ui)
        {
            var box = new Rectangle(ui.Width / 6, ui.Height / 2 - (int)(ui.LineHeight * 2.5f), ui.Width * 2 / 3, (int)(ui.LineHeight * 5));
            ui.Frame(box, new Color(10, 10, 40, 240), Color.Yellow);
            ui.Text(Title, new Vector2(ui.Width / 2f, box.Y + ui.LineHeight * 0.6f), Color.Yellow, center: true);
            string caret = (Environment.TickCount / 500) % 2 == 0 ? "_" : " ";
            ui.Text(Value + caret, new Vector2(ui.Width / 2f, box.Y + ui.LineHeight * 2f), Color.White, center: true);
            ui.Text("Enter: OK   Esc: cancel   Ctrl+V: paste", new Vector2(ui.Width / 2f, box.Y + ui.LineHeight * 3.5f), Color.LightGray, center: true);
        }
    }

    /// <summary>"Press a key" capture for rebinding.</summary>
    public sealed class KeyCapture
    {
        private readonly string action;
        private readonly Action<Keys> done;

        public KeyCapture(string action, Action<Keys> done)
        {
            this.action = action;
            this.done = done;
        }

        public void Done(Keys k) => done(k);

        public void Draw(Ui ui)
        {
            var box = new Rectangle(ui.Width / 5, ui.Height / 2 - (int)(ui.LineHeight * 2), ui.Width * 3 / 5, (int)(ui.LineHeight * 4));
            ui.Frame(box, new Color(10, 10, 40, 240), Color.Yellow);
            ui.Text($"Press a key for \"{action}\"", new Vector2(ui.Width / 2f, box.Y + ui.LineHeight), Color.White, center: true);
            ui.Text("Esc: cancel", new Vector2(ui.Width / 2f, box.Y + ui.LineHeight * 2.4f), Color.LightGray, center: true);
        }
    }
}
