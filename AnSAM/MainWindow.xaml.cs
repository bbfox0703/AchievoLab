using AnSAM.Services;
using AnSAM.Steam;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;
using WinRT.Interop;

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
        private ScrollViewer? _gamesScrollViewer;

        public MainWindow(SteamClient steamClient)
        {
            _steamClient = steamClient;
            InitializeComponent();

            if (Content is FrameworkElement root)
            {
                root.KeyDown += OnWindowKeyDown;
            }

            // 取得 AppWindow
            var hwnd = WindowNative.GetWindowHandle(this);
            var winId = Win32Interop.GetWindowIdFromWindow(hwnd);
            var appWin = AppWindow.GetFromWindowId(winId);
            // 設定 Icon：指向打包後的實體檔案路徑
            var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "AnSAM.ico");
            if (File.Exists(iconPath))
                appWin.SetIcon(iconPath);

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

        private void OnWindowKeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key != Windows.System.VirtualKey.PageDown &&
                e.Key != Windows.System.VirtualKey.PageUp &&
                e.Key != Windows.System.VirtualKey.Home &&
                e.Key != Windows.System.VirtualKey.End)
                return;

            var sv = _gamesScrollViewer ??= FindScrollViewer(GamesView);
            if (sv == null)
                return;

            double? offset = null;
            var delta = sv.ViewportHeight;

            switch (e.Key)
            {
                case Windows.System.VirtualKey.PageDown when delta > 0:
                    offset = sv.VerticalOffset + delta;
                    break;
                case Windows.System.VirtualKey.PageUp when delta > 0:
                    offset = sv.VerticalOffset - delta;
                    break;
                case Windows.System.VirtualKey.Home:
                    offset = 0;
                    break;
                case Windows.System.VirtualKey.End:
                    offset = sv.ScrollableHeight;
                    break;
            }

            if (offset.HasValue)
            {
                sv.ChangeView(null, offset.Value, null);
                e.Handled = true;
            }
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
            var userGamesPath = Path.Combine(cacheDir, "usergames.xml");

            Games.Clear();
            _allGames.Clear();

            using var http = new HttpClient();
            await GameListService.LoadAsync(cacheDir, http);

            if (File.Exists(userGamesPath))
            {
                try
                {
                    var doc = XDocument.Load(userGamesPath);
                    var gamesById = GameListService.Games.ToDictionary(g => g.Id);
                    foreach (var node in doc.Root?.Elements("game") ?? Enumerable.Empty<XElement>())
                    {
                        if (!int.TryParse(node.Attribute("id")?.Value, out var id))
                        {
                            continue;
                        }

                        gamesById.TryGetValue(id, out var game);
                        var title = string.IsNullOrEmpty(game.Name)
                            ? id.ToString(CultureInfo.InvariantCulture)
                            : game.Name;
                        _allGames.Add(GameItem.FromSteamApp(new SteamAppData(id, title, null)));
                    }

                    if (_allGames.Count > 0)
                    {
                        FilterGames(null);
                        StatusText.Text = $"Loaded {_allGames.Count} games from cache";
                        await Task.Yield();
                    }
                }
                catch (Exception ex)
                {
#if DEBUG
                    Debug.WriteLine($"Failed to load user games cache: {ex.GetBaseException().Message}");
#endif
                }
            }

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
                    coverUrl = GameImageUrlResolver.GetGameImageUrl(_steamClient, appId, "english");
                }
                var data = new SteamAppData(game.Id, title, coverUrl);
                _allGames.Add(GameItem.FromSteamApp(data));
            }
#if DEBUG
            Debug.WriteLine($"Processed {total} games; owned {owned}; added {_allGames.Count}");
#endif

            FilterGames(null);

            if (steamReady)
            {
                try
                {
                    Directory.CreateDirectory(cacheDir);
                    var tempPath = userGamesPath + ".tmp";
                    using (var writer = XmlWriter.Create(tempPath, new XmlWriterSettings { Indent = true, Encoding = Encoding.UTF8 }))
                    {
                        writer.WriteStartElement("games");
                        foreach (var id in _allGames.Select(g => g.ID).Distinct().OrderBy(i => i))
                        {
                            writer.WriteStartElement("game");
                            writer.WriteAttributeString("id", id.ToString(CultureInfo.InvariantCulture));
                            writer.WriteEndElement();
                        }
                        writer.WriteEndElement();
                    }

                    if (File.Exists(userGamesPath))
                    {
                        File.Replace(tempPath, userGamesPath, null);
                    }
                    else
                    {
                        File.Move(tempPath, userGamesPath);
                    }
                }
                catch (Exception ex)
                {
#if DEBUG
                    Debug.WriteLine($"Failed to save user games cache: {ex.GetBaseException().Message}");
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

            var list = filtered
                .OrderBy(g => g.Title)
                .ToList();
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

    public class GameItem : System.ComponentModel.INotifyPropertyChanged
    {
        public string Title { get; set; }
        public int ID { get; set; }
        public Uri? CoverPath
        {
            get => _coverPath;
            set
            {
                if (_coverPath != value)
                {
                    _coverPath = value;
                    PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(CoverPath)));
                    PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(CoverImage)));
                }
            }
        }

        public ImageSource? CoverImage => _coverPath != null ? new BitmapImage(_coverPath) : null;

        private Uri? _coverPath;

        public string? ExePath { get; set; }
        public string? Arguments { get; set; }
        public string? UriScheme { get; set; }

        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

        public GameItem(string title,
                         int id,
                         Uri? coverPath = null,
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

            if (app.CoverUrl != null)
            {
#if DEBUG
                Debug.WriteLine($"Queueing icon download for {app.AppId} from {app.CoverUrl}");
#endif
                var dispatcher = DispatcherQueue.GetForCurrentThread();
                _ = LoadIconAsync();

                async Task LoadIconAsync()
                {
                    Uri? coverUri = null;
                    try
                    {
                        if (Uri.TryCreate(app.CoverUrl, UriKind.Absolute, out var remoteUri))
                        {
                            var result = await IconCache.GetIconPathAsync(app.AppId, remoteUri);
#if DEBUG
                            if (result.Downloaded)
                            {
                                Debug.WriteLine($"Icon for {app.AppId} stored at {result.Path}");
                            }
#endif
                            if (Uri.TryCreate(result.Path, UriKind.Absolute, out var localUri))
                            {
                                coverUri = localUri;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
#if DEBUG
                        Debug.WriteLine($"Icon download failed for {app.AppId}: {ex.GetBaseException().Message}");
#endif
                    }

                    coverUri ??= new Uri("ms-appx:///no_icon.png");
                    if (dispatcher != null)
                    {
                        _ = dispatcher.TryEnqueue(() => item.CoverPath = coverUri);
                    }
                    else
                    {
                        item.CoverPath = coverUri;
                    }
                }
            }
            else
            {
#if DEBUG
                Debug.WriteLine($"No icon URL for {app.AppId}");
#endif
                item.CoverPath = new Uri("ms-appx:///no_icon.png");
            }

            return item;
        }
    }
}
