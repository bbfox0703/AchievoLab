using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Navigation;
using MyOwnGames.Services;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using Windows.Foundation;
using Windows.Foundation.Collections;
using Windows.UI;
using Windows.UI.ViewManagement;
using WinRT.Interop;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace MyOwnGames
{
    /// <summary>
    /// An empty window that can be used on its own or navigated to within a Frame.
    /// </summary>
    public sealed partial class MainWindow : Window, INotifyPropertyChanged
    {
        public ObservableCollection<GameEntry> GameItems { get; } = new();
        public ObservableCollection<string> LogEntries { get; } = new();
        private readonly GameImageService _imageService = new();
        private readonly GameDataService _dataService = new();
        private readonly Action<string> _logHandler;
        private SteamApiService? _steamService;
        private bool _isShuttingDown;

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
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        public void AppendLog(string message)
        {
            var entry = $"[{DateTime.Now:HH:mm:ss}] {message}";
            LogEntries.Add(entry);
            if (LogEntries.Count > 0)
            {
                LogList.UpdateLayout();
                LogList.ScrollIntoView(LogEntries[LogEntries.Count - 1]);
            }
        }
        public MainWindow()
        {
            InitializeComponent();
            this.ExtendsContentIntoTitleBar = true;
            this.AppWindow.Title = "My Own Steam Games";

            _logHandler = message => DispatcherQueue.TryEnqueue(() => AppendLog(message));
            DebugLogger.OnLog += _logHandler;

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

            // Load saved games on startup
            _ = LoadSavedGamesAsync();
        }
        private async Task LoadSavedGamesAsync()
        {
            try
            {
                AppendLog("Loading saved games...");
                StatusText = "Loading saved games...";
                var enteredId = SteamIdBox.Password?.Trim() ?? string.Empty;
                await EnsureSteamIdHashConsistencyAsync(enteredId);
                var savedGames = await _dataService.LoadGamesFromXmlAsync();
                
                GameItems.Clear();
                foreach (var game in savedGames.Take(10)) // Limit to first 10 for demo
                {
                    var entry = new GameEntry
                    {
                        AppId = game.AppId,
                        NameEn = game.NameEn,
                        NameLocalized = game.NameLocalized,
                        IconUri = "ms-appx:///Assets/steam_placeholder.png" // Will be updated async
                    };
                    
                    GameItems.Add(entry);
                    
                    // Load image asynchronously in a thread-safe way
                    _ = LoadGameImageAsync(entry, game.AppId);
                }
                
                var exportInfo = await _dataService.GetExportInfoAsync();
                if (exportInfo != null)
                {
                    StatusText = $"Loaded {savedGames.Count} saved games (exported: {exportInfo.ExportDate:yyyy-MM-dd}, language: {exportInfo.Language})";
                }
                else
                {
                    StatusText = "Ready. Enter Steam API Key and SteamID to fetch your games.";
                }

                AppendLog($"Loaded {savedGames.Count} saved games from {_dataService.GetXmlFilePath()}");
            }
            catch (Exception ex)
            {
                StatusText = $"Error loading saved games: {ex.Message}";
                AppendLog($"Error loading saved games: {ex.Message}");
            }
        }

        private async Task LoadGameImageAsync(GameEntry entry, int appId)
        {
            try
            {
                var imagePath = await _imageService.GetGameImageAsync(appId);
                if (!string.IsNullOrEmpty(imagePath) && File.Exists(imagePath))
                {
                    // Thread-safe UI update using DispatcherQueue
                    this.DispatcherQueue.TryEnqueue(() =>
                    {
                        entry.IconUri = imagePath;
                    });
                    AppendLog($"Loaded image for {appId}");
                }
                else
                {
                    AppendLog($"Image not found for {appId}");
                }
            }
            catch (Exception ex)
            {
                DebugLogger.LogDebug($"Error loading image for {appId}: {ex.Message}");
                AppendLog($"Error loading image for {appId}: {ex.Message}");
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
                    AppendLog("SteamID changed, cleared previous data and image cache.");
                }
            }
        }

        private async void GetGamesButton_Click(object sender, RoutedEventArgs e)
        {
            var apiKey = ApiKeyBox.Password?.Trim();
            var steamId64 = SteamIdBox.Password?.Trim();

            if (string.IsNullOrEmpty(apiKey) || string.IsNullOrEmpty(steamId64))
            {
                StatusText = "Please enter Steam API Key and SteamID_64.";
                return;
            }

            await EnsureSteamIdHashConsistencyAsync(steamId64);

            string? xmlPath = null;
            try
            {
                AppendLog("Starting game retrieval...");
                IsLoading = true;
                StatusText = "Fetching game list from Steam Web API...";
                ProgressValue = 0;

                GameItems.Clear();

                // Create progress reporter
                var progress = new Progress<double>(value => 
                {
                    ProgressValue = value;
                });

                // Get selected language from ComboBox
                var selectedLanguage = "tchinese"; // Default
                if (LanguageComboBox.SelectedItem is ComboBoxItem selectedItem)
                {
                    selectedLanguage = selectedItem.Content?.ToString() ?? "tchinese";
                }

                // Load existing app IDs to avoid re-fetching
                var (existingAppIds, _) = await _dataService.LoadRetrievedAppIdsAsync();

                // Use real Steam API service with selected language
                _steamService = new SteamApiService(apiKey);
                var total = await _steamService.GetOwnedGamesAsync(steamId64, selectedLanguage, async game =>
                {
                    var entry = new GameEntry
                    {
                        AppId = game.AppId,
                        NameEn = game.NameEn,
                        NameLocalized = game.NameLocalized,
                        IconUri = "ms-appx:///Assets/steam_placeholder.png" // Will be updated async
                    };

                    GameItems.Add(entry);

                    // Load image asynchronously in a thread-safe way
                    _ = LoadGameImageAsync(entry, game.AppId);

                    await _dataService.AppendGameAsync(game, steamId64, apiKey, selectedLanguage);
                }, progress, existingAppIds);

                xmlPath = _dataService.GetXmlFilePath();

                var savedCount = (existingAppIds?.Count ?? 0) + GameItems.Count;
                if (savedCount < total)
                {
                    await _dataService.UpdateRemainingCountAsync(total - savedCount);
                }
                else
                {
                    await _dataService.UpdateRemainingCountAsync(0);
                }

                StatusText = $"Successfully loaded {GameItems.Count} games ({selectedLanguage}) and saved to {xmlPath}";
                AppendLog($"Retrieved {GameItems.Count} games and saved to {xmlPath}");
            }
            catch (Exception ex)
            {
                StatusText = "Error: " + ex.Message;
                AppendLog($"Error retrieving games: {ex.Message}");
            }
            finally
            {
                _steamService?.Dispose();
                _steamService = null;
                IsLoading = false;
                ProgressValue = 100;
                AppendLog("Finished retrieving games.");
            }
        }

        private void KeywordBox_QuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
        {
            var keyword = args.QueryText?.Trim();
            if (string.IsNullOrEmpty(keyword))
            {
                StatusText = "Please enter search keywords.";
                return;
            }

            // Here you can use keyword for client-side filtering, or call the search API again
            StatusText = $"Searching for: {keyword}";
            // TODO: Filter GameItems or perform server-side search
        }

        private void KeywordBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
        {
            // Real-time search functionality could be implemented here
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
            _imageService.Dispose();
            DebugLogger.OnLog -= _logHandler;
            DebugLogger.LogDebug($"Shutdown completed ({reason})");
        }
    }

    public class GameEntry : INotifyPropertyChanged
    {
        public int AppId { get; set; }
        
        private string _iconUri = "";
        public string IconUri 
        { 
            get => _iconUri; 
            set 
            { 
                if (_iconUri != value) 
                { 
                    _iconUri = value; 
                    OnPropertyChanged(); 
                } 
            } 
        }
        
        public string NameEn { get; set; } = "";
        public string NameLocalized { get; set; } = "";

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

