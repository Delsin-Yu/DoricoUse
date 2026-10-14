using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;
using DoricoUse.Cli.Dorico;
using DoricoUse.Cli.Json;

namespace DoricoUse.Cli.Ui;

/// <summary>
/// The modeless windows Dorico has up: what they are, what they are made of, and how to work one of their elements.
/// </summary>
/// <remarks>
/// <para>
/// A panel is not a dialog, and this file exists so that the difference is visible in the payload rather than
/// buried. <c>dialog</c> keeps its meaning — the one blocking modal, or null — and a panel is reported beside it, in
/// <c>panels</c>. An agent that only knew about dialogs cannot be surprised by a panel appearing where a dialog was
/// expected, and an agent that needs the library manager has a name for it.
/// </para>
/// <para>
/// The <c>panels</c> array on every payload is cheap Win32 and carries no elements. Reading a panel's elements is
/// UI Automation, so it happens only in <c>ui panels</c> and <c>ui press</c>, which are the two commands that asked
/// for it.
/// </para>
/// </remarks>
internal static class PanelSurface
{
    /// <summary>Every Dorico window that is not the document window, with the elements of the one that was asked for.</summary>
    /// <param name="panelId">The <c>panel.id</c> to read in full, or null to list the windows without elements.</param>
    /// <param name="raw">True to keep every element the walk visited, for a caller that is diagnosing.</param>
    public static JsonObject List(string? panelId, bool raw)
    {
        var started = Stopwatch.GetTimestamp();
        var presence = DialogProbe.Probe();
        var panels = new JsonArray();
        var read = 0;
        string? missing = null;

        foreach (var window in presence.All ?? [])
        {
            var isDocument = presence.Document != nint.Zero && window.Handle == presence.Document;
            var id = PanelModel.PanelId(window.Title, window.WindowClass, PanelModel.KindUnknown, isDocument);
            var wanted = panelId is null
                || string.Equals(id, panelId, StringComparison.Ordinal)
                || window.Handle.ToString(CultureInfo.InvariantCulture) == panelId
                || Handle(window.Handle) == panelId;

            if (!wanted)
            {
                continue;
            }

            if (panelId is not null)
            {
                // Written out rather than with a collection expression: assigning a `JsonObject` into a `JsonArray`
                // that way picks the generic `JsonArray.Add<T>`, which needs code generation and is refused by the
                // Native AOT publish (IL2026/IL3050).
                JsonArr.AddNode(panels, ReadWindow(window, presence, raw, ref read));
                missing = null;

                break;
            }

            JsonArr.AddNode(panels, PanelModel.Describe(
                window,
                presence.DocumentEnabled,
                presence.Document,
                presence.Dialog?.Handle ?? nint.Zero));
        }

        if (panelId is not null && panels.Count == 0 && missing is null)
        {
            missing = $"No visible window of Dorico has the id '{panelId}'. The `panels` array of this payload lists "
                + "the ones it has, each with its own id.";
        }

        var result = new JsonObject
        {
            ["panels"] = panels,
            ["count"] = panels.Count,
            ["readWithUia"] = read,
            ["ms"] = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds,
            ["probe"] = DialogModel.ProbePayload(new DialogProbeInfo(
                How: read > 0 ? "win32+uia" : DialogProbe.How,
                Milliseconds: presence.Milliseconds,
                Degraded: presence.Degraded,
                Error: presence.Error,
                Windows: presence.Windows,
                DocumentEnabled: presence.DocumentEnabled)),
        };

        if (missing is not null)
        {
            result["miss"] = missing;
        }

        result["note"] = "A panel is a modeless window: it does not stall Dorico's command queue, which is why it is "
            + "reported here and not in `dialog`. `blocksQueue` is the hint from the window facts; `execution` on a "
            + "command's payload is the measurement of the queue.";

        return result;
    }

