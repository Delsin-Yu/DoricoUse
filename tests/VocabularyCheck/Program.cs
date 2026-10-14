using System.Diagnostics;
using System.Text.Json.Nodes;
using DoricoUse.Cli;
using DoricoUse.Cli.Dorico;
using DoricoUse.Cli.Ui;

// A self-check for the built-in enum table: no Dorico, no host, no extracted file, nothing to generate first.
//
//   dotnet run --project tests/VocabularyCheck
//
// Two things are being checked here, and the second matters more than the first:
//   1. the table says what it is supposed to say (a known value is known, a typo is refused with suggestions);
//   2. it refuses *only* what it can speak about — a value for a parameter this build does not know goes to
//      Dorico unchecked, because a spelling nobody here has heard of is far more likely to be newer than wrong.

var failures = 0;

void Check(string name, bool ok, string? detail = null)
{
    if (ok)
    {
        Console.WriteLine($"  ok   {name}");
        return;
    }

    failures++;
    Console.WriteLine($"  FAIL {name}{(detail is null ? string.Empty : ": " + detail)}");
}

Console.WriteLine("== the table itself");
Check("there is always a vocabulary, with nothing to install", EnumVocabulary.Parameters > 0, EnumVocabulary.Parameters.ToString());
Check("and it holds a usable number of values", EnumVocabulary.Spellings >= 30, EnumVocabulary.Spellings.ToString());

foreach (var (key, values) in new[]
{
    ("LogDuration", EnumVocabulary.ValuesForKey("LogDuration")),
    ("NoteInputMode", EnumVocabulary.ValuesForKey("NoteInputMode")),
    ("WindowMode", EnumVocabulary.ValuesForKey("WindowMode")),
    ("RhythmicGridResolutionValue", EnumVocabulary.ValuesForKey("RhythmicGridResolutionValue")),
    ("Type", EnumVocabulary.ValuesForKey("Type", "NoteInput.SetAccidental")),
    ("OptionsType", EnumVocabulary.ValuesForKey("OptionsType")),
})
{
    Check($"{key} is a parameter this build checks", values.Count > 0);
    Check($"every {key} value looks like a Dorico spelling", values.All(EnumVocabulary.IsSpelling), string.Join(',', values));
    Check($"no {key} value is listed twice", values.Distinct(StringComparer.Ordinal).Count() == values.Count);
}

// A command-scoped parameter: `Type` means the seven accidentals to NoteInput.SetAccidental and a property name to
// the two property commands, which is the difference measured 2026-10-13 - `EventEdit.ChangeProperty?Type=...` was
// refused for not being an accidental, and a property name is exactly what that command wants.
Check("Type is fixed by the command that takes it, not by the table", EnumVocabulary.CommandScopedKeys.Contains("Type"));
Check(
    "the accidentals are the set NoteInput.SetAccidental is checked against",
    EnumVocabulary.ValuesForKey("Type", "NoteInput.SetAccidental").Count == 7
        && EnumVocabulary.ValuesForKey("Type", "NoteInput.SetAccidental").Contains("kSharp"));

foreach (var propertyCommand in new[] { "EventEdit.ChangeProperty", "UI.InvokePropertyChangeValue" })
{
    Check(
        $"{propertyCommand}'s Type is a property name and is not checked against the accidentals",
        EnumVocabulary.ValuesForKey("Type", propertyCommand).Count == 0
            && EnumVocabulary.Check("Type", "kNoteSpelling", propertyCommand) == VocabularyVerdict.NotCheckedForCommand);
}

Check(
    "a command this build has not measured Type on is left to Dorico rather than refused",
    EnumVocabulary.Check("Type", "kNoteSpelling", "EventEdit.SomeFutureCommand") == VocabularyVerdict.NotCheckedForCommand
        && EnumVocabulary.Check("Type", "kSharp", "EventEdit.SomeFutureCommand") == VocabularyVerdict.NotCheckedForCommand);
Check(
    "while a misspelt accidental is still unknown for the command that takes one",
    EnumVocabulary.Check("Type", "kShorp", "NoteInput.SetAccidental") == VocabularyVerdict.Unknown
        && EnumVocabulary.Check("Type", "kSharp", "NoteInput.SetAccidental") == VocabularyVerdict.Known);

var propertyRefused = false;

try
{
    // The route the report used to change an event's length: a property name as Type must get through.
    EnumVocabulary.Refuse("--param", "Type", "kNoteSpelling", allowUnknown: false, command: "EventEdit.ChangeProperty");
    EnumVocabulary.Refuse("--param", "Type", "kNoteSpelling", allowUnknown: false, command: "UI.InvokePropertyChangeValue");
}
catch (CliException)
{
    propertyRefused = true;
}

Check("and a property name is not refused for either property command", !propertyRefused);

var accidentalRefused = false;
var accidentalSaid = string.Empty;

try
{
    EnumVocabulary.Refuse("--param", "Type", "kShorp", allowUnknown: false, command: "NoteInput.SetAccidental");
}
catch (CliException ex)
{
    accidentalRefused = true;
    accidentalSaid = ex.Message;
}

Check("while a misspelt accidental is still refused for the command that takes one", accidentalRefused);
Check(
    "and the refusal names the command and keeps the --allow-unknown-enum way out",
    accidentalSaid.Contains("NoteInput.SetAccidental", StringComparison.Ordinal)
        && accidentalSaid.Contains("--allow-unknown-enum", StringComparison.Ordinal),
    accidentalSaid);

Console.WriteLine();
Console.WriteLine("== the table and the code agree (this is what catches drift)");
Check(
    "the note values this CLI resolves are exactly the ones it validates",
    NoteValue.AllSpellings.All(spelling => EnumVocabulary.Check(spelling) == VocabularyVerdict.Known),
    string.Join(',', NoteValue.AllSpellings.Where(s => EnumVocabulary.Check(s) != VocabularyVerdict.Known)));
Check(
    "the ten spellings the binary's own LogDuration template lists are all known",
    NoteValue.Spellings.All(spelling => EnumVocabulary.Check(spelling) == VocabularyVerdict.Known));

foreach (var mode in new[] { "kInsert", "kOverwrite", "kChordMerge" })
{
    Check($"{mode} is a note input mode this build knows", EnumVocabulary.Check(mode) == VocabularyVerdict.Known);
}

foreach (var optionsType in new[]
{
    DoricoProtocol.OptionsEngraving, DoricoProtocol.OptionsLayout, DoricoProtocol.OptionsNotation,
})
{
    Check($"{optionsType} is an option type this build knows", EnumVocabulary.ValuesForKey("OptionsType").Contains(optionsType));
}

foreach (var accidental in new[] { "kNatural", "kSharp", "kFlat", "kDoubleSharp", "kDoubleFlat" })
{
    Check($"{accidental} is an accidental this build derives and knows", EnumVocabulary.Check(accidental) == VocabularyVerdict.Known);
}

Console.WriteLine();
Console.WriteLine("== what a value's shape means");
Check("a known spelling is known", EnumVocabulary.Check("kQuaver") == VocabularyVerdict.Known);
Check("a misspelling is unknown", EnumVocabulary.Check("kQuavor") == VocabularyVerdict.Unknown);
Check("a written-out note value is not a spelling", EnumVocabulary.Check("crotchet") == VocabularyVerdict.NotASpelling);
Check("a fraction is not a spelling", EnumVocabulary.Check("1/4") == VocabularyVerdict.NotASpelling);
Check("a file path is not a spelling", EnumVocabulary.Check(@"C:\scores\a.dorico") == VocabularyVerdict.NotASpelling);
Check("lower case after k is not a spelling", !EnumVocabulary.IsSpelling("kquaver"));
Check("a bare k is not a spelling", !EnumVocabulary.IsSpelling("k"));
Check("kWriteMode is a spelling", EnumVocabulary.IsSpelling("kWriteMode"));
Check("kd is not a spelling (no capital after k)", !EnumVocabulary.IsSpelling("kd"));
Check("a value with a space is not a spelling", !EnumVocabulary.IsSpelling("kWrite Mode"));

Console.WriteLine();
Console.WriteLine("== refusal: only for a parameter this build knows");
var refused = false;

try
{
    EnumVocabulary.Refuse("--duration", "LogDuration", "kQuavor");
}
catch (CliException ex)
{
    refused = true;
    Check("a typo in a known parameter is refused with InvalidArgument", ex.Code == "InvalidArgument", ex.Code);
    Check("and the exit code says it was the arguments' fault", ex.ExitCode == 1, ex.ExitCode.ToString());

    var data = ex.Details;
    Check("and error.data carries the parameter", NodeRead.Str(data, "key") == "LogDuration");
    Check(
        "and the nearest spelling",
        (NodeRead.Arr(data, "suggestions") ?? []).Select(item => NodeRead.Text(item)).Contains("kQuaver"),
        ex.Message[..Math.Min(120, ex.Message.Length)]);
    Check("and the message names the value and the parameter", ex.Message.Contains("kQuavor") && ex.Message.Contains("LogDuration"));
    Check("and it says how to override it", ex.Message.Contains("--allow-unknown-enum"));
}

Check("a refused typo throws", refused);

// `Suggest` answers with an indexable list, so its first item is read through the indexer rather than with a LINQ
// call over a collection that already has one; this keeps the "nothing to suggest reads as null" behaviour.
static string? FirstOrNull(IReadOnlyList<string> values) => values.Count > 0 ? values[0] : null;

// The control types an action may target, spelled out so the list is visible in one place rather than implied by
// IsActionable's own body.
int[] actionableControlTypes = [50000, 50002, 50003, 50004, 50007, 50011, 50013, 50019, 50024, 50029, 50031];

// The CLI error a call threw, or null when it threw nothing: every refusal in this section is asserted by its
// code and its exit code, not by the sentence it carries.
static CliException? Refusal(Action action)
{
    try
    {
        action();
        return null;
    }
    catch (CliException ex)
    {
        return ex;
    }
}

static bool Throws(Action action)
{
    try
    {
        action();
        return false;
    }
    catch (CliException)
    {
        return true;
    }
}

Check("the value the parameter really takes is not refused", !Throws(() => EnumVocabulary.Refuse("--duration", "LogDuration", "kCrotchet")));
Check("a number is not refused", !Throws(() => EnumVocabulary.Refuse("--param", "LogDuration", "4")));
Check("an unknown parameter is never refused", !Throws(() => EnumVocabulary.Refuse("--param", "SomeParameterFromANewerDorico", "kSomethingNew")));
Check("no parameter at all is never refused", !Throws(() => EnumVocabulary.Refuse("--value", null, "kSomethingNew")));
Check("--allow-unknown-enum overrides the refusal", !Throws(() => EnumVocabulary.Refuse("--duration", "LogDuration", "kQuavor", allowUnknown: true)));

Console.WriteLine();
Console.WriteLine("== suggestions");
Check("a typo points at the value it meant", FirstOrNull(EnumVocabulary.Suggest("kQuavor", "LogDuration")) == "kQuaver", string.Join(',', EnumVocabulary.Suggest("kQuavor", "LogDuration")));
Check("a typo in another parameter's value still finds it", EnumVocabulary.Suggest("kSemibrefe").Contains("kSemibreve"), string.Join(',', EnumVocabulary.Suggest("kSemibrefe")));
Check("the parameter's own values come first", FirstOrNull(EnumVocabulary.Suggest("kWriteMod", "WindowMode")) == "kWriteMode", string.Join(',', EnumVocabulary.Suggest("kWriteMod", "WindowMode")));
Check("a value with nothing near it suggests nothing", EnumVocabulary.Suggest("kZzzzzzzz").Count == 0, string.Join(',', EnumVocabulary.Suggest("kZzzzzzzz")));

Console.WriteLine();
Console.WriteLine("== what a payload reports");
var described = EnumVocabulary.Describe([("LogDuration", "kCrotchet", EnumVocabulary.Check("kCrotchet"))]);
Check("the report says the table is built in", NodeRead.Str(described, "source") == "built in");
Check("it counts its parameters and values", NodeRead.Int(described, "parameters") == EnumVocabulary.Parameters && NodeRead.Int(described, "values") == EnumVocabulary.Spellings);
Check("it says what is checked and what is not", (NodeRead.Str(described, "note") ?? string.Empty).Contains("sent unchecked"));
Check("and a looked-at value carries its verdict", NodeRead.Str(NodeRead.Arr(described, "lookedAt")?[0], "verdict") == "known");

Console.WriteLine();
Console.WriteLine("== the scripting surface: refused unless the build was compiled to expose it");
// The same source is compiled twice with different constants: this project ordinarily (the surface is off, which
// is what a released binary is) and once in ci.yml with -p:ExposeUnsafeScriptRunApi=true (the surface is on).
// Both runs must exit 0, so each direction asserts its own facts and nothing here needs Dorico either way.
#if EXPOSE_UNSAFE_SCRIPT_RUN_API
Check("this build was compiled with the scripting surface exposed", UnsafeSurface.ScriptRunExposed);
Check("so a Script.* command is not refused", !Throws(() => UnsafeSurface.RefuseCommand("Script.RunScript")));
Check("and neither is any other name in the family", !Throws(() => UnsafeSurface.RefuseCommand("script.foo")));
Check("and a name outside the family is still untouched", !Throws(() => UnsafeSurface.RefuseCommand("NoteInput.Enter")));
#else
Check("this build was not compiled with the scripting surface exposed", !UnsafeSurface.ScriptRunExposed);

