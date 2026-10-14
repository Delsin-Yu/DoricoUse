using System.Text.Json.Nodes;
using DoricoUse.Cli.Json;

namespace DoricoUse.Cli.Dorico;

/// <summary>
/// The enum-looking values this CLI sends to Dorico, checked before they go.
/// </summary>
/// <remarks>
/// <para>
/// Dorico answers <c>kOK</c> to <c>NoteInput.NoteValue?LogDuration=kCrotchet</c> whatever the value is, and an
/// unknown one silently becomes something else — a semibreve for <c>LogDuration</c>. A typo therefore shows up in
/// neither the ack nor the status; it shows up as the wrong music. This class is what turns that into an error.
/// </para>
/// <para>
/// The table is <b>built in</b>: the values below are the ones this project has used and seen Dorico accept (or
/// read back from its status, its command catalogue and its own exports). Nothing is extracted from any binary at
/// run time and no file has to be generated first, so the check works on a fresh machine.
/// </para>
/// <para>
/// It is deliberately small, and the check is deliberately narrow: **only the parameters named below are
/// checked**. A value passed to any other parameter is sent unchecked, because a spelling this build has never
/// heard of is far more likely to be something a newer Dorico accepts than a typo. Refusing those would be worse
/// than not checking them — it would break working commands.
/// </para>
/// <para>
/// Nothing here is per-process state, so the class is static: the table became a constant of the build when it
/// stopped being extracted from the binary, and the one instance that used to wrap it held nothing.
/// </para>
/// </remarks>
internal static class EnumVocabulary
{
    /// <summary>The values each parameter is known to take, and the whole point of this class.</summary>
    private static readonly Dictionary<string, string[]> ParameterValues = new(StringComparer.Ordinal)
    {
        // Verified live: the note values the binary's own LogDuration template lists, plus the older names for the
        // same note values that this CLI accepts.
        ["LogDuration"] = [.. NoteValue.AllSpellings.Order(StringComparer.Ordinal)],

        // Verified live: NoteInput.Mode?NoteInputMode=kInsert read back as noteInputMode "kInsert".
        ["NoteInputMode"] = ["kInsert", "kOverwrite", "kChordMerge"],

        // Verified live: Window.SwitchMode read back through the status for the first three.
        ["WindowMode"] = ["kSetupMode", "kWriteMode", "kEngraveMode", "kPlayMode", "kPrintMode"],

        // Verified live: the toolbar's grid value reads back as rhythmicGridResolutionValue.
        ["RhythmicGridResolutionValue"] =
        [
            "kSemibreve", "kDottedMinim", "kMinim", "kDottedCrotchet", "kCrotchet", "kDottedQuaver",
            "kQuaver", "kDottedSemiquaver", "kSemiquaver", "kDemisemiquaver",
        ],

        // Verified live: getoptions/setoptions take exactly these.
        ["OptionsType"] = ["kEngraving", "kLayout", "kNotation"],

        // setoptions targets: every layout, or every flow.
        ["layoutIDs"] = ["kAll"],
        ["flowIDs"] = ["kAll"],
    };

    /// <summary>
    /// The parameters whose meaning depends on the <b>command</b> they are passed to.
    /// </summary>
    /// <remarks>
    /// Measured 2026-10-13: <c>Type</c> was one global row holding the seven accidental spellings, so
    /// <c>EventEdit.ChangeProperty?Type=...</c> and <c>UI.InvokePropertyChangeValue?Type=...</c> were refused unless
    /// the caller's property name happened to be an accidental - and a property name is exactly what those two
    /// commands take. The report's own route to changing an event's length goes through one of them, so the refusal
    /// stood in the way of the thing the tool exists for. A parameter is now only checked for a command this build
    /// measured it on: <c>NoteInput.SetAccidental?Type=kSharp</c> for the accidentals, and nothing else.
    /// </remarks>
    private static readonly Dictionary<(string Command, string Key), string[]> CommandParameters =
        new(new CommandKeyComparer())
        {
            // Verified live: the spellings the status reports back for a note's accidental, and the ones this CLI's
            // own file reader derives.
            [("NoteInput.SetAccidental", "Type")] =
            [
                "kNatural", "kSharp", "kFlat", "kDoubleSharp", "kDoubleFlat", "kTripleSharp", "kTripleFlat",
            ],

            // An empty row is a statement, not a gap: this Type is a property name (kNoteSpelling, or a name read
            // out of the project file), so there is no list to hold and the value must never be refused. The row is
            // there so the payload can say the value was looked at and deliberately not judged, instead of leaving
            // it looking like an oversight.
            [("EventEdit.ChangeProperty", "Type")] = [],
            [("UI.InvokePropertyChangeValue", "Type")] = [],
        };

