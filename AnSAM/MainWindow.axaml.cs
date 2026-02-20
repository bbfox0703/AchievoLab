using AnSAM.Services;
using AnSAM.Steam;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Styling;
using Avalonia.Threading;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using CommonUtilities;

namespace AnSAM
{
    public partial class MainWindow : Window
    {
        public ObservableCollection<GameItem> Games { get; } = new();
        private readonly List<GameItem> _allGames = new();
        private readonly SteamClient _steamClient;
        private readonly SharedImageService _imageService;
        private volatile bool _isLanguageSwitching = false;
        private string _currentLanguage = "english";
        private CancellationTokenSource? _languageSwitchCts;
        private readonly SemaphoreSlim _languageSwitchLock = new(1, 1);
        private CancellationTokenSource _sequentialLoadCts = new();

        private enum ProgressContext
        {
            None = 0,
            InitialLoad = 1,
            LanguageSwitch = 2,
            CacheCheck = 3
        }
        private ProgressContext _currentProgressContext = ProgressContext.None;
        private readonly object _progressLock = new object();

        private bool _autoLoaded;
        private bool _languageInitialized;
        private readonly DispatcherTimer _cdnStatsTimer;
        private DispatcherTimer? _searchDebounceTimer;
        private readonly ThemeManagementService _themeService = new();
        private readonly ApplicationSettingsService _settingsService = new();

        public MainWindow()
        {
            InitializeComponent();
            _steamClient = new SteamClient();
            _imageService = new SharedImageService(HttpClientProvider.Shared);
            DataContext = this;
        }

        public MainWindow(SteamClient steamClient, ThemeVariant theme)
        {
            _steamClient = steamClient;
            _imageService = new SharedImageService(HttpClientProvider.Shared);
            InitializeComponent();
            DataContext = this;

            InitializeLanguageComboBox();

            // Set window icon
            var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "AnSAM.ico");
            if (File.Exists(iconPath))
            {
                Icon = new WindowIcon(iconPath);
            }

            ApplyTheme(theme, save: false);
            KeyDown += OnWindowKeyDown;

            if (!_steamClient.Initialized)
            {
                StatusText.Text = "Steam unavailable";
            }
            GameListService.StatusChanged += OnGameListStatusChanged;
            GameListService.ProgressChanged += OnGameListProgressChanged;
            Opened += OnWindowOpened;
            Closed += OnWindowClosed;

            GameLauncher.Initialize();

            _cdnStatsTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(2)
            };
            _cdnStatsTimer.Tick += CdnStatsTimer_Tick;
            _cdnStatsTimer.Start();

