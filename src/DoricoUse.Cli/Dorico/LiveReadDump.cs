using System.Globalization;
using System.Text.RegularExpressions;
using System.Text.Json.Nodes;
using DoricoUse.Cli.Json;

namespace DoricoUse.Cli.Dorico;

/// <summary>
/// Turns the text a live read writes into JSON.
/// </summary>
/// <remarks>
/// <para>
/// Kept apart from <see cref="LiveRead"/> — which owns the script and the exchange with Dorico — because this half
/// is a pure function of a list of lines, so it can be tested without Dorico at all (see
/// <c>tests/ProjectFileCheck</c>). It matters: the input is Dorico's own printer format, not a data format, and a
/// line this build cannot read is counted and reported rather than guessed at.
/// </para>
/// <para>
/// The lines are tab-separated and written by the script in <see cref="LiveRead.Script"/>; the note lines carry
/// Dorico's printer text as their last field, which is what <see cref="Parse"/> keeps for <c>unparsedLines</c>.
/// </para>
/// </remarks>
internal static class LiveReadDump
{
    /// <summary>The first line of the dump, so a truncated or foreign file is caught rather than half-read.</summary>
    public const string Header = "dorico-use live read";

    private static readonly Regex StreamLine = new(
        @"^stream\t(?<flow>\d+)\t(?<id>-?\d+)\t(?<events>-?\d+)\t(?<kinds>.*)$",
        RegexOptions.CultureInvariant);

    private static readonly Regex NoteLine = new(
        @"^note\t(?<flow>\d+)\t(?<stream>-?\d+)\t(?<id>-?\d+)\t(?<position>[^\t]+)\t(?<duration>[^\t]+)\t(?<pitch>[^\t]*)\t(?<line>[^\t]*)$",
        RegexOptions.CultureInvariant);

    private static readonly Regex ScoreLine = new(
        @"^score\t(?<id>[^\t]*)\t(?<version>[^\t]*)$",
        RegexOptions.CultureInvariant);

    private static readonly Regex TimebaseLine = new(
        @"^timebase\t(?<flow>\d+)\t(?<id>-?\d+)\t(?<events>-?\d+)\t(?<kinds>.*)$",
        RegexOptions.CultureInvariant);

    /// <summary>
    /// One rehearsal mark as the timebase printer wrote it. <c>text</c> may be empty: measured, Dorico's line for a
    /// mark is <c>300 (300): kRehearsalMarkEvent (id:69445) at 300</c> and carries no text at all, so the field is
    /// filled from the line only when the printer puts something quoted in it, and the whole line is kept.
    /// </summary>
    private static readonly Regex RehearsalLine = new(
        @"^rehearsal\t(?<flow>\d+)\t(?<stream>-?\d+)\t(?<id>-?\d+)\t(?<position>[^\t]*)\t(?<text>[^\t]*)\t(?<line>.*)$",
        RegexOptions.CultureInvariant);

    private static readonly Regex NotesTrailer = new(
        @"^notes\t(?<written>\d+)$",
        RegexOptions.CultureInvariant);

