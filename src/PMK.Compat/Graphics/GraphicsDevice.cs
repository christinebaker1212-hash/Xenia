using Microsoft.Xna.Framework;
using Fna = Microsoft.Xna.Framework.Graphics;

namespace PMK.Compat.Graphics
{
    /// <summary>XNA 3.1 GraphicsDevice facade over an FNA device.</summary>
    public class GraphicsDevice
    {
        public Fna.GraphicsDevice Native { get; }
        public RenderState RenderState { get; } = new RenderState();
        public SamplerStateCollection SamplerStates { get; } = new SamplerStateCollection();

        public GraphicsDevice(Fna.GraphicsDevice native)
        {
            Native = native;
        }

        public void Clear(Color color) => Native.Clear(color);

        /// <summary>
        /// XNA 3.1 render target binding. Index 0 with null means "the back
        /// buffer", which here is the launcher's virtual back buffer.
        /// </summary>
        public void SetRenderTarget(int index, RenderTarget2D target)
        {
            if (index != 0)
                return; // The game only uses one target.
            if (target != null)
            {
                Native.SetRenderTarget(target.Native);
                CompatHost.BackBufferBound = false;
            }
            else
            {
                CompatHost.BindBackBuffer();
            }
        }
    }

    /// <summary>Recorded only: SpriteBatch sets the state it needs itself.</summary>
    public class RenderState
    {
        public bool AlphaBlendEnable { get; set; }
        public CullMode CullMode { get; set; } = CullMode.CullCounterClockwiseFace;
    }

    public class SamplerStateCollection
    {
        private readonly SamplerState[] states = { new SamplerState(), new SamplerState(), new SamplerState(), new SamplerState() };
        public SamplerState this[int index] => states[index & 3];
    }

    public class SamplerState
    {
        public TextureFilter MagFilter { get; set; } = TextureFilter.Linear;
        public TextureFilter MinFilter { get; set; } = TextureFilter.Linear;
        public TextureFilter MipFilter { get; set; } = TextureFilter.Linear;
    }

    /// <summary>XNA 3.1 RenderTarget2D: separate object with GetTexture().</summary>
    public class RenderTarget2D : System.IDisposable
    {
        public Fna.RenderTarget2D Native { get; }

        public RenderTarget2D(GraphicsDevice device, int width, int height, int numberLevels, SurfaceFormat format)
        {
            Native = new Fna.RenderTarget2D(device.Native, width, height, numberLevels > 1, Fna.SurfaceFormat.Color,
                Fna.DepthFormat.None, 0, Fna.RenderTargetUsage.PreserveContents);
        }

        public int Width => Native.Width;
        public int Height => Native.Height;

        public Fna.Texture2D GetTexture() => Native;

        public void Dispose() => Native.Dispose();
    }
}
