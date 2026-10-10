using System.CommandLine;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;
using DoricoUse.Cli.Dorico;
using DoricoUse.Cli.Ipc;
using DoricoUse.Cli.Json;
using DoricoUse.Cli.Views;

namespace DoricoUse.Cli.Commands;

/// <summary>
/// Reading the live session. Everything here goes through the host, which owns the connection; `session probe` is
/// the exception and talks to Dorico directly, because it exists to measure the connection itself.
/// </summary>
internal static class SessionCommands
{
    public static Command Build()
    {
        var session = new Command("session", "Read the state of the running Dorico session.");
        session.Subcommands.Add(BuildStatus());
        session.Subcommands.Add(BuildInfo());
        session.Subcommands.Add(BuildConnect());
        session.Subcommands.Add(BuildEvents());
        session.Subcommands.Add(BuildProbe());
        session.Subcommands.Add(BuildRead());
        return session;
    }

    /// <summary>
    /// `session read`: make Dorico save a copy of the project it has OPEN, then decode that file.
    /// </summary>
    /// <remarks>
    /// This is the missing half of the loop. Dorico has no query for the note stream, so "did my edit land?" could
    /// only be answered by a human taking a screenshot; from here an agent edits, runs one command, and reads the
    /// notes out of the file Dorico itself just wrote. The route and its side effect are documented on
    /// <see cref="SaveCopy"/>.
    /// </remarks>
    private static Command BuildRead()
    {
        var to = new Option<string?>("--to")
        {
            Description = "The file to write the copy to. Default: a timestamped file in the system temporary "
                + "directory. An existing file is never overwritten.",
        };

        var limit = new Option<string>("--limit")
        {
            Description = "How many notes to return: a count, 'all' for every note, or 0 for the shape alone.",
            DefaultValueFactory = _ => ProjectFileView.DefaultNoteLimit.ToString(CultureInfo.InvariantCulture),
        };

        var measure = new Option<string?>("--measure")
        {
            Description = "Only notes in one measure (12) or a range (12-16).",
        };

        var player = new Option<string?>("--player")
        {
            Description = "Only notes of players, instruments or staves matching this glob, e.g. *Flute* or Violin.",
        };

        var timeout = CommandRoot.TimeoutOption();
        var expectScore = CommandRoot.ExpectScoreOption();

        var command = new Command(
            "read",
            "Save a copy of the OPEN project (File.SaveAs) and decode it: players, measures and the notes that are "
            + "really in the score now. It saves whichever document Dorico has active, and Dorico rebinds that "
            + "document to the copy — see the report's save.document.")
        {
            to,
            limit,
            measure,
            player,
            timeout,
            expectScore,
        };

        command.SetAction(parse => CommandRoot.RunAsync("session read", async () =>
        {
            var destination = SaveCopy.Resolve(parse.GetValue(to), DateTimeOffset.Now);
            SaveCopy.RefuseExisting(destination);

            var limits = ProjectFileView.ParseMeasures(parse.GetValue(measure));

            var options = new ProjectFileView.Options
            {
                NoteLimit = ProjectFileView.ParseLimit(parse.GetValue(limit)),
                FirstMeasure = limits?.First,
                LastMeasure = limits?.Last,
                Player = parse.GetValue(player),
            };

            var protocolTimeout = CommandRoot.Timeout(parse, timeout);
            var watchBudget = TimeSpan.FromMilliseconds(
                Math.Min(SaveCopy.DefaultWait.TotalMilliseconds, protocolTimeout.TotalMilliseconds));

            var acknowledgement = await SaveCopy
                .SaveAsync(destination, protocolTimeout, CancellationToken.None, parse.GetValue(expectScore))
                .ConfigureAwait(false);

            ProjectFile? file = null;
            SaveCopy.SavedFile saved = new(destination, 0, 0, Stable: false);
            CliException? unfinished = null;

            // Dorico answers kOK before it has written the last byte, so the file is watched until it stops growing.
            // A ZIP that is still being written reads as NotAContainer, which is the one failure worth retrying.
            for (var attempt = 1; attempt <= 2 && file is null; attempt++)
            {
                saved = await SaveCopy
                    .WaitForFileAsync(destination, watchBudget, CancellationToken.None)
                    .ConfigureAwait(false);

                if (saved.Bytes == 0)
                {
                    unfinished = new CliException(
                        "SaveWroteNothing",
                        $"File.SaveAs answered but no file appeared at '{destination}' within "
                        + $"{watchBudget.TotalSeconds:0.#} s. Check {SaveCopy.SaveAsCommand} in "
                        + "%APPDATA%\\Steinberg\\Dorico 6\\application.log: a command that waits on a dialog "
                        + "writes nothing.",
                        2);

                    break;
                }

                try
                {
                    file = ProjectFileReader.Open(destination);
                }
                catch (CliException ex) when (ex.Code is "NotAContainer" or "MalformedMember")
                {
                    unfinished = ex;
                }
            }

            if (file is null)
            {
                throw unfinished
                    ?? new CliException("SaveWroteNothing", $"'{destination}' could not be read back.", 2);
            }

            var payload = BuildReadPayload(file, options, acknowledgement, saved);

            var sideEffects = new JsonArray();
            JsonArr.AddText(sideEffects, SaveCopy.SaveDocumentNote);
            payload["sideEffects"] = sideEffects;

            // `session read` reports exactly what `file read` reports, so both can be read the same way.
            foreach (var (key, value) in ProjectFileView.Report(file, options))
            {
                payload[key] = value?.DeepClone();
            }

            return payload;
        }));

        return command;
    }

