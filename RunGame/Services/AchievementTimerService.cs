using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using RunGame.Models;
using RunGame.Services;
using CommonUtilities;

namespace RunGame.Services
{
    /// <summary>
    /// Immutable snapshot of one achievement's state, pushed from the UI thread to the timer service.
    /// Lets the completionist ordering/guard run entirely off the timer thread without ever reading
    /// UI-thread-owned mutable collections (e.g. GameStatsService's achievement definition list).
    /// </summary>
    public readonly record struct AchievementState(string Id, bool IsAchieved, bool IsProtected, bool IsCompletionist);

    /// <summary>
    /// Manages scheduled achievement unlocks with automatic batching to minimize Steam API calls.
    /// Checks every second for achievements that should be unlocked, then batches unlocks to reduce
    /// the number of StoreStats() calls sent to Steam.
    /// </summary>
    public class AchievementTimerService : IDisposable
    {
        private readonly GameStatsService _gameStatsService;
        // _state guards _scheduledAchievements, _pendingUnlocks, _lastStoreTime, _snapshot,
        // _unlockedSinceReload — mutated from the System.Threading.Timer thread + UI thread.
        private readonly object _state = new();
        private readonly Dictionary<string, DateTime> _scheduledAchievements = new();
        private readonly System.Threading.Timer _timer;
        private readonly List<string> _pendingUnlocks = new();
        private DateTime? _lastStoreTime = null;
        private volatile bool _disposed = false;

        // Immutable per-achievement state snapshot pushed from the UI on every (re)load. Used to
        // classify completionist achievements and to run the write-time protection guard without
        // touching UI-thread-owned collections.
        private readonly Dictionary<string, AchievementState> _snapshot = new();

        // Achievements this service has unlocked since the last pushed snapshot (their unlock is not
        // yet reflected in _snapshot). Counted as "will be unlocked" by the guard.
        private readonly HashSet<string> _unlockedSinceReload = new();

        // Guards against overlapping timer callbacks: the tick is now 100ms (0.1s resolution) and a
        // single pass can perform Steam I/O that may take longer than the interval.
        private int _inCallback;

        /// <summary>
        /// Gets or sets whether completionist protection is enabled (mirrors the UI opt-in).
        /// When true, a completionist achievement scheduled at the same time as others is only
        /// unlocked once every other (non-protected) achievement is already unlocked.
        /// </summary>
        public bool ProtectionEnabled { get; set; } = true;

        /// <summary>
        /// Occurs when the service status changes (e.g., achievement unlocked, stats stored).
        /// </summary>
        public event Action<string>? StatusUpdated;

        /// <summary>
        /// Occurs when an achievement is unlocked by the timer service.
        /// The string parameter is the achievement ID that was unlocked.
        /// </summary>
        public event Action<string>? AchievementUnlocked;

        /// <summary>
        /// Initializes a new instance of the <see cref="AchievementTimerService"/> class.
        /// Starts a timer that checks every second for scheduled achievements to unlock.
        /// </summary>
        /// <param name="gameStatsService">The game stats service used to unlock achievements and store stats.</param>
        public AchievementTimerService(GameStatsService gameStatsService)
        {
            _gameStatsService = gameStatsService;

            // Check every 100ms so scheduled unlocks fire at 0.1-second resolution.
            _timer = new System.Threading.Timer(CheckScheduledAchievements, null,
                TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(100));

            AppLogger.LogDebug("AchievementTimerService initialized");
        }

        /// <summary>
        /// Schedules an achievement to be unlocked at a specific time.
        /// If the unlock time is in the past, the schedule is ignored.
        /// </summary>
        /// <param name="achievementId">The unique achievement identifier.</param>
        /// <param name="unlockTime">The date and time when the achievement should be unlocked.</param>
        public void ScheduleAchievement(string achievementId, DateTime unlockTime)
        {
            // Normalize to UTC so DST shifts don't shift the schedule.
            // Unspecified kind is treated as Local (typical for callers using DateTime.Now-based input).
            var unlockUtc = unlockTime.Kind == DateTimeKind.Utc
                ? unlockTime
                : unlockTime.ToUniversalTime();

            if (unlockUtc <= DateTime.UtcNow)
            {
                AppLogger.LogDebug($"Unlock time {unlockTime} is in the past, ignoring schedule for {achievementId}");
                return;
            }

            lock (_state)
            {
                _scheduledAchievements[achievementId] = unlockUtc;
            }
            AppLogger.LogDebug($"Scheduled achievement {achievementId} to unlock at {unlockTime}");
        }

