using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;

namespace DoricoUse.Cli.Ui;

/// <summary>
/// The one place this build sends <b>real</b> input: a click on a control and the text as keystrokes, through
/// <c>SendInput</c>, for <c>ui press --commit-input</c>.
/// </summary>
/// <remarks>
/// <para>
/// Why it exists, measured 2026-10-13 on the 工程信息 dialog: <c>ui press --element titleLineEdit --value
/// 狸猫的五十种变化</c> wrote through the UI Automation value pattern, the field read the text back, and the dialog
/// stayed <b>unmodified</b> — 应用 was still disabled and the value reverted. A Qt field takes a value-pattern write
/// as a programmatic set, so the change signal the dialog listens for never fires. The workaround the report used is
/// the one implemented here: click the control, then send Ctrl+A and the text as keystrokes, which is input the
/// widget cannot tell from a person's.
/// </para>
/// <para>
/// <b>Unverified against Dorico.</b> No live run of this pass has been measured — the measurement that justifies it
/// is the failure it works around, and the shape of it (a mouse click plus keystrokes) is the one the report used
/// through another tool. So the payload distinguishes what it <i>did</i> from what it <i>achieved</i>: the pass
/// reports the click's coordinates, how many key events <c>SendInput</c> accepted, the value it read back afterwards
/// and whether that equals the text asked for, plus <c>ran: false</c> with a reason when the click could not be
/// aimed at all (an element with no rectangle, a window that is not on screen) instead of clicking somewhere
/// arbitrary.
/// </para>
/// <para>
/// It is opt-in for a reason that is not safety theatre: it moves the user's pointer and types at whatever has
/// focus, so a caller that did not ask for it must never get it. Nothing else in this build clicks a coordinate.
/// </para>
/// </remarks>
internal static class InputSimulator
{
    /// <summary>How long to let the window come forward and the control take focus after the click.</summary>
    /// <remarks>
    /// A guess with a margin rather than a measurement, and said so: Qt needs the activation and the focus round
    /// trip to have happened before the keystrokes arrive, or they go to whatever had focus. 150 ms is far longer
    /// than the 82 ms a Qt dialog took to be destroyed after a press (measured 2026-10-11) and short enough that a
    /// caller notices it not at all.
    /// </remarks>
    private const int FocusSettleMs = 150;

    /// <summary>How long to wait after activating a window that was not in front before clicking into it.</summary>
    private const int ActivateSettleMs = 200;

    private const uint InputMouse = 0;

    private const uint InputKeyboard = 1;

    private const uint MouseMove = 0x0001;

    private const uint MouseLeftDown = 0x0002;

    private const uint MouseLeftUp = 0x0004;

    private const uint MouseAbsolute = 0x8000;

    /// <summary>Coordinates are over the whole virtual desktop, not the primary monitor alone.</summary>
    private const uint MouseVirtualDesk = 0x4000;

    private const uint KeyboardKeyUp = 0x0002;

    private const uint KeyboardUnicode = 0x0004;

    private const ushort VirtualKeyControl = 0x11;

    private const ushort VirtualKeyA = 0x41;

    // GetSystemMetrics: the virtual screen, which is where an absolute SendInput coordinate lives.
    private const int MetricVirtualX = 76;
    private const int MetricVirtualY = 77;
    private const int MetricVirtualWidth = 78;
    private const int MetricVirtualHeight = 79;

