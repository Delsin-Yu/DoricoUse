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
    public static readonly TimeSpan DefaultWait = TimeSpan.FromSeconds(20);

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
        var deadline = DateTimeOffset.UtcNow + timeout;
        var quiet = 0;
        long previous = -1;
        JsonObject? dialog = null;

        while (DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(100, cancellationToken).ConfigureAwait(false);

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

        throw new CliException(
            "LiveReadWroteNothing",
            $"The {what} script produced no output at '{outputPath}' within {timeout.TotalSeconds:0.#} s. Dorico "
            + "answers kOK to Script.RunScript whether the script runs, dies or sits behind a dialog, and with "
            + "ShowConsole=false a Lua error has nowhere to go, so this is all that can be reported. Check that a "
            + "document with at least one player is open (`session status`), that nothing modal is holding Dorico's "
            + "command queue (see %APPDATA%\\Steinberg\\Dorico 6\\application.log), and see docs/scripting.md before "
            + "probing the scripting layer further.",
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
