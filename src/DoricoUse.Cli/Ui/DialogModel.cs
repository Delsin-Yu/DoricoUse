using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using DoricoUse.Cli.Json;

namespace DoricoUse.Cli.Ui;

/// <summary>A button as the UI Automation reader saw it.</summary>
/// <param name="Name">The label the dialog really shows, verbatim. May be empty, and may repeat another button's.</param>
/// <param name="AutomationId">
/// The element's own automation id, verbatim. For a Qt button this is a QML path whose last dot-separated segment
/// is the meaningful part; for a native <c>#32770</c> dialog it is the classic control id ("1" is accept, "2" is
/// cancel, and both were measured).
/// </param>
/// <param name="Invokable">True when the element exposes a pattern that can press it (invoke, toggle or a default action).</param>
/// <param name="Left">Screen x of the element's bounding rectangle, for the position fallback id.</param>
/// <param name="Top">Screen y of the element's bounding rectangle, for the position fallback id.</param>
internal sealed record DialogButtonInput(
    string Name,
    string? AutomationId,
    bool Invokable,
    int Left = 0,
    int Top = 0);

/// <summary>
/// Everything measured about one dialog, in plain data: no Win32 handle, no COM pointer, nothing Windows-shaped,
/// so this record can be built by a check project and reasoned about without Dorico.
/// </summary>
/// <param name="Title">The window caption, verbatim.</param>
/// <param name="WindowClass">The Win32 window class, verbatim (<c>Qt6101QWindow</c>, <c>#32770</c>, …).</param>
/// <param name="Owner">The owner window handle, as measured, or null when the window owns nothing.</param>
/// <param name="TextSource">Where the body text came from: <c>uia</c>, <c>legacy</c> or <c>none</c>.</param>
/// <param name="TextLines">The body text, in the order the dialog shows it, verbatim.</param>
/// <param name="Buttons">The pressable elements, in the order the reader found them.</param>
internal sealed record DialogInput(
    string Title,
    string WindowClass,
    string? Owner,
    string TextSource,
    IReadOnlyList<string> TextLines,
    IReadOnlyList<DialogButtonInput> Buttons);

/// <summary>What the read of a dialog cost and whether it was complete. No Windows types here either.</summary>
/// <param name="How">The mechanism: <c>win32</c>, or <c>win32+uia</c> when UI Automation was read as well.</param>
/// <param name="Milliseconds">What it cost.</param>
/// <param name="Degraded">True when the probe or the read failed, in which case the window facts stand alone.</param>
/// <param name="Error">Why it failed, when it did.</param>
/// <param name="Nodes">How many elements the read walked. Zero when it never got that far.</param>
/// <param name="Truncated">True when a limit stopped the read before the tree ended.</param>
/// <param name="Windows">
/// How many visible top-level windows Dorico had, when the caller measured it. Reported so that a null
/// <c>dialog</c> is a measurement and not an absence of one: zero windows means Dorico is not running, which is a
/// different answer from "nothing is modal".
/// </param>
/// <param name="DocumentEnabled">Whether Dorico's document window was enabled, when one was found.</param>
internal sealed record DialogProbeInfo(
    string How,
    long Milliseconds,
    bool Degraded = false,
    string? Error = null,
    int Nodes = 0,
    bool Truncated = false,
    int? Windows = null,
    bool? DocumentEnabled = null);

/// <summary>One reported button, with the id an agent passes back to <c>ui press</c>.</summary>
internal sealed record DialogButtonDescription(
    string Id,
    string? Alias,
    string Name,
    string? AutomationId,
    int Index,
    string Role,
    bool Safe,
    bool Invokable);

/// <summary>A dialog, described for the payload.</summary>
internal sealed record DialogDescription(
    string Id,
    string Kind,
    string Shape,
    string WindowClass,
    string? Owner,
    bool BlocksQueue,
    string Title,
    string? Description,
    string TextSource,
    IReadOnlyList<DialogButtonDescription> Buttons,
    string? PressWith);

