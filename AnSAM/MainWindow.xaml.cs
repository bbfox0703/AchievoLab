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
using System.Diagnostics;
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

        private bool _autoLoaded;

        public MainWindow(SteamClient steamClient)
        {
            _steamClient = steamClient;
            InitializeComponent();
            GameListService.StatusChanged += OnGameListStatusChanged;
            GameListService.ProgressChanged += OnGameListProgressChanged;
            IconCache.ProgressChanged += OnIconProgressChanged;
            Activated += OnWindowActivated;
        }

        private async void OnWindowActivated(object sender, WindowActivatedEventArgs args)
        {
            if (_autoLoaded) return;
            _autoLoaded = true;
            await RefreshAsync();
        }

        private void GameCard_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
        {
            if (sender is FrameworkElement element && element.DataContext is GameItem game)
            {
                StatusText.Text = $"Launching {game.Title}...";
                StatusProgress.IsIndeterminate = true;
                StatusExtra.Text = string.Empty;
                GameLauncher.Launch(game);
                StatusProgress.IsIndeterminate = false;
                StatusText.Text = "Ready";
            }
        }

        private async void OnRefreshClicked(object sender, RoutedEventArgs e)
        {
            await RefreshAsync();
        }

        private async Task RefreshAsync()
        {
            StatusText.Text = "Refresh";
            StatusProgress.Value = 0;
            StatusExtra.Text = "0%";

            Games.Clear();
            _allGames.Clear();
            IconCache.ResetProgress();

            using var http = new HttpClient();
            var baseDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AnSAM");
            var cacheDir = Path.Combine(baseDir, "cache");

            await GameListService.LoadAsync(cacheDir, http);

            bool steamReady = _steamClient.Initialized;
#if DEBUG
            Debug.WriteLine($"Steam API initialized: {steamReady}");
            Debug.WriteLine($"Game list loaded with {GameListService.Games.Count} entries");
            if (GameListService.Games.Count > 0)
            {
                var sampleIds = string.Join(", ", GameListService.Games.Take(20).Select(g => g.Id));
                Debug.WriteLine($"Sample parsed IDs: {sampleIds}{(GameListService.Games.Count > 20 ? ", ..." : string.Empty)}");
            }
#endif
            int total = 0, owned = 0;
            foreach (var game in GameListService.Games)
            {
                total++;
                uint appId = (uint)game.Id;
                string title = game.Name;
                string? coverUrl = null;
                if (steamReady)
                {
                    if (!_steamClient.IsSubscribedApp(appId))
                    {
                        continue;
                    }
                    owned++;
                    title = _steamClient.GetAppData(appId, "name") ?? title;
                    coverUrl = GameImageUrlResolver.GetGameImageUrl(
                        (id, key) => _steamClient.GetAppData(id, key),
                        appId,
                        "english");
                }
                var data = new SteamAppData(game.Id, title, coverUrl);
                _allGames.Add(GameItem.FromSteamApp(data));
            }
#if DEBUG
            Debug.WriteLine($"Processed {total} games; owned {owned}; added {_allGames.Count}");
#endif

            FilterGames(null);
            StatusText.Text = steamReady
                ? $"Loaded {_allGames.Count} games"
                : $"Steam unavailable - showing {_allGames.Count} games";
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

            var list = filtered.ToList();
#if DEBUG
            Debug.WriteLine($"FilterGames('{keyword}') -> {list.Count} items");
#endif
            Games.Clear();
            int logged = 0;
            foreach (var game in list)
            {
                Games.Add(game);
#if DEBUG
                if (logged < 20)
                {
                    Debug.WriteLine($"Added game icon: {game.ID} - {game.Title}");
                    logged++;
                }
#endif
            }

            StatusText.Text = $"Showing {Games.Count} of {_allGames.Count} games";
            StatusProgress.Value = 0;
            StatusExtra.Text = $"{Games.Count}/{_allGames.Count}";
#if DEBUG
            Debug.WriteLine($"Games collection now has {Games.Count} items displayed");
#endif
        }

        private void OnGameListStatusChanged(string message)
        {
            _ = DispatcherQueue.TryEnqueue(() => StatusText.Text = message);
        }

        private void OnGameListProgressChanged(double progress)
        {
            _ = DispatcherQueue.TryEnqueue(() =>
            {
                StatusProgress.Value = progress;
                StatusExtra.Text = $"{progress:0}%";
            });
        }

        private void OnIconProgressChanged(int completed, int total)
        {
            _ = DispatcherQueue.TryEnqueue(() =>
            {
                double p = total > 0 ? (double)completed / total * 100 : 0;
                StatusProgress.Value = p;
                StatusExtra.Text = $"{completed}/{total}";
                if (completed < total)
                {
                    StatusText.Text = "Downloading icons";
                }
                else if (total > 0)
                {
                    StatusText.Text = $"Loaded {_allGames.Count} games";
                }
            });
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
#if DEBUG
            Debug.WriteLine($"Creating GameItem for {app.AppId} - {app.Title}");
#endif
            var item = new GameItem(app.Title,
                                    app.AppId,
                                    null,
                                    app.ExePath,
                                    app.Arguments,
                                    app.UriScheme);

            if (!string.IsNullOrEmpty(app.CoverUrl) && Uri.TryCreate(app.CoverUrl, UriKind.Absolute, out var uri))
            {
#if DEBUG
                Debug.WriteLine($"Queueing icon download for {app.AppId} from {uri}");
#endif
                _ = IconCache.GetIconPathAsync(app.AppId, uri).ContinueWith(t =>
                {
                    if (t.Status == TaskStatus.RanToCompletion)
                    {
#if DEBUG
                        Debug.WriteLine($"Icon for {app.AppId} stored at {t.Result}");
#endif
                        item.CoverPath = t.Result;
                    }
#if DEBUG
                    else if (t.IsFaulted)
                    {
                        Debug.WriteLine($"Icon download failed for {app.AppId}: {t.Exception?.GetBaseException().Message}");
                        item.CoverPath = "ms-appx:///Assets/StoreLogo.png";
                    }
#endif
                });
            }
            else
            {
#if DEBUG
                Debug.WriteLine($"No icon URL for {app.AppId}");
#endif
                item.CoverPath = "ms-appx:///Assets/StoreLogo.png";
            }

            return item;
        }
    }
}
