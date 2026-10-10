using System.Globalization;
using System.Text.Json.Nodes;
using DoricoUse.Cli.Dorico;
using DoricoUse.Cli.Json;

namespace DoricoUse.Cli.Views;

/// <summary>
/// The report <c>file read</c> prints: what a <c>.dorico</c> file contains, decoded from disk.
/// </summary>
/// <remarks>
/// The shape follows the agent's question. <c>file</c> says which container this is, <c>metadata</c> is what
/// scoreinfo.xml says, <c>score</c> is what the binary holds: players, layouts, flows, the staves notes were found
/// on, the metre, and then the notes themselves — pitches, rational durations, measures and beats.
/// </remarks>
internal static class ProjectFileView
{
    /// <summary>How many notes the report carries unless the caller asks for more.</summary>
    public const int DefaultNoteLimit = 100;

    /// <summary>
    /// What the reader derives rather than reads. An agent that trusts "pitch" as notation, or "measure" as a stored
    /// number, would be wrong in ways the JSON cannot show.
    /// </summary>
    private static readonly JsonArray Caveats =
    [
        (JsonNode?)JsonValue.Create(
            "pitch: the project stores a MIDI note number, not a spelling; the name here is derived from it and uses "
            + "sharps only. Read 'midi' when the exact pitch matters."),
        (JsonNode?)JsonValue.Create(
            "duration: an exact rational in crotchets (a 16th is 1/4, a 4/4 bar is 4). It is the sounding duration of "
            + "the tie chain, so a note tied across a bar line is one event."),
        (JsonNode?)JsonValue.Create(
            "measure/beat: bar lines are not stored. Measures are replayed from the bar-division events in 'meters' "
            + "and every note's 'position', so they are derived, and rests are gaps rather than events."),
        (JsonNode?)JsonValue.Create(
            "stave: notes are attributed to the player and instrument their stream belongs to; the sounding pitch is "
            + "the note event's, and a part layout applies its own transposition when it is printed."),
    ];

    /// <summary>What to include, and what to leave out.</summary>
    public sealed record Options(int NoteLimit, int? FirstMeasure, int? LastMeasure, string? Player);

    public static JsonObject Report(ProjectFile file, Options options)
    {
        var report = new JsonObject
        {
            ["file"] = File(file),
            ["metadata"] = Metadata(file.Metadata),
            ["members"] = Members(file.Members),
        };

        report["score"] = Score(file.Score, options);

        return report;
    }

    private static JsonObject File(ProjectFile file) => new()
    {
        ["path"] = file.Path,
        ["bytes"] = file.Bytes,
        ["kind"] = "Dorico project (ZIP package)",
        ["container"] = new JsonObject
        {
            ["version"] = file.Container.Version,
            ["rootFile"] = file.Container.RootPath,
            ["mediaType"] = file.Container.MediaType,
        },
        ["members"] = file.Members.Count,
    };

    private static JsonObject Metadata(ProjectFileMetadata metadata) => new()
    {
        ["title"] = metadata.Title,
        ["subtitle"] = metadata.Subtitle,
        ["composer"] = metadata.Composer,
        ["arranger"] = metadata.Arranger,
        ["lyricist"] = metadata.Lyricist,
        ["copyright"] = metadata.Copyright,
        ["createdDate"] = metadata.CreatedDate,
        ["lastSavedDate"] = metadata.LastSavedDate,
        ["createdVersion"] = metadata.CreatedVersion,
        ["lastSavedVersion"] = metadata.LastSavedVersion,
        ["projectDuration"] = metadata.ProjectDuration,
        ["players"] = Strings(metadata.Players),
        ["flows"] = FlowTitles(metadata.FlowTitles, metadata.FlowDurations),
    };

    private static JsonArray Strings(IReadOnlyList<string> values)
    {
        var array = new JsonArray();

        foreach (var value in values)
        {
            JsonArr.AddText(array, value);
        }

        return array;
    }

