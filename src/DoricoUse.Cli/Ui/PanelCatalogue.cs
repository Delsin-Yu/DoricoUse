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
                + "出厂设置 / 用户库 / Dorico 工程, a tablist and the buttons 关闭 and 应用. What the walk visits is "
                + "27 nodes, and only those five are an element this build can act on; the window draws much more "
                + "than that (an 选项 column of five rows, a 集合 column, a 此工程 list whose rows read \"… 所有选项都"
                + "匹配\", the 显示: 全部 / 不同 / 不在此工程内 filter, a 搜索 field and the drop target 将 .dorico "
                + "工程文件放在这里), and every one of those is either absent from the tree or a Group with an empty "
                + "name and an empty value. The 13 CollectionListDelegate groups that are there (5 for the 选项 rows, "
                + "8 for the 集合 rows, which are themselves fewer than the window draws) expose an invoke pattern "
                + "that does nothing: measured twice, invoking one changed neither the row nor the rest of the "
                + "window. Nothing in the tree raises a file picker. The dialog's content is readable elsewhere: the "
                + "protocol's `library collections` lists the same 50 collections the 集合 rows stand for, and "
                + "`library entities` names their entries.",
            TitleGlobs: ["库管理"],
            ClassPrefixes: ["Qt"]),
        new PanelCatalogueEntry(
            Id: "dorico.layout-options",
            Note: "Measured: the layout options dialog, raised by Project.Layout.AllPartOptions (全部声部布局选项…, "
                + "every part layout selected) or by Project.Layout.Options?LayoutIDArray=<id> (that layout alone). "
                + "It is a modeless Qt6101QWindowIcon titled \"<document> - 布局选项\" and it is fully driveable: 162 "
                + "elements, of which 64 are rows (the option pages, and the layout list whose rows carry each "
                + "layout's own name), 74 are option controls, and the buttons include 保存为默认 (saveAsDefault), "
                + "重置到保存的默认值 (resetToSavedDefaults), 重置到出厂值 (resetToFactory), 移除保存的默认值 "
                + "(removeSavedDefaults), 选择全部 (playersSelectAllButton, selectAllLayoutsButton) and three glyph "
                + "buttons that select layouts by kind. Its changes reach the document at once — measured, one press "
                + "of resetToSavedDefaults restored two dirtied part layouts in the live document, before 应用 was "
                + "pressed — while the close and apply buttons have no unique automation id in Qt, so their element "
                + "ids are derived from their position and move when the dialog's page changes. The four default "
                + "buttons have stable ids.",
            TitleGlobs: ["*布局选项"],
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
