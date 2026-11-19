using System;
using System.Text;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace PMK.Launcher.UI
{
    /// <summary>
    /// Minimal immediate-mode drawing for launcher screens. Text uses the
    /// game's own DebugFont (ASCII 32-126) once the game is extracted.
    /// </summary>
    public sealed class Ui : IDisposable
    {
        private readonly GraphicsDevice device;
        private readonly SpriteBatch batch;
        private readonly Texture2D pixel;
        public SpriteFont Font { get; set; }
        public float TextScale { get; set; } = 1f;

        public Ui(GraphicsDevice device)
        {
            this.device = device;
            batch = new SpriteBatch(device);
            pixel = new Texture2D(device, 1, 1);
            pixel.SetData(new[] { Color.White });
        }

        public int Width => device.Viewport.Width;
        public int Height => device.Viewport.Height;

        public void Begin() => batch.Begin(SpriteSortMode.Deferred, BlendState.NonPremultiplied, SamplerState.LinearClamp, null, RasterizerState.CullNone);
        public void End() => batch.End();

        public void Rect(Rectangle r, Color c) => batch.Draw(pixel, r, c);

        public void Frame(Rectangle r, Color fill, Color border)
        {
            Rect(r, fill);
            Rect(new Rectangle(r.X, r.Y, r.Width, 2), border);
            Rect(new Rectangle(r.X, r.Bottom - 2, r.Width, 2), border);
            Rect(new Rectangle(r.X, r.Y, 2, r.Height), border);
            Rect(new Rectangle(r.Right - 2, r.Y, 2, r.Height), border);
        }

        public void Bar(Rectangle r, float fraction, Color fill)
        {
            Frame(r, new Color(0, 0, 0, 160), Color.White);
            int w = (int)((r.Width - 6) * Math.Clamp(fraction, 0f, 1f));
            Rect(new Rectangle(r.X + 3, r.Y + 3, w, r.Height - 6), fill);
        }

        public float LineHeight => Font != null ? Font.LineSpacing * TextScale : 16 * TextScale;

        public string Sanitize(string s)
        {
            if (s == null) return string.Empty;
            var sb = new StringBuilder(s.Length);
            foreach (char ch in s)
            {
                if (ch == '\n' || (ch >= 32 && ch <= 126)) sb.Append(ch);
                else sb.Append('?');
            }
            return sb.ToString();
        }

        public Vector2 Measure(string s) =>
            Font != null ? Font.MeasureString(Sanitize(s)) * TextScale : new Vector2(s.Length * 8 * TextScale, 16 * TextScale);

        public void Text(string s, Vector2 pos, Color c, bool center = false)
        {
            if (Font == null) return; // Before extraction: shapes only (status goes in the title bar).
            s = Sanitize(s);
            if (center) pos.X -= Measure(s).X / 2f;
            pos = new Vector2(MathF.Round(pos.X), MathF.Round(pos.Y));
            batch.DrawString(Font, s, pos + new Vector2(TextScale, TextScale), new Color(0, 0, 0, 200), 0f, Vector2.Zero, TextScale, SpriteEffects.None, 0f);
            batch.DrawString(Font, s, pos, c, 0f, Vector2.Zero, TextScale, SpriteEffects.None, 0f);
        }

        public void Draw(Texture2D tex, Rectangle dest, Color c) => batch.Draw(tex, dest, c);

        public void Dispose()
        {
            batch.Dispose();
            pixel.Dispose();
        }
    }
}
