﻿using SS14.Client.Graphics;
using SS14.Client.Graphics.Render;
using SS14.Client.Graphics.Input;
using SS14.Client.Interfaces.Input;
using SS14.Client.Interfaces.Network;
using SS14.Client.Interfaces.Resource;
using SS14.Client.Interfaces.State;
using SS14.Client.Interfaces.UserInterface;
using SS14.Client.Interfaces;
using SS14.Client.State.States;
using SS14.Shared.Interfaces.Configuration;
using SS14.Shared.Interfaces.Map;
using SS14.Shared.Interfaces.Serialization;
using SS14.Shared.Configuration;
using SS14.Shared.IoC;
using SS14.Shared.Log;
using SS14.Shared.Prototypes;
using System;
using System.Diagnostics;
using System.IO;
using SS14.Client.Console;
using SS14.Shared.ContentPack;
using SS14.Shared.Interfaces;
using SS14.Shared.Interfaces.Network;
using SS14.Shared.Interfaces.Timing;
using SS14.Shared.Interfaces.Timers;
using SS14.Shared.Network.Messages;
using SS14.Client.Interfaces.GameObjects;
using SS14.Client.Interfaces.GameStates;
using SS14.Shared.Maths;
using SS14.Client.Graphics.Lighting;
using SS14.Client.Interfaces.Placement;
using SS14.Shared.Interfaces.GameObjects;
using SS14.Shared.Timing;
using FrameEventArgs = SS14.Client.Graphics.FrameEventArgs;

namespace SS14.Client
{
    public class GameController : IGameController
    {
        #region Fields

        [Dependency]
        readonly private IConfigurationManager _configurationManager;
        [Dependency]
        readonly private INetworkGrapher _netGrapher;
        [Dependency]
        readonly private IClientNetManager _networkManager;
        [Dependency]
        readonly private IStateManager _stateManager;
        [Dependency]
        readonly private IUserInterfaceManager _userInterfaceManager;
        [Dependency]
        readonly private IResourceCache _resourceCache;
        [Dependency]
        readonly private ITileDefinitionManager _tileDefinitionManager;
        [Dependency]
        readonly private ISS14Serializer _serializer;
        [Dependency]
        private readonly IGameTiming _time;
        [Dependency]
        private readonly IResourceManager _resourceManager;
        [Dependency]
        private readonly IMapManager _mapManager;
        [Dependency]
        private readonly IPlacementManager _placementManager;
        [Dependency]
        private readonly IBaseClient _client;
        [Dependency]
        private readonly IClientChatConsole _console;
        [Dependency]
        private readonly ITimerManager _timerManager;
        [Dependency]
        private readonly IClientEntityManager _entityManager;

        #endregion Fields

        #region Methods

        #region Constructors

        private GameLoop _mainLoop;
        