    private static readonly HashSet<string> All =
    [
        .. ParameterValues.Values.SelectMany(values => values),
        .. CommandParameters.Values.SelectMany(values => values),
    ];

    /// <summary>How many distinct values the table holds.</summary>
    public static int Spellings => All.Count;

    /// <summary>How many parameters are checked at all, counted per command for the command-scoped ones.</summary>
    public static int Parameters => ParameterValues.Count + CommandParameters.Count;

    /// <summary>
    /// Whether a value has the shape of a Dorico enum spelling: <c>k</c>, an uppercase letter, then letters,
    /// digits or underscores (<c>kQuaver</c>, <c>kWriteMode</c>, <c>kInsert</c>). Everything else — a number,
    /// <c>true</c>, a path, a display name — is a value this class cannot speak about, and is left alone.
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

    /// <summary>What the table says about one value, for a parameter whose meaning does not depend on the command.</summary>
    public static VocabularyVerdict Check(string value)
        => !IsSpelling(value) ? VocabularyVerdict.NotASpelling
            : All.Contains(value) ? VocabularyVerdict.Known
            : VocabularyVerdict.Unknown;

    /// <summary>
    /// What the table says about one value of one parameter of one command: the form a caller that sends a command
    /// wants, because a parameter like <c>Type</c> means different things to different commands.
    /// </summary>
    /// <param name="key">The parameter's name.</param>
    /// <param name="value">The value itself.</param>
    /// <param name="command">
    /// The command the value is passed to, or null when this invocation composes no command.
    /// </param>
    /// <remarks>
    /// The third answer is why this exists: a value of a parameter this build scopes to other commands is
    /// <see cref="VocabularyVerdict.NotCheckedForCommand"/> and is never refused. Measured 2026-10-13, the
    /// property-name <c>Type</c> of <c>EventEdit.ChangeProperty</c> and of <c>UI.InvokePropertyChangeValue</c> was
    /// refused for not being one of the seven accidentals.
    /// </remarks>
    public static VocabularyVerdict Check(string? key, string value, string? command)
    {
        if (!IsSpelling(value))
        {
            return VocabularyVerdict.NotASpelling;
        }

        if (key is not null && CommandKeys.Contains(key))
        {
            if (!CommandParameters.TryGetValue((command ?? string.Empty, key), out var scoped))
            {
                // A command this build has not measured this parameter on. Refusing would break the command the
                // caller is building, which is the bug this overload fixes, so it is left to Dorico and said so.
                return VocabularyVerdict.NotCheckedForCommand;
            }

            return scoped.Length == 0 ? VocabularyVerdict.NotCheckedForCommand
                : scoped.Contains(value, StringComparer.Ordinal) ? VocabularyVerdict.Known
                : VocabularyVerdict.Unknown;
        }

        if (key is not null && ParameterValues.TryGetValue(key, out var allowed))
        {
            return allowed.Contains(value, StringComparer.Ordinal) ? VocabularyVerdict.Known : VocabularyVerdict.Unknown;
        }

        return All.Contains(value) ? VocabularyVerdict.Known : VocabularyVerdict.Unknown;
    }

    /// <summary>The parameters whose meaning depends on the command: the keys of the command-scoped table.</summary>
    private static readonly HashSet<string> CommandKeys = [.. CommandParameters.Keys.Select(pair => pair.Key)];

    /// <summary>
    /// The parameters whose meaning depends on the command, so a caller can see which ones they are and why a value
    /// of one was not judged.
    /// </summary>
    public static IReadOnlyList<string> CommandScopedKeys { get; } =
        [.. CommandKeys.Order(StringComparer.Ordinal)];

