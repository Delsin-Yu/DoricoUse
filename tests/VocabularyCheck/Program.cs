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
    ("Type", EnumVocabulary.ValuesForKey("Type")),
    ("OptionsType", EnumVocabulary.ValuesForKey("OptionsType")),
})
{
    Check($"{key} is a parameter this build checks", values.Count > 0);
    Check($"every {key} value looks like a Dorico spelling", values.All(EnumVocabulary.IsSpelling), string.Join(',', values));
    Check($"no {key} value is listed twice", values.Distinct(StringComparer.Ordinal).Count() == values.Count);
}

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
Check("and it carries no queue sentence", NodeRead.Str(DialogModel.Payload(DialogModel.Describe(Stranger() with { WindowClass = "Qt6101QWindowIcon" }), new DialogProbeInfo("win32+uia", 3)), "note") is null);

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

Console.WriteLine();
Console.WriteLine("== the refusal codes and the exit codes they promise");

foreach (var code in new[]
{
    "DialogNotFound", "WrongDialog", "ButtonNotFound", "ButtonNotInvokable", "UnknownDialog", "DestructiveRefused", "PickerNotSettable",
})
{
    Check($"{code} is a refusal this build defines", DialogModel.RefusalCodes.Contains(code));
}

Check("and there are no others", DialogModel.RefusalCodes.Count == 7, string.Join(',', DialogModel.RefusalCodes));
Check("a refusal exits 1, because it is the caller's fault", DialogModel.RefusalExitCode == 1);
Check("a probe failure exits 2, because it is not", DialogModel.ProbeFailureExitCode == 2);

Console.WriteLine();
Console.WriteLine("== the catalogue: one entry, one measurement, one policy");

