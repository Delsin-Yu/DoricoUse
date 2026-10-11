namespace DoricoUse.Cli.Ui;

/// <summary>One button of a catalogued dialog, with what this build is willing to claim about it.</summary>
/// <param name="Id">The id an agent passes to <c>ui press --button</c>.</param>
/// <param name="Alias">A generic name for the same button (<c>yes</c>, <c>no</c>, <c>ok</c>, <c>cancel</c>, <c>discard</c>), or null.</param>
/// <param name="Role">One of <c>default</c>, <c>dismiss</c>, <c>destructive</c>, <c>unknown</c>.</param>
/// <param name="Safe">True only when the measurement showed this button cannot lose work by itself.</param>
/// <param name="MatchLabels">Labels that identify this button, matched exactly.</param>
/// <param name="MatchAutomationIdTails">The last dot-separated segment of a Qt automation id.</param>
/// <param name="MatchClassicIds">The classic Win32 control id of a <c>#32770</c> dialog's button.</param>
internal sealed record DialogCatalogueButton(
    string Id,
    string? Alias,
    string Role,
    bool Safe,
    IReadOnlyList<string> MatchLabels,
    IReadOnlyList<string> MatchAutomationIdTails,
    IReadOnlyList<string> MatchClassicIds);

/// <summary>What one read measured about a window: the facts an entry may match on, and nothing else.</summary>
/// <remarks>
/// One record rather than a longer parameter list, because the whole safety property of the table below is "which
/// facts does this entry claim to have measured": with the fingerprint spelled out member by member, a caller
/// cannot pass the labels where the automation ids belong, and adding a sixth fact is an edit the compiler carries
/// to every caller instead of one a call site can silently skip.
/// </remarks>
/// <param name="Title">The window caption, verbatim — and generic in one measured case: Dorico's error box is titled
/// just <c>Dorico</c>, which another window of the same session is titled too.</param>
/// <param name="WindowClass">The Win32 window class, verbatim.</param>
/// <param name="BodyText">The dialog's own text, joined with newlines, verbatim; empty when it shows none.</param>
/// <param name="ButtonLabels">The labels the buttons really show, in the order they were read. May repeat.</param>
/// <param name="ButtonAutomationIds">
/// The buttons' own automation ids, verbatim and in the same order, empty strings included. An entry that wants a
/// path has to look here and not at <paramref name="ButtonLabels"/>: measured, the two buttons of Dorico's error box
/// share the automation id path <c>buttonBox.QPushButton</c>, so the path identifies the dialog and not the button.
/// </param>
internal sealed record DialogFingerprint(
    string Title,
    string WindowClass,
    string BodyText,
    IReadOnlyList<string> ButtonLabels,
    IReadOnlyList<string> ButtonAutomationIds);

/// <summary>One catalogued dialog and the matcher that recognises it.</summary>
/// <param name="Id">The stable <c>dialog.id</c>. Part of the published contract; never renamed.</param>
/// <param name="Measured">True when the title or the labels below came from reading the real dialog.</param>
/// <param name="Note">What was measured, and by which dialog, in one sentence.</param>
/// <param name="TitleGlobs">Window captions that match. An empty list means the other criteria identify the dialog.</param>
/// <param name="RequiredButtonLabels">Every one of these must be present for the entry to apply.</param>
/// <param name="Buttons">The buttons this build knows, in match order.</param>
/// <param name="WindowClasses">
/// Window classes that match, all of them one of these, when the entry declares any. Null is "this entry does not
/// claim this fact", the same idiom the <c>Button</c> helper below uses: an entry written before this criterion
/// existed keeps matching exactly what it matched before, because a criterion nobody declared is not checked.
/// </param>
/// <param name="RequiredBodyText">
/// Text that must appear in the dialog's own body, every one of these, when the entry declares any. Added for the
/// error box and measured on it: a caption is not always an identity, and the body is the next fact the read has.
/// </param>
/// <param name="RequiredAutomationIds">
/// Paths that must appear inside the buttons' automation ids, every one of them, when the entry declares any.
/// "Some button carries this path" is the relation, not equality, because that is what the measurement supports:
/// the error box's two buttons share one path.
/// </param>
internal sealed record DialogCatalogueEntry(
    string Id,
    bool Measured,
    string Note,
    IReadOnlyList<string> TitleGlobs,
    IReadOnlyList<string> RequiredButtonLabels,
    IReadOnlyList<DialogCatalogueButton> Buttons,
    IReadOnlyList<string>? WindowClasses = null,
    IReadOnlyList<string>? RequiredBodyText = null,
    IReadOnlyList<string>? RequiredAutomationIds = null);