    /// <summary>What the save itself was: the command, its receipt, and the file it left on disk.</summary>
    private static JsonObject BuildReadPayload(
        ProjectFile file,
        ProjectFileView.Options options,
        JsonObject acknowledgement,
        SaveCopy.SavedFile saved)
    {
        var placed = new JsonObject
        {
            ["path"] = saved.Path,
            ["bytes"] = saved.Bytes,
            ["watchedMs"] = saved.WaitedMs,
            ["stoppedGrowing"] = saved.Stable,
        };

        var document = new JsonObject
        {
            ["repointed"] = true,
            ["now"] = saved.Path,
            ["evidence"] = "Dorico logs 'Saving document: <id> to <path>', the document window's title follows the "
                + "new file name, and the document's autosave and backup move with it.",
            ["note"] = SaveCopy.SaveDocumentNote,
        };

        var save = new JsonObject
        {
            ["command"] = SaveCopy.SaveAsCommand,
            ["ack"] = NodeRead.Str(acknowledgement, "ack"),
            ["file"] = placed,
            ["document"] = document,
            ["note"] = "kOK is a receipt; the note list below is decoded from the file that is really on disk.",
        };

        if (NodeRead.Obj(acknowledgement, "parameters") is { } parameters)
        {
            save["parameters"] = parameters.DeepClone();
        }

        if (NodeRead.Long(acknowledgement, "roundTripMs") is { } roundTripMs)
        {
            save["roundTripMs"] = roundTripMs;
        }

        var measures = options.FirstMeasure is { } first && options.LastMeasure is { } last
            ? first == last ? first.ToString(CultureInfo.InvariantCulture) : $"{first}-{last}"
            : null;

        return new JsonObject
        {
            ["save"] = save,
            ["decodedFrom"] = file.Path,
            ["options"] = new JsonObject
            {
                ["noteLimit"] = options.NoteLimit,
                ["measures"] = measures,
                ["player"] = options.Player,
            },
        };
    }

    private static Command BuildStatus()
    {
        var timeout = CommandRoot.TimeoutOption();
        var raw = CommandRoot.RawOption();

        var command = new Command(
            "status",
            "Score loaded, selection, note input, mode, playback. Ask for this after a command: it is the feedback.")
        {
            timeout,
            raw,
        };

        command.SetAction(parse => CommandRoot.RunAsync("session status", async () =>
        {
            var args = new JsonObject { ["raw"] = parse.GetValue(raw) };

            return await HostClient.CallAsync(
                HostProtocol.OpStatus,
                args,
                CommandRoot.Timeout(parse, timeout),
                CancellationToken.None);
        }));

        return command;
    }

    private static Command BuildInfo()
    {
        var timeout = CommandRoot.TimeoutOption();
        var raw = CommandRoot.RawOption();

        var command = new Command("info", "Which Dorico this is, and how the host reaches it.")
        {
            timeout,
            raw,
        };

        command.SetAction(parse => CommandRoot.RunAsync("session info", async () =>
        {
            var args = new JsonObject { ["raw"] = parse.GetValue(raw) };

            return await HostClient.CallAsync(
                HostProtocol.OpInfo,
                args,
                CommandRoot.Timeout(parse, timeout),
                CancellationToken.None);
        }));

        return command;
    }

