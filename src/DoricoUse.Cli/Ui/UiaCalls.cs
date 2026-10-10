using System.Text.Json.Nodes;

namespace DoricoUse.Cli.Ui;

/// <summary>
/// The UI Automation calls this build makes, one method per COM method, each with its vtable slot named after the
/// method it is.
/// </summary>
/// <remarks>
/// <para>
/// The slots come from the UI Automation client interface definitions and are the 0-based method index after
/// IUnknown's three entries — the same numbering <see cref="UiaCom"/> uses. They are collected here so there is one
/// place to check a number against, and no call site has to know one.
/// </para>
/// <para>
/// Everything a caller gets back is an owned pointer: release it with <see cref="UiaCom.Release"/>.
/// </para>
/// </remarks>
internal static class UiaCalls
{
    // IUIAutomation
    private const int AutomationElementFromHandle = 3;
    private const int AutomationRawViewWalker = 13;

    // IUIAutomationTreeWalker
    private const int WalkerFirstChild = 1;
    private const int WalkerNextSibling = 3;

    // IUIAutomationElement
    private const int ElementGetCurrentPattern = 13;
    private const int ElementCurrentControlType = 18;
    private const int ElementCurrentName = 20;
    private const int ElementCurrentIsEnabled = 25;
    private const int ElementCurrentAutomationId = 26;
    private const int ElementCurrentClassName = 27;
    private const int ElementCurrentBoundingRectangle = 40;

    // IUIAutomationInvokePattern
    private const int Invoke = 0;

    // IUIAutomationValuePattern
    private const int ValueSetValue = 0;
    private const int ValueCurrentValue = 1;
    private const int ValueIsReadOnly = 2;

    // IUIAutomationTogglePattern
    private const int Toggle = 0;

    // IUIAutomationLegacyIAccessiblePattern
    private const int LegacyDoDefaultAction = 1;
    private const int LegacyCurrentName = 4;
    private const int LegacyCurrentValue = 5;
    private const int LegacyCurrentDefaultAction = 12;

    // IUIAutomationTextPattern and IUIAutomationTextRange
    private const int TextPatternDocumentRange = 4;
    private const int TextRangeText = 9;

    /// <summary>Pattern ids as the client interface definitions spell them.</summary>
    public const int InvokePatternId = 10000;

    public const int ValuePatternId = 10002;

    public const int TogglePatternId = 10015;

    public const int TextPatternId = 10014;

    public const int LegacyIAccessiblePatternId = 10018;

    /// <summary>Control type ids this build distinguishes.</summary>
    public const int ControlTypeButton = 50000;

    public const int ControlTypeEdit = 50004;

    public const int ControlTypeComboBox = 50003;

    public const int ControlTypeText = 50020;

    /// <summary>S_FALSE, which a tree walk returns for "no such child" rather than an error.</summary>
    private const int False = 1;

    public static nint ElementFromHandle(nint automation, nint handle) => Pointer(
        UiaCom.CallHandleOutPointer(automation, AutomationElementFromHandle, handle, out var element),
        element,
        nameof(ElementFromHandle));

    public static nint RawViewWalker(nint automation) => Pointer(
        UiaCom.CallPointer(automation, AutomationRawViewWalker, out var walker),
        walker,
        nameof(RawViewWalker));

    public static nint FirstChild(nint walker, nint element)
        => Walk(UiaCom.CallInPointerOutPointer(walker, WalkerFirstChild, element, out var child), child);

    public static nint NextSibling(nint walker, nint element)
        => Walk(UiaCom.CallInPointerOutPointer(walker, WalkerNextSibling, element, out var sibling), sibling);

    public static int ControlType(nint element)
        => UiaCom.CallInt(element, ElementCurrentControlType, out var value) < 0 ? 0 : value;

    public static string CurrentName(nint element)
        => UiaCom.CallBstr(element, ElementCurrentName, out var value) < 0 ? string.Empty : value;

    public static string AutomationId(nint element)
        => UiaCom.CallBstr(element, ElementCurrentAutomationId, out var value) < 0 ? string.Empty : value;

    public static string CurrentClassName(nint element)
        => UiaCom.CallBstr(element, ElementCurrentClassName, out var value) < 0 ? string.Empty : value;

    public static bool IsEnabled(nint element)
        => UiaCom.CallInt(element, ElementCurrentIsEnabled, out var value) >= 0 && value != 0;

    public static (int Left, int Top, int Right, int Bottom) Bounds(nint element)
        => UiaCom.CallRect(element, ElementCurrentBoundingRectangle, out var rect) < 0 ? default : rect;

    /// <summary>The pattern object for a pattern id, or zero when the element does not expose it.</summary>
    public static nint Pattern(nint element, int patternId)
    {
        var hr = UiaCom.CallIntOutPointer(element, ElementGetCurrentPattern, patternId, out var pattern);

        return hr < 0 || pattern == nint.Zero ? nint.Zero : pattern;
    }

    /// <summary>Pressing a button: the first of the three patterns that this build will use.</summary>
    public static void InvokeButton(nint element)
    {
        var pattern = Pattern(element, InvokePatternId);

        if (pattern == nint.Zero)
        {
            throw new CliException(
                "ButtonNotInvokable",
                "The element exposes no invoke pattern.",
                DialogModel.RefusalExitCode);
        }

        try
        {
            Check(UiaCom.CallVoid(pattern, Invoke), nameof(InvokeButton));
        }
        finally
        {
            UiaCom.Release(pattern);
        }
    }

