using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CommonUtilities;
using MyOwnGames.Services;

namespace MyOwnGames
{
    public partial class MainWindow : Window, INotifyPropertyChanged
    {
        public ObservableCollection<GameEntry> GameItems { get; } = new();
        private List<GameEntry> AllGameItems { get; } = new();
        private readonly SharedImageService _imageService;
        private readonly GameDataService _dataService = new();
        private readonly Action<string> _logHandler;
        private SteamApiService? _steamService;
        private bool _isShuttingDown = false;
        private CancellationTokenSource? _cancellationTokenSource;
        private CancellationTokenSource _sequentialLoadCts = new();
        private DispatcherTimer? _searchDebounceTimer;
        private DispatcherTimer? _cdnStatsTimer;

        private readonly string _detectedLanguage = GetDefaultLanguage();
        private readonly string _defaultLanguage = "english";

        private string _statusText = "Ready.";
        public string StatusText
        {
            get => _statusText;
            set { if (_statusText != value) { _statusText = value; OnPropertyChanged(); } }
        }

        private bool _isLoading;
        public bool IsLoading
        {
            get => _isLoading;
            set
            {
                if (_isLoading != value)
                {
                    _isLoading = value;
                    OnPropertyChanged();
                    IsProgressVisible = value;
                    if (value) ProgressPercentText = "";
                }
            }
        }

        private double _progressValue;
        public double ProgressValue
        {
            get => _progressValue;
            set { if (Math.Abs(_progressValue - value) > double.Epsilon) { _progressValue = value; OnPropertyChanged(); ProgressPercentText = $"{(int)_progressValue}%"; } }
        }

        private bool _isProgressVisible;
        public bool IsProgressVisible
        {
            get => _isProgressVisible;
            set { if (_isProgressVisible != value) { _isProgressVisible = value; OnPropertyChanged(); } }
        }

        private string _progressPercentText = "";
        public string ProgressPercentText
        {
            get => _progressPercentText;
            set { if (_progressPercentText != value) { _progressPercentText = value; OnPropertyChanged(); } }
        }

        public new event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? name = null)
        {
            try
            {
                if (!_isShuttingDown)
                {
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
                }
            }
            catch (Exception ex) when (_isShuttingDown)
            {
                AppLogger.LogDebug($"Ignored exception during shutdown in OnPropertyChanged: {ex.Message}");
            }
        }

        public void AppendLog(string message)
        {
            // NO-OP: UI log removed, AppLogger handles all logging at source
        }

        private static string GetDefaultLanguage()
        {
            var culture = CultureInfo.CurrentCulture.Name;
            if (culture.Equals("zh-TW", StringComparison.OrdinalIgnoreCase))
                return "tchinese";
            if (culture.StartsWith("ja", StringComparison.OrdinalIgnoreCase))
                return "japanese";
            if (culture.StartsWith("ko", StringComparison.OrdinalIgnoreCase))
                return "korean";
            if (culture.StartsWith("en", StringComparison.OrdinalIgnoreCase))
                return "english";
            return "english";
        }

        private void ReorderLanguageOptions()
        {
            if (_detectedLanguage == "english")
                return;

            var detectedItem = LanguageComboBox.Items
                .OfType<ComboBoxItem>()
                .FirstOrDefault(i => string.Equals(i.Content?.ToString(), _detectedLanguage, StringComparison.OrdinalIgnoreCase));

            if (detectedItem != null)
            {
                LanguageComboBox.Items.Remove(detectedItem);
                LanguageComboBox.Items.Insert(0, detectedItem);
                AppendLog($"Moved detected language '{_detectedLanguage}' to first position, but defaulting to English");
            }
        }

        public MainWindow()
        {
            _imageService = new SharedImageService(HttpClientProvider.Shared);
            InitializeComponent();
            DataContext = this;

            // Set window icon
            var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "MyOwnGames.ico");
            if (File.Exists(iconPath))
                Icon = new WindowIcon(iconPath);

            // Rearrange language options: move detected OS language to first position
            ReorderLanguageOptions();

            // Set default selection to English
            var defaultItem = LanguageComboBox.Items
                .OfType<ComboBoxItem>()
                .FirstOrDefault(i => string.Equals(i.Content?.ToString(), _defaultLanguage, StringComparison.OrdinalIgnoreCase));
            if (defaultItem != null)
            {
                LanguageComboBox.SelectedItem = defaultItem;
            }

            _logHandler = msg => { };

            // Subscribe to image download completion events
            _imageService.ImageDownloadCompleted += OnImageDownloadCompleted;

            // Initialize image service with default language
            var initialLanguage = GetCurrentLanguage();
            _ = _imageService.SetLanguage(initialLanguage);
            AppendLog($"Initialized with language: {initialLanguage}");

            // Initialize CDN statistics timer (updates every 2 seconds)
            _cdnStatsTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            _cdnStatsTimer.Tick += CdnStatsTimer_Tick;
            _cdnStatsTimer.Start();

            // Subscribe to keyboard events
            KeyDown += OnWindowKeyDown;

            // Subscribe to text changes for search debounce
            KeywordBox.TextChanged += KeywordBox_TextChanged;