    /// <summary>
    /// Waits, bounded, for a panel with this id to be up, and says whether it appeared.
    /// </summary>
    /// <param name="panelId">The <c>panel.id</c> a caller means to work an element of.</param>
    /// <param name="timeoutMs">How long to keep looking, in milliseconds.</param>
    /// <remarks>
    /// For a command that raises a panel itself and then works an element of it: measured, the layout options
    /// dialog raised by <c>Project.Layout.AllPartOptions</c> is the <c>Qt6101QWindowIcon</c> window titled
    /// <c>&lt;document&gt; - 布局选项</c> and its 162 elements are readable within the same second, but a raise that
    /// has not finished drawing yet would otherwise be reported as a missing panel. The loop is the cheap Win32
    /// probe, so it costs nothing while it waits.
    /// </remarks>
    public static bool WaitForPanel(string panelId, int timeoutMs)
    {
        ArgumentNullException.ThrowIfNull(panelId);

        var deadline = Stopwatch.GetTimestamp() + (long)(Math.Max(0, timeoutMs) * (double)Stopwatch.Frequency / 1000);

        while (true)
        {
            var presence = DialogProbe.Probe();

            foreach (var window in presence.All ?? [])
            {
                var isDocument = presence.Document != nint.Zero && window.Handle == presence.Document;
                var id = PanelModel.PanelId(window.Title, window.WindowClass, PanelModel.KindUnknown, isDocument);

                if (string.Equals(id, panelId, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            if (Stopwatch.GetTimestamp() >= deadline)
            {
                return false;
            }

            Thread.Sleep(100);
        }
    }

    /// <summary>
    /// Works one element of one panel: chooses a tab, flips a check box, selects a list or tree item, opens an
    /// expander, presses a button, or writes a text field through the value pattern.
    /// </summary>
    /// <param name="panelId">The <c>panel.id</c> the caller named.</param>
    /// <param name="elementId">The <c>element.id</c> the caller named.</param>
    /// <param name="value">Text to write into the element, when it has a value pattern.</param>
    /// <param name="allowUnknownDialog">True when <c>--allow-unknown-dialog</c> was passed.</param>
    /// <param name="commitInput">
    /// True when <c>--commit-input</c> was passed: after the value write, click the control and type the text as
    /// real input. See <see cref="InputSimulator"/> for why a value-pattern write is not enough for a Qt field.
    /// </param>
    public static JsonObject Act(
        string panelId,
        string elementId,
        string? value,
        bool allowUnknownDialog,
        bool commitInput = false)
    {
        var presence = DialogProbe.Probe();
        var window = (presence.All ?? [])
            .FirstOrDefault(candidate => string.Equals(
                PanelModel.PanelId(
                    candidate.Title,
                    candidate.WindowClass,
                    PanelModel.KindUnknown,
                    presence.Document != nint.Zero && candidate.Handle == presence.Document),
                panelId,
                StringComparison.Ordinal));

        if (window is null)
        {
            throw new CliException(
                "PanelNotFound",
                $"No visible window of Dorico has the id '{panelId}'. `ui panels` lists the ones it has, each with its "
                + "own id and with the elements of the one you name. Dorico's document window - the one holding the "
                + "Setup player, flow and layout lists - is `" + PanelModel.DocumentWindowId + "`.",
                DialogModel.RefusalExitCode)
            {
                Details = new JsonObject
                {
                    ["requested"] = panelId,
                    ["ids"] = Ids(presence),
                },
            };
        }

        var isDocument = presence.Document != nint.Zero && window.Handle == presence.Document;
        var known = PanelCatalogue.Match(window.Title, window.WindowClass);

        // The document window is not in the panel catalogue - its caption is the document's name, which changes -
        // but it is measured, so working an element inside it needs no --allow-unknown-dialog.
        if (known is null && !isDocument && !allowUnknownDialog)
        {
            throw new CliException(
                "UnknownDialog",
                $"The window '{window.Title}' ({window.WindowClass}) is not in this build's panel catalogue, so "
                + "working one of its elements is refused. It is reported in full by `ui panels`. Pass "
                + "--allow-unknown-dialog to act on an unmeasured panel anyway.",
                DialogModel.RefusalExitCode)
            {
                Details = new JsonObject
                {
                    ["panel"] = PanelModel.PanelId(window.Title, window.WindowClass, PanelModel.KindUnknown, isDocument),
                    ["title"] = window.Title,
                    ["class"] = window.WindowClass,
                },
            };
        }

        var started = Stopwatch.GetTimestamp();
        var read = PanelReader.Read(window.Handle);

        try
        {
            var described = PanelModel.Describe(read.Elements);
            var index = IndexOf(described, elementId);

            if (index < 0)
            {
                throw new CliException(
                    "ElementNotFound",
                    $"The panel '{panelId}' ('{window.Title}') has no element '{elementId}'. Its ids are: "
                    + (described.Count == 0 ? "(none: the read found no actionable element)" : string.Join(", ", described.Select(element => element.Id))),
                    DialogModel.RefusalExitCode)
                {
                    Details = new JsonObject
                    {
                        ["panel"] = panelId,
                        ["requested"] = elementId,
                        ["ids"] = Ids(described),
                    },
                };
            }

            var element = described[index];
            var input = read.Elements[index];

            // A disabled target is refused before any pattern is called, exactly as a panel element is: measured
            // 2026-10-13, a pattern call on a disabled element reports success and changes nothing.
            if (!element.Enabled)
            {
                throw PanelModel.RefuseDisabled(panelId, element.Id, element.Name, element.Role);
            }

            var action = PanelModel.Action(input);

            if (action == PanelAction.None)
            {
                throw new CliException(
                    "ElementNotActionable",
                    $"Element '{element.Id}' ('{element.Name}') of '{panelId}' exposes no pattern this build can use: "
                    + "no invoke, no toggle, no selection, no expand-collapse and no value. Its role is "
                    + $"'{element.Role}'. This build never clicks a coordinate and never types at random.",
                    DialogModel.RefusalExitCode)
                {
                    Details = new JsonObject
                    {
                        ["panel"] = panelId,
                        ["element"] = element.Id,
                        ["name"] = element.Name,
                        ["role"] = element.Role,
                    },
                };
            }

            // The action itself is the dialog surface's action too: one implementation, so an element behaves the
            // same wherever it lives. What is added here is what a panel's payload says around it.
            var performed = ElementActions.Perform(
                read.Handles[index],
                action,
                element,
                value,
                panelId,
                commitInput,
                window.Handle);
            performed["panel"] = panelId;

            performed["ms"] = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            performed["note"] = commitInput
                ? "The action's own answer is not evidence — measured, a press that destroys its own window "
                    + "reported an error after it had worked. `stateAfter` and the `panels` array below are a second "
                    + "read of the same window, in the same invocation, which is the evidence. `written` and "
                    + "`committed` say whether the text only went in through the value pattern or also arrived as real "
                    + "input; whether the window then accepted it is what `stateAfter` and the window's own buttons "
                    + "say."
                : "The action's own answer is not evidence — measured, a press that destroys its own window "
                    + "reported an error after it had worked. `stateAfter` and the `panels` array below are a "
                    + "second read of the same window, in the same invocation, which is the evidence.";

            // One read again, and it answers both questions: what the element says now, and what the panel is now. A
            // separate `stateAfter` read would be a third snapshot of a window that may already differ from the one
            // the `panels` array describes, so there is one.
            var (stateAfter, after) = ReRead(window, presence, elementId, started);

            performed["stateAfter"] = stateAfter;

            var panelAfter = new JsonArray();
            JsonArr.AddNode(panelAfter, after);

            return new JsonObject
            {
                ["worked"] = performed,
                ["panels"] = panelAfter,
                ["count"] = 1,
                ["readWithUia"] = 1,
            };
        }
        finally
        {
            PanelReader.ReleaseAll(read.Handles);
        }
    }

    /// <summary>
    /// Presses the one button of a panel whose label is this, re-reading the id from a fresh read because the id
    /// itself is not stable.
    /// </summary>
    /// <param name="panelId">The <c>panel.id</c> the caller named.</param>
    /// <param name="label">The button's label, verbatim — <c>关闭</c> for the 布局选项 dialog.</param>
    /// <param name="allowUnknownDialog">True when <c>--allow-unknown-dialog</c> was passed.</param>
    /// <returns>
    /// The press's own payload, with the id it used, how many reads it took, and — when the button could not be
    /// matched — <c>pressed: null</c> and the reason, because a caller that asked for a dialog to be closed must be
    /// able to tell "closed" from "no such button here" without reading the window's whole element list.
    /// </returns>
    /// <remarks>
    /// Measured 2026-10-13: <c>project layout-defaults</c> left the 布局选项 dialog up, and its 关闭/应用 buttons
    /// have no unique automation id in Qt — 关闭 was <c>button-98</c> in one read — so an id remembered from a
    /// previous payload is a guess. The label is what is stable, and the match is exact and unique
    /// (<see cref="PanelModel.ButtonByLabel"/>), so an ambiguous or missing label presses nothing. The caller
    /// decides which label; this never presses a button it was not asked for, and in particular never 应用.
    /// </remarks>
    public static JsonObject PressByLabel(string panelId, string label, bool allowUnknownDialog = false)
    {
        ArgumentNullException.ThrowIfNull(panelId);
        ArgumentNullException.ThrowIfNull(label);

        for (var attempt = 1; attempt <= 2; attempt++)
        {
            var presence = DialogProbe.Probe();
            var window = (presence.All ?? [])
                .FirstOrDefault(candidate => string.Equals(
                    PanelModel.PanelId(
                        candidate.Title,
                        candidate.WindowClass,
                        PanelModel.KindUnknown,
                        presence.Document != nint.Zero && candidate.Handle == presence.Document),
                    panelId,
                    StringComparison.Ordinal));

            if (window is null)
            {
                return Miss(panelId, label, attempt, "no visible window of Dorico has that id");
            }

            var read = PanelReader.Read(window.Handle);

            try
            {
                var described = PanelModel.Describe(read.Elements);
                var target = PanelModel.ButtonByLabel(described, label);

                if (target is null)
                {
                    var sameLabel = described.Count(element => string.Equals(element.Name, label, StringComparison.Ordinal));

                    return Miss(
                        panelId,
                        label,
                        attempt,
                        sameLabel == 0
                            ? $"no button of '{window.Title}' is labelled '{label}'"
                            : $"{sameLabel} elements of '{window.Title}' are labelled '{label}', so pressing one "
                                + "would be a guess");
                }

                try
                {
                    var result = Act(panelId, target.Id, null, allowUnknownDialog);

                    result["match"] = new JsonObject
                    {
                        ["label"] = label,
                        ["element"] = target.Id,
                        ["role"] = target.Role,
                        ["attempts"] = attempt,
                        ["note"] = "The button was matched by its label in a fresh read of the window, because this "
                            + "build's own ids for the 布局选项 dialog's close and apply buttons are positional and "
                            + "move when the page changes.",
                    };

                    return result;
                }
                catch (CliException ex) when (ex.Code == "ElementNotFound" && attempt == 1)
                {
                    // The id moved between this read and the press's own read: read again and press the label's new
                    // id. One retry only - more would be a loop around a window that is changing under the tool.
                }
            }
            finally
            {
                PanelReader.ReleaseAll(read.Handles);
            }
        }

        return Miss(panelId, label, 2, "the button's id moved between two reads of the window");
    }

    /// <summary>
    /// What a by-label press reports when it found nothing to press: the same shape a successful one has, so a
    /// caller reads `pressed` and not the absence of a field.
    /// </summary>
    private static JsonObject Miss(string panelId, string label, int attempts, string reason)
        => new()
        {
            ["panel"] = panelId,
            ["label"] = label,
            ["pressed"] = null,
            ["attempts"] = attempts,
            ["reason"] = reason,
            ["note"] = "Nothing was pressed. A by-label press only presses an exact, unique label match, so a label "
                + "that is missing or shared presses nothing rather than the first candidate.",
        };

    /// <summary>
    /// Reads the panel once more, in the same invocation, to see what the action did: the element's own new state,
    /// and the panel as it is now. A fresh read is the only honest answer — the element pointers from the first read
    /// describe the tree as it was before the action.
    /// </summary>
    private static (JsonNode? State, JsonObject Panel) ReRead(
        WindowFacts window,
        DialogPresence presence,
        string elementId,
        long started)
    {
        try
        {
            var read = PanelReader.Read(window.Handle);

            try
            {
                var described = PanelModel.Describe(read.Elements);
                var panel = PanelModel.Elements(
                    window,
                    presence.DocumentEnabled,
                    read.Elements,
                    read.Truncated,
                    (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                    error: null,
                    documentHandle: presence.Document,
                    dialogHandle: presence.Dialog?.Handle ?? nint.Zero,
                    nodesVisited: read.Nodes);

                // An element that is gone is a real answer too: it is what a tab that was closed and a panel that
                // was dismissed look like, and the null says exactly that.
                var state = PanelModel.StateAfter(described, read.Elements, elementId);

                return (state, panel);
            }
            finally
            {
                PanelReader.ReleaseAll(read.Handles);
            }
        }
        catch (CliException ex)
        {
            return (
                new JsonObject { ["error"] = ex.Code },
                PanelModel.Elements(window, presence.DocumentEnabled, null, truncated: false, 0, ex.Message));
        }
    }

    /// <summary>The window facts plus the elements of one window, for <c>ui panels &lt;id&gt;</c>.</summary>
    private static JsonObject ReadWindow(WindowFacts window, DialogPresence presence, bool raw, ref int read)
    {
        var started = Stopwatch.GetTimestamp();

        try
        {
            var result = PanelReader.Read(window.Handle, every: raw);
            read++;

            try
            {
                var payload = PanelModel.Elements(
                    window,
                    presence.DocumentEnabled,
                    result.Elements,
                    result.Truncated,
                    (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                    error: null,
                    documentHandle: presence.Document,
                    dialogHandle: presence.Dialog?.Handle ?? nint.Zero,
                    nodesVisited: result.Nodes);

                if (result.Every is not null)
                {
                    // --raw adds the whole tree the walk saw, every node, kept or not. That is the read which tells
                    // "this window holds five controls" apart from "UI Automation exposes five controls of this
                    // window": measured, the library manager's 27 nodes are 13 unnamed CollectionListDelegate
                    // groups, 2 labels, 4 panes and 2 scroll bars around its 5 elements.
                    payload["nodes"] = PanelModel.NodePayload(result.Every, out var nodesReturned);
                    payload["nodesReturned"] = nodesReturned;
                    payload["nodesNote"] = "Every node the walk visited, not only the elements an action can "
                        + "target: `kept` says which is which. The values of these nodes were not read — this is the "
                        + "shape of the tree, and a node with no name and no value is exactly what a Qt/QML window "
                        + "gives a reader that expects a list row.";
                }

                return payload;
            }
            finally
            {
                PanelReader.ReleaseAll(result.Handles);
            }
        }
        catch (Exception ex)
        {
            // A panel that cannot be read is still reported: knowing the window is up matters even when its content
            // is not reachable, and the error says so instead of leaving an empty element list to be misread.
            return PanelReader.Failure(
                window,
                presence.DocumentEnabled,
                (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                ex.Message);
        }
    }

    private static int IndexOf(List<PanelElementDescription> elements, string id)
    {
        for (var index = 0; index < elements.Count; index++)
        {
            if (string.Equals(elements[index].Id, id, StringComparison.Ordinal))
            {
                return index;
            }
        }

        return -1;
    }

    private static JsonArray Ids(List<PanelElementDescription> elements)
    {
        var ids = new JsonArray();

        foreach (var element in elements)
        {
            JsonArr.AddText(ids, element.Id);
        }

        return ids;
    }

    /// <summary>The page ids of the windows that are up, for an error message that tells the caller what to name.</summary>
    private static JsonArray Ids(DialogPresence presence)
    {
        var ids = new JsonArray();

        foreach (var window in presence.All ?? [])
        {
            JsonArr.AddText(ids, PanelModel.PanelId(
                window.Title,
                window.WindowClass,
                PanelModel.KindUnknown,
                presence.Document != nint.Zero && window.Handle == presence.Document));
        }

        return ids;
    }

    private static string Handle(nint handle) => "0x" + ((long)handle).ToString("X8", CultureInfo.InvariantCulture);
}
