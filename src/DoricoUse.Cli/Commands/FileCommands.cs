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
            Description = "Only notes in one measure (12) or a range (12-16).",
        };

        var player = new Option<string?>("--player")
        {
            Description = "Only notes of players, instruments or staves matching this glob, e.g. *Flute* or Violin.",
        };

        var command = new Command("read", "Report what a .dorico project file contains: players, layouts, notes.")
        {
            path,
            limit,
            measure,
            player,
        };

        command.SetAction(parse => CommandRoot.RunAsync("file read", () =>
        {
            var file = ProjectFileReader.Open(parse.GetValue(path)!);
            var limits = ProjectFileView.ParseMeasures(parse.GetValue(measure));

            var options = new ProjectFileView.Options(
                ProjectFileView.ParseLimit(parse.GetValue(limit)),
                limits?.First,
                limits?.Last,
                parse.GetValue(player));

            return Task.FromResult(ProjectFileView.Report(file, options));
        }));

        return command;
    }
}
