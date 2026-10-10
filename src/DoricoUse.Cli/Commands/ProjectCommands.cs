using System.CommandLine;
using System.Text.Json.Nodes;
using DoricoUse.Cli.Dorico;
using DoricoUse.Cli.Ipc;

namespace DoricoUse.Cli.Commands;

/// <summary>The flows and layouts of the open project: the IDs every other request asks for.</summary>
internal static class ProjectCommands
{
    public static Command Build()
    {
        var project = new Command("project", "Flows and layouts of the project that is open in Dorico.");
        project.Subcommands.Add(BuildFlows());
        project.Subcommands.Add(BuildLayouts());
        project.Subcommands.Add(BuildPlayers());
        return project;
    }

    /// <summary>
    /// The players of the open project. There is no protocol request for them, so this reads the running document
    /// through the scripting layer; the payload says exactly what that layer answered.
    /// </summary>
    private static Command BuildPlayers()
    {
        var timeout = CommandRoot.TimeoutOption(20000);
        var expectScore = CommandRoot.ExpectScoreOption();
        var command = new Command(
            "players",
            "The players of the document Dorico has open: their index, the name Dorico describes them with, and "
            + "every other member the scripting layer exposes. No protocol request returns players, so this is a "
            + "live read (about a second) rather than a `getflows`-style fetch.")
        {
            timeout,
            expectScore,
        };

        command.SetAction(parse => CommandRoot.RunAsync("project players", () =>
        {
            var wait = CommandRoot.Timeout(parse, timeout);

            return LivePlayers.RunAsync(
                wait < LivePlayers.DefaultWait ? LivePlayers.DefaultWait : wait,
                CancellationToken.None,
                parse.GetValue(expectScore));
        }));

        return command;
    }

    private static Command BuildFlows()
    {
        var raw = CommandRoot.RawOption();
        var timeout = CommandRoot.TimeoutOption();

        var command = new Command("flows", "The flows in the project; notation options are per flow.")
        {
            raw,
            timeout,
        };

        command.SetAction(parse => CommandRoot.RunAsync("project flows", async () =>
            await HostClient.CallAsync(
                HostProtocol.OpFlows,
                new JsonObject { ["raw"] = parse.GetValue(raw) },
                CommandRoot.Timeout(parse, timeout),
                CancellationToken.None)));

        return command;
    }

    private static Command BuildLayouts()
    {
        var kind = new Option<string?>("--kind")
        {
            Description = "Only layouts of this kind: full-score or part.",
        };

        var raw = CommandRoot.RawOption();
        var timeout = CommandRoot.TimeoutOption();

        var command = new Command("layouts", "The layouts in the project; layout options are per layout.")
        {
            kind,
            raw,
            timeout,
        };

        command.SetAction(parse => CommandRoot.RunAsync("project layouts", async () =>
        {
            var args = new JsonObject { ["raw"] = parse.GetValue(raw) };
            var kindValue = parse.GetValue(kind);

            if (!string.IsNullOrWhiteSpace(kindValue))
            {
                args["kind"] = kindValue;
            }

            return await HostClient.CallAsync(
                HostProtocol.OpLayouts,
                args,
                CommandRoot.Timeout(parse, timeout),
                CancellationToken.None);
        }));

        return command;
    }
}
