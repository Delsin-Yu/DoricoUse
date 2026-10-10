namespace DoricoUse.Cli.Dorico;

/// <summary>
/// Note values as Dorico names them in <c>NoteInput.NoteValue?LogDuration=</c>.
/// </summary>
/// <remarks>
/// <para>
/// The parameter is called LogDuration but it takes the rhythm-value enum spelling, and an unknown value makes
/// Dorico fall back to a semibreve — verified by entering a note for each candidate and looking at the stave
/// (<c>kCrotchet</c> live, and the spellings below from the binary's own parameter template).
/// </para>
/// <para>
/// <see cref="LogDurationSpellings"/> is the set the application itself associates with this parameter, taken from
/// its command template: <c>kMaxima, kLong, kBreve, kSemibreve, kMinim, kCrotchet, kQuaver, kSemiQuaver,
/// kDemiSemiQuaver, kHemiDemiSemiQuaver</c>. Note that this is <b>not</b> the same list as the rhythm *grid*
/// (<c>NoteInput.SetGridResolution</c>, where Dorico says <c>kSemiquaver</c>): the two commands spell 16ths
/// differently, and both spellings exist in the binary. The older spellings stay accepted here so a script or a
/// habit that uses them still works, but the friendly names resolve to the LogDuration ones.
/// </para>
/// </remarks>
internal static class NoteValue
{
    /// <summary>The spellings the binary's LogDuration parameter documents, longest to shortest.</summary>
    private static readonly string[] LogDurationSpellings =
    [
        "kMaxima",
        "kLong",
        "kBreve",
        "kSemibreve",
        "kMinim",
        "kCrotchet",
        "kQuaver",
        "kSemiQuaver",
        "kDemiSemiQuaver",
        "kHemiDemiSemiQuaver",
    ];

    private static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["maxima"] = "kMaxima",
        ["maxim"] = "kMaxima",
        ["long"] = "kLong",
        ["longa"] = "kLong",
        ["breve"] = "kBreve",
        ["doublewhole"] = "kBreve",
        ["semibreve"] = "kSemibreve",
        ["whole"] = "kSemibreve",
        ["minim"] = "kMinim",
        ["half"] = "kMinim",
        ["crotchet"] = "kCrotchet",
        ["quarter"] = "kCrotchet",
        ["quaver"] = "kQuaver",
        ["eighth"] = "kQuaver",
        ["semiquaver"] = "kSemiQuaver",
        ["semiQuaver"] = "kSemiQuaver",
        ["sixteenth"] = "kSemiQuaver",
        ["demisemiquaver"] = "kDemiSemiQuaver",
        ["demiSemiQuaver"] = "kDemiSemiQuaver",
        ["thirtysecond"] = "kDemiSemiQuaver",
        ["hemidemisemiquaver"] = "kHemiDemiSemiQuaver",
        ["hemiDemiSemiQuaver"] = "kHemiDemiSemiQuaver",
        ["sixtyfourth"] = "kHemiDemiSemiQuaver",
        ["dottedminim"] = "kDottedMinim",
        ["dottedhalf"] = "kDottedMinim",
        ["dottedcrotchet"] = "kDottedCrotchet",
        ["dottedquarter"] = "kDottedCrotchet",
        ["dottedquaver"] = "kDottedQuaver",
        ["dottedeighth"] = "kDottedQuaver",
        ["dottedsemiquaver"] = "kDottedSemiquaver",
        ["dottedsixteenth"] = "kDottedSemiquaver",
        ["2"] = "kSemibreve",
        ["1"] = "kSemibreve",
        ["1/2"] = "kMinim",
        ["1/4"] = "kCrotchet",
        ["1/8"] = "kQuaver",
        ["1/16"] = "kSemiQuaver",
        ["1/32"] = "kDemiSemiQuaver",
        ["1/64"] = "kHemiDemiSemiQuaver",
    };

    /// <summary>
    /// Every spelling that resolves: the ones LogDuration documents, the older names for the same values, and the
    /// dotted forms. Kept explicit so <c>--duration kFoo</c> is refused by name rather than silently substituted.
    /// </summary>
    private static readonly HashSet<string> Known = new(StringComparer.Ordinal)
    {
        "kMaxima",
        "kLong",
        "kBreve",
        "kSemibreve",
        "kMinim",
        "kCrotchet",
        "kQuaver",
        "kSemiQuaver",
        "kDemiSemiQuaver",
        "kHemiDemiSemiQuaver",
        // The grid command's spellings for the same values; both exist in the binary.
        "kSemiquaver",
        "kDemisemiquaver",
        "kDottedMinim",
        "kDottedCrotchet",
        "kDottedQuaver",
        "kDottedSemiquaver",
    };

    public static bool TryResolve(string text, out string value)
    {
        value = string.Empty;

        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var trimmed = text.Trim();

        if (Known.Contains(trimmed))
        {
            value = trimmed;

            return true;
        }

        var key = new string(trimmed
            .Where(character => char.IsLetterOrDigit(character) || character == '/')
            .ToArray());

        if (Aliases.TryGetValue(key, out var alias))
        {
            value = alias;

            return true;
        }

        return false;
    }

    /// <summary>The spellings this class accepts outright, in the order the binary lists them.</summary>
    public static IReadOnlyList<string> Spellings => LogDurationSpellings;

    public static string Names() => string.Join(
        ", ",
        "maxima/longa/breve, semibreve/whole, minim/half, crotchet/quarter, quaver/eighth, semiquaver/16th, "
        + "demisemiquaver/32nd, hemidemisemiquaver/64th, a dotted form (dottedcrotchet, ...), a fraction (1/8), "
        + "or a Dorico spelling (kQuaver, kSemiQuaver)");
}
