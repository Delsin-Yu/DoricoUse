using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using DoricoUse.Cli;
using DoricoUse.Cli.Dorico;

// A self-check for the vocabulary: the extractor that reads `k` spellings off a Dorico binary, and the lookup
// the CLI refuses an unknown value with.
//
//   dotnet build tests/VocabularyCheck/VocabularyCheck.csproj
//   ./tests/VocabularyCheck/bin/Debug/net10.0/vocabulary-check.exe
//
// It needs neither the 154 MB executable nor Dorico: extraction is asserted against a synthetic binary this
// program writes itself, and lookup against a synthetic vocabulary. The two checks that do touch this machine
// (the vocabulary somebody extracted here, and a built CLI) report SKIP when they are not there, so the output
// never claims more than it checked.

var failures = 0;
var skipped = 0;

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

void Skip(string name)
{
    skipped++;
    Console.WriteLine($"  skip {name}");
}

void Head(string title) => Console.WriteLine($"== {title}");

// ---------------------------------------------------------------- a synthetic vocabulary

Head("enum lookup, against a synthetic vocabulary");

// Small on purpose: eight spellings, one parameter whose values the binary ties them to, and one spelling
// (kOK) that is only three characters long.
const string SyntheticVocabulary = """
{
  "format": "dorico-use vocabulary",
  "formatVersion": 1,
  "extractedAtUtc": "2026-01-02T03:04:05Z",
  "extractedBy": "tests/VocabularyCheck",
  "source": {
    "path": "C:\\synthetic\\Dorico9.exe",
    "bytes": 4242,
    "sha256": "0000000000000000000000000000000000000000000000000000000000000000",
    "lastWriteUtc": "2026-01-01T00:00:00Z",
    "version": { "fileVersion": "9.9.9.1", "productVersion": "9.9.9" }
  },
  "counts": { "enums": 8, "types": 2, "commandNames": 1, "optionPaths": 1, "names": 1, "templates": 1 },
  "enums": ["kOK", "kQuaver", "kCrotchet", "kMinim", "kSemiquaver", "kWriteMode", "kInsert", "kOverwrite"],
  "parameterValues": { "LogDuration": ["kQuaver", "kCrotchet", "kMinim", "kSemiquaver"] }
}
""";

