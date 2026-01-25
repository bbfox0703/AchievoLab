namespace RunGame.Models
{
    /// <summary>
    /// Represents the definition of a Steam achievement, including metadata and icon paths.
    /// This is parsed from the UserGameStatsSchema VDF file.
    /// </summary>
    public class AchievementDefinition
    {
        /// <summary>
        /// Gets or sets the unique identifier for this achievement.
        /// </summary>
        public string Id { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the localized display name of the achievement.
        /// </summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the English display name of the achievement for search purposes.
        /// </summary>
        public string EnglishName { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the localized description of the achievement.
        /// </summary>
        public string Description { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the English description of the achievement for search purposes.
        /// </summary>
        public string EnglishDescription { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the file path to the unlocked/achieved icon image.
        /// </summary>
        public string IconNormal { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the file path to the locked/unachieved icon image.
        /// </summary>
        public string IconLocked { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets a value indicating whether this achievement is hidden until unlocked.
        /// </summary>
        public bool IsHidden { get; set; }

        /// <summary>
        /// Gets or sets the permission level for this achievement.
        /// </summary>
        public int Permission { get; set; }

        /// <summary>
        /// Returns a string representation of this achievement definition.
        /// </summary>
        /// <returns>A string containing the name (or ID) and permission level.</returns>
        public override string ToString()
        {
            return $"{Name ?? Id ?? base.ToString()}: {Permission}";
        }
    }
}
