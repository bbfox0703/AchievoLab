using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Platform;
using Avalonia.Threading;

namespace CommonUtilities
{
    /// <summary>
    /// Gives an Avalonia <see cref="Window"/> persistent position/size/maximized
    /// memory across restarts, while fixing two long-standing Avalonia quirks:
    ///
    /// <list type="number">
    ///   <item>A window restored onto a now-disconnected monitor is detected as
    ///   off-screen and re-centered on the primary monitor.</item>
    ///   <item>Repeated maximize/restore cycles no longer drift, because the
    ///   "normal" rectangle is snapshotted (with a deferred commit that survives
    ///   Avalonia's Width/Height-before-WindowState property ordering) and forcibly
    ///   re-applied whenever the window returns to the Normal state.</item>
    /// </list>
    ///
    /// Placement is persisted through the shared <see cref="ApplicationSettingsService"/>
    /// (one settings key per window), so all three executables round-trip through the
    /// same <c>settings.json</c>. Attach once, right after <c>InitializeComponent()</c>:
    /// <code>WindowPlacementManager.Attach(this, "AnSAM");</code>
    /// </summary>
    public sealed class WindowPlacementManager
    {
        // Hard floor so a corrupt/tiny saved value can never restore a degenerate window,
        // even when the window declares no MinWidth/MinHeight.
        private const double MinRestoreWidth = 200;
        private const double MinRestoreHeight = 150;

        // Used only if a window somehow reports NaN Width/Height at attach time.
        private const double FallbackDefaultWidth = 1000;
        private const double FallbackDefaultHeight = 700;

        private readonly Window _window;
        private readonly string _settingsKey;
        private readonly ApplicationSettingsService _settings;
        private readonly double _defaultWidth;
        private readonly double _defaultHeight;

        // Committed "normal" snapshot — the rect to restore to when un-maximizing or
        // when persisting while non-Normal. Position in physical px, size in DIPs.
        private PixelPoint? _normalPosition;
        private double _normalWidth;
        private double _normalHeight;

        // Pending stash, promoted into the snapshot one dispatcher tick later (so the
        // Width/Height changes that precede the WindowState change during a maximize
        // are discarded once we re-read the settled WindowState).
        private PixelPoint _pendingPosition;
        private double _pendingWidth;
        private double _pendingHeight;
        private bool _commitScheduled;

        private WindowState _previousState = WindowState.Normal;
        private WindowState _lastNonMinimizedState = WindowState.Normal;

        private bool _restorePending;   // a saved rect was applied; validate it on Opened
        private bool _closed;

        private WindowPlacementManager(Window window, string settingsKey, ApplicationSettingsService settings)
        {
            _window = window;
            _settingsKey = settingsKey;
            _settings = settings;

            _defaultWidth = double.IsNaN(window.Width) || window.Width <= 0 ? FallbackDefaultWidth : window.Width;
            _defaultHeight = double.IsNaN(window.Height) || window.Height <= 0 ? FallbackDefaultHeight : window.Height;
            _normalWidth = _defaultWidth;
            _normalHeight = _defaultHeight;
            _pendingWidth = _defaultWidth;
            _pendingHeight = _defaultHeight;
        }

        /// <summary>
        /// Attaches placement memory to <paramref name="window"/>. Call once in the
        /// window constructor immediately after <c>InitializeComponent()</c>, before
        /// the window is shown.
        /// </summary>
        /// <param name="window">The window to manage.</param>
        /// <param name="name">
        /// Stable per-window identifier (e.g. "AnSAM"). Used as the settings key suffix,
        /// so the three executables that share <c>settings.json</c> stay independent.
        /// </param>
        /// <param name="settings">
        /// Optional settings service; a fresh one is created when omitted (they all point
        /// at the same shared <c>settings.json</c>).
        /// </param>
        public static WindowPlacementManager Attach(Window window, string name, ApplicationSettingsService? settings = null)
        {
            ArgumentNullException.ThrowIfNull(window);
            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentException("A non-empty window name is required.", nameof(name));

            var manager = new WindowPlacementManager(window, $"Window.{name}", settings ?? new ApplicationSettingsService());
            manager.Initialize();
            return manager;
        }