    /// <summary>
    /// Clicks the control and types <paramref name="text"/> into it as real keystrokes, and reports what happened.
    /// </summary>
    /// <param name="topLevel">The top-level window the control lives in, for the activation check.</param>
    /// <param name="element">The UI Automation element to aim at; its bounding rectangle is the target.</param>
    /// <param name="text">The text to type, exactly as the caller asked for it.</param>
    /// <remarks>
    /// Never throws for a Win32 failure: the report is the answer, and <see cref="ElementActions"/> turns a report
    /// that did not run into the refusal the caller gets. A reason in the payload is worth more here than an
    /// exception in the middle of a write that already happened through the value pattern.
    /// </remarks>
    public static JsonObject Commit(nint topLevel, nint element, string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var started = Stopwatch.GetTimestamp();
        var report = new JsonObject();
        var bounds = UiaCalls.Bounds(element);

        if (bounds.Right <= bounds.Left || bounds.Bottom <= bounds.Top)
        {
            return Failed(
                report,
                started,
                "the control reports no bounding rectangle, so there is nothing to click and this build will not "
                + "click a coordinate it did not measure");
        }

        var x = bounds.Left + ((bounds.Right - bounds.Left) / 2);
        var y = bounds.Top + ((bounds.Bottom - bounds.Top) / 2);
        var inFront = GetForegroundWindow() == topLevel;
        var clicks = 0;

        // A window that is not in front gets the click anyway: pressing the mouse button on it activates it, and the
        // click then reaches the control underneath the pointer. The extra click is for the focus, because the click
        // that activates a window is also the one some widgets eat.
        if (!inFront)
        {
            _ = SetForegroundWindow(topLevel);
            Thread.Sleep(ActivateSettleMs);
        }

        if (!Click(x, y))
        {
            return Failed(
                report,
                started,
                $"SendInput refused the mouse click at {x},{y} (Win32 error {Marshal.GetLastWin32Error()}); the "
                + "window may be elevated and out of this process's reach");
        }

        clicks++;

        if (!Click(x, y))
        {
            return Failed(
                report,
                started,
                $"SendInput refused the focus click at {x},{y} (Win32 error {Marshal.GetLastWin32Error()})");
        }

        clicks++;
        Thread.Sleep(FocusSettleMs);

        var previousForeground = GetForegroundWindow();

        // Ctrl+A first: the field may hold the text the value pattern wrote a moment ago, and typing has to replace
        // it rather than append to it.
        if (!SendKey(VirtualKeyControl, down: true, unicode: false)
            || !SendKey(VirtualKeyA, down: true, unicode: false)
            || !SendKey(VirtualKeyA, down: false, unicode: false)
            || !SendKey(VirtualKeyControl, down: false, unicode: false))
        {
            return Failed(report, started, "SendInput refused the Ctrl+A that clears the field");
        }

        var keys = 4;

        // KEYEVENTF_UNICODE with the character in wScan is how text goes in without depending on the keyboard
        // layout: the field receives "狸" whether or not the user has an IME or a Chinese keyboard. A surrogate pair
        // is sent as its two code units, which is what the API expects.
        foreach (var character in text)
        {
            if (!SendKey(character, down: true, unicode: true) || !SendKey(character, down: false, unicode: true))
            {
                return Failed(report, started, $"SendInput refused the keystroke for U+{(int)character:X4}");
            }

            keys += 2;
        }

        // A last settle, then the read: the value pattern is the same view the dialog has of its own field, so this
        // says whether the field now holds the text for real.
        Thread.Sleep(FocusSettleMs);

        var after = UiaCalls.TryValue(element, out var value, out _) ? value : null;
        var foreground = GetForegroundWindow();

        report["ran"] = true;
        report["cursor"] = new JsonObject { ["x"] = x, ["y"] = y };
        report["clicks"] = clicks;
        report["keys"] = keys;
        report["wasInFront"] = inFront;
        report["foreground"] = foreground == topLevel;
        report["foregroundBefore"] = DialogModel.HandleText(previousForeground);
        report["valueAfter"] = after;
        report["valueMatches"] = after is not null && string.Equals(after, text, StringComparison.Ordinal);
        report["ms"] = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        report["note"] = "Real input: the pointer moved to the control's centre and was clicked, then Ctrl+A and the "
            + "text went in as KEYEVENTF_UNICODE keystrokes. `valueAfter` is the field read back through the value "
            + "pattern afterwards, and `valueMatches` says whether it holds what was typed. Read `foreground`: a "
            + "click that did not bring the window forward types into whatever had focus, and the two together are "
            + "the difference between 'committed' and 'written'.";

        return report;
    }

