using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
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
using CommonUtilities;

namespace RunGame
{
    public partial class MainWindow : Window
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

        // New services
        private AchievementTimerService? _achievementTimerService;
        private MouseMoverService? _mouseMoverService;
        private AchievementIconService? _achievementIconService;

        public MainWindow() : this(0) { }

        public MainWindow(long gameId)
        {
            InitializeComponent();

            _gameId = gameId;

            // Set window icon
            var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "RunGame.ico");
            if (File.Exists(iconPath))
                Icon = new WindowIcon(iconPath);

            // Set Steam AppID environment variable - some games require this
            Environment.SetEnvironmentVariable("SteamAppId", gameId.ToString());
            AppLogger.LogDebug($"Set SteamAppId environment variable to {gameId}");

            // Try modern Steam client first, fallback to legacy if needed
            _steamClient = CreateSteamClient(gameId);

            _gameStatsService = new GameStatsService(_steamClient, gameId);

            // Initialize timers
            _callbackTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
            _callbackTimer.Tick += (_, _) =>
            {
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
                try
                {
                    _searchDebounceTimer.Stop();
                    await LoadAchievements();
                }
                catch (Exception ex)
                {
                    AppLogger.LogDebug($"Error in search debounce timer: {ex.GetType().Name}: {ex.Message}");
                    AppLogger.LogDebug($"Stack trace: {ex.StackTrace}");
                    StatusLabel.Text = $"Search error: {ex.Message}";
                }
            };

            // Set up event handlers
            _gameStatsService.UserStatsReceived += OnUserStatsReceived;

            // Set window title
            string gameName = _steamClient.GetAppData((uint)gameId, "name") ?? gameId.ToString();
            string debugMode = AppLogger.IsDebugMode ? " [DEBUG MODE]" : "";
            this.Title = $"AchievoLab:RunGame | {gameName}{debugMode}";

            // Initialize language options
            InitializeLanguageComboBox();

            // Initialize column layout options
            InitializeColumnLayoutComboBox();

            // Set up list views
            AchievementListView.ItemsSource = _achievements;
            StatisticsListView.ItemsSource = _statistics;

            // Subscribe to search text changed
            SearchTextBox.TextChanged += OnSearchTextChanged;

            // Setup Debug mode label
            if (AppLogger.IsDebugMode)
            {
                DebugModeLabel.Text = "DEBUG MODE";
                ClearLogButton.IsVisible = true;
            }
            else
            {
                ClearLogButton.IsVisible = false;
            }

            AppLogger.LogDebug($"RunGame started for game {gameId} in {(AppLogger.IsDebugMode ? "DEBUG" : "RELEASE")} mode");

            // Initialize new services
            _achievementTimerService = new AchievementTimerService(_gameStatsService);
            _achievementTimerService.StatusUpdated += OnTimerStatusUpdated;
            _achievementTimerService.AchievementUnlocked += OnTimerAchievementUnlocked;

            // Get window handle for mouse service
            _mouseMoverService = new MouseMoverService(IntPtr.Zero); // Will be updated when window is shown

            // Initialize icon service
            _achievementIconService = new AchievementIconService(gameId);

            // Test game ownership before loading stats
            TestGameOwnership();

            // Start loading stats
            _ = LoadStatsAsync();

