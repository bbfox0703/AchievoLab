using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using CommonUtilities;
using static RunGame.Steam.SteamGameClient;

namespace RunGame.Steam
{
    /// <summary>
    /// Modern Steamworks SDK wrapper using Steamworks SDK 162+ flat API.
    /// This replaces the old complex vtable parsing with direct P/Invoke calls.
    /// Uses steam_api64.dll with modern interface accessors (SteamAPI_SteamUserStats, etc.).
    /// </summary>
    /// <remarks>
    /// Design notes:
    /// - Uses modern SteamAPI_InitFlat() instead of manual pipe/user management
    /// - Direct P/Invoke to flat API functions (no vtable parsing required)
    /// - Callbacks handled automatically by Steam client (no manual pump needed)
    /// - Debug builds log operations without writing to Steam (safety feature)
    /// - Requires steam_api64.dll from game installations or Steam directory
    /// </remarks>
    public sealed class ModernSteamClient : IDisposable, ISteamUserStats
    {
        private bool _initialized;
        private readonly long _gameId;
        private IntPtr _steamUserStats;
        private IntPtr _steamApps;
        private IntPtr _steamUser;
        
        static ModernSteamClient()
        {
            NativeLibrary.SetDllImportResolver(typeof(ModernSteamClient).Assembly, ResolveSteamApi);
        }

        /// <summary>
        /// Gets a value indicating whether the Steam API was successfully initialized.
        /// </summary>
        public bool Initialized => _initialized;

        /// <summary>
        /// Initializes a new instance of the <see cref="ModernSteamClient"/> class.
        /// Sets the SteamAppId environment variable and initializes the Steam API.
        /// </summary>
        /// <param name="gameId">The Steam AppID of the game.</param>
        public ModernSteamClient(long gameId)
        {
            _gameId = gameId;
            Initialize();
        }

        /// <summary>
        /// Initializes the Steam API using modern flat API functions.
        /// Sets SteamAppId environment variable, calls SteamAPI_InitFlat, retrieves interface pointers,
        /// and verifies user is logged in.
        /// </summary>
        /// <returns>True if initialization succeeded; false otherwise.</returns>
        private bool Initialize()
        {
            try
            {
                AppLogger.LogDebug($"Initializing ModernSteamClient for game {_gameId}");
                
                // Set AppID environment variable
                Environment.SetEnvironmentVariable("SteamAppId", _gameId.ToString());
                AppLogger.LogDebug($"Set SteamAppId to {_gameId}");
                
                // Modern Steam API initialization
                var errMsg = new StringBuilder(1024);
                var result = SteamAPI_InitFlat(errMsg);
                
                if (result != ESteamAPIInitResult.k_ESteamAPIInitResult_OK)
                {
                    var error = errMsg.ToString();
                    AppLogger.LogDebug($"Steam API initialization failed: {result}, Error: {error}");
                    LogInitializationError(result, error);
                    return false;
                }
                
                AppLogger.LogDebug("Steam API initialized successfully");
                
                // Get Steam interfaces using modern accessors
                _steamUserStats = SteamAPI_SteamUserStats();
                _steamApps = SteamAPI_SteamApps();
                _steamUser = SteamAPI_SteamUser();
                
                AppLogger.LogDebug($"Steam interfaces - UserStats: {_steamUserStats}, Apps: {_steamApps}, User: {_steamUser}");
                
                if (_steamUserStats == IntPtr.Zero || _steamApps == IntPtr.Zero || _steamUser == IntPtr.Zero)
                {
                    AppLogger.LogDebug("Failed to get required Steam interfaces");
                    return false;
                }
                
                // Verify user is logged in
                if (!SteamAPI_ISteamUser_BLoggedOn(_steamUser))
                {
                    AppLogger.LogDebug("User is not logged in to Steam");
                    return false;
                }
                
                // Get Steam ID for verification
                var steamId = SteamAPI_ISteamUser_GetSteamID(_steamUser);
                AppLogger.LogDebug($"Current Steam ID: {steamId}");
                
                // Verify app ownership (optional, but recommended)
                var hasLicense = SteamAPI_ISteamUser_UserHasLicenseForApp(_steamUser, steamId, (uint)_gameId);
                if (hasLicense == EUserHasLicenseForAppResult.k_EUserHasLicenseResultDoesNotHaveLicense)
                {
                    AppLogger.LogDebug($"User does not own game {_gameId}");
                    // Continue anyway for testing purposes
                }
                
                _initialized = true;
                AppLogger.LogDebug("ModernSteamClient initialized successfully");
                
                return true;
            }
            catch (Exception ex)
            {
                AppLogger.LogDebug($"Exception during Steam client initialization: {ex}");
                return false;
            }
        }

