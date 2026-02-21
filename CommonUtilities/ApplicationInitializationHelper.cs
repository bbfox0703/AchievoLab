using System;
using System.IO;
using Serilog;
using Serilog.Events;

namespace CommonUtilities
{
    /// <summary>
    /// Provides unified application initialization helpers for logging and configuration.
    /// Shared across AnSAM, RunGame, and MyOwnGames to eliminate duplicate initialization code.
    /// </summary>
    public static class ApplicationInitializationHelper
    {
        private const string OutputTemplate =
            "[{Timestamp:yyyy-MM-dd HH:mm:ss.fff}] [{Level:u3}] {Message:lj}{NewLine}{Exception}";

        /// <summary>
        /// Initializes Serilog logging with code-based configuration (AOT-compatible).
        /// Uses programmatic setup instead of ReadFrom.Configuration() which requires reflection.
        /// </summary>
        /// <param name="appName">The application name for logging identification (e.g., "AnSAM", "RunGame", "MyOwnGames").</param>
        public static void InitializeLogging(string appName)
        {
            try
            {
                var logDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "AchievoLab", "logs");
                Directory.CreateDirectory(logDir);

                var logPath = Path.Combine(logDir, $"{appName.ToLowerInvariant()}-.log");

                Log.Logger = new LoggerConfiguration()
                    .MinimumLevel.Debug()
                    .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
                    .MinimumLevel.Override("System", LogEventLevel.Warning)
                    .WriteTo.Console(outputTemplate: OutputTemplate)
                    .WriteTo.Debug(outputTemplate: OutputTemplate)
                    .WriteTo.File(logPath,
                        rollingInterval: RollingInterval.Day,
                        retainedFileCountLimit: 7,
                        shared: true,
                        outputTemplate: OutputTemplate)
                    .Enrich.FromLogContext()
                    .CreateLogger();

                AppLogger.Initialize(Log.Logger);
                AppLogger.LogInfo($"{appName} application logging initialized");
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
        /// <param name="appName">The application name for logging identification.</param>
        /// <param name="defaultConfiguration">The default configuration JSON string from DefaultConfigurations class.</param>
        public static void EnsureConfigurationFile(string appName, string defaultConfiguration)
        {
            try
            {
                var configPath = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
                var configManager = new ConfigurationFileManager(configPath, defaultConfiguration);

                if (configManager.EnsureConfigurationExists())
                {
                    AppLogger.LogDebug($"{appName}: Configuration file was created or updated");
                }
            }
            catch (Exception ex)
            {
                AppLogger.LogDebug($"{appName}: Failed to ensure configuration file: {ex.Message}");
                // Don't throw - allow app to continue with defaults
            }
        }
    }
}