var scratch = Path.Combine(Path.GetTempPath(), "vocabulary-check-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(scratch);
var vocabularyPath = Path.Combine(scratch, "vocabulary.json");
File.WriteAllText(vocabularyPath, SyntheticVocabulary);

var vocabulary = EnumVocabulary.Load(vocabularyPath);

Check("the vocabulary is read", vocabulary.Present, vocabulary.AbsentReason);
Check("every spelling is counted", vocabulary.Spellings == 8, vocabulary.Spellings.ToString());
Check(
    "the Dorico it came from is kept",
    vocabulary.SourceVersion == "9.9.9.1",
    vocabulary.SourceVersion);

Check("a known spelling is known", vocabulary.Check("kQuaver") == VocabularyVerdict.Known);
Check("a three-letter spelling is known (kOK)", vocabulary.Check("kOK") == VocabularyVerdict.Known);
Check("a misspelling is unknown", vocabulary.Check("kQuavor") == VocabularyVerdict.Unknown);
Check(
    "a note value the CLI resolves itself is not an enum spelling",
    vocabulary.Check("crotchet") == VocabularyVerdict.NotASpelling);
Check("a number is not an enum spelling", vocabulary.Check("1/4") == VocabularyVerdict.NotASpelling);
Check("lowercase after k is not a spelling", !EnumVocabulary.IsSpelling("kquaver"));
Check("a bare k is not a spelling", !EnumVocabulary.IsSpelling("k"));
Check("a path is not a spelling", !EnumVocabulary.IsSpelling("C:\\scores\\a.dorico"));
Check("kWriteMode is a spelling", EnumVocabulary.IsSpelling("kWriteMode"));

var nearest = vocabulary.Suggest("kQuavor");

Check("the nearest spelling is found", nearest.Contains("kQuaver"), string.Join(", ", nearest));
Check("the nearest spelling comes first", nearest.Count > 0 && nearest[0] == "kQuaver", string.Join(", ", nearest));
Check(
    "a shared beginning beats a shared tail",
    vocabulary.Suggest("kSemibreve").FirstOrDefault() == "kSemiquaver",
    string.Join(", ", vocabulary.Suggest("kSemibreve")));
Check(
    "the values the binary ties to the parameter are offered first",
    vocabulary.Suggest("kQuavr", "LogDuration").FirstOrDefault() == "kQuaver",
    string.Join(", ", vocabulary.Suggest("kQuavr", "LogDuration")));
Check(
    "a parameter with no template offers nothing",
    vocabulary.ValuesForKey("RhythmicGridResolutionValue").Count == 0);
Check("a parameter with a template offers its values", vocabulary.ValuesForKey("LogDuration").Count == 4);

// The refusal itself: the shape an agent reads, and the exit code a shell reads.
var refusal = (CliException?)null;

try
{
    vocabulary.Refuse("--param", "LogDuration", "kQuavor");
}
catch (CliException ex)
{
    refusal = ex;
}

Check("an unknown value is refused", refusal is not null);
Check("the refusal is InvalidArgument", refusal?.Code == "InvalidArgument", refusal?.Code);
Check("the refusal exits 1", refusal?.ExitCode == 1, refusal?.ExitCode.ToString() ?? "not refused");
Check(
    "the refusal names the parameter and the value",
    NodeRead.Str(refusal?.Details, "key") == "LogDuration" && NodeRead.Str(refusal?.Details, "value") == "kQuavor");
Check(
    "the refusal carries the suggestions",
    NodeRead.Arr(refusal?.Details, "suggestions") is { Count: > 0 } suggestions
        && suggestions.Any(node => NodeRead.Text(node) == "kQuaver"));
Check(
    "the refusal carries the values the binary ties to the parameter",
    NodeRead.Arr(refusal?.Details, "valuesForKey") is { Count: 4 });
Check(
    "the refusal states which vocabulary refused it",
    NodeRead.Obj(refusal?.Details, "vocabulary") is { } report
        && NodeRead.Bool(report, "present") == true
        && NodeRead.Str(NodeRead.Obj(report, "extractedFrom"), "version") == "9.9.9.1");
Check(
    "the refusal says what an unknown value would have done",
    refusal?.Message.Contains("kOK", StringComparison.Ordinal) == true
        && refusal.Message.Contains("--allow-unknown-enum", StringComparison.Ordinal),
    refusal?.Message);

string? allowed = null;

try
{
    vocabulary.Refuse("--param", "LogDuration", "kQuavor", allowUnknown: true);
    allowed = "no exception";
}
catch (CliException ex)
{
    allowed = ex.Code;
}

Check("--allow-unknown-enum lets the value through", allowed == "no exception", allowed);

var knownAccepted = true;

try
{
    vocabulary.Refuse("--grid", "RhythmicGridResolutionValue", "kSemiquaver");
}
catch (CliException)
{
    knownAccepted = false;
}

Check("a known value is accepted", knownAccepted);

var described = vocabulary.Describe([("LogDuration", "kQuaver", VocabularyVerdict.Known)]);

Check("the payload says the vocabulary is present", NodeRead.Bool(described, "present") == true);
Check("the payload counts its spellings", NodeRead.Long(described, "spellings") == 8);
Check("the payload says where it came from", NodeRead.Str(described, "path") == vocabularyPath);
Check("the payload records what was looked at", NodeRead.Arr(described, "lookedAt") is { Count: 1 });

// The machine where nobody ran the extractor: the CLI has to work there, and say so instead of skipping.
Head("enum lookup, with no vocabulary on the machine");

var missing = EnumVocabulary.Load(Path.Combine(scratch, "not-there.json"));

Check("a missing file is not a failure", !missing.Present);
Check("the absence is explained", missing.AbsentReason is not null, missing.AbsentReason);
Check(
    "a value cannot be checked without a vocabulary",
    missing.Check("kQuavor") == VocabularyVerdict.NoVocabulary);
Check("a value is not an enum spelling without a vocabulary", missing.Check("crotchet") == VocabularyVerdict.NotASpelling);

var sentUnchecked = true;

try
{
    missing.Refuse("--param", "LogDuration", "kQuavor");
}
catch (CliException)
{
    sentUnchecked = false;
}

Check("the value is accepted when there is no vocabulary", sentUnchecked);
Check(
    "the payload says the value went unchecked",
    NodeRead.Bool(missing.Describe(), "present") == false
        && NodeRead.Str(missing.Describe(), "reason") is { Length: > 0 }
        && NodeRead.Str(missing.Describe(), "note")?.Contains("extract_vocabulary.py", StringComparison.Ordinal)
            == true,
    NodeRead.Str(missing.Describe(), "note"));

var foreignPath = Path.Combine(scratch, "foreign.json");
File.WriteAllText(foreignPath, """{"format":"something else","enums":["kQuaver"]}""");
var foreign = EnumVocabulary.Load(foreignPath);

Check("a foreign JSON is not read as a vocabulary", !foreign.Present);
Check(
    "the format is what it was told apart by",
    foreign.AbsentReason?.Contains("format", StringComparison.Ordinal) == true,
    foreign.AbsentReason);

var malformedPath = Path.Combine(scratch, "malformed.json");
File.WriteAllText(malformedPath, "{ not json");
var malformed = EnumVocabulary.Load(malformedPath);

Check("a malformed file is not a crash", !malformed.Present, malformed.AbsentReason);

// ---------------------------------------------------------------- the extractor

Head("the extractor, against a synthetic executable");

var extractor = Find("tools/extract_vocabulary.py");
var python = FindPython();

if (extractor is null)
{
    Skip("every extractor check (tools/extract_vocabulary.py was not found)");
}
else if (python is null)
{
    Skip("every extractor check (no python on PATH)");
}
else
{
    // A binary with the strings a Dorico executable carries, and two traps: a k-shaped window inside
    // non-printable bytes (what machine code looks like to a scanner) and a UTF-16 spelling.
    var binaryPath = Path.Combine(scratch, "Synthetic.exe");
    File.WriteAllBytes(binaryPath, SyntheticBinary());

    var extractedPath = Path.Combine(scratch, "nested", "extracted.json");

    var run = Run(python, [extractor, binaryPath, "--out", extractedPath]);
    var summary = run.Output;

    Check("the extractor exits 0", run.ExitCode == 0, run.Output);
    Check("it wrote to a directory that did not exist", File.Exists(extractedPath));

    var extracted = JsonNode.Parse(File.ReadAllText(extractedPath)) as JsonObject;
    var spellings = (extracted?["enums"] as JsonArray ?? [])
        .Select(node => NodeRead.Text(node) ?? string.Empty)
        .ToHashSet(StringComparer.Ordinal);

    Check("the file declares the format", NodeRead.Str(extracted, "format") == "dorico-use vocabulary");
    Check("the file declares a format version", NodeRead.Long(extracted, "formatVersion") == 1);

    foreach (var spelling in new[] { "kQuaver", "kCrotchet", "kSemiquaver", "kWriteMode", "kInsert", "kOK" })
    {
        Check($"a whole string literal is found ({spelling})", spellings.Contains(spelling));
    }

    Check(
        "a value the binary spells in a command template is found (kBreve)",
        spellings.Contains("kBreve"),
        string.Join(", ", spellings.Order(StringComparer.Ordinal)));
    Check("a spelling after `::` is found (kBeamAccelRit1)", spellings.Contains("kBeamAccelRit1"));
    Check(
        "a k-shaped window inside machine code is not a spelling (kQ7x)",
        !spellings.Contains("kQ7x"));
    Check("a UTF-16 spelling is not read", !spellings.Contains("kUtf16Only"));
    Check(
        "the count is exactly what was planted",
        NodeRead.Obj(extracted, "counts") is { } counts && NodeRead.Long(counts, "enums") == 8,
        string.Join(", ", spellings.Order(StringComparer.Ordinal)));

    var template = (extracted?["templates"] as JsonArray ?? []).Select(node => NodeRead.Text(node)).ToList();

    Check(
        "the command template is kept whole",
        template.Contains("NoteInput.NoteValue?LogDuration=kBreve&SetBasedOnButtonCheckedState"),
        string.Join(" | ", template));
    Check(
        "the parameter value is tied to its parameter",
        NodeRead.Arr(NodeRead.Obj(extracted, "parameterValues"), "LogDuration") is { Count: 1 } logDuration
            && NodeRead.Text(logDuration[0]) == "kBreve");

    Check(
        "an RTTI type name is found",
        (extracted?["types"] as JsonArray ?? []).Any(node => NodeRead.Text(node) == "NoteEventDefinition"));
    Check(
        "a command name is found",
        (extracted?["commandNames"] as JsonArray ?? []).Any(node => NodeRead.Text(node) == "NoteInput.Pitch"));
    Check(
        "an option path is found",
        (extracted?["optionPaths"] as JsonArray ?? [])
            .Any(node => NodeRead.Text(node) == "rhythmDotPositionOptions.noteDotGap"));
    Check(
        "a PascalCase name is found",
        (extracted?["names"] as JsonArray ?? [])
            .Any(node => NodeRead.Text(node) == "DynamicGroupEventDefinition"));

    var digest = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(binaryPath))).ToLowerInvariant();
    var source = NodeRead.Obj(extracted, "source");

    Check("the source hash is the file it read", NodeRead.Str(source, "sha256") == digest);
    Check(
        "the source size is the file it read",
        NodeRead.Long(source, "bytes") == new FileInfo(binaryPath).Length,
        NodeRead.Long(source, "bytes")?.ToString());
    Check("it names the file it read", NodeRead.Str(source, "path") == binaryPath);
    Check(
        "the version resource is read from the executable",
        NodeRead.Str(NodeRead.Obj(source, "version"), "fileVersion") == "9.9.9.4021",
        NodeRead.Str(NodeRead.Obj(source, "version"), "fileVersion"));
    Check("the scan counts what it read", NodeRead.Long(extracted?["scan"], "stringLiterals") is > 0);
    Check("the method is written down", NodeRead.Str(extracted?["method"], "scan") is { Length: > 0 });

    Check("the summary names the numbers", summary.Contains("enums", StringComparison.Ordinal), summary);

    // --summary-only writes nothing: that is the way to look at a binary without leaving a file behind.
    var nothingPath = Path.Combine(scratch, "must-not-exist.json");
    var summaryOnly = Run(python, [extractor, binaryPath, "--out", nothingPath, "--summary-only"]);

    Check("--summary-only exits 0", summaryOnly.ExitCode == 0, summaryOnly.Output);
    Check("--summary-only writes nothing", !File.Exists(nothingPath));

    var toStdout = Run(python, [extractor, binaryPath, "--stdout", "--quiet"]);

    Check("--stdout exits 0", toStdout.ExitCode == 0, toStdout.Output);
    Check(
        "--stdout writes the vocabulary to stdout",
        (JsonNode.Parse(toStdout.Output) as JsonObject) is { } piped
            && NodeRead.Long(NodeRead.Obj(piped, "counts"), "enums") == 8);

    // A path that is not a file is a usage error, not a crash and not an empty vocabulary.
    var noFile = Run(python, [extractor, Path.Combine(scratch, "no-such-dorico.exe")]);

    Check("a path that is not a file exits 2", noFile.ExitCode == 2, noFile.Output);

    // The CLI looks in one place and the extractor writes to one place: the two have to be the same place, or
    // the extracted file would never be found.
    var defaultPath = Run(
        python,
        ["-c", "import sys; sys.path.insert(0, r'" + Path.GetDirectoryName(extractor) + "'); "
            + "import extract_vocabulary as v; print(v.default_output_path())"]);

    Check(
        "the extractor's default output is where the CLI looks",
        defaultPath.ExitCode == 0 && defaultPath.Output.Trim() == EnumVocabulary.PerUserPath,
        $"{defaultPath.Output.Trim()} vs {EnumVocabulary.PerUserPath}");

    Environment.SetEnvironmentVariable(EnumVocabulary.PathVariable, vocabularyPath);

    Check(
        "the lookup path can be pointed elsewhere",
        EnumVocabulary.DefaultPath == vocabularyPath,
        EnumVocabulary.DefaultPath);

    Environment.SetEnvironmentVariable(EnumVocabulary.PathVariable, null);

    Check(
        "and falls back to the per-user file",
        EnumVocabulary.DefaultPath == EnumVocabulary.PerUserPath,
        EnumVocabulary.DefaultPath);
}

