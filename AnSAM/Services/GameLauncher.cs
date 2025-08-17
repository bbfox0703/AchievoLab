using System;
using System.Diagnostics;
using System.Globalization;

namespace AnSAM.Services
{
    /// <summary>
    /// Provides helpers to launch games via URI schemes or executable paths.
    /// </summary>
    public static class GameLauncher
    {
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

