using System.CommandLine;
using System.Text.Json;
using System.Text.Json.Nodes;
using DoricoUse.Cli.Dorico;
using DoricoUse.Cli.Ipc;

namespace DoricoUse.Cli.Commands;

/// <summary>
/// Dorico's own command surface: the 1090 commands it accepts, their parameters, and the commands themselves.
/// </summary>
internal static class CommandCommands
{
    public static Command Build()
    {
        var command = new Command("command", "Send Dorico commands and search its command catalogue.");
        command.Subcommands.Add(BuildSend());
        command.Subcommands.Add(BuildList());
        command.Subcommands.Add(BuildInfo());
        return command;
    }

    private static Command BuildSend()
    {
        var name = new Option<string>("--name")
        {
            Description = "Dorico command name, e.g. NoteInput.Enter or File.Open.",
            Required = true,
        };

        var parameters = new Option<string[]>("--param")
        {
            Description = "A command parameter as Key=Value. Repeatable: --param File=\"D:\\score.dorico\".",
        };

        var timeoutOption = CommandRoot.TimeoutOption();
        var waitOption = new Option<int>("--wait-ms")
        {
            Description = "Wait this long before reading the status back; opening a project takes seconds.",
            DefaultValueFactory = _ => 0,
        };

        var noVerify = new Option<bool>("--no-verify")
        {
            Description = "Do not read the status back after the command.",
        };

        var expectScore = CommandRoot.ExpectScoreOption();

        var command = new Command(
            "send",
            "Send one Dorico command. The status is read back afterwards, because Dorico answers kOK even for "
            + "commands that then fail.")
        {
            name,
            parameters,
            timeoutOption,
            waitOption,
            noVerify,
            expectScore,
        };

        command.SetAction(parse => CommandRoot.RunAsync("command send", async () =>
        {
            var waitMs = Math.Clamp(parse.GetValue(waitOption), 0, 120_000);
            var timeout = CommandRoot.Timeout(parse, timeoutOption);
            var values = ParseParameters(parse.GetValue(parameters));

            var args = new JsonObject
            {
                ["name"] = parse.GetValue(name),
                ["waitMs"] = waitMs,
                ["verify"] = !parse.GetValue(noVerify),
            };

            if (values.Count > 0)
            {
                args["parameters"] = values;
            }

            CommandRoot.AddExpectScore(args, parse.GetValue(expectScore));

            // The host may exchange twice with Dorico (command, then status) plus the requested wait.
            var budget = (timeout * 2) + TimeSpan.FromMilliseconds(waitMs);

            return await HostClient.CallAsync(HostProtocol.OpCommand, args, budget, CancellationToken.None);
        }));

        return command;
    }

    private static Command BuildList()
    {
        var family = new Option<string?>("--family")
        {
            Description = "Only commands whose name starts with this family, e.g. NoteInput.",
        };

        var filter = new Option<string?>("--filter")
        {
            Description = "Match the command name or its display name: a substring, or a glob with * and ?.",
        };

        var limit = new Option<int>("--limit")
        {
            Description = "How many matches to return.",
            DefaultValueFactory = _ => 100,
        };

        var timeout = CommandRoot.TimeoutOption();

        var command = new Command("list", "Search the command catalogue Dorico reports.")
        {
            family,
            filter,
            limit,
            timeout,
        };

        command.SetAction(parse => CommandRoot.RunAsync("command list", async () =>
        {
            var args = new JsonObject
            {
                ["limit"] = Math.Clamp(parse.GetValue(limit), 1, 5000),
            };

            AddIfSet(args, "family", parse.GetValue(family));
            AddIfSet(args, "filter", parse.GetValue(filter));

            return await HostClient.CallAsync(
                HostProtocol.OpCommands,
                args,
                CommandRoot.Timeout(parse, timeout),
                CancellationToken.None);
        }));

        return command;
    }

    private static Command BuildInfo()
    {
        var name = new Option<string>("--name")
        {
            Description = "Exact command name, e.g. NoteInput.Pitch.",
            Required = true,
        };

        var timeout = CommandRoot.TimeoutOption();

        var command = new Command("info", "One catalogue entry: display name and the parameters it accepts.")
        {
            name,
            timeout,
        };

        command.SetAction(parse => CommandRoot.RunAsync("command info", async () =>
        {
            var commandName = parse.GetValue(name);
            var args = new JsonObject
            {
                ["name"] = commandName,
                ["limit"] = 1,
            };

            var payload = await HostClient.CallAsync(
                HostProtocol.OpCommands,
                args,
                CommandRoot.Timeout(parse, timeout),
                CancellationToken.None);

            var matches = NodeRead.Arr(payload, "matches");

            if (matches is null || matches.Count == 0)
            {
                throw new CliException("CommandNotFound", $"Dorico has no command named '{commandName}'.", 2);
            }

            return new JsonObject
            {
                ["command"] = matches[0]?.DeepClone(),
                ["catalogue"] = NodeRead.Obj(payload, "catalogue")?.DeepClone(),
            };
        }));

        return command;
    }

    /// <summary>Turns repeated --param Key=Value into a JSON object.</summary>
    private static JsonObject ParseParameters(string[]? values)
    {
        var parameters = new JsonObject();

        foreach (var value in values ?? [])
        {
            var separator = value.IndexOf('=', StringComparison.Ordinal);

            if (separator <= 0)
            {
                throw new CliException("InvalidArgument", $"--param expects Key=Value, but got '{value}'.", 1);
            }

            parameters[value[..separator]] = value[(separator + 1)..];
        }

        return parameters;
    }

    private static void AddIfSet(JsonObject args, string key, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            args[key] = value;
        }
    }
}
