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
internal sealed record PanelRead(
    IReadOnlyList<PanelElementInput> Elements,
    IReadOnlyList<nint> Handles,
    int Nodes,
    bool Truncated);

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
    public static PanelRead Read(nint handle)
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
            var visited = 0;
            var truncated = false;

            Visit(root, walker, 0, elements, handles, ref visited, ref truncated);

            return new PanelRead(elements, handles, visited, truncated);
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
        ref int visited,
        ref bool truncated)
    {
        visited++;

        if (visited > MaxNodes || depth > MaxDepth)
        {
            truncated = true;

            return false;
        }

        var controlType = UiaCalls.ControlType(element);
        var kept = false;

        if (IsTarget(controlType))
        {
            var state = UiaCalls.State(element, controlType);
            var bounds = UiaCalls.Bounds(element);

            elements.Add(new PanelElementInput(
                ControlType: controlType,
                Name: UiaCalls.CurrentName(element),
                AutomationId: UiaCalls.AutomationId(element),
                Value: state.Value,
                ToggleState: state.Toggle,
                Selected: state.Selected,
                Expanded: state.Expanded,
                Invokable: state.Invokable,
                Left: bounds.Left,
                Top: bounds.Top));

            handles.Add(element);
            kept = true;
        }

        var child = UiaCalls.FirstChild(walker, element);

        while (child != nint.Zero)
        {
            var next = UiaCalls.NextSibling(walker, child);

            if (!Visit(child, walker, depth + 1, elements, handles, ref visited, ref truncated))
            {
                UiaCom.Release(child);
            }

            child = next;
        }

        return kept;
    }

    /// <summary>
    /// Whether an element is one an action could target. A text label, a group and a pane are not: they are what the
    /// panel is made of rather than what an agent operates.
    /// </summary>
    private static bool IsTarget(int controlType) => controlType is
        UiaCalls.ControlTypeButton or     // 50000
        UiaCalls.ControlTypeCheckBox or   // 50002
        UiaCalls.ControlTypeComboBox or   // 50003
        UiaCalls.ControlTypeEdit or       // 50004
        UiaCalls.ControlTypeMenuItem or   // 50011
        UiaCalls.ControlTypeRadioButton or // 50013
        UiaCalls.ControlTypeTabItem or    // 50019
        UiaCalls.ControlTypeTreeItem or   // 50024
        UiaCalls.ControlTypeListItem or   // 50029
        UiaCalls.ControlTypeSplitButton;  // 50031

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
