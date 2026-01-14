using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace MyOwnGames;

/// <summary>
/// JSON serialization context for Steam Web API responses.
/// Provides source-generated JSON serialization for improved performance and AOT compatibility.
/// Supports serialization of OwnedGamesResponse and app details dictionaries.
/// </summary>
[JsonSerializable(typeof(OwnedGamesResponse))]
[JsonSerializable(typeof(Dictionary<string, AppDetailsResponse>))]
internal partial class SteamApiJsonContext : JsonSerializerContext
{
}

