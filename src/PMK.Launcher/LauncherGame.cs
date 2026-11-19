using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using DetRandom = PMK.Compat.DetRandom;
using CompatHost = PMK.Compat.CompatHost;
using InputRouter = PMK.Compat.Input.InputRouter;
using PMK.Launcher.Net;
using PMK.Launcher.UI;
using PMK.Patcher;

namespace PMK.Launcher
{
    /// <summary>
    /// The real FNA game: owns the window, runs first-time setup, the menus,
    /// online sessions and the updater, and drives the original game.
    /// </summary>
    public sealed class LauncherGame : Microsoft.Xna.Framework.Game
    {
        private enum Screen { Setup, Menu, Playing }

        private readonly GraphicsDeviceManager graphics;
        private readonly string dataDir;
        private readonly Options options;
        private readonly Settings settings;
        private readonly string settingsPath;
        private readonly GameInstallation install;
        private readonly LocalInput localInput;
        private readonly MenuInput menuInput = new MenuInput();
        private readonly Stack<MenuPage> pages = new Stack<MenuPage>();
        private readonly Updater updater;

        private Screen screen = Screen.Setup;
        private Ui ui;
        private PMK.Compat.Game guest;
        private Texture2D menuBackground;
        private bool overlayOpen;
        private NetSession net;
        private TextPrompt prompt;
        private KeyCapture keyCapture;
        private string toast;
        private int toastFrames;
        private long frameCounter;
        private bool windowDirty = true;

        // Setup state (worker thread writes, main thread reads).
        private volatile string setupStatus = "Preparing...";
        private volatile float setupProgress;
        private Task setupTask;
        private volatile bool waitingForPackagePick;
        private volatile string pickedPackage;

        public int ExitCode { get; private set; }

        public LauncherGame(string dataDir, Options options)
        {
            this.dataDir = dataDir;
            this.options = options;
            settingsPath = Path.Combine(dataDir, "settings.json");
            settings = Settings.Load(settingsPath);
            install = new GameInstallation(dataDir);
            localInput = new LocalInput(settings);
            updater = new Updater(dataDir);

            graphics = new GraphicsDeviceManager(this)
            {
                PreferredBackBufferWidth = CompatHost.NativeWidth * settings.WindowScale,
                PreferredBackBufferHeight = CompatHost.NativeHeight * settings.WindowScale,
                SynchronizeWithVerticalRetrace = settings.VSync,
                IsFullScreen = settings.Fullscreen && !options.Windowed,
            };
            IsFixedTimeStep = true;
            TargetElapsedTime = TimeSpan.FromTicks(TimeSpan.TicksPerSecond / 60);
            InactiveSleepTime = TimeSpan.Zero;
            Window.AllowUserResizing = true;
            Window.Title = "Protect Me Knight";
            Window.ClientSizeChanged += (s, e) => windowDirty = true;
            IsMouseVisible = false;

            CompatHost.Attach(this, graphics);
            ApplyAudioSettings();
            CompatHost.SharpPixels = settings.SharpPixels;
            InputRouter.Provider = localInput;
            if (options.InputScript != null)
                localInput.Script = InputScript.Load(options.InputScript);
        }

        protected override void Initialize()
        {
            base.Initialize();
            Dialogs.Window = Window.Handle;
            TextInputEXT.TextInput += OnTextInput;
        }

        protected override void LoadContent()
        {
            ui = new Ui(GraphicsDevice);
            UpdateRenderScale();
            if (install.IsExtracted && !install.NeedsRelink && !install.NeedsAudio)
                OnInstalled();
            else
                BeginSetup();
        }

        // ------------------------------------------------------------------ setup

