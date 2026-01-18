using System;
using System.IO;
using Microsoft.Extensions.Configuration;
using Serilog;

namespace CommonUtilities
{
    /// <summary>
    /// Provides unified application initialization helpers for logging and configuration.
    /// Shared across AnSAM, RunGame, and MyOwnGames to eliminate duplicate initialization code.
    /// </summary>
    public static class ApplicationInitializationHelper
    {
        /// <summary>
        /// Initializes Serilog logging from appsettings.json.
        /// Reads configuration from the application's base directory and initializes AppLogger.
        /// </summary>
        /// <param name="appName">The application name for logging identification (e.g., "AnSAM", "RunGame", "MyOwnGames").</param>
        public static void InitializeLogging(string appName)
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
