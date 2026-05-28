using System.Text.RegularExpressions;

namespace CommonUtilities
{
    /// <summary>
    /// Provides validation methods for Steam Web API credentials and identifiers.
    /// Shared across applications that need to validate Steam-related input.
    /// </summary>
    public static partial class InputValidator
    {
        /// <summary>
        /// Regular expression pattern for validating Steam Web API keys (32 hexadecimal characters).
        /// Source-generated (<c>[GeneratedRegex]</c>) — AOT-safe; <c>RegexOptions.Compiled</c>
        /// is a no-op under Native AOT, the source generator already emits a compiled matcher.
        /// </summary>
        [GeneratedRegex("^[0-9A-Fa-f]{32}$")]
        private static partial Regex ApiKeyRegex();

        /// <summary>
        /// Regular expression pattern for validating Steam ID 64 format (starts with 7656119 followed by 10 digits).
        /// </summary>
        [GeneratedRegex(@"^7656119\d{10}$")]
        private static partial Regex SteamId64Regex();

        /// <summary>
        /// Validates whether the provided string is a valid Steam Web API key.
        /// </summary>
        /// <param name="apiKey">The API key string to validate.</param>
        /// <returns>True if the API key is exactly 32 hexadecimal characters; otherwise, false.</returns>
        public static bool IsValidApiKey(string? apiKey)
            => apiKey is not null && ApiKeyRegex().IsMatch(apiKey);

        /// <summary>
        /// Validates whether the provided string is a valid Steam ID 64.
        /// </summary>
        /// <param name="steamId64">The Steam ID 64 string to validate.</param>
        /// <returns>True if the Steam ID 64 starts with 7656119 and is followed by 10 digits; otherwise, false.</returns>
        public static bool IsValidSteamId64(string? steamId64)
            => steamId64 is not null && SteamId64Regex().IsMatch(steamId64);
    }
}
