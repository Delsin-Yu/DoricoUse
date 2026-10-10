using System.CommandLine;
using System.Text.Json.Nodes;
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
                + "and the caret moves on, so this writes a melodic line.",
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

        var command = new Command("insert", "Insert one or more notes, or a rest.")
        {
            pitch,
            octave,
            notes,
            rest,
            duration,
            grid,
            stay,
            dryRun,
            waitMs,
            timeout,
        };

        command.SetAction(parse => CommandRoot.RunAsync("note insert", async () =>
        {
            var single = parse.GetValue(pitch);
            var octaveValue = parse.GetValue(octave);
            var list = new JsonArray();

            if (!string.IsNullOrWhiteSpace(parse.GetValue(notes)))
            {
                foreach (var text in parse.GetValue(notes)!
                    .Split([' ', ',', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    JsonArr.AddText(list, text);
                }
            }
            else if (!string.IsNullOrWhiteSpace(single))
            {
                if (octaveValue is null)
                {
                    throw new CliException("InvalidArgument", "--pitch needs --octave (C4 is middle C).", 1);
                }

                JsonArr.AddText(list, $"{single}{octaveValue}");
            }

            var waitMsValue = Math.Clamp(parse.GetValue(waitMs), 0, 120_000);
            var timeoutValue = CommandRoot.Timeout(parse, timeout);

            var args = new JsonObject
            {
                ["notes"] = list,
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

            // Every step is a command of its own, so the host needs room for the whole sequence.
            var budget = (timeoutValue * 2)
                + TimeSpan.FromMilliseconds((list.Count * 3 + 4) * 5000d)
                + TimeSpan.FromMilliseconds(waitMsValue);

            return await HostClient.CallAsync(HostProtocol.OpNoteInsert, args, budget, CancellationToken.None);
        }));

        return command;
    }
}
