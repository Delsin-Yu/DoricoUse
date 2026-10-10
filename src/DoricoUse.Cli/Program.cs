using System.CommandLine;
using System.Text;
using DoricoUse.Cli.Commands;
using DoricoUse.Cli.Json;

namespace DoricoUse.Cli;

internal static class Program
{
    private static readonly string[] Groups =
    [
        "session",
        "host",
        "command",
    ];

    private static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;

        var root = CommandRoot.Build();
        var parseResult = root.Parse(args);

        // Surface parse and arity errors as JSON, so an agent never has to read prose on stderr.
        if (parseResult.Errors.Count > 0)
        {
            var command = InferCommand(args);
            var message = string.Join("; ", parseResult.Errors.Select(error => error.Message));
            var help = IsMissingOrUnknownCommand(parseResult) ? InferHelp(args) : null;
            return ResponseWriter.FailParse(command, message, help);
        }

        return parseResult.Invoke();
    }

    private static string InferCommand(string[] args)
    {
        var parts = args.Where(arg => !arg.StartsWith('-')).Take(2).ToArray();
        return parts.Length == 0 ? "dorico-use" : string.Join(' ', parts);
    }

    private static bool IsMissingOrUnknownCommand(ParseResult parseResult)
    {
        if (parseResult.CommandResult.Command is RootCommand)
        {
            return true;
        }

        // A group without a subcommand, e.g. bare `dorico-use session`.
        return parseResult.Errors.Any(error =>
            error.Message.Contains("required command", StringComparison.OrdinalIgnoreCase)
            || error.Message.Contains("Unrecognized command", StringComparison.OrdinalIgnoreCase));
    }

    private static string InferHelp(string[] args)
    {
        var parts = args.Where(arg => !arg.StartsWith('-')).Take(1).ToArray();

        return parts.Length == 1 && Groups.Contains(parts[0], StringComparer.OrdinalIgnoreCase)
            ? $"dorico-use {parts[0]} --help"
            : "dorico-use --help";
    }
}