        /// <summary>
        /// Cancels a previously scheduled achievement unlock.
        /// </summary>
        /// <param name="achievementId">The unique achievement identifier.</param>
        public void CancelSchedule(string achievementId)
        {
            bool removed;
            lock (_state)
            {
                removed = _scheduledAchievements.Remove(achievementId);
            }
            if (removed)
            {
                AppLogger.LogDebug($"Cancelled scheduled unlock for achievement {achievementId}");
            }
        }

        /// <summary>
        /// Gets the scheduled unlock time for a specific achievement.
        /// </summary>
        /// <param name="achievementId">The unique achievement identifier.</param>
        /// <returns>The scheduled unlock time, or null if the achievement is not scheduled.</returns>
        public DateTime? GetScheduledTime(string achievementId)
        {
            lock (_state)
            {
                // Return as Local so existing UI binding semantics are preserved.
                return _scheduledAchievements.TryGetValue(achievementId, out var timeUtc)
                    ? timeUtc.ToLocalTime()
                    : null;
            }
        }

        /// <summary>
        /// Gets a copy of all currently scheduled achievements and their unlock times.
        /// </summary>
        /// <returns>A dictionary mapping achievement IDs to their scheduled unlock times (local time).</returns>
        public Dictionary<string, DateTime> GetAllScheduledAchievements()
        {
            lock (_state)
            {
                return _scheduledAchievements.ToDictionary(kv => kv.Key, kv => kv.Value.ToLocalTime());
            }
        }

        /// <summary>
        /// Notifies the service that stats have been reloaded from Steam.
        /// Updates the status message with information about active timers.
        /// </summary>
        public void NotifyStatsReloaded()
        {
            int count;
            double? secondsToNext = null;
            lock (_state)
            {
                count = _scheduledAchievements.Count;
                if (count > 0)
                {
                    var nextUtc = GetNextScheduledTimeUtcUnlocked();
                    if (nextUtc.HasValue)
                    {
                        secondsToNext = (nextUtc.Value - DateTime.UtcNow).TotalSeconds;
                    }
                }
            }
            if (count > 0 && secondsToNext.HasValue)
            {
                StatusUpdated?.Invoke($"Stats reloaded. {count} timer{(count != 1 ? "s" : "")} active, next in {secondsToNext.Value:F0}s");
            }
        }

        /// <summary>
        /// Replaces the per-achievement state snapshot. Called by the UI whenever achievements are
        /// (re)loaded so the service can classify completionist achievements and run the write-time
        /// protection guard without reading UI-thread-owned data. Clears the "unlocked since reload"
        /// delta because the fresh snapshot already reflects those unlocks.
        /// </summary>
        /// <param name="states">The current state of every achievement in the game.</param>
        public void UpdateAchievementSnapshot(IEnumerable<AchievementState> states)
        {
            lock (_state)
            {
                _snapshot.Clear();
                foreach (var s in states)
                {
                    if (!string.IsNullOrEmpty(s.Id))
                        _snapshot[s.Id] = s;
                }
                _unlockedSinceReload.Clear();
            }
        }

