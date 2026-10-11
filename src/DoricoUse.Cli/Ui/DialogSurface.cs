using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;
using DoricoUse.Cli.Dorico;

namespace DoricoUse.Cli.Ui;

/// <summary>
/// The one place a dialog is looked at, and the one place an invocation decides what to report about it.
/// </summary>
/// <remarks>
/// <para>
/// Every command's payload passes through <see cref="Extras"/> on its way out, so no command can forget to say that
/// a dialog is up. The cost is only paid when there is something to say: the presence probe is cheap Win32, and the
/// UI Automation read behind it runs only when a window is actually modal.
/// </para>
/// <para>
/// The <c>dialog</c> field is always present — <c>null</c> when nothing is modal — and the <c>execution</c> field
/// only appears when this invocation posted something to Dorico, because that is when it has something to say.
/// </para>
/// </remarks>
internal static class DialogSurface
{
    /// <summary>How often a wait loop may look: often enough to catch a dialog, rare enough to be free.</summary>
    private static readonly TimeSpan PeekInterval = TimeSpan.FromMilliseconds(400);

    /// <summary>How long <c>ui press --timeout-ms</c> waits for its dialog by default.</summary>
    public const int DefaultPressTimeoutMs = 0;

    /// <summary>How often the re-probe after a press checks whether the transition has happened.</summary>
    private static readonly TimeSpan SettleInterval = TimeSpan.FromMilliseconds(50);

    /// <summary>How long the re-probe after a press waits for that transition.</summary>
    /// <remarks>
    /// Measured on 2026-10-11, pressing 取消 on the MusicXML export dialog: the press returned at 0.157 s into the
    /// instrument's window log and the Qt window was gone by 0.239 s, 82 ms later. The bound is that measurement
    /// with a wide margin, and the full bound is paid only by a button that leaves its dialog up (选择全部 does):
    /// a dialog that closes is reported as soon as it has gone.
    /// </remarks>
    private static readonly TimeSpan SettleBudget = TimeSpan.FromMilliseconds(1000);

    private static long lastPeek;
    private static JsonObject? peeked;

    /// <summary>Starts the execution log and the dialog probe for one invocation.</summary>
    public static void Begin() => LogQueue.Begin();

    /// <summary>Records a command this invocation sent to Dorico, so the log can be asked what became of it.</summary>
    public static void MarkCommand(string? name)
    {
        if (!string.IsNullOrWhiteSpace(name))
        {
            LogQueue.Mark(name);
        }
    }

    /// <summary>Records Dorico's answer, which is what tells "never posted" apart from "posted but not run".</summary>
    public static void AcknowledgeCommand(string? name, string? ack)
    {
        if (!string.IsNullOrWhiteSpace(name))
        {
            LogQueue.Acknowledge(name, ack);
        }
    }

    /// <summary>
    /// What this invocation has to say about Dorico's dialogs, its panels and its command queue, ready to be merged
    /// into the payload. Always carries <c>dialog</c> and <c>panels</c>; <c>dialog</c> is null when nothing is modal,
    /// and a panel is never reported there.
    /// </summary>
    /// <param name="dialogOwned">
    /// True when the command's own payload already carries <c>dialog</c> — <c>ui dialog</c> and <c>ui press</c> do,
    /// and theirs is the better answer, so the probe is not repeated here.
    /// </param>
    /// <param name="panelsOwned">
    /// True when the payload already carries <c>panels</c>: <c>ui panels</c> reads UI Automation behind them, which
    /// is the better answer, and this one costs only Win32.
    /// </param>
    public static JsonObject Extras(bool dialogOwned = false, bool panelsOwned = false)
    {
        var extras = new JsonObject();
        var execution = LogQueue.Verdict(LogQueue.DefaultBudget);

        if (execution is not null)
        {
            extras["execution"] = execution;
        }

        if (dialogOwned && panelsOwned)
        {
            return extras;
        }

        // One probe serves both fields: it is the cheap Win32 fact, and calling it twice would report two different
        // moments of a window list that a dialog can change between them.
        var presence = DialogProbe.Probe();

        if (!dialogOwned)
        {
            extras["dialog"] = Describe(presence, raw: false, out _);
        }

        if (!panelsOwned)
        {
            extras["panels"] = PanelModel.Windows(
                presence.All ?? [],
                presence.DocumentEnabled,
                presence.Document,
                presence.Dialog?.Handle ?? nint.Zero);
        }

        return extras;
    }

