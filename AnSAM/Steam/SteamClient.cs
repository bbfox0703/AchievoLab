using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

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
                _initialized = SteamAPI_Init();
                if (_initialized)
                {
                    _apps = SteamAPI_SteamApps_v012();
                    _callbackTimer = new Timer(_ => SteamAPI_RunCallbacks(), null, 0, 100);
                }
            }
            catch (Exception ex)
            {
                _initialized = false;
#if DEBUG
                Debug.WriteLine($"Steam API init failed: {ex.Message}");
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

        [DllImport("steam_api64", CallingConvention = CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.I1)]
        private static extern bool SteamAPI_Init();

        [DllImport("steam_api64", CallingConvention = CallingConvention.Cdecl)]
        private static extern void SteamAPI_RunCallbacks();

        [DllImport("steam_api64", CallingConvention = CallingConvention.Cdecl)]
        private static extern void SteamAPI_Shutdown();

        [DllImport("steam_api64", CallingConvention = CallingConvention.Cdecl, EntryPoint = "SteamAPI_SteamApps_v012")]
        private static extern IntPtr SteamAPI_SteamApps_v012();

        [DllImport("steam_api64", CallingConvention = CallingConvention.Cdecl, EntryPoint = "SteamAPI_ISteamApps_BIsSubscribedApp")]
        [return: MarshalAs(UnmanagedType.I1)]
        private static extern bool SteamAPI_ISteamApps_BIsSubscribedApp(IntPtr self, uint appId);

        [DllImport("steam_api64", CallingConvention = CallingConvention.Cdecl, EntryPoint = "SteamAPI_ISteamApps_GetAppData")]
        private static extern int SteamAPI_ISteamApps_GetAppData(IntPtr self,
                                                                 uint appId,
                                                                 [MarshalAs(UnmanagedType.LPStr)] string key,
                                                                 StringBuilder value,
                                                                 int valueBufferSize);
    }
}

