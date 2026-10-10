using System.Diagnostics;
using System.Runtime.InteropServices;

namespace DoricoUse.Cli.Ui;

/// <summary>What the presence probe found, including how it failed when it did.</summary>
/// <param name="Dialog">The modal window, or null when nothing of Dorico's is modal.</param>
/// <param name="How">The mechanism, for <c>probe.how</c>.</param>
/// <param name="Milliseconds">What the probe cost.</param>
/// <param name="Windows">
/// How many visible top-level windows Dorico had when the probe ran. Zero means Dorico is not running or has
/// nothing on screen, which is a different answer from "no dialog is up" — and without this number the two look
/// exactly alike in a payload, which is the one way a null `dialog` could be lying.
/// </param>
/// <param name="DocumentEnabled">
/// Whether Dorico's document window was enabled, or null when no document window was found. This is the fact the
/// modality rule turns on, so it is reported rather than inferred from an absent dialog.
/// </param>
/// <param name="All">
/// Every visible top-level window of Dorico's own process, topmost first. The document window and the modal window
/// are both in here and are flagged as such, because a list that silently dropped them could not answer "which of
/// these is the one holding the queue" either.
/// </param>
/// <param name="Document">The handle the probe resolved as Dorico's document window, or zero.</param>
/// <param name="Degraded">True when the probe itself failed.</param>
/// <param name="Error">Why it failed, when it did.</param>
internal sealed record DialogPresence(
    WindowFacts? Dialog,
    string How,
    long Milliseconds,
    int Windows = 0,
    bool? DocumentEnabled = null,
    IReadOnlyList<WindowFacts>? All = null,
    nint Document = default,
    bool Degraded = false,
    string? Error = null);

/// <summary>
/// Is a modal window of Dorico up right now, and which one?
/// </summary>
/// <remarks>
/// <para>
/// This is the <b>presence</b> fact and nothing more: it is cheap Win32, it runs on every invocation, and it never
/// claims anything about Dorico's command queue. That is what <see cref="LogQueue"/> is for, because the two do
/// not agree — measured, the library manager (<c>Qt6101QWindowIcon</c>) is a window a user can see while Dorico's
/// queue keeps moving 31 ms per command, and the MusicXML export dialog (<c>Qt6101QWindow</c>) stalls it for
/// 56 seconds.
/// </para>
/// <para>
/// The rule that decides "modal" comes from the same measurements. A dialog is up when Dorico's document window is
/// <b>disabled</b> and its process has another visible top-level window: the export dialog disables the document
/// window, the native folder picker it raises disables it too (measured: it disabled the Qt dialog that raised
/// it), the modeless library manager leaves it enabled while its own window needs no owner, and the
/// authorization dialog was observed to disable the Hub window the same way. Neither <c>IsWindowEnabled</c> nor
/// <c>GW_OWNER</c> identifies a dialog on its own; together with "there is another window", they do.
/// </para>
/// <para>
/// Dorico is found by its process image name, which is <c>Dorico6</c> on the measured installation. The check is a
/// prefix so a later major version keeps working, and a wrong guess costs nothing but an empty probe.
/// </para>
/// </remarks>
internal static class DialogProbe
{
    /// <summary>The tool name reported in <c>probe.how</c>.</summary>
    public const string How = "win32";

    /// <summary>Probes once, with no waiting: the caller decides whether to retry.</summary>
    public static DialogPresence Probe()
    {
        var started = Stopwatch.GetTimestamp();

        try
        {
            var windows = Enumerate();
            var dorico = DoricoProcessIds(windows);
            var mine = windows.Where(window => dorico.Contains(window.ProcessId)).ToList();

            if (dorico.Count == 0)
            {
                return new DialogPresence(null, How, Elapsed(started));
            }

            var document = DocumentWindow(windows, dorico);
            var enabled = document is null ? (bool?)null : IsWindowEnabled(document.Handle);

            // Measured correction: while a modal that owns nothing is up — the crash reporter is one, and it
            // disables every other window of the process — Windows still calls one of those disabled windows the
            // process's main window, and it is not always the document. The document window is the <b>largest</b>
            // window that a modal disabled, which is a fact about how a score window fills a screen: measured with
            // the crash reporter up, the resolution named the 1768x1077 library manager and the 3286x1841 document
            // window beside it. When the resolved window is enabled, or is the largest disabled one already, nothing
            // changes.
            if (document is not null && enabled == false)
            {
                var largest = windows
                    .Where(window => dorico.Contains(window.ProcessId)
                        && window.Handle != document.Handle
                        && window.Owner == nint.Zero
                        && !window.Enabled
                        && Area(window.Handle) > Area(document.Handle))
                    .OrderByDescending(window => Area(window.Handle))
                    .FirstOrDefault();

                if (largest is not null)
                {
                    document = largest;
                }
            }

            var documentHandle = document?.Handle ?? nint.Zero;
            var all = mine.ToArray();

            if (document is null || enabled == true)
            {
                // No document window, or nothing has disabled it: whatever else is on screen is not modal.
                return new DialogPresence(null, How, Elapsed(started), mine.Count, enabled, all, documentHandle);
            }

            var candidates = windows
                .Where(window => IsDialogCandidate(window, dorico, document.Handle))
                .ToList();

            if (candidates.Count == 0)
            {
                return new DialogPresence(null, How, Elapsed(started), mine.Count, enabled, all, documentHandle);
            }

            var chosen = candidates
                .OrderByDescending(window => window.Foreground)
                .ThenByDescending(window => window.Owner == document.Handle)
                .First();

            return new DialogPresence(chosen, How, Elapsed(started), mine.Count, enabled, all, documentHandle);
        }
        catch (Exception ex)
        {
            // Every failure here is reported, never thrown: the probe is a courtesy on every command, and a command
            // that already did its work must not fail because Windows would not answer a question about windows.
            return new DialogPresence(null, How, Elapsed(started), Degraded: true, Error: ex.Message);
        }
    }

