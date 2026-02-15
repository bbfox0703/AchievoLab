using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using CommonUtilities;

namespace AnSAM.Services
{
    /// <summary>
    /// Provides helpers to launch games via URI schemes or executable paths.
    /// </summary>
    public static class GameLauncher
    {
        private static readonly HashSet<string> AllowedSchemes = new(StringComparer.OrdinalIgnoreCase)
        {
            "steam", "http", "https", "com.epicgames.launcher", "uplay", "origin", "origin2"
        };

        private static bool IsAllowedUri(string fileName)
        {
            if (Uri.TryCreate(fileName, UriKind.Absolute, out var uri))
            {
                return AllowedSchemes.Contains(uri.Scheme);
            }
            return false;
        }

        private static bool IsAllowedExePath(string fileName)
        {
            try
            {
                var fullPath = Path.GetFullPath(fileName);
                return File.Exists(fullPath) &&
                       fullPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        private static string? _runGameFullPath = null;

        /// <summary>
        /// Initializes the GameLauncher by locating RunGame.exe.
        /// Should be called once during application startup.
        /// </summary>
        public static void Initialize()
        {
            var relativePath = Path.Combine(AppContext.BaseDirectory, "..", "RunGame", "RunGame.exe");
            var fullPath = Path.GetFullPath(relativePath);

            if (File.Exists(fullPath))
            {
                _runGameFullPath = fullPath;
                AppLogger.LogDebug($"GameLauncher initialized: RunGame.exe found at {fullPath}");
            }
            else
            {
                _runGameFullPath = null;
                AppLogger.LogDebug($"GameLauncher initialized: RunGame.exe NOT found at {fullPath}");
            }
        }

        /// <summary>
        /// Indicates whether the bundled achievement manager executable is available.
        /// </summary>
        public static bool IsManagerAvailable => _runGameFullPath != null;

        /// <summary>
        /// Launches the given <see cref="GameItem"/> by trying, in order:
        /// 1. The game's custom URI scheme.
        /// 2. The game's executable path and arguments.
        /// 3. Falling back to the Steam URI scheme using the game's ID.
        /// </summary>
        /// <param name="item">Game item containing launch information.</param>
        public static void Launch(GameItem item)
        {
            if (item == null)
            {
                AppLogger.LogDebug("Launch: item is null");
                return;
            }

            AppLogger.LogDebug($"Launch: Attempting to launch game {item.ID} ({item.Title})");

            // Try custom URI scheme first
            if (!string.IsNullOrWhiteSpace(item.UriScheme))
            {
                if (!IsAllowedUri(item.UriScheme))
                {
                    AppLogger.LogDebug($"Launch: URI scheme not allowed: {item.UriScheme}");
                }
                else
                {
                    AppLogger.LogDebug($"Launch: Trying URI scheme: {item.UriScheme}");
                    if (TryStart(item.UriScheme))
                    {
                        AppLogger.LogDebug($"Launch: Successfully launched via URI scheme");
                        return;
                    }
                }
            }

            // Then try executable path with arguments
            if (!string.IsNullOrWhiteSpace(item.ExePath))
            {
                if (!IsAllowedExePath(item.ExePath))
                {
                    AppLogger.LogDebug($"Launch: Executable path not allowed: {item.ExePath}");
                }
                else
                {
                    AppLogger.LogDebug($"Launch: Trying executable: {item.ExePath}");
                    if (TryStart(item.ExePath, item.Arguments))
                    {
                        AppLogger.LogDebug($"Launch: Successfully launched via executable");
                        return;
                    }
                }
            }

            // Fallback to Steam run URL
            var steamUri = $"steam://run/{item.ID.ToString(CultureInfo.InvariantCulture)}";
            AppLogger.LogDebug($"Launch: Falling back to Steam URI: {steamUri}");
            TryStart(steamUri);
        }

        /// <summary>
        /// Launches the bundled achievement manager for the given <see cref="GameItem"/>.
        /// </summary>
        /// <param name="item">Game item containing launch information.</param>
        public static void LaunchAchievementManager(GameItem item)
        {
            if (item == null)
            {
                AppLogger.LogDebug("LaunchAchievementManager: item is null");
                return;
            }

            AppLogger.LogDebug($"LaunchAchievementManager: Attempting to launch for game {item.ID} ({item.Title})");

            if (_runGameFullPath == null)
            {
                AppLogger.LogDebug("LaunchAchievementManager: RunGame.exe not available");
                return;
            }

            var appId = item.ID.ToString(CultureInfo.InvariantCulture);
            AppLogger.LogDebug($"LaunchAchievementManager: Launching {_runGameFullPath} with argument {appId}");

            var success = TryStart(_runGameFullPath, appId);
            AppLogger.LogDebug($"LaunchAchievementManager: Launch success={success}");
        }

        private static bool TryStart(string fileName, string? arguments = null)
        {
            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = fileName,
                    UseShellExecute = true
                };
                if (!string.IsNullOrWhiteSpace(arguments))
                {
                    startInfo.Arguments = arguments;
                }

                AppLogger.LogDebug($"TryStart: FileName={fileName}, Arguments={arguments ?? "(none)"}");
                Process.Start(startInfo);
                AppLogger.LogDebug($"TryStart: Process started successfully");
                return true;
            }
            catch (Exception ex)
            {
                AppLogger.LogDebug($"TryStart: Failed to start process - {ex.Message}");
                // Ignore launch failures and allow fallback.
            }

            return false;
        }
    }
}

