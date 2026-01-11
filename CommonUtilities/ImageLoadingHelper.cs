using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;

namespace CommonUtilities
{
    /// <summary>
    /// Helper class providing common image loading logic with English fallback support.
    /// </summary>
    public static class ImageLoadingHelper
    {
        /// <summary>
        /// Loads an image with automatic English fallback for non-English languages.
        /// For non-English languages, displays English version first (if cached) while downloading target language.
        /// </summary>
        /// <param name="imageService">The image service to use.</param>
        /// <param name="appId">The game's AppID.</param>
        /// <param name="targetLanguage">The target language to load.</param>
        /// <param name="dispatcher">Dispatcher queue for UI updates.</param>
        /// <param name="onEnglishFallbackLoaded">Callback when English fallback is loaded (optional).</param>
        /// <param name="currentLanguageGetter">Function to get current global language (for validation).</param>
        /// <returns>The final image path (target language or fallback).</returns>
        public static async Task<(string? imagePath, string loadedLanguage)> LoadWithEnglishFallbackAsync(
            SharedImageService imageService,
            int appId,
            string targetLanguage,
            DispatcherQueue dispatcher,
            Action<string>? onEnglishFallbackLoaded = null,
            Func<string>? currentLanguageGetter = null)
        {
            bool isNonEnglish = !string.Equals(targetLanguage, "english", StringComparison.OrdinalIgnoreCase);
            bool englishCached = isNonEnglish && imageService.IsImageCached(appId, "english");

            // For non-English languages, load English fallback first if cached
            if (englishCached)
            {
                var englishPath = await imageService.GetGameImageAsync(appId, "english").ConfigureAwait(false);
                if (!string.IsNullOrEmpty(englishPath) && File.Exists(englishPath))
                {
                    // Verify language hasn't changed (if validator provided)
                    if (currentLanguageGetter != null)
                    {
                        var globalLanguage = currentLanguageGetter();
                        if (!string.Equals(targetLanguage, globalLanguage, StringComparison.OrdinalIgnoreCase))
                        {
#if DEBUG
                            AppLogger.LogDebug($"Skipping English fallback for {appId} - language changed to {globalLanguage}");
#endif
                            // Language changed during load, abort
                            return (null, "");
                        }
                    }

                    // Notify caller about English fallback (for immediate UI update)
                    try
                    {
                        onEnglishFallbackLoaded?.Invoke(englishPath);
                    }
                    catch (Exception ex)
                    {
#if DEBUG
                        AppLogger.LogDebug($"Error in English fallback callback for {appId}: {ex.GetType().Name}: {ex.Message}");
                        AppLogger.LogDebug($"Stack trace: {ex.StackTrace}");
#endif
                        // Don't rethrow - callback errors shouldn't stop image loading
                    }

#if DEBUG
                    AppLogger.LogDebug($"Loaded English fallback for {appId}, will attempt {targetLanguage} next");
#endif
                }
            }

            // Now load the target language image
            var targetPath = await imageService.GetGameImageAsync(appId, targetLanguage).ConfigureAwait(false);

            if (!string.IsNullOrEmpty(targetPath) && File.Exists(targetPath))
            {
                // Determine actual language from path
                var loadedLanguage = DetermineLanguageFromPath(targetPath, targetLanguage);
                return (targetPath, loadedLanguage);
            }

            // If target language failed but we had English fallback, return that
            if (englishCached)
            {
                var englishPath = await imageService.GetGameImageAsync(appId, "english").ConfigureAwait(false);
                if (!string.IsNullOrEmpty(englishPath) && File.Exists(englishPath))
                {
                    return (englishPath, "english");
                }
            }

            return (null, "");
        }

        /// <summary>
        /// Determines the language of an image from its file path.
        /// </summary>
        /// <param name="imagePath">The full path to the image file.</param>
        /// <param name="requestedLanguage">The originally requested language.</param>
        /// <returns>The detected language (e.g., "english", "japanese", etc.).</returns>
        public static string DetermineLanguageFromPath(string imagePath, string requestedLanguage)
        {
            if (string.IsNullOrEmpty(imagePath))
                return requestedLanguage;

            // Check if path contains language folder
            // Pattern: .../ImageCache/{language}/...
            var normalizedPath = imagePath.Replace('/', '\\');
            var parts = normalizedPath.Split('\\');

            for (int i = 0; i < parts.Length - 1; i++)
            {
                if (string.Equals(parts[i], "ImageCache", StringComparison.OrdinalIgnoreCase))
                {
                    // Next part should be the language
                    if (i + 1 < parts.Length)
                    {
                        return parts[i + 1].ToLowerInvariant();
                    }
                }
            }

            // Fallback: assume requested language
            return requestedLanguage;
        }

        /// <summary>
        /// Checks if an image path corresponds to the specified language.
        /// </summary>
        /// <param name="imagePath">The image path to check.</param>
        /// <param name="language">The language to check for.</param>
        /// <returns>True if the path contains the language folder.</returns>
        public static bool IsPathFromLanguage(string? imagePath, string language)
        {
            if (string.IsNullOrEmpty(imagePath))
                return false;

            var normalizedPath = imagePath.Replace('/', '\\');
            return normalizedPath.Contains($"\\{language}\\", StringComparison.OrdinalIgnoreCase) ||
                   normalizedPath.Contains($"/{language}/", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Gets the fallback image path (no_icon.png).
        /// </summary>
        /// <returns>The URI string for the no_icon asset.</returns>
        public static string GetNoIconPath()
        {
            return "ms-appx:///Assets/no_icon.png";
        }

        /// <summary>
        /// Checks if an icon URI is the no_icon fallback.
        /// </summary>
        /// <param name="iconUri">The icon URI to check.</param>
        /// <returns>True if it's the no_icon fallback.</returns>
        public static bool IsNoIcon(string? iconUri)
        {
            return string.IsNullOrEmpty(iconUri) ||
                   iconUri.Contains("no_icon.png", StringComparison.OrdinalIgnoreCase) ||
                   iconUri.Contains("ms-appx://", StringComparison.OrdinalIgnoreCase);
        }
    }
}