// ---------------------------------------------------------------- this machine

Head("the vocabulary extracted on this machine");

if (!File.Exists(EnumVocabulary.PerUserPath))
{
    Skip("every check on the extracted vocabulary (nobody ran the extractor here)");
}
else
{
    var real = EnumVocabulary.Load(EnumVocabulary.PerUserPath);

    Check("it is readable and present", real.Present, real.AbsentReason);
    Check("it holds more than a handful of spellings", real.Spellings > 1000, real.Spellings.ToString());
    Check("it records the Dorico it came from", real.SourceVersion is { Length: > 0 }, real.SourceVersion);

    foreach (var spelling in new[] { "kQuaver", "kCrotchet", "kMinim", "kWriteMode", "kInsert", "kOverwrite" })
    {
        Check($"it holds {spelling}", real.Check(spelling) == VocabularyVerdict.Known);
    }

    var realNear = real.Suggest("kQuavor");

    Check("it refuses a misspelling's suggestion", real.Check("kQuavor") == VocabularyVerdict.Unknown);
    Check("it suggests kQuaver for kQuavor", realNear.Contains("kQuaver"), string.Join(", ", realNear));
    Check(
        "the vocabulary is a per-user file, not a repository file",
        Path.GetFullPath(EnumVocabulary.PerUserPath).StartsWith(
            Path.GetFullPath(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)),
            StringComparison.OrdinalIgnoreCase),
        EnumVocabulary.PerUserPath);
}

