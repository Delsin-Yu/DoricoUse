using System.Text.Json.Nodes;
using DoricoUse.Cli.Dorico;
using DoricoUse.Cli.Json;

namespace DoricoUse.Cli.Ui;

/// <summary>
/// Acts on one element of a window through the pattern that element's own state exposed.
/// </summary>
/// <remarks>
/// <para>
/// One implementation for both windows an element can live in, because the rule has to be the same in a panel and
/// in a dialog: what an agent may act on comes from the element's patterns (<see cref="PanelModel.Action"/>), never
/// from where the element happens to be. A dialog used to keep only its buttons, so its edit fields could be read
/// and not used; a dialog's elements now go through here exactly as a panel's do.
/// </para>
/// <para>
/// This file holds no rule of its own — it calls UI Automation and turns a refusal into the stable code the caller
/// switches on. Every decision about <i>which</i> pattern, and whether an element may be touched at all, is in
/// <see cref="PanelModel"/>, which the check projects compile.
/// </para>
/// </remarks>
internal static class ElementActions
{
    /// <summary>
    /// Performs one element's action and reports what was done, or throws the refusal that fits.
    /// </summary>
    /// <param name="handle">The UI Automation element the read kept for this element.</param>
    /// <param name="action">The action <see cref="PanelModel.Action"/> chose for it, never <see cref="PanelAction.None"/>.</param>
    /// <param name="element">The element as the payload describes it.</param>
    /// <param name="value">Text to write, when the action is <see cref="PanelAction.Value"/>.</param>
    /// <param name="where">What the element belongs to, named in a refusal: a panel id or a dialog id.</param>
    /// <param name="commitInput">
    /// True when the caller passed <c>--commit-input</c>: after the value write, click the control and send the text
    /// as real keystrokes, because a Qt field accepts a value-pattern write without telling its dialog that anything
    /// changed (measured 2026-10-13 on the 工程信息 dialog's 标题 field: the write read back and 应用 stayed
    /// disabled). Only a <see cref="PanelAction.Value"/> has anything to commit, and anything else is refused.
    /// </param>
    /// <param name="topLevel">The top-level window the element lives in, for the activation check of that pass.</param>
    /// <returns>The <c>worked</c> object to report, without the timing and without the re-read.</returns>
    public static JsonObject Perform(
        nint handle,
        PanelAction action,
        PanelElementDescription element,
        string? value,
        string where,
        bool commitInput = false,
        nint topLevel = default)
    {
        ArgumentNullException.ThrowIfNull(element);

        if (commitInput && !PanelModel.CommitInputApplicable(action))
        {
            throw PanelModel.RefuseCommitInput(where, element);
        }

        var performed = new JsonObject
        {
            ["element"] = element.Id,
            ["role"] = element.Role,
            ["name"] = element.Name,
            ["action"] = action.ToString().ToLowerInvariant(),
            ["stateBefore"] = element.State,
        };

        switch (action)
        {
            case PanelAction.Value:
                if (value is null)
                {
                    throw new CliException(
                        "ValueRequired",
                        $"Element '{element.Id}' ('{element.Name}') is a text field: it needs --value to be written "
                        + "into, because pressing a text field does nothing this build can verify.",
                        DialogModel.RefusalExitCode)
                    {
                        Details = new JsonObject { ["window"] = where, ["element"] = element.Id },
                    };
                }

                if (!UiaCalls.TrySetValue(handle, value, out var problem))
                {
                    throw new CliException(
                        "ElementNotSettable",
                        $"Element '{element.Id}' ('{element.Name}') exposes no value pattern, so '{value}' was not "
                        + "written anywhere.",
                        DialogModel.RefusalExitCode)
                    {
                        Details = new JsonObject { ["window"] = where, ["element"] = element.Id },
                    };
                }

                if (problem is not null)
                {
                    throw new CliException(
                        "ElementNotSettable",
                        $"Element '{element.Id}' ('{element.Name}') refused the value: {problem}.",
                        DialogModel.RefusalExitCode)
                    {
                        Details = new JsonObject { ["window"] = where, ["element"] = element.Id, ["value"] = value },
                    };
                }

                performed["value"] = value;

                // Written and committed are two different claims from here on, and the payload says which happened:
                // `written` is the value pattern taking the text (which a Qt field does without telling its dialog),
                // `commit` is the real-input pass, and `committed` is the two together.
                performed["written"] = true;

                if (commitInput)
                {
                    var commit = InputSimulator.Commit(topLevel, handle, value);

                    performed["commit"] = commit;
                    performed["committed"] = NodeRead.Bool(commit, "ran") == true
                        && NodeRead.Bool(commit, "valueMatches") == true;

                    if (NodeRead.Bool(commit, "ran") != true)
                    {
                        // The caller asked for real input and got none, so this is a failure and not a note: exit 2,
                        // because the runtime could not do what was asked, and the details carry the write that did
                        // happen so a caller cannot read this as "nothing was written".
                        throw new CliException(
                            "InputNotCommitted",
                            $"'{value}' was written into '{element.Id}' through the value pattern, and the real-input "
                            + "pass that --commit-input asks for could not run: "
                            + (NodeRead.Text(commit["reason"]) ?? "no reason was reported")
                            + ". The field holds the written text and the dialog may not have noticed it; the "
                            + "details carry the write.",
                            DialogModel.ProbeFailureExitCode)
                        {
                            Details = new JsonObject
                            {
                                ["window"] = where,
                                ["element"] = element.Id,
                                ["value"] = value,
                                ["written"] = true,
                                ["committed"] = false,
                                ["commit"] = commit.DeepClone(),
                            },
                        };
                    }
                }
                else
                {
                    performed["committed"] = false;
                }

                break;

            case PanelAction.Toggle:
                UiaCalls.ToggleButton(handle);
                break;

            case PanelAction.Select:
                UiaCalls.SelectItem(handle);
                break;

            case PanelAction.Expand:
                UiaCalls.Expand(handle, open: true);
                break;

            case PanelAction.Collapse:
                UiaCalls.Expand(handle, open: false);
                break;

            case PanelAction.Invoke:
                UiaCalls.InvokeButton(handle);
                break;

            default:
                // The callers refuse this case before reaching here, so it is a programming error rather than a
                // policy decision - and it is still reported as a refusal, because a wrong code is better than a
                // stack trace in an agent's payload.
                throw new CliException(
                    "ElementNotActionable",
                    $"Element '{element.Id}' ('{element.Name}') exposes no pattern this build can use: no invoke, no "
                    + "toggle, no selection, no expand-collapse and no value. Its role is '" + element.Role + "'. This "
                    + "build never clicks a coordinate and never types at random.",
                    DialogModel.RefusalExitCode)
                {
                    Details = new JsonObject { ["window"] = where, ["element"] = element.Id, ["role"] = element.Role },
                };
        }

        return performed;
    }

    /// <summary>The refusal for an element id that is not there, listing the ids that are.</summary>
    /// <param name="where">The panel or dialog id the caller named.</param>
    /// <param name="requested">The id the caller asked for.</param>
    /// <param name="ids">The ids that exist now.</param>
    /// <param name="what">What was searched, for the message.</param>
    public static CliException NotFound(string where, string requested, IReadOnlyList<string> ids, string what)
    {
        ArgumentNullException.ThrowIfNull(ids);

        var list = new JsonArray();

        foreach (var id in ids)
        {
            JsonArr.AddText(list, id);
        }

        return new CliException(
            "ElementNotFound",
            $"{what} '{where}' has no element '{requested}'. Its ids are: "
            + (ids.Count == 0 ? "(none: the read found no actionable element)" : string.Join(", ", ids)),
            DialogModel.RefusalExitCode)
        {
            Details = new JsonObject
            {
                ["window"] = where,
                ["requested"] = requested,
                ["ids"] = list,
            },
        };
    }
}
