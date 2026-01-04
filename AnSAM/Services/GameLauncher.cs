using System;
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
                DebugLogger.LogDebug($"GameLauncher initialized: RunGame.exe found at {fullPath}");
            }
            else
            {
                _runGameFullPath = null;
                DebugLogger.LogDebug($"GameLauncher initialized: RunGame.exe NOT found at {fullPath}");
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
                DebugLogger.LogDebug("Launch: item is null");
                return;
            }

            DebugLogger.LogDebug($"Launch: Attempting to launch game {item.ID} ({item.Title})");

            // Try custom URI scheme first
            if (!string.IsNullOrWhiteSpace(item.UriScheme))
            {
                DebugLogger.LogDebug($"Launch: Trying URI scheme: {item.UriScheme}");
                if (TryStart(item.UriScheme))
                {
                    DebugLogger.LogDebug($"Launch: Successfully launched via URI scheme");
                    return;
                }
            }

            // Then try executable path with arguments
            if (!string.IsNullOrWhiteSpace(item.ExePath))
            {
                DebugLogger.LogDebug($"Launch: Trying executable: {item.ExePath}");
                if (TryStart(item.ExePath, item.Arguments))
                {
                    DebugLogger.LogDebug($"Launch: Successfully launched via executable");
                    return;
                }
            }

            // Fallback to Steam run URL
            var steamUri = $"steam://run/{item.ID.ToString(CultureInfo.InvariantCulture)}";
            DebugLogger.LogDebug($"Launch: Falling back to Steam URI: {steamUri}");
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
                DebugLogger.LogDebug("LaunchAchievementManager: item is null");
                return;
            }

            DebugLogger.LogDebug($"LaunchAchievementManager: Attempting to launch for game {item.ID} ({item.Title})");

            if (_runGameFullPath == null)
            {
                DebugLogger.LogDebug("LaunchAchievementManager: RunGame.exe not available");
                return;
            }

            var appId = item.ID.ToString(CultureInfo.InvariantCulture);
            DebugLogger.LogDebug($"LaunchAchievementManager: Launching {_runGameFullPath} with argument {appId}");

            var success = TryStart(_runGameFullPath, appId);
            DebugLogger.LogDebug($"LaunchAchievementManager: Launch success={success}");
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

                DebugLogger.LogDebug($"TryStart: FileName={fileName}, Arguments={arguments ?? "(none)"}");
                Process.Start(startInfo);
                DebugLogger.LogDebug($"TryStart: Process started successfully");
                return true;
            }
            catch (Exception ex)
            {
                DebugLogger.LogDebug($"TryStart: Failed to start process - {ex.Message}");
                // Ignore launch failures and allow fallback.
            }

            return false;
        }
    }
}

