using System;
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

                if (File.Exists(path))
                {
                    // Load via MemoryStream to avoid holding file handles open
                    // Decode to limited height (200px) to reduce memory and decoding time
                    // Steam header images are 460x215, no need to decode at full resolution
                    var bytes = File.ReadAllBytes(path);
                    using var ms = new MemoryStream(bytes);
                    return Bitmap.DecodeToHeight(ms, 200);
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