        private void Initialize()
        {
            try
            {
                ApplySavedPlacement();
            }
            catch (Exception ex)
            {
                // Placement is best-effort; never let it break window creation.
                AppLogger.LogDebug($"WindowPlacementManager[{_settingsKey}]: restore failed: {ex.Message}");
                _restorePending = false;
                _window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            }

            // Subscribe AFTER applying the initial placement so our own setup writes
            // don't get mistaken for user moves/resizes.
            _window.PropertyChanged += OnWindowPropertyChanged;
            _window.PositionChanged += OnPositionChanged;
            _window.Opened += OnOpened;
            _window.Closing += OnClosing;
        }

        private void ApplySavedPlacement()
        {
            if (_settings.TryGetString(_settingsKey, out var raw) &&
                WindowPlacementRecord.TryParse(raw, out var record))
            {
                double w = Math.Max(record.Width, EffectiveMinWidth());
                double h = Math.Max(record.Height, EffectiveMinHeight());
                var pos = new PixelPoint(record.X, record.Y);

                _window.WindowStartupLocation = WindowStartupLocation.Manual;
                _window.Position = pos;
                _window.Width = w;
                _window.Height = h;

                _normalPosition = pos;
                _normalWidth = w;
                _normalHeight = h;
                _pendingPosition = pos;
                _pendingWidth = w;
                _pendingHeight = h;

                _restorePending = true;

                if (record.Maximized)
                {
                    _previousState = WindowState.Maximized;
                    _lastNonMinimizedState = WindowState.Maximized;
                    _window.WindowState = WindowState.Maximized;
                }
            }
            else
            {
                // First run / corrupt value: center on the screen, keep XAML default size.
                _window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            }
        }

        // ── Live tracking ────────────────────────────────────────────────────────

        private void OnWindowPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
        {
            if (e.Property == Window.WindowStateProperty)
            {
                var newState = e.GetNewValue<WindowState>();
                HandleWindowStateTransition(_previousState, newState);
                _previousState = newState;
                if (newState != WindowState.Minimized)
                    _lastNonMinimizedState = newState;
            }
            else if (e.Property == Layoutable.WidthProperty || e.Property == Layoutable.HeightProperty)
            {
                if (_window.WindowState == WindowState.Normal)
                {
                    _pendingWidth = ResolveWidth();
                    _pendingHeight = ResolveHeight();
                    ScheduleCommit();
                }
            }
        }

        private void OnPositionChanged(object? sender, PixelPointEventArgs e)
        {
            // Position is not an AvaloniaProperty in Avalonia 12 — it surfaces here.
            if (_window.WindowState == WindowState.Normal &&
                IsPositionAcceptable(_window.Position, _pendingWidth, _pendingHeight))
            {
                _pendingPosition = _window.Position;
                ScheduleCommit();
            }
        }

        private void ScheduleCommit()
        {
            if (_commitScheduled || _closed)
                return;
            _commitScheduled = true;
            Dispatcher.UIThread.Post(CommitSnapshot, DispatcherPriority.Background);
        }

        /// <summary>
        /// Promote the pending stash into the committed snapshot — but only while the
        /// window is genuinely Normal. If it flipped to Maximized/Minimized since the
        /// stash, the stashed (maximized-origin) values are discarded.
        /// </summary>
        private void CommitSnapshot()
        {
            _commitScheduled = false;
            if (_closed || _window.WindowState != WindowState.Normal)
                return;

            if (_pendingWidth > 0)
                _normalWidth = _pendingWidth;
            if (_pendingHeight > 0)
                _normalHeight = _pendingHeight;
            if (IsPositionAcceptable(_pendingPosition, _pendingWidth, _pendingHeight))
                _normalPosition = _pendingPosition;
        }

