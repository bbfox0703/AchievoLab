using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace RunGame.Services
{
    public class MouseMoverService : IDisposable
    {
        [DllImport("user32.dll")]
        private static extern bool GetCursorPos(out POINT lpPoint);

        [DllImport("user32.dll")]
        private static extern bool SetCursorPos(int x, int y);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int X;
            public int Y;
        }

        private readonly System.Threading.Timer _timer;
        private bool _isEnabled;
        private bool _disposed = false;
        private POINT _lastMousePos;
        private bool _moveRight = true;
        private readonly IntPtr _windowHandle;

        public MouseMoverService(IntPtr windowHandle)
        {
            _windowHandle = windowHandle;

            // Move mouse every 30 seconds when enabled and window is in foreground
            _timer = new System.Threading.Timer(MoveMouse, null,
                Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);

            DebugLogger.LogDebug("MouseMoverService initialized");
        }

        public bool IsEnabled
        {
            get => _isEnabled;
            set
            {
                if (_isEnabled != value)
                {
                    _isEnabled = value;
                    
                    if (_isEnabled)
                    {
                        // Get current mouse position as starting point
                        GetCursorPos(out _lastMousePos);
                        
                        // Start the timer
                        _timer.Change(TimeSpan.FromSeconds(30),
                                     TimeSpan.FromSeconds(30));
                        
                        DebugLogger.LogDebug("Mouse auto-movement enabled");
                    }
                    else
                    {
                        // Stop the timer
                        _timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
                        DebugLogger.LogDebug("Mouse auto-movement disabled");
                    }
                }
            }
        }

        private void MoveMouse(object? state)
        {
            try
            {
                if (!_isEnabled) return;

                // Check if our window is in the foreground
                var foregroundWindow = GetForegroundWindow();
                if (foregroundWindow != _windowHandle)
                {
                    DebugLogger.LogDebug("Window not in foreground, skipping mouse movement");
                    return;
                }

                // Get current cursor position
                GetCursorPos(out var currentPos);

                // If the cursor has moved since last run, update and exit
                if (currentPos.X != _lastMousePos.X || currentPos.Y != _lastMousePos.Y)
                {
                    _lastMousePos = currentPos;
                    return;
                }

                int moveDistance = 5;
                int direction = _moveRight ? 1 : -1;

                // Move gradually to avoid detection
                for (int i = 1; i <= moveDistance; i++)
                {
                    SetCursorPos(currentPos.X + direction * i, currentPos.Y);
                    Thread.Sleep(15);
                }

                // Toggle direction for next call
                _moveRight = !_moveRight;

                // Update last known position
                _lastMousePos = new POINT { X = currentPos.X + direction * moveDistance, Y = currentPos.Y };

                DebugLogger.LogDebug($"Auto-moved mouse: {currentPos.X},{currentPos.Y} -> {_lastMousePos.X},{_lastMousePos.Y}");
            }
            catch (Exception ex)
            {
                DebugLogger.LogDebug($"Error in MoveMouse: {ex.Message}");
            }
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                IsEnabled = false;
                _timer?.Dispose();
                _disposed = true;
                DebugLogger.LogDebug("MouseMoverService disposed");
            }
        }
    }
}