var scriptingRefused = default(CliException);

try
{
    UnsafeSurface.RefuseCommand("Script.RunScript");
}
catch (CliException ex)
{
    scriptingRefused = ex;
}

var refusalMessage = scriptingRefused?.Message ?? string.Empty;

Check("a Script.* command is refused", scriptingRefused is not null);
Check("with the code a caller switches on", scriptingRefused?.Code == "ScriptingDisabled", scriptingRefused?.Code ?? "nothing was thrown");
Check("and exit code 1, because it is the call's fault, not Dorico's", scriptingRefused?.ExitCode == 1, scriptingRefused?.ExitCode.ToString());
Check("and error.data names the command that was refused", NodeRead.Str(scriptingRefused?.Details, "command") == "Script.RunScript", NodeRead.Str(scriptingRefused?.Details, "command"));
Check("and error.data carries the reason in one line", !string.IsNullOrWhiteSpace(NodeRead.Str(scriptingRefused?.Details, "reason")), NodeRead.Str(scriptingRefused?.Details, "reason"));
Check("and the message says this build does not expose the layer", refusalMessage.Contains("does not expose"), refusalMessage[..Math.Min(120, refusalMessage.Length)]);
Check("and points at the record of what happened", refusalMessage.Contains("docs/scripting.md"));
Check("and does not tell the reader how to turn it back on", !refusalMessage.Contains("ExposeUnsafeScriptRunApi"), refusalMessage[..Math.Min(120, refusalMessage.Length)]);
Check("the family is matched case-insensitively, as command names are", Throws(() => UnsafeSurface.RefuseCommand("script.foo")));

var upperCaseRefused = default(CliException);

try
{
    UnsafeSurface.RefuseCommand("SCRIPT.Foo");
}
catch (CliException ex)
{
    upperCaseRefused = ex;
}

Check("an all-caps family is refused the same way", upperCaseRefused?.Code == "ScriptingDisabled", upperCaseRefused?.Code ?? "nothing was thrown");
Check("and the refusal keeps the name exactly as it was typed", NodeRead.Str(upperCaseRefused?.Details, "command") == "SCRIPT.Foo", NodeRead.Str(upperCaseRefused?.Details, "command"));
Check("a name outside the family is never refused", !Throws(() => UnsafeSurface.RefuseCommand("NoteInput.Enter")));
Check("so is any other catalogue command, Script. is the whole of it", !Throws(() => UnsafeSurface.RefuseCommand("File.Open")));
#endif


Console.WriteLine();
Console.WriteLine("== a dialog the catalogue knows: the ids come from the automation id's tail");

// One handle stands in for the window every fixture below is, so that the payload's `hwnd` is asserted against a
// value the check wrote down rather than against whatever the formatter happened to produce.
const nint DialogHandle = 0x006C1132;

// A stand-in for what was measured on the MusicXML export dialog, one fact per line: two buttons share the label
// 选择全部, one button's label is a private-use glyph, and every button carries a QML automation id whose last
// segment is the meaningful part. This is the input the whole id scheme exists for.
DialogInput Export() => new(
    Title: "导出MusicXML",
    WindowClass: "Qt6101QWindow",
    Owner: "0x006C1132",
    TextSource: "uia",
    TextLines: ["导出到哪里", "格式"],
    Buttons:
    [
        new("选择全部", "DoricoQApplication.root.QMLTYPE_1.Button_QMLTYPE_53.flowsSelectAllButton", true, 100, 200),
        new("全部不选", "DoricoQApplication.root.QMLTYPE_1.Button_QMLTYPE_54.flowsSelectNoneButton", true, 180, 200),
        new("选择全部", "DoricoQApplication.root.QMLTYPE_1.Button_QMLTYPE_55.layoutsSelectAllButton", true, 100, 240),
        new("全部不选", "DoricoQApplication.root.QMLTYPE_1.Button_QMLTYPE_56.layoutsSelectNoneButton", true, 180, 240),
        new("\ue0c8", "DoricoQApplication.root.QMLTYPE_1.Button_QMLTYPE_57.chooseFolderPathPushButton", true, 100, 280),
        new("确定", "DoricoQApplication.root.buttonBoxFrame.buttonBox.QPushButton", true, 300, 320),
        new("取消", "DoricoQApplication.root.buttonBoxFrame.buttonBox.QPushButton", true, 380, 320),
    ]);

// Two buttons with unique labels and no automation id at all: the plain case a dialog usually is.
DialogInput Stranger() => new(
    Title: "Something Else",
    WindowClass: "Qt6101QWindow",
    Owner: null,
    TextSource: "none",
    TextLines: [],
    Buttons: [new("Yes", null, true, 10, 10), new("No", null, true, 90, 10)]);

// The native folder picker, measured: class #32770, accept is the classic control id 1, cancel is 2.
DialogInput Native() => new(
    Title: "选择文件夹",
    WindowClass: "#32770",
    Owner: "0x006C1132",
    TextSource: "uia",
    TextLines: ["地址: C:\\scores"],
    Buttons: [new("选择", "1", true, 400, 500), new("取消", "2", true, 480, 500)]);

var export = DialogModel.Describe(Export());

Check("the dialog is the catalogued one", export.Id == "dorico.export-musicxml", export.Id);
Check("and it is reported as known", export.Kind == DialogModel.KindKnown, export.Kind);
Check("its shape is the Qt one", export.Shape == DialogModel.ShapeQt, export.Shape);
Check("its class and owner are reported verbatim", export.WindowClass == "Qt6101QWindow" && export.Owner == "0x006C1132");
Check("a Qt dialog window is a blocking class", export.BlocksQueue);
Check("the body text is the dialog's own, joined with newlines", export.Description == "导出到哪里\n格式", export.Description);
Check("and the source of that text is named", export.TextSource == "uia", export.TextSource);
Check("every button is reported", export.Buttons.Count == 7, export.Buttons.Count.ToString());
Check(
    "the two buttons that share a label are told apart by their automation id",
    export.Buttons[0].Id == "flowsSelectAllButton" && export.Buttons[2].Id == "layoutsSelectAllButton",
    $"{export.Buttons[0].Id} / {export.Buttons[2].Id}");
Check(
    "the button with no label is still addressable",
    export.Buttons[4].Id == "folder-picker" && export.Buttons[4].Name == "\ue0c8",
    $"{export.Buttons[4].Id} / {export.Buttons[4].Name}");
Check("the catalogue names the accept button", export.Buttons[5].Id == "accept" && export.Buttons[5].Role == DialogModel.RoleDefault);
Check("the catalogue names the cancel button", export.Buttons[6].Id == "cancel" && export.Buttons[6].Role == DialogModel.RoleDismiss);
Check(
    "an alias exists only where the catalogue maps one",
    export.Buttons[0].Alias is null && export.Buttons[5].Alias == "yes" && export.Buttons[6].Alias == "no");
Check("an uncatalogued button of a known dialog is not safe", !export.Buttons[0].Safe && !export.Buttons[4].Safe);
Check("the reported index is the order the reader found them in", export.Buttons.Select(button => button.Index).SequenceEqual(Enumerable.Range(0, 7)));
Check(
    "pressWith points at the safest button, and is a line an agent can paste",
    export.PressWith == "dorico-use ui press --dialog dorico.export-musicxml --button cancel",
    export.PressWith);

Console.WriteLine();
Console.WriteLine("== ids are derived, and they are stable");

Check(
    "the same dialog describes identically three times",
    DialogModel.Describe(Export()).Id == export.Id && DialogModel.Describe(Export()).Id == export.Id);
Check(
    "and so does every one of its button ids",
    DialogModel.Describe(Export()).Buttons.Select(button => button.Id).SequenceEqual(export.Buttons.Select(button => button.Id)));

var stranger = DialogModel.Describe(Stranger());

Check("an unknown dialog gets a derived id", stranger.Id.StartsWith("unknown.", StringComparison.Ordinal), stranger.Id);
Check("twelve hex digits of the hash, and nothing else", stranger.Id.Length == 20 && stranger.Id[8..].All(Uri.IsHexDigit), stranger.Id);
Check("the same unknown dialog keeps its id", stranger.Id == DialogModel.Describe(Stranger()).Id, stranger.Id);
Check("an unknown dialog is reported as unknown", stranger.Kind == DialogModel.KindUnknown);
Check("every button of an unknown dialog is unsafe", stranger.Buttons.All(button => !button.Safe));
Check("and has no role and no alias", stranger.Buttons.All(button => button.Role == DialogModel.RoleUnknown && button.Alias is null));
Check("so there is no pressWith to paste", stranger.PressWith is null, stranger.PressWith);
Check("a different caption gets a different id", DialogModel.Describe(Stranger() with { Title = "Another One" }).Id != stranger.Id);
Check(
    "and so does the same caption with different buttons",
    DialogModel.Describe(Stranger() with
    {
        Buttons = [new("Yes", null, true, 10, 10), new("No", null, true, 90, 10), new("Maybe", null, true, 170, 10)],
    }).Id != stranger.Id);

Console.WriteLine();
Console.WriteLine("== the id rules, one at a time");

var native = DialogModel.Describe(Native());

Check("a #32770 window is the native shape", native.Shape == DialogModel.ShapeNative, native.Shape);
Check("and a native dialog is a blocking class", native.BlocksQueue);
Check("the classic control ids name the buttons", native.Buttons[0].Id == "accept" && native.Buttons[1].Id == "cancel");
Check("with the aliases the catalogue maps", native.Buttons[0].Alias == "yes" && native.Buttons[1].Alias == "no");
Check("and the labels are reported as the dialog shows them", native.Buttons[0].Name == "选择");

var unmeasuredNative = DialogModel.Describe(Native() with
{
    Title = "Open",
    Buttons = [new("Open", "1", true, 400, 500), new("Cancel", "2", true, 480, 500)],
});

Check("an unmeasured native dialog is still recognized by its class", unmeasuredNative.Shape == DialogModel.ShapeNative);
Check("but its buttons claim nothing", unmeasuredNative.Buttons.All(button => !button.Safe && button.Alias is null));

var stray = DialogModel.Describe(Native() with
{
    Title = "Unmeasured",
    Buttons = [new("Help", "9", true, 10, 10), new("Options", "1001", true, 90, 10)],
});

Check("a classic id this build does not know is still stable", stray.Buttons[0].Id == "win-9", stray.Buttons[0].Id);
Check("and so is a bigger one", stray.Buttons[1].Id == "win-1001", stray.Buttons[1].Id);

var byLabel = DialogModel.Describe(Stranger() with
{
    Title = "Unmeasured label case",
    Buttons = [new("Apply", null, true, 10, 10), new("Reset", null, true, 90, 10)],
});

Check("a unique label becomes the id when there is no automation id", byLabel.Buttons[0].Id == "apply" && byLabel.Buttons[1].Id == "reset", string.Join(',', byLabel.Buttons.Select(button => button.Id)));

var byPosition = DialogModel.Describe(Stranger() with
{
    Title = "Unmeasured position case",
    Buttons = [new("OK", null, true, 40, 100), new("OK", null, true, 300, 100), new("OK", null, true, 40, 200)],
});

Check(
    "a label used three times falls through to the position, left to right and then top to bottom",
    byPosition.Buttons[0].Id == "position-1" && byPosition.Buttons[1].Id == "position-2" && byPosition.Buttons[2].Id == "position-3",
    string.Join(',', byPosition.Buttons.Select(button => button.Id)));

var byGlyph = DialogModel.Describe(Stranger() with
{
    Title = "Unmeasured glyph case",
    Buttons = [new("\ue0c8", null, true, 10, 10), new("\ue0c9", null, true, 90, 10)],
});

Check(
    "a label that is a private-use glyph is never an id",
    byGlyph.Buttons.All(button => button.Id.StartsWith("position-", StringComparison.Ordinal)),
    string.Join(',', byGlyph.Buttons.Select(button => button.Id)));

Check("a modeless window class is not called blocking", !DialogModel.Describe(Stranger() with { WindowClass = "Qt6101QWindowIcon" }).BlocksQueue);
Check("and it carries no queue sentence", NodeRead.Str(DialogModel.Payload(DialogModel.Describe(Stranger() with { WindowClass = "Qt6101QWindowIcon" }), new DialogProbeInfo("win32+uia", 3), DialogHandle), "note") is null);

Console.WriteLine();
Console.WriteLine("== the policy: what may be pressed, and what it says when it may not");

Check("a catalogued, safe button may be pressed", DialogModel.Authorize(export, "cancel", allowUnknownDialog: false, allowDestructive: false).Id == "cancel");

var unknownDialog = Refusal(() => DialogModel.Authorize(stranger, "yes", allowUnknownDialog: false, allowDestructive: false));

Check("an unknown dialog is refused", unknownDialog?.Code == "UnknownDialog", unknownDialog?.Code ?? "nothing was thrown");
Check("and the refusal is the caller's fault, so exit 1", unknownDialog?.ExitCode == DialogModel.RefusalExitCode, unknownDialog?.ExitCode.ToString());
Check("and error.data names the dialog it refused", NodeRead.Str(unknownDialog?.Details, "dialog") == stranger.Id);
Check("and lists the ids it did have", (NodeRead.Arr(unknownDialog?.Details, "ids") ?? []).Count == 2);