        private void BeginSetup()
        {
            screen = Screen.Setup;
            string package = FindPackage();
            if (install.IsExtracted)
            {
                setupTask = Task.Run(() => RunSetup(null));
            }
            else if (package != null)
            {
                setupTask = Task.Run(() => RunSetup(package));
            }
            else
            {
                waitingForPackagePick = true;
                setupStatus = "Select your Protect Me Knight package";
                Dialogs.Info("Protect Me Knight PC - first-time setup",
                    "This port needs your own copy of the Xbox 360 game.\n\n"
                    + "Next, select the game's package file: the 42-character file (75B16C4F...) inside\n"
                    + "...\\584E07D2\\00000002\\ in your Xenia content folder or your Xbox 360 storage.\n\n"
                    + "It is extracted and converted once; nothing is uploaded anywhere.");
                Dialogs.PickFile("Protect Me Knight package", path => pickedPackage = path ?? "");
            }
        }

        private string FindPackage()
        {
            foreach (string candidate in new[] { options.InstallPackage, settings.PackagePath })
                if (!string.IsNullOrEmpty(candidate) && File.Exists(candidate)) return candidate;
            return PackageFinder.Search(Log.Write);
        }

        private void RunSetup(string package)
        {
            try
            {
                if (package != null)
                {
                    setupStatus = "Extracting the game package...";
                    setupProgress = 0.05f;
                    install.Extract(package, Log.Write);
                    settings.PackagePath = package;
                    settings.Save(settingsPath);
                }
                if (install.NeedsRelink)
                {
                    setupStatus = "Converting the game code to run on PC...";
                    setupProgress = 0.2f;
                    install.Relink(Log.Write);
                }
                if (install.NeedsAudio)
                {
                    setupStatus = "Looking for ffmpeg (used once to convert the Xbox audio)...";
                    setupProgress = 0.3f;
                    string ffmpeg = FfmpegLocator.Find(dataDir);
                    if (ffmpeg == null && FfmpegLocator.CanDownload)
                    {
                        setupStatus = "Downloading ffmpeg (one time only)...";
                        ffmpeg = FfmpegLocator.DownloadAsync(dataDir, new Progress<double>(p =>
                        {
                            setupProgress = 0.3f + 0.3f * (float)p;
                            setupStatus = $"Downloading ffmpeg (one time only)... {p * 100:F0}%";
                        })).GetAwaiter().GetResult();
                    }
                    if (ffmpeg != null)
                    {
                        setupStatus = "Converting music and sound effects...";
                        install.ConvertAudio(ffmpeg, Log.Write, (i, n) =>
                        {
                            setupProgress = 0.6f + 0.4f * i / Math.Max(1, n);
                            setupStatus = $"Converting music and sound effects... {i}/{n}";
                        });
                    }
                    else
                    {
                        Dialogs.Info("Protect Me Knight PC", "ffmpeg was not found, so the game will run without sound for now.\n"
                            + "Install ffmpeg and start the game again to convert the audio.");
                    }
                }
                setupProgress = 1f;
                setupStatus = "Done";
            }
            catch (Exception ex)
            {
                Log.Write("Setup failed: " + ex);
                setupStatus = "SETUP FAILED: " + ex.Message;
                throw;
            }
        }

        private void UpdateSetup()
        {
            if (waitingForPackagePick && pickedPackage != null)
            {
                waitingForPackagePick = false;
                if (pickedPackage.Length == 0)
                {
                    Exit();
                    return;
                }
                string pkg = pickedPackage;
                pickedPackage = null;
                setupTask = Task.Run(() => RunSetup(pkg));
            }
            Window.Title = "Protect Me Knight - setup: " + setupStatus;
            if (ui.Font == null && install.IsExtracted)
                TryLoadFont();
            if (setupTask != null && setupTask.IsCompleted)
            {
                if (setupTask.IsFaulted)
                {
                    var ex = setupTask.Exception?.GetBaseException();
                    setupTask = null;
                    Dialogs.Error("Protect Me Knight PC - setup failed", ex?.Message ?? "Unknown error");
                    settings.PackagePath = "";
                    settings.Save(settingsPath);
                    ExitCode = 1;
                    Exit();
                    return;
                }
                setupTask = null;
                OnInstalled();
            }
        }

