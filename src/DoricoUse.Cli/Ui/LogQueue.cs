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

    /// <summary>How long to keep looking for a command's lines before deciding. Measured: they appear in the same millisecond.</summary>
    public static readonly TimeSpan DefaultBudget = TimeSpan.FromMilliseconds(1000);

    /// <summary>
    /// How long to look when this invocation posted nothing by name — a note insert is a sequence the host sends.
    /// There is no line to wait for in particular, so this stops as soon as the log shows anything at all.
    /// </summary>
    public static readonly TimeSpan PassiveBudget = TimeSpan.FromMilliseconds(250);

    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(50);

    /// <summary>The log this invocation watches, or null when no Dorico log could be found.</summary>
    public static string? LogPath => logPath;

    /// <summary>Remembers where Dorico's log ended, so the delta is this invocation's own work.</summary>
    public static void Begin()
    {
        Posted.Clear();
        started = true;
        failure = null;
        startOffset = 0;
        logPath = FindLogPath();

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
    /// Decides what happened, waiting briefly for the lines Dorico writes. Null when this invocation posted
    /// nothing and the log gained nothing, in which case the payload carries no <c>execution</c> field at all.
    /// </summary>
    /// <param name="budget">How long to keep looking before reporting what is on disk.</param>
    public static JsonObject? Verdict(TimeSpan budget)
    {
        if (!started)
        {
            return null;
        }

        if (logPath is null || failure is not null)
        {
            return Posted.Count == 0
                ? null
                : Report("unknown", failure ?? "No Dorico log was found, so whether the command ran cannot be said.", []);
        }

        string[] lines;

        var passive = Posted.Count == 0;
        var window = passive && budget > PassiveBudget ? PassiveBudget : budget;

        try
        {
            lines = ReadDelta(window, passive);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Report("unknown", $"The Dorico log could not be read ({ex.Message}).", []);
        }

        if (Posted.Count == 0 && lines.Length == 0)
        {
            return null;
        }

        var evidence = new List<string>();

        foreach (var posted in Posted)
        {
            var state = Classify(posted, lines, window, out var sentence);
            evidence.Add($"{posted.Name}: {state} - {sentence}");
        }

        // Commands the host posted on its own (a note insert is a sequence of them) are visible in the log but not
        // in Posted. A posting with no Executing line among them is the same alarm.
        var orphans = Unmatched(lines);

        foreach (var orphan in orphans)
        {
            evidence.Add($"{orphan}: queued - posted with no Executing line "
                + $"{Seconds(window)} s later (posted by the host, not by name from here)");
        }

        var states = new List<string>();

        foreach (var posted in Posted)
        {
            states.Add(Classify(posted, lines, window, out _));
        }

        states.AddRange(orphans.Select(_ => "queued"));

        if (states.Count == 0)
        {
            states.Add("unknown");
            evidence.Add("No command was posted during this invocation, and the log gained no Posting line.");
        }

        return Report(Worst(states), string.Join("; ", evidence), lines);
    }

    private static string Classify(PostedCommand posted, string[] lines, TimeSpan budget, out string sentence)
    {
        if (WasExecuted(lines, posted.Name))
        {
            sentence = "Dorico's log shows it posting and executing";
            return "ran";
        }

        if (WasPosted(lines, posted.Name))
        {
            sentence = $"posted with no Executing line {Seconds(budget)} s later: the command is on Dorico's queue, "
                + "not run, which is what a modal dialog does";
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
    /// Reads what the log gained since <see cref="Begin"/>, polling until everything asked for is there or the
    /// budget runs out. Lines before the first newline are dropped: the offset was taken while Dorico may have been
    /// mid-write.
    /// </summary>
    private static string[] ReadDelta(TimeSpan budget, bool passive)
    {
        var deadline = DateTime.UtcNow + budget;

        while (true)
        {
            var lines = ReadFrom(logPath!, startOffset);

            if (Settled(lines, passive) || DateTime.UtcNow >= deadline)
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

        return Posted.All(posted => WasExecuted(lines, posted.Name));
    }

    private static string[] ReadFrom(string path, long offset)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        stream.Seek(offset, SeekOrigin.Begin);

        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false);
        var text = reader.ReadToEnd();

        if (offset > 0)
        {
            var newline = text.IndexOf('\n', StringComparison.Ordinal);
            text = newline < 0 ? string.Empty : text[(newline + 1)..];
        }

        return text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static bool WasPosted(string[] lines, string name) => lines.Any(line => Name(line, PostingMarker) == name);

    private static bool WasExecuted(string[] lines, string name) => lines.Any(line => Name(line, ExecutingMarker) == name);

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
