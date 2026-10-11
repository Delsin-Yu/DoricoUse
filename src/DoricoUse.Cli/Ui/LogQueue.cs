using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using DoricoUse.Cli.Json;

namespace DoricoUse.Cli.Ui;

/// <summary>One command this invocation posted to Dorico, and the answer Dorico gave it at posting time.</summary>
internal sealed record PostedCommand(string Name, string? Ack);

/// <summary>
/// The queue fact: did the command Dorico accepted actually run, or is it sitting behind a modal dialog?
/// </summary>
/// <remarks>
/// <para>
/// This is the whole reason the feature exists. Dorico answers <c>kOK</c> to a command it has merely put on its
/// queue, and the command then runs when the dialog that stalled the queue is answered — measured, with
/// timestamps: <c>NoteInput.Enter</c> was posted at 05:16:54.727 and executed at 05:17:18.757, twenty-four
/// seconds later, after 取消 closed the export dialog. An ack therefore says nothing about when a command ran.
/// </para>
/// <para>
/// Dorico's own log is the only witness. It echoes <c>Posting command (requested): &lt;name&gt;</c> when a command
/// is queued and <c>Executing command: &lt;name&gt;</c> when it starts; a command that was posted and has no
/// <c>Executing</c> line has not run. A command the API refused outright is never posted at all — measured:
/// <c>Play.StopScrubPlayback</c> answered <c>kError kCommandNotAllowed</c> and left no <c>Posting</c> line.
/// </para>
/// <para>
/// The state lives in static fields because one CLI process handles exactly one invocation and exits; nothing here
/// survives a command.
/// </para>
/// </remarks>
internal static class LogQueue
{
    /// <summary>The states, in the order that decides which one an invocation reports when it posted several commands.</summary>
    /// <remarks>
    /// <c>queued</c> outranks everything: it is the state that used to be invisible, and a caller that queues a
    /// save behind a dialog is the accident this feature is here to prevent. <c>ran</c> is the only good answer.
    /// </remarks>
    private static readonly string[] Severity = ["ran", "unknown", "refused", "queued"];

    private static readonly List<PostedCommand> Posted = [];

    private static string? logPath;
    private static long startOffset;
    private static bool started;
    private static string? failure;

    /// <summary>How long to wait for the <c>Posting</c> line of the command this invocation sent.</summary>
    /// <remarks>
    /// Measured on 2026-10-11, on the reproduction of the bug this replaces: the offset was taken at
    /// 05:42:49.45 and the line Dorico wrote is stamped 05:42:49.593, so the flush took about 75 ms and the line
    /// is normally there on the first or second poll. The budget is for a slower flush, not for the normal case.
    /// It is bounded rather than open-ended because a command the API refused leaves no line at all — measured:
    /// <c>Play.StopScrubPlayback</c> answered <c>kError kCommandNotAllowed</c> and was never posted — and that
    /// verdict has to arrive too.
    /// </remarks>
    public static readonly TimeSpan DefaultBudget = TimeSpan.FromMilliseconds(1000);

    /// <summary>
    /// How long to keep watching after the <c>Posting</c> line for the matching <c>Executing</c> line, which is
    /// what separates <c>ran</c> from <c>queued</c>.
    /// </summary>
    /// <remarks>
    /// Measured: a command that really runs executes in the same millisecond it is posted (<c>Posting
    /// 05:15:50.603</c>, <c>Executing 05:15:50.603</c>), while a command posted behind the export dialog produced
    /// no <c>Executing</c> line for 24 s (05:16:54.727, then 05:17:18.757 after 取消). So this second waits only
    /// for the good answer to have a chance to appear; a command still without an <c>Executing</c> line when it
    /// expires is reported as queued, with the time actually watched named in the evidence. The cost is paid only
    /// by a command that is not running, because a command that ran ends the wait immediately.
    /// </remarks>
    public static readonly TimeSpan ExecutingBudget = TimeSpan.FromMilliseconds(1000);

    /// <summary>
    /// How long to look when this invocation posted nothing by name — a note insert is a sequence the host sends.
    /// There is no line to wait for in particular, so this stops as soon as the log shows anything at all.
    /// </summary>
    public static readonly TimeSpan PassiveBudget = TimeSpan.FromMilliseconds(250);

    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(50);

    /// <summary>The log this invocation watches, or null when no Dorico log could be found.</summary>
    public static string? LogPath => logPath;

    /// <summary>Remembers where Dorico's log ended, so the delta is this invocation's own work.</summary>
    public static void Begin() => Begin(FindLogPath());