// ---------------------------------------------------------------- the CLI, end to end

Head("the CLI, end to end (against the synthetic vocabulary, with no host)");

var cli = FindCli();
JsonObject? probe = null;

if (cli is not null)
{
    var probeRun = RunCli(cli, vocabularyPath, [
        "command", "send", "--name", "NoteInput.NoteValue", "--param", "LogDuration=kCrotchet", "--dry-run",
    ]);

    try
    {
        probe = JsonNode.Parse(probeRun.Output) as JsonObject;
    }
    catch (Exception)
    {
        // A build that predates this feature: reported as a skip below, because nothing can be concluded from
        // an old binary about the sources it was not built from.
    }
}

if (cli is null || probe?["plan"]?["vocabulary"] is null)
{
    Skip(
        cli is null
            ? "every CLI check (no built dorico-use.dll: build one, or set DORICO_USE_CLI_DLL to its path)"
            : $"every CLI check (the build at {cli} predates the vocabulary check: rebuild it first)");
}
else
{
    // Neither call may reach the host: the refusal happens before it is asked to do anything, and --dry-run
    // never asks. That is what makes this safe to run while somebody is working in Dorico.
    var refused = RunCli(cli, vocabularyPath, [
        "command", "send", "--name", "NoteInput.NoteValue", "--param", "LogDuration=kQuavor",
    ]);

    JsonObject? refusedPayload = null;

    try
    {
        refusedPayload = JsonNode.Parse(refused.Output) as JsonObject;
    }
    catch (Exception)
    {
        // Reported as a failed check below.
    }

    Check("the CLI exits 1 for an unknown value", refused.ExitCode == 1, $"{refused.ExitCode}: {refused.Output}");
    Check("the CLI says InvalidArgument", NodeRead.Str(NodeRead.Obj(refusedPayload, "error"), "code") == "InvalidArgument");
    Check(
        "the CLI carries the suggestions",
        NodeRead.Arr(NodeRead.Obj(NodeRead.Obj(refusedPayload, "error"), "data"), "suggestions") is { Count: > 0 }
            data && data.Any(node => NodeRead.Text(node) == "kQuaver"));
    Check(
        "the CLI used the vocabulary it was pointed at",
        NodeRead.Obj(NodeRead.Obj(refusedPayload, "error"), "data") is { } details
            && NodeRead.Long(NodeRead.Obj(details, "vocabulary"), "spellings") == 8);

    var accepted = RunCli(cli, vocabularyPath, [
        "command", "send", "--name", "NoteInput.NoteValue", "--param", "LogDuration=kCrotchet", "--dry-run",
    ]);

    Check("the CLI exits 0 for a known value", accepted.ExitCode == 0, $"{accepted.ExitCode}: {accepted.Output}");
    Check(
        "the CLI composes the command line it would have sent",
        NodeRead.Str(NodeRead.Obj(probe, "plan"), "command")
            == "NoteInput.NoteValue?LogDuration=kCrotchet");
    Check(
        "the CLI does not start a host to check a value",
        accepted.Output.Contains("no host was started", StringComparison.Ordinal));
}

