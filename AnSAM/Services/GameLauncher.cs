using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;

namespace AnSAM.Services
{
    /// <summary>
    /// Provides helpers to launch games via URI schemes or executable paths.
    /// </summary>
    public static class GameLauncher
    {
        private static readonly string RunGamePath = Path.Combine(AppContext.BaseDirectory, "..", "RunGame.exe");
        private static readonly string SamGamePath = Path.Combine(AppContext.BaseDirectory, "SAM", "SAM.Game.exe");

        public static bool IsSamGameAvailable => File.Exists(RunGamePath) || File.Exists(SamGamePath);

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
                return;
            }

            // Try custom URI scheme first
            if (!string.IsNullOrWhiteSpace(item.UriScheme) && TryStart(item.UriScheme))
            {
                return;
            }

            // Then try executable path with arguments
            if (!string.IsNullOrWhiteSpace(item.ExePath))
            {
                if (TryStart(item.ExePath, item.Arguments))
                {
                    return;
                }
            }

            // Fallback to Steam run URL
            var steamUri = $"steam://run/{item.ID.ToString(CultureInfo.InvariantCulture)}";
            TryStart(steamUri);
        }

        /// <summary>
        /// Launches the achievement manager for the given <see cref="GameItem"/>.
        /// Tries RunGame.exe first, then falls back to SAM.Game.exe if available.
        /// </summary>
        /// <param name="item">Game item containing launch information.</param>
        public static void LaunchSamGame(GameItem item)
        {
            if (item == null)
            {
                return;
            }

            if (!IsSamGameAvailable)
            {
                return;
            }

            // Try RunGame.exe first (modern implementation)
            if (File.Exists(RunGamePath))
            {
                TryStart(RunGamePath, item.ID.ToString(CultureInfo.InvariantCulture));
            }
            // Fall back to SAM.Game.exe (legacy implementation)
            else if (File.Exists(SamGamePath))
            {
                TryStart(SamGamePath, item.ID.ToString(CultureInfo.InvariantCulture));
            }
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

                Process.Start(startInfo);
                return true;
            }
            catch
            {
                // Ignore launch failures and allow fallback.
            }

            return false;
        }
    }
}