    /// <summary>
    /// Watches one named file from one offset, which is the same thing <see cref="Begin()"/> does with the log
    /// Dorico is writing.
    /// </summary>
    /// <remarks>
    /// This overload exists so the wait can be measured without Dorico: the check project points it at a fixture
    /// log and appends lines to that file while <see cref="Verdict"/> waits, which is the one way to assert the
    /// retry and the offset rule — the two things that were wrong — without a live modal dialog (no check can raise
    /// one). The CLI itself only ever calls the parameterless overload.
    /// </remarks>
    /// <param name="path">The file to watch, or null when there is none.</param>
    public static void Begin(string? path)
    {
        Posted.Clear();
        started = true;
        failure = null;
        startOffset = 0;
        logPath = path;

        if (logPath is null)
        {
            return;
        }

        try
        {
            startOffset = new FileInfo(logPath).Length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            failure = $"The Dorico log at {logPath} could not be read ({ex.Message}).";
        }
    }

    /// <summary>Records a command this invocation sent to Dorico, before or right after it was sent.</summary>
    public static void Mark(string name)
    {
        if (!string.IsNullOrWhiteSpace(name) && !Posted.Any(posted => string.Equals(posted.Name, name, StringComparison.Ordinal)))
        {
            Posted.Add(new PostedCommand(name, null));
        }
    }

    /// <summary>Remembers what Dorico answered, which is what separates "never posted" from "posted but not run".</summary>
    public static void Acknowledge(string name, string? ack)
    {
        for (var index = 0; index < Posted.Count; index++)
        {
            if (string.Equals(Posted[index].Name, name, StringComparison.Ordinal))
            {
                Posted[index] = Posted[index] with { Ack = ack };
                return;
            }
        }

        if (!string.IsNullOrWhiteSpace(name))
        {
            Posted.Add(new PostedCommand(name, ack));
        }
    }

    /// <summary>
    /// Decides what happened, waiting for the lines Dorico writes: first the command's own <c>Posting</c> line,
    /// then its <c>Executing</c> line. Null when this invocation has nothing to say about Dorico's queue, in
    /// which case the payload carries no <c>execution</c> field at all.
    /// </summary>
    /// <param name="budget">How long to wait for the <c>Posting</c> line before reporting what is on disk.</param>
    public static JsonObject? Verdict(TimeSpan budget)
    {
        if (!started)
        {
            return null;
        }

        var passive = Posted.Count == 0;

        if (logPath is null || failure is not null)
        {
            return passive
                ? null
                : Report("unknown", failure ?? "No Dorico log was found, so whether the command ran cannot be said.", []);
        }

        string[] lines;
        var startedAt = Stopwatch.GetTimestamp();

        try
        {
            lines = ReadDelta(passive && budget > PassiveBudget ? PassiveBudget : budget, passive);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Report("unknown", $"The Dorico log could not be read ({ex.Message}).", []);
        }

        var waited = Stopwatch.GetElapsedTime(startedAt);

        // Commands the host posted on its own (a note insert is a sequence of them) are visible in the log but not
        // in Posted. A posting with no Executing line among them is the same alarm.
        var orphans = Unmatched(lines);

        if (passive)
        {
            // This invocation sent nothing to Dorico by name — `ui press`, `ui dialog`, the live read — so the only
            // thing the field could honestly say is that one of the postings the log did gain is stuck on the
            // queue. With none, the payload carries no `execution` field: an `unknown` here would be a verdict on
            // nothing, and the log's unrelated lines would be its evidence.
            return orphans.Count == 0
                ? null
                : Report("queued", string.Join("; ", orphans.Select(orphan => $"{orphan}: queued - posted with "
                    + $"no Executing line {Seconds(waited)} s later (posted by the host, not by name from here)")),
                    lines);
        }

        var evidence = new List<string>();
        var states = new List<string>();

        foreach (var posted in Posted)
        {
            var state = Classify(posted, lines, waited, out var sentence);
            states.Add(state);
            evidence.Add($"{posted.Name}: {state} - {sentence}");
        }

        foreach (var orphan in orphans)
        {
            states.Add("queued");
            evidence.Add($"{orphan}: queued - posted with no Executing line "
                + $"{Seconds(waited)} s later (posted by the host, not by name from here)");
        }

        return Report(Worst(states), string.Join("; ", evidence), lines);
    }

    private static string Classify(PostedCommand posted, string[] lines, TimeSpan waited, out string sentence)
    {
        if (WasExecuted(lines, posted.Name))
        {
            sentence = "Dorico's log shows it posting and executing";
            return "ran";
        }

        if (WasPosted(lines, posted.Name))
        {
            sentence = $"posted, with no Executing line {Seconds(waited)} s later: the command is on Dorico's "
                + "queue, not run, which is what a modal dialog does"
                + (WasQueued(lines, posted.Name)
                    ? ", and Dorico's own log names it in its queue (`Queue after push`)"
                    : string.Empty);
            return "queued";
        }

        if (posted.Ack is not null && posted.Ack.StartsWith("kError", StringComparison.Ordinal))
        {
            sentence = $"Dorico answered {posted.Ack} and its log gained no Posting line: the command was refused, "
                + "never queued";
            return "refused";
        }

        sentence = "the log gained no Posting line for it, so whether it ran cannot be said from the log alone";
        return "unknown";
    }

