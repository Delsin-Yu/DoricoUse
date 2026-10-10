using System.Globalization;
using System.Text.Json.Nodes;
using DoricoUse.Cli.Dorico;
using DoricoUse.Cli.Json;

namespace DoricoUse.Cli.Views;

/// <summary>
/// What an export left on disk, and what Dorico said while it was leaving it.
/// </summary>
/// <remarks>
/// <para>
/// Only one of the two routes is unattended. <c>File.Export.Text?File=&amp;Show=false</c> writes its CSV in about
/// 200 ms with no dialog, so the command can send it and then prove the file exists. MusicXML cannot do that:
/// <c>File.Export?FilterID=MusicXMLExportFilter</c> answers <c>kOK</c> immediately, opens Dorico's
/// <c>FlowExportDialog</c>, and writes nothing until somebody presses 确定 (OK) in the Dorico window. Dorico's
/// command surface has no parameter for that dialog's destination (the declared parameters of <c>File.Export</c>
/// are <c>FilterID</c> alone, and a stray <c>File=</c> is logged and ignored) and there is no way to press its
/// button from the API: a command sent while the dialog is up is queued, not executed.
/// </para>
/// <para>
/// So <c>export musicxml</c> sends the export, watches the folder it was given for as long as it is told to, and
/// reports the files that really appeared there. It never claims an export it did not see.
/// </para>
/// </remarks>
internal static class ExportView
{
    /// <summary>The value of <c>File.Export?FilterID=</c> that selects Dorico's MusicXML exporter.</summary>
    public const string MusicXmlFilterId = "MusicXMLExportFilter";

    /// <summary>Dorico's own text exporter: the only export that runs without a dialog.</summary>
    public const string TextCommand = "File.Export.Text";

    /// <summary>
    /// The two suffixes the MusicXML exporter can produce: uncompressed (the dialog's default tab) and the
    /// compressed <c>.mxl</c> one.
    /// </summary>
    public static readonly string[] MusicXmlPatterns = ["*.musicxml", "*.mxl"];

    public const int DefaultMusicXmlWaitMs = 15_000;
    public const int DefaultTextWaitMs = 5_000;

    /// <summary>How long the folder must stop changing before a write is called finished.</summary>
    private static readonly TimeSpan QuietPeriod = TimeSpan.FromMilliseconds(400);

    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(200);

    /// <summary>One file found in the watched folder.</summary>
    public readonly record struct FileStamp(string Name, string Path, long Bytes, DateTime Modified);

    /// <summary>
    /// The folder to watch. A <c>--path</c> that names a file means "the folder that file is in", because Dorico
    /// names the exported files itself from the project, flow and layout. The folder is created if it is missing,
    /// so that whoever confirms the dialog has somewhere to point it.
    /// </summary>
    public static string ResolveDirectory(string? path)
    {
        var requested = string.IsNullOrWhiteSpace(path) ? Directory.GetCurrentDirectory() : path;

        string directory;

        try
        {
            directory = File.Exists(requested)
                ? Path.GetDirectoryName(Path.GetFullPath(requested)) ?? Directory.GetCurrentDirectory()
                : Path.GetFullPath(requested);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new CliException("InvalidArgument", $"--path is not a usable path: {ex.Message}", 1);
        }

        try
        {
            Directory.CreateDirectory(directory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            throw new CliException(
                "InvalidArgument",
                $"--path '{directory}' cannot be used as an export folder: {ex.Message}",
                1);
        }

        return directory;
    }

    /// <summary>Resolves <c>--path</c> for the text export, which writes one file and needs its name.</summary>
    public static string ResolveTextFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new CliException(
                "InvalidArgument",
                "export text needs --path: Dorico writes one CSV per export and names it from the path you give.",
                1);
        }

        if (Directory.Exists(path))
        {
            throw new CliException(
                "InvalidArgument",
                $"--path '{path}' is a directory; export text needs the path of the file to write, "
                + "e.g. --path exports/score.csv.",
                1);
        }

        string full;

        try
        {
            full = Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new CliException("InvalidArgument", $"--path is not a usable path: {ex.Message}", 1);
        }

        var directory = Path.GetDirectoryName(full);

        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
        {
            throw new CliException(
                "InvalidArgument",
                $"The folder '{directory}' does not exist. Dorico answers kOK to File.Export.Text with a path it "
                + "cannot write and then writes nothing, so create the folder first.",
                1);
        }

