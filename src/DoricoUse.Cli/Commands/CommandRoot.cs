using System.CommandLine;
using System.Text.Json.Nodes;
using DoricoUse.Cli.Dorico;
using DoricoUse.Cli.Json;

namespace DoricoUse.Cli.Commands;

internal static class CommandRoot
{
    public static RootCommand Build()
    {
        var root = new RootCommand("dorico-use: JSON CLI for steering Dorico through its Remote Control API.");
        root.Subcommands.Add(SessionCommands.Build());
        return root;
    }

    internal static Option<string> AddressOption() => new("--address")
    {
        Description = $"Address of Dorico's Remote Control server (default {DoricoProtocol.DefaultAddress}).",
    };

    internal static Option<string> ClientNameOption() => new("--client-name")
    {
        Description = "Client name Dorico shows in its authorization dialog.",
    };

    internal static Option<int> TimeoutOption(int defaultMilliseconds = 15000) => new("--timeout-ms")
    {
        Description = "How long to wait for each protocol answer, in milliseconds.",
        DefaultValueFactory = _ => defaultMilliseconds,
    };

    internal static Option<bool> RawOption() => new("--raw")
    {
        Description = "Also return the unmodified Dorico payload.",
    };

    /// <summary>
    /// Loads the stored settings and applies the per-invocation overrides. Nothing is saved here: only a real
    /// handshake writes the store, so a read-only command never rewrites state.
    /// </summary>
    internal static SessionStore LoadStore(
        ParseResult parse,
        Option<string> addressOption,
        Option<string> clientNameOption)
    {
        var store = SessionStore.Load();

        var address = parse.GetValue(addressOption);
        if (!string.IsNullOrWhiteSpace(address))
        {
            store.Address = address;
        }

        var clientName = parse.GetValue(clientNameOption);
        if (!string.IsNullOrWhiteSpace(clientName))
        {
            store.ClientName = clientName;
        }

        return store;
    }

    internal static TimeSpan Timeout(ParseResult parse, Option<int> timeoutOption)
        => TimeSpan.FromMilliseconds(Math.Clamp(parse.GetValue(timeoutOption), 250, 600000));

    internal static int RunAsync(string command, Func<Task<JsonObject>> action)
    {
        try
        {
            return ResponseWriter.Success(command, action().GetAwaiter().GetResult());
        }
        catch (Exception ex)
        {
            return ResponseWriter.Fail(command, ex);
        }
    }

    internal static long ElapsedMilliseconds(long startedTimestamp)
        => (long)System.Diagnostics.Stopwatch.GetElapsedTime(startedTimestamp).TotalMilliseconds;
}
