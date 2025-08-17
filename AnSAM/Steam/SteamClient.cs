using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Win32;

namespace AnSAM.Steam
{
    /// <summary>
    /// Minimal Steamworks client wrapper.
    /// Initializes the Steam API, pumps callbacks on a timer,
    /// and exposes a couple of convenience helpers.
    /// </summary>
    public sealed class SteamClient : IDisposable
    {
        private readonly Timer? _callbackTimer;
        private readonly IntPtr _apps;
        private readonly bool _initialized;

        static SteamClient()
        {
            NativeLibrary.SetDllImportResolver(typeof(SteamClient).Assembly, ResolveSteamClient);
        }

        /// <summary>
        /// Indicates whether the Steam API was successfully initialized.
        /// </summary>
        public bool Initialized => _initialized;

        /// <summary>
        /// Initializes the Steam API and starts the callback pump.
        /// </summary>
        public SteamClient()
        {
            try
            {
#if DEBUG
                Debug.WriteLine($"SteamAppId env: {Environment.GetEnvironmentVariable("SteamAppId") ?? "<null>"}");
                Debug.WriteLine($"SteamAppName env: {Environment.GetEnvironmentVariable("SteamAppName") ?? "<null>"}");
#endif

                _initialized = SteamAPI_Init();
#if DEBUG
                Debug.WriteLine($"SteamAPI_Init returned: {_initialized}");
#endif
                if (_initialized)
                {
                    _apps = SteamAPI_SteamApps_v012();
#if DEBUG
                    Debug.WriteLine($"SteamAPI_SteamApps_v012 handle: 0x{_apps.ToString("X")}");
#endif
                    _callbackTimer = new Timer(_ =>
                    {
                        try
                        {
                            SteamAPI_RunCallbacks();
                        }
                        catch (Exception cbEx)
                        {
                            Debug.WriteLine($"SteamAPI_RunCallbacks failed: {cbEx.Message}");
                        }
                    }, null, 0, 100);
                }
            }
            catch (Exception ex)
            {
                _initialized = false;
#if DEBUG
                Debug.WriteLine($"Steam API init threw: {ex}");
#endif
            }
#if DEBUG
            if (!_initialized)
            {
                Debug.WriteLine("Steam API not initialized");
            }
#endif
        }

        /// <summary>
        /// Returns true if the current user owns the specified app id.
        /// </summary>
        public bool IsSubscribedApp(uint id)
        {
            return _initialized && SteamAPI_ISteamApps_BIsSubscribedApp(_apps, id);
        }

        /// <summary>
        /// Retrieves a piece of metadata for the given app id.
        /// Returns null if the key is missing or the client is uninitialized.
        /// </summary>
        public string? GetAppData(uint id, string key)
        {
            if (!_initialized)
                return null;

            var sb = new StringBuilder(4096);
            int len = SteamAPI_ISteamApps_GetAppData(_apps, id, key, sb, sb.Capacity);
            return len > 0 ? sb.ToString() : null;
        }

        /// <summary>
        /// Shuts down the Steam API and stops the callback pump.
        /// </summary>
        public void Dispose()
        {
            _callbackTimer?.Dispose();
            if (_initialized)
            {
                SteamAPI_Shutdown();
            }
        }

        private static IntPtr ResolveSteamClient(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
        {
            if (!libraryName.Equals("steamclient64", StringComparison.OrdinalIgnoreCase))
                return IntPtr.Zero;

            string? installPath = GetSteamPath();
#if DEBUG
            Debug.WriteLine($"Steam install path: {installPath ?? "<null>"}");
#endif
            if (string.IsNullOrEmpty(installPath))
                return IntPtr.Zero;

            string libraryPath = Path.Combine(installPath, "steamclient64.dll");
            if (!File.Exists(libraryPath))
            {
                libraryPath = Path.Combine(installPath, "bin", "steamclient64.dll");
            }
#if DEBUG
            Debug.WriteLine($"Resolved steamclient64 path: {libraryPath}");
#endif
            if (!File.Exists(libraryPath))
                return IntPtr.Zero;

            Native.AddDllDirectory(installPath);
            Native.AddDllDirectory(Path.Combine(installPath, "bin"));
            return Native.LoadLibraryEx(libraryPath, IntPtr.Zero,
                Native.LoadLibrarySearchDefaultDirs | Native.LoadLibrarySearchUserDirs);
        }

        private static string? GetSteamPath()
        {
            const string subKey = @"Software\\Valve\\Steam";
            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                using var key = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view).OpenSubKey(subKey);
                if (key == null)
                    continue;
                var path = key.GetValue("InstallPath") as string;
                if (string.IsNullOrEmpty(path) == false)
                    return path;
            }
            return null;
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

        [DllImport("steamclient64", CallingConvention = CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.I1)]
        private static extern bool SteamAPI_Init();

        [DllImport("steamclient64", CallingConvention = CallingConvention.Cdecl)]
        private static extern void SteamAPI_RunCallbacks();

        [DllImport("steamclient64", CallingConvention = CallingConvention.Cdecl)]
        private static extern void SteamAPI_Shutdown();

        [DllImport("steamclient64", CallingConvention = CallingConvention.Cdecl, EntryPoint = "SteamAPI_SteamApps_v012")]
        private static extern IntPtr SteamAPI_SteamApps_v012();

        [DllImport("steamclient64", CallingConvention = CallingConvention.Cdecl, EntryPoint = "SteamAPI_ISteamApps_BIsSubscribedApp")]
        [return: MarshalAs(UnmanagedType.I1)]
        private static extern bool SteamAPI_ISteamApps_BIsSubscribedApp(IntPtr self, uint appId);

        [DllImport("steamclient64", CallingConvention = CallingConvention.Cdecl, EntryPoint = "SteamAPI_ISteamApps_GetAppData")]
        private static extern int SteamAPI_ISteamApps_GetAppData(IntPtr self,
                                                                 uint appId,
                                                                 [MarshalAs(UnmanagedType.LPStr)] string key,
                                                                 StringBuilder value,
                                                                 int valueBufferSize);
    }
}