    private static Command BuildConnect()
    {
        var timeout = CommandRoot.TimeoutOption(120000);
        var raw = CommandRoot.RawOption();
        var reset = new Option<bool>("--reset")
        {
            Description = "Forget the stored session token and let Dorico ask for authorization again.",
        };

        var command = new Command(
            "connect",
            "Open the session and store its token. The first connection under a client name needs a click on Allow "
            + "inside Dorico.")
        {
            timeout,
            raw,
            reset,
        };

        command.SetAction(parse => CommandRoot.RunAsync("session connect", async () =>
        {
            var args = new JsonObject { ["reset"] = parse.GetValue(reset) };

            return await HostClient.CallAsync(
                HostProtocol.OpConnect,
                args,
                CommandRoot.Timeout(parse, timeout),
                CancellationToken.None);
        }));

        return command;
    }

    private static Command BuildEvents()
    {
        var limit = new Option<int>("--limit")
        {
            Description = "How many of the most recent messages to return.",
            DefaultValueFactory = _ => 20,
        };

        var payload = new Option<bool>("--payload")
        {
            Description = "Include each message as Dorico sent it, not just its kind and time.",
        };

        var timeout = CommandRoot.TimeoutOption();

        var command = new Command(
            "events",
            "The messages Dorico pushed on its own (status patches, selection changes) since the host started.")
        {
            limit,
            payload,
            timeout,
        };

        command.SetAction(parse => CommandRoot.RunAsync("session events", async () =>
        {
            var args = new JsonObject
            {
                ["limit"] = Math.Clamp(parse.GetValue(limit), 1, 500),
                ["payload"] = parse.GetValue(payload),
            };

            return await HostClient.CallAsync(
                HostProtocol.OpEvents,
                args,
                CommandRoot.Timeout(parse, timeout),
                CancellationToken.None);
        }));

        return command;
    }

