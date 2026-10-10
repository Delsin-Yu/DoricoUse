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

var vocabulary = EnumVocabulary.Shared;

Console.WriteLine("== the table itself");
Check("there is always a vocabulary, with nothing to install", vocabulary.Parameters > 0, vocabulary.Parameters.ToString());
Check("and it holds a usable number of values", vocabulary.Spellings >= 30, vocabulary.Spellings.ToString());

foreach (var (key, values) in new[]
{
    ("LogDuration", vocabulary.ValuesForKey("LogDuration")),
    ("NoteInputMode", vocabulary.ValuesForKey("NoteInputMode")),
    ("WindowMode", vocabulary.ValuesForKey("WindowMode")),
    ("RhythmicGridResolutionValue", vocabulary.ValuesForKey("RhythmicGridResolutionValue")),
    ("Type", vocabulary.ValuesForKey("Type")),
    ("OptionsType", vocabulary.ValuesForKey("OptionsType")),
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
    NoteValue.AllSpellings.All(spelling => vocabulary.Check(spelling) == VocabularyVerdict.Known),
    string.Join(',', NoteValue.AllSpellings.Where(s => vocabulary.Check(s) != VocabularyVerdict.Known)));
Check(
    "the ten spellings the binary's own LogDuration template lists are all known",
    NoteValue.Spellings.All(spelling => vocabulary.Check(spelling) == VocabularyVerdict.Known));

foreach (var mode in new[] { "kInsert", "kOverwrite", "kChordMerge" })
{
    Check($"{mode} is a note input mode this build knows", vocabulary.Check(mode) == VocabularyVerdict.Known);
}

foreach (var optionsType in new[]
{
    DoricoProtocol.OptionsEngraving, DoricoProtocol.OptionsLayout, DoricoProtocol.OptionsNotation,
})
{
    Check($"{optionsType} is an option type this build knows", vocabulary.ValuesForKey("OptionsType").Contains(optionsType));
}

foreach (var accidental in new[] { "kNatural", "kSharp", "kFlat", "kDoubleSharp", "kDoubleFlat" })
{
    Check($"{accidental} is an accidental this build derives and knows", vocabulary.Check(accidental) == VocabularyVerdict.Known);
}

Console.WriteLine();
Console.WriteLine("== what a value's shape means");
Check("a known spelling is known", vocabulary.Check("kQuaver") == VocabularyVerdict.Known);
Check("a misspelling is unknown", vocabulary.Check("kQuavor") == VocabularyVerdict.Unknown);
Check("a written-out note value is not a spelling", vocabulary.Check("crotchet") == VocabularyVerdict.NotASpelling);
Check("a fraction is not a spelling", vocabulary.Check("1/4") == VocabularyVerdict.NotASpelling);
Check("a file path is not a spelling", vocabulary.Check(@"C:\scores\a.dorico") == VocabularyVerdict.NotASpelling);
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
    vocabulary.Refuse("--duration", "LogDuration", "kQuavor");
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

Check("the value the parameter really takes is not refused", !Throws(() => vocabulary.Refuse("--duration", "LogDuration", "kCrotchet")));
Check("a number is not refused", !Throws(() => vocabulary.Refuse("--param", "LogDuration", "4")));
Check("an unknown parameter is never refused", !Throws(() => vocabulary.Refuse("--param", "SomeParameterFromANewerDorico", "kSomethingNew")));
Check("no parameter at all is never refused", !Throws(() => vocabulary.Refuse("--value", null, "kSomethingNew")));
Check("--allow-unknown-enum overrides the refusal", !Throws(() => vocabulary.Refuse("--duration", "LogDuration", "kQuavor", allowUnknown: true)));

Console.WriteLine();
Console.WriteLine("== suggestions");
Check("a typo points at the value it meant", vocabulary.Suggest("kQuavor", "LogDuration").FirstOrDefault() == "kQuaver", string.Join(',', vocabulary.Suggest("kQuavor", "LogDuration")));
Check("a typo in another parameter's value still finds it", vocabulary.Suggest("kSemibrefe").Contains("kSemibreve"), string.Join(',', vocabulary.Suggest("kSemibrefe")));
Check("the parameter's own values come first", vocabulary.Suggest("kWriteMod", "WindowMode").FirstOrDefault() == "kWriteMode", string.Join(',', vocabulary.Suggest("kWriteMod", "WindowMode")));
Check("a value with nothing near it suggests nothing", vocabulary.Suggest("kZzzzzzzz").Count == 0, string.Join(',', vocabulary.Suggest("kZzzzzzzz")));

Console.WriteLine();
Console.WriteLine("== what a payload reports");
var described = vocabulary.Describe([("LogDuration", "kCrotchet", vocabulary.Check("kCrotchet"))]);
Check("the report says the table is built in", NodeRead.Str(described, "source") == "built in");
Check("it counts its parameters and values", NodeRead.Int(described, "parameters") == vocabulary.Parameters && NodeRead.Int(described, "values") == vocabulary.Spellings);
Check("it says what is checked and what is not", (NodeRead.Str(described, "note") ?? string.Empty).Contains("sent unchecked"));
Check("and a looked-at value carries its verdict", NodeRead.Str(NodeRead.Arr(described, "lookedAt")?[0], "verdict") == "known");

Console.WriteLine();
Console.WriteLine($"{failures} failed, 0 skipped");
return failures == 0 ? 0 : 1;
