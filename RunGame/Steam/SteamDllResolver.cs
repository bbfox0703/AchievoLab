using System;
using System.Reflection;
using System.Runtime.InteropServices;

namespace RunGame.Steam
{
    /// <summary>
    /// Registers a single DLL import resolver for the RunGame assembly.
    /// Combines resolution for both steamclient64.dll and steam_api64.dll
    /// since .NET only allows one resolver per assembly.
    /// </summary>
    internal static class SteamDllResolver
    {
        private static volatile bool _registered;
        private static readonly object _lock = new();

        internal static void EnsureRegistered()
        {
            if (_registered) return;
            lock (_lock)
            {
                if (_registered) return;
                NativeLibrary.SetDllImportResolver(typeof(SteamDllResolver).Assembly, CombinedResolver);
                _registered = true;
            }
        }

        private static IntPtr CombinedResolver(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
        {
            // Try steamclient64 resolver first
            var result = CommonUtilities.SteamPathResolver.ResolveSteamClientDll(libraryName, assembly, searchPath);
            if (result != IntPtr.Zero)
                return result;

            // Then try steam_api64 resolver
            return ModernSteamClient.ResolveSteamApi(libraryName, assembly, searchPath);
        }
    }
}
