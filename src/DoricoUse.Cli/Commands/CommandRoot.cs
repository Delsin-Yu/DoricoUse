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
        root.Subcommands.Add(HostCommands.Build());
        root.Subcommands.Add(CommandCommands.Build());
        root.Subcommands.Add(ProjectCommands.Build());
        root.Subcommands.Add(OptionsCommands.Build());
        root.Subcommands.Add(ScoreCommands.BuildLibrary());
        root.Subcommands.Add(ScoreCommands.BuildPlayback());
        root.Subcommands.Add(ScoreCommands.BuildProperties());
        root.Subcommands.Add(NoteCommands.Build());
        root.Subcommands.Add(ExportCommands.Build());
        root.Subcommands.Add(FileCommands.Build());
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
    /// Guards against acting on the wrong document: the host compares this with the score Dorico has active and
    /// refuses the command when they differ.
    /// </summary>
    internal static Option<int?> ExpectScoreOption() => new("--expect-score")
    {
        Description = "Refuse to run unless Dorico's active document is this score ID. `session status` and "
            + "`host status` report the ID; use it when several agents share one Dorico.",
    };

    internal static void AddExpectScore(JsonObject args, int? expectScore)
    {
        if (expectScore is not null)
        {
            args["expectScore"] = expectScore.Value;
        }
    }

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

    /// <param name="silent">
    /// Print nothing on success. Used by `host serve`: when it is started detached its stdout belongs to a pipe
    /// nobody reads, and its log file is where the story lives.
    /// </param>
    internal static int RunAsync(string command, Func<Task<JsonObject>> action, bool silent = false)
    {
        try
        {
            var payload = action().GetAwaiter().GetResult();

            return silent ? 0 : ResponseWriter.Success(command, payload);
        }
        catch (Exception ex)
        {
            if (!silent)
            {
                return ResponseWriter.Fail(command, ex);
            }

            WriteErrorIfAttached(ex);

            return ex is CliException cli ? cli.ExitCode : 2;
        }
    }

    private static void WriteErrorIfAttached(Exception error)
    {
        try
        {
            if (!Console.IsErrorRedirected)
            {
                Console.Error.WriteLine($"dorico-use: {error.Message}");
            }
        }
        catch (IOException)
        {
            // A detached process has nowhere to write.
        }
    }

    internal static long ElapsedMilliseconds(long startedTimestamp)
        => (long)System.Diagnostics.Stopwatch.GetElapsedTime(startedTimestamp).TotalMilliseconds;
}
