using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DoricoUse.Cli.Dorico;
using DoricoUse.Cli.Ipc;
using DoricoUse.Cli.Json;
using DoricoUse.Cli.Views;

namespace DoricoUse.Cli.Host;

internal sealed partial class HostServer
{
    private async Task<JsonObject> NoteInsertAsync(JsonObject args, TimeSpan timeout)
    {
        var notes = ReadStringArray(args, "notes");
        var chords = ReadChordGroups(args, "chords");
        var rest = NodeRead.Bool(args, "rest") ?? false;
        var duration = NodeRead.Str(args, "duration");
        var grid = NodeRead.Str(args, "grid");
        var tuplet = NodeRead.Str(args, "tuplet");
        var staff = ReadStringArray(args, "staff");
        var exit = NodeRead.Bool(args, "exit") ?? true;
        var waitMs = Math.Clamp(NodeRead.Long(args, "waitMs") ?? 0, 0, 120_000);
        var verify = NodeRead.Bool(args, "verify") ?? true;
        var dryRun = NodeRead.Bool(args, "dryRun") ?? false;
        var mode = NodeRead.Str(args, "mode");
        var fromStart = NodeRead.Bool(args, "fromStart") ?? false;
        var read = NodeRead.Bool(args, "read") ?? false;
        var readRebind = NodeRead.Bool(args, "readRebind") ?? false;
        var readPath = NodeRead.Str(args, "readPath");
        var readOptions = ReadReadOptions(NodeRead.Obj(args, "readOptions"));
        string? requestedMode = null;

        // `--read` has two routes and only one of them saves a file, so the whole difference is this flag. The
        // rebind route is File.SaveAs and needs a path; the live route is a read-only script the CLI runs after
        // this call returns, so the host has nothing to do for it and must not ask for a path it will not use.
        var rebind = read && readRebind;

        if (readRebind && !read)
        {
            throw new CliException(
                "InvalidArgument",
                "note insert --read-rebind says how --read reads, so it needs --read as well.",
                1);
        }

        // Defensive: a copy path or read options on a live read would be silently ignored (there is no copy to save
        // and nothing to filter), which is the kind of quiet no-op this tool refuses elsewhere. The CLI never sends
        // the combination, so this only catches a CLI and a host of different vintages talking to each other.
        if (read && !readRebind
            && (!string.IsNullOrWhiteSpace(readPath) || NodeRead.Obj(args, "readOptions") is not null))
        {
            throw new CliException(
                "InvalidArgument",
                "note insert was given a copy path (or read options) for a live read, and a live read saves no copy "
                + "and has nothing to filter. Those belong to --read --read-rebind: set readRebind to true, or send "
                + "the live read without them.",
                1);
        }

        if (rebind && string.IsNullOrWhiteSpace(readPath))
        {
            throw new CliException(
                "InvalidArgument",
                "note insert --read --read-rebind needs the path of the copy to save (the CLI resolves it from "
                + "--read-to).",
                1);
        }

        if (!string.IsNullOrWhiteSpace(mode))
        {
            if (!NoteMode.TryResolve(mode, out requestedMode))
            {
                throw new CliException(
                    "InvalidArgument",
                    $"'{mode}' is not a note input mode. Use {NoteMode.Names()}.",
                    1);
            }
        }

        // `notes` is the flat list (the original contract); `chords` is the same thing grouped, one entry per
        // rhythmic position. A version of the CLI that predates chords sends only `notes`, so it still works.
        var events = chords.Count > 0 ? chords : notes.Select(note => new List<string> { note }).ToList();
        var chordCount = events.Count(group => group.Count > 1);

        if (events.Count == 0 && !rest)
        {
            throw new CliException(
                "InvalidArgument",
                "note insert needs notes (--pitch/--octave, --notes or --chord) or --rest.",
                1);
        }

        var steps = new List<Step>();
        string? resolvedDuration = null;

        if (!string.IsNullOrWhiteSpace(duration))
        {
            if (!NoteValue.TryResolve(duration, out resolvedDuration))
            {
                throw new CliException(
                    "InvalidArgument",
                    $"'{duration}' is not a note value. Use {NoteValue.Names()}.",
                    1);
            }

            // The parameter is called LogDuration but it takes the rhythm value enum.
            steps.Add(new Step("NoteInput.NoteValue", [new("LogDuration", resolvedDuration)]));
        }

        if (!string.IsNullOrWhiteSpace(grid))
        {
            // The grid is where the caret snaps; it is not the note value.
            steps.Add(new Step("NoteInput.SetGridResolution", [new("RhythmicGridResolutionValue", grid)]));
        }

        var staffSteps = new List<Step>();

        foreach (var move in staff)
        {
            if (!NoteStaff.TryResolve(move, out var staffCommand))
            {
                throw new CliException(
                    "InvalidArgument",
                    $"'{move}' is not a stave move. Use {NoteStaff.Names()}.",
                    1);
            }

            staffSteps.Add(new Step(staffCommand));
        }

        steps.AddRange(staffSteps);

        string? resolvedTuplet = null;

        if (!string.IsNullOrWhiteSpace(tuplet))
        {
            if (!NoteTuplet.TryResolve(tuplet, out resolvedTuplet))
            {
                throw new CliException(
                    "InvalidArgument",
                    $"'{tuplet}' is not a tuplet definition. Use {NoteTuplet.Names()}.",
                    1);
            }

            // Opening the run before the first note is what makes the following notes a tuplet.
            steps.Add(new Step("NoteInput.StartTupletRun", [new("Definition", resolvedTuplet)]));
        }

        foreach (var group in events)
        {
            var parsed = new List<(string Pitch, int Octave, string? Accidental)>();

            foreach (var text in group)
            {
                if (!NoteName.TryParse(text, out var pitch, out var octave, out var accidental))
                {
                    throw new CliException(
                        "InvalidArgument",
                        $"Could not read the note name '{text}'. Write it like F#5, Bb4 or C4.",
                        1);
                }

                parsed.Add((pitch, octave, accidental));
            }

            if (parsed.Count > 1)
            {
                // Chord input is a toggle in Dorico (the Q key), and the status does not report it, so the CLI
                // opens it for the group and closes it again afterwards: a chord is one rhythmic position.
                steps.Add(new Step("NoteInput.StartEndChord"));
            }

            foreach (var (pitch, octave, accidental) in parsed)
            {
                if (accidental is not null)
                {
                    steps.Add(new Step("NoteInput.SetAccidental", [new("Type", accidental)]));
                }

                steps.Add(new Step("NoteInput.Pitch", [new("Pitch", pitch), new("OctaveValue", Invariant(octave))]));

                // No advance here: NoteInput.Pitch commits the note and moves the caret on by the current note
                // value. Sending MoveAdvance as well would insert a rest after every note.
            }

            if (parsed.Count > 1)
            {
                steps.Add(new Step("NoteInput.StartEndChord"));
            }
        }

        if (resolvedTuplet is not null)
        {
            // A tuplet run that the last note completed has already closed itself; this is what closes one the
            // notes left open, and it is a no-op otherwise.
            steps.Add(new Step("NoteInput.EndTupletRun"));
        }

        if (rest)
        {
            // Advancing the caret is what inserts a rest.
            steps.Add(new Step("NoteInput.MoveAdvance"));
        }

        if (exit)
        {
            steps.Add(new Step("NoteInput.Exit"));
        }

        if (dryRun)
        {
            var plan = new JsonObject
            {
                ["dryRun"] = true,
                ["notes"] = NotesJson(notes),
                ["rest"] = rest,
                ["duration"] = resolvedDuration,
                ["grid"] = grid,
                ["tuplet"] = resolvedTuplet,
                ["steps"] = StepsJson(fromStart ? CaretResetSteps(steps) : steps),
                ["enterDecision"] = "Note input needs Write mode, and NoteInput.Enter is a toggle, so both are "
                    + "sent only when the status says they are needed. Run without --dry-run to see which ones "
                    + "were.",
                ["mode"] = new JsonObject
                {
                    ["requested"] = requestedMode is null ? null : NoteMode.ShortName(requestedMode),
                    ["plannedCommand"] = requestedMode is null
                        ? null
                        : $"{NoteMode.Command}?{NoteMode.Parameter}={requestedMode}",
                    ["note"] = requestedMode is null
                        ? "No --mode was given, so the note input mode is read from the status and reported, and "
                            + "nothing changes it."
                        : $"The current mode is read from the status at run time, and {NoteMode.Command} is sent "
                            + "only when it differs, so --dry-run cannot say whether it will be needed.",
                },
            };

            if (fromStart)
            {
                plan["fromStart"] = new JsonObject
                {
                    ["commands"] = new JsonArray(
                        (JsonNode?)JsonValue.Create("NoteInput.Exit (only when note input is on)"),
                        (JsonNode?)JsonValue.Create("Edit.SelectNone"),
                        (JsonNode?)JsonValue.Create("NoteInput.Enter")),
                    ["note"] = CaretResetNote,
                };
            }

            if (read)
            {
                // Each route says what it will do, because the difference between them is a side effect on the
                // document the user has open, and --dry-run is where that difference has to be readable before
                // anything is sent.
                plan["read"] = rebind
                    ? ReadPlan(readPath!, readOptions)
                    : LiveReadPlan(PlannedReadLimit(args));
            }

            if (chordCount > 0)
            {
                plan["chords"] = ChordsJson(events);
            }

            if (staff.Count > 0)
            {
                plan["staff"] = StaffJson(staff);
            }

            return new JsonObject { ["note"] = plan };
        }

        var stepTimeout = timeout < TimeSpan.FromSeconds(5) ? timeout : TimeSpan.FromSeconds(5);
        var statusTimeout = timeout;

        // +3 covers the two conditional preamble commands (write mode, note input) and the note input mode.
        var budget = (stepTimeout * (steps.Count + 3)) + statusTimeout + TimeSpan.FromMilliseconds(waitMs);

        if (rebind)
        {
            // Writing the copy and decoding it: the save is about a second, the watch budget is the rest. The live
            // route saves nothing, so it adds nothing here - its wait belongs to the CLI, which runs the read.
            budget += SaveCopy.DefaultWait + TimeSpan.FromSeconds(10);
        }

        return await _keeper.ExecuteAsync("note insert", async client =>
        {
            var results = new JsonArray();
            var started = Stopwatch.GetTimestamp();
            var enteredNoteInput = false;
            var leftNoteInput = false;
            var caretReset = false;

            var status = await client.GetStatusAsync(statusTimeout, _cts.Token).ConfigureAwait(false);
            EnsureExpectedScore(args, status);
            var enteredWriteMode = false;

            // A fresh project starts in Setup mode, where there is no caret to write at.
            if (NodeRead.Str(status, "windowMode") is { } windowMode && windowMode != "kWriteMode")
            {
                var switchParameters = new List<KeyValuePair<string, string>> { new("WindowMode", "kWriteMode") };
                var switchStarted = Stopwatch.GetTimestamp();
                var switchAck = await client.SendCommandAsync(
                    "Window.SwitchMode",
                    switchParameters,
                    stepTimeout,
                    _cts.Token).ConfigureAwait(false);

                enteredWriteMode = true;
                JsonArr.AddNode(results, AckJson(
                    "Window.SwitchMode",
                    switchParameters,
                    switchAck,
                    (long)Stopwatch.GetElapsedTime(switchStarted).TotalMilliseconds));
            }

            var noteInputActive = NodeRead.Bool(status, "noteInputActive") == true;

            if (fromStart)
            {
                // The only way back to a known place. NoteInput.Enter puts the caret at the start of the flow only
                // when nothing is selected AND it was off, and Edit.SelectNone on its own leaves the caret where
                // the last write left it, so the cycle is forced: off, deselect, enter.
                if (noteInputActive)
                {
                    var exitStarted = Stopwatch.GetTimestamp();
                    var exitAck = await client.SendCommandAsync("NoteInput.Exit", null, stepTimeout, _cts.Token)
                        .ConfigureAwait(false);

                    leftNoteInput = true;
                    JsonArr.AddNode(results, AckJson(
                        "NoteInput.Exit",
                        null,
                        exitAck,
                        (long)Stopwatch.GetElapsedTime(exitStarted).TotalMilliseconds));
                }

                var selectNoneStarted = Stopwatch.GetTimestamp();
                var selectNoneAck = await client.SendCommandAsync("Edit.SelectNone", null, stepTimeout, _cts.Token)
                    .ConfigureAwait(false);

                JsonArr.AddNode(results, AckJson(
                    "Edit.SelectNone",
                    null,
                    selectNoneAck,
                    (long)Stopwatch.GetElapsedTime(selectNoneStarted).TotalMilliseconds));

                var enterStarted = Stopwatch.GetTimestamp();
                var enterAck = await client.SendCommandAsync("NoteInput.Enter", null, stepTimeout, _cts.Token)
                    .ConfigureAwait(false);

                enteredNoteInput = true;
                caretReset = true;
                JsonArr.AddNode(results, AckJson(
                    "NoteInput.Enter",
                    null,
                    enterAck,
                    (long)Stopwatch.GetElapsedTime(enterStarted).TotalMilliseconds));
            }
            else if (!noteInputActive)
            {
                var enterAck = await client.SendCommandAsync("NoteInput.Enter", null, stepTimeout, _cts.Token)
                    .ConfigureAwait(false);

                enteredNoteInput = true;
                JsonArr.AddNode(results, AckJson("NoteInput.Enter", null, enterAck, 0));
            }

            // The note input mode is a state rather than a step of the sequence, and the status is the only way
            // to know it: it is read above, so the command is sent only when the mode is not already what was
            // asked for, and the payload reports what was in force either way.
            var modeBefore = NodeRead.Str(status, "noteInputMode");
            var modeChanged = false;
            var pending = new List<Step>();

            if (requestedMode is not null && !string.Equals(modeBefore, requestedMode, StringComparison.Ordinal))
            {
                pending.Add(new Step(NoteMode.Command, [new(NoteMode.Parameter, requestedMode)]));
                modeChanged = true;
            }

            pending.AddRange(steps);

            string? stoppedAt = null;

            foreach (var step in pending)
            {
                var stepStarted = Stopwatch.GetTimestamp();
                var ack = await client.SendCommandAsync(step.Name, step.Parameters, stepTimeout, _cts.Token)
                    .ConfigureAwait(false);

                JsonArr.AddNode(results, AckJson(
                    step.Name,
                    step.Parameters,
                    ack,
                    (long)Stopwatch.GetElapsedTime(stepStarted).TotalMilliseconds));

                if (NodeRead.IsError(ack))
                {
                    stoppedAt = step.Name;
                    break;
                }
            }

            var payload = new JsonObject
            {
                ["notes"] = NotesJson(notes),
                ["rest"] = rest,
                ["duration"] = resolvedDuration,
                ["grid"] = grid,
                ["tuplet"] = resolvedTuplet,
                ["enteredWriteMode"] = enteredWriteMode,
                ["enteredNoteInput"] = enteredNoteInput,
                ["steps"] = results,
                ["roundTripMs"] = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds,
            };

            // What the mode is now: the value the sequence left behind, unless a later status can say for sure.
            string? modeEffective = modeChanged ? requestedMode : modeBefore;
            JsonObject modePayload(string? effective) => ModeJson(
                requestedMode,
                modeBefore,
                effective ?? modeEffective,
                modeChanged);

            payload["mode"] = modePayload(modeEffective);

            if (chordCount > 0)
            {
                payload["chords"] = ChordsJson(events);
            }

            if (staff.Count > 0)
            {
                payload["staff"] = StaffJson(staff);
            }

            if (fromStart)
            {
                payload["fromStart"] = new JsonObject
                {
                    ["command"] = "Edit.SelectNone",
                    ["noteInputCycled"] = leftNoteInput,
                    ["enteredNoteInput"] = caretReset,
                    ["note"] = CaretResetNote,
                };
            }

            // The proof: ask Dorico to write a copy of the document the notes went into. This happens from here,
            // inside the same gated exchange as the notes, because File.SaveAs saves whichever document is active
            // and a window that comes to the front in between would send the copy to somebody else's project; the
            // guard is read again immediately before the save. The CLI decodes the copy (see NoteReadback).
            //
            // The live route has nothing to do here: it saves no copy, so it has no proof to set up, and the CLI
            // fills in `read` itself after this call returns (see LiveRead). Inventing a `read` object here would
            // say a save happened when none did.
            async Task AddReadAsync()
            {
                if (!rebind)
                {
                    return;
                }

                await EnsureExpectedScoreAsync(client, args, statusTimeout).ConfigureAwait(false);

                payload["read"] = await NoteReadback
                    .SaveAsync(client, readPath!, statusTimeout, _cts.Token)
                    .ConfigureAwait(false);
            }

            if (stoppedAt is not null)
            {
                payload["stoppedAt"] = stoppedAt;
                payload["note"] = "The sequence stopped at the first command Dorico refused.";
                await AddReadAsync().ConfigureAwait(false);

                return new JsonObject { ["note"] = payload };
            }

            if (!verify)
            {
                await AddReadAsync().ConfigureAwait(false);

                return new JsonObject { ["note"] = payload };
            }

            if (waitMs > 0)
            {
                await Task.Delay((int)waitMs, _cts.Token).ConfigureAwait(false);
            }

            var after = await client.GetStatusAsync(statusTimeout, _cts.Token).ConfigureAwait(false);
            payload["status"] = SessionView.Status(after);
            payload["mode"] = modePayload(NodeRead.Str(after, "noteInputMode"));
            payload["note"] = rebind
                ? "kOK only acknowledges receipt; 'read' is the answer. Dorico was asked to write a copy of the "
                    + "document it just wrote into (File.SaveAs, the same round trip as `session read --rebind`) and "
                    + "the CLI decoded it, so read.score.notes is the position of every note that is really in the "
                    + "score. The document is rebound to that copy."
                : read
                    ? "kOK only acknowledges receipt; 'read' is the answer. After this returns the CLI has Dorico run "
                        + "the read-only read script (Script.RunScript) against the document the notes went into and "
                        + "parses the file that script wrote, so read.source is 'live' and read.notes is what the "
                        + "score holds. Nothing is saved and the document is not rebound."
                    : "kOK only acknowledges receipt; the status shows the caret is where the commands left it and the "
                        + "mode that was in force, not which notes landed. Check the score (or an undo) to be sure.";

            await AddReadAsync().ConfigureAwait(false);

            return new JsonObject { ["note"] = payload };
        }, budget, _cts.Token).ConfigureAwait(false);
    }

