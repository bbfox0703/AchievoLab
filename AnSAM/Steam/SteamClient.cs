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
    /// Minimal Steamworks client wrapper that loads steamclient64.dll directly
    /// and exposes helpers for app ownership and metadata queries.
    /// </summary>
    public sealed class SteamClient : IDisposable
    {
        private readonly Timer? _callbackTimer;
        private readonly IntPtr _client;
        private readonly IntPtr _apps;
        private readonly int _pipe;
        private readonly int _user;

        static SteamClient()
        {
            NativeLibrary.SetDllImportResolver(typeof(SteamClient).Assembly, ResolveSteamClient);
        }

        /// <summary>
        /// Indicates whether the Steam API was successfully initialized.
        /// </summary>
        public bool Initialized { get; }

        /// <summary>
        /// Initializes the Steam API and starts the callback pump.
        /// </summary>
        public SteamClient()
        {
            try
            {
                _client = SteamAPI_SteamClient_v018();
#if DEBUG
                Debug.WriteLine($"SteamAPI_SteamClient_v018 handle: 0x{_client.ToString("X")}");
#endif
                _pipe = SteamAPI_ISteamClient_CreateSteamPipe(_client);
#if DEBUG
                Debug.WriteLine($"CreateSteamPipe returned: {_pipe}");
#endif
                _user = SteamAPI_ISteamClient_ConnectToGlobalUser(_client, _pipe);
#if DEBUG
                Debug.WriteLine($"ConnectToGlobalUser returned: {_user}");
#endif
                _apps = SteamAPI_ISteamClient_GetISteamApps(_client, _user, _pipe, "STEAMAPPS_INTERFACE_VERSION008");
#if DEBUG
                Debug.WriteLine($"GetISteamApps returned: 0x{_apps.ToString("X")}");
#endif
                Initialized = _apps != IntPtr.Zero;

                if (Initialized)
                {
                    _callbackTimer = new Timer(_ => PumpCallbacks(), null, 0, 100);
                }
            }
            catch (Exception ex)
            {
                Initialized = false;
#if DEBUG
                Debug.WriteLine($"Steam API init threw: {ex}");
#endif
            }

#if DEBUG
            if (!Initialized)
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
            return Initialized && SteamAPI_ISteamApps_BIsSubscribedApp(_apps, id);
        }

        /// <summary>
        /// Retrieves a piece of metadata for the given app id.
        /// Returns null if the key is missing or the client is uninitialized.
        /// </summary>
        public string? GetAppData(uint id, string key)
        {
            if (!Initialized)
                return null;

            var sb = new StringBuilder(4096);
            int len = SteamAPI_ISteamApps_GetAppData(_apps, id, key, sb, sb.Capacity);
            return len > 0 ? sb.ToString() : null;
        }

        private void PumpCallbacks()
        {
            while (Steam_BGetCallback(_pipe, out var msg, out _))
            {
                Steam_FreeLastCallback(_pipe);
            }
        }

        /// <summary>
        /// Releases Steam resources and stops the callback pump.
        /// </summary>
        public void Dispose()
        {
            _callbackTimer?.Dispose();
            if (Initialized)
            {
                SteamAPI_ISteamClient_ReleaseUser(_client, _pipe, _user);
                SteamAPI_ISteamClient_ReleaseSteamPipe(_client, _pipe);
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

        [StructLayout(LayoutKind.Sequential)]
        private struct CallbackMsg
        {
            public int User;
            public int Id;
            public IntPtr ParamPointer;
            public int ParamSize;
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

        [DllImport("steamclient64", CallingConvention = CallingConvention.Cdecl, EntryPoint = "SteamAPI_SteamClient_v018")]
        private static extern IntPtr SteamAPI_SteamClient_v018();

        [DllImport("steamclient64", CallingConvention = CallingConvention.Cdecl, EntryPoint = "SteamAPI_ISteamClient_CreateSteamPipe")]
        private static extern int SteamAPI_ISteamClient_CreateSteamPipe(IntPtr self);

        [DllImport("steamclient64", CallingConvention = CallingConvention.Cdecl, EntryPoint = "SteamAPI_ISteamClient_ConnectToGlobalUser")]
        private static extern int SteamAPI_ISteamClient_ConnectToGlobalUser(IntPtr self, int pipe);

        [DllImport("steamclient64", CallingConvention = CallingConvention.Cdecl, EntryPoint = "SteamAPI_ISteamClient_GetISteamApps")]
        private static extern IntPtr SteamAPI_ISteamClient_GetISteamApps(IntPtr self, int user, int pipe, [MarshalAs(UnmanagedType.LPStr)] string version);

        [DllImport("steamclient64", CallingConvention = CallingConvention.Cdecl, EntryPoint = "SteamAPI_ISteamClient_ReleaseUser")]
        private static extern void SteamAPI_ISteamClient_ReleaseUser(IntPtr self, int pipe, int user);

        [DllImport("steamclient64", CallingConvention = CallingConvention.Cdecl, EntryPoint = "SteamAPI_ISteamClient_ReleaseSteamPipe")]
        private static extern void SteamAPI_ISteamClient_ReleaseSteamPipe(IntPtr self, int pipe);

        [DllImport("steamclient64", CallingConvention = CallingConvention.Cdecl, EntryPoint = "Steam_BGetCallback")]
        [return: MarshalAs(UnmanagedType.I1)]
        private static extern bool Steam_BGetCallback(int pipe, out CallbackMsg message, out int call);

        [DllImport("steamclient64", CallingConvention = CallingConvention.Cdecl, EntryPoint = "Steam_FreeLastCallback")]
        [return: MarshalAs(UnmanagedType.I1)]
        private static extern bool Steam_FreeLastCallback(int pipe);

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

