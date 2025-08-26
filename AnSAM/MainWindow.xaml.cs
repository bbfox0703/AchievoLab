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
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;
using WinRT.Interop;
using Windows.Storage;
using Windows.UI;
using Windows.UI.ViewManagement;
using CommonUtilities;

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
        private readonly HttpClient _imageHttpClient = new();
        private readonly SharedImageService _imageService;
        private string _currentLanguage = "english";

        private bool _autoLoaded;
        private bool _languageInitialized;
        private ScrollViewer? _gamesScrollViewer;
        private readonly UISettings _uiSettings = new();

        public MainWindow(SteamClient steamClient, ElementTheme theme)
        {
            _steamClient = steamClient;
            _imageService = new SharedImageService(_imageHttpClient);
            InitializeComponent();

            _uiSettings.ColorValuesChanged += UiSettings_ColorValuesChanged;

            InitializeLanguageComboBox();

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
                ApplyTheme(theme, save: false);
                root.KeyDown += OnWindowKeyDown;
                if (AppWindowTitleBar.IsCustomizationSupported())
                {
                    root.ActualThemeChanged += (_, _) => UpdateTitleBar(root.ActualTheme);
                }
            }

            if (!_steamClient.Initialized)
            {
                StatusText.Text = "Steam unavailable";
            }
            GameListService.StatusChanged += OnGameListStatusChanged;
            GameListService.ProgressChanged += OnGameListProgressChanged;
            Activated += OnWindowActivated;
            Closed += OnWindowClosed;
        }

        private void InitializeLanguageComboBox()
        {
            var languages = SteamLanguageResolver.SupportedLanguages;

            var osLanguage = SteamLanguageResolver.GetSteamLanguage();
            if (!languages.Contains(osLanguage, StringComparer.OrdinalIgnoreCase))
            {
                osLanguage = "english";
            }

            var ordered = languages.Where(l => !string.Equals(l, osLanguage, StringComparison.OrdinalIgnoreCase) &&
                                               !string.Equals(l, "english", StringComparison.OrdinalIgnoreCase))
                                   .OrderBy(l => l)
                                   .ToList();

            ordered.Insert(0, "english");
            if (!string.Equals(osLanguage, "english", StringComparison.OrdinalIgnoreCase))
            {
                ordered.Insert(0, osLanguage);
            }

            foreach (var lang in ordered)
            {
                LanguageComboBox.Items.Add(lang);
            }

            string? saved = null;
            try
            {
                var settings = ApplicationData.Current.LocalSettings;
                saved = settings.Values["Language"] as string;
            }
            catch (InvalidOperationException)
            {
                // Ignore inability to persist settings
            }
            // Always initialize to English, regardless of saved settings
            string initial = "english";
            
            LanguageComboBox.SelectedItem = initial;
            SteamLanguageResolver.OverrideLanguage = initial;
            _imageService.SetLanguage(initial).GetAwaiter().GetResult();
            _currentLanguage = initial; // Set current language to English
            _languageInitialized = true;
        }
        private void ApplyTheme(ElementTheme theme, bool save = true)
        {
            if (Content is FrameworkElement root)
            {
                root.RequestedTheme = theme;
                ApplyAccentBrush(root);
                UpdateTitleBar(theme);
            }

            StatusText.Text = theme switch
            {
                ElementTheme.Default => "Theme: System default",
                ElementTheme.Light => "Theme: Light",
                ElementTheme.Dark => "Theme: Dark",
                _ => "Theme: ?",
            };

            if (save)
            {
                try
                {
                    var settings = ApplicationData.Current.LocalSettings;
                    settings.Values["AppTheme"] = theme.ToString();
                }
                catch (InvalidOperationException)
                {
                    // Ignore inability to persist settings
                }
            }

            // Don't set Application.RequestedTheme to avoid COMException in WinUI 3
        }
        private void Theme_Default_Click(object sender, RoutedEventArgs e) => ApplyTheme(ElementTheme.Default);
        private void Theme_Light_Click(object sender, RoutedEventArgs e) => ApplyTheme(ElementTheme.Light);
        private void Theme_Dark_Click(object sender, RoutedEventArgs e) => ApplyTheme(ElementTheme.Dark);

        private async void LanguageComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_languageInitialized)
            {
                return;
            }

            if (LanguageComboBox.SelectedItem is string lang)
            {
                // Check if language actually changed
                if (lang == _currentLanguage)
                {
                    DebugLogger.LogDebug($"Language not changed, staying with: {lang}");
                    return; // No action needed if language hasn't changed
                }

                DebugLogger.LogDebug($"Language changed from {_currentLanguage} to {lang}");
                
                SteamLanguageResolver.OverrideLanguage = lang;
                await _imageService.SetLanguage(lang);

                try
                {
                    var settings = ApplicationData.Current.LocalSettings;
                    settings.Values["Language"] = lang;
                }
                catch (InvalidOperationException)
                {
                    // Ignore inability to persist settings
                }

                try
                {
                    // Load localized titles from steam_games.xml if switching to non-English
                    if (lang != "english")
                    {
                        LoadLocalizedTitlesFromXml();
                    }
                    
                    // Update game titles for the new language
                    UpdateAllGameTitles(lang);
                    
                    StatusText.Text = lang == "english" 
                        ? $"Switched to English - displaying original titles" 
                        : $"Switched to {lang} - using localized titles where available";
                }
                catch (Exception ex)
                {
                    StatusProgress.IsIndeterminate = false;
                    StatusProgress.Value = 0;
                    StatusExtra.Text = string.Empty;
                    StatusText.Text = "Language switch failed";

                    DebugLogger.LogDebug($"Language switch error: {ex}");

                    var dialog = new ContentDialog
                    {
                        Title = "Language switch failed",
                        Content = "Unable to switch language. Please try again.",
                        CloseButtonText = "OK",
                        XamlRoot = Content.XamlRoot
                    };

                    await dialog.ShowAsync();
                    StatusText.Text = "Ready";
                }
            }
        }

        private void UpdateTitleBar(ElementTheme theme)
        {
            if (!AppWindowTitleBar.IsCustomizationSupported())
                return;

            var titleBar = _appWindow.TitleBar;
            var accent = _uiSettings.GetColorValue(UIColorType.Accent);
            var accentDark1 = _uiSettings.GetColorValue(UIColorType.AccentDark1);
            var accentDark2 = _uiSettings.GetColorValue(UIColorType.AccentDark2);
            var accentDark3 = _uiSettings.GetColorValue(UIColorType.AccentDark3);
            var accentLight1 = _uiSettings.GetColorValue(UIColorType.AccentLight1);
            var foreground = _uiSettings.GetColorValue(UIColorType.Foreground);
            var background = _uiSettings.GetColorValue(UIColorType.Background);
            var inactiveForeground = Color.FromArgb(
                foreground.A,
                (byte)(foreground.R / 2),
                (byte)(foreground.G / 2),
                (byte)(foreground.B / 2));

            if (theme == ElementTheme.Dark)
            {
                titleBar.BackgroundColor = accentDark2;
                titleBar.ForegroundColor = foreground;

                titleBar.ButtonBackgroundColor = accentDark2;
                titleBar.ButtonForegroundColor = foreground;
                titleBar.ButtonHoverBackgroundColor = accent;
                titleBar.ButtonHoverForegroundColor = foreground;
                titleBar.ButtonPressedBackgroundColor = accentDark2;
                titleBar.ButtonPressedForegroundColor = foreground;

                titleBar.InactiveBackgroundColor = accentDark2;
                titleBar.InactiveForegroundColor = inactiveForeground;
                titleBar.ButtonInactiveBackgroundColor = accentDark2;
                titleBar.ButtonInactiveForegroundColor = inactiveForeground;
            }
            else
            {
                titleBar.BackgroundColor = accentLight1;
                titleBar.ForegroundColor = foreground;

                titleBar.ButtonBackgroundColor = accentLight1;
                titleBar.ButtonForegroundColor = foreground;
                titleBar.ButtonHoverBackgroundColor = accent;
                titleBar.ButtonHoverForegroundColor = foreground;
                titleBar.ButtonPressedBackgroundColor = accentDark1;
                titleBar.ButtonPressedForegroundColor = foreground;

                titleBar.InactiveBackgroundColor = accentLight1;
                titleBar.InactiveForegroundColor = inactiveForeground;
                titleBar.ButtonInactiveBackgroundColor = accentLight1;
                titleBar.ButtonInactiveForegroundColor = inactiveForeground;
            }
        }

        private void ApplyAccentBrush(FrameworkElement root)
        {
            var accent = _uiSettings.GetColorValue(UIColorType.Accent);
            var brush = new SolidColorBrush(accent);
            root.Resources["AppAccentBrush"] = brush;
            StatusText.Foreground = brush;
        }

        private void UiSettings_ColorValuesChanged(UISettings sender, object args)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                if (Content is FrameworkElement root)
                {
                    ApplyAccentBrush(root);
                    UpdateTitleBar(root.ActualTheme);
                }
            });
        }

        // ]i^b MainWindow() غcŪ^WܡG
        //if (Windows.Storage.ApplicationData.Current.LocalSettings.Values.TryGetValue("AppTheme", out var t)
        //    && Enum.TryParse<ElementTheme>(t?.ToString(), out var saved)) {
        //    ApplyTheme(saved);
        //}
        private void OnWindowActivated(object sender, WindowActivatedEventArgs args)
        {
            if (_autoLoaded) return;
            _autoLoaded = true;
            DispatcherQueue.TryEnqueue(async () => await RefreshAsync());
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

        private void OnWindowClosed(object sender, WindowEventArgs args)
        {
            GameListService.StatusChanged -= OnGameListStatusChanged;
            GameListService.ProgressChanged -= OnGameListProgressChanged;
            _uiSettings.ColorValuesChanged -= UiSettings_ColorValuesChanged;
            Activated -= OnWindowActivated;
            _imageService.Dispose();
            _imageHttpClient.Dispose();

            if (Content is FrameworkElement root)
            {
                root.KeyDown -= OnWindowKeyDown;
            }
        }

        private async void GamesView_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs e)
        {
            if (e.InRecycleQueue || e.Item is not GameItem game)
            {
                return;
            }

            await game.LoadCoverAsync(_imageService);
        }


        private void GameCard_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
        {
            if (sender is FrameworkElement element && element.DataContext is GameItem game)
            {
                if (game.IsSamGameAvailable)
                {
                    StartSamGame(game);
                }
                else
                {
                    StatusText.Text = "Achievement manager not found";
                }
            }
        }


        private void OnLaunchSamGameClicked(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement element && element.DataContext is GameItem game)
            {
                if (game.IsSamGameAvailable)
                {
                    StartSamGame(game);
                }
                else
                {
                    StatusText.Text = "Achievement manager not found";
                }
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
            StatusText.Text = $"Launching achievement manager for {game.Title}...";
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
                return;
            }

            StatusText.Text = "Refresh";
            StatusProgress.IsIndeterminate = false;
            StatusProgress.Value = 0;
            StatusExtra.Text = "0%";

            var baseDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AchievoLab");

            var http = HttpClientProvider.Shared;
            var apps = await GameCacheService.RefreshAsync(baseDir, _steamClient, http);

            var (allGames, filteredGames) = await BuildGameListAsync(apps, null);

            _ = DispatcherQueue.TryEnqueue(() =>
            {
                _allGames.Clear();
                _allGames.AddRange(allGames);

                // Load localized titles if current language is not English
                if (_currentLanguage != "english")
                {
                    LoadLocalizedTitlesFromXml();
                }
                
                // Update all game titles to current language
                UpdateAllGameTitles(_currentLanguage);

                Games.Clear();
                foreach (var game in filteredGames)
                {
                    Games.Add(game);
                }

                StatusText.Text = _steamClient.Initialized
                    ? $"Loaded {_allGames.Count} games (Language: {_currentLanguage})"
                    : $"Steam unavailable - showing {_allGames.Count} games (Language: {_currentLanguage})";
                StatusProgress.Value = 0;
                StatusExtra.Text = $"{Games.Count}/{_allGames.Count}";
            });
        }

        private Task<(List<GameItem> allGames, List<GameItem> filteredGames)> BuildGameListAsync(IEnumerable<SteamAppData> apps, string? keyword)
        {
            return Task.Run(() =>
            {
                var allGames = new List<GameItem>();
                foreach (var app in apps)
                {
                    allGames.Add(GameItem.FromSteamApp(app));
                }

                allGames.Sort((a, b) => string.Compare(a.Title, b.Title, StringComparison.OrdinalIgnoreCase));

                List<GameItem> filtered;
                if (string.IsNullOrEmpty(keyword))
                {
                    filtered = new List<GameItem>(allGames);
                }
                else
                {
                    filtered = allGames
                        .Where(g => g.Title.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                        .ToList();
                }

                return (allGames, filtered);
            });
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
            DebugLogger.LogDebug($"FilterGames('{kw}') -> {Games.Count} items");
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

        /// <summary>
        /// Load localized game titles from MyOwnGames steam_games.xml
        /// </summary>
        private void LoadLocalizedTitlesFromXml()
        {
            try
            {
                var steamGamesXmlPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "AchievoLab", "cache", "steam_games.xml");

                if (!File.Exists(steamGamesXmlPath))
                {
                    DebugLogger.LogDebug("steam_games.xml not found, using English titles only");
                    return;
                }

                var doc = XDocument.Load(steamGamesXmlPath);
                var gameElements = doc.Root?.Elements("Game");
                
                if (gameElements == null) return;

                var localizedData = new Dictionary<int, Dictionary<string, string>>();
                
                foreach (var gameElement in gameElements)
                {
                    if (int.TryParse(gameElement.Attribute("AppID")?.Value, out var appId))
                    {
                        var gameLocalizedTitles = new Dictionary<string, string>();
                        
                        // Find all Name_* elements
                        foreach (var nameElement in gameElement.Elements())
                        {
                            var elementName = nameElement.Name.LocalName;
                            if (elementName.StartsWith("Name_") && !string.IsNullOrEmpty(nameElement.Value))
                            {
                                var language = elementName.Substring(5); // Remove "Name_" prefix
                                gameLocalizedTitles[language] = nameElement.Value;
                            }
                        }
                        
                        if (gameLocalizedTitles.Count > 0)
                        {
                            localizedData[appId] = gameLocalizedTitles;
                        }
                    }
                }

                // Apply localized titles to existing games
                foreach (var game in _allGames)
                {
                    if (localizedData.TryGetValue(game.ID, out var titles))
                    {
                        game.LocalizedTitles = titles;
                        DebugLogger.LogDebug($"Loaded {titles.Count} localized titles for game {game.ID} ({game.EnglishTitle})");
                    }
                }
                
                DebugLogger.LogDebug($"Loaded localized titles from {steamGamesXmlPath} for {localizedData.Count} games");
            }
            catch (Exception ex)
            {
                DebugLogger.LogDebug($"Error loading localized titles: {ex.Message}");
            }
        }

        /// <summary>
        /// Update all games to display titles in the specified language
        /// </summary>
        private void UpdateAllGameTitles(string language)
        {
            foreach (var game in _allGames)
            {
                game.UpdateDisplayTitle(language);
            }
            
            // Also update filtered games in the UI
            foreach (var game in Games)
            {
                game.UpdateDisplayTitle(language);
            }
            
            _currentLanguage = language;
            DebugLogger.LogDebug($"Updated all game titles to language: {language}");
        }

    }

    public class GameItem : System.ComponentModel.INotifyPropertyChanged
    {
        private string _displayTitle;
        public string Title 
        { 
            get => _displayTitle;
            set
            {
                if (_displayTitle != value)
                {
                    _displayTitle = value;
                    PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(Title)));
                }
            }
        }
        
        // Store original English title from Steam client
        public string EnglishTitle { get; set; }
        
        // Store localized titles from steam_games.xml if available
        public Dictionary<string, string> LocalizedTitles { get; set; } = new();
        
        public int ID { get; set; }
        public int AppId => ID;
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
        public bool IsSamGameAvailable => GameLauncher.IsSamGameAvailable;

        private bool _coverLoading;
        public bool IsCoverLoading => _coverLoading;

        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

        public GameItem(string title,
                         int id,
                         Uri? coverPath = null,
                         string? exePath = null,
                         string? arguments = null,
                         string? uriScheme = null)
        {
            EnglishTitle = title; // Store original English title
            _displayTitle = title; // Initialize display title
            ID = id;
            CoverPath = coverPath;
            ExePath = exePath;
            Arguments = arguments;
            UriScheme = uriScheme;
        }

        /// <summary>
        /// Update display title based on current language
        /// </summary>
        /// <param name="language">Target language (e.g., "tchinese", "japanese")</param>
        public void UpdateDisplayTitle(string language)
        {
            if (language == "english")
            {
                Title = EnglishTitle;
            }
            else if (LocalizedTitles.TryGetValue(language, out var localizedTitle) && 
                     !string.IsNullOrEmpty(localizedTitle))
            {
                Title = localizedTitle;
            }
            else
            {
                Title = EnglishTitle; // Fallback to English
            }
        }

        public async Task LoadCoverAsync(SharedImageService imageService)
        {
            if (CoverPath != null || _coverLoading)
            {
                return;
            }

            _coverLoading = true;
            var dispatcher = DispatcherQueue.GetForCurrentThread();
            var coverAssigned = false;

            try
            {
                string language = SteamLanguageResolver.GetSteamLanguage();

                var path = await imageService.GetGameImageAsync(ID, language).ConfigureAwait(false);
                if (!string.IsNullOrEmpty(path) && Uri.TryCreate(path, UriKind.Absolute, out var localUri))
                {
                    coverAssigned = true;
                    if (dispatcher != null)
                    {
                        _ = dispatcher.TryEnqueue(() => CoverPath = localUri);
                    }
                    else
                    {
                        CoverPath = localUri;
                    }
                }
            }
            catch (Exception ex)
            {
#if DEBUG
                DebugLogger.LogDebug($"Icon download failed for {ID}: {ex.GetBaseException().Message}");
#endif
            }
            finally
            {
                if (!coverAssigned && CoverPath == null)
                {
                    var fallback = new Uri("ms-appx:///Assets/no_icon.png", UriKind.Absolute);
                    if (dispatcher != null)
                    {
                        _ = dispatcher.TryEnqueue(() => CoverPath = fallback);
                    }
                    else
                    {
                        CoverPath = fallback;
                    }
                }

                _coverLoading = false;
            }
        }

        public static GameItem FromSteamApp(SteamAppData app)
        {
#if DEBUG
            DebugLogger.LogDebug($"Creating GameItem for {app.AppId} - {app.Title}");
#endif
            return new GameItem(app.Title,
                                app.AppId,
                                null,
                                app.ExePath,
                                app.Arguments,
                                app.UriScheme);
        }
    }
}
