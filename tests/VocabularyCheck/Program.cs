using DoricoUse.Cli;
using DoricoUse.Cli.Dorico;

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
Console.WriteLine($"{failures} failed, 0 skipped");
return failures == 0 ? 0 : 1;
