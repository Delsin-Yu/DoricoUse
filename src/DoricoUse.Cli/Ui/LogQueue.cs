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

    /// <summary>
    /// Whether <see cref="Begin"/> has taken the log's offset, under the name the completion wait reads it by.
    /// </summary>
    /// <remarks>
    /// The same flag <c>started</c> has always been, spelled for its reader: the wait for a command's completion
    /// line has to know the delta's origin exists, and a field named after the verdict alone read as if only the
    /// verdict could use it.
    /// </remarks>
    private static bool startedWaiting => started;

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

    /// <summary>
    /// How long <c>--wait-for-completion</c> waits for a command's completion line unless the caller says otherwise.
    /// </summary>
    /// <remarks>
    /// Above the longest command the report measured (54 s for a long export, 25 s for <c>Players.DeleteEmpty</c>),
    /// because a bound below a real command's cost would report a timeout for work that is still running. It is
    /// still a bound: a command that never finishes — or one whose completion line cannot be attributed — has to
    /// come back with an answer rather than hang a caller. It lives here, beside the wait it is the default for, so
    /// `command send` and `event set` cannot drift apart on the number.
    /// </remarks>
    public const int DefaultCompletionTimeoutMs = 90_000;

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
    /// log and hands <see cref="Verdict(TimeSpan, Action{TimeSpan})"/> a wait of its own, so the check decides on
    /// which poll the log gains a line — which is the one way to assert the retry and the offset rule, the two
    /// things that were wrong, without a live modal dialog (no check can raise one). The CLI itself only ever calls
    /// the parameterless overload.
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
    public static JsonObject? Verdict(TimeSpan budget) => Verdict(budget, Thread.Sleep);

    /// <summary>
    /// The same verdict with the poll's wait supplied by the caller. This is the seam the check drives; the CLI
    /// always calls <see cref="Verdict(TimeSpan)"/> and therefore waits exactly as it did before.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A check has to be able to see a line arrive <b>while</b> the verdict waits. That re-read is the retry this
    /// class exists for (the first version of this code dropped the delta's first line, which is the command's own
    /// <c>Posting</c> line, and reported <c>unknown</c> about a command the log had queued), and the second phase's
    /// watch for <c>Executing</c> is what singles out the good answer. Without this parameter the only way a check
    /// can produce that arrival is by racing a writer thread against a bounded wait — and it loses: measured, the
    /// fixture-log section of the vocabulary check failed 3 runs in 60 that way, always at its one case whose
    /// append overlapped another's, because <c>File.AppendAllText</c> opens with <c>FileShare.Read</c>, so two
    /// appends that meet leave one of them throwing <c>IOException … being used by another process</c> into a task
    /// nobody observes. Reproduced: of 1920 overlapping append pairs, 368 lost their line, while 1920 single
    /// appends all landed before a 400 ms read.
    /// </para>
    /// <para>
    /// Handing the wait over turns that arrival into an ordering the check names instead of a hope: the callback is
    /// spent once per poll, so it can append the line on the poll it chooses, and the verdict's own budgets and
    /// classification are untouched.
    /// </para>
    /// </remarks>
    /// <param name="budget">How long to wait for the <c>Posting</c> line before reporting what is on disk.</param>
    /// <param name="wait">How to spend one <see cref="PollInterval"/> between two reads of the log.</param>
    public static JsonObject? Verdict(TimeSpan budget, Action<TimeSpan> wait)
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
            lines = ReadDelta(passive && budget > PassiveBudget ? PassiveBudget : budget, passive, wait);
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
    private static string[] ReadDelta(TimeSpan budget, bool passive, Action<TimeSpan> wait)
    {
        var lines = Poll(budget, seen => Settled(seen, passive), wait);

        if (passive || !Posted.All(posted => WasPosted(lines, posted.Name)))
        {
            return lines;
        }

        return Poll(ExecutingBudget, seen => Posted.All(posted => WasExecuted(seen, posted.Name)), wait);
    }

    /// <summary>
    /// Reads the delta every <see cref="PollInterval"/> until it satisfies <paramref name="settled"/> or the budget
    /// runs out. The wait between two reads is <paramref name="wait"/> rather than <see cref="Thread.Sleep(TimeSpan)"/>
    /// so that the check can decide which poll the log gains a line on; see
    /// <see cref="Verdict(TimeSpan, Action{TimeSpan})"/>.
    /// </summary>
    private static string[] Poll(TimeSpan budget, Func<string[], bool> settled, Action<TimeSpan> wait)
    {
        var deadline = DateTime.UtcNow + budget;

        while (true)
        {
            var lines = ReadFrom(logPath!, startOffset);

            if (settled(lines) || DateTime.UtcNow >= deadline)
            {
                return lines;
            }

            wait(PollInterval);
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

    private static bool WasPosted(string[] lines, string name) => lines.Any(line => Name(line, PostingMarker) == name);

    private static bool WasExecuted(string[] lines, string name) => lines.Any(line => Name(line, ExecutingMarker) == name);

    /// <summary>
    /// Waits, bounded, for one command's own completion line in Dorico's log, and reports how long it waited and
    /// whether it timed out.
    /// </summary>
    /// <param name="name">The command name this invocation sent, or null when it sent none.</param>
    /// <param name="budget">How long to wait at most.</param>
    /// <param name="wait">How to spend one <see cref="PollInterval"/> between two reads of the log.</param>
    /// <remarks>
    /// <para>
    /// This is the recipe the dogfood run used by hand, made a flag. Measured 2026-10-13: a command's own cost is
    /// not guessable — <c>Instrument.MoveToPlayer</c> took 4.4 s and <c>Players.DeleteEmpty</c> 25 s in the same
    /// session, against 54 s for a long export — so a caller that picks a fixed sleep either waits too little or
    /// wastes the difference. Dorico's log names the moment for it:
    /// <c>notifyPostCommandExecute: &lt;name&gt;?&lt;params&gt; (&lt;ms&gt; ms)</c>, which is the line a manual
    /// runbook counted before and after the command. The delta starts at <see cref="Begin"/>'s offset, so only
    /// lines this invocation produced are considered.
    /// </para>
    /// <para>
    /// Two limits are stated rather than hidden. The delta is a window on a file a person and other agents also
    /// write to, so a completion line for the same command name from another invocation that overlaps this one
    /// would satisfy the wait; and the count of like-named lines is not tracked, because one <c>command send</c>
    /// sends one command. <c>posted</c> and the <c>waitedMs</c> beside <c>completed</c> are what a caller checks.
    /// </para>
    /// </remarks>
    public static JsonObject WaitForCompletion(string? name, TimeSpan budget, Action<TimeSpan> wait)
    {
        var started = Stopwatch.GetTimestamp();

        if (!startedWaiting || logPath is null || failure is not null)
        {
            return Completion(
                name,
                completed: false,
                timedOut: name is null,
                waited: Stopwatch.GetElapsedTime(started),
                budget,
                posted: false,
                completions: 0,
                failure ?? "No Dorico log was found, so no completion line can be waited for.");
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            // Nothing was sent by name (`note insert` posts a sequence from the host), so there is no line for this
            // command to wait for. Saying so beats waiting the whole budget for an answer that cannot come.
            return Completion(
                name,
                completed: false,
                timedOut: true,
                waited: Stopwatch.GetElapsedTime(started),
                budget,
                posted: false,
                completions: 0,
                "This invocation sent no command by name, so there is no completion line of its own to wait for.");
        }

        var deadline = DateTime.UtcNow + budget;
        string[] lines;

        while (true)
        {
            try
            {
                lines = ReadFrom(logPath, startOffset);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return Completion(
                    name,
                    completed: false,
                    timedOut: true,
                    waited: Stopwatch.GetElapsedTime(started),
                    budget,
                    posted: false,
                    completions: 0,
                    $"The Dorico log could not be read ({ex.Message}).");
            }

            var completions = CompletionLines(lines, name);

            if (completions > 0 || DateTime.UtcNow >= deadline)
            {
                return Completion(
                    name,
                    completed: completions > 0,
                    timedOut: completions == 0,
                    waited: Stopwatch.GetElapsedTime(started),
                    budget,
                    posted: WasPosted(lines, name),
                    completions,
                    null);
            }

            wait(PollInterval);
        }
    }

    /// <summary>The <c>completion</c> object of a <c>command send --wait-for-completion</c> payload.</summary>
    private static JsonObject Completion(
        string? name,
        bool completed,
        bool timedOut,
        TimeSpan waited,
        TimeSpan budget,
        bool posted,
        int completions,
        string? failure)
    {
        var payload = new JsonObject
        {
            ["command"] = name,
            ["completed"] = completed,
            ["timedOut"] = timedOut,
            ["waitedMs"] = (long)waited.TotalMilliseconds,
            ["budgetMs"] = (long)budget.TotalMilliseconds,
            ["posted"] = posted,
            ["completedLines"] = completions,
            ["log"] = logPath,
            ["note"] = completed
                ? "Dorico's log gained this command's own `notifyPostCommandExecute` line, so the command finished. "
                    + "`waitedMs` is how long that took from the moment this invocation started watching."
                : timedOut
                    ? "No completion line for this command appeared inside the budget. A long command can still be "
                        + "running: the log is the witness, and `execution` on this payload says what it shows about "
                        + "the queue. `posted: false` means the log did not even gain the command's Posting line, "
                        + "which is what a refused command looks like."
                    : "The wait ended without a completion line and without a timeout, which no measured path does.",
        };

        if (failure is not null)
        {
            payload["failure"] = failure;
        }

        return payload;
    }

    /// <summary>How many completion lines this delta holds for one command.</summary>
    /// <remarks>
    /// The logged name may be a refinement of the one the caller sent — Dorico writes
    /// <c>notifyPostCommandExecute: File.Export.Text?...</c> for the command the catalogue calls <c>File.Export</c>
    /// (the line in <c>docs/scripting.md</c> is the measurement) — so a logged name that extends the requested one
    /// with a dot-qualified part counts as that command. The opposite direction is not accepted: a logged
    /// <c>File.Export</c> is not evidence that <c>File.Export.Text</c> ran.
    /// </remarks>
    private static int CompletionLines(string[] lines, string name)
    {
        var count = 0;

        foreach (var line in lines)
        {
            var logged = Name(line, CompletedMarker);

            if (logged is not null
                && (logged.Equals(name, StringComparison.Ordinal)
                    || logged.StartsWith(name + ".", StringComparison.Ordinal)))
            {
                count++;
            }
        }

        return count;
    }

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

    /// <summary>
    /// The line Dorico writes when a command has finished, which is what <c>--wait-for-completion</c> waits for:
    /// <c>notifyPostCommandExecute: File.Export.Text?...&amp;Show=false (1894 ms)</c>.
    /// </summary>
    /// <remarks>
    /// Distinct from <see cref="ExecutingMarker"/> and stronger than it: a command that starts executing is not a
    /// command that finished, which is exactly the shape of the 4.4 s to 54 s commands the report measured. The name
    /// is read the same way a posting's is — up to the first space or '?' — so the parameters after it do not have
    /// to be parsed.
    /// </remarks>
    private const string CompletedMarker = "notifyPostCommandExecute: ";

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