        /// <summary>
        /// Logs user-friendly error messages based on Steam API initialization result codes.
        /// </summary>
        /// <param name="result">The initialization result code from SteamAPI_InitFlat.</param>
        /// <param name="error">The detailed error message from Steam.</param>
        private void LogInitializationError(ESteamAPIInitResult result, string error)
        {
            switch (result)
            {
                case ESteamAPIInitResult.k_ESteamAPIInitResult_NoSteamClient:
                    AppLogger.LogDebug("Steam is not running. Please start Steam and try again.");
                    break;
                case ESteamAPIInitResult.k_ESteamAPIInitResult_VersionMismatch:
                    AppLogger.LogDebug("Steam client version is out of date. Please update Steam.");
                    break;
                case ESteamAPIInitResult.k_ESteamAPIInitResult_FailedGeneric:
                    AppLogger.LogDebug($"Steam API initialization failed: {error}");
                    break;
                default:
                    AppLogger.LogDebug($"Unknown Steam API initialization error: {result}");
                    break;
            }
        }

        /// <summary>
        /// Gets the current Steam UI language code.
        /// </summary>
        /// <returns>The language code (e.g., "english", "tchinese", "japanese"), or "english" if unavailable.</returns>
        public string GetCurrentGameLanguage()
        {
            try
            {
                if (!_initialized)
                    return "english";
                
                // Get Steam Utils interface for language detection
                var steamUtils = SteamAPI_SteamUtils();
                if (steamUtils == IntPtr.Zero)
                    return "english";
                
                var langPtr = SteamAPI_ISteamUtils_GetSteamUILanguage(steamUtils);
                if (langPtr == IntPtr.Zero)
                    return "english";
                
                var language = Marshal.PtrToStringAnsi(langPtr);
                return !string.IsNullOrEmpty(language) ? language : "english";
            }
            catch (Exception ex)
            {
                AppLogger.LogDebug($"Error getting Steam UI language: {ex.Message}");
                return "english";
            }
        }

        // ISteamUserStats implementation using modern flat API

