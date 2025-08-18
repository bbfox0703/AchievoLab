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
        private readonly IntPtr _apps008;
        private readonly IntPtr _apps001;
        private readonly int _pipe;
        private readonly int _user;

        // Delegates retrieved from the ISteamClient018 vtable.
        private readonly CreateSteamPipeDelegate? _createSteamPipe;
        private readonly ConnectToGlobalUserDelegate? _connectToGlobalUser;
        private readonly GetISteamAppsDelegate? _getISteamApps;
        private readonly ReleaseUserDelegate? _releaseUser;
        private readonly ReleaseSteamPipeDelegate? _releaseSteamPipe;
        private readonly IsSubscribedAppDelegate? _isSubscribedApp;
        private readonly GetAppDataDelegate? _getAppData;

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
                                _apps008 = _getISteamApps(_client, _user, _pipe, "STEAMAPPS_INTERFACE_VERSION008");
                                _apps001 = _getISteamApps(_client, _user, _pipe, "STEAMAPPS_INTERFACE_VERSION001");
#if DEBUG
                                Debug.WriteLine($"GetISteamApps(008) returned: 0x{_apps008.ToString("X")}");
                                Debug.WriteLine($"GetISteamApps(001) returned: 0x{_apps001.ToString("X")}");
#endif
                                if (_apps008 != IntPtr.Zero)
                                {
                                    IntPtr appsVTable = Marshal.ReadIntPtr(_apps008);
                                    _isSubscribedApp = Marshal.GetDelegateForFunctionPointer<IsSubscribedAppDelegate>(Marshal.ReadIntPtr(appsVTable + IntPtr.Size * 6));
                                }
                                if (_apps001 != IntPtr.Zero)
                                {
                                    IntPtr apps1VTable = Marshal.ReadIntPtr(_apps001);
                                    _getAppData = Marshal.GetDelegateForFunctionPointer<GetAppDataDelegate>(Marshal.ReadIntPtr(apps1VTable));
                                }
                                Initialized = _apps008 != IntPtr.Zero && _isSubscribedApp != null;
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

            if (_isSubscribedApp == null)
            {
#if DEBUG
                Debug.WriteLine("IsSubscribedApp delegate missing");
#endif
                return false;
            }
            bool result = _isSubscribedApp(_apps008, id);
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

            if (_getAppData == null || _apps001 == IntPtr.Zero)
            {
#if DEBUG
                Debug.WriteLine("GetAppData delegate missing");
#endif
                return null;
            }
            const int bufferSize = 4096;
            IntPtr buffer = Marshal.AllocHGlobal(bufferSize);
            try
            {
                int len = _getAppData(_apps001, id, key, buffer, bufferSize);
                if (len <= 0)
                {
                    return null;
                }

                var bytes = new byte[len];
                Marshal.Copy(buffer, bytes, 0, len);
                int terminator = Array.IndexOf<byte>(bytes, 0);
                if (terminator >= 0)
                {
                    len = terminator;
                }
                string result = Encoding.UTF8.GetString(bytes, 0, len);
#if DEBUG
                if (_loggedAppData < 20)
                {
                    Debug.WriteLine($"GetAppData({id}, '{key}') => {result}");
                    _loggedAppData++;
                }
#endif
                return result;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
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

        [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
        [return: MarshalAs(UnmanagedType.I1)]
        private delegate bool IsSubscribedAppDelegate(IntPtr self, uint appId);

        [UnmanagedFunctionPointer(CallingConvention.ThisCall, CharSet = CharSet.Ansi)]
        private delegate int GetAppDataDelegate(IntPtr self,
                                                uint appId,
                                                [MarshalAs(UnmanagedType.LPStr)] string key,
                                                IntPtr value,
                                                int valueBufferSize);
    }
}