/// <summary>
/// The measured whitelist: which dialogs this build will press a button in, and which buttons it calls safe.
/// </summary>
/// <remarks>
/// <para>
/// Safety comes from this table and from nothing else. A dialog that is not matched here is reported in full and
/// refused (<c>UnknownDialog</c>), and every one of its buttons is <c>safe:false</c> — including cancel, because
/// "cancel is harmless" is a claim about a specific dialog and this build only makes claims it measured.
/// </para>
/// <para>
/// The captions and labels below are the ones the maintainer's installation showed, which is a Chinese Dorico UI.
/// On another UI language the same dialog is reported as <c>unknown</c>: it is read, it is refused, and no button
/// is pressed. That is the intended failure, and it is the reason <c>--allow-unknown-dialog</c> exists.
/// </para>
/// </remarks>
internal static class DialogCatalogue
{
    /// <summary>The alias for an accept button. Only ever set by a catalogued entry.</summary>
    public const string AliasYes = "yes";

    public const string AliasNo = "no";

    public const string AliasOk = "ok";

    public const string AliasCancel = "cancel";

    public const string AliasDiscard = "discard";

    /// <summary>
    /// The dialogs the handover names whose content has never been read, so no entry can be written for them
    /// honestly: a matcher has to come from a measurement, and every label in the handover's table is the
    /// maintainer's description rather than a measured string.
    /// </summary>
    /// <remarks>
    /// These ids are reserved. The vocabulary check asserts that none of them has an entry, so a guessed matcher
    /// cannot quietly appear here: until the dialog has been read, it is reported as <c>unknown</c> and refused.
    /// <list type="bullet">
    /// <item><description><c>dorico.allow-remote-client</c> — raised by <c>session connect --reset</c>; observed to
    /// disable the Hub window, never read.</description></item>
    /// <item><description><c>dorico.import-musicxml</c> — only a real MusicXML import raises it.</description></item>
    /// <item><description><c>dorico.save-picker</c> — the Open picker is measured and catalogued, but no <b>save</b>
    /// picker has been read, so its caption and its accept button are unknown.</description></item>
    /// <item><description><c>dorico.overwrite-file</c> — never seen.</description></item>
    /// </list>
    /// </remarks>
    public static IReadOnlyList<string> Reserved { get; } =
    [
        "dorico.allow-remote-client",
        "dorico.import-musicxml",
        "dorico.save-picker",
        "dorico.overwrite-file",
    ];