    private static Command BuildProbe()
    {
        var address = CommandRoot.AddressOption();
        var clientName = CommandRoot.ClientNameOption();
        var timeout = CommandRoot.TimeoutOption();
        var raw = CommandRoot.RawOption();

        var rounds = new Option<int>("--rounds")
        {
            Description = "How many connect/status/disconnect cycles to measure.",
            DefaultValueFactory = _ => 3,
        };

        var observe = new Option<int>("--observe-ms")
        {
            Description = "Stay connected this long after the last cycle and report the messages Dorico pushed unprompted.",
            DefaultValueFactory = _ => 1500,
        };

        var command = new Command(
            "probe",
            "Diagnostics for the connection itself: it talks to Dorico directly, not through the host.")
        {
            address,
            clientName,
            timeout,
            raw,
            rounds,
            observe,
        };

        command.SetAction(parse => CommandRoot.RunAsync("session probe", async () =>
        {
            var store = CommandRoot.LoadStore(parse, address, clientName);
            var wait = CommandRoot.Timeout(parse, timeout);
            var roundCount = Math.Clamp(parse.GetValue(rounds), 1, 10);
            var observeMs = Math.Clamp(parse.GetValue(observe), 0, 30000);
            var includeRaw = parse.GetValue(raw);

            var probe = new JsonObject
            {
                ["address"] = store.Address,
                ["clientName"] = store.ClientName,
                ["tokenStored"] = store.HasToken,
            };

            var findings = new JsonArray();
            var roundResults = new JsonArray();
            probe["rounds"] = roundResults;

            DoricoClient? observing = null;

            for (var round = 1; round <= roundCount; round++)
            {
                var result = new JsonObject { ["round"] = round };
                JsonArr.AddNode(roundResults, result);

                var connectStarted = Stopwatch.GetTimestamp();
                DoricoClient client;

                try
                {
                    client = await DoricoClient.ConnectAsync(
                        new DoricoConnectOptions(store, wait),
                        CancellationToken.None);
                }
                catch (Exception ex)
                {
                    result["ok"] = false;
                    result["connectMs"] = CommandRoot.ElapsedMilliseconds(connectStarted);
                    result["error"] = DescribeError(ex);
                    JsonArr.AddText(
                        findings,
                        $"round {round}: connect failed after {CommandRoot.ElapsedMilliseconds(connectStarted)} ms");
                    break;
                }

                result["ok"] = true;
                result["connectMs"] = CommandRoot.ElapsedMilliseconds(connectStarted);
                result["authorization"] = NodeRead.Str(client.Handshake, "authorization");
                result["code"] = NodeRead.Str(client.Handshake, "code");

                var statusStarted = Stopwatch.GetTimestamp();
                var status = await client.GetStatusAsync(wait, CancellationToken.None);
                result["statusMs"] = CommandRoot.ElapsedMilliseconds(statusStarted);
                result["statusKeys"] = status.Count;

                if (round == 1)
                {
                    probe["app"] = SessionView.App(await client.AppInfoAsync(wait, CancellationToken.None));
                    probe["status"] = BuildStatusReport(status, includeRaw);
                    probe["commands"] = await BuildCommandReportAsync(client, wait);
                }

                var disconnectStarted = Stopwatch.GetTimestamp();
                var closedByDorico = await client.Session.DisconnectAsync(wait, CancellationToken.None);
                result["disconnectMs"] = CommandRoot.ElapsedMilliseconds(disconnectStarted);
                result["closedByDorico"] = closedByDorico;

                if (round < roundCount)
                {
                    await client.DisposeAsync();
                }
                else
                {
                    observing = client;
                }
            }

            var authorizations = roundResults
                .OfType<JsonObject>()
                .Select(entry => NodeRead.Str(entry, "authorization"))
                .Where(value => value is not null)
                .ToList();

            var reuseRounds = authorizations.Count(value => value == "stored-token");
            var dialogRounds = authorizations.Count(value => value == "consent-dialog");
            var laterRounds = authorizations.Skip(1).ToList();

            probe["authorizationDialogRounds"] = dialogRounds;
            probe["tokenReusedAfterFirstConnect"] = laterRounds.Count > 0
                && laterRounds.TrueForAll(value => value == "stored-token");

            if (authorizations.Count > 0)
            {
                JsonArr.AddText(findings, reuseRounds == authorizations.Count
                    ? "the stored session token authorized every round, so no dialog appears after the first connect"
                    : $"the authorization dialog was needed on {dialogRounds} of {authorizations.Count} rounds");

                if (dialogRounds <= 1 && reuseRounds >= 1 && roundCount > 1)
                {
                    JsonArr.AddText(
                        findings,
                        "the session token survived disconnect, so a connection per invocation is safe");
                }
            }

            if (observing is not null)
            {
                if (observeMs > 0)
                {
                    await Task.Delay(observeMs);
                    var events = observing.Session.Events;

                    probe["unsolicited"] = new JsonObject
                    {
                        ["windowMs"] = observeMs,
                        ["counts"] = SessionView.EventCounts(events),
                        ["sample"] = Sample(events, includeRaw),
                    };

                    JsonArr.AddText(findings, events.Count > 0
                        ? $"Dorico pushed {events.Count} message(s) unprompted in {observeMs} ms"
                        : $"Dorico pushed nothing unprompted in {observeMs} ms of idle");
                }

                await observing.DisposeAsync();
            }

            probe["findings"] = findings;
            return new JsonObject { ["probe"] = probe };
        }));

        return command;
    }

    private static JsonObject BuildStatusReport(JsonObject status, bool includeRaw)
    {
        var report = new JsonObject
        {
            ["keyCount"] = status.Count,
            ["keys"] = Keys(status),
            ["view"] = SessionView.Status(status),
        };

        if (includeRaw)
        {
            report["raw"] = status.DeepClone();
        }

        return report;
    }

    private static async Task<JsonObject> BuildCommandReportAsync(DoricoClient client, TimeSpan wait)
    {
        var started = Stopwatch.GetTimestamp();
        var response = await client.GetCommandsAsync(wait, CancellationToken.None);
        var elapsed = CommandRoot.ElapsedMilliseconds(started);

        return CatalogueView.Report(response, elapsed);
    }

    private static JsonArray Sample(IReadOnlyList<JsonObject> events, bool includePayload)
    {
        var sample = new JsonArray();

        foreach (var evt in events.TakeLast(8))
        {
            JsonArr.AddNode(sample, includePayload
                ? evt.DeepClone()
                : new JsonObject
                {
                    ["kind"] = NodeRead.Str(evt, "kind"),
                    ["atUtc"] = NodeRead.Str(evt, "atUtc"),
                });
        }

        return sample;
    }

    private static JsonArray Keys(JsonObject obj)
    {
        var keys = new JsonArray();

        foreach (var (key, _) in obj)
        {
            JsonArr.AddText(keys, key);
        }

        return keys;
    }

    private static JsonObject DescribeError(Exception error) => new()
    {
        ["code"] = error is CliException cli ? cli.Code : error.GetType().Name,
        ["message"] = error.Message,
    };
}
