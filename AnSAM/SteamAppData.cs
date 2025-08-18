using System;
using System.Collections.Generic;

namespace AnSAM
{
    public record SteamAppData(int AppId,
                               string Title,
                               IReadOnlyList<string>? CoverUrls = null,
                               string? ExePath = null,
                               string? Arguments = null,
                               string? UriScheme = null);
}