try
{
    Directory.Delete(scratch, recursive: true);
}
catch (IOException)
{
    // A leftover temp directory is not a failed check.
}

// ---------------------------------------------------------------- helpers

string? Find(string relative)
{
    var from = new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory };

    foreach (var start in from)
    {
        var directory = new DirectoryInfo(start);

        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, relative.Replace('/', Path.DirectorySeparatorChar));

            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }
    }

    return null;
}

string? FindPython()
{
    foreach (var candidate in new[] { "python", "python3", "py" })
    {
        var probe = Run(candidate, ["--version"]);

        if (probe.ExitCode == 0)
        {
            return candidate;
        }
    }

    return null;
}

string? FindCli()
{
    if (Environment.GetEnvironmentVariable("DORICO_USE_CLI_DLL") is { Length: > 0 } configured
        && File.Exists(configured))
    {
        return configured;
    }

    foreach (var candidate in new[]
    {
        "src/DoricoUse.Cli/bin/Debug/net10.0/dorico-use.dll",
        "src/DoricoUse.Cli/bin/Release/net10.0/dorico-use.dll",
    })
    {
        if (Find(candidate) is { } found)
        {
            return found;
        }
    }

    return null;
}

(int ExitCode, string Output) RunCli(string dll, string vocabulary, string[] arguments)
{
    var environment = new Dictionary<string, string> { [EnumVocabulary.PathVariable] = vocabulary };

    return Run("dotnet", [dll, .. arguments], environment);
}

