using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using DoricoUse.Cli.Dorico;
using DoricoUse.Cli.Json;

namespace DoricoUse.Cli.Ui;

/// <summary>How long the dialog that is up has been up, and which occurrence of its window handle it is.</summary>
/// <param name="OpenMs">
/// Whole milliseconds since the store first saw this window handle. It cannot be measured inside one invocation,
/// because this CLI is short-lived: the moment the window went up belongs to a process that has already exited.
/// </param>
/// <param name="Instance">
/// The 1-based occurrence counter of the handle: 1 the first time the store sees a handle, repeated unchanged while
/// that handle stays up — so two payloads that name the same <c>hwnd</c> and the same <c>instance</c> are the same
/// window — and back to 1 for a handle the store has forgotten, even when that handle hosts the dialog id it hosted
/// before. That last clause is why the handle is in the payload at all: the id is the same on every raise of the
/// same dialog, and measured on Dorico's error box the caption is not an identity either.
/// </param>
/// <remarks>
/// Under the rules in <see cref="DialogInstances"/> this is always 1: an entry is created at 1 and forgotten as soon
/// as the window it names is gone, so nothing raises it. `hwnd` is therefore the half of the pair that does the work
/// today. The field is carried anyway, because the contract is about the pair — a caller compares both together —
/// and that is cheaper than a caller relearning the field the day an entry survives a gap between two sightings.
/// </remarks>
internal sealed record DialogOccurrence(long OpenMs, int Instance);

