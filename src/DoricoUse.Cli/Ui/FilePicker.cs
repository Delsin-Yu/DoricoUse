using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using DoricoUse.Cli.Dorico;
using DoricoUse.Cli.Json;

namespace DoricoUse.Cli.Ui;

/// <summary>
/// Filling in a file dialog's file-name box before its accept button is pressed.
/// </summary>
/// <remarks>
/// <para>
/// Two shapes have to be handled, and only one of them was ever read. Measured: Dorico's folder picker is a native
/// <c>#32770</c> window, owned by the document window, whose accept and cancel buttons carry the classic control
/// ids <c>1</c> and <c>2</c> — and which has <b>no</b> file-name box at all, because a folder picker does not need
/// one. A Qt picker inside Dorico's own process is the other shape, and the same search finds its edit field.
/// </para>
/// <para>
/// <b>Unverified.</b> No <i>file</i> picker has been read, so neither the control type nor the automation id of its
/// file-name box is known here. This code therefore claims nothing: it looks for the control the classic dialog
/// uses (<c>automationId="1148"</c>), then for any writable edit, then for any writable combo box; it writes the
/// path through the UI Automation value pattern; and it refuses with <c>PickerNotSettable</c> when there is none. It
/// never types keystrokes, because the measurement that would justify that has not been made.
/// </para>
/// </remarks>
internal static class FilePicker
{
    /// <summary>The classic Win32 file-name box. Measured to be absent from the folder picker Dorico raises.</summary>
    public const string ClassicFileNameControlId = "1148";

    private const int MaxNodes = 2000;

    private const int MaxDepth = 24;

    /// <summary>The longest value this build will read out of a control for its report.</summary>
    private const int MaxValueLength = 1000;

    /// <summary>
    /// Writes <paramref name="value"/> into the dialog's file-name box and reports which control took it.
    /// </summary>
    /// <exception cref="CliException">
    /// <c>PickerNotSettable</c> (exit 1) when the dialog has no control this build can write through, naming what
    /// it did find.
    /// </exception>
    public static JsonObject Fill(nint handle, string value)
    {
        var automation = UiaCom.CreateAutomation();

        try
        {
            var seen = new List<JsonObject>();
            var chosen = Locate(automation, handle, seen, classicOnly: true)
                ?? Locate(automation, handle, seen, editOnly: true)
                ?? Locate(automation, handle, seen, comboOnly: true);

            if (chosen is null)
            {
                throw Refuse(seen);
            }

            try
            {
                var previous = UiaCalls.TryValue(chosen.Element, out var before, out _) ? Truncate(before) : null;

                if (!UiaCalls.TrySetValue(chosen.Element, value, out var problem) || problem is not null)
                {
                    throw Refuse(seen, problem);
                }

                var written = UiaCalls.TryValue(chosen.Element, out var after, out _) ? after : value;

                return new JsonObject
                {
                    ["control"] = DialogReader.ControlTypeName(chosen.ControlType),
                    ["automationId"] = chosen.AutomationId.Length == 0 ? null : chosen.AutomationId,
                    ["className"] = chosen.ClassName.Length == 0 ? null : chosen.ClassName,
                    ["pattern"] = "value",
                    ["previous"] = previous,
                    ["value"] = Truncate(written),
                    ["requested"] = value,
                    ["verified"] = string.Equals(written, value, StringComparison.Ordinal),
                    ["note"] = "The path was written through the UI Automation value pattern, which is the only way "
                        + "this build writes anything into a control. Whether the picker then accepts it is whatever "
                        + "the re-probe after the press says.",
                };
            }
            finally
            {
                UiaCom.Release(chosen.Element);
            }
        }
        finally
        {
            UiaCom.Release(automation);
        }
    }

