using System;
using AnSAM.Steam;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using CommonUtilities;

namespace AnSAM
{
    public partial class App : Application
    {
        private SteamClient? _steamClient;

        public override void Initialize()
        {
            AvaloniaXamlLoader.Load(this);
            ApplicationInitializationHelper.InitializeLogging("AnSAM");
            ApplicationInitializationHelper.EnsureConfigurationFile("AnSAM", DefaultConfigurations.AnSAM);
            ExceptionHandlingHelper.RegisterGlobalExceptionHandlers();
        }

        public override void OnFrameworkInitializationCompleted()
        {
            var theme = LoadThemePreference() ?? ThemeManagementService.GetSystemTheme();
            RequestedThemeVariant = theme;

            _steamClient = new SteamClient();

            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                var window = new MainWindow(_steamClient, theme);
                window.Closed += (_, _) => _steamClient?.Dispose();
                desktop.MainWindow = window;
            }

            base.OnFrameworkInitializationCompleted();
        }

        private static ThemeVariant? LoadThemePreference()
        {
            var settingsService = new ApplicationSettingsService();
            if (settingsService.TryGetString("AppTheme", out var savedTheme) && savedTheme != null)
            {
                return savedTheme switch
                {
                    "Dark" => ThemeVariant.Dark,
                    "Light" => ThemeVariant.Light,
                    _ => ThemeVariant.Default
                };
            }
            return null;
        }
    }
}