    private static string Worst(List<string> states)
    {
        var worst = "ran";

        foreach (var state in states)
        {
            if (Array.IndexOf(Severity, state) > Array.IndexOf(Severity, worst))
            {
                worst = state;
            }
        }

        return worst;
    }

    private static JsonObject Report(string state, string evidence, string[] lines)
    {
        var payload = new JsonObject
        {
            ["state"] = state,
            ["evidence"] = evidence,
            ["log"] = logPath,
            ["postings"] = lines.Length,
        };

        payload["note"] = state switch
        {
            "queued" => "This command is on Dorico's queue and has not run. It is not lost: Dorico runs it as soon "
                + "as the dialog that stalled the queue is answered. Nothing destructive should be posted from here.",
            "refused" => "Dorico refused the command at the API and never queued it. That is a different outcome "
                + "from a queue, and the two must not be confused.",
            "unknown" => "The log cannot say whether the command ran. `Posting` and `Executing` are the two lines "
                + "this reads; a missing log, a slow flush or a second Dorico installation all look like this.",
            _ => "Dorico's log shows the command posting and executing.",
        };

        return payload;
    }

    private static string Seconds(TimeSpan budget) => budget.TotalSeconds.ToString("0.#", CultureInfo.InvariantCulture);

    /// <summary>
    /// Reads what the log gained since <see cref="Begin"/>, in the two phases the verdict needs: wait for the
    /// command's own <c>Posting</c> line, then watch <see cref="ExecutingBudget"/> for the matching
    /// <c>Executing</c> line that separates <c>ran</c> from <c>queued</c>.
    /// </summary>
    /// <remarks>
    /// The second phase runs only once every posted command has a <c>Posting</c> line, because before that there is
    /// nothing to watch an <c>Executing</c> line for, and an invocation that posted nothing by name has no posting
    /// of its own to wait for at all.
    /// </remarks>
    private static string[] ReadDelta(TimeSpan budget, bool passive)
    {
        var lines = Poll(budget, seen => Settled(seen, passive));

        if (passive || !Posted.All(posted => WasPosted(lines, posted.Name)))
        {
            return lines;
        }

        return Poll(ExecutingBudget, seen => Posted.All(posted => WasExecuted(seen, posted.Name)));
    }

    /// <summary>Reads the delta every <see cref="PollInterval"/> until it satisfies <paramref name="settled"/> or the budget runs out.</summary>
    private static string[] Poll(TimeSpan budget, Func<string[], bool> settled)
    {
        var deadline = DateTime.UtcNow + budget;

        while (true)
        {
            var lines = ReadFrom(logPath!, startOffset);

            if (settled(lines) || DateTime.UtcNow >= deadline)
            {
                return lines;
            }

            Thread.Sleep(PollInterval);
        }
    }

    private static bool Settled(string[] lines, bool passive)
    {
        if (passive || Posted.Count == 0)
        {
            return lines.Length > 0;
        }

        return Posted.All(posted => WasPosted(lines, posted.Name));
    }

    private static string[] ReadFrom(string path, long offset)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

        // A log that shrank (a restart that truncated it) makes the remembered offset meaningless: read the whole
        // file rather than a delta that no longer exists.
        if (offset > stream.Length)
        {
            offset = 0;
        }

        // The offset can land inside a line Dorico was writing when Begin() took it, and half a line is not a line
        // this can classify: that leading fragment is dropped. It is dropped only when the offset really is inside
        // a line - the byte before it being anything but a newline. Dropping it unconditionally was the bug this
        // replaces: the end of a quiet log is a line boundary, so the first line of the delta was always thrown
        // away, and that line is the command's own `Posting` line - measured on 2026-10-11, where the payload said
        // `unknown` while Dorico's log held `Posting command (requested): NoteInput.Enter` at that exact offset.
        var partial = offset > 0;

        if (partial)
        {
            stream.Seek(offset - 1, SeekOrigin.Begin);
            partial = stream.ReadByte() != '\n';
        }

