using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.Win32;
using System;
using Windows.UI;
using Windows.UI.ViewManagement;
using WinRT.Interop;

namespace CommonUtilities
{
    /// <summary>
    /// Manages theme application, accent colors, and title bar customization for WinUI 3 applications.
    /// Shared across AnSAM, RunGame, and MyOwnGames for consistent theming.
    /// </summary>
    public class ThemeManagementService
    {
        private FrameworkElement? _root;
        private AppWindow? _appWindow;
        private readonly UISettings _uiSettings = new();

        /// <summary>
        /// Determines the current system theme from the Windows registry.
        /// </summary>
        /// <returns>ElementTheme.Light or ElementTheme.Dark based on system settings. Defaults to Light on error.</returns>
        public static ElementTheme GetSystemTheme()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
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

        /// <summary>
        /// Converts an ElementTheme to the corresponding ApplicationTheme.
        /// </summary>
        /// <param name="theme">The theme to convert</param>
        /// <returns>ApplicationTheme.Dark or ApplicationTheme.Light</returns>
        public static ApplicationTheme ToApplicationTheme(ElementTheme theme)
        {
            var resolved = theme == ElementTheme.Default ? GetSystemTheme() : theme;
            return resolved == ElementTheme.Dark ? ApplicationTheme.Dark : ApplicationTheme.Light;
        }

        /// <summary>
        /// Initializes the service with window and root element references.
        /// Must be called before using ApplyTheme, ApplyAccentBrush, or UpdateTitleBar.
        /// </summary>
        /// <param name="window">The WinUI 3 window</param>
        /// <param name="root">The root FrameworkElement (typically window.Content)</param>
        public void Initialize(Window window, FrameworkElement root)
        {
            _root = root;
            var hwnd = WindowNative.GetWindowHandle(window);
            var winId = Win32Interop.GetWindowIdFromWindow(hwnd);
            _appWindow = AppWindow.GetFromWindowId(winId);
        }

        /// <summary>
        /// Applies the specified theme to the window, including accent brush and title bar customization.
        /// </summary>
        /// <param name="theme">The theme to apply</param>
        public void ApplyTheme(ElementTheme theme)
        {
            if (_root is null)
            {
                AppLogger.LogDebug("ThemeManagementService.ApplyTheme() called before Initialize()");
                return;
            }

            AppLogger.LogDebug($"ThemeManagementService.ApplyTheme() - Setting theme to {theme}");

            _root.RequestedTheme = theme;

            ApplyAccentBrush();
            UpdateTitleBar(theme);

            // Simple layout update for OS theme changes
            _root.UpdateLayout();

            AppLogger.LogDebug($"ThemeManagementService.ApplyTheme() Complete - Theme set to {theme}, ActualTheme is {_root.ActualTheme}");
        }

        /// <summary>
        /// Updates the accent color brush from the current system settings.
        /// </summary>
        public void ApplyAccentBrush()
        {
            if (_root is null)
            {
                AppLogger.LogDebug("ThemeManagementService.ApplyAccentBrush() called before Initialize()");
                return;
            }

            AppLogger.LogDebug("ThemeManagementService.ApplyAccentBrush() Start");

            try
            {
                var accent = _uiSettings.GetColorValue(UIColorType.Accent);
                var brush = new SolidColorBrush(accent);
                _root.Resources["AppAccentBrush"] = brush;
            }
            catch (Exception ex)
            {
                AppLogger.LogDebug($"ThemeManagementService.ApplyAccentBrush() error: {ex.Message}");
                // Use fallback color if UISettings fails
                var fallbackBrush = new SolidColorBrush(Colors.Blue);
                _root.Resources["AppAccentBrush"] = fallbackBrush;
            }
        }

        /// <summary>
        /// Customizes the title bar colors based on the specified theme.
        /// </summary>
        /// <param name="theme">The theme to use for title bar colors</param>
        public void UpdateTitleBar(ElementTheme theme)
        {
            if (_appWindow is null || !AppWindowTitleBar.IsCustomizationSupported())
            {
                return;
            }

            AppLogger.LogDebug("ThemeManagementService.UpdateTitleBar() Start");

            try
            {
                // Resolve actual theme if Default
                var actualTheme = theme;
                if (theme == ElementTheme.Default)
                {
                    actualTheme = GetSystemTheme();
                }

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

                if (actualTheme == ElementTheme.Dark)
                {
                    AppLogger.LogDebug("ThemeManagementService.UpdateTitleBar() - Setting dark theme");
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
                    AppLogger.LogDebug("ThemeManagementService.UpdateTitleBar() - Setting light theme");
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
            catch (Exception ex)
            {
                AppLogger.LogDebug($"ThemeManagementService.UpdateTitleBar() error: {ex.Message}");
                // Skip title bar customization if UISettings fails
            }
        }

        /// <summary>
        /// Gets the UISettings instance for responding to system color changes.
        /// Call this to subscribe to ColorValuesChanged events.
        /// </summary>
        public UISettings GetUISettings() => _uiSettings;

        /// <summary>
        /// Gets the root FrameworkElement (for accessing ActualTheme).
        /// </summary>
        public FrameworkElement? Root => _root;
    }
}
