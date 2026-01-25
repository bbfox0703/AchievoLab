using Microsoft.UI.Xaml;
using System;
using System.Runtime.InteropServices;
using System.Threading;
using Windows.Storage;
using CommonUtilities;

namespace RunGame
{
    /// <summary>
    /// Provides application-specific behavior for the RunGame achievement manager.
    /// Handles command-line parsing for AppID, theme management, and application initialization.
    /// </summary>
    public partial class App : Application
    {
        /// <summary>
        /// Gets the application-specific local settings container.
        /// May be null if LocalSettings access fails during initialization.
        /// </summary>
        internal static ApplicationDataContainer? LocalSettings { get; private set; }

        /// <summary>
        /// Mutex to prevent multiple instances of the same game from running.
        /// </summary>
        private Mutex? _instanceMutex;

        /// <summary>
        /// Converts a WinUI ElementTheme to a WinUI ApplicationTheme.
        /// Resolves ElementTheme.Default to the current system theme.
        /// </summary>
        /// <param name="theme">The ElementTheme to convert.</param>
        /// <returns>The corresponding ApplicationTheme (Light or Dark).</returns>
        internal static ApplicationTheme ToApplicationTheme(ElementTheme theme)
            => ThemeManagementService.ToApplicationTheme(theme);

        /// <summary>
        /// Initializes the singleton application object.
        /// Sets up logging and ensures configuration file exists with defaults.
        /// </summary>
        public App()
        {
            this.InitializeComponent();
            ApplicationInitializationHelper.InitializeLogging("RunGame");
            ApplicationInitializationHelper.EnsureConfigurationFile("RunGame", DefaultConfigurations.RunGame);
        }

        /// <summary>
        /// Invoked when the application is launched.
        /// Validates command-line arguments for AppID parameter and creates the main window.
        /// Displays an error dialog and exits if launched without proper parameters.
        /// </summary>
        /// <param name="args">Details about the launch request and process.</param>
        protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
        {
            try
            {
                LocalSettings = ApplicationData.Current.LocalSettings;
            }
            catch (InvalidOperationException ex)
            {
                System.Diagnostics.Debug.WriteLine($"InvalidOperationException in LocalSettings access: {ex.Message}");
                LocalSettings = null;
            }
            catch (System.IO.IOException ex)
            {
                System.Diagnostics.Debug.WriteLine($"IOException in LocalSettings access: {ex.Message}");
                LocalSettings = null;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Exception in LocalSettings access: {ex.Message}");
                LocalSettings = null;
            }

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
                Exit();
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
                Exit();
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
                Exit();
                return;
            }

            m_window = new MainWindow(gameId);
            m_window.Closed += OnMainWindowClosed;
            m_window.Activate();
        }

        /// <summary>
        /// Handles main window closed event to release the mutex.
        /// </summary>
        private void OnMainWindowClosed(object sender, WindowEventArgs args)
        {
            ReleaseMutex();
        }

        /// <summary>
        /// Releases the instance mutex if it exists.
        /// </summary>
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

        #region Native MessageBox

        /// <summary>
        /// Icon types for native message box.
        /// </summary>
        private enum MessageBoxIcon : uint
        {
            None = 0x00000000,
            Error = 0x00000010,
            Question = 0x00000020,
            Warning = 0x00000030,
            Information = 0x00000040
        }

        /// <summary>
        /// Shows a native Win32 message box that works without a WinUI window.
        /// </summary>
        /// <param name="message">The message to display.</param>
        /// <param name="title">The title of the message box.</param>
        /// <param name="icon">The icon to display.</param>
        private static void ShowNativeMessageBox(string message, string title, MessageBoxIcon icon)
        {
            // MB_OK = 0x00000000, MB_TOPMOST = 0x00040000, MB_SETFOREGROUND = 0x00010000
            uint flags = 0x00000000 | (uint)icon | 0x00040000 | 0x00010000;
            _ = MessageBox(IntPtr.Zero, message, title, flags);
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern int MessageBox(IntPtr hWnd, string lpText, string lpCaption, uint uType);

        #endregion

        /// <summary>
        /// The main application window instance.
        /// </summary>
        private Window? m_window;
    }
}
