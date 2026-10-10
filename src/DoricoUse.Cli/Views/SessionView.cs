using System.Text.Json.Nodes;
using DoricoUse.Cli.Dorico;

namespace DoricoUse.Cli.Views;

/// <summary>
/// Agent-first projections of Dorico payloads: stable field names, protocol trivia removed, values passed through
/// unchanged so that enum spellings (kWriteMode, kCrotchet, ...) stay recognisable.
/// </summary>
internal static class SessionView
{
    public static JsonObject App(JsonObject? version) => new()
    {
        ["variant"] = Clone(version, "variant"),
        ["number"] = Clone(version, "number"),
    };

    /// <summary>
    /// The parts of the status an agent can act on. Dorico sends a long list of display and panel flags that say
    /// nothing about the music; the rest lives behind --raw.
    /// </summary>
    public static JsonObject Status(JsonObject? status)
    {
        if (status is null)
        {
            return [];
        }

        return new JsonObject
        {
            ["hasScore"] = Clone(status, "hasScore"),
            ["scoreId"] = Number(status, "activeOpenScoreID"),
            ["editable"] = Clone(status, "notReadOnly"),
            ["canUndo"] = Clone(status, "canUndo"),
            ["hasSelection"] = Clone(status, "hasSelection"),
            ["inPlayback"] = Clone(status, "inPlayback"),
            ["isRecording"] = Clone(status, "isRecording"),
            ["mode"] = new JsonObject
            {
                ["window"] = Clone(status, "windowMode"),
                ["tool"] = Clone(status, "toolType"),
                ["playModeTool"] = Clone(status, "activePlayModeTool"),
            },
            ["noteInput"] = new JsonObject
            {
                ["active"] = Clone(status, "noteInputActive"),
                ["mode"] = Clone(status, "noteInputMode"),
                ["stepTime"] = Clone(status, "inStepTimeInput"),
                ["pitchMode"] = Clone(status, "noteInputPitchModeValue"),
                ["insertActive"] = Clone(status, "insertActive"),
            },
            ["selection"] = new JsonObject
            {
                ["duration"] = Clone(status, "duration"),
                ["rhythmDots"] = Number(status, "rhythmDots"),
                ["accidental"] = Clone(status, "accidental"),
                ["restMode"] = Clone(status, "restMode"),
            },
            ["rhythmGrid"] = Clone(status, "rhythmicGridResolutionValue"),
            ["zoomPercent"] = Number(status, "zoomPercent"),
            ["condensingEnabled"] = Clone(status, "condensingEnabled"),
        };
    }

    /// <summary>Counts unsolicited messages by kind.</summary>
    public static JsonObject EventCounts(IReadOnlyList<JsonObject> events)
    {
        var counts = new JsonObject();

        foreach (var evt in events)
        {
            var kind = NodeRead.Str(evt, "kind") ?? DoricoProtocol.Unknown;
            counts[kind] = (NodeRead.Int(counts, kind) ?? 0) + 1;
        }

        return counts;
    }

    private static JsonNode? Clone(JsonObject? source, string key)
        => source is not null && source.TryGetPropertyValue(key, out var value) ? value?.DeepClone() : null;

    /// <summary>Dorico sometimes sends numbers as strings ("144"); an agent should not have to care.</summary>
    private static JsonNode? Number(JsonObject source, string key)
    {
        if (!source.TryGetPropertyValue(key, out var value) || value is null)
        {
            return null;
        }

        if (value is JsonValue json && json.TryGetValue<string>(out var text))
        {
            return long.TryParse(text, out var parsed) ? JsonValue.Create(parsed) : value.DeepClone();
        }

        return value.DeepClone();
    }
}
