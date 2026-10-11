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

    /// <summary>
    /// True while a handle is still a window, which is the store's test for "the dialog it remembers has gone".
    /// </summary>
    /// <remarks>
    /// Asked of Windows and not of the probe's own window list, and that is the point: an entry has to be forgotten
    /// when the window it names is destroyed, and a dialog that has been answered is a handle no enumeration will
    /// hand out again. <see cref="DialogInstances"/> takes this as a function so that its rules stay testable
    /// without a window to look at.
    /// </remarks>
    public static bool IsWindowAlive(nint handle) => handle != nint.Zero && IsWindow(handle);

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
    /// Dorico's document window: the visible window of the process that owns nothing and has the largest
    /// <b>restored</b> rectangle.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Size, and not <c>Process.MainWindowHandle</c>, and that is a correction with a measurement behind it. With
    /// the modeless library manager up, .NET's "main window" is whichever visible unowned window <c>EnumWindows</c>
    /// hands out first, and measured on 2026-10-11 that was the 库管理 window (0x038A0A34, restored 816187 pixels)
    /// rather than the document window beside it (0x00C91106, restored 2688357) — so the library manager was
    /// reported as <c>document: true</c> and the real document window as <c>false</c>, twice measured. A document
    /// window is the largest thing a score application puts on screen, and this holds in every measured case: only
    /// the document up; the library manager up; the MusicXML export dialog and the folder picker (both owned, so
    /// excluded); and the crash reporter, which owns nothing but is 1768x1077 beside a 3286x1841 document window.
    /// </para>
    /// <para>
    /// The rectangle must be the <b>restored</b> one, <c>WINDOWPLACEMENT.rcNormalPosition</c>, because a minimized
    /// window answers <c>GetWindowRect</c> with its icon rectangle: measured, the document window read
    /// <c>-21333,-21333, 158x26</c> while its restored rectangle was 2191x1227. Ranking by the on-screen rectangle
    /// would have handed the title to every other window the moment the user minimized Dorico.
    /// </para>
    /// <para>
    /// An inference, not a certainty: a future modeless window larger than the document window, owning nothing,
    /// would be mistaken for it. Every window that has been measured is smaller, and the alternative — a UI
    /// Automation read on every invocation — costs a COM round trip on a probe that is meant to be cheap Win32.
    /// </para>
    /// </remarks>
    private static WindowFacts? DocumentWindow(List<WindowFacts> windows, List<uint> dorico)
    {
        WindowFacts? best = null;
        var bestArea = -1L;

        foreach (var window in windows)
        {
            if (!dorico.Contains(window.ProcessId) || window.Owner != nint.Zero)
            {
                continue;
            }

            var area = RestoredArea(window.Handle);

            if (area > bestArea)
            {
                best = window;
                bestArea = area;
            }
        }

        return best;
    }

    /// <summary>
    /// How big a window is when it is not minimized, in pixels of its restored rectangle.
    /// </summary>
    /// <remarks>
    /// <c>rcNormalPosition</c> is the placement the window returns to, and it is valid while the window is
    /// minimized — which is exactly when <c>GetWindowRect</c> is not. The fallback is for a window whose placement
    /// cannot be read: ranking it below a window that answered is better than treating "unknown" as "huge".
    /// </remarks>
    private static long RestoredArea(nint handle)
    {
        if (GetWindowPlacement(handle, ref placement))
        {
            var restored = placement.NormalPosition;

            return (long)Math.Max(0, restored.Right - restored.Left) * Math.Max(0, restored.Bottom - restored.Top);
        }

        return Area(handle);
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

    /// <summary>
    /// <c>IsWindow</c>: whether a handle still names a window, used by <see cref="IsWindowAlive"/>.
    /// </summary>
    /// <remarks>
    /// Distinct from <c>IsWindowVisible</c> and not a substitute for it: a handle that Windows has recycled names a
    /// window that is visible, and the store's rule is about the window being <b>gone</b>, not about it being shown.
    /// </remarks>
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(nint handle);

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

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowPlacement(nint handle, ref WindowPlacement placement);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    /// <summary>
    /// <c>WINDOWPLACEMENT</c>. <c>rcNormalPosition</c> is the member this build reads, and it is the one that is
    /// still meaningful while the window is minimized.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct WindowPlacement
    {
        public int Length;
        public int Flags;
        public int ShowCommand;
        public Point MinPosition;
        public Point MaxPosition;
        public Rect NormalPosition;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    /// <summary>
    /// One reusable <c>WINDOWPLACEMENT</c> with its length filled in, because the struct's first member is its own
    /// size and <c>GetWindowPlacement</c> refuses a zero there. The probe runs on every invocation and reads one
    /// window's placement once, so reusing it is enough and allocating per call would not be.
    /// </summary>
    private static WindowPlacement placement = new()
    {
        Length = Marshal.SizeOf<WindowPlacement>(),
    };
}
