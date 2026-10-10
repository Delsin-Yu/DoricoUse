using System.CommandLine;
using System.Text.Json.Nodes;
using DoricoUse.Cli.Dorico;
using DoricoUse.Cli.Ipc;
using DoricoUse.Cli.Views;

namespace DoricoUse.Cli.Commands;

/// <summary>
/// Getting the score out of Dorico as a file, which is the only way to read what a score actually contains.
/// </summary>
/// <remarks>
/// Two routes, and they are not the same kind of thing. <c>export text</c> is unattended: Dorico's own
/// <c>File.Export.Text</c> honours a <c>File=</c> path and a <c>Show=false</c> flag and writes its CSV about
/// 200 ms later. <c>export musicxml</c> is not: <c>File.Export?FilterID=MusicXMLExportFilter</c> opens Dorico's
/// FlowExportDialog and writes nothing until a human presses 确定. Both commands therefore read the folder back
/// and report the files that are really there, because Dorico answers <c>kOK</c> to an export it then refuses or
/// waits on.
/// </remarks>
internal static class ExportCommands
{
    public static Command Build()
    {
        var export = new Command("export", "Write the open score out as a file.");
        export.Subcommands.Add(BuildMusicXml());
        export.Subcommands.Add(BuildText());
        return export;
    }

    private static Command BuildMusicXml()
    {
        var path = new Option<string?>("--path")
        {
            Description = "Folder to write to. Dorico names the files itself, and its API cannot set the "
                + "destination, so this is the folder the export dialog must be pointed at.",
        };

        var wait = new Option<int>("--wait-ms")
        {
            Description = "How long to watch --path for the files, in milliseconds. 0 sends the export and "
                + "reports the open dialog without waiting.",
            DefaultValueFactory = _ => ExportView.DefaultMusicXmlWaitMs,
        };

        var timeout = CommandRoot.TimeoutOption();

        var command = new Command(
            "musicxml",
            "Export the open score as MusicXML (File.Export with Dorico's MusicXML filter). Dorico writes nothing "
            + "until its export dialog is confirmed, so the files are read back from disk.")
        {
            path,
            wait,
            timeout,
        };

        command.SetAction(parse => CommandRoot.RunAsync("export musicxml", async () =>
        {
            var directory = ExportView.ResolveDirectory(parse.GetValue(path));
            var waitedMs = Math.Clamp(parse.GetValue(wait), 0, 600_000);
            var before = ExportView.Snapshot(directory, ExportView.MusicXmlPatterns);

            var acknowledgement = await SendAsync(
                "File.Export",
                new JsonObject { ["FilterID"] = ExportView.MusicXmlFilterId },
                CommandRoot.Timeout(parse, timeout)).ConfigureAwait(false);

            var files = await ExportView.WaitAsync(
                    directory,
                    ExportView.MusicXmlPatterns,
                    before,
                    TimeSpan.FromMilliseconds(Math.Max(waitedMs, 250)),
                    CancellationToken.None)
                .ConfigureAwait(false);

            if (files.Count == 0)
            {
                throw new CliException("ExportNeedsConfirmation", ExportView.PendingMessage(directory, waitedMs), 2);
            }

            return ExportView.MusicXmlReport(acknowledgement, directory, waitedMs, files);
        }));

        return command;
    }

    private static Command BuildText()
    {
        var path = new Option<string?>("--path")
        {
            Description = "The CSV to write, e.g. exports/score.csv. Dorico forces the .csv extension, so "
                + "--path exports/score.txt still writes score.csv.",
        };

        var wait = new Option<int>("--wait-ms")
        {
            Description = "How long to wait for the file to appear, in milliseconds.",
            DefaultValueFactory = _ => ExportView.DefaultTextWaitMs,
        };

        var timeout = CommandRoot.TimeoutOption();

        var command = new Command(
            "text",
            "Export the score's text items (titles, text frames, lyrics and other text) as CSV. This is the one "
            + "Dorico export that needs no dialog.")
        {
            path,
            wait,
            timeout,
        };

        command.SetAction(parse => CommandRoot.RunAsync("export text", async () =>
        {
            var requested = ExportView.ResolveTextFile(parse.GetValue(path));
            var written = ExportView.TextOutputPath(requested);
            var waitedMs = Math.Clamp(parse.GetValue(wait), 0, 600_000);
            var before = ExportView.Snapshot(Path.GetDirectoryName(written) ?? ".", [Path.GetFileName(written)]);

            var acknowledgement = await SendAsync(
                ExportView.TextCommand,
                new JsonObject
                {
                    ["File"] = requested,
                    ["Show"] = "false",
                },
                CommandRoot.Timeout(parse, timeout)).ConfigureAwait(false);

            var files = await ExportView.WaitAsync(
                Path.GetDirectoryName(written) ?? ".",
                [Path.GetFileName(written)],
                before,
                TimeSpan.FromMilliseconds(Math.Max(waitedMs, 250)),
                CancellationToken.None).ConfigureAwait(false);

            if (files.Count == 0)
            {
                throw new CliException("ExportWroteNothing", ExportView.TextFailureMessage(written), 2);
            }

            return ExportView.TextReport(acknowledgement, requested, written, waitedMs, files[0]);
        }));

        return command;
    }

    /// <summary>
    /// Sends one command and returns the host's receipt for it. The status is deliberately not read back: a
    /// MusicXML export holds the command open until its dialog is confirmed, and reading the status afterwards
    /// would say nothing about the files.
    /// </summary>
    private static async Task<JsonObject> SendAsync(string name, JsonObject parameters, TimeSpan timeout)
    {
        var args = new JsonObject
        {
            ["name"] = name,
            ["parameters"] = parameters,
            ["waitMs"] = 0,
            ["verify"] = false,
        };

        var payload = await HostClient
            .CallAsync(HostProtocol.OpCommand, args, timeout, CancellationToken.None)
            .ConfigureAwait(false);

        // The host wraps a command answer as {"result":{name,parameters,ack,roundTripMs}}.
        return NodeRead.Obj(payload, "result") ?? payload;
    }
}
