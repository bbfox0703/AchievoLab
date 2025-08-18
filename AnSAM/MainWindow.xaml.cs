using Microsoft.UI.Dispatching;
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
using System.Text.Json;
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
            RefreshButton.IsEnabled = _steamClient.Initialized;
            if (!_steamClient.Initialized)
            {
                StatusText.Text = "Steam unavailable";
            }
            GameListService.StatusChanged += OnGameListStatusChanged;
            GameListService.ProgressChanged += OnGameListProgressChanged;
            IconCache.ProgressChanged += OnIconProgressChanged;
            Activated += OnWindowActivated;
        }
        private void ApplyTheme(ElementTheme theme)
        {
            if (Content is FrameworkElement root)
            {
                root.RequestedTheme = theme;
            }
            // ]i^sAC
            StatusText.Text = theme switch
            {
                ElementTheme.Default => "Theme: System default",
                ElementTheme.Light => "Theme: Light",
                ElementTheme.Dark => "Theme: Dark",
                _ => "Theme: ?"
            };

            // ]i^[ƨϥΪ̿
            //var settings = Windows.Storage.ApplicationData.Current.LocalSettings;
            //settings.Values["AppTheme"] = theme.ToString();
        }

        private void Theme_Default_Click(object sender, RoutedEventArgs e) => ApplyTheme(ElementTheme.Default);
        private void Theme_Light_Click(object sender, RoutedEventArgs e) => ApplyTheme(ElementTheme.Light);
        private void Theme_Dark_Click(object sender, RoutedEventArgs e) => ApplyTheme(ElementTheme.Dark);

        // ]i^b MainWindow() غcŪ^WܡG
        //if (Windows.Storage.ApplicationData.Current.LocalSettings.Values.TryGetValue("AppTheme", out var t)
        //    && Enum.TryParse<ElementTheme>(t?.ToString(), out var saved)) {
        //    ApplyTheme(saved);
        //}
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
                StartSamGame(game);
            }
        }

        private void OnLaunchSamGameClicked(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement element && element.DataContext is GameItem game)
            {
                StartSamGame(game);
            }
        }

        private void OnLaunchGameClicked(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement element && element.DataContext is GameItem game)
            {
                StartGame(game);
            }
        }

        private void StartSamGame(GameItem game)
        {
            StatusText.Text = $"Launching SAM.Game for {game.Title}...";
            StatusProgress.IsIndeterminate = true;
            StatusExtra.Text = string.Empty;
            GameLauncher.LaunchSamGame(game);
            StatusProgress.IsIndeterminate = false;
            StatusText.Text = "Ready";
        }

        private void StartGame(GameItem game)
        {
            StatusText.Text = $"Launching {game.Title}...";
            StatusProgress.IsIndeterminate = true;
            StatusExtra.Text = string.Empty;
            GameLauncher.Launch(game);
            StatusProgress.IsIndeterminate = false;
            StatusText.Text = "Ready";
        }

        private async void OnRefreshClicked(object sender, RoutedEventArgs e)
        {
            try
            {
                await RefreshAsync();
            }
            catch (Exception ex)
            {
                StatusProgress.IsIndeterminate = false;
                StatusProgress.Value = 0;
                StatusExtra.Text = string.Empty;
                StatusText.Text = "Refresh failed";

                Debug.WriteLine(ex);

                var dialog = new ContentDialog
                {
                    Title = "Refresh failed",
                    Content = "Unable to refresh game list. Please try again.",
                    CloseButtonText = "OK",
                    XamlRoot = Content.XamlRoot
                };

                await dialog.ShowAsync();
                StatusText.Text = "Ready";
            }
        }

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
                RefreshButton.IsEnabled = false;
                return;
            }

            StatusText.Text = "Refresh";
            StatusProgress.Value = 0;
            StatusExtra.Text = "0%";

            IconCache.ResetProgress();

            var baseDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AnSAM");
            Directory.CreateDirectory(baseDir);
            var cacheDir = Path.Combine(baseDir, "cache");
            var ownedPath = Path.Combine(baseDir, "owned.json");

            Games.Clear();
            _allGames.Clear();
            if (File.Exists(ownedPath))
            {
                try
                {
                    await using var fs = File.OpenRead(ownedPath);
                    var cached = await JsonSerializer.DeserializeAsync<List<SteamAppData>>(fs);
                    if (cached != null)
                    {
#if DEBUG
                        Debug.WriteLine($"Using cached owned games from {ownedPath}");
#endif
                        foreach (var app in cached)
                        {
                            _allGames.Add(GameItem.FromSteamApp(app));
                        }
                        FilterGames(null);
                        StatusText.Text = $"Loaded {_allGames.Count} games from cache";
                    }
                }
                catch (Exception ex)
                {
#if DEBUG
                    Debug.WriteLine($"Failed to load owned cache: {ex.GetBaseException().Message}");
#endif
                }
            }

            using var http = new HttpClient();
            await GameListService.LoadAsync(cacheDir, http);

            Games.Clear();
            _allGames.Clear();

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
            var ownedApps = new List<SteamAppData>();
            foreach (var game in GameListService.Games)
            {
                total++;
                uint appId = (uint)game.Id;
                string title = game.Name;
                IReadOnlyList<string>? coverUrls = null;
                if (steamReady)
                {
                    if (!_steamClient.IsSubscribedApp(appId))
                    {
                        continue;
                    }
                    owned++;
                    title = _steamClient.GetAppData(appId, "name") ?? title;
                    coverUrls = GameImageUrlResolver.GetGameImageUrls(
                        (id, key) => _steamClient.GetAppData(id, key),
                        appId,
                        "english");
                }
                var data = new SteamAppData(game.Id, title, coverUrls);
                _allGames.Add(GameItem.FromSteamApp(data));
                if (steamReady)
                {
                    ownedApps.Add(data);
                }
            }