var stillDestructive = Refusal(() => DialogModel.Authorize(stranger, "yes", allowUnknownDialog: true, allowDestructive: false));

Check(
    "allowing an unknown dialog is not enough on its own: no button of it is safe",
    stillDestructive?.Code == "DestructiveRefused",
    stillDestructive?.Code ?? "nothing was thrown");
Check(
    "and its message says which two flags are needed",
    stillDestructive?.Message.Contains("--allow-unknown-dialog") == true
        && stillDestructive?.Message.Contains("--allow-destructive") == true);

Check("both flags together let it through", DialogModel.Authorize(stranger, "yes", allowUnknownDialog: true, allowDestructive: true).Id == "yes");

var notThere = Refusal(() => DialogModel.Authorize(export, "no-such-button", allowUnknownDialog: true, allowDestructive: true));

Check("an id that is not in the dialog is refused", notThere?.Code == "ButtonNotFound", notThere?.Code ?? "nothing was thrown");
Check("and error.data lists the ids that are there", (NodeRead.Arr(notThere?.Details, "ids") ?? []).Any(id => NodeRead.Text(id) == "cancel"));

var destructive = Refusal(() => DialogModel.Authorize(export, "folder-picker", allowUnknownDialog: true, allowDestructive: false));

Check("a button the catalogue does not call safe is refused", destructive?.Code == "DestructiveRefused", destructive?.Code ?? "nothing was thrown");
Check("and error.data carries the role it was refused for", NodeRead.Str(destructive?.Details, "role") == DialogModel.RoleUnknown);

var withoutPattern = DialogModel.Describe(Export() with
{
    Buttons =
    [
        new("确定", "DoricoQApplication.root.buttonBoxFrame.buttonBox.QPushButton", true),
        new("取消", "DoricoQApplication.root.buttonBoxFrame.buttonBox.QPushButton2", false),
    ],
});

var dead = Refusal(() => DialogModel.Authorize(withoutPattern, "cancel", allowUnknownDialog: false, allowDestructive: false));

Check("a button with no pressable pattern is refused", dead?.Code == "ButtonNotInvokable", dead?.Code ?? "nothing was thrown");
Check("and error.data names the button", NodeRead.Str(dead?.Details, "button") == "cancel");

// Measured 2026-10-13: `ui press` on an element whose UI Automation IsEnabled is false answered ok: true with an
// invoke that did nothing. The window says so itself - any accessibility reader shows `button "应用"
// [disabled]` from the same dialog - so the tool reads the same fact and refuses on it. The negative case is the
// point of the check: a disabled target must not be pressed, whatever the catalogue thinks of it.
var disabledDialog = DialogModel.Describe(Export() with
{
    Buttons =
    [
        new("应用", "DoricoQApplication.ScoreInfoDialog.applyButton", true, 10, 10, Enabled: false),
        new("关闭", "DoricoQApplication.ScoreInfoDialog.closeButton", true, 90, 10),
    ],
});

var disabled = Refusal(() => DialogModel.Authorize(disabledDialog, "applyButton", allowUnknownDialog: true, allowDestructive: true));

Check(
    "a disabled button is refused even when every flag that would let it through is passed",
    disabled?.Code == PanelModel.DisabledCode,
    disabled?.Code ?? "nothing was thrown");
Check("and the refusal is the caller's fault, so exit 1", disabled?.ExitCode == 1, disabled?.ExitCode.ToString());
Check("and error.data says the element is disabled", NodeRead.Bool(disabled?.Details, "enabled") == false);
Check("and names the element", NodeRead.Str(disabled?.Details, "element") == "applyButton");
Check("and the enabled button beside it still may be pressed", DialogModel.Authorize(disabledDialog, "closeButton", allowUnknownDialog: true, allowDestructive: true).Id == "closeButton");

// The same rule for a panel element and for a dialog's controls, because one code and one sentence have to come out
// wherever the disabled element lives.
Check(
    "the same refusal is built for a panel element",
    PanelModel.RefuseDisabled("dorico.document-window", "applyButton", "应用", "button").Code == PanelModel.DisabledCode
        && PanelModel.RefuseDisabled("dorico.document-window", "applyButton", "应用", "button").ExitCode == 1);
Check(
    "and --commit-input is only for a text field, which is a rule and not a click",
    PanelModel.CommitInputApplicable(PanelAction.Value)
        && !PanelModel.CommitInputApplicable(PanelAction.Invoke)
        && !PanelModel.CommitInputApplicable(PanelAction.Select)
        && PanelModel.RefuseCommitInput(
            "dorico.document-window",
            PanelModel.Describe([new PanelElementInput(ControlTypes.ListItem, "Piccolo", null, null, null, true, null, false)])[0]).Code
            == "InvalidArgument");

Console.WriteLine();
Console.WriteLine("== the refusal codes and the exit codes they promise");

foreach (var code in new[]
{
    "DialogNotFound", "WrongDialog", "ButtonNotFound", "ButtonNotInvokable", "UnknownDialog", "DestructiveRefused", "PickerNotSettable",
    "ElementDisabled",
})
{
    Check($"{code} is a refusal this build defines", DialogModel.RefusalCodes.Contains(code));
}

Check("and there are no others", DialogModel.RefusalCodes.Count == 8, string.Join(',', DialogModel.RefusalCodes));
Check("a refusal exits 1, because it is the caller's fault", DialogModel.RefusalExitCode == 1);
Check("a probe failure exits 2, because it is not", DialogModel.ProbeFailureExitCode == 2);

Console.WriteLine();
Console.WriteLine("== the catalogue: one entry, one measurement, one policy");

foreach (var entry in DialogCatalogue.Entries)
{
    Check(
        $"{entry.Id} has a matcher",
        entry.TitleGlobs.Count > 0
            || entry.RequiredButtonLabels.Count > 0
            || entry.WindowClasses is { Count: > 0 }
            || entry.RequiredBodyText is { Count: > 0 }
            || entry.RequiredAutomationIds is { Count: > 0 });
    Check($"{entry.Id} says what was measured", entry.Note.Length > 40);
    Check($"{entry.Id} is a measured entry", entry.Measured);
    Check($"{entry.Id} has buttons", entry.Buttons.Count > 0);
    Check(
        $"{entry.Id}'s required labels are all labels of its own buttons",
        entry.RequiredButtonLabels.All(label => entry.Buttons.Any(button => button.MatchLabels.Contains(label, StringComparer.Ordinal))));

    foreach (var button in entry.Buttons)
    {
        Check(
            $"{entry.Id}/{button.Id} has a role this build knows",
            button.Role is DialogModel.RoleDefault or DialogModel.RoleDismiss or DialogModel.RoleDestructive or DialogModel.RoleUnknown);
        Check($"{entry.Id}/{button.Id} has an alias from the list, or none", button.Alias is null or "yes" or "no" or "ok" or "cancel" or "discard");
        Check(
            $"{entry.Id}/{button.Id} has something that identifies it",
            button.MatchLabels.Count > 0 || button.MatchAutomationIdTails.Count > 0 || button.MatchClassicIds.Count > 0);

        if (button.Role == DialogModel.RoleDestructive)
        {
            Check($"{entry.Id}/{button.Id} is destructive and therefore not safe", !button.Safe);
        }
    }

    // A dialog built out of the entry itself must be recognised as that entry, which is what "the table and the
    // matcher agree" means: the labels the entry requires are the labels its buttons carry, and every other fact the
    // entry claims — the class, the body text, the automation-id path — is a fact the dialog it describes therefore
    // shows. A criterion the entry does not declare is not invented here either.
    var buttons = entry.Buttons
        .Select(button => new DialogButtonInput(
            button.MatchLabels.Count > 0 ? button.MatchLabels[0] : "unlabelled",
            button.MatchClassicIds.Count > 0
                ? button.MatchClassicIds[0]
                : button.MatchAutomationIdTails.Count > 0
                    ? "DoricoQApplication.root.QMLTYPE_1.Button_QMLTYPE_53." + button.MatchAutomationIdTails[0]
                    : button.MatchLabels.Count > 0 && entry.RequiredAutomationIds is { Count: > 0 } paths
                        ? "DoricoQApplication.root.root." + paths[0]
                        : null,
            true))
        .ToList();

    var matched = DialogModel.Describe(new DialogInput(
        Title: entry.TitleGlobs.Count > 0 ? entry.TitleGlobs[0].Replace("*", "x", StringComparison.Ordinal) : "a caption this entry does not constrain",
        WindowClass: entry.WindowClasses is { Count: > 0 } classes
            ? classes[0]
            : entry.Id == "dorico.folder-picker" ? "#32770" : "Qt6101QWindow",
        Owner: null,
        TextSource: "none",
        TextLines: entry.RequiredBodyText is { Count: > 0 } body ? [.. body] : [],
        Buttons: buttons));

    Check($"{entry.Id} recognises the dialog it describes", matched.Id == entry.Id && matched.Kind == DialogModel.KindKnown, matched.Id);
    Check(
        $"{entry.Id} reports every button it knows",
        entry.Buttons.All(button => matched.Buttons.Any(reported => reported.Id == button.Id)),
        string.Join(',', matched.Buttons.Select(reported => reported.Id)));

    var safe = matched.Buttons.FirstOrDefault(button => button.Safe);

    if (safe is not null)
    {
        Check($"{entry.Id} can be pressed without a flag", DialogModel.Authorize(matched, safe.Id, false, false).Id == safe.Id);
        Check(
            $"{entry.Id} offers a pressWith an agent can paste",
            matched.PressWith is not null && matched.PressWith.StartsWith("dorico-use ui press --dialog " + entry.Id, StringComparison.Ordinal),
            matched.PressWith);
    }
}

Check(
    "no entry was written for a dialog nobody has read",
    DialogCatalogue.Reserved.All(id => DialogCatalogue.Entries.All(entry => entry.Id != id)),
    string.Join(',', DialogCatalogue.Entries.Select(entry => entry.Id)));
Check(
    "every catalogued id is distinct",
    DialogCatalogue.Entries.Select(entry => entry.Id).Distinct(StringComparer.Ordinal).Count() == DialogCatalogue.Entries.Count);

Console.WriteLine();
Console.WriteLine("== a caption is matched exactly unless it is written with a wildcard");

Check("the measured caption matches itself", DialogCatalogue.Glob("导出MusicXML", "导出MusicXML"));
Check("the case does not matter", DialogCatalogue.Glob("export musicxml", "Export MusicXML"));
Check(
    "a caption that merely contains the measured one does NOT match, which is the safety property",
    !DialogCatalogue.Glob("导出MusicXML 选项", "导出MusicXML"));
Check("a wildcard still works", DialogCatalogue.Glob("导出MusicXML 选项", "*MusicXML*"));
Check("a leading wildcard anchors at the end", !DialogCatalogue.Glob("导出MusicXML 选项", "*MusicXML"));
Check("a trailing wildcard anchors at the start", DialogCatalogue.Glob("导出MusicXML 选项", "导出MusicXML*"));

Console.WriteLine();
Console.WriteLine("== the error box: a caption that is not an identity, and the three facts that are");

// Measured, captured verbatim from a real payload: Dorico's error message box is class Qt6101QWindowIcon, its caption
// is the bare word Dorico — which a second window of the same session carries too — its body says 错误发生, and its
// two buttons 关闭 and 确定 share the automation id path buttonBox.QPushButton. The entry was written from those
// facts and nothing else, so this is the fingerprint, and every case below takes one of its facts away again.
DialogInput ErrorBox() => new(
    Title: "Dorico",
    WindowClass: "Qt6101QWindowIcon",
    Owner: null,
    TextSource: "uia",
    TextLines: ["错误发生"],
    Buttons:
    [
        new("关闭", "DoricoQApplication.root.buttonBox.QPushButton", true, 300, 320),
        new("确定", "DoricoQApplication.root.buttonBox.QPushButton", true, 380, 320),
    ]);

// A window that shares the caption and nothing else: the one the entry must not claim.
DialogInput Decoy() => ErrorBox() with
{
    WindowClass = "Qt6101QWindow",
    TextLines = [],
    Buttons =
    [
        new("关闭", "DoricoQApplication.root.closePushButton", true, 300, 320),
        new("确定", "DoricoQApplication.root.okPushButton", true, 380, 320),
    ],
};

var errorBox = DialogModel.Describe(ErrorBox());
var errorEntry = DialogCatalogue.Entries.FirstOrDefault(entry => entry.Id == "dorico.error");

Check("the error box is catalogued now that it has been read", errorEntry is not null && errorEntry.Measured);
Check("and its entry declares each measured fact", errorEntry!.WindowClasses?.Contains("Qt6101QWindowIcon") == true
    && errorEntry.RequiredBodyText?.Contains("错误发生") == true
    && errorEntry.RequiredAutomationIds?.Contains("buttonBox.QPushButton") == true,
    $"{string.Join(',', errorEntry.WindowClasses ?? [])} | {string.Join(',', errorEntry.RequiredBodyText ?? [])} | {string.Join(',', errorEntry.RequiredAutomationIds ?? [])}");
