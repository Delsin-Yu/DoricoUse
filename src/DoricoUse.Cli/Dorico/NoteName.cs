namespace DoricoUse.Cli.Dorico;

/// <summary>
/// Pitch names as Dorico spells them in the two commands that enter a note:
/// <c>NoteInput.SetAccidental?Type=kSharp</c> and <c>NoteInput.Pitch?Pitch=F,OctaveValue=5</c>.
/// </summary>
/// <remarks>
/// Octave numbers follow Dorico's: C4 is middle C (MIDI 60), so "F#5" is the F# an octave and a fourth above it.
/// Microtonal accidentals are not supported by the API.
/// </remarks>
internal static class NoteName
{
    /// <summary>The separator that turns a run of note names into one chord: <c>"C4+E4+G4"</c>.</summary>
    public const char ChordSeparator = '+';

    public static bool TryParse(string text, out string pitch, out int octave, out string? accidental)
    {
        pitch = string.Empty;
        octave = 0;
        accidental = null;

        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var trimmed = text.Trim();
        var letter = char.ToUpperInvariant(trimmed[0]);

        if (letter is < 'A' or > 'G')
        {
            return false;
        }

        var index = 1;
        var accidentalText = string.Empty;

        while (index < trimmed.Length && !char.IsAsciiDigit(trimmed[index]) && trimmed[index] != '-')
        {
            accidentalText += trimmed[index];
            index++;
        }

        var octaveText = trimmed[index..];

        if (!int.TryParse(octaveText, out var parsedOctave))
        {
            return false;
        }

        accidental = AccidentalOf(accidentalText);

        if (accidental is null && accidentalText.Length > 0)
        {
            return false;
        }

        pitch = letter.ToString();
        octave = parsedOctave;

        return true;
    }

    /// <summary>Maps the accidental part of a note name onto Dorico's enum spelling.</summary>
    public static string? AccidentalOf(string text) => text switch
    {
        "" or "n" => null,
        "#" or "s" => "kSharp",
        "##" or "x" => "kDoubleSharp",
        "b" or "f" => "kFlat",
        "bb" => "kDoubleFlat",
        _ => null,
    };

    /// <summary>
    /// Splits one token into the notes it names: <c>"C4"</c> is one note, <c>"C4+E4+G4"</c> is a chord of
    /// three. A chord is written with <see cref="ChordSeparator"/> because that character never appears in a
    /// note name, so the split is unambiguous.
    /// </summary>
    public static bool TryParseGroup(string text, out List<string> notes)
    {
        notes = [];

        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var parts = text.Split(ChordSeparator, StringSplitOptions.TrimEntries);

        foreach (var part in parts)
        {
            // Validate every member here, so a bad chord is rejected before anything is sent.
            if (part.Length == 0 || !TryParse(part, out _, out _, out _))
            {
                notes = [];

                return false;
            }
        }

        notes = [.. parts];

        return notes.Count > 0;
    }
}