        private void OnInstalled()
        {
            Window.Title = "Protect Me Knight";
            CompatHost.GameDataDir = install.GameDir;
            GuestLoader.Init(install.BinDir);
            TryLoadFont();
            TryLoadMenuBackground();

            if (options.Join != null)
                StartJoin(options.Join);
            else if (options.Host != null)
                StartHost();
            else if (options.SkipMenu)
                StartLocalGame();
            else
                ShowMainMenu();

            if (settings.CheckForUpdates && options.AutoFrames == 0)
                updater.CheckInBackground(settings, settingsPath);
        }

        private void TryLoadFont()
        {
            try
            {
                var cm = new Microsoft.Xna.Framework.Content.ContentManager(Services, install.GameDir);
                ui.Font = cm.Load<SpriteFont>(Path.Combine("Content", "DebugFont"));
                ui.TextScale = 1f;
            }
            catch (Exception ex)
            {
                Log.Write("UI font unavailable: " + ex.Message);
            }
        }

        private void TryLoadMenuBackground()
        {
            try
            {
                var cm = new Microsoft.Xna.Framework.Content.ContentManager(Services, install.GameDir);
                menuBackground = cm.Load<Texture2D>(Path.Combine("Content", "Title", "Title_BG"));
            }
            catch (Exception ex)
            {
                Log.Write("Menu background unavailable: " + ex.Message);
            }
        }

        // ------------------------------------------------------------------ menus

        private void ShowMainMenu()
        {
            screen = Screen.Menu;
            pages.Clear();
            var page = new MenuPage("PROTECT ME KNIGHT  -  PC")
            {
                Footer = () => $"v{AppInfo.Version}   {updater.StatusLine}",
            };
            page.Add(MenuItem.Button("Play", StartLocalGame));
            page.Add(MenuItem.Button("Online: host a game", StartHost));
            page.Add(MenuItem.Button("Online: join a game", () => AskText("Host address (ip or ip:port)", settings.LastJoinAddress, s =>
            {
                settings.LastJoinAddress = s;
                settings.Save(settingsPath);
                StartJoin(s);
            })));
            page.Add(MenuItem.Button("Options", () => pages.Push(BuildOptions())));
            page.Add(MenuItem.Button(() => updater.MenuLabel, () => updater.MenuAction(settings, settingsPath, this)));
            page.Add(MenuItem.Button("Quit", Exit));
            pages.Push(page);
            menuInput.Swallow();
        }

        private MenuPage BuildOptions()
        {
            var p = new MenuPage("OPTIONS") { OnBack = () => { settings.Save(settingsPath); pages.Pop(); } };
            p.Add(MenuItem.Value(() => $"Window size: {settings.WindowScale}x ({640 * settings.WindowScale}x{360 * settings.WindowScale})", d =>
            {
                settings.WindowScale = Math.Clamp(settings.WindowScale + d, 1, 8);
                ApplyWindowSettings();
            }));
            p.Add(MenuItem.Value(() => "Fullscreen: " + OnOff(settings.Fullscreen), d => { settings.Fullscreen = !settings.Fullscreen; ApplyWindowSettings(); }));
            p.Add(MenuItem.Value(() => "VSync: " + OnOff(settings.VSync), d => { settings.VSync = !settings.VSync; ApplyWindowSettings(); }));
            p.Add(MenuItem.Value(() => "Pixels: " + (settings.SharpPixels ? "sharp" : "smooth (Xbox)"), d =>
            {
                settings.SharpPixels = !settings.SharpPixels;
                CompatHost.SharpPixels = settings.SharpPixels;
            }));
            p.Add(MenuItem.Value(() => "Integer scaling: " + OnOff(settings.IntegerScaling), d => { settings.IntegerScaling = !settings.IntegerScaling; windowDirty = true; }));
            p.Add(MenuItem.Value(() => $"Music volume: {settings.MusicVolume}", d => { settings.MusicVolume = Math.Clamp(settings.MusicVolume + d, 0, 10); ApplyAudioSettings(); }));
            p.Add(MenuItem.Value(() => $"Sound volume: {settings.SoundVolume}", d => { settings.SoundVolume = Math.Clamp(settings.SoundVolume + d, 0, 10); ApplyAudioSettings(); }));
            p.Add(MenuItem.Value(() => "Vibration: " + OnOff(settings.Vibration), d => settings.Vibration = !settings.Vibration));
            p.Add(MenuItem.Value(() => "Keyboard controls: " + (settings.KeyboardSlot < 0 ? "off" : "player " + (settings.KeyboardSlot + 1)), d =>
            {
                settings.KeyboardSlot = settings.KeyboardSlot + d < -1 ? 3 : settings.KeyboardSlot + d > 3 ? -1 : settings.KeyboardSlot + d;
            }));
            p.Add(MenuItem.Button("Keyboard bindings...", () => pages.Push(BuildBindings())));
            p.Add(MenuItem.Button("Online settings...", () => pages.Push(BuildOnlineOptions())));
            p.Add(MenuItem.Value(() => "Check for updates at start: " + OnOff(settings.CheckForUpdates), d => settings.CheckForUpdates = !settings.CheckForUpdates));
            p.Add(MenuItem.Button("Back", () => p.OnBack()));
            return p;
        }