            // Subscribe to window events
            this.Closing += OnWindowClosing;
            this.Opened += OnWindowOpened;
        }

        private void OnWindowOpened(object? sender, EventArgs e)
        {
            // Update mouse mover service with actual window handle
            var handle = this.TryGetPlatformHandle();
            if (handle != null && _mouseMoverService != null)
            {
                _mouseMoverService.Dispose();
                _mouseMoverService = new MouseMoverService(handle.Handle);
            }
        }

        private void OnWindowClosing(object? sender, WindowClosingEventArgs e)
        {
            try
            {
                // Unsubscribe event handlers to prevent leaks
                _gameStatsService.UserStatsReceived -= OnUserStatsReceived;
                if (_achievementTimerService != null)
                {
                    _achievementTimerService.StatusUpdated -= OnTimerStatusUpdated;
                    _achievementTimerService.AchievementUnlocked -= OnTimerAchievementUnlocked;
                }

                // Dispose services
                _achievementTimerService?.Dispose();
                _mouseMoverService?.Dispose();
                _achievementIconService?.Dispose();

                // Stop timers
                _callbackTimer.Stop();
                _timeTimer.Stop(); _timeTimer.Tick -= OnTimeTimerTick;
                _achievementTimer.Stop(); _achievementTimer.Tick -= OnAchievementTimerTick;
                _mouseTimer.Stop(); _mouseTimer.Tick -= OnMouseTimerTick;
                _searchDebounceTimer.Stop();

                // Dispose Steam client to release Steam pipe and user handles
                if (_steamClient is IDisposable disposableSteamClient)
                {
                    AppLogger.LogDebug("Disposing Steam client");
                    disposableSteamClient.Dispose();
                }

                AppLogger.LogDebug("MainWindow closed and resources cleaned up");
            }
            catch (Exception ex)
            {
                AppLogger.LogDebug($"Error during cleanup: {ex.Message}");
            }
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
            LoadingBar.IsVisible = true;
            StatusLabel.Text = "Loading game statistics...";

            try
            {
                AppLogger.LogDebug("LoadStatsAsync: Requesting user stats...");
                bool success = await _gameStatsService.RequestUserStatsAsync();
                AppLogger.LogDebug($"LoadStatsAsync: RequestUserStatsAsync returned {success}");
                if (!success)
                {
                    StatusLabel.Text = "Failed to request user stats from Steam";
                    AppLogger.LogDebug("LoadStatsAsync: Failed to request user stats from Steam");
                    LoadingBar.IsVisible = false;
                }
            }
            catch (Exception ex)
            {
                StatusLabel.Text = $"Error loading stats: {ex.Message}";
                LoadingBar.IsVisible = false;
            }
            finally
            {
                _isLoadingStats = false;
            }
        }

        private void OnUserStatsReceived(object? sender, UserStatsReceivedEventArgs e)
        {
            AppLogger.LogDebug($"MainWindow.OnUserStatsReceived - GameId: {e.GameId}, Result: {e.Result}, UserId: {e.UserId}");

            Dispatcher.UIThread.Post(async () =>
            {
                try
                {
                    AppLogger.LogDebug($"MainWindow.OnUserStatsReceived dispatched to UI thread");

                    if (e.Result != 1)
                    {
                        AppLogger.LogDebug($"UserStatsReceived failed with result: {e.Result}");
                        StatusLabel.Text = $"Error retrieving stats: {GetErrorDescription(e.Result)}";
                        return;
                    }

                    string currentLanguage = LanguageComboBox.SelectedItem as string ?? "english";
                    AppLogger.LogDebug($"Loading schema for language: {currentLanguage}");

                    if (!_gameStatsService.LoadUserGameStatsSchema(currentLanguage))
                    {
                        StatusLabel.Text = "Failed to load game schema";
                        return;
                    }

                    AppLogger.LogDebug("Loading achievements and statistics...");
                    LoadingBar.IsVisible = true;

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

                    AppLogger.LogDebug($"UI updated - {_achievements.Count} achievements, {_statistics.Count} statistics");
                    StatusLabel.Text = $"Retrieved {_achievements.Count} achievements and {_statistics.Count} statistics";

                    // Start loading achievement icons
                    await LoadAchievementIconsAsync();

                    // Notify timer service that stats have been reloaded
                    _achievementTimerService?.NotifyStatsReloaded();

                    LoadingBar.IsVisible = false;
                }
                catch (Exception ex)
                {
                    AppLogger.LogDebug($"Error in OnUserStatsReceived: {ex.GetType().Name}: {ex.Message}");
                    AppLogger.LogDebug($"Stack trace: {ex.StackTrace}");
                    StatusLabel.Text = $"Error loading stats: {ex.Message}";
                    LoadingBar.IsVisible = false;
                }
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
                        achievement.Description.IndexOf(searchText, StringComparison.OrdinalIgnoreCase) >= 0 ||
                        (!string.IsNullOrEmpty(achievement.EnglishName) &&
                         achievement.EnglishName.IndexOf(searchText, StringComparison.OrdinalIgnoreCase) >= 0) ||
                        (!string.IsNullOrEmpty(achievement.EnglishDescription) &&
                         achievement.EnglishDescription.IndexOf(searchText, StringComparison.OrdinalIgnoreCase) >= 0) ||
                        achievement.Id.IndexOf(searchText, StringComparison.OrdinalIgnoreCase) >= 0;

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
                        Dispatcher.UIThread.Post(() =>
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

            // Restore timer display state for filtered achievements
            UpdateScheduledTimesDisplay();
        }

        private void LoadStatistics()
        {
            _statistics.Clear();
            var stats = _gameStatsService.GetStatistics();

            foreach (var stat in stats)
            {
                _statistics.Add(stat);
            }

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
            try
            {
                await LoadStatsAsync();
            }
            catch (Exception ex)
            {
                AppLogger.LogDebug($"Error in OnRefresh: {ex.GetType().Name}: {ex.Message}");
                AppLogger.LogDebug($"Stack trace: {ex.StackTrace}");
                StatusLabel.Text = $"Refresh failed: {ex.Message}";
                LoadingBar.IsVisible = false;
            }
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

            // Separate achievements by their current state for clear confirmation
            var achievedCount = selectedAchievements.Count(a => a.IsAchieved);
            var unachievedCount = selectedAchievements.Count - achievedCount;

            string confirmMessage;
            if (achievedCount > 0 && unachievedCount > 0)
            {
                confirmMessage = $"You are about to toggle {selectedAchievements.Count} achievement(s):\n\n" +
                               $"• {unachievedCount} locked achievement(s) will be UNLOCKED\n" +
                               $"• {achievedCount} unlocked achievement(s) will be LOCKED\n\n" +
                               $"Are you sure you want to continue?";
            }
            else if (achievedCount > 0)
            {
                confirmMessage = $"Are you sure you want to LOCK {achievedCount} unlocked achievement(s)?\n\n" +
                               $"This will reset them to unachieved state.\n" +
                               $"This action cannot be easily undone.";
            }
            else
            {
                confirmMessage = $"Are you sure you want to UNLOCK {unachievedCount} locked achievement(s)?";
            }

            var result = await ShowConfirmationDialog("Confirm Achievement Toggle", confirmMessage);

            if (!result)
            {
                return;
            }

            // Show loading indicator and disable Store button to prevent multiple clicks
            LoadingBar.IsVisible = true;
            StoreButton.IsEnabled = false;
            StatusLabel.Text = "Storing changes...";

            try
            {
                await Task.Run(() => PerformStoreToggle(selectedAchievements));
            }
            catch (Exception ex)
            {
                AppLogger.LogDebug($"Error in OnStore: {ex.GetType().Name}: {ex.Message}");
                AppLogger.LogDebug($"Stack trace: {ex.StackTrace}");
                StatusLabel.Text = $"Store failed: {ex.Message}";
            }
            finally
            {
                LoadingBar.IsVisible = false;
                StoreButton.IsEnabled = true;
            }
        }

        private void PerformStoreToggle(List<AchievementInfo> selectedAchievements)
        {
            try
            {
                AppLogger.LogDebug($"PerformStoreToggle called for {selectedAchievements.Count} achievements");
                AppLogger.LogDebug($"Debug mode: {AppLogger.IsDebugMode}");

                int achievementCount = 0;
                foreach (var achievement in selectedAchievements)
                {
                    if (achievement.IsProtected)
                    {
                        AppLogger.LogDebug($"Skipping protected achievement {achievement.Id}");
                        continue;
                    }

                    bool newState = !achievement.IsAchieved;
                    AppLogger.LogDebug($"Achievement {achievement.Id} toggle: {achievement.IsAchieved} -> {newState}");

                    if (!_gameStatsService.SetAchievement(achievement.Id, newState))
                    {
                        Dispatcher.UIThread.Post(() =>
                        {
                            if (achievement.IsProtected)
                            {
                                ShowErrorDialog($"Cannot modify protected achievement:\n\n" +
                                              $"ID: {achievement.Id}\n" +
                                              $"Name: {achievement.Name}\n\n" +
                                              $"This achievement is protected by the game developer and cannot be modified.");
                            }
                            else
                            {
                                ShowErrorDialog($"Failed to set achievement '{achievement.Id}'. Steam API rejected the change.");
                            }
                        });
                        return;
                    }

                    Dispatcher.UIThread.Post(() =>
                    {
                        achievement.IsAchieved = newState;
                    });

                    achievementCount++;
                }

                int statCount = StoreStatistics(true);

                if (statCount < 0)
                {
                    AppLogger.LogDebug("Statistics store failed in PerformStoreToggle - refreshing");
                    RefreshAfterFailure(false);
                    return;
                }

                bool success = _gameStatsService.StoreStats();
                AppLogger.LogDebug($"StoreStats result: {success}");

                if (!success)
                {
                    AppLogger.LogDebug("StoreStats failed in PerformStoreToggle - refreshing");
                    Dispatcher.UIThread.Post(() =>
                    {
                        StatusLabel.Text = "Failed to commit changes to Steam. Refreshing...";
                    });
                    RefreshAfterFailure(false);
                    return;
                }

                int finalAchievementCount = achievementCount;
                int finalStatCount = statCount;
                Dispatcher.UIThread.Post(() =>
                {
                    string prefix = AppLogger.IsDebugMode ? "[DEBUG MODE] Fake toggled" : "Successfully toggled";
                    StatusLabel.Text = $"{prefix} {finalAchievementCount} achievements and {Math.Max(0, finalStatCount)} statistics. Refreshing...";

                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await Task.Delay(500);
                            Dispatcher.UIThread.Post(async () =>
                            {
                                try
                                {
                                    await LoadStatsAsync();
                                }
                                catch (Exception ex)
                                {
                                    AppLogger.LogDebug($"Error reloading stats: {ex.GetType().Name}: {ex.Message}");
                                }
                            });
                        }
                        catch (Exception ex)
                        {
                            AppLogger.LogDebug($"Error in delayed reload task: {ex.GetType().Name}: {ex.Message}");
                        }
                    });
                });
            }
            catch (Exception ex)
            {
                AppLogger.LogDebug($"Error in PerformStoreToggle: {ex.Message}");
                Dispatcher.UIThread.Post(() =>
                {
                    StatusLabel.Text = $"Error: {ex.Message}";
                });
            }
        }

        private void PerformStore(bool silent)
        {
            try
            {
                AppLogger.LogDebug($"PerformStore called (silent: {silent})");

                int achievementCount = StoreAchievements(silent);
                if (achievementCount < 0)
                {
                    RefreshAfterFailure(silent);
                    return;
                }

                int statCount = StoreStatistics(silent);
                if (statCount < 0)
                {
                    RefreshAfterFailure(silent);
                    return;
                }

                if (!_gameStatsService.StoreStats())
                {
                    if (!silent)
                    {
                        ShowErrorDialog("Failed to commit changes to Steam. Refreshing to restore correct state...");
                    }
                    AppLogger.LogDebug("StoreStats failed - refreshing to resync with Steam");
                    RefreshAfterFailure(silent);
                    return;
                }

                if (!silent)
                {
                    int finalAchievementCount = achievementCount;
                    int finalStatCount = statCount;
                    Dispatcher.UIThread.Post(() =>
                    {
                        string prefix = AppLogger.IsDebugMode ? "[DEBUG MODE] Fake stored" : "Successfully stored";
                        StatusLabel.Text = $"{prefix} {finalAchievementCount} achievements and {finalStatCount} statistics. Refreshing...";

                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                await Task.Delay(500);
                                Dispatcher.UIThread.Post(async () =>
                                {
                                    try
                                    {
                                        await LoadStatsAsync();
                                    }
                                    catch (Exception ex)
                                    {
                                        AppLogger.LogDebug($"Error reloading stats: {ex.GetType().Name}: {ex.Message}");
                                    }
                                });
                            }
                            catch (Exception ex)
                            {
                                AppLogger.LogDebug($"Error in delayed reload task: {ex.GetType().Name}: {ex.Message}");
                            }
                        });
                    });
                }
                else
                {
                    _ = LoadStatsAsync();
                }
            }
            catch (Exception ex)
            {
                AppLogger.LogDebug($"Error in PerformStore: {ex.Message}");
                if (!silent)
                {
                    Dispatcher.UIThread.Post(() =>
                    {
                        ShowErrorDialog($"Error storing stats: {ex.Message}");
                    });
                }
            }
        }

        private int StoreAchievements(bool silent)
        {
            int count = 0;

            var modifiedAchievements = _achievements.Where(a => a.IsModified).ToList();

            if (modifiedAchievements.Count == 0)
                return 0;

            var protectedAchievements = modifiedAchievements.Where(a => a.IsProtected).ToList();
            if (protectedAchievements.Count > 0)
            {
                if (!silent)
                {
                    var protectedIds = string.Join(", ", protectedAchievements.Select(a => a.Id));
                    ShowErrorDialog($"Cannot modify protected achievements:\n\n{protectedIds}\n\n" +
                                  $"These achievements are protected by the game developer.");
                }
                AppLogger.LogDebug($"Blocked attempt to modify {protectedAchievements.Count} protected achievements");
                return -1;
            }

            var sortedAchievements = SortAchievementsByStatisticDependency(modifiedAchievements);

            foreach (var achievement in sortedAchievements)
            {
                AppLogger.LogDebug($"Achievement {achievement.Id} modified: {achievement.OriginalIsAchieved} -> {achievement.IsAchieved}");

                if (!_gameStatsService.SetAchievement(achievement.Id, achievement.IsAchieved))
                {
                    if (!silent)
                    {
                        if (achievement.IsProtected)
                        {
                            ShowErrorDialog($"Cannot modify protected achievement:\n\n" +
                                          $"ID: {achievement.Id}\n" +
                                          $"Name: {achievement.Name}\n\n" +
                                          $"This achievement is protected by the game developer.");
                        }
                        else
                        {
                            ShowErrorDialog($"Failed to set achievement '{achievement.Id}'. Steam API rejected the change.");
                        }
                    }
                    return -1;
                }

                achievement.OriginalIsAchieved = achievement.IsAchieved;
                count++;
            }

            return count;
        }

        private List<AchievementInfo> SortAchievementsByStatisticDependency(List<AchievementInfo> achievements)
        {
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

            return achievements.OrderBy(a =>
            {
                if (achievementStatMap.TryGetValue(a.Id, out var statInfo))
                {
                    return statInfo.requiredValue;
                }
                return 0;
            }).ToList();
        }

        private void RefreshAfterFailure(bool silent)
        {
            AppLogger.LogDebug("RefreshAfterFailure called - reloading stats from Steam");

            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(300);

                    Dispatcher.UIThread.Post(async () =>
                    {
                        try
                        {
                            if (!silent)
                            {
                                StatusLabel.Text = "Refreshing data from Steam...";
                            }
                            await LoadStatsAsync();
                            if (!silent)
                            {
                                StatusLabel.Text = "Data refreshed from Steam";
                            }
                        }
                        catch (Exception ex)
                        {
                            AppLogger.LogDebug($"Error during refresh after failure: {ex.GetType().Name}: {ex.Message}");
                            if (!silent)
                            {
                                StatusLabel.Text = "Error refreshing data from Steam";
                            }
                        }
                    });
                }
                catch (Exception ex)
                {
                    AppLogger.LogDebug($"Error in RefreshAfterFailure task: {ex.GetType().Name}: {ex.Message}");
                }
            });
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
                        string errorMessage = BuildStatValidationErrorMessage(stat);
                        ShowErrorDialog(errorMessage);
                    }
                    return -1;
                }
                count++;
            }

            return count;
        }

        private string BuildStatValidationErrorMessage(StatInfo stat)
        {
            if (stat is IntStatInfo intStat)
            {
                if (intStat.IsIncrementOnly && intStat.IntValue < intStat.OriginalValue)
                {
                    return $"Cannot decrease IncrementOnly statistic '{stat.DisplayName}' ({stat.Id})\n" +
                           $"Original: {intStat.OriginalValue}, Attempted: {intStat.IntValue}\n\n" +
                           $"This statistic can only be increased, never decreased.";
                }

                if (intStat.IntValue < intStat.MinValue || intStat.IntValue > intStat.MaxValue)
                {
                    return $"Statistic '{stat.DisplayName}' ({stat.Id}) value out of range\n" +
                           $"Attempted: {intStat.IntValue}\n" +
                           $"Valid range: [{intStat.MinValue}, {intStat.MaxValue}]\n\n" +
                           $"Please enter a value within the allowed range.";
                }

                if (intStat.MaxChange > 0)
                {
                    int change = Math.Abs(intStat.IntValue - intStat.OriginalValue);
                    if (change > intStat.MaxChange)
                    {
                        return $"Statistic '{stat.DisplayName}' ({stat.Id}) change too large\n" +
                               $"Original: {intStat.OriginalValue}, Attempted: {intStat.IntValue}\n" +
                               $"Change: {change} (max allowed: {intStat.MaxChange})\n\n" +
                               $"This statistic can only change by {intStat.MaxChange} at a time.";
                    }
                }
            }
            else if (stat is FloatStatInfo floatStat)
            {
                if (floatStat.IsIncrementOnly && floatStat.FloatValue < floatStat.OriginalValue)
                {
                    return $"Cannot decrease IncrementOnly statistic '{stat.DisplayName}' ({stat.Id})\n" +
                           $"Original: {floatStat.OriginalValue:F2}, Attempted: {floatStat.FloatValue:F2}\n\n" +
                           $"This statistic can only be increased, never decreased.";
                }

                if (floatStat.FloatValue < floatStat.MinValue || floatStat.FloatValue > floatStat.MaxValue)
                {
                    return $"Statistic '{stat.DisplayName}' ({stat.Id}) value out of range\n" +
                           $"Attempted: {floatStat.FloatValue:F2}\n" +
                           $"Valid range: [{floatStat.MinValue:F2}, {floatStat.MaxValue:F2}]\n\n" +
                           $"Please enter a value within the allowed range.";
                }

                if (floatStat.MaxChange > float.Epsilon)
                {
                    float change = Math.Abs(floatStat.FloatValue - floatStat.OriginalValue);
                    if (change > floatStat.MaxChange)
                    {
                        return $"Statistic '{stat.DisplayName}' ({stat.Id}) change too large\n" +
                               $"Original: {floatStat.OriginalValue:F2}, Attempted: {floatStat.FloatValue:F2}\n" +
                               $"Change: {change:F2} (max allowed: {floatStat.MaxChange:F2})\n\n" +
                               $"This statistic can only change by {floatStat.MaxChange:F2} at a time.";
                    }
                }
            }

            return $"Failed to set statistic '{stat.DisplayName}' ({stat.Id})\n\n" +
                   $"The value may violate Steam API constraints.";
        }

        private void OnLockAll(object sender, RoutedEventArgs e)
        {
            AppLogger.LogDebug("Select all unlocked button clicked");

            AchievementListView.SelectedItems.Clear();

            var unlockedAchievements = _achievements
                .Where(a => !a.IsProtected && a.IsAchieved)
                .ToList();

            foreach (var achievement in unlockedAchievements)
            {
                AchievementListView.SelectedItems.Add(achievement);
            }

            AppLogger.LogDebug($"Selected {unlockedAchievements.Count} unlocked achievements");
        }

        private void OnUnlockAll(object sender, RoutedEventArgs e)
        {
            AppLogger.LogDebug("Select all locked button clicked");

            AchievementListView.SelectedItems.Clear();

            var lockedAchievements = _achievements
                .Where(a => !a.IsProtected && !a.IsAchieved)
                .ToList();

            foreach (var achievement in lockedAchievements)
            {
                AchievementListView.SelectedItems.Add(achievement);
            }

            AppLogger.LogDebug($"Selected {lockedAchievements.Count} locked achievements");
        }

        private void OnInvertAll(object sender, RoutedEventArgs e)
        {
            AppLogger.LogDebug("Select All button clicked");

            AchievementListView.SelectedItems.Clear();

            var selectableAchievements = _achievements
                .Where(a => !a.IsProtected)
                .ToList();

            foreach (var achievement in selectableAchievements)
            {
                AchievementListView.SelectedItems.Add(achievement);
            }

            AppLogger.LogDebug($"Selected {selectableAchievements.Count} achievements");
        }

        private void OnSearchTextChanged(object? sender, TextChangedEventArgs e)
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

        private async void OnLanguageChanged(object? sender, SelectionChangedEventArgs e)
        {
            if (_gameStatsService == null)
                return;

            string currentLanguage = LanguageComboBox.SelectedItem as string ?? "english";

            LoadingBar.IsVisible = true;

            foreach (var achievement in _allAchievements)
            {
                achievement.PropertyChanged -= OnAchievementPropertyChanged;
            }

            if (!_gameStatsService.LoadUserGameStatsSchema(currentLanguage))
            {
                StatusLabel.Text = "Failed to load game schema";
                LoadingBar.IsVisible = false;
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

            LoadingBar.IsVisible = false;
        }

        private void OnColumnLayoutChanged(object? sender, SelectionChangedEventArgs e)
        {
            if (ColumnLayoutComboBox.SelectedItem is string layout)
            {
                ApplyColumnLayout(layout);
            }
        }

        private void ApplyColumnLayout(string layout)
        {
            AppLogger.LogDebug($"Column layout changed to: {layout}");
            StatusLabel.Text = $"Column layout set to: {layout}. Use scroll and zoom for better viewing.";

            if (layout == "Compact")
            {
                StatusLabel.Text = "Compact view selected. Tip: Use Ctrl+Mouse wheel to zoom for better readability.";
            }
            else if (layout == "Extra Wide")
            {
                StatusLabel.Text = "Extra Wide view selected. Tip: Use horizontal scroll to see all columns.";
            }
        }

        private async void OnSetTimer(object sender, RoutedEventArgs e)
        {
            var selectedAchievements = AchievementListView.SelectedItems
                .OfType<AchievementInfo>()
                .Where(a => !a.IsAchieved && !a.IsProtected)
                .ToList();

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
                var dialogResult = await ShowTimerDialog(selectedAchievements);

                if (dialogResult.HasValue)
                {
                    var unlockTime = dialogResult.Value;

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
                    AppLogger.LogDebug("Set Timer dialog was cancelled by user");
                }
            }
            catch (Exception ex)
            {
                AppLogger.LogDebug($"Error in OnSetTimer: {ex.Message}");
                ShowErrorDialog($"Error setting timer: {ex.Message}");
            }
        }

        private async Task<DateTime?> ShowTimerDialog(List<AchievementInfo> achievements)
        {
            var dialog = new Window
            {
                Title = "Set Achievement Unlock Time",
                Width = 450,
                Height = 450,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                CanResize = false
            };

            var defaultTime = DateTime.Now.AddHours(1);
            var tcs = new TaskCompletionSource<DateTime?>();

            var stack = new StackPanel { Margin = new Thickness(20), Spacing = 10 };

            stack.Children.Add(new TextBlock
            {
                Text = "Select the date and time when the achievement should be unlocked:",
                TextWrapping = TextWrapping.Wrap
            });

            stack.Children.Add(new TextBlock
            {
                Text = $"Achievements to be scheduled ({achievements.Count}):",
                FontWeight = FontWeight.SemiBold
            });

            stack.Children.Add(new TextBlock
            {
                Text = string.Join("\n", achievements.Take(5).Select(a => $"• {a.Id}: {a.Name}")) +
                       (achievements.Count > 5 ? $"\n... and {achievements.Count - 5} more" : ""),
                FontFamily = new FontFamily("Consolas"),
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                MaxHeight = 120
            });

            var datePicker = new DatePicker { SelectedDate = new DateTimeOffset(defaultTime.Date) };
            stack.Children.Add(datePicker);

            stack.Children.Add(new TextBlock { Text = "Time:" });
            var timePicker = new TimePicker { SelectedTime = defaultTime.TimeOfDay };
            stack.Children.Add(timePicker);

            stack.Children.Add(new TextBlock { Text = "Seconds (0-59):" });
            var secondsBox = new NumericUpDown { Value = 0, Minimum = 0, Maximum = 59 };
            stack.Children.Add(secondsBox);

            var buttonPanel = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 10, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };

            var okButton = new Button { Content = "Set Timer" };
            okButton.Click += (_, _) =>
            {
                var selectedDate = datePicker.SelectedDate?.Date ?? defaultTime.Date;
                var selectedTime = timePicker.SelectedTime ?? defaultTime.TimeOfDay;
                int seconds = (int)(secondsBox.Value ?? 0);
                var unlockTime = selectedDate.Add(selectedTime).AddSeconds(seconds);
                tcs.TrySetResult(unlockTime);
                dialog.Close();
            };

            var cancelButton = new Button { Content = "Cancel" };
            cancelButton.Click += (_, _) =>
            {
                tcs.TrySetResult(null);
                dialog.Close();
            };

            buttonPanel.Children.Add(okButton);
            buttonPanel.Children.Add(cancelButton);
            stack.Children.Add(buttonPanel);

            dialog.Content = stack;
            dialog.Closed += (_, _) => tcs.TrySetResult(null);

            await dialog.ShowDialog(this);
            return await tcs.Task;
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
                AppLogger.LogDebug($"Reset all timers - {scheduledAchievements.Count} timers cancelled");

                UpdateScheduledTimesDisplay();
            }
            catch (Exception ex)
            {
                AppLogger.LogDebug($"Error in OnResetAllTimers: {ex.Message}");
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
                AppLogger.LogDebug($"Reset selected timers - {resetCount} timers cancelled");

                UpdateScheduledTimesDisplay();
            }
            catch (Exception ex)
            {
                AppLogger.LogDebug($"Error in OnResetSelectedTimers: {ex.Message}");
            }
        }

        private void UpdateScheduledTimesDisplay()
        {
            if (_achievementTimerService == null) return;

            var scheduledAchievements = _achievementTimerService.GetAllScheduledAchievements();

            foreach (var achievement in _allAchievements)
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

        private void OnAutoMouseMove(object sender, RoutedEventArgs e)
        {
            if (_mouseMoverService != null)
            {
                bool enabled = AutoMouseMoveButton.IsChecked == true;
                _mouseMoverService.IsEnabled = enabled;
                AutoMouseMoveButton.Content = enabled ? "Stop Auto Mouse" : "Auto Mouse";
                ToolTip.SetTip(AutoMouseMoveButton, enabled ? "Stop Auto Mouse" : "Auto Mouse");
                AppLogger.LogDebug($"Auto mouse movement {(enabled ? "enabled" : "disabled")}");
            }
        }

        private void OnTimerToggle(object sender, RoutedEventArgs e)
        {
            if (TimerToggleButton.IsChecked == true)
            {
                _achievementTimer.Start();
                TimerToggleButton.Content = "Disable Timer";
                UpdateTimerStatusIndicator(true);
            }
            else
            {
                _achievementTimer.Stop();
                TimerToggleButton.Content = "Enable Timer";
                UpdateTimerStatusIndicator(false);
            }
        }

        private void OnTimeTimerTick(object? sender, EventArgs e)
        {
            CurrentTimeLabel.Text = $"Current Time: {DateTime.Now:yyyy/MM/dd HH:mm:ss}";
        }

        private void OnAchievementTimerTick(object? sender, EventArgs e)
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

        private void OnMouseTimerTick(object? sender, EventArgs e)
        {
            // Mouse jiggle handled by MouseMoverService
        }

        private async Task<bool> ShowConfirmationDialog(string title, string message)
        {
            var dialog = new Window
            {
                Title = title,
                Width = 450,
                Height = 280,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                CanResize = false
            };

            var tcs = new TaskCompletionSource<bool>();

            var stack = new StackPanel { Margin = new Thickness(20), Spacing = 15 };

            stack.Children.Add(new TextBlock
            {
                Text = message,
                TextWrapping = TextWrapping.Wrap
            });

            var buttonPanel = new StackPanel
            {
                Orientation = Avalonia.Layout.Orientation.Horizontal,
                Spacing = 10,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                Margin = new Thickness(0, 10, 0, 0)
            };

            var yesButton = new Button { Content = "Yes" };
            yesButton.Click += (_, _) =>
            {
                tcs.TrySetResult(true);
                dialog.Close();
            };

            var noButton = new Button { Content = "No" };
            noButton.Click += (_, _) =>
            {
                tcs.TrySetResult(false);
                dialog.Close();
            };

            buttonPanel.Children.Add(yesButton);
            buttonPanel.Children.Add(noButton);
            stack.Children.Add(buttonPanel);

            dialog.Content = stack;
            dialog.Closed += (_, _) => tcs.TrySetResult(false);

            await dialog.ShowDialog(this);
            return await tcs.Task;
        }

        private void ShowErrorDialog(string message)
        {
            try
            {
                Dispatcher.UIThread.Post(() =>
                {
                    StatusLabel.Text = $"Error: {message}";
                    AppLogger.LogDebug($"Error dialog: {message}");
                });
            }
            catch (Exception ex)
            {
                AppLogger.LogDebug($"Error showing error dialog: {ex.Message}");
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
                bool ownsGame = _steamClient.IsSubscribedApp((uint)_gameId);
                AppLogger.LogDebug($"Game ownership check for {_gameId}: {ownsGame}");

                string? gameName = _steamClient.GetAppData((uint)_gameId, "name");
                AppLogger.LogDebug($"Game name: {gameName ?? "Unknown"}");

                string? gameType = _steamClient.GetAppData((uint)_gameId, "type");
                AppLogger.LogDebug($"Game type: {gameType ?? "Unknown"}");

                string? gameState = _steamClient.GetAppData((uint)_gameId, "state");
                AppLogger.LogDebug($"Game state: {gameState ?? "Unknown"}");

                if (!ownsGame)
                {
                    StatusLabel.Text = "Warning: Steam reports you don't own this game";
                }
            }
            catch (Exception ex)
            {
                AppLogger.LogDebug($"Error testing game ownership: {ex.Message}");
            }
        }

        private void OnClearLog(object sender, RoutedEventArgs e)
        {
            AppLogger.ClearLog();
            AppLogger.LogDebug("Debug log cleared by user");
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
                                Dispatcher.UIThread.Post(() =>
                                {
                                    try
                                    {
                                        achievement.IconImage = new Bitmap(iconPath);
                                    }
                                    catch (Exception ex)
                                    {
                                        AppLogger.LogDebug($"Error creating Bitmap for {achievement.Id}: {ex.Message}");
                                    }
                                });
                            }
                        }
                        catch (Exception ex)
                        {
                            AppLogger.LogDebug($"Error loading icon for {achievement.Id}: {ex.Message}");
                        }
                    });

                    await Task.WhenAll(tasks);
                    processed += batch.Length;

                    int progress = processed;
                    Dispatcher.UIThread.Post(() =>
                    {
                        StatusLabel.Text = $"Loading icons... {progress}/{total}";
                    });

                    await Task.Delay(1);
                }

                AppLogger.LogDebug($"Finished loading icons for {total} achievements");
            }
            catch (Exception ex)
            {
                AppLogger.LogDebug($"Error loading achievement icons: {ex.Message}");
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
                    Dispatcher.UIThread.Post(() =>
                    {
                        try
                        {
                            achievement.IconImage = new Bitmap(iconPath);
                        }
                        catch (Exception ex)
                        {
                            AppLogger.LogDebug($"Error creating Bitmap for {achievement.Id}: {ex.Message}");
                        }
                    });
                }
            }
            catch (Exception ex)
            {
                AppLogger.LogDebug($"Error updating icon for {achievement.Id}: {ex.Message}");
            }
        }

        private void UpdateTimerStatusIndicator(bool isActive)
        {
            if (isActive)
            {
                TimerStatusText.Text = "\U0001F7E2 Timer On";
                TimerStatusText.Foreground = new SolidColorBrush(Colors.Green);
            }
            else
            {
                TimerStatusText.Text = "\u26AA Timer Off";
                TimerStatusText.Foreground = new SolidColorBrush(Colors.Gray);
            }
        }

        private void OnTimerStatusUpdated(string status)
        {
            Dispatcher.UIThread.Post(() =>
            {
                StatusLabel.Text = status;
            });
        }

        private void OnTimerAchievementUnlocked(string achievementId)
        {
            Dispatcher.UIThread.Post(() =>
            {
                var achievement = _allAchievements.FirstOrDefault(a => a.Id == achievementId);
                if (achievement != null && !achievement.IsAchieved)
                {
                    achievement.IsAchieved = true;
                    achievement.ScheduledUnlockTime = null;
                    AppLogger.LogDebug($"UI updated for timer-unlocked achievement: {achievementId}");
                }
            });
        }

        private ISteamUserStats CreateSteamClient(long gameId)
        {
            try
            {
                AppLogger.LogDebug("Using Legacy SteamGameClient for Steam execution simulation...");
                var legacyClient = new SteamGameClient(gameId);

                if (legacyClient.Initialized)
                {
                    AppLogger.LogDebug("Legacy SteamGameClient initialized successfully - can simulate game execution");
                    return legacyClient;
                }
                else
                {
                    AppLogger.LogDebug("Legacy SteamGameClient failed to initialize");
                }
            }
            catch (Exception ex)
            {
                AppLogger.LogDebug($"Legacy SteamGameClient creation failed: {ex.Message}");
            }

            try
            {
                AppLogger.LogDebug("Falling back to ModernSteamClient (limited functionality)...");
                var modernClient = new ModernSteamClient(gameId);

                if (modernClient.Initialized)
                {
                    AppLogger.LogDebug("ModernSteamClient initialized successfully (but cannot simulate game execution)");
                    return modernClient;
                }
                else
                {
                    AppLogger.LogDebug("ModernSteamClient failed to initialize, disposing...");
                    modernClient.Dispose();
                }
            }
            catch (Exception ex)
            {
                AppLogger.LogDebug($"ModernSteamClient creation failed: {ex.Message}");
            }

            AppLogger.LogDebug("Both Steam clients failed, creating non-functional ModernSteamClient for compatibility");
            return new ModernSteamClient(gameId);
        }
    }
}