/// <summary>
/// Turns what the Windows side measured into the identifiers, the policy and the JSON an agent reads.
/// </summary>
/// <remarks>
/// <para>
/// This file has no Windows API in it on purpose. Every rule that decides what an agent may press lives here, so
/// a check project can compile it on <c>net10.0</c> and assert the ids, the ordering, the refusal codes and the
/// payload shape without Dorico, a dialog or a COM pointer.
/// </para>
/// <para>
/// Two measured facts shape the id scheme (both from the dialogs that were read in full): a button label is not
/// an identifier — the MusicXML export dialog has two buttons labelled <c>选择全部</c> and one whose label is a
/// private-use glyph — and the classic Win32 control ids in a <c>#32770</c> dialog (<c>1</c> accept, <c>2</c>
/// cancel) are language-independent. So an id is derived, never positional: the catalogue's own entry first, then
/// a unique automation-id tail, then the classic control id, then a unique label, then the button's position.
/// </para>
/// </remarks>
internal static class DialogModel
{
    /// <summary>The window class of the classic Win32 dialog, measured for Dorico's folder picker.</summary>
    public const string NativeDialogClass = "#32770";

    /// <summary>How the dialog is shaped, for the payload: a Dorico/QML window or a native Windows dialog.</summary>
    public const string ShapeQt = "qt";

    public const string ShapeNative = "native";

    public const string KindKnown = "known";

    public const string KindUnknown = "unknown";

    public const string RoleDefault = "default";

    public const string RoleDismiss = "dismiss";

    public const string RoleDestructive = "destructive";

    public const string RoleUnknown = "unknown";

    /// <summary>How far apart two buttons may sit and still count as one row, for the position fallback.</summary>
    /// <remarks>
    /// An inference, not a measurement: no measured dialog needed the position fallback, and without a tolerance a
    /// one-pixel difference between two buttons of the same visual row would order them by luck.
    /// </remarks>
    private const int RowTolerancePixels = 8;

    /// <summary>The longest automation-id tail or label slug that will be used as an id.</summary>
    private const int MaxDerivedIdLength = 64;

    /// <summary>Describes one dialog: its identity, its buttons and the one press an agent should reach for.</summary>
    /// <param name="input">What the Windows side measured.</param>
    /// <param name="documentEnabled">
    /// Whether Dorico's document window was enabled when the dialog was found. False is the fact that decides this
    /// one: a window that is up while the document window is disabled is holding the queue, whatever its class says.
    /// Measured, and measured against the class hint: Dorico's crash reporter is a <c>Qt6101QWindowIcon</c> — the
    /// class this build reads as modeless, because the library manager has it — and while it was up it had disabled
    /// the document window <i>and</i> the library manager. Null falls back to the class hint alone.
    /// </param>
    public static DialogDescription Describe(DialogInput input, bool? documentEnabled = null)
    {
        ArgumentNullException.ThrowIfNull(input);

        var shape = IsNative(input.WindowClass) ? ShapeNative : ShapeQt;
        var entry = DialogCatalogue.Match(input.Title, input.WindowClass, [.. input.Buttons.Select(button => button.Name)]);
        var id = entry?.Id ?? UnknownId(input);
        var buttons = DeriveButtons(input, entry, shape);
        var lines = input.TextLines.Where(line => !string.IsNullOrWhiteSpace(line)).ToArray();

        return new DialogDescription(
            Id: id,
            Kind: entry is null ? KindUnknown : KindKnown,
            Shape: shape,
            WindowClass: input.WindowClass,
            Owner: input.Owner,
            BlocksQueue: documentEnabled == false || BlocksQueueHint(input.WindowClass),
            Title: input.Title,
            Description: lines.Length == 0 ? null : string.Join('\n', lines),
            TextSource: lines.Length == 0 ? "none" : input.TextSource,
            Buttons: buttons,
            PressWith: PressWith(id, buttons));
    }

    /// <summary>True for the classic Win32 dialog class, whose control ids are language-independent.</summary>
    public static bool IsNative(string windowClass)
        => string.Equals(windowClass, NativeDialogClass, StringComparison.Ordinal);