        private MenuPage BuildBindings()
        {
            var p = new MenuPage("KEYBOARD BINDINGS") { OnBack = () => { settings.Save(settingsPath); pages.Pop(); } };
            p.Footer = () => "Enter: change   Esc: back";
            foreach (string action in Settings.Actions)
            {
                string a = action;
                p.Add(MenuItem.Button(() => $"{a,-6} {settings.Binding(a)}", () => keyCapture = new KeyCapture(a, k =>
                {
                    settings.KeyBindings[a] = k;
                    settings.Save(settingsPath);
                })));
            }
            p.Add(MenuItem.Button("Reset to defaults", () => { settings.KeyBindings = Settings.DefaultBindings(); settings.Save(settingsPath); }));
            p.Add(MenuItem.Button("Back", () => p.OnBack()));
            return p;
        }

        private MenuPage BuildOnlineOptions()
        {
            var p = new MenuPage("ONLINE SETTINGS") { OnBack = () => { settings.Save(settingsPath); pages.Pop(); } };
            p.Add(MenuItem.Button(() => "Name: " + settings.PlayerName, () => AskText("Your name", settings.PlayerName, s => { settings.PlayerName = s; settings.Clamp(); })));
            p.Add(MenuItem.Value(() => $"Players on this PC: {settings.LocalPlayers}", d => settings.LocalPlayers = Math.Clamp(settings.LocalPlayers + d, 1, 4)));
            p.Add(MenuItem.Value(() => $"Port: {settings.Port}", d => settings.Port = Math.Clamp(settings.Port + d, 1024, 65535)));
            p.Add(MenuItem.Value(() => $"Input delay (frames): {settings.InputDelayFrames}", d => settings.InputDelayFrames = Math.Clamp(settings.InputDelayFrames + d, 1, 10)));
            p.Add(MenuItem.Value(() => "Automatic port forwarding (UPnP): " + OnOff(settings.UseUpnp), d => settings.UseUpnp = !settings.UseUpnp));
            p.Add(MenuItem.Button("Back", () => p.OnBack()));
            return p;
        }

        private static string OnOff(bool b) => b ? "on" : "off";

        private void AskText(string title, string initial, Action<string> done)
        {
            prompt = new TextPrompt(title, initial, done);
            TextInputEXT.StartTextInput();
        }

        private void OnTextInput(char c) => prompt?.OnChar(c);

        public void ShowToast(string text, int frames = 300)
        {
            toast = text;
            toastFrames = frames;
        }

        public void PushPage(MenuPage page) => pages.Push(page);
        public void PopPage() { if (pages.Count > 0) pages.Pop(); }

        // ------------------------------------------------------------------ game sessions

