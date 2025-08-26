using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace CommonUtilities;

[JsonSerializable(typeof(Dictionary<string, StoreApiResponse>))]
internal partial class StoreApiJsonContext : JsonSerializerContext
{
}