Check("so it is no longer an unknown dialog", errorBox.Id == "dorico.error" && errorBox.Kind == DialogModel.KindKnown, errorBox.Id);
Check(
    "and its buttons get the catalogue's own ids rather than the positional ones the unmeasured read produced",
    errorBox.Buttons.Select(button => button.Id).SequenceEqual(["dismiss", "ok"]),
    string.Join(',', errorBox.Buttons.Select(button => button.Id)));
Check("its body text is reported verbatim", errorBox.Description == "错误发生", errorBox.Description);
Check("both buttons are safe: an error report's 关闭 and 确定 only close it", errorBox.Buttons.All(button => button.Safe));
Check(
    "and pressWith offers the dismiss line, which needs no flag at all",
    errorBox.PressWith == "dorico-use ui press --dialog dorico.error --button dismiss",
    errorBox.PressWith);

// The measurement the entry exists for: another window of the same class in the same session is also called Dorico,
// so a matcher that stopped at the caption would claim it, and pressing a button in the wrong window is the one
// thing this table exists to prevent. Each of the other three facts is taken away in turn.
var decoy = DialogModel.Describe(Decoy());

Check("a window that only shares the caption Dorico is NOT the error box", decoy.Id != "dorico.error", decoy.Id);
Check("it is reported as unknown, and every one of its buttons is unsafe", decoy.Kind == DialogModel.KindUnknown && decoy.Buttons.All(button => !button.Safe));
Check("so answering it still needs both flags", Refusal(() => DialogModel.Authorize(decoy, decoy.Buttons[0].Id, false, false))?.Code == "UnknownDialog");
Check(
    "the class alone does not identify it either",
    DialogModel.Describe(ErrorBox() with { TextLines = [] }).Id != "dorico.error");
Check(
    "nor does the caption plus the body text in another window class",
    DialogModel.Describe(ErrorBox() with { WindowClass = "Qt6101QWindow" }).Id != "dorico.error");
Check(
    "nor a Dorico-titled QtIcon box whose body says something else",
    DialogModel.Describe(ErrorBox() with { TextLines = ["保存失败"] }).Id != "dorico.error");
Check(
    "nor one whose 关闭 and 确定 carry no button-box path",
    DialogModel.Describe(Decoy() with { WindowClass = "Qt6101QWindowIcon", TextLines = ["错误发生"] }).Id != "dorico.error");
Check(
    "and every fact counts, including the caption: all four hold or the entry lets go",
    DialogModel.Describe(ErrorBox() with { Title = "另一个窗口" }).Id != "dorico.error");

Console.WriteLine();
Console.WriteLine("== the payload an agent reads");

var exportPayload = DialogModel.Payload(export, new DialogProbeInfo("win32+uia", 12, Nodes: 41), DialogHandle, new DialogOccurrence(OpenMs: 4210, Instance: 1));

Check(
    "the dialog's own handle is there, in the panel payload's own spelling",
    NodeRead.Str(exportPayload, "hwnd") == "0x006C1132",
    NodeRead.Str(exportPayload, "hwnd"));
Check(
    "and the three window fields sit together, right after kind and shape",
    exportPayload.Select(pair => pair.Key).Take(6).SequenceEqual(["id", "kind", "shape", "hwnd", "openMs", "instance"]),
    string.Join(',', exportPayload.Select(pair => pair.Key).Take(6)));
Check(
    "how long the window has been up, and which occurrence of its handle it is",
    NodeRead.Long(exportPayload, "openMs") == 4210 && NodeRead.Int(exportPayload, "instance") == 1,
    $"{NodeRead.Long(exportPayload, "openMs")} / {NodeRead.Int(exportPayload, "instance")}");

Check(
    "id, kind, shape, class, owner and title are there",
    NodeRead.Str(exportPayload, "id") == "dorico.export-musicxml"
        && NodeRead.Str(exportPayload, "kind") == "known"
        && NodeRead.Str(exportPayload, "shape") == "qt"
        && NodeRead.Str(exportPayload, "class") == "Qt6101QWindow"
        && NodeRead.Str(exportPayload, "owner") == "0x006C1132"
        && NodeRead.Str(exportPayload, "title") == "导出MusicXML");
Check("blocksQueue is a boolean hint", NodeRead.Bool(exportPayload, "blocksQueue") == true);
Check("the body text and its source are there", NodeRead.Str(exportPayload, "description") == "导出到哪里\n格式" && NodeRead.Str(exportPayload, "textSource") == "uia");
Check("pressWith is the ready-to-paste line", NodeRead.Str(exportPayload, "pressWith") == export.PressWith);

var reportedButtons = NodeRead.Arr(exportPayload, "buttons");

Check("every button is an object", reportedButtons?.Count == 7, reportedButtons?.Count.ToString());
Check(
    "a button carries its id, name, automation id, index, role, safe and invokable",
    NodeRead.Str(reportedButtons?[6], "id") == "cancel"
        && NodeRead.Str(reportedButtons?[6], "name") == "取消"
        && NodeRead.Int(reportedButtons?[6], "index") == 6
        && NodeRead.Str(reportedButtons?[6], "role") == "dismiss"
        && NodeRead.Bool(reportedButtons?[6], "safe") == true
        && NodeRead.Bool(reportedButtons?[6], "invokable") == true
        && NodeRead.Str(reportedButtons?[6], "automationId") == "DoricoQApplication.root.buttonBoxFrame.buttonBox.QPushButton");
Check("and an alias only where the catalogue maps one", NodeRead.Str(reportedButtons?[6], "alias") == "no" && NodeRead.Str(reportedButtons?[0], "alias") is null);
Check(
    "a label-less button keeps its glyph label and its own id",
    NodeRead.Str(reportedButtons?[4], "name") == "\ue0c8" && NodeRead.Str(reportedButtons?[4], "id") == "folder-picker");

var probe = NodeRead.Obj(exportPayload, "probe");

Check(
    "the probe says how, how long and how many elements",
    NodeRead.Str(probe, "how") == "win32+uia" && NodeRead.Int(probe, "ms") == 12 && NodeRead.Int(probe, "nodes") == 41);
Check("and that it was neither degraded nor truncated", NodeRead.Bool(probe, "degraded") == false && NodeRead.Bool(probe, "truncated") == false);
Check("a blocking dialog comes with the sentence about the queue", NodeRead.Str(exportPayload, "note")?.Contains("queued, not lost") == true, NodeRead.Str(exportPayload, "note"));

var degraded = DialogModel.Payload(export, new DialogProbeInfo("win32", 9, Degraded: true, Error: "UI Automation refused"), DialogHandle);

Check("a degraded probe carries its error", NodeRead.Str(NodeRead.Obj(degraded, "probe"), "error") == "UI Automation refused");
Check("and says it was degraded", NodeRead.Bool(NodeRead.Obj(degraded, "probe"), "degraded") == true);

var warned = DialogModel.Payload(stranger, new DialogProbeInfo("win32+uia", 4), DialogHandle, warning: "warning text");

Check("an allowed-unknown press carries its warning", NodeRead.Str(warned, "warning") == "warning text");
Check("a dialog with no text reports no description", NodeRead.Str(DialogModel.Payload(stranger, new DialogProbeInfo("win32+uia", 4), DialogHandle), "description") is null);
Check(
    "and a store that could not say leaves openMs and instance out rather than printing a zero that reads like `just opened`",
    NodeRead.Long(warned, "openMs") is null && NodeRead.Int(warned, "instance") is null,
    $"{NodeRead.Long(warned, "openMs")} / {NodeRead.Int(warned, "instance")}");

// A null `dialog` has to be a measurement. Without these two numbers a payload cannot tell "nothing is modal" from
// "Dorico was not found", which is the one way the field could lie.
var presence = DialogModel.ProbePayload(new DialogProbeInfo("win32", 9, Windows: 1, DocumentEnabled: true));

Check(
    "a probe that measured Dorico's windows says how many there were",
    NodeRead.Int(presence, "doricoWindows") == 1 && NodeRead.Bool(presence, "documentEnabled") == true);
Check(
    "and a probe that did not measure them leaves the fields out rather than printing a zero",
    NodeRead.Int(DialogModel.ProbePayload(new DialogProbeInfo("win32", 9)), "doricoWindows") is null);
Check(
    "a document window that could not be found is reported as unknown, not as enabled",
    NodeRead.Bool(DialogModel.ProbePayload(new DialogProbeInfo("win32", 9, Windows: 2)), "documentEnabled") is null);

Console.WriteLine();
Console.WriteLine("== the dialog memory: openMs and instance, with no clock of its own and no window to look at");

// `openMs` and `instance` cannot be measured inside one invocation: this CLI exits between the two observations they
// are about, and the moment the window went up belongs to a process that has already gone. So the memory is a file
// under %LOCALAPPDATA%\dorico-use, and what is asserted here is that file's rules — with a clock the check passes in
// and a liveness predicate it answers itself, because the alternative is sleeping six hours for a prune.
var memoryRoot = Path.Combine(Path.GetTempPath(), $"dorico-use-check-{Environment.ProcessId}-{Guid.NewGuid():N}");
var memory = Path.Combine(memoryRoot, "dialogs.json");
var t0 = new DateTimeOffset(2026, 10, 11, 9, 12, 31, TimeSpan.Zero);

// The two answers the Windows side supplies as a function: IsWindow, asked of a window the check cannot look at.
Func<nint, bool> alive = _ => true;
Func<nint, bool> gone = _ => false;

// The first thing every invocation does is the cheap case: no dialog, so nothing to remember.
DialogInstances.Forget(memory);

Check("an invocation with no dialog and nothing to forget does not create a state file to say so", !File.Exists(memory));

var first = DialogInstances.Observe(0x00C91106, alive, t0, memory);

Check("a handle seen for the first time is occurrence 1, and has just opened", first is { OpenMs: 0, Instance: 1 }, first?.OpenMs.ToString());
Check("and the memory is on disk afterwards, in a directory of this check's own", File.Exists(memory));

var sameHandle = DialogInstances.Observe(0x00C91106, alive, t0.AddMilliseconds(4210), memory);

Check("the same handle keeps its firstSeen, so openMs is how long that window has been up", sameHandle is { OpenMs: 4210, Instance: 1 }, sameHandle?.OpenMs.ToString());
Check("and the occurrence does not increase while the handle stays up", sameHandle?.Instance == 1);

Check(
    "the file names the handle in the payload's own spelling",
    NodeRead.Str(NodeRead.Arr(JsonNode.Parse(File.ReadAllText(memory)), "dialogs")?[0], "hwnd") == "0x00C91106",
    File.ReadAllText(memory).Replace('\n', ' '));

var second = DialogInstances.Observe(0x00DEB120A, alive, t0.AddHours(1), memory);

Check("a second handle is a second entry, and starts at 1 as well", second is { OpenMs: 0, Instance: 1 });
Check("so the memory holds both", NodeRead.Arr(JsonNode.Parse(File.ReadAllText(memory)), "dialogs")?.Count == 2);

var pruned = DialogInstances.Observe(
    0x00DEB120A,
    alive,
    t0 + DialogInstances.StaleAfter + TimeSpan.FromHours(1),
    memory);

Check("an entry that has not been the dialog for longer than the bound is pruned", NodeRead.Arr(JsonNode.Parse(File.ReadAllText(memory)), "dialogs")?.Count == 1);
Check("while the dialog that is up keeps its own firstSeen, however long a user left it there", pruned is { OpenMs: 21600000, Instance: 1 }, pruned?.OpenMs.ToString());

var recycled = DialogInstances.Observe(0x00C91106, gone, t0.AddHours(2), memory);

Check("a handle whose window is gone is forgotten rather than carried over", recycled is { OpenMs: 0, Instance: 1 });
Check(
    "so a later dialog on a recycled handle counts from 1 again",
    DialogInstances.Observe(0x00C91106, gone, t0.AddHours(3), memory) is { OpenMs: 0, Instance: 1 });

// Put one entry back after the recycled-handle cases, so that the Forget below has something to forget.
DialogInstances.Observe(0x00C91106, alive, t0, memory);
DialogInstances.Forget(memory);

Check(
    "a probe that finds no dialog forgets every handle",
    NodeRead.Arr(JsonNode.Parse(File.ReadAllText(memory)), "dialogs")?.Count == 0,
    File.ReadAllText(memory).Replace('\n', ' '));

var afterForget = DialogInstances.Observe(0x00C91106, alive, t0.AddHours(4), memory);

Check("and the handle it was remembering is new again", afterForget is { OpenMs: 0, Instance: 1 });

Check(
    "a store that cannot be written degrades to nothing, not to a number nobody can stand behind",
    DialogInstances.Observe(0x00C91106, alive, t0, memoryRoot) is null);

// A file this feature wrote and cannot make sense of is not a failure: refusing to read it would turn one bad byte
// into two fields missing from every payload until somebody deleted a file they have never heard of.
File.WriteAllText(memory, "{\"dialogs\": [ {\"hwnd\": \"not a handle\"} ]}");

Check(
    "a memory that says nothing this build can use is replaced rather than treated as a failure",
    DialogInstances.Observe(0x00C91106, alive, t0.AddHours(5), memory) is { OpenMs: 0, Instance: 1 }
        && File.ReadAllText(memory).Contains("\"hwnd\": \"0x00C91106\"", StringComparison.Ordinal),
    File.ReadAllText(memory).Replace('\n', ' '));

