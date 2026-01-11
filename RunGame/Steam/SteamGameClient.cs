using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Win32;
using CommonUtilities;

namespace RunGame.Steam
{
    public sealed class SteamGameClient : IDisposable, ISteamUserStats
    {
        private readonly Timer? _callbackTimer;
        private readonly IntPtr _client;
        private readonly IntPtr _apps008;
        private readonly IntPtr _apps001;
        private readonly IntPtr _userStats;
        private readonly IntPtr _user006;
        private readonly int _pipe;
        private readonly int _user;
        private readonly long _gameId;

        // ISteamClient018 delegates
        private readonly CreateSteamPipeDelegate? _createSteamPipe;
        private readonly ConnectToGlobalUserDelegate? _connectToGlobalUser;
        private readonly GetISteamAppsDelegate? _getISteamApps;
        private readonly GetISteamUserStatsDelegate? _getISteamUserStats;
        private readonly GetISteamUserDelegate? _getISteamUser;
        private readonly GetISteamUtilsDelegate? _getISteamUtils;
        private readonly ReleaseUserDelegate? _releaseUser;
        private readonly ReleaseSteamPipeDelegate? _releaseSteamPipe;

        // ISteamApps008 delegates
        private readonly IsSubscribedAppDelegate? _isSubscribedApp;
        private readonly GetAppDataDelegate? _getAppData;

        // ISteamUserStats013 delegates
        private readonly RequestUserStatsDelegate? _requestUserStats;
        private readonly GetAchievementAndUnlockTimeDelegate? _getAchievementAndUnlockTime;
        private readonly SetAchievementDelegate? _setAchievement;
        private readonly ClearAchievementDelegate? _clearAchievement;
        private readonly GetStatIntDelegate? _getStatInt;
        private readonly GetStatFloatDelegate? _getStatFloat;
        private readonly SetStatIntDelegate? _setStatInt;
        private readonly SetStatFloatDelegate? _setStatFloat;
        private readonly StoreStatsDelegate? _storeStats;
        private readonly ResetAllStatsDelegate? _resetAllStats;

        // ISteamUser012 delegates
        private readonly GetSteamIdDelegate? _getSteamId;

        // ISteamUtils delegates
        private readonly GetAppIdDelegate? _getAppId;
        private readonly GetSteamUILanguageDelegate? _getSteamUILanguage;
        private readonly IntPtr _utils;

        private readonly List<Action<UserStatsReceived>> _userStatsCallbacks = new();

        static SteamGameClient()
        {
            NativeLibrary.SetDllImportResolver(typeof(SteamGameClient).Assembly, ResolveSteamClient);
        }

        public bool Initialized { get; }
        
        public string GetCurrentGameLanguage()
        {
            // Default to English if we can't determine the language
            try
            {
                if (Initialized && _getSteamUILanguage != null && _utils != IntPtr.Zero)
                {
                    var ptr = _getSteamUILanguage(_utils);
                    if (ptr != IntPtr.Zero)
                    {
                        var lang = Marshal.PtrToStringAnsi(ptr);
                        if (!string.IsNullOrEmpty(lang))
                            return lang;
                    }
                }
            }
            catch (Exception ex)
            {
                AppLogger.LogDebug($"Error getting Steam UI language: {ex.Message}");
            }

            return "english";
        }