        public void Run()
        {
            Logger.Debug("Initializing GameController.");

            _configurationManager.LoadFromFile(PathHelpers.ExecutableRelativeFile("client_config.toml"));

            _resourceCache.LoadBaseResources();
            // Load resources used by splash screen and main menu.
            LoadSplashResources();
            ShowSplashScreen();

            _resourceCache.LoadLocalResources();
            
            //identical code for server in baseserver
            if (!AssemblyLoader.TryLoadAssembly<GameShared>(_resourceManager, $"Content.Shared"))
                if (!AssemblyLoader.TryLoadAssembly<GameShared>(_resourceManager, $"Sandbox.Shared"))
                    Logger.Warning($"[ENG] Could not load any Shared DLL.");

            if (!AssemblyLoader.TryLoadAssembly<GameClient>(_resourceManager, $"Content.Client"))
                if (!AssemblyLoader.TryLoadAssembly<GameClient>(_resourceManager, $"Sandbox.Client"))
                    Logger.Warning($"[ENG] Could not load any Client DLL.");

            IoCManager.Resolve<ILightManager>().Initialize();

            // Call Init in game assemblies.
            AssemblyLoader.BroadcastRunLevel(AssemblyLoader.RunLevel.Init);

            IoCManager.Resolve<IEntitySystemManager>().Initialize();

            //Setup Cluwne first, as the rest depends on it.
            SetupCluwne();
            CleanupSplashScreen();

            //Initialization of private members
            _tileDefinitionManager.InitializeResources();

            _serializer.Initialize();
            var prototypeManager = IoCManager.Resolve<IPrototypeManager>();
            prototypeManager.LoadDirectory(@"Prototypes");
            prototypeManager.Resync();
            _networkManager.Initialize(false);
            _console.Initialize();
            _netGrapher.Initialize();
            _userInterfaceManager.Initialize();
            _mapManager.Initialize();
            _placementManager.Initialize();
            _entityManager.Initialize();

            _networkManager.RegisterNetMessage<MsgFullState>(MsgFullState.NAME, message => IoCManager.Resolve<IGameStateManager>().HandleFullStateMessage((MsgFullState)message));
            _networkManager.RegisterNetMessage<MsgStateUpdate>(MsgStateUpdate.NAME, message => IoCManager.Resolve<IGameStateManager>().HandleStateUpdateMessage((MsgStateUpdate)message));

            _client.Initialize();

            _stateManager.RequestStateChange<MainScreen>();

            _mainLoop = new GameLoop(_time);
            _mainLoop.SleepMode = SleepMode.None;

            _mainLoop.Input += (sender, args) => Process(new FrameEventArgs(args.DeltaSeconds));
            _mainLoop.Tick += (sender, args) => Update(new FrameEventArgs(args.DeltaSeconds));
            _mainLoop.Render += (sender, args) => Render(new FrameEventArgs(args.DeltaSeconds));

            _mainLoop.Run();

            _networkManager.ClientDisconnect("Client disconnected from game.");
            CluwneLib.Terminate();
            Logger.Info("GameController terminated.");

            IoCManager.Resolve<IConfigurationManager>().SaveToFile();
        }

        /// <summary>
        /// Processes all simulation I/O. Keyboard/Mouse/Network code gets called here.
        /// </summary>
        private void Process(FrameEventArgs e)
        {
        }

        /// <summary>
        /// Runs a tick of the simulation.
        /// </summary>
        /// <param name="e">Current GameTiming.FrameTime</param>
        private void Update(FrameEventArgs e)
        {
            _networkManager.ProcessPackets();
            CluwneLib.RunIdle(this, e);
            AssemblyLoader.BroadcastUpdate(AssemblyLoader.UpdateLevel.PreEngine, e.Elapsed);
            _timerManager.UpdateTimers(e.Elapsed);
            _stateManager.Update(e);
            AssemblyLoader.BroadcastUpdate(AssemblyLoader.UpdateLevel.PostEngine, e.Elapsed);

            if (!CluwneLib.IsRunning)
                _mainLoop.Running = false;
        }

        /// <summary>
        /// Renders the view of the simulation.
        /// </summary>
        /// <param name="e">Current GameTiming.RealFrameTime</param>
        private void Render(FrameEventArgs e)
        {
            CluwneLib.ClearCurrentRendertarget(Color.Black);
            CluwneLib.Window.DispatchEvents();

            // draw everything
            _stateManager.Render(e);

            // interface runs in realtime, so it is updated here
            _userInterfaceManager.Update(e);

            _userInterfaceManager.Render(e);

            _netGrapher.Update();

            // swap buffers to show the screen
            CluwneLib.Window.Graphics.Display();
        }

        private void LoadSplashResources()
        {
            var logoTexture = _resourceCache.LoadTextureFrom("ss14_logo", _resourceManager.ContentFileRead(@"Textures/Logo/logo.png"));
            _resourceCache.LoadSpriteFromTexture("ss14_logo", logoTexture);

            var backgroundTexture = _resourceCache.LoadTextureFrom("ss14_logo_background", _resourceManager.ContentFileRead(@"Textures/Logo/background.png"));
            _resourceCache.LoadSpriteFromTexture("ss14_logo_background", backgroundTexture);

            var nanotrasenTexture = _resourceCache.LoadTextureFrom("ss14_logo_nt", _resourceManager.ContentFileRead(@"Textures/Logo/nanotrasen.png"));
            _resourceCache.LoadSpriteFromTexture("ss14_logo_nt", nanotrasenTexture);
        }