    /// <summary>What <c>--from-start</c> does, in the words both the plan and the report carry.</summary>
    private const string CaretResetNote =
        "NoteInput.Exit (only when note input was on), Edit.SelectNone and NoteInput.Enter were sent before the "
        + "notes. With nothing selected and note input off, NoteInput.Enter puts the caret at the start of the flow, "
        + "which is the only way to write from a known place: Dorico has no caret query, NoteInput.EnterAtPoint does "
        + "not address a point, and Edit.GoTo does not jump to a bar (both measured, see the README).";

    /// <summary>The commands <c>--from-start</c> sends, for the dry-run plan.</summary>
    private static List<Step> CaretResetSteps(List<Step> steps)
    {
        var planned = new List<Step>
        {
            new("NoteInput.Exit"),
            new("Edit.SelectNone"),
            new("NoteInput.Enter"),
        };

        planned.AddRange(steps);

        return planned;
    }

    /// <summary>What the planned rebind read would do, for the dry-run plan.</summary>
    private static JsonObject ReadPlan(string path, ProjectFileView.Options options)
    {
        var measures = options.FirstMeasure is { } first && options.LastMeasure is { } last
            ? first == last
                ? first.ToString(System.Globalization.CultureInfo.InvariantCulture)
                : $"{first}-{last}"
            : null;

        return new JsonObject
        {
            ["route"] = "rebind",
            ["path"] = path,
            ["saveCommand"] = $"{SaveCopy.SaveAsCommand}?{SaveCopy.PathParameter}={path}",
            ["options"] = new JsonObject
            {
                ["noteLimit"] = options.NoteLimit,
                ["measures"] = measures,
                ["player"] = options.Player,
            },
            ["note"] = "After the notes are entered, Dorico saves a copy to this path and the copy is decoded with "
                + "the same reader as `file read`, so the payload's read.score.notes is what really landed. The "
                + "path is checked for an existing file before anything is sent, and the document is rebound to the "
                + "copy (File.SaveAs) - see read.save.document.",
        };
    }

