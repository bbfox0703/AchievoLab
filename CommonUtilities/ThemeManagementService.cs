using Avalonia;
using Avalonia.Styling;
using Microsoft.Win32;
using System;

namespace CommonUtilities
{
    /// <summary>
    /// Manages theme application for Avalonia applications.
    /// Shared across AnSAM, RunGame, and MyOwnGames for consistent theming.
    /// </summary>
    public class ThemeManagementService
    {
        /// <summary>
        /// Determines the current system theme from the Windows registry.
        /// </summary>
        /// <returns>ThemeVariant.Light or ThemeVariant.Dark based on system settings. Defaults to Light on error.</returns>
        public static ThemeVariant GetSystemTheme()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
                var value = key?.GetValue("AppsUseLightTheme");
                if (value is int i)
                {
                    return i != 0 ? ThemeVariant.Light : ThemeVariant.Dark;
                }
            }
            catch
            {
                // Fall back to light theme if the registry is unavailable
            }
            return ThemeVariant.Light;
        }

        /// <summary>
        /// Converts a ThemeVariant to determine if it's dark.
        /// Resolves Default to the actual system theme.
        /// </summary>
        /// <param name="theme">The theme to check</param>
        /// <returns>True if the resolved theme is dark</returns>
        public static bool IsDarkTheme(ThemeVariant theme)
        {
            var resolved = theme == ThemeVariant.Default ? GetSystemTheme() : theme;
            return resolved == ThemeVariant.Dark;
        }

        /// <summary>
        /// Applies the specified theme to the application.
        /// </summary>
        /// <param name="theme">The theme to apply</param>
        public void ApplyTheme(ThemeVariant theme)
        {
            var app = Application.Current;
            if (app is null)
            {
                AppLogger.LogDebug("ThemeManagementService.ApplyTheme() called before Application.Current is available");
                return;
            }

            AppLogger.LogDebug($"ThemeManagementService.ApplyTheme() - Setting theme to {theme}");

            app.RequestedThemeVariant = theme;

            AppLogger.LogDebug($"ThemeManagementService.ApplyTheme() Complete - Theme set to {theme}");
        }
    }
}
