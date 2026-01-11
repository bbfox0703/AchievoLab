using System.Collections.Generic;

namespace MyOwnGames.Services
{
    /// <summary>
    /// Represents game data with multi-language support
    /// </summary>
    public class MultiLanguageGameData
    {
        public int AppId { get; set; }
        public int PlaytimeForever { get; set; }
        public string NameEn { get; set; } = "";
        public string IconUrl { get; set; } = "";
        
        /// <summary>
        /// Dictionary of localized names by language code
        /// Key: language code (e.g., "tchinese", "japanese")
        /// Value: localized game name
        /// </summary>
        public Dictionary<string, string> LocalizedNames { get; set; } = new();
        
        /// <summary>
        /// Gets the localized name for a specific language, with fallback to English
        /// </summary>
        /// <param name="language">Language code</param>
        /// <returns>Localized name or English name if not available</returns>
        public string GetLocalizedName(string language)
        {
            if (!string.IsNullOrEmpty(language) && 
                LocalizedNames.TryGetValue(language, out var localizedName) && 
                !string.IsNullOrEmpty(localizedName))
            {
                return localizedName;
            }
            
            // Fallback to English
            return NameEn;
        }
        
        /// <summary>
        /// Gets all available languages for this game
        /// </summary>
        /// <returns>List of language codes</returns>
        public List<string> GetAvailableLanguages()
        {
            var languages = new List<string> { "english" }; // English is always available
            languages.AddRange(LocalizedNames.Keys);
            return languages;
        }
    }
}
