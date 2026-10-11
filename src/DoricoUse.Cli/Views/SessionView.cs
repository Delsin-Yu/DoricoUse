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

        var payload = new JsonObject
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

        var view = View(status);

        if (view is not null)
        {
            payload["view"] = view;
        }

        return payload;
    }

    /// <summary>
    /// The two ids a caller needs to make <b>a specific layout</b> the current one, and the command to paste.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Measured:</b> <c>Window.SwitchToLayout</c> requires <c>OpenScoreID</c>, <c>ScoreLayoutViewContainerID</c>
    /// and <c>LayoutID</c>, and the first two are exactly what Dorico's own status reports as
    /// <c>activeOpenScoreID</c> and <c>activeViewContainerID</c> — nothing else in any payload names them, which is
    /// why <c>Edit.ToggleLayout*</c> looked unusable (it acts on the current layout, and the current layout could
    /// not be chosen). Switching to layout 1 flipped <c>condensingEnabled</c> from true to false and switching back
    /// flipped it back, so the route works.
    /// </para>
    /// <para>
    /// <b>Measured:</b> no payload <i>reads</i> which layout is current. This block is therefore the ids plus a
    /// ready-to-paste line, and not a claim about the current layout; the closest read there is,
    /// <c>condensingEnabled</c>, only identifies a layout whose own <c>enableCondensing</c> differs from its
    /// neighbours'.
    /// </para>
    /// </remarks>
    private static JsonObject? View(JsonObject status)
    {
        var score = Number(status, "activeOpenScoreID");
        var container = Number(status, "activeViewContainerID");

        if (score is null || container is null)
        {
            return null;
        }

        var openScoreId = score.ToJsonString();
        var containerId = container.ToJsonString();

        return new JsonObject
        {
            ["openScoreID"] = score.DeepClone(),
            ["viewContainerID"] = container.DeepClone(),
            ["switchWith"] = "dorico-use command send --name Window.SwitchToLayout --param OpenScoreID="
                + openScoreId + " --param ScoreLayoutViewContainerID=" + containerId + " --param LayoutID=<layout id>",
            ["note"] = "Measured: these two ids are what Window.SwitchToLayout needs to make a specific layout the "
                + "current one, and it takes them from Dorico's own status. The current layout cannot be read back: no "
                + "payload names it. For a layout's own stored settings, `options set --type layout --layout N` is the "
                + "route that needs no current layout at all.",
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
