using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Navigation;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Tasks;
using Windows.Foundation;
using Windows.Foundation.Collections;
using Windows.UI;
using Windows.UI.ViewManagement;
using WinRT.Interop;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.
using CommonUtilities;
using MyOwnGames.Services;

namespace MyOwnGames
{
    /// <summary>
    /// An empty window that can be used on its own or navigated to within a Frame.
    /// </summary>
    public sealed partial class MainWindow : Window, INotifyPropertyChanged
    {
        public ObservableCollection<GameEntry> GameItems { get; } = new();
        private List<GameEntry> AllGameItems { get; } = new();
        public ObservableCollection<string> LogEntries { get; } = new();
        private readonly HttpClient _imageHttpClient = new();
        private readonly SharedImageService _imageService;
        private readonly GameDataService _dataService = new();
        private readonly Action<string> _logHandler;
        private SteamApiService? _steamService;
        private bool _isShuttingDown = false;
        private CancellationTokenSource? _cancellationTokenSource;
        private ScrollViewer? _gamesScrollViewer;
        private readonly HashSet<string> _imagesCurrentlyLoading = new();
        private readonly HashSet<string> _imagesSuccessfullyLoaded = new();
        private readonly object _imageLoadingLock = new();
        private readonly Dictionary<string, DateTime> _duplicateImageLogTimes = new();

        private readonly string _detectedLanguage = GetDefaultLanguage();
        private readonly string _defaultLanguage = "english"; // Always default to English for selection

