using System.CommandLine;
using System.Text.Json.Nodes;
using DoricoUse.Cli.Ui;

namespace DoricoUse.Cli.Commands;

/// <summary>
/// The modal dialog Dorico has up, and the one button an agent needs to answer it.
/// </summary>
/// <remarks>
/// <para>
/// Two commands, because an interruptible action is act → press and nothing more. Every payload already carries
/// the <c>dialog</c> field, so an agent usually never calls <c>ui dialog</c> at all: the command that stalled
/// behind a dialog reports the dialog, what it says, and the ready-to-paste <c>pressWith</c> line.
/// </para>
/// <para>
/// This whole surface is Windows-only, and it is absent from no build: there is no switch. What an agent may press
/// is decided by the catalogue in <see cref="DialogCatalogue"/> and by the two <c>--allow-*</c> flags, never by what
/// was compiled in.
/// </para>
/// </remarks>
internal static class UiCommands
{
    public static Command Build()
    {
        var ui = new Command(
            "ui",
            "Read and answer the modal dialog Dorico has up. Windows only: the field `dialog` on every other "
            + "command's payload reports the same thing without a second invocation.");

        ui.Subcommands.Add(BuildDialog());
        ui.Subcommands.Add(BuildPress());
        return ui;
    }

    private static Command BuildDialog()
    {
        var raw = new Option<bool>("--raw")
        {
            Description = "Also return every UI Automation element the read walked, in tree order, with its control "
                + "type, name, automation id, class and bounds.",
        };

        var command = new Command(
            "dialog",
            "Probe Dorico for a modal dialog and print what it says and which buttons it offers. `dialog` is null "
            + "when nothing is modal.")
        {
            raw,
        };

        command.SetAction(parse => CommandRoot.RunAsync("ui dialog", () =>
        {
            var dialog = DialogSurface.Read(parse.GetValue(raw), out var probe);

            return Task.FromResult(new JsonObject
            {
                ["dialog"] = dialog,

                // The probe's own cost, reported whether or not a dialog was found: `dialog: null` has to be a
                // measurement, not an absence of one.
                ["probe"] = probe,

                ["note"] = dialog is null
                    ? "No window of Dorico is modal right now. That is the presence fact only; whether Dorico's "
                        + "command queue is moving is what the `execution` field of a command's payload says."
                    : "Everything here is verbatim: the title, the body text and the button labels are the strings "
                        + "the dialog really shows, in whatever language and however they are encoded.",
            });
        }));

        return command;
    }

    private static Command BuildPress()
    {
        var dialog = new Option<string>("--dialog")
        {
            Description = "The dialog.id to press in, e.g. dorico.export-musicxml. Every payload that reports a "
                + "dialog carries it, and so does `ui dialog`.",
            Required = true,
        };

        var button = new Option<string>("--button")
        {
            Description = "The button id to press, e.g. cancel or accept. `dialog.buttons[].id` lists them; "
                + "`dialog.pressWith` is a ready-to-paste line for the safest one.",
            Required = true,
        };

        var allowDestructive = new Option<bool>("--allow-destructive")
        {
            Description = "Press a button this build does not call safe — discard, overwrite, anything of an "
                + "unknown dialog. Without it the press is refused with DestructiveRefused.",
        };

        var allowUnknown = new Option<bool>("--allow-unknown-dialog")
        {
            Description = "Press a button of a dialog that is not in this build's catalogue. The payload then "
                + "carries a warning, every button of such a dialog is unsafe, so --allow-destructive is needed "
                + "too.",
        };

        var path = new Option<string?>("--path")
        {
            Description = "Write this path into a file dialog's file-name box before pressing, through the UI "
                + "Automation value pattern. Refused with PickerNotSettable when the control will not take it. "
                + "Unverified for a file picker: no file picker has been read yet.",
        };

        var timeout = new Option<int>("--timeout-ms")
        {
            Description = "How long to wait for the named dialog to appear, in milliseconds. 0 probes once.",
            DefaultValueFactory = _ => DialogSurface.DefaultPressTimeoutMs,
        };

        var command = new Command(
            "press",
            "Press one button of the dialog that is up, after re-probing it: the dialog named must be the one that "
            + "is up, or the press refuses with WrongDialog. The payload's `dialog` is what is up afterwards, which "
            + "is the only evidence — a press that closes its own window can report an error after succeeding.")
        {
            dialog,
            button,
            allowDestructive,
            allowUnknown,
            path,
            timeout,
        };

        command.SetAction(parse => CommandRoot.RunAsync("ui press", () => Task.FromResult(DialogSurface.Press(
            parse.GetValue(dialog) ?? string.Empty,
            parse.GetValue(button) ?? string.Empty,
            parse.GetValue(allowUnknown),
            parse.GetValue(allowDestructive),
            parse.GetValue(path),
            Math.Clamp(parse.GetValue(timeout), 0, 120_000)))));

        return command;
    }
}
