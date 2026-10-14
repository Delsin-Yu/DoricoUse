using System.CommandLine;
using System.Text.Json.Nodes;
using DoricoUse.Cli.Dorico;
using DoricoUse.Cli.Ipc;
using DoricoUse.Cli.Ui;

namespace DoricoUse.Cli.Commands;

/// <summary>The flows and layouts of the open project: the IDs every other request asks for.</summary>
internal static class ProjectCommands
{
    public static Command Build()
    {
        var project = new Command("project", "Flows and layouts of the project that is open in Dorico.");
        project.Subcommands.Add(BuildFlows());
        project.Subcommands.Add(BuildLayouts());
        project.Subcommands.Add(BuildPlayers());
        project.Subcommands.Add(BuildLayoutDefaults());
        return project;
    }

    /// <summary>
    /// The 布局选项 dialog's 重置到保存的默认值 button, as one command: the measured route for the engraving
    /// checklist's part-layout item.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Measured on Dorico 6.2.30: <c>Project.Layout.AllPartOptions</c> raises the layout options dialog with every
    /// part layout selected and <c>Project.Layout.Options?LayoutIDArray=&lt;id&gt;</c> raises it for one layout; the
    /// dialog's <c>resetToSavedDefaults</c> (重置到保存的默认值) button restores the layout options of the layouts
    /// it is open for, and its effect reaches the document at once — a press restored two dirtied part layouts
    /// before 应用 was pressed, and `file read --layout` found the new values after a save while the full score (not
    /// in the dialog's selection) kept its own.
    /// </para>
    /// <para>
    /// This command is the two steps together because a caller that has to raise a dialog, discover its panel id,
    /// find an element id and then press it is walking a discovery sequence the tool exists to remove. What it does
    /// <b>not</b> do is claim the effect: the payload carries the panel as it is afterwards and a <c>verifyWith</c>
    /// line, because whether a layout's options really changed is a question for <c>options get</c> or
    /// <c>file read --layout</c>, not for a button's acknowledgement.
    /// </para>
    /// <para>
    /// The dialog is then closed by this command, by 关闭 and never by 应用. Its close and apply buttons have no
    /// unique automation id in Qt — measured 2026-10-13, 关闭 was <c>button-98</c> in one read and its id moves when
    /// the dialog's page changes — so the close is a fresh read matched on the label (exact and unique) rather than
    /// an id remembered from an earlier payload. The reset itself needs no 应用 press: its effect reaches the
    /// document immediately (measured: two dirtied part layouts restored while the dialog was still up). A close
    /// that cannot be matched presses nothing and says so in <c>closed</c>, because pressing the first button of a
    /// dialog this tool does not fully understand is how a 应用 gets pressed by accident.
    /// </para>
    /// </remarks>
    private static Command BuildLayoutDefaults()
    {
        var partLayouts = new Option<bool>("--part-layouts")
        {
            Description = "Open 全部声部布局选项 (Project.Layout.AllPartOptions): every part layout, which is what "
                + "the engraving checklist's part-layout item needs.",
        };

        var layout = new Option<int?>("--layout")
        {
            Description = "Open 布局选项 for this layout ID alone (Project.Layout.Options?LayoutIDArray=<id>). "
                + "`project layouts` lists the IDs.",
        };

        var timeout = CommandRoot.TimeoutOption();
        var expectScore = CommandRoot.ExpectScoreOption();

        var command = new Command(
            "layout-defaults",
            "Restore the layouts' options to the user's saved defaults: the 布局选项 dialog's 重置到保存的默认值 "
            + "button (resetToSavedDefaults), pressed for the part layouts or for one named layout. Measured: one "
            + "press restores every layout the dialog is open for and leaves the layouts that are not in it alone, "
            + "and the effect reaches the document immediately. The dialog is left open; verify with `options get "
            + "--type layout --layout <id>` or, after a save, `file read --path <file> --layout`.")
        {
            partLayouts,
            layout,
            timeout,
            expectScore,
        };

        command.SetAction(parse => CommandRoot.RunAsync("project layout-defaults", async () =>
        {
            var allParts = parse.GetValue(partLayouts);
            var one = parse.GetValue(layout);

            // Exactly one scope: both given, or neither, is a usage error rather than a guess.
            if (allParts == (one is not null))
            {
                throw new CliException(
                    "InvalidArgument",
                    "Pass exactly one of --part-layouts (every part layout) or --layout <id> (one layout). The "
                    + "dialog's layout selection is what the reset acts on, and there is no measured way to name "
                    + "more than one layout in the command's own array parameter.",
                    1);
            }

            var budget = CommandRoot.Timeout(parse, timeout) * 2;
            var raiseName = allParts ? "Project.Layout.AllPartOptions" : "Project.Layout.Options";
            var args = new JsonObject
            {
                ["name"] = raiseName,
                ["waitMs"] = 0,
                ["verify"] = true,
            };

            if (!allParts)
            {
                args["parameters"] = new JsonObject { ["LayoutIDArray"] = one!.Value };
            }

            CommandRoot.AddExpectScore(args, parse.GetValue(expectScore));

            // The raise goes through the host, so --expect-score is checked in the same exchange as the command,
            // exactly as `command send` does it.
            var raised = await HostClient.CallAsync(HostProtocol.OpCommand, args, budget, CancellationToken.None)
                .ConfigureAwait(false);

            var ack = NodeRead.Str(NodeRead.Obj(raised, "result"), "ack");

            // A raise whose window has not been drawn yet is not a missing panel: the dialog is a Qt window and its
            // widgets appear with it, so the wait is bounded and the answer is reported either way.
            if (!PanelSurface.WaitForPanel(PanelCataloguedLayoutOptions, 10_000))
            {
                throw new CliException(
                    "LayoutOptionsNotUp",
                    $"{raiseName} answered {(ack is null ? "no acknowledgement" : ack)} and no window of the layout "
                    + "options dialog was up ten seconds later, so nothing was pressed. `ui panels` lists the "
                    + "windows Dorico really has; the dialog is the one titled \"<document> - 布局选项\".",
                    2)
                {
                    Details = new JsonObject { ["raised"] = raiseName, ["ack"] = ack },
                };
            }

            var worked = PanelSurface.Act(PanelCataloguedLayoutOptions, ResetToSavedDefaultsElement, null, allowUnknownDialog: false);
            var element = NodeRead.Obj(worked, "worked");

            // The panel as it was before the close: the payload a caller verifies the reset against, and the one the
            // close is about to make meaningless. It is read here rather than after the close for exactly that
            // reason.
            var beforeClose = NodeRead.Arr(worked, "panels") is { Count: > 0 } panels ? panels[0]?.DeepClone() : null;

            // And now the dialog is closed, by label, in a fresh read: the reset above leaves it up, and a caller
            // that has to close it itself is a caller that may press the wrong button.
            var closed = PanelSurface.PressByLabel(PanelCataloguedLayoutOptions, CloseLabel);

            var result = new JsonObject
            {
                ["scope"] = allParts ? "part-layouts" : "layout",
                ["layout"] = allParts ? null : one!.Value,
                ["raised"] = new JsonObject { ["name"] = raiseName, ["ack"] = ack },
                ["panel"] = beforeClose,
                ["closed"] = closed,
                ["pressed"] = element?.DeepClone(),
                ["what"] = "重置到保存的默认值 (resetToSavedDefaults) restores the layout options of the layouts the "
                    + "dialog is open for to the defaults saved in the user's library, and its effect reaches the "
                    + "document immediately — no 应用 press is needed for it. Measured: one press restored two "
                    + "dirtied part layouts (staff size 9.9 and 7.7 back to 4.96 in the saved file) and left the "
                    + "full score, which was not in the dialog's selection, at its own value.",
                ["verifyWith"] = "dorico-use options get --type layout --layout <id> --filter *   (live), or save the "
                    + "project and read the file: dorico-use file read --path <project.dorico> --limit 0 --layout",
                ["note"] = "The dialog is closed again by this command: 关闭 is matched by its label in a fresh "
                    + "read, because its element id is positional and moves when the page changes, and 应用 is never "
                    + "pressed — the reset needs no apply. `closed.pressed` says what was pressed, `closed.attempts` "
                    + "how many reads it took, and `closed.reason` why nothing was pressed when that is what "
                    + "happened. The `panel` above is the dialog as it was before the close, which is the read the "
                    + "reset is verified against.",
            };

            return new JsonObject { ["layoutDefaults"] = result };
        }));

        return command;
    }