    /// <summary>The dialog that is up right now, or null, without waiting.</summary>
    public static JsonObject? Current() => Describe(DialogProbe.Probe(), raw: false, out _);

    /// <summary>
    /// Probes while a caller is waiting for something else, at most once every <see cref="PeekInterval"/>. Once a
    /// dialog has been seen it is kept, because what a wait loop has to report is that a dialog <b>was</b> there.
    /// </summary>
    public static JsonObject? Peek()
    {
        if (peeked is not null)
        {
            return peeked;
        }

        var now = Stopwatch.GetTimestamp();

        if (lastPeek != 0 && Stopwatch.GetElapsedTime(lastPeek).TotalMilliseconds < PeekInterval.TotalMilliseconds)
        {
            return null;
        }

        lastPeek = now;
        peeked = Current();

        return peeked;
    }

    /// <summary>Reads the dialog that is up, for <c>ui dialog</c>.</summary>
    /// <param name="raw">True to include every UI Automation element the read walked.</param>
    /// <param name="probe">The presence probe's own cost, whether or not a dialog was found.</param>
    public static JsonObject? Read(bool raw, out JsonObject probe)
    {
        var presence = DialogProbe.Probe();
        var dialog = Describe(presence, raw, out _);

        probe = DialogModel.ProbePayload(new DialogProbeInfo(
            How: dialog is null ? presence.How : presence.How + "+uia",
            Milliseconds: presence.Milliseconds,
            Degraded: presence.Degraded,
            Error: presence.Error,
            Windows: presence.Windows,
            DocumentEnabled: presence.DocumentEnabled));

        return dialog;
    }

