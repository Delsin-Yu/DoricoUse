using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;
using System.Xml.Linq;
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
/// <para>
/// Because the destination lives in that dialog and nowhere in the API, the command also reads Dorico's own
/// configuration (<c>preferences.xml</c>) and reports the folder the dialog will open at, plus how to change it:
/// the dialog's 导出到 (Export to) label is filled from <c>defaultProjectPath</c> for a project that has never been
/// saved, and from the project's own folder for one that has. <c>--path</c> is a <b>watch</b> folder in this
/// command, which the payload says outright (<c>pathRole</c>) — for <c>export text</c> it is the output file,
/// because that export does honour <c>File=</c>.
/// </para>
/// </remarks>
internal static class ExportView
{
    /// <summary>The value of <c>File.Export?FilterID=</c> that selects Dorico's MusicXML exporter.</summary>
    public const string MusicXmlFilterId = "MusicXMLExportFilter";

    /// <summary>Dorico's own text exporter: the only export that runs without a dialog.</summary>
    public const string TextCommand = "File.Export.Text";

    /// <summary>
    /// What <c>--path</c> means for each of the two exports. MusicXML cannot be told where to write, so its
    /// <c>--path</c> can only be a folder to watch; the text export honours <c>File=</c>, so there it is the file.
    /// </summary>
    public const string MusicXmlPathRole = "watch";

    public const string TextPathRole = "output-file";

    /// <summary>Said in the payload next to <c>pathRole</c>, so nobody reads the watch folder as a destination.</summary>
    public const string MusicXmlPathRoleNote =
        "--path is the folder dorico-use WATCHES for the files Dorico writes, not the export destination. "
        + "Dorico decides the destination inside its 导出MusicXML dialog (the 导出到 label, filled from "
        + "defaultProjectPath or from the project's own folder) and the Remote Control API has no parameter for "
        + "it, so nothing a client sends can change where the export lands. Point --path at the folder the dialog "
        + "shows, or set the dialog to --path with its '...' picker.";

    /// <summary>How to change the destination, in one sentence, for payloads and error messages.</summary>
    public const string DestinationChangeHow =
        "The destination is set in the dialog, not through the API: press the '...' button next to the 导出到 "
        + "(Export to) label, pick or type the folder, accept the picker, then press 确定. That label is a label, "
        + "not an editable field, and File.Export declares FilterID alone (a stray File= is ignored), so no client "
        + "can set this path. A picker choice lasts for that one export — the next dialog opens at the document's "
        + "own folder again (observed) — and pressing 取消 writes nothing.";

    /// <summary>What Dorico's <c>defaultProjectPath</c> preference means for an export, and its limit.</summary>
    public const string DestinationMeaning =
        "Where Dorico's 导出MusicXML dialog writes, as far as a client can know: its 导出到 label is filled with "
        + "the folder of the active document's own file. Measured on this machine: the project whose file is in "
        + "C:\\scores opened the dialog at 'C:/scores'; the document saved as .spinney\\myread\\read\\"
        + "saveas2.dorico opened at '<repo>/.spinney/myread/read'; the one in .spinney\\w1\\t1.dorico "
        + "opened at '<repo>/.spinney/w1'. For a project that has never been saved there is no such "
        + "folder, and the label is filled from defaultProjectPath instead (the untitled project in this session "
        + "opened at 'C:/Users/<you>/Documents/Dorico 工程', this very value). The API exposes no document "
        + "path at all, so the dialog's label is the only authority and this value is what an unsaved project will "
        + "really use.";

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
    /// What one export's watch over a folder saw: the files, how many times the folder was read, and how long the
    /// reading took. <paramref name="WaitedMs"/> is the budget that was asked for, so <c>--wait-ms 0</c> shows up
    /// as one check and no budget rather than as a fast failure that pretended to wait.
    /// </summary>
    public readonly record struct WatchResult(List<FileStamp> Files, int Checks, int WaitedMs, long ElapsedMs);

