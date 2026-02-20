using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CommonUtilities
{
    /// <summary>
    /// Provides a centralized, error-tolerant interface for application settings storage.
    /// Uses a JSON file in LocalApplicationData for persistence.
    /// Shared across AnSAM, RunGame, and MyOwnGames for consistent settings management.
    /// </summary>
    public class ApplicationSettingsService
    {
        private Dictionary<string, string>? _settings;
        private string? _settingsFilePath;
        private bool _initializationFailed;
        private static readonly object _initLock = new();

        /// <summary>
        /// Initializes the service and loads settings from the JSON file.
        /// Safe to call multiple times - will only initialize once.
        /// </summary>
        /// <param name="appName">Optional application name for the settings folder. Defaults to "AchievoLab".</param>
        public void Initialize(string appName = "AchievoLab")
        {
            if (_settings != null || _initializationFailed)
                return;

            lock (_initLock)
            {
                if (_settings != null || _initializationFailed)
                    return;

                try
                {
                    var folder = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        appName);
                    Directory.CreateDirectory(folder);
                    _settingsFilePath = Path.Combine(folder, "settings.json");

                    if (File.Exists(_settingsFilePath))
                    {
                        var json = File.ReadAllText(_settingsFilePath);
                        _settings = JsonSerializer.Deserialize(json, SettingsJsonContext.Default.DictionaryStringString)
                                    ?? new Dictionary<string, string>();
                    }
                    else
                    {
                        _settings = new Dictionary<string, string>();
                    }
                }
                catch (Exception ex)
                {
                    AppLogger.LogDebug($"ApplicationSettingsService: Exception loading settings: {ex.Message}");
                    _initializationFailed = true;
                    _settings = null;
                }
            }
        }

        /// <summary>
        /// Gets whether the settings storage is available.
        /// </summary>
        public bool IsAvailable
        {
            get
            {
                Initialize();
                return _settings != null;
            }
        }

        /// <summary>
        /// Attempts to read a string value from settings.
        /// </summary>
        public bool TryGetString(string key, out string? value)
        {
            Initialize();
            value = null;

            if (_settings == null)
                return false;

            try
            {
                if (_settings.TryGetValue(key, out var str))
                {
                    value = str;
                    return true;
                }
            }
            catch (Exception ex)
            {
                AppLogger.LogDebug($"ApplicationSettingsService: Error reading key '{key}': {ex.Message}");
            }

            return false;
        }

        /// <summary>
        /// Attempts to read an enum value from settings.
        /// </summary>
        public bool TryGetEnum<TEnum>(string key, out TEnum value) where TEnum : struct, Enum
        {
            Initialize();
            value = default;

            if (_settings == null)
                return false;

            try
            {
                if (_settings.TryGetValue(key, out var str))
                {
                    return Enum.TryParse<TEnum>(str, out value);
                }
            }
            catch (Exception ex)
            {
                AppLogger.LogDebug($"ApplicationSettingsService: Error reading enum key '{key}': {ex.Message}");
            }

            return false;
        }

        /// <summary>
        /// Attempts to read an integer value from settings.
        /// </summary>
        public bool TryGetInt(string key, out int value)
        {
            Initialize();
            value = 0;

            if (_settings == null)
                return false;

            try
            {
                if (_settings.TryGetValue(key, out var str) && int.TryParse(str, out var intVal))
                {
                    value = intVal;
                    return true;
                }
            }
            catch (Exception ex)
            {
                AppLogger.LogDebug($"ApplicationSettingsService: Error reading int key '{key}': {ex.Message}");
            }

            return false;
        }

        /// <summary>
        /// Attempts to read a boolean value from settings.
        /// </summary>
        public bool TryGetBool(string key, out bool value)
        {
            Initialize();
            value = false;

            if (_settings == null)
                return false;

            try
            {
                if (_settings.TryGetValue(key, out var str) && bool.TryParse(str, out var boolVal))
                {
                    value = boolVal;
                    return true;
                }
            }
            catch (Exception ex)
            {
                AppLogger.LogDebug($"ApplicationSettingsService: Error reading bool key '{key}': {ex.Message}");
            }

            return false;
        }

        /// <summary>
        /// Attempts to write a string value to settings.
        /// </summary>
        public bool TrySetString(string key, string value)
        {
            Initialize();

            if (_settings == null)
                return false;

            try
            {
                _settings[key] = value;
                Save();
                return true;
            }
            catch (Exception ex)
            {
                AppLogger.LogDebug($"ApplicationSettingsService: Error writing key '{key}': {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Attempts to write an enum value to settings (stored as string).
        /// </summary>
        public bool TrySetEnum<TEnum>(string key, TEnum value) where TEnum : struct, Enum
        {
            return TrySetString(key, value.ToString());
        }

        /// <summary>
        /// Attempts to write an integer value to settings.
        /// </summary>
        public bool TrySetInt(string key, int value)
        {
            return TrySetString(key, value.ToString());
        }

        /// <summary>
        /// Attempts to write a boolean value to settings.
        /// </summary>
        public bool TrySetBool(string key, bool value)
        {
            return TrySetString(key, value.ToString());
        }

        /// <summary>
        /// Attempts to remove a value from settings.
        /// </summary>
        public bool TryRemove(string key)
        {
            Initialize();

            if (_settings == null)
                return false;

            try
            {
                _settings.Remove(key);
                Save();
                return true;
            }
            catch (Exception ex)
            {
                AppLogger.LogDebug($"ApplicationSettingsService: Error removing key '{key}': {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Attempts to check if a key exists in settings.
        /// </summary>
        public bool ContainsKey(string key)
        {
            Initialize();

            if (_settings == null)
                return false;

            try
            {
                return _settings.ContainsKey(key);
            }
            catch (Exception ex)
            {
                AppLogger.LogDebug($"ApplicationSettingsService: Error checking key '{key}': {ex.Message}");
                return false;
            }
        }

        private void Save()
        {
            if (_settings == null || _settingsFilePath == null)
                return;

            try
            {
                var json = JsonSerializer.Serialize(_settings, SettingsJsonContext.Default.DictionaryStringString);
                File.WriteAllText(_settingsFilePath, json);
            }
            catch (Exception ex)
            {
                AppLogger.LogDebug($"ApplicationSettingsService: Error saving settings: {ex.Message}");
            }
        }
    }

    [JsonSerializable(typeof(Dictionary<string, string>))]
    internal partial class SettingsJsonContext : JsonSerializerContext
    {
    }
}