        // ── Maximize / restore drift fix ─────────────────────────────────────────

        private void HandleWindowStateTransition(WindowState oldState, WindowState newState)
        {
            // Returning to Normal: forcibly re-apply the committed snapshot so the OS's
            // restore placement can't drift or straddle monitors. Deferred to Background
            // priority so it runs after Avalonia finishes its own restore layout.
            if (newState == WindowState.Normal && oldState != WindowState.Normal)
            {
                Dispatcher.UIThread.Post(ReapplyNormalSnapshot, DispatcherPriority.Background);
            }
        }

        private void ReapplyNormalSnapshot()
        {
            // Never fight the startup restore validation, and never touch a closed window.
            if (_closed || _restorePending || _window.WindowState != WindowState.Normal)
                return;

            if (_normalWidth > 0)
                _window.Width = _normalWidth;
            if (_normalHeight > 0)
                _window.Height = _normalHeight;
            if (_normalPosition is { } pos)
                _window.Position = pos;

            // Re-seed the stash so the re-apply's own change events aren't read as a move.
            _pendingPosition = _normalPosition ?? _pendingPosition;
            _pendingWidth = _normalWidth;
            _pendingHeight = _normalHeight;
        }

        // ── Startup off-screen validation ────────────────────────────────────────

        private void OnOpened(object? sender, EventArgs e)
        {
            _window.Opened -= OnOpened;
            if (!_restorePending)
                return;
            _restorePending = false;

            var screens = CurrentScreenWorkingAreas();
            if (screens.Count == 0)
                return; // can't reason about screens; keep restored placement

            double scale = RenderScale();
            int rx = _normalPosition?.X ?? _window.Position.X;
            int ry = _normalPosition?.Y ?? _window.Position.Y;
            int rw = (int)Math.Round(_normalWidth * scale);
            int rh = (int)Math.Round(_normalHeight * scale);

            if (WindowPlacement.IsVisibleEnough(rx, ry, rw, rh, screens))
                return; // a grabbable chunk is on some monitor — leave as restored

            ResetToDefaultPlacement();
        }

        private void ResetToDefaultPlacement()
        {
            if (_window.WindowState != WindowState.Normal)
                _window.WindowState = WindowState.Normal;

            _window.Width = _defaultWidth;
            _window.Height = _defaultHeight;

            // Center using the PRIMARY monitor's own scaling (correct on mixed-DPI setups).
            var primary = PrimaryWorkingArea();
            double primaryScale = PrimaryScaling();
            int pw = (int)Math.Round(_defaultWidth * primaryScale);
            int ph = (int)Math.Round(_defaultHeight * primaryScale);
            var (cx, cy) = WindowPlacement.CenterIn(primary, pw, ph);
            var pos = new PixelPoint(cx, cy);
            _window.Position = pos;

            _normalPosition = pos;
            _normalWidth = _defaultWidth;
            _normalHeight = _defaultHeight;
            _pendingPosition = pos;
            _pendingWidth = _defaultWidth;
            _pendingHeight = _defaultHeight;
        }

        // ── Persistence ──────────────────────────────────────────────────────────

        private void OnClosing(object? sender, WindowClosingEventArgs e)
        {
            SavePlacement();
        }

        /// <summary>
        /// Persists the current placement immediately and detaches. Idempotent and safe
        /// to call from a <c>ProcessExit</c> / shutdown backstop in addition to the
        /// window's <c>Closing</c> event — once placement has been saved this no-ops, so
        /// there is no double-write.
        /// </summary>
        public void SavePlacement()
        {
            if (_closed)
                return;
            _closed = true;

            SaveCurrentPlacement();

            _window.PropertyChanged -= OnWindowPropertyChanged;
            _window.PositionChanged -= OnPositionChanged;
            _window.Closing -= OnClosing;
        }

