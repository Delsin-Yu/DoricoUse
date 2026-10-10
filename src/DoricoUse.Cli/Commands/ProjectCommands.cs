using System.CommandLine;
using System.Text.Json.Nodes;
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
        return project;
    }

    private static Command BuildFlows()
    {
        var timeout = CommandRoot.TimeoutOption();

        var command = new Command("flows", "The flows in the project; notation options are per flow.")
        {
            timeout,
        };

        command.SetAction(parse => CommandRoot.RunAsync("project flows", async () =>
            await HostClient.CallAsync(
                HostProtocol.OpFlows,
                null,
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

        var timeout = CommandRoot.TimeoutOption();

        var command = new Command("layouts", "The layouts in the project; layout options are per layout.")
        {
            kind,
            timeout,
        };

        command.SetAction(parse => CommandRoot.RunAsync("project layouts", async () =>
        {
            var args = new JsonObject();
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