        [Conditional("RELEASE")]
        private void ShowSplashScreen()
        {
            // Do nothing when we're on DEBUG builds.
            // The splash is just annoying.
            const int SIZE_X = 600;
            const int SIZE_Y = 300;
            var Size = new Vector2i(SIZE_X, SIZE_Y);
            // Size of the NT logo in the bottom right.
            const float NT_SIZE_X = SIZE_X / 10f;
            const float NT_SIZE_Y = SIZE_Y / 10f;
            var NTSize = new Vector2(NT_SIZE_X, NT_SIZE_Y);
            var window = CluwneLib.ShowSplashScreen(new VideoMode(SIZE_X, SIZE_Y)).Graphics;

            var logo = _resourceCache.GetSprite("ss14_logo");
            logo.Position = Size / 2 - logo.TextureRect.Size / 2;

            var background = _resourceCache.GetSprite("ss14_logo_background");
            background.Scale = (Vector2)Size / background.TextureRect.Size;

            var nanotrasen = _resourceCache.GetSprite("ss14_logo_nt");
            nanotrasen.Scale = NTSize / nanotrasen.TextureRect.Size;
            nanotrasen.Position = Size - NTSize - 5;
            nanotrasen.Color = Color.White.WithAlpha(64);

            window.Draw(background);
            window.Draw(logo);
            window.Draw(nanotrasen);
            window.Display();
        }

        [Conditional("RELEASE")]
        private void CleanupSplashScreen()
        {
            CluwneLib.CleanupSplashScreen();
        }

        #endregion Constructors

        #region EventHandlers

        private void MainWindowResizeEnd(object sender, SizeEventArgs e)
        {
            _stateManager.FormResize();
            // Not sure if changing the view manually is needed.
            // Better safe than sorry.
            CluwneLib.Window.View = new Graphics.Views.View(
               Box2.FromDimensions(0, 0, CluwneLib.Window.Size.X, CluwneLib.Window.Size.Y));
        }
        private void MainWindowRequestClose(object sender, EventArgs e)
        {
            CluwneLib.Stop();
        }

        #region Input Handling

        /// <summary>
        /// Handles any keydown events.
        /// </summary>
        /// <param name="sender">The source of the event.</param>
        /// <param name="e">The KeyArgsinstance containing the event data.</param>
        private void KeyDownEvent(object sender, KeyEventArgs e)
        {
            _stateManager?.KeyDown(e);

            switch (e.Key)
            {
                case Keyboard.Key.F3:
                    IoCManager.Resolve<INetworkGrapher>().Toggle();
                    break;
            }
        }

        /// <summary>
        /// Handles any keyup events.
        /// </summary>
        /// <param name="sender">The source of the event.</param>
        /// <param name="e">The KeyArgs instance containing the event data.</param>
        private void KeyUpEvent(object sender, KeyEventArgs e)
        {
            _stateManager?.KeyUp(e);
        }

        /// <summary>
        /// Handles mouse wheel input.
        /// </summary>
        /// <param name="sender">The source of the event.</param>
        /// <param name="e">The MouseWheelEventArgs instance containing the event data.</param>
        private void MouseWheelMoveEvent(object sender, MouseWheelScrollEventArgs e)
        {
            _stateManager?.MouseWheelMove(e);
        }

        /// <summary>
        /// Handles any mouse input.
        /// </summary>
        /// <param name="sender">The source of the event.</param>
        /// <param name="e">The MouseMoveEventArgs instance containing the event data.</param>
        private void MouseMoveEvent(object sender, MouseMoveEventArgs e)
        {
            _stateManager?.MouseMove(e);
        }