    /// <summary>
    /// Presses one button, after re-probing: the dialog named must be the one that is up, or the press refuses.
    /// </summary>
    /// <param name="dialogId">The <c>dialog.id</c> the caller named.</param>
    /// <param name="buttonId">The <c>button.id</c> the caller named.</param>
    /// <param name="allowUnknownDialog">True when <c>--allow-unknown-dialog</c> was passed.</param>
    /// <param name="allowDestructive">True when <c>--allow-destructive</c> was passed.</param>
    /// <param name="path">A value to write into a picker's file-name box first, or null.</param>
    /// <param name="timeoutMs">How long to wait for the named dialog to appear, in milliseconds.</param>
    public static JsonObject Press(
        string dialogId,
        string buttonId,
        bool allowUnknownDialog,
        bool allowDestructive,
        string? path,
        int timeoutMs)
    {
        var presence = WaitForDialog(timeoutMs);
        var window = presence.Dialog
            ?? throw new CliException(
                "DialogNotFound",
                $"Nothing is modal in Dorico, so there is no '{dialogId}' to press '{buttonId}' in. A dialog can be "
                + "raised by a command in another invocation; `ui dialog` reads the current state.",
                DialogModel.RefusalExitCode)
            {
                Details = new JsonObject
                {
                    ["requestedDialog"] = dialogId,
                    ["requestedButton"] = buttonId,
                    ["probe"] = DialogModel.ProbePayload(new DialogProbeInfo(
                        presence.How,
                        presence.Milliseconds,
                        presence.Degraded,
                        presence.Error,
                        Windows: presence.Windows,
                        DocumentEnabled: presence.DocumentEnabled)),
                },
            };

        var owner = Format(window.Owner);
        var started = Stopwatch.GetTimestamp();
        DialogRead read;

        try
        {
            read = DialogReader.Read(window.Handle, window.Title, window.WindowClass, owner, raw: false);
        }
        catch (CliException ex) when (NodeRead.Str(ex.Details, "hresult") == GoneHresult)
        {
            // The dialog went away between the probe and the read. That is not a broken tool and not a pressable
            // dialog either: there is nothing to press in any more, which is what DialogNotFound says.
            throw new CliException(
                "DialogNotFound",
                $"The dialog '{window.Title}' was there a moment ago and is gone now, so there is nothing to press "
                + "in. Nothing was pressed.",
                DialogModel.RefusalExitCode)
            {
                Details = new JsonObject
                {
                    ["requestedDialog"] = dialogId,
                    ["requestedButton"] = buttonId,
                    ["title"] = window.Title,
                },
            };
        }
        catch (Exception ex)
        {
            throw new CliException(
                "DialogProbeFailed",
                $"The dialog '{window.Title}' is up but could not be read through UI Automation ({ex.Message}), so "
                + "no button can be identified and nothing was pressed.",
                DialogModel.ProbeFailureExitCode)
            {
                Details = new JsonObject
                {
                    ["title"] = window.Title,
                    ["class"] = window.WindowClass,
                },
            };
        }

        try
        {
            var description = DialogModel.Describe(read.Input, presence.DocumentEnabled);

            if (!string.Equals(description.Id, dialogId, StringComparison.Ordinal))
            {
                throw new CliException(
                    "WrongDialog",
                    $"The dialog that is up is '{description.Id}' ('{description.Title}'), not '{dialogId}'. Pressing "
                    + "the button by id would have acted on the wrong dialog, so nothing was pressed. `ui dialog` "
                    + "reads what is up now.",
                    DialogModel.RefusalExitCode)
                {
                    Details = new JsonObject
                    {
                        ["requested"] = dialogId,
                        ["actual"] = description.Id,
                        ["title"] = description.Title,
                        ["buttons"] = DialogModel.Payload(description, new DialogProbeInfo(presence.How, presence.Milliseconds))["buttons"]?.DeepClone(),
                    },
                };
            }

            // A button id presses a button, exactly as it always has; any other id is one of the dialog's controls
            // - the edit field, the check box, the list row - and is worked through the same pattern rules a panel
            // element goes through. The two id sets are disjoint, so this is decided by lookup, not by guesswork.
            var describedControls = PanelModel.Describe(read.Elements);

            if (description.Buttons.All(candidate => !string.Equals(candidate.Id, buttonId, StringComparison.Ordinal)))
            {
                return PressControl(window, description, read, buttonId, path, allowUnknownDialog, started);
            }

            var button = DialogModel.Authorize(description, buttonId, allowUnknownDialog, allowDestructive);
            var index = IndexOf(description, button.Id);
            var picker = path is null ? null : FilePicker.Fill(window.Handle, path);
            var outcome = DialogPresser.Press(read.ButtonElements[index]);

            var pressed = new JsonObject
            {
                ["dialog"] = description.Id,
                ["button"] = button.Id,
                ["name"] = button.Name,
                ["alias"] = button.Alias,
                ["role"] = button.Role,
                ["pattern"] = outcome.Pattern,
                ["elementGone"] = outcome.ElementGone,
                ["note"] = outcome.ElementGone
                    ? "The press reported that the element had gone while it was pressed (UIA_E_ELEMENTNOTAVAILABLE). "
                        + "That is what a press which closes its own window does, measured on the library manager's "
                        + "关闭 button, so it is a success and not a failure. The `dialog` field below is the "
                        + "re-probe and is the only evidence."
                    : "The press reported success. That is not evidence either: the `dialog` field below is the "
                        + "re-probe of what is up now.",
            };

            var result = new JsonObject { ["pressed"] = pressed };

            if (picker is not null)
            {
                result["picker"] = picker;
            }

            // The re-probe waits, and that wait is the fix for the second measured defect: the press of 取消
            // returned ok:true while the Qt dialog was still on screen and that window was destroyed 82 ms later,
            // so an immediate re-probe named the dialog that had just been answered - the one thing this payload
            // must never do.
            var after = Describe(SettledAfterPress(window.Handle), raw: false, out _);

            result["dialog"] = after;
            pressed["afterDialog"] = after is null ? null : NodeRead.Str(after, "id");
            pressed["readMs"] = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;

            return result;
        }
        finally
        {
            DialogPresser.ReleaseAll(read.ButtonElements);
            PanelReader.ReleaseAll(read.ElementHandles);
        }
    }