    /// <summary>The panel id of the 布局选项 dialog. Its catalogue entry was written from a live reading.</summary>
    private const string PanelCataloguedLayoutOptions = "dorico.layout-options";

    /// <summary>The measured reset button, which is the one control in that dialog with a unique automation id.</summary>
    private const string ResetToSavedDefaultsElement = "resetToSavedDefaults";

    /// <summary>
    /// The label of the dialog's close button. A label rather than an id, and read for the reason
    /// <see cref="PanelSurface.PressByLabel"/> gives: measured 2026-10-13, that dialog's 关闭 and 应用 have no unique
    /// automation id in Qt and their positional ids move when the page changes.
    /// </summary>
    private const string CloseLabel = "关闭";

    /// <summary>
    /// The players of the open project. There is no protocol request for them, so this reads the running document
    /// through the scripting layer; the payload says exactly what that layer answered.
    /// </summary>
    private static Command BuildPlayers()
    {
        var timeout = CommandRoot.TimeoutOption(20000);
        var expectScore = CommandRoot.ExpectScoreOption();
        var command = new Command(
            "players",
            "The players of the document Dorico has open: their index, the name Dorico describes them with, and "
            + "every other member the scripting layer exposes. No protocol request returns players, so this is a "
            + "live read (about a second) rather than a `getflows`-style fetch.")
        {
            timeout,
            expectScore,
        };

        command.SetAction(parse => CommandRoot.RunAsync("project players", () =>
        {
            var wait = CommandRoot.Timeout(parse, timeout);

            return LivePlayers.RunAsync(
                wait < LivePlayers.DefaultWait ? LivePlayers.DefaultWait : wait,
                CancellationToken.None,
                parse.GetValue(expectScore));
        }));

        return command;
    }

