using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;

namespace AnSAM
{
    public static class GameLauncher
    {
        public static Task LaunchAsync(GameItem game)
        {
            if (game == null)
                return Task.CompletedTask;

            try
            {
                string gameExe = Path.Combine(AppContext.BaseDirectory, "SAM.Game.exe");
                if (File.Exists(gameExe))
                {
                    var startInfo = new ProcessStartInfo
                    {
                        FileName = gameExe,
                        Arguments = game.ID.ToString(CultureInfo.InvariantCulture),
                        UseShellExecute = true
                    };
                    Process.Start(startInfo);
                }
            }
            catch
            {
                // Ignore launch failures for now
            }

            return Task.CompletedTask;
        }
    }
}