    /// <summary>
    /// Reads one dump. Never throws on a line it does not understand: those are collected and reported through
    /// <c>counts.unparsedLines</c> and the caveats, because a printer format can change between Dorico versions and
    /// a silent misreading would be worse than a visible gap.
    /// </summary>
    /// <param name="lines">The dump, as the script wrote it.</param>
    /// <param name="noteLimit">The limit the script was given, reported back as <c>counts.noteLimit</c>.</param>
    public static JsonObject Parse(string[] lines, int noteLimit)
    {
        var flows = new JsonArray();
        var notes = new JsonArray();
        var rehearsals = new JsonArray();
        var kinds = new JsonObject();
        var timebaseKinds = new JsonObject();
        var unparsed = new JsonArray();

        var current = (JsonObject?)null;
        var streams = 0;
        var timebaseStreams = 0;
        var events = 0;
        var noteCount = 0;
        var rehearsalCount = 0;
        var header = false;
        string? version = null;
        int? scoreId = null;
        string? error = null;

        foreach (var line in lines)
        {
            if (!header)
            {
                header = line.StartsWith(Header, StringComparison.Ordinal);
                continue;
            }

            if (line.StartsWith("error\t", StringComparison.Ordinal))
            {
                error = line["error\t".Length..];
                continue;
            }

            var score = ScoreLine.Match(line);

            if (score.Success)
            {
                scoreId = int.TryParse(score.Groups["id"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id)
                    ? id
                    : null;
                version = score.Groups["version"].Value;

                continue;
            }

            // The script's own trailer, which repeats what the note lines already said.
            if (NotesTrailer.IsMatch(line))
            {
                continue;
            }

            var stream = StreamLine.Match(line);

            if (stream.Success)
            {
                var flowIndex = int.Parse(stream.Groups["flow"].Value, CultureInfo.InvariantCulture);
                var streamKinds = Counts(stream.Groups["kinds"].Value, kinds);
                var entry = FlowOf(flows, ref current, flowIndex);

                var streamId = int.TryParse(stream.Groups["id"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var sid)
                    ? sid
                    : -1;

                var streamEvents = int.TryParse(stream.Groups["events"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var sevents)
                    ? sevents
                    : -1;

                JsonArr.AddNode((JsonArray)entry["streams"]!, new JsonObject
                {
                    ["id"] = streamId,
                    ["events"] = streamEvents,
                    ["kinds"] = streamKinds,
                });

                streams++;
                events += Math.Max(0, streamEvents);

                continue;
            }

            var timebase = TimebaseLine.Match(line);

            if (timebase.Success)
            {
                var flowIndex = int.Parse(timebase.Groups["flow"].Value, CultureInfo.InvariantCulture);
                var streamKinds = Counts(timebase.Groups["kinds"].Value, timebaseKinds);
                var entry = FlowOf(flows, ref current, flowIndex);

                JsonArr.AddNode((JsonArray)entry["timebaseStreams"]!, new JsonObject
                {
                    ["id"] = int.TryParse(timebase.Groups["id"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var tid) ? tid : -1,
                    ["events"] = int.TryParse(timebase.Groups["events"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var tevents) ? tevents : -1,
                    ["kinds"] = streamKinds,
                });

                timebaseStreams++;

                continue;
            }

            var rehearsal = RehearsalLine.Match(line);

            if (rehearsal.Success)
            {
                rehearsalCount++;

                JsonArr.AddNode(rehearsals, new JsonObject
                {
                    ["flow"] = int.Parse(rehearsal.Groups["flow"].Value, CultureInfo.InvariantCulture),
                    ["stream"] = int.Parse(rehearsal.Groups["stream"].Value, CultureInfo.InvariantCulture),
                    ["eventId"] = Long(rehearsal.Groups["id"].Value),
                    ["position"] = rehearsal.Groups["position"].Value,
                    ["text"] = rehearsal.Groups["text"].Value.Length == 0 ? null : rehearsal.Groups["text"].Value,
                    ["line"] = rehearsal.Groups["line"].Value,
                });

                continue;
            }

            var note = NoteLine.Match(line);

            if (note.Success)
            {
                noteCount++;

                JsonArr.AddNode(notes, new JsonObject
                {
                    ["flow"] = int.Parse(note.Groups["flow"].Value, CultureInfo.InvariantCulture),
                    ["stream"] = int.Parse(note.Groups["stream"].Value, CultureInfo.InvariantCulture),
                    ["eventId"] = Long(note.Groups["id"].Value),
                    ["position"] = note.Groups["position"].Value,
                    ["duration"] = note.Groups["duration"].Value,
                    ["pitch"] = note.Groups["pitch"].Value,
                });

                continue;
            }

            JsonArr.AddText(unparsed, line);
        }

        var payload = new JsonObject
        {
            ["source"] = "live",
            ["score"] = new JsonObject
            {
                ["openScoreId"] = Number(scoreId),
                ["version"] = version,
            },
            ["flows"] = flows,
            ["counts"] = new JsonObject
            {
                ["flows"] = flows.Count,
                ["streams"] = streams,
                ["timebaseStreams"] = timebaseStreams,
                ["events"] = events,
                ["notes"] = noteCount,
                ["rehearsals"] = rehearsalCount,
                ["unparsedLines"] = unparsed.Count,
                ["noteLimit"] = Number(noteLimit <= 0 ? null : noteLimit),
            },
            ["kinds"] = kinds,
            ["timebaseKinds"] = timebaseKinds,
            ["notes"] = notes,
            ["rehearsals"] = rehearsals,
        };

        var caveats = new JsonArray();

        JsonArr.AddText(caveats,
            "live: read from the running document by a script that is handed to Dorico through Script.RunScript. "
            + "Nothing is saved and the document is not rebound, which is the whole reason this is the default. "
            + "What it can see is what Dorico's own printer prints.");

        JsonArr.AddText(caveats,
            "The note lines are parsed out of that printer's text with regular expressions, so a line this build "
            + "does not recognise is counted in counts.unparsedLines instead of being guessed at"
            + (unparsed.Count == 0 ? " (none in this read)." : $" ({unparsed.Count} in this read)."));

        JsonArr.AddText(caveats,
            "No MIDI number, accidental, dynamic value, articulation or layout comes out of this path: the printer "
            + "writes written pitch names. For exact data use `file read` on a saved project, or "
            + "`session read --rebind`, which saves a copy and decodes it - and rebinds the open document to that "
            + "copy, which is why it is opt-in.");

        JsonArr.AddText(caveats,
            "`pitch` here is the name the score prints, so it is the spelled name (a B flat is 'Bb', not 'A#'); "
            + "the exact pitch class and accidental need the file.");

        JsonArr.AddText(caveats,
            "Rehearsal marks come from the flow's timebase stream, where Dorico's printer writes them as "
            + "`kRehearsalMarkEvent (id:...) at <position>`. Measured: that line carries the position and the event "
            + "id and no text, so `text` is filled only when the printer quotes one and the whole line is kept as "
            + "`line`. `counts.rehearsals` is how many were seen; a flow with no mark reports zero rather than "
            + "staying silent, and `counts.timebaseStreams` says how many timebase streams were read at all.");

        if (noteCount == 0 && streams > 0)
        {
            JsonArr.AddText(caveats,
                "No note line was parsed although " + streams + " stream(s) were read: either the document has no "
                + "notes, or this build cannot read the printer's current output. counts.events says how many events "
                + "were seen.");
        }

        if (flows.Count == 0)
        {
            JsonArr.AddText(caveats,
                "No flow at all: Dorico has no document open, or the one it has is empty. Open a project and read "
                + "again.");
        }

        if (error is not null)
        {
            payload["error"] = error;
            JsonArr.AddText(caveats, "The script reported: " + error);
        }

        payload["caveats"] = caveats;

        return payload;
    }

    /// <summary>
    /// A nullable number as a JSON node. Written out because assigning an <c>int?</c> straight into a
    /// <see cref="JsonObject"/> picks the generic <c>JsonValue.Create&lt;T&gt;</c>, which needs reflection-based
    /// serialization metadata — and this project builds with <c>PublishAot</c>, where that is switched off, so the
    /// assignment throws at run time instead of writing a number.
    /// </summary>
    private static JsonValue? Number(int? value) => value is null ? null : JsonValue.Create(value.Value);

    /// <inheritdoc cref="Number(int?)"/>
    private static JsonValue? Long(string text)
        => long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? JsonValue.Create(value)
            : null;

    /// <summary>The <c>kKind=count</c> summary of one stream line, also added up into a running total.</summary>
    private static JsonObject Counts(string summary, JsonObject total)
    {
        var counts = new JsonObject();

        foreach (var pair in summary.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var split = pair.Split('=', 2);

            if (split.Length != 2)
            {
                continue;
            }

            var count = int.TryParse(split[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : 0;

            counts[split[0]] = count;
            total[split[0]] = (NodeRead.Int(total, split[0]) ?? 0) + count;
        }

        return counts;
    }

    /// <summary>
    /// The flow entry for one index, created on first sight. Either kind of stream line can be the first thing a
    /// flow reports — a flow with no notes still has a timebase — so both go through here.
    /// </summary>
    private static JsonObject FlowOf(JsonArray flows, ref JsonObject? current, int flowIndex)
    {
        if (current is null || (int?)current["index"] != flowIndex)
        {
            current = new JsonObject
            {
                ["index"] = flowIndex,
                ["streams"] = new JsonArray(),
                ["timebaseStreams"] = new JsonArray(),
            };

            JsonArr.AddNode(flows, current);
        }

        return current;
    }
}
