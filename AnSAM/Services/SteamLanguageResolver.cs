using System;
using System.Globalization;

namespace AnSAM.Services
{
    public static class SteamLanguageResolver
    {
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

        public static string GetSteamLanguage() => GetSteamLanguage(CultureInfo.CurrentUICulture);
    }
}
