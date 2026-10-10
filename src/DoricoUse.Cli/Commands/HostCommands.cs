using System.CommandLine;
using System.Text.Json.Nodes;
using DoricoUse.Cli.Dorico;
using DoricoUse.Cli.Host;
using DoricoUse.Cli.Ipc;

namespace DoricoUse.Cli.Commands;

/// <summary>
/// The host is a separate, long-lived process: it owns the Dorico connection so the unsolicited stream keeps
/// arriving between CLI invocations. These commands start it, report on it and stop it.
/// </summary>
internal static class HostCommands
{
    public static Command Build()
    {
        var host = new Command("host", "The background process that keeps the Dorico connection open.");
        host.Subcommands.Add(BuildStart());
        host.Subcommands.Add(BuildStatus());
        host.Subcommands.Add(BuildStop());
        host.Subcommands.Add(BuildRestart());
        host.Subcommands.Add(BuildServe());
        return host;
    }

    private static Command BuildStart()
    {
        var address = CommandRoot.AddressOption();
        var clientName = CommandRoot.ClientNameOption();

        var command = new Command(
            "start",
            "Start the host as a separate process and wait until it answers. It keeps running until `host stop`.")
        {
            address,
            clientName,
        };

        command.SetAction(parse => CommandRoot.RunAsync("host start", async () =>
        {
            var started = await HostClient.StartAsync(
                parse.GetValue(address),
                parse.GetValue(clientName),
                CancellationToken.None);

            var host = NodeRead.Obj(started, "host") is { } startedHost
                ? (JsonObject)startedHost.DeepClone()
                : [];

            host["alreadyRunning"] = NodeRead.Bool(started, "alreadyRunning") ?? false;

            return new JsonObject { ["host"] = host };
        }));

        return command;
    }

    private static Command BuildStatus()
    {
        var command = new Command("status", "Is the host running, is it connected to Dorico, and what has it seen?");

        command.SetAction(parse => CommandRoot.RunAsync("host status", async () =>
        {
            var ping = await HostClient.PingAsync(TimeSpan.FromSeconds(3), CancellationToken.None);

            if (ping is null)
            {
                var stale = HostRuntimeFile.Read();

                return new JsonObject
                {
                    ["host"] = new JsonObject
                    {
                        ["running"] = false,
                        ["staleAnnouncement"] = stale is null
                            ? null
                            : new JsonObject
                            {
                                ["pid"] = stale.Pid,
                                ["startedAtUtc"] = stale.StartedAt.ToUniversalTime().ToString("O"),
                                ["note"] = "A previous host left this announcement behind; it is no longer answering.",
                            },
                    },
                };
            }

            var host = NodeRead.Obj(NodeRead.Obj(ping, "payload"), "host") is { } hostInfo
                ? (JsonObject)hostInfo.DeepClone()
                : [];

            host["running"] = true;

            return new JsonObject { ["host"] = host };
        }));

        return command;
    }

    private static Command BuildStop()
    {
        var command = new Command("stop", "Ask the host to stop and disconnect from Dorico.");

        command.SetAction(parse => CommandRoot.RunAsync("host stop", async () =>
            new JsonObject { ["host"] = await HostClient.StopAsync(CancellationToken.None) }));

        return command;
    }

    private static Command BuildRestart()
    {
        var address = CommandRoot.AddressOption();
        var clientName = CommandRoot.ClientNameOption();

        var command = new Command(
            "restart",
            "Stop the host and start it again. Useful after upgrading dorico-use: a running host keeps the "
            + "executable locked.")
        {
            address,
            clientName,
        };

        command.SetAction(parse => CommandRoot.RunAsync("host restart", async () =>
        {
            var stopped = await HostClient.StopAsync(CancellationToken.None);
            var started = await HostClient.StartAsync(
                parse.GetValue(address),
                parse.GetValue(clientName),
                CancellationToken.None);

            var host = NodeRead.Obj(started, "host") is { } info ? (JsonObject)info.DeepClone() : [];
            host["alreadyRunning"] = NodeRead.Bool(started, "alreadyRunning") ?? false;

            return new JsonObject
            {
                ["stopped"] = stopped,
                ["host"] = host,
            };
        }));

        return command;
    }

    private static Command BuildServe()
    {
        var address = CommandRoot.AddressOption();
        var clientName = CommandRoot.ClientNameOption();
        var verbose = new Option<bool>("--verbose")
        {
            Description = "Also echo the host log to stderr.",
        };

        var command = new Command(
            "serve",
            "Run the host in the foreground. `host start` does this for you; a second host refuses to start.")
        {
            address,
            clientName,
            verbose,
        };

        command.SetAction(parse => CommandRoot.RunAsync("host serve", async () =>
        {
            using var mutex = new Mutex(initiallyOwned: true, HostProtocol.HostMutexName, out var isFirst);

            if (!isFirst)
            {
                throw new CliException(
                    "HostAlreadyRunning",
                    "Another dorico-use host is already running. Use `host status` or `host stop`.",
                    3);
            }

            var store = SessionStore.Load();
            var addressOverride = parse.GetValue(address);
            var clientNameOverride = parse.GetValue(clientName);

            var addressChanged = !string.IsNullOrWhiteSpace(addressOverride)
                && !string.Equals(store.Address, addressOverride, StringComparison.OrdinalIgnoreCase);
            var clientNameChanged = !string.IsNullOrWhiteSpace(clientNameOverride)
                && !string.Equals(store.ClientName, clientNameOverride, StringComparison.Ordinal);

            if (addressChanged)
            {
                store.Address = addressOverride!;
            }

            if (clientNameChanged)
            {
                store.ClientName = clientNameOverride!;
            }

            // Only when an override actually changed something. The host holds these two in memory, so a rewrite it
            // does not need buys nothing - and in a build that does not persist the session token it would erase a
            // leftover token from an earlier build before the host could tell the user about it, which is the one
            // thing SessionStore.StoredTokenIgnored exists to report.
            if (addressChanged || clientNameChanged)
            {
                store.Save();
            }

            var options = new HostOptions(store.Address, store.ClientName, parse.GetValue(verbose));
            var exitCode = await new HostServer(options, new HostLog(options.Verbose)).RunAsync();

            return new JsonObject
            {
                ["host"] = new JsonObject
                {
                    ["stopped"] = true,
                    ["exitCode"] = exitCode,
                    ["pid"] = Environment.ProcessId,
                },
            };
        }, silent: Console.IsOutputRedirected));

        return command;
    }
}