    /// <summary>Every catalogued dialog. One test per entry exists in the vocabulary check.</summary>
    public static IReadOnlyList<DialogCatalogueEntry> Entries { get; } =
    [
        new DialogCatalogueEntry(
            Id: "dorico.open-picker",
            Measured: true,
            Note: "Measured: raised by Project.ImportLibrary and by File.Import.OtherProjectFlow, caption 打开, "
                + "class #32770, owned by the document window, and it disables that window while it is up. Its "
                + "accept button is 打开 with the classic control id 1 and its cancel button is 取消 with classic "
                + "control id 2; its file-name box is an Edit carrying automation id 1148 and its file filter is a "
                + "comboBox with 1136. Accepting an import changes the document, so it is not safe; cancel is.",
            TitleGlobs: ["打开"],
            RequiredButtonLabels: ["打开", "取消"],
            Buttons:
            [
                // Both criteria, not either: matched by the label 打开 alone, this entry also claimed the two
                // `DropDown` buttons of the file-name and file-type combo boxes, which carry the same label, so
                // `--button accept` would have pressed a combo box's arrow. Measured on the live window: the accept
                // button is the one with automation id "1", the combo boxes are at index 15 and 16 with no classic id
                // at all. That is what made every criterion in the table an "and" rather than an "or".
                Button("accept", DialogModel.RoleDefault, safe: false, alias: AliasOk, labels: ["打开"], classicIds: ["1"]),
                Button("cancel", DialogModel.RoleDismiss, safe: true, alias: AliasCancel, labels: ["取消"], classicIds: ["2"]),
            ]),

        new DialogCatalogueEntry(
            Id: "dorico.export-musicxml",
            Measured: true,
            Note: "Measured: the caption is 导出MusicXML, class Qt6101QWindow, and its two button-box buttons are "
                + "确定 (the accept button) and 取消. The seven buttons include two labelled 选择全部 and one whose "
                + "label is a private-use glyph, which is why the ids come from the automation id's tail.",
            TitleGlobs: ["导出MusicXML"],
            RequiredButtonLabels: ["确定", "取消"],
            Buttons:
            [
                Button("accept", DialogModel.RoleDefault, safe: true, alias: AliasYes, labels: ["确定"]),
                Button("cancel", DialogModel.RoleDismiss, safe: true, alias: AliasNo, labels: ["取消"]),
                Button(
                    "folder-picker",
                    DialogModel.RoleUnknown,
                    safe: false,
                    tails: ["chooseFolderPathPushButton"]),
            ]),

        new DialogCatalogueEntry(
            Id: "dorico.crash-report",
            Measured: true,
            Note: "Measured: Dorico's crash reporter is a Qt6101QWindowIcon window titled "
                + "向 Steinberg 发送崩溃报告 with the three buttons 发送到 Steinberg, 不发送 and 不再询问. It owns "
                + "nothing, yet it disabled every other window of the process — measured with it up, both the "
                + "document window and the modeless library manager read enabled=false — which is why it is a "
                + "blocking dialog and not a panel, whatever its window class says. All three of its buttons share "
                + "one automation id, so their ids come from their labels.",
            TitleGlobs: ["向 Steinberg 发送崩溃报告"],
            RequiredButtonLabels: ["不发送", "不再询问"],
            Buttons:
            [
                Button("upload", DialogModel.RoleUnknown, safe: false, labels: ["发送到 Steinberg"]),
                Button("dismiss", DialogModel.RoleDismiss, safe: true, alias: AliasNo, labels: ["不发送"]),
                Button("no-ask-again", DialogModel.RoleUnknown, safe: false, labels: ["不再询问"]),
            ]),

        new DialogCatalogueEntry(
            Id: "dorico.error",
            Measured: true,
            Note: "Measured: Dorico's error message box, captured from a real payload. Its caption is the bare word "
                + "Dorico, which another window of the same session carries as well, so the caption is not the "
                + "identity and this entry declares three more measured facts: the class Qt6101QWindowIcon, the body "
                + "text 错误发生, and the button-box path buttonBox.QPushButton its two buttons share. Its buttons are "
                + "关闭 and 确定 — an error report, not a question — and neither can lose work by itself, so both are "
                + "safe. It is a blocking modal: it had disabled the document window while it was up, whatever its "
                + "class reads as, and it was reported as blocksQueue true.",
            TitleGlobs: ["Dorico"],
            RequiredButtonLabels: ["关闭", "确定"],
            WindowClasses: ["Qt6101QWindowIcon"],
            RequiredBodyText: ["错误发生"],
            RequiredAutomationIds: ["buttonBox.QPushButton"],
            // Both labels were measured. Neither id is `accept`: an error box has nothing to accept, and the id is
            // what an agent pastes back — the uncatalogued read of this same dialog produced `position-1` for the
            // 关闭 button and `QPushButton` for the 确定 one, and neither of those is an id worth remembering. The
            // catalogue's own names are what make them stable, which is what an entry is for.
            Buttons:
            [
                Button("dismiss", DialogModel.RoleDismiss, safe: true, alias: AliasCancel, labels: ["关闭"]),
                Button("ok", DialogModel.RoleDefault, safe: true, alias: AliasOk, labels: ["确定"]),
            ]),

        new DialogCatalogueEntry(
            Id: "dorico.folder-picker",
            Measured: true,
            Note: "Measured: raised by the export dialog's folder button, caption 选择文件夹, class #32770, in "
                + "Dorico's own process, and owning the Qt dialog that raised it. Its accept and cancel buttons "
                + "carry the classic control ids 1 and 2, which do not change with the UI language.",
            TitleGlobs: ["选择文件夹"],
            RequiredButtonLabels: ["选择", "取消"],
            Buttons:
            [
                Button("accept", DialogModel.RoleDefault, safe: true, alias: AliasYes, labels: ["选择"], classicIds: ["1"]),
                Button("cancel", DialogModel.RoleDismiss, safe: true, alias: AliasNo, labels: ["取消"], classicIds: ["2"]),
            ]),

        new DialogCatalogueEntry(
            Id: "dorico.unsaved-changes",
            Measured: true,
            Note: "The labels come from earlier work on this repository, not from a reading: the save prompt's "
                + "discard button is 丢弃 and its cancel button is 取消. The caption was never seen, so this entry "
                + "matches on the labels alone; any further button the prompt shows is reported as unknown and is "
                + "not safe.",
            TitleGlobs: [],
            RequiredButtonLabels: ["丢弃", "取消"],
            Buttons:
            [
                Button("discard", DialogModel.RoleDestructive, safe: false, alias: AliasDiscard, labels: ["丢弃"]),
                Button("cancel", DialogModel.RoleDismiss, safe: true, alias: AliasNo, labels: ["取消"]),
            ]),
    ];

