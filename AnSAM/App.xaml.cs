using System;
using AnSAM.Steam;
using Microsoft.UI.Xaml;
using Microsoft.Win32;
using Windows.Storage;
using Windows.ApplicationModel;
using Windows.ApplicationModel.Activation;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace AnSAM
{
    /// <summary>
    /// Provides application-specific behavior to supplement the default Application class.
    /// </summary>
    public partial class App : Application
    {
        private Window? _window;
        private SteamClient? _steamClient;

        /// <summary>
        /// Initializes the singleton application object.  This is the first line of authored code
        /// executed, and as such is the logical equivalent of main() or WinMain().
        /// </summary>
        public App()
        {
            InitializeComponent();
        }

        /// <summary>
        /// Invoked when the application is launched.
        /// </summary>
        /// <param name="args">Details about the launch request and process.</param>
        protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
        {
            var theme = LoadThemePreference() ?? GetSystemTheme();
            
            // Don't set Application.RequestedTheme to avoid COMException
            // Theme will be applied at MainWindow level instead
            
            _steamClient = new SteamClient();
            _window = new MainWindow(_steamClient, theme);
            _window.Closed += (_, _) => _steamClient?.Dispose();
            _window.Activate();
        }

        private static ElementTheme? LoadThemePreference()
        {
            try
            {
                var settings = ApplicationData.Current.LocalSettings;
                if (settings.Values.TryGetValue("AppTheme", out var t) &&
                    Enum.TryParse<ElementTheme>(t?.ToString(), out var savedTheme))
                {
                    return savedTheme;
                }
            }
            catch (InvalidOperationException)
            {
                // Ignore inability to read settings
            }
            return null;
        }

        internal static ElementTheme GetSystemTheme()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"Software\\Microsoft\\Windows\\CurrentVersion\\Themes\\Personalize");
                var value = key?.GetValue("AppsUseLightTheme");
                if (value is int i)
                {
                    return i != 0 ? ElementTheme.Light : ElementTheme.Dark;
                }
            }
            catch
            {
                // Fall back to light theme if the registry is unavailable
            }
            return ElementTheme.Light;
        }

        internal static ApplicationTheme ToApplicationTheme(ElementTheme theme)
        {
            var resolved = theme == ElementTheme.Default ? GetSystemTheme() : theme;
            return resolved == ElementTheme.Dark ? ApplicationTheme.Dark : ApplicationTheme.Light;
        }
    }
}
