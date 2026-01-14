namespace AnSAM.Steam
{
    /// <summary>
    /// Defines the contract for interacting with the Steam client API.
    /// Provides methods for querying app ownership and metadata.
    /// </summary>
    public interface ISteamClient
    {
        /// <summary>
        /// Gets a value indicating whether the Steam API was successfully initialized.
        /// </summary>
        bool Initialized { get; }

        /// <summary>
        /// Determines whether the current user owns or is subscribed to the specified app.
        /// </summary>
        /// <param name="appId">The Steam app ID to check.</param>
        /// <returns>True if the user owns the app; otherwise, false.</returns>
        bool IsSubscribedApp(uint appId);

        /// <summary>
        /// Retrieves metadata for a specific app from the Steam client.
        /// </summary>
        /// <param name="appId">The Steam app ID to query.</param>
        /// <param name="key">The metadata key to retrieve (e.g., "name", "installdir").</param>
        /// <returns>The metadata value if found; otherwise, null.</returns>
        string? GetAppData(uint appId, string key);
    }
}
