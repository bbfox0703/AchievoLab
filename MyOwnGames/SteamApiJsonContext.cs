using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace MyOwnGames;

[JsonSerializable(typeof(OwnedGamesResponse))]
[JsonSerializable(typeof(Dictionary<string, AppDetailsResponse>))]
internal partial class SteamApiJsonContext : JsonSerializerContext
{
}