        public SteamGameClient(long gameId)
        {
            _gameId = gameId;
            
            try
            {
                AppLogger.LogDebug($"Initializing SteamGameClient for game {gameId}");
                
                // Check if Steam is running
                if (!IsSteamRunning())
                {
                    AppLogger.LogDebug("Steam is not running - cannot initialize Steam client");
                    return;
                }
                
                _client = Steam_CreateInterface("SteamClient018", IntPtr.Zero);
                AppLogger.LogDebug($"Steam_CreateInterface result: {_client}");
                
                if (_client != IntPtr.Zero)
                {
                    // Get ISteamClient018 vtable
                    IntPtr vtable = Marshal.ReadIntPtr(_client);
                    AppLogger.LogDebug($"ISteamClient018 vtable: {vtable}");
                    
                    _createSteamPipe = Marshal.GetDelegateForFunctionPointer<CreateSteamPipeDelegate>(Marshal.ReadIntPtr(vtable + IntPtr.Size * 0));
                    _releaseSteamPipe = Marshal.GetDelegateForFunctionPointer<ReleaseSteamPipeDelegate>(Marshal.ReadIntPtr(vtable + IntPtr.Size * 1));
                    _connectToGlobalUser = Marshal.GetDelegateForFunctionPointer<ConnectToGlobalUserDelegate>(Marshal.ReadIntPtr(vtable + IntPtr.Size * 2));
                    _releaseUser = Marshal.GetDelegateForFunctionPointer<ReleaseUserDelegate>(Marshal.ReadIntPtr(vtable + IntPtr.Size * 4));
                    _getISteamUser = Marshal.GetDelegateForFunctionPointer<GetISteamUserDelegate>(Marshal.ReadIntPtr(vtable + IntPtr.Size * 5));
                    _getISteamUtils = Marshal.GetDelegateForFunctionPointer<GetISteamUtilsDelegate>(Marshal.ReadIntPtr(vtable + IntPtr.Size * 9));
                    _getISteamUserStats = Marshal.GetDelegateForFunctionPointer<GetISteamUserStatsDelegate>(Marshal.ReadIntPtr(vtable + IntPtr.Size * 13));
                    _getISteamApps = Marshal.GetDelegateForFunctionPointer<GetISteamAppsDelegate>(Marshal.ReadIntPtr(vtable + IntPtr.Size * 15));

                    if (_createSteamPipe != null && _connectToGlobalUser != null)
                    {
                        _pipe = _createSteamPipe(_client);
                        AppLogger.LogDebug($"Steam pipe created: {_pipe}");
                        
                        if (_pipe != 0)
                        {
                            _user = _connectToGlobalUser(_client, _pipe);
                            AppLogger.LogDebug($"Connected to global user: {_user}");
                            
                            if (_user != 0)
                            {
                                _apps008 = _getISteamApps?.Invoke(_client, _user, _pipe, "STEAMAPPS_INTERFACE_VERSION008") ?? IntPtr.Zero;
                                _apps001 = _getISteamApps?.Invoke(_client, _user, _pipe, "STEAMAPPS_INTERFACE_VERSION001") ?? IntPtr.Zero;
                                _utils = _getISteamUtils?.Invoke(_client, _pipe, "SteamUtils005") ?? IntPtr.Zero;
                                
                                // Try different UserStats interface versions
                                string[] userStatsVersions = { "STEAMUSERSTATS_INTERFACE_VERSION013", "STEAMUSERSTATS_INTERFACE_VERSION012", "STEAMUSERSTATS_INTERFACE_VERSION011" };
                                foreach (var version in userStatsVersions)
                                {
                                    _userStats = _getISteamUserStats?.Invoke(_client, _user, _pipe, version) ?? IntPtr.Zero;
                                    if (_userStats != IntPtr.Zero)
                                    {
                                        AppLogger.LogDebug($"Successfully obtained {version} interface: {_userStats}");
                                        break;
                                    }
                                    else
                                    {
                                        AppLogger.LogDebug($"Failed to get {version} interface");
                                    }
                                }
                                
                                // Try different SteamUser interface versions starting with the one SAM uses
                                string[] userVersions = { "SteamUser012", "SteamUser020", "SteamUser019", "SteamUser018" };
                                foreach (var version in userVersions)
                                {
                                    _user006 = _getISteamUser?.Invoke(_client, _user, _pipe, version) ?? IntPtr.Zero;
                                    if (_user006 != IntPtr.Zero)
                                    {
                                        AppLogger.LogDebug($"Successfully obtained {version} interface: {_user006}");
                                        break;
                                    }
                                    else
                                    {
                                        AppLogger.LogDebug($"Failed to get {version} interface");
                                    }
                                }

                                AppLogger.LogDebug($"Steam interfaces - Apps008: {_apps008}, Apps001: {_apps001}, UserStats: {_userStats}, User006: {_user006}, Utils: {_utils}");

                                // Initialize ISteamApps delegates using correct offsets from ISteamApps008
                                if (_apps008 != IntPtr.Zero)
                                {
                                    IntPtr appsVTable = Marshal.ReadIntPtr(_apps008);
                                    _isSubscribedApp = Marshal.GetDelegateForFunctionPointer<IsSubscribedAppDelegate>(Marshal.ReadIntPtr(appsVTable + IntPtr.Size * 6));
                                    AppLogger.LogDebug($"ISteamApps008 IsSubscribedApp delegate initialized");
                                }
                                
                                if (_apps001 != IntPtr.Zero)
                                {
                                    IntPtr apps1VTable = Marshal.ReadIntPtr(_apps001);
                                    _getAppData = Marshal.GetDelegateForFunctionPointer<GetAppDataDelegate>(Marshal.ReadIntPtr(apps1VTable));
                                }

                                // Initialize ISteamUserStats delegates using correct offsets from ISteamUserStats013
                                if (_userStats != IntPtr.Zero)
                                {
                                    IntPtr userStatsVTable = Marshal.ReadIntPtr(_userStats);
                                    _getStatFloat = Marshal.GetDelegateForFunctionPointer<GetStatFloatDelegate>(Marshal.ReadIntPtr(userStatsVTable + IntPtr.Size * 0));
                                    _getStatInt = Marshal.GetDelegateForFunctionPointer<GetStatIntDelegate>(Marshal.ReadIntPtr(userStatsVTable + IntPtr.Size * 1));
                                    _setStatFloat = Marshal.GetDelegateForFunctionPointer<SetStatFloatDelegate>(Marshal.ReadIntPtr(userStatsVTable + IntPtr.Size * 2));
                                    _setStatInt = Marshal.GetDelegateForFunctionPointer<SetStatIntDelegate>(Marshal.ReadIntPtr(userStatsVTable + IntPtr.Size * 3));
                                    _setAchievement = Marshal.GetDelegateForFunctionPointer<SetAchievementDelegate>(Marshal.ReadIntPtr(userStatsVTable + IntPtr.Size * 6));
                                    _clearAchievement = Marshal.GetDelegateForFunctionPointer<ClearAchievementDelegate>(Marshal.ReadIntPtr(userStatsVTable + IntPtr.Size * 7));
                                    _getAchievementAndUnlockTime = Marshal.GetDelegateForFunctionPointer<GetAchievementAndUnlockTimeDelegate>(Marshal.ReadIntPtr(userStatsVTable + IntPtr.Size * 8));
                                    _storeStats = Marshal.GetDelegateForFunctionPointer<StoreStatsDelegate>(Marshal.ReadIntPtr(userStatsVTable + IntPtr.Size * 9));
                                    _requestUserStats = Marshal.GetDelegateForFunctionPointer<RequestUserStatsDelegate>(Marshal.ReadIntPtr(userStatsVTable + IntPtr.Size * 15));
                                    _resetAllStats = Marshal.GetDelegateForFunctionPointer<ResetAllStatsDelegate>(Marshal.ReadIntPtr(userStatsVTable + IntPtr.Size * 20));
                                }

                                // Initialize ISteamUser delegates
                                if (_user006 != IntPtr.Zero)
                                {
                                    IntPtr userVTable = Marshal.ReadIntPtr(_user006);
                                    _getSteamId = Marshal.GetDelegateForFunctionPointer<GetSteamIdDelegate>(Marshal.ReadIntPtr(userVTable + IntPtr.Size * 2));
                                }

                                // Initialize ISteamUtils delegates
                                if (_utils != IntPtr.Zero)
                                {
                                    IntPtr utilsVTable = Marshal.ReadIntPtr(_utils);
                                    _getAppId = Marshal.GetDelegateForFunctionPointer<GetAppIdDelegate>(Marshal.ReadIntPtr(utilsVTable + IntPtr.Size * 9));
                                    _getSteamUILanguage = Marshal.GetDelegateForFunctionPointer<GetSteamUILanguageDelegate>(Marshal.ReadIntPtr(utilsVTable + IntPtr.Size * 22));
                                    AppLogger.LogDebug($"ISteamUtils005 GetAppId delegate initialized");
                                    AppLogger.LogDebug($"ISteamUtils005 GetSteamUILanguage delegate initialized");
                                }

                                // Check if we can get Steam ID to verify user is logged in
                                ulong steamId = 0;
                                if (_getSteamId != null && _user006 != IntPtr.Zero)
                                {
                                    try
                                    {
                                        _getSteamId(_user006, out steamId);
                                        AppLogger.LogDebug($"Current Steam ID: {steamId}");
                                    }
                                    catch (Exception ex)
                                    {
                                        AppLogger.LogDebug($"Error getting Steam ID: {ex.Message}");
                                        steamId = 0;
                                    }
                                }
                                
                                // Verify AppID matches - this is key for Legacy SAM compatibility
                                uint currentAppId = 0;
                                bool appIdMatches = false;
                                if (_getAppId != null && _utils != IntPtr.Zero)
                                {
                                    try
                                    {
                                        currentAppId = _getAppId(_utils);
                                        AppLogger.LogDebug($"Steam Utils GetAppId returned: {currentAppId}");
                                        appIdMatches = currentAppId == (uint)_gameId;
                                    }
                                    catch (Exception ex)
                                    {
                                        AppLogger.LogDebug($"Error getting AppId from Steam Utils: {ex.Message}");
                                        // For now, continue without AppID verification
                                        appIdMatches = true;
                                    }
                                }
                                else
                                {
                                    AppLogger.LogDebug("SteamUtils not available, skipping AppID verification");
                                    appIdMatches = true; // Continue without verification for now
                                }

                                AppLogger.LogDebug($"AppID verification - Expected: {_gameId}, Current: {currentAppId}, Matches: {appIdMatches}");

                                // Now we can safely require Steam ID since the function signature is correct
                                Initialized = _userStats != IntPtr.Zero && _requestUserStats != null && _getSteamId != null && steamId != 0 && appIdMatches;
                                AppLogger.LogDebug($"Steam client initialization result: {Initialized}");
                                
                                if (!Initialized)
                                {
                                    AppLogger.LogDebug($"Init failed - UserStats: {_userStats != IntPtr.Zero}, RequestUserStats: {_requestUserStats != null}, GetSteamId: {_getSteamId != null}, SteamId: {steamId}, AppIdMatches: {appIdMatches}");
                                    if (steamId == 0)
                                    {
                                        AppLogger.LogDebug("User may not be logged in to Steam");
                                    }
                                    if (!appIdMatches)
                                    {
                                        AppLogger.LogDebug($"AppID mismatch! Steam thinks current app is {currentAppId}, but we need {_gameId}");
                                        AppLogger.LogDebug("This suggests the SteamAppId environment variable is not working correctly");
                                    }
                                }
                                else
                                {
                                    AppLogger.LogDebug($"Steam client successfully initialized - UserStats: {_userStats}, SteamId: {steamId}");
                                }
                            }
                            else
                            {
                                AppLogger.LogDebug("Failed to connect to global user");
                            }
                        }
                        else
                        {
                            AppLogger.LogDebug("Failed to create Steam pipe");
                        }
                    }
                    else
                    {
                        AppLogger.LogDebug($"Required delegates not found - CreateSteamPipe: {_createSteamPipe != null}, ConnectToGlobalUser: {_connectToGlobalUser != null}");
                    }
                }
                else
                {
                    AppLogger.LogDebug("Failed to create Steam client interface");
                }

                if (Initialized)
                {
                    _callbackTimer = new Timer(_ => RunCallbacks(), null, 0, 100);
                    AppLogger.LogDebug("Steam client successfully initialized and callback timer started");
                }
            }
            catch (Exception ex)
            {
                Initialized = false;
                AppLogger.LogDebug($"Steam API init threw: {ex}");
            }
        }

