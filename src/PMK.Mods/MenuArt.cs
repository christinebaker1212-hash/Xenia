using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using AncHell;

namespace PMK.Mods
{
    /// <summary>
    /// Builds menu words such as "ENDLESS" in the game's own menu lettering.
    /// Letters are cut at run time from the user's copy of the game's menu
    /// textures (nothing from the game is stored in this repository); letters
    /// the game never drew, such as X, are generated in the same style.
    /// </summary>
    public static class MenuArt
    {
        private const string GameModeTex = "Content/Title/GameMode_TEX0";
        private const string DifficultyTex = "Content/Title/Difficulty_TEX0";

        public const int GlyphHeight = 35;
        private const int GlyphWidth = 28;
        private const int Spacing = 1;

        /// <summary>Where each letter sits in the game's menu textures (texture, x, y, width).</summary>
        private static readonly Dictionary<char, (string tex, int x, int y, int w)> Letters = new()
        {
            ['A'] = (GameModeTex, 21, 3, GlyphWidth),
            ['R'] = (GameModeTex, 50, 3, GlyphWidth),
            ['C'] = (GameModeTex, 79, 3, GlyphWidth),
            ['D'] = (GameModeTex, 137, 3, GlyphWidth),
            ['E'] = (GameModeTex, 166, 3, GlyphWidth),
            ['S'] = (GameModeTex, 11, 43, GlyphWidth),
            ['U'] = (GameModeTex, 40, 43, GlyphWidth),
            ['V'] = (GameModeTex, 98, 43, GlyphWidth),
            ['I'] = (GameModeTex, 127, 43, 20),
            ['M'] = (GameModeTex, 1, 82, GlyphWidth),
            ['O'] = (GameModeTex, 30, 82, GlyphWidth),
            ['Y'] = (DifficultyTex, 126, 3, GlyphWidth),
            ['N'] = (DifficultyTex, 9, 43, GlyphWidth),
            ['H'] = (DifficultyTex, 39, 123, GlyphWidth),
            ['L'] = (DifficultyTex, 97, 123, GlyphWidth),
        };

        /// <summary>The highlight frame drawn around the selected menu entry.</summary>
        public static readonly Rectangle SelectionBox = new Rectangle(2, 163, 212, 51);

        private static readonly Dictionary<string, Color[]> pixels = new();
        private static readonly Dictionary<string, Texture2D> words = new();

        public static Texture2D BoxTexture(Main main) => main.objContent.Load<Texture2D>(GameModeTex);

        /// <summary>The word drawn in menu lettering; <paramref name="disabled"/> greys it out.</summary>
        public static Texture2D Word(Main main, string text, bool disabled = false)
        {
            string key = text + (disabled ? "#off" : "");
            if (words.TryGetValue(key, out var cached) && !cached.IsDisposed) return cached;

            var reference = main.objContent.Load<Texture2D>(GameModeTex);
            int width = -Spacing;
            foreach (char c in text) width += LetterWidth(c) + Spacing;
            var data = new Color[width * GlyphHeight];
            int x = 0;
            foreach (char c in text)
            {
                if (c != ' ') Blit(main, c, data, width, x);
                x += LetterWidth(c) + Spacing;
            }
            if (disabled) GreyOut(data);

            // Same surface format as the game's textures, so the colour channels line up.
            var tex = new Texture2D(reference.GraphicsDevice, width, GlyphHeight, false, reference.Format);
            tex.SetData(data);
            words[key] = tex;
            return tex;
        }

        private static int LetterWidth(char c) =>
            c == ' ' ? 16 : Letters.TryGetValue(c, out var l) ? l.w : GlyphWidth;

        private static void Blit(Main main, char c, Color[] dst, int dstWidth, int dstX)
        {
            Color[] glyph;
            int w;
            if (Letters.TryGetValue(c, out var l))
            {
                var src = Pixels(main, l.tex, out int srcWidth);
                w = l.w;
                glyph = new Color[w * GlyphHeight];
                for (int y = 0; y < GlyphHeight; y++)
                    Array.Copy(src, (l.y + y) * srcWidth + l.x, glyph, y * w, w);
            }
            else
            {
                w = GlyphWidth;
                glyph = Generate(main, c);
            }
            for (int y = 0; y < GlyphHeight; y++)
                Array.Copy(glyph, y * w, dst, y * dstWidth + dstX, w);
        }

