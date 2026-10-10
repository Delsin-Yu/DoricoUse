using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using DoricoUse.Cli.Json;

namespace DoricoUse.Cli.Ui;

/// <summary>
/// One top-level window, as Win32 describes it: plain data, with no handle arithmetic and no COM behind it.
/// </summary>
/// <remarks>
/// It lives here rather than beside the probe because <see cref="PanelModel"/> takes it as input, and PanelModel is
/// a pure file the check projects compile on <c>net10.0</c> — a record of a handle, a process id and four scalars is
/// not a Windows API, so the pure half can name the same type the probe fills in.
/// </remarks>
/// <param name="Handle">The window handle.</param>
/// <param name="ProcessId">The process that owns the window.</param>
/// <param name="Owner">The window's <c>GW_OWNER</c>, or zero.</param>
/// <param name="Foreground">True for the window that currently holds the foreground.</param>
/// <param name="Enabled">
/// The window's own <c>IsWindowEnabled</c>. For a modeless panel it is true, and so is the document window's; for a
/// blocking dialog it is true while its owner's is not.
/// </param>
/// <param name="WindowClass">The Win32 window class, verbatim.</param>
/// <param name="Title">The window caption, verbatim.</param>
internal sealed record WindowFacts(
    nint Handle,
    uint ProcessId,
    nint Owner,
    bool Foreground,
    bool Enabled,
    string WindowClass,
    string Title);

/// <summary>One element of a panel or dialog, in plain data: no COM pointer, no Win32 handle, no UI Automation type.</summary>
/// <param name="ControlType">The UI Automation control type id (50000 is a button, 50002 a check box, …).</param>
/// <param name="Name">The label the element really shows, verbatim. May be empty, may repeat another element's.</param>
/// <param name="AutomationId">The element's own automation id, verbatim (for Qt, a QML path).</param>
/// <param name="Value">The value pattern's current value, or null when the element has no value pattern.</param>
/// <param name="ToggleState">The toggle pattern's state (<c>on</c>, <c>off</c>, <c>indeterminate</c>), or null.</param>
/// <param name="Selected">The selection-item pattern's state, or null.</param>
/// <param name="Expanded">The expand-collapse state (<c>expanded</c>, <c>collapsed</c>, …), or null.</param>
/// <param name="Invokable">True when the element exposes an invoke pattern.</param>
/// <param name="Left">Screen x of the element's bounding rectangle.</param>
/// <param name="Top">Screen y of the element's bounding rectangle.</param>
internal sealed record PanelElementInput(
    int ControlType,
    string Name,
    string? AutomationId,
    string? Value,
    string? ToggleState,
    bool? Selected,
    string? Expanded,
    bool Invokable,
    int Left = 0,
    int Top = 0);

/// <summary>One element as the payload reports it, with the id an agent passes back to <c>ui press --element</c>.</summary>
/// <param name="Id">The stable id, derived from the same rules the dialog buttons use.</param>
/// <param name="Role">What the element is, in the vocabulary an agent acts on.</param>
/// <param name="Name">The label, verbatim.</param>
/// <param name="AutomationId">The raw automation id, verbatim, or null.</param>
/// <param name="Index">The position in the read order.</param>
/// <param name="State">
/// The measurable state: <c>checked</c> for a toggle, <c>selected</c> for a selection item, <c>value</c> when a
/// value pattern answered, and <c>expanded</c>/<c>collapsed</c> for an expander. Null when the element offers none.
/// </param>
/// <param name="Action">
/// Which pattern an action would use — <c>invoke</c>, <c>toggle</c>, <c>select</c>, <c>expand</c>, <c>collapse</c>
/// or <c>value</c> — or null when the element exposes none of them, in which case acting on it is refused.
/// </param>
internal sealed record PanelElementDescription(
    string Id,
    string Role,
    string Name,
    string? AutomationId,
    int Index,
    string? State,
    string? Action);