        public bool IsSubscribedApp(uint gameId)
        {
            if (!Initialized || _isSubscribedApp == null || _apps008 == IntPtr.Zero)
            {
                AppLogger.LogDebug($"IsSubscribedApp failed: not initialized or missing delegates");
                return false;
            }

            try
            {
                bool result = _isSubscribedApp(_apps008, gameId);
                AppLogger.LogDebug($"IsSubscribedApp for game {gameId}: {result}");
                return result;
            }
            catch (Exception ex)
            {
                AppLogger.LogDebug($"Exception in IsSubscribedApp: {ex.Message}");
                return false;
            }
        }

        public bool RequestUserStats(uint gameId)
        {
            if (!Initialized)
            {
                AppLogger.LogDebug($"SteamGameClient.RequestUserStats failed: client not initialized (gameId={gameId})");
                return false;
            }
            if (_requestUserStats == null)
            {
                AppLogger.LogDebug("SteamGameClient.RequestUserStats failed: _requestUserStats delegate is null");
                return false;
            }
            if (_userStats == IntPtr.Zero)
            {
                AppLogger.LogDebug("SteamGameClient.RequestUserStats failed: _userStats interface is null");
                return false;
            }

            // First check if user owns the game
            if (!IsSubscribedApp(gameId))
            {
                AppLogger.LogDebug($"User does not own game {gameId} - RequestUserStats will likely fail");
                // Continue anyway, as some games might still work
            }

            try
            {
                // Try to get Steam ID, but don't fail if it doesn't work
                ulong steamId = 0;
                if (_getSteamId != null && _user006 != IntPtr.Zero)
                {
                    try
                    {
                        _getSteamId(_user006, out steamId);
                    }
                    catch (Exception ex)
                    {
                        AppLogger.LogDebug($"Warning: Could not get Steam ID: {ex.Message}");
                        // Try using a default Steam ID or the current user
                        steamId = 0x0110000100000000UL; // Generic Steam ID format
                    }
                }

                AppLogger.LogDebug($"SteamGameClient.RequestUserStats calling for game {gameId} (steamId={steamId})");
                bool result = _requestUserStats(_userStats, steamId) != 0;
                AppLogger.LogDebug($"SteamGameClient.RequestUserStats result for game {gameId}: {result}");
                return result;
            }
            catch (Exception ex)
            {
                AppLogger.LogDebug($"Exception in RequestUserStats: {ex.Message}");
                return false;
            }
        }

