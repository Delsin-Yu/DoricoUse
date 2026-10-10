using System.Text.Json.Nodes;
using DoricoUse.Cli.Json;

namespace DoricoUse.Cli.Ui;

/// <summary>What one read of a dialog found: the model's input, the elements behind its buttons, and what it cost.</summary>
/// <param name="Input">The plain data the pure model turns into ids, policy and JSON.</param>
/// <param name="ButtonElements">
/// One UI Automation element pointer per entry of <see cref="DialogInput.Buttons"/>, in the same order, each an
/// owned reference the caller must release once it is done with them.
/// </param>
/// <param name="Raw">Every element the read walked, when the caller asked for <c>--raw</c>; otherwise null.</param>
/// <param name="Nodes">How many elements were walked.</param>
/// <param name="Truncated">True when a limit stopped the walk before the tree ended.</param>
internal sealed record DialogRead(
    DialogInput Input,
    IReadOnlyList<nint> ButtonElements,
    JsonArray? Raw,
    int Nodes,
    bool Truncated);

/// <summary>
/// Reads one dialog through UI Automation: its body text, its buttons, and the facts that identify it.
/// </summary>
/// <remarks>
/// <para>
/// The tree is walked with the <b>raw</b> view rather than the control view. The raw view is what the measured
/// dialogs expose their buttons and text lines in — the MusicXML export dialog's seven buttons, the library
/// manager's labels and check boxes — and the control view can filter out the very elements an agent has to press.
/// </para>
/// <para>
/// A button is an element whose control type is <c>Button</c>. That is the measured shape of both kinds of dialog:
/// the Qt buttons that carry an <c>automationId</c> path, and the native <c>#32770</c> dialog's <c>选择</c> and
/// <c>取消</c>. A button's own children are not walked — the label inside a button is a separate text element, and
/// reading it as the dialog's body text would report every label twice.
/// </para>
/// </remarks>
internal static class DialogReader
{
    /// <summary>How many elements one read may walk before it stops and says it stopped.</summary>
    private const int MaxNodes = 3000;

    /// <summary>How deep one read may go. Deeper than any dialog that has been measured.</summary>
    private const int MaxDepth = 24;

    /// <summary>How much text the text pattern may return, when a dialog exposes one.</summary>
    private const int MaxTextLength = 20_000;

    /// <summary>Reads the dialog behind one window handle.</summary>
    /// <param name="handle">The dialog's window handle, from the presence probe.</param>
    /// <param name="title">The caption the probe read with Win32, used when UI Automation reports none.</param>
    /// <param name="windowClass">The window class the probe read.</param>
    /// <param name="owner">The owner handle, already formatted, or null.</param>
    /// <param name="raw">True to keep every walked element in the result, for <c>ui dialog --raw</c>.</param>
    public static DialogRead Read(nint handle, string title, string windowClass, string? owner, bool raw)
    {
        var automation = UiaCom.CreateAutomation();
        nint root = nint.Zero;
        nint walker = nint.Zero;
        var state = new WalkState(raw);

        try
        {
            root = UiaCalls.ElementFromHandle(automation, handle);
            walker = UiaCalls.RawViewWalker(automation);

            Visit(root, walker, 0, state);

            var caption = title.Length > 0 ? title : UiaCalls.CurrentName(root);
            var lines = state.TextLines.Count > 0 ? state.TextLines : state.LegacyLines;
            var source = state.TextLines.Count > 0 ? "uia" : state.LegacyLines.Count > 0 ? "legacy" : "none";
            var buttons = new List<DialogButtonInput>(state.Buttons.Count);
            var elements = new List<nint>(state.Buttons.Count);

            foreach (var button in state.Buttons)
            {
                buttons.Add(button.Input);
                elements.Add(button.Element);
            }

            var input = new DialogInput(
                Title: caption,
                WindowClass: windowClass,
                Owner: owner,
                TextSource: source,
                TextLines: lines,
                Buttons: buttons);

            return new DialogRead(input, elements, state.Raw, state.Nodes, state.Truncated);
        }
        catch
        {
            state.ReleaseButtons();
            throw;
        }
        finally
        {
            UiaCom.Release(walker);
            UiaCom.Release(root);
            UiaCom.Release(automation);
        }
    }

    /// <summary>A dialog with no readable content, for a caller that only has the window facts.</summary>
    public static DialogInput Empty(string title, string windowClass, string? owner)
        => new(title, windowClass, owner, "none", [], []);

