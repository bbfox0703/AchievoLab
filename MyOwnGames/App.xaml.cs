using Microsoft.UI.Xaml;
using System;
using CommonUtilities;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace MyOwnGames
{
    /// <summary>
    /// Provides application-specific behavior to supplement the default Application class.
    /// </summary>
    public partial class App : Application
    {
        /// <summary>
        /// The main application window instance.
        /// </summary>
        private Window? _window;

        /// <summary>
        /// Initializes the singleton application object.  This is the first line of authored code
        /// executed, and as such is the logical equivalent of main() or WinMain().
        /// </summary>
        public App()
        {
            InitializeComponent();
            ApplicationInitializationHelper.InitializeLogging("MyOwnGames");
            ApplicationInitializationHelper.EnsureConfigurationFile("MyOwnGames", DefaultConfigurations.MyOwnGames);
            AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
            UnhandledException += OnUnhandledException;
        }

        /// <summary>
        /// Invoked when the application is launched.
        /// </summary>
        /// <param name="args">Details about the launch request and process.</param>
        protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
        {
            _window = new MainWindow();
            _window.Activate();
        }

        /// <summary>
        /// Handles the ProcessExit event by saving application state and disposing resources.
        /// </summary>
        /// <param name="sender">The source of the event.</param>
        /// <param name="e">Event data.</param>
        private void OnProcessExit(object? sender, EventArgs e)
        {
            if (_window is MainWindow mw)
            {
                AppLogger.LogDebug("Process exiting");
                mw.SaveAndDisposeAsync("process exit").GetAwaiter().GetResult();
            }
        }

        /// <summary>
        /// Handles unhandled exceptions by logging and attempting to save application state.
        /// </summary>
        /// <param name="sender">The source of the event.</param>
        /// <param name="e">Event data containing the unhandled exception.</param>
        private async void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
        {
            AppLogger.LogDebug($"Unhandled exception: {e.Exception.Message}");
            if (_window is MainWindow mw)
            {
                await mw.SaveAndDisposeAsync("unhandled exception");
            }
        }
    }
}