    private static JsonObject Failed(JsonObject report, long started, string reason)
    {
        report["ran"] = false;
        report["reason"] = reason;
        report["ms"] = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;

        return report;
    }

    /// <summary>One left click at a screen position, as a move and a button press, all through <c>SendInput</c>.</summary>
    private static bool Click(int x, int y)
    {
        var (width, height) = VirtualScreen();

        if (width <= 1 || height <= 1)
        {
            return false;
        }

        var (left, top) = VirtualOrigin();

        // SendInput's absolute coordinates are normalised to 0..65535 over the virtual desktop; a plain move is
        // relative to wherever the pointer happens to be, which is not a target this code knows.
        var move = new[]
        {
            MouseEvent(MouseMove | MouseAbsolute | MouseVirtualDesk,
                Normalise(x, left, width),
                Normalise(y, top, height)),
            MouseEvent(MouseLeftDown | MouseAbsolute | MouseVirtualDesk,
                Normalise(x, left, width),
                Normalise(y, top, height)),
            MouseEvent(MouseLeftUp | MouseAbsolute | MouseVirtualDesk,
                Normalise(x, left, width),
                Normalise(y, top, height)),
        };

        return SendInput((uint)move.Length, move, Marshal.SizeOf<Input>()) == move.Length;
    }

    private static int Normalise(int value, int origin, int size)
        => (int)Math.Round((value - origin) * 65535.0 / (size - 1), MidpointRounding.AwayFromZero);

    /// <summary>One keyboard event: a virtual key, or a character with <c>KEYEVENTF_UNICODE</c>.</summary>
    private static bool SendKey(ushort key, bool down, bool unicode)
    {
        var flags = (down ? 0u : KeyboardKeyUp) | (unicode ? KeyboardUnicode : 0u);
        var one = new[] { KeyEvent(flags, unicode ? (ushort)0 : key, unicode ? key : (ushort)0) };

        return SendInput(1, one, Marshal.SizeOf<Input>()) == 1;
    }

    private static Input MouseEvent(uint flags, int x, int y) => new()
    {
        Type = InputMouse,
        Union = new InputUnion { Mouse = new MouseInput { X = x, Y = y, Flags = flags } },
    };

    private static Input KeyEvent(uint flags, ushort virtualKey, ushort scan) => new()
    {
        Type = InputKeyboard,
        Union = new InputUnion { Keyboard = new KeyboardInput { VirtualKey = virtualKey, ScanCode = scan, Flags = flags } },
    };

    private static (int Width, int Height) VirtualScreen()
        => (GetSystemMetrics(MetricVirtualWidth), GetSystemMetrics(MetricVirtualHeight));

    private static (int Left, int Top) VirtualOrigin()
        => (GetSystemMetrics(MetricVirtualX), GetSystemMetrics(MetricVirtualY));

    /// <summary>
    /// <c>INPUT</c>: a type tag and the union of the three input shapes. Sequential, so the union lands at offset 8
    /// on the 64-bit targets Native AOT publishes, which is what <c>Marshal.SizeOf</c> passes to <c>SendInput</c>.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct Input
    {
        public uint Type;

        public InputUnion Union;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)]
        public MouseInput Mouse;

        [FieldOffset(0)]
        public KeyboardInput Keyboard;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput
    {
        public int X;

        public int Y;

        public uint Data;

        public uint Flags;

        public uint Time;

        public nint ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInput
    {
        public ushort VirtualKey;

        public ushort ScanCode;

        public uint Flags;

        public uint Time;

        public nint ExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, [In] Input[] inputs, int size);

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(nint handle);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);
}
