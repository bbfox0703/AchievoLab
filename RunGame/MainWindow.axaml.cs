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

        private readonly ObservableCollection<AchievementInfo> _achievements = new();
        private readonly ObservableCollection<StatInfo> _statistics = new();
        private readonly Dictionary<string, int> _achievementCounters = new();
        private List<AchievementInfo> _allAchievements = new();
        private readonly DispatcherTimer _searchDebounceTimer;

        private bool _isLoadingStats = false;

        // Completionist protection ("防呆") opt-in; default ON. Persisted in the shared settings.json.
        private readonly ApplicationSettingsService _settingsService = new();
        private const string ProtectUnlockAllSettingKey = "RunGame.ProtectUnlockAllAchievements";
        private bool _protectUnlockAll = true;

        // Unlock time of the last successful "Set Timer", reused as the next dialog's default.
        // Scheduling a batch one achievement at a time otherwise re-seeds from DateTime.Now on every
        // open, so the minute silently drifts between dialogs and only the hour/seconds the user
        // actually touched stay put. Session-scoped: a remembered time is stale after a restart.
        private DateTime? _lastScheduledUnlockTime;

        // Close-confirmation state for pending scheduled unlocks. _closePromptOpen keeps a second
        // close attempt from stacking dialogs; _pendingTimersCloseConfirmed lets the programmatic
        // Close() that follows a "yes" fall straight through to teardown.
        private bool _closePromptOpen;
        private bool _pendingTimersCloseConfirmed;

        // Suppresses the SelectionChanged handlers while the constructor seeds the combo boxes.
        // Assigning SelectedItem raises SelectionChanged synchronously, so OnLanguageChanged used
        // to run a full schema load before LoadStatsAsync had even been started.
        private bool _initializing = true;

        // Background work that calls into native Steam (currently Store). OnWindowClosing waits for
        // it before disposing the Steam client, so the pipe and user handles are never released
        // while a thread pool thread is still using them.
        private Task? _pendingSteamWork;
        private bool _awaitingSteamWorkToClose;

        // New services
        private AchievementTimerService? _achievementTimerService;
        private MouseMoverService? _mouseMoverService;
        private AchievementIconService? _achievementIconService;

        public MainWindow() : this(0) { }

        public MainWindow(long gameId)
        {
            InitializeComponent();

            // Restore last position/size/maximized state and keep it remembered.
            WindowPlacementManager.Attach(this, "RunGame");

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

            // Initialize timers (named handlers so OnWindowClosing can -= them)
            _callbackTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
            _callbackTimer.Tick += OnCallbackTimerTick;
            _callbackTimer.Start();

            _timeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _timeTimer.Tick += OnTimeTimerTick;
            _timeTimer.Start();

            _achievementTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _achievementTimer.Tick += OnAchievementTimerTick;

            _searchDebounceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
            _searchDebounceTimer.Tick += OnSearchDebounceTimerTick;

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

            // Restore the completionist-protection opt-in (default ON)
            InitializeCompletionistProtection();

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
            _achievementTimerService.ProtectionEnabled = _protectUnlockAll;
            // The Timer toggle is the master switch for scheduled unlocks; it starts off.
            _achievementTimerService.SchedulingEnabled = TimerToggleButton.IsChecked == true;

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

            // Everything the language handler needs now exists, so let it run from here on.
            _initializing = false;
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
            // Scheduled unlocks are held in memory by AchievementTimerService only, and this handler
            // is what disposes it — so confirm before any teardown runs, not after.
            if (!_pendingTimersCloseConfirmed)
            {
                if (_closePromptOpen)
                {
                    e.Cancel = true;
                    return;
                }

                var pending = _achievementTimerService?.GetAllScheduledAchievements();
                if (pending is { Count: > 0 })
                {
                    e.Cancel = true;
                    _closePromptOpen = true;
                    _ = ConfirmDiscardPendingTimersAsync(pending);
                    return;
                }
            }

            // A Store in flight is still calling into native Steam on a thread pool thread.
            // Disposing the client now would release the pipe out from under it, so hold the
            // window open and close again once that work has finished.
            var steamWork = _pendingSteamWork;
            if (steamWork is { IsCompleted: false })
            {
                e.Cancel = true;
                if (!_awaitingSteamWorkToClose)
                {
                    _awaitingSteamWorkToClose = true;
                    _ = CloseAfterSteamWorkAsync(steamWork);
                }
                return;
            }

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

                // Stop timers and unsubscribe Tick handlers so the timer instances (held
                // by the Avalonia Dispatcher until GC) don't keep MainWindow rooted.
                _callbackTimer.Stop(); _callbackTimer.Tick -= OnCallbackTimerTick;
                _timeTimer.Stop(); _timeTimer.Tick -= OnTimeTimerTick;
                _achievementTimer.Stop(); _achievementTimer.Tick -= OnAchievementTimerTick;
                _searchDebounceTimer.Stop(); _searchDebounceTimer.Tick -= OnSearchDebounceTimerTick;
                SearchTextBox.TextChanged -= OnSearchTextChanged;

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

        /// <summary>
        /// Waits for in-flight native Steam work to finish, then re-closes the window. Called by
        /// <see cref="OnWindowClosing"/> after it cancels the close.
        /// </summary>
        private async Task CloseAfterSteamWorkAsync(Task steamWork)
        {
            try
            {
                StatusLabel.Text = "Finishing store operation before closing...";
                await steamWork.ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                AppLogger.LogDebug($"Pending Steam work faulted while closing: {ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                _pendingSteamWork = null;
                _awaitingSteamWorkToClose = false;
                Close();
            }
        }

        /// <summary>
        /// Asks whether to discard scheduled unlocks that have not fired yet, then re-closes the
        /// window if the user agrees. Called by <see cref="OnWindowClosing"/> after it cancels the
        /// close, so a "no" simply leaves the window (and every timer) alone.
        /// </summary>
        private async Task ConfirmDiscardPendingTimersAsync(Dictionary<string, DateTime> pending)
        {
            try
            {
                const int previewCount = 5;
                var preview = string.Join("\n", pending
                    .OrderBy(kv => kv.Value)
                    .Take(previewCount)
                    .Select(kv => $"• {kv.Value:yyyy-MM-dd HH:mm:ss.f}  {kv.Key}"));
                if (pending.Count > previewCount)
                    preview += $"\n... and {pending.Count - previewCount} more";

                AppLogger.LogDebug($"Close requested with {pending.Count} pending scheduled unlock(s)");

                var confirmed = await ShowConfirmationDialog(
                    "Pending Scheduled Unlocks",
                    $"{pending.Count} scheduled unlock(s) have not fired yet. Timers are kept in " +
                    $"memory only, so closing RunGame cancels them:\n\n{preview}\n\nClose anyway?");

                if (!confirmed)
                {
                    AppLogger.LogDebug("Close cancelled - pending scheduled unlocks kept");
                    return;
                }

                AppLogger.LogDebug($"Discarding {pending.Count} pending scheduled unlock(s) on close");
                _pendingTimersCloseConfirmed = true;
                Close();
            }
            catch (Exception ex)
            {
                AppLogger.LogDebug($"Error confirming pending timers on close: {ex.Message}");
            }
            finally
            {
                _closePromptOpen = false;
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

                    var superseded = _allAchievements;
                    _allAchievements = _gameStatsService.GetAchievements().ToList();

                    foreach (var achievement in _allAchievements)
                    {
                        if (_achievementCounters.TryGetValue(achievement.Id, out int counter))
                        {
                            achievement.Counter = counter;
                        }
                        achievement.ResetStaging();
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
                    PushAchievementSnapshotToTimer();
                    _achievementTimerService?.NotifyStatsReloaded();

                    ReleaseSupersededIcons(superseded);

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

        /// <summary>
        /// Releases the icon bitmaps of an achievement list that has been replaced.
        /// </summary>
        /// <remarks>
        /// AchievementInfo disposes the previous bitmap in its IconImage setter, but a reload
        /// discards the whole list at once, so that setter never runs for those instances and every
        /// decoded icon was left for the finalizer. Setting IconImage to null drives the existing
        /// setter, which disposes correctly.
        ///
        /// Queued at Background priority so it lands after the UI has rebound and rendered the new
        /// list — disposing a bitmap that is still being drawn is not safe.
        /// </remarks>
        private static void ReleaseSupersededIcons(List<AchievementInfo> superseded)
        {
            if (superseded.Count == 0)
            {
                return;
            }

            Dispatcher.UIThread.Post(() =>
            {
                foreach (var achievement in superseded)
                {
                    achievement.IconImage = null;
                }
            }, DispatcherPriority.Background);
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
            // Everything whose checkbox no longer matches Steam. Scoped to _allAchievements rather
            // than the filtered view so a change staged before the user narrowed the filter is still
            // written — the confirmation below spells out exactly what that is.
            var selectedAchievements = _allAchievements
                .Where(a => a.IsModified && !a.IsProtected)
                .ToList();

            if (selectedAchievements.Count == 0)
            {
                ShowErrorDialog(
                    "No changes to store. Tick an achievement to unlock it, or untick one to lock it, then press Store.");
                return;
            }

            // A scheduled achievement stays staged until its timer fires, so it is always in
            // selectedAchievements. Skip those rows rather than aborting the whole batch —
            // otherwise a single pending timer blocks every unrelated change the user made.
            var timerConflicts = selectedAchievements
                .Where(a => _achievementTimerService?.GetScheduledTime(a.Id) != null)
                .ToList();

            foreach (var scheduled in timerConflicts)
            {
                selectedAchievements.Remove(scheduled);
            }

            if (selectedAchievements.Count == 0)
            {
                ShowErrorDialog($"Cannot store changes for achievements with active timers: {string.Join(", ", timerConflicts.Select(a => a.Id))}");
                return;
            }

            // Completionist protection ("防呆", opt-in / default ON): don't let a game's meta
            // "unlock every achievement" achievement be unlocked before all of the others are.
            // A later DLC can add achievements, so this guard can be turned off by the user.
            var skippedCompletionist = new List<AchievementInfo>();
            int stillLockedForCompletionist = 0;
            if (_protectUnlockAll)
            {
                // Store writes each checkbox as-is, so the projected state is simply the ticked one
                // (which equals IsAchieved for every row the user left alone).
                skippedCompletionist = AchievementCompletionDetector.FindUnsafeCompletionists(
                    _allAchievements, selectedAchievements, a => a.DesiredAchieved, out stillLockedForCompletionist);

                foreach (var blocked in skippedCompletionist)
                {
                    selectedAchievements.Remove(blocked);
                }

                if (selectedAchievements.Count == 0)
                {
                    ShowErrorDialog(
                        $"Completionist protection is ON. \"{string.Join(", ", skippedCompletionist.Select(a => a.Id))}\" " +
                        $"unlocks automatically once every other achievement is unlocked ({stillLockedForCompletionist} still locked). " +
                        $"Unlock the rest first, or turn off the \"Protect Completionist\" button to override.");
                    return;
                }
            }

            // Every item here is staged, so its current state tells you which way it is going:
            // currently unlocked means the user unticked it, currently locked means they ticked it.
            var achievedCount = selectedAchievements.Count(a => a.IsAchieved);
            var unachievedCount = selectedAchievements.Count - achievedCount;

            string confirmMessage;
            if (achievedCount > 0 && unachievedCount > 0)
            {
                confirmMessage = $"You are about to change {selectedAchievements.Count} achievement(s):\n\n" +
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

            // Staged rows the current filter hides would otherwise be written invisibly.
            int hiddenCount = selectedAchievements.Count(a => !_achievements.Contains(a));
            if (hiddenCount > 0)
            {
                confirmMessage +=
                    $"\n\nNote: {hiddenCount} of these are not visible under the current filter or search.";
            }

            if (timerConflicts.Count > 0)
            {
                confirmMessage +=
                    $"\n\nNote: {timerConflicts.Count} achievement(s) with an active timer " +
                    $"({string.Join(", ", timerConflicts.Select(a => a.Id))}) will be SKIPPED — " +
                    $"they are written when their timer fires. Cancel the timer to store them now.";
            }

            if (skippedCompletionist.Count > 0)
            {
                confirmMessage +=
                    $"\n\nNote: {skippedCompletionist.Count} completionist achievement(s) " +
                    $"({string.Join(", ", skippedCompletionist.Select(a => a.Id))}) will be SKIPPED — " +
                    $"they unlock automatically after every other achievement is unlocked " +
                    $"({stillLockedForCompletionist} still locked). Turn off \"Protect Completionist\" to override.";
            }

            var result = await ShowConfirmationDialog("Confirm Achievement Changes", confirmMessage);

            if (!result)
            {
                return;
            }

            // Show loading indicator and disable Store button to prevent multiple clicks
            LoadingBar.IsVisible = true;
            StoreButton.IsEnabled = false;
            StatusLabel.Text = "Storing changes...";

            // Snapshot the modified statistics here, on the UI thread, so the background store
            // never enumerates the ObservableCollection bound to the list view.
            var modifiedStats = _statistics.Where(s => s.IsModified).ToList();

            try
            {
                // Kept on a field so OnWindowClosing can defer teardown until these native Steam
                // calls have finished, rather than releasing the pipe out from under them.
                _pendingSteamWork = Task.Run(() => PerformStoreStaged(selectedAchievements, modifiedStats));
                await _pendingSteamWork;
            }
            catch (Exception ex)
            {
                AppLogger.LogDebug($"Error in OnStore: {ex.GetType().Name}: {ex.Message}");
                AppLogger.LogDebug($"Stack trace: {ex.StackTrace}");
                StatusLabel.Text = $"Store failed: {ex.Message}";
            }
            finally
            {
                _pendingSteamWork = null;
                LoadingBar.IsVisible = false;
                StoreButton.IsEnabled = true;
            }
        }

        private void PerformStoreStaged(List<AchievementInfo> selectedAchievements, IReadOnlyList<StatInfo> modifiedStats)
        {
            try
            {
                AppLogger.LogDebug($"PerformStoreStaged called for {selectedAchievements.Count} achievements");
                AppLogger.LogDebug($"Debug mode: {AppLogger.IsDebugMode}");

                // A game's "unlock every achievement" (completionist) achievement must be committed
                // LAST, in a separate StoreStats, so its unlock is recorded after every other change
                // in this batch. This applies whether or not completionist protection is enabled.
                var completionists = selectedAchievements
                    .Where(AchievementCompletionDetector.IsUnlockAllAchievement).ToList();
                var others = selectedAchievements
                    .Where(a => !AchievementCompletionDetector.IsUnlockAllAchievement(a)).ToList();

                int achievementCount = 0;

                // Tracks what this phase has written into Steam's pending buffer so an abort can
                // put it back — the buffer outlives this method and the next StoreStats from any
                // source would otherwise commit it.
                var staged = new List<(AchievementInfo Achievement, bool OriginalState)>();

                // Phase 1: every non-completionist change, plus statistics, committed together.
                if (!TryApplyStagedAchievements(others, ref achievementCount, staged))
                {
                    RevertStagedAchievements(staged);
                    return;
                }

                int statCount = StoreStatistics(modifiedStats, true);
                if (statCount < 0)
                {
                    AppLogger.LogDebug("Statistics store failed in PerformStoreStaged - refreshing");
                    RevertStagedAchievements(staged);
                    RefreshAfterFailure(false);
                    return;
                }

                // Only commit phase 1 if it staged something — avoids an empty StoreStats when the
                // selection was a completionist only (its unlock is committed in phase 2).
                if ((achievementCount > 0 || statCount > 0) && !CommitStore("PerformStoreStaged phase 1"))
                {
                    RevertStagedAchievements(staged);
                    return;
                }

                // Phase 1 is committed from here on; only phase 2 work is still undoable.
                staged.Clear();

                // Phase 2: completionist changes committed last, in their own StoreStats.
                if (completionists.Count > 0)
                {
                    if (!TryApplyStagedAchievements(completionists, ref achievementCount, staged))
                    {
                        RevertStagedAchievements(staged);
                        return;
                    }
                    if (!CommitStore("PerformStoreStaged phase 2 (completionist)"))
                    {
                        RevertStagedAchievements(staged);
                        return;
                    }
                }

                int finalAchievementCount = achievementCount;
                int finalStatCount = statCount;
                Dispatcher.UIThread.Post(() =>
                {
                    string prefix = AppLogger.IsDebugMode ? "[DEBUG MODE] Fake stored" : "Successfully stored";
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
                AppLogger.LogDebug($"Error in PerformStoreStaged: {ex.Message}");
                Dispatcher.UIThread.Post(() =>
                {
                    StatusLabel.Text = $"Error: {ex.Message}";
                });
            }
        }

        /// <summary>
        /// Writes each item's staged <see cref="AchievementInfo.DesiredAchieved"/> via SetAchievement,
        /// without committing. On the first Steam API failure it surfaces an error and returns false so
        /// the caller aborts.
        /// </summary>
        /// <summary>
        /// Stages each achievement's target state into Steam's pending buffer.
        /// </summary>
        /// <param name="applied">
        /// Receives every achievement this call staged, with the state it had beforehand, so the
        /// caller can undo them if a later step in the same batch fails.
        /// </param>
        private bool TryApplyStagedAchievements(
            List<AchievementInfo> list,
            ref int achievementCount,
            List<(AchievementInfo Achievement, bool OriginalState)> applied)
        {
            foreach (var achievement in list)
            {
                if (achievement.IsProtected)
                {
                    AppLogger.LogDebug($"Skipping protected achievement {achievement.Id}");
                    continue;
                }

                bool originalState = achievement.IsAchieved;
                bool newState = achievement.DesiredAchieved;
                AppLogger.LogDebug($"Achievement {achievement.Id} change: {achievement.IsAchieved} -> {newState}");

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
                    return false;
                }

                applied.Add((achievement, originalState));

                Dispatcher.UIThread.Post(() =>
                {
                    achievement.IsAchieved = newState;
                });

                achievementCount++;
            }

            return true;
        }

        /// <summary>
        /// Undoes achievements staged earlier in a batch that has since failed.
        /// </summary>
        /// <remarks>
        /// SetAchievement only writes Steam's pending buffer; nothing reaches Steam until
        /// StoreStats. Leaving a failed batch in that buffer meant the next StoreStats from any
        /// source — the unlock timer firing, a later Store — silently committed a half-applied
        /// batch the user was told had failed.
        /// </remarks>
        private void RevertStagedAchievements(List<(AchievementInfo Achievement, bool OriginalState)> applied)
        {
            if (applied.Count == 0)
            {
                return;
            }

            AppLogger.LogDebug($"Store failed; reverting {applied.Count} achievement(s) staged before the failure");

            foreach (var (achievement, originalState) in applied)
            {
                if (!_gameStatsService.SetAchievement(achievement.Id, originalState))
                {
                    AppLogger.LogDebug($"Could not revert staged achievement {achievement.Id}");
                }

                var restored = originalState;
                Dispatcher.UIThread.Post(() =>
                {
                    achievement.IsAchieved = restored;
                });
            }

            applied.Clear();
        }

        /// <summary>
        /// Commits staged achievement/stat changes to Steam via StoreStats. On failure it refreshes
        /// from Steam and returns false so the caller aborts.
        /// </summary>
        private bool CommitStore(string context)
        {
            bool success = _gameStatsService.StoreStats();
            AppLogger.LogDebug($"StoreStats result: {success} ({context})");

            if (!success)
            {
                AppLogger.LogDebug($"StoreStats failed in {context} - refreshing");
                Dispatcher.UIThread.Post(() =>
                {
                    StatusLabel.Text = "Failed to commit changes to Steam. Refreshing...";
                });
                RefreshAfterFailure(false);
                return false;
            }

            return true;
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

                int statCount = StoreStatistics(_statistics.Where(s => s.IsModified).ToList(), silent);
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
                AppLogger.LogDebug($"Achievement {achievement.Id} modified: {achievement.IsAchieved} -> {achievement.DesiredAchieved}");

                if (!_gameStatsService.SetAchievement(achievement.Id, achievement.DesiredAchieved))
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

                // The write succeeded, so the staged value is now what Steam holds.
                achievement.IsAchieved = achievement.DesiredAchieved;
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

        /// <summary>
        /// Writes the given statistics to Steam.
        /// </summary>
        /// <param name="modifiedStats">
        /// Snapshot of the modified rows, taken on the UI thread by the caller. Passing a snapshot
        /// rather than reading <c>_statistics</c> here matters: this runs on the thread pool during
        /// a Store, and <c>_statistics</c> is the ObservableCollection bound to the list view, which
        /// the UI thread clears and refills on every reload.
        /// </param>
        /// <param name="silent">True to suppress the error dialog on a validation failure.</param>
        /// <returns>The number of statistics written, or -1 on failure.</returns>
        private int StoreStatistics(IReadOnlyList<StatInfo> modifiedStats, bool silent)
        {
            int count = 0;

            foreach (var stat in modifiedStats)
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

        /// <summary>
        /// Stages the ticked state for every modifiable achievement currently in view. Only the
        /// checkboxes change — nothing reaches Steam until Store is pressed.
        /// </summary>
        /// <param name="desired">The value to write into each checkbox, given its current state.</param>
        /// <param name="action">Verb used in the log and status line.</param>
        private void StageAll(Func<AchievementInfo, bool> desired, string action)
        {
            // Scoped to the visible list so a filtered-out achievement is never staged behind the
            // user's back — what you see is what these buttons touch.
            var editable = _achievements.Where(a => !a.IsProtected).ToList();

            foreach (var achievement in editable)
            {
                achievement.DesiredAchieved = desired(achievement);
            }

            ReportStagedChanges($"{action} {editable.Count} achievement(s)");
            AppLogger.LogDebug($"{action} {editable.Count} achievements");
        }

        /// <summary>
        /// Updates the status line with how many changes are staged and waiting for Store.
        /// </summary>
        private void ReportStagedChanges(string prefix)
        {
            var staged = _allAchievements.Where(a => a.IsModified && !a.IsProtected).ToList();
            if (staged.Count == 0)
            {
                StatusLabel.Text = $"{prefix}. No pending changes.";
                return;
            }

            int toUnlock = staged.Count(a => a.DesiredAchieved);
            int toLock = staged.Count - toUnlock;
            StatusLabel.Text =
                $"{prefix}. Pending: {toUnlock} to unlock, {toLock} to lock — press Store to apply.";
        }

        private void OnLockAll(object sender, RoutedEventArgs e) => StageAll(_ => false, "Unticked");

        private void OnUnlockAll(object sender, RoutedEventArgs e) => StageAll(_ => true, "Ticked");

        private void OnInvertAll(object sender, RoutedEventArgs e) =>
            StageAll(a => !a.DesiredAchieved, "Inverted");

        private void OnResetStagedChanges(object sender, RoutedEventArgs e)
        {
            // Resets everything, not just the visible rows, so nothing stays staged out of sight.
            // A tick that belongs to a scheduled timer is left alone — it records a commitment
            // already made, and withdrawing it is what "Reset Ticked Timers" is for.
            int kept = 0;
            foreach (var achievement in _allAchievements)
            {
                if (_achievementTimerService?.GetScheduledTime(achievement.Id) != null)
                {
                    if (achievement.IsModified) kept++;
                    continue;
                }
                achievement.ResetStaging();
            }

            StatusLabel.Text = kept > 0
                ? $"Discarded staged changes. {kept} tick(s) kept for scheduled timers."
                : "Discarded all staged changes.";
            AppLogger.LogDebug($"Staged changes reset ({kept} kept for scheduled timers)");
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
            // The constructor's SelectedItem assignment fires this synchronously, before
            // LoadStatsAsync has requested anything from Steam. Running then meant a redundant
            // schema load (with its synchronous disk I/O and retry backoff) and a flash of the
            // wrong status text while the window was still being built.
            if (_initializing || _gameStatsService == null)
                return;

            // async void: an escaping exception would take the process down, so nothing may leave
            // this method. Matches OnRefresh and OnSearchDebounceTimerTick.
            try
            {
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

                var superseded = _allAchievements;
                _allAchievements = _gameStatsService.GetAchievements().ToList();

                foreach (var achievement in _allAchievements)
                {
                    if (_achievementCounters.TryGetValue(achievement.Id, out int counter))
                    {
                        achievement.Counter = counter;
                    }
                    achievement.ResetStaging();
                    achievement.PropertyChanged += OnAchievementPropertyChanged;
                }

                await LoadAchievements();
                LoadStatistics();
                UpdateScheduledTimesDisplay();
                await LoadAchievementIconsAsync();
                PushAchievementSnapshotToTimer();
                _achievementTimerService?.NotifyStatsReloaded();

                ReleaseSupersededIcons(superseded);

                LoadingBar.IsVisible = false;
            }
            catch (Exception ex)
            {
                AppLogger.LogDebug($"Error in OnLanguageChanged: {ex.GetType().Name}: {ex.Message}");
                StatusLabel.Text = $"Error switching language: {ex.Message}";
                LoadingBar.IsVisible = false;
            }
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
            // Same input as Store: the checkboxes. Ticking a locked achievement says "I want this
            // unlocked" — Store does it now, Set Timer does it at a time you choose.
            var staged = _allAchievements
                .Where(a => a.IsModified && !a.IsProtected)
                .ToList();

            var selectedAchievements = staged.Where(a => a.DesiredAchieved).ToList();
            var stagedLocks = staged.Where(a => !a.DesiredAchieved).ToList();

            if (stagedLocks.Count > 0)
            {
                ShowErrorDialog(
                    $"A timer can only unlock achievements, but {stagedLocks.Count} of the ticked " +
                    $"changes would LOCK an achievement ({string.Join(", ", stagedLocks.Take(5).Select(a => a.Id))}). " +
                    $"Re-tick those, or apply them with Store instead.");
                return;
            }

            if (selectedAchievements.Count == 0)
            {
                ShowErrorDialog("Tick the locked achievements you want to unlock, then press Set Timer.");
                return;
            }

            // Completionist protection ("防呆"): mirror the Store guard on the timer path so a game's
            // "unlock every achievement" achievement cannot be scheduled to unlock before every other
            // achievement is unlocked or already scheduled. Opt-in / default ON.
            string completionistSkipNote = string.Empty;
            if (_protectUnlockAll)
            {
                var batchSet = new HashSet<AchievementInfo>(selectedAchievements);

                // An achievement "will be unlocked" if it is already unlocked, is being scheduled in
                // this same batch, or already has an active scheduled timer.
                bool WillBeUnlocked(AchievementInfo a) =>
                    a.IsAchieved
                    || batchSet.Contains(a)
                    || _achievementTimerService?.GetScheduledTime(a.Id) != null;

                var blockedCompletionists = AchievementCompletionDetector.FindUnsafeCompletionists(
                    _allAchievements, selectedAchievements, WillBeUnlocked, out int stillPendingForTimer);

                foreach (var blocked in blockedCompletionists)
                {
                    selectedAchievements.Remove(blocked);
                }

                if (blockedCompletionists.Count > 0)
                {
                    if (selectedAchievements.Count == 0)
                    {
                        ShowErrorDialog(
                            $"Completionist protection is ON. \"{string.Join(", ", blockedCompletionists.Select(a => a.Id))}\" " +
                            $"can only be scheduled once every other achievement is unlocked or already scheduled " +
                            $"({stillPendingForTimer} still pending). Schedule the rest first, or turn off the \"Protect Completionist\" button to override.");
                        return;
                    }

                    completionistSkipNote =
                        $" (skipped {blockedCompletionists.Count} completionist achievement(s): " +
                        $"{string.Join(", ", blockedCompletionists.Select(a => a.Id))} — schedule them after the others)";
                    StatusLabel.Text = "Completionist protection: skipped" + completionistSkipNote;
                }
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

                    // Seed the next dialog so scheduling the rest of the batch keeps this minute.
                    _lastScheduledUnlockTime = unlockTime;

                    // The Timer toggle gates the whole scheduler, so scheduling while it is off would
                    // otherwise queue something that silently never fires.
                    var pausedNote = TimerToggleButton.IsChecked == true
                        ? string.Empty
                        : " — TIMER IS OFF, nothing will unlock until you press Start Timer";

                    var formattedTime = unlockTime.ToString("yyyy-MM-dd HH:mm:ss.f");
                    StatusLabel.Text =
                        $"Scheduled {selectedAchievements.Count} achievement(s) to unlock at {formattedTime}{completionistSkipNote}{pausedNote}";
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

            // Reuse the last scheduled time so a batch keeps the same date/hour/minute and only the
            // seconds need bumping. Falls back to one hour from now when nothing was scheduled yet,
            // or when the remembered time has already passed (OnSetTimer would reject it anyway).
            var defaultTime = _lastScheduledUnlockTime is { } remembered && remembered > DateTime.Now
                ? remembered
                : DateTime.Now.AddHours(1);

            // Split into whole minutes (TimePicker has no seconds column) plus the sub-minute
            // remainder for the seconds box, truncated to the box's 0.1s resolution. Seeding
            // SelectedTime with a TimeOfDay that still carries seconds double-counts them when the
            // two controls are composed back together on OK.
            var defaultTimeOfDay = new TimeSpan(defaultTime.Hour, defaultTime.Minute, 0);
            var defaultSeconds = Math.Truncate((decimal)(defaultTime.TimeOfDay - defaultTimeOfDay).TotalSeconds * 10m) / 10m;

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
            var timePicker = new TimePicker { SelectedTime = defaultTimeOfDay };
            stack.Children.Add(timePicker);

            stack.Children.Add(new TextBlock { Text = "Seconds (0.0 – 59.9):" });
            var secondsBox = new NumericUpDown { Value = defaultSeconds, Minimum = 0, Maximum = 59.9m, Increment = 0.1m, FormatString = "0.0" };
            stack.Children.Add(secondsBox);

            var buttonPanel = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 10, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };

            var okButton = new Button { Content = "Set Timer" };
            okButton.Click += (_, _) =>
            {
                var selectedDate = datePicker.SelectedDate?.Date ?? defaultTime.Date;
                var selectedTime = timePicker.SelectedTime ?? defaultTimeOfDay;
                // Compose via exact decimal ticks: casting 0.1-step decimals to double and using
                // AddSeconds loses precision (e.g. 1.2s would render as 1.1 after ".f" truncation).
                decimal seconds = secondsBox.Value ?? 0m;
                var unlockTime = selectedDate.Add(selectedTime).AddTicks((long)(seconds * TimeSpan.TicksPerSecond));
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
                    // The tick recorded the schedule's intent, so withdraw it with the schedule.
                    _allAchievements.FirstOrDefault(a => a.Id == achievementId)?.ResetStaging();
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

                // Keyed off the ticks, like Store and Set Timer — a scheduled achievement keeps its
                // tick as the standing "I want this unlocked" intent until the timer fires.
                var selectedAchievements = _allAchievements
                    .Where(a => a.IsModified && !a.IsProtected)
                    .ToList();

                if (selectedAchievements.Count == 0)
                {
                    ShowErrorDialog("Tick the achievements whose timers you want to cancel.");
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
                        // The intent is withdrawn along with the schedule.
                        achievement.ResetStaging();
                        resetCount++;
                    }
                }

                if (resetCount == 0)
                {
                    ShowErrorDialog("None of the ticked achievements have active timers");
                    return;
                }

                StatusLabel.Text = $"Reset {resetCount} timer(s) for ticked achievements";
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
                    // A pending timer is a standing "this will be unlocked", so keep its tick on.
                    // Reloading replaces every AchievementInfo, which would otherwise drop the tick
                    // and leave the timer unreachable from the tick-driven Reset Ticked Timers.
                    achievement.DesiredAchieved = true;
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
                // Keep the detailed ToolTip from the XAML; the button Content already reflects state.
                AppLogger.LogDebug($"Auto mouse movement {(enabled ? "enabled" : "disabled")}");
            }
        }

        private void InitializeCompletionistProtection()
        {
            if (_settingsService.TryGetBool(ProtectUnlockAllSettingKey, out bool stored))
            {
                _protectUnlockAll = stored;
            }
            else
            {
                _protectUnlockAll = true; // default ON
            }

            // Programmatic IsChecked assignment does not raise Click, so the handler won't run here.
            ProtectUnlockAllButton.IsChecked = _protectUnlockAll;
        }

        private void OnToggleUnlockAllProtection(object sender, RoutedEventArgs e)
        {
            _protectUnlockAll = ProtectUnlockAllButton.IsChecked == true;
            _settingsService.TrySetBool(ProtectUnlockAllSettingKey, _protectUnlockAll);
            if (_achievementTimerService != null)
                _achievementTimerService.ProtectionEnabled = _protectUnlockAll;

            StatusLabel.Text = _protectUnlockAll
                ? "Completionist protection ON: an 'unlock all achievements' achievement is blocked until every other achievement is unlocked."
                : "Completionist protection OFF: an 'unlock all achievements' achievement can be unlocked at any time (use this for DLC-added achievements).";
            AppLogger.LogDebug($"Completionist protection set to {_protectUnlockAll}");
        }

        /// <summary>
        /// Pushes an immutable snapshot of every achievement's state (achieved / protected /
        /// completionist) to the timer service so scheduled unlocks can order and guard the
        /// completionist achievement without reading UI-thread-owned collections.
        /// </summary>
        private void PushAchievementSnapshotToTimer()
        {
            if (_achievementTimerService == null) return;

            var states = _allAchievements
                .Select(a => new AchievementState(
                    a.Id, a.IsAchieved, a.IsProtected, AchievementCompletionDetector.IsUnlockAllAchievement(a)))
                .ToList();

            _achievementTimerService.UpdateAchievementSnapshot(states);
        }

        private void OnTimerToggle(object sender, RoutedEventArgs e)
        {
            bool on = TimerToggleButton.IsChecked == true;

            // Master switch: pausing keeps every schedule, it just stops them being committed.
            // Anything that came due while paused is committed on resume, in scheduled-time order.
            if (_achievementTimerService != null)
                _achievementTimerService.SchedulingEnabled = on;

            if (on)
            {
                _achievementTimer.Start();
                TimerToggleButton.Content = "Stop Timer";
            }
            else
            {
                _achievementTimer.Stop();
                TimerToggleButton.Content = "Start Timer";
            }

            UpdateTimerStatusIndicator(on);
            // Repaint right away instead of leaving the old text until the next 1s tick.
            UpdateNextUnlockCountdown();
        }

        private void OnTimeTimerTick(object? sender, EventArgs e)
        {
            CurrentTimeLabel.Text = $"Current Time: {DateTime.Now:yyyy/MM/dd HH:mm:ss}";
            // Driven from here rather than _achievementTimer so the "paused" warning still appears
            // (and stays current) while the Timer toggle — and with it _achievementTimer — is off.
            UpdateNextUnlockCountdown();
        }

        private void OnCallbackTimerTick(object? sender, EventArgs e)
        {
            _steamClient.RunCallbacks();
        }

        private async void OnSearchDebounceTimerTick(object? sender, EventArgs e)
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
                    // Stage the unlock; PerformStore below writes everything that is staged and
                    // brings IsAchieved up to date once Steam has accepted it.
                    achievement.DesiredAchieved = true;
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

        private async Task<bool> ShowConfirmationDialog(string title, string message)
        {
            // Height follows the message: callers pass anything from one line to a multi-line list
            // of pending timers, and a fixed height clipped the buttons off the longer ones.
            var dialog = new Window
            {
                Title = title,
                Width = 450,
                MinHeight = 160,
                SizeToContent = SizeToContent.Height,
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

        /// <summary>
        /// Refreshes the label beside the timer indicator, once a second. With the Timer toggle on
        /// it counts down to the next scheduled unlock; with it off it warns that pending schedules
        /// are paused, since the toggle is the master switch and nothing will fire until it is on.
        /// </summary>
        private void UpdateNextUnlockCountdown()
        {
            var scheduled = _achievementTimerService?.GetAllScheduledAchievements();
            bool on = TimerToggleButton.IsChecked == true;

            if (scheduled is not { Count: > 0 })
            {
                NextUnlockText.Text = on ? "no scheduled unlocks" : string.Empty;
                return;
            }

            if (!on)
            {
                NextUnlockText.Text = $"⏸ {scheduled.Count} scheduled unlock(s) paused — press Start Timer";
                return;
            }

            var next = scheduled.OrderBy(kv => kv.Value).First();

            // Clamp: a due entry can sit here for up to one tick before the service commits it.
            var remaining = next.Value - DateTime.Now;
            if (remaining < TimeSpan.Zero)
                remaining = TimeSpan.Zero;

            var eta = remaining.TotalHours >= 1
                ? $"{(int)remaining.TotalHours}:{remaining.Minutes:00}:{remaining.Seconds:00}"
                : $"{remaining.Minutes}:{remaining.Seconds:00}";

            var name = _allAchievements.FirstOrDefault(a => a.Id == next.Key)?.Name;
            if (string.IsNullOrWhiteSpace(name))
                name = next.Key;

            NextUnlockText.Text = scheduled.Count > 1
                ? $"⏳ next: {name} in {eta} (+{scheduled.Count - 1} queued)"
                : $"⏳ next: {name} in {eta}";
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
                    // Steam now holds this, so clear any tick the user had staged for it.
                    achievement.ResetStaging();
                    achievement.ScheduledUnlockTime = null;
                    AppLogger.LogDebug($"UI updated for timer-unlocked achievement: {achievementId}");
                }
            });
        }

        private ISteamUserStats CreateSteamClient(long gameId)
        {
            // The constructor acquires the Steam pipe and global user handle before it decides
            // whether Initialized is true, and Dispose is the only thing that gives them back
            // (there is no finalizer). Falling through to the modern client without disposing
            // leaked both for the life of the process.
            SteamGameClient? legacyClient = null;
            try
            {
                AppLogger.LogDebug("Using Legacy SteamGameClient for Steam execution simulation...");
                legacyClient = new SteamGameClient(gameId);

                if (legacyClient.Initialized)
                {
                    AppLogger.LogDebug("Legacy SteamGameClient initialized successfully - can simulate game execution");
                    var initialized = legacyClient;
                    legacyClient = null; // ownership transfers to the caller
                    return initialized;
                }

                AppLogger.LogDebug("Legacy SteamGameClient failed to initialize");
            }
            catch (Exception ex)
            {
                AppLogger.LogDebug($"Legacy SteamGameClient creation failed: {ex.Message}");
            }
            finally
            {
                legacyClient?.Dispose();
            }

            ModernSteamClient? modernClient = null;
            try
            {
                AppLogger.LogDebug("Falling back to ModernSteamClient (limited functionality)...");
                modernClient = new ModernSteamClient(gameId);

                if (modernClient.Initialized)
                {
                    AppLogger.LogDebug("ModernSteamClient initialized successfully (but cannot simulate game execution)");
                    var initialized = modernClient;
                    modernClient = null; // ownership transfers to the caller
                    return initialized;
                }

                AppLogger.LogDebug("ModernSteamClient failed to initialize, disposing...");
            }
            catch (Exception ex)
            {
                AppLogger.LogDebug($"ModernSteamClient creation failed: {ex.Message}");
            }
            finally
            {
                // Shuts the native API down even when only SteamAPI_InitFlat got as far as
                // succeeding, so the retry below does not re-init an already-initialized API.
                modernClient?.Dispose();
            }

            AppLogger.LogDebug("Both Steam clients failed, creating non-functional ModernSteamClient for compatibility");
            return new ModernSteamClient(gameId);
        }
    }
}