    /// <summary>
    /// Works one of a dialog's controls — an edit field, a check box, a list row, a tab — through its own pattern.
    /// </summary>
    /// <remarks>
    /// The guard is the same one a panel element gets: an uncatalogued dialog needs <c>--allow-unknown-dialog</c>,
    /// an element with no usable pattern is refused, and a text field needs <c>--value</c>. The difference from a
    /// button is only that the ids come from the element read. Measured, this is what makes a rename field fillable
    /// and the classic file picker's <c>1148</c> name box settable, neither of which needs a coordinate or a
    /// keystroke.
    /// </remarks>
    private static JsonObject PressControl(
        WindowFacts window,
        DialogDescription description,
        DialogRead read,
        string controlId,
        string? value,
        bool allowUnknownDialog,
        long started)
    {
        var controls = PanelModel.Describe(read.Elements);
        var index = -1;

        for (var candidate = 0; candidate < controls.Count; candidate++)
        {
            if (string.Equals(controls[candidate].Id, controlId, StringComparison.Ordinal))
            {
                index = candidate;

                break;
            }
        }

        if (index < 0)
        {
            var ids = new List<string>();

            foreach (var button in description.Buttons)
            {
                ids.Add(button.Id);
            }

            foreach (var control in controls)
            {
                ids.Add(control.Id);
            }

            throw ElementActions.NotFound(description.Id, controlId, ids, "The dialog");
        }

        if (description.Kind == DialogModel.KindUnknown && !allowUnknownDialog)
        {
            throw new CliException(
                "UnknownDialog",
                $"The dialog '{description.Title}' is not in this build's catalogue, so working one of its controls is "
                + "refused. It is reported in full above, with the control ids. Pass --allow-unknown-dialog to work a "
                + "control of an unmeasured dialog anyway.",
                DialogModel.RefusalExitCode)
            {
                Details = new JsonObject
                {
                    ["dialog"] = description.Id,
                    ["title"] = description.Title,
                    ["control"] = controlId,
                },
            };
        }

        var target = controls[index];
        var action = PanelModel.Action(read.Elements[index]);

        if (action == PanelAction.None)
        {
            throw new CliException(
                "ElementNotActionable",
                $"Control '{target.Id}' ('{target.Name}') of '{description.Id}' exposes no pattern this build can "
                + "use: no invoke, no toggle, no selection, no expand-collapse and no value. Its role is '"
                + target.Role + "'. This build never clicks a coordinate and never types at random.",
                DialogModel.RefusalExitCode)
            {
                Details = new JsonObject
                {
                    ["dialog"] = description.Id,
                    ["control"] = target.Id,
                    ["name"] = target.Name,
                    ["role"] = target.Role,
                },
            };
        }

        var worked = ElementActions.Perform(read.ElementHandles[index], action, target, value, description.Id);

        // The same evidence rule as a button press: the action's own answer is not the proof, the re-probe is.
        var after = Describe(SettledAfterPress(window.Handle), raw: false, out _);

        worked["ms"] = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        worked["note"] = "The action's own answer is not evidence. The `dialog` field below is a second read of the "
            + "same window, in the same invocation, and that is what proves it happened.";

        return new JsonObject
        {
            ["worked"] = worked,
            ["dialog"] = after,
        };
    }

    /// <summary><c>UIA_E_ELEMENTNOTAVAILABLE</c> as a string, because the caller switches on it, not on English.</summary>
    private static string GoneHresult => $"0x{UiaCom.ElementNotAvailable:X8}";

    private static int IndexOf(DialogDescription description, string buttonId)    {
        for (var index = 0; index < description.Buttons.Count; index++)
        {
            if (string.Equals(description.Buttons[index].Id, buttonId, StringComparison.Ordinal))
            {
                return index;
            }
        }

        // Authorize has already refused an id that is not there, so this cannot happen.
        throw new CliException("ButtonNotFound", $"No button '{buttonId}' in '{description.Id}'.", DialogModel.RefusalExitCode);
    }

    /// <summary>
    /// The dialog that is up after a press, waiting for the press to have taken effect: the window that was pressed
    /// is gone, a different dialog has taken its place, or the bound expires.
    /// </summary>
    /// <remarks>
    /// The presence probe is the cheap Win32 fact, so this loop costs no UI Automation read per attempt; the read
    /// runs once, on the settled answer. A dialog still up when the bound expires is reported as still up, which is
    /// the truth for a button that does not close its dialog.
    /// </remarks>
    /// <param name="pressed">The window handle the button was pressed in.</param>
    private static DialogPresence SettledAfterPress(nint pressed)
    {
        var deadline = Stopwatch.GetTimestamp() + (long)(SettleBudget.TotalMilliseconds * Stopwatch.Frequency / 1000);

        while (true)
        {
            var presence = DialogProbe.Probe();

            if (presence.Dialog is not { } window || window.Handle != pressed || Stopwatch.GetTimestamp() >= deadline)
            {
                return presence;
            }

            Thread.Sleep(SettleInterval);
        }
    }

