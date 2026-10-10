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
/// <para>
/// That difference is also what <c>--path</c> means in each: for the text export it is the file Dorico writes,
/// for MusicXML it can only be a folder to <b>watch</b>, since no client can set the dialog's destination. The
/// payload says which (<c>pathRole</c>) and the MusicXML command also reports the destination Dorico will open
/// at, read from its own <c>preferences.xml</c>, because that is the one thing an agent cannot get from the API.
/// </para>
/// </remarks>
internal static class ExportCommands
{
    /// <summary>How long a cheap status request may take before the session is called stalled.</summary>
    private static readonly TimeSpan QueueProbeTimeout = TimeSpan.FromMilliseconds(1500);

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
            Description = "A folder to WATCH for the files Dorico writes, not the destination: the Remote Control "
                + "API cannot set where File.Export writes, only the dialog's '...' picker can. Defaults to "
                + "Dorico's configured destination (defaultProjectPath in preferences.xml) when that folder "
                + "exists, else the current directory.",
        };

        var wait = new Option<int>("--wait-ms")
        {
            Description = "How long to watch --path for the files, in milliseconds. 0 reads the folder once and "
                + "reports what is there without waiting at all.",
            DefaultValueFactory = _ => ExportView.DefaultMusicXmlWaitMs,
        };

        var timeout = CommandRoot.TimeoutOption();

        var command = new Command(
            "musicxml",
            "Export the open score as MusicXML (File.Export with Dorico's MusicXML filter). Dorico writes nothing "
            + "until somebody presses 确定 in its export dialog, so the files are read back from disk and the "
            + "dialog's destination is reported.")
        {
            path,
            wait,
            timeout,
        };

        command.SetAction(parse => CommandRoot.RunAsync("export musicxml", async () =>
        {
            // Read before anything else: a missing preferences.xml must not cost an export, only detail.
            var preferences = ExportView.DoricoPreferences.Load();
            var (directory, pathSource) = ExportView.ResolveWatchDirectory(parse.GetValue(path), preferences);
            var waitedMs = Math.Clamp(parse.GetValue(wait), 0, 600_000);
            var before = ExportView.Snapshot(directory, ExportView.MusicXmlPatterns);

            var acknowledgement = await SendAsync(
                "File.Export",
                new JsonObject { ["FilterID"] = ExportView.MusicXmlFilterId },
                CommandRoot.Timeout(parse, timeout)).ConfigureAwait(false);

            var queue = await ProbeQueueAsync().ConfigureAwait(false);

            // --wait-ms 0 means look now: one reading, no delay, and the same honest answer.
            var watch = waitedMs == 0
                ? ExportView.CheckOnce(directory, ExportView.MusicXmlPatterns, before)
                : await ExportView.WaitAsync(
                        directory,
                        ExportView.MusicXmlPatterns,
                        before,
                        TimeSpan.FromMilliseconds(waitedMs),
                        CancellationToken.None)
                    .ConfigureAwait(false);

            if (watch.Files.Count == 0)
            {
                // Same shape as the success payload, so an agent reads one structure either way.
                var facts = ExportView.MusicXmlReport(acknowledgement, watch, directory, pathSource, preferences, queue);

                throw new CliException(
                    "ExportNeedsConfirmation",
                    ExportView.PendingMessage(directory, waitedMs, pathSource, preferences, queue),
                    2)
                {
                    Details = facts,
                };
            }

            return ExportView.MusicXmlReport(acknowledgement, watch, directory, pathSource, preferences, queue);
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
                TimeSpan.FromMilliseconds(waitedMs),
                CancellationToken.None).ConfigureAwait(false);

            if (files.Files.Count == 0)
            {
                throw new CliException("ExportWroteNothing", ExportView.TextFailureMessage(written), 2);
            }

            return ExportView.TextReport(acknowledgement, requested, written, waitedMs, files.Files[0]);
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

    /// <summary>
    /// Asks Dorico one cheap question and times the answer, to tell a stalled session from a working one.
    /// </summary>
    /// <remarks>
    /// This is the only stall signal the API can give, and it is a weak one: Dorico answers getstatus from its
    /// modal loop, so a MusicXML export's dialog does <b>not</b> stall it (86 ms measured with that dialog open,
    /// same fields as always), and <c>File.Export</c> answers kOK at posting time, before the command even runs.
    /// A "stalled" verdict therefore means the session itself is wedged or gone, never "no dialog is open", and
    /// "alive" does not mean the queue is moving. What the probe does buy is a named cause instead of a bare
    /// timeout on the next request the agent sends.
    /// </remarks>
    private static async Task<JsonObject> ProbeQueueAsync()
    {
        var started = System.Diagnostics.Stopwatch.GetTimestamp();

        try
        {
            await HostClient
                .CallAsync(HostProtocol.OpStatus, null, QueueProbeTimeout, CancellationToken.None)
                .ConfigureAwait(false);

            return new JsonObject
            {
                ["op"] = "status",
                ["answered"] = true,
                ["roundTripMs"] = CommandRoot.ElapsedMilliseconds(started),
                ["verdict"] = "alive",
                ["note"] = "Dorico answered a getstatus during this export, so the session itself is not stalled. "
                    + "This does NOT mean the export dialog is absent: Dorico answers getstatus from its modal "
                    + "loop, so an open dialog looks exactly like this.",
            };
        }
        catch (CliException ex) when (ex.Code is "Timeout" or "HostTimeout" or "HostGone" or "HostUnreachable")
        {
            return new JsonObject
            {
                ["op"] = "status",
                ["answered"] = false,
                ["timeoutMs"] = (long)QueueProbeTimeout.TotalMilliseconds,
                ["elapsedMs"] = CommandRoot.ElapsedMilliseconds(started),
                ["code"] = ex.Code,
                ["verdict"] = "stalled",
                ["note"] = $"A getstatus sent right after the export did not come back within "
                    + $"{QueueProbeTimeout.TotalMilliseconds:0} ms ({ex.Code}): Dorico is not answering cheap "
                    + "questions either, so its command queue is stalled. A modal dialog is the usual cause (this "
                    + "export's own dialog, or another one); a wedged or closing Dorico is the other.",
            };
        }
        catch (Exception ex)
        {
            // The probe is a courtesy and must never cost the caller the export's own answer.
            return new JsonObject
            {
                ["op"] = "status",
                ["answered"] = false,
                ["elapsedMs"] = CommandRoot.ElapsedMilliseconds(started),
                ["code"] = ex is CliException cli ? cli.Code : "RuntimeError",
                ["verdict"] = "unknown",
                ["note"] = $"The stall probe itself failed ({ex.Message}), which says nothing about the export; "
                    + "the receipt and the watched folder above are still the answer.",
            };
        }
    }
}
