using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
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
            this.ExtendsContentIntoTitleBar = true; // �i��G�����D�C�Y�D�D
            this.AppWindow.Title = "Steam Games (WinUI 3)";

            // �j�w DataContext
            // Set DataContext for binding
            RootGrid.DataContext = this;

            // Demo �w�]��ơ]�i�����^
            SeedDemoRows();
        }
        private void SeedDemoRows()
        {
            GameItems.Add(new GameEntry
            {
                AppId = 570,
                IconUri = "ms-appx:///Assets/steam_placeholder.png", // �Ч�ϩ�b�M�� Assets
                NameEn = "Dota 2",
                NameLocalized = "Dota 2" // Demo�G�ȮɦP�^��
            });

            GameItems.Add(new GameEntry
            {
                AppId = 730,
                IconUri = "ms-appx:///Assets/steam_placeholder.png",
                NameEn = "Counter-Strike 2",
                NameLocalized = "Counter-Strike 2"
            });
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

                // Use real Steam API service
                steamService = new SteamApiService(apiKey);
                var steamGames = await steamService.GetOwnedGamesAsync(steamId64, progress);

                // Convert to GameEntry format
                foreach (var game in steamGames)
                {
                    GameItems.Add(new GameEntry
                    {
                        AppId = game.AppId,
                        IconUri = game.IconUrl,
                        NameEn = game.NameEn,
                        NameLocalized = game.NameLocalized
                    });
                }

                StatusText = $"Successfully loaded {GameItems.Count} games";
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
            // �i��G���ѫ�ĳ�M��]AutoSuggest�^�F���B�ٲ�
        }
    }

    public class GameEntry
    {
        public int AppId { get; set; }
        public string IconUri { get; set; } = "";
        public string NameEn { get; set; } = "";
        public string NameLocalized { get; set; } = "";
    }
}

