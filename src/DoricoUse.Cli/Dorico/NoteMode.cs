namespace DoricoUse.Cli.Dorico;

/// <summary>
/// The note input mode: whether writing at a rhythmic position that already holds notes replaces them
/// (overwrite) or pushes them to the right (insert).
/// </summary>
/// <remarks>
/// A brand-new project is in overwrite mode, so note entry silently destroys what is already under the caret
/// unless the mode is changed first.
/// Verified live against Dorico Pro 6.2.30 on Windows 11 (piano grand stave, one Piano player, empty flow):
/// <c>NoteInput.Mode?NoteInputMode=kInsert</c> is accepted with a bare <c>kOK</c> and the status then reports
/// <c>noteInputMode: "kInsert"</c> together with <c>insertActive: true</c>; the same command with
/// <c>kOverwrite</c> names the value explicitly, and <c>NoteInput.Mode</c> with no parameter at all behaves as a
/// toggle (it is the command the UI's 插入 button runs, and its display name is 切换插入). The mode survives
/// switching documents, so every run reads the current value from the status instead of assuming one.
/// </remarks>
internal static class NoteMode
{
    /// <summary>The command that sets it; with no parameter it toggles, which is why the CLI always names a value.</summary>
    public const string Command = "NoteInput.Mode";

    /// <summary>The optional parameter the command takes.</summary>
    public const string Parameter = "NoteInputMode";

    /// <summary>Insert: what is already at the caret moves to the right.</summary>
    public const string Insert = "kInsert";

    /// <summary>Overwrite: what is already at the caret is replaced, which is what a fresh project starts in.</summary>
    public const string Overwrite = "kOverwrite";

    /// <summary>
    /// Maps the CLI's <c>--mode</c> spelling to the enum value Dorico wants. Both the plain words and the enum
    /// spellings are accepted, because the status reports the latter.
    /// </summary>
    public static bool TryResolve(string? text, out string value)
    {
        value = string.Empty;

        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        switch (text.Trim().ToLowerInvariant())
        {
            case "insert":
            case "ins":
            case "kinsert":
            case "insertmode":
                value = Insert;

                return true;

            case "overwrite":
            case "over":
            case "koverwrite":
            case "overwritemode":
                value = Overwrite;

                return true;

            default:
                return false;
        }
    }

    /// <summary>The value as the CLI spells it, for a payload an agent can compare with its own argument.</summary>
    public static string? ShortName(string? value) => value switch
    {
        Insert => "insert",
        Overwrite => "overwrite",
        null => null,
        _ => value,
    };

    /// <summary>What the phrase means, for a status that came back with an enum spelling nobody asked for.</summary>
    public static string? Meaning(string? value) => value switch
    {
        Insert => "existing notes are pushed to the right",
        Overwrite => "existing notes at the caret are replaced",
        "kChordMerge" => "the note entry mode Dorico calls chord merge; neither insert nor overwrite",
        _ => null,
    };

    public static string Names() => "insert or overwrite";
}