try
{
    Directory.Delete(memoryRoot, recursive: true);
}
catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
{
    // The check's own temporary directory: failing to delete it is not a failure of anything asserted here.
}

Console.WriteLine();
Console.WriteLine("== the execution verdict, from a fixture log (no Dorico: a check cannot raise a modal dialog)");

// Both defects this section guards were found by running the tool against a real Dorico dialog, which no check can
// raise. What a check can do is put Dorico's lines into a file between the offset and the read - which is what
// Dorico does while the CLI waits - and the measured shape of the real thing is what makes that worth writing: the
// offset is taken at a line boundary, and the command's own Posting line is then the first line the log gains,
// which is the line the verdict is made of.
//
// Nothing here is written from another thread any more. It used to be: the lines were appended by a Task on a
// timer and the check then slept 400 ms for them, and that lost 3 runs in 60, always at this section's one case
// whose append overlapped the append before it. `File.AppendAllText` opens with FileShare.Read, so two appends
// that meet leave one of them throwing `IOException ... being used by another process` into a task nobody
// observes, and the line simply never appears. Measured in a reproduction of the pair: of 1920 overlapping pairs,
// 368 lost their line, while 1920 single appends all landed before a 400 ms read - so it was the overlap and not
// a slow machine. A line that has to arrive while the verdict is waiting now arrives through the verdict's own
// wait, which the seam below hands to this check: the append happens on the poll this check names, so the arrival
// is an ordering rather than a race against the clock.
var fixture = Path.Combine(Path.GetTempPath(), $"dorico-use-logqueue-check-{Environment.ProcessId}.log");

// The state every case below starts from: Dorico's log truncated to what was already in it, with the offset taken
// where a real invocation would take it.
void ResetLog(string logSoFar)
{
    File.WriteAllText(fixture, logSoFar);
    LogQueue.Begin(fixture);
}

// The log gains this line, which is the delta the verdict is made of. Appended from this thread, so there is no
// second writer to fight over the file.
void GainLine(string line) => File.AppendAllText(fixture, line);

// One poll of the verdict: it reads, and it waits only when what it read did not settle the question. This stands
// in for that wait and appends the line named for the wait it is, so "the log gained this line after the verdict
// had already looked once" is a fact about the ordering. A wait that has no line to gain sleeps as the CLI does,
// so a case that really has to wait out a budget still costs what it costs there.
static Action<TimeSpan> LogGainsOnWait(string path, params (int Wait, string Line)[] gains)
{
    var waits = 0;

    return interval =>
    {
        waits++;
        var line = gains.Where(gain => gain.Wait == waits).Select(gain => gain.Line).FirstOrDefault();

        if (line is null)
        {
            Thread.Sleep(interval);
            return;
        }

        File.AppendAllText(path, line);
    };
}

// ---------------------------------------------------------------- the panel layer

// Measured, live, on the library manager while it was up: five actionable elements, the three collection check
// boxes (出厂设置 off, 用户库 on, Dorico 工程 off — the same state an earlier measurement of this panel recorded) and
// the two buttons 关闭 and 应用. The lines below are that reading, and every assertion after this one is about what
// the pure half does with it.
var libraryElements = new List<PanelElementInput>
{
    new(50002, "出厂设置", "DoricoQApplication.LibraryDialog.collectionFactoryCheckBox", null, "off", null, null, false, 10, 100),
    new(50002, "用户库", "DoricoQApplication.LibraryDialog.collectionUserCheckBox", null, "on", null, null, false, 10, 130),
    new(50002, "Dorico 工程", "DoricoQApplication.LibraryDialog.collectionProjectCheckBox", null, "off", null, null, false, 10, 160),
    new(50000, "关闭", "DoricoQApplication.LibraryDialog.closePushButton", null, null, null, null, true, 10, 200),
    new(50000, "应用", "DoricoQApplication.LibraryDialog.applyPushButton", null, null, null, null, true, 60, 200),
};

var library = PanelModel.Describe(libraryElements);

Check("every actionable element of the panel gets a description", library.Count == 5, library.Count.ToString());
Check("a check box is a check box and a button is a button", library[0].Role == "checkBox" && library[3].Role == "button", library[0].Role + "," + library[3].Role);
Check("a check box reports the state the toggle pattern answered", library[0].State == "off" && library[1].State == "on", library[0].State + "," + library[1].State);
Check("a button with no state pattern reports no state", library[3].State is null, library[3].State);
Check("acting on a check box uses the toggle pattern, never invoke", library[0].Action == "toggle", library[0].Action);
Check("acting on a button uses invoke", library[3].Action == "invoke", library[3].Action);
Check("an element id comes from the automation id's tail when that tail is unique", library[0].Id == "collectionFactoryCheckBox", library[0].Id);
Check("the same reading gives the same ids three times over", Enumerable.Range(0, 3).All(_ => PanelModel.Describe(libraryElements).Select(element => element.Id).SequenceEqual(library.Select(element => element.Id))));

// Two elements can share a label and one can have none, exactly as the dialog buttons can — measured on the crash
// reporter, whose three buttons all share one automation id.
var sharing = PanelModel.Describe(
[
    new(50000, "不发送", "DoricoQApplication.CrashUploadConfirmationDialog_QMLTYPE_127.Button_QMLTYPE_47", null, null, null, null, true, 10, 10),
    new(50000, "不再询问", "DoricoQApplication.CrashUploadConfirmationDialog_QMLTYPE_127.Button_QMLTYPE_47", null, null, null, null, true, 70, 10),
    new(50000, "", "DoricoQApplication.CrashUploadConfirmationDialog_QMLTYPE_127.Button_QMLTYPE_47", null, null, null, null, true, 130, 10),
]);

Check("three elements that share an automation id still get three different ids", sharing.Select(element => element.Id).Distinct(StringComparer.Ordinal).Count() == 3, string.Join(',', sharing.Select(element => element.Id)));
Check("the label still comes through verbatim beside the id", sharing[0].Name == "不发送" && sharing[2].Name.Length == 0, sharing[0].Name);

// Selection and expansion, the two patterns a dialog never needed.
var tab = PanelModel.Describe([new(50019, "集合", "…tabBar.tab", null, null, true, null, false, 5, 5)])[0];
var tree = PanelModel.Describe([new(50024, "Percussion", "…tree.treeItem", null, null, null, "collapsed", false, 5, 40)])[0];
var field = PanelModel.Describe([new(50004, "", "…fileNameEdit", "C:\\scores", null, null, null, false, 5, 70)])[0];

Check("a chosen tab reports itself as selected and is acted on with the selection pattern", tab.State == "selected" && tab.Action == "select", tab.State + "," + tab.Action);
Check("a collapsed tree item offers expansion, not invoke", tree.State == "collapsed" && tree.Action == "expand", tree.State + "," + tree.Action);
Check("a text field is acted on through the value pattern", field.Action == "value" && field.State == "value", field.Action + "," + field.State);
Check("an element with no value, no toggle, no selection, no expander and no invoke has no action at all", PanelModel.Describe([new(50020, "text", null, null, null, null, null, false)])[0].Action is null);

// The window classification: the fact the whole layer turns on.
Check("a Qt class is the qt shape and a #32770 dialog is the native one", PanelModel.Shape("Qt6101QWindowIcon") == "qt" && PanelModel.Shape("#32770") == "native", PanelModel.Shape("#32770"));

var modeless = PanelModel.Describe(
    new WindowFacts(0x1000, 1, nint.Zero, false, true, "Qt6101QWindowIcon", "库管理"),
    documentEnabled: true);
var blocking = PanelModel.Describe(
    new WindowFacts(0x2000, 1, 0x10, true, true, "Qt6101QWindow", "导出MusicXML"),
    documentEnabled: false);
var unownedBlocking = PanelModel.Describe(
    new WindowFacts(0x3000, 1, nint.Zero, true, true, "Qt6101QWindowIcon", "向 Steinberg 发送崩溃报告"),
    documentEnabled: false);

Check("the measured library manager is recognised as a panel by name", NodeRead.Str(modeless, "id") == "dorico.library-manager" && NodeRead.Str(modeless, "kind") == "known", modeless.ToJsonString());
Check("a modeless panel does not block the queue, however Qt its class looks", NodeRead.Bool(modeless, "blocksQueue") == false);
Check("a dialog that disables the document window blocks", NodeRead.Bool(blocking, "blocksQueue") == true);
Check("a window that is up while the document window is disabled blocks even with no owner", NodeRead.Bool(unownedBlocking, "blocksQueue") == true);
Check("a window nobody measured is unknown and says so", NodeRead.Str(unownedBlocking, "kind") == "unknown" && NodeRead.Str(unownedBlocking, "note")?.Contains("panel catalogue", StringComparison.Ordinal) == true);
Check("an unknown panel's id is a hash of its caption and class, not a guess", NodeRead.Str(unownedBlocking, "id")?.StartsWith("unknown.", StringComparison.Ordinal) == true, NodeRead.Str(unownedBlocking, "id"));

var windows = PanelModel.Windows(
[
    new WindowFacts(0x1000, 1, nint.Zero, false, true, "Qt6101QWindowIcon", "库管理"),
    new WindowFacts(0x2000, 1, nint.Zero, false, true, "Qt6101QWindowIcon", "谱章 1 在 fixture 内"),
],
documentEnabled: true,
documentHandle: 0x2000,
dialogHandle: nint.Zero);

Check("the document window is in the list and is flagged as the document", windows.Count == 2 && NodeRead.Bool(windows[1], "document") == true, windows.ToJsonString());
Check("the modal window is flagged as the dialog", NodeRead.Bool(windows[0], "dialog") == false);

// The element payload of `ui panels --panel <id>`: what an agent reads before it acts.
var panelPayload = PanelModel.Elements(
    new WindowFacts(0x1000, 1, nint.Zero, false, true, "Qt6101QWindowIcon", "库管理"),
    documentEnabled: true,
    libraryElements,
    truncated: false,
    readMs: 30);

Check("the panel payload reports the elements it read", NodeRead.Int(panelPayload, "elementCount") == 5 && NodeRead.Arr(panelPayload, "elements")?.Count == 5);
Check("the panel payload names the read's mechanism", NodeRead.Str(panelPayload, "textSource") == "uia");
Check("the element cap is stated rather than applied in silence", NodeRead.Bool(panelPayload, "truncated") == false && PanelModel.MaxElements > 5, PanelModel.MaxElements.ToString());

// `enabled` and `invokable`: the two facts that say whether acting here would do anything, and the pair whose
// absence let a disabled 应用 look like a live target. Both are reported for every element, because measured
// 2026-10-13 a value write the field accepted was reverted by a dialog whose apply button was disabled.
var enabledPayload = PanelModel.ElementPayload(
    PanelModel.Describe(
    [
        new(ControlTypes.Button, "应用", "DoricoQApplication.ScoreInfoDialog.applyButton", null, null, null, null, true, 10, 10, Enabled: false),
        new(ControlTypes.Edit, "标题", "DoricoQApplication.ScoreInfoDialog.titleLineEdit", "", null, null, null, false, 10, 40),
    ]),
    out var enabledReturned);

Check("every element carries `enabled`, and a disabled one says false", enabledReturned == 2
    && NodeRead.Bool(enabledPayload[0], "enabled") == false
    && NodeRead.Bool(enabledPayload[1], "enabled") == true);
Check("every element carries `invokable`, not only a dialog's buttons", NodeRead.Bool(enabledPayload[0], "invokable") == true
    && NodeRead.Bool(enabledPayload[1], "invokable") == false);
Check("and a disable does not change the element's id or its action", NodeRead.Str(enabledPayload[0], "id") == "applyButton"
    && NodeRead.Str(enabledPayload[0], "action") == "invoke", NodeRead.Str(enabledPayload[0], "id"));

// A dialog's buttons carry `enabled` too, in the payload and in the description the policy reads.
var disabledButtonPayload = DialogModel.Payload(disabledDialog, new DialogProbeInfo("win32+uia", 4), DialogHandle);
var disabledButtons = NodeRead.Arr(disabledButtonPayload, "buttons");

Check("a dialog's buttons carry `enabled` as well", disabledButtons is not null
    && NodeRead.Bool(disabledButtons[0], "enabled") == false
    && NodeRead.Bool(disabledButtons[1], "enabled") == true);

var capped = PanelModel.Elements(
    new WindowFacts(0x1000, 1, nint.Zero, false, true, "Qt6101QWindowIcon", "库管理"),
    documentEnabled: true,
    [.. Enumerable.Range(0, PanelModel.MaxElements + 20).Select(index => new PanelElementInput(50000, "b" + index, null, null, null, null, null, true, index, 0))],
    truncated: false,
    readMs: 1);

Check("more elements than the cap is reported as truncated, not dropped quietly", NodeRead.Bool(capped, "truncated") == true && NodeRead.Arr(capped, "elements")?.Count == PanelModel.MaxElements, NodeRead.Arr(capped, "elements")?.Count.ToString());

Console.WriteLine("== reading a control's value, and the write that had no read-back");
Console.WriteLine("== the row ceiling: a viewport is not a list");

