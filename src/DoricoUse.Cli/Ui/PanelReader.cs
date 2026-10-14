using System.Text.Json.Nodes;

namespace DoricoUse.Cli.Ui;

/// <summary>What one read of a panel found: the elements, the element pointers behind them, and what it cost.</summary>
/// <param name="Elements">The plain data <see cref="PanelModel"/> turns into ids, roles and actions.</param>
/// <param name="Handles">
/// One UI Automation element pointer per entry of <see cref="Elements"/>, in the same order, each an owned
/// reference the caller must release once it is done with it.
/// </param>
/// <param name="Nodes">How many elements the walk visited, kept or not.</param>
/// <param name="Truncated">True when a limit stopped the walk before the tree ended.</param>
/// <param name="Every">
/// Every node the walk visited, in read order, when the caller asked for them — or null, which is the cheap path
/// every read but <c>--raw</c> takes.
/// </param>
internal sealed record PanelRead(
    IReadOnlyList<PanelElementInput> Elements,
    IReadOnlyList<nint> Handles,
    int Nodes,
    bool Truncated,
    IReadOnlyList<PanelNodeInput>? Every = null);

/// <summary>
/// Reads a panel's elements through UI Automation.
/// </summary>
/// <remarks>
/// <para>
/// This is the part the modal layer did not need. A dialog is made of buttons, and <see cref="DialogReader"/> looks
/// for exactly that; a panel is made of check boxes, tabs, list items, tree items and text fields, and acting on one
/// means knowing which pattern it offers. So this walk keeps <b>every element an action could target</b> — buttons,
/// check boxes, tabs, list items, tree items, edits and combo boxes — together with the state its own patterns
/// answer, and it keeps nothing else: a read that returned every group, pane and label of a library tree would bury
/// the four elements that matter.
/// </para>
/// <para>
/// A kept element's children are still walked: a list item can have a label child, and a tree item can have a
/// subtree under it.
/// </para>
/// </remarks>
internal static class PanelReader
{
    /// <summary>How many elements one read may visit before it stops and says it stopped.</summary>
    private const int MaxNodes = 4000;

    /// <summary>How deep one read may go.</summary>
    private const int MaxDepth = 24;

    /// <summary>Reads the window behind one handle.</summary>
    /// <param name="handle">The window's handle, from the presence probe.</param>
    /// <param name="every">
    /// True to keep every node the walk visited, for <c>ui panels --raw</c>: the difference between "this window
    /// holds five controls" and "UI Automation exposes five controls of this window" is exactly the nodes a read
    /// drops, and a reader who concluded "there is no such control" from a short element list is the mistake this
    /// makes visible.
    /// </param>
    public static PanelRead Read(nint handle, bool every = false)
    {
        var automation = UiaCom.CreateAutomation();
        nint root = nint.Zero;
        nint walker = nint.Zero;

        try
        {
            root = UiaCalls.ElementFromHandle(automation, handle);
            walker = UiaCalls.RawViewWalker(automation);

            var elements = new List<PanelElementInput>();
            var handles = new List<nint>();
            var nodes = every ? new List<PanelNodeInput>() : null;
            var visited = 0;
            var truncated = false;

            Visit(root, walker, 0, elements, handles, nodes, ref visited, ref truncated);

            return new PanelRead(elements, handles, visited, truncated, nodes);
        }
        catch
        {
            throw;
        }
        finally
        {
            UiaCom.Release(walker);
            UiaCom.Release(root);
            UiaCom.Release(automation);
        }
    }

    /// <summary>
    /// Visits one element and everything below it, keeping the ones an action could target. Returns true when the
    /// element was kept, so its caller does not release it.
    /// </summary>
    private static bool Visit(
        nint element,
        nint walker,
        int depth,
        List<PanelElementInput> elements,
        List<nint> handles,
        List<PanelNodeInput>? nodes,
        ref int visited,
        ref bool truncated,
        string? container = null,
        bool containerScrollable = false)
    {
        visited++;

        if (visited > MaxNodes || depth > MaxDepth)
        {
            truncated = true;

            return false;
        }

        var controlType = UiaCalls.ControlType(element);
        var name = UiaCalls.CurrentName(element);
        var automationId = UiaCalls.AutomationId(element);
        var kept = false;

        // A row container names itself, so every row underneath can say which list it came from - and whether that
        // list could be scrolled, which is the difference between "the rows that are there" and "the rows that can
        // be reached".
        var here = container;
        var scrollable = containerScrollable;

        if (ControlTypes.IsRowContainer(controlType))
        {
            here = PanelModel.ContainerName(automationId, name, controlType);
            scrollable = UiaCalls.HasScroll(element);
        }

        if (ControlTypes.IsActionable(controlType))
        {
            var state = UiaCalls.State(element, controlType);
            var bounds = UiaCalls.Bounds(element);
            var row = ControlTypes.IsRow(controlType);

            elements.Add(new PanelElementInput(
                ControlType: controlType,
                Name: name,
                AutomationId: automationId,
                Value: state.Value,
                ToggleState: state.Toggle,
                Selected: state.Selected,
                Expanded: state.Expanded,
                Invokable: state.Invokable,
                Left: bounds.Left,
                Top: bounds.Top,
                ValueReadOnly: state.ValueReadOnly,
                Container: row ? here : null,
                ContainerScrollable: row && scrollable,
                // One more COM getter per kept element, and the one that says whether acting here would do anything:
                // measured 2026-10-13, a field took a written value while the dialog's 应用 stayed disabled, and
                // nothing else in the element list could tell a caller that.
                Enabled: UiaCalls.IsEnabled(element)));

            handles.Add(element);
            kept = true;
        }

        nodes?.Add(new PanelNodeInput(controlType, name, automationId, depth, kept));

        var child = UiaCalls.FirstChild(walker, element);

        while (child != nint.Zero)
        {
            var next = UiaCalls.NextSibling(walker, child);

            if (!Visit(child, walker, depth + 1, elements, handles, nodes, ref visited, ref truncated, here, scrollable))
            {
                UiaCom.Release(child);
            }

            child = next;
        }

        return kept;
    }

    /// <summary>
    /// Releases every element a read kept, when the read is not going to hand them over.
    /// </summary>
    public static void ReleaseAll(IReadOnlyList<nint> handles)
    {
        ArgumentNullException.ThrowIfNull(handles);

        foreach (var handle in handles)
        {
            UiaCom.Release(handle);
        }
    }

    /// <summary>The <c>panels</c> entry for a window the read could not touch: the facts Win32 gave, and why.</summary>
    public static JsonObject Failure(WindowFacts window, bool? documentEnabled, long readMs, string error)
        => PanelModel.Elements(window, documentEnabled, null, truncated: false, readMs, error);
}
