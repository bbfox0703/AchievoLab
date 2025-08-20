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
        private POINT _originalPosition;
        private bool _moveRight = true;
        private readonly IntPtr _windowHandle;

        public MouseMoverService(IntPtr windowHandle)
        {
            _windowHandle = windowHandle;
            
            // Move mouse every 30 seconds when enabled and window is in foreground
            _timer = new System.Threading.Timer(MoveMouse, null, 
                Timeout.Infinite, TimeSpan.FromSeconds(30).Milliseconds);
            
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
                        GetCursorPos(out _originalPosition);
                        
                        // Start the timer
                        _timer.Change(TimeSpan.FromSeconds(30).Milliseconds, 
                                     TimeSpan.FromSeconds(30).Milliseconds);
                        
                        DebugLogger.LogDebug("Mouse auto-movement enabled");
                    }
                    else
                    {
                        // Stop the timer
                        _timer.Change(Timeout.Infinite, Timeout.Infinite);
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

                // Calculate new position (small movement to fool Steam)
                int deltaX = _moveRight ? 5 : -5;
                int newX = currentPos.X + deltaX;
                int newY = currentPos.Y;

                // Set new cursor position
                SetCursorPos(newX, newY);

                // Immediately move back to avoid user annoyance
                Thread.Sleep(100);
                SetCursorPos(currentPos.X, currentPos.Y);

                // Alternate direction for next movement
                _moveRight = !_moveRight;

                DebugLogger.LogDebug($"Auto-moved mouse: {currentPos.X},{currentPos.Y} -> {newX},{newY} -> {currentPos.X},{currentPos.Y}");
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