        return full;
    }

    /// <summary>What <c>File.Export.Text?File=</c> really writes: Dorico forces the extension to .csv.</summary>
    public static string TextOutputPath(string requested)
        => Path.ChangeExtension(requested, ".csv");

    /// <summary>Reads the folder for export output. Subfolders are included, because the dialog can be told to
    /// create one per export.</summary>
    public static Dictionary<string, FileStamp> Snapshot(string directory, string[] patterns)
    {
        var stamps = new Dictionary<string, FileStamp>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in Enumerate(directory, patterns))
        {
            if (Describe(file) is { } stamp)
            {
                stamps[stamp.Path] = stamp;
            }
        }

        return stamps;
    }

    /// <summary>
    /// Waits for files the snapshot did not contain, or that changed since it was taken, and returns them once
    /// their size has stopped moving for <see cref="QuietPeriod"/>. Empty files are ignored: a zero-length
    /// .musicxml is a write in progress, not a result.
    /// </summary>
    public static async Task<List<FileStamp>> WaitAsync(
        string directory,
        string[] patterns,
        Dictionary<string, FileStamp> before,
        TimeSpan budget,
        CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + budget;
        var stable = new Dictionary<string, FileStamp>(StringComparer.OrdinalIgnoreCase);
        var found = new Dictionary<string, FileStamp>(StringComparer.OrdinalIgnoreCase);
        var lastChange = DateTime.UtcNow;

        while (true)
        {
            var changed = false;

            foreach (var file in Enumerate(directory, patterns))
            {
                var stamp = Describe(file);

                if (stamp is not { Bytes: > 0 } current)
                {
                    continue;
                }

                if (before.TryGetValue(current.Path, out var previous)
                    && previous.Bytes == current.Bytes
                    && previous.Modified == current.Modified)
                {
                    continue;
                }

                // Two polls with the same size and timestamp: the writer has finished with this file.
                if (stable.TryGetValue(current.Path, out var seen)
                    && seen.Bytes == current.Bytes
                    && seen.Modified == current.Modified)
                {
                    if (!found.ContainsKey(current.Path))
                    {
                        found[current.Path] = current;
                        changed = true;
                    }
                }
                else
                {
                    stable[current.Path] = current;
                }
            }

            var now = DateTime.UtcNow;

            if (changed)
            {
                lastChange = now;
            }

            if (found.Count > 0 && now - lastChange >= QuietPeriod)
            {
                break;
            }

            if (now >= deadline)
            {
                break;
            }

            var remaining = deadline - now;
            await Task.Delay(remaining < PollInterval ? remaining : PollInterval, cancellationToken)
                .ConfigureAwait(false);
        }

        return [.. found.Values.OrderBy(stamp => stamp.Name, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>The MusicXML report: the receipt, the folder, and the files that are really there.</summary>
    public static JsonObject MusicXmlReport(
        JsonObject acknowledgement,
        string directory,
        int waitedMs,
        List<FileStamp> files)
    {
        return new JsonObject
        {
            ["export"] = new JsonObject
            {
                ["format"] = "MusicXML",
                ["filterId"] = MusicXmlFilterId,
                ["unattended"] = false,
                ["command"] = NodeRead.Str(acknowledgement, "name") ?? "File.Export",
                ["parameters"] = NodeRead.Obj(acknowledgement, "parameters")?.DeepClone(),
                ["ack"] = NodeRead.Str(acknowledgement, "ack"),
                ["roundTripMs"] = NodeRead.Long(acknowledgement, "roundTripMs"),
                ["path"] = directory,
                ["waitedMs"] = waitedMs,
                ["count"] = files.Count,
                ["files"] = Files(files),
                ["confirmation"] = Confirmation(directory),
            },
        };
    }

    /// <summary>The text report: the CSV, its header and how many rows it carries.</summary>
    public static JsonObject TextReport(
        JsonObject acknowledgement,
        string requested,
        string written,
        int waitedMs,
        FileStamp? file)
    {
        var summary = file is null ? (Header: (string?)null, Rows: 0) : Csv(written);

        return new JsonObject
        {
            ["export"] = new JsonObject
            {
                ["format"] = "Text (CSV)",
                ["unattended"] = true,
                ["command"] = TextCommand,
                ["parameters"] = NodeRead.Obj(acknowledgement, "parameters")?.DeepClone(),
                ["ack"] = NodeRead.Str(acknowledgement, "ack"),
                ["roundTripMs"] = NodeRead.Long(acknowledgement, "roundTripMs"),
                ["requested"] = requested,
                ["path"] = written,
                ["name"] = file?.Name,
                ["bytes"] = file?.Bytes,
                ["modified"] = file is null ? null : Timestamp(file.Value.Modified),
                ["header"] = summary.Header,
                ["rows"] = summary.Rows,
                ["waitedMs"] = waitedMs,
                ["count"] = file is null ? 0 : 1,
                ["files"] = file is null ? [] : Files([file.Value], _ => "Text (CSV)"),
            },
        };
    }

    /// <summary>What a caller has to do to a MusicXML export it has not seen land.</summary>
    public static string PendingMessage(string directory, int waitedMs)
        => $"Dorico accepted File.Export?FilterID={MusicXmlFilterId} (it answers kOK immediately) but no "
        + $".musicxml or .mxl file appeared in '{directory}' while watching for {waitedMs} ms, so nothing was "
        + "written. "
        + "MusicXML export is not unattended: the command opens Dorico's 'Export MusicXML' (导出MusicXML) dialog "
        + "and finishes only when somebody presses 确定 in that window, and the Remote Control API has neither a "
        + "parameter for the destination nor a way to press the button. In Dorico, set 导出到 (Export to) to "
        + $"'{directory}' and press 确定, then re-run this command with a longer --wait-ms to have dorico-use watch "
        + "the folder and report the files. Driving the dialog with a posted click on 确定 while "
        + "this command waits is the one fully automatic route.";

    /// <summary>What a caller has to check when the text export wrote nothing.</summary>
    public static string TextFailureMessage(string written)
        => $"Dorico answered kOK, which is a receipt and not proof: no file appeared at '{written}' within the "
        + "wait budget. Check that the folder exists and that a project is open, then look at Dorico's log "
        + "(%APPDATA%\\Steinberg\\Dorico 6\\application.log) for the File.Export.Text line.";

    private static string Confirmation(string directory)
        => "Confirm Dorico's 'Export MusicXML' dialog: choose " + directory
        + " as the destination, pick the flows and layouts, and press 确定. Dorico names the files itself "
        + "(<project> - <layout> - <flow>.musicxml), so --path names a folder, never a file name.";

    private static JsonArray Files(List<FileStamp> files)
        => Files(files, Format);

    private static JsonArray Files(List<FileStamp> files, Func<string, string> describe)
    {
        var array = new JsonArray();

        foreach (var file in files)
        {
            JsonArr.AddNode(array, new JsonObject
            {
                ["name"] = file.Name,
                ["path"] = file.Path,
                ["bytes"] = file.Bytes,
                ["modified"] = Timestamp(file.Modified),
                ["format"] = describe(file.Name),
            });
        }

        return array;
    }

    private static string Format(string name)
        => Path.GetExtension(name).Equals(".mxl", StringComparison.OrdinalIgnoreCase)
            ? "MusicXML (compressed)"
            : "MusicXML";

    /// <summary>The CSV header and its row count, which is what makes a text export worth looking at.</summary>
    private static (string? Header, int Rows) Csv(string path)
    {
        try
        {
            string? header = null;
            var rows = 0;

            foreach (var line in File.ReadLines(path))
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                if (header is null)
                {
                    header = line;
                }
                else
                {
                    rows++;
                }
            }

            return (header, rows);
        }
        catch (IOException)
        {
            return (null, 0);
        }
        catch (UnauthorizedAccessException)
        {
            return (null, 0);
        }
    }

    private static string Timestamp(DateTime value)
        => value.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);

    private static FileStamp? Describe(string file)
    {
        try
        {
            var info = new FileInfo(file);

            return new FileStamp(info.Name, info.FullName, info.Length, info.LastWriteTime);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>The folder, then one level of subfolders: the dialog can be told to create one per export.</summary>
    private static IEnumerable<string> Enumerate(string directory, string[] patterns)
    {
        var folders = new List<string> { directory };

        try
        {
            folders.AddRange(Directory.EnumerateDirectories(directory));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A folder we cannot list simply contributes nothing.
        }

        foreach (var folder in folders)
        {
            foreach (var pattern in patterns)
            {
                IEnumerable<string> matches;

                try
                {
                    matches = Directory.EnumerateFiles(folder, pattern, SearchOption.TopDirectoryOnly);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    continue;
                }

                foreach (var match in matches)
                {
                    yield return match;
                }
            }
        }
    }
}
