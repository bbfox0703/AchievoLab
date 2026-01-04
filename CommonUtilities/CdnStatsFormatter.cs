using System;
using System.Collections.Generic;
using System.Linq;

namespace CommonUtilities
{
    /// <summary>
    /// Formats CDN statistics for UI display.
    /// Shared between AnSAM and MyOwnGames for consistent CDN status reporting.
    /// </summary>
    public static class CdnStatsFormatter
    {
        private static readonly Dictionary<string, string> CdnNames = new()
        {
            ["shared.cloudflare.steamstatic.com"] = "CF",
            ["cdn.steamstatic.com"] = "Steam",
            ["shared.akamai.steamstatic.com"] = "Akamai"
        };

        /// <summary>
        /// Formats CDN statistics into a human-readable string for status bar display.
        /// </summary>
        /// <param name="stats">CDN statistics from SharedImageService.GetCdnStats()</param>
        /// <returns>Formatted string showing active connections, blocked indicators, and success rate</returns>
        /// <example>
        /// Returns strings like:
        /// - "CDN: CF:3 Steam:2 (95%)" - Normal operation
        /// - "CDN: CF:5⚠ Steam:1 (60%)" - Cloudflare blocked/degraded
        /// - "CDN OK (100%)" - All good but no active connections
        /// - "" - No stats available
        /// </example>
        public static string FormatCdnStats(Dictionary<string, (int Active, bool IsBlocked, double SuccessRate)> stats)
        {
            if (stats == null || stats.Count == 0)
            {
                return string.Empty;
            }

            var statParts = new List<string>();
            foreach (var kvp in stats.OrderByDescending(x => x.Value.Active))
            {
                var domain = kvp.Key;
                var (active, isBlocked, successRate) = kvp.Value;

                var name = GetShortName(domain);

                if (active > 0 || isBlocked)
                {
                    var blockedIndicator = isBlocked ? "⚠" : "";
                    statParts.Add($"{name}:{active}{blockedIndicator}");
                }
            }

            // Calculate overall success rate
            var totalSuccess = stats.Values.Sum(s => s.SuccessRate * 100);
            var avgSuccessRate = stats.Count > 0 ? totalSuccess / stats.Count : 0;

            if (statParts.Count > 0)
            {
                return $"CDN: {string.Join(" ", statParts)} ({avgSuccessRate:0}%)";
            }
            else if (stats.Any(s => s.Value.SuccessRate > 0))
            {
                return $"CDN OK ({avgSuccessRate:0}%)";
            }
            else
            {
                return string.Empty;
            }
        }

        /// <summary>
        /// Gets a short, friendly name for a CDN domain.
        /// </summary>
        /// <param name="domain">Full CDN domain name</param>
        /// <returns>Short name (e.g., "CF" for Cloudflare) or first part of domain</returns>
        public static string GetShortName(string domain)
        {
            if (CdnNames.TryGetValue(domain, out var shortName))
            {
                return shortName;
            }

            // Fallback: use first part of domain (e.g., "shared" from "shared.example.com")
            var parts = domain.Split('.');
            return parts.Length > 0 ? parts[0] : domain;
        }

        /// <summary>
        /// Adds or updates a CDN name mapping.
        /// Useful for custom CDN configurations.
        /// </summary>
        /// <param name="domain">Full CDN domain name</param>
        /// <param name="shortName">Short display name</param>
        public static void AddCdnName(string domain, string shortName)
        {
            CdnNames[domain] = shortName;
        }
    }
}