    private static DialogPresence WaitForDialog(int timeoutMs)
    {
        var deadline = Stopwatch.GetTimestamp() + (long)(Math.Max(0, timeoutMs) * (double)Stopwatch.Frequency / 1000);
        DialogPresence presence;

        do
        {
            presence = DialogProbe.Probe();

            if (presence.Dialog is not null || Stopwatch.GetTimestamp() >= deadline)
            {
                return presence;
            }

            Thread.Sleep(150);
        }
        while (true);
    }

    /// <summary>
    /// The <c>dialog</c> object for one probe result: the window facts, the UI Automation read behind them, and what
    /// the read cost. A read that fails still reports the window, because knowing a dialog is up matters even when
    /// its content cannot be read.
    /// </summary>
    private static JsonObject? Describe(DialogPresence presence, bool raw, out bool truncated)
    {
        truncated = false;

        if (presence.Dialog is not { } window)
        {
            return null;
        }

        var owner = Format(window.Owner);
        var started = Stopwatch.GetTimestamp();

        try
        {
            var read = DialogReader.Read(window.Handle, window.Title, window.WindowClass, owner, raw);
            truncated = read.Truncated;

            try
            {
                var description = DialogModel.Describe(read.Input, presence.DocumentEnabled);
                var probe = new DialogProbeInfo(
                    How: "win32+uia",
                    Milliseconds: presence.Milliseconds + (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                    Degraded: false,
                    Error: null,
                    Nodes: read.Nodes,
                    Truncated: read.Truncated,
                    Windows: presence.Windows,
                    DocumentEnabled: presence.DocumentEnabled);

                var payload = DialogModel.Payload(description, probe);
                var elements = PanelModel.Describe(read.Elements);

                // A dialog's actionable elements beside its buttons, additively. They are called `controls` and not
                // `elements` because `--raw` already publishes the whole tree it walked under `elements`, and this
                // build never repurposes a field. Measured: the 重命名演奏者 dialog's fullNameLineEdit and
                // shortNameLineEdit were in `elements` (the raw walk) and in no shaped field, so they could be seen
                // and not used.
                var array = PanelModel.ElementPayload(elements, out var returned);
                payload["controls"] = array;
                payload["controlCount"] = elements.Count;
                payload["controlsReturned"] = returned;

                if (elements.Count > 0)
                {
                    payload["controlsNote"] = "Everything actionable here that is not a button: a text field, a check "
                        + "box, a list row, a tab. Act on one with `dorico-use ui press --dialog " + description.Id
                        + " --element <control id>` and `--value <text>` for a field. These ids never collide with the "
                        + "`buttons` ids.";
                }

                if (raw && read.Raw is not null)
                {
                    payload["elements"] = read.Raw;
                }

                return payload;
            }
            finally
            {
                DialogPresser.ReleaseAll(read.ButtonElements);
                PanelReader.ReleaseAll(read.ElementHandles);
            }
        }
        catch (Exception ex)
        {
            // The window is modal, so it is reported with what Win32 knows and no buttons. Everything that could
            // not be read is named in probe.error rather than hidden behind an empty dialog.
            var description = DialogModel.Describe(
                DialogReader.Empty(window.Title, window.WindowClass, owner),
                presence.DocumentEnabled);
            var probe = new DialogProbeInfo(
                How: presence.How,
                Milliseconds: presence.Milliseconds + (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                Degraded: true,
                Error: ex.Message,
                Windows: presence.Windows,
                DocumentEnabled: presence.DocumentEnabled);

            return DialogModel.Payload(description, probe);
        }
    }

    private static string? Format(nint handle)
        => handle == nint.Zero ? null : "0x" + ((long)handle).ToString("X8", CultureInfo.InvariantCulture);
}
