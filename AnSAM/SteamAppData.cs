using System;

namespace AnSAM
{
    public record SteamAppData(int AppId,
                               string Title,
                               string? CoverUrl = null,
                               string? ExePath = null,
                               string? Arguments = null,
                               string? UriScheme = null);
}