    private static Command BuildFlows()
    {
        var raw = CommandRoot.RawOption();
        var timeout = CommandRoot.TimeoutOption();

        var command = new Command("flows", "The flows in the project; notation options are per flow.")
        {
            raw,
            timeout,
        };

        command.SetAction(parse => CommandRoot.RunAsync("project flows", async () =>
            await HostClient.CallAsync(
                HostProtocol.OpFlows,
                new JsonObject { ["raw"] = parse.GetValue(raw) },
                CommandRoot.Timeout(parse, timeout),
                CancellationToken.None)));

        return command;
    }

    private static Command BuildLayouts()
    {
        var kind = new Option<string?>("--kind")
        {
            Description = "Only layouts of this kind: full-score or part.",
        };

        var raw = CommandRoot.RawOption();
        var timeout = CommandRoot.TimeoutOption();

        var command = new Command("layouts", "The layouts in the project; layout options are per layout.")
        {
            kind,
            raw,
            timeout,
        };

        command.SetAction(parse => CommandRoot.RunAsync("project layouts", async () =>
        {
            var args = new JsonObject { ["raw"] = parse.GetValue(raw) };
            var kindValue = parse.GetValue(kind);

            if (!string.IsNullOrWhiteSpace(kindValue))
            {
                args["kind"] = kindValue;
            }

            return await HostClient.CallAsync(
                HostProtocol.OpLayouts,
                args,
                CommandRoot.Timeout(parse, timeout),
                CancellationToken.None);
        }));

        return command;
    }
}