    /// <summary>
    /// The note limit the CLI resolved for a read, as it reached the host. Only the plan needs it: the live read
    /// itself is handed the number by the CLI, and the rebind route carries its own options.
    /// </summary>
    private static int PlannedReadLimit(JsonObject args) => (int)Math.Clamp(
        NodeRead.Long(args, "readLimit") ?? ProjectFileView.DefaultNoteLimit,
        -1,
        100_000);

    /// <summary>
    /// What the planned live read would do, for the dry-run plan: one read-only command, no save and no path.
    /// </summary>
    /// <remarks>
    /// The number here is the one the CLI resolved and the note lines come from Dorico's own printer, so this route
    /// can narrow how many notes are listed and nothing else: a measure or a player filter belongs to the copy the
    /// rebind route saves, and both options are refused without --read-rebind.
    /// </remarks>
    private static JsonObject LiveReadPlan(int noteLimit) => new()
    {
        ["route"] = "live",
        ["command"] = "Script.RunScript",
        ["script"] = LiveRead.ScriptPath.Replace('\\', '/'),
        ["output"] = LiveRead.OutputPath.Replace('\\', '/'),
        ["saves"] = false,
        ["path"] = null,
        ["options"] = new JsonObject
        {
            // The script reads 0 as "every note", so the plan says that rather than repeating the number back.
            ["noteLimit"] = noteLimit <= 0 ? null : noteLimit,
        },
        ["note"] = "After the notes are entered, the CLI has Dorico run the read-only read script "
            + "(Script.RunScript, ShowConsole=false) and parses the file it writes: nothing is saved, no path is "
            + "chosen and the document is not rebound. What comes back is the text Dorico's own printer writes, so "
            + "read.source is 'live' and there is no measure or player to filter on.",
    };

