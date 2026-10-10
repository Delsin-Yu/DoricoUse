using System.Text.Json.Nodes;

namespace DoricoUse.Cli.Json;

/// <summary>
/// Adding to a <see cref="JsonArray"/>.
/// </summary>
/// <remarks>
/// Every call casts to <see cref="JsonNode"/> on purpose. JsonArray.Add&lt;T&gt; would otherwise be chosen for a
/// string or a JsonValue and that overload needs runtime code generation, which a Native AOT build cannot do.
/// </remarks>
internal static class JsonArr
{
    public static void AddText(JsonArray array, string? value) => array.Add((JsonNode?)JsonValue.Create(value));

    public static void AddNumber(JsonArray array, long value) => array.Add((JsonNode?)JsonValue.Create(value));

    public static void AddNode(JsonArray array, JsonNode? node) => array.Add((JsonNode?)node);
}
