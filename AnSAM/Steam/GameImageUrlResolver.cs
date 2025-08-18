using System;
using System.IO;
using System.Diagnostics;

namespace AnSAM.Steam
{
    internal static class GameImageUrlResolver
    {
        internal static string? GetGameImageUrl(Func<uint, string, string?> getAppData, uint id, string language)
        {
            string? candidate;

            candidate = getAppData(id, $"small_capsule/{language}");
            if (!string.IsNullOrEmpty(candidate) && TrySanitize(candidate, out var safeCandidate))
            {
                return $"https://shared.cloudflare.steamstatic.com/store_item_assets/steam/apps/{id}/{safeCandidate}";
            }
            else
            {
                Debug.WriteLine($"Missing small_capsule for app {id} language {language}");
            }

            if (!string.Equals(language, "english", StringComparison.OrdinalIgnoreCase))
            {
                candidate = getAppData(id, "small_capsule/english");
                if (!string.IsNullOrEmpty(candidate) && TrySanitize(candidate, out safeCandidate))
                {
                    return $"https://shared.cloudflare.steamstatic.com/store_item_assets/steam/apps/{id}/{safeCandidate}";
                }
                else
                {
                    Debug.WriteLine($"Missing small_capsule for app {id} language english");
                }
            }

            candidate = getAppData(id, "logo");
            if (!string.IsNullOrEmpty(candidate) && TrySanitize(candidate, out var safe))
            {
                return $"https://cdn.steamstatic.com/steamcommunity/public/images/apps/{id}/{safe}.jpg";
            }
            else
            {
                Debug.WriteLine($"Missing logo for app {id}");
            }

            candidate = getAppData(id, "library_600x900");
            if (!string.IsNullOrEmpty(candidate) && TrySanitize(candidate, out safe))
            {
                return $"https://shared.cloudflare.steamstatic.com/steam/apps/{id}/{safe}";
            }
            else
            {
                Debug.WriteLine($"Missing library_600x900 for app {id}");
            }

            candidate = getAppData(id, "header_image");
            if (!string.IsNullOrEmpty(candidate) && TrySanitize(candidate, out safe))
            {
                return $"https://shared.cloudflare.steamstatic.com/steam/apps/{id}/{safe}";
            }
            else
            {
                Debug.WriteLine($"Missing header_image for app {id}");
            }

            return null;
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