foreach (var entry in DialogCatalogue.Entries)
{
    Check($"{entry.Id} has a matcher", entry.TitleGlobs.Count > 0 || entry.RequiredButtonLabels.Count > 0);
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
    // matcher agree" means: the labels the entry requires are the labels its buttons carry.
    var buttons = entry.Buttons
        .Select(button => new DialogButtonInput(
            button.MatchLabels.Count > 0 ? button.MatchLabels[0] : "unlabelled",
            button.MatchClassicIds.Count > 0
                ? button.MatchClassicIds[0]
                : button.MatchAutomationIdTails.Count > 0
                    ? "DoricoQApplication.root.QMLTYPE_1.Button_QMLTYPE_53." + button.MatchAutomationIdTails[0]
                    : null,
            true))
        .ToList();

    var matched = DialogModel.Describe(new DialogInput(
        Title: entry.TitleGlobs.Count > 0 ? entry.TitleGlobs[0].Replace("*", "x", StringComparison.Ordinal) : "a caption this entry does not constrain",
        WindowClass: entry.Id == "dorico.folder-picker" ? "#32770" : "Qt6101QWindow",
        Owner: null,
        TextSource: "none",
        TextLines: [],
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
Console.WriteLine("== the payload an agent reads");

var exportPayload = DialogModel.Payload(export, new DialogProbeInfo("win32+uia", 12, Nodes: 41));

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

var degraded = DialogModel.Payload(export, new DialogProbeInfo("win32", 9, Degraded: true, Error: "UI Automation refused"));

Check("a degraded probe carries its error", NodeRead.Str(NodeRead.Obj(degraded, "probe"), "error") == "UI Automation refused");
Check("and says it was degraded", NodeRead.Bool(NodeRead.Obj(degraded, "probe"), "degraded") == true);

var warned = DialogModel.Payload(stranger, new DialogProbeInfo("win32+uia", 4), "warning text");

Check("an allowed-unknown press carries its warning", NodeRead.Str(warned, "warning") == "warning text");
Check("a dialog with no text reports no description", NodeRead.Str(DialogModel.Payload(stranger, new DialogProbeInfo("win32+uia", 4)), "description") is null);

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
Console.WriteLine("== the execution verdict, from a fixture log (no Dorico: a check cannot raise a modal dialog)");

// Both defects this section guards were found by running the tool against a real Dorico dialog, which no check can
// raise. What a check can do is write Dorico's lines into a file from another thread while the verdict waits -
// which is what Dorico does while the CLI waits - and that is what these cases do. The measured shape of the real
// thing is what makes them worth writing: the offset is taken at a line boundary, and the command's own Posting
// line is then the first line the log gains, which is the line the verdict is made of.
var fixture = Path.Combine(Path.GetTempPath(), $"dorico-use-logqueue-check-{Environment.ProcessId}.log");

void WriteLater(string text, int delayMs) => _ = Task.Run(async () =>
{
    await Task.Delay(delayMs).ConfigureAwait(false);
    File.AppendAllText(fixture, text);
});

File.WriteAllText(fixture, "2026-10-11 05:42:42.463 [info] ping response received in ipc://Dorico6.lic\n");
LogQueue.Begin(fixture);
LogQueue.Mark("NoteInput.Enter");
WriteLater(
    "2026-10-11 05:42:49.593 [info] Posting command (requested): NoteInput.Enter\n"
    + "2026-10-11 05:42:49.593 [info] Queue after push: {NoteInput.Enter} \n",
    150);

var onTheQueue = LogQueue.Verdict(TimeSpan.FromMilliseconds(600));

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

File.WriteAllText(fixture, "2026-10-11 05:15:50.603 [info] Sync complete\n");
LogQueue.Begin(fixture);
LogQueue.Mark("NoteInput.Enter");
WriteLater(
    "2026-10-11 05:15:50.603 [info] Posting command (requested): NoteInput.Enter\n"
    + "2026-10-11 05:15:50.603 [info] Executing command: NoteInput.Enter\n",
    150);

var executed = LogQueue.Verdict(TimeSpan.FromMilliseconds(600));

Check(
    "an Executing line right after the Posting line makes it ran",
    NodeRead.Str(executed, "state") == "ran",
    NodeRead.Str(executed, "state"));

File.WriteAllText(fixture, "2026-10-11 05:44:09.452 [info] Auto-save skipped\n");
LogQueue.Begin(fixture);
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

File.WriteAllText(fixture, "2026-10-11 05:44:09.452 [info] Auto-save skipped\n");
LogQueue.Begin(fixture);
LogQueue.Mark("NoteInput.Enter");
LogQueue.Acknowledge("NoteInput.Enter", "kOK");

var nothing = LogQueue.Verdict(TimeSpan.FromMilliseconds(300));

Check(
    "a kOK with no Posting line after the wait is unknown: the wait is bounded, and what it saw is all it claims",
    NodeRead.Str(nothing, "state") == "unknown",
    NodeRead.Str(nothing, "state"));

// The other half of the offset rule: the fragment of a line Dorico was still writing when the offset was taken must
// not be read as a line. The fix above must not have turned the drop off, only made it conditional.
File.WriteAllText(fixture, "2026-10-11 05:44:09.452 [info] Auto-save ski");
LogQueue.Begin(fixture);
LogQueue.Mark("File.Open");
WriteLater(
    "pped\n2026-10-11 05:45:01.100 [info] Posting command (requested): File.Open File=C:\\scores\\a.dorico\n"
    + "2026-10-11 05:45:01.101 [info] Executing command: File.Open?File=C:\\scores\\a.dorico\n",
    150);

var afterPartial = LogQueue.Verdict(TimeSpan.FromMilliseconds(600));

Check(
    "a half-written line at the offset is still dropped rather than read as a Posting line",
    NodeRead.Str(afterPartial, "state") == "ran",
    NodeRead.Str(afterPartial, "state"));

File.WriteAllText(fixture, "2026-10-11 05:44:09.452 [info] Auto-save skipped\n");
LogQueue.Begin(fixture);

Check(
    "an invocation that posted nothing by name carries no execution field at all, instead of an unknown about nothing",
    LogQueue.Verdict(TimeSpan.FromMilliseconds(200)) is null);

File.WriteAllText(fixture, "2026-10-11 05:44:09.452 [info] Auto-save skipped\n");
LogQueue.Begin(fixture);
WriteLater("2026-10-11 05:46:00.000 [info] Posting command (requested): NoteInput.Enter\n", 100);

var someoneElses = LogQueue.Verdict(LogQueue.PassiveBudget);

Check(
    "a posting the host sent on its own is still reported as queued when nothing executed it",
    NodeRead.Str(someoneElses, "state") == "queued"
        && NodeRead.Str(someoneElses, "evidence")?.Contains("posted by the host", StringComparison.Ordinal) == true,
    NodeRead.Str(someoneElses, "evidence"));

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
