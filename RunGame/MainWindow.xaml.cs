using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media.Imaging;
using RunGame.Models;
using RunGame.Services;
using RunGame.Steam;
using System;
using System.IO;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Windows.Storage;
using Windows.UI;
using Windows.UI.ViewManagement;
using Windows.UI.WindowManagement;
using WinRT.Interop;
using CommonUtilities;

namespace RunGame
{
    public sealed partial class MainWindow : Window
    {
        private readonly long _gameId;
        private readonly ISteamUserStats _steamClient;
        private readonly GameStatsService _gameStatsService;
        private readonly DispatcherTimer _callbackTimer;
        private readonly DispatcherTimer _timeTimer;
        private readonly DispatcherTimer _achievementTimer;
        private readonly DispatcherTimer _mouseTimer;
        
        private readonly ObservableCollection<AchievementInfo> _achievements = new();
        private readonly ObservableCollection<StatInfo> _statistics = new();
        private readonly Dictionary<string, int> _achievementCounters = new();
        private List<AchievementInfo> _allAchievements = new();
        private readonly DispatcherTimer _searchDebounceTimer;

        private bool _isLoadingStats = false;
        private bool _lastMouseMoveRight = true;

        private readonly Microsoft.UI.Windowing.AppWindow _appWindow;

        // Theme
        private readonly UISettings _uiSettings = new();

        // New services
        private AchievementTimerService? _achievementTimerService;
        private MouseMoverService? _mouseMoverService;
        private AchievementIconService? _achievementIconService;

        public MainWindow(long gameId)
        {
            this.InitializeComponent();
            
            _gameId = gameId;

            // 取得 AppWindow
            var hwnd = WindowNative.GetWindowHandle(this);
            var winId = Win32Interop.GetWindowIdFromWindow(hwnd);
            _appWindow = Microsoft.UI.Windowing.AppWindow.GetFromWindowId(winId);
            // 設定 Icon：指向打包後的實體檔案路徑
            var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "RunGame.ico");
            if (File.Exists(iconPath))
                _appWindow.SetIcon(iconPath);

            // Set Steam AppID environment variable - some games require this
            Environment.SetEnvironmentVariable("SteamAppId", gameId.ToString());
            DebugLogger.LogDebug($"Set SteamAppId environment variable to {gameId}");
            
            // Try modern Steam client first, fallback to legacy if needed
            _steamClient = CreateSteamClient(gameId);
            
            _gameStatsService = new GameStatsService(_steamClient, gameId);
            
            // Initialize timers
            _callbackTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
            _callbackTimer.Tick += (_, _) => 
            {
                // Run callbacks for both legacy and modern Steam clients
                _steamClient.RunCallbacks();
            };
            _callbackTimer.Start();
            
            _timeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _timeTimer.Tick += OnTimeTimerTick;
            _timeTimer.Start();
            
            _achievementTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _achievementTimer.Tick += OnAchievementTimerTick;

            _mouseTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
            _mouseTimer.Tick += OnMouseTimerTick;

            _searchDebounceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
            _searchDebounceTimer.Tick += async (_, _) =>
            {
                _searchDebounceTimer.Stop();
                await LoadAchievements();
            };

            // Set up event handlers
            _gameStatsService.UserStatsReceived += OnUserStatsReceived;

            if (Content is FrameworkElement root)
            {
                ThemeService.Initialize(this, root);
                
                // Clear any old theme settings to ensure we follow system theme
                var settings = TryGetLocalSettings();
                if (settings != null)
                {
                    try
                    {
                        settings.Values.Remove("AppTheme");
                        DebugLogger.LogDebug("Cleared old AppTheme setting");
                    }
                    catch (InvalidOperationException)
                    {
                        // Ignore inability to clear settings
                    }
                }
                
                // Always use system theme (Default = follow system)
                ElementTheme themeToApply = ElementTheme.Default;
                ThemeService.ApplyTheme(themeToApply);
                
                root.ActualThemeChanged += (_, _) => 
                {
                    ThemeService.ApplyAccentBrush();
                    ThemeService.UpdateTitleBar(root.ActualTheme);
                };
            }
            _uiSettings.ColorValuesChanged += UiSettings_ColorValuesChanged;
            
            // Set window title
            string gameName = _steamClient.GetAppData((uint)gameId, "name") ?? gameId.ToString();
            string debugMode = DebugLogger.IsDebugMode ? " [DEBUG MODE]" : "";
            this.Title = $"AchievoLab:RunGame | {gameName}{debugMode}";
            
            // Initialize language options
            InitializeLanguageComboBox();
            
            // Initialize column layout options
            InitializeColumnLayoutComboBox();
            
            // Set up list views - simplified approach
            AchievementListView.ItemsSource = _achievements;
            StatisticsListView.ItemsSource = _statistics;
            
            // 設置 Debug 模式標籤
            if (DebugLogger.IsDebugMode)
            {
                DebugModeLabel.Text = "DEBUG MODE";
                ClearLogButton.Visibility = Visibility.Visible;
            }
            else
            {
                ClearLogButton.Visibility = Visibility.Collapsed;
            }
            
            // 初始化日誌
            DebugLogger.LogDebug($"RunGame started for game {gameId} in {(DebugLogger.IsDebugMode ? "DEBUG" : "RELEASE")} mode");
            
            // Initialize new services
            _achievementTimerService = new AchievementTimerService(_gameStatsService);
            _achievementTimerService.StatusUpdated += OnTimerStatusUpdated;
            
            // Get window handle for mouse service
            var windowHandle = WinRT.Interop.WindowNative.GetWindowHandle(this);
            _mouseMoverService = new MouseMoverService(windowHandle);
            
            // Initialize icon service
            _achievementIconService = new AchievementIconService(gameId);
            
            // Test game ownership before loading stats
            TestGameOwnership();
            
            // Start loading stats
            _ = LoadStatsAsync();
            
            // Subscribe to window closing event for cleanup
            this.Closed += OnWindowClosed;
        }
        
