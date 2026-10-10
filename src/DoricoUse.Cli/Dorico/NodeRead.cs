using System.Text.Json.Nodes;

namespace DoricoUse.Cli.Dorico;

/// <summary>
/// Null-safe readers for the loosely typed JSON that Dorico sends. Every read returns null instead of
/// throwing, because the protocol is undocumented and property names or value types can be missing.
/// </summary>
internal static class NodeRead
{
    public static string? Str(JsonNode? node, string key)
        => node is JsonObject obj && obj.TryGetPropertyValue(key, out var value) ? Text(value) : null;

    /// <summary>A scalar node as text (numbers and booleans are rendered unquoted).</summary>
    public static string? Text(JsonNode? node)
    {
        if (node is not JsonValue value)
        {
            return null;
        }

        return value.TryGetValue<string>(out var text) ? text : value.ToJsonString();
    }

    public static long? Long(JsonNode? node, string key)
    {
        if (node is not JsonObject obj || !obj.TryGetPropertyValue(key, out var value) || value is not JsonValue json)
        {
            return null;
        }

        if (json.TryGetValue<long>(out var wide))
        {
            return wide;
        }

        if (json.TryGetValue<int>(out var number))
        {
            return number;
        }

        return json.TryGetValue<string>(out var text) && long.TryParse(text, out var parsed) ? parsed : null;
    }

    public static int? Int(JsonNode? node, string key)
    {
        var value = Long(node, key);

        return value is null ? null : (int)Math.Clamp(value.Value, int.MinValue, int.MaxValue);
    }

    public static bool? Bool(JsonNode? node, string key)
    {
        if (node is not JsonObject obj || !obj.TryGetPropertyValue(key, out var value) || value is not JsonValue json)
        {
            return null;
        }

        if (json.TryGetValue<bool>(out var flag))
        {
            return flag;
        }

        return json.TryGetValue<string>(out var text) && bool.TryParse(text, out var parsed) ? parsed : null;
    }

    public static JsonObject? Obj(JsonNode? node, string key)
        => node is JsonObject obj && obj.TryGetPropertyValue(key, out var value) ? value as JsonObject : null;

    public static JsonArray? Arr(JsonNode? node, string key)
        => node is JsonObject obj && obj.TryGetPropertyValue(key, out var value) ? value as JsonArray : null;

    public static bool IsError(JsonObject? response)
        => NodeRead.Str(response, "code") == DoricoProtocol.CodeError;
}
