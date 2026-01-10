using System;
using System.IO;

namespace CommonUtilities
{
    public static class ImageValidation
    {
        public static bool IsValidImage(string path)
        {
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists || info.Length == 0)
                {
                    return false;
                }

                Span<byte> header = stackalloc byte[12];
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                int read = fs.Read(header);
                if (read >= 4)
                {
                    if (header[0] == 0x89 && header[1] == 0x50 && header[2] == 0x4E && header[3] == 0x47)
                        return true; // PNG
                    if (header[0] == 0xFF && header[1] == 0xD8)
                        return true; // JPEG
                    if (header[0] == 0x47 && header[1] == 0x49 && header[2] == 0x46)
                        return true; // GIF
                    if (header[0] == 0x42 && header[1] == 0x4D)
                        return true; // BMP
                    if (header[0] == 0x00 && header[1] == 0x00 && header[2] == 0x01 && header[3] == 0x00)
                        return true; // ICO
                    if (read >= 12 && header[4] == 0x66 && header[5] == 0x74 && header[6] == 0x79 && header[7] == 0x70 &&
                        header[8] == 0x61 && header[9] == 0x76 && header[10] == 0x69 && header[11] == 0x66)
                        return true; // AVIF
                    if (read >= 12 && header[0] == 0x52 && header[1] == 0x49 && header[2] == 0x46 && header[3] == 0x46 &&
                        header[8] == 0x57 && header[9] == 0x45 && header[10] == 0x42 && header[11] == 0x50)
                        return true; // WEBP
                }
            }
            catch (UnauthorizedAccessException ex)
            {
                DebugLogger.LogDebug($"Access denied validating image '{path}': {ex.Message}");
            }
            catch (IOException ex)
            {
                DebugLogger.LogDebug($"IO error validating image '{path}': {ex.Message}");
            }
            catch (Exception ex)
            {
                DebugLogger.LogDebug($"Unexpected error validating image '{path}': {ex.GetType().Name} - {ex.Message}");
            }
            return false;
        }
    }
}