        private void StartLocalGame()
        {
            if (options.Seed != 0) DetRandom.Reset(options.Seed);
            InputRouter.Provider = localInput;
            LaunchGuest();
        }

        private void LaunchGuest()
        {
            pages.Clear();
            overlayOpen = false;
            try
            {
                guest = GuestLoader.CreateGame();
                guest.Window.Title = "Protect Me Knight";
                guest.HostInitialize();
                screen = Screen.Playing;
                Log.Write("Game started");
            }
            catch (Exception ex)
            {
                Log.Write("Could not start the game: " + ex);
                Dialogs.Error("Protect Me Knight PC", "The game could not start:\n" + ex.Message);
                ExitCode = 1;
                Exit();
            }
        }

        private void StartHost()
        {
            net?.Dispose();
            net = NetSession.Host(settings, install.Manifest.GameExeSha256);
            net.SampleLocal = SampleLocalForNet;
            ShowLobby();
        }

        private void StartJoin(string address)
        {
            net?.Dispose();
            net = NetSession.Join(settings, address, install.Manifest.GameExeSha256);
            net.SampleLocal = SampleLocalForNet;
            ShowLobby();
        }

        private Microsoft.Xna.Framework.Input.GamePadState SampleLocalForNet(int localIndex, int frame)
        {
            if (localIndex == 0 && localInput.Script != null)
                return localInput.Script(frame) ?? localInput.ReadSlot(0);
            return localInput.ReadSlot(localIndex);
        }

        private void ShowLobby()
        {
            screen = Screen.Menu;
            pages.Clear();
            var page = new MenuPage(net.IsHost ? "ONLINE  -  HOSTING" : "ONLINE  -  JOINING")
            {
                Footer = () => net.LobbyText(),
                OnBack = LeaveOnline,
            };
            if (net.IsHost)
                page.Add(new MenuItem { Label = () => "Start game", Activate = () => net.StartGame(), Enabled = () => net.CanStart });
            page.Add(MenuItem.Button("Leave", LeaveOnline));
            pages.Push(page);
            menuInput.Swallow();
        }

        private void LeaveOnline()
        {
            net?.Dispose();
            net = null;
            if (guest != null)
                Restart(null);
            else
                ShowMainMenu();
        }

        private void OnNetStarted(NetStartInfo info)
        {
            DetRandom.Reset(info.Seed);
            InputRouter.Provider = net.Input;
            Log.Write($"Online game starting: seed {info.Seed}, delay {info.InputDelay}, slots {string.Join(",", info.SlotOwners)}");
            LaunchGuest();
        }

        /// <summary>
        /// Restarts the program (the game keeps its state in statics, so a
        /// fresh process is the clean way back to the menu).
        /// </summary>
        public void Restart(string[] args)
        {
            try
            {
                if (options.AutoFrames > 0) { Exit(); return; } // automated runs never respawn
                string exe = Environment.ProcessPath;
                var psi = new ProcessStartInfo(exe) { UseShellExecute = false };
                // Under "dotnet ProtectMeKnight.dll" the process is the dotnet host.
                if (string.Equals(Path.GetFileNameWithoutExtension(exe), "dotnet", StringComparison.OrdinalIgnoreCase))
                    psi.ArgumentList.Add(typeof(LauncherGame).Assembly.Location);
                if (options.DataDir != null) { psi.ArgumentList.Add("--data"); psi.ArgumentList.Add(options.DataDir); }
                if (args != null) foreach (var a in args) psi.ArgumentList.Add(a);
                Process.Start(psi);
            }
            catch (Exception ex)
            {
                Log.Write("Restart failed: " + ex.Message);
            }
            Exit();
        }

        // ------------------------------------------------------------------ loop

