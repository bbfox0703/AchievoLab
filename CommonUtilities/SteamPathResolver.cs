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
    public static partial class SteamPathResolver
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
        /// Resolves the steamclient64.dll path and loads it using NativeLibrary.Load (AOT-compatible).
        /// This method is designed to be used as a NativeLibrary.SetDllImportResolver callback.
        /// </summary>
        public static IntPtr ResolveSteamClientDll(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
        {
            try
            {
                if (!libraryName.Equals("steamclient64", StringComparison.OrdinalIgnoreCase))
                    return IntPtr.Zero;

                string? installPath = GetSteamInstallPath();
                if (string.IsNullOrEmpty(installPath))
                {
                    AppLogger.LogDebug("SteamPathResolver: Steam install path not found in registry");
                    return IntPtr.Zero;
                }

                // Add Steam directories to DLL search path for dependency resolution
                Native.AddDllDirectory(installPath);
                Native.AddDllDirectory(Path.Combine(installPath, "bin"));

                string libraryPath = Path.Combine(installPath, "steamclient64.dll");
                if (!File.Exists(libraryPath))
                {
                    libraryPath = Path.Combine(installPath, "bin", "steamclient64.dll");
                }

                if (!File.Exists(libraryPath))
                {
                    AppLogger.LogDebug($"SteamPathResolver: steamclient64.dll not found at {installPath}");
                    return IntPtr.Zero;
                }

                AppLogger.LogDebug($"SteamPathResolver: Loading steamclient64.dll from {libraryPath}");
                var handle = NativeLibrary.Load(libraryPath);
                AppLogger.LogDebug($"SteamPathResolver: steamclient64.dll loaded successfully, handle=0x{handle:X}");
                return handle;
            }
            catch (Exception ex)
            {
                AppLogger.LogDebug($"SteamPathResolver: Failed to load steamclient64.dll: {ex.Message}");
                return IntPtr.Zero;
            }
        }

        /// <summary>
        /// Checks whether the Steam client process is currently running.
        /// </summary>
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
        private static partial class Native
        {
            [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
            internal static partial IntPtr LoadLibraryEx(string lpFileName, IntPtr hFile, uint dwFlags);

            [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
            internal static partial IntPtr AddDllDirectory(string lpPathName);

            internal const uint LoadLibrarySearchDefaultDirs = 0x00001000;
            internal const uint LoadLibrarySearchUserDirs = 0x00000400;
        }
    }
}
