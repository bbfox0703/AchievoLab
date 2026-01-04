using System;
using Windows.Storage;

namespace CommonUtilities
{
    /// <summary>
    /// Provides a centralized, error-tolerant interface for application settings storage.
    /// Wraps Windows.Storage.ApplicationData.Current.LocalSettings with proper error handling.
    /// Shared across AnSAM, RunGame, and MyOwnGames for consistent settings management.
    /// </summary>
    public class ApplicationSettingsService
    {
        private ApplicationDataContainer? _settings;
        private bool _initializationFailed = false;
        private static readonly object _initLock = new object();

        /// <summary>
        /// Initializes the service and attempts to access LocalSettings.
        /// Safe to call multiple times - will only initialize once.
        /// If initialization fails, stops retrying to avoid repeated exceptions.
        /// </summary>
        public void Initialize()
        {
            // Skip if already initialized or if previous initialization failed
            if (_settings != null || _initializationFailed)
                return;

            lock (_initLock)
            {
                // Double-check after acquiring lock
                if (_settings != null || _initializationFailed)
                    return;

                try
                {
                    _settings = ApplicationData.Current.LocalSettings;
                }
                catch (InvalidOperationException ex)
                {
                    DebugLogger.LogDebug($"ApplicationSettingsService: InvalidOperationException accessing LocalSettings: {ex.Message}");
                    _initializationFailed = true;
                    _settings = null;
                }
                catch (System.IO.IOException ex)
                {
                    DebugLogger.LogDebug($"ApplicationSettingsService: IOException accessing LocalSettings: {ex.Message}");
                    _initializationFailed = true;
                    _settings = null;
                }
                catch (Exception ex)
                {
                    DebugLogger.LogDebug($"ApplicationSettingsService: Exception accessing LocalSettings: {ex.Message}");
                    _initializationFailed = true;
                    _settings = null;
                }
            }
        }

        /// <summary>
        /// Gets whether LocalSettings is available.
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
        /// <param name="key">The setting key</param>
        /// <param name="value">The value if found, null otherwise</param>
        /// <returns>True if the value was successfully read, false otherwise</returns>
        public bool TryGetString(string key, out string? value)
        {
            Initialize();
            value = null;

            if (_settings == null)
                return false;

            try
            {
                if (_settings.Values.TryGetValue(key, out var obj))
                {
                    value = obj as string;
                    return value != null;
                }
            }
            catch (Exception ex)
            {
                DebugLogger.LogDebug($"ApplicationSettingsService: Error reading key '{key}': {ex.Message}");
            }

            return false;
        }

        /// <summary>
        /// Attempts to read an enum value from settings.
        /// </summary>
        /// <typeparam name="TEnum">The enum type</typeparam>
        /// <param name="key">The setting key</param>
        /// <param name="value">The parsed enum value if found, default(TEnum) otherwise</param>
        /// <returns>True if the value was successfully read and parsed, false otherwise</returns>
        public bool TryGetEnum<TEnum>(string key, out TEnum value) where TEnum : struct, Enum
        {
            Initialize();
            value = default;

            if (_settings == null)
                return false;

            try
            {
                if (_settings.Values.TryGetValue(key, out var obj) && obj is string str)
                {
                    return Enum.TryParse<TEnum>(str, out value);
                }
            }
            catch (Exception ex)
            {
                DebugLogger.LogDebug($"ApplicationSettingsService: Error reading enum key '{key}': {ex.Message}");
            }

            return false;
        }

        /// <summary>
        /// Attempts to read an integer value from settings.
        /// </summary>
        /// <param name="key">The setting key</param>
        /// <param name="value">The value if found, 0 otherwise</param>
        /// <returns>True if the value was successfully read, false otherwise</returns>
        public bool TryGetInt(string key, out int value)
        {
            Initialize();
            value = 0;

            if (_settings == null)
                return false;

            try
            {
                if (_settings.Values.TryGetValue(key, out var obj))
                {
                    if (obj is int intVal)
                    {
                        value = intVal;
                        return true;
                    }
                    // Try parsing if it's stored as string
                    if (obj is string str && int.TryParse(str, out intVal))
                    {
                        value = intVal;
                        return true;
                    }
                }
            }
            catch (Exception ex)
            {
                DebugLogger.LogDebug($"ApplicationSettingsService: Error reading int key '{key}': {ex.Message}");
            }

            return false;
        }

