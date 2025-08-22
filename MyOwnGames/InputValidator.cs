using System.Text.RegularExpressions;

namespace MyOwnGames
{
    public static class InputValidator
    {
        private static readonly Regex ApiKeyRegex = new("^[0-9A-Fa-f]{32}$", RegexOptions.Compiled);
        private static readonly Regex SteamId64Regex = new("^7656119\\d{10}$", RegexOptions.Compiled);

        public static bool IsValidApiKey(string? apiKey)
            => apiKey is not null && ApiKeyRegex.IsMatch(apiKey);

        public static bool IsValidSteamId64(string? steamId64)
            => steamId64 is not null && SteamId64Regex.IsMatch(steamId64);
    }
}
