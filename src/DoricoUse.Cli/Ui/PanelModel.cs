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
/// <param name="ValueReadOnly">
/// Whether the value pattern reports itself read-only. Null when there is no value pattern. A read-only field is
/// what a write has to be refused on, so it is carried rather than assumed from the control type.
/// </param>
/// <param name="Container">
/// The identity of the nearest ancestor that holds rows — a list, a tree, a data grid — or null when the element
/// is not inside one. It is what makes a viewport distinguishable from a whole list: measured, a Dorico list view
/// realises only the rows around its viewport and exposes no scroll pattern, so the reported rows are a window
/// onto the list, and a caller has to be able to see which window an id came from.
/// </param>
/// <param name="ContainerScrollable">
/// Whether that ancestor exposes the scroll pattern. Measured false on every list view this build has read, which
/// is why the read cannot page to the rows the viewport hides.
/// </param>
/// <param name="Enabled">
/// The element's own UI Automation <c>IsEnabled</c>. It is the cheapest answer to "would an action here do
/// anything", and it was missing: measured 2026-10-13 on the 工程信息 dialog, the value-pattern write into 标题 read
/// back verbatim while 应用 stayed disabled and the write was reverted, and nothing in the element list said so —
/// any accessibility reader shows `button "应用" [disabled]` for that window. False means an action is
/// refused with <c>ElementDisabled</c> rather than reported as an invoke that did nothing.
/// </param>
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
    int Top = 0,
    bool? ValueReadOnly = null,
    string? Container = null,
    bool ContainerScrollable = false,
    bool Enabled = true);

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
/// <param name="Value">
/// The value pattern's current value, or null when this read did not get one. This is the field a write is verified
/// with: it used to be dropped, so a payload reported <c>state: "value"</c> and the caller could see that a field
/// holds something and not what — measured on the 重命名演奏者 dialog's <c>shortNameLineEdit</c>, which read
/// <c>Picc.</c> and, after a write, <c>Probe.</c>, while the payload said <c>"value"</c> both times. A read asks a
/// value pattern for the control types that carry one (<c>Edit</c>, <c>ComboBox</c>, and anything that is not a
/// button, a check box or a row), so null on a button or a row means "not asked", not "empty".
/// </param>
/// <param name="ValueFrom">
/// Which pattern the value came from, or null when there is none. Always <c>valuePattern</c> today: the legacy
/// accessibility pattern repeats the same text on every measured field, but it is not what this build reads, so it
/// is not claimed as a source.
/// </param>
/// <param name="ValueReadOnly">Whether the value pattern is read-only, or null when there is none.</param>
/// <param name="Container">The id or label of the row container this element belongs to, or null.</param>
/// <param name="Enabled">The element's own UI Automation <c>IsEnabled</c>; false is what <c>ui press</c> refuses.</param>
/// <param name="Invokable">
/// True when the element exposes an invoke pattern. Reported for every element, not only for a dialog's buttons
/// (which have carried it since the dialog layer was written), because "there is a pressable pattern here" and
/// "would a press do anything" are two different facts and a caller needs both.
/// </param>
internal sealed record PanelElementDescription(
    string Id,
    string Role,
    string Name,
    string? AutomationId,
    int Index,
    string? State,
    string? Action,
    string? Value = null,
    string? ValueFrom = null,
    bool? ValueReadOnly = null,
    string? Container = null,
    bool Enabled = true,
    bool Invokable = false);