    private static JsonArray FlowTitles(IReadOnlyList<string> titles, IReadOnlyList<string> durations)
    {
        var array = new JsonArray();

        for (var index = 0; index < titles.Count; index++)
        {
            JsonArr.AddNode(array, new JsonObject
            {
                ["title"] = titles[index],
                ["duration"] = index < durations.Count ? durations[index] : null,
            });
        }

        return array;
    }

    private static JsonArray Members(IReadOnlyList<ProjectFileMember> members)
    {
        var array = new JsonArray();

        foreach (var member in members)
        {
            JsonArr.AddNode(array, new JsonObject
            {
                ["path"] = member.Path,
                ["bytes"] = member.Bytes,
                ["compressedBytes"] = member.CompressedBytes,
            });
        }

        return array;
    }

    private static JsonObject Score(ProjectFileScore score, Options options)
    {
        var (notes, matched) = Select(score.Notes, options);

        var report = new JsonObject
        {
            ["info"] = new JsonObject
            {
                ["fileVersion"] = score.FileVersion,
                ["title"] = score.Info.Title,
                ["subtitle"] = score.Info.Subtitle,
                ["composer"] = score.Info.Composer,
                ["arranger"] = score.Info.Arranger,
                ["lyricist"] = score.Info.Lyricist,
                ["createdDate"] = score.Info.CreatedDate,
                ["lastSavedDate"] = score.Info.LastSavedDate,
                ["createdVersion"] = score.Info.CreatedVersion,
                ["lastSavedVersion"] = score.Info.LastSavedVersion,
                ["originator"] = score.Info.Originator,
            },
            ["players"] = Players(score.Players),
            ["layouts"] = Layouts(score.Layouts),
            ["flows"] = Flows(score.Flows),
            ["staves"] = Staves(score.Staves),
            ["meters"] = Meters(score.Meters),
            ["measures"] = new JsonObject
            {
                ["count"] = score.LastMeasure,
                ["derived"] = true,
                ["note"] = "bar lines are not stored; measures are replayed from the metre events below",
            },
            ["counts"] = new JsonObject
            {
                ["notes"] = score.Notes.Count,
                ["notesMatched"] = matched,
                ["notesReturned"] = notes.Count,
                ["players"] = score.Players.Count,
                ["layouts"] = score.Layouts.Count,
                ["flows"] = score.Flows.Count,
                ["stavesWithNotes"] = score.Staves.Count,
                ["measures"] = score.LastMeasure,
            },
            ["caveats"] = Caveats,
            ["parser"] = new JsonObject
            {
                ["names"] = score.NameCount,
                ["values"] = score.ValueCount,
                ["records"] = score.RecordCount,
            },
        };

        report["notes"] = Notes(notes);

        return report;
    }

    private static (List<ProjectFileNote> Notes, int Matched) Select(
        IReadOnlyList<ProjectFileNote> notes,
        Options options)
    {
        var matched = 0;
        var selected = new List<ProjectFileNote>();

        foreach (var note in notes)
        {
            if (options.FirstMeasure is { } first && note.Measure < first)
            {
                continue;
            }

            if (options.LastMeasure is { } last && note.Measure > last)
            {
                continue;
            }

            if (options.Player is { } player
                && !Pattern.Matches(note.Player, player)
                && !Pattern.Matches(note.Instrument, player))
            {
                continue;
            }

            matched++;

            if (options.NoteLimit < 0 || selected.Count < options.NoteLimit)
            {
                selected.Add(note);
            }
        }

        return (selected, matched);
    }

    private static JsonArray Players(IReadOnlyList<ProjectFilePlayer> players)
    {
        var array = new JsonArray();

        foreach (var player in players)
        {
            var instruments = new JsonArray();

            foreach (var instrument in player.Instruments)
            {
                JsonArr.AddNode(instruments, new JsonObject
                {
                    ["id"] = instrument.Id,
                    ["name"] = instrument.Name,
                    ["entityId"] = instrument.EntityId,
                    ["staves"] = instrument.Staves,
                    ["range"] = instrument.Range,
                });
            }

            JsonArr.AddNode(array, new JsonObject
            {
                ["id"] = player.Id,
                ["name"] = player.Name,
                ["shortName"] = player.ShortName,
                ["instruments"] = instruments,
            });
        }

        return array;
    }

