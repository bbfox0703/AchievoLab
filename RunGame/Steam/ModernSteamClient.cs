using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using CommonUtilities;
using static RunGame.Steam.SteamGameClient;

namespace RunGame.Steam
{
    public sealed partial class ModernSteamClient : IDisposable, ISteamUserStats
    {
        private bool _initialized;
        private readonly long _gameId;
        private IntPtr _steamUserStats;
        private IntPtr _steamApps;
        private IntPtr _steamUser;

        static ModernSteamClient()
        {
            SteamDllResolver.EnsureRegistered();
        }

        public bool Initialized => _initialized;

        public ModernSteamClient(long gameId)
        {
            _gameId = gameId;
            Initialize();
        }

        private bool Initialize()
        {
            try
            {
                AppLogger.LogDebug($"Initializing ModernSteamClient for game {_gameId}");

                // Set AppID environment variable
                Environment.SetEnvironmentVariable("SteamAppId", _gameId.ToString());
                AppLogger.LogDebug($"Set SteamAppId to {_gameId}");

                // Modern Steam API initialization
                var errMsg = new byte[1024];
                var result = SteamAPI_InitFlat(errMsg);

                if (result != ESteamAPIInitResult.k_ESteamAPIInitResult_OK)
                {
                    var error = Encoding.UTF8.GetString(errMsg).TrimEnd('\0');
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
            return true;
#else
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
            return true;
#else
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
            return true;
#else
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
            return true;
#else
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
                switch (key?.ToLowerInvariant())
                {
                    case "name":
                        return $"App {appId}";

                    case "type":
                        bool isDlc = SteamAPI_ISteamApps_BIsDlcInstalled(_steamApps, appId);
                        return isDlc ? "DLC" : "Game";

                    case "state":
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

            AppLogger.LogDebug($"RequestUserStats for game {gameId} - stats are auto-synchronized in modern SDK");
            return true;
        }

        public void RunCallbacks()
        {
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
            AppLogger.LogDebug("RegisterUserStatsCallback called - modern SDK handles callbacks automatically");
        }

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

        internal static IntPtr ResolveSteamApi(string libraryName, System.Reflection.Assembly assembly, DllImportSearchPath? searchPath)
        {
            if (!libraryName.Equals("steam_api64", StringComparison.OrdinalIgnoreCase))
                return IntPtr.Zero;

            try
            {
                AppLogger.LogDebug("Attempting to locate steam_api64.dll");

                var searchPaths = GetSteamApiSearchPaths();

                foreach (var path in searchPaths)
                {
                    if (System.IO.File.Exists(path))
                    {
                        AppLogger.LogDebug($"Found steam_api64.dll at: {path}");
                        try
                        {
                            var directory = System.IO.Path.GetDirectoryName(path);
                            if (!string.IsNullOrEmpty(directory))
                            {
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

            string? steamPath = GetSteamInstallPath();
            if (!string.IsNullOrEmpty(steamPath))
            {
                paths.Add(System.IO.Path.Combine(steamPath, "steam_api64.dll"));
                paths.Add(System.IO.Path.Combine(steamPath, "bin", "steam_api64.dll"));
            }

            var gameDirectories = FindSteamGameDirectories();
            foreach (var gameDir in gameDirectories)
            {
                var apiPath = System.IO.Path.Combine(gameDir, "steam_api64.dll");
                if (System.IO.File.Exists(apiPath))
                {
                    paths.Add(apiPath);
                }
            }

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

                var libraryFoldersPath = System.IO.Path.Combine(steamPath, "config", "libraryfolders.vdf");
                if (System.IO.File.Exists(libraryFoldersPath))
                {
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

        // Modern Steam API LibraryImport declarations

        // Initialization
        [LibraryImport("steam_api64", StringMarshalling = StringMarshalling.Utf8)]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        private static partial ESteamAPIInitResult SteamAPI_InitFlat(
            [Out, MarshalAs(UnmanagedType.LPArray, SizeConst = 1024)] byte[] pOutErrMsg);

        [LibraryImport("steam_api64")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        private static partial void SteamAPI_Shutdown();

        [LibraryImport("steam_api64")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        private static partial void SteamAPI_RunCallbacks();

        // Interface accessors
        [LibraryImport("steam_api64")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        private static partial IntPtr SteamAPI_SteamUserStats();

        [LibraryImport("steam_api64")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        private static partial IntPtr SteamAPI_SteamApps();

        [LibraryImport("steam_api64")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        private static partial IntPtr SteamAPI_SteamUser();

        [LibraryImport("steam_api64")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        private static partial IntPtr SteamAPI_SteamUtils();

        // User methods
        [LibraryImport("steam_api64")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool SteamAPI_ISteamUser_BLoggedOn(IntPtr steamUser);

        [LibraryImport("steam_api64")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        private static partial ulong SteamAPI_ISteamUser_GetSteamID(IntPtr steamUser);

        [LibraryImport("steam_api64")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        private static partial EUserHasLicenseForAppResult SteamAPI_ISteamUser_UserHasLicenseForApp(IntPtr steamUser, ulong steamID, uint appID);

        // Utils methods
        [LibraryImport("steam_api64")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        private static partial IntPtr SteamAPI_ISteamUtils_GetSteamUILanguage(IntPtr steamUtils);

        // Apps methods
        [LibraryImport("steam_api64")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool SteamAPI_ISteamApps_BIsSubscribed(IntPtr steamApps);

        [LibraryImport("steam_api64")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool SteamAPI_ISteamApps_BIsSubscribedApp(IntPtr steamApps, uint appID);

        [LibraryImport("steam_api64")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool SteamAPI_ISteamApps_BIsDlcInstalled(IntPtr steamApps, uint appID);

        [LibraryImport("steam_api64")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool SteamAPI_ISteamApps_BIsAppInstalled(IntPtr steamApps, uint appID);

        // UserStats methods
        [LibraryImport("steam_api64", StringMarshalling = StringMarshalling.Utf8)]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool SteamAPI_ISteamUserStats_GetAchievementAndUnlockTime(IntPtr steamUserStats, string pchName, [MarshalAs(UnmanagedType.Bool)] out bool pbAchieved, out uint punUnlockTime);

        [LibraryImport("steam_api64", StringMarshalling = StringMarshalling.Utf8)]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool SteamAPI_ISteamUserStats_SetAchievement(IntPtr steamUserStats, string pchName);

        [LibraryImport("steam_api64", StringMarshalling = StringMarshalling.Utf8)]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool SteamAPI_ISteamUserStats_ClearAchievement(IntPtr steamUserStats, string pchName);

        [LibraryImport("steam_api64", StringMarshalling = StringMarshalling.Utf8)]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool SteamAPI_ISteamUserStats_GetStatInt32(IntPtr steamUserStats, string pchName, out int pData);

        [LibraryImport("steam_api64", StringMarshalling = StringMarshalling.Utf8)]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool SteamAPI_ISteamUserStats_GetStatFloat(IntPtr steamUserStats, string pchName, out float pData);

        [LibraryImport("steam_api64", StringMarshalling = StringMarshalling.Utf8)]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool SteamAPI_ISteamUserStats_SetStatInt32(IntPtr steamUserStats, string pchName, int nData);

        [LibraryImport("steam_api64", StringMarshalling = StringMarshalling.Utf8)]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool SteamAPI_ISteamUserStats_SetStatFloat(IntPtr steamUserStats, string pchName, float fData);

        [LibraryImport("steam_api64")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool SteamAPI_ISteamUserStats_StoreStats(IntPtr steamUserStats);

        [LibraryImport("steam_api64")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool SteamAPI_ISteamUserStats_ResetAllStats(IntPtr steamUserStats, [MarshalAs(UnmanagedType.Bool)] bool bAchievementsToo);

        // Enums

        public enum ESteamAPIInitResult
        {
            k_ESteamAPIInitResult_OK = 0,
            k_ESteamAPIInitResult_FailedGeneric = 1,
            k_ESteamAPIInitResult_NoSteamClient = 2,
            k_ESteamAPIInitResult_VersionMismatch = 3,
        }

        public enum EUserHasLicenseForAppResult
        {
            k_EUserHasLicenseResultHasLicense = 0,
            k_EUserHasLicenseResultDoesNotHaveLicense = 1,
            k_EUserHasLicenseResultNoAuth = 2,
        }
    }
}