// Measured, and the reason both sections exist. Before this round a payload could say `state: "value"` and nothing
// else, so a write was reported by the string that was sent and could not be checked: on the Open picker's file-name
// box the tool wrote a path and said so, and on the 重命名演奏者 dialog's shortNameLineEdit it wrote "Probe." over
// "Picc." while the re-read said "value" both times. The value pattern carries the text (its legacy value repeats
// it), and the two fields below are what now says so.
var typed = new List<PanelElementInput>
{
    new(ControlTypes.Edit, "文件名(N):", "…open-picker.fileNameEdit", "", null, null, null, false, 0, 0,
        ValueReadOnly: false, Container: null),
    new(ControlTypes.ComboBox, "文件类型(T):", "1136", "Any Readable File (*.doricolib)", null, null, null, false, 0, 20,
        ValueReadOnly: false),
    new(ControlTypes.Edit, "短名：", "…RenamePlayerDialog.shortNameLineEdit", "Picc.", null, null, null, false, 0, 40,
        ValueReadOnly: false),
    new(ControlTypes.Edit, "大小", "…System.Size", "", null, null, null, false, 0, 60, ValueReadOnly: true),
    new(ControlTypes.Button, "关闭", "…closePushButton", null, null, null, null, true, 0, 80),
    new(ControlTypes.ListItem, "Piano", null, null, null, true, null, false, 0, 100,
        Container: "playersListView"),
};

// ElementPayload returns the `elements` array itself, for both a panel and a dialog.
var typedElements = PanelModel.ElementPayload(PanelModel.Describe(typed), out var typedReturned);

Check("the elements are all there", typedReturned == 6 && typedElements.Count == 6, typedReturned.ToString());
Check("a field reports the value its value pattern answered, not the word 'value'", NodeRead.Str(typedElements[2], "value") == "Picc.", NodeRead.Str(typedElements[2], "value"));
Check("and says which pattern it came from", NodeRead.Str(typedElements[2], "valueFrom") == PanelModel.ValueFromValuePattern, NodeRead.Str(typedElements[2], "valueFrom"));
Check("an empty field is an empty string and not a missing value", NodeRead.Str(typedElements[0], "value") == string.Empty, NodeRead.Str(typedElements[0], "value") ?? "(absent)");
Check("the file-type box reads back the filter the picker is showing", NodeRead.Str(typedElements[1], "value") == "Any Readable File (*.doricolib)", NodeRead.Str(typedElements[1], "value"));
Check("a read-only field says so, which is what a write has to be refused on", NodeRead.Bool(typedElements[3], "valueReadOnly") == true);
Check("a button carries no value at all", typedElements[4]!.AsObject()!.ContainsKey("value") == false, typedElements[4]!.ToJsonString());
Check("and neither does a list row", typedElements[5]!.AsObject()!.ContainsKey("value") == false);

// The evidence a press owes: the same element, read again in the same invocation.
var reRead = PanelModel.Describe([new(ControlTypes.Edit, "短名：", "…RenamePlayerDialog.shortNameLineEdit", "Probe.", null, null, null, false, 0, 40, ValueReadOnly: false)]);

Check(
    "the re-read of the element a write acted on reports the text the field really holds",
    NodeRead.Str(PanelModel.StateAfter(reRead, [new(ControlTypes.Edit, "短名：", "…RenamePlayerDialog.shortNameLineEdit", "Probe.", null, null, null, false, 0, 40, ValueReadOnly: false)], "shortNameLineEdit"), "value") == "Probe.");
Check(
    "an element that is gone reads as null rather than as an empty state",
    PanelModel.StateAfter([], [], "shortNameLineEdit") is null);

var dialogAfter = new JsonObject
{
    ["id"] = "dorico.open-picker",
    ["controls"] = JsonNode.Parse(
        """
        [{"id":"edit-1148","role":"edit","value":"C:\\Temp\\a.doricolib","valueReadOnly":false,"state":"value"}]
        """),
};

Check("a dialog's control is read back out of the dialog payload too", NodeRead.Str(PanelModel.ControlAfter(dialogAfter, "edit-1148"), "value") == "C:\\Temp\\a.doricolib", PanelModel.ControlAfter(dialogAfter, "edit-1148")?.ToJsonString());
Check("and a control that is gone is null, not a guess", PanelModel.ControlAfter(dialogAfter, "edit-999") is null);
Check("a dialog that closed reads as null", PanelModel.ControlAfter(null, "edit-1148") is null);

// The ceiling: measured on the Setup lists (30 of 49 player rows and 28 of 45 layout rows) and on the
// 繁衍声部格式 dialog's two lists (15 and 14 of 44), none of which exposes a scroll pattern.
var ceilingInput = new List<PanelElementInput>
{
    new(ControlTypes.ListItem, "组 1", "…playersListView", null, null, null, null, false, 0, 0, Container: "playersListView"),
    new(ControlTypes.ListItem, "Piccolo", "…playersListView", null, null, null, null, false, 0, 20, Container: "playersListView"),
    new(ControlTypes.ListItem, "总谱", "…layoutsListView", null, null, null, null, false, 0, 40, Container: "layoutsListView"),
    new(ControlTypes.Edit, "文件名(N):", "1148", "", null, null, null, false, 0, 60, ValueReadOnly: false),
};

var ceiling = PanelModel.Elements(
    new WindowFacts(0x1000, 1, nint.Zero, true, true, "Qt6101QWindowIcon", "谱章 1 在 fixture 内"),
    documentEnabled: true,
    ceilingInput,
    truncated: false,
    readMs: 40);
var ceilingLists = NodeRead.Arr(ceiling, "lists")!;

Check("the rows are counted per list, not as one number", NodeRead.Int(ceiling, "rows") == 3 && ceilingLists.Count == 2, NodeRead.Int(ceiling, "rows")?.ToString());
Check("each list reports how many rows it realised", NodeRead.Str(ceilingLists[0], "container") == "playersListView" && NodeRead.Int(ceilingLists[0], "rows") == 2, ceilingLists[0]!.ToJsonString());
Check("and that it cannot be scrolled, which is why the rest is out of reach", NodeRead.Bool(ceilingLists[0], "scrollPattern") == false);
Check("the payload says the rows are a viewport rather than the list", NodeRead.Bool(ceiling, "viewportLimited") == true);
Check("and spells the ceiling out where a caller cannot miss it", NodeRead.Str(ceiling, "rowCeiling")?.Contains("30 of 49", StringComparison.Ordinal) == true);
Check("a row carries the container it came from, so two lists stay apart", NodeRead.Str(typedElements[5], "container") == "playersListView", NodeRead.Str(typedElements[5], "container"));
Check("a field that is not in a list carries no container", typedElements[4]!.AsObject()!.ContainsKey("container") == false);

var noRows = PanelModel.Elements(
    new WindowFacts(0x1000, 1, nint.Zero, true, true, "Qt6101QWindowIcon", "库管理"),
    documentEnabled: true,
    libraryElements,
    truncated: false,
    readMs: 30);

Check("a window with no list carries no ceiling at all", NodeRead.Int(noRows, "rows") is null && NodeRead.Str(noRows, "rowCeiling") is null);

Check("a row container names a list, a tree, a grid and a table", ControlTypes.IsRowContainer(ControlTypes.List) && ControlTypes.IsRowContainer(ControlTypes.Tree) && ControlTypes.IsRowContainer(ControlTypes.DataGrid) && ControlTypes.IsRowContainer(ControlTypes.Table));
Check("a plain pane is not a row container", !ControlTypes.IsRowContainer(ControlTypes.Pane));
Check("a list row, a tree item and a data item are rows", ControlTypes.IsRow(ControlTypes.ListItem) && ControlTypes.IsRow(ControlTypes.TreeItem) && ControlTypes.IsRow(ControlTypes.DataItem));
Check("a text field is not a row", !ControlTypes.IsRow(ControlTypes.Edit));
Check("a container is named by its automation-id tail, which is what a caller already saw", PanelModel.ContainerName("DoricoQApplication.DocumentWindow…playersContentFrame.playersListView", "", ControlTypes.List) == "playersListView", PanelModel.ContainerName("a.b.playersListView", "", ControlTypes.List));
Check("a container with no tail falls back to the label the window shows, verbatim", PanelModel.ContainerName(null, "布局", ControlTypes.List) == "布局", PanelModel.ContainerName(null, "布局", ControlTypes.List));
Check("and with neither, to its role", PanelModel.ContainerName(null, "", ControlTypes.List) == "list");

Check("every catalogued panel has a note, an id and a matcher", PanelCatalogue.Entries.All(entry => entry.Id.Length > 0 && entry.Note.Length > 0 && (entry.TitleGlobs.Count > 0 || entry.ClassPrefixes.Count > 0)));
Check("a panel catalogue entry only matches the class it was measured in", PanelCatalogue.Match("库管理", "#32770") is null);
Check("an empty caption does not match a catalogued panel", PanelCatalogue.Match("", "Qt6101QWindowIcon") is null);

Console.WriteLine("== the control type table: one wrong number is a capability that disappears");

// Measured, and the reason this section exists: `ListItem` was 50029 for a whole release, which is `DataItem`, so
// every list row of Dorico's Setup lists and of the 繁衍声部格式 dialog was filtered out of the read. Dorico answers
// 50007 for a row - read with an independent walker and again through this build's own reader.
Check("a list row is control type 50007", ControlTypes.ListItem == 50007, ControlTypes.ListItem.ToString());
Check("and 50029 is something else entirely", ControlTypes.DataItem == 50029 && ControlTypes.DataItem != ControlTypes.ListItem);
Check("a list row is something an agent may act on", ControlTypes.IsActionable(ControlTypes.ListItem));
Check("a text label is not", !ControlTypes.IsActionable(ControlTypes.Text));
Check("a group is not", !ControlTypes.IsActionable(ControlTypes.Group));
Check("and neither is a pane", !ControlTypes.IsActionable(ControlTypes.Pane));
Check("a row is named as one", ControlTypes.Name(ControlTypes.ListItem) == "ListItem", ControlTypes.Name(ControlTypes.ListItem));
Check("and its role is how an agent sees it", ControlTypes.Role(ControlTypes.ListItem) == "listItem", ControlTypes.Role(ControlTypes.ListItem));
Check("an unnamed control type still reports its number", ControlTypes.Role(50999) == "controlType-50999", ControlTypes.Role(50999));
Check("every actionable control type has a role of its own", Array.TrueForAll(actionableControlTypes, id => ControlTypes.IsActionable(id) && !ControlTypes.Role(id).StartsWith("controlType-", StringComparison.Ordinal)));

var row = new PanelElementInput(ControlTypes.ListItem, "Piccolo", null, null, null, true, null, false, 0, 0);

Check("a selected list row reports its state", PanelModel.State(row) == "selected", PanelModel.State(row));
Check("and the action on it is a selection", PanelModel.Action(row) == PanelAction.Select, PanelModel.Action(row).ToString());

var rowDescription = PanelModel.Describe([row]);

Check("a row with no automation id falls back to its position and still gets an id", rowDescription[0].Id.Length > 0 && rowDescription[0].Role == "listItem", rowDescription[0].Id);

Console.WriteLine("== the classic Win32 control id, kept where it is shared");

// Measured on the Open picker: the file-name box, the ComboBox inside it and the ComboBoxEx32 pane around that all
// answer automation id 1148, so an unqualified number is not unique and would fall through to a position id.
var classic = PanelModel.Describe(
[
    new PanelElementInput(ControlTypes.ComboBox, "文件名(N):", "1148", "", null, null, null, false, 0, 0),
    new PanelElementInput(ControlTypes.Edit, "文件名(N):", "1148", "", null, null, null, false, 0, 0),
    new PanelElementInput(ControlTypes.ComboBox, "文件类型(T):", "1136", "", null, null, null, false, 0, 0),
]);

Check("a shared classic id is qualified by the role", classic[0].Id == "comboBox-1148" && classic[1].Id == "edit-1148", string.Join(',', classic.Select(entry => entry.Id)));
Check("and an id that is not shared is left as it is", classic[2].Id == "1136", classic[2].Id);

Console.WriteLine("== the document window: the one window an agent has to be able to name");

Check(
    "the document window has a constant id, not a hash of its caption",
    PanelModel.PanelId("「狸猫的五十种变化」… 内*", "Qt6101QWindowIcon", PanelModel.KindUnknown, document: true) == PanelModel.DocumentWindowId,
    PanelModel.DocumentWindowId);
Check(
    "so the same window keeps that id when the document is renamed or unsaved",
    PanelModel.PanelId("A", "Qt6101QWindowIcon", PanelModel.KindUnknown, document: true)
        == PanelModel.PanelId("B*", "Qt6101QWindowIcon", PanelModel.KindUnknown, document: true));
Check(
    "a window that is not the document still gets a derived id",
    PanelModel.PanelId("库管理", "Qt6101QWindowIcon", PanelModel.KindUnknown, document: false) == "dorico.library-manager");
Check(
    "and a window nothing knows stays unknown",
    PanelModel.PanelId("something else", "Qt6101QWindowIcon", PanelModel.KindUnknown, document: false).StartsWith("unknown.", StringComparison.Ordinal));

var documentPanel = PanelModel.Describe(
    new WindowFacts(0x1000, 1, nint.Zero, false, true, "Qt6101QWindowIcon", "A score"),
    documentEnabled: true,
    documentHandle: 0x1000);

