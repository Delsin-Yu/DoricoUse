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

/// <param name="Address">Remote Control address the host connects to.</param>
/// <param name="ClientName">Client name Dorico authorizes.</param>
/// <param name="Verbose">Also echo the host log to stderr, for a human running it in a terminal.</param>
internal sealed record HostOptions(string Address, string ClientName, bool Verbose);

/// <summary>
/// The long-lived process: it holds the Dorico connection and answers CLI requests over a named pipe.
/// </summary>
/// <remarks>
/// It never stops on its own — the connection has to stay open for the unsolicited status and selection stream to
/// be collected. `host stop` (or `host serve --stop`) is the only way it goes away.
/// </remarks>
internal sealed partial class HostServer(HostOptions options, HostLog log)
{
    private const int MaxParameterLength = 100_000;

    private readonly SessionKeeper _keeper = new(options.Address, options.ClientName);
    private readonly HostRuntimeFile _runtime = new()
    {
        Pid = Environment.ProcessId,
        Pipe = HostProtocol.PipeName,
        Address = options.Address,
        ClientName = options.ClientName,
        StartedAt = DateTimeOffset.UtcNow,
    };

    private readonly CancellationTokenSource _cts = new();
    private readonly Stopwatch _uptime = Stopwatch.StartNew();

    private int _connections;

    public async Task<int> RunAsync()
    {
        _runtime.Write();
        log.Write($"listening on \\\\.\\pipe\\{HostProtocol.PipeName} as pid {Environment.ProcessId}; "
            + $"Dorico at {options.Address} as '{options.ClientName}'");

        if (!SessionStore.PersistsToken)
        {
            // The one thing that explains a dialog this user will see again: the file cannot authorize anything, so
            // the token lives and dies with this process. Measured from the build's own constant, not from a guess.
            log.Write("this build does not persist the session token; only this process holds it, so Dorico asks the "
                + "user to allow this client again after the host restarts");
        }

        try
        {
            while (!_cts.IsCancellationRequested)
            {
                NamedPipeServerStream pipe;

                try
                {
                    pipe = new NamedPipeServerStream(
                        HostProtocol.PipeName,
                        PipeDirection.InOut,
                        NamedPipeServerStream.MaxAllowedServerInstances,
                        PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous);
                }
                catch (IOException ex)
                {
                    log.Write($"could not open the pipe: {ex.Message}");
                    return 2;
                }

                try
                {
                    await pipe.WaitForConnectionAsync(_cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    await pipe.DisposeAsync().ConfigureAwait(false);
                    break;
                }

                _ = Task.Run(() => HandleAsync(pipe), CancellationToken.None);
            }
        }
        finally
        {
            try
            {
                await _keeper.StopAsync().WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                log.Write($"disconnect on shutdown failed: {ex.Message}");
            }

            HostRuntimeFile.Delete();
            log.Write("stopped");
        }

        return 0;
    }

    private async Task HandleAsync(NamedPipeServerStream pipe)
    {
        Interlocked.Increment(ref _connections);

        try
        {
            using var reader = new StreamReader(pipe, new UTF8Encoding(false), false, 4096, leaveOpen: true);
            using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, leaveOpen: true)
            {
                AutoFlush = true,
                NewLine = "\n",
            };

            using var readTimeout = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
            readTimeout.CancelAfter(TimeSpan.FromSeconds(30));

            var line = await reader.ReadLineAsync(readTimeout.Token).ConfigureAwait(false);

            if (line is null)
            {
                return;
            }

            var (response, stop) = await DispatchAsync(line).ConfigureAwait(false);
            await writer.WriteLineAsync(response.ToJsonString()).ConfigureAwait(false);

            if (stop)
            {
                _cts.Cancel();
            }
        }
        catch (OperationCanceledException)
        {
            // Client hung up, or the host is stopping.
        }
        catch (IOException ex)
        {
            log.Write($"pipe failed: {ex.Message}");
        }
        finally
        {
            Interlocked.Decrement(ref _connections);

            try
            {
                await pipe.DisposeAsync().ConfigureAwait(false);
            }
            catch (IOException)
            {
                // Already gone.
            }
        }
    }

