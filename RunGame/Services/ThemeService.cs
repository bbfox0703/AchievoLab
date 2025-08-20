using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.Win32;
using System;
using Windows.UI;
using Windows.UI.ViewManagement;
using WinRT.Interop;

namespace RunGame.Services
{
    public static class ThemeService
    {
        private static FrameworkElement? _root;
        private static AppWindow? _appWindow;
        private static readonly UISettings _uiSettings = new();

        public static void Initialize(Window window, FrameworkElement root)
        {
            _root = root;
            var hwnd = WindowNative.GetWindowHandle(window);
            var winId = Win32Interop.GetWindowIdFromWindow(hwnd);
            _appWindow = AppWindow.GetFromWindowId(winId);
        }

        public static ElementTheme GetCurrentTheme()
        {
            DebugLogger.LogDebug("GetCurrentTheme() Start");

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
                // Fall back to light theme if we can't read the registry
            }
            return ElementTheme.Light;
        }

        public static void ApplyTheme(ElementTheme theme)
        {
            if (_root is null)
                return;
            DebugLogger.LogDebug("ApplyTheme() Start");

            _root.RequestedTheme = theme;
            ApplyAccentBrush();
            UpdateTitleBar(theme);
        }

        public static void ApplyAccentBrush()
        {
            if (_root is null)
                return;
            DebugLogger.LogDebug("ApplyAccentBrush() Start");

            var accent = _uiSettings.GetColorValue(UIColorType.Accent);
            var brush = new SolidColorBrush(accent);
            _root.Resources["AppAccentBrush"] = brush;
        }

        public static void UpdateTitleBar(ElementTheme theme)
        {
            if (_appWindow is null || !AppWindowTitleBar.IsCustomizationSupported())
                return;

            DebugLogger.LogDebug("UpdateTitleBar() Start");

            var titleBar = _appWindow.TitleBar;
            var accent = _uiSettings.GetColorValue(UIColorType.Accent);
            var accentDark1 = _uiSettings.GetColorValue(UIColorType.AccentDark1);
            var accentDark2 = _uiSettings.GetColorValue(UIColorType.AccentDark2);
            var accentLight1 = _uiSettings.GetColorValue(UIColorType.AccentLight1);
            var foreground = _uiSettings.GetColorValue(UIColorType.Foreground);
            var inactiveForeground = Color.FromArgb(
                foreground.A,
                (byte)(foreground.R / 2),
                (byte)(foreground.G / 2),
                (byte)(foreground.B / 2));

            if (theme == ElementTheme.Dark)
            {
                DebugLogger.LogDebug("UpdateTitleBar(): set dark theme");
                titleBar.BackgroundColor = accentDark2;
                titleBar.ForegroundColor = foreground;

                titleBar.ButtonBackgroundColor = accentDark2;
                titleBar.ButtonForegroundColor = foreground;
                titleBar.ButtonHoverBackgroundColor = accent;
                titleBar.ButtonHoverForegroundColor = foreground;
                titleBar.ButtonPressedBackgroundColor = accentDark2;
                titleBar.ButtonPressedForegroundColor = foreground;

                titleBar.InactiveBackgroundColor = accentDark2;
                titleBar.InactiveForegroundColor = inactiveForeground;
                titleBar.ButtonInactiveBackgroundColor = accentDark2;
                titleBar.ButtonInactiveForegroundColor = inactiveForeground;
            }
            else
            {
                DebugLogger.LogDebug("UpdateTitleBar(): set light theme");
                titleBar.BackgroundColor = accentLight1;
                titleBar.ForegroundColor = foreground;

                titleBar.ButtonBackgroundColor = accentLight1;
                titleBar.ButtonForegroundColor = foreground;
                titleBar.ButtonHoverBackgroundColor = accent;
                titleBar.ButtonHoverForegroundColor = foreground;
                titleBar.ButtonPressedBackgroundColor = accentDark1;
                titleBar.ButtonPressedForegroundColor = foreground;

                titleBar.InactiveBackgroundColor = accentLight1;
                titleBar.InactiveForegroundColor = inactiveForeground;
                titleBar.ButtonInactiveBackgroundColor = accentLight1;
                titleBar.ButtonInactiveForegroundColor = inactiveForeground;
            }
        }
    }
}

