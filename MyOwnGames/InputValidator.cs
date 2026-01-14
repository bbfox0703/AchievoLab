using System.Text.RegularExpressions;

namespace MyOwnGames
{
    /// <summary>
    /// Provides validation methods for Steam Web API credentials and identifiers.
    /// </summary>
    public static class InputValidator
    {
        /// <summary>
        /// Regular expression pattern for validating Steam Web API keys (32 hexadecimal characters).
        /// </summary>
        private static readonly Regex ApiKeyRegex = new("^[0-9A-Fa-f]{32}$", RegexOptions.Compiled);

        /// <summary>
        /// Regular expression pattern for validating Steam ID 64 format (starts with 7656119 followed by 10 digits).
        /// </summary>
        private static readonly Regex SteamId64Regex = new("^7656119\\d{10}$", RegexOptions.Compiled);

        /// <summary>
        /// Validates whether the provided string is a valid Steam Web API key.
        /// </summary>
        /// <param name="apiKey">The API key string to validate.</param>
        /// <returns>True if the API key is exactly 32 hexadecimal characters; otherwise, false.</returns>
        public static bool IsValidApiKey(string? apiKey)
            => apiKey is not null && ApiKeyRegex.IsMatch(apiKey);

        /// <summary>
        /// Validates whether the provided string is a valid Steam ID 64.
        /// </summary>
        /// <param name="steamId64">The Steam ID 64 string to validate.</param>
        /// <returns>True if the Steam ID 64 starts with 7656119 and is followed by 10 digits; otherwise, false.</returns>
        public static bool IsValidSteamId64(string? steamId64)
            => steamId64 is not null && SteamId64Regex.IsMatch(steamId64);
    }
}