    /// <summary>Toggling a check box: the second pattern this build will use, and one that really changes state.</summary>
    public static void ToggleButton(nint element)
    {
        var pattern = Pattern(element, TogglePatternId);

        if (pattern == nint.Zero)
        {
            throw new CliException(
                "ButtonNotInvokable",
                "The element exposes no toggle pattern.",
                DialogModel.RefusalExitCode);
        }

        try
        {
            Check(UiaCom.CallVoid(pattern, Toggle), nameof(ToggleButton));
        }
        finally
        {
            UiaCom.Release(pattern);
        }
    }

    /// <summary>The legacy accessibility pattern's default action, the last resort before refusing.</summary>
    public static void LegacyDefaultAction(nint element)
    {
        var pattern = Pattern(element, LegacyIAccessiblePatternId);

        if (pattern == nint.Zero)
        {
            throw new CliException(
                "ButtonNotInvokable",
                "The element exposes neither an invoke pattern, a toggle pattern nor a legacy default action.",
                DialogModel.RefusalExitCode);
        }

        try
        {
            Check(UiaCom.CallVoid(pattern, LegacyDoDefaultAction), nameof(LegacyDefaultAction));
        }
        finally
        {
            UiaCom.Release(pattern);
        }
    }

    /// <summary>What a legacy accessibility element offers, or null when it has none.</summary>
    public static (string Name, string Value, string DefaultAction)? Legacy(nint element)
    {
        var pattern = Pattern(element, LegacyIAccessiblePatternId);

        if (pattern == nint.Zero)
        {
            return null;
        }

        try
        {
            _ = UiaCom.CallBstr(pattern, LegacyCurrentName, out var name);
            _ = UiaCom.CallBstr(pattern, LegacyCurrentValue, out var value);
            _ = UiaCom.CallBstr(pattern, LegacyCurrentDefaultAction, out var action);

            return (name, value, action);
        }
        finally
        {
            UiaCom.Release(pattern);
        }
    }

    /// <summary>The current value of an element that has a value pattern: a file-name box, an edit field.</summary>
    public static bool TryValue(nint element, out string value, out bool readOnly)
    {
        value = string.Empty;
        readOnly = false;

        var pattern = Pattern(element, ValuePatternId);

        if (pattern == nint.Zero)
        {
            return false;
        }

        try
        {
            _ = UiaCom.CallBstr(pattern, ValueCurrentValue, out value);
            _ = UiaCom.CallInt(pattern, ValueIsReadOnly, out var flag);
            readOnly = flag != 0;

            return true;
        }
        finally
        {
            UiaCom.Release(pattern);
        }
    }

    /// <summary>Writes a value through the value pattern. False when the element does not have one.</summary>
    public static bool TrySetValue(nint element, string value, out string? problem)
    {
        problem = null;
        var pattern = Pattern(element, ValuePatternId);

        if (pattern == nint.Zero)
        {
            return false;
        }

        try
        {
            if (UiaCom.CallInt(pattern, ValueIsReadOnly, out var readOnly) >= 0 && readOnly != 0)
            {
                problem = "the element reports its value as read-only";

                return true;
            }

            var bstr = UiaCom.AllocBstr(value);

            try
            {
                var hr = UiaCom.CallBstrIn(pattern, ValueSetValue, bstr);

                if (hr < 0)
                {
                    problem = $"the value pattern refused the write (HRESULT 0x{hr:X8})";

                    return true;
                }
            }
            finally
            {
                UiaCom.FreeBstr(bstr);
            }

            return true;
        }
        finally
        {
            UiaCom.Release(pattern);
        }
    }

    /// <summary>
    /// The whole text of an element that exposes the text pattern, or null.
    /// </summary>
    /// <remarks>
    /// The measured dialogs expose their body text as individual <c>Text</c> elements instead, so this is a
    /// fallback for a dialog whose text is one document. It is capped, because a text pattern on a large document
    /// would otherwise pull megabytes into a payload nobody reads.
    /// </remarks>
    public static string? TryText(nint element, int maximum)
    {
        var pattern = Pattern(element, TextPatternId);

        if (pattern == nint.Zero)
        {
            return null;
        }

        nint range = nint.Zero;

        try
        {
            if (UiaCom.CallPointer(pattern, TextPatternDocumentRange, out range) < 0 || range == nint.Zero)
            {
                return null;
            }

            return UiaCom.CallIntOutBstr(range, TextRangeText, maximum, out var text) < 0 ? null : text;
        }
        finally
        {
            UiaCom.Release(range);
            UiaCom.Release(pattern);
        }
    }

    private static nint Pointer(int hr, nint value, string api)
    {
        Check(hr, api);

        return value;
    }

    private static nint Walk(int hr, nint value)
        => hr == False ? nint.Zero : Pointer(hr, value, "tree walk");

    private static void Check(int hr, string api)
    {
        if (hr >= 0)
        {
            return;
        }

        throw new CliException(
            "DialogProbeFailed",
            $"The UI Automation call {api} failed (HRESULT 0x{hr:X8}).",
            DialogModel.ProbeFailureExitCode)
        {
            // The HRESULT is what the caller switches on: 0x80040201 is "the element went away", which for a press
            // means the press worked. Matching a number is honest; matching the English in a message is not.
            Details = new JsonObject
            {
                ["api"] = api,
                ["hresult"] = $"0x{hr:X8}",
            },
        };
    }
}
