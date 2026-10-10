namespace DoricoUse.Cli.Dorico;

/// <summary>
/// Moving the caret to another stave of the same instrument, which is how a chord or a line is aimed at the
/// other hand of a piano.
/// </summary>
/// <remarks>
/// Dorico's API cannot address a stave by name and cannot say which stave the caret is on, so the only lever is
/// relative: <c>NoteInput.MoveUpTop</c> / <c>NoteInput.MoveDownBottom</c>. Verified live on a piano grand stave:
/// after <c>NoteInput.MoveDownBottom</c>, the next pitch landed on the lower stave, drawn in the bass clef, while
/// the rhythmic position was kept. Both commands are no-ops when the caret is already on that stave, which is why
/// the CLI sends them without reading anything back.
/// </remarks>
internal static class NoteStaff
{
    /// <summary>The command that takes the caret to the top stave of the instrument.</summary>
    public const string TopCommand = "NoteInput.MoveUpTop";

    /// <summary>The command that takes the caret to the bottom stave of the instrument.</summary>
    public const string BottomCommand = "NoteInput.MoveDownBottom";

    public static bool TryResolve(string text, out string command)
    {
        command = string.Empty;

        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        switch (text.Trim().ToLowerInvariant())
        {
            case "up":
            case "top":
            case "upper":
            case "above":
                command = TopCommand;

                return true;

            case "down":
            case "bottom":
            case "lower":
            case "below":
                command = BottomCommand;

                return true;

            default:
                return false;
        }
    }

    public static string Names() => "up/top or down/bottom (the stave above or below the caret's own)";
}
