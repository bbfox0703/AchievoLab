using Microsoft.UI.Xaml;
using System;

namespace RunGame
{
    public partial class App : Application
    {
        public App()
        {
            this.InitializeComponent();
        }

        protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
        {
            var cmdArgs = Environment.GetCommandLineArgs();
            
            if (cmdArgs.Length < 2)
            {
                // Show error dialog and exit
                var dialog = new Windows.UI.Popups.MessageDialog(
                    "This application requires a game ID as a command line parameter.",
                    "Error");
                _ = dialog.ShowAsync();
                Exit();
                return;
            }

            if (!long.TryParse(cmdArgs[1], out long gameId))
            {
                var dialog = new Windows.UI.Popups.MessageDialog(
                    "Invalid game ID provided as command line parameter.",
                    "Error");
                _ = dialog.ShowAsync();
                Exit();
                return;
            }

            m_window = new MainWindow(gameId);
            m_window.Activate();
        }

        private Window? m_window;
    }
}