    /// <summary>
    /// The read options the CLI resolved: how many notes, which measures, which player.
    /// </summary>
    private static ProjectFileView.Options ReadReadOptions(JsonObject? options) => new()
    {
        // -1 is "every note" and 0 is "the shape alone", so the clamp keeps both.
        NoteLimit = (int)Math.Clamp(NodeRead.Long(options, "noteLimit") ?? ProjectFileView.DefaultNoteLimit, -1, 100_000),
        FirstMeasure = NodeRead.Int(options, "firstMeasure"),
        LastMeasure = NodeRead.Int(options, "lastMeasure"),
        Player = NodeRead.Str(options, "player"),
    };

    /// <summary>
    /// Reports the note input mode the way an agent needs it: what it asked for, what was in force before, what
    /// is in force afterwards, and whether the CLI had to change anything.
    /// </summary>
    private static JsonObject ModeJson(string? requested, string? before, string? effective, bool changed)
    {
        var json = new JsonObject
        {
            ["requested"] = requested is null ? null : NoteMode.ShortName(requested),
            ["before"] = before,
            ["effective"] = effective,
            ["changed"] = changed,
        };

        if (NoteMode.Meaning(effective) is { } meaning)
        {
            json["meaning"] = meaning;
        }

        if (requested is null)
        {
            json["note"] = "No --mode was given, so the mode was left alone; 'effective' is what the status "
                + "reported at the end of the run.";
        }
        else if (!changed)
        {
            json["note"] = $"Dorico was already in {NoteMode.ShortName(requested)} mode, so {NoteMode.Command} was "
                + "not sent.";
        }

        return json;
    }

