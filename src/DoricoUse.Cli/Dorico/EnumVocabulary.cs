using System.Text.Json;
using System.Text.Json.Nodes;
using DoricoUse.Cli.Json;

namespace DoricoUse.Cli.Dorico;

/// <summary>
/// What Dorico's binary knows, read off it: the <c>k</c> spellings its enum parameters take.
/// </summary>
/// <remarks>
/// <para>
/// Dorico answers <c>kOK</c> to <c>NoteInput.NoteValue?LogDuration=kCrotchet</c> whatever the value is, and an
/// unknown one silently becomes something else (a semibreve), so a typo is invisible in the ack and in the
/// status. The spellings are string literals in the binary, and
/// <c>tools/extract_vocabulary.py</c> is the extractor that reads them out.
/// </para>
/// <para>
/// The extracted file is deliberately <b>not</b> in this repository: it is derived from Steinberg's binary and
/// must not be redistributed. It is a per-user file next to <c>session.json</c>
/// (<c>%LOCALAPPDATA%\dorico-use\vocabulary.json</c>), which means this CLI has to work on a machine where
/// nobody ever ran the extractor. It does: <see cref="Present"/> is false there, every value is accepted, and
/// <see cref="Describe"/> says so in the payload instead of skipping the check in silence.
/// </para>
/// <para>
/// What a hit proves is bounded, and the wording everywhere here keeps it that way: the binary uses this
/// spelling <i>somewhere</i>. It does not prove that this parameter accepts it — the vocabulary is one flat
/// set of spellings plus, where the binary spells a template out (<c>InsertScope?InsertModeScope=kVoice</c>),
/// which values it uses for one named parameter. A value this file does not contain is very likely wrong; a
/// value it contains may still be wrong for the parameter in front of it, so the parameter values are offered
/// as a hint and never as the reason for a refusal.
/// </para>
/// </remarks>
internal sealed class EnumVocabulary
{
    /// <summary>The file name inside the CLI's per-user state directory.</summary>
    public const string FileName = "vocabulary.json";

    /// <summary>
    /// Points the lookup at another file. Set by the tests (they run against a synthetic vocabulary) and by
    /// anyone who keeps the extracted file somewhere else; unset, the per-user default is used.
    /// </summary>
    public const string PathVariable = "DORICO_USE_VOCABULARY";

    /// <summary>What the file writes into <c>format</c>, so a foreign JSON is not read as a vocabulary.</summary>
    public const string Format = "dorico-use vocabulary";

    /// <summary>How the file is produced, quoted in the payload when none is present.</summary>
    public const string ExtractionCommand = "python tools/extract_vocabulary.py <path to the Dorico executable>";

    private static readonly Lazy<EnumVocabulary> Cached = new(Load);

    private readonly HashSet<string> _spellings;
    private readonly Dictionary<string, string[]> _parameterValues;

    private readonly string? _sourceVersion;

    private EnumVocabulary(
        string path,
        string? absentReason,
        HashSet<string> spellings,
        Dictionary<string, string[]> parameterValues,
        JsonObject? source,
        JsonObject? counts,
        string? extractedAtUtc,
        int formatVersion)
    {
        Path = path;
        AbsentReason = absentReason;
        _spellings = spellings;
        _parameterValues = parameterValues;
        Source = source;
        Counts = counts;
        ExtractedAtUtc = extractedAtUtc;
        FormatVersion = formatVersion;
        _sourceVersion = NodeRead.Str(NodeRead.Obj(source, "version"), "fileVersion");
    }

    /// <summary>
    /// The vocabulary for this process, read once. A vocabulary is 970 KB
    /// (5384 spellings plus the raw material around them), so it is parsed at most once per invocation and
    /// only when a value actually has to be checked.
    /// </summary>
    public static EnumVocabulary Shared => Cached.Value;

    /// <summary>The per-user file the extractor writes by default, whether or not it exists.</summary>
    public static string PerUserPath { get; } = System.IO.Path.Combine(SessionStore.StateDirectory, FileName);

    /// <summary>Where the vocabulary is looked for: <see cref="PathVariable"/>, or the per-user default.</summary>
    public static string DefaultPath
        => Environment.GetEnvironmentVariable(PathVariable) is { Length: > 0 } custom ? custom : PerUserPath;

    /// <summary>Where this instance was read from (or looked for).</summary>
    public string Path { get; }

    /// <summary>True when the file was found, understood and has spellings.</summary>
    public bool Present => _spellings.Count > 0;

    /// <summary>Why nothing was read, when <see cref="Present"/> is false: a fact, not a failure.</summary>
    public string? AbsentReason { get; }

    /// <summary>How many <c>k</c> spellings the file holds.</summary>
    public int Spellings => _spellings.Count;

    /// <summary>The <c>source</c> block: which executable this came from, and its version.</summary>
    public JsonObject? Source { get; }

