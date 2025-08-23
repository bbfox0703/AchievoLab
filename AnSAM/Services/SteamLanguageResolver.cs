using System;
using System.Collections.Generic;
using System.Globalization;

namespace AnSAM.Services
{
    public static class SteamLanguageResolver
    {
        private static string? _overrideLanguage;

        public static readonly IReadOnlyList<string> SupportedLanguages = new[]
        {
            "arabic", "brazilian", "bulgarian", "czech", "danish", "dutch",
            "english", "finnish", "french", "german", "greek", "hungarian",
            "indonesian", "italian", "japanese", "koreana", "latam", "norwegian",
            "polish", "portuguese", "romanian", "russian", "schinese", "spanish",
            "swedish", "thai", "turkish", "ukrainian", "vietnamese", "tchinese"
        };

        public static string? OverrideLanguage
        {
            get => _overrideLanguage;
            set => _overrideLanguage = string.IsNullOrWhiteSpace(value) ? null : value;
        }

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
