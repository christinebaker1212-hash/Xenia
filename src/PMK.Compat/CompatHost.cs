using System;
using System.IO;
using FnaGame = Microsoft.Xna.Framework.Game;
using FnaGdm = Microsoft.Xna.Framework.GraphicsDeviceManager;
using Microsoft.Xna.Framework.Graphics;

namespace PMK.Compat
{
    /// <summary>
    /// Shared state between the launcher (which owns the real FNA game loop)
    /// and the XNA 3.1 facade the relinked game talks to.
    /// </summary>
    public static class CompatHost
    {
        /// <summary>The real FNA game that owns the window and device.</summary>
        public static FnaGame Host { get; private set; }
        public static FnaGdm HostGraphics { get; private set; }

        /// <summary>Folder that holds the extracted game (contains "Content").</summary>
        public static string GameDataDir { get; set; } = AppContext.BaseDirectory;

        /// <summary>
        /// Render target that stands in for the Xbox back buffer (1280x720).
        /// The launcher scales it to the real window.
        /// </summary>
        public static RenderTarget2D VirtualBackBuffer { get; private set; }

        public const int VirtualWidth = 1280;
        public const int VirtualHeight = 720;

        /// <summary>The game's native resolution (it draws 640x360 and scales it 2x).</summary>
        public const int NativeWidth = 640;
        public const int NativeHeight = 360;

        /// <summary>Integer multiple of 640x360 the virtual back buffer is rendered at.</summary>
        public static int RenderScale { get; private set; } = 2;

        /// <summary>
        /// Extra scale applied to sprite batches drawn to the back buffer, so
        /// the game's own 2x blit lands at RenderScale without resampling.
        /// </summary>
        public static float BackBufferScale => RenderScale / 2f;

        /// <summary>True while the game has the back buffer bound.</summary>
        public static bool BackBufferBound { get; set; }

        /// <summary>Point sampling for sprites (sharp pixels) instead of the Xbox's linear filter.</summary>
        public static bool SharpPixels { get; set; } = true;

        /// <summary>Master volumes applied on top of the game's own volumes (0..1).</summary>
        public static float MusicVolume { get; set; } = 1f;
        public static float SoundVolume { get; set; } = 1f;

        /// <summary>Set while a launcher overlay owns input; the game sees idle pads.</summary>
        public static bool InputSuspended { get; set; }

        /// <summary>The XNA 3.1 game instance created by the relinked assembly.</summary>
        public static Game Guest { get; internal set; }

        public static void Attach(FnaGame host, FnaGdm graphics)
        {
            Host = host;
            HostGraphics = graphics;
        }

        /// <summary>(Re)creates the virtual back buffer at <paramref name="scale"/> x 640x360.</summary>
        public static void CreateDeviceResources(int scale)
        {
            scale = System.Math.Clamp(scale, 1, 8);
            if (VirtualBackBuffer != null && !VirtualBackBuffer.IsDisposed && RenderScale == scale)
                return;
            VirtualBackBuffer?.Dispose();
            RenderScale = scale;
            VirtualBackBuffer = new RenderTarget2D(
                Host.GraphicsDevice, NativeWidth * scale, NativeHeight * scale, false,
                SurfaceFormat.Color, DepthFormat.None, 0, RenderTargetUsage.PreserveContents);
        }

        /// <summary>Binds the virtual back buffer (before the game draws).</summary>
        public static void BindBackBuffer()
        {
            Host.GraphicsDevice.SetRenderTarget(VirtualBackBuffer);
            BackBufferBound = true;
        }

        /// <summary>Resolves a game-relative path such as "Content\\Sound\\se\\se.xsb".</summary>
        public static string ResolveGamePath(string relative)
        {
            string norm = relative.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);
            if (Path.IsPathRooted(norm))
                return norm;
            return Path.Combine(GameDataDir, norm);
        }
    }
}