Check("the document window's entry carries that id", NodeRead.Str(documentPanel, "id") == PanelModel.DocumentWindowId, NodeRead.Str(documentPanel, "id"));
Check("and says what can be reached inside it", NodeRead.Str(documentPanel, "description")?.Contains("Setupless", StringComparison.Ordinal) == false && NodeRead.Str(documentPanel, "pressWith")?.Contains(PanelModel.DocumentWindowId, StringComparison.Ordinal) == true, NodeRead.Str(documentPanel, "pressWith"));

var elementsArray = PanelModel.ElementPayload(PanelModel.Describe([row]), out var returned);

Check("the elements array is the shape an agent pastes an id back from", returned == 1 && NodeRead.Str(elementsArray[0], "role") == "listItem" && NodeRead.Str(elementsArray[0], "action") == "select", elementsArray.ToJsonString());

Console.WriteLine("== the Open picker: measured, catalogued, and its name box is a control");

var picker = DialogCatalogue.Entries.FirstOrDefault(entry => entry.Id == "dorico.open-picker");

Check("the file picker is catalogued now that it has been read", picker is not null && picker.Measured);
Check("and it is no longer only a reserved id", !DialogCatalogue.Reserved.Contains("dorico.open-picker"));
Check("its caption is the one the window showed", picker is not null && DialogCatalogue.Glob("打开", picker.TitleGlobs[0]));
Check("its accept button is not safe, because accepting an import changes the document", picker!.Buttons[0].Id == "accept" && !picker.Buttons[0].Safe);
Check("and its cancel button is", picker.Buttons[1].Id == "cancel" && picker.Buttons[1].Safe);
Check("and every criterion it declares is one all of its buttons carry", picker.Buttons.All(button => button.MatchLabels.Count == 1 && button.MatchClassicIds.Count == 1), string.Join('|', picker.Buttons.Select(button => string.Join(',', button.MatchClassicIds))));

// The measurement this rule comes from: the live Open picker has two `DropDown` buttons whose label is also 打开,
// so a label-only matcher handed them the accept id and `--button accept` would press a combo box's arrow.
var pickerLookalike = DialogModel.Describe(new DialogInput(
    Title: "打开",
    WindowClass: "#32770",
    Owner: "0x00C91106",
    TextSource: "uia",
    TextLines: [],
    Buttons:
    [
        new DialogButtonInput("打开", "DropDown", true),
        new DialogButtonInput("打开", "1", true),
        new DialogButtonInput("取消", "2", true),
    ]));

Check("the picker is recognised by its caption and its labels", pickerLookalike.Id == "dorico.open-picker", pickerLookalike.Id);
Check("a button that carries the accept label and no classic id is not the accept button", pickerLookalike.Buttons[0].Id != "accept", pickerLookalike.Buttons[0].Id);
Check("the one that carries the classic id is", pickerLookalike.Buttons[1].Id == "accept" && pickerLookalike.Buttons[1].Alias == "ok", pickerLookalike.Buttons[1].Id);
Check("and its pressWith offers the safe cancel line", pickerLookalike.PressWith == "dorico-use ui press --dialog dorico.open-picker --button cancel", pickerLookalike.PressWith);

Console.WriteLine("== the modal guard: command send refuses while a blocking dialog is up");

// Measured twice on Dorico 6.2.30: a command that raises a dialog while a dialog is already up kills the process,
// and the tool knows a modal is up from the `dialog` field it already carries. The rule is pure - it takes the
// dialog's JSON, not a window - so it is asserted here without a dialog, which no check can raise.
var upDialog = new JsonObject
{
    ["id"] = "dorico.export-musicxml",
    ["title"] = "导出MusicXML",
    ["kind"] = "known",
    ["class"] = "Qt6101QWindow",
    ["pressWith"] = "dorico-use ui press --dialog dorico.export-musicxml --button cancel",
};

var refusedWhileUp = DialogModel.CommandSendGuard(upDialog, allowModalDialog: false);
var guardData = refusedWhileUp?.Data;

Check("a blocking dialog up refuses the send", refusedWhileUp is not null);
Check(
    "with a code of its own, not one of the press refusals",
    DialogModel.ModalDialogUpCode == "ModalDialogUp"
        && !DialogModel.RefusalCodes.Contains(DialogModel.ModalDialogUpCode, StringComparer.Ordinal));
Check("and exit 1, like every other refusal", DialogModel.RefusalExitCode == 1);
Check(
    "and the dialog's identity in error.data",
    NodeRead.Str(NodeRead.Obj(guardData, "dialog"), "id") == "dorico.export-musicxml"
        && NodeRead.Str(NodeRead.Obj(guardData, "dialog"), "title") == "导出MusicXML",
    guardData?.ToJsonString());
Check(
    "and the line that answers it",
    NodeRead.Str(NodeRead.Obj(guardData, "dialog"), "pressWith") == upDialog["pressWith"]!.GetValue<string>());
Check("and the override's name", NodeRead.Str(guardData, "override") == "--allow-modal-dialog", NodeRead.Str(guardData, "override"));
Check("and the reason it exists, so the refusal explains itself", NodeRead.Str(guardData, "reason")?.Contains("kills the process", StringComparison.Ordinal) == true);
Check(
    "and the sentence names the dialog and the way out",
    refusedWhileUp!.Message.Contains("导出MusicXML", StringComparison.Ordinal)
        && refusedWhileUp.Message.Contains("--allow-modal-dialog", StringComparison.Ordinal));

Check("the override sends anyway", DialogModel.CommandSendGuard(upDialog, allowModalDialog: true) is null);
Check("nothing modal sends", DialogModel.CommandSendGuard(null, allowModalDialog: false) is null);
Check(
    "and a dialog object that cannot be named does not refuse either",
    DialogModel.CommandSendGuard(new JsonObject { ["title"] = "?" }, allowModalDialog: false) is null);

Console.WriteLine("== blockedByDialog: the dialog this invocation's own send raised");

// Measured 2026-10-13, and the reason the field exists: `command send` of
// MouseEdit.ChangeEventPositionOrDuration?NewDuration=4 answered kOK, `execution.state` read `ran` - the log shows
// the posting and the execution - and nothing in the document changed, because the send had raised Dorico's
// `dorico.error` box (openMs 0, first seen by that invocation) and every later command was posted and not executed.
// The payload has to say so next to `execution`, or it claims a command ran while nothing ran.
var raisedBySelf = new JsonObject { ["id"] = "dorico.error", ["openMs"] = 0, ["instance"] = 1 };
var raisedEarlier = new JsonObject { ["id"] = "dorico.error", ["openMs"] = 26_079, ["instance"] = 1 };
var noOccurrence = new JsonObject { ["id"] = "dorico.error" };

Check("a dialog this invocation was the first to see is its own", DialogModel.RaisedByThisInvocation(raisedBySelf));
Check(
    "and a dialog that was already up is not: openMs is what tells them apart",
    !DialogModel.RaisedByThisInvocation(raisedEarlier));
Check(
    "and a store that could not say openMs is not read as 'just appeared'",
    !DialogModel.RaisedByThisInvocation(noOccurrence));
Check("and no dialog at all is not a blocked one", !DialogModel.RaisedByThisInvocation(null));
Check(
    "the sentence says what the field voids",
    DialogModel.BlockedByDialogNote.Contains("openMs", StringComparison.Ordinal)
        && DialogModel.BlockedByDialogNote.Contains("execution.state", StringComparison.Ordinal));

Console.WriteLine("== a short element list is not a small window");

// Measured, and the reason `visited` is on every panel read rather than under --raw: the library manager's UIA tree
// visits 28 nodes and only 5 of them are elements an action can target, while the window draws rows, a filter, a
// search field and a drop target that have no node at all. `elementCount: 5` must not read as "empty dialog".
var walkedPanel = PanelModel.Elements(
    new WindowFacts(0x2000, 1, nint.Zero, false, true, "Qt6101QWindowIcon", "库管理"),
    documentEnabled: true,
    elements: [row],
    truncated: false,
    readMs: 12,
    error: null,
    nodesVisited: 28);

Check("a read says how many nodes it walked", NodeRead.Int(walkedPanel, "visited") == 28, NodeRead.Int(walkedPanel, "visited")?.ToString());
Check("and says so in words when the walk kept fewer", NodeRead.Str(walkedPanel, "walkNote")?.Contains("visited 28 elements", StringComparison.Ordinal) == true, NodeRead.Str(walkedPanel, "walkNote"));
Check("the sentence names the measured example", NodeRead.Str(walkedPanel, "walkNote")?.Contains("library manager", StringComparison.Ordinal) == true);
Check("and it claims only what it saw", NodeRead.Str(walkedPanel, "walkNote")?.Contains("not proof that the window is small", StringComparison.Ordinal) == true);

var completePanel = PanelModel.Elements(
    new WindowFacts(0x2001, 1, nint.Zero, false, true, "Qt6101QWindowIcon", "小窗口"),
    documentEnabled: true,
    elements: [row],
    truncated: false,
    readMs: 3,
    error: null,
    nodesVisited: 1);

Check("a read whose walk kept everything carries no such warning", completePanel["walkNote"] is null && NodeRead.Int(completePanel, "visited") == 1);

var everyNode = PanelModel.NodePayload(
[
    new PanelNodeInput(ControlTypes.Window, "库管理", "DoricoQApplication.root", 0, false),
    new PanelNodeInput(ControlTypes.Group, string.Empty, "…CollectionListDelegate_QMLTYPE_629", 1, false),
    new PanelNodeInput(ControlTypes.CheckBox, "用户库", "…Button_QMLTYPE_43", 2, true),
    new PanelNodeInput(ControlTypes.ScrollBar, string.Empty, "…ScrollBar_QMLTYPE_34", 2, false),
], out var nodesReturned);

Check("the raw walk reports every node, not only the kept ones", nodesReturned == 4 && everyNode.Count == 4, nodesReturned.ToString());
Check("and says which ones an action could target", NodeRead.Bool(everyNode[1], "kept") == false && NodeRead.Bool(everyNode[2], "kept") == true);
Check(
    "and names the roles a tree is made of, not their numbers",
    NodeRead.Str(everyNode[0], "role") == "window" && NodeRead.Str(everyNode[3], "role") == "scrollBar",
    NodeRead.Str(everyNode[3], "role"));
Check("and reports what the window draws with nothing in it", string.IsNullOrEmpty(NodeRead.Str(everyNode[1], "name")) && NodeRead.Str(everyNode[1], "controlType") == "Group");

Console.WriteLine("== the layout options dialog: the reset control the checklist needs");

var layoutOptions = PanelCatalogue.Entries.FirstOrDefault(entry => entry.Id == "dorico.layout-options");

Check("the layout options dialog is catalogued now that it has been read", layoutOptions is not null);
Check("by its measured caption, which carries the document's name", layoutOptions is not null && DialogCatalogue.Glob("layoutprobe - 布局选项", layoutOptions.TitleGlobs[0]), layoutOptions?.TitleGlobs[0]);
Check("and by the Qt class its reading found", layoutOptions is not null && layoutOptions.ClassPrefixes.Contains("Qt", StringComparer.OrdinalIgnoreCase));
Check("its note names the reset button this build presses", layoutOptions?.Note.Contains("resetToSavedDefaults", StringComparison.Ordinal) == true);
Check("and what the press measured", layoutOptions?.Note.Contains("restored two dirtied part layouts", StringComparison.Ordinal) == true);
Check(
    "the library manager's note carries this round's reading",
    PanelCatalogue.Entries.First(entry => entry.Id == "dorico.library-manager").Note.Contains("27 nodes", StringComparison.Ordinal));

// The command's own Posting line is not in the log when the verdict first looks and is there when it looks again:
// that second read is the retry this class exists for, and without it a command Dorico had queued reads `unknown`,
// which is the defect the fixture is shaped after. Dorico's own queue line is gained in the same write, because a
// real log has it there.
ResetLog("2026-10-11 05:42:42.463 [info] ping response received in ipc://Dorico6.lic\n");
LogQueue.Mark("NoteInput.Enter");

var queuedGain = LogGainsOnWait(
    fixture,
    (1, "2026-10-11 05:42:49.593 [info] Posting command (requested): NoteInput.Enter\n"
        + "2026-10-11 05:42:49.593 [info] Queue after push: {NoteInput.Enter} \n"));

var onTheQueue = LogQueue.Verdict(LogQueue.DefaultBudget, queuedGain);

Check(
    "a command whose first log line is its own Posting line is queued, not unknown",
    NodeRead.Str(onTheQueue, "state") == "queued",
    NodeRead.Str(onTheQueue, "state"));
Check(
    "and the evidence says the Posting line was seen and no Executing line followed",
    NodeRead.Str(onTheQueue, "evidence")?.Contains("NoteInput.Enter: queued", StringComparison.Ordinal) == true
        && NodeRead.Str(onTheQueue, "evidence")?.Contains("no Executing line", StringComparison.Ordinal) == true,
    NodeRead.Str(onTheQueue, "evidence"));
Check(
    "and Dorico's own queue line is named when the log has one",
    NodeRead.Str(onTheQueue, "evidence")?.Contains("Queue after push", StringComparison.Ordinal) == true,
    NodeRead.Str(onTheQueue, "evidence"));