    /// <summary>
    /// Dorico's own settings, read from disk. They are the only place a client can learn anything about where the
    /// export dialog will write, since the API exposes no path of any kind.
    /// </summary>
    /// <param name="PreferencesFile">The <c>preferences.xml</c> that was read, or null when none was found.</param>
    /// <param name="DefaultProjectPath">The value of <c>defaultProjectPath</c>, or null when it is empty/unreadable.</param>
    /// <param name="Problem">Why there is no value, in words, when there is none.</param>
    public readonly record struct DoricoPreferences(
        string? PreferencesFile,
        string? DefaultProjectPath,
        string? Problem)
    {
        /// <summary>The preference key the export dialog's 导出到 label comes from for an unsaved project.</summary>
        public const string DestinationKey = "defaultProjectPath";

        /// <summary>
        /// Reads the newest <c>%APPDATA%\Steinberg\Dorico *\preferences.xml</c>. Every failure is reported, never
        /// thrown: a missing Dorico installation, a corrupt file or an empty value must not stop an export from
        /// being attempted, it must only make the CLI say less.
        /// </summary>
        public static DoricoPreferences Load()
        {
            var root = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Steinberg");

            string? newest = null;
            var newestVersion = -1;

            try
            {
                if (Directory.Exists(root))
                {
                    foreach (var folder in Directory.EnumerateDirectories(root, "Dorico*"))
                    {
                        var candidate = Path.Combine(folder, "preferences.xml");

                        if (!File.Exists(candidate))
                        {
                            continue;
                        }

                        // "Dorico 6" -> 6, "Dorico 5.1" -> 5: the newest install is the one that is running.
                        var version = VersionOf(Path.GetFileName(folder));

                        if (version > newestVersion)
                        {
                            newestVersion = version;
                            newest = candidate;
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                return new DoricoPreferences(null, null, $"Could not list '{root}': {ex.Message}");
            }

            if (newest is null)
            {
                return new DoricoPreferences(
                    null,
                    null,
                    $"No Dorico preferences.xml under '{root}': Dorico has not run for this user, or it is "
                    + "installed elsewhere.");
            }

            try
            {
                var document = XDocument.Load(newest);
                var value = document
                    .Descendants(DestinationKey)
                    .Elements("value")
                    .Select(element => element.Value.Trim())
                    .FirstOrDefault(text => text.Length > 0);

                return value is null
                    ? new DoricoPreferences(newest, null, $"{DestinationKey} is missing or empty in '{newest}'.")
                    : new DoricoPreferences(newest, value, null);
            }
            catch (Exception ex) when (ex is System.Xml.XmlException or IOException or UnauthorizedAccessException)
            {
                return new DoricoPreferences(newest, null, $"Could not read '{newest}': {ex.Message}");
            }
        }

        /// <summary>The first integer in a folder name such as <c>Dorico 6</c>; -1 when there is none.</summary>
        private static int VersionOf(string folderName)
        {
            var digits = new string(folderName.SkipWhile(c => !char.IsDigit(c)).TakeWhile(char.IsDigit).ToArray());

            return int.TryParse(digits, NumberStyles.Integer, CultureInfo.InvariantCulture, out var version)
                ? version
                : -1;
        }

        /// <summary>The configured folder, or null when there is nothing to report.</summary>
        public string? Folder => DefaultProjectPath;

        /// <summary>One sentence naming the folder and where it came from, for an error message.</summary>
        public string Describe() => DefaultProjectPath is { } path
            ? $"'{path}' (defaultProjectPath in {PreferencesFile})"
            : $"unknown ({Problem ?? "no defaultProjectPath could be read"})";
    }

    /// <summary>
    /// The folder to watch, and where the choice came from. A <c>--path</c> that names a file means "the folder
    /// that file is in", because Dorico names the exported files itself from the project, flow and layout. The
    /// folder is created if it is missing, so that whoever confirms the dialog has somewhere to point it.
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

    /// <summary>
    /// The folder to watch and where it came from: <c>argument</c> for an explicit <c>--path</c>,
    /// <c>dorico-preferences</c> for Dorico's configured destination, <c>cwd</c> when neither is available.
    /// </summary>
    /// <remarks>
    /// Without a <c>--path</c> the watch should start where the dialog will write, so Dorico's configured
    /// destination is used when that folder exists. It is deliberately not created: the CLI does not make folders
    /// in a user's Documents just because it was asked to watch one, and a watch folder that does not exist has
    /// nothing to show either way.
    /// </remarks>
    public static (string Directory, string Source) ResolveWatchDirectory(
        string? path,
        DoricoPreferences preferences)
    {
        if (!string.IsNullOrWhiteSpace(path))
        {
            return (ResolveDirectory(path), "argument");
        }

        if (preferences.Folder is { } configured)
        {
            try
            {
                if (Directory.Exists(configured))
                {
                    return (Path.GetFullPath(configured), "dorico-preferences");
                }
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                // A malformed preference is reported in the payload; the watch simply falls back to the cwd.
            }
        }

        return (Directory.GetCurrentDirectory(), "cwd");
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
    /// One reading of the folder, as the files the snapshot did not contain or that changed since it was taken.
    /// Empty files are ignored: a zero-length .musicxml is a write in progress, not a result.
    /// </summary>
    private static List<FileStamp> ScanOnce(
        string directory,
        string[] patterns,
        Dictionary<string, FileStamp> before)
    {
        var candidates = new List<FileStamp>();

        foreach (var file in Enumerate(directory, patterns))
        {
            if (Describe(file) is not { Bytes: > 0 } current)
            {
                continue;
            }

            if (before.TryGetValue(current.Path, out var previous)
                && previous.Bytes == current.Bytes
                && previous.Modified == current.Modified)
            {
                continue;
            }

            candidates.Add(current);
        }

        return candidates;
    }

    /// <summary>
    /// Reads the folder exactly once and reports what is there, with no waiting at all. This is what
    /// <c>--wait-ms 0</c> means: look now, answer now, and let the caller decide. A file still being written is
    /// reported at the size it currently has, which is the price of not waiting.
    /// </summary>
    public static WatchResult CheckOnce(string directory, string[] patterns, Dictionary<string, FileStamp> before)
    {
        var started = Stopwatch.GetTimestamp();
        var files = ScanOnce(directory, patterns, before);

        files.Sort((left, right) => string.Compare(left.Name, right.Name, StringComparison.OrdinalIgnoreCase));

        return new WatchResult(files, 1, 0, (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds);
    }

    /// <summary>
    /// Waits for files the snapshot did not contain, or that changed since it was taken, and returns them once
    /// their size has stopped moving for <see cref="QuietPeriod"/>. Empty files are ignored: a zero-length
    /// .musicxml is a write in progress, not a result.
    /// </summary>
    public static async Task<WatchResult> WaitAsync(
        string directory,
        string[] patterns,
        Dictionary<string, FileStamp> before,
        TimeSpan budget,
        CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        var deadline = DateTime.UtcNow + budget;
        var stable = new Dictionary<string, FileStamp>(StringComparer.OrdinalIgnoreCase);
        var found = new Dictionary<string, FileStamp>(StringComparer.OrdinalIgnoreCase);
        var lastChange = DateTime.UtcNow;
        var checks = 0;

        while (true)
        {
            checks++;
            var changed = false;

            foreach (var current in ScanOnce(directory, patterns, before))
            {
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

        return new WatchResult(
            [.. found.Values.OrderBy(stamp => stamp.Name, StringComparer.OrdinalIgnoreCase)],
            checks,
            (int)budget.TotalMilliseconds,
            (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds);
    }

    /// <summary>The MusicXML report: the receipt, the watched folder, the destination, and the files that are
    /// really there.</summary>
    public static JsonObject MusicXmlReport(
        JsonObject acknowledgement,
        WatchResult watch,
        string directory,
        string pathSource,
        DoricoPreferences preferences,
        JsonObject commandQueue)
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
                ["pathRole"] = MusicXmlPathRole,
                ["pathRoleNote"] = MusicXmlPathRoleNote,
                ["pathSource"] = pathSource,
                ["waitedMs"] = watch.WaitedMs,
                ["checks"] = watch.Checks,
                ["watchElapsedMs"] = watch.ElapsedMs,
                ["count"] = watch.Files.Count,
                ["files"] = Files(watch.Files),
                ["destination"] = DestinationJson(preferences),
                ["commandQueue"] = commandQueue,
                ["confirmation"] = Confirmation(directory, preferences),
            },
        };
    }

    /// <summary>
    /// Where Dorico will write and how to change it, as JSON. Reported on every MusicXML export, so an agent that
    /// gets no files still learns which folder to look at next.
    /// </summary>
    public static JsonObject DestinationJson(DoricoPreferences preferences)
    {
        var path = preferences.Folder;
        var destination = new JsonObject
        {
            ["path"] = path,
            ["key"] = DoricoPreferences.DestinationKey,
            ["source"] = preferences.PreferencesFile is null ? null : "preferences.xml",
            ["preferencesFile"] = preferences.PreferencesFile,
            ["folderExists"] = path is null ? null : Directory.Exists(path),
            ["meaning"] = DestinationMeaning,
            ["changeHow"] = DestinationChangeHow,
        };

        if (preferences.Problem is not null)
        {
            destination["problem"] = preferences.Problem;
        }

        return destination;
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
                ["pathRole"] = TextPathRole,
                ["pathRoleNote"] = "File.Export.Text honours File=, so for this export --path really is where "
                    + "Dorico writes: no dialog and no destination to choose. It forces the .csv extension, which "
                    + "is why this is the path that came out.",
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
    /// <param name="commandQueue">
    /// The stall probe's verdict, when there is one: a stalled session is appended to the message, because a
    /// timeout on the agent's next request would otherwise be the first sign of it.
    /// </param>
    public static string PendingMessage(
        string directory,
        int waitedMs,
        string pathSource,
        DoricoPreferences preferences,
        JsonObject? commandQueue = null)
    {
        var budget = waitedMs == 0
            ? "one look at the folder (--wait-ms 0 asks for no waiting, so the dialog may not even have appeared "
                + "yet)"
            : $"{waitedMs} ms";

        var message = $"Dorico answered kOK to File.Export?FilterID={MusicXmlFilterId}, which is a receipt for "
        + "posting the command and not proof that it ran, and nothing appeared in the watched folder "
        + $"'{directory}' after {budget}: no file was written. "
        + $"--path ('{directory}', chosen by {pathSource}) is a WATCH folder, not the export destination; the "
        + "destination is the 导出到 (Export to) label inside Dorico's 导出MusicXML dialog, which by default is the "
        + $"folder of the project's own file, and {preferences.Describe()} when the project has never been saved. "
        + DestinationChangeHow + " "
        + "Then either point --path at the folder that label shows, or use the picker to set it to the folder you "
        + $"want watched and re-run with a longer --wait-ms (e.g. {DefaultMusicXmlWaitMs}) so dorico-use is "
        + "watching while you confirm. Nor can the dialog be dismissed from here: while it is open Dorico's whole "
        + "command queue is stalled and other clients' commands pile up behind it, so press 确定 or 取消. "
        + "Driving that dialog with a posted click on 确定 while this command waits is the one "
        + "fully automatic route.";

        if (NodeRead.Str(commandQueue, "verdict") == "stalled")
        {
            message += $" AND Dorico is not even answering cheap questions: a getstatus sent right after the export "
            + $"did not come back within {NodeRead.Long(commandQueue, "timeoutMs") ?? 0} ms "
            + $"({NodeRead.Str(commandQueue, "code") ?? "timeout"}), so a modal dialog is probably open in the "
            + "Dorico window right now and nothing will run until it is answered or cancelled.";
        }

        return message;
    }

    /// <summary>What a caller has to check when the text export wrote nothing.</summary>
    public static string TextFailureMessage(string written)
        => $"Dorico answered kOK, which is a receipt and not proof: no file appeared at '{written}' within the "
        + "wait budget. Check that the folder exists and that a project is open, then look at Dorico's log "
        + "(%APPDATA%\\Steinberg\\Dorico 6\\application.log) for the File.Export.Text line.";

    private static string Confirmation(string directory, DoricoPreferences preferences)
        => "Confirm Dorico's 导出MusicXML dialog: its 导出到 label opens at the folder of the project's own file "
        + $"(or at {preferences.Describe()} when the project has never been saved), so change it with the '...' "
        + $"picker if the export should land in '{directory}' instead, pick the flows and layouts, and press 确定. "
        + "Dorico names the files itself (<project> - <layout> - <flow>.musicxml, or .mxl for compressed) and "
        + "puts them in a subfolder when 为导出的文件创建文件夹 is ticked, so --path names a folder to watch, never "
        + "a file name.";

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
