namespace DoricoUse.Cli.Dorico;

/// <summary>
/// Note values as Dorico names them in <c>NoteInput.NoteValue?LogDuration=</c>.
/// </summary>
/// <remarks>
/// The parameter is called LogDuration but it takes the rhythm-value enum spelling, and an unknown value makes
/// Dorico fall back to a semibreve — verified by entering a note for each candidate and looking at the stave.
/// </remarks>
internal static class NoteValue
{
    private static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["semibreve"] = "kSemibreve",
        ["whole"] = "kSemibreve",
        ["minim"] = "kMinim",
        ["half"] = "kMinim",
        ["crotchet"] = "kCrotchet",
        ["quarter"] = "kCrotchet",
        ["quaver"] = "kQuaver",
        ["eighth"] = "kQuaver",
        ["semiquaver"] = "kSemiquaver",
        ["sixteenth"] = "kSemiquaver",
        ["demisemiquaver"] = "kDemisemiquaver",
        ["thirtysecond"] = "kDemisemiquaver",
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
        ["1/16"] = "kSemiquaver",
        ["1/32"] = "kDemisemiquaver",
    };

    private static readonly HashSet<string> Known = new(StringComparer.Ordinal)
    {
        "kSemibreve",
        "kMinim",
        "kCrotchet",
        "kQuaver",
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

    public static string Names() => string.Join(
        ", ",
        "semibreve/whole, minim/half, crotchet/quarter, quaver/eighth, semiquaver/16th, demisemiquaver/32nd, "
        + "and the dotted forms (dottedcrotchet, ...) or a Dorico name (kQuaver)");
}
