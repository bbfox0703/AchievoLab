namespace RunGame.Models
{
    /// <summary>
    /// Base class for Steam statistic definitions parsed from UserGameStatsSchema VDF files.
    /// Stats are game-defined numerical values that can be integer or floating-point.
    /// </summary>
    public abstract class StatDefinition
    {
        /// <summary>
        /// Gets or sets the unique identifier for this statistic (e.g., "kills_total", "distance_traveled").
        /// </summary>
        public string Id { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the localized display name for this statistic shown in the UI.
        /// </summary>
        public string DisplayName { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets a value indicating whether this statistic can only be incremented, never decreased.
        /// Used for cumulative stats like total kills or distance traveled.
        /// </summary>
        public bool IncrementOnly { get; set; }

        /// <summary>
        /// Gets or sets the permission flags for this statistic.
        /// Non-zero values indicate protected stats that cannot be modified by the user.
        /// </summary>
        public int Permission { get; set; }
    }

    /// <summary>
    /// Represents an integer-based statistic definition with validation constraints.
    /// </summary>
    public class IntegerStatDefinition : StatDefinition
    {
        /// <summary>
        /// Gets or sets the minimum allowed value for this statistic.
        /// </summary>
        public int MinValue { get; set; } = int.MinValue;

        /// <summary>
        /// Gets or sets the maximum allowed value for this statistic.
        /// </summary>
        public int MaxValue { get; set; } = int.MaxValue;

        /// <summary>
        /// Gets or sets the maximum change allowed per update (0 = no limit).
        /// Prevents large single updates that might indicate cheating.
        /// </summary>
        public int MaxChange { get; set; } = 0;

        /// <summary>
        /// Gets or sets a value indicating whether this statistic can only be modified by a trusted game server.
        /// </summary>
        public bool SetByTrustedGameServer { get; set; }

        /// <summary>
        /// Gets or sets the default value for this statistic when first initialized.
        /// </summary>
        public int DefaultValue { get; set; } = 0;
    }

    /// <summary>
    /// Represents a floating-point statistic definition with validation constraints.
    /// </summary>
    public class FloatStatDefinition : StatDefinition
    {
        /// <summary>
        /// Gets or sets the minimum allowed value for this statistic.
        /// </summary>
        public float MinValue { get; set; } = float.MinValue;

        /// <summary>
        /// Gets or sets the maximum allowed value for this statistic.
        /// </summary>
        public float MaxValue { get; set; } = float.MaxValue;

        /// <summary>
        /// Gets or sets the maximum change allowed per update (0 = no limit).
        /// Prevents large single updates that might indicate cheating.
        /// </summary>
        public float MaxChange { get; set; } = 0.0f;

        /// <summary>
        /// Gets or sets the default value for this statistic when first initialized.
        /// </summary>
        public float DefaultValue { get; set; } = 0.0f;
    }
}