/// <summary>How one element would be acted on: the pattern, and what it would change.</summary>
public enum PanelAction
{
    /// <summary>The element exposes no pattern this build will use. Acting on it is refused.</summary>
    None,
    Invoke,
    Toggle,
    Select,
    Expand,
    Collapse,
    Value,
}

/// <summary>
/// Turns the window facts and the elements of a panel into the ids, the roles and the JSON an agent reads.
/// </summary>
/// <remarks>
/// <para>
/// Pure on purpose, and that is not a detail: every rule that decides what an agent may press on belongs here, so a
/// check project can compile this file on <c>net10.0</c> and assert the ids, the roles, the action choice and the
/// refusals without Dorico, a window or a COM pointer.
/// </para>
/// <para>
/// A panel is <b>not</b> a dialog, and the one field that matters is <c>blocksQueue</c>. Measured: the library
/// manager (<c>Qt6101QWindowIcon</c>) has no owner, leaves the document window enabled, and Dorico's command queue
/// keeps moving while it is up — 31 ms per command — whereas the MusicXML export dialog (<c>Qt6101QWindow</c>)
/// disables the document window and stalls that queue for 56 seconds. Reporting a modeless panel as a blocking
/// dialog would be the one lie this layer must not tell, so the classification is by the document window's
/// <b>enabled</b> state and the window's owner, and never by "a window is up".
/// </para>
/// </remarks>
internal static class PanelModel
{
    /// <summary>Shape of a Dorico/QML window.</summary>
    public const string ShapeQt = "qt";

    /// <summary>Shape of a classic Win32 dialog.</summary>
    public const string ShapeNative = "native";

    /// <summary>Shape of anything else.</summary>
    public const string ShapeOther = "other";

    public const string KindKnown = "known";

    public const string KindUnknown = "unknown";

    /// <summary>The longest automation-id tail or label slug that will be used as an element id.</summary>
    private const int MaxDerivedIdLength = 64;

    /// <summary>How far apart two elements may sit and still count as one row, for the position fallback.</summary>
    private const int RowTolerancePixels = 8;

    /// <summary>How many elements one panel or dialog may report.</summary>
    /// <remarks>
    /// A limit rather than a promise: the read already stops at its own node budget and says so
    /// (<c>truncated</c>), and an agent that gets four hundred elements of a library tree has what it needs to act.
    /// </remarks>
    public const int MaxElements = 400;

    /// <summary>How the class of a window is shaped, for <c>panel.shape</c>.</summary>
    public static string Shape(string windowClass)
    {
        ArgumentNullException.ThrowIfNull(windowClass);

        if (windowClass.StartsWith("Qt", StringComparison.OrdinalIgnoreCase))
        {
            return ShapeQt;
        }

        return windowClass == DialogModel.NativeDialogClass ? ShapeNative : ShapeOther;
    }

    /// <summary>
    /// The id of a panel: the panel catalogue's own id when it recognises this window, else a deterministic hash of
    /// the caption and the class, so the same panel keeps the same id in any later invocation.
    /// </summary>
    public static string PanelId(string title, string windowClass, string kind)
    {
        var entry = PanelCatalogue.Match(title, windowClass);

        if (entry is not null)
        {
            return entry.Id;
        }

        var material = Encoding.UTF8.GetBytes(title + "\0" + windowClass + "\0" + kind);
        var hash = SHA256.HashData(material);

        return "unknown." + Convert.ToHexStringLower(hash.AsSpan(0, 6));
    }