    /// <summary>The window caption and class of one handle, for the reader and for error messages.</summary>
    public static (string Title, string WindowClass) Describe(nint handle)
        => (Text(handle), Class(handle));

    /// <summary>Enumerates the visible top-level windows, topmost first, which is the order Win32 hands them out.</summary>
    private static List<WindowFacts> Enumerate()
    {
        var foreground = GetForegroundWindow();
        var windows = new List<WindowFacts>();

        _ = EnumWindows(
            (handle, parameter) =>
            {
                if (!IsWindowVisible(handle))
                {
                    return true;
                }

                _ = GetWindowThreadProcessId(handle, out var processId);
                windows.Add(new WindowFacts(
                    handle,
                    processId,
                    GetWindow(handle, GwOwner),
                    handle == foreground,
                    IsWindowEnabled(handle),
                    Class(handle),
                    Text(handle)));

                return true;
            },
            nint.Zero);

        return windows;
    }

    /// <summary>The process ids whose image is Dorico, out of the windows that are on screen.</summary>
    private static List<uint> DoricoProcessIds(List<WindowFacts> windows)
    {
        var found = new List<uint>();
        var seen = new Dictionary<uint, bool>();

        foreach (var processId in windows.Select(window => window.ProcessId).Distinct())
        {
            if (!seen.TryGetValue(processId, out var isDorico))
            {
                isDorico = IsDorico(processId);
                seen[processId] = isDorico;
            }

            if (isDorico)
            {
                found.Add(processId);
            }
        }

        return found;
    }

    private static bool IsDorico(uint processId)
    {
        try
        {
            using var process = Process.GetProcessById((int)processId);

            return process.ProcessName.StartsWith("Dorico", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Dorico's document window: the window the taskbar knows as the process's main one when it is on screen, else
    /// the largest visible window of the process that owns nothing. The fallback exists because MainWindowHandle can
    /// answer zero while a modal dialog is up.
    /// </summary>
    private static WindowFacts? DocumentWindow(List<WindowFacts> windows, List<uint> dorico)
    {
        foreach (var processId in dorico)
        {
            nint main;

            try
            {
                using var process = Process.GetProcessById((int)processId);
                main = process.MainWindowHandle;
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                main = nint.Zero;
            }

            if (main != nint.Zero)
            {
                var match = windows.FirstOrDefault(window => window.Handle == main);

                if (match is not null)
                {
                    return match;
                }
            }
        }

        return windows
            .Where(window => dorico.Contains(window.ProcessId) && window.Owner == nint.Zero)
            .OrderByDescending(window => Area(window.Handle))
            .FirstOrDefault();
    }

    /// <summary>
    /// A window that could be the dialog: on screen, not the document window, and either Dorico's own or owned by
    /// one of Dorico's windows — measured, a native file dialog can belong to another process.
    /// </summary>
    private static bool IsDialogCandidate(WindowFacts window, List<uint> dorico, nint document)
    {
        if (window.Handle == document)
        {
            return false;
        }

        if (dorico.Contains(window.ProcessId))
        {
            return true;
        }

        if (window.Owner == nint.Zero)
        {
            return false;
        }

        _ = GetWindowThreadProcessId(window.Owner, out var ownerProcessId);

        return dorico.Contains(ownerProcessId);
    }

    private static long Area(nint handle)
    {
        if (!GetWindowRect(handle, out var rect))
        {
            return 0;
        }

        return (long)Math.Max(0, rect.Right - rect.Left) * Math.Max(0, rect.Bottom - rect.Top);
    }

    private static long Elapsed(long started) => (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;

    private static string Text(nint handle)
    {
        var buffer = new char[512];
        var length = GetWindowText(handle, buffer, buffer.Length);

        return length <= 0 ? string.Empty : new string(buffer, 0, Math.Min(length, buffer.Length));
    }

    private static string Class(nint handle)
    {
        var buffer = new char[256];
        var length = GetClassName(handle, buffer, buffer.Length);

        return length <= 0 ? string.Empty : new string(buffer, 0, Math.Min(length, buffer.Length));
    }

    private const int GwOwner = 4;

    private delegate bool EnumWindowsProc(nint handle, nint parameter);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc callback, nint parameter);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint handle, out uint processId);

    [DllImport("user32.dll")]
    private static extern nint GetWindow(nint handle, int command);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(nint handle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowEnabled(nint handle);

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetWindowTextW")]
    private static extern int GetWindowText(nint handle, [Out] char[] text, int maximum);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetClassNameW")]
    private static extern int GetClassName(nint handle, [Out] char[] text, int maximum);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(nint handle, out Rect rect);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }
}
