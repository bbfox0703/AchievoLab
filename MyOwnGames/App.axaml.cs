using System;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using CommonUtilities;

namespace MyOwnGames
{
    public partial class App : Application
    {
        private MainWindow? _window;

        public override void Initialize()
        {
            AvaloniaXamlLoader.Load(this);
            ApplicationInitializationHelper.InitializeLogging("MyOwnGames");
            ApplicationInitializationHelper.EnsureConfigurationFile("MyOwnGames", DefaultConfigurations.MyOwnGames);
            ExceptionHandlingHelper.RegisterGlobalExceptionHandlers();
        }

        public override void OnFrameworkInitializationCompleted()
        {
            var theme = LoadThemePreference() ?? ThemeManagementService.GetSystemTheme();
            RequestedThemeVariant = theme;

            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                _window = new MainWindow();
                _window.Closed += OnWindowClosed;
                AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
                desktop.MainWindow = _window;
            }

            base.OnFrameworkInitializationCompleted();
        }

        private void OnProcessExit(object? sender, EventArgs e)
        {
            if (_window != null)
            {
                AppLogger.LogDebug("Process exiting");
                _window.SaveAndDisposeAsync("process exit").GetAwaiter().GetResult();
            }
        }

        private async void OnWindowClosed(object? sender, EventArgs e)
        {
            if (_window != null)
            {
                await _window.SaveAndDisposeAsync("window closed");
            }
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