        private readonly AppWindow _appWindow;

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
                    // Toggle progress visibility
                    ProgressVisibility = value ? Visibility.Visible : Visibility.Collapsed;
                    if (value) ProgressPercentText = ""; // when indeterminate
                }
            }
        }

        private double _progressValue;
        public double ProgressValue
        {
            get => _progressValue;
            set { if (Math.Abs(_progressValue - value) > double.Epsilon) { _progressValue = value; OnPropertyChanged(); ProgressPercentText = $"{(int)_progressValue}%"; } }
        }

        private Visibility _progressVisibility = Visibility.Collapsed;
        public Visibility ProgressVisibility
        {
            get => _progressVisibility;
            set { if (_progressVisibility != value) { _progressVisibility = value; OnPropertyChanged(); } }
        }

        private string _progressPercentText = "";
        public string ProgressPercentText
        {
            get => _progressPercentText;
            set { if (_progressPercentText != value) { _progressPercentText = value; OnPropertyChanged(); } }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? name = null)
        {
            try
            {
                if (!_isShuttingDown)
                {
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
                }
            }
            catch (System.Runtime.InteropServices.COMException)
            {
                // Ignore COM exceptions during shutdown
            }
            catch (Exception ex) when (_isShuttingDown)
            {
                // Ignore all exceptions during shutdown
                DebugLogger.LogDebug($"Ignored exception during shutdown in OnPropertyChanged: {ex.Message}");
            }
        }

        public void AppendLog(string message)
        {
            if (_isShuttingDown) return; // Don't add logs during shutdown

            try
            {
                var entry = $"[{DateTime.Now:HH:mm:ss}] {message}";

                void addEntry()
                {
                    if (_isShuttingDown) return;
                    LogEntries.Add(entry);

                    // Auto-scroll to bottom after a short delay to ensure UI is updated
                    DispatcherQueue?.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
                    {
                        if (_isShuttingDown) return; // Double check during async execution

                        try
                        {
                            LogScrollViewer?.ScrollToVerticalOffset(LogScrollViewer.ScrollableHeight);
                        }
                        catch (System.Runtime.InteropServices.COMException)
                        {
                            // Ignore COM exceptions during shutdown
                        }
                        catch
                        {
                            // Fallback: scroll ListView to last item
                            try
                            {
                                LogList?.ScrollIntoView(LogEntries[LogEntries.Count - 1]);
                            }
                            catch (System.Runtime.InteropServices.COMException)
                            {
                                // Ignore COM exceptions during shutdown
                            }
                            catch { }
                        }
                    });
                }

                if (DispatcherQueue?.HasThreadAccess == false)
                {
                    DispatcherQueue?.TryEnqueue(addEntry);
                }
                else
                {
                    addEntry();
                }
            }
            catch (Exception ex)
            {
                if (!_isShuttingDown)
                {
                    DebugLogger.LogDebug($"Exception in AppendLog: {ex.Message}");
                }
                // Ignore all exceptions during shutdown
            }
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
            // Skip reordering if detected language is already "english" (already first in XAML)
            if (_detectedLanguage == "english")
                return;

            // Find the detected language item
            var detectedItem = LanguageComboBox.Items
                .OfType<ComboBoxItem>()
                .FirstOrDefault(i => string.Equals(i.Content?.ToString(), _detectedLanguage, StringComparison.OrdinalIgnoreCase));

            if (detectedItem != null)
            {
                // Remove it from current position
                LanguageComboBox.Items.Remove(detectedItem);
                // Insert at the beginning (position 0)
                LanguageComboBox.Items.Insert(0, detectedItem);
                AppendLog($"Moved detected language '{_detectedLanguage}' to first position, but defaulting to English");
            }
        }
        public MainWindow()
        {
            _imageService = new SharedImageService(_imageHttpClient);
            InitializeComponent();
            this.ExtendsContentIntoTitleBar = true;
            this.AppWindow.Title = "My Own Steam Games";

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

            _logHandler = msg =>
            {
                var queue = DispatcherQueue;
                queue?.TryEnqueue(() => AppendLog(msg));
            };
            DebugLogger.OnLog += _logHandler;
            
            // Subscribe to image download completion events
            _imageService.ImageDownloadCompleted += OnImageDownloadCompleted;

            // Initialize image service with default language
            var initialLanguage = GetCurrentLanguage();
            _imageService.SetLanguage(initialLanguage).GetAwaiter().GetResult();
            AppendLog($"Initialized with language: {initialLanguage}");

            // Subscribe to scroll events for on-demand image loading - defer until after UI is loaded
            _ = Task.Delay(1000).ContinueWith(_ => this.DispatcherQueue.TryEnqueue(() => SetupScrollEvents()));

            // 取得 AppWindow
            var hwnd = WindowNative.GetWindowHandle(this);
            var winId = Win32Interop.GetWindowIdFromWindow(hwnd);
            _appWindow = AppWindow.GetFromWindowId(winId);
            _appWindow.Closing += OnAppWindowClosing;
            // 設定 Icon：指向打包後的實體檔案路徑
            var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "MyOwnGames.ico");
            if (File.Exists(iconPath))
                _appWindow.SetIcon(iconPath);


            // Set DataContext for binding
            RootGrid.DataContext = this;
            RootGrid.KeyDown += OnWindowKeyDown;

            // Load saved games after window is displayed
            RootGrid.Loaded += MainWindow_Loaded;

            // Clean up old failed download records on startup
            _ = CleanupOldFailedRecordsAsync();

            UpdateGetGamesButtonState();
        }

        private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            RootGrid.Loaded -= MainWindow_Loaded;
            await LoadSavedGamesAsync();
        }

        private void OnImageDownloadCompleted(int appId, string? imagePath)
        {
            if (_isShuttingDown) return; // Don't update UI during shutdown
            
            // Find the corresponding game entry and update its image
            this.DispatcherQueue?.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                if (_isShuttingDown) return; // Double check during async execution
                
                try
                {
                    var gameEntry = AllGameItems.FirstOrDefault(g => g.AppId == appId);
                    if (gameEntry != null && !string.IsNullOrEmpty(imagePath) && File.Exists(imagePath))
                    {
                        // Check if the current IconUri is already pointing to this image
                        var fileUri = new Uri(imagePath).AbsoluteUri;
                        if (gameEntry.IconUri == fileUri)
                        {
                            // Already updated, no need to update again
                            return;
                        }

                        // Only update if different
                        gameEntry.IconUri = fileUri;
                        
                        DebugLogger.LogDebug($"Updated UI for downloaded image {appId}: {fileUri}");
                        AppendLog($"Image updated for {appId}");
                    }
                }
                catch (System.Runtime.InteropServices.COMException) when (_isShuttingDown)
                {
                    // Ignore COM exceptions during shutdown
                }
                catch (System.Runtime.InteropServices.COMException comEx)
                {
                    // COM exception during normal operation - try to handle gracefully
                    DebugLogger.LogDebug($"COM exception updating UI for {appId}: {comEx.Message}");
                }
                catch (ObjectDisposedException) when (_isShuttingDown)
                {
                    // Ignore object disposed exceptions during shutdown
                }
                catch (Exception) when (_isShuttingDown)
                {
                    // Ignore all exceptions during shutdown
                }
                catch (Exception ex)
                {
                    DebugLogger.LogDebug($"Error updating UI for downloaded image {appId}: {ex.Message}");
                }
            });
        }

        private async Task CleanupOldFailedRecordsAsync()
        {
            try
            {
                await Task.Run(() =>
                {
                    var imageFailureService = new CommonUtilities.ImageFailureTrackingService();
                    // Cleanup happens automatically when the service is created
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
                var enteredId = SteamIdBox.Password?.Trim() ?? string.Empty;
                await EnsureSteamIdHashConsistencyAsync(enteredId);

                // Load games with multi-language support
                var savedGamesWithLanguages = await _dataService.LoadGamesWithLanguagesAsync();
                var currentLanguage = GetCurrentLanguage();

                // Update image service language
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
                            IconUri = "ms-appx:///Assets/no_icon.png" // Will be updated async
                        };

                        DispatcherQueue?.TryEnqueue(() =>
                        {
                            GameItems.Add(entry);
                            AllGameItems.Add(entry);
                        });

                        // Load image asynchronously in a thread-safe way with language
                        _ = LoadGameImageAsync(entry, game.AppId, currentLanguage);
                    }
                });

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

        private async Task LoadGameImageAsync(GameEntry entry, int appId, string? language = null, bool forceImmediate = false)
        {
            if (_isShuttingDown) return; // Don't start new image loads during shutdown

            language ??= _imageService.GetCurrentLanguage();
            var key = $"{appId}_{language}";

            // Prevent multiple simultaneous image loads for the same game, but allow retries for failed loads
            lock (_imageLoadingLock)
            {
                if (_imagesCurrentlyLoading.Contains(key))
                {
                    if (!_duplicateImageLogTimes.TryGetValue(key, out var lastLog) || (DateTime.Now - lastLog).TotalSeconds > 30)
                    {
                        DebugLogger.LogDebug($"Image load already in progress for {appId}, skipping duplicate request");
                        _duplicateImageLogTimes[key] = DateTime.Now;
                    }
                    return;
                }
                if (_imagesSuccessfullyLoaded.Contains(key))
                {
                    // Image already loaded successfully, no need to load again
                    return;
                }
                _imagesCurrentlyLoading.Add(key);
            }

            try
            {
                // Check if image is already cached before async call
                bool isCached = _imageService.IsImageCached(appId, language);
                
                // If forceImmediate is true and image is cached, process immediately
                if (forceImmediate && isCached)
                {
                    // Get cached path synchronously if possible, or use async
                    var cachedPath = GetCachedImagePath(appId, language);
                    if (!string.IsNullOrEmpty(cachedPath) && File.Exists(cachedPath))
                    {
                        // Update UI immediately for cached images during Get Game List
                        UpdateImageUI(entry, appId, cachedPath, true);
                        
                        // Mark as successfully loaded
                        lock (_imageLoadingLock)
                        {
                            _imagesSuccessfullyLoaded.Add(key);
                        }
                        return;
                    }
                }
                
                var imagePath = await _imageService.GetGameImageAsync(appId, language);
                
                if (!string.IsNullOrEmpty(imagePath) && File.Exists(imagePath))
                {
                    DebugLogger.LogDebug($"Image found for {appId}: {imagePath} (cached: {isCached})");
                    
                    // Mark as successfully loaded
                    lock (_imageLoadingLock)
                    {
                        _imagesSuccessfullyLoaded.Add(key);
                    }
                    
                    // Update UI using helper method
                    UpdateImageUI(entry, appId, imagePath, isCached);
                    
                    if (!isCached)
                    {
                        AppendLog($"Downloaded image for {appId}");
                    }
                }
                else
                {
                    DebugLogger.LogDebug($"Image not found for {appId}");
                    AppendLog($"Image not found for {appId}");
                }
            }
            catch (Exception ex)
            {
                DebugLogger.LogDebug($"Error loading image for {appId}: {ex.Message}");
                AppendLog($"Error loading image for {appId}: {ex.Message}");
            }
            finally
            {
                // Always remove from tracking dictionaries when done
                lock (_imageLoadingLock)
                {
                    _imagesCurrentlyLoading.Remove(key);
                    _duplicateImageLogTimes.Remove(key);
                }
            }
        }

        private string? GetCachedImagePath(int appId, string language)
        {
            // Try to get cached image path without async operation
            try
            {
                // Use the GameImageCache TryGetCachedPath method through SharedImageService
                var baseDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AchievoLab", "ImageCache");
                var cache = new GameImageCache(baseDir);
                var cachedPath = cache.TryGetCachedPath(appId.ToString(), language, checkEnglishFallback: false);
                cache?.Dispose();
                return cachedPath;
            }
            catch
            {
                return null;
            }
        }

        private void UpdateImageUI(GameEntry entry, int appId, string imagePath, bool isCached)
        {
            // For cached images, use higher priority to show immediately
            var priority = isCached ? Microsoft.UI.Dispatching.DispatcherQueuePriority.High : 
                                     Microsoft.UI.Dispatching.DispatcherQueuePriority.Normal;
            
            // Thread-safe UI update with additional safety checks
            if (!_isShuttingDown && this.DispatcherQueue != null)
            {
                this.DispatcherQueue.TryEnqueue(priority, () =>
                {
                    if (_isShuttingDown) return; // Double check during async execution
                    
                    try
                    {
                        // Additional safety: check if entry is still valid
                        if (entry != null && AllGameItems.Contains(entry))
                        {
                            // Force a new URI to ensure UI refresh
                            var fileUri = new Uri(imagePath).AbsoluteUri;
                            
                            // Update with additional validation
                            if (!string.IsNullOrEmpty(fileUri))
                            {
                                entry.IconUri = fileUri;
                                DebugLogger.LogDebug($"Updated UI for {appId} to {fileUri} (cached: {isCached})");
                            }
                        }
                    }
                    catch (System.Runtime.InteropServices.COMException) when (_isShuttingDown)
                    {
                        // Ignore COM exceptions during shutdown
                    }
                    catch (ObjectDisposedException) when (_isShuttingDown)
                    {
                        // Ignore object disposed exceptions during shutdown
                    }
                    catch (Exception ex)
                    {
                        if (!_isShuttingDown)
                        {
                            DebugLogger.LogDebug($"Error updating UI for image {appId}: {ex.Message}");
                            // Fallback: try simple assignment
                            try
                            {
                                if (entry != null)
                                    entry.IconUri = imagePath;
                            }
                            catch
                            {
                                // If even fallback fails, just ignore
                            }
                        }
                    }
                });
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

        private async void GetGamesButton_Click(object sender, RoutedEventArgs e)
        {
            var apiKey = ApiKeyBox.Password?.Trim();
            var steamId64 = SteamIdBox.Password?.Trim();

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

            // Disable controls during operation
            SetControlsEnabledState(false);

            await EnsureSteamIdHashConsistencyAsync(steamId64!);

            string? xmlPath = null;
            
            // Create cancellation token source for this operation
            _cancellationTokenSource?.Cancel();
            _cancellationTokenSource?.Dispose();
            _cancellationTokenSource = new CancellationTokenSource();
            var cancellationToken = _cancellationTokenSource.Token;
            
            try
            {
                // Get selected language from ComboBox first
                var selectedLanguage = _defaultLanguage;
                if (LanguageComboBox.SelectedItem is ComboBoxItem selectedItem)
                {
                    selectedLanguage = selectedItem.Content?.ToString() ?? _defaultLanguage;
                }

                AppendLog($"Starting complete game scan for language: {selectedLanguage}...");
                IsLoading = true;
                StatusText = $"Scanning all games for {selectedLanguage} language data...";
                ProgressValue = 0;

                // Don't clear existing items - we'll update them with current language
                AppendLog($"Current games in list: {GameItems.Count}. Checking all games for {selectedLanguage} data.");

                // Create progress reporter
                var progress = new Progress<double>(value => 
                {
                    ProgressValue = value;
                });

                // Load existing games to check current language data status
                var existingGamesData = await _dataService.LoadGamesWithLanguagesAsync();
                var existingLocalizedNames = new Dictionary<int, string>();
                var skipAppIds = new HashSet<int>();
                var gamesMissingEnglishNames = new HashSet<int>();

                // Process existing games on a background thread to avoid UI blocking
                StatusText = $"Preparing existing game data ({existingGamesData.Count} entries)...";
                await Task.Run(() =>
                {
                    foreach (var game in existingGamesData)
                    {
                        // Check if game is missing English name
                        if (string.IsNullOrEmpty(game.NameEn))
                        {
                            gamesMissingEnglishNames.Add(game.AppId);
                            AppendLog($"Game {game.AppId} missing English name, will force English data update");
                        }

                        if (game.LocalizedNames != null && game.LocalizedNames.TryGetValue(selectedLanguage, out var name) &&
                            !string.IsNullOrEmpty(name))
                        {
                            existingLocalizedNames[game.AppId] = name;
                            // Always skip API call for games with existing localized names
                            skipAppIds.Add(game.AppId);
                        }
                    }
                }, cancellationToken);

                // If we're selecting non-English language but have games missing English names,
                // we need to force English data update first
                bool needsEnglishUpdate = selectedLanguage != "english" && 
                                        (existingGamesData.Count == 0 || gamesMissingEnglishNames.Count > 0);
                
                if (needsEnglishUpdate)
                {
                    AppendLog($"Found {gamesMissingEnglishNames.Count} games missing English names. Forcing English data update first...");
                    StatusText = "First updating English game names (required for localization)...";

                    // Force English update first
                    ArgumentNullException.ThrowIfNull(_steamService);
                    var englishTotal = await _steamService.GetOwnedGamesAsync(steamId64!, "english", async englishGame =>
                    {
                        // Always update English data for games missing it
                        if (gamesMissingEnglishNames.Contains(englishGame.AppId) || existingGamesData.Count == 0)
                        {
                            await _dataService.AppendGameAsync(englishGame, steamId64!, apiKey!, "english");
                            AppendLog($"Updated English data for {englishGame.AppId} - {englishGame.NameEn}");
                        }
                    }, progress, existingAppIds: null, existingLocalizedNames: null, cancellationToken);
                    
                    // Reload the games data after English update
                    existingGamesData = await _dataService.LoadGamesWithLanguagesAsync();
                    existingLocalizedNames.Clear();
                    gamesMissingEnglishNames.Clear();
                    
                    // Reprocess the updated data
                    await Task.Run(() =>
                    {
                        foreach (var game in existingGamesData)
                        {
                            if (game.LocalizedNames != null && game.LocalizedNames.TryGetValue(selectedLanguage, out var name) &&
                                !string.IsNullOrEmpty(name))
                            {
                                existingLocalizedNames[game.AppId] = name;
                                // Always skip API call for games with existing localized names (after English update)
                                skipAppIds.Add(game.AppId);
                            }
                        }
                    }, cancellationToken);
                }
                StatusText = selectedLanguage == "english"
                    ? "Scanning all games..."
                    : $"Scanning all games for {selectedLanguage} language data (this will be slower to avoid Steam API rate limits)...";

                // Use real Steam API service with selected language
                _steamService = new SteamApiService(apiKey!);
                var total = await _steamService.GetOwnedGamesAsync(steamId64!, selectedLanguage, async game =>
                {
                    var shouldSkip = skipAppIds.Contains(game.AppId);
                    // Check if this game has data for the current language
                    var existingGameData = existingGamesData.FirstOrDefault(g => g.AppId == game.AppId);
                    var hasLanguageData = existingGameData?.LocalizedNames?.ContainsKey(selectedLanguage) == true &&
                                         !string.IsNullOrEmpty(existingGameData.LocalizedNames[selectedLanguage]);
                    
                    // Always process the game to ensure XML consistency for current language
                    AppendLog($"Processing game {game.AppId} - {game.NameEn} ({selectedLanguage}){(hasLanguageData ? " [updating]" : " [new data]")}");

                    // Check if game already exists in the UI list
                    var existingEntry = AllGameItems.FirstOrDefault(g => g.AppId == game.AppId);

                    if (existingEntry != null)
                    {
                        // Update existing UI entry
                        existingEntry.NameEn = game.NameEn;
                        existingEntry.SetLocalizedName(selectedLanguage, game.NameLocalized);
                        existingEntry.CurrentLanguage = selectedLanguage; // Update display language
                        
                        AppendLog($"Updated UI entry: {game.AppId} - {game.NameEn}");
                    }
                    else
                    {
                        // Create new UI entry
                        var newEntry = new GameEntry
                        {
                            AppId = game.AppId,
                            NameEn = game.NameEn,
                            CurrentLanguage = selectedLanguage,
                            IconUri = "ms-appx:///Assets/no_icon.png" // Will be updated async
                        };
                        
                        // Set localized name for current language
                        newEntry.SetLocalizedName(selectedLanguage, game.NameLocalized);

                        GameItems.Add(newEntry);
                        AllGameItems.Add(newEntry);
                        AppendLog($"Added new game: {game.AppId} - {game.NameEn} ({selectedLanguage})");
                    }

                    if (!shouldSkip)
                    {
                        // Save/update game data in XML for current language
                        await _dataService.AppendGameAsync(game, steamId64!, apiKey!, selectedLanguage);
                    }
                }, progress, skipAppIds, existingLocalizedNames, cancellationToken);

                xmlPath = _dataService.GetXmlFilePath();
                
                // Process skipped games (those with existing localized data) to ensure UI and images are updated
                AppendLog($"Processing {skipAppIds.Count} games with existing {selectedLanguage} data...");
                foreach (var skippedAppId in skipAppIds)
                {
                    try
                    {
                        var existingGameData = existingGamesData.FirstOrDefault(g => g.AppId == skippedAppId);
                        if (existingGameData != null && existingGameData.LocalizedNames != null &&
                            existingGameData.LocalizedNames.TryGetValue(selectedLanguage, out var localizedName))
                        {
                            // Check if game already exists in the UI list
                            var existingEntry = AllGameItems.FirstOrDefault(g => g.AppId == skippedAppId);

                            if (existingEntry != null)
                            {
                                // Update existing UI entry for current language
                                existingEntry.SetLocalizedName(selectedLanguage, localizedName);
                                existingEntry.CurrentLanguage = selectedLanguage;
                                AppendLog($"Updated existing UI entry for game {skippedAppId} ({selectedLanguage})");
                            }
                            else
                            {
                                // Create new UI entry for skipped game
                                var newEntry = new GameEntry
                                {
                                    AppId = skippedAppId,
                                    NameEn = existingGameData.NameEn ?? skippedAppId.ToString(),
                                    CurrentLanguage = selectedLanguage,
                                    IconUri = "ms-appx:///Assets/no_icon.png"
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

                // Update remaining count based on actual total vs current items
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
                    // Ignore exceptions during shutdown
                    DebugLogger.LogDebug($"Ignored exception during shutdown: {ex.Message}");
                }
                
                IsLoading = false;
                ProgressValue = 100;
                
                // Re-enable controls after operation completes
                SetControlsEnabledState(true);
                
                if (!_isShuttingDown)
                {
                    AppendLog("Finished retrieving games.");
                }
            }
        }

        private void InputFields_PasswordChanged(object sender, RoutedEventArgs e)
        {
            UpdateGetGamesButtonState();
        }

        private void UpdateGetGamesButtonState()
        {
            var apiKey = ApiKeyBox.Password?.Trim();
            var steamId64 = SteamIdBox.Password?.Trim();
            GetGamesButton.IsEnabled = InputValidator.IsValidApiKey(apiKey) && InputValidator.IsValidSteamId64(steamId64);
        }

        private void SetControlsEnabledState(bool enabled)
        {
            ApiKeyBox.IsEnabled = enabled;
            SteamIdBox.IsEnabled = enabled;
            LanguageComboBox.IsEnabled = enabled;
            GetGamesButton.IsEnabled = enabled && InputValidator.IsValidApiKey(ApiKeyBox.Password?.Trim()) && InputValidator.IsValidSteamId64(SteamIdBox.Password?.Trim());
            StopButton.IsEnabled = !enabled;
        }

        private void StopButton_Click(object sender, RoutedEventArgs e)
        {
            // Cancel the current operation
            _cancellationTokenSource?.Cancel();
            StatusText = "Operation cancelled by user.";
            AppendLog("Get Game List operation cancelled by user.");
            
            // Re-enable controls
            SetControlsEnabledState(true);
            IsLoading = false;
        }

        private void KeywordBox_QuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
        {
            var keyword = args.QueryText?.Trim();
            FilterGameItems(keyword);
        }

        private void KeywordBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
        {
            if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput)
            {
                FilterGameItems(sender.Text?.Trim());
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

        private void OnWindowKeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key != Windows.System.VirtualKey.PageDown &&
                e.Key != Windows.System.VirtualKey.PageUp &&
                e.Key != Windows.System.VirtualKey.Down &&
                e.Key != Windows.System.VirtualKey.Up)
                return;

            var sv = _gamesScrollViewer ??= FindScrollViewer(GamesGridView);
            if (sv == null)
                return;

            double offset = sv.VerticalOffset;
            double delta = e.Key switch
            {
                Windows.System.VirtualKey.PageDown => sv.ViewportHeight,
                Windows.System.VirtualKey.PageUp => -sv.ViewportHeight,
                Windows.System.VirtualKey.Down => 100,
                Windows.System.VirtualKey.Up => -100,
                _ => 0
            };

            var target = Math.Max(0, Math.Min(offset + delta, sv.ScrollableHeight));
            sv.ChangeView(null, target, null);
            e.Handled = true;
        }

        private void GamesGridView_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
        {
            // Find the GameEntry from the double-tapped element
            if (e.OriginalSource is FrameworkElement element)
            {
                var gameEntry = FindGameEntryFromElement(element);
                if (gameEntry != null)
                {
                    // Launch RunGame with the selected game ID
                    LaunchRunGame(gameEntry.AppId);
                }
            }
        }

        private void GamesGridView_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs e)
        {
            if (e.InRecycleQueue)
                return;

            if (e.Phase == 0)
            {
                e.RegisterUpdateCallback(GamesGridView_ContainerContentChanging);
                e.Handled = true;
            }
            else if (e.Phase == 1 && e.Item is GameEntry entry)
            {
                var language = entry.CurrentLanguage ?? _imageService.GetCurrentLanguage();
                bool isCached = _imageService.IsImageCached(entry.AppId, language);
                _ = LoadGameImageAsync(entry, entry.AppId, language, forceImmediate: isCached);
            }
        }

        private GameEntry? FindGameEntryFromElement(FrameworkElement element)
        {
            // Walk up the visual tree to find the DataContext
            var current = element;
            while (current != null)
            {
                if (current.DataContext is GameEntry gameEntry)
                {
                    return gameEntry;
                }
                current = current.Parent as FrameworkElement;
            }
            return null;
        }

        private void LaunchRunGame(int appId)
        {
            try
            {
                // Try multiple possible paths for RunGame.exe
                var possiblePaths = new[]
                {
                    // Same directory as MyOwnGames
                    Path.Combine(
                        Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location) ?? "",
                        "RunGame.exe"),
                    
                    // Relative path from build output
                    Path.Combine(
                        Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location) ?? "",
                        "..", "RunGame", "RunGame.exe"),
                    
                    // Full relative path from development structure
                    Path.Combine(
                        Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location) ?? "",
                        "..", "..", "..", "..", "output", "Debug", "x64", "net8.0-windows10.0.22621.0", "RunGame", "RunGame.exe")
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
                DebugLogger.LogDebug($"Error launching RunGame: {ex.Message}");
            }
        }

        private async void OnAppWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
        {
            await SaveAndDisposeAsync("window closing");
        }

        public async Task SaveAndDisposeAsync(string reason)
        {
            if (_isShuttingDown)
                return;
            _isShuttingDown = true;
            DebugLogger.OnLog -= _logHandler;

            // Cancel any ongoing operations
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
            
            // Unsubscribe from events before disposing
            try
            {
                if (_imageService != null)
                {
                    _imageService.ImageDownloadCompleted -= OnImageDownloadCompleted;
                    _imageService.Dispose();
                }
                _imageHttpClient.Dispose();
            }
            catch (Exception ex)
            {
                DebugLogger.LogDebug($"Error disposing image service: {ex.Message}");
            }
            
            // Dispose cancellation token source
            try
            {
                _cancellationTokenSource?.Dispose();
                _cancellationTokenSource = null;
            }
            catch (Exception ex)
            {
                DebugLogger.LogDebug($"Error disposing cancellation token source: {ex.Message}");
            }

            DebugLogger.LogDebug($"Shutdown completed ({reason})");
        }

        private string GetCurrentLanguage()
        {
            if (LanguageComboBox.SelectedItem is ComboBoxItem selectedItem)
            {
                return selectedItem.Content?.ToString() ?? _defaultLanguage;
            }
            return _defaultLanguage;
        }

        private async void LanguageComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isLoading || LanguageComboBox.SelectedItem == null)
                return;

            var newLanguage = GetCurrentLanguage();
            var currentImageServiceLanguage = _imageService?.GetCurrentLanguage();

            AppendLog($"Language switching: UI={newLanguage}, ImageService={currentImageServiceLanguage}");

            if (newLanguage == currentImageServiceLanguage) // 避免重複切換同一語言
            {
                AppendLog($"Language switch skipped - already using {newLanguage}");
                return;
            }

            AppendLog($"Language changed from {currentImageServiceLanguage} to: {newLanguage}");
            StatusText = $"Switching to {newLanguage}...";

            try
            {
                // 設定為正在載入狀態，但不阻塞 UI
                _isLoading = true;

                // Update image service language first
                if (_imageService != null)
                {
                    await _imageService.SetLanguage(newLanguage);
                }

                // Clear tracking for previous language
                if (!string.IsNullOrEmpty(currentImageServiceLanguage))
                {
                    lock (_imageLoadingLock)
                    {
                        _imagesCurrentlyLoading.RemoveWhere(k => k.EndsWith($"_{currentImageServiceLanguage}", StringComparison.OrdinalIgnoreCase));
                        _imagesSuccessfullyLoaded.RemoveWhere(k => k.EndsWith($"_{currentImageServiceLanguage}", StringComparison.OrdinalIgnoreCase));
                        var toRemove = _duplicateImageLogTimes.Keys
                            .Where(k => k.EndsWith($"_{currentImageServiceLanguage}", StringComparison.OrdinalIgnoreCase))
                            .ToList();
                        foreach (var key in toRemove)
                        {
                            _duplicateImageLogTimes.Remove(key);
                        }
                    }
                }

                // 先快速更新語言顯示
                foreach (var gameEntry in AllGameItems)
                {
                    gameEntry.CurrentLanguage = newLanguage;
                }

                // 異步處理圖片刷新：優先可見圖片，隱藏圖片清空
                _ = Task.Run(async () =>
                {
                    await ProcessLanguageSwitchImageRefresh(newLanguage);
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
                // 1. 獲取當前可見的遊戲項目 (必須在 UI 線程執行)
                var (visibleItems, hiddenItems) = (new List<GameEntry>(), new List<GameEntry>());
                
                // 在 UI 線程獲取可見項目
                await Task.Run(() =>
                {
                    this.DispatcherQueue.TryEnqueue(() =>
                    {
                        var result = GetVisibleAndHiddenGameItems();
                        visibleItems.AddRange(result.visible);
                        hiddenItems.AddRange(result.hidden);
                    });
                });
                
                // 等待 UI 操作完成
                await Task.Delay(50);

                AppendLog($"Found {visibleItems.Count} visible games, {hiddenItems.Count} hidden games");

                // 2. 清空所有隱藏項目的圖片（設置為預設圖片）並移除成功載入記錄
                this.DispatcherQueue.TryEnqueue(() =>
                {
                    foreach (var hiddenItem in hiddenItems)
                    {
                        hiddenItem.IconUri = "ms-appx:///Assets/no_icon.png";
                        var key = $"{hiddenItem.AppId}_{newLanguage}";
                        lock (_imageLoadingLock)
                        {
                            _imagesSuccessfullyLoaded.Remove(key);
                        }
                    }
                });

                // 3. 優先載入可見項目的語系圖片
                await LoadVisibleItemsImages(visibleItems, newLanguage);

                this.DispatcherQueue.TryEnqueue(() =>
                {
                    StatusText = $"Language switched to {newLanguage}. Visible images loaded.";
                    AppendLog($"Loaded {visibleItems.Count} visible images for {newLanguage}");
                });
            }
            catch (Exception ex)
            {
                AppendLog($"Error processing language switch image refresh: {ex.Message}");
            }
        }

        private (List<GameEntry> visible, List<GameEntry> hidden) GetVisibleAndHiddenGameItems()
        {
            var visibleItems = new List<GameEntry>();
            var hiddenItems = new List<GameEntry>();

            if (GamesGridView?.ItemsPanelRoot is ItemsWrapGrid wrapGrid)
            {
                // 獲取 ScrollViewer
                var scrollViewer = FindScrollViewer(GamesGridView);
                if (scrollViewer != null)
                {
                    var viewportHeight = scrollViewer.ViewportHeight;
                    var verticalOffset = scrollViewer.VerticalOffset;

                    // 估算可見範圍內的項目
                    var itemHeight = 180; // tile height
                    var itemsPerRow = Math.Max(1, (int)(scrollViewer.ViewportWidth / 180));
                    var firstVisibleRow = Math.Max(0, (int)(verticalOffset / itemHeight));
                    var lastVisibleRow = (int)((verticalOffset + viewportHeight) / itemHeight) + 1;
                    
                    var firstVisibleIndex = firstVisibleRow * itemsPerRow;
                    var lastVisibleIndex = Math.Min(AllGameItems.Count - 1, (lastVisibleRow + 1) * itemsPerRow);

                    for (int i = 0; i < AllGameItems.Count; i++)
                    {
                        if (i >= firstVisibleIndex && i <= lastVisibleIndex)
                            visibleItems.Add(AllGameItems[i]);
                        else
                            hiddenItems.Add(AllGameItems[i]);
                    }
                }
                else
                {
                    // 如果無法獲取 ScrollViewer，假設前 20 個項目可見
                    for (int i = 0; i < AllGameItems.Count; i++)
                    {
                        if (i < 20)
                            visibleItems.Add(AllGameItems[i]);
                        else
                            hiddenItems.Add(AllGameItems[i]);
                    }
                }
            }
            else
            {
                // Fallback: 假設前 20 個項目可見
                for (int i = 0; i < AllGameItems.Count; i++)
                {
                    if (i < 20)
                        visibleItems.Add(AllGameItems[i]);
                    else
                        hiddenItems.Add(AllGameItems[i]);
                }
            }

            return (visibleItems, hiddenItems);
        }

        private async Task LoadVisibleItemsImages(List<GameEntry> visibleItems, string language)
        {
            // First, load all cached images immediately without delay
            var cachedItems = new List<GameEntry>();
            var nonCachedItems = new List<GameEntry>();
            
            foreach (var item in visibleItems)
            {
                if (_imageService.IsImageCached(item.AppId, language))
                {
                    cachedItems.Add(item);
                }
                else
                {
                    nonCachedItems.Add(item);
                }
            }
            
            // Load cached images immediately without batching or delay
            if (cachedItems.Count > 0)
            {
                var cachedTasks = cachedItems.Select(entry => LoadGameImageAsync(entry, entry.AppId, language));
                await Task.WhenAll(cachedTasks);
            }
            
            // Then batch process non-cached items with delay
            const int batchSize = 3;
            for (int i = 0; i < nonCachedItems.Count; i += batchSize)
            {
                var batch = nonCachedItems.Skip(i).Take(batchSize);
                var tasks = batch.Select(entry => LoadGameImageAsync(entry, entry.AppId, language));
                
                await Task.WhenAll(tasks);
                await Task.Delay(30); // 較短延遲，因為只處理可見項目
            }
        }

        private void SetupScrollEvents()
        {
            // 訂閱滾動事件以實現按需載入
            var scrollViewer = FindScrollViewer(GamesGridView);
            if (scrollViewer != null)
            {
                scrollViewer.ViewChanged += GamesGridView_ViewChanged;
            }
        }

        private void GamesGridView_ViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
        {
            if (sender is not ScrollViewer scrollViewer)
                return;

            try
            {
                // 獲取當前語言
                var currentLanguage = _imageService?.GetCurrentLanguage() ?? "english";

                // 找出新進入可見範圍的項目
                var visibleItems = GetCurrentlyVisibleItems(scrollViewer);

                // 只載入那些圖片為預設圖片（即之前被清空）的項目
                var itemsNeedingImages = visibleItems.Where(item =>
                    item.IconUri == "ms-appx:///Assets/no_icon.png").ToList();

                if (itemsNeedingImages.Any())
                {
                    // 小批次載入，避免影響滾動性能
                    bool skipDownloads = _isLoading;
                    _ = Task.Run(async () =>
                    {
                        await LoadOnDemandImages(itemsNeedingImages, currentLanguage, skipDownloads);
                    });
                }
            }
            catch (Exception ex)
            {
                DebugLogger.LogDebug($"Error in GamesGridView_ViewChanged: {ex.Message}");
            }
        }

        private List<GameEntry> GetCurrentlyVisibleItems(ScrollViewer scrollViewer)
        {
            var visibleItems = new List<GameEntry>();
            
            var viewportHeight = scrollViewer.ViewportHeight;
            var verticalOffset = scrollViewer.VerticalOffset;

            // 估算可見範圍內的項目
            var itemHeight = 180;
            var itemsPerRow = Math.Max(1, (int)(scrollViewer.ViewportWidth / 180));
            var firstVisibleRow = Math.Max(0, (int)(verticalOffset / itemHeight));
            var lastVisibleRow = (int)((verticalOffset + viewportHeight) / itemHeight) + 1;
            
            var firstVisibleIndex = firstVisibleRow * itemsPerRow;
            var lastVisibleIndex = Math.Min(AllGameItems.Count - 1, (lastVisibleRow + 1) * itemsPerRow);

            for (int i = firstVisibleIndex; i <= lastVisibleIndex && i < AllGameItems.Count; i++)
            {
                visibleItems.Add(AllGameItems[i]);
            }

            return visibleItems;
        }

        private async Task LoadOnDemandImages(List<GameEntry> items, string language, bool skipNetworkDownloads = false)
        {
            // First, load all cached images immediately without delay
            var cachedItems = new List<GameEntry>();
            var nonCachedItems = new List<GameEntry>();

            foreach (var item in items)
            {
                if (_imageService.IsImageCached(item.AppId, language))
                {
                    cachedItems.Add(item);
                }
                else
                {
                    nonCachedItems.Add(item);
                }
            }

            // Load cached images immediately without batching or delay
            if (cachedItems.Count > 0)
            {
                var cachedTasks = cachedItems.Select(entry => LoadGameImageAsync(entry, entry.AppId, language));
                await Task.WhenAll(cachedTasks);
            }

            if (skipNetworkDownloads)
                return;

            // Then batch process non-cached items with longer delay (background loading)
            const int batchSize = 2;
            for (int i = 0; i < nonCachedItems.Count; i += batchSize)
            {
                var batch = nonCachedItems.Skip(i).Take(batchSize);
                var tasks = batch.Select(entry => LoadGameImageAsync(entry, entry.AppId, language));

                await Task.WhenAll(tasks);
                await Task.Delay(100); // 較長延遲，避免影響滾動
            }
        }

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
    }

    public class GameEntry : INotifyPropertyChanged
    {
        public int AppId { get; set; }
        
        private string _iconUri = "";
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
                        OnPropertyChanged(); // This already triggers IconUri property change
                    } 
                }
                finally 
                {
                    _isUpdatingIcon = false;
                }
            } 
        }
        
        public string NameEn { get; set; } = "";
        
        // Multi-language support - store names by language code
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
        
        // Display name based on current language
        public string DisplayName
        {
            get
            {
                // Try to get localized name for current language
                if (!string.IsNullOrEmpty(CurrentLanguage) && 
                    LocalizedNames.TryGetValue(CurrentLanguage, out var localizedName) && 
                    !string.IsNullOrEmpty(localizedName))
                {
                    return localizedName;
                }
                
                // Fall back to English name
                return NameEn;
            }
        }
        
        // Legacy property for backward compatibility
        public string NameLocalized 
        { 
            get => DisplayName; 
            set 
            {
                // Store in current language
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
            catch (System.Runtime.InteropServices.COMException)
            {
                // Ignore COM exceptions during shutdown
            }
            catch (Exception ex)
            {
                DebugLogger.LogDebug($"Error in GameEntry.OnPropertyChanged: {ex.Message}");
            }
        }
        
        // Method to force UI refresh
        public void ForceIconRefresh()
        {
            try
            {
                OnPropertyChanged(nameof(IconUri));
            }
            catch (System.Runtime.InteropServices.COMException)
            {
                // Ignore COM exceptions during UI refresh
            }
            catch (ObjectDisposedException)
            {
                // Ignore object disposed exceptions
            }
        }
        
        // Method to update localized name for specific language
        public void SetLocalizedName(string language, string name)
        {
            LocalizedNames[language] = name;
            if (language == CurrentLanguage)
            {
                OnPropertyChanged(nameof(DisplayName));
                OnPropertyChanged(nameof(NameLocalized));
            }
        }
    }
}

