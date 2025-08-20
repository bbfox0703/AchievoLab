using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using RunGame.Models;
using RunGame.Services;
using RunGame.Steam;
using System.Globalization;
using Microsoft.UI.Dispatching;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml.Media.Imaging;
using System.ComponentModel;
using Windows.UI.ViewManagement;
using Windows.Storage;

namespace RunGame
{
    public sealed partial class MainWindow : Window
    {
        private readonly long _gameId;
        private readonly SteamGameClient _steamClient;
        private readonly GameStatsService _gameStatsService;
        private readonly DispatcherTimer _callbackTimer;
        private readonly DispatcherTimer _timeTimer;
        private readonly DispatcherTimer _achievementTimer;
        private readonly DispatcherTimer _mouseTimer;
        
        private readonly ObservableCollection<AchievementInfo> _achievements = new();
        private readonly ObservableCollection<StatInfo> _statistics = new();
        private readonly Dictionary<string, int> _achievementCounters = new();
        
        private bool _isLoadingStats = false;
        private bool _lastMouseMoveRight = true;

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
            
            // Set Steam AppID environment variable - some games require this
            Environment.SetEnvironmentVariable("SteamAppId", gameId.ToString());
            DebugLogger.LogDebug($"Set SteamAppId environment variable to {gameId}");
            
            _steamClient = new SteamGameClient(gameId);
            _gameStatsService = new GameStatsService(_steamClient, gameId);
            
            // Initialize timers
            _callbackTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
            _callbackTimer.Tick += (_, _) => _steamClient.RunCallbacks();
            _callbackTimer.Start();
            
            _timeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _timeTimer.Tick += OnTimeTimerTick;
            _timeTimer.Start();
            
            _achievementTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _achievementTimer.Tick += OnAchievementTimerTick;
            
            _mouseTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
            _mouseTimer.Tick += OnMouseTimerTick;

            // Set up event handlers
            _gameStatsService.UserStatsReceived += OnUserStatsReceived;

            if (Content is FrameworkElement root)
            {
                ThemeService.Initialize(this, root);
                var settings = ApplicationData.Current.LocalSettings;
                if (settings.Values.TryGetValue("AppTheme", out var t) && Enum.TryParse<ElementTheme>(t?.ToString(), out var savedTheme))
                {
                    ThemeService.ApplyTheme(savedTheme);
                }
                else
                {
                    ThemeService.ApplyTheme(ThemeService.GetCurrentTheme());
                }
                root.ActualThemeChanged += (_, _) => ThemeService.UpdateTitleBar(root.ActualTheme);
            }
            _uiSettings.ColorValuesChanged += UiSettings_ColorValuesChanged;
            
            // Set window title
            string gameName = _steamClient.GetAppData((uint)gameId, "name") ?? gameId.ToString();
            string debugMode = DebugLogger.IsDebugMode ? " [DEBUG MODE]" : "";
            this.Title = $"AnSAM RunGame | {gameName}{debugMode}";
            
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
            DebugLogger.LogDebug($"AnSAM_RunGame started for game {gameId} in {(DebugLogger.IsDebugMode ? "DEBUG" : "RELEASE")} mode");
            
            // Initialize new services
            _achievementTimerService = new AchievementTimerService(_gameStatsService);
            
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

        private void Theme_Default_Click(object sender, RoutedEventArgs e) => SetTheme(ElementTheme.Default);

        private void Theme_Light_Click(object sender, RoutedEventArgs e) => SetTheme(ElementTheme.Light);

        private void Theme_Dark_Click(object sender, RoutedEventArgs e) => SetTheme(ElementTheme.Dark);

        private void SetTheme(ElementTheme theme)
        {
            ThemeService.ApplyTheme(theme);
            var settings = ApplicationData.Current.LocalSettings;
            settings.Values["AppTheme"] = theme.ToString();
        }