    /// <summary>
    /// Visits one element and everything below it, collecting text and buttons. Returns true when the element was
    /// kept because it is a button, so its caller does not release it.
    /// </summary>
    private static bool Visit(nint element, nint walker, int depth, WalkState state)
    {
        state.Nodes++;

        if (state.Nodes > MaxNodes || depth > MaxDepth)
        {
            state.Truncated = true;

            return false;
        }

        var controlType = UiaCalls.ControlType(element);
        var name = UiaCalls.CurrentName(element);
        var automationId = UiaCalls.AutomationId(element);
        var bounds = UiaCalls.Bounds(element);

        if (state.Raw is not null)
        {
            AddRaw(
                state.Raw,
                depth,
                controlType,
                name,
                automationId,
                UiaCalls.CurrentClassName(element),
                UiaCalls.IsEnabled(element),
                bounds);
        }

        if (controlType == UiaCalls.ControlTypeButton)
        {
            state.Buttons.Add(new ButtonCandidate(
                new DialogButtonInput(name, automationId, IsInvokable(element), bounds.Left, bounds.Top),
                element));

            // Deliberately not descending: a button's children are its label, and the label is already its name.
            return true;
        }

        if (controlType == UiaCalls.ControlTypeText)
        {
            if (!string.IsNullOrWhiteSpace(name))
            {
                state.TextLines.Add(name);
            }
            else if (UiaCalls.Legacy(element) is { } legacy)
            {
                // A dialog whose labels reach UI Automation only through the legacy accessibility bridge. The
                // measured dialogs do not need this route, which is why it is the fallback and not the first try.
                var text = legacy.Name.Length > 0 ? legacy.Name : legacy.Value;

                if (!string.IsNullOrWhiteSpace(text))
                {
                    state.LegacyLines.Add(text);
                }
            }
        }

        var child = UiaCalls.FirstChild(walker, element);

        while (child != nint.Zero)
        {
            var next = UiaCalls.NextSibling(walker, child);

            if (!Visit(child, walker, depth + 1, state))
            {
                UiaCom.Release(child);
            }

            child = next;
        }

        // A dialog whose whole text is one document rather than a stack of labels. Read once, at the window itself,
        // and only when the walk found no text of its own.
        if (depth == 0 && state.TextLines.Count == 0 && UiaCalls.TryText(element, MaxTextLength) is { } document)
        {
            foreach (var line in document.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                state.TextLines.Add(line.TrimEnd('\r'));
            }
        }

        return false;
    }

    /// <summary>
    /// Whether an element can be pressed at all: an invoke pattern first, then a toggle, then the legacy default
    /// action. The presser uses the same order.
    /// </summary>
    private static bool IsInvokable(nint element)
    {
        if (HasPattern(element, UiaCalls.InvokePatternId) || HasPattern(element, UiaCalls.TogglePatternId))
        {
            return true;
        }

        return UiaCalls.Legacy(element) is { } legacy && legacy.DefaultAction.Length > 0;
    }

    private static bool HasPattern(nint element, int patternId)
    {
        var pattern = UiaCalls.Pattern(element, patternId);

        if (pattern == nint.Zero)
        {
            return false;
        }

        UiaCom.Release(pattern);

        return true;
    }

    private static void AddRaw(
        JsonArray raw,
        int depth,
        int controlType,
        string name,
        string automationId,
        string className,
        bool enabled,
        (int Left, int Top, int Right, int Bottom) bounds)
    {
        JsonArr.AddNode(raw, new JsonObject
        {
            ["depth"] = depth,
            ["controlType"] = ControlTypeName(controlType),
            ["name"] = name,
            ["automationId"] = automationId,
            ["className"] = className,
            ["enabled"] = enabled,
            ["bounds"] = new JsonObject
            {
                ["left"] = bounds.Left,
                ["top"] = bounds.Top,
                ["right"] = bounds.Right,
                ["bottom"] = bounds.Bottom,
            },
        });
    }

    /// <summary>The handful of control type ids this build names, and the number for everything else.</summary>
    public static string ControlTypeName(int controlType) => controlType switch
    {
        50000 => "Button",
        50002 => "CheckBox",
        50003 => "ComboBox",
        50004 => "Edit",
        50005 => "Hyperlink",
        50006 => "Image",
        50008 => "List",
        50009 => "Menu",
        50011 => "MenuItem",
        50012 => "ProgressBar",
        50013 => "RadioButton",
        50015 => "Slider",
        50016 => "Spinner",
        50018 => "Tab",
        50019 => "TabItem",
        50020 => "Text",
        50021 => "ToolBar",
        50023 => "Tree",
        50024 => "TreeItem",
        50025 => "Custom",
        50026 => "Group",
        50030 => "Document",
        50031 => "SplitButton",
        50032 => "Window",
        50033 => "Pane",
        50034 => "Header",
        50035 => "HeaderItem",
        50036 => "Table",
        50037 => "TitleBar",
        50038 => "Separator",
        _ => $"controlType({controlType})",
    };

    private sealed record ButtonCandidate(DialogButtonInput Input, nint Element);

    private sealed class WalkState(bool raw)
    {
        public List<string> TextLines { get; } = [];

        public List<string> LegacyLines { get; } = [];

        public List<ButtonCandidate> Buttons { get; } = [];

        public JsonArray? Raw { get; } = raw ? [] : null;

        public int Nodes { get; set; }

        public bool Truncated { get; set; }

        /// <summary>Drops the button elements a read kept, when the read is not going to hand them over.</summary>
        public void ReleaseButtons()
        {
            foreach (var button in Buttons)
            {
                UiaCom.Release(button.Element);
            }

            Buttons.Clear();
        }
    }
}