/// <summary>One node of a panel's accessibility tree, kept or not, for <c>ui panels --raw</c>.</summary>
/// <remarks>
/// It lives in <c>PanelModel.cs</c> rather than beside the reader that fills it because that file is the pure half
/// the check projects compile, and <see cref="PanelModel.NodePayload"/> is a rule about what a payload claims.
/// </remarks>
/// <param name="ControlType">The UI Automation control type id.</param>
/// <param name="Name">The name the node carries, verbatim. Empty for most of a Qt/QML tree.</param>
/// <param name="AutomationId">The node's own automation id, verbatim.</param>
/// <param name="Depth">How deep the node sits below the window.</param>
/// <param name="Kept">True when this node is one of the elements the read reports and an action can target.</param>
internal sealed record PanelNodeInput(int ControlType, string Name, string? AutomationId, int Depth, bool Kept);

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

    /// <summary>
    /// The id of Dorico's document window.
    /// </summary>
    /// <remarks>
    /// A constant rather than a derived id, and that is a correction: the id used to be the same
    /// <c>unknown.&lt;hash&gt;</c> every other unnamed window gets, with the <b>caption</b> in the hash — so the
    /// document window's id changed with the document's name and with the unsaved marker Dorico appends to the
    /// title, and no invocation could name the window the previous one had read. Measured: the same window answered
    /// to <c>unknown.3d5b24e60c7f</c> while the title read <c>… 内</c> and to <c>unknown.a91b50b9a382</c> once it
    /// read <c>… 内*</c>.
    /// </remarks>
    public const string DocumentWindowId = "dorico.document-window";

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
    /// The id of a panel: <see cref="DocumentWindowId"/> for Dorico's document window, the panel catalogue's own
    /// id when it recognises this window, else a deterministic hash of the caption and the class, so the same
    /// panel keeps the same id in any later invocation.
    /// </summary>
    /// <param name="title">The window caption, verbatim.</param>
    /// <param name="windowClass">The Win32 window class, verbatim.</param>
    /// <param name="kind">The panel catalogue's id, or <see cref="KindUnknown"/>.</param>
    /// <param name="document">
    /// True for the window the probe resolved as Dorico's document window. Measured, dorico's own Setup work
    /// (players, groups, layouts) is inside that window, so it is the one window an agent has to be able to name.
    /// </param>
    public static string PanelId(string title, string windowClass, string kind, bool document)
    {
        if (document)
        {
            return DocumentWindowId;
        }

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

        return ControlTypes.Role(element.ControlType);
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
                ?? ClassicTail(tails, index, role)
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
                Action: Action(element) == PanelAction.None ? null : Action(element).ToString().ToLowerInvariant(),
                // The value is reported only when a value pattern answered: the key's presence is the fact that the
                // element has one, and an empty field therefore reads as value: "" rather than as "no value".
                Value: element.Value,
                ValueFrom: element.Value is null ? null : ValueFromValuePattern,
                ValueReadOnly: element.Value is null ? null : element.ValueReadOnly,
                Container: element.Container,
                Enabled: element.Enabled,
                Invokable: element.Invokable));
        }

        return described;
    }

    /// <summary>The pattern name a reported value came from. See <see cref="PanelElementDescription.ValueFrom"/>.</summary>
    public const string ValueFromValuePattern = "valuePattern";

    /// <summary>
    /// The code <c>ui press</c> refuses a disabled target with, and a code of its own rather than a generic one.
    /// </summary>
    /// <remarks>
    /// Measured 2026-10-13: pressing an element whose <c>IsEnabled</c> is false reports <c>ok: true</c> with an
    /// invoke pattern that does nothing — the tool said it worked and the window did not change. A disabled target is
    /// a refusal the caller can act on (wait, select the row the dialog wants, or stop asking), so it is reported as
    /// itself and never as a press that happened. Exit 1, like every other refusal: the caller named something it may
    /// not work.
    /// </remarks>
    public const string DisabledCode = "ElementDisabled";

    /// <summary>
    /// The refusal for an element that is up but disabled: refused before any pattern is called, so nothing is
    /// half-done.
    /// </summary>
    /// <param name="where">The panel or dialog id the element belongs to.</param>
    /// <param name="id">The element's id.</param>
    /// <param name="name">The element's label, verbatim.</param>
    /// <param name="role">The element's role.</param>
    public static CliException RefuseDisabled(string where, string id, string name, string role)
    {
        ArgumentNullException.ThrowIfNull(where);
        ArgumentNullException.ThrowIfNull(id);

        return new CliException(
            DisabledCode,
            $"Element '{id}' ('{name}') of '{where}' is disabled (UI Automation IsEnabled false), so it was not "
            + "acted on: a pattern call on a disabled element reports success and changes nothing, which is the "
            + "false `ok: true` this refusal replaces. Read `enabled` on the element list to see which targets are "
            + "live, and make the window enable this one first — measured 2026-10-13 on the 工程信息 dialog, 应用 "
            + "stayed disabled until the row the dialog edits was selected.",
            DialogModel.RefusalExitCode)
        {
            Details = new JsonObject
            {
                ["window"] = where,
                ["element"] = id,
                ["name"] = name,
                ["role"] = role,
                ["enabled"] = false,
            },
        };
    }

    /// <summary>
    /// The one button of a window whose label is this, or null when there is none or more than one.
    /// </summary>
    /// <param name="elements">The elements of a fresh read, in read order.</param>
    /// <param name="label">The label to match, verbatim as the window shows it.</param>
    /// <remarks>
    /// A label is a value and an id is an identity, and this exists for the one measured case where the identity is
    /// not stable: the 布局选项 dialog's 关闭 and 应用 buttons have no unique automation id in Qt, so their element
    /// ids are derived from their position and move when the dialog's page changes (measured 2026-10-13: 关闭 was
    /// <c>button-98</c> in one read). A caller that means "close this dialog, whatever its close button is called
    /// this time" therefore has to re-read and match on the label — and the two rules that keep that from becoming a
    /// guess are here: the match is exact, and an ambiguous label matches nothing at all rather than the first one,
    /// because pressing the wrong one of two same-labelled buttons is exactly the accident this is for.
    /// </remarks>
    public static PanelElementDescription? ButtonByLabel(IReadOnlyList<PanelElementDescription> elements, string label)
    {
        ArgumentNullException.ThrowIfNull(elements);
        ArgumentNullException.ThrowIfNull(label);

        PanelElementDescription? found = null;

        foreach (var element in elements)
        {
            if (!string.Equals(element.Role, RoleButton, StringComparison.Ordinal)
                || !string.Equals(element.Name, label, StringComparison.Ordinal))
            {
                continue;
            }

            if (found is not null)
            {
                return null;
            }

            found = element;
        }

        return found;
    }

    /// <summary>The role a pressable button reports, which is what <see cref="ButtonByLabel"/> matches on.</summary>
    public const string RoleButton = "button";

    /// <summary>
    /// Whether <c>--commit-input</c> applies to an action. Only a value write has text to commit.
    /// </summary>
    /// <remarks>
    /// The rule lives here rather than beside the click it guards because it is a rule and not a Windows call: the
    /// check project asserts it, and the click itself (<see cref="InputSimulator"/>) is the only thing that has to
    /// be Windows-shaped.
    /// </remarks>
    public static bool CommitInputApplicable(PanelAction action) => action == PanelAction.Value;

    /// <summary>The refusal for <c>--commit-input</c> aimed at something that is not a text field.</summary>
    /// <param name="where">The panel or dialog id the element belongs to.</param>
    /// <param name="element">The element the caller named.</param>
    public static CliException RefuseCommitInput(string where, PanelElementDescription element)
    {
        ArgumentNullException.ThrowIfNull(element);

        return new CliException(
            "InvalidArgument",
            $"--commit-input sends real input into a text field, and '{element.Id}' ('{element.Name}') is not one: its "
            + $"action is {ActionText(element.Action)}, not value. Real input for anything else has not been measured "
            + "here, so this build refuses rather than clicking at a coordinate on a guess.",
            DialogModel.RefusalExitCode)
        {
            Details = new JsonObject
            {
                ["window"] = where,
                ["element"] = element.Id,
                ["action"] = element.Action,
            },
        };
    }

    /// <summary>The reported action of an element, or <c>none</c> when it exposes no pattern to act through.</summary>
    private static string ActionText(string? action) => action ?? "none";

    /// <summary>
    /// The row containers an element list realised, with how many rows each one, and whether it could be scrolled.
    /// </summary>
    /// <param name="elements">The elements a read kept, in read order.</param>
    /// <param name="rows">How many of them are rows of some container.</param>
    /// <remarks>
    /// <para>
    /// Measured, and this is the reason the field exists: Dorico's list views realise only the rows around their
    /// viewport. The Setup players list implements 30 of that project's 49 rows, the layouts list 28 of its 45, and
    /// the 繁衍声部格式 dialog's two lists 15 and 14 of 44 — the counts do not change when the window is minimized
    /// or restored. Nothing about the list or any of its ancestors exposes the scroll pattern (measured on the full
    /// ancestor chain of both Setup lists and of the dialog's list, and on a row of each, which exposes no
    /// scroll-item pattern either), and the only two scrollbars in the document window belong to the score canvas
    /// viewport: driving one of those with its range value (1212 -> 0 -> 1212 of 1574) left the player and layout
    /// rows at 30 and 28. So there is no paging this build can do, and what it owes the caller instead is the plain
    /// fact: these are the rows that are real, and the rest cannot be read or chosen.
    /// </para>
    /// <para>
    /// A container is reported by its automation-id tail when it has one, because that is what a caller can match
    /// against a <c>ui panels</c> payload it already has; otherwise by its label; otherwise by its role.
    /// </para>
    /// </remarks>
    public static JsonArray Lists(IReadOnlyList<PanelElementInput> elements, out int rows)
    {
        ArgumentNullException.ThrowIfNull(elements);

        rows = 0;
        var order = new List<string>();
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        var scrollable = new Dictionary<string, bool>(StringComparer.Ordinal);

        foreach (var element in elements)
        {
            if (!ControlTypes.IsRow(element.ControlType) || string.IsNullOrEmpty(element.Container))
            {
                continue;
            }

            rows++;

            if (!counts.TryGetValue(element.Container, out var count))
            {
                order.Add(element.Container);
                scrollable[element.Container] = element.ContainerScrollable;
            }

            counts[element.Container] = count + 1;
        }

        var lists = new JsonArray();

        foreach (var container in order)
        {
            JsonArr.AddNode(lists, new JsonObject
            {
                ["container"] = container,
                ["rows"] = counts[container],
                ["scrollPattern"] = scrollable[container],
            });
        }

        return lists;
    }

    /// <summary>
    /// What the payload says when a read realised rows: the rows it saw are what the viewport holds, and the list
    /// itself is longer. Reported only when there are rows, so a window with no list does not carry a warning about
    /// one.
    /// </summary>
    public const string RowCeiling =
        "The rows with a `container` are the rows that list view has realised, which is the viewport and not the "
        + "list. Measured: Dorico's Setup players and layouts lists realise 30 of 49 and 28 of 45 rows of a "
        + "44-player project, and the 繁衍声部格式 dialog's two lists 15 and 14 of 44, with the same counts while "
        + "minimized and restored. Neither list nor any ancestor exposes a scroll pattern and no row exposes a "
        + "scroll-item pattern, so this build cannot page to the rest: a row outside the viewport can be neither "
        + "read nor chosen, and `lists[].rows` is a count of what was seen, never the length of the list.";

    /// <summary>
    /// The name a row's container is reported under: the container's automation-id tail when it has one, the label
    /// the window shows when it has not, and its role when it has neither. Measured, a Dorico list view's tail is
    /// its own meaningful name (<c>playersListView</c>, <c>sourceLayoutListView</c>), which is what a caller matches
    /// against a payload it already read.
    /// </summary>
    /// <remarks>
    /// The label is reported <b>verbatim</b> rather than slugged: a container name is a value in a payload, not an
    /// id an agent pastes back, so a Chinese list label must not be turned into an empty string — which is what the
    /// id rules would do with it, and what a check here caught.
    /// </remarks>
    public static string ContainerName(string? automationId, string name, int controlType)
    {
        var tail = Tail(automationId);

        if (tail.Length > 0)
        {
            return tail;
        }

        return string.IsNullOrWhiteSpace(name) ? ControlTypes.Role(controlType) : name.Trim();
    }

    /// <summary>
    /// The state of one element in a fresh read, for the evidence a press owes: the same fields the element carries
    /// in the payload. Null when the element is gone, which is a real answer too — it is what a dialog that was
    /// dismissed and a tab that was closed look like.
    /// </summary>
    /// <param name="described">The ids and states of a fresh read, in the order the read found them.</param>
    /// <param name="inputs">The same read's plain data, which carries the toggle and selection states a payload
    /// states only as one word.</param>
    /// <param name="id">The id the caller acted on.</param>
    public static JsonObject? StateAfter(
        IReadOnlyList<PanelElementDescription> described,
        IReadOnlyList<PanelElementInput> inputs,
        string id)
    {
        ArgumentNullException.ThrowIfNull(described);
        ArgumentNullException.ThrowIfNull(inputs);
        ArgumentNullException.ThrowIfNull(id);

        for (var index = 0; index < described.Count; index++)
        {
            if (!string.Equals(described[index].Id, id, StringComparison.Ordinal))
            {
                continue;
            }

            return new JsonObject
            {
                ["state"] = described[index].State,
                ["value"] = described[index].Value,
                ["valueReadOnly"] = described[index].ValueReadOnly,
                ["toggle"] = index < inputs.Count ? inputs[index].ToggleState : null,
                ["selected"] = index < inputs.Count ? inputs[index].Selected : null,
                // `enabled` is part of the evidence because it is what a caller acts on next: a press that enables
                // 应用 is exactly the transition the 工程信息 dialog needs, and a press that leaves everything
                // disabled has to be visible as such rather than inferred from a missing change.
                ["enabled"] = described[index].Enabled,
                ["invokable"] = described[index].Invokable,
            };
        }

        return null;
    }

    /// <summary>
    /// The same evidence for a dialog's control, read back out of the re-read payload: a dialog's fresh read is
    /// published as JSON (<c>dialog.controls</c>), not as an element list, so this finds the control the press acted
    /// on there.
    /// </summary>
    /// <param name="dialog">The <c>dialog</c> object of the re-read, or null when the dialog is gone.</param>
    /// <param name="id">The control id the caller acted on.</param>
    public static JsonObject? ControlAfter(JsonNode? dialog, string id)
    {
        ArgumentNullException.ThrowIfNull(id);

        if (dialog is not JsonObject window || window["controls"] is not JsonArray controls)
        {
            return null;
        }

        foreach (var node in controls)
        {
            if (node is not JsonObject control || (string?)control["id"] != id)
            {
                continue;
            }

            return new JsonObject
            {
                ["state"] = control["state"]?.DeepClone(),
                ["value"] = control["value"]?.DeepClone(),
                ["valueReadOnly"] = control["valueReadOnly"]?.DeepClone(),
                ["role"] = control["role"]?.DeepClone(),
                ["name"] = control["name"]?.DeepClone(),
                ["container"] = control["container"]?.DeepClone(),
                // The same two facts a panel element's evidence carries: what the control is now, and whether it
                // would be acted on at all.
                ["enabled"] = control["enabled"]?.DeepClone(),
                ["invokable"] = control["invokable"]?.DeepClone(),
            };
        }

        return null;
    }

    /// <summary>
    /// The whole tree a read walked, for <c>ui panels --raw</c>: every node, kept or not, in read order.
    /// </summary>
    /// <param name="nodes">The nodes the walk visited.</param>
    /// <param name="returned">How many were written, so a caller can see whether a cap applied.</param>
    /// <remarks>
    /// This is the answer to a mistake this build has made twice: a read that returned a short element list was read
    /// as "there is no such control in that window". Measured, the library manager's 27 nodes are 13 unnamed
    /// <c>CollectionListDelegate</c> groups, 2 labels, 4 panes and 2 scroll bars around the 5 elements that reach a
    /// payload — and the window draws more than the tree holds. <c>kept</c> on each node says which side of that
    /// line it fell on, so the gap is visible instead of implied. Values are deliberately not read here: a raw walk
    /// exists to show the shape, and asking every node for a value pattern would turn a cheap read into a slow one.
    /// </remarks>
    public static JsonArray NodePayload(IReadOnlyList<PanelNodeInput> nodes, out int returned)
    {
        ArgumentNullException.ThrowIfNull(nodes);

        var array = new JsonArray();

        foreach (var node in nodes)
        {
            if (array.Count >= MaxNodes)
            {
                break;
            }

            JsonArr.AddNode(array, new JsonObject
            {
                ["index"] = array.Count,
                ["depth"] = node.Depth,
                ["role"] = ControlTypes.Role(node.ControlType),
                ["controlType"] = ControlTypes.Name(node.ControlType),
                ["name"] = node.Name,
                ["automationId"] = node.AutomationId,
                ["kept"] = node.Kept,
            });
        }

        returned = array.Count;

        return array;
    }

    /// <summary>How many nodes one raw walk may report, so that a payload stays readable.</summary>
    public const int MaxNodes = 4000;

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
        var owner = window.Owner == nint.Zero ? null : DialogModel.HandleText(window.Owner);
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
            ["hwnd"] = DialogModel.HandleText(window.Handle),
            ["id"] = PanelId(window.Title, window.WindowClass, kind?.Id ?? KindUnknown, isDocument),
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

        if (isDocument)
        {
            // Dorico's Setup work - the player, flow and layout lists - lives inside this window, so it is the one
            // "panel" an agent has to be able to name. It is not in the panel catalogue because its caption is the
            // document's name, which changes; the id is a constant instead.
            payload["description"] = "Dorico's own document window. Read it with `ui panels --panel "
                + DocumentWindowId + "` to reach the Setup lists inside it (players, flows, layouts) and the panels "
                + "of the current window mode, with the same element ids, states and actions every other panel "
                + "reports. Measured: its UIA tree carries the player/flow/layout list views and their rows.";
            payload["pressWith"] = "dorico-use ui panels --panel " + DocumentWindowId
                + "  (then: dorico-use ui press --panel " + DocumentWindowId + " --element <element id>)";
        }
        else if (kind is not null)
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

    /// <summary>
    /// The <c>elements</c> array of the payload: one object per element an action could target, in read order,
    /// capped at <see cref="MaxElements"/>.
    /// </summary>
    /// <param name="elements">The elements <see cref="Describe"/> produced.</param>
    /// <param name="returned">How many of them were written, so a caller can say whether the cap was hit.</param>
    /// <remarks>
    /// <para>
    /// One function for both windows an element can live in, because the id an agent pastes back must mean the
    /// same thing in a panel and in a dialog. A dialog's edit fields and list rows are reported through this, which
    /// is what makes <c>ui press --dialog … --element … --value …</c> possible at all.
    /// </para>
    /// <para>
    /// Each element carries its <b>current value</b> when the read got one, and that is the field this round exists
    /// for: a write used to be reported by the string that was sent and by nothing else, so no caller could verify
    /// one. The key is present only when the value pattern answered, which is what makes <c>value: ""</c> a field
    /// that is really empty and keeps a button or a check box from looking like one.
    /// </para>
    /// </remarks>
    public static JsonArray ElementPayload(IReadOnlyList<PanelElementDescription> elements, out int returned)
    {
        ArgumentNullException.ThrowIfNull(elements);

        var array = new JsonArray();

        foreach (var element in elements)
        {
            if (array.Count >= MaxElements)
            {
                break;
            }

            var item = new JsonObject
            {
                ["id"] = element.Id,
                ["role"] = element.Role,
                ["name"] = element.Name,
                ["automationId"] = element.AutomationId,
                ["index"] = element.Index,
                ["state"] = element.State,
                ["action"] = element.Action,
                // The two facts that answer "would acting here do anything". `enabled` is UI Automation's own
                // IsEnabled and `invokable` is the presence of an invoke pattern; both are reported for every
                // element, because measured 2026-10-13 a value write that the field accepted was reverted by a
                // dialog whose 应用 stayed disabled, and the element list could not say so.
                ["enabled"] = element.Enabled,
                ["invokable"] = element.Invokable,
            };

            if (element.Value is not null)
            {
                item["value"] = element.Value;
                item["valueFrom"] = element.ValueFrom;
                item["valueReadOnly"] = element.ValueReadOnly;
            }

            if (element.Container is not null)
            {
                item["container"] = element.Container;
            }

            JsonArr.AddNode(array, item);
        }

        returned = array.Count;

        return array;
    }

    /// <summary>The <c>ui panels</c> payload for one panel: its window facts plus the elements a UI Automation read found.</summary>
    /// <param name="window">The window.</param>
    /// <param name="documentEnabled">Whether the document window was enabled when the probe ran.</param>
    /// <param name="elements">The elements the read found, or null when it could not read them.</param>
    /// <param name="truncated">True when the read stopped before the tree ended.</param>
    /// <param name="readMs">What the read cost, in milliseconds.</param>
    /// <param name="error">Why the read failed, when it did.</param>
    /// <param name="nodesVisited">
    /// How many nodes the walk visited, kept or not, or null when nothing was walked. Reported so that a short
    /// <c>elements</c> array cannot be mistaken for a small window: the two numbers together are the difference
    /// between "this window holds five controls" and "UI Automation exposes five controls of this window".
    /// </param>
    public static JsonObject Elements(
        WindowFacts window,
        bool? documentEnabled,
        IReadOnlyList<PanelElementInput>? elements,
        bool truncated,
        long readMs,
        string? error = null,
        nint documentHandle = default,
        nint dialogHandle = default,
        int? nodesVisited = null)
    {
        var payload = Describe(window, documentEnabled, documentHandle, dialogHandle);

        var described = Describe(elements ?? []);
        var array = ElementPayload(described, out var returned);

        payload["textSource"] = error is null ? "uia" : "none";
        payload["elements"] = array;
        payload["elementCount"] = described.Count;
        payload["returned"] = returned;
        payload["truncated"] = truncated || described.Count > returned;
        payload["readMs"] = readMs;

        if (nodesVisited is not null)
        {
            payload["visited"] = nodesVisited.Value;

            if (nodesVisited.Value > described.Count)
            {
                // The honest version of a short element list. Measured, and this is why the field exists: the
                // library manager draws an 选项 column, a 集合 column, a 此工程 list, a filter, a search field and a
                // .dorico drop target, and UI Automation exposes five elements of it - so "elementCount: 5" would
                // otherwise read as "there is nothing in that dialog".
                payload["walkNote"] = $"The walk visited {nodesVisited.Value} elements of this window and kept "
                    + $"{described.Count}: a button, a check box, a field, a combo box, a tab and a list row are "
                    + "elements an action can target, while a pane, a group, a label and a scroll bar are what a "
                    + "window is made of. A short list here is not proof that the window is small — measured, the "
                    + "library manager draws rows, a filter, a search field and a drop target that have no UI "
                    + "Automation node at all, so this build can list only what the accessibility tree carries.";
            }
        }

        var lists = Lists(elements ?? [], out var rows);

        if (rows > 0)
        {
            // A read that realised rows owes the caller the fact that those rows are a viewport. Without it an
            // element list that is short of the real list looks like the whole list, which is exactly how "select
            // all" was mistaken for a proof that every layout was chosen.
            payload["lists"] = lists;
            payload["rows"] = rows;
            payload["viewportLimited"] = !AllScrollable(elements ?? []);
            payload["rowCeiling"] = RowCeiling;
        }

        if (error is not null)
        {
            payload["error"] = error;
        }

        return payload;
    }

    /// <summary>Whether every row container a read saw can be scrolled, which is what makes paging possible at all.</summary>
    private static bool AllScrollable(IReadOnlyList<PanelElementInput> elements)
    {
        var seen = false;

        foreach (var element in elements)
        {
            if (!ControlTypes.IsRow(element.ControlType) || string.IsNullOrEmpty(element.Container))
            {
                continue;
            }

            seen = true;

            if (!element.ContainerScrollable)
            {
                return false;
            }
        }

        return seen;
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

    /// <summary>
    /// The classic Win32 control id of an element, qualified by its role, or null.
    /// </summary>
    /// <remarks>
    /// A native Windows dialog numbers its controls, and those numbers do not change with the UI language — measured
    /// on the Open picker, the file-name box is <c>1148</c> and the file filter <c>1136</c>. They are not unique
    /// inside one window there (the <c>ComboBoxEx32</c> pane, the <c>ComboBox</c> and the <c>Edit</c> inside it all
    /// answer <c>1148</c>), so an unqualified number would fall through to the position rule and an agent would get
    /// an id it cannot recognise. Qualifying it with the role — <c>edit-1148</c>, <c>comboBox-1136</c> — keeps the
    /// number, keeps it stable across invocations, and says what it is.
    /// </remarks>
    private static string? ClassicTail(string[] tails, int index, string role)
    {
        var tail = tails[index];

        if (tail.Length == 0 || !tail.All(char.IsAsciiDigit))
        {
            return null;
        }

        return role + "-" + tail;
    }

    private static string? UniqueOrNull(string[] values, int index, string candidate)    {
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
