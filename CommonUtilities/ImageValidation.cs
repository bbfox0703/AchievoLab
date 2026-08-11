using System;
using System.IO;

namespace CommonUtilities
{
    /// <summary>
    /// Provides utilities for validating image file formats by inspecting file headers (magic numbers).
    /// Supports PNG, JPEG, GIF, BMP, ICO, AVIF, and WEBP formats.
    /// </summary>
    public static class ImageValidation
    {
        /// <summary>
        /// Validates whether the file at the specified path is a supported image format.
        /// Performs validation by reading the file header and matching against known magic numbers.
        /// </summary>
        /// <param name="path">The full path to the file to validate.</param>
        /// <returns>
        /// True if the file exists, is non-empty, and has a recognized image format header;
        /// otherwise, false.
        /// </returns>
        /// <remarks>
        /// Collapses <see cref="ImageValidationOutcome.Invalid"/> and
        /// <see cref="ImageValidationOutcome.Unreadable"/> into false. Callers that act
        /// destructively on the result — deleting the file, recording a download failure — must
        /// use <see cref="Validate"/> instead and tell those two apart.
        /// </remarks>
        public static bool IsValidImage(string path) => Validate(path) == ImageValidationOutcome.Valid;

        /// <summary>
        /// Inspects the file header and reports whether it is a supported image, is definitely not
        /// one, or could not be read at all.
        /// </summary>
        /// <param name="path">The full path to the file to validate.</param>
        public static ImageValidationOutcome Validate(string path)
        {
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists || info.Length == 0)
                {
                    return ImageValidationOutcome.Invalid;
                }

                Span<byte> header = stackalloc byte[12];
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                int read = fs.Read(header);
                if (read >= 4)
                {
                    if (header[0] == 0x89 && header[1] == 0x50 && header[2] == 0x4E && header[3] == 0x47)
                        return ImageValidationOutcome.Valid; // PNG
                    if (header[0] == 0xFF && header[1] == 0xD8)
                        return ImageValidationOutcome.Valid; // JPEG
                    if (header[0] == 0x47 && header[1] == 0x49 && header[2] == 0x46)
                        return ImageValidationOutcome.Valid; // GIF
                    if (header[0] == 0x42 && header[1] == 0x4D)
                        return ImageValidationOutcome.Valid; // BMP
                    if (header[0] == 0x00 && header[1] == 0x00 && header[2] == 0x01 && header[3] == 0x00)
                        return ImageValidationOutcome.Valid; // ICO
                    if (read >= 12 && header[4] == 0x66 && header[5] == 0x74 && header[6] == 0x79 && header[7] == 0x70 &&
                        header[8] == 0x61 && header[9] == 0x76 && header[10] == 0x69 && header[11] == 0x66)
                        return ImageValidationOutcome.Valid; // AVIF
                    if (read >= 12 && header[0] == 0x52 && header[1] == 0x49 && header[2] == 0x46 && header[3] == 0x46 &&
                        header[8] == 0x57 && header[9] == 0x45 && header[10] == 0x42 && header[11] == 0x50)
                        return ImageValidationOutcome.Valid; // WEBP
                }

                // Bytes were read and match nothing we support.
                return ImageValidationOutcome.Invalid;
            }
            catch (UnauthorizedAccessException ex)
            {
                AppLogger.LogDebug($"Access denied validating image '{path}': {ex.Message}");
            }
            catch (IOException ex)
            {
                AppLogger.LogDebug($"IO error validating image '{path}': {ex.Message}");
            }
            catch (Exception ex)
            {
                AppLogger.LogDebug($"Unexpected error validating image '{path}': {ex.GetType().Name} - {ex.Message}");
            }

            // Could not read the file. Nothing was learned about its contents, so callers must not
            // treat this as "corrupt" and delete it.
            return ImageValidationOutcome.Unreadable;
        }
    }

    /// <summary>
    /// Result of inspecting a file's header with <see cref="ImageValidation.Validate"/>.
    /// </summary>
    public enum ImageValidationOutcome
    {
        /// <summary>The header matched a supported image format.</summary>
        Valid,

        /// <summary>The file was read and is not a supported image (missing, empty, or wrong header).</summary>
        Invalid,

        /// <summary>
        /// The file could not be read this time — locked by another process, access denied, or an
        /// I/O error. This says nothing about the contents.
        /// </summary>
        Unreadable,
    }
}