    private async Task<(JsonObject Response, bool Stop)> DispatchAsync(string line)
    {
        JsonObject? request;

        try
        {
            request = JsonNode.Parse(line) as JsonObject;
        }
        catch (JsonException ex)
        {
            return (HostProtocol.Error("InvalidRequest", ex.Message, 1), false);
        }

        if (request is null)
        {
            return (HostProtocol.Error("InvalidRequest", "The request was not a JSON object.", 1), false);
        }

        var op = NodeRead.Str(request, "op") ?? string.Empty;
        var args = NodeRead.Obj(request, "args") ?? [];
        var timeout = TimeSpan.FromMilliseconds(
            Math.Clamp(NodeRead.Long(request, "timeoutMs") ?? 15000, 250, 600_000));

        log.Write($"op {op}");

        try
        {
            return op switch
            {
                HostProtocol.OpPing => (HostProtocol.Ok(BuildHostPayload()), false),
                HostProtocol.OpStatus => (HostProtocol.Ok(await StatusAsync(args, timeout).ConfigureAwait(false)), false),
                HostProtocol.OpInfo => (HostProtocol.Ok(await InfoAsync(args, timeout).ConfigureAwait(false)), false),
                HostProtocol.OpConnect => (HostProtocol.Ok(await ConnectAsync(args, timeout).ConfigureAwait(false)), false),
                HostProtocol.OpCommands => (HostProtocol.Ok(await CommandsAsync(args, timeout).ConfigureAwait(false)), false),
                HostProtocol.OpCommand => (HostProtocol.Ok(await CommandAsync(args, timeout).ConfigureAwait(false)), false),
                HostProtocol.OpSequence => (HostProtocol.Ok(await SequenceAsync(args, timeout).ConfigureAwait(false)), false),
                HostProtocol.OpNoteInsert => (HostProtocol.Ok(await NoteInsertAsync(args, timeout).ConfigureAwait(false)), false),
                HostProtocol.OpEvents => (HostProtocol.Ok(Events(args)), false),
                HostProtocol.OpFlows => (HostProtocol.Ok(await FlowsAsync(timeout).ConfigureAwait(false)), false),
                HostProtocol.OpLayouts => (HostProtocol.Ok(await LayoutsAsync(args, timeout).ConfigureAwait(false)), false),
                HostProtocol.OpOptionsGet => (HostProtocol.Ok(await OptionsGetAsync(args, timeout).ConfigureAwait(false)), false),
                HostProtocol.OpOptionsSet => (HostProtocol.Ok(await OptionsSetAsync(args, timeout).ConfigureAwait(false)), false),
                HostProtocol.OpProperties => (HostProtocol.Ok(await PropertiesAsync(timeout).ConfigureAwait(false)), false),
                HostProtocol.OpTechniques => (HostProtocol.Ok(await TechniquesAsync(timeout).ConfigureAwait(false)), false),
                HostProtocol.OpLibraryCollections => (HostProtocol.Ok(await LibraryCollectionsAsync(timeout).ConfigureAwait(false)), false),
                HostProtocol.OpLibraryEntities => (HostProtocol.Ok(await LibraryEntitiesAsync(args, timeout).ConfigureAwait(false)), false),
                HostProtocol.OpShutdown => (HostProtocol.Ok(new JsonObject { ["stopping"] = true }), true),
                _ => (HostProtocol.Error("UnknownOp", $"Unknown host operation '{op}'.", 1), false),
            };
        }
        catch (CliException ex)
        {
            return (HostProtocol.Error(ex.Code, ex.Message, ex.ExitCode, ex.Details), false);
        }
        catch (Exception ex)
        {
            log.Write($"op {op} failed: {ex}");
            return (HostProtocol.Error("HostError", ex.Message), false);
        }
    }

    private async Task<JsonObject> StatusAsync(JsonObject args, TimeSpan timeout)
    {
        var includeRaw = NodeRead.Bool(args, "raw") == true;

        return await _keeper.ExecuteAsync("status", async client =>
        {
            var status = await client.GetStatusAsync(timeout, _cts.Token).ConfigureAwait(false);

            var payload = new JsonObject
            {
                ["status"] = SessionView.Status(status),
            };

            if (includeRaw)
            {
                payload["raw"] = status.DeepClone();
            }

            return payload;
        }, timeout, _cts.Token).ConfigureAwait(false);
    }

    private async Task<JsonObject> InfoAsync(JsonObject args, TimeSpan timeout)
    {
        var includeRaw = NodeRead.Bool(args, "raw") == true;

        return await _keeper.ExecuteAsync("info", async client =>
        {
            var app = await client.AppInfoAsync(timeout, _cts.Token).ConfigureAwait(false);

            var payload = new JsonObject
            {
                ["app"] = SessionView.App(app),
                ["session"] = client.Handshake.DeepClone(),
                ["storePath"] = SessionStore.FilePath,
            };

            if (includeRaw)
            {
                payload["raw"] = app.DeepClone();
            }

            return payload;
        }, timeout, _cts.Token).ConfigureAwait(false);
    }

    private async Task<JsonObject> ConnectAsync(JsonObject args, TimeSpan timeout)
    {
        var reset = NodeRead.Bool(args, "reset") == true;

        if (reset)
        {
            log.Write("resetting the stored session token; Dorico may ask the user to authorize again");
        }

        return await _keeper.ConnectAsync(reset, timeout, _cts.Token).ConfigureAwait(false);
    }

