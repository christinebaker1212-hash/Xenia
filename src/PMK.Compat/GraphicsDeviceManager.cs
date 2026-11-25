using PMK.Compat.Graphics;

namespace PMK.Compat
{
    /// <summary>
    /// XNA 3.1 GraphicsDeviceManager facade. Back buffer requests are recorded
    /// but the launcher decides the real window size (the game always draws
    /// into a 1280x720 virtual back buffer, as on the Xbox).
    /// </summary>
    public class GraphicsDeviceManager
    {
        private readonly Game game;

        public GraphicsDeviceManager(Game game)
        {
            this.game = game;
        }

        public GraphicsDevice GraphicsDevice => game.GraphicsDevice;

        public int PreferredBackBufferWidth { get; set; } = CompatHost.VirtualWidth;
        public int PreferredBackBufferHeight { get; set; } = CompatHost.VirtualHeight;
        public SurfaceFormat PreferredBackBufferFormat { get; set; } = SurfaceFormat.Color;
        public ShaderProfile MinimumPixelShaderProfile { get; set; }
        public ShaderProfile MinimumVertexShaderProfile { get; set; }
        public bool IsFullScreen { get; set; }
        public bool SynchronizeWithVerticalRetrace { get; set; } = true;

        public void ApplyChanges() { }
        public void ToggleFullScreen() { }
    }
}
