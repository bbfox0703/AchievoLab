using System;
using AnSAM.Steam;
using Microsoft.UI.Xaml;
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
            ApplicationInitializationHelper.InitializeLogging("AnSAM");
            ApplicationInitializationHelper.EnsureConfigurationFile("AnSAM", DefaultConfigurations.AnSAM);

            // CRITICAL: Register global exception handlers to catch crashes that escape try-catch blocks
            // These handlers ensure stack traces are logged even for background thread exceptions
            ExceptionHandlingHelper.RegisterGlobalExceptionHandlers(this);
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