    private async Task<JsonObject> CommandsAsync(JsonObject args, TimeSpan timeout)
    {
        var name = NodeRead.Str(args, "name");
        var family = NodeRead.Str(args, "family");
        var filter = NodeRead.Str(args, "filter");
        var limit = (int)Math.Clamp(NodeRead.Long(args, "limit") ?? 100, 1, 5000);

        return await _keeper.ExecuteAsync("commands", async client =>
        {
            var started = Stopwatch.GetTimestamp();
            var response = await client.GetCommandsAsync(timeout, _cts.Token).ConfigureAwait(false);
            var elapsed = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;

            var matches = CatalogueView.Select(response, name, family, filter, limit);

            return new JsonObject
            {
                ["catalogue"] = CatalogueView.Report(response, elapsed),
                ["matches"] = matches,
                ["matchCount"] = matches.Count,
            };
        }, timeout, _cts.Token).ConfigureAwait(false);
    }

    /// <summary>
    /// Refuses to act when the document Dorico has active is not the one the caller said it expects.
    /// </summary>
    /// <remarks>
    /// Every command acts on whichever document is active, and bringing any window to the front changes that.
    /// When several agents share one Dorico this is how a save lands in somebody else's project, so a caller that
    /// knows which document it means can say so with `--expect-score` and get a loud refusal instead. The check
    /// runs inside the same gated exchange as the action, so the window between them is one round trip.
    /// </remarks>
    private async Task EnsureExpectedScoreAsync(DoricoClient client, JsonObject args, TimeSpan timeout)
    {
        if (NodeRead.Int(args, "expectScore") is null)
        {
            return;
        }

        var status = await client.GetStatusAsync(timeout, _cts.Token).ConfigureAwait(false);
        EnsureExpectedScore(args, status);
    }

    private static void EnsureExpectedScore(JsonObject args, JsonObject status)
    {
        if (NodeRead.Int(args, "expectScore") is not { } expected)
        {
            return;
        }

        var actual = NodeRead.Int(status, "activeOpenScoreID");

        if (actual == expected)
        {
            return;
        }

        var details = new JsonObject { ["expected"] = expected };

        if (actual is not null)
        {
            details["actual"] = actual.Value;
        }

        throw new CliException(
            "WrongActiveScore",
            $"Dorico's active document is not the one this command was told to expect (expected score "
            + $"{expected}, found {(actual is null ? "none" : actual.Value.ToString())}). Bring the right window to "
            + "the front and run it again, or drop --expect-score to act on whatever is active - which is how a "
            + "command ends up in somebody else's project.",
            2)
        {
            Details = details,
        };
    }

    private async Task<JsonObject> CommandAsync(JsonObject args, TimeSpan timeout)
    {
        var name = NodeRead.Str(args, "name");

        if (string.IsNullOrWhiteSpace(name))
        {
            throw new CliException("InvalidArgument", "The command operation needs a name.", 1);
        }

        var parameters = ReadParameterMap(NodeRead.Obj(args, "parameters"));
        var waitMs = Math.Clamp(NodeRead.Long(args, "waitMs") ?? 0, 0, 120_000);
        var verify = NodeRead.Bool(args, "verify") ?? true;

        return await _keeper.ExecuteAsync($"command {name}", async client =>
        {
            await EnsureExpectedScoreAsync(client, args, timeout).ConfigureAwait(false);

            var started = Stopwatch.GetTimestamp();
            var ack = await client.SendCommandAsync(name, parameters, timeout, _cts.Token).ConfigureAwait(false);
            var elapsed = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;

            var result = new JsonObject
            {
                ["name"] = name,
                ["parameters"] = ParametersJson(parameters),
                ["ack"] = NodeRead.Str(ack, "code") ?? NodeRead.Str(ack, "message") ?? "unknown",
                ["roundTripMs"] = elapsed,
            };

            var detail = NodeRead.Str(ack, "detail");

            if (detail is not null)
            {
                result["detail"] = detail;
            }

            if (verify)
            {
                if (waitMs > 0)
                {
                    await Task.Delay((int)waitMs, _cts.Token).ConfigureAwait(false);
                }

                var status = await client.GetStatusAsync(timeout, _cts.Token).ConfigureAwait(false);
                result["status"] = SessionView.Status(status);
                result["note"] = "kOK only acknowledges receipt; status (or the window) is what proves the effect.";
            }

            return new JsonObject { ["result"] = result };
        }, timeout + TimeSpan.FromMilliseconds(waitMs), _cts.Token).ConfigureAwait(false);
    }