        protected override void Update(GameTime gameTime)
        {
            frameCounter++;
            menuInput.BeginFrame();
            if (windowDirty) UpdateRenderScale();
            HandleGlobalKeys();
            net?.Pump();
            if (net != null && net.IsHost && net.StartInfo == null && options.AutoStartPlayers > 0 && net.PlayerCount >= options.AutoStartPlayers)
                net.StartGame();
            if (net != null && net.StartInfo != null && guest == null)
                OnNetStarted(net.StartInfo);
            if (net != null && net.FatalError != null)
            {
                string err = net.FatalError;
                net.Dispose();
                net = null;
                Dialogs.Error("Online play", err);
                if (guest != null) { Restart(null); return; }
                ShowMainMenu();
            }
            if (toastFrames > 0) toastFrames--;

            switch (screen)
            {
                case Screen.Setup:
                    UpdateSetup();
                    break;
                case Screen.Menu:
                    UpdateMenus();
                    break;
                case Screen.Playing:
                    UpdatePlaying(gameTime);
                    break;
            }

            if (options.AutoFrames > 0 && frameCounter >= options.AutoFrames)
                Exit();
            base.Update(gameTime);
        }

        private void HandleGlobalKeys()
        {
            var kb = Keyboard.GetState();
            bool alt = kb.IsKeyDown(Keys.LeftAlt) || kb.IsKeyDown(Keys.RightAlt);
            if (menuInput.KeyPressed(Keys.F11) || (alt && menuInput.KeyPressed(Keys.Enter)))
            {
                settings.Fullscreen = !settings.Fullscreen;
                ApplyWindowSettings();
                settings.Save(settingsPath);
            }
        }

        private void UpdateMenus()
        {
            if (keyCapture != null)
            {
                var kb = Keyboard.GetState();
                foreach (Keys k in kb.GetPressedKeys())
                {
                    if (menuInput.PreviousKeyboard.IsKeyDown(k)) continue;
                    if (k != Keys.Escape) keyCapture.Done(k);
                    keyCapture = null;
                    menuInput.Swallow();
                    break;
                }
                menuInput.Poll();
                return;
            }
            if (prompt != null)
            {
                var kb = Keyboard.GetState();
                bool ctrl = kb.IsKeyDown(Keys.LeftControl) || kb.IsKeyDown(Keys.RightControl);
                if (ctrl && menuInput.KeyPressed(Keys.V)) prompt.Paste();
                var key = menuInput.Poll();
                if (menuInput.KeyPressed(Keys.Enter) || key == MenuKey.Accept && !menuInput.Keyboard.IsKeyDown(Keys.Z))
                    FinishPrompt(true);
                else if (menuInput.KeyPressed(Keys.Escape))
                    FinishPrompt(false);
                return;
            }
            var mk = menuInput.Poll();
            if (pages.Count > 0)
                pages.Peek().Handle(mk);
        }

        private void FinishPrompt(bool accept)
        {
            TextInputEXT.StopTextInput();
            var p = prompt;
            prompt = null;
            menuInput.Swallow();
            if (accept && p.Value.Trim().Length > 0) p.Done(p.Value.Trim());
        }

        private void UpdatePlaying(GameTime gameTime)
        {
            // Esc (or Back+Start on a controller) opens the port menu.
            var kb = Keyboard.GetState();
            bool padMenu = false;
            for (int i = 0; i < 4; i++)
            {
                var s = Microsoft.Xna.Framework.Input.GamePad.GetState((PlayerIndex)i);
                if (s.IsButtonDown(Buttons.Back) && s.IsButtonDown(Buttons.Start) && s.IsButtonDown(Buttons.LeftShoulder)) padMenu = true;
            }
            if (!overlayOpen && (menuInput.KeyPressed(Keys.Escape) || padMenu))
                OpenOverlay();
            else if (overlayOpen)
            {
                if (prompt != null || keyCapture != null)
                    UpdateMenus();
                else
                {
                    var mk = menuInput.Poll();
                    if (pages.Count > 0) pages.Peek().Handle(mk);
                    if (pages.Count == 0) overlayOpen = false;
                }
            }
            else
            {
                menuInput.Poll();
            }

            CompatHost.InputSuspended = overlayOpen;
            // Offline the overlay pauses the game; online the simulation must keep going.
            if (overlayOpen && net == null)
                return;
            if (net != null && !net.Input.CanAdvance())
                return;

            InputRouter.LatchFrame();
            guest.HostUpdate(gameTime);
            net?.Input.AfterFrame();
            RenderGuest(gameTime);
        }

