using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;
using DoricoUse.Cli.Ipc;

namespace DoricoUse.Cli.Dorico;

/// <summary>
/// Makes Dorico write a copy of the OPEN project to a file, so the project-file decoder can be pointed at what is
/// really in the score instead of at a file the user saved by hand.
/// </summary>
/// <remarks>
/// <para>
/// <b>The route is <c>File.SaveAs?File=&lt;path&gt;</c>, and it is the only one.</b> Verified against Dorico
/// 6.2.30: it writes a complete <c>.dorico</c> package with no dialog and no click, in about a second, and Dorico's
/// own log confirms it (<c>Saving document: 5 to H:/…/copy.dorico</c>). The neighbours do not work:
/// </para>
/// <list type="bullet">
/// <item><description>
/// <c>File.Save?File=&lt;path&gt;</c> accepts the parameter and then ignores it: Dorico logs
/// <c>Saving document: 6 to &lt;the document's own file&gt;</c> and writes nothing at the path asked for. It is an
/// in-place save of the active document.
/// </description></item>
/// <item><description>
/// <c>File.Export?FilterID=DoricoFileExportFilter&amp;File=&lt;path&gt;</c> answers <c>kOK</c> immediately, opens the
/// modal 导出谱章 dialog (Dorico's <c>FlowExportDialog</c>), ignores <c>File</c>, writes no file and stalls Dorico's
/// whole command queue until somebody presses 确定 or 取消 — exactly like the MusicXML export.
/// </description></item>
/// </list>
/// <para>
/// <b>SaveAs rebinds the open document.</b> After the call the document, its autosave and its backups all follow the
/// new path, and the window title changes to the new file's name, so a later <c>File.Save</c> writes the copy rather
/// than the file the user opened. That is a side effect of Dorico's own command, not of this helper, and the caller
/// has to say so: <see cref="SaveDocumentNote"/> is the sentence the report carries.
/// </para>
/// <para>
/// <b>Nothing here runs unless a caller asks for it by name.</b> The read this tool does by default is
/// <see cref="LiveRead"/>, which writes no copy and touches nothing; going through a save is the opt-in second route
/// (<c>session read --rebind</c>, <c>note insert --read --read-rebind</c>) and is opt-in because of the rebinding
/// above.
/// </para>
/// </remarks>
internal static class SaveCopy
{
    /// <summary>The one command that saves a copy of the open project to a path this CLI chooses.</summary>
    public const string SaveAsCommand = "File.SaveAs";

    /// <summary>The path parameter of <see cref="SaveAsCommand"/>.</summary>
    public const string PathParameter = "File";

    /// <summary>
    /// Where the copy goes when <c>--to</c> is not given: a per-user folder under the system temporary directory,
    /// so a read never litters the folder the user is working in.
    /// </summary>
    public static string DefaultFolder => Path.Combine(Path.GetTempPath(), "dorico-use", "read");

    /// <summary>What the report says about the document SaveAs rebinds.</summary>
    public const string SaveDocumentNote =
        "File.SaveAs rebinds the open document to the copy: Dorico logs 'Saving document: <id> to <path>', the "
        + "window title follows the new file name, and later File.Save / autosave / backup go to the copy. Nothing "
        + "on disk is lost, but the document being edited now points at the copy, so run this only against a scratch "
        + "project, or re-open the original file afterwards.";

    /// <summary>How long the file is watched before the copy is declared missing or unfinished.</summary>
    public static readonly TimeSpan DefaultWait = TimeSpan.FromSeconds(20);

    /// <summary>The copy on disk, once it stopped growing.</summary>
    public sealed record SavedFile(string Path, long Bytes, long WaitedMs, bool Stable);

    /// <summary>
    /// The absolute path to save to: <c>--to</c> when given, otherwise a timestamped file under
    /// <see cref="DefaultFolder"/>, i.e. in the system temporary directory.
    /// </summary>
    /// <remarks>
    /// Only a caller that is really going to save calls this: the default read (<see cref="LiveRead"/>) writes no
    /// copy, so it must not create a folder — and <see cref="RefuseExisting"/> must not refuse a file — that
    /// nothing was ever going to write to.
    /// </remarks>
    public static string Resolve(string? requested, DateTimeOffset now)
    {
        var candidate = string.IsNullOrWhiteSpace(requested)
            ? Path.Combine(DefaultFolder, now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".dorico")
            : requested.Trim();

        string full;

        try
        {
            full = Path.GetFullPath(candidate);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new CliException("InvalidArgument", $"--to is not a usable path: {ex.Message}", 1);
        }

        var directory = Path.GetDirectoryName(full);

        if (string.IsNullOrEmpty(directory))
        {
            throw new CliException("InvalidArgument", $"--to must name a file, not '{candidate}'.", 1);
        }

        try
        {
            Directory.CreateDirectory(directory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new CliException(
                "InvalidArgument",
                $"The folder for the copy, '{directory}', could not be created: {ex.Message}",
                1);
        }

        return full;
    }

    /// <summary>
    /// Refuses to write over anything that is already there. A save copy is cheap; losing a project because a
    /// command reused a path is not, so an existing file is an error rather than an overwrite.
    /// </summary>
    public static void RefuseExisting(string path)
    {
        if (File.Exists(path))
        {
            throw new CliException(
                "WouldOverwrite",
                $"'{path}' already exists, and this command never overwrites a project file. Pass a different --to "
                + "or delete that file.",
                1)
            {
                Details = new JsonObject { ["path"] = path },
            };
        }
    }

    /// <summary>
    /// Sends one Dorico command through whoever owns the connection: the CLI's host process (the default) or the
    /// host itself, which already holds the connection and must not call back into its own pipe.
    /// </summary>
    /// <param name="name">The Dorico command name.</param>
    /// <param name="parameters">Its parameters, or null for a command without any.</param>
    /// <param name="timeout">How long to wait for the answer.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    public delegate Task<JsonObject> CommandSender(
        string name,
        IReadOnlyList<KeyValuePair<string, string>>? parameters,
        TimeSpan timeout,
        CancellationToken cancellationToken);

