using Microsoft.UI.Xaml;
using System;
using System.IO;
using Windows.Storage;
using CommonUtilities;

namespace RunGame
{
    public partial class App : Application
    {
        internal static ApplicationDataContainer? LocalSettings { get; private set; }

        internal static ApplicationTheme ToApplicationTheme(ElementTheme theme)
        {
            var resolved = theme == ElementTheme.Default ? GetSystemTheme() : theme;
            return resolved == ElementTheme.Dark ? ApplicationTheme.Dark : ApplicationTheme.Light;
        }

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

        public App()
        {
            this.InitializeComponent();
            EnsureConfigurationFile();
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
                    DebugLogger.LogDebug("RunGame: Configuration file was created or updated");
                }
            }
            catch (Exception ex)
            {
                DebugLogger.LogDebug($"RunGame: Failed to ensure configuration file: {ex.Message}");
                // Don't throw - allow app to continue with defaults
            }
        }

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

        private Window? m_window;
    }
}