        stream.Seek(offset, SeekOrigin.Begin);

        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false);
        var text = reader.ReadToEnd();

        if (partial)
        {
            var newline = text.IndexOf('\n', StringComparison.Ordinal);
            text = newline < 0 ? string.Empty : text[(newline + 1)..];
        }

        return text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    /// <summary>
    /// The commands this invocation has seen posted but not executed, read from the log <b>without waiting</b>.
    /// </summary>
    /// <remarks>
    /// This is the one thing a read that is waiting for its own work needs to know: is Dorico's queue still busy,
    /// and therefore is the file it is waiting for going to be late rather than never? Measured, that is the whole
    /// of the defect this answers — a live read's script was posted and executed 30.1 s later, behind a long
    /// command, while the read gave up after 20 s. An empty list is also the answer when the log cannot be read at
    /// all: a caller that only wants to decide whether to keep waiting must not be told "busy" by a file it could
    /// not open.
    /// </remarks>
    public static IReadOnlyList<string> PendingSinceStart()
    {
        if (!started || logPath is null || failure is not null)
        {
            return [];
        }

        string[] lines;

        try
        {
            lines = ReadFrom(logPath, startOffset);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }

        // Unlike the verdict's own orphan scan, this counts the commands <b>this</b> invocation posted by name too:
        // a live read that is waiting for its own script's file has exactly that posting on the queue, and skipping
        // it was why the first version of this answered "nothing is pending" while the payload beside it said
        // `execution.state: queued`.
        var pending = new List<string>();

        foreach (var line in lines)
        {
            var name = Name(line, PostingMarker);

            if (name is null || string.IsNullOrEmpty(name) || WasExecuted(lines, name)
                || pending.Contains(name, StringComparer.Ordinal))
            {
                continue;
            }

            pending.Add(name);
        }

        return pending;
    }

    private static bool WasPosted(string[] lines, string name) => lines.Any(line => Name(line, PostingMarker) == name);    private static bool WasExecuted(string[] lines, string name) => lines.Any(line => Name(line, ExecutingMarker) == name);

    /// <summary>
    /// True when Dorico's own log names the command in its queue (<c>Queue after push: {…}</c>).
    /// </summary>
    /// <remarks>
    /// Measured on 2026-10-11: the queued <c>NoteInput.Enter</c> that the defect was reported with produced
    /// <c>Queue after push: {NoteInput.Enter} {NoteInput.Enter}</c> while the export dialog held the queue — Dorico
    /// itself naming the queue, which is stronger evidence than a <c>Posting</c> line alone. It is not used to
    /// decide the state, because the same measurement showed the line is not written for every push (the first of
    /// those two postings left no such line at all); it only says so in the queued verdict's evidence when it is
    /// there.
    /// </remarks>
    private static bool WasQueued(string[] lines, string name)
    {
        var braced = "{" + name;

        return lines.Any(line => line.Contains(QueueMarker, StringComparison.Ordinal)
            && (line.Contains(braced + "}", StringComparison.Ordinal)
                || line.Contains(braced + "?", StringComparison.Ordinal)));
    }

    /// <summary>The postings in this delta that nothing ever executed.</summary>
    private static List<string> Unmatched(string[] lines)
    {
        var orphans = new List<string>();

        foreach (var line in lines)
        {
            var name = Name(line, PostingMarker);

            if (name is null || string.IsNullOrEmpty(name) || Posted.Any(posted => posted.Name == name)
                || WasExecuted(lines, name) || orphans.Contains(name, StringComparer.Ordinal))
            {
                continue;
            }

            orphans.Add(name);
        }

        return orphans;
    }

    private const string PostingMarker = "Posting command (requested): ";

    private const string ExecutingMarker = "Executing command: ";

    /// <summary>Dorico's own name for its queue, written after a push: <c>Queue after push: {…}</c>.</summary>
    private const string QueueMarker = "Queue after push:";

    /// <summary>
    /// The command name out of one log line. The logged name is the command followed by its parameters, so the
    /// tail is cut at the first space (a posting) or the first '?' (an execution).
    /// </summary>
    private static string? Name(string line, string marker)
    {
        var at = line.IndexOf(marker, StringComparison.Ordinal);

        if (at < 0)
        {
            return null;
        }

        var rest = line[(at + marker.Length)..].Trim();
        var end = rest.IndexOfAny([' ', '?']);

        return end < 0 ? rest : rest[..end];
    }

    /// <summary>
    /// The newest <c>%APPDATA%\Steinberg\Dorico *\application.log</c>.
    /// </summary>
    /// <remarks>
    /// The version in the folder name is not hard-coded: the running installation writes the newest log, and this
    /// picks that one. The user name is never part of the path — it comes from the folder Windows reports.
    /// </remarks>
    public static string? FindLogPath()
    {
        try
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

            if (string.IsNullOrEmpty(appData))
            {
                return null;
            }

            var steinberg = Path.Combine(appData, "Steinberg");

            if (!Directory.Exists(steinberg))
            {
                return null;
            }

            return Directory.EnumerateDirectories(steinberg, "Dorico *")
                .Select(folder => Path.Combine(folder, "application.log"))
                .Where(File.Exists)
                .Select(path => new FileInfo(path))
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .FirstOrDefault()?.FullName;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }
}