        public bool GetAchievementAndUnlockTime(string id, out bool achieved, out uint unlockTime)
        {
            achieved = false;
            unlockTime = 0;
            
            if (!Initialized || _getAchievementAndUnlockTime == null) return false;
            
            return _getAchievementAndUnlockTime(_userStats, id, out achieved, out unlockTime);
        }

        public bool SetAchievement(string id, bool achieved)
        {
            AppLogger.LogAchievementSet(id, achieved, AppLogger.IsDebugMode);
            
            if (!Initialized) return false;
            
#if DEBUG
            // Debug 模�?下�?實�?寫入，只記�?
            return true;
#else
            // Release 模�?下實?�寫??
            if (achieved)
            {
                if (_setAchievement == null) return false;
                return _setAchievement(_userStats, id);
            }
            else
            {
                if (_clearAchievement == null) return false;
                return _clearAchievement(_userStats, id);
            }
#endif
        }

        public bool GetStatValue(string name, out int value)
        {
            value = 0;
            if (!Initialized || _getStatInt == null) return false;
            
            return _getStatInt(_userStats, name, out value);
        }

        public bool GetStatValue(string name, out float value)
        {
            value = 0.0f;
            if (!Initialized || _getStatFloat == null) return false;
            
            return _getStatFloat(_userStats, name, out value);
        }