        private void OnWindowClosed(object sender, WindowEventArgs args)
        {
            try
            {
                // Dispose services
                _achievementTimerService?.Dispose();
                _mouseMoverService?.Dispose();
                _achievementIconService?.Dispose();
                
                // Stop timers
                _callbackTimer?.Stop();
                _timeTimer?.Stop();
                _achievementTimer?.Stop();
                _mouseTimer?.Stop();
                
                DebugLogger.LogDebug("MainWindow closed and resources cleaned up");
            }
            catch (Exception ex)
            {
                DebugLogger.LogDebug($"Error during cleanup: {ex.Message}");
            }
        }

        private void UiSettings_ColorValuesChanged(UISettings sender, object args)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                ThemeService.ApplyAccentBrush();
                if (Content is FrameworkElement root)
                {
                    ThemeService.UpdateTitleBar(root.ActualTheme);
                }
            });
        }

        private void SetTheme(ElementTheme theme)
        {
            DebugLogger.LogDebug($"SetTheme() called with {theme}");
            ThemeService.ApplyTheme(theme);
            // Note: No longer saving theme settings - always follow system theme
        }

        private static ApplicationDataContainer? TryGetLocalSettings()
        {
            DebugLogger.LogDebug("TryGetLocalSettings() Start");
            return App.LocalSettings;
        }

        private void InitializeLanguageComboBox()
        {
            var languages = SteamLanguageResolver.SupportedLanguages.ToList();
            var osLanguage = SteamLanguageResolver.GetSteamLanguage(CultureInfo.CurrentUICulture);

            var orderedLanguages = new List<string> { osLanguage };
            if (!string.Equals(osLanguage, "english", StringComparison.OrdinalIgnoreCase))
            {
                orderedLanguages.Add("english");
            }

            orderedLanguages.AddRange(
                languages.Where(l => l != osLanguage && l != "english")
                         .OrderBy(l => l));

            foreach (var lang in orderedLanguages)
            {
                LanguageComboBox.Items.Add(lang);
            }

            var selected = languages.Contains(osLanguage) ? osLanguage : "english";
            LanguageComboBox.SelectedItem = selected;
        }

        private void InitializeColumnLayoutComboBox()
        {
            var layouts = new[] 
            { 
                "Compact", "Normal", "Wide", "Extra Wide" 
            };
            
            foreach (var layout in layouts)
            {
                ColumnLayoutComboBox.Items.Add(layout);
            }
            
            ColumnLayoutComboBox.SelectedItem = "Normal";
        }

        private async Task LoadStatsAsync()
        {
            if (_isLoadingStats) return;
            
            _isLoadingStats = true;
            LoadingRing.IsActive = true;
            StatusLabel.Text = "Loading game statistics...";

            try
            {
                DebugLogger.LogDebug("LoadStatsAsync: Requesting user stats...");
                bool success = await _gameStatsService.RequestUserStatsAsync();
                DebugLogger.LogDebug($"LoadStatsAsync: RequestUserStatsAsync returned {success}");
                if (!success)
                {
                    StatusLabel.Text = "Failed to request user stats from Steam";
                    DebugLogger.LogDebug("LoadStatsAsync: Failed to request user stats from Steam");
                    LoadingRing.IsActive = false;
                }
            }
            catch (Exception ex)
            {
                StatusLabel.Text = $"Error loading stats: {ex.Message}";
                LoadingRing.IsActive = false;
            }
            finally
            {
                _isLoadingStats = false;
            }
        }

        private void OnUserStatsReceived(object? sender, UserStatsReceivedEventArgs e)
        {
            DebugLogger.LogDebug($"MainWindow.OnUserStatsReceived - GameId: {e.GameId}, Result: {e.Result}, UserId: {e.UserId}");
            
            this.DispatcherQueue.TryEnqueue(async () =>
            {
                DebugLogger.LogDebug($"MainWindow.OnUserStatsReceived dispatched to UI thread");
                
                if (e.Result != 1)
                {
                    DebugLogger.LogDebug($"UserStatsReceived failed with result: {e.Result}");
                    StatusLabel.Text = $"Error retrieving stats: {GetErrorDescription(e.Result)}";
                    return;
                }

                string currentLanguage = LanguageComboBox.SelectedItem as string ?? "english";
                DebugLogger.LogDebug($"Loading schema for language: {currentLanguage}");
                
                if (!_gameStatsService.LoadUserGameStatsSchema(currentLanguage))
                {
                    StatusLabel.Text = "Failed to load game schema";
                    return;
                }

                DebugLogger.LogDebug("Loading achievements and statistics...");
                LoadingRing.IsActive = true;

                foreach (var achievement in _allAchievements)
                {
                    achievement.PropertyChanged -= OnAchievementPropertyChanged;
                }

                _allAchievements = _gameStatsService.GetAchievements().ToList();

                foreach (var achievement in _allAchievements)
                {
                    if (_achievementCounters.TryGetValue(achievement.Id, out int counter))
                    {
                        achievement.Counter = counter;
                    }
                    achievement.OriginalIsAchieved = achievement.IsAchieved;
                    achievement.PropertyChanged += OnAchievementPropertyChanged;
                }

                await LoadAchievements();
                LoadStatistics();

                // Restore timer display after loading achievements
                UpdateScheduledTimesDisplay();

                DebugLogger.LogDebug($"UI updated - {_achievements.Count} achievements, {_statistics.Count} statistics");
                StatusLabel.Text = $"Retrieved {_achievements.Count} achievements and {_statistics.Count} statistics";

                // Start loading achievement icons
                await LoadAchievementIconsAsync();

                // Notify timer service that stats have been reloaded
                _achievementTimerService?.NotifyStatsReloaded();

                LoadingRing.IsActive = false;
            });
        }

        private async Task LoadAchievements()
        {
            StatusLabel.Text = "Filtering achievements...";

            string searchText = SearchTextBox.Text ?? string.Empty;
            bool showLockedOnly = ShowLockedButton.IsChecked == true;
            bool showUnlockedOnly = ShowUnlockedButton.IsChecked == true;

            var filtered = new List<AchievementInfo>();
            int total = _allAchievements.Count;

            await Task.Run(() =>
            {
                int processed = 0;
                foreach (var achievement in _allAchievements)
                {
                    bool matchesSearch = string.IsNullOrEmpty(searchText) ||
                        achievement.Name.IndexOf(searchText, StringComparison.OrdinalIgnoreCase) >= 0 ||
                        achievement.Description.IndexOf(searchText, StringComparison.OrdinalIgnoreCase) >= 0;

                    bool matchesFilter = (!showLockedOnly && !showUnlockedOnly) ||
                        (showLockedOnly && !achievement.IsAchieved) ||
                        (showUnlockedOnly && achievement.IsAchieved);

                    if (matchesSearch && matchesFilter)
                    {
                        filtered.Add(achievement);
                    }

                    processed++;
                    if (processed % 50 == 0)
                    {
                        int progress = processed;
                        this.DispatcherQueue.TryEnqueue(() =>
                        {
                            StatusLabel.Text = $"Filtering achievements... {progress}/{total}";
                        });
                    }
                }
            });

            _achievements.Clear();
            foreach (var achievement in filtered)
            {
                _achievements.Add(achievement);
            }
        }

        private void LoadStatistics()
        {
            _statistics.Clear();
            var stats = _gameStatsService.GetStatistics();
            
            foreach (var stat in stats)
            {
                _statistics.Add(stat);
            }

            // Enable/disable the stats editing checkbox based on whether there are any statistics
            bool hasStatistics = _statistics.Count > 0;
            EnableStatsEditingCheckBox.IsEnabled = hasStatistics;
            if (!hasStatistics)
            {
                EnableStatsEditingCheckBox.IsChecked = false;
                EnableStatsEditingCheckBox.Content = "No statistics available for this game";
            }
            else
            {
                EnableStatsEditingCheckBox.Content = "I understand that modifying stats can cause issues";
            }
        }

        private async void OnRefresh(object sender, RoutedEventArgs e)
        {
            await LoadStatsAsync();
        }

        private async void OnStore(object sender, RoutedEventArgs e)
        {
            // Get selected achievements
            var selectedAchievements = AchievementListView.SelectedItems
                .OfType<AchievementInfo>()
                .Where(a => !a.IsProtected)
                .ToList();

            if (selectedAchievements.Count == 0)
            {
                ShowErrorDialog("Please select unprotected achievements to toggle");
                return;
            }

            // Check for timer conflicts
            var timerConflicts = selectedAchievements
                .Where(a => _achievementTimerService?.GetScheduledTime(a.Id) != null)
                .ToList();

            if (timerConflicts.Count > 0)
            {
                ShowErrorDialog($"Cannot store changes for achievements with active timers: {string.Join(", ", timerConflicts.Select(a => a.Id))}");
                return;
            }

            // Check for achieved -> unachieved changes and confirm
            var achievedToUnachieved = selectedAchievements
                .Where(a => a.IsAchieved)
                .ToList();

            if (achievedToUnachieved.Count > 0)
            {
                var result = await ShowConfirmationDialog(
                    "Confirm Achievement Reset", 
                    $"Are you sure you want to reset {achievedToUnachieved.Count} achieved achievement(s) to unachieved?\n\nThis action cannot be easily undone.");

                if (result != Microsoft.UI.Xaml.Controls.ContentDialogResult.Primary)
                {
                    return;
                }
            }

            // Show loading indicator and disable Store button to prevent multiple clicks
            LoadingRing.IsActive = true;
            StoreButton.IsEnabled = false;
            StatusLabel.Text = "Storing changes...";
            
            try
            {
                await Task.Run(() => PerformStoreToggle(selectedAchievements));
            }
            finally
            {
                LoadingRing.IsActive = false;
                StoreButton.IsEnabled = true;
            }
        }

        private void PerformStoreToggle(List<AchievementInfo> selectedAchievements)
        {
            try
            {
                DebugLogger.LogDebug($"PerformStoreToggle called for {selectedAchievements.Count} achievements");
                DebugLogger.LogDebug($"Debug mode: {DebugLogger.IsDebugMode}");
                
                int achievementCount = 0;
                foreach (var achievement in selectedAchievements)
                {
                    // Toggle the achievement state
                    bool newState = !achievement.IsAchieved;
                    DebugLogger.LogDebug($"Achievement {achievement.Id} toggle: {achievement.IsAchieved} -> {newState}");
                    
                    if (!_gameStatsService.SetAchievement(achievement.Id, newState))
                    {
                        this.DispatcherQueue.TryEnqueue(() =>
                        {
                            ShowErrorDialog($"Failed to set achievement '{achievement.Id}'");
                        });
                        return;
                    }
                    
                    achievementCount++;
                }
                
                // Store statistics (if any were modified)
                int statCount = StoreStatistics(true);
                
                // Store changes to Steam
                bool success = _gameStatsService.StoreStats();
                DebugLogger.LogDebug($"StoreStats result: {success}");
                
                // Update UI on main thread
                this.DispatcherQueue.TryEnqueue(() =>
                {
                    if (DebugLogger.IsDebugMode)
                    {
                        StatusLabel.Text = $"[DEBUG MODE] Fake toggled {achievementCount} achievements and {Math.Max(0, statCount)} statistics (not written to Steam). Refreshing to show actual state...";
                        
                        // Even in debug mode, reload to show Steam's actual state
                        _ = Task.Run(async () =>
                        {
                            await Task.Delay(500);
                            this.DispatcherQueue.TryEnqueue(async () => await LoadStatsAsync());
                        });
                    }
                    else
                    {
                        StatusLabel.Text = $"Successfully toggled {achievementCount} achievements and {Math.Max(0, statCount)} statistics to Steam. Refreshing...";
                        
                        // Reload data from Steam after successful store
                        _ = Task.Run(async () =>
                        {
                            await Task.Delay(500);
                            this.DispatcherQueue.TryEnqueue(async () => await LoadStatsAsync());
                        });
                    }
                });
            }
            catch (Exception ex)
            {
                DebugLogger.LogDebug($"Error in PerformStoreToggle: {ex.Message}");
                this.DispatcherQueue.TryEnqueue(() =>
                {
                    StatusLabel.Text = $"Error: {ex.Message}";
                });
            }
        }

        private void PerformStore(bool silent)
        {
            try
            {
                DebugLogger.LogDebug($"PerformStore called (silent: {silent})");
                
                int achievementCount = StoreAchievements(silent);
                if (achievementCount < 0) return;

                int statCount = StoreStatistics(silent);
                if (statCount < 0) return;

                if (!_gameStatsService.StoreStats())
                {
                    if (!silent)
                    {
                        ShowErrorDialog("Failed to store stats to Steam");
                    }
                    return;
                }

                if (!silent)
                {
                    // Ensure UI updates happen on the UI thread
                    this.DispatcherQueue.TryEnqueue(() =>
                    {
                        if (DebugLogger.IsDebugMode)
                        {
                            StatusLabel.Text = $"[DEBUG MODE] Fake stored {achievementCount} achievements and {statCount} statistics (not written to Steam). Refreshing to show actual state...";
                            // Refresh in debug mode to show that changes weren't actually applied (like Legacy SAM.Game)
                            _ = Task.Run(async () =>
                            {
                                await Task.Delay(500); // Brief delay to show status message
                                this.DispatcherQueue.TryEnqueue(async () => await LoadStatsAsync());
                            });
                        }
                        else
                        {
                            StatusLabel.Text = $"Successfully stored {achievementCount} achievements and {statCount} statistics to Steam. Refreshing...";
                            // Reload data from Steam after successful store (like Legacy SAM.Game)
                            _ = Task.Run(async () =>
                            {
                                await Task.Delay(500); // Brief delay to allow Steam to update
                                this.DispatcherQueue.TryEnqueue(async () => await LoadStatsAsync());
                            });
                        }
                    });
                }
                else
                {
                    // Silent mode: always refresh after store (like Legacy SAM.Game)
                    _ = LoadStatsAsync();
                }
            }
            catch (Exception ex)
            {
                DebugLogger.LogDebug($"Error in PerformStore: {ex.Message}");
                if (!silent)
                {
                    this.DispatcherQueue.TryEnqueue(() =>
                    {
                        ShowErrorDialog($"Error storing stats: {ex.Message}");
                    });
                }
            }
        }

        private int StoreAchievements(bool silent)
        {
            int count = 0;
            
            // Get all modified achievements
            var modifiedAchievements = _achievements.Where(a => a.IsModified).ToList();
            
            if (modifiedAchievements.Count == 0)
                return 0;
            
            // Sort achievements by their statistic requirements to ensure proper ordering
            var sortedAchievements = SortAchievementsByStatisticDependency(modifiedAchievements);
            
            foreach (var achievement in sortedAchievements)
            {
                DebugLogger.LogDebug($"Achievement {achievement.Id} modified: {achievement.OriginalIsAchieved} -> {achievement.IsAchieved}");
                
                if (!_gameStatsService.SetAchievement(achievement.Id, achievement.IsAchieved))
                {
                    if (!silent)
                    {
                        ShowErrorDialog($"Failed to set achievement '{achievement.Id}'");
                    }
                    return -1;
                }
                
                // Update original state after successful write
                achievement.OriginalIsAchieved = achievement.IsAchieved;
                count++;
            }
            
            return count;
        }
        
        private List<AchievementInfo> SortAchievementsByStatisticDependency(List<AchievementInfo> achievements)
        {
            // Define the same mapping as in GameStatsService for consistency
            var achievementStatMap = new Dictionary<string, (string statId, int requiredValue)>
            {
                { "DestroyXUnits", ("DestroyXUnits_Stat", 5000) },
                { "037_DestroyXUnitsLow", ("DestroyXUnits_Stat", 2500) },
                { "WinXSkirmishGames", ("WinXSkirmishGames_Stat", 10) },
                { "PillageXLocations", ("PillageXLocations_Stat", 100) },
                { "RebuildXLocations", ("RebuildXLocations_Stat", 100) },
                { "GetXLocationsToMaxProsperity", ("GetXLocationsToMaxProsperity_Stat", 500) },
                { "BuildXBuildings", ("BuildXBuildings_Stat", 500) },
                { "RecruitXUnits", ("RecruitXUnits_Stat", 2000) },
                { "PlayXCombatCards", ("PlayXCombatCards_Stat", 6000) },
                { "FindWanderingEruditeOnAllMaps", ("FindWanderingEruditeOnAllMaps_Stat", 8) },
                { "FinishCampaignOnHardDifficulty", ("FinishCampaignOnHardDifficulty_Stat", 1) },
                { "UnlockXLexicanumEntries", ("UnlockXLexicanumEntries_Stat", 999) }
            };
            
            // Sort achievements: those with lower stat requirements first
            return achievements.OrderBy(a =>
            {
                if (achievementStatMap.TryGetValue(a.Id, out var statInfo))
                {
                    // Return the required value for sorting (lower values first)
                    return statInfo.requiredValue;
                }
                // Achievements without stat requirements go first
                return 0;
            }).ToList();
        }

        private int StoreStatistics(bool silent)
        {
            int count = 0;
            
            foreach (var stat in _statistics.Where(s => s.IsModified))
            {
                if (!_gameStatsService.SetStatistic(stat))
                {
                    if (!silent)
                    {
                        ShowErrorDialog($"Failed to set statistic '{stat.Id}'");
                    }
                    return -1;
                }
                count++;
            }
            
            return count;
        }

        private void OnLockAll(object sender, RoutedEventArgs e)
        {
            DebugLogger.LogDebug("Select all unlocked button clicked");
            
            // Clear current selection
            AchievementListView.SelectedItems.Clear();
            
            // Select all unlocked achievements that are not protected
            var unlockedAchievements = _achievements
                .Where(a => !a.IsProtected && a.IsAchieved)
                .ToList();
            
            foreach (var achievement in unlockedAchievements)
            {
                AchievementListView.SelectedItems.Add(achievement);
            }
            
            DebugLogger.LogDebug($"Selected {unlockedAchievements.Count} unlocked achievements");
        }

        private void OnUnlockAll(object sender, RoutedEventArgs e)
        {
            DebugLogger.LogDebug("Select all locked button clicked");
            
            // Clear current selection
            AchievementListView.SelectedItems.Clear();
            
            // Select all locked achievements that are not protected
            var lockedAchievements = _achievements
                .Where(a => !a.IsProtected && !a.IsAchieved)
                .ToList();
            
            foreach (var achievement in lockedAchievements)
            {
                AchievementListView.SelectedItems.Add(achievement);
            }
            
            DebugLogger.LogDebug($"Selected {lockedAchievements.Count} locked achievements");
        }

        private void OnInvertAll(object sender, RoutedEventArgs e)
        {
            DebugLogger.LogDebug("Select All button clicked");
            
            // Clear current selection
            AchievementListView.SelectedItems.Clear();
            
            // Select all achievements that are not protected
            var selectableAchievements = _achievements
                .Where(a => !a.IsProtected)
                .ToList();
            
            foreach (var achievement in selectableAchievements)
            {
                AchievementListView.SelectedItems.Add(achievement);
            }
            
            DebugLogger.LogDebug($"Selected {selectableAchievements.Count} achievements");
        }


        private void OnSearchTextChanged(object sender, TextChangedEventArgs e)
        {
            _searchDebounceTimer.Stop();
            _searchDebounceTimer.Start();
        }

        private async void OnShowLockedToggle(object sender, RoutedEventArgs e)
        {
            if (ShowLockedButton.IsChecked == true && ShowUnlockedButton.IsChecked == true)
            {
                ShowUnlockedButton.IsChecked = false;
            }
            await LoadAchievements();
        }

        private async void OnShowUnlockedToggle(object sender, RoutedEventArgs e)
        {
            if (ShowLockedButton.IsChecked == true && ShowUnlockedButton.IsChecked == true)
            {
                ShowLockedButton.IsChecked = false;
            }
            await LoadAchievements();
        }

        private async void OnLanguageChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_gameStatsService == null)
                return;

            string currentLanguage = LanguageComboBox.SelectedItem as string ?? "english";

            LoadingRing.IsActive = true;

            foreach (var achievement in _allAchievements)
            {
                achievement.PropertyChanged -= OnAchievementPropertyChanged;
            }

            if (!_gameStatsService.LoadUserGameStatsSchema(currentLanguage))
            {
                StatusLabel.Text = "Failed to load game schema";
                LoadingRing.IsActive = false;
                return;
            }

            _allAchievements = _gameStatsService.GetAchievements().ToList();

            foreach (var achievement in _allAchievements)
            {
                if (_achievementCounters.TryGetValue(achievement.Id, out int counter))
                {
                    achievement.Counter = counter;
                }
                achievement.OriginalIsAchieved = achievement.IsAchieved;
                achievement.PropertyChanged += OnAchievementPropertyChanged;
            }

            await LoadAchievements();
            LoadStatistics();
            UpdateScheduledTimesDisplay();
            await LoadAchievementIconsAsync();
            _achievementTimerService?.NotifyStatsReloaded();

            LoadingRing.IsActive = false;
        }

        private void OnColumnLayoutChanged(object sender, SelectionChangedEventArgs e)
        {
            if (ColumnLayoutComboBox.SelectedItem is string layout)
            {
                ApplyColumnLayout(layout);
            }
        }

        private void ApplyColumnLayout(string layout)
        {
            DebugLogger.LogDebug($"Column layout changed to: {layout}");
            
            // For WinUI 3 limitations, we'll implement a practical solution
            // Show user feedback and provide information about the column layout feature
            StatusLabel.Text = $"Column layout set to: {layout}. Use scroll and zoom for better viewing.";
            
            // In a production implementation, you could:
            // 1. Save the preference to user settings
            // 2. Create multiple XAML DataTemplate resources and switch between them
            // 3. Use a third-party DataGrid control that supports resizable columns
            // 4. Implement a custom ListView with resizable column headers
            
            // For now, provide users with information about the current limitations
            if (layout == "Compact")
            {
                StatusLabel.Text = "Compact view selected. Tip: Use Ctrl+Mouse wheel to zoom for better readability.";
            }
            else if (layout == "Extra Wide")
            {
                StatusLabel.Text = "Extra Wide view selected. Tip: Use horizontal scroll to see all columns.";
            }
        }

        private void OnStatsEditingToggle(object sender, RoutedEventArgs e)
        {
            // Enable/disable statistics editing
        }

        private async void OnSetTimer(object sender, RoutedEventArgs e)
        {
            // Set timer for selected achievements (only allow unachieved -> achieved)
            var selectedAchievements = AchievementListView.SelectedItems
                .OfType<AchievementInfo>()
                .Where(a => !a.IsAchieved && !a.IsProtected)
                .ToList();

            // Check if user tried to select achieved achievements
            var achievedSelected = AchievementListView.SelectedItems
                .OfType<AchievementInfo>()
                .Where(a => a.IsAchieved)
                .ToList();

            if (achievedSelected.Count > 0)
            {
                ShowErrorDialog("Timer can only be set for unachieved achievements. To change achieved achievements to unachieved, use direct Store operation.");
                return;
            }

            if (selectedAchievements.Count == 0)
            {
                ShowErrorDialog("Please select unachieved, unprotected achievements to schedule");
                return;
            }

            try
            {
                // Create the dialog content
                var dialogContent = CreateTimerDialogContent(selectedAchievements);
                
                // Show a content dialog to set the unlock time
                var dialog = new ContentDialog
                {
                    Title = "Set Achievement Unlock Time",
                    Content = dialogContent,
                    PrimaryButtonText = "Set Timer",
                    CloseButtonText = "Cancel",
                    XamlRoot = this.Content.XamlRoot
                };

                var result = await dialog.ShowAsync();
                
                if (result == ContentDialogResult.Primary)
                {
                    // Extract the controls from our dialog content
                    var datePicker = (DatePicker)dialogContent.Children[3];
                    var timePicker = (TimePicker)dialogContent.Children[5];
                    var secondsBox = (NumberBox)dialogContent.Children[7];

                    var selectedDate = datePicker.Date.Date;
                    var selectedTime = timePicker.Time;
                    
                    // Add seconds from NumberBox
                    int seconds = (int)(secondsBox.Value);
                    var unlockTime = selectedDate.Add(selectedTime).AddSeconds(seconds);

                    if (unlockTime <= DateTime.Now)
                    {
                        ShowErrorDialog("Unlock time must be in the future");
                        return;
                    }

                    foreach (var achievement in selectedAchievements)
                    {
                        achievement.ScheduledUnlockTime = unlockTime;
                        _achievementTimerService?.ScheduleAchievement(achievement.Id, unlockTime);
                    }
                    var formattedTime = unlockTime.ToString("yyyy-MM-dd HH:mm:ss");
                    StatusLabel.Text =
                        $"Scheduled {selectedAchievements.Count} achievement(s) to unlock at {formattedTime}";
                }
                else
                {
                    DebugLogger.LogDebug("Set Timer dialog was cancelled by user");
                }
            }
            catch (Exception ex)
            {
                DebugLogger.LogDebug($"Error in OnSetTimer: {ex.Message}");
                ShowErrorDialog($"Error setting timer: {ex.Message}");
            }
        }

        private void OnResetAllTimers(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_achievementTimerService == null)
                {
                    ShowErrorDialog("Timer service is not available");
                    return;
                }

                var scheduledAchievements = _achievementTimerService.GetAllScheduledAchievements();
                if (scheduledAchievements.Count == 0)
                {
                    ShowErrorDialog("No active timers to reset");
                    return;
                }

                foreach (var achievementId in scheduledAchievements.Keys)
                {
                    _achievementTimerService.CancelSchedule(achievementId);
                }

                StatusLabel.Text = $"Reset {scheduledAchievements.Count} active timer(s)";
                DebugLogger.LogDebug($"Reset all timers - {scheduledAchievements.Count} timers cancelled");

                // Update UI to reflect timer status
                UpdateScheduledTimesDisplay();
            }
            catch (Exception ex)
            {
                DebugLogger.LogDebug($"Error in OnResetAllTimers: {ex.Message}");
            }
        }

        private void OnResetSelectedTimers(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_achievementTimerService == null)
                {
                    ShowErrorDialog("Timer service is not available");
                    return;
                }

                var selectedAchievements = AchievementListView.SelectedItems
                    .OfType<AchievementInfo>()
                    .ToList();

                if (selectedAchievements.Count == 0)
                {
                    ShowErrorDialog("Please select achievements to reset their timers");
                    return;
                }

                int resetCount = 0;
                foreach (var achievement in selectedAchievements)
                {
                    var scheduledTime = _achievementTimerService.GetScheduledTime(achievement.Id);
                    if (scheduledTime.HasValue)
                    {
                        _achievementTimerService.CancelSchedule(achievement.Id);
                        achievement.ScheduledUnlockTime = null;
                        resetCount++;
                    }
                }

                if (resetCount == 0)
                {
                    ShowErrorDialog("None of the selected achievements have active timers");
                    return;
                }

                StatusLabel.Text = $"Reset {resetCount} timer(s) for selected achievements";
                DebugLogger.LogDebug($"Reset selected timers - {resetCount} timers cancelled");

                // Update UI to reflect timer status
                UpdateScheduledTimesDisplay();
            }
            catch (Exception ex)
            {
                DebugLogger.LogDebug($"Error in OnResetSelectedTimers: {ex.Message}");
            }
        }

        private void UpdateScheduledTimesDisplay()
        {
            if (_achievementTimerService == null) return;

            var scheduledAchievements = _achievementTimerService.GetAllScheduledAchievements();
            
            foreach (var achievement in _achievements)
            {
                if (scheduledAchievements.TryGetValue(achievement.Id, out var scheduledTime))
                {
                    achievement.ScheduledUnlockTime = scheduledTime;
                }
                else
                {
                    achievement.ScheduledUnlockTime = null;
                }
            }
        }

        private Grid CreateTimerDialogContent(List<AchievementInfo> achievements)
        {
            var grid = new Grid();
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var instructions = new TextBlock
            {
                Text = "Select the date and time when the achievement should be unlocked:",
                Margin = new Thickness(0, 0, 0, 10),
                TextWrapping = TextWrapping.Wrap
            };
            Grid.SetRow(instructions, 0);

            // Add achievement information display
            var achievementHeader = new TextBlock
            {
                Text = $"Achievements to be scheduled ({achievements.Count}):",
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 5)
            };
            Grid.SetRow(achievementHeader, 1);

            var achievementList = new TextBlock
            {
                Text = string.Join("\n", achievements.Take(5).Select(a => $"• {a.Id}: {a.Name}")) + 
                       (achievements.Count > 5 ? $"\n... and {achievements.Count - 5} more" : ""),
                FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"),
                FontSize = 12,
                Margin = new Thickness(0, 0, 0, 15),
                TextWrapping = TextWrapping.Wrap,
                MaxHeight = 120,
                IsTextSelectionEnabled = true
            };
            Grid.SetRow(achievementList, 2);

            var defaultTime = DateTime.Now.AddHours(1);
            var datePicker = new DatePicker
            {
                Date = defaultTime.Date,
                Margin = new Thickness(0, 0, 0, 10)
            };
            Grid.SetRow(datePicker, 3);

            var timeLabel = new TextBlock
            {
                Text = "Time:",
                Margin = new Thickness(0, 0, 0, 5)
            };
            Grid.SetRow(timeLabel, 4);

            var timePicker = new TimePicker
            {
                Time = defaultTime.TimeOfDay,
                Margin = new Thickness(0, 0, 0, 10)
            };
            Grid.SetRow(timePicker, 5);

            var secondsLabel = new TextBlock
            {
                Text = "Seconds (0-59):",
                Margin = new Thickness(0, 0, 0, 5)
            };
            Grid.SetRow(secondsLabel, 6);

            var secondsBox = new NumberBox
            {
                Value = 0,
                Minimum = 0,
                Maximum = 59,
                SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline
            };
            Grid.SetRow(secondsBox, 7);

            grid.Children.Add(instructions);
            grid.Children.Add(achievementHeader);
            grid.Children.Add(achievementList);
            grid.Children.Add(datePicker);
            grid.Children.Add(timeLabel);
            grid.Children.Add(timePicker);
            grid.Children.Add(secondsLabel);
            grid.Children.Add(secondsBox);

            return grid;
        }

        private void OnAutoMouseMove(object sender, RoutedEventArgs e)
        {
            if (_mouseMoverService != null)
            {
                bool enabled = AutoMouseMoveButton.IsChecked == true;
                _mouseMoverService.IsEnabled = enabled;
                AutoMouseMoveButton.Label = enabled ? "Stop Auto Mouse" : "Auto Mouse";
                AutoMouseMoveButton.Icon = enabled ? new SymbolIcon(Symbol.Pause) : new SymbolIcon(Symbol.Target);
                ToolTipService.SetToolTip(AutoMouseMoveButton, enabled ? "Stop Auto Mouse" : "Auto Mouse");
                DebugLogger.LogDebug($"Auto mouse movement {(enabled ? "enabled" : "disabled")}");
            }
        }

        private void OnTimerToggle(object sender, RoutedEventArgs e)
        {
            if (TimerToggleButton.IsChecked == true)
            {
                _achievementTimer.Start();
                TimerToggleButton.Label = "Disable Timer";
                UpdateTimerStatusIndicator(true);
            }
            else
            {
                _achievementTimer.Stop();
                TimerToggleButton.Label = "Enable Timer";
                UpdateTimerStatusIndicator(false);
            }
        }

        private void OnTimeTimerTick(object? sender, object e)
        {
            CurrentTimeLabel.Text = $"Current Time: {DateTime.Now:yyyy/MM/dd HH:mm:ss}";
        }

        private void OnAchievementTimerTick(object? sender, object e)
        {
            bool shouldStore = false;
            TimerStatusLabel.Text = DateTime.Now.Second % 2 == 0 ? "*" : "-";

            foreach (var achievement in _achievements.Where(a => a.Counter > 0))
            {
                achievement.Counter--;
                _achievementCounters[achievement.Id] = achievement.Counter;

                if (achievement.Counter == 0)
                {
                    achievement.IsAchieved = true;
                    achievement.Counter = -1;
                    _achievementCounters[achievement.Id] = -1;
                    shouldStore = true;
                }
            }

            if (shouldStore)
            {
                PerformStore(true);
            }
        }

        private void OnMouseTimerTick(object? sender, object e)
        {
            // Simple mouse jiggle to prevent idle using Win32 API
            GetCursorPos(out var currentPos);
            var newX = _lastMouseMoveRight ? currentPos.X + 1 : currentPos.X - 1;
            SetCursorPos(newX, currentPos.Y);
            _lastMouseMoveRight = !_lastMouseMoveRight;
        }

        [DllImport("user32.dll")]
        private static extern bool SetCursorPos(int X, int Y);

        [DllImport("user32.dll")]
        private static extern bool GetCursorPos(out POINT lpPoint);

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int X;
            public int Y;
        }

        private async Task<Microsoft.UI.Xaml.Controls.ContentDialogResult> ShowConfirmationDialog(string title, string message)
        {
            var dialog = new Microsoft.UI.Xaml.Controls.ContentDialog
            {
                Title = title,
                Content = message,
                PrimaryButtonText = "Yes",
                SecondaryButtonText = "No",
                DefaultButton = Microsoft.UI.Xaml.Controls.ContentDialogButton.Secondary,
                XamlRoot = this.Content.XamlRoot
            };

            return await dialog.ShowAsync();
        }

        private void ShowErrorDialog(string message)
        {
            try
            {
                this.DispatcherQueue.TryEnqueue(() =>
                {
                    StatusLabel.Text = $"Error: {message}";
                    DebugLogger.LogDebug($"Error dialog: {message}");
                });
            }
            catch (Exception ex)
            {
                DebugLogger.LogDebug($"Error showing error dialog: {ex.Message}");
            }
        }

        private static string GetErrorDescription(int errorCode)
        {
            return errorCode switch
            {
                1 => "Success",
                2 => "Generic error - game may not be running, data not synchronized, or access denied",
                3 => "No connection to Steam",
                4 => "Invalid user",
                5 => "Invalid app ID",
                6 => "Invalid state",
                7 => "Invalid parameter",
                8 => "Not logged in",
                9 => "Wrong user",
                10 => "Invalid version",
                _ => $"Unknown error code: {errorCode}"
            };
        }

        private void TestGameOwnership()
        {
            try
            {
                // Check if user owns the game
                bool ownsGame = _steamClient.IsSubscribedApp((uint)_gameId);
                DebugLogger.LogDebug($"Game ownership check for {_gameId}: {ownsGame}");
                
                // Check if we can get game name
                string? gameName = _steamClient.GetAppData((uint)_gameId, "name");
                DebugLogger.LogDebug($"Game name: {gameName ?? "Unknown"}");
                
                // Check if we can get other game data
                string? gameType = _steamClient.GetAppData((uint)_gameId, "type");
                DebugLogger.LogDebug($"Game type: {gameType ?? "Unknown"}");
                
                string? gameState = _steamClient.GetAppData((uint)_gameId, "state");
                DebugLogger.LogDebug($"Game state: {gameState ?? "Unknown"}");
                
                if (!ownsGame)
                {
                    StatusLabel.Text = "Warning: Steam reports you don't own this game";
                }
            }
            catch (Exception ex)
            {
                DebugLogger.LogDebug($"Error testing game ownership: {ex.Message}");
            }
        }

        private void OnClearLog(object sender, RoutedEventArgs e)
        {
            DebugLogger.ClearLog();
            DebugLogger.LogDebug("Debug log cleared by user");
            StatusLabel.Text = "Debug log cleared";
        }

        private async Task LoadAchievementIconsAsync()
        {
            if (_achievementIconService == null) return;

            try
            {
                var achievements = _achievements.ToList();
                int total = achievements.Count;
                int processed = 0;
                const int batchSize = 10;

                foreach (var batch in achievements.Chunk(batchSize))
                {
                    var tasks = batch.Select(async achievement =>
                    {
                        string iconFileName = achievement.IsAchieved || string.IsNullOrEmpty(achievement.IconLocked)
                            ? achievement.IconNormal
                            : achievement.IconLocked;

                        if (string.IsNullOrEmpty(iconFileName)) return;

                        try
                        {
                            var iconPath = await _achievementIconService.GetAchievementIconAsync(
                                achievement.Id, iconFileName, achievement.IsAchieved);

                            if (!string.IsNullOrEmpty(iconPath))
                            {
                                this.DispatcherQueue?.TryEnqueue(() =>
                                {
                                    try
                                    {
                                        achievement.IconImage = new BitmapImage(new Uri(iconPath));
                                    }
                                    catch (Exception ex)
                                    {
                                        DebugLogger.LogDebug($"Error creating BitmapImage for {achievement.Id}: {ex.Message}");
                                    }
                                });
                            }
                        }
                        catch (Exception ex)
                        {
                            DebugLogger.LogDebug($"Error loading icon for {achievement.Id}: {ex.Message}");
                        }
                    });

                    await Task.WhenAll(tasks);
                    processed += batch.Length;

                    int progress = processed;
                    this.DispatcherQueue?.TryEnqueue(() =>
                    {
                        if (StatusLabel != null)
                        {
                            StatusLabel.Text = $"Loading icons... {progress}/{total}";
                        }
                    });

                    await Task.Delay(1);
                }

                DebugLogger.LogDebug($"Finished loading icons for {total} achievements");
            }
            catch (Exception ex)
            {
                DebugLogger.LogDebug($"Error loading achievement icons: {ex.Message}");
            }
        }

        private async void OnAchievementPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (_achievementIconService == null) return;
            if (e.PropertyName != nameof(AchievementInfo.IsAchieved)) return;
            if (sender is not AchievementInfo achievement) return;

            string iconFileName = achievement.IsAchieved || string.IsNullOrEmpty(achievement.IconLocked)
                ? achievement.IconNormal
                : achievement.IconLocked;
            if (string.IsNullOrEmpty(iconFileName)) return;

            try
            {
                var iconPath = await _achievementIconService.GetAchievementIconAsync(
                    achievement.Id, iconFileName, achievement.IsAchieved);

                if (!string.IsNullOrEmpty(iconPath))
                {
                    this.DispatcherQueue?.TryEnqueue(() =>
                    {
                        try
                        {
                            achievement.IconImage = new BitmapImage(new Uri(iconPath));
                        }
                        catch (Exception ex)
                        {
                            DebugLogger.LogDebug($"Error creating BitmapImage for {achievement.Id}: {ex.Message}");
                        }
                    });
                }
            }
            catch (Exception ex)
            {
                DebugLogger.LogDebug($"Error updating icon for {achievement.Id}: {ex.Message}");
            }
        }

        private void UpdateTimerStatusIndicator(bool isActive)
        {
            if (isActive)
            {
                TimerStatusText.Text = "🟢 Timer On";
                TimerStatusText.Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Green);
            }
            else
            {
                TimerStatusText.Text = "⚪ Timer Off";
                TimerStatusText.Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Gray);
            }
        }

        private void OnTimerStatusUpdated(string status)
        {
            // Update status on UI thread
            DispatcherQueue.TryEnqueue(() =>
            {
                StatusLabel.Text = status;
            });
        }

        /// <summary>
        /// Creates a Steam client, prioritizing Legacy SteamGameClient for game execution simulation
        /// </summary>
        private ISteamUserStats CreateSteamClient(long gameId)
        {
            // Legacy SteamGameClient is required for core functionality:
            // - Simulates game execution to Steam client
            // - Enables achievement data retrieval 
            // - Works without game installation
            try
            {
                DebugLogger.LogDebug("Using Legacy SteamGameClient for Steam execution simulation...");
                var legacyClient = new SteamGameClient(gameId);
                
                if (legacyClient.Initialized)
                {
                    DebugLogger.LogDebug("Legacy SteamGameClient initialized successfully - can simulate game execution");
                    return legacyClient;
                }
                else
                {
                    DebugLogger.LogDebug("Legacy SteamGameClient failed to initialize");
                }
            }
            catch (Exception ex)
            {
                DebugLogger.LogDebug($"Legacy SteamGameClient creation failed: {ex.Message}");
            }
            
            // Fallback to Modern client (for future use when Steam API supports execution simulation)
            try
            {
                DebugLogger.LogDebug("Falling back to ModernSteamClient (limited functionality)...");
                var modernClient = new ModernSteamClient(gameId);
                
                if (modernClient.Initialized)
                {
                    DebugLogger.LogDebug("ModernSteamClient initialized successfully (but cannot simulate game execution)");
                    return modernClient;
                }
                else
                {
                    DebugLogger.LogDebug("ModernSteamClient failed to initialize, disposing...");
                    modernClient.Dispose();
                }
            }
            catch (Exception ex)
            {
                DebugLogger.LogDebug($"ModernSteamClient creation failed: {ex.Message}");
            }
            
            // If both fail, create a non-functional modern client for interface compatibility
            DebugLogger.LogDebug("Both Steam clients failed, creating non-functional ModernSteamClient for compatibility");
            return new ModernSteamClient(gameId);
        }
    }

    // Value converters for XAML binding
    public class InverseBooleanConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            return !(bool)value;
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            return !(bool)value;
        }
    }

    public class NullToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            return value != null ? Visibility.Visible : Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            throw new NotImplementedException();
        }
    }
    
    public class InverseNullToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            return value == null ? Visibility.Visible : Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            throw new NotImplementedException();
        }
    }
    
    public class ProtectionLockConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            if (value is AchievementInfo achievement)
            {
                // Show lock only if achievement is protected AND not achieved
                return achievement.IsProtected && !achievement.IsAchieved ? Visibility.Visible : Visibility.Collapsed;
            }
            return Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            throw new NotImplementedException();
        }
    }
}