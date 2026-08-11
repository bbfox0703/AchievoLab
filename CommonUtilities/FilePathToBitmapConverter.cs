using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Avalonia.Data.Converters;
using Avalonia.Media.Imaging;

namespace CommonUtilities
{
    /// <summary>
    /// Converts a file path string to an Avalonia Bitmap for use with Image.Source bindings.
    /// Required for compiled bindings where string → IImage auto-conversion is not available.
    /// </summary>
    public class FilePathToBitmapConverter : IValueConverter
    {
        public static FilePathToBitmapConverter Instance { get; } = new();
        private int _logCount;

        /// <summary>
        /// Maximum number of decoded bitmaps kept alive. Large enough to cover a full non-virtualized
        /// game grid, so an entry is only ever dropped once it is well out of view.
        /// </summary>
        private const int MaxCachedBitmaps = 512;

        private readonly object _cacheLock = new();
        private readonly Dictionary<string, Bitmap> _cache = new(StringComparer.OrdinalIgnoreCase);
        private readonly Queue<string> _insertionOrder = new();

        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is not string path || string.IsNullOrEmpty(path))
                return null;

            try
            {
                // Handle file:// URIs (e.g., "file:///C:/path/to/image.jpg")
                if (path.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
                {
                    path = new Uri(path).LocalPath;
                }

                lock (_cacheLock)
                {
                    if (_cache.TryGetValue(path, out var cached))
                    {
                        return cached;
                    }
                }

                if (File.Exists(path))
                {
                    // Load via MemoryStream to avoid holding file handles open
                    // Decode to limited height (200px) to reduce memory and decoding time
                    // Steam header images are 460x215, no need to decode at full resolution
                    var bytes = File.ReadAllBytes(path);
                    using var ms = new MemoryStream(bytes);
                    var bitmap = Bitmap.DecodeToHeight(ms, 200);

                    lock (_cacheLock)
                    {
                        // A concurrent call may have decoded the same path already; prefer the
                        // cached instance so every Image bound to this path shares one bitmap.
                        if (_cache.TryGetValue(path, out var raced))
                        {
                            bitmap.Dispose();
                            return raced;
                        }

                        _cache[path] = bitmap;
                        _insertionOrder.Enqueue(path);

                        while (_insertionOrder.Count > MaxCachedBitmaps)
                        {
                            var evicted = _insertionOrder.Dequeue();
                            // Deliberately not disposed: the converter hands the same instance to
                            // every Image bound to that path and has no way to know whether one is
                            // still on screen. Dropping the reference lets the GC reclaim it.
                            _cache.Remove(evicted);
                        }
                    }

                    return bitmap;
                }
                else if (_logCount < 10)
                {
                    AppLogger.LogDebug($"FilePathToBitmapConverter: File not found: {path}");
                    _logCount++;
                }
            }
            catch (Exception ex)
            {
                if (_logCount < 10)
                {
                    AppLogger.LogDebug($"FilePathToBitmapConverter error for '{path}': {ex.Message}");
                    _logCount++;
                }
            }
            return null;
        }

        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }
}
