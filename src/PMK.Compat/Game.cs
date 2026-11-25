using System;
using Microsoft.Xna.Framework;
using PMK.Compat.Graphics;
using FnaGameTime = Microsoft.Xna.Framework.GameTime;

namespace PMK.Compat
{
    /// <summary>
    /// XNA 3.1 <c>Microsoft.Xna.Framework.Game</c> as seen by the relinked game.
    /// It does not own a window: the launcher's FNA game drives it.
    /// </summary>
    public class Game : IDisposable
    {
        private readonly GameComponentCollection components = new GameComponentCollection();
        private Content.ContentManager content;
        private GraphicsDevice device;
        private bool initialized;

        public Game()
        {
            if (CompatHost.Host == null)
                throw new InvalidOperationException("CompatHost.Attach must be called before creating the game.");
            CompatHost.Guest = this;
            content = new Content.ContentManager(Services);
        }

        public GameComponentCollection Components => components;
        public GameServiceContainer Services => CompatHost.Host.Services;
        public GameWindow Window => CompatHost.Host.Window;

        public Content.ContentManager Content
        {
            get => content;
            set => content = value;
        }

        public GraphicsDevice GraphicsDevice
        {
            get
            {
                if (device == null || device.Native != CompatHost.Host.GraphicsDevice)
                    device = new GraphicsDevice(CompatHost.Host.GraphicsDevice);
                return device;
            }
        }

        /// <summary>The launcher always runs at a fixed 60 Hz, like the Xbox.</summary>
        public bool IsFixedTimeStep { get; set; } = true;

        public bool IsActive => CompatHost.Host.IsActive;

        public void Run()
        {
            // The launcher owns the loop; Run() only exists for API compatibility.
        }

        public void Exit() => CompatHost.Host.Exit();

        protected virtual void Initialize()
        {
            foreach (IGameComponent c in components)
                c.Initialize();
            LoadContent();
        }

        protected virtual void LoadContent() { }
        protected virtual void UnloadContent() { }

        protected virtual void Update(FnaGameTime gameTime)
        {
            foreach (IGameComponent c in components)
                (c as IUpdateable)?.Update(gameTime);
        }

        protected virtual void Draw(FnaGameTime gameTime) { }

        // Entry points used by the launcher.
        public void HostInitialize()
        {
            if (initialized) return;
            initialized = true;
            Initialize();
        }

        public void HostUpdate(FnaGameTime t) => Update(t);
        public void HostDraw(FnaGameTime t) => Draw(t);

        public void HostUnload()
        {
            if (!initialized) return;
            UnloadContent();
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (disposing && CompatHost.Guest == this)
                CompatHost.Guest = null;
        }
    }
}