    /// <summary>
    /// The values this build knows for one parameter, or empty when it checks nothing for it (or for that
    /// command).
    /// </summary>
    /// <param name="key">The parameter's name.</param>
    /// <param name="command">The command, for a parameter whose meaning depends on one.</param>
    public static IReadOnlyList<string> ValuesForKey(string? key, string? command = null)
    {
        if (key is null)
        {
            return [];
        }

        if (CommandKeys.Contains(key))
        {
            return CommandParameters.TryGetValue((command ?? string.Empty, key), out var scoped) ? scoped : [];
        }

        return ParameterValues.TryGetValue(key, out var values) ? values : [];
    }

    /// <summary>
    /// Whether this parameter is checked at all for this command. A parameter this build has never heard of, and a
    /// command-scoped one asked about a command it was not measured on, are both "not looked at" rather than
    /// "wrong".
    /// </summary>
    private static bool IsChecked(string? key, string? command)
        => key is not null
        && (CommandKeys.Contains(key)
            ? CommandParameters.TryGetValue((command ?? string.Empty, key), out var scoped) && scoped.Length > 0
            : ParameterValues.ContainsKey(key));

    /// <summary>
    /// The nearest spellings to a value that is not known: the longest shared beginning first, edit distance
    /// breaking ties — the same ranking <c>file read --tree</c> uses for a name nobody has. The values of the
    /// parameter itself are offered first, because they are the most likely answer.
    /// </summary>
    /// <remarks>
    /// "Near" is bounded, so a value with nothing like it gets no suggestions at all rather than eight arbitrary
    /// <c>k</c> words: the distance has to be within half the value's length (at least two characters of slack),
    /// and the comparison of the shared beginning skips the leading <c>k</c>, which every spelling has.
    /// </remarks>
    public static IReadOnlyList<string> Suggest(string value, string? key = null, int limit = 8)
    {
        var suggestions = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var known in Ranked(ValuesForKey(key), value, limit))
        {
            if (seen.Add(known))
            {
                suggestions.Add(known);
            }
        }

        foreach (var known in Ranked(All, value, limit))
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

    /// <summary>
    /// Refuses a value the table contradicts, the way a mistyped argument is refused anywhere else in this CLI:
    /// <c>InvalidArgument</c>, exit code 1, and <c>error.data</c> carrying the suggestions.
    /// </summary>
    /// <param name="what">Which argument carried it, e.g. <c>--param</c> or <c>--grid</c>.</param>
    /// <param name="key">The parameter or option path the value belongs to.</param>
    /// <param name="value">The value itself.</param>
    /// <param name="allowUnknown">Send it anyway, for the caller who knows better than this table.</param>
    /// <param name="command">
    /// The command the value is passed to, or null when this invocation composes no command. A parameter whose
    /// meaning depends on the command is checked only for the commands this build measured it on, which is what lets
    /// <c>EventEdit.ChangeProperty?Type=kNoteSpelling</c> through while still refusing a misspelt accidental.
    /// </param>
    /// <remarks>
    /// A parameter this build does not know is never refused: the check only speaks about the parameters in the
    /// table, because for anything else "not in the table" means "not looked at", not "wrong" - and a command-scoped
    /// parameter asked about a command it was not measured on is the same kind of thing.
    /// </remarks>
    public static void Refuse(string what, string? key, string value, bool allowUnknown = false, string? command = null)
    {
        if (allowUnknown || !IsChecked(key, command))
        {
            return;
        }

        var allowed = ValuesForKey(key, command);

        if (!IsSpelling(value) || allowed.Contains(value, StringComparer.Ordinal))
        {
            return;
        }

        var suggestions = Suggest(value, key);

        var nearest = suggestions.Count == 0
            ? "No close spelling was found."
            : $"The nearest spellings are {string.Join(", ", suggestions)}.";

        throw new CliException(
            "InvalidArgument",
            $"'{value}' is not one of the values this build knows for {key} ({what}"
            + (command is null ? string.Empty : $" of {command}")
            + $"): {string.Join(", ", allowed)}. "
            + $"{nearest} Dorico answers kOK to an unknown enum value and silently substitutes another — an unknown "
            + "LogDuration becomes a semibreve — so this is refused rather than sent. Use --allow-unknown-enum if it "
            + "is nonetheless the value you mean, and please report it: a value missing from this table is a bug "
            + "worth fixing.",
            1)
        {
            Details = new JsonObject
            {
                ["argument"] = what,
                ["key"] = key,
                ["value"] = value,
                ["valuesForKey"] = Strings(allowed),
                ["suggestions"] = Strings(suggestions),
                ["vocabulary"] = Describe(),
            },
        };
    }

