using System;
using System.IO;
using System.Threading.Tasks;
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

            // CRITICAL: Register global exception handlers to catch crashes that escape try-catch blocks
            // These handlers ensure stack traces are logged even for background thread exceptions
            UnhandledException += OnUnhandledException;
            AppDomain.CurrentDomain.UnhandledException += OnAppDomainUnhandledException;
            TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
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

        /// <summary>
        /// Handles unhandled exceptions in the WinUI 3 application (UI thread).
        /// </summary>
        private void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
        {
            DebugLogger.LogDebug("=== UNHANDLED EXCEPTION (UI Thread) ===");
            DebugLogger.LogDebug($"Exception Type: {e.Exception.GetType().FullName}");
            DebugLogger.LogDebug($"Message: {e.Exception.Message}");
            DebugLogger.LogDebug($"Stack Trace: {e.Exception.StackTrace}");

            if (e.Exception.InnerException != null)
            {
                DebugLogger.LogDebug($"Inner Exception: {e.Exception.InnerException.GetType().FullName}");
                DebugLogger.LogDebug($"Inner Message: {e.Exception.InnerException.Message}");
                DebugLogger.LogDebug($"Inner Stack Trace: {e.Exception.InnerException.StackTrace}");
            }

            DebugLogger.LogDebug("=== END UNHANDLED EXCEPTION ===");

            // Mark as handled to prevent crash (for debugging purposes)
            // Remove this line if you want the app to crash and show the error
            e.Handled = true;
        }

        /// <summary>
        /// Handles unhandled exceptions in any AppDomain thread (background threads).
        /// </summary>
        private void OnAppDomainUnhandledException(object sender, System.UnhandledExceptionEventArgs e)
        {
            DebugLogger.LogDebug("=== APPDOMAIN UNHANDLED EXCEPTION (Background Thread) ===");

            if (e.ExceptionObject is Exception ex)
            {
                DebugLogger.LogDebug($"Exception Type: {ex.GetType().FullName}");
                DebugLogger.LogDebug($"Message: {ex.Message}");
                DebugLogger.LogDebug($"Stack Trace: {ex.StackTrace}");

                if (ex.InnerException != null)
                {
                    DebugLogger.LogDebug($"Inner Exception: {ex.InnerException.GetType().FullName}");
                    DebugLogger.LogDebug($"Inner Message: {ex.InnerException.Message}");
                    DebugLogger.LogDebug($"Inner Stack Trace: {ex.InnerException.StackTrace}");
                }
            }
            else
            {
                DebugLogger.LogDebug($"Non-Exception object thrown: {e.ExceptionObject}");
            }

            DebugLogger.LogDebug($"Is Terminating: {e.IsTerminating}");
            DebugLogger.LogDebug("=== END APPDOMAIN UNHANDLED EXCEPTION ===");
        }

        /// <summary>
        /// Handles unobserved exceptions in fire-and-forget Tasks.
        /// </summary>
        private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
        {
            DebugLogger.LogDebug("=== UNOBSERVED TASK EXCEPTION (Fire-and-forget) ===");
            DebugLogger.LogDebug($"Exception Type: {e.Exception.GetType().FullName}");
            DebugLogger.LogDebug($"Message: {e.Exception.Message}");

            foreach (var ex in e.Exception.InnerExceptions)
            {
                DebugLogger.LogDebug($"  Inner Exception: {ex.GetType().FullName}");
                DebugLogger.LogDebug($"  Inner Message: {ex.Message}");
                DebugLogger.LogDebug($"  Inner Stack Trace: {ex.StackTrace}");
            }

            DebugLogger.LogDebug("=== END UNOBSERVED TASK EXCEPTION ===");

            // Mark as observed to prevent crash during GC
            e.SetObserved();
        }
    }
}
