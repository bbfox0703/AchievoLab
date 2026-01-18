using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace CommonUtilities
{
    /// <summary>
    /// Provides Steam installation path resolution and DLL loading utilities.
    /// Shared across AnSAM and RunGame for consistent Steam client initialization.
    /// </summary>
    public static class SteamPathResolver
    {
        /// <summary>
        /// Gets the Steam installation path from the Windows registry.
        /// Checks HKLM (64-bit and 32-bit views) and HKCU for the InstallPath value.
        /// </summary>
        /// <returns>The Steam installation path, or null if not found.</returns>
        public static string? GetSteamInstallPath()
        {
            const string subKey = @"Software\Valve\Steam";

            // Check HKLM 64-bit and 32-bit (WOW6432Node) views
            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                try
                {
                    using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                    using var key = baseKey.OpenSubKey(subKey);
                    var path = key?.GetValue("InstallPath") as string;
                    if (!string.IsNullOrEmpty(path))
                    {
                        return path;
                    }
                }
                catch
                {
                    // Continue to next registry view
                }
            }

            // Fall back to HKCU
            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                try
                {
                    using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, view);
                    using var key = baseKey.OpenSubKey(subKey);
                    var path = key?.GetValue("InstallPath") as string;
                    if (!string.IsNullOrEmpty(path))
                    {
                        return path;
                    }
                }
                catch
                {
                    // Continue to next registry view
                }
            }

            return null;
        }

        /// <summary>
        /// Resolves the steamclient64.dll path and loads it using Windows API.
        /// This method is designed to be used as a NativeLibrary.SetDllImportResolver callback.
        /// </summary>
        /// <param name="libraryName">The name of the library being resolved.</param>
        /// <param name="assembly">The assembly requesting the library.</param>
        /// <param name="searchPath">The DLL import search path.</param>
        /// <returns>A handle to the loaded library, or IntPtr.Zero if loading failed.</returns>
        public static IntPtr ResolveSteamClientDll(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
        {
            try
            {
                if (!libraryName.Equals("steamclient64", StringComparison.OrdinalIgnoreCase))
                    return IntPtr.Zero;

                string? installPath = GetSteamInstallPath();
                if (string.IsNullOrEmpty(installPath))
                    return IntPtr.Zero;

                string libraryPath = Path.Combine(installPath, "steamclient64.dll");
                if (!File.Exists(libraryPath))
                {
                    libraryPath = Path.Combine(installPath, "bin", "steamclient64.dll");
                }

                if (!File.Exists(libraryPath))
                    return IntPtr.Zero;

                Native.AddDllDirectory(installPath);
                Native.AddDllDirectory(Path.Combine(installPath, "bin"));

                return Native.LoadLibraryEx(libraryPath, IntPtr.Zero,
                    Native.LoadLibrarySearchDefaultDirs | Native.LoadLibrarySearchUserDirs);
            }
            catch
            {
                return IntPtr.Zero;
            }
        }

        /// <summary>
        /// Checks whether the Steam client process is currently running.
        /// </summary>
        /// <returns>True if Steam is running; otherwise, false.</returns>
        public static bool IsSteamRunning()
        {
            try
            {
                var steamProcesses = Process.GetProcessesByName("steam");
                return steamProcesses.Length > 0;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Native Windows API methods for DLL loading.
        /// </summary>
        private static class Native
        {
            [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
            internal static extern IntPtr LoadLibraryEx(string lpFileName, IntPtr hFile, uint dwFlags);

            [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
            internal static extern IntPtr AddDllDirectory(string lpPathName);

            internal const uint LoadLibrarySearchDefaultDirs = 0x00001000;
            internal const uint LoadLibrarySearchUserDirs = 0x00000400;
        }
    }
}