        /// <summary>
        /// Handles any mouse input.
        /// </summary>
        /// <param name="sender">The source of the event.</param>
        /// <param name="e">The MouseButtonEventArgs instance containing the event data.</param>
        private void MouseDownEvent(object sender, MouseButtonEventArgs e)
        {
            _stateManager?.MouseDown(e);
        }

        /// <summary>
        /// Handles any mouse input.
        /// </summary>
        /// <param name="sender">The source of the event.</param>
        /// <param name="e">The MouseButtonEventArgs instance containing the event data.</param>
        private void MouseUpEvent(object sender, MouseButtonEventArgs e)
        {
            _stateManager?.MouseUp(e);
        }

        /// <summary>
        /// Handles any mouse input.
        /// </summary>
        /// <param name="sender">The source of the event.</param>
        /// <param name="e">The EventArgs instance containing the event data.</param>
        private void MouseEntered(object sender, EventArgs e)
        {
            _stateManager?.MouseEntered(e);
        }

        /// <summary>
        /// Handles any mouse input.
        /// </summary>
        /// <param name="sender">The source of the event.</param>
        /// <param name="e">The EventArgs instance containing the event data.</param>
        private void MouseLeft(object sender, EventArgs e)
        {
            _stateManager?.MouseLeft(e);
        }

        private void TextEntered(object sender, TextEventArgs e)
        {
            _stateManager?.TextEntered(e);
        }

        #endregion Input Handling

        #endregion EventHandlers

        #region Privates

        bool onetime = true;

        private void SetupCluwne()
        {
            _configurationManager.RegisterCVar("display.width", 1280, CVar.ARCHIVE);
            _configurationManager.RegisterCVar("display.height", 720, CVar.ARCHIVE);
            _configurationManager.RegisterCVar("display.fullscreen", false, CVar.ARCHIVE);
            _configurationManager.RegisterCVar("display.refresh", 60, CVar.ARCHIVE);
            _configurationManager.RegisterCVar("display.vsync", false, CVar.ARCHIVE);

            uint displayWidth = (uint)_configurationManager.GetCVar<int>("display.width");
            uint displayHeight = (uint)_configurationManager.GetCVar<int>("display.height");
            bool isFullscreen = _configurationManager.GetCVar<bool>("display.fullscreen");
            uint refresh = (uint)_configurationManager.GetCVar<int>("display.refresh");

            CluwneLib.Video.SetFullScreen(isFullscreen);
            CluwneLib.Video.SetRefreshRate(refresh);
            CluwneLib.Video.SetWindowSize(displayWidth, displayHeight);
            CluwneLib.Initialize();
            if (onetime)
            {
                //every time the video settings change we close the old screen and create a new one
                //SetupCluwne Gets called to reset the event handlers to the new screen
                CluwneLib.RefreshVideoSettings += SetupCluwne;
                onetime = false;
            }
            CluwneLib.Window.SetMouseCursorVisible(false);
            CluwneLib.Window.Graphics.BackgroundColor = Color.Black;
            CluwneLib.Window.Resized += MainWindowResizeEnd;
            CluwneLib.Window.Closed += MainWindowRequestClose;
            CluwneLib.Input.KeyPressed += KeyDownEvent;
            CluwneLib.Input.KeyReleased += KeyUpEvent;
            CluwneLib.Input.MouseButtonPressed += MouseDownEvent;
            CluwneLib.Input.MouseButtonReleased += MouseUpEvent;
            CluwneLib.Input.MouseMoved += MouseMoveEvent;
            CluwneLib.Input.MouseWheelMoved += MouseWheelMoveEvent;
            CluwneLib.Input.MouseEntered += MouseEntered;
            CluwneLib.Input.MouseLeft += MouseLeft;
            CluwneLib.Input.TextEntered += TextEntered;

            CluwneLib.Go();
            IoCManager.Resolve<IKeyBindingManager>().Initialize();
        }

        #endregion Privates

        #endregion Methods
    }
}