        /// <summary>
        /// Gets the achievement status and unlock time for a specific achievement.
        /// </summary>
        /// <param name="id">The unique achievement identifier.</param>
        /// <param name="achieved">Receives true if the achievement is unlocked; false otherwise.</param>
        /// <param name="unlockTime">Receives the Unix timestamp when the achievement was unlocked (0 if locked).</param>
        /// <returns>True if the achievement data was retrieved successfully; false otherwise.</returns>
        public bool GetAchievementAndUnlockTime(string id, out bool achieved, out uint unlockTime)
        {
            achieved = false;
            unlockTime = 0;
            
            if (!_initialized || _steamUserStats == IntPtr.Zero)
                return false;
            
            try
            {
                return SteamAPI_ISteamUserStats_GetAchievementAndUnlockTime(_steamUserStats, id, out achieved, out unlockTime);
            }
            catch (Exception ex)
            {
                AppLogger.LogDebug($"Error getting achievement {id}: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Sets an achievement to achieved or locked state.
        /// Debug builds log the operation without writing to Steam.
        /// </summary>
        /// <param name="id">The unique achievement identifier.</param>
        /// <param name="achieved">True to unlock the achievement, false to lock it.</param>
        /// <returns>True if the operation succeeded (or was logged in debug mode); false otherwise.</returns>
        public bool SetAchievement(string id, bool achieved)
        {
            AppLogger.LogAchievementSet(id, achieved, AppLogger.IsDebugMode);
            
            if (!_initialized || _steamUserStats == IntPtr.Zero)
                return false;
            
#if DEBUG
            // Debug mode - don't actually write, just log
            return true;
#else
            // Release mode - actually write to Steam
            try
            {
                bool result;
                if (achieved)
                {
                    result = SteamAPI_ISteamUserStats_SetAchievement(_steamUserStats, id);
                }
                else
                {
                    result = SteamAPI_ISteamUserStats_ClearAchievement(_steamUserStats, id);
                }
                
                if (result)
                {
                    // Store stats to commit the achievement change
                    SteamAPI_ISteamUserStats_StoreStats(_steamUserStats);
                }
                
                return result;
            }
            catch (Exception ex)
            {
                AppLogger.LogDebug($"Error setting achievement {id}: {ex.Message}");
                return false;
            }
#endif
        }

        public bool GetStatValue(string name, out int value)
        {
            value = 0;
            
            if (!_initialized || _steamUserStats == IntPtr.Zero)
                return false;
            
            try
            {
                return SteamAPI_ISteamUserStats_GetStatInt32(_steamUserStats, name, out value);
            }
            catch (Exception ex)
            {
                AppLogger.LogDebug($"Error getting int stat {name}: {ex.Message}");
                return false;
            }
        }

        public bool GetStatValue(string name, out float value)
        {
            value = 0.0f;
            
            if (!_initialized || _steamUserStats == IntPtr.Zero)
                return false;
            
            try
            {
                return SteamAPI_ISteamUserStats_GetStatFloat(_steamUserStats, name, out value);
            }
            catch (Exception ex)
            {
                AppLogger.LogDebug($"Error getting float stat {name}: {ex.Message}");
                return false;
            }
        }

        public bool SetStatValue(string name, int value)
        {
            AppLogger.LogStatSet(name, value, AppLogger.IsDebugMode);
            
            if (!_initialized || _steamUserStats == IntPtr.Zero)
                return false;
            
#if DEBUG
            // Debug mode - don't actually write, just log
            return true;
#else
            // Release mode - actually write to Steam
            try
            {
                return SteamAPI_ISteamUserStats_SetStatInt32(_steamUserStats, name, value);
            }
            catch (Exception ex)
            {
                AppLogger.LogDebug($"Error setting int stat {name}: {ex.Message}");
                return false;
            }
#endif
        }

        public bool SetStatValue(string name, float value)
        {
            AppLogger.LogStatSet(name, value, AppLogger.IsDebugMode);
            
            if (!_initialized || _steamUserStats == IntPtr.Zero)
                return false;
            
#if DEBUG
            // Debug mode - don't actually write, just log
            return true;
#else
            // Release mode - actually write to Steam
            try
            {
                return SteamAPI_ISteamUserStats_SetStatFloat(_steamUserStats, name, value);
            }
            catch (Exception ex)
            {
                AppLogger.LogDebug($"Error setting float stat {name}: {ex.Message}");
                return false;
            }
#endif
        }

        public bool StoreStats()
        {
            AppLogger.LogStoreStats(AppLogger.IsDebugMode);
            
            if (!_initialized || _steamUserStats == IntPtr.Zero)
                return false;
            
#if DEBUG
            // Debug mode - don't actually write, just log
            return true;
#else
            // Release mode - actually write to Steam
            try
            {
                return SteamAPI_ISteamUserStats_StoreStats(_steamUserStats);
            }
            catch (Exception ex)
            {
                AppLogger.LogDebug($"Error storing stats: {ex.Message}");
                return false;
            }
#endif
        }

        public bool ResetAllStats(bool achievementsToo)
        {
            AppLogger.LogResetAllStats(achievementsToo, AppLogger.IsDebugMode);
            
            if (!_initialized || _steamUserStats == IntPtr.Zero)
                return false;
            
#if DEBUG
            // Debug mode - don't actually write, just log
            return true;
#else
            // Release mode - actually write to Steam
            try
            {
                return SteamAPI_ISteamUserStats_ResetAllStats(_steamUserStats, achievementsToo);
            }
            catch (Exception ex)
            {
                AppLogger.LogDebug($"Error resetting stats: {ex.Message}");
                return false;
            }
#endif
        }

        public bool IsSubscribedApp(uint gameId)
        {
            if (!_initialized || _steamApps == IntPtr.Zero)
                return false;
            
            try
            {
                // Use BIsSubscribedApp to check specific app ID
                return SteamAPI_ISteamApps_BIsSubscribedApp(_steamApps, gameId);
            }
            catch (Exception ex)
            {
                AppLogger.LogDebug($"Error checking app subscription for {gameId}: {ex.Message}");
                return false;
            }
        }

        public string? GetAppData(uint appId, string key)
        {
            if (!_initialized || _steamApps == IntPtr.Zero)
                return null;
            
            try
            {
                // GetAppData doesn't exist in modern Steam API
                // We can provide basic app information using other methods
                switch (key?.ToLowerInvariant())
                {
                    case "name":
                        // Try to get app name - this is not directly available in Steam API
                        // For now, we'll return the App ID as string
                        return $"App {appId}";
                        
                    case "type":
                        // Check if it's a DLC or regular app
                        bool isDlc = SteamAPI_ISteamApps_BIsDlcInstalled(_steamApps, appId);
                        return isDlc ? "DLC" : "Game";
                        
                    case "state":
                        // Check if app is installed
                        bool isInstalled = SteamAPI_ISteamApps_BIsAppInstalled(_steamApps, appId);
                        return isInstalled ? "Installed" : "Not Installed";
                        
                    default:
                        AppLogger.LogDebug($"GetAppData: Unknown key '{key}' for app {appId}");
                        return null;
                }
            }
            catch (Exception ex)
            {
                AppLogger.LogDebug($"Error getting app data for {appId}, key {key}: {ex.Message}");
                return null;
            }
        }

        public bool RequestUserStats(uint gameId)
        {
            if (!_initialized)
            {
                AppLogger.LogDebug("Steam client not initialized for RequestUserStats");
                return false;
            }
            
            // Note: In modern Steam SDK, RequestCurrentStats is no longer needed
            // Stats are automatically synchronized before game launch
            AppLogger.LogDebug($"RequestUserStats for game {gameId} - stats are auto-synchronized in modern SDK");
            return true;
        }

        public void RunCallbacks()
        {
            // In modern Steam SDK, callbacks are handled automatically by the Steam client
            // This method is kept for compatibility with the interface
            if (_initialized)
            {
                try
                {
                    SteamAPI_RunCallbacks();
                }
                catch (Exception ex)
                {
                    AppLogger.LogDebug($"Error running Steam callbacks: {ex.Message}");
                }
            }
        }

        public void RegisterUserStatsCallback(System.Action<UserStatsReceived> callback)
        {
            // For compatibility with legacy interface, but not implemented in modern client
            // Modern Steam SDK handles callbacks differently
            AppLogger.LogDebug("RegisterUserStatsCallback called - modern SDK handles callbacks automatically");
        }

        /// <summary>
        /// Releases resources and shuts down the Steam API.
        /// </summary>
        public void Dispose()
        {
            if (_initialized)
            {
                try
                {
                    SteamAPI_Shutdown();
                    AppLogger.LogDebug("Steam API shutdown completed");
                }
                catch (Exception ex)
                {
                    AppLogger.LogDebug($"Error during Steam API shutdown: {ex.Message}");
                }
                finally
                {
                    _initialized = false;
                }
            }
        }

        /// <summary>
        /// DLL import resolver for steam_api64.dll.
        /// Searches multiple locations: Steam installation directory, game directories, and PATH.
        /// </summary>
        /// <param name="libraryName">The library name being resolved.</param>
        /// <param name="assembly">The assembly requesting the library.</param>
        /// <param name="searchPath">The DLL import search path.</param>
        /// <returns>A handle to the loaded library, or IntPtr.Zero if not found.</returns>
        private static IntPtr ResolveSteamApi(string libraryName, System.Reflection.Assembly assembly, DllImportSearchPath? searchPath)
        {
            if (!libraryName.Equals("steam_api64", StringComparison.OrdinalIgnoreCase))
                return IntPtr.Zero;

            try
            {
                AppLogger.LogDebug("Attempting to locate steam_api64.dll");
                
                // Try multiple locations in order of preference
                var searchPaths = GetSteamApiSearchPaths();
                
                foreach (var path in searchPaths)
                {
                    if (System.IO.File.Exists(path))
                    {
                        AppLogger.LogDebug($"Found steam_api64.dll at: {path}");
                        try
                        {
                            // Add the directory to DLL search path
                            var directory = System.IO.Path.GetDirectoryName(path);
                            if (!string.IsNullOrEmpty(directory))
                            {
                                NativeLibrary.SetDllImportResolver(typeof(ModernSteamClient).Assembly, ResolveSteamApi); // Reset resolver
                                return NativeLibrary.Load(path);
                            }
                        }
                        catch (Exception loadEx)
                        {
                            AppLogger.LogDebug($"Failed to load {path}: {loadEx.Message}");
                            continue;
                        }
                    }
                }
                
                AppLogger.LogDebug("steam_api64.dll not found in any expected locations");
                return IntPtr.Zero;
            }
            catch (Exception ex)
            {
                AppLogger.LogDebug($"Error in ResolveSteamApi: {ex.Message}");
                return IntPtr.Zero;
            }
        }

        private static List<string> GetSteamApiSearchPaths()
        {
            var paths = new List<string>();
            
            // 1. Steam installation directory
            string? steamPath = GetSteamInstallPath();
            if (!string.IsNullOrEmpty(steamPath))
            {
                paths.Add(System.IO.Path.Combine(steamPath, "steam_api64.dll"));
                paths.Add(System.IO.Path.Combine(steamPath, "bin", "steam_api64.dll"));
            }
            
            // 2. Game installation directories (look for any installed games with steam_api64.dll)
            var gameDirectories = FindSteamGameDirectories();
            foreach (var gameDir in gameDirectories)
            {
                var apiPath = System.IO.Path.Combine(gameDir, "steam_api64.dll");
                if (System.IO.File.Exists(apiPath))
                {
                    paths.Add(apiPath);
                }
            }
            
            // 3. Common Steam library locations
            if (!string.IsNullOrEmpty(steamPath))
            {
                var steamAppsPath = System.IO.Path.Combine(steamPath, "steamapps", "common");
                if (System.IO.Directory.Exists(steamAppsPath))
                {
                    try
                    {
                        foreach (var gameDir in System.IO.Directory.GetDirectories(steamAppsPath))
                        {
                            var apiPath = System.IO.Path.Combine(gameDir, "steam_api64.dll");
                            if (System.IO.File.Exists(apiPath))
                            {
                                paths.Add(apiPath);
                                // Only add first few to avoid too many attempts
                                if (paths.Count(p => p.Contains("steamapps")) >= 5) break;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        AppLogger.LogDebug($"Error searching steamapps directory: {ex.Message}");
                    }
                }
            }
            
            // 4. Current directory and PATH fallback
            paths.Add("steam_api64.dll");
            
            AppLogger.LogDebug($"Steam API search paths: {string.Join("; ", paths)}");
            return paths;
        }

        private static List<string> FindSteamGameDirectories()
        {
            var directories = new List<string>();
            
            try
            {
                string? steamPath = GetSteamInstallPath();
                if (string.IsNullOrEmpty(steamPath)) return directories;
                
                // Check libraryfolders.vdf for additional Steam library locations
                var libraryFoldersPath = System.IO.Path.Combine(steamPath, "config", "libraryfolders.vdf");
                if (System.IO.File.Exists(libraryFoldersPath))
                {
                    // Simple parsing of libraryfolders.vdf to find additional Steam library paths
                    var content = System.IO.File.ReadAllText(libraryFoldersPath);
                    var lines = content.Split('\n');
                    
                    foreach (var line in lines)
                    {
                        if (line.Contains("\"path\""))
                        {
                            var match = System.Text.RegularExpressions.Regex.Match(line, @"""path""\s*""([^""]+)""");
                            if (match.Success)
                            {
                                var path = match.Groups[1].Value.Replace("\\\\", "\\");
                                var commonPath = System.IO.Path.Combine(path, "steamapps", "common");
                                if (System.IO.Directory.Exists(commonPath))
                                {
                                    directories.Add(commonPath);
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                AppLogger.LogDebug($"Error finding Steam game directories: {ex.Message}");
            }
            
            return directories;
        }

        private static string? GetSteamInstallPath()
        {
            try
            {
                AppLogger.LogDebug("Searching for Steam install path in registry...");
                const string subKey = @"SOFTWARE\Valve\Steam";

                // Check HKLM 64-bit and 32-bit (WOW6432Node) views
                foreach (var view in new[] { Microsoft.Win32.RegistryView.Registry64, Microsoft.Win32.RegistryView.Registry32 })
                {
                    try
                    {
                        using var key = Microsoft.Win32.RegistryKey.OpenBaseKey(Microsoft.Win32.RegistryHive.LocalMachine, view).OpenSubKey(subKey);
                        var path = key?.GetValue("InstallPath") as string;
                        if (!string.IsNullOrEmpty(path))
                        {
                            AppLogger.LogDebug($"Found Steam install path in HKLM Registry{(view == Microsoft.Win32.RegistryView.Registry32 ? "32" : "64")}: {path}");
                            return path;
                        }
                    }
                    catch (Exception ex)
                    {
                        AppLogger.LogDebug($"Error accessing HKLM registry (view: {view}): {ex.Message}");
                    }
                }

                // Fall back to HKCU
                foreach (var view in new[] { Microsoft.Win32.RegistryView.Registry64, Microsoft.Win32.RegistryView.Registry32 })
                {
                    try
                    {
                        using var key = Microsoft.Win32.RegistryKey.OpenBaseKey(Microsoft.Win32.RegistryHive.CurrentUser, view).OpenSubKey(subKey);
                        var path = key?.GetValue("InstallPath") as string;
                        if (!string.IsNullOrEmpty(path))
                        {
                            AppLogger.LogDebug($"Found Steam install path in HKCU Registry{(view == Microsoft.Win32.RegistryView.Registry32 ? "32" : "64")}: {path}");
                            return path;
                        }
                    }
                    catch (Exception ex)
                    {
                        AppLogger.LogDebug($"Error accessing HKCU registry (view: {view}): {ex.Message}");
                    }
                }

                AppLogger.LogDebug("Steam install path not found in registry");
                return null;
            }
            catch (Exception ex)
            {
                AppLogger.LogDebug($"Error in GetSteamInstallPath: {ex.Message}");
                return null;
            }
        }

        // Modern Steam API P/Invoke declarations
        
        // Initialization
        [DllImport("steam_api64", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        private static extern ESteamAPIInitResult SteamAPI_InitFlat(StringBuilder pOutErrMsg);

        [DllImport("steam_api64", CallingConvention = CallingConvention.Cdecl)]
        private static extern void SteamAPI_Shutdown();

        [DllImport("steam_api64", CallingConvention = CallingConvention.Cdecl)]
        private static extern void SteamAPI_RunCallbacks();

        // Interface accessors
        [DllImport("steam_api64", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr SteamAPI_SteamUserStats();

        [DllImport("steam_api64", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr SteamAPI_SteamApps();

        [DllImport("steam_api64", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr SteamAPI_SteamUser();

        [DllImport("steam_api64", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr SteamAPI_SteamUtils();

        // User methods
        [DllImport("steam_api64", CallingConvention = CallingConvention.Cdecl)]
        private static extern bool SteamAPI_ISteamUser_BLoggedOn(IntPtr steamUser);

        [DllImport("steam_api64", CallingConvention = CallingConvention.Cdecl)]
        private static extern ulong SteamAPI_ISteamUser_GetSteamID(IntPtr steamUser);

        [DllImport("steam_api64", CallingConvention = CallingConvention.Cdecl)]
        private static extern EUserHasLicenseForAppResult SteamAPI_ISteamUser_UserHasLicenseForApp(IntPtr steamUser, ulong steamID, uint appID);

        // Utils methods
        [DllImport("steam_api64", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr SteamAPI_ISteamUtils_GetSteamUILanguage(IntPtr steamUtils);

        // Apps methods
        [DllImport("steam_api64", CallingConvention = CallingConvention.Cdecl)]
        private static extern bool SteamAPI_ISteamApps_BIsSubscribed(IntPtr steamApps);

        [DllImport("steam_api64", CallingConvention = CallingConvention.Cdecl)]
        private static extern bool SteamAPI_ISteamApps_BIsSubscribedApp(IntPtr steamApps, uint appID);

        [DllImport("steam_api64", CallingConvention = CallingConvention.Cdecl)]
        private static extern bool SteamAPI_ISteamApps_BIsDlcInstalled(IntPtr steamApps, uint appID);

        [DllImport("steam_api64", CallingConvention = CallingConvention.Cdecl)]
        private static extern bool SteamAPI_ISteamApps_BIsAppInstalled(IntPtr steamApps, uint appID);

        // UserStats methods
        [DllImport("steam_api64", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        private static extern bool SteamAPI_ISteamUserStats_GetAchievementAndUnlockTime(IntPtr steamUserStats, string pchName, out bool pbAchieved, out uint punUnlockTime);

        [DllImport("steam_api64", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        private static extern bool SteamAPI_ISteamUserStats_SetAchievement(IntPtr steamUserStats, string pchName);

        [DllImport("steam_api64", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        private static extern bool SteamAPI_ISteamUserStats_ClearAchievement(IntPtr steamUserStats, string pchName);

        [DllImport("steam_api64", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        private static extern bool SteamAPI_ISteamUserStats_GetStatInt32(IntPtr steamUserStats, string pchName, out int pData);

        [DllImport("steam_api64", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        private static extern bool SteamAPI_ISteamUserStats_GetStatFloat(IntPtr steamUserStats, string pchName, out float pData);

        [DllImport("steam_api64", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        private static extern bool SteamAPI_ISteamUserStats_SetStatInt32(IntPtr steamUserStats, string pchName, int nData);

        [DllImport("steam_api64", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        private static extern bool SteamAPI_ISteamUserStats_SetStatFloat(IntPtr steamUserStats, string pchName, float fData);

        [DllImport("steam_api64", CallingConvention = CallingConvention.Cdecl)]
        private static extern bool SteamAPI_ISteamUserStats_StoreStats(IntPtr steamUserStats);

        [DllImport("steam_api64", CallingConvention = CallingConvention.Cdecl)]
        private static extern bool SteamAPI_ISteamUserStats_ResetAllStats(IntPtr steamUserStats, bool bAchievementsToo);

        // Enums

        /// <summary>
        /// Steam API initialization result codes.
        /// </summary>
        public enum ESteamAPIInitResult
        {
            /// <summary>
            /// Initialization succeeded.
            /// </summary>
            k_ESteamAPIInitResult_OK = 0,

            /// <summary>
            /// Generic failure.
            /// </summary>
            k_ESteamAPIInitResult_FailedGeneric = 1,

            /// <summary>
            /// Steam is not running.
            /// </summary>
            k_ESteamAPIInitResult_NoSteamClient = 2,

            /// <summary>
            /// Steam client version is out of date.
            /// </summary>
            k_ESteamAPIInitResult_VersionMismatch = 3,
        }

        /// <summary>
        /// User license status result codes for checking app ownership.
        /// </summary>
        public enum EUserHasLicenseForAppResult
        {
            /// <summary>
            /// User owns the app.
            /// </summary>
            k_EUserHasLicenseResultHasLicense = 0,

            /// <summary>
            /// User does not own the app.
            /// </summary>
            k_EUserHasLicenseResultDoesNotHaveLicense = 1,

            /// <summary>
            /// No auth/license information available.
            /// </summary>
            k_EUserHasLicenseResultNoAuth = 2,
        }
    }
}
