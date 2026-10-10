using System.CommandLine;
using System.Globalization;
using System.Text.Json.Nodes;
using DoricoUse.Cli.Dorico;
using DoricoUse.Cli.Ipc;
using DoricoUse.Cli.Json;
using DoricoUse.Cli.Views;

namespace DoricoUse.Cli.Commands;

/// <summary>
/// Entering notes. Underneath it is the same command channel, but the order matters and one of the commands
/// (NoteInput.Enter) is a toggle, so the host decides what to send after reading the status.
/// </summary>
internal static class NoteCommands
{
    public static Command Build()
    {
        var note = new Command("note", "Enter notes and rests into the open project.");
        note.Subcommands.Add(BuildInsert());
        return note;
    }

    private static Command BuildInsert()
    {
        var pitch = new Option<string?>("--pitch")
        {
            Description = "One pitch name, e.g. F#, Bb or C.",
        };

        var octave = new Option<int?>("--octave")
        {
            Description = "Octave for --pitch, Dorico's numbering: C4 is middle C.",
        };

        var notes = new Option<string>("--notes")
        {
            Description = "Several pitches in one call, space separated, e.g. \"F#5 D5 Bb4\". Each pitch is entered "
                + "and the caret moves on, so this writes a melodic line. Join pitches with '+' to write a chord at "
                + "one rhythmic position instead, e.g. \"C4+E4+G4 D5\".",
        };

        var chords = new Option<string[]>("--chord")
        {
            Description = "One chord, given as the pitches it holds at one rhythmic position, e.g. "
                + "--chord \"C4 E4 G4\" (or C4,E4,G4). Repeat the option to write a sequence of chords. Use it "
                + "instead of --notes/--pitch.",
        };

        var tuplet = new Option<string?>("--tuplet")
        {
            Description = "Wrap the notes of this call in a tuplet, as a ratio of notes to the time they take: "
                + "--tuplet 3:2 is a triplet (or a name such as triplet or quintuplet).",
        };

        var staff = new Option<string[]>("--staff")
        {
            Description = "Move the caret to the stave above (up/top) or below (down/bottom) before entering the "
                + "notes; on a piano, down is the left hand. Repeat it to move twice.",
        };

        var rest = new Option<bool>("--rest")
        {
            Description = "Insert a rest at the caret instead of a note.",
        };

        var duration = new Option<string?>("--duration")
        {
            Description = "Note value for these notes: crotchet, quaver, minim, semibreve, dottedcrotchet, ... "
                + "(or a Dorico name such as kQuaver, which is checked against the vocabulary extracted from "
                + "Dorico's binary).",
        };

        var grid = new Option<string?>("--grid")
        {
            Description = "Rhythm grid the caret snaps to, e.g. kSemiquaver (checked against the vocabulary when "
                + "one has been extracted from Dorico's binary). This is not the note value.",
        };

        var mode = new Option<string?>("--mode")
        {
            Description = "What to do with the notes already at the caret: overwrite replaces them, which is what a "
                + "fresh project does anyway, and insert pushes them to the right. Changes Dorico's note input mode "
                + "(NoteInput.Mode) before the notes are written, and the payload reports the mode that was in "
                + "force. Leave the option out and the mode is not touched; the payload still reports it.",
        };

        var stay = new Option<bool>("--stay")
        {
            Description = "Leave note input on when finished, instead of returning to the state you started in.",
        };

        var dryRun = new Option<bool>("--dry-run")
        {
            Description = "Show the commands that would be sent, without sending them.",
        };

        var allowUnknownEnum = new Option<bool>("--allow-unknown-enum")
        {
            Description = "Pass --duration/--grid through even when the vocabulary extracted from Dorico's "
                + "binary does not hold the value. The vocabulary is a string scan of one build, so it can miss "
                + "a spelling that is real.",
        };

        var fromStart = new Option<bool>("--from-start")
        {
            Description = "Put the caret at the start of the flow before entering the notes. Dorico has no caret "
                + "query and no go-to-bar, so this is the only way to write from a known place: NoteInput.Exit "
                + "(when note input is on), Edit.SelectNone and NoteInput.Enter are sent first, because Enter puts "
                + "the caret at the top of the flow only when nothing is selected and it was off. Without it the "
                + "caret stays where the last write (yours or another agent's) left it.",
        };

        var read = new Option<bool>("--read")
        {
            Description = "After the notes are entered, make Dorico save a copy of the document (File.SaveAs, the "
                + "same round trip as `session read`) and decode it, so the payload carries the notes that are "
                + "really in the score. One call writes and proves it.",
        };

        var readTo = new Option<string?>("--read-to")
        {
            Description = "Where the copy for --read goes. Default: .spinney/read/<timestamp>.dorico. An existing "
                + "file is never overwritten, and the open document is rebound to the copy (that is File.SaveAs; see "
                + "read.save.document).",
        };

        var readLimit = new Option<string?>("--limit")
        {
            Description = "How many notes the read-back returns: a count, 'all' for every note, or 0 for the shape "
                + $"alone. Only used with --read; the default is {ProjectFileView.DefaultNoteLimit}.",
        };

        var readMeasure = new Option<string?>("--measure")
        {
            Description = "Only read back notes in one measure (12) or a range (12-16). Only used with --read.",
        };

        var readPlayer = new Option<string?>("--player")
        {
            Description = "Only read back notes of players, instruments or staves matching this glob, e.g. "
                + "*Flute*. Only used with --read.",
        };

        var waitMs = new Option<int>("--wait-ms")
        {
            Description = "Wait this long before reading the status back.",
            DefaultValueFactory = _ => 0,
        };

        var timeout = CommandRoot.TimeoutOption();
        var expectScore = CommandRoot.ExpectScoreOption();

        var command = new Command("insert", "Insert one or more notes, a chord, a tuplet, or a rest.")
        {
            pitch,
            octave,
            notes,
            chords,
            tuplet,
            staff,
            rest,
            duration,
            grid,
            mode,
            stay,
            dryRun,
            allowUnknownEnum,
            fromStart,
            read,
            readTo,
            readLimit,
            readMeasure,
            readPlayer,
            waitMs,
            timeout,
            expectScore,
        };

        command.SetAction(parse => CommandRoot.RunAsync("note insert", async () =>
        {
            var single = parse.GetValue(pitch);
            var octaveValue = parse.GetValue(octave);
            var list = new JsonArray();
            var groups = new JsonArray();
            var notesValue = parse.GetValue(notes);
            var chordValues = ChordOccurrences(parse, chords);
            var hasNotes = !string.IsNullOrWhiteSpace(notesValue);
            var hasPitch = !string.IsNullOrWhiteSpace(single);

            if (hasNotes && chordValues.Count > 0)
            {
                throw new CliException(
                    "InvalidArgument",
                    "--notes and --chord were both given, and their order would be guesswork. Use --notes with "
                    + $"'{NoteName.ChordSeparator}' to mix chords and single notes, e.g. \"C4+E4+G4 D5\".",
                    1);
            }

            if (chordValues.Count > 0 && hasPitch)
            {
                throw new CliException(
                    "InvalidArgument",
                    "--chord and --pitch were both given. Give every pitch of the chord to --chord, or write the "
                    + "whole line with --notes.",
                    1);
            }

            // --notes wins over --pitch, which is the order this command has always resolved them in.
            if (hasNotes)
            {
                foreach (var text in notesValue!
                    .Split([' ', ',', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    AddGroup(list, groups, text);
                }
            }
            else if (chordValues.Count > 0)
            {
                foreach (var chord in chordValues)
                {
                    AddGroup(list, groups, string.Join(NoteName.ChordSeparator, chord));
                }
            }
            else if (hasPitch)
            {
                if (octaveValue is null)
                {
                    throw new CliException("InvalidArgument", "--pitch needs --octave (C4 is middle C).", 1);
                }

                AddGroup(list, groups, $"{single}{octaveValue}");
            }

            if (groups.Count == 0 && !parse.GetValue(rest))
            {
                throw new CliException(
                    "InvalidArgument",
                    "note insert needs notes (--notes, --chord or --pitch/--octave) or --rest.",
                    1);
            }

            var waitMsValue = Math.Clamp(parse.GetValue(waitMs), 0, 120_000);
            var timeoutValue = CommandRoot.Timeout(parse, timeout);

            var args = new JsonObject
            {
                ["notes"] = list,
                ["chords"] = groups,
                ["rest"] = parse.GetValue(rest),
                ["exit"] = !parse.GetValue(stay),
                ["dryRun"] = parse.GetValue(dryRun),
                ["waitMs"] = waitMsValue,
                ["verify"] = true,
            };

            if (parse.GetValue(fromStart))
            {
                args["fromStart"] = true;
            }

            var readRequested = parse.GetValue(read);
            var readToValue = parse.GetValue(readTo);
            var measureValue = parse.GetValue(readMeasure);
            var playerValue = parse.GetValue(readPlayer);
            var limitValue = parse.GetValue(readLimit);

            if (!readRequested && (readToValue is not null || measureValue is not null || playerValue is not null
                || limitValue is not null))
            {
                throw new CliException(
                    "InvalidArgument",
                    "--read-to, --limit, --measure and --player describe what --read returns. Add --read to write "
                    + "and read back in one call, or use `session read` on the project afterwards.",
                    1);
            }

            string? readPath = null;
            JsonObject? readOptions = null;

            if (readRequested)
            {
                // Resolved here so a bad path or an existing file is a usage error (exit 1) before anything is
                // written, and the host is handed a path that is already known to be free.
                readPath = SaveCopy.Resolve(readToValue, DateTimeOffset.Now);
                SaveCopy.RefuseExisting(readPath);

                var measures = ProjectFileView.ParseMeasures(measureValue);
                readOptions = new JsonObject
                {
                    ["noteLimit"] = limitValue is null
                        ? ProjectFileView.DefaultNoteLimit
                        : ProjectFileView.ParseLimit(limitValue),
                    ["player"] = playerValue,
                };

                if (measures is { } range)
                {
                    readOptions["firstMeasure"] = range.First;
                    readOptions["lastMeasure"] = range.Last;
                }

                args["read"] = true;
                args["readPath"] = readPath;
                args["readOptions"] = readOptions;
            }

            var durationValue = parse.GetValue(duration);
            var gridValue = parse.GetValue(grid);
            var allowUnknown = parse.GetValue(allowUnknownEnum);

            // The two enum-valued arguments are checked here, before the host is asked to do anything: Dorico
            // accepts an unknown grid resolution and silently keeps the old one, and `--duration` reaches the
            // host's own note-value table, so a typo there should not need a round trip to be refused.
            var vocabulary = EnumVocabulary.Shared;
            var lookedAt = new List<(string Key, string Value, VocabularyVerdict Verdict)>();

            if (!string.IsNullOrWhiteSpace(durationValue))
            {
                // The parameter the value ends up in; the binary spells it out as
                // NoteInput.NoteValue?LogDuration=kCrotchet.
                vocabulary.Refuse("--duration", "LogDuration", durationValue, allowUnknown);
                lookedAt.Add(("LogDuration", durationValue, vocabulary.Check(durationValue)));
                args["duration"] = durationValue;
            }

            if (!string.IsNullOrWhiteSpace(gridValue))
            {
                vocabulary.Refuse("--grid", "RhythmicGridResolutionValue", gridValue, allowUnknown);
                lookedAt.Add(("RhythmicGridResolutionValue", gridValue, vocabulary.Check(gridValue)));
                args["grid"] = gridValue;
            }

            CommandRoot.AddExpectScore(args, parse.GetValue(expectScore));

            var modeValue = parse.GetValue(mode);

            if (!string.IsNullOrWhiteSpace(modeValue))
            {
                // Resolved here so a typo is a usage error with exit code 1, not a protocol round trip.
                if (!NoteMode.TryResolve(modeValue, out var resolvedMode))
                {
                    throw new CliException(
                        "InvalidArgument",
                        $"'{modeValue}' is not a note input mode. Use {NoteMode.Names()}.",
                        1);
                }

                args["mode"] = NoteMode.ShortName(resolvedMode);
            }

            var tupletValue = parse.GetValue(tuplet);

            if (!string.IsNullOrWhiteSpace(tupletValue))
            {
                args["tuplet"] = tupletValue;
            }

            var staffValue = parse.GetValue(staff) ?? [];

            if (staffValue.Length > 0)
            {
                var moves = new JsonArray();

                foreach (var move in staffValue)
                {
                    JsonArr.AddText(moves, move);
                }

                args["staff"] = moves;
            }

            // Every step is a command of its own, so the host needs room for the whole sequence.
            var budget = (timeoutValue * 2)
                + TimeSpan.FromMilliseconds((list.Count * 3 + staffValue.Length + 5) * 5000d)
                + TimeSpan.FromMilliseconds(waitMsValue);

            if (readRequested)
            {
                // The read-back adds the save and the decode, inside the same host call.
                budget += SaveCopy.DefaultWait + TimeSpan.FromSeconds(15);
            }

            var payload = await HostClient
                .CallAsync(HostProtocol.OpNoteInsert, args, budget, CancellationToken.None)
                .ConfigureAwait(false);

            if (lookedAt.Count > 0)
            {
                payload["vocabulary"] = vocabulary.Describe(lookedAt);
            }

            // A dry run sends and writes nothing, so there is no file to wait for: skip the read half instead of
            // blocking on a copy that will never appear.
            if (readRequested && !parse.GetValue(dryRun))
            {
                await CompleteReadAsync(
                    payload,
                    readOptions!,
                    readPath!,
                    CommandRoot.Timeout(parse, timeout),
                    CancellationToken.None).ConfigureAwait(false);
            }

            return payload;
        }));

        return command;
    }

    /// <summary>
    /// The second half of <c>--read</c>: wait for the copy the host asked Dorico to write, decode it, and put the
    /// report into the same payload. This is the round trip <c>session read</c> performs, with the save already
    /// done inside the host, using the shared <see cref="SaveCopy"/> helpers and the shared decoder
    /// (<see cref="ProjectFileReader"/> plus <see cref="ProjectFileView"/>); nothing is re-implemented here.
    /// </summary>
    /// <remarks>
    /// It runs in the CLI process rather than in the host on purpose: the report is built once per process, and the
    /// long-lived host would call it once per invocation (the view reuses a node, so a second call inside one
    /// process throws "The node already has a parent."). A one-shot process has that property for free.
    /// </remarks>
    private static async Task CompleteReadAsync(
        JsonObject payload,
        JsonObject readOptions,
        string path,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        if (NodeRead.Obj(NodeRead.Obj(payload, "note"), "read") is not { } read)
        {
            return;
        }

        var dimensions = NodeRead.Obj(read, "options");
        var options = new ProjectFileView.Options
        {
            NoteLimit = (int)Math.Clamp(
                NodeRead.Long(readOptions, "noteLimit") ?? ProjectFileView.DefaultNoteLimit,
                -1,
                100_000),
            FirstMeasure = NodeRead.Int(readOptions, "firstMeasure"),
            LastMeasure = NodeRead.Int(readOptions, "lastMeasure"),
            Player = NodeRead.Str(readOptions, "player"),
        };

        var watchBudget = TimeSpan.FromMilliseconds(
            Math.Min(SaveCopy.DefaultWait.TotalMilliseconds, timeout.TotalMilliseconds));

        ProjectFile? file = null;
        SaveCopy.SavedFile saved = new(path, 0, 0, Stable: false);
        CliException? unfinished = null;

        // Dorico answered before it wrote the last byte, so the file is watched until it stops growing; a ZIP that
        // is still being written reads as NotAContainer, which is the one failure worth retrying.
        for (var attempt = 1; attempt <= 2 && file is null; attempt++)
        {
            saved = await SaveCopy.WaitForFileAsync(path, watchBudget, cancellationToken).ConfigureAwait(false);

            if (saved.Bytes == 0)
            {
                unfinished = new CliException(
                    "SaveWroteNothing",
                    $"The notes were entered, but the copy {SaveCopy.SaveAsCommand} was asked for never appeared at "
                    + $"'{path}' within {watchBudget.TotalSeconds:0.#} s, so the result could not be read back. Check "
                    + $"{SaveCopy.SaveAsCommand} in %APPDATA%\\Steinberg\\Dorico 6\\application.log: a command that "
                    + "waits on a dialog writes nothing.",
                    2);

                break;
            }

            try
            {
                file = ProjectFileReader.Open(path);
            }
            catch (CliException ex) when (ex.Code is "NotAContainer" or "MalformedMember")
            {
                unfinished = ex;
            }
        }

        if (file is null)
        {
            throw unfinished ?? new CliException("SaveWroteNothing", $"'{path}' could not be read back.", 2);
        }

        if (NodeRead.Obj(read, "save") is { } save)
        {
            save["file"] = new JsonObject
            {
                ["path"] = saved.Path,
                ["bytes"] = saved.Bytes,
                ["watchedMs"] = saved.WaitedMs,
                ["stoppedGrowing"] = saved.Stable,
            };
        }

        var measures = options.FirstMeasure is { } first && options.LastMeasure is { } last
            ? first == last
                ? first.ToString(CultureInfo.InvariantCulture)
                : $"{first}-{last}"
            : dimensions is null ? null : NodeRead.Str(dimensions, "measures");

        read["options"] = new JsonObject
        {
            ["noteLimit"] = options.NoteLimit,
            ["measures"] = measures,
            ["player"] = options.Player,
        };

        read["decodedFrom"] = file.Path;

        // Exactly what `file read` reports, so both can be read the same way.
        foreach (var (key, value) in ProjectFileView.Report(file, options))
        {
            read[key] = value?.DeepClone();
        }
    }

    /// <summary>
    /// Reads --chord as one chord per option value: <c>--chord "C4 E4 G4"</c> (or <c>--chord C4,E4,G4</c>) is one
    /// chord, and repeating the option writes a sequence of them.
    /// </summary>
    private static List<List<string>> ChordOccurrences(ParseResult parse, Option<string[]> chords)
    {
        var occurrences = new List<List<string>>();

        foreach (var value in parse.GetValue(chords) ?? [])
        {
            var members = value
                .Split([' ', ',', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToList();

            if (members.Count > 0)
            {
                occurrences.Add(members);
            }
        }

        return occurrences;
    }

    /// <summary>
    /// Adds one group of pitches: the flat name list keeps the original payload shape, and the group list is what
    /// the host reads, so a chord is one entry of two or more names.
    /// </summary>
    private static void AddGroup(JsonArray? list, JsonArray groups, string text)
    {
        if (!NoteName.TryParseGroup(text, out var members))
        {
            throw new CliException(
                "InvalidArgument",
                $"Could not read the note name '{text}'. Write it like F#5, Bb4 or C4, "
                + $"and join a chord with '{NoteName.ChordSeparator}'.",
                1);
        }

        var group = new JsonArray();

        foreach (var member in members)
        {
            JsonArr.AddText(group, member);

            if (list is not null)
            {
                JsonArr.AddText(list, member);
            }
        }

        JsonArr.AddNode(groups, group);
    }
}
