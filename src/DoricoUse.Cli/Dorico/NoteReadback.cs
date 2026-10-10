using System.Text.Json.Nodes;

namespace DoricoUse.Cli.Dorico;

/// <summary>
/// The half of <c>note insert --read --read-rebind</c> that must run inside the host: asking Dorico to write a copy
/// of the document the notes just went into. The default <c>--read</c> does not come through here — it saves
/// nothing, so its read is <see cref="LiveRead"/> and the CLI runs it after the notes.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why the save is here and not in the CLI.</b> <c>File.SaveAs</c> saves whichever document is active, and a
/// window that comes to the front between the notes and the save sends the copy to somebody else's project. So the
/// save runs in the same gated exchange as the notes, with the document guard read again immediately before it —
/// the same guard <c>--expect-score</c> uses, so a window that changed in between is refused with
/// <c>WrongActiveScore</c> instead of being saved.
/// </para>
/// <para>
/// <b>Why the decode is not here.</b> The decoder itself is <see cref="ProjectFileReader"/> plus
/// <c>DoricoUse.Cli.Views.ProjectFileView.Report</c> — the same pair <c>file read</c> and <c>session read</c> use,
/// and neither is re-implemented anywhere in this file. It is called from the CLI process, which is the one that
/// waits for the copy to stop growing and that already owns its path. That was once a hard requirement — the view
/// shared one node across calls, so a second <c>Report</c> inside the long-lived host threw "The node already has a
/// parent." — and it is not any more: the view clones what it shares, and <c>ProjectFileCheck</c> builds the report
/// three times in one process precisely so that stays true.
/// </para>
/// </remarks>
internal static class NoteReadback
{
    /// <summary>
    /// Sends <c>File.SaveAs</c> over the connection the host already owns and returns the <c>read</c> object the
    /// payload carries. The caller (the CLI) waits for the file, decodes it and fills in <c>save.file</c> and the
    /// report.
    /// </summary>
    public static async Task<JsonObject> SaveAsync(
        DoricoClient client,
        string path,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var acknowledgement = await SaveCopy
            .SaveAsync(path, timeout, null, client.SendCommandAsync, cancellationToken)
            .ConfigureAwait(false);

        var save = new JsonObject
        {
            ["command"] = SaveCopy.SaveAsCommand,

            // The host-side send answers with Dorico's own response (code/message), the CLI-side route wraps it.
            ["ack"] = NodeRead.Str(acknowledgement, "ack")
                ?? NodeRead.Str(acknowledgement, "code")
                ?? NodeRead.Str(acknowledgement, "message")
                ?? "unknown",
            ["document"] = new JsonObject
            {
                ["repointed"] = true,
                ["now"] = path,
                ["evidence"] = "Dorico logs 'Saving document: <id> to <path>', the document window's title follows "
                    + "the new file name, and the document's autosave and backup move with it.",
                ["note"] = SaveCopy.SaveDocumentNote,
            },
            ["note"] = "kOK is a receipt; the notes below are decoded from the file that is really on disk.",
        };

        var parameters = new JsonObject { [SaveCopy.PathParameter] = path };

        if (acknowledgement["parameters"] is { } sent)
        {
            parameters = sent.DeepClone() as JsonObject ?? parameters;
        }

        save["parameters"] = parameters;

        if (NodeRead.Str(acknowledgement, "detail") is { } detail)
        {
            save["detail"] = detail;
        }

        return new JsonObject
        {
            ["save"] = save,
            ["decodedFrom"] = path,
            ["sideEffects"] = new JsonArray((JsonNode?)JsonValue.Create(SaveCopy.SaveDocumentNote)),
            ["note"] = "The copy is written and the document is rebound to it. The payload's read carries the "
                + "decoded project: read.score.notes is what is really in the score now. This is the rebind route "
                + "only - the default live read saves nothing and fills in its own payload.",
        };
    }
}
