using System;
using System.IO;
using System.Text;
using System.Text.Json;

namespace CommonUtilities
{
    /// <summary>
    /// Manages application configuration files (appsettings.json).
    /// Ensures configuration files exist with all required parameters and comments.
    /// </summary>
    public class ConfigurationFileManager
    {
        private readonly string _configFilePath;
        private readonly string _defaultConfigContent;

        /// <summary>
        /// Creates a new configuration file manager.
        /// </summary>
        /// <param name="configFilePath">Path to the appsettings.json file</param>
        /// <param name="defaultConfigContent">Default content with comments if file doesn't exist</param>
        public ConfigurationFileManager(string configFilePath, string defaultConfigContent)
        {
            _configFilePath = configFilePath;
            _defaultConfigContent = defaultConfigContent;
        }

        /// <summary>
        /// Ensures the configuration file exists and is valid.
        /// If the file doesn't exist, creates it with default content.
        /// If the file exists but is incomplete, backs it up and regenerates it.
        /// </summary>
        /// <returns>True if file was created or updated, false if it was already valid</returns>
        public bool EnsureConfigurationExists()
        {
            try
            {
                // Check if file exists
                if (!File.Exists(_configFilePath))
                {
                    AppLogger.LogDebug($"ConfigurationFileManager: Config file not found at '{_configFilePath}', creating with defaults");
                    WriteDefaultConfiguration();
                    return true;
                }

                // Validate existing file
                if (!ValidateConfiguration())
                {
                    AppLogger.LogDebug($"ConfigurationFileManager: Config file at '{_configFilePath}' is incomplete or invalid");
                    BackupAndRegenerate();
                    return true;
                }

                AppLogger.LogDebug($"ConfigurationFileManager: Config file at '{_configFilePath}' is valid");
                return false;
            }
            catch (Exception ex)
            {
                AppLogger.LogDebug($"ConfigurationFileManager: Error ensuring configuration: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Writes the default configuration to file.
        /// </summary>
        private void WriteDefaultConfiguration()
        {
            try
            {
                // Ensure directory exists
                var directory = Path.GetDirectoryName(_configFilePath);
                if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                File.WriteAllText(_configFilePath, _defaultConfigContent, Encoding.UTF8);
                AppLogger.LogDebug($"ConfigurationFileManager: Successfully wrote default configuration to '{_configFilePath}'");
            }
            catch (Exception ex)
            {
                AppLogger.LogDebug($"ConfigurationFileManager: Error writing default configuration: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// Validates that the configuration file is valid JSON and can be read.
        /// Basic validation - checks if file can be parsed as JSON.
        /// </summary>
        /// <returns>True if configuration is valid, false otherwise</returns>
        private bool ValidateConfiguration()
        {
            try
            {
                var content = File.ReadAllText(_configFilePath);

                // Check if file is empty
                if (string.IsNullOrWhiteSpace(content))
                {
                    AppLogger.LogDebug("ConfigurationFileManager: Config file is empty");
                    return false;
                }

                // Try to parse as JSON (with comments allowed)
                var options = new JsonDocumentOptions
                {
                    CommentHandling = JsonCommentHandling.Skip,
                    AllowTrailingCommas = true
                };

                using (var doc = JsonDocument.Parse(content, options))
                {
                    // Basic validation passed
                    return true;
                }
            }
            catch (JsonException ex)
            {
                AppLogger.LogDebug($"ConfigurationFileManager: JSON parse error: {ex.Message}");
                return false;
            }
            catch (Exception ex)
            {
                AppLogger.LogDebug($"ConfigurationFileManager: Validation error: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Backs up the existing configuration and regenerates it with defaults.
        /// </summary>
        private void BackupAndRegenerate()
        {
            try
            {
                // Create backup with timestamp
                var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                var backupPath = _configFilePath + $".backup_{timestamp}";

                if (File.Exists(_configFilePath))
                {
                    File.Copy(_configFilePath, backupPath, true);
                    AppLogger.LogDebug($"ConfigurationFileManager: Backed up existing config to '{backupPath}'");
                }

                // Write new default configuration
                WriteDefaultConfiguration();
            }
            catch (Exception ex)
            {
                AppLogger.LogDebug($"ConfigurationFileManager: Error during backup and regeneration: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// Gets the path to the configuration file.
        /// </summary>
        public string ConfigFilePath => _configFilePath;
    }
}
