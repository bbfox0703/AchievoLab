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
                _ => culture.TwoLetterISOLanguageName switch
                {
                    "es" => "spanish",
                    "fr" => "french",
                    "de" => "german",
                    "it" => "italian",
                    "pt" => "portuguese",
                    "ru" => "russian",
                    "ja" => "japanese",
                    "ko" => "korean",
                    _ => "english"
                }
            };
        }
    }
}
