namespace DoricoUse.Cli.Ui;

/// <summary>One window the tool has measured and recognises as a panel of its own.</summary>
/// <param name="Id">The stable <c>panel.id</c>. Part of the published contract; never renamed.</param>
/// <param name="Note">What was measured, in one sentence, so a reader can tell a measurement from a guess.</param>
/// <param name="TitleGlobs">Window captions that match. A panel with none matches on its class alone.</param>
/// <param name="ClassPrefixes">
/// The window class must start with one of these. The class is what tells the measured two kinds of Dorico window
/// apart (<c>Qt6101QWindow</c> for a blocking dialog, <c>Qt6101QWindowIcon</c> for the modeless library manager),
/// so it is part of the matcher rather than decoration.
/// </param>
internal sealed record PanelCatalogueEntry(
    string Id,
    string Note,
    IReadOnlyList<string> TitleGlobs,
    IReadOnlyList<string> ClassPrefixes);

/// <summary>
/// The panels this build recognises by name, each of which was read off a live window.
/// </summary>
/// <remarks>
/// <para>
/// This table does <b>not</b> decide what may be pressed — that is <see cref="DialogCatalogue"/>, because a panel
/// element is acted on by a pattern and the danger is in the pattern, not in the window. What it decides is whether
/// the payload says <c>known</c> or <c>unknown</c>, and whether an agent gets a name it can act on instead of a
/// hash.
/// </para>
/// <para>
/// An entry is added only from a reading of the real window. A panel that is not here is still reported in full,
/// with the facts Win32 gave and an <c>unknown.&lt;hash&gt;</c> id, which is the honest answer for a window nobody
/// has looked at.
/// </para>
/// </remarks>
internal static class PanelCatalogue
{
    /// <summary>One panel's caption was measured on the maintainer's installation, which is a Chinese UI.</summary>
    public static IReadOnlyList<PanelCatalogueEntry> Entries { get; } =
    [
        new PanelCatalogueEntry(
            Id: "dorico.library-manager",
            Note: "Measured: the library manager, raised by Project.ShowLibraryImportExportDialog, is the modeless "
                + "class Qt6101QWindowIcon with no owner, and it leaves the document window enabled while Dorico's "
                + "command queue keeps moving. Its own UIA tree exposes a titlebar, text, groups, the check boxes "
                + "出厂设置 / 用户库 / Dorico 工程, a tablist and the buttons 关闭 and 应用.",
            TitleGlobs: ["库管理"],
            ClassPrefixes: ["Qt"]),
    ];

    /// <summary>The entry that recognises this window, or null when nothing does.</summary>
    /// <param name="title">The window caption, verbatim.</param>
    /// <param name="windowClass">The Win32 window class, verbatim.</param>
    public static PanelCatalogueEntry? Match(string title, string windowClass)
    {
        foreach (var entry in Entries)
        {
            if (entry.ClassPrefixes.Count > 0
                && !entry.ClassPrefixes.Any(prefix => windowClass.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            if (entry.TitleGlobs.Count > 0 && !entry.TitleGlobs.Any(glob => DialogCatalogue.Glob(title, glob)))
            {
                continue;
            }

            return entry;
        }

        return null;
    }
}