    /// <summary>The file's own counts: types, command names, option paths, names, templates.</summary>
    public JsonObject? Counts { get; }

    /// <summary>The format the file declares, reported so a stale file can be told apart.</summary>
    public int FormatVersion { get; }

    /// <summary>When the extraction ran, from the file's own <c>extractedAtUtc</c>.</summary>
    public string? ExtractedAtUtc { get; }

    /// <summary>The Dorico the vocabulary was extracted from, e.g. <c>6.2.30.6245</c>.</summary>
    public string? SourceVersion => _sourceVersion;

    /// <summary>
    /// Reads the vocabulary. Never throws: a missing, unreadable or foreign file is reported through
    /// <see cref="Present"/> and <see cref="AbsentReason"/>, because a machine without one is a normal machine.
    /// </summary>
    public static EnumVocabulary Load() => Load(DefaultPath);

    /// <inheritdoc cref="Load()"/>
    public static EnumVocabulary Load(string path)
    {
        JsonObject? json;

        try
        {
            if (!File.Exists(path))
            {
                return Empty(path, "no vocabulary file on this machine");
            }

            json = JsonNode.Parse(File.ReadAllText(path)) as JsonObject;

            if (json is null)
            {
                return Empty(path, "the file is not a JSON object");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return Empty(path, ex.Message);
        }

        if (NodeRead.Str(json, "format") != Format)
        {
            return Empty(path, $"the file does not declare format '{Format}'");
        }

        var spellings = new HashSet<string>(StringComparer.Ordinal);
        var parameterValues = new Dictionary<string, string[]>(StringComparer.Ordinal);

        foreach (var node in NodeRead.Arr(json, "enums") ?? [])
        {
            if (NodeRead.Text(node) is { Length: > 0 } spelling)
            {
                spellings.Add(spelling);
            }
        }

        if (NodeRead.Obj(json, "parameterValues") is { } parameters)
        {
            foreach (var (parameter, values) in parameters)
            {
                var list = new List<string>();

                foreach (var node in values as JsonArray ?? [])
                {
                    if (NodeRead.Text(node) is { Length: > 0 } value)
                    {
                        list.Add(value);
                    }
                }

                if (list.Count > 0)
                {
                    parameterValues[parameter] = [.. list];
                }
            }
        }

        var version = NodeRead.Long(json, "formatVersion");

        return new EnumVocabulary(
            path,
            null,
            spellings,
            parameterValues,
            NodeRead.Obj(json, "source")?.DeepClone() as JsonObject,
            NodeRead.Obj(json, "counts")?.DeepClone() as JsonObject,
            NodeRead.Str(json, "extractedAtUtc"),
            version is null ? 0 : (int)version.Value);
    }

    private static EnumVocabulary Empty(string path, string reason)
        => new(path, reason, [], [], null, null, null, 0);

    // ---------------------------------------------------------------- checking

    /// <summary>
    /// Whether a value has the shape of a Dorico enum spelling: <c>k</c>, an uppercase letter, then letters,
    /// digits or underscores (<c>kQuaver</c>, <c>kWriteMode</c>, <c>kInsert</c>). Everything else — a number,
    /// <c>true</c>, a path, a display name — is a value this vocabulary cannot speak about, and is left alone.
    /// </summary>
    public static bool IsSpelling(string? value)
    {
        if (value is null || value.Length < 3 || value[0] != 'k' || value[1] is < 'A' or > 'Z')
        {
            return false;
        }

        for (var index = 2; index < value.Length; index++)
        {
            var character = value[index];

            if (!char.IsAsciiLetterOrDigit(character) && character != '_')
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>What the vocabulary says about one value.</summary>
    public VocabularyVerdict Check(string value)
    {
        if (!IsSpelling(value))
        {
            return VocabularyVerdict.NotASpelling;
        }

        if (!Present)
        {
            return VocabularyVerdict.NoVocabulary;
        }

        return _spellings.Contains(value) ? VocabularyVerdict.Known : VocabularyVerdict.Unknown;
    }

    /// <summary>
    /// The values the binary is known to pair with one command parameter, from the templates it spells out
    /// (<c>InsertScope?InsertModeScope=kVoice</c>). Empty when the binary spells out no template for it, and
    /// empty for anything that is not a command parameter (an option path, say). This is a hint for a refusal
    /// message, never a condition: the templates are a handful, the spellings are 5384.
    /// </summary>
    public IReadOnlyList<string> ValuesForKey(string? key)
        => key is not null && _parameterValues.TryGetValue(key, out var values) ? values : [];

    /// <summary>
    /// The nearest spellings to a value that is not in the vocabulary: the ranking <c>file read --tree</c> uses
    /// for a name nobody has — the longest shared beginning first — with the edit distance breaking ties, so
    /// <c>kQuavor</c> finds <c>kQuaver</c> and <c>kSemibrefe</c> finds <c>kSemibreve</c>.
    /// </summary>
    /// <param name="key">
    /// The name the value belongs to: a command parameter (<c>LogDuration</c>) or an option path
    /// (<c>rhythmDotPositionOptions.noteDotGap</c>). When the binary ties that name to values, they are offered
    /// first, because they are the most likely answer.
    /// </param>
    public IReadOnlyList<string> Suggest(string value, string? key = null, int limit = 8)
    {
        var suggestions = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var known in Ranked(ValuesForKey(key), value, 4))
        {
            if (seen.Add(known))
            {
                suggestions.Add(known);
            }
        }

        foreach (var known in Ranked(_spellings, value, limit))
        {
            if (suggestions.Count >= limit)
            {
                break;
            }

            if (seen.Add(known))
            {
                suggestions.Add(known);
            }
        }

        return suggestions;
    }

    private static List<string> Ranked(IEnumerable<string> candidates, string value, int limit)
    {
        var ranked = new List<(int Prefix, int Distance, string Name)>();

        foreach (var candidate in candidates)
        {
            var prefix = SharedPrefix(value, candidate);

            // A candidate that shares nothing and is a very different length is noise, not a near miss; the
            // distance is still computed for the rest, and the caller only ever shows a handful.
            if (prefix == 0 && Math.Abs(candidate.Length - value.Length) > 4)
            {
                continue;
            }

            ranked.Add((prefix, Distance(value, candidate), candidate));
        }

        return
        [
            .. ranked
                .OrderByDescending(entry => entry.Prefix)
                .ThenBy(entry => entry.Distance)
                .ThenBy(entry => entry.Name, StringComparer.Ordinal)
                .Take(limit)
                .Select(entry => entry.Name),
        ];
    }

    private static int SharedPrefix(string left, string right)
    {
        var limit = Math.Min(left.Length, right.Length);
        var index = 0;

        while (index < limit && char.ToLowerInvariant(left[index]) == char.ToLowerInvariant(right[index]))
        {
            index++;
        }

        return index;
    }

    /// <summary>Levenshtein distance, cut off once it can no longer beat <paramref name="ceiling"/>.</summary>
    private static int Distance(string left, string right, int ceiling = 12)
    {
        if (Math.Abs(left.Length - right.Length) > ceiling)
        {
            return ceiling + 1;
        }

        var previous = new int[right.Length + 1];
        var current = new int[right.Length + 1];

        for (var column = 0; column <= right.Length; column++)
        {
            previous[column] = column;
        }

        for (var row = 1; row <= left.Length; row++)
        {
            current[0] = row;
            var best = current[0];

            for (var column = 1; column <= right.Length; column++)
            {
                var substitution = previous[column - 1]
                    + (char.ToLowerInvariant(left[row - 1]) == char.ToLowerInvariant(right[column - 1]) ? 0 : 1);

                current[column] = Math.Min(
                    Math.Min(current[column - 1] + 1, previous[column] + 1),
                    substitution);

                best = Math.Min(best, current[column]);
            }

            if (best > ceiling)
            {
                return ceiling + 1;
            }

            (previous, current) = (current, previous);
        }

        return previous[right.Length];
    }

    // ---------------------------------------------------------------- refusing

    /// <summary>
    /// Refuses a value the vocabulary does not contain, the way a mistyped argument is refused anywhere else in
    /// this CLI: <c>InvalidArgument</c>, exit code 1, and <c>error.data</c> carrying the suggestions and what the
    /// refusal rests on.
    /// </summary>
    /// <param name="what">Which argument carried it, e.g. <c>--param</c> or <c>--grid</c>.</param>
    /// <param name="key">The name the value belongs to: the command parameter, or the option path.</param>
    /// <param name="value">The value itself.</param>
    /// <param name="allowUnknown">
    /// Send it anyway. The vocabulary is a scan of one build's string literals, so a spelling it does not hold
    /// is very likely wrong — but "very likely" is not "certainly", and this is the way out for the caller who
    /// knows better than the file.
    /// </param>
    public void Refuse(string what, string? key, string value, bool allowUnknown = false)
    {
        if (allowUnknown || Check(value) != VocabularyVerdict.Unknown)
        {
            return;
        }

        var suggestions = Suggest(value, key);
        var parameterValues = ValuesForKey(key);

        var named = key is null ? string.Empty : $" for '{key}'";
        var nearest = suggestions.Count == 0
            ? "No close spelling was found."
            : $"The nearest spellings are {string.Join(", ", suggestions)}.";

        var tie = parameterValues.Count == 0
            ? string.Empty
            : $" The binary spells {key} with {string.Join(", ", parameterValues)}, so a value outside that list "
            + "is worth a second look.";

        throw new CliException(
            "InvalidArgument",
            $"'{value}'{named} is not a spelling this Dorico's vocabulary holds ({what}): the vocabulary "
            + $"extracted from {SourceVersion ?? "the Dorico executable"} has {Spellings} of them. {nearest}{tie} "
            + "Dorico answers kOK to an unknown enum value and silently substitutes another, so this is refused "
            + "rather than sent: use --allow-unknown-enum if the value is nonetheless the one you mean.",
            1)
        {
            Details = new JsonObject
            {
                ["argument"] = what,
                ["key"] = key,
                ["value"] = value,
                ["suggestions"] = Strings(suggestions),
                ["valuesForKey"] = parameterValues.Count == 0 ? null : Strings(parameterValues),
                ["vocabulary"] = Describe(),
            },
        };
    }

    // ---------------------------------------------------------------- reporting

    /// <summary>
    /// What to put in a payload so a reader can tell whether a value was checked at all. Present but empty on a
    /// machine without a vocabulary would be a silent skip, so absence is stated with the way to fix it.
    /// </summary>
    /// <param name="lookedAt">The values this invocation put through the vocabulary, and what it said.</param>
    public JsonObject Describe(IEnumerable<(string Key, string Value, VocabularyVerdict Verdict)>? lookedAt = null)
    {
        var report = new JsonObject
        {
            ["present"] = Present,
            ["path"] = Path,
        };

        if (!Present)
        {
            var override_ = Environment.GetEnvironmentVariable(PathVariable);

            report["reason"] = AbsentReason;
            report["note"] = "No vocabulary on this machine, so enum-looking values are sent to Dorico "
                + "unchecked and an unknown one is ignored there (an unknown LogDuration becomes a semibreve). "
                + $"Build one with `{ExtractionCommand}`; it writes to {PerUserPath}, it is never committed, and "
                + "it is version-specific, so re-extract after a Dorico update."
                + (string.IsNullOrEmpty(override_)
                    ? string.Empty
                    : $" {PathVariable} is set to '{override_}', which is what was looked in.");

            return report;
        }

        report["spellings"] = Spellings;

        if (SourceVersion is { Length: > 0 } version)
        {
            report["extractedFrom"] = new JsonObject
            {
                ["version"] = version,
                ["path"] = NodeRead.Str(Source, "path"),
                ["extractedAtUtc"] = ExtractedAtUtc,
                ["sha256"] = NodeRead.Str(Source, "sha256") is { Length: > 0 } hash ? hash[..16] : null,
            };
        }

        report["note"] = "A spelling is proven to appear in that binary, not proven to be accepted by the "
            + "parameter it was passed to.";

        if (lookedAt is not null)
        {
            var list = new JsonArray();

            foreach (var (key, value, verdict) in lookedAt)
            {
                JsonArr.AddNode(list, new JsonObject
                {
                    ["key"] = key,
                    ["value"] = value,
                    ["verdict"] = VerdictName(verdict),
                });
            }

            report["lookedAt"] = list;
        }

        return report;
    }

    /// <summary>How many of the binary's names of each kind the file also carries, for a caller to display.</summary>
    public JsonObject Summary() => new()
    {
        ["enums"] = Spellings,
        ["types"] = NodeRead.Long(Counts, "types"),
        ["commandNames"] = NodeRead.Long(Counts, "commandNames"),
        ["optionPaths"] = NodeRead.Long(Counts, "optionPaths"),
        ["names"] = NodeRead.Long(Counts, "names"),
        ["templates"] = NodeRead.Long(Counts, "templates"),
    };

    private static JsonArray Strings(IEnumerable<string> values)
    {
        var array = new JsonArray();

        foreach (var value in values)
        {
            JsonArr.AddText(array, value);
        }

        return array;
    }

    /// <summary>A value's verdict, as <c>error.data</c> and the payload spell it.</summary>
    public static string VerdictName(VocabularyVerdict verdict) => verdict switch
    {
        VocabularyVerdict.Known => "known",
        VocabularyVerdict.Unknown => "unknown",
        VocabularyVerdict.NoVocabulary => "not checked (no vocabulary on this machine)",
        VocabularyVerdict.NotASpelling => "not an enum spelling, not checked",
        _ => "unknown verdict",
    };
}

/// <summary>What <see cref="EnumVocabulary.Check"/> found.</summary>
internal enum VocabularyVerdict
{
    /// <summary>Not <c>k</c> + uppercase: a number, a word, a path, or a note value the CLI resolves itself.</summary>
    NotASpelling,

    /// <summary>The value has the spelling shape, but no vocabulary is on this machine, so nothing was checked.</summary>
    NoVocabulary,

    /// <summary>The vocabulary holds this spelling.</summary>
    Known,

    /// <summary>The value has the spelling shape and the vocabulary does not hold it.</summary>
    Unknown,
}
