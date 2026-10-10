using System.Text.Json.Nodes;
using DoricoUse.Cli.Dorico;

namespace DoricoUse.Cli.Ui;

/// <summary>Which of the three patterns actually pressed the button, and what the call had to say.</summary>
/// <param name="Pattern">The pattern that was used: <c>invoke</c>, <c>toggle</c> or <c>legacy-default-action</c>.</param>
/// <param name="ElementGone">
/// True when the press reported that the element had disappeared. Read the handover's trap: a press that closes
/// its own window reports an error <b>after</b> it succeeded, so this is a success, not a failure.
/// </param>
internal sealed record PressOutcome(string Pattern, bool ElementGone);

/// <summary>
/// Presses one button, and refuses rather than guessing.
/// </summary>
/// <remarks>
/// <para>
/// The pattern order is invoke, then toggle, then the legacy accessibility default action — the three the measured
/// Qt and native buttons offer between them. There is deliberately no fourth option: this build never posts a
/// mouse event or clicks a coordinate, so an element with none of the three is refused with
/// <c>ButtonNotInvokable</c> and reported rather than approximated.
/// </para>
/// <para>
/// The trap that shapes this file was measured: pressing 关闭 on the library manager closed the dialog and
/// <b>then</b> returned <c>0x80040201</c> (<c>UIA_E_ELEMENTNOTAVAILABLE</c>) because the element had gone with the
/// window. So that one HRESULT is a success, and the caller must re-probe instead of trusting either answer.
/// </para>
/// </remarks>
internal static class DialogPresser
{
    /// <summary>Presses one element, or throws <c>ButtonNotInvokable</c> / <c>PressFailed</c>.</summary>
    public static PressOutcome Press(nint element)
    {
        if (HasPattern(element, UiaCalls.InvokePatternId))
        {
            return Call(element, "invoke", UiaCalls.InvokeButton);
        }

        if (HasPattern(element, UiaCalls.TogglePatternId))
        {
            return Call(element, "toggle", UiaCalls.ToggleButton);
        }

        if (UiaCalls.Legacy(element) is { } legacy && legacy.DefaultAction.Length > 0)
        {
            return Call(element, "legacy-default-action", UiaCalls.LegacyDefaultAction);
        }

        throw new CliException(
            "ButtonNotInvokable",
            "The button exposes none of the three patterns this build can press: no invoke pattern, no toggle "
            + "pattern and no legacy default action. This build never clicks a coordinate, so there is nothing "
            + "else it could do.",
            DialogModel.RefusalExitCode)
        {
            Details = new JsonObject
            {
                ["patterns"] = "this build presses through invoke (10000), toggle (10015) or the legacy "
                    + "accessibility default action (10018), in that order",
                ["available"] = Available(element),
            },
        };
    }

    /// <summary>Releases the button elements a read handed over. Safe to call more than once per list.</summary>
    public static void ReleaseAll(IReadOnlyList<nint> elements)
    {
        foreach (var element in elements)
        {
            UiaCom.Release(element);
        }
    }

    private static PressOutcome Call(nint element, string pattern, Action<nint> call)
    {
        try
        {
            call(element);

            return new PressOutcome(pattern, ElementGone: false);
        }
        catch (CliException ex) when (IsElementGone(ex))
        {
            // The element is gone because the press worked. Measured: pressing 关闭 closed the library manager and
            // then answered exactly this, so an error here is evidence of success and the caller must re-probe.
            return new PressOutcome(pattern, ElementGone: true);
        }
        catch (CliException ex) when (ex.Code == "DialogProbeFailed")
        {
            throw new CliException(
                "PressFailed",
                $"The {pattern} pattern failed: {ex.Message} The dialog was left as it was found; press again or "
                + "read `ui dialog` to see what is up now.",
                2);
        }
    }

    /// <summary>True for <c>UIA_E_ELEMENTNOTAVAILABLE</c>, which a press that destroys its window returns.</summary>
    private static bool IsElementGone(CliException exception)
        => NodeRead.Str(exception.Details, "hresult")
            == $"0x{UiaCom.ElementNotAvailable:X8}";

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

    /// <summary>What the element does expose, for the refusal's <c>error.data</c>. Measured, not guessed.</summary>
    private static string Available(nint element)
    {
        var available = new List<string>();

        foreach (var (name, id) in new[]
        {
            ("invoke", UiaCalls.InvokePatternId),
            ("value", UiaCalls.ValuePatternId),
            ("toggle", UiaCalls.TogglePatternId),
            ("text", UiaCalls.TextPatternId),
            ("legacy", UiaCalls.LegacyIAccessiblePatternId),
        })
        {
            var pattern = UiaCalls.Pattern(element, id);

            if (pattern == nint.Zero)
            {
                continue;
            }

            available.Add(name);
            UiaCom.Release(pattern);
        }

        return available.Count == 0 ? "nothing" : string.Join(", ", available);
    }
}
