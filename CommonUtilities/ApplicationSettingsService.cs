using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;

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

        // Keys this process has changed/removed since the last successful save. Save()
        // merges only these over the latest on-disk state, so a second AchievoLab
        // executable sharing settings.json can never clobber keys it didn't touch.
        private readonly object _writeLock = new();
        private readonly HashSet<string> _dirtyKeys = new();
        private readonly HashSet<string> _removedKeys = new();

        // Session-scoped lock: AnSAM/RunGame/MyOwnGames run in the same interactive
        // session and share %LOCALAPPDATA%\AchievoLab\settings.json.
        private const string SettingsMutexName = @"Local\AchievoLab_settings_json";

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

                    _settings = LoadSettingsFile(_settingsFilePath);
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
        /// Reads and parses settings.json, tolerating both a concurrent writer and a corrupt file.
        /// </summary>
        /// <remarks>
        /// A sharing violation while a sibling process is mid-write used to propagate out of
        /// Initialize and latch _initializationFailed for the lifetime of this instance, silently
        /// disabling theme, language and window-placement persistence. Retry briefly instead.
        ///
        /// Unparseable content is not a reason to give up either: the corrupt file is moved aside
        /// so the user can still save new settings, rather than hitting the same failure on every
        /// start with no way to recover from inside the app.
        /// </remarks>
        private static Dictionary<string, string> LoadSettingsFile(string path)
        {
            if (!File.Exists(path))
            {
                return new Dictionary<string, string>();
            }

            const int attempts = 3;
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    var json = File.ReadAllText(path);
                    return JsonSerializer.Deserialize(json, SettingsJsonContext.Default.DictionaryStringString)
                           ?? new Dictionary<string, string>();
                }
                catch (JsonException ex)
                {
                    AppLogger.LogDebug($"ApplicationSettingsService: settings.json is corrupt ({ex.Message}); starting fresh.");
                    try { File.Move(path, path + ".corrupt", overwrite: true); }
                    catch (Exception moveEx)
                    {
                        AppLogger.LogDebug($"ApplicationSettingsService: could not set the corrupt file aside: {moveEx.Message}");
                    }
                    return new Dictionary<string, string>();
                }
                catch (IOException) when (attempt < attempts - 1)
                {
                    Thread.Sleep(50);
                }
                catch (UnauthorizedAccessException) when (attempt < attempts - 1)
                {
                    Thread.Sleep(50);
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
                lock (_writeLock)
                {
                    _settings[key] = value;
                    _dirtyKeys.Add(key);
                    _removedKeys.Remove(key);
                    Save();
                }
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
                lock (_writeLock)
                {
                    _settings.Remove(key);
                    _removedKeys.Add(key);
                    _dirtyKeys.Remove(key);
                    Save();
                }
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

        /// <summary>
        /// Persists settings by merging only this process's changed/removed keys over
        /// the latest on-disk state, guarded by a cross-process mutex. This prevents one
        /// running AchievoLab executable from reverting keys (e.g. another window's
        /// placement, or Theme/Language) saved by a sibling process that shares the file.
        /// </summary>
        private void Save()
        {
            if (_settings == null || _settingsFilePath == null)
                return;

            lock (_writeLock)
            {
                Mutex? mutex = null;
                bool ownsMutex = false;
                try
                {
                    try
                    {
                        mutex = new Mutex(false, SettingsMutexName);
                        try { ownsMutex = mutex.WaitOne(TimeSpan.FromSeconds(5)); }
                        catch (AbandonedMutexException) { ownsMutex = true; }
                    }
                    catch (Exception ex)
                    {
                        // Couldn't create/acquire the cross-process lock; proceed best-effort.
                        AppLogger.LogDebug($"ApplicationSettingsService: settings mutex unavailable: {ex.Message}");
                    }

                    // Re-read the newest on-disk state, then overlay our own changes.
                    Dictionary<string, string> merged;
                    try
                    {
                        if (File.Exists(_settingsFilePath))
                        {
                            var existingJson = File.ReadAllText(_settingsFilePath);
                            merged = JsonSerializer.Deserialize(existingJson, SettingsJsonContext.Default.DictionaryStringString)
                                     ?? new Dictionary<string, string>();
                        }
                        else
                        {
                            merged = new Dictionary<string, string>();
                        }
                    }
                    catch (Exception ex)
                    {
                        AppLogger.LogDebug($"ApplicationSettingsService: re-read failed, using in-memory copy: {ex.Message}");
                        merged = new Dictionary<string, string>(_settings);
                    }

                    foreach (var key in _dirtyKeys)
                    {
                        if (_settings.TryGetValue(key, out var value))
                            merged[key] = value;
                    }
                    foreach (var key in _removedKeys)
                    {
                        merged.Remove(key);
                    }

                    var json = JsonSerializer.Serialize(merged, SettingsJsonContext.Default.DictionaryStringString);

                    // Write to a temp file and swap it in, rather than truncating settings.json in
                    // place. File.WriteAllText leaves a window where the real file is empty or
                    // half-written; losing power or closing the app inside it left behind truncated
                    // JSON that failed to parse on every subsequent start.
                    var tempPath = $"{_settingsFilePath}.{Environment.ProcessId}.tmp";
                    try
                    {
                        File.WriteAllText(tempPath, json);
                        File.Move(tempPath, _settingsFilePath, overwrite: true);
                    }
                    catch
                    {
                        try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
                        throw;
                    }

                    // Adopt the merged view so this instance also sees sibling-process keys.
                    _settings = merged;
                    _dirtyKeys.Clear();
                    _removedKeys.Clear();
                }
                catch (Exception ex)
                {
                    AppLogger.LogDebug($"ApplicationSettingsService: Error saving settings: {ex.Message}");
                }
                finally
                {
                    if (mutex != null)
                    {
                        if (ownsMutex)
                        {
                            try { mutex.ReleaseMutex(); } catch { /* best-effort */ }
                        }
                        mutex.Dispose();
                    }
                }
            }
        }
    }

    [JsonSerializable(typeof(Dictionary<string, string>))]
    internal partial class SettingsJsonContext : JsonSerializerContext
    {
    }
}
