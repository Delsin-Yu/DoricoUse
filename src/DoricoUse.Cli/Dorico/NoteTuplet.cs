using System.Globalization;

namespace DoricoUse.Cli.Dorico;

/// <summary>
/// Tuplet definitions as <c>NoteInput.StartTupletRun?Definition=</c> takes them.
/// </summary>
/// <remarks>
/// The parameter is a ratio written "<c>notes:inTheTimeOf</c>", so a quaver triplet is <c>3:2</c>: three quavers
/// in the time of two. Verified live on Dorico Pro 6.2.30 — the stave shows a bracketed 3 over the group. The
/// name spellings are aliases for the same ratios; the ratio is the form to trust, because the catalogue lists
/// only the parameter, never its values.
/// </remarks>
internal static class NoteTuplet
{
    private static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["triplet"] = "3:2",
        ["triolet"] = "3:2",
        ["duplet"] = "2:3",
        ["quadruplet"] = "4:3",
        ["quintuplet"] = "5:4",
        ["quintololet"] = "5:4",
        ["sextuplet"] = "6:4",
        ["septuplet"] = "7:4",
        ["septimole"] = "7:4",
    };

    public static bool TryResolve(string text, out string definition)
    {
        definition = string.Empty;

        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var trimmed = text.Trim();

        if (Aliases.TryGetValue(trimmed, out var alias))
        {
            definition = alias;

            return true;
        }

        var parts = trimmed.Split([':', '/', ';'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

        if (parts.Length != 2)
        {
            return false;
        }

        if (!int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var notes)
            || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var span))
        {
            return false;
        }

        // 1:1 is not a tuplet, and a ratio with a zero is a typo rather than a tuplet Dorico understands.
        if (notes < 2 || span < 1 || notes == span)
        {
            return false;
        }

        definition = $"{notes}:{span}";

        return true;
    }

    public static string Names() => "3:2 (a triplet of whatever note value --duration sets), 5:4, 7:4, ... "
        + "or a name such as triplet, quintuplet, sextuplet or septuplet";
}