        /// <summary>
        /// Timer callback that checks for achievements scheduled to be unlocked.
        /// Non-completionist achievements are written first (with the usual batching); any
        /// completionist ("unlock every achievement") achievement due at the same time is always
        /// committed LAST in its own StoreStats. When <see cref="ProtectionEnabled"/> is true, a due
        /// completionist is only unlocked if every other non-protected achievement is already
        /// unlocked or is being unlocked in this same tick.
        /// </summary>
        /// <param name="state">Timer state (unused).</param>
        private void CheckScheduledAchievements(object? state)
        {
            // Skip this tick if the previous one is still running (100ms interval + Steam I/O), or if
            // the service has been disposed (window closing) so we don't touch a torn-down Steam client.
            if (_disposed || System.Threading.Interlocked.CompareExchange(ref _inCallback, 1, 0) != 0)
                return;

            try
            {
                var nowUtc = DateTime.UtcNow;

                // Snapshot due achievements + the pushed state map under lock, release during Steam I/O.
                List<KeyValuePair<string, DateTime>> due;
                Dictionary<string, AchievementState> snapshot;
                lock (_state)
                {
                    due = _scheduledAchievements.Where(kvp => kvp.Value <= nowUtc).ToList();
                    snapshot = new Dictionary<string, AchievementState>(_snapshot);
                }
                if (due.Count == 0)
                    return;

                bool IsCompletionist(string id) => snapshot.TryGetValue(id, out var s) && s.IsCompletionist;

                var dueOthers = due.Where(kvp => !IsCompletionist(kvp.Key)).ToList();
                var dueCompletionists = due.Where(kvp => IsCompletionist(kvp.Key)).ToList();

                // ---- Phase 1: non-completionist unlocks ----
                bool phase1Unlocked = false;
                foreach (var kvp in dueOthers)
                {
                    if (UnlockOne(kvp.Key, kvp.Value, nowUtc))
                        phase1Unlocked = true;
                    lock (_state) { _scheduledAchievements.Remove(kvp.Key); }
                }

                if (dueCompletionists.Count == 0)
                {
                    // Common case: keep the existing batching (delay StoreStats to group unlocks).
                    if (phase1Unlocked)
                        MaybeStoreWithBatching(nowUtc);
                    return;
                }

                // A completionist is due → it must be the LAST write. Commit everything staged so far
                // (phase-1 + any prior pending) before touching the completionist.
                bool anythingPending;
                lock (_state) { anythingPending = _pendingUnlocks.Count > 0; }
                if (anythingPending)
                    StoreNow(nowUtc);

                // What counts as "already unlocked" for the guard: everything unlocked by this service
                // since the last snapshot (includes this tick's phase-1 unlocks and prior timer unlocks).
                HashSet<string> unlockedView;
                lock (_state) { unlockedView = new HashSet<string>(_unlockedSinceReload); }

                // ---- Phase 2: completionists, written last ----
                bool phase2Unlocked = false;
                foreach (var kvp in dueCompletionists)
                {
                    var id = kvp.Key;

                    if (ProtectionEnabled && !IsCompletionistSafe(id, snapshot, unlockedView, out int remaining))
                    {
                        AppLogger.LogDebug($"Completionist protection: skipping scheduled unlock of {id} ({remaining} still locked)");
                        StatusUpdated?.Invoke($"Completionist protection: skipped scheduled '{id}' — {remaining} achievement(s) still locked");
                        lock (_state) { _scheduledAchievements.Remove(id); }
                        continue;
                    }

                    if (UnlockOne(id, kvp.Value, nowUtc))
                        phase2Unlocked = true;
                    lock (_state) { _scheduledAchievements.Remove(id); }
                }

                if (phase2Unlocked)
                    StoreNow(nowUtc);
            }
            catch (Exception ex)
            {
                AppLogger.LogDebug($"Error in CheckScheduledAchievements: {ex.Message}");
            }
            finally
            {
                System.Threading.Interlocked.Exchange(ref _inCallback, 0);
            }
        }

        /// <summary>
        /// Returns whether a completionist achievement is safe to unlock now: every OTHER non-protected
        /// achievement in the snapshot must be already unlocked, or unlocked by the timer since the last
        /// snapshot (<paramref name="unlockedView"/>). Protected achievements are excluded because the
        /// tool can never toggle them. Outputs how many are still locked.
        /// </summary>
        private static bool IsCompletionistSafe(
            string completionistId,
            Dictionary<string, AchievementState> snapshot,
            HashSet<string> unlockedView,
            out int remaining)
        {
            remaining = 0;
            foreach (var s in snapshot.Values)
            {
                if (s.Id == completionistId) continue;
                if (s.IsProtected) continue;
                if (s.IsAchieved || unlockedView.Contains(s.Id)) continue;
                remaining++;
            }
            return remaining == 0;
        }