    /// <summary>
    /// The entry that recognises this dialog, or null when nothing does.
    /// </summary>
    /// <param name="fingerprint">What the read measured: the facts an entry may match on.</param>
    public static DialogCatalogueEntry? Match(DialogFingerprint fingerprint)
    {
        ArgumentNullException.ThrowIfNull(fingerprint);

        foreach (var entry in Entries)
        {
            if (Matches(entry, fingerprint))
            {
                return entry;
            }
        }

        return null;
    }

    private static bool Matches(DialogCatalogueEntry entry, DialogFingerprint fingerprint)
    {
        // An entry with no criterion at all would match every dialog there is. There is no such entry, and this
        // keeps one from being added by accident.
        if (entry.TitleGlobs.Count == 0
            && entry.RequiredButtonLabels.Count == 0
            && entry.WindowClasses is not { Count: > 0 }
            && entry.RequiredBodyText is not { Count: > 0 }
            && entry.RequiredAutomationIds is not { Count: > 0 })
        {
            return false;
        }

        if (entry.TitleGlobs.Count > 0 && !entry.TitleGlobs.Any(glob => Glob(fingerprint.Title, glob)))
        {
            return false;
        }

        if (entry.WindowClasses is { Count: > 0 } classes
            && !classes.Contains(fingerprint.WindowClass, StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        // A substring, not an equality: 错误发生 is what the body of the measured error box says, and a translation
        // or a longer sentence around it would still be that dialog. Matched case-insensitively like the captions,
        // which costs nothing here and is the same rule an entry for a Latin-script dialog would want.
        if (entry.RequiredBodyText is { Count: > 0 } body
            && !body.All(text => fingerprint.BodyText.Contains(text, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        if (entry.RequiredAutomationIds is { Count: > 0 } paths
            && !paths.All(path => fingerprint.ButtonAutomationIds.Any(
                id => id.Contains(path, StringComparison.OrdinalIgnoreCase))))
        {
            return false;
        }

        return entry.RequiredButtonLabels.All(required => fingerprint.ButtonLabels.Contains(required, StringComparer.Ordinal));
    }

    /// <summary>
    /// Matches a window caption against a catalogue glob.
    /// </summary>
    /// <remarks>
    /// Deliberately stricter than the glob the option and catalogue filters use, which falls back to a substring
    /// match: a whitelist that matched a caption merely <i>containing</i> the measured one would also match
    /// "导出MusicXML 选项". A pattern with no <c>*</c> therefore has to be the whole caption.
    /// </remarks>
    public static bool Glob(string value, string pattern)
    {
        if (!pattern.Contains('*', StringComparison.Ordinal))
        {
            return string.Equals(value, pattern, StringComparison.OrdinalIgnoreCase);
        }

        var parts = pattern.Split('*');
        var position = 0;

        for (var index = 0; index < parts.Length; index++)
        {
            var part = parts[index];

            if (part.Length == 0)
            {
                continue;
            }

            var found = value.IndexOf(part, position, StringComparison.OrdinalIgnoreCase);

            if (found < 0 || (index == 0 && found != 0))
            {
                return false;
            }

            position = found + part.Length;
        }

        var tail = parts[^1];

        return tail.Length == 0 || position == value.Length;
    }

    private static DialogCatalogueButton Button(
        string id,
        string role,
        bool safe,
        string? alias = null,
        string[]? labels = null,
        string[]? tails = null,
        string[]? classicIds = null)
        => new(id, alias, role, safe, labels ?? [], tails ?? [], classicIds ?? []);
}
