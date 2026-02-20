using System;
using System.Runtime.InteropServices;
using System.Threading;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using CommonUtilities;

namespace RunGame
{
    public partial class App : Application
    {
        private Mutex? _instanceMutex;
        private MainWindow? _window;

        public override void Initialize()
        {
            AvaloniaXamlLoader.Load(this);
            ApplicationInitializationHelper.InitializeLogging("RunGame");
            ApplicationInitializationHelper.EnsureConfigurationFile("RunGame", DefaultConfigurations.RunGame);
            ExceptionHandlingHelper.RegisterGlobalExceptionHandlers();
        }

        public override void OnFrameworkInitializationCompleted()
        {
            var theme = LoadThemePreference() ?? ThemeManagementService.GetSystemTheme();
            RequestedThemeVariant = theme;

            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                var cmdArgs = Environment.GetCommandLineArgs();

                if (cmdArgs.Length < 2)
                {
                    ShowNativeMessageBox(
                        "This application cannot be run directly.\n\n" +
                        "RunGame is a companion application that must be launched from AnSAM " +
                        "(the main game library interface) by double-clicking a game.\n\n" +
                        "Please launch AnSAM instead to manage your Steam achievements and statistics.",
                        "RunGame - Invalid Launch Method",
                        MessageBoxIcon.Warning);
                    desktop.Shutdown();
                    return;
                }

                if (!long.TryParse(cmdArgs[1], out long gameId))
                {
                    ShowNativeMessageBox(
                        $"Invalid game ID parameter: \"{cmdArgs[1]}\"\n\n" +
                        "RunGame requires a valid Steam AppID as a command line parameter.\n\n" +
                        "This application is designed to be launched from AnSAM. " +
                        "Please use AnSAM to open games instead of running RunGame directly.",
                        "RunGame - Invalid Parameter",
                        MessageBoxIcon.Error);
                    desktop.Shutdown();
                    return;
                }

                // Check if another instance with the same game ID is already running
                string mutexName = $"Global\\RunGame_AppId_{gameId}";
                _instanceMutex = new Mutex(true, mutexName, out bool createdNew);

                if (!createdNew)
                {
                    ShowNativeMessageBox(
                        $"RunGame is already running for this game (AppID: {gameId}).\n\n" +
                        "Only one instance of RunGame can run per game at a time.\n\n" +
                        "Please use the existing RunGame window or close it before starting a new one.",
                        "RunGame - Already Running",
                        MessageBoxIcon.Information);
                    _instanceMutex.Dispose();
                    _instanceMutex = null;
                    desktop.Shutdown();
                    return;
                }

                _window = new MainWindow(gameId);
                _window.Closed += OnMainWindowClosed;
                desktop.MainWindow = _window;
            }

            base.OnFrameworkInitializationCompleted();
        }

        private void OnMainWindowClosed(object? sender, EventArgs e)
        {
            ReleaseMutex();
        }

        private void ReleaseMutex()
        {
            if (_instanceMutex != null)
            {
                try
                {
                    _instanceMutex.ReleaseMutex();
                }
                catch (ApplicationException)
                {
                    // Mutex was not owned by this thread, ignore
                }
                _instanceMutex.Dispose();
                _instanceMutex = null;
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

        #region Native MessageBox

        private enum MessageBoxIcon : uint
        {
            None = 0x00000000,
            Error = 0x00000010,
            Question = 0x00000020,
            Warning = 0x00000030,
            Information = 0x00000040
        }

        private static void ShowNativeMessageBox(string message, string title, MessageBoxIcon icon)
        {
            // MB_OK = 0x00000000, MB_TOPMOST = 0x00040000, MB_SETFOREGROUND = 0x00010000
            uint flags = 0x00000000 | (uint)icon | 0x00040000 | 0x00010000;
            _ = MessageBox(IntPtr.Zero, message, title, flags);
        }

        [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16, EntryPoint = "MessageBoxW")]
        private static partial int MessageBox(IntPtr hWnd, string lpText, string lpCaption, uint uType);

        #endregion
    }
}
