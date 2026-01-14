using Microsoft.UI.Xaml;
using System;
using System.IO;
using Windows.Storage;
using CommonUtilities;
using Serilog;
using Microsoft.Extensions.Configuration;

namespace RunGame
{
    /// <summary>
    /// Provides application-specific behavior for the RunGame achievement manager.
    /// Handles command-line parsing for AppID, theme management, and application initialization.
    /// </summary>
    public partial class App : Application
    {
        /// <summary>
        /// Gets the application-specific local settings container.
        /// May be null if LocalSettings access fails during initialization.
        /// </summary>
        internal static ApplicationDataContainer? LocalSettings { get; private set; }

        /// <summary>
        /// Converts a WinUI ElementTheme to a WinUI ApplicationTheme.
        /// Resolves ElementTheme.Default to the current system theme.
        /// </summary>
        /// <param name="theme">The ElementTheme to convert.</param>
        /// <returns>The corresponding ApplicationTheme (Light or Dark).</returns>
        internal static ApplicationTheme ToApplicationTheme(ElementTheme theme)
        {
            var resolved = theme == ElementTheme.Default ? GetSystemTheme() : theme;
            return resolved == ElementTheme.Dark ? ApplicationTheme.Dark : ApplicationTheme.Light;
        }

        /// <summary>
        /// Retrieves the current system theme preference from the Windows registry.
        /// </summary>
        /// <returns>ElementTheme.Dark if system uses dark theme, otherwise ElementTheme.Light.</returns>
        private static ElementTheme GetSystemTheme()
        {
            try
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
                var value = key?.GetValue("AppsUseLightTheme");
                if (value is int i)
                {
                    return i != 0 ? ElementTheme.Light : ElementTheme.Dark;
                }
            }
            catch
            {
                // Fall back to light theme if we can't read the registry
            }
            return ElementTheme.Light;
        }

        /// <summary>
        /// Initializes the singleton application object.
        /// Sets up logging and ensures configuration file exists with defaults.
        /// </summary>
        public App()
        {
            this.InitializeComponent();
            InitializeLogging();
            EnsureConfigurationFile();
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
                AppLogger.LogInfo("RunGame application logging initialized");
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
                var configPath = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
                var configManager = new ConfigurationFileManager(configPath, DefaultConfigurations.RunGame);

                if (configManager.EnsureConfigurationExists())
                {
                    AppLogger.LogDebug("RunGame: Configuration file was created or updated");
                }
            }
            catch (Exception ex)
            {
                AppLogger.LogDebug($"RunGame: Failed to ensure configuration file: {ex.Message}");
                // Don't throw - allow app to continue with defaults
            }
        }

        /// <summary>
        /// Invoked when the application is launched.
        /// Validates command-line arguments for AppID parameter and creates the main window.
        /// Displays an error dialog and exits if launched without proper parameters.
        /// </summary>
        /// <param name="args">Details about the launch request and process.</param>
        protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
        {
            try
            {
                LocalSettings = ApplicationData.Current.LocalSettings;
            }
            catch (InvalidOperationException ex)
            {
                System.Diagnostics.Debug.WriteLine($"InvalidOperationException in LocalSettings access: {ex.Message}");
                LocalSettings = null;
            }
            catch (System.IO.IOException ex)
            {
                System.Diagnostics.Debug.WriteLine($"IOException in LocalSettings access: {ex.Message}");
                LocalSettings = null;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Exception in LocalSettings access: {ex.Message}");
                LocalSettings = null;
            }

            var cmdArgs = Environment.GetCommandLineArgs();
            
            if (cmdArgs.Length < 2)
            {
                // Show error dialog and exit
                var dialog = new Windows.UI.Popups.MessageDialog(
                    "This application cannot be run directly.\n\n" +
                    "RunGame is a companion application that must be launched from AnSAM " +
                    "(the main game library interface) by double-clicking a game.\n\n" +
                    "Please launch AnSAM instead to manage your Steam achievements and statistics.",
                    "RunGame - Invalid Launch Method");
                _ = dialog.ShowAsync();
                Exit();
                return;
            }

            if (!long.TryParse(cmdArgs[1], out long gameId))
            {
                var dialog = new Windows.UI.Popups.MessageDialog(
                    $"Invalid game ID parameter: \"{cmdArgs[1]}\"\n\n" +
                    "RunGame requires a valid Steam AppID as a command line parameter.\n\n" +
                    "This application is designed to be launched from AnSAM. " +
                    "Please use AnSAM to open games instead of running RunGame directly.",
                    "RunGame - Invalid Parameter");
                _ = dialog.ShowAsync();
                Exit();
                return;
            }

            m_window = new MainWindow(gameId);
            m_window.Activate();
        }

        /// <summary>
        /// The main application window instance.
        /// </summary>
        private Window? m_window;
    }
}