    /// <summary>
    /// Whether a window class is one whose dialogs stall Dorico's command queue.
    /// </summary>
    /// <remarks>
    /// A hint from the class alone, never the queue fact. Measured: <c>Qt6101QWindow</c> (the MusicXML export
    /// dialog) stalls the queue, <c>Qt6101QWindowIcon</c> (the library manager) does not, and a native
    /// <c>#32770</c> picker disables the window that raised it. So "Qt…Icon" is not a blocking class and a
    /// blocking dialog is reported as such; the trustworthy statement about the queue is <c>execution</c>, which
    /// comes from Dorico's own log.
    /// </remarks>
    public static bool BlocksQueueHint(string windowClass)
    {
        if (IsNative(windowClass))
        {
            return true;
        }

        return windowClass.StartsWith("Qt", StringComparison.OrdinalIgnoreCase)
            && !windowClass.EndsWith("Icon", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The deterministic id of a dialog the catalogue does not know: twelve hex digits of the SHA-256 over the
    /// title and the sorted button labels. The same dialog therefore keeps the same id across invocations.
    /// </summary>
    public static string UnknownId(DialogInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        var labels = input.Buttons.Select(button => button.Name).ToArray();
        Array.Sort(labels, StringComparer.Ordinal);

        var material = new StringBuilder(input.Title);
        material.Append('\0');

        foreach (var label in labels)
        {
            material.Append(label).Append('\0');
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(material.ToString()));

        return "unknown." + Convert.ToHexStringLower(hash.AsSpan(0, 6));
    }

    /// <summary>The <c>ui press</c> line an agent can paste, aimed at the safest button the dialog offers.</summary>
    /// <remarks>
    /// A dismiss button first, then the default one: measured, cancelling the MusicXML export dialog releases the
    /// commands Dorico had queued behind it, so the safe button is also the one that unblocks the queue.
    /// </remarks>
    public static string? PressWith(string dialogId, IReadOnlyList<DialogButtonDescription> buttons)
    {
        ArgumentNullException.ThrowIfNull(buttons);

        var target = buttons.FirstOrDefault(button => button.Safe && button.Invokable && button.Role == RoleDismiss)
            ?? buttons.FirstOrDefault(button => button.Safe && button.Invokable && button.Role == RoleDefault)
            ?? buttons.FirstOrDefault(button => button.Safe && button.Invokable);

        return target is null ? null : $"dorico-use ui press --dialog {dialogId} --button {target.Id}";
    }

    /// <summary>The <c>dialog</c> object of the payload, exactly as section 11 of the handover specifies it.</summary>
    public static JsonObject Payload(DialogDescription dialog, DialogProbeInfo probe, string? warning = null)
    {
        ArgumentNullException.ThrowIfNull(dialog);
        ArgumentNullException.ThrowIfNull(probe);

        var buttons = new JsonArray();

        foreach (var button in dialog.Buttons)
        {
            var item = new JsonObject
            {
                ["id"] = button.Id,
                ["name"] = button.Name,
                ["automationId"] = button.AutomationId,
                ["index"] = button.Index,
                ["role"] = button.Role,
                ["safe"] = button.Safe,
                ["invokable"] = button.Invokable,
            };

            if (button.Alias is not null)
            {
                item["alias"] = button.Alias;
            }

            JsonArr.AddNode(buttons, item);
        }

        var payload = new JsonObject
        {
            ["id"] = dialog.Id,
            ["kind"] = dialog.Kind,
            ["shape"] = dialog.Shape,
            ["class"] = dialog.WindowClass,
            ["owner"] = dialog.Owner,
            ["blocksQueue"] = dialog.BlocksQueue,
            ["title"] = dialog.Title,
            ["description"] = dialog.Description,
            ["textSource"] = dialog.TextSource,
            ["buttons"] = buttons,
            ["pressWith"] = dialog.PressWith,
            ["probe"] = ProbePayload(probe),
        };

        if (dialog.BlocksQueue)
        {
            payload["note"] = "This window's class is one whose dialogs stall Dorico's command queue (a hint from "
                + "the class, not a measurement of the queue): a command posted while it is up is queued, not lost, "
                + "and runs as soon as the dialog is answered. `execution` on this payload is the measurement.";
        }

        if (warning is not null)
        {
            payload["warning"] = warning;
        }

        return payload;
    }

    /// <summary>The <c>probe</c> object: what the read cost, whether it was complete, and how it failed if it did.</summary>
    public static JsonObject ProbePayload(DialogProbeInfo probe)
    {
        ArgumentNullException.ThrowIfNull(probe);

        var payload = new JsonObject
        {
            ["how"] = probe.How,
            ["ms"] = probe.Milliseconds,
            ["degraded"] = probe.Degraded,
            ["nodes"] = probe.Nodes,
            ["truncated"] = probe.Truncated,
        };

        if (probe.Windows is not null)
        {
            payload["doricoWindows"] = probe.Windows.Value;
            payload["documentEnabled"] = probe.DocumentEnabled;
        }

        if (probe.Error is not null)
        {
            payload["error"] = probe.Error;
        }

        return payload;
    }

    /// <summary>
    /// Assigns every button its id, in the order section 7 of the handover fixes: the catalogue's own entry, then
    /// a unique automation-id tail, then the classic Win32 control id, then a unique label, then the position.
    /// </summary>
    private static List<DialogButtonDescription> DeriveButtons(
        DialogInput input,
        DialogCatalogueEntry? entry,
        string shape)
    {
        var tails = input.Buttons.Select(button => Tail(button.AutomationId)).ToArray();
        var slugs = input.Buttons.Select(button => Slug(button.Name)).ToArray();
        var ranks = PositionRanks(input.Buttons);
        var taken = new HashSet<string>(StringComparer.Ordinal);
        var described = new List<DialogButtonDescription>(input.Buttons.Count);

        for (var index = 0; index < input.Buttons.Count; index++)
        {
            var button = input.Buttons[index];
            var catalogued = MatchCatalogued(entry, button, tails[index]);
            var id = catalogued?.Id
                ?? ClassicId(shape, button.AutomationId)
                ?? UniqueOrNull(tails, index, tails[index])
                ?? UniqueOrNull(slugs, index, slugs[index])
                ?? $"position-{ranks[index].ToString(CultureInfo.InvariantCulture)}";

            // Two buttons can still collide (the same label twice, both reaching the label rule) - the position is
            // then the only thing left that tells them apart, and a wrong guess would press the wrong one.
            if (!taken.Add(id))
            {
                id = $"{id}-{ranks[index].ToString(CultureInfo.InvariantCulture)}";
            }

            described.Add(new DialogButtonDescription(
                Id: id,
                Alias: catalogued?.Alias,
                Name: button.Name,
                AutomationId: string.IsNullOrEmpty(button.AutomationId) ? null : button.AutomationId,
                Index: index,
                Role: catalogued?.Role ?? RoleUnknown,
                Safe: catalogued?.Safe ?? false,
                Invokable: button.Invokable));
        }

        return described;
    }

    /// <summary>
    /// The classic control id of a native dialog's button, or null.
    /// </summary>
    /// <remarks>
    /// Measured on the folder picker Dorico raises: accept is <c>automationId="1"</c> and cancel is <c>"2"</c>,
    /// which is what every Win32 dialog uses and what does not change with the UI language. The alias is
    /// deliberately not set here: aliases come from the catalogue, so only a measured dialog claims one.
    /// </remarks>
    private static string? ClassicId(string shape, string? automationId)
    {
        if (shape != ShapeNative || string.IsNullOrEmpty(automationId)
            || !int.TryParse(automationId, NumberStyles.None, CultureInfo.InvariantCulture, out var id))
        {
            return null;
        }

        return id switch
        {
            1 => "accept",
            2 => "cancel",
            _ => "win-" + id.ToString(CultureInfo.InvariantCulture),
        };
    }

    private static DialogCatalogueButton? MatchCatalogued(DialogCatalogueEntry? entry, DialogButtonInput button, string tail)
    {
        if (entry is null)
        {
            return null;
        }

        foreach (var candidate in entry.Buttons)
        {
            if (candidate.MatchAutomationIdTails.Contains(tail, StringComparer.OrdinalIgnoreCase)
                || candidate.MatchClassicIds.Contains(button.AutomationId ?? string.Empty, StringComparer.Ordinal)
                || candidate.MatchLabels.Contains(button.Name, StringComparer.Ordinal))
            {
                return candidate;
            }
        }

        return null;
    }

    private static string? UniqueOrNull(string[] values, int index, string candidate)
    {
        if (string.IsNullOrEmpty(candidate))
        {
            return null;
        }

        return values.Count(value => string.Equals(value, candidate, StringComparison.Ordinal)) == 1
            ? candidate
            : null;
    }

    /// <summary>The stable, meaningful last segment of a Qt automation id, or an empty string.</summary>
    private static string Tail(string? automationId)
    {
        if (string.IsNullOrEmpty(automationId))
        {
            return string.Empty;
        }

        var text = automationId.Trim();
        var dot = text.LastIndexOf('.');

        if (dot >= 0)
        {
            text = text[(dot + 1)..];
        }

        if (text.Length == 0 || text.Length > MaxDerivedIdLength)
        {
            return string.Empty;
        }

        // A tail is only usable as an identifier if an agent can paste it back verbatim into a shell, so anything
        // outside this set disqualifies it and the next rule (the label, then the position) takes over.
        foreach (var character in text)
        {
            if (!char.IsAsciiLetterOrDigit(character) && character is not ('_' or '-' or '.'))
            {
                return string.Empty;
            }
        }

        return text;
    }

    /// <summary>
    /// A label turned into something an agent can paste. Everything outside ASCII letters and digits becomes a
    /// dash, so the private-use glyph one measured button carries as its label yields an empty slug rather than an
    /// unprintable identifier.
    /// </summary>
    private static string Slug(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return string.Empty;
        }

        var text = new StringBuilder(name.Length);
        var lastWasDash = false;

        foreach (var character in name.Trim())
        {
            if (char.IsAsciiLetterOrDigit(character))
            {
                text.Append(char.ToLowerInvariant(character));
                lastWasDash = false;
            }
            else if (!lastWasDash)
            {
                text.Append('-');
                lastWasDash = true;
            }
        }

        var slug = text.ToString().Trim('-');

        return slug.Length is 0 or > MaxDerivedIdLength ? string.Empty : slug;
    }

    /// <summary>
    /// One-based ranks of the buttons in reading order: left to right within a row, rows top to bottom, the label
    /// breaking a tie. Used only for the id of last resort, so the reported <c>index</c> stays the order the reader
    /// actually found them in.
    /// </summary>
    private static int[] PositionRanks(IReadOnlyList<DialogButtonInput> buttons)
    {
        var order = Enumerable.Range(0, buttons.Count)
            .OrderBy(index => buttons[index].Top / RowTolerancePixels)
            .ThenBy(index => buttons[index].Left)
            .ThenBy(index => buttons[index].Name, StringComparer.Ordinal)
            .ToArray();

        var ranks = new int[buttons.Count];

        for (var rank = 0; rank < order.Length; rank++)
        {
            ranks[order[rank]] = rank + 1;
        }

        return ranks;
    }

    /// <summary>
    /// Decides whether one button may be pressed, and refuses with the handover's stable code when it may not.
    /// </summary>
    /// <param name="dialog">The dialog as it is up right now.</param>
    /// <param name="buttonId">The id the caller asked for, or null to press nothing.</param>
    /// <param name="allowUnknownDialog">True when the caller passed <c>--allow-unknown-dialog</c>.</param>
    /// <param name="allowDestructive">True when the caller passed <c>--allow-destructive</c>.</param>
    /// <returns>The button that may be pressed.</returns>
    public static DialogButtonDescription Authorize(
        DialogDescription dialog,
        string buttonId,
        bool allowUnknownDialog,
        bool allowDestructive)
    {
        ArgumentNullException.ThrowIfNull(dialog);
        ArgumentNullException.ThrowIfNull(buttonId);

        var button = dialog.Buttons.FirstOrDefault(candidate => string.Equals(candidate.Id, buttonId, StringComparison.Ordinal));

        if (button is null)
        {
            throw Refuse(
                "ButtonNotFound",
                $"The dialog '{dialog.Id}' ('{dialog.Title}') has no button '{buttonId}'. Its ids are: "
                + Ids(dialog),
                new JsonObject
                {
                    ["dialog"] = dialog.Id,
                    ["requested"] = buttonId,
                    ["ids"] = IdList(dialog),
                });
        }

        if (dialog.Kind == KindUnknown && !allowUnknownDialog)
        {
            throw Refuse(
                "UnknownDialog",
                $"The dialog '{dialog.Title}' is not in this build's catalogue, so pressing its buttons is refused. "
                + "It is reported in full above. Pass --allow-unknown-dialog to press a button of an unmeasured "
                + "dialog anyway, and --allow-destructive as well, because no button of an unknown dialog is "
                + "catalogued as safe.",
                new JsonObject
                {
                    ["dialog"] = dialog.Id,
                    ["title"] = dialog.Title,
                    ["ids"] = IdList(dialog),
                });
        }

        if (!button.Safe && !allowDestructive)
        {
            throw Refuse(
                "DestructiveRefused",
                $"Button '{button.Id}' ('{button.Name}') is not catalogued as safe"
                + (dialog.Kind == KindUnknown ? ", and this dialog is unknown" : string.Empty)
                + ", so pressing it is refused. Pass "
                + (dialog.Kind == KindUnknown ? "--allow-unknown-dialog and --allow-destructive" : "--allow-destructive")
                + " only when the caller means it.",
                new JsonObject
                {
                    ["dialog"] = dialog.Id,
                    ["button"] = button.Id,
                    ["name"] = button.Name,
                    ["role"] = button.Role,
                    ["safe"] = false,
                });
        }

        if (!button.Invokable)
        {
            throw Refuse(
                "ButtonNotInvokable",
                $"Button '{button.Id}' ('{button.Name}') exposes no pattern that can press it (no invoke, no toggle, "
                + "no default action), so there is nothing to call. This build never clicks a coordinate.",
                new JsonObject
                {
                    ["dialog"] = dialog.Id,
                    ["button"] = button.Id,
                    ["name"] = button.Name,
                });
        }

        return button;
    }

    /// <summary>The codes <c>ui press</c> can refuse with, for the check that every one of them is exit 1.</summary>
    public static IReadOnlyList<string> RefusalCodes { get; } =
    [
        "DialogNotFound",
        "WrongDialog",
        "ButtonNotFound",
        "ButtonNotInvokable",
        "UnknownDialog",
        "DestructiveRefused",
        "PickerNotSettable",
    ];

    /// <summary>The exit code a refusal carries. A refusal is the caller's fault, so it is always 1.</summary>
    public const int RefusalExitCode = 1;

    /// <summary>The exit code a probe failure carries: the runtime failed, the caller did not.</summary>
    public const int ProbeFailureExitCode = 2;

    private static CliException Refuse(string code, string message, JsonObject details)
        => new(code, message, RefusalExitCode) { Details = details };

    private static string Ids(DialogDescription dialog)
    {
        var ids = dialog.Buttons.Select(button => button.Id).ToArray();

        return ids.Length == 0 ? "(none: the dialog exposes no button)" : string.Join(", ", ids);
    }

    private static JsonArray IdList(DialogDescription dialog)
    {
        var ids = new JsonArray();

        foreach (var button in dialog.Buttons)
        {
            JsonArr.AddText(ids, button.Id);
        }

        return ids;
    }
}