            _searchDebounceTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(300)
            };
            _searchDebounceTimer.Tick += OnSearchDebounceTimerTick;

            // Subscribe to AutoCompleteBox text changes in code (event signature differs from XAML binding)
            SearchBox.TextChanged += SearchBox_TextChanged;
        }

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
            string initial = "english";

            LanguageComboBox.SelectedItem = initial;
            SteamLanguageResolver.OverrideLanguage = initial;
            _ = _imageService.SetLanguage(initial);
            _currentLanguage = initial;
            _languageInitialized = true;
        }

        private void ApplyTheme(ThemeVariant theme, bool save = true)
        {
            _themeService.ApplyTheme(theme);

            StatusText.Text = theme == ThemeVariant.Default ? "Theme: System default"
                : theme == ThemeVariant.Light ? "Theme: Light"
                : theme == ThemeVariant.Dark ? "Theme: Dark"
                : "Theme: ?";

            if (save)
            {
                var themeStr = theme == ThemeVariant.Dark ? "Dark"
                    : theme == ThemeVariant.Light ? "Light"
                    : "Default";
                _settingsService.TrySetString("AppTheme", themeStr);
            }
        }

        private void Theme_Default_Click(object? sender, RoutedEventArgs e) => ApplyTheme(ThemeVariant.Default);
        private void Theme_Light_Click(object? sender, RoutedEventArgs e) => ApplyTheme(ThemeVariant.Light);
        private void Theme_Dark_Click(object? sender, RoutedEventArgs e) => ApplyTheme(ThemeVariant.Dark);

        private async void LaunchByAppId_Click(object? sender, RoutedEventArgs e)
        {
            try
            {
                var inputBox = new TextBox
                {
                    Watermark = "Enter Steam App ID (e.g., 730 for CS:GO)"
                };

                var dialog = new Window
                {
                    Title = "Launch by App ID",
                    Width = 400,
                    Height = 160,
                    WindowStartupLocation = WindowStartupLocation.CenterOwner,
                    Content = new StackPanel
                    {
                        Margin = new Thickness(16),
                        Spacing = 12,
                        Children =
                        {
                            inputBox,
                            new StackPanel
                            {
                                Orientation = Avalonia.Layout.Orientation.Horizontal,
                                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                                Spacing = 8,
                                Children =
                                {
                                    new Button { Content = "Launch", Name = "OkButton" },
                                    new Button { Content = "Cancel", Name = "CancelButton" }
                                }
                            }
                        }
                    }
                };

                var tcs = new TaskCompletionSource<string?>();
                dialog.Opened += (_, _) =>
                {
                    var panel = (StackPanel)dialog.Content!;
                    var buttonPanel = (StackPanel)panel.Children[1];
                    ((Button)buttonPanel.Children[0]).Click += (_, _) =>
                    {
                        tcs.TrySetResult(inputBox.Text);
                        dialog.Close();
                    };
                    ((Button)buttonPanel.Children[1]).Click += (_, _) =>
                    {
                        tcs.TrySetResult(null);
                        dialog.Close();
                    };
                };
                dialog.Closed += (_, _) => tcs.TrySetResult(null);

                await dialog.ShowDialog(this);
                var input = await tcs.Task;

                if (string.IsNullOrEmpty(input?.Trim()))
                {
                    StatusText.Text = "No App ID entered";
                    return;
                }

                input = input.Trim();
                if (!uint.TryParse(input, out var appId) || appId == 0)
                {
                    StatusText.Text = "Invalid App ID format";
                    return;
                }

                var baseDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AchievoLab");
                bool isOwned = GameCacheService.TryAddUserGame(baseDir, _steamClient, (int)appId);

                if (!isOwned && _steamClient.Initialized)
                {
                    StatusText.Text = $"Warning: You don't own App ID {appId}. Launching anyway...";
                }
                else if (isOwned)
                {
                    StatusText.Text = $"App ID {appId} verified and saved to usergames.xml";
                }

                var tempGame = new GameItem($"App {appId}", (int)appId);
                tempGame.IconUri = ImageLoadingHelper.GetNoIconPath();
                StartAchievementManager(tempGame);

                StatusText.Text = isOwned
                    ? $"Launched App ID {appId} (saved to cache)"
                    : $"Launched App ID {appId} (not verified)";
            }
            catch (Exception ex)
            {
                AppLogger.LogDebug($"LaunchByAppId_Click error: {ex}");
                StatusText.Text = "Failed to launch by App ID";
            }
        }

        private async void LanguageComboBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
        {
            if (!_languageInitialized)
                return;

            if (LanguageComboBox.SelectedItem is string lang)
            {
                if (lang == _currentLanguage)
                {
                    AppLogger.LogDebug($"Language not changed, staying with: {lang}");
                    return;
                }

                AppLogger.LogDebug($"Language changed from {_currentLanguage} to {lang}");

                bool lockAcquired = false;
                try
                {
                    await _languageSwitchLock.WaitAsync();
                    lockAcquired = true;
                    _languageSwitchCts?.Dispose();
                    var cts = new CancellationTokenSource();
                    _languageSwitchCts = cts;

                    try
                    {
                        _isLanguageSwitching = true;

                        SteamLanguageResolver.OverrideLanguage = lang;
                        await _imageService.SetLanguage(lang);
                        _settingsService.TrySetString("Language", lang);

                        // Scroll to top
                        GamesScrollViewer.Offset = new Vector(0, 0);
                        await Task.Delay(100);

                        if (lang != "english")
                        {
                            LoadLocalizedTitlesFromXml();
                        }

                        UpdateAllGameTitles(lang);
                        await Task.Delay(200);

                        await RefreshImagesForLanguageSwitch(lang);

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

                        await ShowMessageDialog("Language switch failed", "Unable to switch language. Please try again.");
                        StatusText.Text = "Ready";
                    }
                    finally
                    {
                        _isLanguageSwitching = false;
                    }
                }
                catch (ObjectDisposedException)
                {
                    AppLogger.LogDebug("Language switch lock disposed during operation");
                }
                catch (Exception ex)
                {
                    AppLogger.LogDebug($"LanguageComboBox_SelectionChanged unexpected error: {ex}");
                }
                finally
                {
                    if (lockAcquired)
                    {
                        try
                        {
                            _languageSwitchLock.Release();
                            AppLogger.LogDebug($"Language switch lock released for {lang}");
                        }
                        catch (ObjectDisposedException) { }
                    }
                }
            }
        }

        private async Task ShowMessageDialog(string title, string message)
        {
            var dialog = new Window
            {
                Title = title,
                Width = 350,
                Height = 140,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Content = new StackPanel
                {
                    Margin = new Thickness(16),
                    Spacing = 12,
                    Children =
                    {
                        new TextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                        new Button
                        {
                            Content = "OK",
                            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right
                        }
                    }
                }
            };

            dialog.Opened += (_, _) =>
            {
                var panel = (StackPanel)dialog.Content!;
                ((Button)panel.Children[1]).Click += (_, _) => dialog.Close();
            };

            await dialog.ShowDialog(this);
        }

        private void OnWindowOpened(object? sender, EventArgs args)
        {
            if (_autoLoaded) return;
            _autoLoaded = true;
            Dispatcher.UIThread.Post(async () =>
            {
                await RefreshAsync();
            });
        }

        private void OnWindowKeyDown(object? sender, KeyEventArgs e)
        {
            if (e.Key != Key.PageDown &&
                e.Key != Key.PageUp &&
                e.Key != Key.Home &&
                e.Key != Key.End)
                return;

            var sv = GamesScrollViewer;
            if (sv == null)
                return;

            double? offset = null;
            var delta = sv.Viewport.Height;

            switch (e.Key)
            {
                case Key.PageDown when delta > 0:
                    offset = sv.Offset.Y + delta;
                    break;
                case Key.PageUp when delta > 0:
                    offset = sv.Offset.Y - delta;
                    break;
                case Key.Home:
                    offset = 0;
                    break;
                case Key.End:
                    offset = sv.Extent.Height - sv.Viewport.Height;
                    break;
            }

            if (offset.HasValue)
            {
                sv.Offset = new Vector(sv.Offset.X, Math.Max(0, offset.Value));
                e.Handled = true;
            }
        }

        private void CdnStatsTimer_Tick(object? sender, EventArgs e)
        {
            try
            {
                if (_imageService == null || StatusCdn == null)
                    return;

                var stats = _imageService.GetCdnStats();
                var pendingCount = _imageService.GetPendingRequestsCount();
                var hasActiveDownloads = pendingCount > 0 || stats.Values.Any(s => s.Active > 0);

                if (!hasActiveDownloads)
                {
                    StatusCdn.Text = string.Empty;
                    return;
                }

                StatusCdn.Text = CdnStatsFormatter.FormatCdnStats(stats);
            }
            catch (ObjectDisposedException) { }
            catch (Exception ex)
            {
                AppLogger.LogDebug($"CDN stats update error: {ex.Message}");
            }
        }

        private void OnWindowClosed(object? sender, EventArgs args)
        {
            _cdnStatsTimer?.Stop();
            _cdnStatsTimer.Tick -= CdnStatsTimer_Tick;

            GameListService.StatusChanged -= OnGameListStatusChanged;
            GameListService.ProgressChanged -= OnGameListProgressChanged;
            Opened -= OnWindowOpened;
            KeyDown -= OnWindowKeyDown;

            _imageService.Dispose();
            _languageSwitchCts?.Dispose();
            _sequentialLoadCts.Dispose();
        }

        private void GameCard_DoubleTapped(object? sender, TappedEventArgs e)
        {
            AppLogger.LogDebug($"GameCard_DoubleTapped: Event triggered");

            if (sender is Control element && element.DataContext is GameItem game)
            {
                AppLogger.LogDebug($"GameCard_DoubleTapped: Game item found - {game.ID} ({game.Title})");
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

        private void OnLaunchManagerClicked(object? sender, RoutedEventArgs e)
        {
            if (sender is MenuItem menuItem && menuItem.Tag is GameItem game)
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

        private void OnLaunchGameClicked(object? sender, RoutedEventArgs e)
        {
            if (sender is MenuItem menuItem && menuItem.Tag is GameItem game)
            {
                StartGame(game);
            }
        }

        private void StartAchievementManager(GameItem game)
        {
            StatusText.Text = $"Launching achievement manager for {game.Title}...";
            StatusProgress.IsIndeterminate = true;

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

        private void StartGame(GameItem game)
        {
            StatusText.Text = $"Launching {game.Title}...";
            StatusProgress.IsIndeterminate = true;

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

        private async Task RefreshAsync()
        {
            try
            {
                if (!_steamClient.Initialized)
                {
                    StatusProgress.IsIndeterminate = false;
                    ClearProgress(ProgressContext.InitialLoad);
                    StatusText.Text = "Steam unavailable";
                    await ShowMessageDialog("Steam unavailable", "Unable to refresh because Steam is not available.");
                    return;
                }

                StatusText.Text = "Refresh";
                StatusProgress.IsIndeterminate = false;
                UpdateProgress(ProgressContext.InitialLoad, 0, "0%");

                var baseDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AchievoLab");
                var http = HttpClientProvider.Shared;
                var apps = await GameCacheService.RefreshAsync(baseDir, _steamClient, http);
                var (allGames, filteredGames) = await BuildGameListAsync(apps, null);

                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    try
                    {
                        _allGames.Clear();
                        _allGames.AddRange(allGames);

                        if (_currentLanguage != "english")
                        {
                            LoadLocalizedTitlesFromXml();
                        }

                        UpdateAllGameTitles(_currentLanguage);

                        Games.Clear();
                        foreach (var game in filteredGames)
                        {
                            Games.Add(game);
                        }

                        StatusText.Text = _steamClient.Initialized
                            ? $"Loaded {_allGames.Count} games (Language: {_currentLanguage})"
                            : $"Steam unavailable - showing {_allGames.Count} games (Language: {_currentLanguage})";

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

                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    try
                    {
                        StatusText.Text = $"Refresh failed: {ex.Message}";
                        ClearProgress(ProgressContext.InitialLoad);
                        StatusProgress.IsIndeterminate = false;
                    }
                    catch { }
                });
            }
        }

        private Task<(List<GameItem> allGames, List<GameItem> filteredGames)> BuildGameListAsync(IEnumerable<SteamAppData> apps, string? keyword)
        {
            return Task.Run(() =>
            {
                var allGames = new List<GameItem>();
                foreach (var app in apps)
                {
                    allGames.Add(GameItem.FromSteamApp(app));
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

        private void SearchBox_TextChanged(object? sender, TextChangedEventArgs e)
        {
            if (_searchDebounceTimer != null)
            {
                _searchDebounceTimer.Stop();
                _searchDebounceTimer.Start();
            }
        }

        private void OnSearchDebounceTimerTick(object? sender, EventArgs args)
        {
            _searchDebounceTimer?.Stop();
            var keyword = SearchBox.Text?.Trim();
            FilterGames(keyword);
        }

        private void FilterGames(string? keyword)
        {
            string kw = keyword ?? string.Empty;
            bool hasKeyword = kw.Length > 0;
            int index = 0;
            foreach (var game in _allGames)
            {
                if (hasKeyword)
                {
                    bool matchCurrentTitle = !string.IsNullOrEmpty(game.Title) &&
                                            game.Title.Contains(kw, StringComparison.OrdinalIgnoreCase);
                    bool matchEnglishTitle = !string.IsNullOrEmpty(game.EnglishTitle) &&
                                            game.EnglishTitle.Contains(kw, StringComparison.OrdinalIgnoreCase);
                    bool matchAppId = game.ID.ToString().Contains(kw);

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

        private void OnGameListStatusChanged(string message)
        {
            Dispatcher.UIThread.Post(() => StatusText.Text = message);
        }

        private void OnGameListProgressChanged(double progress)
        {
            UpdateProgress(ProgressContext.InitialLoad, progress, $"{progress:0}%");
        }

        private void UpdateProgress(ProgressContext context, double progress, string extraText)
        {
            Dispatcher.UIThread.Post(() =>
            {
                lock (_progressLock)
                {
                    if (_currentProgressContext == ProgressContext.None || (int)context <= (int)_currentProgressContext)
                    {
                        StatusProgress.Value = progress;
                        StatusExtra.Text = extraText;
                        _currentProgressContext = context;
                    }
                }
            });
        }

        private void ClearProgress(ProgressContext context)
        {
            Dispatcher.UIThread.Post(() =>
            {
                lock (_progressLock)
                {
                    if (context == _currentProgressContext)
                    {
                        StatusProgress.Value = 0;
                        StatusExtra.Text = string.Empty;
                        _currentProgressContext = ProgressContext.None;
                    }
                }
            });
        }

        private async void StartSequentialImageLoading(ProgressContext progressContext = ProgressContext.InitialLoad)
        {
            try
            {
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

                Dispatcher.UIThread.Post(() => StatusText.Text = "Loading cached images...");

                // PHASE 1: Instant load of cached images
                var gamesNeedingEnglish = new List<GameItem>();
                var gamesNeedingTarget = new List<GameItem>();
                const int phase1BatchSize = 50;
                int totalGames = _allGames.Count;
                var noIconPath = ImageLoadingHelper.GetNoIconPath();

                for (int i = 0; i < _allGames.Count; i += phase1BatchSize)
                {
                    try
                    {
                        if (ct.IsCancellationRequested || _currentLanguage != currentLanguage)
                            break;

                        double phase1Progress = (i * 33.0) / Math.Max(1, totalGames);
                        UpdateProgress(progressContext, phase1Progress, $"{i}/{totalGames}");

                        for (int j = 0; j < phase1BatchSize && (i + j) < _allGames.Count; j++)
                        {
                            var game = _allGames[i + j];
                            if (game.IconUri == noIconPath || ImageLoadingHelper.IsNoIcon(game.IconUri))
                            {
                                string? cachedPath = _imageService.TryGetCachedPath(game.ID, currentLanguage, checkEnglishFallback: false);

                                if (!string.IsNullOrEmpty(cachedPath) && File.Exists(cachedPath))
                                {
                                    var path = cachedPath;
                                    Dispatcher.UIThread.Post(() => game.IconUri = path);
                                }
                                else if (!isEnglish)
                                {
                                    cachedPath = _imageService.TryGetCachedPath(game.ID, "english", checkEnglishFallback: false);

                                    if (!string.IsNullOrEmpty(cachedPath) && File.Exists(cachedPath))
                                    {
                                        var path = cachedPath;
                                        Dispatcher.UIThread.Post(() => game.IconUri = path);
                                        gamesNeedingTarget.Add(game);
                                    }
                                    else
                                    {
                                        gamesNeedingEnglish.Add(game);
                                    }
                                }
                                else
                                {
                                    gamesNeedingEnglish.Add(game);
                                }
                            }
                        }

                        await Task.Delay(1, ct);
                    }
                    catch (OperationCanceledException) { break; }
                    catch (Exception ex)
                    {
#if DEBUG
                        AppLogger.LogDebug($"Phase 1 error at {i}: {ex.Message}");
#endif
                    }
                }

                UpdateProgress(progressContext, 33.0, $"{totalGames}/{totalGames}");

#if DEBUG
                AppLogger.LogDebug($"Phase 1 complete. Need English: {gamesNeedingEnglish.Count}, Need target: {gamesNeedingTarget.Count}");
#endif

                Dispatcher.UIThread.Post(() => StatusText.Text = "Downloading English fallbacks...");

                // PHASE 2: Download English fallback
                const int phase2BatchSize = 5;

                for (int i = 0; i < gamesNeedingEnglish.Count; i += phase2BatchSize)
                {
                    try
                    {
                        if (ct.IsCancellationRequested || _currentLanguage != currentLanguage)
                            break;

                        double phase2Progress = 33.0 + (i * 33.0) / Math.Max(1, gamesNeedingEnglish.Count);
                        UpdateProgress(progressContext, phase2Progress, $"{i}/{gamesNeedingEnglish.Count}");

                        var phase2Tasks = new List<Task>();
                        for (int j = 0; j < phase2BatchSize && (i + j) < gamesNeedingEnglish.Count; j++)
                        {
                            var game = gamesNeedingEnglish[i + j];
                            var tcs = new TaskCompletionSource<bool>();
                            Dispatcher.UIThread.Post(async () =>
                            {
                                try
                                {
                                    await game.LoadCoverAsync(_imageService);
                                    tcs.TrySetResult(true);
                                }
                                catch (Exception ex) { tcs.TrySetException(ex); }
                            });
                            phase2Tasks.Add(tcs.Task);

                            if (!isEnglish)
                            {
                                gamesNeedingTarget.Add(game);
                            }
                        }

                        if (phase2Tasks.Count > 0)
                        {
                            await Task.WhenAll(phase2Tasks);
                        }

                        await Task.Delay(50, ct);
                    }
                    catch (OperationCanceledException) { break; }
                    catch (Exception ex)
                    {
#if DEBUG
                        AppLogger.LogDebug($"Phase 2 error at {i}: {ex.Message}");
#endif
                    }
                }

                UpdateProgress(progressContext, 66.0, $"{gamesNeedingEnglish.Count}/{gamesNeedingEnglish.Count}");

                // PHASE 3: Download target language
                if (!isEnglish)
                {
                    Dispatcher.UIThread.Post(() => StatusText.Text = $"Downloading {currentLanguage} images...");

                    const int phase3BatchSize = 3;

                    for (int i = 0; i < gamesNeedingTarget.Count; i += phase3BatchSize)
                    {
                        try
                        {
                            if (ct.IsCancellationRequested || _currentLanguage != currentLanguage)
                                break;

                            double phase3Progress = 66.0 + (i * 34.0) / Math.Max(1, gamesNeedingTarget.Count);
                            UpdateProgress(progressContext, phase3Progress, $"{i}/{gamesNeedingTarget.Count}");

                            var phase3Tasks = new List<Task>();
                            for (int j = 0; j < phase3BatchSize && (i + j) < gamesNeedingTarget.Count; j++)
                            {
                                var game = gamesNeedingTarget[i + j];
                                var tcs = new TaskCompletionSource<bool>();
                                Dispatcher.UIThread.Post(async () =>
                                {
                                    try
                                    {
                                        await game.LoadCoverAsync(_imageService, languageOverride: null, forceReload: true);
                                        tcs.TrySetResult(true);
                                    }
                                    catch (Exception ex) { tcs.TrySetException(ex); }
                                });
                                phase3Tasks.Add(tcs.Task);
                            }

                            if (phase3Tasks.Count > 0)
                            {
                                await Task.WhenAll(phase3Tasks);
                            }

                            await Task.Delay(100, ct);
                        }
                        catch (OperationCanceledException) { break; }
                        catch (Exception ex)
                        {
#if DEBUG
                            AppLogger.LogDebug($"Phase 3 error at {i}: {ex.Message}");
#endif
                        }
                    }
                }

                ClearProgress(progressContext);
                Dispatcher.UIThread.Post(() => StatusText.Text = "Ready");

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

        private async Task RefreshImagesForLanguageSwitch(string newLanguage)
        {
            await CleanSlateLanguageSwitcher.SwitchLanguageAsync(
                GamesView,
                Games,
                newLanguage
            );

            StartSequentialImageLoading(ProgressContext.LanguageSwitch);
        }

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

                        foreach (var nameElement in gameElement.Elements())
                        {
                            var elementName = nameElement.Name.LocalName;
                            if (elementName.StartsWith("Name_") && !string.IsNullOrEmpty(nameElement.Value))
                            {
                                var language = elementName.Substring(5);
                                gameLocalizedTitles[language] = nameElement.Value;
                            }
                        }

                        if (gameLocalizedTitles.Count > 0)
                        {
                            localizedData[appId] = gameLocalizedTitles;
                        }
                    }
                }

                foreach (var game in _allGames)
                {
                    if (localizedData.TryGetValue(game.ID, out var titles))
                    {
                        game.LocalizedTitles = titles;
                    }
                }

                AppLogger.LogDebug($"Loaded localized titles from {steamGamesXmlPath} for {localizedData.Count} games");
            }
            catch (Exception ex)
            {
                AppLogger.LogDebug($"Error loading localized titles: {ex.Message}");
            }
        }

        private void UpdateAllGameTitles(string language)
        {
            foreach (var game in _allGames)
            {
                game.UpdateDisplayTitle(language);
            }

            _allGames.Sort((a, b) => string.Compare(a.Title, b.Title, StringComparison.OrdinalIgnoreCase));

            var sortedGames = Games.OrderBy(g => g.Title, StringComparer.OrdinalIgnoreCase).ToList();

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
    }

    public class GameItem : System.ComponentModel.INotifyPropertyChanged, IImageLoadableItem
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
                    }
                }
            }
        }

        public string EnglishTitle { get; set; }
        public Dictionary<string, string> LocalizedTitles { get; set; } = new();
        public int ID { get; set; }
        public int AppId => ID;

        private string _iconUri;
        private volatile bool _isUpdatingIcon = false;

        public string IconUri
        {
            get => _iconUri;
            set
            {
                if (_isUpdatingIcon) return;

                try
                {
                    _isUpdatingIcon = true;

                    if (_iconUri != value || !string.IsNullOrEmpty(value))
                    {
                        _iconUri = value;
                        try
                        {
                            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(IconUri)));
                        }
                        catch (Exception ex)
                        {
                            AppLogger.LogDebug($"Error in PropertyChanged for IconUri (ID: {ID}): {ex.GetType().Name}: {ex.Message}");
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
        public bool IsManagerAvailable => GameLauncher.IsManagerAvailable;

        private bool _coverLoading;
        private string? _loadedLanguage;
        public bool IsCoverLoading => _coverLoading;

        public void ResetCover()
        {
            Dispatcher.UIThread.Post(() =>
            {
                IconUri = ImageLoadingHelper.GetNoIconPath();
            });
            _coverLoading = false;
            _loadedLanguage = null;
        }

        public void ClearLoadingState()
        {
            _coverLoading = false;
            _loadedLanguage = null;
        }

        public bool IsCoverFromLanguage(string language)
        {
            return _loadedLanguage != null &&
                   string.Equals(_loadedLanguage, language, StringComparison.OrdinalIgnoreCase);
        }

        public string? GetLoadedLanguage() => _loadedLanguage;

        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

        public GameItem(string title, int id,
                         string? exePath = null,
                         string? arguments = null,
                         string? uriScheme = null)
        {
            EnglishTitle = title;
            _displayTitle = title;
            _iconUri = ImageLoadingHelper.GetNoIconPath();
            ID = id;
            ExePath = exePath;
            Arguments = arguments;
            UriScheme = uriScheme;
        }

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
                Title = EnglishTitle;
            }
        }

        public async Task LoadCoverAsync(SharedImageService imageService, string? languageOverride = null, bool forceReload = false)
        {
            if (_coverLoading)
                return;

            string currentLanguage = languageOverride ?? SteamLanguageResolver.GetSteamLanguage();
            var noIconPath = ImageLoadingHelper.GetNoIconPath();
            if (!forceReload && !string.IsNullOrEmpty(IconUri) && !ImageLoadingHelper.IsNoIcon(IconUri))
                return;

            _coverLoading = true;

            try
            {
                var (imagePath, loadedLanguage) = await ImageLoadingHelper.LoadWithEnglishFallbackAsync(
                    imageService,
                    ID,
                    currentLanguage,
                    onEnglishFallbackLoaded: (englishPath) =>
                    {
                        _loadedLanguage = "english";
                        Dispatcher.UIThread.Post(() =>
                        {
                            var globalLanguage = SteamLanguageResolver.GetSteamLanguage();
                            if (currentLanguage == globalLanguage)
                            {
                                IconUri = englishPath;
                            }
                        });
                    },
                    currentLanguageGetter: () => SteamLanguageResolver.GetSteamLanguage()
                );

                if (!string.IsNullOrEmpty(imagePath) && File.Exists(imagePath))
                {
                    _loadedLanguage = loadedLanguage;
                    Dispatcher.UIThread.Post(() =>
                    {
                        var globalLanguage = SteamLanguageResolver.GetSteamLanguage();
                        if (currentLanguage == globalLanguage || _loadedLanguage == globalLanguage)
                        {
                            IconUri = imagePath;
                        }
                    });
                }
                else
                {
                    Dispatcher.UIThread.Post(() => IconUri = noIconPath);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
#if DEBUG
                AppLogger.LogDebug($"Error loading image for {ID}: {ex.Message}");
#endif
                Dispatcher.UIThread.Post(() => IconUri = noIconPath);
            }
            finally
            {
                _coverLoading = false;
            }
        }

        public static GameItem FromSteamApp(SteamAppData app)
        {
#if DEBUG
            AppLogger.LogDebug($"Creating GameItem for {app.AppId} - {app.Title}");
#endif
            return new GameItem(app.Title,
                                app.AppId,
                                app.ExePath,
                                app.Arguments,
                                app.UriScheme);
        }
    }
}
