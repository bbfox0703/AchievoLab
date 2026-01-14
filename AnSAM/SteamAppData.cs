using System;

namespace AnSAM
{
    /// <summary>
    /// Represents metadata for a Steam application, including launch information.
    /// </summary>
    /// <param name="AppId">The unique Steam application identifier.</param>
    /// <param name="Title">The display name of the application.</param>
    /// <param name="CoverUrl">Optional URL to the application's cover image.</param>
    /// <param name="ExePath">Optional path to the executable for direct launch.</param>
    /// <param name="Arguments">Optional command-line arguments to pass when launching.</param>
    /// <param name="UriScheme">Optional custom URI scheme for launching (e.g., steam://rungameid/).</param>
    public record SteamAppData(int AppId,
                               string Title,
                               string? CoverUrl = null,
                               string? ExePath = null,
                               string? Arguments = null,
                               string? UriScheme = null);
}
