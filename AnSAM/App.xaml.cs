using System;
using System.IO;
using AnSAM.Steam;
using Microsoft.UI.Xaml;
using Microsoft.Win32;
using Windows.Storage;
using Windows.ApplicationModel;
using Windows.ApplicationModel.Activation;
using CommonUtilities;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace AnSAM
{
    /// <summary>
    /// Provides application-specific behavior to supplement the default Application class.
    /// </summary>
    public partial class App : Application
    {
        private Window? _window;
        private SteamClient? _steamClient;

        /// <summary>
        /// Initializes the singleton application object.  This is the first line of authored code
        /// executed, and as such is the logical equivalent of main() or WinMain().
        /// </summary>
        public App()
        {
            InitializeComponent();
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
                var configManager = new ConfigurationFileManager(configPath, DefaultConfigurations.AnSAM);

                if (configManager.EnsureConfigurationExists())
                {
                    DebugLogger.LogDebug("AnSAM: Configuration file was created or updated");
                }
            }
            catch (Exception ex)
            {
                DebugLogger.LogDebug($"AnSAM: Failed to ensure configuration file: {ex.Message}");
                // Don't throw - allow app to continue with defaults
            }
        }

        /// <summary>
        /// Invoked when the application is launched.
        /// </summary>
        /// <param name="args">Details about the launch request and process.</param>
        protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
        {
            var theme = LoadThemePreference() ?? ThemeManagementService.GetSystemTheme();
            
            // Don't set Application.RequestedTheme to avoid COMException
            // Theme will be applied at MainWindow level instead
            
            _steamClient = new SteamClient();
            _window = new MainWindow(_steamClient, theme);
            _window.Closed += (_, _) => _steamClient?.Dispose();
            _window.Activate();
        }

        /// <summary>
        /// Attempts to load a previously saved theme preference from local settings.
        /// </summary>
        private static ElementTheme? LoadThemePreference()
        {
            var settingsService = new ApplicationSettingsService();
            if (settingsService.TryGetEnum("AppTheme", out ElementTheme savedTheme))
            {
                return savedTheme;
            }
            return null;
        }
    }
}