    /// <summary>
    /// What to put in a payload so a reader can see what was checked and on what basis. There is no file to point
    /// at any more, and no reason for a machine to be in a different state from another.
    /// </summary>
    /// <param name="lookedAt">The values this invocation put through the table, and what it said.</param>
    public static JsonObject Describe(IEnumerable<(string Key, string Value, VocabularyVerdict Verdict)>? lookedAt = null)
    {
        var report = new JsonObject
        {
            ["source"] = "built in",
            ["parameters"] = Parameters,
            ["values"] = Spellings,
            ["commandScoped"] = Strings(CommandScopedKeys),
            ["note"] = "Only the parameters this build knows are checked, and a parameter whose meaning depends on "
                + "the command (see commandScoped: Type) is checked only for the commands it was measured on. A "
                + "value passed to any other parameter is sent unchecked, because a spelling this build has never "
                + "heard of is more likely to be newer than wrong. A known value is proven to be one Dorico uses, "
                + "not proven to be accepted by the parameter it was passed to.",
        };

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

    private static List<string> Ranked(IEnumerable<string> candidates, string value, int limit)
    {
        var ranked = new List<(int Prefix, int Distance, string Name)>();
        var ceiling = Math.Max(2, value.Length / 2);

        foreach (var candidate in candidates)
        {
            // A value whose best edit distance is more than half its own length away is not a near miss.
            var distance = Distance(value, candidate, ceiling);

            if (distance > ceiling)
            {
                continue;
            }

            ranked.Add((SharedPrefix(Body(value), Body(candidate)), distance, candidate));
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

    /// <summary>A spelling without its leading <c>k</c>, which every one of them has.</summary>
    private static string Body(string value) => value.Length > 1 && value[0] == 'k' ? value[1..] : value;

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
        VocabularyVerdict.NotASpelling => "not an enum spelling, not checked",
        VocabularyVerdict.NotCheckedForCommand => "not checked for this command",
        _ => "unknown verdict",
    };
}

/// <summary>
/// Compares a (command, parameter) pair case-insensitively, so a caller's spelling of a command name cannot decide
/// whether the table is consulted.
/// </summary>
internal sealed class CommandKeyComparer : IEqualityComparer<(string Command, string Key)>
{
    public bool Equals((string Command, string Key) left, (string Command, string Key) right)
        => string.Equals(left.Command, right.Command, StringComparison.OrdinalIgnoreCase)
            && string.Equals(left.Key, right.Key, StringComparison.Ordinal);

    public int GetHashCode((string Command, string Key) value)
        => HashCode.Combine(
            StringComparer.OrdinalIgnoreCase.GetHashCode(value.Command),
            StringComparer.Ordinal.GetHashCode(value.Key));
}

/// <summary>What <see cref="EnumVocabulary.Check"/> found.</summary>
internal enum VocabularyVerdict
{
    /// <summary>
    /// The parameter's meaning depends on the command and this build has no list for this one, so the value was
    /// looked at and deliberately not judged: measured 2026-10-13, a property name passed as
    /// <c>EventEdit.ChangeProperty?Type=</c> is exactly what that command wants, and the accidental table has
    /// nothing to say about it.
    /// </summary>
    NotCheckedForCommand,

    /// <summary>Not <c>k</c> + uppercase: a number, a word, a path, or a value this build leaves to Dorico.</summary>
    NotASpelling,

    /// <summary>The table holds this spelling.</summary>
    Known,

    /// <summary>The value has the spelling shape and the table does not hold it.</summary>
    Unknown,
}
