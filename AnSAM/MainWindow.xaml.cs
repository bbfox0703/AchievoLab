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
        private readonly DispatcherQueue _dispatcher;
        private string _currentLanguage = "english";

        private bool _autoLoaded;
        private bool _languageInitialized;
        private ScrollViewer? _gamesScrollViewer;
        private readonly DispatcherTimer _cdnStatsTimer;
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

            // Initialize CDN statistics timer
            _cdnStatsTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(2)
            };
            _cdnStatsTimer.Tick += CdnStatsTimer_Tick;
            _cdnStatsTimer.Start();
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
                    DebugLogger.LogDebug($"Language not changed, staying with: {lang}");
                    return; // No action needed if language hasn't changed
                }

                DebugLogger.LogDebug($"Language changed from {_currentLanguage} to {lang}");
                
                SteamLanguageResolver.OverrideLanguage = lang;
                await _imageService.SetLanguage(lang);

                _settingsService.TrySetString("Language", lang);

                try
                {
                    // Load localized titles from steam_games.xml if switching to non-English
                    if (lang != "english")
                    {
                        LoadLocalizedTitlesFromXml();
                    }

                    // Update game titles for the new language
                    UpdateAllGameTitles(lang);

                    // Refresh images using MyOwnGames strategy: explicit visible/hidden handling
                    await RefreshImagesForLanguageSwitch(lang);

                    StatusText.Text = lang == "english"
                        ? $"Switched to English - displaying original titles"
                        : $"Switched to {lang} - using localized titles where available";
                }
                catch (Exception ex)
                {
                    StatusProgress.IsIndeterminate = false;
                    StatusProgress.Value = 0;
                    StatusExtra.Text = string.Empty;
                    StatusText.Text = "Language switch failed";

                    DebugLogger.LogDebug($"Language switch error: {ex}");

                    var dialog = new ContentDialog
                    {
                        Title = "Language switch failed",
                        Content = "Unable to switch language. Please try again.",
                        CloseButtonText = "OK",
                        XamlRoot = Content.XamlRoot
                    };

                    await dialog.ShowAsync();
                    StatusText.Text = "Ready";
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

        // ]i^b MainWindow() غcŪ^WܡG
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
            DispatcherQueue.TryEnqueue(async () =>
            {
                // DISABLED: CleanupDuplicatedEnglishImages was mistakenly deleting legitimate language-specific
                // images that happen to have identical content to English versions (e.g., games without localized assets).
                // This cleanup mechanism was designed for an old bug where English images were copied to language folders,
                // but that copying mechanism has been removed, so cleanup is no longer needed.

                // _ = Task.Run(() =>
                // {
                //     try
                //     {
                //         var duplicates = _imageService.CleanupDuplicatedEnglishImages(dryRun: false);
                //         if (duplicates > 0)
                //         {
                //             DebugLogger.LogDebug($"Startup cleanup: removed {duplicates} duplicated images");
                //         }
                //     }
                //     catch (Exception ex)
                //     {
                //         DebugLogger.LogDebug($"Cleanup error: {ex.Message}");
                //     }
                // });

                await RefreshAsync();
            });
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

            var sv = _gamesScrollViewer ??= FindScrollViewer(GamesView);
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
                var stats = _imageService.GetCdnStats();
                if (stats.Count == 0)
                {
                    StatusCdn.Text = "";
                    return;
                }

                var cdnNames = new Dictionary<string, string>
                {
                    ["shared.cloudflare.steamstatic.com"] = "CF",
                    ["cdn.steamstatic.com"] = "Steam",
                    ["shared.akamai.steamstatic.com"] = "Akamai"
                };

                var statParts = new List<string>();
                foreach (var kvp in stats.OrderByDescending(x => x.Value.Active))
                {
                    var domain = kvp.Key;
                    var (active, isBlocked, successRate) = kvp.Value;

                    var name = cdnNames.ContainsKey(domain) ? cdnNames[domain] : domain.Split('.')[0];

                    if (active > 0 || isBlocked)
                    {
                        var blockedIndicator = isBlocked ? "⚠" : "";
                        statParts.Add($"{name}:{active}{blockedIndicator}");
                    }
                }

                // Calculate overall success rate
                var totalSuccess = stats.Values.Sum(s => s.SuccessRate * 100);
                var avgSuccessRate = stats.Count > 0 ? totalSuccess / stats.Count : 0;

                if (statParts.Count > 0)
                {
                    StatusCdn.Text = $"CDN: {string.Join(" ", statParts)} ({avgSuccessRate:0}%)";
                }
                else if (stats.Any(s => s.Value.SuccessRate > 0))
                {
                    StatusCdn.Text = $"CDN OK ({avgSuccessRate:0}%)";
                }
                else
                {
                    StatusCdn.Text = "";
                }
            }
            catch (Exception ex)
            {
                DebugLogger.LogDebug($"CDN stats update error: {ex.Message}");
            }
        }

        /// <summary>
        /// Unsubscribes from events and disposes resources when closing.
        /// </summary>
        private void OnWindowClosed(object sender, WindowEventArgs args)
        {
            _cdnStatsTimer.Stop();
            _cdnStatsTimer.Tick -= CdnStatsTimer_Tick;

            GameListService.StatusChanged -= OnGameListStatusChanged;
            GameListService.ProgressChanged -= OnGameListProgressChanged;
            _themeService.GetUISettings().ColorValuesChanged -= UiSettings_ColorValuesChanged;
            Activated -= OnWindowActivated;
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
        private async void GamesView_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs e)
        {
            if (e.InRecycleQueue)
                return;

            if (e.Item is not GameItem game)
                return;

            // Phase 0: Just register for phase 1, don't do any work yet
            if (e.Phase == 0)
            {
                e.RegisterUpdateCallback(GamesView_ContainerContentChanging);
                e.Handled = true;
            }
            // Phase 1: Load the image
            else if (e.Phase == 1)
            {
                // Check if we need to reset cover due to language mismatch
                string currentLanguage = SteamLanguageResolver.GetSteamLanguage();
                var isFallbackIcon = game.IconUri == "ms-appx:///Assets/no_icon.png";

                // Reset if cover exists, is not fallback, and doesn't match current language
                if (!string.IsNullOrEmpty(game.IconUri) &&
                    !isFallbackIcon &&
                    !game.IsCoverFromLanguage(currentLanguage))
                {
                    game.ResetCover();
                }

                // Load the cover image
                await game.LoadCoverAsync(_imageService);
            }
        }


        /// <summary>
        /// Opens the achievement manager when a game card is double-tapped.
        /// </summary>
        private void GameCard_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
        {
            if (sender is FrameworkElement element && element.DataContext is GameItem game)
            {
                if (game.IsManagerAvailable)
                {
                    StartAchievementManager(game);
                }
                else
                {
                    StatusText.Text = "Achievement manager not found";
                }
            }
        }


        /// <summary>
        /// Handles the context menu command to launch the achievement manager for a game.
        /// </summary>
        private void OnLaunchManagerClicked(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement element && element.DataContext is GameItem game)
            {
                if (game.IsManagerAvailable)
                {
                    StartAchievementManager(game);
                }
                else
                {
                    StatusText.Text = "Achievement manager not found";
                }
            }
        }

        /// <summary>
        /// Launches the selected game via the context menu.
        /// </summary>
        private void OnLaunchGameClicked(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement element && element.DataContext is GameItem game)
            {
                StartGame(game);
            }
        }

        /// <summary>
        /// Launches the bundled achievement manager for the specified game and updates status text.
        /// </summary>
        private void StartAchievementManager(GameItem game)
        {
            StatusText.Text = $"Launching achievement manager for {game.Title}...";
            StatusProgress.IsIndeterminate = true;
            StatusExtra.Text = string.Empty;
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
            StatusExtra.Text = string.Empty;
            GameLauncher.Launch(game);
            StatusProgress.IsIndeterminate = false;
            StatusText.Text = "Ready";
        }


        /// <summary>
        /// Retrieves game data and rebuilds the game list UI.
        /// </summary>
        private async Task RefreshAsync()
        {
            if (!_steamClient.Initialized)
            {
                StatusProgress.IsIndeterminate = false;
                StatusProgress.Value = 0;
                StatusExtra.Text = string.Empty;
                StatusText.Text = "Steam unavailable";

                var dialog = new ContentDialog
                {
                    Title = "Steam unavailable",
                    Content = "Unable to refresh because Steam is not available.",
                    CloseButtonText = "OK",
                    XamlRoot = Content.XamlRoot
                };

                await dialog.ShowAsync();
                return;
            }

            StatusText.Text = "Refresh";
            StatusProgress.IsIndeterminate = false;
            StatusProgress.Value = 0;
            StatusExtra.Text = "0%";

            var baseDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AchievoLab");

            var http = HttpClientProvider.Shared;
            var apps = await GameCacheService.RefreshAsync(baseDir, _steamClient, http);

            var (allGames, filteredGames) = await BuildGameListAsync(apps, null);

            _ = DispatcherQueue.TryEnqueue(() =>
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
                StatusProgress.Value = 0;
                StatusExtra.Text = $"{Games.Count}/{_allGames.Count}";
            });
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
            }
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
            StatusProgress.Value = 0;
            StatusExtra.Text = $"{Games.Count}/{_allGames.Count}";
