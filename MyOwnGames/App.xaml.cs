using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.UI.Xaml.Shapes;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Runtime.CompilerServices;
using Windows.ApplicationModel;
using Windows.ApplicationModel.Activation;
using Windows.Foundation;
using Windows.Foundation.Collections;
using CommonUtilities;
using Serilog;
using Microsoft.Extensions.Configuration;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace MyOwnGames
{
    /// <summary>
    /// Provides application-specific behavior to supplement the default Application class.
    /// </summary>
    public partial class App : Application
    {
        private Window? _window;

        /// <summary>
        /// Initializes the singleton application object.  This is the first line of authored code
        /// executed, and as such is the logical equivalent of main() or WinMain().
        /// </summary>
        public App()
        {
            InitializeComponent();
            InitializeLogging();
            EnsureConfigurationFile();
            AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
            UnhandledException += OnUnhandledException;
        }

        /// <summary>
        /// Initializes Serilog logging from appsettings.json
        /// </summary>
        private static void InitializeLogging()
        {
            try
            {
                var configuration = new ConfigurationBuilder()
                    .SetBasePath(AppContext.BaseDirectory)
                    .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
                    .Build();

                Log.Logger = new LoggerConfiguration()
                    .ReadFrom.Configuration(configuration)
                    .CreateLogger();

                AppLogger.Initialize(Log.Logger);
                AppLogger.LogInfo("MyOwnGames application logging initialized");
            }
            catch (Exception ex)
            {
                // Fallback to console if logging initialization fails
                Console.WriteLine($"Failed to initialize logging: {ex.Message}");
            }
        }

        /// <summary>
        /// Ensures appsettings.json exists with all required parameters.
        /// If the file doesn't exist or is incomplete, creates/updates it with defaults.
        /// </summary>
        private static void EnsureConfigurationFile()
        {
            try
            {
                var configPath = System.IO.Path.Combine(AppContext.BaseDirectory, "appsettings.json");
                var configManager = new ConfigurationFileManager(configPath, DefaultConfigurations.MyOwnGames);

                if (configManager.EnsureConfigurationExists())
                {
                    AppLogger.LogDebug("MyOwnGames: Configuration file was created or updated");
                }
            }
            catch (Exception ex)
            {
                AppLogger.LogDebug($"MyOwnGames: Failed to ensure configuration file: {ex.Message}");
                // Don't throw - allow app to continue with defaults
            }
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

        private void OnProcessExit(object? sender, EventArgs e)
        {
            if (_window is MainWindow mw)
            {
                AppLogger.LogDebug("Process exiting");
                mw.SaveAndDisposeAsync("process exit").GetAwaiter().GetResult();
            }
        }

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