#if DEBUG
            Debug.WriteLine($"Processed {total} games; owned {owned}; added {_allGames.Count}");
#endif

            FilterGames(null);

            if (steamReady)
            {
                try
                {
                    await using var fs = File.Create(ownedPath);
                    await JsonSerializer.SerializeAsync(fs, ownedApps);
                }
                catch (Exception ex)
                {
#if DEBUG
                    Debug.WriteLine($"Failed to save owned cache: {ex.GetBaseException().Message}");
#endif
                }
            }

            StatusText.Text = steamReady
                ? $"Loaded {_allGames.Count} games"
                : $"Steam unavailable - showing {_allGames.Count} games";
        }

        private void OnClearCacheClicked(object sender, RoutedEventArgs e)
        {
            try
            {
                var baseDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AnSAM");
                var iconDir = Path.Combine(baseDir, "appcache");
                var cacheDir = Path.Combine(baseDir, "cache");
                var gameListPath = Path.Combine(cacheDir, "games.xml");

                if (Directory.Exists(iconDir))
                {
                    Directory.Delete(iconDir, true);
                }

                if (File.Exists(gameListPath))
                {
                    File.Delete(gameListPath);
                }

                StatusText.Text = "Cache cleared";
            }
            catch (Exception ex)
            {
#if DEBUG
                Debug.WriteLine($"Failed to clear cache: {ex.Message}");
#endif
                StatusText.Text = "Failed to clear cache";
            }
        }

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

            if (app.CoverUrls is { Count: >0 })
            {
#if DEBUG
                Debug.WriteLine($"Queueing icon download for {app.AppId} from {string.Join(", ", app.CoverUrls)}");
#endif
                var dispatcher = DispatcherQueue.GetForCurrentThread();
                _ = LoadIconAsync();

                async Task LoadIconAsync()
                {
                    string? path = null;
                    try
                    {
                        path = await IconCache.GetIconPathAsync(app.AppId, app.CoverUrls);
#if DEBUG
                        if (path != null)
                        {
                            Debug.WriteLine($"Icon for {app.AppId} stored at {path}");
                        }
#endif
                    }
                    catch (Exception ex)
                    {
#if DEBUG
                        Debug.WriteLine($"Icon download failed for {app.AppId}: {ex.GetBaseException().Message}");
#endif
                    }

                    path ??= "ms-appx:///Assets/StoreLogo.png";
                    _ = dispatcher.TryEnqueue(() => item.CoverPath = path);
                }
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
