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
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Foundation;
using Windows.Foundation.Collections;
using MyOwnGames.Services;

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
        private readonly GameImageService _imageService = new();
        private readonly GameDataService _dataService = new();

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
        public MainWindow()
        {
            InitializeComponent();
            this.ExtendsContentIntoTitleBar = true;
            this.AppWindow.Title = "My Own Steam Games";

            // Set DataContext for binding
            RootGrid.DataContext = this;

            // Load saved games on startup
            _ = LoadSavedGamesAsync();
        }
        private async Task LoadSavedGamesAsync()
        {
            try
            {
                StatusText = "Loading saved games...";
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
            }
            catch (Exception ex)
            {
                StatusText = $"Error loading saved games: {ex.Message}";
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
                }
            }
            catch (Exception ex)
            {
                DebugLogger.LogDebug($"Error loading image for {appId}: {ex.Message}");
            }
        }

        private async void GetGamesButton_Click(object sender, RoutedEventArgs e)
        {
            var apiKey = ApiKeyBox.Text?.Trim();
            var steamId64 = SteamIdBox.Text?.Trim();

            if (string.IsNullOrEmpty(apiKey) || string.IsNullOrEmpty(steamId64))
            {
                StatusText = "Please enter Steam API Key and SteamID_64.";
                return;
            }

            SteamApiService? steamService = null;
            try
            {
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

                // Use real Steam API service with selected language
                steamService = new SteamApiService(apiKey);
                var steamGames = await steamService.GetOwnedGamesAsync(steamId64, selectedLanguage, progress);

                // Convert to GameEntry format and load images asynchronously
                foreach (var game in steamGames)
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

                // Save to XML for AnSAM usage
                await _dataService.SaveGamesToXmlAsync(steamGames, steamId64, apiKey, selectedLanguage);
                var xmlPath = _dataService.GetXmlFilePath();

                StatusText = $"Successfully loaded {GameItems.Count} games ({selectedLanguage}) and saved to {xmlPath}";
            }
            catch (Exception ex)
            {
                StatusText = "Error: " + ex.Message;
            }
            finally
            {
                steamService?.Dispose();
                IsLoading = false;
                ProgressValue = 100;
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

        // Clean up resources when window is closing
        ~MainWindow()
        {
            _imageService?.Dispose();
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

