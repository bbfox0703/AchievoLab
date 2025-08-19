using Microsoft.UI.Xaml;
using Microsoft.Win32;
using System;

namespace AnSAM.RunGame.Services
{
    public static class ThemeService
    {
        public static ElementTheme GetCurrentTheme()
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
                // Fall back to light theme if we can't read the registry
            }
            
            return ElementTheme.Light;
        }

        public static void ApplyTheme(FrameworkElement element)
        {
            element.RequestedTheme = GetCurrentTheme();
        }
    }
}