    private static JsonObject AckJson(
        string name,
        List<KeyValuePair<string, string>>? parameters,
        JsonObject ack,
        long milliseconds)
    {
        var entry = new JsonObject
        {
            ["name"] = name,
            ["ack"] = NodeRead.Str(ack, "code") ?? NodeRead.Str(ack, "message") ?? "unknown",
        };

        if (parameters is { Count: > 0 })
        {
            entry["parameters"] = ParametersJson(parameters);
        }

        var detail = NodeRead.Str(ack, "detail");

        if (detail is not null)
        {
            entry["detail"] = detail;
        }

        if (milliseconds > 0)
        {
            entry["ms"] = milliseconds;
        }

        return entry;
    }

    private static JsonArray StepsJson(List<Step> steps)
    {
        var json = new JsonArray();

        foreach (var step in steps)
        {
            var entry = new JsonObject { ["name"] = step.Name };

            if (step.Parameters is { Count: > 0 })
            {
                entry["parameters"] = ParametersJson(step.Parameters);
            }

            JsonArr.AddNode(json, entry);
        }

        return json;
    }

    private static JsonArray NotesJson(List<string> notes)
    {
        var json = new JsonArray();

        foreach (var note in notes)
        {
            JsonArr.AddText(json, note);
        }

        return json;
    }

