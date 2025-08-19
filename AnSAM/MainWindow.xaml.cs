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
using Windows.UI;

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
        private readonly AppWindow _appWindow;

        private bool _autoLoaded;
        private ScrollViewer? _gamesScrollViewer;

        public MainWindow(SteamClient steamClient)
        {
            _steamClient = steamClient;
            InitializeComponent();

            // 取得 AppWindow
            var hwnd = WindowNative.GetWindowHandle(this);
            var winId = Win32Interop.GetWindowIdFromWindow(hwnd);
            _appWindow = AppWindow.GetFromWindowId(winId);
            // 設定 Icon：指向打包後的實體檔案路徑
            var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "AnSAM.ico");
            if (File.Exists(iconPath))
                _appWindow.SetIcon(iconPath);

            if (Content is FrameworkElement root)
            {
                root.KeyDown += OnWindowKeyDown;
                if (AppWindowTitleBar.IsCustomizationSupported())
                {
                    root.ActualThemeChanged += (_, _) => UpdateTitleBar(root.ActualTheme);
                    UpdateTitleBar(root.ActualTheme);
                }
            }

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

        private void UpdateTitleBar(ElementTheme theme)
        {
            if (!AppWindowTitleBar.IsCustomizationSupported())
                return;

            var titleBar = _appWindow.TitleBar;
            if (theme == ElementTheme.Dark)
            {
                titleBar.BackgroundColor = Colors.Black;
                titleBar.ForegroundColor = Colors.White;

                titleBar.ButtonBackgroundColor = Colors.Black;
                titleBar.ButtonForegroundColor = Colors.White;
                titleBar.ButtonHoverBackgroundColor = Colors.DimGray;
                titleBar.ButtonHoverForegroundColor = Colors.White;
                titleBar.ButtonPressedBackgroundColor = Colors.Gray;
                titleBar.ButtonPressedForegroundColor = Colors.White;

                titleBar.InactiveBackgroundColor = Colors.Black;
                titleBar.InactiveForegroundColor = Colors.Gray;
                titleBar.ButtonInactiveBackgroundColor = Colors.Black;
                titleBar.ButtonInactiveForegroundColor = Colors.Gray;
            }
            else
            {
                titleBar.BackgroundColor = Colors.White;
                titleBar.ForegroundColor = Colors.Black;

                titleBar.ButtonBackgroundColor = Colors.White;
                titleBar.ButtonForegroundColor = Colors.Black;
                titleBar.ButtonHoverBackgroundColor = Colors.LightGray;
                titleBar.ButtonHoverForegroundColor = Colors.Black;
                titleBar.ButtonPressedBackgroundColor = Colors.DarkGray;
                titleBar.ButtonPressedForegroundColor = Colors.Black;

                titleBar.InactiveBackgroundColor = Colors.White;
                titleBar.InactiveForegroundColor = Colors.Gray;
                titleBar.ButtonInactiveBackgroundColor = Colors.White;
                titleBar.ButtonInactiveForegroundColor = Colors.Gray;
            }
        }

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

            Games.Clear();
            _allGames.Clear();

            using var http = new HttpClient();
            var apps = await GameCacheService.RefreshAsync(baseDir, _steamClient, http);

            foreach (var app in apps)
            {
                var withCover = _steamClient.Initialized
                    ? app with { CoverUrl = GameImageUrlResolver.GetGameImageUrl(_steamClient, (uint)app.AppId, "english") }
                    : app;
                _allGames.Add(GameItem.FromSteamApp(withCover));
            }

            SortAllGames();
            FilterGames(null);

            StatusText.Text = _steamClient.Initialized
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
                List<string> matches;
                if (string.IsNullOrWhiteSpace(keyword))
                {
                    matches = new List<string>();
                }
                else
                {
                    var titles = new HashSet<string>();
                    matches = new List<string>();
                    foreach (var game in Games)
                    {
                        if (game.Title.Contains(keyword, StringComparison.OrdinalIgnoreCase) && titles.Add(game.Title))
                        {
                            matches.Add(game.Title);
                            if (matches.Count == 10)
                            {
                                break;
                            }
                        }
                    }
                }
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

        private void SortAllGames()
        {
            _allGames.Sort((a, b) => string.Compare(a.Title, b.Title, StringComparison.OrdinalIgnoreCase));
        }

        private void FilterGames(string? keyword)
        {
            string kw = keyword ?? string.Empty;
            bool hasKeyword = kw.Length > 0;
            int index = 0;
            foreach (var game in _allGames)
            {
                if (hasKeyword && !game.Title.Contains(kw, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                while (index < Games.Count && !ReferenceEquals(Games[index], game))
                {
                    Games.RemoveAt(index);
                }

                if (index >= Games.Count)
                {
                    Games.Add(game);
                }

                index++;
            }

            while (Games.Count > index)
            {
                Games.RemoveAt(Games.Count - 1);
            }

            StatusText.Text = $"Showing {Games.Count} of {_allGames.Count} games";
            StatusProgress.Value = 0;
            StatusExtra.Text = $"{Games.Count}/{_allGames.Count}";
#if DEBUG
            Debug.WriteLine($"FilterGames('{kw}') -> {Games.Count} items");
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
