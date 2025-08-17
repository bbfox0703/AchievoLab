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

        // Delegates retrieved from the ISteamClient018 vtable.
        private readonly CreateSteamPipeDelegate? _createSteamPipe;
        private readonly ConnectToGlobalUserDelegate? _connectToGlobalUser;
        private readonly GetISteamAppsDelegate? _getISteamApps;
        private readonly ReleaseUserDelegate? _releaseUser;
        private readonly ReleaseSteamPipeDelegate? _releaseSteamPipe;

#if DEBUG
        private int _loggedSubscriptions;
        private int _loggedAppData;
#endif

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
                _client = Steam_CreateInterface("SteamClient018", IntPtr.Zero);
#if DEBUG
                Debug.WriteLine($"CreateInterface('SteamClient018') returned: 0x{_client.ToString("X")}");
#endif
                if (_client != IntPtr.Zero)
                {
                    // Retrieve function pointers from the interface vtable.
                    IntPtr vtable = Marshal.ReadIntPtr(_client);
                    _createSteamPipe = Marshal.GetDelegateForFunctionPointer<CreateSteamPipeDelegate>(Marshal.ReadIntPtr(vtable + IntPtr.Size * 0));
                    _releaseSteamPipe = Marshal.GetDelegateForFunctionPointer<ReleaseSteamPipeDelegate>(Marshal.ReadIntPtr(vtable + IntPtr.Size * 1));
                    _connectToGlobalUser = Marshal.GetDelegateForFunctionPointer<ConnectToGlobalUserDelegate>(Marshal.ReadIntPtr(vtable + IntPtr.Size * 2));
                    _releaseUser = Marshal.GetDelegateForFunctionPointer<ReleaseUserDelegate>(Marshal.ReadIntPtr(vtable + IntPtr.Size * 4));
                    _getISteamApps = Marshal.GetDelegateForFunctionPointer<GetISteamAppsDelegate>(Marshal.ReadIntPtr(vtable + IntPtr.Size * 15));

                    if (_createSteamPipe != null && _connectToGlobalUser != null && _getISteamApps != null)
                    {
                        _pipe = _createSteamPipe(_client);
#if DEBUG
                        Debug.WriteLine($"CreateSteamPipe returned: {_pipe}");
#endif
                        if (_pipe != 0)
                        {
                            _user = _connectToGlobalUser(_client, _pipe);
#if DEBUG
                            Debug.WriteLine($"ConnectToGlobalUser returned: {_user}");
#endif
                            if (_user != 0)
                            {
                                _apps = _getISteamApps(_client, _user, _pipe, "STEAMAPPS_INTERFACE_VERSION008");
#if DEBUG
                                Debug.WriteLine($"GetISteamApps returned: 0x{_apps.ToString("X")}");
#endif
                                Initialized = _apps != IntPtr.Zero;
                            }
                        }
                    }
                }

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
            if (!Initialized)
            {
#if DEBUG
                Debug.WriteLine($"IsSubscribedApp({id}) called before Steam initialization");
#endif
                return false;
            }

            bool result = SteamAPI_ISteamApps_BIsSubscribedApp(_apps, id);
#if DEBUG
            if (_loggedSubscriptions < 20)
            {
                Debug.WriteLine($"IsSubscribedApp({id}) => {result}");
                _loggedSubscriptions++;
            }
#endif
            return result;
        }

        /// <summary>
        /// Retrieves a piece of metadata for the given app id.
        /// Returns null if the key is missing or the client is uninitialized.
        /// </summary>
        public string? GetAppData(uint id, string key)
        {
            if (!Initialized)
            {
#if DEBUG
                Debug.WriteLine($"GetAppData({id}, '{key}') called before Steam initialization");
#endif
                return null;
            }

            var sb = new StringBuilder(4096);
            int len = SteamAPI_ISteamApps_GetAppData(_apps, id, key, sb, sb.Capacity);
#if DEBUG
            if (_loggedAppData < 20)
            {
                Debug.WriteLine($"GetAppData({id}, '{key}') => {(len > 0 ? sb.ToString() : "<null>")}");
                _loggedAppData++;
            }
#endif
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
                _releaseUser?.Invoke(_client, _pipe, _user);
                _releaseSteamPipe?.Invoke(_client, _pipe);
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

        [DllImport("steamclient64", CallingConvention = CallingConvention.Cdecl,
            CharSet = CharSet.Ansi, EntryPoint = "CreateInterface")]
        private static extern IntPtr Steam_CreateInterface(string version, IntPtr returnCode);

        [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
        private delegate int CreateSteamPipeDelegate(IntPtr self);

        [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
        private delegate int ConnectToGlobalUserDelegate(IntPtr self, int pipe);

        [UnmanagedFunctionPointer(CallingConvention.ThisCall, CharSet = CharSet.Ansi)]
        private delegate IntPtr GetISteamAppsDelegate(IntPtr self, int user, int pipe, string version);

        [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
        private delegate void ReleaseUserDelegate(IntPtr self, int pipe, int user);

        [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
        private delegate void ReleaseSteamPipeDelegate(IntPtr self, int pipe);

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

