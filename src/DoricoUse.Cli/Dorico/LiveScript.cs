using System.Text;
using System.Text.Json.Nodes;
using DoricoUse.Cli.Ui;

namespace DoricoUse.Cli.Dorico;

/// <summary>
/// The plumbing every live read shares: write a Lua script, hand it to Dorico, wait for the file it writes, and
/// say what the acknowledgement is worth.
/// </summary>
/// <remarks>
/// <para>
/// The scripting layer is the only channel that can see the <b>running</b> document (see
/// <see cref="LiveRead"/> for why that matters and <c>docs/scripting.md</c> for what the layer exposes), so more
/// than one read uses it. What is shared here is only the exchange; each read owns its own script text and its own
/// parser, which is what keeps a printer-format change in one read from touching the other.
/// </para>
/// <para>
/// The ack proves nothing. Dorico answers <c>kOK</c> to a <c>RunScript</c> that dies on its first line and
/// <c>ShowConsole=false</c> leaves a Lua error nowhere to go, and it answers <c>kOK</c> just as readily while a
/// modal dialog holds its command queue, in which case the script runs minutes later. The proof is the file.
/// </para>
/// </remarks>
internal static class LiveScript
{
    /// <summary>How long an output file is allowed to take, in total, after the script was sent.</summary>
    /// <remarks>
    /// This is the budget for a script that can run. Measured twice: a script behind a 45–55 s command was posted
    /// and executed 30.1 s later, and <c>Instrument.MoveToPlayer</c> takes 54.8 s, so a fixed budget cannot cover
    /// the case this bound exists for. That is what <see cref="QueuedBudget"/> is for — this one is the bound for a
    /// script the log does not name at all.
    /// </remarks>
    public static readonly TimeSpan DefaultWait = TimeSpan.FromSeconds(20);

    /// <summary>
    /// How long the wait may last once Dorico's own log says the queue is busy.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The measured lateness is a property of whatever holds the queue, not of this read: 30.1 s behind a file
    /// picker that was answered late (measured, 2026-10-11, script posted 10:04:50.116, executed 10:05:20.180),
    /// 54.8 s for <c>Instrument.MoveToPlayer</c> and 52.7 s for a transposition toggle, both from the earlier
    /// session's log. The bound is an <b>inference</b> from those numbers with a wide margin, not a measurement of
    /// its own, and it is only ever reached while the log shows a posting with no matching execution — a read whose
    /// script is not queued never sees it.
    /// </para>
    /// <para>
    /// It is bounded rather than open-ended on purpose: an agent that waits forever is worse off than one that gets
    /// told the queue is stuck, so a read that hits this says which commands were still pending.
    /// </para>
    /// </remarks>
    public static readonly TimeSpan QueuedBudget = TimeSpan.FromSeconds(180);

    /// <summary>Where these scripts live and write, under the system temporary directory. Never inside the repo.</summary>
    public static string Folder { get; } = Path.Combine(Path.GetTempPath(), "dorico-use");