/// <summary>
/// What outlives a single invocation about Dorico's dialog windows: how long each one has been up, and which
/// occurrence of its handle it is.
/// </summary>
/// <remarks>
/// <para>
/// Every invocation of this tool is its own process, so "the same dialog is still up" is not a fact one process can
/// see — it needs a memory. The memory is <c>%LOCALAPPDATA%\dorico-use\dialogs.json</c>, the directory the session
/// store already uses, and its whole shape is one entry per window handle:
/// <c>{"dialogs":[{"hwnd":"0x00C91106","firstSeenUtc":"2026-10-11T09:12:31.4800000+00:00","instance":1}]}</c>.
/// </para>
/// <para>
/// The rules, all of them here rather than in the probe: a handle seen for the first time gets
/// <c>firstSeenUtc = now</c> and <c>instance = 1</c>; a handle already in the store keeps both; a handle that is no
/// longer up — this probe found no dialog at all, or the window that handle names has gone — is forgotten, so a
/// later dialog on a recycled handle counts from 1 again; and an entry that has not been the dialog for
/// <see cref="StaleAfter"/> is pruned, which is what keeps the file from growing without bound.
/// </para>
/// <para>
/// The store is an instrument and not the measurement. If it cannot be read or written, <see cref="Observe"/>
/// answers null and the payload leaves <c>openMs</c> and <c>instance</c> out: a state file that will not cooperate
/// is no reason for a probe to fail, and a number invented to fill the gap would be worse than no number at all.
/// </para>
/// </remarks>
internal static class DialogInstances
{
    /// <summary>The state directory this file shares with the session store.</summary>
    /// <remarks>
    /// Spelled out here rather than read from <c>SessionStore</c> because this file is compiled on its own by the
    /// vocabulary check — it holds no Windows API and no session, which is exactly what makes that possible — and a
    /// directory name is not worth a dependency the check would then have to follow.
    /// </remarks>
    public static string StateDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "dorico-use");

    /// <summary>The dialog memory: one entry per window handle that is, or recently was, the dialog up.</summary>
    public static string FilePath => Path.Combine(StateDirectory, "dialogs.json");

    /// <summary>How long an entry may go on being the dialog before it is pruned.</summary>
    /// <remarks>
    /// Six hours: long enough that a dialog a user walked away from over lunch keeps its <c>openMs</c>, short enough
    /// that a handle cannot sit in this file for a whole working day. In practice the entry goes much sooner,
    /// because the window it names is gone the moment the dialog is answered — this bound is the backstop for a
    /// window that is alive and is not the dialog any more.
    /// </remarks>
    public static TimeSpan StaleAfter => TimeSpan.FromHours(6);

    /// <summary>
    /// Records that <paramref name="handle"/> is the dialog up now, and answers how long it has been up.
    /// </summary>
    /// <param name="handle">The window handle the probe chose, or zero when it found no dialog at all.</param>
    /// <param name="alive">
    /// Whether a stored handle is still a window. The Windows side owns that question (<c>IsWindow</c>); it arrives
    /// here as a function so that these rules can be checked without a window to look at.
    /// </param>
    /// <param name="now">The moment of this observation.</param>
    /// <param name="path">The file to use, or the real one under <c>%LOCALAPPDATA%\dorico-use</c>.</param>
    /// <returns>The occurrence, or null when the store could not be read or written.</returns>
    public static DialogOccurrence? Observe(
        nint handle,
        Func<nint, bool> alive,
        DateTimeOffset now,
        string? path = null)
    {
        ArgumentNullException.ThrowIfNull(alive);

        return Update(handle, alive, now, path ?? FilePath);
    }

    /// <summary>
    /// Forgets every handle, because this probe found no dialog.
    /// </summary>
    /// <remarks>
    /// This is the half of the forgetting rule a probe can state positively: nothing is modal now, so nothing the
    /// store remembers is the dialog up now either. Without it an entry would outlive the dialog that made it until
    /// some later invocation happened to check its window, and a new dialog raised on the handle Windows hands back
    /// would inherit a <c>firstSeenUtc</c> that belongs to the window before it.
    /// </remarks>
    /// <param name="path">The file to use, or the real one under <c>%LOCALAPPDATA%\dorico-use</c>.</param>
    public static void Forget(string? path = null)
        => _ = Update(nint.Zero, static _ => false, DateTimeOffset.UtcNow, path ?? FilePath);

    private static DialogOccurrence? Update(nint handle, Func<nint, bool> alive, DateTimeOffset now, string path)
    {
        if (!TryLoad(path, out var known, out var raw))
        {
            return null;
        }

        var kept = new List<Entry>();

        foreach (var entry in known)
        {
            // Two ways an entry stops being true, and both are the same statement — it is no longer up. The window
            // that handle names has gone, or the handle has not been the dialog for StaleAfter, which is the bound
            // that keeps an entry whose window is alive but is not a dialog from living here forever. The handle
            // observed right now is exempt from the second one: a dialog a user left up for seven hours has been up
            // for seven hours, and saying `openMs: 0` about it would be a lie.
            if (alive(entry.Handle) && (entry.Handle == handle || now - entry.FirstSeenUtc <= StaleAfter))
            {
                kept.Add(entry);
            }
        }

        DialogOccurrence? occurrence = null;

        if (handle != nint.Zero)
        {
            var entry = kept.Find(candidate => candidate.Handle == handle);

            if (entry is null)
            {
                entry = new Entry(handle, now, Instance: 1);
                kept.Add(entry);
            }

            occurrence = new DialogOccurrence(WholeMilliseconds(now - entry.FirstSeenUtc), entry.Instance);
        }
        else
        {
            // "The probe found no dialog" is the other half of the forgetting rule, said as itself rather than
            // inferred from the window list: nothing is up, so nothing is remembered.
            kept.Clear();
        }

        return Save(path, kept, raw) ? occurrence : null;
    }

    /// <summary>One window handle this store remembers, and when it was first seen.</summary>
    private sealed record Entry(nint Handle, DateTimeOffset FirstSeenUtc, int Instance);

    /// <summary>
    /// Reads the file. False only when it cannot be read at all, which is what makes the payload leave
    /// <c>openMs</c> and <c>instance</c> out.
    /// </summary>
    /// <remarks>
    /// A file that is there but says nothing this build can use — not JSON, not the shape below, an entry with a
    /// handle or a timestamp that will not parse — is reported as an empty memory rather than as a failure, and the
    /// next save replaces it. That is deliberate: this is the feature's own file, so refusing to read it would leave
    /// both fields off every payload for good, until somebody deleted a file they have never heard of by hand. The
    /// only honest failure is that the file cannot be read, and then there is nothing to replace.
    /// </remarks>
    private static bool TryLoad(string path, out List<Entry> known, out string raw)
    {
        known = [];
        raw = string.Empty;

        try
        {
            if (!File.Exists(path))
            {
                return true;
            }

            raw = File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }

        try
        {
            if (JsonNode.Parse(raw) is not JsonObject json || json["dialogs"] is not JsonArray array)
            {
                return true;
            }

            foreach (var item in array)
            {
                if (item is not JsonObject entry
                    || Handle(entry) is not { } handle
                    || Moment(entry, "firstSeenUtc") is not { } firstSeen)
                {
                    continue;
                }

                known.Add(new Entry(handle, firstSeen, Math.Max(1, NodeRead.Int(entry, "instance") ?? 1)));
            }
        }
        catch (JsonException)
        {
            // Unparseable: an empty memory, and the save below rewrites the file in the shape this build reads.
        }

        return true;
    }

    /// <summary>Writes the memory, and only when it changed. False when it could not be written.</summary>
    private static bool Save(string path, List<Entry> kept, string raw)
    {
        if (kept.Count == 0 && raw.Length == 0)
        {
            // Nothing to remember and nothing on disk to erase: an invocation that found no dialog does not create a
            // state file just to say so, which matters because that is the common case for every command there is.
            return true;
        }

        var text = Text(kept);

        if (string.Equals(text, raw, StringComparison.Ordinal))
        {
            return true;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
            File.WriteAllText(path, text);

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static string Text(List<Entry> entries)
    {
        var array = new JsonArray();

        // Ordered by handle so that the same memory always serializes to the same bytes: the file is only rewritten
        // when it changes, and a set that came out in a different order would change it for nothing.
        foreach (var entry in entries.OrderBy(entry => entry.Handle))
        {
            var item = new JsonObject
            {
                ["hwnd"] = DialogModel.HandleText(entry.Handle),
                ["firstSeenUtc"] = entry.FirstSeenUtc.ToString("O", CultureInfo.InvariantCulture),
                ["instance"] = entry.Instance,
            };

            JsonArr.AddNode(array, item);
        }

        var json = new JsonObject { ["dialogs"] = array };

        return json.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>The handle an entry names, or null when it names one this build cannot read.</summary>
    private static nint? Handle(JsonObject entry)
    {
        var text = NodeRead.Str(entry, "hwnd");

        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        var digits = text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? text[2..] : text;

        return long.TryParse(digits, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value) && value != 0
            ? (nint)value
            : null;
    }

    private static DateTimeOffset? Moment(JsonObject entry, string key)
        => DateTimeOffset.TryParse(
            NodeRead.Str(entry, key),
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind,
            out var value)
            ? value
            : null;

    private static long WholeMilliseconds(TimeSpan elapsed)
        => elapsed.TotalMilliseconds <= 0 ? 0 : (long)elapsed.TotalMilliseconds;
}