        private static Color[] Pixels(Main main, string texture, out int width)
        {
            var tex = main.objContent.Load<Texture2D>(texture);
            width = tex.Width;
            if (!pixels.TryGetValue(texture, out var data))
            {
                data = new Color[tex.Width * tex.Height];
                tex.GetData(data);
                pixels[texture] = data;
            }
            return data;
        }

        /// <summary>
        /// Draws a letter the game doesn't have, in its style: a fill shape,
        /// a 1px dark outline, a 1px light outline and a light drop shadow two
        /// pixels down and right. Colours are taken from the game's own V.
        /// </summary>
        private static Color[] Generate(Main main, char c)
        {
            var fillMask = new bool[GlyphWidth, GlyphHeight];
            switch (c)
            {
                case 'X':
                    // Two 8px strokes crossing, stepping one pixel every two rows,
                    // like the diagonals of the game's V.
                    for (int y = 2; y <= 30; y++)
                    {
                        int s = (y - 2) / 2;
                        for (int x = 2 + s; x < 10 + s; x++) fillMask[x, y] = true;
                        for (int x = 16 - s; x < 24 - s; x++) fillMask[x, y] = true;
                    }
                    break;
                default:
                    throw new ArgumentException("No glyph for '" + c + "'");
            }

            var v = Letters['V'];
            var src = Pixels(main, v.tex, out int srcWidth);
            Color Sample(int x, int y) => src[(v.y + y) * srcWidth + v.x + x];
            Color fill = Sample(4, 5), dark = Sample(1, 5), light = Sample(0, 5);

            bool Fill(int x, int y) => x >= 0 && y >= 0 && x < GlyphWidth && y < GlyphHeight && fillMask[x, y];
            var outline = new bool[GlyphWidth, GlyphHeight];
            for (int y = 0; y < GlyphHeight; y++)
                for (int x = 0; x < GlyphWidth; x++)
                    outline[x, y] = !fillMask[x, y] && (Fill(x - 1, y) || Fill(x + 1, y) || Fill(x, y - 1) || Fill(x, y + 1));
            bool Solid(int x, int y) => x >= 0 && y >= 0 && x < GlyphWidth && y < GlyphHeight && (fillMask[x, y] || outline[x, y]);

            var halo = new bool[GlyphWidth, GlyphHeight];
            for (int y = 0; y < GlyphHeight; y++)
                for (int x = 0; x < GlyphWidth; x++)
                    for (int dy = -1; dy <= 1 && !halo[x, y]; dy++)
                        for (int dx = -1; dx <= 1; dx++)
                            if (Solid(x + dx, y + dy)) { halo[x, y] = true; break; }

            var result = new Color[GlyphWidth * GlyphHeight];
            for (int y = 0; y < GlyphHeight; y++)
                for (int x = 0; x < GlyphWidth; x++)
                {
                    Color p = Color.Transparent;
                    if (fillMask[x, y]) p = fill;
                    else if (outline[x, y]) p = dark;
                    else if (halo[x, y]
                        || (x >= 1 && y >= 1 && halo[x - 1, y - 1])
                        || (x >= 2 && y >= 2 && halo[x - 2, y - 2])) p = light;
                    result[y * GlyphWidth + x] = p;
                }
            return result;
        }

        private static void GreyOut(Color[] data)
        {
            for (int i = 0; i < data.Length; i++)
            {
                var p = data[i];
                if (p.A == 0) continue;
                // Symmetric in R and B, so it doesn't matter which order the texture stores them in.
                int lum = (p.R + 2 * p.G + p.B) / 4;
                byte g = (byte)(56 + lum * 45 / 100);
                data[i] = new Color(g, g, g, p.A);
            }
        }
    }
}