    private static JsonArray ChordsJson(List<List<string>> events)
    {
        var json = new JsonArray();

        foreach (var group in events)
        {
            var entry = new JsonArray();

            foreach (var note in group)
            {
                JsonArr.AddText(entry, note);
            }

            JsonArr.AddNode(json, entry);
        }

        return json;
    }

    private static JsonArray StaffJson(List<string> staff)
    {
        var json = new JsonArray();

        foreach (var move in staff)
        {
            JsonArr.AddText(json, move);
        }

        return json;
    }

    /// <summary>
    /// Reads `chords`, the grouped form of `notes`: one inner array per rhythmic position, so an inner array of
    /// two or more names is one chord.
    /// </summary>
    private static List<List<string>> ReadChordGroups(JsonObject args, string key)
    {
        var groups = new List<List<string>>();
        var array = NodeRead.Arr(args, key);

        if (array is null)
        {
            return groups;
        }

        foreach (var item in array)
        {
            if (item is JsonArray inner)
            {
                var group = new List<string>();

                foreach (var note in inner)
                {
                    var text = NodeRead.Text(note);

                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        group.Add(text);
                    }
                }

                if (group.Count > 0)
                {
                    groups.Add(group);
                }

                continue;
            }

            // Tolerate a flat list of names under the same key.
            var single = NodeRead.Text(item);

            if (!string.IsNullOrWhiteSpace(single))
            {
                groups.Add([single]);
            }
        }

        return groups;
    }

    private static List<string> ReadStringArray(JsonObject args, string key)
    {
        var values = new List<string>();
        var array = NodeRead.Arr(args, key);

        if (array is null)
        {
            return values;
        }

        foreach (var item in array)
        {
            var text = NodeRead.Text(item);

            if (!string.IsNullOrWhiteSpace(text))
            {
                values.Add(text);
            }
        }

        return values;
    }

    private static string Invariant(int value) => value.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>One command of a sequence, with the parameters it takes.</summary>
    private sealed record Step(string Name, List<KeyValuePair<string, string>>? Parameters = null);

}