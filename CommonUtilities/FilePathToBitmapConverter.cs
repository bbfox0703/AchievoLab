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
                    return new Bitmap(path);
                }
            }
            catch
            {
                // Ignore invalid paths or corrupted images
            }
            return null;
        }

        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }
}