            // Subscribe to password field changes
            ApiKeyBox.TextChanged += InputFields_TextChanged;
            SteamIdBox.TextChanged += InputFields_TextChanged;

            // Initialize search debounce timer
            _searchDebounceTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(300)
            };
            _searchDebounceTimer.Tick += OnSearchDebounceTimerTick;

            // Load saved games after window is displayed
            Opened += MainWindow_Opened;

            // Clean up old failed download records on startup
            _ = CleanupOldFailedRecordsAsync();

            // Closing event
            Closing += OnWindowClosing;

            UpdateGetGamesButtonState();
        }

        private async void MainWindow_Opened(object? sender, EventArgs e)
        {
            Opened -= MainWindow_Opened;
            await LoadSavedGamesAsync();
        }

        private void OnImageDownloadCompleted(int appId, string? imagePath)
        {
            if (_isShuttingDown) return;

            Dispatcher.UIThread.Post(() =>
            {
                if (_isShuttingDown) return;

                try
                {
                    var gameEntry = AllGameItems.FirstOrDefault(g => g.AppId == appId);
                    if (gameEntry != null && !string.IsNullOrEmpty(imagePath) && File.Exists(imagePath))
                    {
                        var fileUri = new Uri(imagePath).AbsoluteUri;
                        if (gameEntry.IconUri == fileUri) return;

                        gameEntry.IconUri = fileUri;
                        AppLogger.LogDebug($"Updated UI for downloaded image {appId}: {fileUri}");
                    }
                }
                catch (Exception ex) when (_isShuttingDown) { }
                catch (Exception ex)
                {
                    AppLogger.LogDebug($"Error updating UI for downloaded image {appId}: {ex.Message}");
                }
            });
        }

        private async Task CleanupOldFailedRecordsAsync()
        {
            try
            {
                await Task.Run(() =>
                {
                    var imageFailureService = new ImageFailureTrackingService();
                });
            }
            catch (Exception ex)
            {
                AppendLog($"Error cleaning up old failed download records: {ex.Message}");
            }
        }

        private async Task LoadSavedGamesAsync()
        {
            IsLoading = true;
            try
            {
                AppendLog("Loading saved games...");
                StatusText = "Loading saved games...";
                var enteredId = SteamIdBox.Text?.Trim() ?? string.Empty;
                await EnsureSteamIdHashConsistencyAsync(enteredId);

                var savedGamesWithLanguages = await _dataService.LoadGamesWithLanguagesAsync();
                var currentLanguage = GetCurrentLanguage();

                await _imageService.SetLanguage(currentLanguage);

                GameItems.Clear();
                AllGameItems.Clear();

                await Task.Run(() =>
                {
                    foreach (var game in savedGamesWithLanguages)
                    {
                        var entry = new GameEntry
                        {
                            AppId = game.AppId,
                            NameEn = game.NameEn,
                            LocalizedNames = new Dictionary<string, string>(game.LocalizedNames),
                            CurrentLanguage = currentLanguage,
                            IconUri = ImageLoadingHelper.GetNoIconPath()
                        };

                        Dispatcher.UIThread.Post(() =>
                        {
                            GameItems.Add(entry);
                            AllGameItems.Add(entry);
                        });
                    }
                });

                StartSequentialImageLoading();

                var exportInfo = await _dataService.GetExportInfoAsync();
                if (exportInfo != null)
                {
                    StatusText = $"Loaded {savedGamesWithLanguages.Count} saved games (exported: {exportInfo.ExportDate:yyyy-MM-dd}, language: {exportInfo.Language})";
                }
                else
                {
                    StatusText = "Ready. Enter Steam API Key and SteamID to fetch your games.";
                }

                AppendLog($"Loaded {savedGamesWithLanguages.Count} saved games from {_dataService.GetXmlFilePath()}");
            }
            catch (Exception ex)
            {
                StatusText = $"Error loading saved games: {ex.Message}";
                AppendLog($"Error loading saved games: {ex.Message}");
            }
            finally
            {
                IsLoading = false;
            }
        }

        private async Task EnsureSteamIdHashConsistencyAsync(string steamId64)
        {
            if (string.IsNullOrWhiteSpace(steamId64))
                return;

            var exportInfo = await _dataService.GetExportInfoAsync();
            if (exportInfo != null && !string.IsNullOrEmpty(exportInfo.SteamIdHash))
            {
                var currentHash = _dataService.GetSteamIdHash(steamId64);
                if (!string.Equals(currentHash, exportInfo.SteamIdHash, StringComparison.OrdinalIgnoreCase))
                {
                    _dataService.ClearGameData();
                    _imageService.ClearCache();
                    GameItems.Clear();
                    AllGameItems.Clear();
                    AppendLog("SteamID changed, cleared previous data and image cache.");
                }
            }
        }

        private async void GetGamesButton_Click(object? sender, RoutedEventArgs e)
        {
            var apiKey = ApiKeyBox.Text?.Trim();
            var steamId64 = SteamIdBox.Text?.Trim();

            if (!InputValidator.IsValidApiKey(apiKey))
            {
                StatusText = "Invalid Steam API Key. It must be 32 hexadecimal characters.";
                return;
            }

            if (!InputValidator.IsValidSteamId64(steamId64))
            {
                StatusText = "Invalid SteamID64. It must be a 17-digit number starting with 7656119.";
                return;
            }

            SetControlsEnabledState(false);
            await EnsureSteamIdHashConsistencyAsync(steamId64!);

            string? xmlPath = null;

            _cancellationTokenSource?.Cancel();
            _cancellationTokenSource?.Dispose();
            _cancellationTokenSource = new CancellationTokenSource();
            var cancellationToken = _cancellationTokenSource.Token;

            try
            {
                var selectedLanguage = _defaultLanguage;
                if (LanguageComboBox.SelectedItem is ComboBoxItem selectedItem)
                {
                    selectedLanguage = selectedItem.Content?.ToString() ?? _defaultLanguage;
                }

                AppendLog($"Starting complete game scan for language: {selectedLanguage}...");
                IsLoading = true;
                StatusText = $"Scanning all games for {selectedLanguage} language data...";
                ProgressValue = 0;

                AppendLog($"Current games in list: {GameItems.Count}. Checking all games for {selectedLanguage} data.");

                var progress = new Progress<double>(value =>
                {
                    ProgressValue = value;
                });

                var existingGamesData = await _dataService.LoadGamesWithLanguagesAsync();
                var existingLocalizedNames = new Dictionary<int, string>();
                var skipAppIds = new HashSet<int>();
                var gamesMissingEnglishNames = new HashSet<int>();

                StatusText = $"Preparing existing game data ({existingGamesData.Count} entries)...";
                await Task.Run(() =>
                {
                    foreach (var game in existingGamesData)
                    {
                        if (string.IsNullOrEmpty(game.NameEn))
                        {
                            gamesMissingEnglishNames.Add(game.AppId);
                            AppendLog($"Game {game.AppId} missing English name, will force English data update");
                        }

                        if (game.LocalizedNames != null && game.LocalizedNames.TryGetValue(selectedLanguage, out var name) &&
                            !string.IsNullOrEmpty(name))
                        {
                            existingLocalizedNames[game.AppId] = name;
                            skipAppIds.Add(game.AppId);
                        }
                    }
                }, cancellationToken);

                bool needsEnglishUpdate = selectedLanguage != "english" &&
                                        (existingGamesData.Count == 0 || gamesMissingEnglishNames.Count > 0);

                _steamService = new SteamApiService(apiKey!);

                if (needsEnglishUpdate)
                {
                    AppendLog($"Found {gamesMissingEnglishNames.Count} games missing English names. Forcing English data update first...");
                    StatusText = "First updating English game names (required for localization)...";
                    var englishBatchBuffer = new List<SteamGame>();
                    const int batchSize = 100;

                    var englishTotal = await _steamService.GetOwnedGamesAsync(steamId64!, "english", async englishGame =>
                    {
                        if (gamesMissingEnglishNames.Contains(englishGame.AppId) || existingGamesData.Count == 0)
                        {
                            englishBatchBuffer.Add(englishGame);
                            AppendLog($"Queued English data for {englishGame.AppId} - {englishGame.NameEn}");

                            if (englishBatchBuffer.Count >= batchSize)
                            {
                                await _dataService.AppendGamesAsync(englishBatchBuffer, steamId64!, apiKey!, "english");
                                AppendLog($"Saved batch of {englishBatchBuffer.Count} English games");
                                englishBatchBuffer.Clear();
                            }
                        }
                    }, progress, existingAppIds: null, existingLocalizedNames: null, cancellationToken);

                    if (englishBatchBuffer.Count > 0)
                    {
                        await _dataService.AppendGamesAsync(englishBatchBuffer, steamId64!, apiKey!, "english");
                        AppendLog($"Saved final batch of {englishBatchBuffer.Count} English games");
                        englishBatchBuffer.Clear();
                    }

                    existingGamesData = await _dataService.LoadGamesWithLanguagesAsync();
                    existingLocalizedNames.Clear();
                    gamesMissingEnglishNames.Clear();

                    await Task.Run(() =>
                    {
                        foreach (var game in existingGamesData)
                        {
                            if (game.LocalizedNames != null && game.LocalizedNames.TryGetValue(selectedLanguage, out var name) &&
                                !string.IsNullOrEmpty(name))
                            {
                                existingLocalizedNames[game.AppId] = name;
                                skipAppIds.Add(game.AppId);
                            }
                        }
                    }, cancellationToken);
                }

                StatusText = selectedLanguage == "english"
                    ? "Scanning all games..."
                    : $"Scanning all games for {selectedLanguage} language data (this will be slower to avoid Steam API rate limits)...";

                var languageBatchBuffer = new List<SteamGame>();
                const int languageBatchSize = 100;

                var total = await _steamService.GetOwnedGamesAsync(steamId64!, selectedLanguage, async game =>
                {
                    var shouldSkip = skipAppIds.Contains(game.AppId);
                    var existingGameData = existingGamesData.FirstOrDefault(g => g.AppId == game.AppId);
                    var hasLanguageData = existingGameData?.LocalizedNames?.ContainsKey(selectedLanguage) == true &&
                                         !string.IsNullOrEmpty(existingGameData.LocalizedNames[selectedLanguage]);

                    AppendLog($"Processing game {game.AppId} - {game.NameEn} ({selectedLanguage}){(hasLanguageData ? " [updating]" : " [new data]")}");

                    var existingEntry = AllGameItems.FirstOrDefault(g => g.AppId == game.AppId);

                    if (existingEntry != null)
                    {
                        existingEntry.NameEn = game.NameEn;
                        existingEntry.SetLocalizedName(selectedLanguage, game.NameLocalized);
                        existingEntry.CurrentLanguage = selectedLanguage;
                        AppendLog($"Updated UI entry: {game.AppId} - {game.NameEn}");
                    }
                    else
                    {
                        var newEntry = new GameEntry
                        {
                            AppId = game.AppId,
                            NameEn = game.NameEn,
                            CurrentLanguage = selectedLanguage,
                            IconUri = ImageLoadingHelper.GetNoIconPath()
                        };

                        newEntry.SetLocalizedName(selectedLanguage, game.NameLocalized);

                        GameItems.Add(newEntry);
                        AllGameItems.Add(newEntry);
                        AppendLog($"Added new game: {game.AppId} - {game.NameEn} ({selectedLanguage})");
                    }

                    if (!shouldSkip)
                    {
                        languageBatchBuffer.Add(game);

                        if (languageBatchBuffer.Count >= languageBatchSize)
                        {
                            await _dataService.AppendGamesAsync(languageBatchBuffer, steamId64!, apiKey!, selectedLanguage);
                            AppendLog($"Saved batch of {languageBatchBuffer.Count} {selectedLanguage} games");
                            languageBatchBuffer.Clear();
                        }
                    }
                }, progress, skipAppIds, existingLocalizedNames, cancellationToken);

                if (languageBatchBuffer.Count > 0)
                {
                    await _dataService.AppendGamesAsync(languageBatchBuffer, steamId64!, apiKey!, selectedLanguage);
                    AppendLog($"Saved final batch of {languageBatchBuffer.Count} {selectedLanguage} games");
                    languageBatchBuffer.Clear();
                }

                xmlPath = _dataService.GetXmlFilePath();

                AppendLog($"Processing {skipAppIds.Count} games with existing {selectedLanguage} data...");
                foreach (var skippedAppId in skipAppIds)
                {
                    try
                    {
                        var existingGameData = existingGamesData.FirstOrDefault(g => g.AppId == skippedAppId);
                        if (existingGameData != null && existingGameData.LocalizedNames != null &&
                            existingGameData.LocalizedNames.TryGetValue(selectedLanguage, out var localizedName))
                        {
                            var existingEntry = AllGameItems.FirstOrDefault(g => g.AppId == skippedAppId);

                            if (existingEntry != null)
                            {
                                existingEntry.SetLocalizedName(selectedLanguage, localizedName);
                                existingEntry.CurrentLanguage = selectedLanguage;
                                AppendLog($"Updated existing UI entry for game {skippedAppId} ({selectedLanguage})");
                            }
                            else
                            {
                                var newEntry = new GameEntry
                                {
                                    AppId = skippedAppId,
                                    NameEn = existingGameData.NameEn ?? skippedAppId.ToString(),
                                    CurrentLanguage = selectedLanguage,
                                    IconUri = ImageLoadingHelper.GetNoIconPath()
                                };

                                newEntry.SetLocalizedName(selectedLanguage, localizedName);

                                GameItems.Add(newEntry);
                                AllGameItems.Add(newEntry);

                                AppendLog($"Added UI entry for skipped game {skippedAppId} ({selectedLanguage})");
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        AppendLog($"Error processing skipped game {skippedAppId}: {ex.Message}");
                    }
                }

                var currentTotalCount = GameItems.Count;
                if (currentTotalCount < total)
                {
                    await _dataService.UpdateRemainingCountAsync(total - currentTotalCount);
                }
                else
                {
                    await _dataService.UpdateRemainingCountAsync(0);
                }

                StatusText = $"Completed full scan: {total} games processed with {selectedLanguage} data. Current list: {GameItems.Count} games. Saved to {xmlPath}";
                AppendLog($"Full language scan complete - Total games: {total}, All games now have {selectedLanguage} data, Current display: {GameItems.Count} games, saved to {xmlPath}");

                StartSequentialImageLoading();
            }
            catch (OperationCanceledException)
            {
                StatusText = "Operation was cancelled.";
                AppendLog("Game retrieval was cancelled.");
            }
            catch (ArgumentException ex)
            {
                StatusText = "Error: " + ex.Message;
                AppendLog($"Validation error: {ex.Message}");
            }
            catch (Exception ex)
            {
                StatusText = "Error: " + ex.Message;
                AppendLog($"Error retrieving games: {ex.Message}");
            }
            finally
            {
                try
                {
                    _steamService?.Dispose();
                    _steamService = null;
                }
                catch (Exception ex) when (_isShuttingDown)
                {
                    AppLogger.LogDebug($"Ignored exception during shutdown: {ex.Message}");
                }

                IsLoading = false;
                ProgressValue = 100;

                SetControlsEnabledState(true);

                if (!_isShuttingDown)
                {
                    AppendLog("Finished retrieving games.");
                }
            }
        }

        private void InputFields_TextChanged(object? sender, TextChangedEventArgs e)
        {
            UpdateGetGamesButtonState();
        }

        private void UpdateGetGamesButtonState()
        {
            var apiKey = ApiKeyBox.Text?.Trim();
            var steamId64 = SteamIdBox.Text?.Trim();
            GetGamesButton.IsEnabled = InputValidator.IsValidApiKey(apiKey) && InputValidator.IsValidSteamId64(steamId64);
        }

        private void SetControlsEnabledState(bool enabled)
        {
            ApiKeyBox.IsEnabled = enabled;
            SteamIdBox.IsEnabled = enabled;
            LanguageComboBox.IsEnabled = enabled;
            GetGamesButton.IsEnabled = enabled && InputValidator.IsValidApiKey(ApiKeyBox.Text?.Trim()) && InputValidator.IsValidSteamId64(SteamIdBox.Text?.Trim());
            StopButton.IsEnabled = !enabled;
        }

        private void StopButton_Click(object? sender, RoutedEventArgs e)
        {
            _cancellationTokenSource?.Cancel();
            StatusText = "Operation cancelled by user.";
            AppendLog("Get Game List operation cancelled by user.");

            SetControlsEnabledState(true);
            IsLoading = false;
        }

        private void KeywordBox_TextChanged(object? sender, TextChangedEventArgs e)
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
            FilterGameItems(KeywordBox.Text?.Trim());
        }

        private void CdnStatsTimer_Tick(object? sender, EventArgs args)
        {
            try
            {
                if (_imageService == null || StatusCdn == null || _isShuttingDown)
                {
                    return;
                }

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

        private void FilterGameItems(string? keyword)
        {
            if (string.IsNullOrWhiteSpace(keyword))
            {
                GameItems.Clear();
                foreach (var item in AllGameItems)
                    GameItems.Add(item);

                StatusText = $"Showing {AllGameItems.Count} game(s).";
                return;
            }

            var filtered = AllGameItems.Where(g =>
                (!string.IsNullOrEmpty(g.NameLocalized) && g.NameLocalized.Contains(keyword, StringComparison.OrdinalIgnoreCase)) ||
                (!string.IsNullOrEmpty(g.NameEn) && g.NameEn.Contains(keyword, StringComparison.OrdinalIgnoreCase)) ||
                g.AppId.ToString().Contains(keyword, StringComparison.OrdinalIgnoreCase))
                .ToList();

            GameItems.Clear();
            foreach (var item in filtered)
                GameItems.Add(item);

            StatusText = filtered.Count > 0
                ? $"Found {filtered.Count} result(s) for \"{keyword}\"."
                : $"No results found for \"{keyword}\".";
        }

        private void OnWindowKeyDown(object? sender, KeyEventArgs e)
        {
            if (e.Key != Key.PageDown &&
                e.Key != Key.PageUp &&
                e.Key != Key.Down &&
                e.Key != Key.Up)
                return;

            var sv = GamesScrollViewer;
            if (sv == null)
                return;

            double offset = sv.Offset.Y;
            double delta = e.Key switch
            {
                Key.PageDown => sv.Viewport.Height,
                Key.PageUp => -sv.Viewport.Height,
                Key.Down => 100,
                Key.Up => -100,
                _ => 0
            };

            var target = Math.Max(0, Math.Min(offset + delta, sv.Extent.Height - sv.Viewport.Height));
            sv.Offset = new Vector(sv.Offset.X, target);
            e.Handled = true;
        }

        private void GameTile_DoubleTapped(object? sender, TappedEventArgs e)
        {
            if (sender is Border border && border.DataContext is GameEntry gameEntry)
            {
                LaunchRunGame(gameEntry.AppId);
            }
        }

        private async void StartSequentialImageLoading()
        {
            try
            {
                var oldCts = _sequentialLoadCts;
                _sequentialLoadCts = new CancellationTokenSource();
                oldCts.Cancel();
                oldCts.Dispose();

                var ct = _sequentialLoadCts.Token;
                var currentLanguage = GetCurrentLanguage();

#if DEBUG
                AppLogger.LogDebug($"StartSequentialImageLoading: TWO-PHASE loading for {AllGameItems.Count} games in {currentLanguage}");
#endif

                // PHASE 1: Instant load of cached images only
                var gamesNeedingEnglish = new List<GameEntry>();
                var gamesNeedingTarget = new List<GameEntry>();
                bool isEnglish = string.Equals(currentLanguage, "english", StringComparison.OrdinalIgnoreCase);
                const int phase1BatchSize = 50;

                for (int i = 0; i < AllGameItems.Count; i += phase1BatchSize)
                {
                    try
                    {
                        if (ct.IsCancellationRequested)
                            break;

                        for (int j = 0; j < phase1BatchSize && (i + j) < AllGameItems.Count; j++)
                        {
                            var entry = AllGameItems[i + j];
                            if (ImageLoadingHelper.IsNoIcon(entry.IconUri))
                            {
                                string? cachedPath = _imageService.TryGetCachedPath(entry.AppId, currentLanguage, checkEnglishFallback: false);

                                if (!string.IsNullOrEmpty(cachedPath) && File.Exists(cachedPath))
                                {
                                    var uri = new Uri(cachedPath).AbsoluteUri;
                                    Dispatcher.UIThread.Post(() => entry.IconUri = uri);
                                }
                                else if (!isEnglish)
                                {
                                    cachedPath = _imageService.TryGetCachedPath(entry.AppId, "english", checkEnglishFallback: false);

                                    if (!string.IsNullOrEmpty(cachedPath) && File.Exists(cachedPath))
                                    {
                                        var uri = new Uri(cachedPath).AbsoluteUri;
                                        Dispatcher.UIThread.Post(() => entry.IconUri = uri);
                                        gamesNeedingTarget.Add(entry);
                                    }
                                    else
                                    {
                                        gamesNeedingEnglish.Add(entry);
                                    }
                                }
                                else
                                {
                                    gamesNeedingEnglish.Add(entry);
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

#if DEBUG
                AppLogger.LogDebug($"Phase 1 complete. Need English: {gamesNeedingEnglish.Count}, Need target: {gamesNeedingTarget.Count}");
#endif

                // PHASE 2: Fast download of English fallback
                const int phase2BatchSize = 5;

                for (int i = 0; i < gamesNeedingEnglish.Count; i += phase2BatchSize)
                {
                    try
                    {
                        if (ct.IsCancellationRequested)
                        {
#if DEBUG
                            AppLogger.LogDebug($"Phase 2 cancelled at {i}/{gamesNeedingEnglish.Count}");
#endif
                            break;
                        }

                        var phase2Tasks = new List<Task>();
                        for (int j = 0; j < phase2BatchSize && (i + j) < gamesNeedingEnglish.Count; j++)
                        {
                            var entry = gamesNeedingEnglish[i + j];
                            var tcs = new TaskCompletionSource<bool>();
                            Dispatcher.UIThread.Post(async () =>
                            {
                                try
                                {
                                    await entry.LoadCoverAsync(_imageService);
                                    tcs.TrySetResult(true);
                                }
                                catch (Exception ex)
                                {
                                    tcs.TrySetException(ex);
                                }
                            });
                            phase2Tasks.Add(tcs.Task);

                            if (!isEnglish)
                            {
                                gamesNeedingTarget.Add(entry);
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

#if DEBUG
                AppLogger.LogDebug($"Phase 2 complete. Now downloading target language for {gamesNeedingTarget.Count} games.");
#endif

                // PHASE 3: Slow download of target language
                if (!isEnglish)
                {
                    const int phase3BatchSize = 3;

                    for (int i = 0; i < gamesNeedingTarget.Count; i += phase3BatchSize)
                    {
                        try
                        {
                            if (ct.IsCancellationRequested)
                            {
#if DEBUG
                                AppLogger.LogDebug($"Phase 3 cancelled at {i}/{gamesNeedingTarget.Count}");
#endif
                                break;
                            }

                            var phase3Tasks = new List<Task>();
                            for (int j = 0; j < phase3BatchSize && (i + j) < gamesNeedingTarget.Count; j++)
                            {
                                var entry = gamesNeedingTarget[i + j];
                                var tcs = new TaskCompletionSource<bool>();
                                Dispatcher.UIThread.Post(async () =>
                                {
                                    try
                                    {
                                        await entry.LoadCoverAsync(_imageService, languageOverride: null, forceReload: true);
                                        tcs.TrySetResult(true);
                                    }
                                    catch (Exception ex)
                                    {
                                        tcs.TrySetException(ex);
                                    }
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

#if DEBUG
                AppLogger.LogDebug("Sequential image loading completed");
#endif
            }
            catch (Exception ex)
            {
                AppLogger.LogDebug($"StartSequentialImageLoading unhandled error: {ex.Message}");
            }
        }

        private void LaunchRunGame(int appId)
        {
            try
            {
                var possiblePaths = new[]
                {
                    Path.Combine(
                        Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location) ?? "",
                        "RunGame.exe"),
                    Path.Combine(
                        Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location) ?? "",
                        "..", "RunGame", "RunGame.exe"),
                    Path.Combine(
                        Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location) ?? "",
                        "..", "..", "..", "..", "output", "Debug", "x64", "net10.0", "RunGame", "RunGame.exe")
                };

                string? runGameExePath = null;
                foreach (var path in possiblePaths)
                {
                    var fullPath = Path.GetFullPath(path);
                    if (File.Exists(fullPath))
                    {
                        runGameExePath = fullPath;
                        break;
                    }
                }

                if (!string.IsNullOrEmpty(runGameExePath))
                {
                    AppendLog($"Found RunGame.exe at: {runGameExePath}");
                    AppendLog($"Launching RunGame with Game ID: {appId}");

                    var process = new System.Diagnostics.Process
                    {
                        StartInfo = new System.Diagnostics.ProcessStartInfo
                        {
                            FileName = runGameExePath,
                            Arguments = appId.ToString(),
                            UseShellExecute = false,
                            CreateNoWindow = false,
                            WorkingDirectory = Path.GetDirectoryName(runGameExePath)
                        }
                    };

                    var started = process.Start();
                    AppendLog($"RunGame process started: {started}");

                    if (started)
                    {
                        AppendLog($"Successfully launched RunGame for game {appId}");
                    }
                    else
                    {
                        AppendLog($"Failed to start RunGame process for game {appId}");
                    }
                }
                else
                {
                    var searchedPaths = string.Join("\n", possiblePaths.Select(Path.GetFullPath));
                    AppendLog($"RunGame.exe not found in any of these locations:\n{searchedPaths}");
                }
            }
            catch (Exception ex)
            {
                AppendLog($"Error launching RunGame for game {appId}: {ex.Message}");
                AppLogger.LogDebug($"Error launching RunGame: {ex.Message}");
            }
        }

        private async void OnWindowClosing(object? sender, WindowClosingEventArgs e)
        {
            await SaveAndDisposeAsync("window closing");
        }

        public async Task SaveAndDisposeAsync(string reason)
        {
            if (_isShuttingDown)
                return;
            _isShuttingDown = true;
            AppLogger.OnLog -= _logHandler;

            _cancellationTokenSource?.Cancel();

            try
            {
                AppendLog("Saving game data...");
                await _dataService.UpdateRemainingCountAsync(0);
                AppendLog("Game data saved.");
            }
            catch (Exception ex)
            {
                AppendLog($"Error saving data: {ex.Message}");
            }

            _steamService?.Dispose();
            _steamService = null;

            try
            {
                if (_imageService != null)
                {
                    _imageService.ImageDownloadCompleted -= OnImageDownloadCompleted;
                    _imageService.Dispose();
                }
            }
            catch (Exception ex)
            {
                AppLogger.LogDebug($"Error disposing image service: {ex.Message}");
            }

            try
            {
                _cancellationTokenSource?.Dispose();
                _cancellationTokenSource = null;
                _sequentialLoadCts.Dispose();
            }
            catch (Exception ex)
            {
                AppLogger.LogDebug($"Error disposing cancellation token source: {ex.Message}");
            }

            try
            {
                if (_cdnStatsTimer != null)
                {
                    _cdnStatsTimer.Stop();
                    _cdnStatsTimer.Tick -= CdnStatsTimer_Tick;
                    _cdnStatsTimer = null;
                }
            }
            catch (Exception ex)
            {
                AppLogger.LogDebug($"Error stopping CDN stats timer: {ex.Message}");
            }

            AppLogger.LogDebug($"Shutdown completed ({reason})");
        }

        private string GetCurrentLanguage()
        {
            if (LanguageComboBox.SelectedItem is ComboBoxItem selectedItem)
            {
                return selectedItem.Content?.ToString() ?? _defaultLanguage;
            }
            return _defaultLanguage;
        }

        private async void LanguageComboBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
        {
            if (_isLoading || LanguageComboBox.SelectedItem == null)
                return;

            var newLanguage = GetCurrentLanguage();
            var currentImageServiceLanguage = _imageService?.GetCurrentLanguage();

            AppendLog($"Language switching: UI={newLanguage}, ImageService={currentImageServiceLanguage}");

            if (newLanguage == currentImageServiceLanguage)
            {
                AppendLog($"Language switch skipped - already using {newLanguage}");
                return;
            }

            AppendLog($"Language changed from {currentImageServiceLanguage} to: {newLanguage}");
            StatusText = $"Switching to {newLanguage}...";

            try
            {
                _isLoading = true;

                if (_imageService != null)
                {
                    await _imageService.SetLanguage(newLanguage);
                }

                foreach (var gameEntry in AllGameItems)
                {
                    gameEntry.CurrentLanguage = newLanguage;
                }

                _ = Task.Run(async () =>
                {
                    try
                    {
                        await ProcessLanguageSwitchImageRefresh(newLanguage);
                    }
                    catch (Exception ex)
                    {
                        AppLogger.LogDebug($"ProcessLanguageSwitchImageRefresh error: {ex.Message}");
                    }
                });

                StatusText = $"Language switched to {newLanguage}. Refreshing images...";
                AppendLog($"Updated {AllGameItems.Count} games for language: {newLanguage}");
            }
            catch (Exception ex)
            {
                AppendLog($"Error switching language: {ex.Message}");
                StatusText = "Error switching language";
            }
            finally
            {
                _isLoading = false;
            }
        }

        private async Task ProcessLanguageSwitchImageRefresh(string newLanguage)
        {
            try
            {
                // Use shared CleanSlateLanguageSwitcher
                await CleanSlateLanguageSwitcher.SwitchLanguageAsync(
                    GamesGridView,
                    GameItems,
                    newLanguage);

                Dispatcher.UIThread.Post(() =>
                {
                    StartSequentialImageLoading();
                    StatusText = $"Language switched to {newLanguage}. Loading images...";
                });
            }
            catch (Exception ex)
            {
                AppLogger.LogDebug($"Error during language switch: {ex.GetType().Name}: {ex.Message}");
                AppLogger.LogDebug($"Stack trace: {ex.StackTrace}");
                if (ex.InnerException != null)
                {
                    AppLogger.LogDebug($"Inner exception: {ex.InnerException.GetType().Name}: {ex.InnerException.Message}");
                }
                AppendLog($"Error processing language switch: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    public class GameEntry : INotifyPropertyChanged, IImageLoadableItem
    {
        public int AppId { get; set; }

        private string _iconUri = "";
        private volatile bool _isUpdatingIcon = false;

        private volatile bool _coverLoading = false;
        private string _loadedLanguage = "";

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
                        OnPropertyChanged();
                    }
                }
                finally
                {
                    _isUpdatingIcon = false;
                }
            }
        }

        public string NameEn { get; set; } = "";

        public Dictionary<string, string> LocalizedNames { get; set; } = new();

        private string _currentLanguage = "english";
        public string CurrentLanguage
        {
            get => _currentLanguage;
            set
            {
                if (_currentLanguage != value)
                {
                    _currentLanguage = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(DisplayName));
                }
            }
        }

        public string DisplayName
        {
            get
            {
                if (!string.IsNullOrEmpty(CurrentLanguage) &&
                    LocalizedNames.TryGetValue(CurrentLanguage, out var localizedName) &&
                    !string.IsNullOrEmpty(localizedName))
                {
                    return localizedName;
                }

                return NameEn;
            }
        }

        public string NameLocalized
        {
            get => DisplayName;
            set
            {
                if (!string.IsNullOrEmpty(CurrentLanguage) && !string.IsNullOrEmpty(value))
                {
                    LocalizedNames[CurrentLanguage] = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(DisplayName));
                }
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? name = null)
        {
            try
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
            }
            catch (Exception ex)
            {
                AppLogger.LogDebug($"Error in GameEntry.OnPropertyChanged: {ex.Message}");
            }
        }

        public void ForceIconRefresh()
        {
            try
            {
                OnPropertyChanged(nameof(IconUri));
            }
            catch (ObjectDisposedException) { }
        }

        public void SetLocalizedName(string language, string name)
        {
            LocalizedNames[language] = name;
            if (language == CurrentLanguage)
            {
                OnPropertyChanged(nameof(DisplayName));
                OnPropertyChanged(nameof(NameLocalized));
            }
        }

        public async Task LoadCoverAsync(SharedImageService imageService, string? languageOverride = null, bool forceReload = false)
        {
            if (_coverLoading)
            {
#if DEBUG
                AppLogger.LogDebug($"Skipping LoadCoverAsync for {AppId}: already loading");
#endif
                return;
            }

            if (!forceReload && !ImageLoadingHelper.IsNoIcon(IconUri))
            {
#if DEBUG
                AppLogger.LogDebug($"Skipping LoadCoverAsync for {AppId}: already has valid image");
#endif
                return;
            }

            _coverLoading = true;

            string currentLanguage = languageOverride ?? CurrentLanguage ?? "english";

#if DEBUG
            AppLogger.LogDebug($"LoadCoverAsync started for {AppId}, language={currentLanguage}");
#endif

            try
            {
                var (imagePath, loadedLanguage) = await ImageLoadingHelper.LoadWithEnglishFallbackAsync(
                    imageService,
                    AppId,
                    currentLanguage,
                    onEnglishFallbackLoaded: (englishPath) =>
                    {
                        _loadedLanguage = "english";
                        var englishUri = new Uri(englishPath).AbsoluteUri;
                        Dispatcher.UIThread.Post(() =>
                        {
                            if (currentLanguage == CurrentLanguage)
                            {
                                IconUri = englishUri;
#if DEBUG
                                AppLogger.LogDebug($"UI updated: {AppId} showing English fallback immediately");
#endif
                            }
                        });
                    },
                    currentLanguageGetter: () => CurrentLanguage ?? "english"
                );

                if (!string.IsNullOrEmpty(imagePath) && File.Exists(imagePath))
                {
                    _loadedLanguage = loadedLanguage;
                    var fileUri = new Uri(imagePath).AbsoluteUri;

                    Dispatcher.UIThread.Post(() =>
                    {
                        if (currentLanguage == CurrentLanguage)
                        {
                            IconUri = fileUri;
#if DEBUG
                            AppLogger.LogDebug($"UI updated: {AppId} final image in {loadedLanguage}");
#endif
                        }
                    });
                }
            }
            catch (Exception ex)
            {
                AppLogger.LogDebug($"Error loading cover for {AppId}: {ex.Message}");
            }
            finally
            {
                _coverLoading = false;
            }
        }

        public void ClearLoadingState()
        {
            _coverLoading = false;
            _loadedLanguage = "";
        }

        public bool IsCoverFromLanguage(string language)
        {
            if (string.IsNullOrEmpty(_loadedLanguage))
                return false;

            return string.Equals(_loadedLanguage, language, StringComparison.OrdinalIgnoreCase);
        }
    }
}
