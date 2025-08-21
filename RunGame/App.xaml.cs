using Microsoft.UI.Xaml;
using System;
using Windows.Storage;

namespace RunGame
{
    public partial class App : Application
    {
        internal static ApplicationDataContainer? LocalSettings { get; private set; }
        
        internal static ApplicationTheme ToApplicationTheme(ElementTheme theme)
        {
            var resolved = theme == ElementTheme.Default ? GetSystemTheme() : theme;
            return resolved == ElementTheme.Dark ? ApplicationTheme.Dark : ApplicationTheme.Light;
        }
        
        private static ElementTheme GetSystemTheme()
        {
            try
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
                var value = key?.GetValue("AppsUseLightTheme");
                if (value is int i)
                {
                    return i != 0 ? ElementTheme.Light : ElementTheme.Dark;
                }
            }
            catch
            {
                // Fall back to light theme if we can't read the registry
            }
            return ElementTheme.Light;
        }

        public App()
        {
            this.InitializeComponent();
        }

        protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
        {
            try
            {
                LocalSettings = ApplicationData.Current.LocalSettings;
            }
            catch (InvalidOperationException)
            {
                LocalSettings = null;
            }

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