#if DEBUG
            DebugLogger.LogDebug($"FilterGames('{kw}') -> {Games.Count} items");
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
            _ = DispatcherQueue.TryEnqueue(() =>
            {
                StatusProgress.Value = progress;
                StatusExtra.Text = $"{progress:0}%";
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
        /// Gets currently visible and hidden game items based on viewport position.
        /// </summary>
        private (List<GameItem> visible, List<GameItem> hidden) GetVisibleAndHiddenGameItems()
        {
            var visibleItems = new List<GameItem>();
            var hiddenItems = new List<GameItem>();

            if (GamesView?.ItemsPanelRoot is ItemsWrapGrid wrapGrid)
            {
                var scrollViewer = _gamesScrollViewer ??= FindScrollViewer(GamesView);
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
        /// Visible items are loaded immediately, hidden items are reset to no_icon.
        /// </summary>
        private async Task RefreshImagesForLanguageSwitch(string newLanguage)
        {
            try
            {
                // Get visible and hidden items
                var (visibleItems, hiddenItems) = GetVisibleAndHiddenGameItems();

                DebugLogger.LogDebug($"Language switch: {visibleItems.Count} visible, {hiddenItems.Count} hidden games");

                // Reset hidden items to no_icon (they'll load when scrolled into view)
                foreach (var game in hiddenItems)
                {
                    game.ResetCover();
                }

                // Load visible items immediately for responsive UI
                var loadTasks = visibleItems.Select(async game =>
                {
                    // Reset first to clear any wrong-language covers
                    game.ResetCover();
                    // Then load for the new language
                    await game.LoadCoverAsync(_imageService, newLanguage);
                });

                await Task.WhenAll(loadTasks);

                DebugLogger.LogDebug($"Loaded {visibleItems.Count} visible covers for {newLanguage}");
            }
            catch (Exception ex)
            {
                DebugLogger.LogDebug($"Error refreshing images for language switch: {ex.Message}");
            }
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
                    DebugLogger.LogDebug("steam_games.xml not found, using English titles only");
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
                        DebugLogger.LogDebug($"Loaded {titles.Count} localized titles for game {game.ID} ({game.EnglishTitle})");
                    }
                }
                
                DebugLogger.LogDebug($"Loaded localized titles from {steamGamesXmlPath} for {localizedData.Count} games");
            }
            catch (Exception ex)
            {
                DebugLogger.LogDebug($"Error loading localized titles: {ex.Message}");
            }
        }

        /// <summary>
        /// Updates all games to display titles in the specified language.
        /// </summary>
        private void UpdateAllGameTitles(string language)
        {
            foreach (var game in _allGames)
            {
                game.UpdateDisplayTitle(language);
            }
            
            // Also update filtered games in the UI
            foreach (var game in Games)
            {
                game.UpdateDisplayTitle(language);
            }
            
            _currentLanguage = language;
            DebugLogger.LogDebug($"Updated all game titles to language: {language}");
        }

        /// <summary>
        /// Refreshes game cover images when switching languages.
        /// Visible items are processed first to reduce UI freezes.
        /// </summary>
        private async Task RefreshGameImages(string language)
        {
            DebugLogger.LogDebug($"Refreshing game images for {language}");

            // Give UI time to stabilize after title updates before determining visible games
            // Title changes trigger PropertyChanged which causes WinUI to re-render items
            // We need to wait for the UI virtualization to settle before calculating visible range
            await Task.Delay(200);
            DebugLogger.LogDebug($"UI stabilization delay completed, calculating visible games");

            var (visibleGames, hiddenGames) = GetVisibleAndHiddenGames();
            DebugLogger.LogDebug($"Found {visibleGames.Count} visible games, {hiddenGames.Count} hidden games");

            // Reset visible game covers to prevent showing images from wrong language
            foreach (var game in visibleGames)
            {
                game.ResetCover();
            }

            // For hidden games, set to fallback icon to avoid black blocks
            // They will be properly loaded when scrolled into view (ContainerContentChanging)
            foreach (var game in hiddenGames)
            {
                game.ResetCover();
                game.IconUri = "ms-appx:///Assets/no_icon.png";
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
                DebugLogger.LogDebug($"Started loading {cachedInEnglishOnly.Count} items with English fallback (non-blocking)");
            }

            // Start loading non-cached images without blocking
            if (notCached.Count > 0)
            {
                foreach (var game in notCached)
                {
                    backgroundLoadTasks.Add(game.LoadCoverAsync(_imageService));
                }
                DebugLogger.LogDebug($"Started loading {notCached.Count} non-cached items (non-blocking)");
            }

            // Let background loads continue without blocking UI
            // Images will appear as they download
            _ = Task.WhenAll(backgroundLoadTasks).ContinueWith(t =>
            {
                if (t.IsFaulted)
                {
                    DebugLogger.LogDebug($"Some background image loads failed: {t.Exception?.GetBaseException().Message}");
                }
            });

            // Don't preload hidden images - let lazy loading (ContainerContentChanging) handle them
            // This prevents race conditions where _coverLoading is set by background task
            // but the actual load fails or is delayed, leaving images as "not found"
            DebugLogger.LogDebug($"Language switch completed: {cachedInTargetLanguage.Count} cached in {language}, " +
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

            var sv = _gamesScrollViewer ??= FindScrollViewer(GamesView);
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

    public class GameItem : System.ComponentModel.INotifyPropertyChanged
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
                    PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(Title)));
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
                        PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(IconUri)));
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

        /// <summary>
        /// Resets the cover image state, allowing it to be reloaded.
        /// </summary>
        public void ResetCover()
        {
            IconUri = "ms-appx:///Assets/no_icon.png";
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

        /// <summary>
        /// Determines the actual language of an image from its file path.
        /// </summary>
        private static string DetermineLanguageFromPath(string path, string requestedLanguage)
        {
            // Check path for language folder indicators
            // Path format is typically: .../ImageCache/{language}/{appid}.jpg
            if (path.Contains("/english/") || path.Contains("\\english\\"))
            {
                return "english";
            }
            else if (path.Contains("/tchinese/") || path.Contains("\\tchinese\\"))
            {
                return "tchinese";
            }
            else if (path.Contains("/schinese/") || path.Contains("\\schinese\\"))
            {
                return "schinese";
            }
            else if (path.Contains("/japanese/") || path.Contains("\\japanese\\"))
            {
                return "japanese";
            }
            else if (path.Contains("/koreana/") || path.Contains("\\koreana\\"))
            {
                return "koreana";
            }

            // If no language folder detected, assume requested language
            return requestedLanguage;
        }

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
        public async Task LoadCoverAsync(SharedImageService imageService, string? languageOverride = null)
        {
            // Skip if already loading
            if (_coverLoading)
            {
#if DEBUG
                DebugLogger.LogDebug($"Skipping LoadCoverAsync for {ID} ({Title}): already loading");
#endif
                return;
            }

            // Skip if already loaded for current language
            string currentLanguage = languageOverride ?? SteamLanguageResolver.GetSteamLanguage();
            if (!string.IsNullOrEmpty(IconUri) &&
                IconUri != "ms-appx:///Assets/no_icon.png" &&
                IsCoverFromLanguage(currentLanguage))
            {
#if DEBUG
                DebugLogger.LogDebug($"Skipping LoadCoverAsync for {ID} ({Title}): already loaded for {currentLanguage}");
#endif
                return;
            }

            _coverLoading = true;

#if DEBUG
            DebugLogger.LogDebug($"LoadCoverAsync started for {ID} ({Title}), language={currentLanguage}");
#endif

            try
            {
                // Get image path from shared service
                var imagePath = await imageService.GetGameImageAsync(ID, currentLanguage).ConfigureAwait(false);

                if (!string.IsNullOrEmpty(imagePath) && File.Exists(imagePath))
                {
                    // Determine actual language from path
                    _loadedLanguage = DetermineLanguageFromPath(imagePath, currentLanguage);

                    // Convert to URI string and update UI
                    var fileUri = new Uri(imagePath).AbsoluteUri;

                    // Use dispatcher for thread-safe UI update
                    var priority = imageService.IsImageCached(ID, currentLanguage)
                        ? Microsoft.UI.Dispatching.DispatcherQueuePriority.High
                        : Microsoft.UI.Dispatching.DispatcherQueuePriority.Normal;

                    _dispatcher.TryEnqueue(priority, () =>
                    {
                        IconUri = fileUri;
#if DEBUG
                        DebugLogger.LogDebug($"UI updated: {ID} ({Title}) IconUri set to {_loadedLanguage}");
#endif
                    });
                }
                else
                {
                    // Fallback to no_icon.png
                    _dispatcher.TryEnqueue(() =>
                    {
                        IconUri = "ms-appx:///Assets/no_icon.png";
                    });
#if DEBUG
                    DebugLogger.LogDebug($"Image not found for {ID}, using fallback icon");
#endif
                }
            }
            catch (Exception ex)
            {
#if DEBUG
                DebugLogger.LogDebug($"Error loading image for {ID}: {ex.Message}");
#endif
                // Fallback to no_icon.png on error
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
            DebugLogger.LogDebug($"Creating GameItem for {app.AppId} - {app.Title}");
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
