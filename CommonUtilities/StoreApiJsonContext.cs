using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace CommonUtilities;

/// <summary>
/// JSON serialization context for Steam Store API responses.
/// Provides source-generated JSON serialization for improved performance and AOT compatibility.
/// </summary>
[JsonSerializable(typeof(Dictionary<string, StoreApiResponse>))]
internal partial class StoreApiJsonContext : JsonSerializerContext
{
}