        /// <summary>
        /// Draws the game into the virtual back buffer right after each
        /// update. The original ran Update and Draw 1:1 at 60 Hz and changes
        /// a little state inside Draw, so drawing once per update (never
        /// skipped or doubled) keeps the simulation identical on every
        /// machine regardless of frame rate - required for online play.
        /// </summary>
        private void RenderGuest(GameTime gameTime)
        {
            var previous = GraphicsDevice.GetRenderTargets();
            CompatHost.BindBackBuffer();
            DetRandom.Suspended = true;
            try { guest.HostDraw(gameTime); }
            finally
            {
                DetRandom.Suspended = false;
                GraphicsDevice.SetRenderTargets(previous);
                CompatHost.BackBufferBound = false;
            }
            guestFramesRendered++;
        }

        private long guestFramesRendered;

        private void OpenOverlay()
        {
            overlayOpen = true;
            pages.Clear();
            var p = new MenuPage(net != null ? "MENU  (online game keeps running)" : "PAUSED")
            {
                OnBack = () => pages.Clear(),
                Footer = () => net?.StatusLine() ?? "Esc: resume",
            };
            p.Add(MenuItem.Button("Resume", () => pages.Clear()));
            p.Add(MenuItem.Button("Options", () => pages.Push(BuildOptions())));
            p.Add(MenuItem.Button(net != null ? "Leave online game" : "Quit to title menu", () => { net?.Dispose(); net = null; Restart(null); }));
            p.Add(MenuItem.Button("Quit to desktop", Exit));
            pages.Push(p);
            menuInput.Swallow();
        }

        protected override void Draw(GameTime gameTime)
        {
            if (screen == Screen.Playing && guest != null)
            {
                GraphicsDevice.SetRenderTarget(null);
                GraphicsDevice.Clear(Color.Black);
                if (guestFramesRendered > 0) Present();
            }
            else
            {
                GraphicsDevice.SetRenderTarget(null);
                GraphicsDevice.Clear(new Color(8, 8, 20));
            }

            ui.TextScale = Math.Max(1, GraphicsDevice.PresentationParameters.BackBufferHeight / 540);
            ui.Begin();
            if (screen == Screen.Setup) DrawSetup();
            if (screen == Screen.Menu && menuBackground != null)
            {
                ui.Draw(menuBackground, Letterbox(menuBackground.Width, menuBackground.Height), new Color(110, 110, 110));
            }
            if ((screen == Screen.Menu || overlayOpen) && pages.Count > 0)
            {
                if (overlayOpen) ui.Rect(new Rectangle(0, 0, ui.Width, ui.Height), new Color(0, 0, 0, 140));
                pages.Peek().Draw(ui);
            }
            if (net != null && screen == Screen.Playing && !overlayOpen)
            {
                string s = net.Input.Waiting ? "Waiting for other players..." : null;
                if (s != null) ui.Text(s, new Vector2(ui.Width / 2f, ui.Height - ui.LineHeight * 2), Color.Yellow, center: true);
                if (net.DesyncWarning != null) ui.Text(net.DesyncWarning, new Vector2(ui.Width / 2f, ui.LineHeight), Color.OrangeRed, center: true);
            }
            if (prompt != null) prompt.Draw(ui);
            if (keyCapture != null) keyCapture.Draw(ui);
            if (toastFrames > 0 && toast != null)
                ui.Text(toast, new Vector2(ui.Width / 2f, ui.Height - ui.LineHeight * 3.5f), Color.White, center: true);
            ui.End();

            base.Draw(gameTime);

            if (options.ScreenshotDir != null && options.ScreenshotEvery > 0 && frameCounter % options.ScreenshotEvery == 0)
                Screenshot.Save(GraphicsDevice, Path.Combine(options.ScreenshotDir, $"frame{frameCounter:D6}.png"));
        }

