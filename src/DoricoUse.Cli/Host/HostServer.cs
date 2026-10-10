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
internal sealed class HostServer(HostOptions options, HostLog log)
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
        var readPath = NodeRead.Str(args, "readPath");
        var readOptions = ReadReadOptions(NodeRead.Obj(args, "readOptions"));
        string? requestedMode = null;

        if (read && string.IsNullOrWhiteSpace(readPath))
        {
            throw new CliException(
                "InvalidArgument",
                "note insert --read needs the path of the copy to save (the CLI resolves it from --read-to).",
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
                plan["read"] = ReadPlan(readPath!, readOptions);
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

        if (read)
        {
            // Writing the copy and decoding it: the save is about a second, the watch budget is the rest.
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
            async Task AddReadAsync()
            {
                if (!read)
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
            payload["note"] = read
                ? "kOK only acknowledges receipt; 'read' is the answer. Dorico was asked to write a copy of the "
                    + "document it just wrote into (File.SaveAs, the same round trip as `session read`) and the CLI "
                    + "decoded it, so read.score.notes is the position of every note that is really in the score."
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

    /// <summary>What the planned read would do, for the dry-run plan.</summary>
    private static JsonObject ReadPlan(string path, ProjectFileView.Options options)
    {
        var measures = options.FirstMeasure is { } first && options.LastMeasure is { } last
            ? first == last
                ? first.ToString(System.Globalization.CultureInfo.InvariantCulture)
                : $"{first}-{last}"
            : null;

        return new JsonObject
        {
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

    private Task<JsonObject> FlowsAsync(TimeSpan timeout)
        => FetchAsync("flows", DoricoProtocol.RidFlowsList, DoricoProtocol.GetFlows(), ScoreView.Flows, timeout);

    private Task<JsonObject> LayoutsAsync(JsonObject args, TimeSpan timeout)
    {
        var kind = NodeRead.Str(args, "kind");

        return FetchAsync(
            "layouts",
            DoricoProtocol.RidLayoutsList,
            DoricoProtocol.GetLayouts(),
            response => ScoreView.Layouts(response, kind),
            timeout);
    }

    private Task<JsonObject> PropertiesAsync(TimeSpan timeout)
        => FetchAsync(
            "properties",
            DoricoProtocol.RidPropertiesList,
            DoricoProtocol.GetProperties(),
            ScoreView.Properties,
            timeout);

    private Task<JsonObject> TechniquesAsync(TimeSpan timeout)
        => FetchAsync(
            "playback techniques",
            DoricoProtocol.RidPlaybackTechniquesList,
            DoricoProtocol.GetPlaybackTechniques(),
            ScoreView.Techniques,
            timeout);

    private Task<JsonObject> LibraryCollectionsAsync(TimeSpan timeout)
        => FetchAsync(
            "library collections",
            DoricoProtocol.RidLibraryCollectionsList,
            DoricoProtocol.GetLibraryCollections(),
            ScoreView.LibraryCollections,
            timeout);

    private Task<JsonObject> LibraryEntitiesAsync(JsonObject args, TimeSpan timeout)
    {
        var collection = NodeRead.Str(args, "collection");

        if (string.IsNullOrWhiteSpace(collection))
        {
            throw new CliException("InvalidArgument", "Library entities need a collection; see `library collections`.", 1);
        }

        return FetchAsync(
            $"library entities {collection}",
            DoricoProtocol.RidLibraryEntitiesList,
            DoricoProtocol.GetLibraryEntities(collection),
            ScoreView.LibraryEntities,
            timeout);
    }

    private Task<JsonObject> OptionsGetAsync(JsonObject args, TimeSpan timeout)
    {
        var optionsType = ReadOptionsType(args);
        var targetId = NodeRead.Int(args, "targetId");
        var filter = NodeRead.Str(args, "filter");
        var limit = (int)Math.Clamp(NodeRead.Long(args, "limit") ?? 200, 1, 5000);

        GuardOptionsTarget(optionsType, targetId is not null ? [targetId.Value] : []);

        return FetchAsync(
            "options get",
            DoricoProtocol.RidOptionsList,
            DoricoProtocol.GetOptions(optionsType, targetId),
            response => ScoreView.Options(response, filter, limit),
            timeout);
    }

    private async Task<JsonObject> OptionsSetAsync(JsonObject args, TimeSpan timeout)
    {
        var optionsType = ReadOptionsType(args);
        var values = ReadParameterMap(NodeRead.Obj(args, "values"));
        var targets = ReadIntArray(args, "targets");
        var verify = NodeRead.Bool(args, "verify") ?? true;

        if (values.Count == 0)
        {
            throw new CliException("InvalidArgument", "options set needs at least one --value path=value.", 1);
        }

        GuardOptionsTarget(optionsType, targets);

        return await _keeper.ExecuteAsync("options set", async client =>
        {
            var response = await client.Session.RequestAsync(
                DoricoProtocol.RidResponse,
                DoricoProtocol.SetOptions(optionsType, values, targets),
                timeout,
                _cts.Token).ConfigureAwait(false);

            if (NodeRead.IsError(response))
            {
                throw new CliException(
                    "DoricoError",
                    $"Dorico refused setoptions: {NodeRead.Str(response, "detail") ?? "no detail"}",
                    2);
            }

            var payload = new JsonObject
            {
                ["optionsType"] = optionsType,
                ["ack"] = NodeRead.Str(response, "code") ?? "unknown",
                ["set"] = ParametersJson(values),
            };

            if (!verify)
            {
                return payload;
            }

            var current = await client.Session.RequestAsync(
                DoricoProtocol.RidOptionsList,
                DoricoProtocol.GetOptions(optionsType, targets.Count > 0 ? targets[0] : null),
                timeout,
                _cts.Token).ConfigureAwait(false);

            payload["verified"] = VerifyOptions(current, values);

            return payload;
        }, timeout * 2, _cts.Token).ConfigureAwait(false);
    }

    /// <summary>One protocol exchange projected into agent-friendly JSON.</summary>
    private async Task<JsonObject> FetchAsync(
        string what,
        string expectedId,
        string message,
        Func<JsonObject, JsonObject> project,
        TimeSpan timeout)
        => await _keeper.ExecuteAsync(what, async client =>
        {
            var started = Stopwatch.GetTimestamp();
            var response = await client.Session.RequestAsync(expectedId, message, timeout, _cts.Token)
                .ConfigureAwait(false);

            if (NodeRead.IsError(response))
            {
                throw new CliException(
                    "DoricoError",
                    $"Dorico refused {what}: {NodeRead.Str(response, "detail") ?? "no detail"}",
                    2);
            }

            var payload = project(response);
            payload["fetchMs"] = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;

            return payload;
        }, timeout, _cts.Token).ConfigureAwait(false);

    private static JsonObject VerifyOptions(JsonObject response, List<KeyValuePair<string, string>> values)
    {
        var options = NodeRead.Arr(response, "options") ?? [];
        var verification = new JsonObject();

        foreach (var (path, wanted) in values)
        {
            string? actual = null;
            var found = false;

            foreach (var option in options)
            {
                if (NodeRead.Str(option, "path") == path)
                {
                    actual = NodeRead.Str(option, "currentValue");
                    found = true;
                    break;
                }
            }

            verification[path] = new JsonObject
            {
                ["wanted"] = wanted,
                ["actual"] = actual,
                ["found"] = found,
                ["ok"] = found && actual == wanted,
            };
        }

        return verification;
    }

    private static string ReadOptionsType(JsonObject args) => NodeRead.Str(args, "optionsType") switch
    {
        DoricoProtocol.OptionsLayout => DoricoProtocol.OptionsLayout,
        DoricoProtocol.OptionsNotation => DoricoProtocol.OptionsNotation,
        _ => DoricoProtocol.OptionsEngraving,
    };

    private static void GuardOptionsTarget(string optionsType, IReadOnlyList<int> targets)
    {
        if (optionsType == DoricoProtocol.OptionsEngraving)
        {
            return;
        }

        var name = optionsType == DoricoProtocol.OptionsLayout ? "--layout" : "--flow";

        if (targets.Count == 0)
        {
            throw new CliException(
                "InvalidArgument",
                $"{optionsType} options are per layout or flow: pass {name} with an ID (`project layouts` lists them).",
                1);
        }
    }

    private static List<int> ReadIntArray(JsonObject args, string key)
    {
        var values = new List<int>();
        var array = NodeRead.Arr(args, key);

        if (array is null)
        {
            return values;
        }

        foreach (var item in array)
        {
            if (item is JsonValue value && value.TryGetValue<int>(out var number))
            {
                values.Add(number);
            }
        }

        return values;
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
