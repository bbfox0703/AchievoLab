using System;
using System.Collections.Generic;
using System.Globalization;

namespace CommonUtilities
{
    /// <summary>
    /// Resolves culture-specific language codes to Steam's language identifiers.
    /// Shared across AnSAM, RunGame, and MyOwnGames for consistent language handling.
    /// </summary>
    public static class SteamLanguageResolver
    {
        private static string? _overrideLanguage;

        /// <summary>
        /// List of all languages supported by Steam.
        /// </summary>
        public static readonly IReadOnlyList<string> SupportedLanguages = new[]
        {
            "arabic", "brazilian", "bulgarian", "czech", "danish", "dutch",
            "english", "finnish", "french", "german", "greek", "hungarian",
            "indonesian", "italian", "japanese", "koreana", "latam", "norwegian",
            "polish", "portuguese", "romanian", "russian", "schinese", "spanish",
            "swedish", "thai", "turkish", "ukrainian", "vietnamese", "tchinese"
        };

        /// <summary>
        /// Gets or sets a language override. When set, GetSteamLanguage() will return this value
        /// instead of detecting from the system culture.
        /// </summary>
        public static string? OverrideLanguage
        {
            get => _overrideLanguage;
            set => _overrideLanguage = string.IsNullOrWhiteSpace(value) ? null : value;
        }

        /// <summary>
        /// Converts a .NET CultureInfo to a Steam language code.
        /// </summary>
        /// <param name="culture">The culture to convert</param>
        /// <returns>Steam language code (e.g., "english", "tchinese", "japanese")</returns>
        public static string GetSteamLanguage(CultureInfo culture)
        {
            return culture.Name switch
            {
                "zh-TW" or "zh-HK" => "tchinese",
                "zh-CN" => "schinese",
                var name when name.StartsWith("zh", StringComparison.OrdinalIgnoreCase) => "tchinese",
                "pt-BR" => "brazilian",
                var name when name.StartsWith("es", StringComparison.OrdinalIgnoreCase) && name != "es" && name != "es-ES" => "latam",
                _ => culture.TwoLetterISOLanguageName switch
                {
                    "ar" => "arabic",
                    "bg" => "bulgarian",
                    "cs" => "czech",
                    "da" => "danish",
                    "nl" => "dutch",
                    "fi" => "finnish",
                    "fr" => "french",
                    "de" => "german",
                    "el" => "greek",
                    "hu" => "hungarian",
                    "id" => "indonesian",
                    "it" => "italian",
                    "ja" => "japanese",
                    "ko" => "koreana",
                    "nb" or "nn" or "no" => "norwegian",
                    "pl" => "polish",
                    "pt" => "portuguese",
                    "ro" => "romanian",
                    "ru" => "russian",
                    "es" => "spanish",
                    "sv" => "swedish",
                    "th" => "thai",
                    "tr" => "turkish",
                    "uk" => "ukrainian",
                    "vi" => "vietnamese",
                    _ => "english"
                }
            };
        }

        /// <summary>
        /// Gets the Steam language code for the current UI culture.
        /// If OverrideLanguage is set, returns that instead.
        /// </summary>
        /// <returns>Steam language code</returns>
        public static string GetSteamLanguage()
        {
            if (!string.IsNullOrEmpty(OverrideLanguage))
            {
                return OverrideLanguage;
            }

            return GetSteamLanguage(CultureInfo.CurrentUICulture);
        }
    }
}
