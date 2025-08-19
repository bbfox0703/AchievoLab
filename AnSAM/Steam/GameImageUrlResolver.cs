using System;
using System.IO;
using AnSAM.Services;

namespace AnSAM.Steam
{
    /// <summary>
    /// Resolves a suitable cover image URL for a Steam application by querying
    /// the same keys used by the legacy SAM version.
    /// </summary>
    internal static class GameImageUrlResolver
    {
        internal static string GetGameImageUrl(SteamClient client, uint id, string language)
        {
            string? candidate;

            candidate = client.GetAppData(id, $"small_capsule/{language}");
            if (!string.IsNullOrEmpty(candidate))
            {
                if (TrySanitize(candidate, out var safe))
                {
                    return $"https://shared.cloudflare.steamstatic.com/store_item_assets/steam/apps/{id}/{safe}";
                }
                DebugLogger.LogDebug($"Invalid small_capsule path for app {id} language {language}: {candidate}");
            }
            else
            {
                DebugLogger.LogDebug($"Missing small_capsule for app {id} language {language}");
            }

            if (!string.Equals(language, "english", StringComparison.OrdinalIgnoreCase))
            {
                candidate = client.GetAppData(id, "small_capsule/english");
                if (!string.IsNullOrEmpty(candidate))
                {
                    if (TrySanitize(candidate, out var safe))
                    {
                        return $"https://shared.cloudflare.steamstatic.com/store_item_assets/steam/apps/{id}/{safe}";
                    }
                    DebugLogger.LogDebug($"Invalid small_capsule path for app {id} language english: {candidate}");
                }
                else
                {
                    DebugLogger.LogDebug($"Missing small_capsule for app {id} language english");
                }
            }

            candidate = client.GetAppData(id, "logo");
            if (!string.IsNullOrEmpty(candidate))
            {
                if (TrySanitize(candidate, out var safe))
                {
                    return $"https://cdn.steamstatic.com/steamcommunity/public/images/apps/{id}/{safe}.jpg";
                }
                DebugLogger.LogDebug($"Invalid logo path for app {id}: {candidate}");
            }
            else
            {
                DebugLogger.LogDebug($"Missing logo for app {id}");
            }

            candidate = client.GetAppData(id, "library_600x900");
            if (!string.IsNullOrEmpty(candidate))
            {
                if (TrySanitize(candidate, out var safe))
                {
                    return $"https://shared.cloudflare.steamstatic.com/steam/apps/{id}/{safe}";
                }
                DebugLogger.LogDebug($"Invalid library_600x900 path for app {id}: {candidate}");
            }
            else
            {
                DebugLogger.LogDebug($"Missing library_600x900 for app {id}");
            }

            candidate = client.GetAppData(id, "header_image");
            if (!string.IsNullOrEmpty(candidate))
            {
                if (TrySanitize(candidate, out var safe))
                {
                    return $"https://shared.cloudflare.steamstatic.com/steam/apps/{id}/{safe}";
                }
                DebugLogger.LogDebug($"Invalid header_image path for app {id}: {candidate}");
            }
            else
            {
                DebugLogger.LogDebug($"Missing header_image for app {id}");
            }

            // Default to generic header as last resort
            return $"https://shared.akamai.steamstatic.com/store_item_assets/steam/apps/{id}/header.jpg";
        }

        private static bool TrySanitize(string candidate, out string sanitized)
        {
            sanitized = Path.GetFileName(candidate);
            if (sanitized.IndexOf("..", StringComparison.Ordinal) >= 0 || sanitized.Contains(':'))
            {
                return false;
            }
            if (Uri.TryCreate(candidate, UriKind.Absolute, out var uri) && !string.IsNullOrEmpty(uri.Scheme))
            {
                return false;
            }
            return true;
        }
    }
}