/// <summary>Runs a process and returns its exit code with stdout and stderr together.</summary>
(int ExitCode, string Output) Run(
    string file,
    string[] arguments,
    Dictionary<string, string>? environment = null)
{
    var start = new ProcessStartInfo(file)
    {
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
        WorkingDirectory = Directory.GetCurrentDirectory(),
    };

    foreach (var argument in arguments)
    {
        start.ArgumentList.Add(argument);
    }

    if (environment is not null)
    {
        foreach (var (key, value) in environment)
        {
            start.Environment[key] = value;
        }
    }

    try
    {
        using var process = Process.Start(start);

        if (process is null)
        {
            return (-1, "the process could not be started");
        }

        var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();

        process.WaitForExit();

        return (process.ExitCode, output.Trim());
    }
    catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or FileNotFoundException)
    {
        return (-1, ex.Message);
    }
}

/// <summary>
/// A stand-in for a Dorico executable: the string shapes the extractor reads, and the two it must not.
/// </summary>
static byte[] SyntheticBinary()
{
    using var stream = new MemoryStream();
    using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);

    // Some bytes before the first string, so the file is not only strings.
    writer.Write(new byte[] { 0x4d, 0x5a, 0x90, 0x00, 0x03, 0x00 });

    void Text(string value, bool utf16 = false)
    {
        writer.Write(utf16 ? Encoding.Unicode.GetBytes(value) : Encoding.ASCII.GetBytes(value));
        writer.Write((byte)0);
        writer.Write((byte)0);
    }

    Text("kQuaver");
    Text("kCrotchet");
    Text("kSemiquaver");
    Text("kWriteMode");
    Text("kInsert");

    // Three characters: Dorico stores kOK in a pool of NUL-separated short strings, and the extractor has to
    // read that pool the way the compiler wrote it.
    Text("kOK");
    Text("trueButton");

    // A command the binary spells out with its parameter values.
    Text("NoteInput.NoteValue?LogDuration=kBreve&SetBasedOnButtonCheckedState");

    // A qualified spelling, the shape `SMuFL::kBeamAccelRit1` has in the real binary.
    Text("SMuFL::kBeamAccelRit1");

    // RTTI, a command name, an option path and a node name.
    Text(".?AVNoteEventDefinition@@");
    Text("NoteInput.Pitch");
    Text("rhythmDotPositionOptions.noteDotGap");
    Text("DynamicGroupEventDefinition");

    // The version resource, UTF-16 like the real one: the key, then its value, NUL-separated.
    void VersionEntry(string key, string value)
    {
        writer.Write(Encoding.Unicode.GetBytes(key));
        writer.Write(new byte[] { 0x00, 0x00 });
        writer.Write(Encoding.Unicode.GetBytes(value));
        writer.Write(new byte[] { 0x00, 0x00 });
    }

    VersionEntry("FileVersion", "9.9.9.4021");
    VersionEntry("ProductName", "Dorico for Windows");

    // Trap 1: a k-shaped printable window inside non-printable bytes, which is what machine code looks like to
    // a scanner. NUL-bounded it is not, so it is not a spelling.
    writer.Write(new byte[] { 0x81, 0x82 });
    writer.Write(Encoding.ASCII.GetBytes("kQ7x"));
    writer.Write(new byte[] { 0x83, 0x84 });

    // Trap 2: a spelling stored as UTF-16. The scan reads ASCII runs, and this must not come back as one.
    writer.Write(Encoding.Unicode.GetBytes("kUtf16Only"));
    writer.Write(new byte[] { 0x00, 0x00 });

    writer.Flush();
    return stream.ToArray();
}

Console.WriteLine();
Console.WriteLine($"{failures} failed, {skipped} skipped");
return failures == 0 ? 0 : 1;
