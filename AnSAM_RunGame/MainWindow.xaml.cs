using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using AnSAM.RunGame.Models;
using AnSAM.RunGame.Services;
using AnSAM.RunGame.Steam;
using System.Globalization;
using Microsoft.UI.Dispatching;
using System.Runtime.InteropServices;

namespace AnSAM.RunGame
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
        private bool _mouseTimerEnabled = false;
        private bool _lastMouseMoveRight = true;

        public MainWindow(long gameId)
        {
            this.InitializeComponent();
            
            _gameId = gameId;
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
            
            // Apply current theme
            if (this.Content is FrameworkElement content)
            {
                ThemeService.ApplyTheme(content);
            }
            
            // Set window title
            string gameName = _steamClient.GetAppData((uint)gameId, "name") ?? gameId.ToString();
            string debugMode = DebugLogger.IsDebugMode ? " [DEBUG MODE]" : "";
            this.Title = $"AnSAM RunGame | {gameName}{debugMode}";
            
            // Initialize language options
            InitializeLanguageComboBox();
            
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
            
            // Start loading stats
            _ = LoadStatsAsync();
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

        private async Task LoadStatsAsync()
        {
            if (_isLoadingStats) return;
            
            _isLoadingStats = true;
            LoadingRing.IsActive = true;
            StatusLabel.Text = "Loading game statistics...";
            
            try
            {
                bool success = await _gameStatsService.RequestUserStatsAsync();
                if (!success)
                {
                    StatusLabel.Text = "Failed to request user stats from Steam";
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
            this.DispatcherQueue.TryEnqueue(() =>
            {
                if (e.Result != 1)
                {
                    StatusLabel.Text = $"Error retrieving stats: {GetErrorDescription(e.Result)}";
                    return;
                }

                string currentLanguage = LanguageComboBox.SelectedItem as string ?? "english";
                if (!_gameStatsService.LoadUserGameStatsSchema(currentLanguage))
                {
                    StatusLabel.Text = "Failed to load game schema";
                    return;
                }

                LoadAchievements();
                LoadStatistics();
                
                StatusLabel.Text = $"Retrieved {_achievements.Count} achievements and {_statistics.Count} statistics";
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

        private void OnStatsEditingToggle(object sender, RoutedEventArgs e)
        {
            // Enable/disable statistics editing
        }

        private void OnSetTimer(object sender, RoutedEventArgs e)
        {
            if (double.IsNaN(TimerValueBox.Value)) return;
            
            int timerValue = (int)TimerValueBox.Value;
            var selectedItems = AchievementListView.SelectedItems.Cast<AchievementInfo>().ToList();
            
            if (selectedItems.Count == 0)
            {
                ShowErrorDialog("Please select at least one achievement");
                return;
            }

            foreach (var achievement in selectedItems)
            {
                achievement.Counter = timerValue;
                _achievementCounters[achievement.Id] = timerValue;
            }
        }

        private void OnAutoMouseMove(object sender, RoutedEventArgs e)
        {
            _mouseTimerEnabled = !_mouseTimerEnabled;
            if (_mouseTimerEnabled)
            {
                _mouseTimer.Start();
                AutoMouseMoveButton.Label = "Stop Auto Mouse Move";
            }
            else
            {
                _mouseTimer.Stop();
                AutoMouseMoveButton.Label = "Auto Mouse Move";
            }
        }

        private void OnTimerToggle(object sender, RoutedEventArgs e)
        {
            if (TimerToggleButton.IsChecked == true)
            {
                _achievementTimer.Start();
                TimerToggleButton.Label = "Disable Timer";
            }
            else
            {
                _achievementTimer.Stop();
                TimerToggleButton.Label = "Enable Timer";
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
                2 => "Generic error - you may not own this game",
                _ => $"Error code: {errorCode}"
            };
        }

        private void OnClearLog(object sender, RoutedEventArgs e)
        {
            DebugLogger.ClearLog();
            DebugLogger.LogDebug("Debug log cleared by user");
            StatusLabel.Text = "Debug log cleared";
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
}