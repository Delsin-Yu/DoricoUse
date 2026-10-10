using System.CommandLine;
using System.Text.Json.Nodes;
using DoricoUse.Cli.Dorico;
using DoricoUse.Cli.Ipc;
using DoricoUse.Cli.Json;

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
                + "(or a Dorico name such as kQuaver).",
        };

        var grid = new Option<string?>("--grid")
        {
            Description = "Rhythm grid the caret snaps to, e.g. kSemiquaver. This is not the note value.",
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

            var durationValue = parse.GetValue(duration);

            if (!string.IsNullOrWhiteSpace(durationValue))
            {
                args["duration"] = durationValue;
            }

            var gridValue = parse.GetValue(grid);

            if (!string.IsNullOrWhiteSpace(gridValue))
            {
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

            return await HostClient.CallAsync(HostProtocol.OpNoteInsert, args, budget, CancellationToken.None);
        }));

        return command;
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