    /// <summary>Writes the script where Dorico can read it, as UTF-8 without a BOM.</summary>
    public static async Task WriteAsync(string scriptPath, string scriptText, CancellationToken cancellationToken)
    {
        var folder = Path.GetDirectoryName(scriptPath);

        if (string.IsNullOrEmpty(folder))
        {
            throw new CliException("RuntimeError", "The temporary folder could not be determined.", 2);
        }

        Directory.CreateDirectory(folder);
        await File.WriteAllTextAsync(scriptPath, scriptText, new UTF8Encoding(false), cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Deletes a script after it has run; a leftover in the temporary folder is harmless either way.</summary>
    public static void Forget(string scriptPath)
    {
        try
        {
            File.Delete(scriptPath);
        }
        catch (IOException)
        {
            // Nothing to do about it, and nothing depends on it.
        }
    }

    /// <summary>
    /// Waits for the file the script writes and returns its lines, together with the dialog that was up while it
    /// was waited for. That wait is the one place a dialog is visible on this route: the script is queued behind
    /// one, and the file then never appears.
    /// </summary>
    /// <param name="outputPath">The file the script writes.</param>
    /// <param name="timeout">How long to watch for it.</param>
    /// <param name="what">What the read was, for the failure message.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    public static async Task<(string[] Lines, JsonObject? Dialog)> WaitForOutputAsync(
        string outputPath,
        TimeSpan timeout,
        string what,
        CancellationToken cancellationToken)
    {
        var started = DateTimeOffset.UtcNow;
        var deadline = started + timeout;
        var queued = 0;
        var quiet = 0;
        long previous = -1;
        JsonObject? dialog = null;

        while (DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(100, cancellationToken).ConfigureAwait(false);

            // The script's output is late rather than missing when Dorico's own queue is busy, and the log is the
            // only thing that says so: the command's ack came back long before it ran. The bound therefore moves
            // out while the log shows a posting with no matching execution, instead of the read reporting a failure
            // that is not one.
            var pending = LogQueue.PendingSinceStart();
            queued = pending.Count;

            if (queued > 0)
            {
                var extended = started + QueuedBudget;

                if (extended > deadline)
                {
                    deadline = extended;
                }
            }

            dialog ??= DialogSurface.Peek();

            if (!File.Exists(outputPath))
            {
                continue;
            }

            var size = new FileInfo(outputPath).Length;

            if (size == previous && size > 0)
            {
                if (++quiet >= 2)
                {
                    try
                    {
                        return (await File.ReadAllLinesAsync(outputPath, cancellationToken).ConfigureAwait(false), dialog);
                    }
                    catch (IOException ex)
                    {
                        throw new CliException(
                            "LiveReadFailed",
                            $"The script's output could not be read: {ex.Message}",
                            2);
                    }
                }

                continue;
            }

            quiet = 0;
            previous = size;
        }

        var waited = DateTimeOffset.UtcNow - started;
        var stillPending = LogQueue.PendingSinceStart();

        throw new CliException(
            "LiveReadWroteNothing",
            $"The {what} script produced no output at '{outputPath}' within {waited.TotalSeconds:0.#} s. Dorico "
            + "answers kOK to Script.RunScript whether the script runs, dies or sits behind a dialog, and with "
            + "ShowConsole=false a Lua error has nowhere to go, so this is all that can be reported. "
            + (stillPending.Count == 0
                ? "Dorico's log shows nothing of this read's own posted work waiting on the queue"
                    + (queued > 0 ? ", though it did while this read waited" : string.Empty)
                    + ", so the wait itself is what ran out"
                : "Dorico's log shows work still posted with no matching Executing line: "
                    + string.Join(", ", stillPending.Take(4))
                    + (stillPending.Count > 4 ? ", …" : string.Empty)
                    + ". Dorico's command queue is busy - a modal dialog, or a command that has not finished - and "
                    + "this read is queued behind it. Answer the dialog or wait for the command, then run the read "
                    + "again: a retry costs one invocation.")
            + " Check that a document with at least one player is open (`session status`), that nothing modal is "
            + "holding Dorico's command queue (see %APPDATA%\\Steinberg\\Dorico 6\\application.log), and see "
            + "docs/scripting.md before probing the scripting layer further.",
            2);
    }

    /// <summary>
    /// The <c>script</c> object a live read reports: the command, its acknowledgement, and the one sentence that
    /// says what that acknowledgement is worth.
    /// </summary>
    public static JsonObject Report(JsonObject acknowledgement)
    {
        ArgumentNullException.ThrowIfNull(acknowledgement);

        return new JsonObject
        {
            ["command"] = "Script.RunScript",
            ["ack"] = NodeRead.Str(acknowledgement, "ack") ?? "unknown",
            ["note"] = "kOK is a receipt: with ShowConsole=false a script that fails leaves no trace, and a modal "
                + "dialog elsewhere in Dorico means the command is queued rather than run. The proof is the file this "
                + "read parsed, not the acknowledgement.",
        };
    }

    /// <summary>Sends a script through the host and waits for what it wrote.</summary>
    /// <param name="scriptPath">Where the script was written.</param>
    /// <param name="outputPath">Where the script writes.</param>
    /// <param name="timeout">How long Dorico has to run it and how long the output is then watched for.</param>
    /// <param name="expectScore">The document guard, when the caller knows which document it means.</param>
    /// <param name="what">What the read is, for the failure message.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    public static async Task<(string[] Lines, JsonObject? Dialog, JsonObject Acknowledgement)> RunAsync(
        string scriptPath,
        string outputPath,
        TimeSpan timeout,
        int? expectScore,
        string what,
        CancellationToken cancellationToken)
    {
        File.Delete(outputPath);

        var acknowledgement = await SaveCopy
            .SendCommandAsync(
                "Script.RunScript",
                [
                    new("ScriptPath", scriptPath.Replace('\\', '/')),
                    new("ShowConsole", "false"),
                ],
                timeout,
                cancellationToken,
                expectScore)
            .ConfigureAwait(false);

        var (lines, dialog) = await WaitForOutputAsync(outputPath, timeout, what, cancellationToken).ConfigureAwait(false);

        return (lines, dialog, acknowledgement);
    }
}
