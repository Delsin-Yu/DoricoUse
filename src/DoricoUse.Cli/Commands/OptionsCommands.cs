using System.CommandLine;
using System.Text.Json.Nodes;
using DoricoUse.Cli.Dorico;
using DoricoUse.Cli.Ipc;
using DoricoUse.Cli.Json;

namespace DoricoUse.Cli.Commands;

/// <summary>
/// Engraving, notation and layout options: 800-odd paths of them. Reading is free; writing is verified by reading
/// the same paths back.
/// </summary>
internal static class OptionsCommands
{
    public static Command Build()
    {
        var options = new Command("options", "Read and write Dorico's engraving, notation and layout options.");
        options.Subcommands.Add(BuildGet());
        options.Subcommands.Add(BuildSet());
        return options;
    }

    private static Command BuildGet()
    {
        var type = TypeOption();
        var layout = new Option<int[]>("--layout")
        {
            Description = "Layout ID for --type layout. Repeatable.",
        };

        var flow = new Option<int[]>("--flow")
        {
            Description = "Flow ID for --type notation. Repeatable.",
        };

        var filter = new Option<string?>("--filter")
        {
            Description = "Match the option path: a substring, or a glob with * and ?.",
        };

        var limit = new Option<int>("--limit")
        {
            Description = "How many options to return.",
            DefaultValueFactory = _ => 200,
        };

        var timeout = CommandRoot.TimeoutOption();

        var command = new Command("get", "The option paths and their current values.")
        {
            type,
            layout,
            flow,
            filter,
            limit,
            timeout,
        };

        command.SetAction(parse => CommandRoot.RunAsync("options get", async () =>
        {
            var optionsType = ParseType(parse.GetValue(type));
            var args = new JsonObject
            {
                ["optionsType"] = optionsType,
                ["limit"] = Math.Clamp(parse.GetValue(limit), 1, 5000),
            };

            AddTarget(args, optionsType, parse.GetValue(layout), parse.GetValue(flow));
            AddIfSet(args, "filter", parse.GetValue(filter));

            return await HostClient.CallAsync(
                HostProtocol.OpOptionsGet,
                args,
                CommandRoot.Timeout(parse, timeout),
                CancellationToken.None);
        }));

        return command;
    }

    private static Command BuildSet()
    {
        var type = TypeOption();

        var values = new Option<string[]>("--value")
        {
            Description = "An option as path=value, e.g. --value rhythmDotPositionOptions.noteDotGap=1/2. Repeatable.",
            Required = true,
        };

        var layout = new Option<int[]>("--layout")
        {
            Description = "Layout ID for --type layout. Repeatable.",
        };

        var flow = new Option<int[]>("--flow")
        {
            Description = "Flow ID for --type notation. Repeatable.",
        };

        var noVerify = new Option<bool>("--no-verify")
        {
            Description = "Do not read the values back afterwards.",
        };

        var timeout = CommandRoot.TimeoutOption();
        var expectScore = CommandRoot.ExpectScoreOption();

        var command = new Command("set", "Set option values, then read them back to prove it.")
        {
            type,
            values,
            layout,
            flow,
            noVerify,
            timeout,
            expectScore,
        };

        command.SetAction(parse => CommandRoot.RunAsync("options set", async () =>
        {
            var optionsType = ParseType(parse.GetValue(type));
            var args = new JsonObject
            {
                ["optionsType"] = optionsType,
                ["values"] = ParsePairs(parse.GetValue(values)),
                ["verify"] = !parse.GetValue(noVerify),
            };

            AddTarget(args, optionsType, parse.GetValue(layout), parse.GetValue(flow));

            return await HostClient.CallAsync(
                HostProtocol.OpOptionsSet,
                args,
                CommandRoot.Timeout(parse, timeout) * 2,
                CancellationToken.None);
        }));

        return command;
    }

    private static Option<string> TypeOption() => new("--type")
    {
        Description = "engraving (global), notation (per flow) or layout (per layout).",
        DefaultValueFactory = _ => "engraving",
    };

    /// <summary>Puts the target ID in the key Dorico expects for that option type.</summary>
    private static void AddTarget(JsonObject args, string optionsType, int[]? layouts, int[]? flows)
    {
        var targets = optionsType switch
        {
            DoricoProtocol.OptionsLayout => layouts,
            DoricoProtocol.OptionsNotation => flows,
            _ => null,
        };

        if (targets is null || targets.Length == 0)
        {
            return;
        }

        var array = new JsonArray();

        foreach (var id in targets)
        {
            JsonArr.AddNumber(array, id);
        }

        args["targets"] = array;

        // A read takes one target ID; a write takes the list.
        args["targetId"] = targets[0];
    }

    private static string ParseType(string? value) => value?.ToLowerInvariant() switch
    {
        "layout" => DoricoProtocol.OptionsLayout,
        "notation" => DoricoProtocol.OptionsNotation,
        "engraving" => DoricoProtocol.OptionsEngraving,
        _ => throw new CliException("InvalidArgument", "--type must be engraving, notation or layout.", 1),
    };

    /// <summary>Turns repeated path=value arguments into a JSON object.</summary>
    private static JsonObject ParsePairs(string[]? values)
    {
        var pairs = new JsonObject();

        foreach (var value in values ?? [])
        {
            var separator = value.IndexOf('=', StringComparison.Ordinal);

            if (separator <= 0)
            {
                throw new CliException("InvalidArgument", $"--value expects path=value, but got '{value}'.", 1);
            }

            pairs[value[..separator]] = value[(separator + 1)..];
        }

        return pairs;
    }

    private static void AddIfSet(JsonObject args, string key, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            args[key] = value;
        }
    }
}