    /// <summary>
    /// Which pattern an action on this element would use, in the order the presser tries them.
    /// </summary>
    /// <remarks>
    /// Toggle before invoke for a check box and a tab: measured, a Qt check box exposes <b>both</b> an invoke and a
    /// toggle pattern, and invoking it does not always change its state, while toggling does. That is the same
    /// ordering rule the dialog presser already uses, extended to the two patterns a panel needs and a dialog did
    /// not have to reach for: selection and expansion.
    /// </remarks>
    public static PanelAction Action(PanelElementInput element)
    {
        ArgumentNullException.ThrowIfNull(element);

        if (element.ToggleState is not null)
        {
            return PanelAction.Toggle;
        }

        if (element.Selected is not null)
        {
            return PanelAction.Select;
        }

        if (element.Expanded is "collapsed")
        {
            return PanelAction.Expand;
        }

        if (element.Expanded is "expanded")
        {
            return PanelAction.Collapse;
        }

        if (element.Invokable)
        {
            return PanelAction.Invoke;
        }

        // A text field is not pressable, but it is usable: the value pattern is how a path goes into a picker.
        return element.Value is not null ? PanelAction.Value : PanelAction.None;
    }

    /// <summary>The role an agent sees, derived from the control type and the element's own patterns.</summary>
    public static string Role(PanelElementInput element)
    {
        ArgumentNullException.ThrowIfNull(element);

        return element.ControlType switch
        {
            50000 => "button",
            50002 => "checkBox",
            50003 => "comboBox",
            50004 => "edit",
            50005 => "hyperlink",
            50008 => "list",
            50011 => "menuItem",
            50013 => "radioButton",
            50015 => "slider",
            50018 => "tab",
            50019 => "tabItem",
            50023 => "tree",
            50024 => "treeItem",
            50026 => "group",
            50029 => "listItem",
            50031 => "splitButton",
            50033 => "pane",
            50037 => "titleBar",
            50038 => "separator",
            _ => "controlType-" + element.ControlType.ToString(CultureInfo.InvariantCulture),
        };
    }

    /// <summary>The measurable state of one element, or null when it offers none of the state patterns.</summary>
    public static string? State(PanelElementInput element)
    {
        ArgumentNullException.ThrowIfNull(element);

        if (element.ToggleState is not null)
        {
            return element.ToggleState;
        }

        if (element.Selected == true)
        {
            return "selected";
        }

        if (element.Selected == false)
        {
            return null;
        }

        if (element.Expanded is not null)
        {
            return element.Expanded;
        }

        return element.Value is null ? null : "value";
    }

    /// <summary>
    /// Describes every element of one panel or dialog, with ids derived in the order the dialog model fixed: the
    /// catalogue's own entry first, then a unique automation-id tail, then the classic Win32 control id, then a
    /// unique label, then the element's position.
    /// </summary>
    public static List<PanelElementDescription> Describe(IReadOnlyList<PanelElementInput> elements)
    {
        ArgumentNullException.ThrowIfNull(elements);

        var tails = elements.Select(element => Tail(element.AutomationId)).ToArray();
        var slugs = elements.Select(element => Slug(element.Name)).ToArray();
        var ranks = PositionRanks(elements);
        var taken = new HashSet<string>(StringComparer.Ordinal);
        var described = new List<PanelElementDescription>(elements.Count);

        for (var index = 0; index < elements.Count; index++)
        {
            var element = elements[index];
            var role = Role(element);
            var id = UniqueOrNull(tails, index, tails[index])
                ?? UniqueOrNull(slugs, index, slugs[index])
                ?? role + "-" + ranks[index].ToString(CultureInfo.InvariantCulture);

            if (!taken.Add(id))
            {
                id = id + "-" + ranks[index].ToString(CultureInfo.InvariantCulture);
            }

            described.Add(new PanelElementDescription(
                Id: id,
                Role: role,
                Name: element.Name,
                AutomationId: string.IsNullOrEmpty(element.AutomationId) ? null : element.AutomationId,
                Index: index,
                State: State(element),
                Action: Action(element) == PanelAction.None ? null : Action(element).ToString().ToLowerInvariant()));
        }

        return described;
    }