Check("and the payload still carries the log it read", NodeRead.Str(onTheQueue, "log") == fixture);

// What a live read needs while it waits for its own file: is the queue still busy, and therefore is the output late
// rather than never? Measured twice on 2026-10-11 - the players script was posted and executed 30.1 s later, behind
// a long command, while the read gave up after 20 s. The live read posts through the host and never names its own
// command to this class, which is why an unmarked posting is the shape that matters here. Nothing waits for a
// writer thread here either: the scan reads the delta it is given.
ResetLog("2026-10-11 10:04:34.619 [info] Sync complete\n");
GainLine("2026-10-11 10:04:50.116 [info] Posting command (requested): Script.RunScript ScriptPath=x.lua\n");

var pending = LogQueue.PendingSinceStart();

Check("work the log shows posted with no execution is reported as pending", pending.Contains("Script.RunScript", StringComparer.Ordinal), string.Join(',', pending));

GainLine("2026-10-11 10:05:20.180 [info] Executing command: Script.RunScript?ScriptPath=x.lua\n");

Check("and once it executes, nothing is pending any more", LogQueue.PendingSinceStart().Count == 0, string.Join(',', LogQueue.PendingSinceStart()));

// The measured pair, in the delta at the read rather than expected to arrive before it: a posting the log also
// shows executing is not pending.
ResetLog("2026-10-11 05:15:50.603 [info] Sync complete\n");
LogQueue.Mark("NoteInput.Enter");
GainLine("2026-10-11 05:15:50.603 [info] Posting command (requested): NoteInput.Enter\n");
GainLine("2026-10-11 05:15:50.603 [info] Executing command: NoteInput.Enter\n");

Check("a command this invocation posted by name and nothing executed is pending too", LogQueue.PendingSinceStart().Count == 0, string.Join(',', LogQueue.PendingSinceStart()));

// A live read posts its own Script.RunScript by name, and that posting is exactly what it is waiting behind - so the
// pending scan must count it, which Unmatched (the verdict's orphan scan) deliberately does not. This is the case
// that failed: measured, 3 runs in 60 reported `pending: (none)` here, because the line it waited for was appended
// from another thread and lost a race with the append before it.
ResetLog("2026-10-11 10:04:34.619 [info] Sync complete\n");
LogQueue.Mark("Script.RunScript");
GainLine("2026-10-11 10:04:50.116 [info] Posting command (requested): Script.RunScript ScriptPath=x.lua\n");

Check("and the read's own named posting counts as pending while it has not executed", LogQueue.PendingSinceStart().Contains("Script.RunScript", StringComparer.Ordinal), string.Join(',', LogQueue.PendingSinceStart()));

// A command that really runs executes in the same millisecond it is posted (measured: Posting 05:15:50.603,
// Executing 05:15:50.603), and the two lines are separate writes: the verdict's second phase has to look again
// after the Posting line to see the Executing one. This gains them on two polls, so the gap is the ordering rather
// than a race. It fails if the retry goes (the first read sees nothing, so the verdict would be `unknown`) and if
// the second phase goes (`queued`, the Posting line having been read and stopped at).
ResetLog("2026-10-11 05:15:50.603 [info] Sync complete\n");
LogQueue.Mark("NoteInput.Enter");

var ranGain = LogGainsOnWait(
    fixture,
    (1, "2026-10-11 05:15:50.603 [info] Posting command (requested): NoteInput.Enter\n"),
    (2, "2026-10-11 05:15:50.603 [info] Executing command: NoteInput.Enter\n"));

var executed = LogQueue.Verdict(LogQueue.DefaultBudget, ranGain);

Check(
    "an Executing line right after the Posting line makes it ran",
    NodeRead.Str(executed, "state") == "ran",
    NodeRead.Str(executed, "state"));

ResetLog("2026-10-11 05:44:09.452 [info] Auto-save skipped\n");
LogQueue.Mark("Play.StopScrubPlayback");
LogQueue.Acknowledge("Play.StopScrubPlayback", "kError kCommandNotAllowed");

var refusedByApi = LogQueue.Verdict(TimeSpan.FromMilliseconds(300));

Check(
    "a kError with no Posting line is refused, which stays a different verdict from queued",
    NodeRead.Str(refusedByApi, "state") == "refused",
    NodeRead.Str(refusedByApi, "state"));
Check(
    "and its evidence says the command was never queued",
    NodeRead.Str(refusedByApi, "evidence")?.Contains("never queued", StringComparison.Ordinal) == true,
    NodeRead.Str(refusedByApi, "evidence"));

ResetLog("2026-10-11 05:44:09.452 [info] Auto-save skipped\n");
LogQueue.Mark("NoteInput.Enter");
LogQueue.Acknowledge("NoteInput.Enter", "kOK");

var nothing = LogQueue.Verdict(TimeSpan.FromMilliseconds(300));

Check(
    "a kOK with no Posting line after the wait is unknown: the wait is bounded, and what it saw is all it claims",
    NodeRead.Str(nothing, "state") == "unknown",
    NodeRead.Str(nothing, "state"));

// The other half of the offset rule: the fragment of a line Dorico was still writing when the offset was taken must
// not be read as a line. The fix above must not have turned the drop off, only made it conditional - and the count
// of lines the delta holds is what says the fragment is gone rather than merely ignored: dropping it leaves the two
// real lines, and a reader that kept it would report three.
ResetLog("2026-10-11 05:44:09.452 [info] Auto-save ski");
LogQueue.Mark("File.Open");
GainLine(
    "pped\n2026-10-11 05:45:01.100 [info] Posting command (requested): File.Open File=C:\\scores\\a.dorico\n"
    + "2026-10-11 05:45:01.101 [info] Executing command: File.Open?File=C:\\scores\\a.dorico\n");

var afterPartial = LogQueue.Verdict(LogQueue.DefaultBudget);

Check(
    "a half-written line at the offset is still dropped rather than read as a Posting line",
    NodeRead.Str(afterPartial, "state") == "ran",
    NodeRead.Str(afterPartial, "state"));
Check(
    "and the fragment is gone from the delta rather than merely ignored",
    NodeRead.Int(afterPartial, "postings") == 2,
    NodeRead.Int(afterPartial, "postings")?.ToString());

// The passive wait - what `ui press`, `ui dialog` and the live read come through, since none of them names a command
// to this class - stops as soon as the log shows anything, and its budget is capped at PassiveBudget. The null below
// is the answer with an empty delta; the clock is a bound, not an expectation of a fast machine, and the budget asked
// for is deliberately longer than the ceiling so that a wait that stopped honouring the cap would be caught here.
ResetLog("2026-10-11 05:44:09.452 [info] Auto-save skipped\n");
var cappedFrom = Stopwatch.GetTimestamp();

Check(
    "an invocation that posted nothing by name carries no execution field at all, instead of an unknown about nothing",
    LogQueue.Verdict(TimeSpan.FromSeconds(5)) is null);
Check(
    "and its wait is the capped one, not the five seconds the budget asked for",
    Stopwatch.GetElapsedTime(cappedFrom) < TimeSpan.FromSeconds(3),
    Stopwatch.GetElapsedTime(cappedFrom).ToString());

ResetLog("2026-10-11 05:44:09.452 [info] Auto-save skipped\n");

var someoneElsesGain = LogGainsOnWait(fixture, (1, "2026-10-11 05:46:00.000 [info] Posting command (requested): NoteInput.Enter\n"));

var someoneElses = LogQueue.Verdict(LogQueue.PassiveBudget, someoneElsesGain);

Check(
    "a posting the host sent on its own is still reported as queued when nothing executed it",
    NodeRead.Str(someoneElses, "state") == "queued"
        && NodeRead.Str(someoneElses, "evidence")?.Contains("posted by the host", StringComparison.Ordinal) == true,
    NodeRead.Str(someoneElses, "evidence"));

Console.WriteLine();
Console.WriteLine("== waiting for a command's own completion line (--wait-for-completion)");

// Measured 2026-10-13: a command's cost is not guessable in this session - Instrument.MoveToPlayer took 4.4 s,
// Players.DeleteEmpty 25 s, a long export 54 s - so a caller that sleeps a fixed amount either comes back too early
// or wastes the difference. Dorico's log names the moment: `notifyPostCommandExecute: <name>?...(<ms> ms)`. The wait
// is driven here through the same seam the verdict uses, so the line arrives on the poll this check names rather
// than on a timer nobody controls.
ResetLog("2026-10-11 10:04:34.619 [info] Sync complete\n");

var completionGain = LogGainsOnWait(
    fixture,
    (1, "2026-10-11 10:04:50.116 [info] Posting command (requested): Players.DeleteEmpty?RemovePlayersWithoutContent=true\n"),
    (3, "2026-10-11 10:05:15.180 [info] notifyPostCommandExecute: Players.DeleteEmpty?RemovePlayersWithoutContent=true (25064 ms)\n"));

var waited = LogQueue.WaitForCompletion("Players.DeleteEmpty", TimeSpan.FromSeconds(5), completionGain);

Check(
    "the command's own completion line ends the wait",
    NodeRead.Bool(waited, "completed") == true && NodeRead.Bool(waited, "timedOut") == false,
    waited.ToJsonString());
Check("and it says the command was posted, not merely that a line matched", NodeRead.Bool(waited, "posted") == true);
Check("and how long it waited", NodeRead.Long(waited, "waitedMs") is >= 0 && NodeRead.Long(waited, "waitedMs") < 5000, NodeRead.Long(waited, "waitedMs")?.ToString());
Check("and the budget it was given", NodeRead.Long(waited, "budgetMs") == 5000, NodeRead.Long(waited, "budgetMs")?.ToString());
Check("and the line it saw, counted", NodeRead.Int(waited, "completedLines") == 1, NodeRead.Int(waited, "completedLines")?.ToString());

// The refinement rule: the log names the command the caller sent as a longer command for the export route (the
// measured line in docs/scripting.md is File.Export.Text for the catalogue's File.Export), so the wait accepts a
// dot-qualified extension of the name - and only in that direction.
ResetLog("2026-10-11 10:04:34.619 [info] Sync complete\n");

var exportGain = LogGainsOnWait(fixture, (1, "2026-10-11 10:04:40.000 [info] notifyPostCommandExecute: File.Export.Text?FilterID=PDFExportFilter (1894 ms)\n"));
var exported = LogQueue.WaitForCompletion("File.Export", TimeSpan.FromSeconds(5), exportGain);

Check(
    "a completion line that refines the command name counts as that command",
    NodeRead.Bool(exported, "completed") == true,
    exported.ToJsonString());

ResetLog("2026-10-11 10:04:34.619 [info] Sync complete\n");

var shorterGain = LogGainsOnWait(fixture, (1, "2026-10-11 10:04:40.000 [info] notifyPostCommandExecute: File.Export (10 ms)\n"));
var shorter = LogQueue.WaitForCompletion("File.Export.Text", TimeSpan.FromMilliseconds(400), shorterGain);

Check(
    "but a shorter logged name is not evidence that the longer command ran",
    NodeRead.Bool(shorter, "completed") == false && NodeRead.Bool(shorter, "timedOut") == true,
    shorter.ToJsonString());

// A command that never finishes has to come back with an answer. The fixture gains its Posting line and nothing
// else, which is what a command queued behind a modal dialog looks like in the log.
ResetLog("2026-10-11 10:04:34.619 [info] Sync complete\n");
GainLine("2026-10-11 10:04:50.116 [info] Posting command (requested): Instrument.MoveToPlayer?PlayerIDArray=1\n");

var timedOut = LogQueue.WaitForCompletion("Instrument.MoveToPlayer", TimeSpan.FromMilliseconds(150), Thread.Sleep);

Check(
    "a command with no completion line times out, and says so",
    NodeRead.Bool(timedOut, "completed") == false && NodeRead.Bool(timedOut, "timedOut") == true,
    timedOut.ToJsonString());
Check("and it still reports the posting it did see", NodeRead.Bool(timedOut, "posted") == true);
Check("and the wait was really bounded by the budget", NodeRead.Long(timedOut, "waitedMs") < 3000, NodeRead.Long(timedOut, "waitedMs")?.ToString());
Check(
    "and the note tells a caller the command may still be running",
    NodeRead.Str(timedOut, "note")?.Contains("still be", StringComparison.Ordinal) == true);

// A `command send --wait-for-completion` for a command the host posted on its own has no name to wait for, and
// waiting the whole budget for a line that cannot come would be the same guess the flag exists to remove.
ResetLog("2026-10-11 10:04:34.619 [info] Sync complete\n");

var unnamedFrom = Stopwatch.GetTimestamp();
var unnamed = LogQueue.WaitForCompletion(null, TimeSpan.FromSeconds(5), Thread.Sleep);

Check(
    "an invocation that sent nothing by name times out at once instead of waiting",
    NodeRead.Bool(unnamed, "completed") == false && Stopwatch.GetElapsedTime(unnamedFrom) < TimeSpan.FromSeconds(2),
    unnamed.ToJsonString());

try
{
    File.Delete(fixture);
}
catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
{
    // The check's own temporary file: failing to delete it is not a failure of anything asserted here.
}

Console.WriteLine();
Console.WriteLine($"{failures} failed, 0 skipped");
return failures == 0 ? 0 : 1;
