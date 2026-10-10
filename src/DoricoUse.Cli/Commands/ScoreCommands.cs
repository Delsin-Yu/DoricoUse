using System.CommandLine;
using System.Text.Json.Nodes;
using DoricoUse.Cli.Ipc;

namespace DoricoUse.Cli.Commands;

/// <summary>
/// The library (50 collections of entities: clefs, accidentals, playing techniques, chord symbols, ...), the
/// properties of the current selection, and the playback techniques the project uses.
/// </summary>
internal static class ScoreCommands
{
    public static Command BuildLibrary()
    {
        var library = new Command("library", "The project's library: collections of entities, and their entries.");
        library.Subcommands.Add(BuildCollections());
        library.Subcommands.Add(BuildEntities());
        return library;
    }

    public static Command BuildPlayback()
    {
        var playback = new Command("playback", "Playback configuration of the project.");
        playback.Subcommands.Add(BuildTechniques());
        return playback;
    }

    public static Command BuildProperties()
    {
        var properties = new Command("properties", "Properties of what is currently selected.");
        properties.Subcommands.Add(BuildPropertiesGet());
        return properties;
    }

    private static Command BuildCollections()
    {
        var timeout = CommandRoot.TimeoutOption();

        var command = new Command("collections", "The 50 library collections this project has.")
        {
            timeout,
        };

        command.SetAction(parse => CommandRoot.RunAsync("library collections", async () =>
            await HostClient.CallAsync(
                HostProtocol.OpLibraryCollections,
                null,
                CommandRoot.Timeout(parse, timeout),
                CancellationToken.None)));

        return command;
    }

    private static Command BuildEntities()
    {
        var collection = new Option<string>("--collection")
        {
            Description = "Collection name from `library collections`, e.g. kClefCollection.",
            Required = true,
        };

        var timeout = CommandRoot.TimeoutOption();

        var command = new Command("entities", "The entities inside one collection.")
        {
            collection,
            timeout,
        };

        command.SetAction(parse => CommandRoot.RunAsync("library entities", async () =>
        {
            var args = new JsonObject
            {
                ["collection"] = parse.GetValue(collection),
            };

            return await HostClient.CallAsync(
                HostProtocol.OpLibraryEntities,
                args,
                CommandRoot.Timeout(parse, timeout),
                CancellationToken.None);
        }));

        return command;
    }

    private static Command BuildTechniques()
    {
        var timeout = CommandRoot.TimeoutOption();

        var command = new Command("techniques", "The expression maps and drum kit note maps in use.")
        {
            timeout,
        };

        command.SetAction(parse => CommandRoot.RunAsync("playback techniques", async () =>
            await HostClient.CallAsync(
                HostProtocol.OpTechniques,
                null,
                CommandRoot.Timeout(parse, timeout),
                CancellationToken.None)));

        return command;
    }

    private static Command BuildPropertiesGet()
    {
        var timeout = CommandRoot.TimeoutOption();

        var command = new Command("get", "What can be read from the current selection, and its values.")
        {
            timeout,
        };

        command.SetAction(parse => CommandRoot.RunAsync("properties get", async () =>
            await HostClient.CallAsync(
                HostProtocol.OpProperties,
                null,
                CommandRoot.Timeout(parse, timeout),
                CancellationToken.None)));

        return command;
    }
}
