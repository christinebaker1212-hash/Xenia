using System;
using Microsoft.Xna.Framework;
using Fna = Microsoft.Xna.Framework.Graphics;

namespace PMK.Compat.Graphics
{
    /// <summary>
    /// XNA 3.1 SpriteBatch facade. XNA 3.1 used non-premultiplied alpha
    /// blending, so AlphaBlend maps to FNA's NonPremultiplied state.
    /// </summary>
    public class SpriteBatch : IDisposable
    {
        private readonly Fna.SpriteBatch batch;
        private readonly GraphicsDevice device;

        public SpriteBatch(GraphicsDevice graphicsDevice)
        {
            device = graphicsDevice;
            batch = new Fna.SpriteBatch(graphicsDevice.Native);
        }

        public GraphicsDevice GraphicsDevice => device;

        public void Begin() => Begin(SpriteBlendMode.AlphaBlend, Fna.SpriteSortMode.Deferred, SaveStateMode.None);

        public void Begin(SpriteBlendMode blendMode) => Begin(blendMode, Fna.SpriteSortMode.Deferred, SaveStateMode.None);

        public void Begin(SpriteBlendMode blendMode, Fna.SpriteSortMode sortMode, SaveStateMode stateMode)
            => Begin(blendMode, sortMode, stateMode, Matrix.Identity);

        public void Begin(SpriteBlendMode blendMode, Fna.SpriteSortMode sortMode, SaveStateMode stateMode, Matrix transform)
        {
            Fna.BlendState blend = blendMode switch
            {
                SpriteBlendMode.None => Fna.BlendState.Opaque,
                SpriteBlendMode.Additive => Fna.BlendState.Additive,
                _ => Fna.BlendState.NonPremultiplied,
            };
            Fna.SamplerState sampler = CompatHost.SharpPixels ? Fna.SamplerState.PointClamp : Fna.SamplerState.LinearClamp;
            if (CompatHost.BackBufferBound && CompatHost.BackBufferScale != 1f)
                transform *= Matrix.CreateScale(CompatHost.BackBufferScale, CompatHost.BackBufferScale, 1f);
            batch.Begin(sortMode, blend, sampler, Fna.DepthStencilState.None, Fna.RasterizerState.CullNone, null, transform);
        }

        public void End() => batch.End();

        public void Draw(Fna.Texture2D texture, Vector2 position, Color color)
            => batch.Draw(texture, position, color);

        public void Draw(Fna.Texture2D texture, Vector2 position, Rectangle? sourceRectangle, Color color)
            => batch.Draw(texture, position, sourceRectangle, color);

        public void Draw(Fna.Texture2D texture, Vector2 position, Rectangle? sourceRectangle, Color color,
            float rotation, Vector2 origin, float scale, Fna.SpriteEffects effects, float layerDepth)
            => batch.Draw(texture, position, sourceRectangle, color, rotation, origin, scale, effects, layerDepth);

        public void Draw(Fna.Texture2D texture, Vector2 position, Rectangle? sourceRectangle, Color color,
            float rotation, Vector2 origin, Vector2 scale, Fna.SpriteEffects effects, float layerDepth)
            => batch.Draw(texture, position, sourceRectangle, color, rotation, origin, scale, effects, layerDepth);

        public void Draw(Fna.Texture2D texture, Rectangle destinationRectangle, Color color)
            => batch.Draw(texture, destinationRectangle, color);

        public void Draw(Fna.Texture2D texture, Rectangle destinationRectangle, Rectangle? sourceRectangle, Color color)
            => batch.Draw(texture, destinationRectangle, sourceRectangle, color);

        public void DrawString(Fna.SpriteFont spriteFont, string text, Vector2 position, Color color)
            => batch.DrawString(spriteFont, text, position, color);

        public void Dispose() => batch.Dispose();
    }
}
