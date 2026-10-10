using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json.Nodes;
using DoricoUse.Cli.Dorico;
using DoricoUse.Cli.Ui;

namespace DoricoUse.Cli.Ipc;

/// <summary>
/// The CLI's side of the pipe: reach the host, start it when it is missing, ask it to stop.
/// </summary>
internal static class HostClient
{
    /// <summary>Sends one request, starting the host first if nothing answers on the pipe.</summary>
    public static async Task<JsonObject> SendAsync(
        string op,
        JsonObject? args,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        bool autoStart = true)
    {
        try
        {
            return await ExchangeAsync(op, args, timeout, cancellationToken).ConfigureAwait(false);
        }
        catch (CliException ex) when (autoStart && ex.Code == "HostUnreachable")
        {
            await StartAsync(null, null, cancellationToken).ConfigureAwait(false);
            return await ExchangeAsync(op, args, timeout, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Sends one request and unwraps the payload, turning a host error into a CLI error.</summary>
    /// <remarks>
    /// A request that carries a Dorico command is reported to the execution tracker, with what Dorico answered:
    /// that pair is what lets the payload say whether the command ran, was queued behind a dialog, or was refused
    /// outright. The tracker is told after the answer, because a request the host itself refused never posted
    /// anything and must not look like a command Dorico ignored.
    /// </remarks>
    public static async Task<JsonObject> CallAsync(
        string op,
        JsonObject? args,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var sent = op == HostProtocol.OpCommand ? NodeRead.Str(args, "name") : null;
        var response = await SendAsync(op, args, timeout, cancellationToken).ConfigureAwait(false);

        if (NodeRead.Bool(response, "ok") == true)
        {
            var payload = NodeRead.Obj(response, "payload") ?? [];
            var answer = NodeRead.Obj(payload, "result") ?? payload;

            DialogSurface.MarkCommand(sent);
            DialogSurface.AcknowledgeCommand(sent, NodeRead.Str(answer, "ack"));

            return payload;
        }

        var error = NodeRead.Obj(response, "error") ?? [];

        throw new CliException(
            NodeRead.Str(error, "code") ?? "HostError",
            NodeRead.Str(error, "message") ?? "The host reported an error.",
            NodeRead.Int(response, "exitCode") ?? 2)
        {
            Details = NodeRead.Obj(error, "data"),
        };
    }

    /// <summary>Answers only if a host is already listening; never starts one.</summary>
    public static async Task<JsonObject?> PingAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        try
        {
            return await ExchangeAsync(HostProtocol.OpPing, null, timeout, cancellationToken).ConfigureAwait(false);
        }
        catch (CliException)
        {
            return null;
        }
    }

    /// <summary>Starts the host as a separate detached process and waits until it answers.</summary>
    public static async Task<JsonObject> StartAsync(
        string? address,
        string? clientName,
        CancellationToken cancellationToken)
    {
        var running = await PingAsync(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);

        if (running is not null)
        {
            return new JsonObject
            {
                ["alreadyRunning"] = true,
                ["host"] = NodeRead.Obj(NodeRead.Obj(running, "payload"), "host") is { } hostInfo
                    ? (JsonObject)hostInfo.DeepClone()
                    : null,
            };
        }

        var processPath = Environment.ProcessPath;
        var arguments = new List<string>();

        if (processPath is not null
            && Path.GetFileNameWithoutExtension(processPath).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            // Started as `dotnet dorico-use.dll`: the child needs the assembly, not the host binary.
            arguments.Add(Path.Combine(AppContext.BaseDirectory, "dorico-use.dll"));
        }
        else if (processPath is null)
        {
            processPath = "dorico-use";
        }

        arguments.Add("host");
        arguments.Add("serve");

        if (!string.IsNullOrWhiteSpace(address))
        {
            arguments.Add("--address");
            arguments.Add(address);
        }

        if (!string.IsNullOrWhiteSpace(clientName))
        {
            arguments.Add("--client-name");
            arguments.Add(clientName);
        }

        var pid = StartHost(processPath, arguments);

        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(20);

        while (DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(150, cancellationToken).ConfigureAwait(false);

            var response = await PingAsync(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);

            if (response is not null)
            {
                var host = NodeRead.Obj(NodeRead.Obj(response, "payload"), "host") is { } info
                    ? (JsonObject)info.DeepClone()
                    : [];

                if (host["pid"] is null)
                {
                    host["pid"] = pid;
                }

                return new JsonObject
                {
                    ["alreadyRunning"] = false,
                    ["pid"] = pid,
                    ["host"] = host,
                };
            }
        }

        throw new CliException(
            "HostStartFailed",
            $"The host did not answer on \\\\.\\pipe\\{HostProtocol.PipeName} within 20 seconds. "
            + $"See {HostRuntimeFile.LogPath}.",
            2);
    }

    /// <summary>Asks the host to stop, and kills it if it will not.</summary>
    public static async Task<JsonObject> StopAsync(CancellationToken cancellationToken)
    {
        var ping = await PingAsync(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);

        if (ping is null)
        {
            HostRuntimeFile.Delete();

            return new JsonObject
            {
                ["stopped"] = false,
                ["running"] = false,
                ["note"] = "No host was answering on the pipe; a stale announcement file was removed if present.",
            };
        }

        var pid = NodeRead.Int(NodeRead.Obj(NodeRead.Obj(ping, "payload"), "host"), "pid") ?? 0;

        try
        {
            await ExchangeAsync(HostProtocol.OpShutdown, null, TimeSpan.FromSeconds(5), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (CliException)
        {
            // It may drop the pipe while exiting, which is fine: we wait for the process below.
        }

        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(5);

        while (DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(100, cancellationToken).ConfigureAwait(false);

            if (await PingAsync(TimeSpan.FromMilliseconds(500), cancellationToken).ConfigureAwait(false) is null)
            {
                HostRuntimeFile.Delete();

                return new JsonObject
                {
                    ["stopped"] = true,
                    ["running"] = false,
                    ["pid"] = pid == 0 ? null : pid,
                    ["forced"] = false,
                };
            }
        }

        var forced = Kill(pid);

        HostRuntimeFile.Delete();

        return new JsonObject
        {
            ["stopped"] = true,
            ["running"] = false,
            ["pid"] = pid == 0 ? null : pid,
            ["forced"] = forced,
            ["note"] = forced
                ? "The host ignored the shutdown request and was killed."
                : "The host was still answering after the shutdown request; no such process was found to kill.",
        };
    }

    /// <summary>Starts the host so that it outlives this process and holds none of its handles.</summary>
    /// <remarks>
    /// A plain Process.Start hands the child every inheritable handle, including the stdout the caller is reading,
    /// so whoever reads that pipe — an agent's shell, or the terminal the CLI ran in — never sees EOF and the
    /// command looks hung long after the CLI exited. Redirecting the child's stdio does not help: the inherited
    /// copy of the original pipe stays open in the child. Going through the shell avoids the inheritance instead.
    /// </remarks>
    private static int StartHost(string fileName, List<string> arguments)
    {
        var throughShell = new ProcessStartInfo(fileName)
        {
            UseShellExecute = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = AppContext.BaseDirectory,
        };

        foreach (var argument in arguments)
        {
            throughShell.ArgumentList.Add(argument);
        }

        try
        {
            using var started = Process.Start(throughShell);

            if (started is not null)
            {
                return started.Id;
            }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception
            or InvalidOperationException
            or PlatformNotSupportedException)
        {
            // No shell to hand the launch to; use a child whose stdio we own and then drop.
        }

        var direct = new ProcessStartInfo(fileName)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = AppContext.BaseDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        foreach (var argument in arguments)
        {
            direct.ArgumentList.Add(argument);
        }

        Process fallback;

        try
        {
            fallback = Process.Start(direct)
                ?? throw new InvalidOperationException("Process.Start returned no process.");
        }
        catch (Exception ex)
        {
            throw new CliException("HostStartFailed", $"Could not start the host process: {ex.Message}", 2);
        }

        var pid = fallback.Id;
        Detach(fallback);

        return pid;
    }

    /// <summary>Drops our ends of the child's pipes, so the host outlives this process without holding it open.</summary>
    private static void Detach(Process process)
    {
        try
        {
            process.StandardInput.Close();
            process.StandardOutput.Close();
            process.StandardError.Close();
        }
        catch (IOException)
        {
            // The child may have exited between starting and detaching; nothing to clean up either way.
        }

        process.Dispose();
    }

    private static bool Kill(int pid)
    {
        if (pid <= 0)
        {
            return false;
        }

        try
        {
            using var process = Process.GetProcessById(pid);
            process.Kill(entireProcessTree: true);
            process.WaitForExit(3000);
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    private static async Task<JsonObject> ExchangeAsync(
        string op,
        JsonObject? args,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var budget = timeout + HostProtocol.PipeGrace;

        try
        {
            await using var pipe = new NamedPipeClientStream(
                ".",
                HostProtocol.PipeName,
                PipeDirection.InOut,
                PipeOptions.Asynchronous);

            try
            {
                await pipe.ConnectAsync(2000, cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                throw new CliException(
                    "HostUnreachable",
                    $"Nothing is listening on \\\\.\\pipe\\{HostProtocol.PipeName}.",
                    2);
            }
            catch (IOException ex)
            {
                throw new CliException("HostUnreachable", $"The host pipe refused the connection: {ex.Message}", 2);
            }

            using var reader = new StreamReader(pipe, new UTF8Encoding(false), false, 4096, leaveOpen: true);
            using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, leaveOpen: true)
            {
                AutoFlush = true,
                NewLine = "\n",
            };

            await writer.WriteLineAsync(HostProtocol.Request(op, args, timeout)).ConfigureAwait(false);

            using var readTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            readTimeout.CancelAfter(budget);

            var line = await reader.ReadLineAsync(readTimeout.Token).ConfigureAwait(false);

            if (line is null)
            {
                throw new CliException("HostGone", "The host closed the pipe without answering.", 2);
            }

            return JsonNode.Parse(line) as JsonObject
                ?? throw new CliException("HostGarbage", "The host answered with something that is not a JSON object.", 2);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new CliException(
                "HostTimeout",
                $"The host did not answer within {budget.TotalSeconds:0.#} s. See {HostRuntimeFile.LogPath}.",
                2);
        }
    }
}
