using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;

namespace PMK.Launcher.UI
{
    public sealed class MenuItem
    {
        public Func<string> Label;
        public Action Activate;
        public Action<int> Adjust;   // -1 / +1 from left/right
        public Func<bool> Enabled = () => true;

        public static MenuItem Button(string label, Action activate) =>
            new MenuItem { Label = () => label, Activate = activate };

        public static MenuItem Button(Func<string> label, Action activate) =>
            new MenuItem { Label = label, Activate = activate };

        public static MenuItem Value(Func<string> label, Action<int> adjust) =>
            new MenuItem { Label = label, Adjust = adjust, Activate = () => adjust(1) };
    }

    /// <summary>A vertical list of items with a title and an optional footer line.</summary>
    public sealed class MenuPage
    {
        public string Title;
        public Func<string> Footer;
        public List<MenuItem> Items = new List<MenuItem>();
        public Action OnBack;
        public int Selected;

        public MenuPage(string title) { Title = title; }

        public MenuPage Add(MenuItem item)
        {
            Items.Add(item);
            return this;
        }

        public void Handle(MenuKey key)
        {
            if (Items.Count == 0)
            {
                if (key == MenuKey.Back || key == MenuKey.Accept) OnBack?.Invoke();
                return;
            }
            switch (key)
            {
                case MenuKey.Up:
                    Move(-1);
                    break;
                case MenuKey.Down:
                    Move(1);
                    break;
                case MenuKey.Left:
                    Items[Selected].Adjust?.Invoke(-1);
                    break;
                case MenuKey.Right:
                    Items[Selected].Adjust?.Invoke(1);
                    break;
                case MenuKey.Accept:
                    if (Items[Selected].Enabled()) Items[Selected].Activate?.Invoke();
                    break;
                case MenuKey.Back:
                    OnBack?.Invoke();
                    break;
            }
        }

        private void Move(int d)
        {
            for (int n = 0; n < Items.Count; n++)
            {
                Selected = (Selected + d + Items.Count) % Items.Count;
                if (Items[Selected].Enabled()) return;
            }
        }

        public void Draw(Ui ui)
        {
            float lh = ui.LineHeight * 1.5f;
            string footer = Footer?.Invoke();
            string[] footerLines = string.IsNullOrEmpty(footer) ? new string[0] : footer.Split('\n');
            float height = (Items.Count + 2) * lh + (footerLines.Length + 1) * ui.LineHeight;
            float width = Math.Max(ui.Width * 0.5f, 360);
            var box = new Rectangle((int)((ui.Width - width) / 2), (int)Math.Max(10, (ui.Height - height) / 2), (int)width, (int)height);
            ui.Frame(box, new Color(10, 10, 30, 220), new Color(255, 210, 90));
            ui.Text(Title, new Vector2(ui.Width / 2f, box.Y + lh * 0.5f), new Color(255, 210, 90), center: true);
            for (int i = 0; i < Items.Count; i++)
            {
                var it = Items[i];
                bool sel = i == Selected;
                Color c = !it.Enabled() ? Color.Gray : sel ? Color.Yellow : Color.White;
                string label = (sel ? "> " : "  ") + it.Label() + (sel ? " <" : "  ");
                ui.Text(label, new Vector2(ui.Width / 2f, box.Y + lh * (i + 1.7f)), c, center: true);
            }
            if (footerLines.Length > 0)
            {
                float y = box.Y + lh * (Items.Count + 1.7f);
                foreach (var line in footerLines)
                {
                    ui.Text(line, new Vector2(ui.Width / 2f, y), Color.LightGray, center: true);
                    y += ui.LineHeight;
                }
            }
        }
    }
}
