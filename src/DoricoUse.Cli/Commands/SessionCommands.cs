using System.CommandLine;
using System.Diagnostics;
using System.Text.Json.Nodes;
using DoricoUse.Cli.Dorico;
using DoricoUse.Cli.Json;
using DoricoUse.Cli.Views;

namespace DoricoUse.Cli.Commands;

/// <summary>Connect to Dorico, read the state of the running session, and check that the environment works.</summary>
internal static class SessionCommands
{
    public static Command Build()
    {
        var session = new Command("session", "Connect to Dorico and read the state of the running session.");
        session.Subcommands.Add(BuildStatus());
        session.Subcommands.Add(BuildConnect());
        session.Subcommands.Add(BuildInfo());
        session.Subcommands.Add(BuildProbe());
        return session;
    }

    private static Command BuildStatus()
    {
        var address = CommandRoot.AddressOption();
        var clientName = CommandRoot.ClientNameOption();
        var timeout = CommandRoot.TimeoutOption();
        var raw = CommandRoot.RawOption();

        var command = new Command(
            "status",
            "Current Dorico state: score loaded, selection, note input, mode, playback.")
        {
            address,
            clientName,
            timeout,
            raw,
        };

        command.SetAction(parse => CommandRoot.RunAsync("session status", async () =>
        {
            var store = CommandRoot.LoadStore(parse, address, clientName);
            var wait = CommandRoot.Timeout(parse, timeout);

            await using var client = await DoricoClient.ConnectAsync(
                new DoricoConnectOptions(store, wait),
                CancellationToken.None);

            var status = await client.GetStatusAsync(wait, CancellationToken.None);

            var payload = new JsonObject
            {
                ["session"] = client.Handshake.DeepClone(),
                ["status"] = SessionView.Status(status),
            };

            if (parse.GetValue(raw))
            {
                payload["raw"] = status.DeepClone();
            }

            return payload;
        }));

        return command;
    }

    private static Command BuildConnect()
    {
        var address = CommandRoot.AddressOption();
        var clientName = CommandRoot.ClientNameOption();
        var timeout = CommandRoot.TimeoutOption(120000);
        var raw = CommandRoot.RawOption();
        var reset = new Option<bool>("--reset")
        {
            Description = "Forget the stored session token and let Dorico ask for authorization again.",
        };

        var command = new Command(
            "connect",
            "Open a session and store its session token. The first connection needs a click on Allow inside Dorico.")
        {
            address,
            clientName,
            timeout,
            raw,
            reset,
        };

        command.SetAction(parse => CommandRoot.RunAsync("session connect", async () =>
        {
            var store = CommandRoot.LoadStore(parse, address, clientName);
            var wait = CommandRoot.Timeout(parse, timeout);
            var resetToken = parse.GetValue(reset);

            await using var client = await DoricoClient.ConnectAsync(
                new DoricoConnectOptions(store, wait, resetToken),
                CancellationToken.None);

            var app = await client.AppInfoAsync(wait, CancellationToken.None);

            var payload = new JsonObject
            {
                ["session"] = client.Handshake.DeepClone(),
                ["app"] = SessionView.App(app),
                ["storePath"] = SessionStore.FilePath,
            };

            if (NodeRead.Str(client.Handshake, "authorization") == "consent-dialog")
            {
                payload["hint"] = "Dorico asked for authorization; the dialog is closed now that the token is stored.";
            }

            if (parse.GetValue(raw))
            {
                payload["raw"] = app.DeepClone();
            }

            return payload;
        }));

        return command;
    }

    private static Command BuildInfo()
    {
        var address = CommandRoot.AddressOption();
        var clientName = CommandRoot.ClientNameOption();
        var timeout = CommandRoot.TimeoutOption();
        var raw = CommandRoot.RawOption();

        var command = new Command("info", "Which Dorico this is, and how the CLI is configured to reach it.")
        {
            address,
            clientName,
            timeout,
            raw,
        };

        command.SetAction(parse => CommandRoot.RunAsync("session info", async () =>
        {
            var store = CommandRoot.LoadStore(parse, address, clientName);
            var wait = CommandRoot.Timeout(parse, timeout);

            await using var client = await DoricoClient.ConnectAsync(
                new DoricoConnectOptions(store, wait),
                CancellationToken.None);

            var app = await client.AppInfoAsync(wait, CancellationToken.None);

            var payload = new JsonObject
            {
                ["app"] = SessionView.App(app),
                ["session"] = client.Handshake.DeepClone(),
                ["storePath"] = SessionStore.FilePath,
            };

            if (parse.GetValue(raw))
            {
                payload["raw"] = app.DeepClone();
            }

            return payload;
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
            "Diagnostics: time the handshake, check that the stored session token is reused, count the command "
            + "catalogue, and watch for unsolicited messages.")
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
                    JsonArr.AddText(findings, $"round {round}: connect failed after {CommandRoot.ElapsedMilliseconds(connectStarted)} ms");
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
                        "the session token survived disconnect, so one invocation can connect and quit freely");
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

        var list = NodeRead.Arr(response, "commands");
        var report = new JsonObject
        {
            ["fetchMs"] = elapsed,
            ["count"] = list?.Count ?? 0,
        };

        if (list is not null && list.Count > 0)
        {
            report["families"] = CommandFamilies(list);
            report["first"] = list[0]?.DeepClone();
        }

        return report;
    }

    private static JsonObject CommandFamilies(JsonArray commands)
    {
        var families = new JsonObject();

        foreach (var command in commands)
        {
            var name = NodeRead.Str(command, "name");

            if (name is null)
            {
                continue;
            }

            var dot = name.IndexOf('.', StringComparison.Ordinal);
            var family = dot > 0 ? name[..dot] : name;
            families[family] = (NodeRead.Int(families, family) ?? 0) + 1;
        }

        return families;
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
                    ["atMs"] = NodeRead.Long(evt, "atMs"),
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
