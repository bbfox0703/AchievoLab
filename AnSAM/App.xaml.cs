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
using Serilog;
using Microsoft.Extensions.Configuration;

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
            InitializeLogging();
            EnsureConfigurationFile();

            // CRITICAL: Register global exception handlers to catch crashes that escape try-catch blocks
            // These handlers ensure stack traces are logged even for background thread exceptions
            UnhandledException += OnUnhandledException;
            AppDomain.CurrentDomain.UnhandledException += OnAppDomainUnhandledException;
            TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
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
                AppLogger.LogInfo("AnSAM application logging initialized");
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
                var configManager = new ConfigurationFileManager(configPath, DefaultConfigurations.AnSAM);

                if (configManager.EnsureConfigurationExists())
                {
                    AppLogger.LogDebug("AnSAM: Configuration file was created or updated");
                }
            }
            catch (Exception ex)
            {
                AppLogger.LogDebug($"AnSAM: Failed to ensure configuration file: {ex.Message}");
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
            AppLogger.LogDebug("=== UNHANDLED EXCEPTION (UI Thread) ===");
            AppLogger.LogDebug($"Exception Type: {e.Exception.GetType().FullName}");
            AppLogger.LogDebug($"Message: {e.Exception.Message}");
            AppLogger.LogDebug($"Stack Trace: {e.Exception.StackTrace}");

            if (e.Exception.InnerException != null)
            {
                AppLogger.LogDebug($"Inner Exception: {e.Exception.InnerException.GetType().FullName}");
                AppLogger.LogDebug($"Inner Message: {e.Exception.InnerException.Message}");
                AppLogger.LogDebug($"Inner Stack Trace: {e.Exception.InnerException.StackTrace}");
            }

            AppLogger.LogDebug("=== END UNHANDLED EXCEPTION ===");

            // Mark as handled to prevent crash (for debugging purposes)
            // Remove this line if you want the app to crash and show the error
            e.Handled = true;
        }

        /// <summary>
        /// Handles unhandled exceptions in any AppDomain thread (background threads).
        /// </summary>
        private void OnAppDomainUnhandledException(object sender, System.UnhandledExceptionEventArgs e)
        {
            AppLogger.LogDebug("=== APPDOMAIN UNHANDLED EXCEPTION (Background Thread) ===");

            if (e.ExceptionObject is Exception ex)
            {
                AppLogger.LogDebug($"Exception Type: {ex.GetType().FullName}");
                AppLogger.LogDebug($"Message: {ex.Message}");
                AppLogger.LogDebug($"Stack Trace: {ex.StackTrace}");

                if (ex.InnerException != null)
                {
                    AppLogger.LogDebug($"Inner Exception: {ex.InnerException.GetType().FullName}");
                    AppLogger.LogDebug($"Inner Message: {ex.InnerException.Message}");
                    AppLogger.LogDebug($"Inner Stack Trace: {ex.InnerException.StackTrace}");
                }
            }
            else
            {
                AppLogger.LogDebug($"Non-Exception object thrown: {e.ExceptionObject}");
            }

            AppLogger.LogDebug($"Is Terminating: {e.IsTerminating}");
            AppLogger.LogDebug("=== END APPDOMAIN UNHANDLED EXCEPTION ===");
        }

        /// <summary>
        /// Handles unobserved exceptions in fire-and-forget Tasks.
        /// </summary>
        private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
        {
            AppLogger.LogDebug("=== UNOBSERVED TASK EXCEPTION (Fire-and-forget) ===");
            AppLogger.LogDebug($"Exception Type: {e.Exception.GetType().FullName}");
            AppLogger.LogDebug($"Message: {e.Exception.Message}");

            foreach (var ex in e.Exception.InnerExceptions)
            {
                AppLogger.LogDebug($"  Inner Exception: {ex.GetType().FullName}");
                AppLogger.LogDebug($"  Inner Message: {ex.Message}");
                AppLogger.LogDebug($"  Inner Stack Trace: {ex.StackTrace}");
            }

            AppLogger.LogDebug("=== END UNOBSERVED TASK EXCEPTION ===");

            // Mark as observed to prevent crash during GC
            e.SetObserved();
        }
    }
}