    private async Task<JsonObject> SequenceAsync(JsonObject args, TimeSpan timeout)
    {
        var steps = NodeRead.Arr(args, "steps") ?? [];
        var waitMs = Math.Clamp(NodeRead.Long(args, "waitMs") ?? 0, 0, 120_000);
        var verify = NodeRead.Bool(args, "verify") ?? true;

        if (steps.Count == 0)
        {
            throw new CliException("InvalidArgument", "The sequence operation needs at least one step.", 1);
        }

        return await _keeper.ExecuteAsync("sequence", async client =>
        {
            await EnsureExpectedScoreAsync(client, args, timeout).ConfigureAwait(false);

            var results = new JsonArray();
            var started = Stopwatch.GetTimestamp();
            string? stoppedAt = null;

            foreach (var step in steps)
            {
                var name = NodeRead.Str(step, "name");

                if (string.IsNullOrWhiteSpace(name))
                {
                    throw new CliException("InvalidArgument", "Every sequence step needs a name.", 1);
                }

                var parameters = ReadParameterMap(NodeRead.Obj(step, "parameters"));
                var stepStarted = Stopwatch.GetTimestamp();
                var ack = await client.SendCommandAsync(name, parameters, timeout, _cts.Token).ConfigureAwait(false);

                var entry = new JsonObject
                {
                    ["name"] = name,
                    ["ms"] = (long)Stopwatch.GetElapsedTime(stepStarted).TotalMilliseconds,
                    ["ack"] = NodeRead.Str(ack, "code") ?? NodeRead.Str(ack, "message") ?? "unknown",
                };

                if (parameters.Count > 0)
                {
                    entry["parameters"] = ParametersJson(parameters);
                }

                var detail = NodeRead.Str(ack, "detail");

                if (detail is not null)
                {
                    entry["detail"] = detail;
                }

                JsonArr.AddNode(results, entry);

                if (NodeRead.IsError(ack))
                {
                    stoppedAt = name;
                    break;
                }
            }

            var payload = new JsonObject
            {
                ["steps"] = results,
                ["roundTripMs"] = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds,
            };

            if (stoppedAt is not null)
            {
                payload["stoppedAt"] = stoppedAt;
                payload["note"] = "The sequence stopped at the first command Dorico refused.";
                return payload;
            }

            if (!verify)
            {
                return payload;
            }

            if (waitMs > 0)
            {
                await Task.Delay((int)waitMs, _cts.Token).ConfigureAwait(false);
            }

            var status = await client.GetStatusAsync(timeout, _cts.Token).ConfigureAwait(false);
            payload["status"] = SessionView.Status(status);
            payload["note"] = "kOK only acknowledges receipt; status (or the window) is what proves the effect.";

            return payload;
        }, timeout * 4 + TimeSpan.FromMilliseconds(waitMs), _cts.Token).ConfigureAwait(false);
    }

    private JsonObject Events(JsonObject args)
    {
        var limit = (int)Math.Clamp(NodeRead.Long(args, "limit") ?? 20, 1, 500);
        var includePayload = NodeRead.Bool(args, "payload") == true;
        var events = _keeper.Events;

        var tail = new JsonArray();

        foreach (var evt in events.TakeLast(limit))
        {
            JsonArr.AddNode(tail, includePayload ? evt.DeepClone() : new JsonObject
            {
                ["kind"] = NodeRead.Str(evt, "kind"),
                ["atUtc"] = NodeRead.Str(evt, "atUtc"),
            });
        }

        return new JsonObject
        {
            ["events"] = tail,
            ["total"] = events.Count,
            ["counts"] = SessionView.EventCounts(events),
            ["connected"] = _keeper.IsConnected,
        };
    }

    private JsonObject BuildHostPayload()
    {
        var host = new JsonObject
        {
            ["pid"] = Environment.ProcessId,
            ["pipe"] = HostProtocol.PipeName,
            ["uptimeMs"] = _uptime.ElapsedMilliseconds,
            ["connections"] = Volatile.Read(ref _connections),
            ["logPath"] = HostLog.LogPath,
        };

        foreach (var (key, value) in _keeper.Snapshot())
        {
            host[key] = value?.DeepClone();
        }

        return new JsonObject { ["host"] = host };
    }

    private static List<KeyValuePair<string, string>> ReadParameterMap(JsonObject? map)
    {
        var parameters = new List<KeyValuePair<string, string>>();

        if (map is null)
        {
            return parameters;
        }

        foreach (var (key, value) in map)
        {
            var text = NodeRead.Text(value) ?? string.Empty;

            if (text.Length > MaxParameterLength)
            {
                throw new CliException("InvalidArgument", $"The value of '{key}' is too long.", 1);
            }

            parameters.Add(new KeyValuePair<string, string>(key, text));
        }

        return parameters;
    }

    private static JsonObject ParametersJson(List<KeyValuePair<string, string>> parameters)
    {
        var json = new JsonObject();

        foreach (var (key, value) in parameters)
        {
            json[key] = value;
        }

        return json;
    }
}