    /// <summary>
    /// The first control in the dialog that matches, or null. Three calls with different predicates make the
    /// preference order explicit: the classic file-name box, then an edit, then a combo box.
    /// </summary>
    private static Candidate? Locate(nint automation, nint handle, List<JsonObject> seen, bool classicOnly = false, bool editOnly = false, bool comboOnly = false)
    {
        nint root = nint.Zero;
        nint walker = nint.Zero;

        try
        {
            root = UiaCalls.ElementFromHandle(automation, handle);
            walker = UiaCalls.RawViewWalker(automation);

            var state = new FindState { Seen = seen, ClassicOnly = classicOnly, EditOnly = editOnly, ComboOnly = comboOnly };
            Search(root, walker, 0, state);

            return state.Found;
        }
        finally
        {
            UiaCom.Release(walker);
            UiaCom.Release(root);
        }
    }

    private static void Search(nint element, nint walker, int depth, FindState state)
    {
        state.Nodes++;

        if (state.Found is not null || state.Nodes > MaxNodes || depth > MaxDepth)
        {
            return;
        }

        var controlType = UiaCalls.ControlType(element);

        if (controlType is ControlTypes.Edit or ControlTypes.ComboBox)
        {
            var automationId = UiaCalls.AutomationId(element);
            var className = UiaCalls.CurrentClassName(element);

            if (UiaCalls.TryValue(element, out var current, out var readOnly))
            {
                // Every value control goes into the report once, whichever search finds it first.
                if (!state.Seen.Any(item => NodeRead.Str(item, "automationId") == (automationId.Length == 0 ? null : automationId)
                    && NodeRead.Str(item, "className") == (className.Length == 0 ? null : className)))
                {
                    state.Seen.Add(new JsonObject
                    {
                        ["control"] = DialogReader.ControlTypeName(controlType),
                        ["automationId"] = automationId.Length == 0 ? null : automationId,
                        ["className"] = className.Length == 0 ? null : className,
                        ["readOnly"] = readOnly,
                        ["value"] = Truncate(current),
                    });
                }

                var classic = string.Equals(automationId, ClassicFileNameControlId, StringComparison.Ordinal);
                var wanted = state.ClassicOnly ? classic
                    : state.EditOnly ? controlType == ControlTypes.Edit
                    : state.ComboOnly ? controlType == ControlTypes.ComboBox
                    : false;

                if (wanted && !readOnly)
                {
                    // The element is about to be released with the rest of the walk, so take a reference of its own.
                    _ = Marshal.AddRef(element);
                    state.Found = new Candidate(element, controlType, automationId, className);

                    return;
                }
            }
        }

        var child = UiaCalls.FirstChild(walker, element);

        while (child != nint.Zero)
        {
            var next = UiaCalls.NextSibling(walker, child);
            Search(child, walker, depth + 1, state);
            UiaCom.Release(child);
            child = next;
        }
    }

    private static string Truncate(string value)
        => value.Length <= MaxValueLength ? value : value[..MaxValueLength] + "…";

    private static CliException Refuse(List<JsonObject> seen, string? problem = null)
    {
        var controls = new JsonArray();

        foreach (var control in seen)
        {
            JsonArr.AddNode(controls, control);
        }

        var details = new JsonObject
        {
            ["classicFileNameControlId"] = ClassicFileNameControlId,
            ["problem"] = problem,
            ["controls"] = controls,
            ["note"] = "The path is written through the UI Automation value pattern and nothing else: this build "
                + "never types keystrokes, which is why a picker whose file-name box refuses the value pattern is "
                + "refused here rather than driven another way.",
        };

        return new CliException(
            "PickerNotSettable",
            "This dialog has no control this build can write the path into"
            + (problem is null ? "." : $": {problem}.")
            + (seen.Count == 0
                ? " It exposes no value control at all, which is what a folder picker looks like (measured: Dorico's "
                    + "folder picker has no file-name box)."
                : $" It exposes {seen.Count} value control(s), none of them writable (see error.data.controls)."),
            DialogModel.RefusalExitCode)
        {
            Details = details,
        };
    }

    private sealed record Candidate(nint Element, int ControlType, string AutomationId, string ClassName);

    private sealed class FindState
    {
        public required List<JsonObject> Seen { get; init; }

        public bool ClassicOnly { get; init; }

        public bool EditOnly { get; init; }

        public bool ComboOnly { get; init; }

        public Candidate? Found { get; set; }

        public int Nodes { get; set; }
    }
}