        private void InitializeLanguageComboBox()
        {
            var languages = new[] 
            { 
                "english", "spanish", "french", "german", "italian", "portuguese", 
                "russian", "japanese", "korean", "schinese", "tchinese" 
            };
            
            foreach (var lang in languages)
            {
                LanguageComboBox.Items.Add(lang);
            }
            
            LanguageComboBox.SelectedItem = "english";
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
                }
            }
            catch (Exception ex)
            {
                StatusLabel.Text = $"Error loading stats: {ex.Message}";
            }
            finally
            {
                _isLoadingStats = false;
                LoadingRing.IsActive = false;
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
                LoadAchievements();
                LoadStatistics();
                
                DebugLogger.LogDebug($"UI updated - {_achievements.Count} achievements, {_statistics.Count} statistics");
                StatusLabel.Text = $"Retrieved {_achievements.Count} achievements and {_statistics.Count} statistics";
                
                // Start loading achievement icons on the UI thread
                await LoadAchievementIconsAsync();
            });
        }

        private void LoadAchievements()
        {
            _achievements.Clear();
            var achievements = _gameStatsService.GetAchievements();
            
            // Apply current filters
            string searchText = SearchTextBox.Text?.ToLower() ?? "";
            bool showLockedOnly = ShowLockedButton.IsChecked == true;
            bool showUnlockedOnly = ShowUnlockedButton.IsChecked == true;
            
            foreach (var achievement in achievements)
            {
                bool matchesSearch = string.IsNullOrEmpty(searchText) ||
                    achievement.Name.ToLower().Contains(searchText) ||
                    achievement.Description.ToLower().Contains(searchText);
                
                bool matchesFilter = (!showLockedOnly && !showUnlockedOnly) ||
                    (showLockedOnly && !achievement.IsAchieved) ||
                    (showUnlockedOnly && achievement.IsAchieved);
                
                if (matchesSearch && matchesFilter)
                {
                    // Restore counter value if exists
                    if (_achievementCounters.TryGetValue(achievement.Id, out int counter))
                    {
                        achievement.Counter = counter;
                    }

                    // Listen for property changes to update icons dynamically
                    achievement.PropertyChanged += OnAchievementPropertyChanged;

                    _achievements.Add(achievement);
                }
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
        }

        private async void OnRefresh(object sender, RoutedEventArgs e)
        {
            await LoadStatsAsync();
        }

        private void OnStore(object sender, RoutedEventArgs e)
        {
            PerformStore(false);
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

                string debugInfo = DebugLogger.IsDebugMode ? " [DEBUG - Not actually stored]" : "";
                if (!silent)
                {
                    StatusLabel.Text = $"Stored {achievementCount} achievements and {statCount} statistics{debugInfo}";
                }
                else
                {
                    _ = LoadStatsAsync(); // Refresh after silent store
                }
            }
            catch (Exception ex)
            {
                DebugLogger.LogDebug($"Error in PerformStore: {ex.Message}");
                if (!silent)
                {
                    ShowErrorDialog($"Error storing stats: {ex.Message}");
                }
            }
        }

        private int StoreAchievements(bool silent)
        {
            int count = 0;
            
            foreach (var achievement in _achievements)
            {
                var originalState = _gameStatsService.GetAchievements()
                    .FirstOrDefault(a => a.Id == achievement.Id)?.IsAchieved ?? false;
                
                if (achievement.IsAchieved != originalState)
                {
                    if (!_gameStatsService.SetAchievement(achievement.Id, achievement.IsAchieved))
                    {
                        if (!silent)
                        {
                            ShowErrorDialog($"Failed to set achievement '{achievement.Id}'");
                        }
                        return -1;
                    }
                    count++;
                }
            }
            
            return count;
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
            DebugLogger.LogDebug("Lock All button clicked");
            foreach (var achievement in _achievements)
            {
                if (!achievement.IsProtected)
                {
                    achievement.IsAchieved = false;
                }
            }
        }

        private void OnUnlockAll(object sender, RoutedEventArgs e)
        {
            DebugLogger.LogDebug("Unlock All button clicked");
            foreach (var achievement in _achievements)
            {
                if (!achievement.IsProtected)
                {
                    achievement.IsAchieved = true;
                }
            }
        }

        private void OnInvertAll(object sender, RoutedEventArgs e)
        {
            DebugLogger.LogDebug("Invert All button clicked");
            foreach (var achievement in _achievements)
            {
                if (!achievement.IsProtected)
                {
                    achievement.IsAchieved = !achievement.IsAchieved;
                }
            }
        }

        private async void OnResetAllStats(object sender, RoutedEventArgs e)
        {
            var dialog = new ContentDialog
            {
                Title = "Reset All Stats",
                Content = "Are you absolutely sure you want to reset all stats?",
                PrimaryButtonText = "Yes",
                SecondaryButtonText = "No",
                DefaultButton = ContentDialogButton.Secondary,
                XamlRoot = this.Content.XamlRoot
            };

            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

            var achievementDialog = new ContentDialog
            {
                Title = "Reset Achievements Too?",
                Content = "Do you want to reset achievements as well?",
                PrimaryButtonText = "Yes",
                SecondaryButtonText = "No",
                DefaultButton = ContentDialogButton.Secondary,
                XamlRoot = this.Content.XamlRoot
            };

            bool resetAchievements = await achievementDialog.ShowAsync() == ContentDialogResult.Primary;

            var confirmDialog = new ContentDialog
            {
                Title = "Final Confirmation",
                Content = "Really really sure? This cannot be undone!",
                PrimaryButtonText = "Yes, Reset Everything",
                SecondaryButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Secondary,
                XamlRoot = this.Content.XamlRoot
            };

            if (await confirmDialog.ShowAsync() != ContentDialogResult.Primary) return;

            if (_gameStatsService.ResetAllStats(resetAchievements))
            {
                await LoadStatsAsync();
            }
            else
            {
                ShowErrorDialog("Failed to reset stats");
            }
        }

        private void OnSearchTextChanged(object sender, TextChangedEventArgs e)
        {
            LoadAchievements();
        }

        private void OnShowLockedToggle(object sender, RoutedEventArgs e)
        {
            if (ShowLockedButton.IsChecked == true && ShowUnlockedButton.IsChecked == true)
            {
                ShowUnlockedButton.IsChecked = false;
            }
            LoadAchievements();
        }

        private void OnShowUnlockedToggle(object sender, RoutedEventArgs e)
        {
            if (ShowLockedButton.IsChecked == true && ShowUnlockedButton.IsChecked == true)
            {
                ShowLockedButton.IsChecked = false;
            }
            LoadAchievements();
        }

        private async void OnLanguageChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_gameStatsService != null)
            {
                await LoadStatsAsync();
            }
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
            var selectedItems = AchievementListView.SelectedItems.Cast<AchievementInfo>().ToList();
            
            if (selectedItems.Count == 0)
            {
                ShowErrorDialog("Please select at least one achievement");
                return;
            }

            try
            {
                // Create the dialog content
                var dialogContent = CreateTimerDialogContent();
                
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
                    var datePicker = (DatePicker)dialogContent.Children[1];
                    var timePicker = (TimePicker)dialogContent.Children[3];
                    var secondsBox = (NumberBox)dialogContent.Children[5];

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

                    foreach (var achievement in selectedItems)
                    {
                        achievement.ScheduledUnlockTime = unlockTime;
                        _achievementTimerService?.ScheduleAchievement(achievement.Id, unlockTime);
                        DebugLogger.LogDebug($"Scheduled achievement {achievement.Id} to unlock at {unlockTime}");
                    }

                    StatusLabel.Text = $"Scheduled {selectedItems.Count} achievement(s) to unlock at {unlockTime:yyyy-MM-dd HH:mm:ss}";
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

        private Grid CreateTimerDialogContent()
        {
            var grid = new Grid();
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

            var datePicker = new DatePicker
            {
                Date = DateTime.Now.Date.AddDays(1),
                Margin = new Thickness(0, 0, 0, 10)
            };
            Grid.SetRow(datePicker, 1);

            var timeLabel = new TextBlock
            {
                Text = "Time:",
                Margin = new Thickness(0, 0, 0, 5)
            };
            Grid.SetRow(timeLabel, 2);

            var timePicker = new TimePicker
            {
                Time = DateTime.Now.TimeOfDay.Add(TimeSpan.FromMinutes(5)),
                Margin = new Thickness(0, 0, 0, 10)
            };
            Grid.SetRow(timePicker, 3);

            var secondsLabel = new TextBlock
            {
                Text = "Seconds (0-59):",
                Margin = new Thickness(0, 0, 0, 5)
            };
            Grid.SetRow(secondsLabel, 4);

            var secondsBox = new NumberBox
            {
                Value = 0,
                Minimum = 0,
                Maximum = 59,
                SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline
            };
            Grid.SetRow(secondsBox, 5);

            grid.Children.Add(instructions);
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

        private void ShowErrorDialog(string message)
        {
            var dialog = new ContentDialog
            {
                Title = "Error",
                Content = message,
                CloseButtonText = "OK",
                XamlRoot = this.Content.XamlRoot
            };
            _ = dialog.ShowAsync();
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
                foreach (var achievement in _achievements)
                {
                    // Get the appropriate icon filename based on achievement state
                    string iconFileName = achievement.IsAchieved || string.IsNullOrEmpty(achievement.IconLocked)
                        ? achievement.IconNormal
                        : achievement.IconLocked;

                    if (!string.IsNullOrEmpty(iconFileName))
                    {
                        var iconPath = await _achievementIconService.GetAchievementIconAsync(
                            achievement.Id, iconFileName, achievement.IsAchieved);

                        if (!string.IsNullOrEmpty(iconPath))
                        {
                            this.DispatcherQueue.TryEnqueue(() =>
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
                }

                DebugLogger.LogDebug($"Finished loading icons for {_achievements.Count} achievements");
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
                    this.DispatcherQueue.TryEnqueue(() =>
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