        /// <summary>
        /// Attempts to read a boolean value from settings.
        /// </summary>
        /// <param name="key">The setting key</param>
        /// <param name="value">The value if found, false otherwise</param>
        /// <returns>True if the value was successfully read, false otherwise</returns>
        public bool TryGetBool(string key, out bool value)
        {
            Initialize();
            value = false;

            if (_settings == null)
                return false;

            try
            {
                if (_settings.Values.TryGetValue(key, out var obj))
                {
                    if (obj is bool boolVal)
                    {
                        value = boolVal;
                        return true;
                    }
                    // Try parsing if it's stored as string
                    if (obj is string str && bool.TryParse(str, out boolVal))
                    {
                        value = boolVal;
                        return true;
                    }
                }
            }
            catch (Exception ex)
            {
                DebugLogger.LogDebug($"ApplicationSettingsService: Error reading bool key '{key}': {ex.Message}");
            }

            return false;
        }

        /// <summary>
        /// Attempts to write a string value to settings.
        /// </summary>
        /// <param name="key">The setting key</param>
        /// <param name="value">The value to write</param>
        /// <returns>True if the value was successfully written, false otherwise</returns>
        public bool TrySetString(string key, string value)
        {
            Initialize();

            if (_settings == null)
                return false;

            try
            {
                _settings.Values[key] = value;
                return true;
            }
            catch (Exception ex)
            {
                DebugLogger.LogDebug($"ApplicationSettingsService: Error writing key '{key}': {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Attempts to write an enum value to settings (stored as string).
        /// </summary>
        /// <typeparam name="TEnum">The enum type</typeparam>
        /// <param name="key">The setting key</param>
        /// <param name="value">The enum value to write</param>
        /// <returns>True if the value was successfully written, false otherwise</returns>
        public bool TrySetEnum<TEnum>(string key, TEnum value) where TEnum : struct, Enum
        {
            return TrySetString(key, value.ToString());
        }

        /// <summary>
        /// Attempts to write an integer value to settings.
        /// </summary>
        /// <param name="key">The setting key</param>
        /// <param name="value">The value to write</param>
        /// <returns>True if the value was successfully written, false otherwise</returns>
        public bool TrySetInt(string key, int value)
        {
            Initialize();

            if (_settings == null)
                return false;

            try
            {
                _settings.Values[key] = value;
                return true;
            }
            catch (Exception ex)
            {
                DebugLogger.LogDebug($"ApplicationSettingsService: Error writing int key '{key}': {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Attempts to write a boolean value to settings.
        /// </summary>
        /// <param name="key">The setting key</param>
        /// <param name="value">The value to write</param>
        /// <returns>True if the value was successfully written, false otherwise</returns>
        public bool TrySetBool(string key, bool value)
        {
            Initialize();

            if (_settings == null)
                return false;

            try
            {
                _settings.Values[key] = value;
                return true;
            }
            catch (Exception ex)
            {
                DebugLogger.LogDebug($"ApplicationSettingsService: Error writing bool key '{key}': {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Attempts to remove a value from settings.
        /// </summary>
        /// <param name="key">The setting key to remove</param>
        /// <returns>True if the value was successfully removed or didn't exist, false on error</returns>
        public bool TryRemove(string key)
        {
            Initialize();

            if (_settings == null)
                return false;

            try
            {
                _settings.Values.Remove(key);
                return true;
            }
            catch (Exception ex)
            {
                DebugLogger.LogDebug($"ApplicationSettingsService: Error removing key '{key}': {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Attempts to check if a key exists in settings.
        /// </summary>
        /// <param name="key">The setting key</param>
        /// <returns>True if the key exists, false otherwise or on error</returns>
        public bool ContainsKey(string key)
        {
            Initialize();

            if (_settings == null)
                return false;

            try
            {
                return _settings.Values.ContainsKey(key);
            }
            catch (Exception ex)
            {
                DebugLogger.LogDebug($"ApplicationSettingsService: Error checking key '{key}': {ex.Message}");
                return false;
            }
        }
    }
}