    /// <summary>
    /// The <c>panels</c> array of the payload, built from the window facts with no UI Automation read at all.
    /// </summary>
    /// <param name="windows">Every visible top-level window of Dorico's own process, topmost first.</param>
    /// <param name="documentEnabled">Whether the document window was enabled, which is what <c>blocksQueue</c> turns on.</param>
    /// <param name="documentHandle">The handle the probe resolved as the document window, or zero.</param>
    /// <param name="dialogHandle">The handle the probe resolved as the modal window, or zero.</param>
    public static JsonArray Windows(
        IReadOnlyList<WindowFacts> windows,
        bool? documentEnabled,
        nint documentHandle,
        nint dialogHandle)
    {
        ArgumentNullException.ThrowIfNull(windows);

        var panels = new JsonArray();

        foreach (var window in windows)
        {
            JsonArr.AddNode(panels, Describe(window, documentEnabled, documentHandle, dialogHandle));
        }

        return panels;
    }

    /// <summary>One window described as a panel: its identity, whether it blocks, and the fact it was measured with Win32 alone.</summary>
    public static JsonObject Describe(
        WindowFacts window,
        bool? documentEnabled,
        nint documentHandle = default,
        nint dialogHandle = default)
    {
        ArgumentNullException.ThrowIfNull(window);

        var shape = Shape(window.WindowClass);
        var kind = PanelCatalogue.Match(window.Title, window.WindowClass);
        var owner = window.Owner == nint.Zero ? null : "0x" + ((long)window.Owner).ToString("X8", CultureInfo.InvariantCulture);
        var isDocument = documentHandle != nint.Zero && window.Handle == documentHandle;
        var isDialog = dialogHandle != nint.Zero && window.Handle == dialogHandle;

        // Whether this window is one of the ones holding the queue. The rule is the document window's enabled state
        // and this window's own claim to the foreground or to a window as its owner, and every clause of it is a
        // measurement:
        //   the export dialog   (Qt6101QWindow)     - document disabled, owns the document window, foreground -> blocks
        //   the folder picker   (#32770)            - document disabled, owns the dialog that raised it       -> blocks
        //   the crash reporter  (Qt6101QWindowIcon) - document disabled, owns nothing, foreground             -> blocks
        //   the library manager (Qt6101QWindowIcon) - document ENABLED                                       -> does not
        // The class is reported beside this and is never the rule: the crash reporter and the library manager share a
        // class and behave in opposite ways.
        var blocks = !isDocument
            && documentEnabled == false
            && (window.Foreground || window.Owner != nint.Zero);

        var payload = new JsonObject
        {
            ["hwnd"] = "0x" + ((long)window.Handle).ToString("X8", CultureInfo.InvariantCulture),
            ["id"] = PanelId(window.Title, window.WindowClass, kind?.Id ?? KindUnknown),
            ["kind"] = kind is null ? KindUnknown : KindKnown,
            ["shape"] = shape,
            ["class"] = window.WindowClass,
            ["title"] = window.Title,
            ["owner"] = owner,
            ["foreground"] = window.Foreground,
            ["enabled"] = window.Enabled,
            ["document"] = isDocument,
            ["dialog"] = isDialog,
            ["blocksQueue"] = blocks,
        };

        if (kind is not null)
        {
            payload["description"] = kind.Note;
            payload["pressWith"] = "dorico-use ui panels --panel " + kind.Id
                + "  (then: dorico-use ui press --panel " + kind.Id + " --element <element id>)";
        }
        else
        {
            payload["note"] = "Not in this build's panel catalogue. It is reported in full and can be read with "
                + "`ui panels`; acting on an element of it needs --allow-unknown-dialog, because nothing about it "
                + "was measured.";
        }

        return payload;
    }

