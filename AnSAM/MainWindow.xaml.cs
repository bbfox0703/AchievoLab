using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.IO;
using System.Net.Http;
using AnSAM.Services;
using AnSAM.Steam;

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

        public MainWindow(SteamClient steamClient)
        {
            _steamClient = steamClient;
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

            foreach (var (id, _) in GameListService.GameTypes)
            {
                uint appId = (uint)id;
                if (_steamClient.IsSubscribedApp(appId))
                {
                    var title = _steamClient.GetAppData(appId, "name") ?? $"App {appId}";
                    var data = new SteamAppData(id, title);
                    _allGames.Add(GameItem.FromSteamApp(data));
                }
            }

            FilterGames(null);
            StatusText.Text = $"Loaded {_allGames.Count} games";
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
                var matches = string.IsNullOrWhiteSpace(keyword)
                    ? new List<string>()
                    : Games.Where(g => g.Title.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                           .Select(g => g.Title)
                           .Distinct()
                           .Take(10)
                           .ToList();
                sender.ItemsSource = matches;
            }
        }

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

    public class GameItem : System.ComponentModel.INotifyPropertyChanged
    {
        public string Title { get; set; }
        public int ID { get; set; }
        public string? CoverPath
        {
            get => _coverPath;
            set
            {
                if (_coverPath != value)
                {
                    _coverPath = value;
                    PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(CoverPath)));
                }
            }
        }

        private string? _coverPath;

        public string? ExePath { get; set; }
        public string? Arguments { get; set; }
        public string? UriScheme { get; set; }

        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

        public GameItem(string title,
                         int id,
                         string? coverPath = null,
                         string? exePath = null,
                         string? arguments = null,
                         string? uriScheme = null)
        {
            Title = title;
            ID = id;
            CoverPath = coverPath;
            ExePath = exePath;
            Arguments = arguments;
            UriScheme = uriScheme;
        }

        public static GameItem FromSteamApp(SteamAppData app)
        {
            var item = new GameItem(app.Title,
                                    app.AppId,
                                    null,
                                    app.ExePath,
                                    app.Arguments,
                                    app.UriScheme);

            if (!string.IsNullOrEmpty(app.CoverUrl) && Uri.TryCreate(app.CoverUrl, UriKind.Absolute, out var uri))
            {
                _ = IconCache.GetIconPathAsync(app.AppId, uri).ContinueWith(t =>
                {
                    if (t.Status == TaskStatus.RanToCompletion)
                    {
                        item.CoverPath = t.Result;
                    }
                });
            }

            return item;
        }
    }
}
