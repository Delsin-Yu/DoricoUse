using System.CommandLine;
using DoricoUse.Cli.Dorico;
using DoricoUse.Cli.Views;

namespace DoricoUse.Cli.Commands;

/// <summary>
/// Reading Dorico files from disk, with no Dorico and no host involved.
/// </summary>
/// <remarks>
/// This is the read-back path the Remote Control API does not have: Dorico can be asked to open and export, but not
/// what a score contains, and nothing at all can be asked when Dorico is not running. A <c>.dorico</c> file is a ZIP
/// package whose <c>score.dtn</c> member holds the notes, and <see cref="ProjectFileScoreReader"/> decodes it — so
/// `file read` works with Dorico closed, and touches neither the host nor the socket.
/// </remarks>
internal static class FileCommands
{
    public static Command Build()
    {
        var file = new Command("file", "Read Dorico files from disk, without Dorico.");
        file.Subcommands.Add(BuildRead());
        return file;
    }

    private static Command BuildRead()
    {
        var path = new Option<string>("--path")
        {
            Description = "Path to a .dorico project file.",
            Required = true,
        };

        var limit = new Option<string>("--limit")
        {
            Description = "How many notes to return: a count, 'all' for every note, or 0 for the shape alone.",
            DefaultValueFactory = _ => ProjectFileView.DefaultNoteLimit.ToString(),
        };

        var measure = new Option<string?>("--measure")
        {
            Description = "Only notes in one measure (12) or a range (12-16). Dynamics are filtered the same way.",
        };

        var player = new Option<string?>("--player")
        {
            Description = "Only notes of players, instruments or staves matching this glob, e.g. *Flute* or Violin.",
        };

        var dynamics = new Option<bool>("--dynamics")
        {
            Description = "Include the dynamic markings: which group each one is, what it is (f, pp, cresc, fz), and "
                + "where it sits.",
        };

        var dynamicsLimit = new Option<string?>("--dynamics-limit")
        {
            Description = "How many dynamics to list: a count, 'all', or -1. Separate from --limit, which stays the "
                + "note count. Default 100.",
        };

        var lyrics = new Option<bool>("--lyrics")
        {
            Description = "Include what the lyric element tables hold: how many there are, how many elements, and the "
                + "field names of an element. Lyrics are table elements, not events, and this reader does not read "
                + "their contents.",
        };

        var tree = new Option<string?>("--tree")
        {
            Description = "Dump the decoded record tree: a node name (e.g. DynamicGroupElementDefinition) shows every "
                + "node with that name, bounded by --depth and --tree-limit; '*' lists every name the file uses with "
                + "how often. Nothing about the file is assumed — this is how to explore a shape this CLI has no code "
                + "for.",
        };

        var depth = new Option<string?>("--depth")
        {
            Description = "How many levels of child nodes --tree shows below a matched node (0-8). Default 2.",
        };

        var treeLimit = new Option<string?>("--tree-limit")
        {
            Description = "How many occurrences, fields and children --tree lists per node: a count, or 'all'/'-1'. "
                + "Default 8, or 200 names for '--tree *'.",
        };

        var command = new Command(
            "read",
            "Report what a .dorico project file contains: players, layouts, notes, dynamics, lyrics.")
        {
            path,
            limit,
            measure,
            player,
            dynamics,
            dynamicsLimit,
            lyrics,
            tree,
            depth,
            treeLimit,
        };

        command.SetAction(parse => CommandRoot.RunAsync("file read", () =>
        {
            var file = ProjectFileReader.Open(parse.GetValue(path)!);
            var limits = ProjectFileView.ParseMeasures(parse.GetValue(measure));

            var options = new ProjectFileView.Options
            {
                NoteLimit = ProjectFileView.ParseLimit(parse.GetValue(limit)),
                FirstMeasure = limits?.First,
                LastMeasure = limits?.Last,
                Player = parse.GetValue(player),
                Dynamics = parse.GetValue(dynamics),
                DynamicLimit = ProjectFileView.ParseOptionalLimit(parse.GetValue(dynamicsLimit), "--dynamics-limit"),
                Lyrics = parse.GetValue(lyrics),
                Tree = parse.GetValue(tree),
                Depth = ProjectFileView.ParseDepth(parse.GetValue(depth)),
                TreeLimit = ProjectFileView.ParseOptionalLimit(parse.GetValue(treeLimit), "--tree-limit"),
            };

            return Task.FromResult(ProjectFileView.Report(file, options));
        }));

        return command;
    }
}
