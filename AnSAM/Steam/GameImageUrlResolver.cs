using System;
using System.IO;
using System.Diagnostics;
using System.Collections.Generic;

namespace AnSAM.Steam
{
    internal static class GameImageUrlResolver
    {
        internal static IReadOnlyList<string> GetGameImageUrls(Func<uint, string, string?> getAppData, uint id, string language)
        {
            var urls = new List<string>();

            void TryAdd(string? raw, string format)
            {
                if (!string.IsNullOrEmpty(raw) && TrySanitize(raw, out var safe))
                {
                    urls.Add(string.Format(format, id, safe));
                }
            }

            var candidate = getAppData(id, $"small_capsule/{language}");
            if (!string.IsNullOrEmpty(candidate))
            {
                TryAdd(candidate, "https://shared.cloudflare.steamstatic.com/store_item_assets/steam/apps/{0}/{1}");
            }
            else
            {
                Debug.WriteLine($"Missing small_capsule for app {id} language {language}");
            }

            if (!string.Equals(language, "english", StringComparison.OrdinalIgnoreCase))
            {
                candidate = getAppData(id, "small_capsule/english");
                if (!string.IsNullOrEmpty(candidate))
                {
                    TryAdd(candidate, "https://shared.cloudflare.steamstatic.com/store_item_assets/steam/apps/{0}/{1}");
                }
                else
                {
                    Debug.WriteLine($"Missing small_capsule for app {id} language english");
                }
            }

            candidate = getAppData(id, "logo");
            if (!string.IsNullOrEmpty(candidate))
            {
                TryAdd(candidate, "https://cdn.steamstatic.com/steamcommunity/public/images/apps/{0}/{1}.jpg");
            }
            else
            {
                Debug.WriteLine($"Missing logo for app {id}");
            }

            candidate = getAppData(id, "library_600x900");
            if (!string.IsNullOrEmpty(candidate))
            {
                TryAdd(candidate, "https://shared.cloudflare.steamstatic.com/steam/apps/{0}/{1}");
            }
            else
            {
                Debug.WriteLine($"Missing library_600x900 for app {id}");
            }

            candidate = getAppData(id, "header_image");
            if (!string.IsNullOrEmpty(candidate))
            {
                TryAdd(candidate, "https://shared.cloudflare.steamstatic.com/steam/apps/{0}/{1}");
            }
            else
            {
                Debug.WriteLine($"Missing header_image for app {id}");
            }

            // Default to generic header as last resort
            urls.Add($"https://shared.akamai.steamstatic.com/store_item_assets/steam/apps/{id}/header.jpg");

            return urls;
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