        /// <summary>
        /// Sets a single achievement to unlocked and, on success, records it as pending and notifies
        /// the UI. Does not commit to Steam (the caller decides when to call StoreStats).
        /// </summary>
        /// <returns><c>true</c> if the achievement was set successfully.</returns>
        private bool UnlockOne(string achievementId, DateTime scheduledTimeUtc, DateTime nowUtc)
        {
            AppLogger.LogDebug($"Unlocking scheduled achievement {achievementId} (scheduled for {scheduledTimeUtc.ToLocalTime()}, now {nowUtc.ToLocalTime()})");

            if (_gameStatsService.SetAchievement(achievementId, true))
            {
                AppLogger.LogDebug($"Successfully set achievement {achievementId} to unlocked");
                lock (_state)
                {
                    _pendingUnlocks.Add(achievementId);
                    _unlockedSinceReload.Add(achievementId);
                }
                AchievementUnlocked?.Invoke(achievementId);
                return true;
            }

            AppLogger.LogDebug($"Failed to unlock achievement {achievementId}");
            return false;
        }

        /// <summary>
        /// Commits all pending unlocks to Steam immediately and reports the result.
        /// </summary>
        private void StoreNow(DateTime nowUtc)
        {
            _gameStatsService.StoreStats();
            int unlockCount;
            lock (_state)
            {
                _lastStoreTime = nowUtc;
                unlockCount = _pendingUnlocks.Count;
                _pendingUnlocks.Clear();
            }
            AppLogger.LogDebug($"Stored {unlockCount} achievement unlocks to Steam");
            StatusUpdated?.Invoke($"Stored {unlockCount} achievement unlock{(unlockCount != 1 ? "s" : "")} to Steam at {nowUtc.ToLocalTime():HH:mm:ss.f}");
        }

        /// <summary>
        /// Decides whether to commit pending unlocks now or delay to batch with an upcoming unlock.
        /// Stores immediately unless another achievement is scheduled within 12 seconds.
        /// </summary>
        private void MaybeStoreWithBatching(DateTime nowUtc)
        {
            DateTime? nextScheduledTimeUtc;
            int pendingCount;
            lock (_state)
            {
                nextScheduledTimeUtc = GetNextScheduledTimeUtcUnlocked();
                pendingCount = _pendingUnlocks.Count;
            }

            bool shouldStore;
            if (nextScheduledTimeUtc == null)
            {
                shouldStore = true;
                AppLogger.LogDebug("No more scheduled achievements, storing changes now");
            }
            else if ((nextScheduledTimeUtc.Value - nowUtc).TotalSeconds > 12)
            {
                shouldStore = true;
                AppLogger.LogDebug($"Next achievement is {(nextScheduledTimeUtc.Value - nowUtc).TotalSeconds:F1} seconds away, storing changes now");
            }
            else
            {
                shouldStore = false;
                AppLogger.LogDebug($"Next achievement is in {(nextScheduledTimeUtc.Value - nowUtc).TotalSeconds:F1} seconds, delaying store");
            }

            if (shouldStore)
            {
                StoreNow(nowUtc);
            }
            else if (pendingCount > 0)
            {
                StatusUpdated?.Invoke($"Pending {pendingCount} achievement unlock{(pendingCount != 1 ? "s" : "")}, next store in {(nextScheduledTimeUtc!.Value - nowUtc).TotalSeconds:F0}s");
            }
        }

        /// <summary>
        /// Returns the next scheduled UTC unlock time. Caller must hold <c>_state</c>.
        /// </summary>
        private DateTime? GetNextScheduledTimeUtcUnlocked()
        {
            if (_scheduledAchievements.Count == 0)
                return null;

            return _scheduledAchievements.Values.Min();
        }

        /// <summary>
        /// Releases resources used by the service, stopping the timer.
        /// </summary>
        public void Dispose()
        {
            if (!_disposed)
            {
                // Set first so a callback that is about to start bails at its _disposed check.
                _disposed = true;
                if (_timer != null)
                {
                    // Wait for any in-flight callback to finish before returning, so the caller can
                    // safely dispose the Steam client without a callback touching it mid-teardown.
                    using var callbacksDone = new System.Threading.ManualResetEvent(false);
                    if (_timer.Dispose(callbacksDone))
                        callbacksDone.WaitOne(TimeSpan.FromSeconds(2));
                }
                AppLogger.LogDebug("AchievementTimerService disposed");
            }
        }
    }
}
