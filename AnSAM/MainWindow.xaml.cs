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
using System.Linq;
using System.Threading.Tasks;
using System.IO;
using System.Net.Http;
using AnSAM.Services;

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

        public MainWindow()
        {
            InitializeComponent();
        }

        private async void GameCard_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
        {
            if (sender is FrameworkElement element && element.DataContext is GameItem game)
            {
                StatusText.Text = $"Launching {game.Title}...";
                StatusProgress.IsIndeterminate = true;
                StatusExtra.Text = string.Empty;
                await GameLauncher.LaunchAsync(game);
                StatusProgress.IsIndeterminate = false;
                StatusText.Text = "Ready";
            }
        }

        private async void OnRefreshClicked(object sender, RoutedEventArgs e)
        {
            StatusText.Text = "Refresh";
            StatusProgress.Value = 0;
            StatusExtra.Text = "0%";

            Games.Clear();
            _allGames.Clear();

            void OnStatus(string msg) => StatusText.Text = msg;
            void OnProgress(double p)
            {
                StatusProgress.Value = p;
                StatusExtra.Text = $"{p:0}%";
            }

            GameListService.StatusChanged += OnStatus;
            GameListService.ProgressChanged += OnProgress;

            using var http = new HttpClient();
            var cacheDir = Path.Combine(AppContext.BaseDirectory, "cache");

            try
            {
                await GameListService.LoadAsync(cacheDir, http);
            }
            finally
            {
                GameListService.StatusChanged -= OnStatus;
                GameListService.ProgressChanged -= OnProgress;
            }

            StatusText.Text = $"Loaded {GameListService.GameTypes.Count} entries";
        }

        //private void OnSearchClicked(object sender, RoutedEventArgs e)
        //{
        //    StatusText.Text = "Filter..";
        //}

        private void SearchBox_QuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
        {
            var keyword = args.QueryText?.Trim();
            FilterGames(keyword);
        }

        private void SearchBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
        {
            if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput)
            {
                var keyword = sender.Text.Trim();
                var suggestions = string.IsNullOrEmpty(keyword)
                    ? new List<string>()
                    : _allGames.Where(g => g.Title.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                               .Select(g => g.Title)
                               .Distinct()
                               .Take(10)
                               .ToList();
                sender.ItemsSource = suggestions;
            }
        }

        private void SearchBox_SuggestionChosen(AutoSuggestBox sender, AutoSuggestBoxSuggestionChosenEventArgs args)
        {
            if (args.SelectedItem is string title)
            {
                FilterGames(title);
            }
        }

        private void FilterGames(string? keyword)
        {
            IEnumerable<GameItem> filtered = string.IsNullOrWhiteSpace(keyword)
                ? _allGames
                : _allGames.Where(g => g.Title.Contains(keyword, StringComparison.OrdinalIgnoreCase));

            Games.Clear();
            foreach (var game in filtered)
            {
                Games.Add(game);
            }

            StatusText.Text = $"Showing {Games.Count} of {_allGames.Count} games";
            StatusProgress.Value = 0;
            StatusExtra.Text = $"{Games.Count}/{_allGames.Count}";
        }

    }

    public class GameItem
    {
        public string Title { get; set; }
        public int ID { get; set; }
        public Uri? CoverUri { get; set; }
        public BitmapIcon? CoverIcon { get; set; }

        public string? ExePath { get; set; }
        public string? Arguments { get; set; }
        public string? UriScheme { get; set; }

        public GameItem(string title,
                         int id,
                         Uri? coverUri = null,
                         string? exePath = null,
                         string? arguments = null,
                         string? uriScheme = null)
        {
            Title = title;
            ID = id;
            CoverUri = coverUri;
            ExePath = exePath;
            Arguments = arguments;
            UriScheme = uriScheme;

            if (coverUri != null)
            {
                CoverIcon = new BitmapIcon { UriSource = coverUri };
            }
        }

        public static GameItem FromSteamApp(SteamAppData app)
        {
            Uri? cover = string.IsNullOrEmpty(app.CoverUrl) ? null : new Uri(app.CoverUrl, UriKind.RelativeOrAbsolute);
            return new GameItem(app.Title,
                                app.AppId,
                                cover,
                                app.ExePath,
                                app.Arguments,
                                app.UriScheme);
        }
    }
}