    /// <summary>The <c>ui panels</c> payload for one panel: its window facts plus the elements a UI Automation read found.</summary>
    /// <param name="window">The window.</param>
    /// <param name="documentEnabled">Whether the document window was enabled when the probe ran.</param>
    /// <param name="elements">The elements the read found, or null when it could not read them.</param>
    /// <param name="truncated">True when the read stopped before the tree ended.</param>
    /// <param name="readMs">What the read cost, in milliseconds.</param>
    /// <param name="error">Why the read failed, when it did.</param>
    public static JsonObject Elements(
        WindowFacts window,
        bool? documentEnabled,
        IReadOnlyList<PanelElementInput>? elements,
        bool truncated,
        long readMs,
        string? error = null,
        nint documentHandle = default,
        nint dialogHandle = default)
    {
        var payload = Describe(window, documentEnabled, documentHandle, dialogHandle);

        var array = new JsonArray();
        var described = Describe(elements ?? []);
        var kept = 0;

        foreach (var element in described)
        {
            if (kept >= MaxElements)
            {
                break;
            }

            JsonArr.AddNode(array, new JsonObject
            {
                ["id"] = element.Id,
                ["role"] = element.Role,
                ["name"] = element.Name,
                ["automationId"] = element.AutomationId,
                ["index"] = element.Index,
                ["state"] = element.State,
                ["action"] = element.Action,
            });

            kept++;
        }

        payload["textSource"] = error is null ? "uia" : "none";
        payload["elements"] = array;
        payload["elementCount"] = described.Count;
        payload["returned"] = array.Count;
        payload["truncated"] = truncated || described.Count > array.Count;
        payload["readMs"] = readMs;

        if (error is not null)
        {
            payload["error"] = error;
        }

        return payload;
    }

    /// <summary>
    /// The stable, meaningful last segment of a Qt automation id, or an empty string. The same rule the dialog
    /// button ids use, so an id an agent saw in a dialog keeps its shape in a panel.
    /// </summary>
    private static string Tail(string? automationId)
    {
        if (string.IsNullOrEmpty(automationId))
        {
            return string.Empty;
        }

        var text = automationId.Trim();
        var dot = text.LastIndexOf('.');

        if (dot >= 0)
        {
            text = text[(dot + 1)..];
        }

        if (text.Length == 0 || text.Length > MaxDerivedIdLength)
        {
            return string.Empty;
        }

        foreach (var character in text)
        {
            if (!char.IsAsciiLetterOrDigit(character) && character is not ('_' or '-' or '.'))
            {
                return string.Empty;
            }
        }

        return text;
    }

    /// <summary>A label turned into something an agent can paste; a label that cannot be one yields an empty slug.</summary>
    private static string Slug(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return string.Empty;
        }

        var text = new StringBuilder(name.Length);
        var lastWasDash = false;

        foreach (var character in name.Trim())
        {
            if (char.IsAsciiLetterOrDigit(character))
            {
                text.Append(char.ToLowerInvariant(character));
                lastWasDash = false;
            }
            else if (!lastWasDash)
            {
                text.Append('-');
                lastWasDash = true;
            }
        }

        var slug = text.ToString().Trim('-');

        return slug.Length is 0 or > MaxDerivedIdLength ? string.Empty : slug;
    }

    private static string? UniqueOrNull(string[] values, int index, string candidate)
    {
        if (string.IsNullOrEmpty(candidate))
        {
            return null;
        }

        return values.Count(value => string.Equals(value, candidate, StringComparison.Ordinal)) == 1
            ? candidate
            : null;
    }

    private static int[] PositionRanks(IReadOnlyList<PanelElementInput> elements)
    {
        var order = Enumerable.Range(0, elements.Count)
            .OrderBy(index => elements[index].Top / RowTolerancePixels)
            .ThenBy(index => elements[index].Left)
            .ThenBy(index => elements[index].Name, StringComparer.Ordinal)
            .ToArray();

        var ranks = new int[elements.Count];

        for (var rank = 0; rank < order.Length; rank++)
        {
            ranks[order[rank]] = rank + 1;
        }

        return ranks;
    }
}