        private void SaveCurrentPlacement()
        {
            try
            {
                bool maximized = _window.WindowState == WindowState.Maximized ||
                    (_window.WindowState == WindowState.Minimized && _lastNonMinimizedState == WindowState.Maximized);

                int x, y;
                double w, h;
                if (_window.WindowState == WindowState.Normal)
                {
                    // Live geometry is authoritative while Normal.
                    x = _window.Position.X;
                    y = _window.Position.Y;
                    w = ResolveWidth();
                    h = ResolveHeight();
                }
                else
                {
                    // Maximized/minimized/fullscreen: persist the tracked normal snapshot.
                    var p = _normalPosition ?? _window.Position;
                    x = p.X;
                    y = p.Y;
                    w = _normalWidth > 0 ? _normalWidth : ResolveWidth();
                    h = _normalHeight > 0 ? _normalHeight : ResolveHeight();
                }

                var record = new WindowPlacementRecord(x, y, w, h, maximized);
                _settings.TrySetString(_settingsKey, record.Serialize());
            }
            catch (Exception ex)
            {
                AppLogger.LogDebug($"WindowPlacementManager[{_settingsKey}]: save failed: {ex.Message}");
            }
        }

        // ── Helpers ──────────────────────────────────────────────────────────────

        private double ResolveWidth()
        {
            double w = _window.Width;
            if (double.IsNaN(w) || w <= 0)
                w = _window.Bounds.Width;
            return w > 0 ? w : _defaultWidth;
        }

        private double ResolveHeight()
        {
            double h = _window.Height;
            if (double.IsNaN(h) || h <= 0)
                h = _window.Bounds.Height;
            return h > 0 ? h : _defaultHeight;
        }

        private double EffectiveMinWidth()
            => Math.Max(MinRestoreWidth, double.IsNaN(_window.MinWidth) ? 0 : _window.MinWidth);

        private double EffectiveMinHeight()
            => Math.Max(MinRestoreHeight, double.IsNaN(_window.MinHeight) ? 0 : _window.MinHeight);

        private bool IsPositionAcceptable(PixelPoint pos, double dipWidth, double dipHeight)
        {
            var screens = CurrentScreenWorkingAreas();
            if (screens.Count == 0)
                return true;

            double scale = RenderScale();
            return WindowPlacement.IsVisibleEnough(
                pos.X, pos.Y,
                (int)Math.Round(dipWidth * scale), (int)Math.Round(dipHeight * scale),
                screens);
        }

        private double RenderScale()
        {
            double s = _window.RenderScaling;
            return s > 0 ? s : 1.0;
        }

        private List<(int X, int Y, int W, int H)> CurrentScreenWorkingAreas()
        {
            var list = new List<(int, int, int, int)>();
            var all = _window.Screens?.All;
            if (all == null)
                return list;

            foreach (var s in all)
            {
                var wa = s.WorkingArea;
                list.Add((wa.X, wa.Y, wa.Width, wa.Height));
            }
            return list;
        }

        private (int X, int Y, int W, int H) PrimaryWorkingArea()
        {
            var primary = ResolvePrimaryScreen();
            if (primary != null)
            {
                var wa = primary.WorkingArea;
                return (wa.X, wa.Y, wa.Width, wa.Height);
            }
            return (0, 0, 1920, 1080);
        }

        private double PrimaryScaling()
        {
            double s = ResolvePrimaryScreen()?.Scaling ?? 1.0;
            return s > 0 ? s : 1.0;
        }

        private Screen? ResolvePrimaryScreen()
        {
            var screens = _window.Screens;
            if (screens == null)
                return null;

            var primary = screens.Primary;
            if (primary != null)
                return primary;

            var all = screens.All;
            return all != null && all.Count > 0 ? all[0] : null;
        }
    }
}