    private static JsonArray Layouts(IReadOnlyList<ProjectFileLayout> layouts)
    {
        var array = new JsonArray();

        foreach (var layout in layouts)
        {
            JsonArr.AddNode(array, new JsonObject
            {
                ["id"] = layout.Id,
                ["number"] = layout.Number,
                ["name"] = layout.Name,
                ["type"] = layout.Type,
            });
        }

        return array;
    }

    private static JsonArray Flows(IReadOnlyList<ProjectFileFlow> flows)
    {
        var array = new JsonArray();

        foreach (var flow in flows)
        {
            JsonArr.AddNode(array, new JsonObject
            {
                ["id"] = flow.Id,
                ["name"] = flow.Name,
                ["streams"] = flow.Streams,
                ["blocks"] = flow.Blocks,
                ["notes"] = flow.Notes,
            });
        }

        return array;
    }

    private static JsonArray Staves(IReadOnlyList<ProjectFileStave> staves)
    {
        var array = new JsonArray();

        foreach (var stave in staves)
        {
            JsonArr.AddNode(array, new JsonObject
            {
                ["stream"] = stave.Stream,
                ["kind"] = stave.Kind,
                ["player"] = stave.Player,
                ["instrument"] = stave.Instrument,
                ["stave"] = stave.Stave,
                ["voice"] = stave.Voice,
                ["notes"] = stave.Notes,
            });
        }

        return array;
    }

    private static JsonArray Meters(IReadOnlyList<ProjectFileMeter> meters)
    {
        var array = new JsonArray();

        foreach (var meter in meters)
        {
            JsonArr.AddNode(array, new JsonObject
            {
                ["bar"] = meter.Bar,
                ["position"] = meter.Position,
                ["timeSignature"] = meter.TimeSignature,
            });
        }

        return array;
    }

    private static JsonArray Notes(IReadOnlyList<ProjectFileNote> notes)
    {
        var array = new JsonArray();

        foreach (var note in notes)
        {
            JsonArr.AddNode(array, new JsonObject
            {
                ["eventId"] = note.EventId,
                ["measure"] = note.Measure,
                ["beat"] = note.Beat,
                ["position"] = note.Position,
                ["duration"] = note.Duration,
                ["pitch"] = note.Pitch,
                ["midi"] = note.Midi,
                ["player"] = note.Player,
                ["instrument"] = note.Instrument,
                ["stave"] = note.Stave,
                ["voice"] = note.Voice,
                ["stream"] = note.Stream,
            });
        }

        return array;
    }

    /// <summary>
    /// Parses <c>--limit</c>: a count, or <c>all</c>/<c>-1</c> for every note. Zero means "no notes, just the shape".
    /// </summary>
    public static int ParseLimit(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            return -1;
        }

        if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) || value < -1)
        {
            throw new CliException("InvalidArgument", $"--limit must be a count, 'all' or -1, not '{text}'.", 1);
        }

        return value;
    }

    /// <summary>Parses <c>--measure</c>: one measure (<c>12</c>) or a range (<c>12-16</c>).</summary>
    public static (int First, int Last)? ParseMeasures(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var trimmed = text.Trim();
        var dash = trimmed.IndexOf('-', 1);

        if (dash < 0)
        {
            return int.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out var single)
                ? (single, single)
                : throw new CliException(
                    "InvalidArgument",
                    $"--measure must be a measure or a range like 12-16, not '{text}'.",
                    1);
        }

        if (!int.TryParse(trimmed[..dash], NumberStyles.Integer, CultureInfo.InvariantCulture, out var first)
            || !int.TryParse(trimmed[(dash + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var last))
        {
            throw new CliException(
                "InvalidArgument",
                $"--measure must be a measure or a range like 12-16, not '{text}'.",
                1);
        }

        return (first, last);
    }
}