        public bool SetStatValue(string name, int value)
        {
            AppLogger.LogStatSet(name, value, AppLogger.IsDebugMode);
            
            if (!Initialized || _setStatInt == null) return false;
            
#if DEBUG
            // Debug 模�?下�?實�?寫入，只記�?
            return true;
#else
            // Release 模�?下實?�寫??
            return _setStatInt(_userStats, name, value);
#endif
        }

        public bool SetStatValue(string name, float value)
        {
            AppLogger.LogStatSet(name, value, AppLogger.IsDebugMode);
            
            if (!Initialized || _setStatFloat == null) return false;
            
#if DEBUG
            // Debug 模�?下�?實�?寫入，只記�?
            return true;
#else
            // Release 模�?下實?�寫??
            return _setStatFloat(_userStats, name, value);
#endif
        }

        public bool StoreStats()
        {
            AppLogger.LogStoreStats(AppLogger.IsDebugMode);
            
            if (!Initialized || _storeStats == null) return false;
            
#if DEBUG
            // Debug 模�?下�?實�?寫入，只記�?
            return true;
#else
            // Release 模�?下實?�寫??
            return _storeStats(_userStats);
#endif
        }

        public bool ResetAllStats(bool achievementsToo)
        {
            AppLogger.LogResetAllStats(achievementsToo, AppLogger.IsDebugMode);
            
            if (!Initialized || _resetAllStats == null) return false;
            
#if DEBUG
            // Debug 模�?下�?實�?寫入，只記�?
            return true;
#else
            // Release 模�?下實?�寫??
            return _resetAllStats(_userStats, achievementsToo);
#endif
        }