        private void DrawSetup()
        {
            var bar = new Rectangle(ui.Width / 8, ui.Height / 2 - 12, ui.Width * 3 / 4, 24);
            ui.Bar(bar, setupProgress, new Color(255, 200, 80));
            ui.Text("Protect Me Knight PC - first-time setup", new Vector2(ui.Width / 2f, bar.Y - ui.LineHeight * 3), Color.White, center: true);
            ui.Text(setupStatus, new Vector2(ui.Width / 2f, bar.Bottom + ui.LineHeight), Color.LightGray, center: true);
        }

        /// <summary>Draws the virtual back buffer into the window.</summary>
        private void Present()
        {
            var src = CompatHost.VirtualBackBuffer;
            var dest = Letterbox(src.Width, src.Height);
            bool exact = dest.Width == src.Width && dest.Height == src.Height;
            var sampler = exact || settings.SharpPixels && dest.Width % CompatHost.NativeWidth == 0 ? SamplerState.PointClamp : SamplerState.LinearClamp;
            var sb = PresentBatch;
            sb.Begin(SpriteSortMode.Immediate, BlendState.Opaque, sampler, null, RasterizerState.CullNone);
            sb.Draw(src, dest, Color.White);
            sb.End();
        }

        private SpriteBatch presentBatch;
        private SpriteBatch PresentBatch => presentBatch ??= new SpriteBatch(GraphicsDevice);

        private Rectangle Letterbox(int w, int h)
        {
            var vp = GraphicsDevice.PresentationParameters;
            int W = vp.BackBufferWidth, H = vp.BackBufferHeight;
            float s = Math.Min(W / (float)w, H / (float)h);
            int dw = (int)(w * s), dh = (int)(h * s);
            return new Rectangle((W - dw) / 2, (H - dh) / 2, dw, dh);
        }

        /// <summary>Picks the render scale that fills the window (integer multiple of 640x360).</summary>
        private void UpdateRenderScale()
        {
            windowDirty = false;
            var pp = GraphicsDevice.PresentationParameters;
            int W = Math.Max(1, pp.BackBufferWidth), H = Math.Max(1, pp.BackBufferHeight);
            float fit = Math.Min(W / (float)CompatHost.NativeWidth, H / (float)CompatHost.NativeHeight);
            int scale = settings.IntegerScaling ? Math.Max(1, (int)Math.Floor(fit)) : Math.Max(1, (int)Math.Ceiling(fit));
            CompatHost.CreateDeviceResources(Math.Min(scale, 8));
        }

        private void ApplyWindowSettings()
        {
            graphics.PreferredBackBufferWidth = CompatHost.NativeWidth * settings.WindowScale;
            graphics.PreferredBackBufferHeight = CompatHost.NativeHeight * settings.WindowScale;
            graphics.IsFullScreen = settings.Fullscreen;
            graphics.SynchronizeWithVerticalRetrace = settings.VSync;
            graphics.ApplyChanges();
            windowDirty = true;
        }

        private void ApplyAudioSettings()
        {
            CompatHost.MusicVolume = settings.MusicVolume / 10f;
            CompatHost.SoundVolume = settings.SoundVolume / 10f;
        }

        protected override void OnExiting(object sender, EventArgs args)
        {
            if (guest != null)
                Log.Write($"Session end: frame {frameCounter}, random draws {DetRandom.DrawCount}, stream hash {DetRandom.StreamHash:X16}");
            settings.Save(settingsPath);
            net?.Dispose();
            try { guest?.HostUnload(); } catch (Exception ex) { Log.Write("Unload: " + ex.Message); }
            base.OnExiting(sender, args);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                TextInputEXT.TextInput -= OnTextInput;
                ui?.Dispose();
                presentBatch?.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
