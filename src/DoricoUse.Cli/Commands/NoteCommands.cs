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
            Description = "After the notes are entered, prove them by reading the running document - the same live "
                + "read `session read` does by default: a read-only script, no save, no side effect, and the payload "
                + "carries read.source \"live\". Add --read-rebind for the exact data instead.",
        };

        var readRebind = new Option<bool>("--read-rebind")
        {
            Description = "--read proves the notes by reading the running document (a read-only script, no save, no "
                + "side effect); --read-rebind saves a copy instead, which gives the exact data but rebinds the open "
                + "document to that copy. Needs --read, and it is the route --read-to, --measure and --player "
                + "belong to.",
        };

        var readTo = new Option<string?>("--read-to")
        {
            Description = "Where the copy for --read --read-rebind goes. Default: a timestamped file in the system "
                + "temporary directory. An existing file is never overwritten, and the open document is rebound to "
                + "the copy (that is File.SaveAs; see read.save.document). The live read (the default --read) saves "
                + "nothing, so it takes no path and this option is refused without --read-rebind.",
        };

        var readLimit = new Option<string?>("--limit")
        {
            Description = "How many notes the read-back returns: a count, or 'all' for every note. Both routes take "
                + $"it, and the default is {ProjectFileView.DefaultNoteLimit}. On --read-rebind 0 means the shape "
                + "alone (a decoded file can describe a score without listing notes); the live read has no shape to "
                + "report, so it reads 0 as every note.",
        };

        var readMeasure = new Option<string?>("--measure")
        {
            Description = "Only read back notes in one measure (12) or a range (12-16). --read --read-rebind only: "
                + "the live read walks the streams Dorico prints and has no measure to filter on.",
        };

        var readPlayer = new Option<string?>("--player")
        {
            Description = "Only read back notes of players, instruments or staves matching this glob, e.g. "
                + "*Flute*. --read --read-rebind only: the live read walks the streams Dorico prints and cannot "
                + "name the player they belong to.",
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
            readRebind,
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
            var rebindRequested = parse.GetValue(readRebind);
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

            if (rebindRequested && !readRequested)
            {
                throw new CliException(
                    "InvalidArgument",
                    "--read-rebind says how --read reads, so it needs --read: add --read for the read-back, or drop "
                    + "--read-rebind for a read that saves nothing.",
                    1);
            }

            if (readRequested && !rebindRequested
                && (readToValue is not null || measureValue is not null || playerValue is not null))
            {
                throw new CliException(
                    "InvalidArgument",
                    "--read-to, --measure and --player describe the copy that --read --read-rebind saves, and the "
                    + "default --read saves no copy: it reads the running document with a read-only script, which "
                    + "can only return the note lines Dorico prints. Add --read-rebind to use them, or use "
                    + "`session read --rebind` for the exact data in a read of its own.",
                    1);
            }

            // The one resolved limit, because both routes read it the same way out of --limit and only differ in
            // what the number means to the reader behind them.
            var readNoteLimit = limitValue is null
                ? ProjectFileView.DefaultNoteLimit
                : ProjectFileView.ParseLimit(limitValue);

            string? readPath = null;
            JsonObject? readOptions = null;

            if (readRequested)
            {
                args["read"] = true;

                // The host carries the number only so its dry-run plan can say which route was chosen; the live
                // read itself gets it from the CLI, after this call returns.
                args["readLimit"] = readNoteLimit;
            }

            if (readRequested && rebindRequested)
            {
                // Resolved here so a bad path or an existing file is a usage error (exit 1) before anything is
                // written, and the host is handed a path that is already known to be free. Only this route does
                // that: the live read writes no copy, so it must not create a folder or refuse a file either.
                readPath = SaveCopy.Resolve(readToValue, DateTimeOffset.Now);
                SaveCopy.RefuseExisting(readPath);

                var measures = ProjectFileView.ParseMeasures(measureValue);
                readOptions = new JsonObject
                {
                    ["noteLimit"] = readNoteLimit,
                    ["player"] = playerValue,
                };

                if (measures is { } range)
                {
                    readOptions["firstMeasure"] = range.First;
                    readOptions["lastMeasure"] = range.Last;
                }

                args["readRebind"] = true;
                args["readPath"] = readPath;
                args["readOptions"] = readOptions;
            }

            var durationValue = parse.GetValue(duration);
            var gridValue = parse.GetValue(grid);
            var allowUnknown = parse.GetValue(allowUnknownEnum);

            // The two enum-valued arguments are checked here, before the host is asked to do anything: Dorico
            // accepts an unknown grid resolution and silently keeps the old one, and `--duration` reaches the
            // host's own note-value table, so a typo there should not need a round trip to be refused.
            var lookedAt = new List<(string Key, string Value, VocabularyVerdict Verdict)>();

            if (!string.IsNullOrWhiteSpace(durationValue))
            {
                // The parameter the value ends up in; the binary spells it out as
                // NoteInput.NoteValue?LogDuration=kCrotchet.
                EnumVocabulary.Refuse("--duration", "LogDuration", durationValue, allowUnknown);
                lookedAt.Add(("LogDuration", durationValue, EnumVocabulary.Check(durationValue)));
                args["duration"] = durationValue;
            }

            if (!string.IsNullOrWhiteSpace(gridValue))
            {
                EnumVocabulary.Refuse("--grid", "RhythmicGridResolutionValue", gridValue, allowUnknown);
                lookedAt.Add(("RhythmicGridResolutionValue", gridValue, EnumVocabulary.Check(gridValue)));
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
                // The rebind route adds the save and the decode, inside this same host call. The live route reads
                // nothing in the host - its read is a second exchange after this one returns - so this is headroom
                // rather than a need: the exchange is short, and the generous number only decides that a host which
                // is stuck is reported by the read's own wait (LiveRead.DefaultWait, in CompleteLiveReadAsync)
                // instead of by a pipe timeout in the middle of the note sequence.
                budget += rebindRequested
                    ? SaveCopy.DefaultWait + TimeSpan.FromSeconds(15)
                    : LiveRead.DefaultWait;
            }

            var payload = await HostClient
                .CallAsync(HostProtocol.OpNoteInsert, args, budget, CancellationToken.None)
                .ConfigureAwait(false);

            if (lookedAt.Count > 0)
            {
                payload["vocabulary"] = EnumVocabulary.Describe(lookedAt);
            }

            // A dry run sends and writes nothing, so there is no file to wait for: skip the read half instead of
            // blocking on a copy or a script output that will never appear.
            if (readRequested && !parse.GetValue(dryRun))
            {
                var protocolTimeout = CommandRoot.Timeout(parse, timeout);

                if (rebindRequested)
                {
                    await CompleteReadAsync(
                        payload,
                        readOptions!,
                        readPath!,
                        protocolTimeout,
                        CancellationToken.None).ConfigureAwait(false);
                }
                else
                {
                    await CompleteLiveReadAsync(
                        payload,
                        readNoteLimit,
                        parse.GetValue(expectScore),
                        protocolTimeout,
                        CancellationToken.None).ConfigureAwait(false);
                }
            }

            return payload;
        }));

        return command;
    }

    /// <summary>
    /// The second half of the default <c>--read</c>: read the document Dorico has open and put that report into the
    /// note payload. It is exactly what <c>session read</c> does by default (<see cref="LiveRead.RunAsync"/>), run
    /// after the notes instead of before them, so a live payload and a <c>session read</c> payload can be read the
    /// same way - <c>read.source</c> is <c>"live"</c> in both.
    /// </summary>
    /// <remarks>
    /// It runs in the CLI rather than inside the host's gated exchange on purpose: the notes are already entered and
    /// the read has no side effect to keep inside the same gate (nothing is saved, nothing is rebound), so handing
    /// the read script to Dorico is one more command like any other. <paramref name="expectScore"/> travels with
    /// that command, because the host's guard is what refuses a window that changed in between.
    /// </remarks>
    private static async Task CompleteLiveReadAsync(
        JsonObject payload,
        int noteLimit,
        int? expectScore,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        if (NodeRead.Obj(payload, "note") is not { } note)
        {
            return;
        }

        // The run's own wait for the script's output file is this number, so it must be at least
        // LiveRead.DefaultWait; there is no copy to watch and no decode to run, so nothing else on the route needs
        // budget.
        var readWait = timeout < LiveRead.DefaultWait ? LiveRead.DefaultWait : timeout;

        note["read"] = await LiveRead
            .RunAsync(readWait, noteLimit, cancellationToken, expectScore)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// The second half of <c>--read --read-rebind</c>: wait for the copy the host asked Dorico to write, decode it,
    /// and put the report into the same payload. This is the round trip <c>session read --rebind</c> performs, with
    /// the save already done inside the host, using the shared <see cref="SaveCopy"/> helpers and the shared decoder
    /// (<see cref="ProjectFileReader"/> plus <see cref="ProjectFileView"/>); nothing is re-implemented here.
    /// </summary>
    /// <remarks>
    /// It runs in the CLI process rather than in the host: the CLI is what waits for the copy to stop growing, so it
    /// is also what should read it, and the decode is the same call <c>file read</c> makes. That used to be a hard
    /// requirement — the view shared one node across calls, so a second <c>Report</c> inside the long-lived host threw
    /// "The node already has a parent." — and it is not any more: the view clones what it shares, and
    /// <c>ProjectFileCheck</c> builds the report three times in one process precisely so that stays true.
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