        public void RunCallbacks()
        {
            try
            {
                while (Steam_BGetCallback(_pipe, out var msg, out _))
                {
                    // Only log important callbacks to reduce noise
                    if (msg.Id == 1101 || msg.Id == 1040015 || msg.Id == 1040044)
                    {
                        AppLogger.LogDebug($"Steam callback received - ID: {msg.Id}, GameId: {_gameId}");
                    }
                    
                    if (msg.Id == 1101) // UserStatsReceived callback
                    {
                        try
                        {
                            var userStatsReceived = Marshal.PtrToStructure<UserStatsReceived>(msg.ParamPointer);
                            AppLogger.LogDebug($"UserStatsReceived - GameId: {userStatsReceived.GameId}, Result: {userStatsReceived.Result}, UserId: {userStatsReceived.UserId}");
                            
                            foreach (var callback in _userStatsCallbacks)
                            {
                                try
                                {
                                    callback(userStatsReceived);
                                }
                                catch (Exception ex)
                                {
                                    AppLogger.LogDebug($"Error in UserStatsReceived callback: {ex.Message}");
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            AppLogger.LogDebug($"Error processing UserStatsReceived: {ex.Message}");
                        }
                    }
                    
                    Steam_FreeLastCallback(_pipe);
                }
            }
            catch (Exception ex)
            {
                AppLogger.LogDebug($"Error in RunCallbacks: {ex.Message}");
            }
        }

        public void RegisterUserStatsCallback(Action<UserStatsReceived> callback)
        {
            _userStatsCallbacks.Add(callback);
        }

        public string? GetAppData(uint id, string key)
        {
            if (!Initialized || _getAppData == null || _apps001 == IntPtr.Zero) return null;

            const int bufferSize = 4096;
            byte[] buffer = ArrayPool<byte>.Shared.Rent(bufferSize);
            try
            {
                var handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
                try
                {
                    int len = _getAppData(_apps001, id, key, handle.AddrOfPinnedObject(), bufferSize);
                    if (len <= 0) return null;

                    int terminator = Array.IndexOf<byte>(buffer, 0, 0, len);
                    if (terminator >= 0) len = terminator;
                    
                    return Encoding.UTF8.GetString(buffer, 0, len);
                }
                finally
                {
                    handle.Free();
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
            }
        }

        public void Dispose()
        {
            // Stop callback timer first to prevent race conditions
            if (_callbackTimer != null)
            {
                // Dispose the timer and wait for any running callbacks to complete
                _callbackTimer.Dispose();

                // Small delay to ensure callback completes
                // Timer.Dispose() waits for callbacks to complete, but add extra safety
                System.Threading.Thread.Sleep(50);
            }

            // Now safe to release Steam resources
            if (Initialized)
            {
                try
                {
                    _releaseUser?.Invoke(_client, _pipe, _user);
                    _releaseSteamPipe?.Invoke(_client, _pipe);
                    AppLogger.LogDebug("Steam client resources released successfully");
                }
                catch (Exception ex)
                {
                    AppLogger.LogDebug($"Error releasing Steam resources: {ex.Message}");
                }
            }
        }

        private static IntPtr ResolveSteamClient(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
        {
            try
            {
                if (!libraryName.Equals("steamclient64", StringComparison.OrdinalIgnoreCase))
                    return IntPtr.Zero;

                AppLogger.LogDebug("Attempting to determine Steam install path");
                string? installPath = GetSteamPath();
                if (string.IsNullOrEmpty(installPath)) return IntPtr.Zero;

                string libraryPath = Path.Combine(installPath, "steamclient64.dll");
                if (!File.Exists(libraryPath))
                {
                    libraryPath = Path.Combine(installPath, "bin", "steamclient64.dll");
                }
                
                if (!File.Exists(libraryPath)) 
                {
                    AppLogger.LogDebug($"steamclient64.dll not found at expected paths in {installPath}");
                    return IntPtr.Zero;
                }

                Native.AddDllDirectory(installPath);
                Native.AddDllDirectory(Path.Combine(installPath, "bin"));
                return Native.LoadLibraryEx(libraryPath, IntPtr.Zero,
                    Native.LoadLibrarySearchDefaultDirs | Native.LoadLibrarySearchUserDirs);
            }
            catch (Exception ex)
            {
                AppLogger.LogDebug($"Error in ResolveSteamClient: {ex.Message}");
                return IntPtr.Zero;
            }
        }

        private static bool IsSteamRunning()
        {
            try
            {
                var steamProcesses = Process.GetProcessesByName("steam");
                bool isRunning = steamProcesses.Length > 0;
                AppLogger.LogDebug($"Steam process check: {isRunning} (found {steamProcesses.Length} processes)");
                return isRunning;
            }
            catch (Exception ex)
            {
                AppLogger.LogDebug($"Error checking Steam process: {ex.Message}");
                return false;
            }
        }

        private static string? GetSteamPath()
        {
            try
            {
                AppLogger.LogDebug("Searching registry for Steam install path");
                const string subKey = @"Software\\Valve\\Steam";

                // Check HKLM 64-bit and 32-bit (WOW6432Node) views
                foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
                {
                    try
                    {
                        using var key = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view).OpenSubKey(subKey);
                        var path = key?.GetValue("InstallPath") as string;
                        if (!string.IsNullOrEmpty(path))
                        {
                            AppLogger.LogDebug($"Steam install path found: {path}");
                            return path;
                        }
                    }
                    catch (Exception ex)
                    {
                        AppLogger.LogDebug($"Error accessing HKLM registry (view: {view}): {ex.Message}");
                    }
                }

                // Fall back to HKCU
                foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
                {
                    try
                    {
                        using var key = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, view).OpenSubKey(subKey);
                        var path = key?.GetValue("InstallPath") as string;
                        if (!string.IsNullOrEmpty(path))
                        {
                            AppLogger.LogDebug($"Steam install path found: {path}");
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
                AppLogger.LogDebug($"Error in GetSteamPath: {ex.Message}");
                return null;
            }
        }

        // P/Invoke and delegate declarations
        [StructLayout(LayoutKind.Sequential)]
        private struct CallbackMsg
        {
            public int User;
            public int Id;
            public IntPtr ParamPointer;
            public int ParamSize;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct UserStatsReceived
        {
            public ulong GameId;
            public int Result;
            public ulong UserId;
        }

        private static class Native
        {
            [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
            internal static extern IntPtr LoadLibraryEx(string lpFileName, IntPtr hFile, uint dwFlags);

            [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
            internal static extern IntPtr AddDllDirectory(string lpPathName);

            internal const uint LoadLibrarySearchDefaultDirs = 0x00001000;
            internal const uint LoadLibrarySearchUserDirs = 0x00000400;
        }

        [DllImport("steamclient64", CallingConvention = CallingConvention.Cdecl,
            CharSet = CharSet.Ansi, EntryPoint = "CreateInterface")]
        private static extern IntPtr Steam_CreateInterface(string version, IntPtr returnCode);

        [DllImport("steamclient64", CallingConvention = CallingConvention.Cdecl, EntryPoint = "Steam_BGetCallback")]
        [return: MarshalAs(UnmanagedType.I1)]
        private static extern bool Steam_BGetCallback(int pipe, out CallbackMsg message, out int call);

        [DllImport("steamclient64", CallingConvention = CallingConvention.Cdecl, EntryPoint = "Steam_FreeLastCallback")]
        [return: MarshalAs(UnmanagedType.I1)]
        private static extern bool Steam_FreeLastCallback(int pipe);

        // ISteamClient018 delegates
        [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
        private delegate int CreateSteamPipeDelegate(IntPtr self);

        [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
        private delegate int ConnectToGlobalUserDelegate(IntPtr self, int pipe);

        [UnmanagedFunctionPointer(CallingConvention.ThisCall, CharSet = CharSet.Ansi)]
        private delegate IntPtr GetISteamAppsDelegate(IntPtr self, int user, int pipe, string version);

        [UnmanagedFunctionPointer(CallingConvention.ThisCall, CharSet = CharSet.Ansi)]
        private delegate IntPtr GetISteamUserStatsDelegate(IntPtr self, int user, int pipe, string version);

        [UnmanagedFunctionPointer(CallingConvention.ThisCall, CharSet = CharSet.Ansi)]
        private delegate IntPtr GetISteamUserDelegate(IntPtr self, int user, int pipe, string version);

        [UnmanagedFunctionPointer(CallingConvention.ThisCall, CharSet = CharSet.Ansi)]
        private delegate IntPtr GetISteamUtilsDelegate(IntPtr self, int pipe, string version);

        [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
        private delegate void ReleaseUserDelegate(IntPtr self, int pipe, int user);

        [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
        private delegate void ReleaseSteamPipeDelegate(IntPtr self, int pipe);

        // ISteamApps delegates
        [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
        [return: MarshalAs(UnmanagedType.I1)]
        private delegate bool IsSubscribedAppDelegate(IntPtr self, uint appId);

        [UnmanagedFunctionPointer(CallingConvention.ThisCall, CharSet = CharSet.Ansi)]
        private delegate int GetAppDataDelegate(IntPtr self, uint appId,
            [MarshalAs(UnmanagedType.LPStr)] string key, IntPtr value, int valueBufferSize);

        // ISteamUserStats delegates
        [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
        private delegate ulong RequestUserStatsDelegate(IntPtr self, ulong steamIdUser);

        [UnmanagedFunctionPointer(CallingConvention.ThisCall, CharSet = CharSet.Ansi)]
        [return: MarshalAs(UnmanagedType.I1)]
        private delegate bool GetAchievementAndUnlockTimeDelegate(IntPtr self,
            [MarshalAs(UnmanagedType.LPStr)] string name, out bool achieved, out uint unlockTime);

        [UnmanagedFunctionPointer(CallingConvention.ThisCall, CharSet = CharSet.Ansi)]
        [return: MarshalAs(UnmanagedType.I1)]
        private delegate bool SetAchievementDelegate(IntPtr self,
            [MarshalAs(UnmanagedType.LPStr)] string name);

        [UnmanagedFunctionPointer(CallingConvention.ThisCall, CharSet = CharSet.Ansi)]
        [return: MarshalAs(UnmanagedType.I1)]
        private delegate bool ClearAchievementDelegate(IntPtr self,
            [MarshalAs(UnmanagedType.LPStr)] string name);

        [UnmanagedFunctionPointer(CallingConvention.ThisCall, CharSet = CharSet.Ansi)]
        [return: MarshalAs(UnmanagedType.I1)]
        private delegate bool GetStatIntDelegate(IntPtr self,
            [MarshalAs(UnmanagedType.LPStr)] string name, out int value);

        [UnmanagedFunctionPointer(CallingConvention.ThisCall, CharSet = CharSet.Ansi)]
        [return: MarshalAs(UnmanagedType.I1)]
        private delegate bool GetStatFloatDelegate(IntPtr self,
            [MarshalAs(UnmanagedType.LPStr)] string name, out float value);

        [UnmanagedFunctionPointer(CallingConvention.ThisCall, CharSet = CharSet.Ansi)]
        [return: MarshalAs(UnmanagedType.I1)]
        private delegate bool SetStatIntDelegate(IntPtr self,
            [MarshalAs(UnmanagedType.LPStr)] string name, int value);

        [UnmanagedFunctionPointer(CallingConvention.ThisCall, CharSet = CharSet.Ansi)]
        [return: MarshalAs(UnmanagedType.I1)]
        private delegate bool SetStatFloatDelegate(IntPtr self,
            [MarshalAs(UnmanagedType.LPStr)] string name, float value);

        [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
        [return: MarshalAs(UnmanagedType.I1)]
        private delegate bool StoreStatsDelegate(IntPtr self);

        [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
        [return: MarshalAs(UnmanagedType.I1)]
        private delegate bool ResetAllStatsDelegate(IntPtr self, bool achievementsToo);

        [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
        private delegate void GetSteamIdDelegate(IntPtr self, out ulong steamId);

        [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
        private delegate uint GetAppIdDelegate(IntPtr self);

        [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
        private delegate IntPtr GetSteamUILanguageDelegate(IntPtr self);
    }
}
