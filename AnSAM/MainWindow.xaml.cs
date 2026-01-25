using AnSAM.Services;
using AnSAM.Steam;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;
using WinRT.Interop;
using Windows.Storage;
using Windows.UI;
using Windows.UI.ViewManagement;
using CommonUtilities;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace AnSAM
{
    /// <summary>
    /// An empty window that can be used on its own or navigated to within a Frame.
    /// </summary>
    public sealed partial class MainWindow : Window
    {
        public ObservableCollection<GameItem> Games { get; } = new();
        private readonly List<GameItem> _allGames = new();
        private readonly SteamClient _steamClient;
        private readonly AppWindow _appWindow;
        private readonly HttpClient _imageHttpClient = new();
        private readonly SharedImageService _imageService;
        private volatile bool _isLanguageSwitching = false;
        private readonly DispatcherQueue _dispatcher;
        private string _currentLanguage = "english";
        private CancellationTokenSource? _languageSwitchCts;
        private readonly SemaphoreSlim _languageSwitchLock = new(1, 1); // Mutex for language switch
        private CancellationTokenSource _sequentialLoadCts = new(); // Cancel sequential image loading

        // Progress bar management with priority system
        private enum ProgressContext
        {
            None = 0,
            InitialLoad = 1,      // Highest priority
            LanguageSwitch = 2,   // Medium priority
            CacheCheck = 3        // Lowest priority
        }
        private ProgressContext _currentProgressContext = ProgressContext.None;
        private readonly object _progressLock = new object();

        private bool _autoLoaded;
        private bool _languageInitialized;
        private ScrollViewer? _gamesScrollViewer;
        private readonly DispatcherTimer _cdnStatsTimer;
        private DispatcherQueueTimer? _searchDebounceTimer;
        private readonly ThemeManagementService _themeService = new();
        private readonly ApplicationSettingsService _settingsService = new();

        public MainWindow(SteamClient steamClient, ElementTheme theme)
        {
            _steamClient = steamClient;
            _imageService = new SharedImageService(_imageHttpClient);
            InitializeComponent();
            _dispatcher = DispatcherQueue;

            InitializeLanguageComboBox();

            // 取得 AppWindow
            var hwnd = WindowNative.GetWindowHandle(this);
            var winId = Win32Interop.GetWindowIdFromWindow(hwnd);
            _appWindow = AppWindow.GetFromWindowId(winId);
            // 設定 Icon：指向打包後的實體檔案路徑
            var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "AnSAM.ico");
            if (File.Exists(iconPath))
                _appWindow.SetIcon(iconPath);

            if (Content is FrameworkElement root)
            {
                // Initialize theme service
                _themeService.Initialize(this, root);
                var uiSettings = _themeService.GetUISettings();
                uiSettings.ColorValuesChanged += UiSettings_ColorValuesChanged;

                ApplyTheme(theme, save: false);
                root.KeyDown += OnWindowKeyDown;
                if (AppWindowTitleBar.IsCustomizationSupported())
                {
                    root.ActualThemeChanged += (_, _) => UpdateTitleBar(root.ActualTheme);
                }
            }

            if (!_steamClient.Initialized)
            {
                StatusText.Text = "Steam unavailable";
            }
            GameListService.StatusChanged += OnGameListStatusChanged;
            GameListService.ProgressChanged += OnGameListProgressChanged;
            Activated += OnWindowActivated;
            Closed += OnWindowClosed;

            // Initialize GameLauncher (locate RunGame.exe once at startup)
            GameLauncher.Initialize();

            // Initialize CDN statistics timer
            _cdnStatsTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(2)
            };
            _cdnStatsTimer.Tick += CdnStatsTimer_Tick;
            _cdnStatsTimer.Start();

            // Initialize search debounce timer for real-time filtering
            _searchDebounceTimer = DispatcherQueue.CreateTimer();
            _searchDebounceTimer.Interval = TimeSpan.FromMilliseconds(300);
            _searchDebounceTimer.IsRepeating = false;
        }

        /// <summary>
        /// Populates the language selector and applies the initial language.
        /// </summary>
        private void InitializeLanguageComboBox()
        {
            var languages = SteamLanguageResolver.SupportedLanguages;

            var osLanguage = SteamLanguageResolver.GetSteamLanguage();
            if (!languages.Contains(osLanguage, StringComparer.OrdinalIgnoreCase))
            {
                osLanguage = "english";
            }

            var ordered = languages.Where(l => !string.Equals(l, osLanguage, StringComparison.OrdinalIgnoreCase) &&
                                               !string.Equals(l, "english", StringComparison.OrdinalIgnoreCase))
                                   .OrderBy(l => l)
                                   .ToList();

            ordered.Insert(0, "english");
            if (!string.Equals(osLanguage, "english", StringComparison.OrdinalIgnoreCase))
            {
                ordered.Insert(0, osLanguage);
            }

            foreach (var lang in ordered)
            {
                LanguageComboBox.Items.Add(lang);
            }

            _settingsService.TryGetString("Language", out var saved);
            // Always initialize to English, regardless of saved settings
            string initial = "english";
            
            LanguageComboBox.SelectedItem = initial;
            SteamLanguageResolver.OverrideLanguage = initial;
            _imageService.SetLanguage(initial).GetAwaiter().GetResult();
            _currentLanguage = initial; // Set current language to English
            _languageInitialized = true;
        }
        /// <summary>
        /// Applies a theme to the window and optionally persists the choice.
        /// </summary>
        private void ApplyTheme(ElementTheme theme, bool save = true)
        {
            _themeService.ApplyTheme(theme);

            StatusText.Text = theme switch
            {
                ElementTheme.Default => "Theme: System default",
                ElementTheme.Light => "Theme: Light",
                ElementTheme.Dark => "Theme: Dark",
                _ => "Theme: ?",
            };

            if (save)
            {
                _settingsService.TrySetEnum("AppTheme", theme);
            }

            // Don't set Application.RequestedTheme to avoid COMException in WinUI 3
        }
        /// <summary>Switches to the system theme.</summary>
        private void Theme_Default_Click(object sender, RoutedEventArgs e) => ApplyTheme(ElementTheme.Default);
        /// <summary>Switches to the light theme.</summary>
        private void Theme_Light_Click(object sender, RoutedEventArgs e) => ApplyTheme(ElementTheme.Light);
        /// <summary>Switches to the dark theme.</summary>
        private void Theme_Dark_Click(object sender, RoutedEventArgs e) => ApplyTheme(ElementTheme.Dark);

        /// <summary>Launches RunGame with a manually entered App ID.</summary>
        private async void LaunchByAppId_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var dialog = new ContentDialog
                {
                    Title = "Launch by App ID",
                    Content = new TextBox
                    {
                        PlaceholderText = "Enter Steam App ID (e.g., 730 for CS:GO)",
                        Name = "AppIdInput"
                    },
                    PrimaryButtonText = "Launch",
                    CloseButtonText = "Cancel",
                    DefaultButton = ContentDialogButton.Primary,
                    XamlRoot = Content?.XamlRoot
                };

                if (Content?.XamlRoot == null)
                    return; // UI not ready

                var result = await dialog.ShowAsync();
                if (result == ContentDialogResult.Primary)
                {
                    var textBox = dialog.Content as TextBox;
                    var input = textBox?.Text?.Trim();

                    if (string.IsNullOrEmpty(input))
                    {
                        StatusText.Text = "No App ID entered";
                        return;
                    }

                    if (!uint.TryParse(input, out var appId) || appId == 0)
                    {
                        StatusText.Text = "Invalid App ID format";
                        return;
                    }

                    // Verify ownership and add to usergames.xml
                    var baseDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AchievoLab");
                    bool isOwned = Services.GameCacheService.TryAddUserGame(baseDir, _steamClient, (int)appId);

                    if (!isOwned && _steamClient.Initialized)
                    {
                        // User doesn't own this game, but allow launch anyway (for testing/debugging)
                        StatusText.Text = $"Warning: You don't own App ID {appId}. Launching anyway...";
                    }
                    else if (isOwned)
                    {
                        StatusText.Text = $"App ID {appId} verified and saved to usergames.xml";
                    }

                    // Create a temporary GameItem and launch RunGame
                    var tempGame = new GameItem(
                        $"App {appId}",     // title
                        (int)appId,         // id
                        DispatcherQueue     // dispatcher
                    );

                    // Set icon to default no_icon.png
                    tempGame.IconUri = "ms-appx:///Assets/no_icon.png";

                    StartAchievementManager(tempGame);

                    if (isOwned)
                    {
                        StatusText.Text = $"Launched App ID {appId} (saved to cache)";
                    }
                    else
                    {
                        StatusText.Text = $"Launched App ID {appId} (not verified)";
                    }
                }
            }
            catch (Exception ex)
            {
                AppLogger.LogDebug($"LaunchByAppId_Click error: {ex}");
                StatusText.Text = "Failed to launch by App ID";
            }
        }

        /// <summary>
        /// Handles language changes and reloads localized resources.
        /// </summary>
        private async void LanguageComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_languageInitialized)
            {
                return;
            }

            if (LanguageComboBox.SelectedItem is string lang)
            {
                // Check if language actually changed
                if (lang == _currentLanguage)
                {
                    AppLogger.LogDebug($"Language not changed, staying with: {lang}");
                    return; // No action needed if language hasn't changed
                }

                AppLogger.LogDebug($"Language changed from {_currentLanguage} to {lang}");

                bool lockAcquired = false;
                try
                {
                    // CRITICAL: Wait for any previous language switch to complete before starting new one
                    // This prevents hang/crash when switching while downloads are in progress
                    // By NOT cancelling previous switches, we ensure each switch completes fully
                    // This prevents UI inconsistency where titles/images are from different languages
                    await _languageSwitchLock.WaitAsync();
                    lockAcquired = true;
                    // Create a new CancellationTokenSource for THIS switch only
                    // We no longer cancel previous switches - they complete naturally via mutex serialization
                    var cts = new CancellationTokenSource();
                    _languageSwitchCts = cts;

                    try
                    {
                        _isLanguageSwitching = true;

                        SteamLanguageResolver.OverrideLanguage = lang;
                        // NOTE: SetLanguage internally cancels pending downloads and waits up to 5s for them to complete
                        await _imageService.SetLanguage(lang);

                        _settingsService.TrySetString("Language", lang);

                        // CRITICAL: Scroll to top FIRST to avoid scroll position conflicts during re-sorting
                        var scrollViewer = GetOrAttachScrollViewer();
                        if (scrollViewer != null)
                        {
                            scrollViewer.ChangeView(null, 0, null, true); // Instant scroll to top
                            await Task.Delay(100); // Wait for scroll to complete
                        }

                        // Load localized titles from steam_games.xml if switching to non-English
                        if (lang != "english")
                        {
                            LoadLocalizedTitlesFromXml();
                        }

                        // Update game titles for the new language (with collection re-sorting)
                        UpdateAllGameTitles(lang);

                        // Wait for any ongoing UI operations to complete before starting image refresh
                        await Task.Delay(200);

                        // CRITICAL: Save focused element before language switch to restore it after
                        // CleanSlateLanguageSwitcher rebinds ItemsSource, causing focus to shift to GridView
                        var focusedElement = Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(Content.XamlRoot) as UIElement;

                        // Refresh images using batched strategy
                        await RefreshImagesForLanguageSwitch(lang);

                        // Restore focus to the previously focused element (typically LanguageComboBox)
                        if (focusedElement != null)
                        {
                            focusedElement.Focus(FocusState.Programmatic);
                        }

                        StatusText.Text = lang == "english"
                            ? $"Switched to English - displaying original titles"
                            : $"Switched to {lang} - using localized titles where available";
                    }
                    catch (Exception ex)
                    {
                        StatusProgress.IsIndeterminate = false;
                        ClearProgress(ProgressContext.LanguageSwitch);
                        StatusText.Text = "Language switch failed";

                        AppLogger.LogDebug($"Language switch error: {ex}");

                        if (Content?.XamlRoot != null)
                        {
                            var dialog = new ContentDialog
                            {
                                Title = "Language switch failed",
                                Content = "Unable to switch language. Please try again.",
                                CloseButtonText = "OK",
                                XamlRoot = Content.XamlRoot
                            };

                            await dialog.ShowAsync();
                        }
                        StatusText.Text = "Ready";
                    }
                    finally
                    {
                        _isLanguageSwitching = false;
                    }
                }
                catch (ObjectDisposedException)
                {
                    // Lock was disposed during shutdown, ignore
                    AppLogger.LogDebug("Language switch lock disposed during operation");
                }
                catch (Exception ex)
                {
                    AppLogger.LogDebug($"LanguageComboBox_SelectionChanged unexpected error: {ex}");
                }
                finally
                {
                    // CRITICAL: Always release the mutex lock, even if cancelled or error occurred
                    if (lockAcquired)
                    {
                        try
                        {
                            _languageSwitchLock.Release();
                            AppLogger.LogDebug($"Language switch lock released for {lang}");
                        }
                        catch (ObjectDisposedException)
                        {
                            // Lock was disposed, ignore
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Updates title bar colors for the specified theme.
        /// </summary>
        private void UpdateTitleBar(ElementTheme theme)
        {
            _themeService.UpdateTitleBar(theme);
        }

        /// <summary>
        /// Applies the accent color brush to the window and updates StatusText foreground.
        /// </summary>
        private void ApplyAccentBrush()
        {
            _themeService.ApplyAccentBrush();

            // Additional UI-specific logic
            if (Content is FrameworkElement root && root.Resources.TryGetValue("AppAccentBrush", out var brushObj) && brushObj is SolidColorBrush brush)
            {
                StatusText.Foreground = brush;
            }
        }

        /// <summary>
        /// Responds to system color changes by updating accent brushes and title bar.
        /// </summary>
        private void UiSettings_ColorValuesChanged(UISettings sender, object args)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                ApplyAccentBrush();
                if (_themeService.Root != null)
                {
                    UpdateTitleBar(_themeService.Root.ActualTheme);
                }
            });
        }

        // ]i^b MainWindow() 媞c讀^W隉G
        //if (Windows.Storage.ApplicationData.Current.LocalSettings.Values.TryGetValue("AppTheme", out var t)
        //    && Enum.TryParse<ElementTheme>(t?.ToString(), out var saved)) {
        //    ApplyTheme(saved);
        //}
        /// <summary>
        /// Performs a refresh when the window is first activated.
        /// </summary>
        private void OnWindowActivated(object sender, WindowActivatedEventArgs args)
        {
            if (_autoLoaded) return;
            _autoLoaded = true;
            if (!DispatcherQueue.TryEnqueue(async () =>
            {
                await RefreshAsync();
            }))
            {
                AppLogger.LogDebug("Failed to enqueue RefreshAsync - DispatcherQueue may be shutting down");
            }
        }

        /// <summary>
        /// Handles page navigation keys for the games list.
        /// </summary>
        private void OnWindowKeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key != Windows.System.VirtualKey.PageDown &&
                e.Key != Windows.System.VirtualKey.PageUp &&
                e.Key != Windows.System.VirtualKey.Home &&
                e.Key != Windows.System.VirtualKey.End)
                return;

            var sv = GetOrAttachScrollViewer();
            if (sv == null)
                return;

            double? offset = null;
            var delta = sv.ViewportHeight;

            switch (e.Key)
            {
                case Windows.System.VirtualKey.PageDown when delta > 0:
                    offset = sv.VerticalOffset + delta;
                    break;
                case Windows.System.VirtualKey.PageUp when delta > 0:
                    offset = sv.VerticalOffset - delta;
                    break;
                case Windows.System.VirtualKey.Home:
                    offset = 0;
                    break;
                case Windows.System.VirtualKey.End:
                    offset = sv.ScrollableHeight;
                    break;
            }

            if (offset.HasValue)
            {
                sv.ChangeView(null, offset.Value, null);
                e.Handled = true;
            }
        }

        /// <summary>
        /// Updates CDN statistics display
        /// </summary>
        private void CdnStatsTimer_Tick(object? sender, object e)
        {
            try
            {
                // Defensive checks to prevent crashes during shutdown or disposal
                if (_imageService == null || StatusCdn == null)
                {
                    return;
                }

                var stats = _imageService.GetCdnStats();
                var pendingCount = _imageService.GetPendingRequestsCount();
                var hasActiveDownloads = pendingCount > 0 || stats.Values.Any(s => s.Active > 0);

                // Reset status when no active downloads
                if (!hasActiveDownloads)
                {
                    StatusCdn.Text = string.Empty;
                    return;
                }

                StatusCdn.Text = CdnStatsFormatter.FormatCdnStats(stats);
            }
            catch (ObjectDisposedException)
            {
                // Ignore if objects are disposed (during shutdown)
            }
            catch (Exception ex)
            {
                AppLogger.LogDebug($"CDN stats update error: {ex.Message}");
            }
        }

        /// <summary>
        /// Unsubscribes from events and disposes resources when closing.
        /// </summary>
        private void OnWindowClosed(object sender, WindowEventArgs args)
        {
            _cdnStatsTimer?.Stop();
            if (_cdnStatsTimer != null)
            {
                _cdnStatsTimer.Tick -= CdnStatsTimer_Tick;
            }

            GameListService.StatusChanged -= OnGameListStatusChanged;
            GameListService.ProgressChanged -= OnGameListProgressChanged;
            _themeService.GetUISettings().ColorValuesChanged -= UiSettings_ColorValuesChanged;
            Activated -= OnWindowActivated;

            if (_gamesScrollViewer != null)
            {
                _gamesScrollViewer.ViewChanged -= GamesScrollViewer_ViewChanged;
            }

            _imageService.Dispose();
            _imageHttpClient.Dispose();

            if (Content is FrameworkElement root)
            {
                root.KeyDown -= OnWindowKeyDown;
            }
        }

        /// <summary>
        /// Lazily loads cover images as items appear in the list.
        /// </summary>
        private void GamesView_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs e)
        {
            if (e.InRecycleQueue)
                return;

            if (e.Item is not GameItem game)
                return;

            // Skip during language switching to avoid collection modification conflicts
            if (_isLanguageSwitching)
            {
                e.Handled = true;
                return;
            }

            // SIMPLIFIED: ContainerContentChanging now does NOTHING
            // Images are loaded sequentially from top to bottom via StartSequentialImageLoading()
            // This avoids WinUI 3 native crash from thundering herd of UI updates during fast scrolling
            // Trade-off: User may see lower games before their images load, but stability > UX
            e.Handled = true;
        }


        /// <summary>
        /// Opens the achievement manager when a game card is double-tapped.
        /// </summary>
        private void GameCard_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
        {
            AppLogger.LogDebug($"GameCard_DoubleTapped: Event triggered");
            AppLogger.LogDebug($"GameCard_DoubleTapped: sender type = {sender?.GetType().Name ?? "null"}");

            if (sender is FrameworkElement element)
            {
                AppLogger.LogDebug($"GameCard_DoubleTapped: sender is FrameworkElement");
                AppLogger.LogDebug($"GameCard_DoubleTapped: DataContext type = {element.DataContext?.GetType().Name ?? "null"}");

                // Try direct DataContext cast
                if (element.DataContext is GameItem game)
                {
                    AppLogger.LogDebug($"GameCard_DoubleTapped: Game item found via DataContext - {game.ID} ({game.Title})");
                    if (game.IsManagerAvailable)
                    {
                        AppLogger.LogDebug($"GameCard_DoubleTapped: Manager available, launching");
                        StartAchievementManager(game);
                    }
                    else
                    {
                        AppLogger.LogDebug($"GameCard_DoubleTapped: Manager NOT available");
                        StatusText.Text = "Achievement manager not found";
                    }
                    return;
                }

                // Try getting GridViewItem parent and use ItemFromContainer
                var gridViewItem = FindParent<GridViewItem>(element);
                if (gridViewItem != null)
                {
                    AppLogger.LogDebug($"GameCard_DoubleTapped: Found GridViewItem parent");

                    // Try Content first
                    if (gridViewItem.Content is GameItem gameFromContent)
                    {
                        AppLogger.LogDebug($"GameCard_DoubleTapped: Game item found via GridViewItem.Content - {gameFromContent.ID} ({gameFromContent.Title})");
                        if (gameFromContent.IsManagerAvailable)
                        {
                            StartAchievementManager(gameFromContent);
                        }
                        else
                        {
                            StatusText.Text = "Achievement manager not found";
                        }
                        return;
                    }

                    // Try ItemFromContainer (WinUI 3 compiled binding approach)
                    var dataItem = GamesView.ItemFromContainer(gridViewItem);
                    AppLogger.LogDebug($"GameCard_DoubleTapped: ItemFromContainer returned: {dataItem?.GetType().Name ?? "null"}");

                    if (dataItem is GameItem gameFromContainer)
                    {
                        AppLogger.LogDebug($"GameCard_DoubleTapped: Game item found via ItemFromContainer - {gameFromContainer.ID} ({gameFromContainer.Title})");
                        if (gameFromContainer.IsManagerAvailable)
                        {
                            StartAchievementManager(gameFromContainer);
                        }
                        else
                        {
                            StatusText.Text = "Achievement manager not found";
                        }
                        return;
                    }
                    else
                    {
                        AppLogger.LogDebug($"GameCard_DoubleTapped: GridViewItem.Content is null and ItemFromContainer failed");
                    }
                }
                else
                {
                    AppLogger.LogDebug($"GameCard_DoubleTapped: Could not find GridViewItem parent");
                }
            }
            else
            {
                AppLogger.LogDebug($"GameCard_DoubleTapped: sender is not FrameworkElement");
            }
        }

        private T? FindParent<T>(DependencyObject child) where T : DependencyObject
        {
            var parent = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(child);
            while (parent != null)
            {
                if (parent is T typedParent)
                    return typedParent;
                parent = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(parent);
            }
            return null;
        }


        /// <summary>
        /// Handles the context menu command to launch the achievement manager for a game.
        /// </summary>
        private void OnLaunchManagerClicked(object sender, RoutedEventArgs e)
        {
            AppLogger.LogDebug($"OnLaunchManagerClicked: Event triggered");

            if (sender is MenuFlyoutItem menuItem)
            {
                AppLogger.LogDebug($"OnLaunchManagerClicked: sender is MenuFlyoutItem");
                AppLogger.LogDebug($"OnLaunchManagerClicked: CommandParameter type = {menuItem.CommandParameter?.GetType().Name ?? "null"}");

                // Try CommandParameter (set via x:Bind in XAML)
                if (menuItem.CommandParameter is GameItem game)
                {
                    AppLogger.LogDebug($"OnLaunchManagerClicked: Game item found via CommandParameter - {game.ID} ({game.Title})");
                    if (game.IsManagerAvailable)
                    {
                        StartAchievementManager(game);
                    }
                    else
                    {
                        StatusText.Text = "Achievement manager not found";
                    }
                    return;
                }
            }
            else
            {
                AppLogger.LogDebug($"OnLaunchManagerClicked: sender is not MenuFlyoutItem: {sender?.GetType().Name ?? "null"}");
            }

            AppLogger.LogDebug($"OnLaunchManagerClicked: Could not get GameItem");
        }

        /// <summary>
        /// Launches the selected game via the context menu.
        /// </summary>
        private void OnLaunchGameClicked(object sender, RoutedEventArgs e)
        {
            AppLogger.LogDebug($"OnLaunchGameClicked: Event triggered");

            if (sender is MenuFlyoutItem menuItem)
            {
                AppLogger.LogDebug($"OnLaunchGameClicked: sender is MenuFlyoutItem");
                AppLogger.LogDebug($"OnLaunchGameClicked: CommandParameter type = {menuItem.CommandParameter?.GetType().Name ?? "null"}");

                // Try CommandParameter (set via x:Bind in XAML)
                if (menuItem.CommandParameter is GameItem game)
                {
                    AppLogger.LogDebug($"OnLaunchGameClicked: Game item found via CommandParameter - {game.ID} ({game.Title})");
                    StartGame(game);
                    return;
                }
            }
            else
            {
                AppLogger.LogDebug($"OnLaunchGameClicked: sender is not MenuFlyoutItem: {sender?.GetType().Name ?? "null"}");
            }

            AppLogger.LogDebug($"OnLaunchGameClicked: Could not get GameItem");
        }

        /// <summary>
        /// Launches the bundled achievement manager for the specified game and updates status text.
        /// </summary>
        private void StartAchievementManager(GameItem game)
        {
            StatusText.Text = $"Launching achievement manager for {game.Title}...";
            StatusProgress.IsIndeterminate = true;

            // Only clear extra text if no operation is in progress
            lock (_progressLock)
            {
                if (_currentProgressContext == ProgressContext.None)
                {
                    StatusExtra.Text = string.Empty;
                }
            }

            GameLauncher.LaunchAchievementManager(game);
            StatusProgress.IsIndeterminate = false;
            StatusText.Text = "Ready";
        }

        /// <summary>
        /// Launches the specified game and updates status indicators.
        /// </summary>
        private void StartGame(GameItem game)
        {
            StatusText.Text = $"Launching {game.Title}...";
            StatusProgress.IsIndeterminate = true;

            // Only clear extra text if no operation is in progress
            lock (_progressLock)
            {
                if (_currentProgressContext == ProgressContext.None)
                {
                    StatusExtra.Text = string.Empty;
                }
            }

            GameLauncher.Launch(game);
            StatusProgress.IsIndeterminate = false;
            StatusText.Text = "Ready";
        }


        /// <summary>
        /// Retrieves game data and rebuilds the game list UI.
        /// </summary>
        private async Task RefreshAsync()
        {
            try
            {
                if (!_steamClient.Initialized)
                {
                    StatusProgress.IsIndeterminate = false;
                    ClearProgress(ProgressContext.InitialLoad);
                    StatusText.Text = "Steam unavailable";

                    // Only show dialog if XamlRoot is available (window is fully initialized)
                    if (Content?.XamlRoot != null)
                    {
                        var dialog = new ContentDialog
                        {
                            Title = "Steam unavailable",
                            Content = "Unable to refresh because Steam is not available.",
                            CloseButtonText = "OK",
                            XamlRoot = Content.XamlRoot
                        };

                        await dialog.ShowAsync();
                    }
                    return;
                }

                StatusText.Text = "Refresh";
                StatusProgress.IsIndeterminate = false;
                UpdateProgress(ProgressContext.InitialLoad, 0, "0%");

                var baseDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AchievoLab");

                var http = HttpClientProvider.Shared;
                var apps = await GameCacheService.RefreshAsync(baseDir, _steamClient, http);

                var (allGames, filteredGames) = await BuildGameListAsync(apps, null);

                _ = DispatcherQueue.TryEnqueue(() =>
                {
                    try
                    {
                        _allGames.Clear();
                        _allGames.AddRange(allGames);

                        // Load localized titles if current language is not English
                        if (_currentLanguage != "english")
                        {
                            LoadLocalizedTitlesFromXml();
                        }

                        // Update all game titles to current language
                        UpdateAllGameTitles(_currentLanguage);

                        Games.Clear();
                        foreach (var game in filteredGames)
                        {
                            Games.Add(game);
                        }

                        StatusText.Text = _steamClient.Initialized
                            ? $"Loaded {_allGames.Count} games (Language: {_currentLanguage})"
                            : $"Steam unavailable - showing {_allGames.Count} games (Language: {_currentLanguage})";

                        // CRITICAL: Start sequential image loading after initial load
                        StartSequentialImageLoading();
                    }
                    catch (Exception ex)
                    {
                        AppLogger.LogDebug($"Error in RefreshAsync UI update: {ex}");
                    }
                });
            }
            catch (Exception ex)
            {
                AppLogger.LogDebug($"Error in RefreshAsync: {ex.GetType().Name}: {ex.Message}");
                AppLogger.LogDebug($"Stack trace: {ex.StackTrace}");

                _ = DispatcherQueue.TryEnqueue(() =>
                {
                    try
                    {
                        StatusText.Text = $"Refresh failed: {ex.Message}";
                        ClearProgress(ProgressContext.InitialLoad);
                        StatusProgress.IsIndeterminate = false;
                    }
                    catch { /* Ignore UI update errors during error handling */ }
                });
            }
        }

        /// <summary>
        /// Builds the game item collection and returns the complete and filtered lists.
        /// </summary>
        private Task<(List<GameItem> allGames, List<GameItem> filteredGames)> BuildGameListAsync(IEnumerable<SteamAppData> apps, string? keyword)
        {
            return Task.Run(() =>
            {
                var allGames = new List<GameItem>();
                foreach (var app in apps)
                {
                    allGames.Add(GameItem.FromSteamApp(app, _dispatcher));
                }

                allGames.Sort((a, b) => string.Compare(a.Title, b.Title, StringComparison.OrdinalIgnoreCase));

                List<GameItem> filtered;
                if (string.IsNullOrEmpty(keyword))
                {
                    filtered = new List<GameItem>(allGames);
                }
                else
                {
                    filtered = allGames
                        .Where(g => g.Title.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                        .ToList();
                }

                return (allGames, filtered);
            });
        }


        /// <summary>
        /// Filters games when the user submits a search query.
        /// </summary>
        private void SearchBox_QuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
        {
            var keyword = args.QueryText?.Trim();
            FilterGames(keyword);
        }

        /// <summary>
        /// Provides autocomplete suggestions as the search text changes.
        /// </summary>
        private void SearchBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
        {
            if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput)
            {
                var keyword = sender.Text.Trim();

                // Provide autocomplete suggestions (up to 10 matches)
                List<string> matches;
                if (string.IsNullOrWhiteSpace(keyword))
                {
                    matches = new List<string>();
                }
                else
                {
                    var titles = new HashSet<string>();
                    matches = new List<string>();
                    foreach (var game in Games)
                    {
                        if (game.Title.Contains(keyword, StringComparison.OrdinalIgnoreCase) && titles.Add(game.Title))
                        {
                            matches.Add(game.Title);
                            if (matches.Count == 10)
                            {
                                break;
                            }
                        }
                    }
                }
                sender.ItemsSource = matches;

                // Real-time filtering with debounce (300ms delay)
                if (_searchDebounceTimer != null)
                {
                    _searchDebounceTimer.Stop();
                    _searchDebounceTimer.Tick -= OnSearchDebounceTimerTick;
                    _searchDebounceTimer.Tick += OnSearchDebounceTimerTick;
                    _searchDebounceTimer.Start();
                }
            }
        }

        /// <summary>
        /// Debounce timer tick handler - performs the actual filtering after user stops typing.
        /// </summary>
        private void OnSearchDebounceTimerTick(DispatcherQueueTimer sender, object args)
        {
            sender.Stop();
            var keyword = SearchBox.Text.Trim();
            FilterGames(keyword);
        }

        /// <summary>
        /// Focuses the game corresponding to a chosen suggestion.
        /// </summary>
        private void SearchBox_SuggestionChosen(AutoSuggestBox sender, AutoSuggestBoxSuggestionChosenEventArgs args)
        {
            if (args.SelectedItem is string title)
            {
                var game = Games.FirstOrDefault(g => g.Title.Equals(title, StringComparison.OrdinalIgnoreCase));
                if (game != null)
                {
                    GamesView.ScrollIntoView(game);
                    GamesView.UpdateLayout();
                    if (GamesView.ContainerFromItem(game) is GridViewItem item)
                    {
                        item.Focus(FocusState.Programmatic);
                    }
                }
            }
        }

        /// <summary>
        /// Filters the displayed games by keyword without rebuilding the entire list.
        /// Searches in current language title, English title, and AppID.
        /// </summary>
        private void FilterGames(string? keyword)
        {
            string kw = keyword ?? string.Empty;
            bool hasKeyword = kw.Length > 0;
            int index = 0;
            foreach (var game in _allGames)
            {
                if (hasKeyword)
                {
                    // Search in: current language title, English title, and AppID
                    bool matchCurrentTitle = !string.IsNullOrEmpty(game.Title) &&
                                            game.Title.Contains(kw, StringComparison.OrdinalIgnoreCase);
                    bool matchEnglishTitle = !string.IsNullOrEmpty(game.EnglishTitle) &&
                                            game.EnglishTitle.Contains(kw, StringComparison.OrdinalIgnoreCase);
                    bool matchAppId = game.ID.ToString().Contains(kw);

                    // Skip if no match found
                    if (!matchCurrentTitle && !matchEnglishTitle && !matchAppId)
                    {
                        continue;
                    }
                }

                while (index < Games.Count && !ReferenceEquals(Games[index], game))
                {
                    Games.RemoveAt(index);
                }

                if (index >= Games.Count)
                {
                    Games.Add(game);
                }

                index++;
            }

            while (Games.Count > index)
            {
                Games.RemoveAt(Games.Count - 1);
            }

            StatusText.Text = $"Showing {Games.Count} of {_allGames.Count} games";

            // Only update progress if no operation is in progress (don't interfere with language switching)
            lock (_progressLock)
            {
                if (_currentProgressContext == ProgressContext.None)
                {
                    StatusProgress.Value = 0;
                    StatusExtra.Text = $"{Games.Count}/{_allGames.Count}";
                }
            }
#if DEBUG
            AppLogger.LogDebug($"FilterGames('{kw}') -> {Games.Count} items");
#endif
        }

        /// <summary>
        /// Updates the status text when game list operations report progress.
        /// </summary>
        private void OnGameListStatusChanged(string message)
        {
            _ = DispatcherQueue.TryEnqueue(() => StatusText.Text = message);
        }

        /// <summary>
        /// Updates the progress bar for game list downloads.
        /// </summary>
        private void OnGameListProgressChanged(double progress)
        {
            UpdateProgress(ProgressContext.InitialLoad, progress, $"{progress:0}%");
        }

        /// <summary>
        /// Updates progress bar with priority system to prevent conflicts.
        /// Only updates if the new context has equal or higher priority than current.
        /// </summary>
        /// <param name="context">The context requesting the update (determines priority)</param>
        /// <param name="progress">Progress value (0-100)</param>
        /// <param name="extraText">Extra text to display (e.g., "120/600")</param>
        private void UpdateProgress(ProgressContext context, double progress, string extraText)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                lock (_progressLock)
                {
                    // Allow update if:
                    // 1. No current operation (None)
                    // 2. New context has equal or higher priority (lower enum value = higher priority)
                    if (_currentProgressContext == ProgressContext.None || (int)context <= (int)_currentProgressContext)
                    {
                        StatusProgress.Value = progress;
                        StatusExtra.Text = extraText;
                        _currentProgressContext = context;
                    }
                }
            });
        }

        /// <summary>
        /// Clears progress bar only if the requesting context owns it.
        /// </summary>
        /// <param name="context">The context requesting the clear</param>
        private void ClearProgress(ProgressContext context)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                lock (_progressLock)
                {
                    // Only clear if this context owns the progress bar
                    if (context == _currentProgressContext)
                    {
                        StatusProgress.Value = 0;
                        StatusExtra.Text = string.Empty;
                        _currentProgressContext = ProgressContext.None;
                    }
                }
            });
        }

        /// <summary>
        /// Recursively searches the visual tree for a <see cref="ScrollViewer"/>.
        /// </summary>
        private static ScrollViewer? FindScrollViewer(DependencyObject root)
        {
            if (root is ScrollViewer sv)
                return sv;

            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            {
                var child = VisualTreeHelper.GetChild(root, i);
                if (FindScrollViewer(child) is ScrollViewer result)
                    return result;
            }

            return null;
        }

        /// <summary>
        /// Gets ScrollViewer and attaches ViewChanged handler to cancel pending loads on rapid scroll/jump
        /// </summary>
        private ScrollViewer? GetOrAttachScrollViewer()
        {
            if (_gamesScrollViewer == null)
            {
                _gamesScrollViewer = FindScrollViewer(GamesView);
                if (_gamesScrollViewer != null)
                {
                    // Attach ViewChanged to detect rapid scrolling (Home/End keys)
                    _gamesScrollViewer.ViewChanged += GamesScrollViewer_ViewChanged;
                }
            }
            return _gamesScrollViewer;
        }

        /// <summary>
        /// ViewChanged handler - no longer needed since ContainerContentChanging doesn't trigger loads.
        /// Kept as placeholder in case ScrollViewer is still attached via GetOrAttachScrollViewer().
        /// </summary>
        private void GamesScrollViewer_ViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
        {
            // Do nothing - sequential loading is independent of scrolling
        }

        /// <summary>
        /// Loads game images sequentially from top to bottom (like old SAM).
        /// THREE-PHASE LOADING STRATEGY:
        /// Phase 1 (Instant): Load all cached images (target language or English)
        /// Phase 2 (Fast): Download English fallback for games with no cache
        /// Phase 3 (Slow): Download target language for games showing English
        /// This ensures users see English fallbacks quickly, then target language updates.
        /// </summary>
        /// <param name="progressContext">The progress context to use (InitialLoad or LanguageSwitch)</param>
        private async void StartSequentialImageLoading(ProgressContext progressContext = ProgressContext.InitialLoad)
        {
            try
            {
                // Cancel any previous sequential loading
                var oldCts = _sequentialLoadCts;
                _sequentialLoadCts = new CancellationTokenSource();
                oldCts.Cancel();
                oldCts.Dispose();

                var ct = _sequentialLoadCts.Token;
                var currentLanguage = _currentLanguage;
                bool isEnglish = string.Equals(currentLanguage, "english", StringComparison.OrdinalIgnoreCase);

#if DEBUG
            AppLogger.LogDebug($"StartSequentialImageLoading: THREE-PHASE loading for {_allGames.Count} games in {currentLanguage}");
#endif

            // Set status text for Phase 1
            DispatcherQueue.TryEnqueue(() => StatusText.Text = "Loading cached images...");

            // PHASE 1: Instant load of cached images only (no downloads, no LoadCoverAsync)
            var gamesNeedingEnglish = new List<GameItem>();
            var gamesNeedingTarget = new List<GameItem>();
            const int phase1BatchSize = 50; // Larger batch since we're directly reading cache
            int totalGames = _allGames.Count;

            for (int i = 0; i < _allGames.Count; i += phase1BatchSize)
            {
                try
                {
                    if (ct.IsCancellationRequested || _currentLanguage != currentLanguage)
                        break;

                    // Update progress: Phase 1 占 33% (0-33)
                    double phase1Progress = (i * 33.0) / Math.Max(1, totalGames);
                    UpdateProgress(progressContext, phase1Progress, $"{i}/{totalGames}");

                    for (int j = 0; j < phase1BatchSize && (i + j) < _allGames.Count; j++)
                    {
                        var game = _allGames[i + j];
                        if (game.IconUri == "ms-appx:///Assets/no_icon.png")
                        {
                            // Try target language cache first
                            string? cachedPath = _imageService.TryGetCachedPath(game.ID, currentLanguage, checkEnglishFallback: false);

                            if (!string.IsNullOrEmpty(cachedPath) && File.Exists(cachedPath))
                            {
                                // Target language cached - show it immediately
                                var uri = new Uri(cachedPath).AbsoluteUri;
                                DispatcherQueue.TryEnqueue(() => game.IconUri = uri);
                            }
                            else if (!isEnglish)
                            {
                                // Try English fallback cache
                                cachedPath = _imageService.TryGetCachedPath(game.ID, "english", checkEnglishFallback: false);

                                if (!string.IsNullOrEmpty(cachedPath) && File.Exists(cachedPath))
                                {
                                    // English cached - show it immediately
                                    var uri = new Uri(cachedPath).AbsoluteUri;
                                    DispatcherQueue.TryEnqueue(() => game.IconUri = uri);

                                    // Will need target language in Phase 3
                                    gamesNeedingTarget.Add(game);
                                }
                                else
                                {
                                    // No cache at all - need English download in Phase 2
                                    gamesNeedingEnglish.Add(game);
                                }
                            }
                            else
                            {
                                // English mode, no cache - need download in Phase 2
                                gamesNeedingEnglish.Add(game);
                            }
                        }
                    }

                    // No async waiting in Phase 1 - just direct cache reads!
                    await Task.Delay(1, ct); // Minimal delay
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
#if DEBUG
                    AppLogger.LogDebug($"Phase 1 error at {i}: {ex.Message}");
#endif
                }
            }

            // Update progress to 33% (Phase 1 complete)
            UpdateProgress(progressContext, 33.0, $"{totalGames}/{totalGames}");

#if DEBUG
            AppLogger.LogDebug($"Phase 1 complete. Need English: {gamesNeedingEnglish.Count}, Need target: {gamesNeedingTarget.Count}");
#endif

            // Set status text for Phase 2
            DispatcherQueue.TryEnqueue(() => StatusText.Text = "Downloading English fallbacks...");

            // PHASE 2: Fast download of English fallback (larger batch, English usually exists)
            const int phase2BatchSize = 5;

            for (int i = 0; i < gamesNeedingEnglish.Count; i += phase2BatchSize)
            {
                try
                {
                    if (ct.IsCancellationRequested || _currentLanguage != currentLanguage)
                    {
#if DEBUG
                        AppLogger.LogDebug($"Phase 2 cancelled at {i}/{gamesNeedingEnglish.Count}");
#endif
                        break;
                    }

                    // Update progress: Phase 2 占 33% (33-66)
                    double phase2Progress = 33.0 + (i * 33.0) / Math.Max(1, gamesNeedingEnglish.Count);
                    UpdateProgress(progressContext, phase2Progress, $"{i}/{gamesNeedingEnglish.Count}");

                    var phase2Tasks = new List<Task>();
                    for (int j = 0; j < phase2BatchSize && (i + j) < gamesNeedingEnglish.Count; j++)
                    {
                        var game = gamesNeedingEnglish[i + j];
                        var tcs = new TaskCompletionSource<bool>();
                        bool enqueued = DispatcherQueue.TryEnqueue(async () =>
                        {
                            try
                            {
                                await game.LoadCoverAsync(_imageService);
                                tcs.SetResult(true);
                            }
                            catch (Exception ex)
                            {
                                tcs.SetException(ex);
                            }
                        });

                        if (!enqueued)
                        {
                            // DispatcherQueue is shutting down, cancel remaining work
                            tcs.SetCanceled();
                        }

                        phase2Tasks.Add(tcs.Task);

                        // If not English mode, will need target language in Phase 3
                        if (!isEnglish)
                        {
                            gamesNeedingTarget.Add(game);
                        }
                    }

                    if (phase2Tasks.Count > 0)
                    {
                        await Task.WhenAll(phase2Tasks);
                    }

                    // Shorter delay for English (usually exists and fast)
                    await Task.Delay(50, ct);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
#if DEBUG
                    AppLogger.LogDebug($"Phase 2 error at {i}: {ex.Message}");
#endif
                }
            }

            // Update progress to 66% (Phase 2 complete)
            UpdateProgress(progressContext, 66.0, $"{gamesNeedingEnglish.Count}/{gamesNeedingEnglish.Count}");

#if DEBUG
            AppLogger.LogDebug($"Phase 2 complete. Now downloading target language for {gamesNeedingTarget.Count} games.");
#endif

            // Set status text for Phase 3
            if (!isEnglish)
            {
                DispatcherQueue.TryEnqueue(() => StatusText.Text = $"Downloading {currentLanguage} images...");
            }

            // PHASE 3: Slow download of target language (smallest batch, may not exist)
            if (!isEnglish)
            {
                const int phase3BatchSize = 3;

                for (int i = 0; i < gamesNeedingTarget.Count; i += phase3BatchSize)
                {
                    try
                    {
                        if (ct.IsCancellationRequested || _currentLanguage != currentLanguage)
                        {
#if DEBUG
                            AppLogger.LogDebug($"Phase 3 cancelled at {i}/{gamesNeedingTarget.Count}");
#endif
                            break;
                        }

                        // Update progress: Phase 3 占 34% (66-100)
                        double phase3Progress = 66.0 + (i * 34.0) / Math.Max(1, gamesNeedingTarget.Count);
                        UpdateProgress(progressContext, phase3Progress, $"{i}/{gamesNeedingTarget.Count}");

                        var phase3Tasks = new List<Task>();
                        for (int j = 0; j < phase3BatchSize && (i + j) < gamesNeedingTarget.Count; j++)
                        {
                            var game = gamesNeedingTarget[i + j];
                            var tcs = new TaskCompletionSource<bool>();
                            bool enqueued = DispatcherQueue.TryEnqueue(async () =>
                            {
                                try
                                {
                                    // Force reload to upgrade from English to target language
                                    await game.LoadCoverAsync(_imageService, languageOverride: null, forceReload: true);
                                    tcs.SetResult(true);
                                }
                                catch (Exception ex)
                                {
                                    tcs.SetException(ex);
                                }
                            });

                            if (!enqueued)
                            {
                                // DispatcherQueue is shutting down, cancel remaining work
                                tcs.SetCanceled();
                            }

                            phase3Tasks.Add(tcs.Task);
                        }

                        if (phase3Tasks.Count > 0)
                        {
                            await Task.WhenAll(phase3Tasks);
                        }

                        // Longer delay for target language (may not exist, respects rate limits)
                        await Task.Delay(100, ct);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
#if DEBUG
                        AppLogger.LogDebug($"Phase 3 error at {i}: {ex.Message}");
#endif
                    }
                }
            }

                // Clear progress and reset status text
                ClearProgress(progressContext);
                DispatcherQueue.TryEnqueue(() => StatusText.Text = "Ready");

#if DEBUG
                AppLogger.LogDebug("Sequential image loading completed");
#endif
            }
            catch (Exception ex)
            {
                AppLogger.LogDebug($"StartSequentialImageLoading error: {ex}");
                ClearProgress(progressContext);
            }
        }

        /// <summary>
        /// Gets currently visible and hidden game items based on viewport position.
        /// </summary>
        private (List<GameItem> visible, List<GameItem> hidden) GetVisibleAndHiddenGameItems()
        {
            var visibleItems = new List<GameItem>();
            var hiddenItems = new List<GameItem>();

            if (GamesView?.ItemsPanelRoot is ItemsWrapGrid wrapGrid)
            {
                var scrollViewer = GetOrAttachScrollViewer();
                if (scrollViewer != null)
                {
                    var viewportHeight = scrollViewer.ViewportHeight;
                    var verticalOffset = scrollViewer.VerticalOffset;

                    // Estimate visible items based on viewport (ItemHeight=180 from XAML)
                    var itemHeight = 180;
                    var itemsPerRow = Math.Max(1, (int)(scrollViewer.ViewportWidth / 240)); // ItemWidth=240
                    var firstVisibleRow = Math.Max(0, (int)(verticalOffset / itemHeight));
                    var lastVisibleRow = (int)((verticalOffset + viewportHeight) / itemHeight) + 1;

                    var firstVisibleIndex = firstVisibleRow * itemsPerRow;
                    var lastVisibleIndex = Math.Min(Games.Count - 1, (lastVisibleRow + 1) * itemsPerRow);

                    for (int i = 0; i < Games.Count; i++)
                    {
                        if (i >= firstVisibleIndex && i <= lastVisibleIndex)
                            visibleItems.Add(Games[i]);
                        else
                            hiddenItems.Add(Games[i]);
                    }
                }
                else
                {
                    // Fallback: assume first 20 items are visible
                    for (int i = 0; i < Games.Count; i++)
                    {
                        if (i < 20)
                            visibleItems.Add(Games[i]);
                        else
                            hiddenItems.Add(Games[i]);
                    }
                }
            }
            else
            {
                // Fallback: assume first 20 items are visible
                for (int i = 0; i < Games.Count; i++)
                {
                    if (i < 20)
                        visibleItems.Add(Games[i]);
                    else
                        hiddenItems.Add(Games[i]);
                }
            }

            return (visibleItems, hiddenItems);
        }

        /// <summary>
        /// Refreshes game cover images after language switch.
        /// Uses CLEAN SLATE approach: unbind GridView, reset all states, rebind.
        /// Then starts sequential image loading from top to bottom (like old SAM).
        /// </summary>
        private async Task RefreshImagesForLanguageSwitch(string newLanguage)
        {
            // Use shared CleanSlateLanguageSwitcher for consistent CLEAN SLATE behavior
            await CommonUtilities.CleanSlateLanguageSwitcher.SwitchLanguageAsync(
                GamesView,
                Games,
                newLanguage,
                _dispatcher
            );

            // CRITICAL: Start sequential loading from top to bottom (like old SAM)
            // This avoids WinUI 3 native crash from thundering herd during fast scrolling
            StartSequentialImageLoading(ProgressContext.LanguageSwitch);
        }

        /// <summary>
        /// Loads localized game titles from the cached steam_games.xml file.
        /// </summary>
        private void LoadLocalizedTitlesFromXml()
        {
            try
            {
                var steamGamesXmlPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "AchievoLab", "cache", "steam_games.xml");

                if (!File.Exists(steamGamesXmlPath))
                {
                    AppLogger.LogDebug("steam_games.xml not found, using English titles only");
                    return;
                }

                var doc = XDocument.Load(steamGamesXmlPath);
                var gameElements = doc.Root?.Elements("Game");
                
                if (gameElements == null) return;

                var localizedData = new Dictionary<int, Dictionary<string, string>>();
                
                foreach (var gameElement in gameElements)
                {
                    if (int.TryParse(gameElement.Attribute("AppID")?.Value, out var appId))
                    {
                        var gameLocalizedTitles = new Dictionary<string, string>();
                        
                        // Find all Name_* elements
                        foreach (var nameElement in gameElement.Elements())
                        {
                            var elementName = nameElement.Name.LocalName;
                            if (elementName.StartsWith("Name_") && !string.IsNullOrEmpty(nameElement.Value))
                            {
                                var language = elementName.Substring(5); // Remove "Name_" prefix
                                gameLocalizedTitles[language] = nameElement.Value;
                            }
                        }
                        
                        if (gameLocalizedTitles.Count > 0)
                        {
                            localizedData[appId] = gameLocalizedTitles;
                        }
                    }
                }

                // Apply localized titles to existing games
                foreach (var game in _allGames)
                {
                    if (localizedData.TryGetValue(game.ID, out var titles))
                    {
                        game.LocalizedTitles = titles;
                        AppLogger.LogDebug($"Loaded {titles.Count} localized titles for game {game.ID} ({game.EnglishTitle})");
                    }
                }
                
                AppLogger.LogDebug($"Loaded localized titles from {steamGamesXmlPath} for {localizedData.Count} games");
            }
            catch (Exception ex)
            {
                AppLogger.LogDebug($"Error loading localized titles: {ex.Message}");
            }
        }

        /// <summary>
        /// Updates all games to display titles in the specified language.
        /// </summary>
        private void UpdateAllGameTitles(string language)
        {
            // NOTE: _isLanguageSwitching is managed by caller (LanguageComboBox_SelectionChanged)
            // Do NOT set it here to avoid premature reset before image loading completes

            foreach (var game in _allGames)
            {
                game.UpdateDisplayTitle(language);
            }

            // Re-sort _allGames based on new titles
            _allGames.Sort((a, b) => string.Compare(a.Title, b.Title, StringComparison.OrdinalIgnoreCase));

            // Re-sort Games ObservableCollection in-place using Move (avoids Clear/Add crash)
            // Get sorted order
            var sortedGames = Games.OrderBy(g => g.Title, StringComparer.OrdinalIgnoreCase).ToList();

            // Move items to correct positions
            for (int i = 0; i < sortedGames.Count; i++)
            {
                var currentItem = sortedGames[i];
                var currentIndex = Games.IndexOf(currentItem);

                if (currentIndex != i)
                {
                    Games.Move(currentIndex, i);
                }
            }

            _currentLanguage = language;
            AppLogger.LogDebug($"Updated and re-sorted all game titles to language: {language}");
        }

        /// <summary>
        /// Refreshes game cover images when switching languages.
        /// Visible items are processed first to reduce UI freezes.
        /// </summary>
        private async Task RefreshGameImages(string language)
        {
            AppLogger.LogDebug($"Refreshing game images for {language}");

            // Give UI time to stabilize after title updates before determining visible games
            // Title changes trigger PropertyChanged which causes WinUI to re-render items
            // We need to wait for the UI virtualization to settle before calculating visible range
            await Task.Delay(200);
            AppLogger.LogDebug($"UI stabilization delay completed, calculating visible games");

            var (visibleGames, hiddenGames) = GetVisibleAndHiddenGames();
            AppLogger.LogDebug($"Found {visibleGames.Count} visible games, {hiddenGames.Count} hidden games");

            // Reset visible game covers to prevent showing images from wrong language
            foreach (var game in visibleGames)
            {
                game.ResetCover();
            }

            // For hidden games, set to fallback icon to avoid black blocks
            // They will be properly loaded when scrolled into view (ContainerContentChanging)
            foreach (var game in hiddenGames)
            {
                game.ResetCover(); // ResetCover now uses dispatcher internally
            }

            // Categorize visible games for optimal loading
            bool isNonEnglish = !string.Equals(language, "english", StringComparison.OrdinalIgnoreCase);
            var cachedInTargetLanguage = new List<GameItem>();
            var cachedInEnglishOnly = new List<GameItem>();
            var notCached = new List<GameItem>();

            foreach (var game in visibleGames)
            {
                if (_imageService.IsImageCached(game.ID, language))
                {
                    cachedInTargetLanguage.Add(game);
                }
                else if (isNonEnglish && _imageService.IsImageCached(game.ID, "english"))
                {
                    cachedInEnglishOnly.Add(game);
                }
                else
                {
                    notCached.Add(game);
                }
            }

            // Load target language cached images immediately (these are fast, from disk cache)
            if (cachedInTargetLanguage.Count > 0)
            {
                var targetLangTasks = cachedInTargetLanguage.Select(g => g.LoadCoverAsync(_imageService));
                await Task.WhenAll(targetLangTasks);
            }

            // For English-only and non-cached items, start loading but don't wait
            // This prevents language switch from hanging while downloads are in progress
            var backgroundLoadTasks = new List<Task>();

            // For English-only cached items, LoadCoverAsync will:
            // 1. Load English first (immediate display - fast from cache)
            // 2. Attempt to download target language (background - may be slow)
            if (cachedInEnglishOnly.Count > 0)
            {
                foreach (var game in cachedInEnglishOnly)
                {
                    backgroundLoadTasks.Add(game.LoadCoverAsync(_imageService));
                }
                AppLogger.LogDebug($"Started loading {cachedInEnglishOnly.Count} items with English fallback (non-blocking)");
            }

            // Start loading non-cached images without blocking
            if (notCached.Count > 0)
            {
                foreach (var game in notCached)
                {
                    backgroundLoadTasks.Add(game.LoadCoverAsync(_imageService));
                }
                AppLogger.LogDebug($"Started loading {notCached.Count} non-cached items (non-blocking)");
            }

            // Let background loads continue without blocking UI
            // Images will appear as they download
            _ = Task.WhenAll(backgroundLoadTasks).ContinueWith(t =>
            {
                if (t.IsFaulted)
                {
                    AppLogger.LogDebug($"Some background image loads failed: {t.Exception?.GetBaseException().Message}");
                }
            });

            // Don't preload hidden images - let lazy loading (ContainerContentChanging) handle them
            // This prevents race conditions where _coverLoading is set by background task
            // but the actual load fails or is delayed, leaving images as "not found"
            AppLogger.LogDebug($"Language switch completed: {cachedInTargetLanguage.Count} cached in {language}, " +
                                $"{cachedInEnglishOnly.Count} English fallback, {notCached.Count} not cached (downloading in background), " +
                                $"{hiddenGames.Count} hidden images will load on scroll");
        }

        /// <summary>
        /// Determine which games are currently visible in the UI to prioritize image loading.
        /// </summary>
        private (List<GameItem> visible, List<GameItem> hidden) GetVisibleAndHiddenGames()
        {
            var visible = new List<GameItem>();
            var hidden = new List<GameItem>();

            var sv = GetOrAttachScrollViewer();
            if (GamesView.ItemsPanelRoot is ItemsWrapGrid wrapGrid && sv != null)
            {
                var itemHeight = wrapGrid.ItemHeight;
                var itemWidth = wrapGrid.ItemWidth;
                var itemsPerRow = Math.Max(1, (int)(sv.ViewportWidth / itemWidth));

                var firstVisibleRow = Math.Max(0, (int)(sv.VerticalOffset / itemHeight));
                var lastVisibleRow = (int)((sv.VerticalOffset + sv.ViewportHeight) / itemHeight) + 1;
                var firstIndex = firstVisibleRow * itemsPerRow;
                var lastIndex = Math.Min(Games.Count - 1, (lastVisibleRow + 1) * itemsPerRow);

                var visibleSet = new HashSet<GameItem>();
                for (int i = 0; i < Games.Count; i++)
                {
                    var game = Games[i];
                    if (i >= firstIndex && i <= lastIndex)
                    {
                        visible.Add(game);
                        visibleSet.Add(game);
                    }
                }

                foreach (var game in _allGames)
                {
                    if (!visibleSet.Contains(game))
                        hidden.Add(game);
                }
            }
            else
            {
                // Fallback when we cannot determine visibility: assume first 20 are visible
                for (int i = 0; i < _allGames.Count; i++)
                {
                    if (i < 20)
                        visible.Add(_allGames[i]);
                    else
                        hidden.Add(_allGames[i]);
                }
            }

            return (visible, hidden);
        }

    }

    public class GameItem : System.ComponentModel.INotifyPropertyChanged, CommonUtilities.IImageLoadableItem
    {
        private string _displayTitle;
        public string Title
        {
            get => _displayTitle;
            set
            {
                if (_displayTitle != value)
                {
                    _displayTitle = value;
                    try
                    {
                        PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(Title)));
                    }
                    catch (Exception ex)
                    {
                        AppLogger.LogDebug($"Error in PropertyChanged for Title (ID: {ID}): {ex.GetType().Name}: {ex.Message}");
                        AppLogger.LogDebug($"Stack trace: {ex.StackTrace}");
                    }
                }
            }
        }

        // Store original English title from Steam client
        public string EnglishTitle { get; set; }

        // Store localized titles from steam_games.xml if available
        public Dictionary<string, string> LocalizedTitles { get; set; } = new();

        public int ID { get; set; }
        public int AppId => ID;

        // Simplified image handling - store URI as string and let WinUI handle BitmapImage creation
        private string _iconUri = "ms-appx:///Assets/no_icon.png";
        private volatile bool _isUpdatingIcon = false;

        public string IconUri
        {
            get => _iconUri;
            set
            {
                if (_isUpdatingIcon) return; // Prevent concurrent updates

                try
                {
                    _isUpdatingIcon = true;

                    if (_iconUri != value || !string.IsNullOrEmpty(value)) // Force update if new value is not empty
                    {
                        _iconUri = value;
                        try
                        {
                            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(IconUri)));
                        }
                        catch (Exception ex)
                        {
                            AppLogger.LogDebug($"Error in PropertyChanged for IconUri (ID: {ID}): {ex.GetType().Name}: {ex.Message}");
                            AppLogger.LogDebug($"Stack trace: {ex.StackTrace}");
                        }
                    }
                }
                finally
                {
                    _isUpdatingIcon = false;
                }
            }
        }

        public string? ExePath { get; set; }
        public string? Arguments { get; set; }
        public string? UriScheme { get; set; }
        /// <summary>
        /// Indicates whether the bundled achievement manager is available.
        /// </summary>
        public bool IsManagerAvailable => GameLauncher.IsManagerAvailable;

        private readonly DispatcherQueue _dispatcher;
        private bool _coverLoading;
        private string? _loadedLanguage;
        public bool IsCoverLoading => _coverLoading;

        // IImageLoadableItem implementation
        public DispatcherQueue Dispatcher => _dispatcher;

        /// <summary>
        /// Resets the cover image state, allowing it to be reloaded.
        /// </summary>
        public void ResetCover()
        {
            // CRITICAL: Always use dispatcher when setting IconUri to prevent threading issues
            _dispatcher.TryEnqueue(() =>
            {
                IconUri = "ms-appx:///Assets/no_icon.png";
            });
            _coverLoading = false;
            _loadedLanguage = null;
        }

        /// <summary>
        /// Clears the loading state flags (for use when IconUri is already set directly on UI thread).
        /// </summary>
        public void ClearLoadingState()
        {
            _coverLoading = false;
            _loadedLanguage = null;
        }

        /// <summary>
        /// Checks if the current cover is from the specified language.
        /// </summary>
        public bool IsCoverFromLanguage(string language)
        {
            return _loadedLanguage != null &&
                   string.Equals(_loadedLanguage, language, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Gets the language of the currently loaded cover (for debugging).
        /// </summary>
        public string? GetLoadedLanguage() => _loadedLanguage;

        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

        /// <summary>
        /// Initializes a new game item with optional launch information.
        /// </summary>
        public GameItem(string title,
                         int id,
                         DispatcherQueue dispatcher,
                         string? exePath = null,
                         string? arguments = null,
                         string? uriScheme = null)
        {
            EnglishTitle = title; // Store original English title
            _displayTitle = title; // Initialize display title
            ID = id;
            _dispatcher = dispatcher;
            ExePath = exePath;
            Arguments = arguments;
            UriScheme = uriScheme;
        }

        /// <summary>
        /// Update display title based on current language
        /// </summary>
        /// <param name="language">Target language (e.g., "tchinese", "japanese")</param>
        public void UpdateDisplayTitle(string language)
        {
            if (language == "english")
            {
                Title = EnglishTitle;
            }
            else if (LocalizedTitles.TryGetValue(language, out var localizedTitle) && 
                     !string.IsNullOrEmpty(localizedTitle))
            {
                Title = localizedTitle;
            }
            else
            {
                Title = EnglishTitle; // Fallback to English
            }
        }

        /// <summary>
        /// Asynchronously loads the game's cover image using the shared image service.
        /// Simplified approach: Let WinUI handle BitmapImage creation from string URI.
        /// </summary>
        public async Task LoadCoverAsync(SharedImageService imageService, string? languageOverride = null, bool forceReload = false)
        {
            // Skip if already loading
            if (_coverLoading)
            {
#if DEBUG
                AppLogger.LogDebug($"Skipping LoadCoverAsync for {ID} ({Title}): already loading");
#endif
                return;
            }

            // SIMPLIFIED: Only skip if we already have a valid image (not no_icon)
            // Unless forceReload is true (for language upgrade in Phase 3)
            string currentLanguage = languageOverride ?? SteamLanguageResolver.GetSteamLanguage();
            if (!forceReload && !string.IsNullOrEmpty(IconUri) && IconUri != "ms-appx:///Assets/no_icon.png")
            {
#if DEBUG
                AppLogger.LogDebug($"Skipping LoadCoverAsync for {ID} ({Title}): already has valid image");
#endif
                return;
            }

            _coverLoading = true;

#if DEBUG
            AppLogger.LogDebug($"LoadCoverAsync started for {ID} ({Title}), language={currentLanguage}");
#endif

            try
            {
                // Use shared ImageLoadingHelper for English fallback logic
                var (imagePath, loadedLanguage) = await CommonUtilities.ImageLoadingHelper.LoadWithEnglishFallbackAsync(
                    imageService,
                    ID,
                    currentLanguage,
                    _dispatcher,
                    onEnglishFallbackLoaded: (englishPath) =>
                    {
                        // Update UI with English fallback immediately
                        _loadedLanguage = "english";
                        var englishUri = new Uri(englishPath).AbsoluteUri;
                        _dispatcher.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.High, () =>
                        {
                            var globalLanguage = SteamLanguageResolver.GetSteamLanguage();
                            if (currentLanguage == globalLanguage)
                            {
                                IconUri = englishUri;
#if DEBUG
                                AppLogger.LogDebug($"UI updated: {ID} ({Title}) showing English fallback immediately");
#endif
                            }
                        });
                    },
                    currentLanguageGetter: () => SteamLanguageResolver.GetSteamLanguage()
                );

                if (!string.IsNullOrEmpty(imagePath) && File.Exists(imagePath))
                {
                    _loadedLanguage = loadedLanguage;
                    var fileUri = new Uri(imagePath).AbsoluteUri;
                    var priority = imageService.IsImageCached(ID, currentLanguage)
                        ? Microsoft.UI.Dispatching.DispatcherQueuePriority.High
                        : Microsoft.UI.Dispatching.DispatcherQueuePriority.Normal;

                    _dispatcher.TryEnqueue(priority, () =>
                    {
                        var globalLanguage = SteamLanguageResolver.GetSteamLanguage();
                        if (currentLanguage == globalLanguage || _loadedLanguage == globalLanguage)
                        {
                            IconUri = fileUri;
#if DEBUG
                            AppLogger.LogDebug($"UI updated: {ID} ({Title}) IconUri set to {_loadedLanguage}");
#endif
                        }
                        else
                        {
#if DEBUG
                            AppLogger.LogDebug($"Skipping IconUri update for {ID} ({Title}) - language mismatch");
#endif
                        }
                    });
                }
                else
                {
                    // No image found
                    _dispatcher.TryEnqueue(() =>
                    {
                        IconUri = "ms-appx:///Assets/no_icon.png";
                    });
#if DEBUG
                    AppLogger.LogDebug($"Image not found for {ID}, using fallback icon");
#endif
                }
            }
            catch (OperationCanceledException)
            {
                // Download was cancelled (e.g., due to rapid scrolling/viewport change)
                // CRITICAL: Don't reset IconUri - keep existing image (English fallback or previous load)
#if DEBUG
                AppLogger.LogDebug($"LoadCoverAsync cancelled for {ID} ({Title}) - keeping existing IconUri");
#endif
            }
            catch (Exception ex)
            {
#if DEBUG
                AppLogger.LogDebug($"Error loading image for {ID}: {ex.Message}");
#endif
                // Fallback to no_icon.png only on real error (not cancellation)
                _dispatcher.TryEnqueue(() =>
                {
                    IconUri = "ms-appx:///Assets/no_icon.png";
                });
            }
            finally
            {
                _coverLoading = false;
            }
        }

        public static GameItem FromSteamApp(SteamAppData app, DispatcherQueue dispatcher)
        {
#if DEBUG
            AppLogger.LogDebug($"Creating GameItem for {app.AppId} - {app.Title}");
#endif
            return new GameItem(app.Title,
                                app.AppId,
                                dispatcher,
                                app.ExePath,
                                app.Arguments,
                                app.UriScheme);
        }
    }
}