    /// <summary>
    /// Asks Dorico to write the open project to <paramref name="path"/> and returns the command's own receipt.
    /// </summary>
    /// <remarks>
    /// The status is deliberately not read back: what proves this command is the file on disk, not a status field,
    /// and the answer is a receipt either way (Dorico answers kOK before it has finished writing).
    /// </remarks>
    public static Task<JsonObject> SaveAsync(
        string path,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        int? expectScore = null)
        => SendThroughHostAsync(
            SaveAsCommand,
            [new KeyValuePair<string, string>(PathParameter, path)],
            timeout,
            cancellationToken,
            expectScore);

    /// <summary>
    /// The same save, sent over a connection the caller already owns. <c>note insert --read --read-rebind</c> uses
    /// this so the save happens inside the host's gated exchange with the notes, and not a round trip later — a
    /// window that changes between the two would make File.SaveAs write somebody else's document.
    /// </summary>
    /// <remarks>
    /// <paramref name="expectScore"/> is not sent over the wire here: this overload exists for a caller that
    /// already owns the live connection (the host itself, for <c>note insert --read --read-rebind</c>), and such a
    /// caller must guard the document with its own status read inside the same exchange. The route through the
    /// host process is <see cref="SaveAsync(string, TimeSpan, CancellationToken, int?)"/>, which does pass the
    /// guard on.
    /// </remarks>
    public static Task<JsonObject> SaveAsync(
        string path,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        int? expectScore,
        CommandSender sender)
    {
        _ = expectScore;

        return sender(
            SaveAsCommand,
            [new KeyValuePair<string, string>(PathParameter, path)],
            timeout,
            cancellationToken);
    }

    /// <summary>The default route: through the host process, which owns the Dorico connection.</summary>
    /// <param name="expectScore">
    /// Passed to the host as the document guard. It must travel with the request: the host refuses the command when
    /// Dorico's active document is not this one, which is the only thing standing between a stale window and
    /// <c>File.SaveAs</c> writing somebody else's project. It is not a Dorico parameter - it is a host-side check.
    /// </param>
    /// <summary>
    /// Sends one Dorico command through the host and returns the host's receipt for it. Used by the live reader,
    /// which needs to run a script and not a save.
    /// </summary>
    /// <param name="name">The Dorico command name.</param>
    /// <param name="parameters">Its parameters.</param>
    /// <param name="timeout">How long to wait for the answer.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <param name="expectScore">
    /// The document guard, handed to the host exactly as <c>SaveAsync</c> hands it over. A live read has no save to
    /// protect, but the script still runs against whichever document is active, so a caller that knows which one it
    /// means says so here and the host refuses the wrong one.
    /// </param>
    public static Task<JsonObject> SendCommandAsync(
        string name,
        IReadOnlyList<KeyValuePair<string, string>> parameters,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        int? expectScore = null)
        => SendThroughHostAsync(name, parameters, timeout, cancellationToken, expectScore);

    private static async Task<JsonObject> SendThroughHostAsync(
        string name,
        IReadOnlyList<KeyValuePair<string, string>>? parameters,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        int? expectScore = null)
    {
        var parameterJson = new JsonObject();

        foreach (var (key, value) in parameters ?? [])
        {
            parameterJson[key] = value;
        }

        var args = new JsonObject
        {
            ["name"] = name,
            ["parameters"] = parameterJson,
            ["waitMs"] = 0,
            ["verify"] = false,
        };

        if (expectScore is not null)
        {
            args["expectScore"] = expectScore.Value;
        }

        var payload = await HostClient
            .CallAsync(HostProtocol.OpCommand, args, timeout, cancellationToken)
            .ConfigureAwait(false);

        // The host wraps a command answer as {"result":{name,parameters,ack,roundTripMs}}.
        return NodeRead.Obj(payload, "result") ?? payload;
    }

    /// <summary>
    /// Waits until the copy exists and has stopped growing. Dorico answers <c>kOK</c> before it has written the
    /// last byte, so parsing straight after the answer would read a half-finished ZIP.
    /// </summary>
    public static async Task<SavedFile> WaitForFileAsync(
        string path,
        TimeSpan budget,
        CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        var lastSize = -1L;
        var deadline = DateTimeOffset.UtcNow + budget;

        while (DateTimeOffset.UtcNow < deadline)
        {
            var size = Size(path);

            if (size > 0)
            {
                if (size == lastSize)
                {
                    return new SavedFile(path, size, Elapsed(started), Stable: true);
                }

                lastSize = size;
            }

            try
            {
                await Task.Delay(400, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        return new SavedFile(path, Size(path), Elapsed(started), Stable: false);
    }

    /// <summary>
    /// The size of the file, or 0 while it is not there. A file another process has open for writing cannot be
    /// read here, and that is fine: the size is what tells whether it is still growing.
    /// </summary>
    private static long Size(string path)
    {
        try
        {
            var info = new FileInfo(path);

            return info.Exists ? info.Length : 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    private static long Elapsed(long startedTimestamp)
        => (long)Stopwatch.GetElapsedTime(startedTimestamp).TotalMilliseconds;
}
