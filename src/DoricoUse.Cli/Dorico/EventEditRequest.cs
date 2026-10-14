using System.Globalization;
using System.Text.Json.Nodes;
using DoricoUse.Cli.Json;

namespace DoricoUse.Cli.Dorico;

/// <summary>
/// Where one event sits: the triple <c>EventEdit.Select</c> takes, in the order the address is written and read
/// (<c>(flowID,eventBlockID,eventID)</c>, e.g. <c>(0,106,120108)</c>).
/// </summary>
internal sealed record EventAddress(int FlowId, int EventBlockId, int EventId);

/// <summary>
/// One command of an <c>event set</c>: its name and its parameters, in the order Dorico will receive them.
/// </summary>
internal sealed record EventEditStep(string Name, IReadOnlyList<KeyValuePair<string, string>> Parameters);

/// <summary>
/// The one place the route to setting one field of one event is composed, and the one place its usage is refused.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists.</b> A dogfood run had to set one event's duration by hand, and it took three steps: a
/// <c>file read --tree block</c> to reverse-look-up which block an <c>eventID</c> lives in, an
/// <c>EventEdit.Select</c> with the triple, and then <c>MouseEdit.ChangeEventPositionOrDuration</c> with <b>all
/// four</b> of its parameters. Getting the command wrong is not a small matter: measured 2026-10-13,
/// <c>MouseEdit.ChangeEventPositionOrDuration</c> given only <c>NewDuration=15</c> answered <c>kOK</c>, raised
/// Dorico's 错误发生 box (<c>dialog: {id: dorico.error, openMs: 0}</c> — this command raised it), left the duration
/// unchanged, and then made every later command look like it ran while it sat on a stalled queue. The four
/// parameters are the ones a person's own drag left in <c>application.log</c>:
/// <c>Address=(0\,106\,120108), EventType=kTrillEvent, NewDuration=15, MouseEditWithSideEffect=false</c>. So they
/// are composed here, together, and a caller cannot leave one out: the only way to build the edit is
/// <see cref="Edit"/>, which takes the address, the event type, the field and the value.
/// </para>
/// <para>
/// <b>Why the address's commas are escaped.</b> The protocol joins a command's parameters with commas, so the line
/// Dorico receives is <c>MouseEdit.ChangeEventPositionOrDuration?Address=(0\,106\,120108),EventType=…</c>; a plain
/// comma inside the address would be read as a parameter separator and the address would arrive in three pieces.
/// The escaped spelling is the one the run's working trill fix sent (<c>"Address": "(0\,106\,120108)"</c> in that
/// invocation's payload), and it is the only spelling this class composes. The deduction is from the join, not
/// from a measurement of the unescaped form — what was measured is that the four escaped parameters changed the
/// file and that omitting them raised the error box.
/// </para>
/// <para>
/// <b>Nothing here is relative.</b> <c>EventEdit.DurationLengthen</c> and <c>DurationShorten</c> are not offered,
/// by this class or by the command that uses it: measured, their step is not constant — 0.5 crotchets on a tempo
/// event, 1/6 once on a trill — and a batch that assumed one step corrupted two trills (m59 4 → 179/3 and m386
/// 4 → 13). An absolute set has no step to be wrong about. See the README for that, under the forbidden route.
/// </para>
/// </remarks>
internal static class EventEditRequest
{
    /// <summary>The command that sets the selection to one event, with no mouse and no coordinates.</summary>
    public const string SelectCommand = "EventEdit.Select";

    /// <summary>The command that sets an event's position or duration absolutely, when it is given its address.</summary>
    public const string EditCommand = "MouseEdit.ChangeEventPositionOrDuration";

    public const string AddressParameter = "Address";

    public const string EventTypeParameter = "EventType";

    /// <summary>The parameter that keeps the edit from being a mouse-driven one, always sent as <c>false</c>.</summary>
    public const string SideEffectParameter = "MouseEditWithSideEffect";

    /// <summary>
    /// The value <see cref="SideEffectParameter"/> always carries.
    /// </summary>
    /// <remarks>
    /// <c>false</c> is what the person's drag wrote, and that drag is the only shape of this command that has been
    /// measured to work. What <c>true</c> does has not been measured here, so this build does not send it.
    /// </remarks>
    public const string SideEffectValue = "false";

    public const string DurationField = "duration";

    public const string PositionField = "position";

    /// <summary>The parameter that carries a new duration, in crotchets.</summary>
    public const string DurationParameter = "NewDuration";

    /// <summary>
    /// The parameter that carries a new position, in crotchets from the start of the flow.
    /// </summary>
    /// <remarks>
    /// Measured 2026-10-13: <c>NewPosition</c> works for a timebase event (the run moved m260's gradual tempo from
    /// position 1039 to 1042, its beat 4) and does nothing for a line attached to a note — a glissando's endpoint
    /// did not move with either <c>NewDuration</c> or <c>NewPosition</c>, and the route there is to delete the line
    /// and create it again from the note it starts on.
    /// </remarks>
    public const string PositionParameter = "NewPosition";

    public const string SelectFlowParameter = "FlowID";

    public const string SelectEventParameter = "EventID";

    public const string SelectBlockParameter = "EventBlockID";

    /// <summary>The suffix every event type spelling ends with, which is what turns one into a record name.</summary>
    private const string EventSuffix = "Event";

    /// <summary>The suffix of the name the file itself uses for the same thing.</summary>
    private const string RecordSuffix = "Definition";

    private static readonly Dictionary<string, string> FieldParameters = new(StringComparer.Ordinal)
    {
        [DurationField] = DurationParameter,
        [PositionField] = PositionParameter,
    };

    /// <summary>The fields this build can set, and the only values <c>--field</c> accepts.</summary>
    public static IReadOnlyList<string> Fields { get; } = [DurationField, PositionField];

    /// <summary>
    /// The triple <c>--address</c> carries, refused unless it really is three numbers.
    /// </summary>
    /// <param name="text">The option's value, with or without the parentheses, and with plain or escaped commas.</param>
    /// <remarks>
    /// Parentheses are optional because a shell needs them quoted either way, and the <c>\,</c> spelling is accepted
    /// because that is how the address appears in Dorico's log and in this tool's own payloads — a caller who
    /// copies one of those should not have to strip the backslashes by hand.
    /// </remarks>
    public static EventAddress ParseAddress(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new CliException(
                "InvalidAddress",
                "--address is missing. It is the event's own triple (flowID,eventBlockID,eventID), and `event list "
                + "--from <project.dorico> --record <k…Event>` prints exactly that for every event of one kind, "
                + "which is the reverse lookup this command exists to save you. Nothing was sent.",
                1)
            {
                Details = new JsonObject
                {
                    ["option"] = "--address",
                    ["expected"] = "(flowID,eventBlockID,eventID)",
                    ["example"] = "(0,106,120108)",
                },
            };
        }

        var trimmed = text.Trim();

        if (trimmed.Length >= 2 && trimmed[0] == '(' && trimmed[^1] == ')')
        {
            trimmed = trimmed[1..^1];
        }

        // The escaped spelling the log shows is the same three numbers with a backslash in front of each comma.
        var parts = trimmed.Replace("\\", string.Empty, StringComparison.Ordinal).Split(',');

        if (parts.Length != 3)
        {
            throw RefuseAddress(
                text,
                $"it has {parts.Length} part{(parts.Length == 1 ? string.Empty : "s")} where an address has three");
        }

        var numbers = new int[3];

        for (var index = 0; index < parts.Length; index++)
        {
            if (!int.TryParse(parts[index].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out numbers[index])
                || numbers[index] < 0)
            {
                throw RefuseAddress(
                    text,
                    $"'{parts[index].Trim()}' is not an ID, and an address holds three (a flow, a block and an event "
                    + "are all identified by whole numbers)");
            }
        }

        return new EventAddress(numbers[0], numbers[1], numbers[2]);
    }

    /// <summary>The address as a reader reads it: <c>(0,106,120108)</c>, the spelling <c>event list</c> prints.</summary>
    public static string AddressText(EventAddress address)
        => $"({Number(address.FlowId)},{Number(address.EventBlockId)},{Number(address.EventId)})";

    /// <summary>
    /// The address as the <c>Address</c> parameter carries it: <c>(0\,106\,120108)</c>, commas escaped, because the
    /// protocol separates a command's parameters with commas.
    /// </summary>
    public static string EscapedAddressText(EventAddress address)
        => $"({Number(address.FlowId)}\\,{Number(address.EventBlockId)}\\,{Number(address.EventId)})";

    /// <summary>The file's own record name for an event type or for a record name, refused if it is neither.</summary>
    /// <param name="name">A spelling such as <c>kTrillEvent</c>, or a record name such as <c>TrillEventDefinition</c>.</param>
    /// <remarks>
    /// The two spellings are the same word with different clothing: <c>kTrillEvent</c> is what
    /// <c>EventType</c> and Dorico's status calls it, and <c>TrillEventDefinition</c> is the node the project file
    /// writes (37 of them in the reference import, against 37 trill events in the live read). The mapping is
    /// derived from those two measured spellings, so a name that fits neither shape is refused instead of guessed
    /// at — a reader that invented a record name would report "nothing found" for a file that holds the events.
    /// </remarks>
    public static string RecordName(string? name)
    {
        var wanted = (name ?? string.Empty).Trim();

        if (wanted.Length == 0)
        {
            throw new CliException(
                "InvalidArgument",
                "A record name is missing. Pass the event type `event set` takes (--event-type kTrillEvent) or the "
                + "name the project file uses (--record TrillEventDefinition).",
                1)
            {
                Details = new JsonObject
                {
                    ["expected"] = "kTrillEvent or TrillEventDefinition",
                    ["example"] = "kGradualTempoChangeEvent",
                },
            };
        }

        if (EnumVocabulary.IsSpelling(wanted))
        {
            if (!wanted.EndsWith(EventSuffix, StringComparison.Ordinal))
            {
                throw new CliException(
                    "InvalidArgument",
                    $"'{wanted}' is a Dorico spelling but not an event type, so the name the project file uses "
                    + $"cannot be derived from it. Every event type this build has seen ends with '{EventSuffix}' "
                    + $"(kTrillEvent, kGradualTempoChangeEvent); pass --record with the file's own node name "
                    + "(TrillEventDefinition) if that is what you mean.",
                    1)
                {
                    Details = new JsonObject
                    {
                        ["name"] = wanted,
                        ["expected"] = $"k…{EventSuffix} or …{RecordSuffix}",
                    },
                };
            }

            return wanted[1..] + RecordSuffix;
        }

        if (wanted.EndsWith(RecordSuffix, StringComparison.Ordinal))
        {
            return wanted;
        }

        throw new CliException(
            "InvalidArgument",
            $"'{wanted}' is neither an event type (kTrillEvent, kGradualTempoChangeEvent) nor a name the project "
            + $"file uses ({RecordSuffix} at the end, e.g. TrillEventDefinition). Nothing was read.",
            1)
        {
            Details = new JsonObject
            {
                ["name"] = wanted,
                ["expected"] = $"k…{EventSuffix} or …{RecordSuffix}",
            },
        };
    }

    /// <summary>The record name to read back for a <c>--verify</c>, from <c>--record</c> or from <c>--event-type</c>.</summary>
    public static string RecordFor(string? record, string? eventType)
        => RecordName(string.IsNullOrWhiteSpace(record) ? eventType : record);

    /// <summary>
    /// The other direction: the event type spelling for a record name, <c>TrillEventDefinition</c> →
    /// <c>kTrillEvent</c>.
    /// </summary>
    /// <remarks>
    /// Used by <c>event list</c>, which accepts either spelling and has to print the one
    /// <c>event set --event-type</c> wants — a caller who listed <c>--record TrillEventDefinition</c> should be
    /// able to paste the address line the answer carries without translating it by hand. It is the exact inverse of
    /// <see cref="RecordName"/>, and the check asserts the round trip in both directions rather than trusting that.
    /// </remarks>
    public static string EventTypeFor(string recordName)
    {
        var name = (recordName ?? string.Empty).Trim();

        if (name.Length > RecordSuffix.Length && name.EndsWith(RecordSuffix, StringComparison.Ordinal))
        {
            return "k" + name[..^RecordSuffix.Length];
        }

        throw new CliException(
            "InvalidArgument",
            $"'{name}' is not a name the project file uses, so the event type it stands for cannot be named "
            + $"({RecordSuffix} at the end, e.g. TrillEventDefinition).",
            1)
        {
            Details = new JsonObject
            {
                ["name"] = name,
                ["expected"] = $"…{RecordSuffix}",
                ["example"] = "TrillEventDefinition",
            },
        };
    }

    /// <summary>The parameter <c>--field</c> stands for, refused unless it is one of the two this build can set.</summary>
    public static string RequireField(string? field)
    {
        var wanted = (field ?? string.Empty).Trim();

        if (wanted.Length == 0)
        {
            throw new CliException(
                "InvalidArgument",
                $"--field is missing. It names what the value is: {DurationField} sets the event's duration, "
                + $"{PositionField} sets where it starts. Nothing was sent.",
                1)
            {
                Details = new JsonObject
                {
                    ["option"] = "--field",
                    ["known"] = Strings(Fields),
                },
            };
        }

        if (!FieldParameters.TryGetValue(wanted, out var parameter))
        {
            throw new CliException(
                "UnknownField",
                $"'{wanted}' is not a field this command can set. It sets {string.Join(" or ", Fields)} "
                + $"({DurationField} sends {DurationParameter}, {PositionField} sends {PositionParameter}), and "
                + "nothing else: the parameters an event's other fields would need have not been measured on this "
                + "command. Nothing was sent.",
                1)
            {
                Details = new JsonObject
                {
                    ["option"] = "--field",
                    ["field"] = wanted,
                    ["known"] = Strings(Fields),
                },
            };
        }

        return parameter;
    }

    /// <summary>The new value, in crotchets, refused unless it is a number or a fraction.</summary>
    /// <param name="value">What the caller typed: <c>4</c>, <c>11/2</c>, <c>2.5</c> is not accepted.</param>
    /// <remarks>
    /// The unit is crotchets, and it is measured: the file writes an event's <c>position</c> and <c>duration</c> as
    /// exact rationals in crotchets (a 16th is <c>1/4</c>, a 4/4 bar is 4), a person's drag of m16 set that
    /// event's <c>duration</c> to <c>2</c> (= two crotchets), and the run's own absolute sets sent
    /// <c>NewDuration=9/2</c> and <c>NewDuration=15</c> and read those exact numbers back out of the saved file.
    /// So a fraction is not a convenience here, it is how Dorico states a triplet's length, and the text is passed
    /// through unreduced for that reason.
    /// </remarks>
    public static string RequireValue(string? value)
    {
        var wanted = (value ?? string.Empty).Trim();

        if (wanted.Length == 0 || !TryFraction(wanted, out var numerator, out var denominator) || numerator < 0)
        {
            throw new CliException(
                "InvalidArgument",
                $"'{(value ?? string.Empty).Trim()}' is not a value in crotchets. Pass a whole number (4) or a "
                + "fraction (11/2) — the file states an event's duration and position as exact rationals in "
                + "crotchets, so a triplet's length is a fraction and a 4/4 bar is 4. Nothing was sent.",
                1)
            {
                Details = new JsonObject
                {
                    ["option"] = "--value",
                    ["value"] = wanted,
                    ["unit"] = "crotchets",
                    ["expected"] = "a number or a fraction, e.g. 4 or 11/2",
                },
            };
        }

        return wanted;
    }

    /// <summary>The event type the <c>EventType</c> parameter carries, refused unless it is a Dorico spelling.</summary>
    /// <remarks>
    /// The shape is all this build checks, on purpose: the vocabulary's table holds the values this project has
    /// seen Dorico accept, and it holds no event type, so a spelling-shaped value is sent unchecked and the payload
    /// says so. Refusing one that is not in a table that has no event types in it would break every working call to
    /// this command — which is the bug <c>EventEdit.ChangeProperty</c>'s <c>Type</c> already caused once.
    /// </remarks>
    public static string RequireEventType(string? eventType)
    {
        var wanted = (eventType ?? string.Empty).Trim();

        if (wanted.Length == 0)
        {
            throw new CliException(
                "InvalidArgument",
                "--event-type is missing. It is the EventType the edit carries (kTrillEvent, "
                + "kGradualTempoChangeEvent) and the one parameter that tells Dorico what kind of event the address "
                + "points at: measured 2026-10-13, the same command with its address and its event type omitted "
                + "raised Dorico's 错误发生 box, which then stalls the whole command queue. Nothing was sent.",
                1)
            {
                Details = new JsonObject
                {
                    ["option"] = "--event-type",
                    ["expected"] = "a Dorico event type spelling, e.g. kTrillEvent",
                    ["example"] = "kGradualTempoChangeEvent",
                },
            };
        }

        if (!EnumVocabulary.IsSpelling(wanted))
        {
            throw new CliException(
                "InvalidArgument",
                $"'{wanted}' is not a Dorico event type spelling. They are 'k' and a capital letter "
                + "(kTrillEvent, kGradualTempoChangeEvent, kNoteEvent), and the value goes straight into the "
                + "command's EventType parameter. `event list --from <file> --record <the same word>` prints the "
                + "spelling for a record name if you have only that. Nothing was sent.",
                1)
            {
                Details = new JsonObject
                {
                    ["option"] = "--event-type",
                    ["eventType"] = wanted,
                    ["expected"] = "k…Event",
                    ["suggestions"] = Strings(EnumVocabulary.Suggest(wanted, "EventType")),
                },
            };
        }

        return wanted;
    }

    /// <summary>The <c>EventEdit.Select</c> step for an address: the same triple the <c>Address</c> carries.</summary>
    public static EventEditStep Select(EventAddress address) => new(
        SelectCommand,
        [
            new(SelectFlowParameter, Number(address.FlowId)),
            new(SelectEventParameter, Number(address.EventId)),
            new(SelectBlockParameter, Number(address.EventBlockId)),
        ]);

    /// <summary>
    /// The edit step, with every one of its four measured parameters.
    /// </summary>
    /// <remarks>
    /// The parameters are ordered as the person's drag left them in the log — <c>Address</c>, <c>EventType</c>,
    /// the new value, <c>MouseEditWithSideEffect</c> — so a reader can compare this command with the one Dorico
    /// logged for that drag without reordering anything in their head.
    /// </remarks>
    public static EventEditStep Edit(EventAddress address, string? eventType, string? field, string? value)
    {
        var type = RequireEventType(eventType);
        var parameter = RequireField(field);
        var text = RequireValue(value);

        return new EventEditStep(
            EditCommand,
            [
                new(AddressParameter, EscapedAddressText(address)),
                new(EventTypeParameter, type),
                new(parameter, text),
                new(SideEffectParameter, SideEffectValue),
            ]);
    }

    /// <summary>The whole route, in the order it is sent: the select, then the edit.</summary>
    /// <param name="select">
    /// False for <c>--no-select</c>, which sends the edit alone. It is false only when the caller says so: the
    /// address is enough to address the edit, and selecting first is what the run did (and what makes Dorico's own
    /// status show which event the next command is about).
    /// </param>
    public static IReadOnlyList<EventEditStep> Compose(
        EventAddress address,
        string? eventType,
        string? field,
        string? value,
        bool select)
    {
        var edit = Edit(address, eventType, field, value);

        return select ? [Select(address), edit] : [edit];
    }

    /// <summary>
    /// The <c>verified</c> entry for one field of one event, in the shape <c>options set</c> already prints:
    /// <c>wanted</c>, <c>actual</c>, <c>found</c>, <c>ok</c>, plus the facts a reader needs to judge them.
    /// </summary>
    /// <param name="wanted">The value this invocation asked for, in crotchets.</param>
    /// <param name="actual">What the file holds, or null when the file holds nothing at that address.</param>
    /// <param name="found">Whether the address holds an event of the record that was asked for.</param>
    /// <param name="addressFound">
    /// Whether the address holds <i>any</i> event: reported beside <paramref name="found"/> so that "the event is
    /// not there" and "the event is there but is another kind" cannot look the same.
    /// </param>
    /// <param name="record">The file's own record name, the one the address was looked up under.</param>
    /// <remarks>
    /// The comparison is exact and it is on the number, not on the spelling: a file that writes <c>4</c> and a
    /// caller who asked for <c>8/2</c> agree, because both are four crotchets. The file writes a duration reduced
    /// (<c>4</c>, <c>7/2</c>, <c>179/3</c>), not always in the spelling the caller typed, so a text comparison
    /// would report a difference that is not one.
    /// </remarks>
    public static JsonObject Verified(
        string wanted,
        string? actual,
        bool found,
        bool addressFound,
        string field,
        string parameter,
        string record,
        int? measure,
        string? beat)
    {
        var match = found && SameRational(wanted, actual);

        var entry = new JsonObject
        {
            ["wanted"] = wanted,
            ["actual"] = actual,
            ["found"] = found,
            ["ok"] = match,
            ["field"] = field,
            ["parameter"] = parameter,
            ["record"] = record,
            ["measure"] = measure is null ? null : JsonValue.Create(measure.Value),
            ["beat"] = beat,
            ["addressFound"] = addressFound,
        };

        entry["note"] = match
            ? "The copy named holds this value at this address, compared as exact rationals in crotchets."
            : !addressFound
                ? "The copy named holds no event at this address. Either the address is wrong, or the copy was "
                    + "saved before the change was made: only a save writes what Dorico holds in memory to a file."
                : !found
                    ? $"The address holds an event, but not one this reader calls {record}: the record it read is "
                        + "reported beside it."
                    : "The copy named holds a different value at this address, so this invocation's edit is not in "
                        + "it. A value written to the document is only in a file after a save.";

        return entry;
    }

    /// <summary>Whether two crotchet values are the same number, exactly: <c>4</c> and <c>8/2</c> agree.</summary>
    public static bool SameRational(string? left, string? right)
    {
        if (!TryFraction(left, out var leftNumerator, out var leftDenominator)
            || !TryFraction(right, out var rightNumerator, out var rightDenominator))
        {
            return false;
        }

        // Reduced first, so the comparison is two pairs of small numbers and cannot overflow on a value nobody has.
        var (reducedLeftNumerator, reducedLeftDenominator) = Reduce(leftNumerator, leftDenominator);
        var (reducedRightNumerator, reducedRightDenominator) = Reduce(rightNumerator, rightDenominator);

        return reducedLeftNumerator == reducedRightNumerator && reducedLeftDenominator == reducedRightDenominator;
    }

    /// <summary>A whole number as text, with the invariant culture, so a payload never carries a locale's digits.</summary>
    public static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>Reads <c>4</c> or <c>11/2</c> exactly, without floating point.</summary>
    private static bool TryFraction(string? text, out long numerator, out long denominator)
    {
        numerator = 0;
        denominator = 1;

        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var parts = text.Trim().Split('/');

        if (parts.Length > 2
            || !long.TryParse(parts[0].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out numerator))
        {
            return false;
        }

        if (parts.Length == 1)
        {
            return true;
        }

        if (!long.TryParse(parts[1].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out denominator)
            || denominator == 0)
        {
            return false;
        }

        if (denominator < 0)
        {
            numerator = -numerator;
            denominator = -denominator;
        }

        return true;
    }

    private static (long Numerator, long Denominator) Reduce(long numerator, long denominator)
    {
        var divisor = Gcd(Math.Abs(numerator), denominator);

        return divisor > 1 ? (numerator / divisor, denominator / divisor) : (numerator, denominator);
    }

    private static long Gcd(long left, long right)
    {
        while (right != 0)
        {
            (left, right) = (right, left % right);
        }

        return left == 0 ? 1 : left;
    }

    private static CliException RefuseAddress(string? text, string why)
        => new(
            "InvalidAddress",
            $"--address is '{text}', and {why}. It is the event's own triple, written (flowID,eventBlockID,eventID), "
            + "e.g. (0,106,120108) - and `event list --from <project.dorico> --record <k…Event>` prints it for "
            + "every event of one kind. Nothing was sent.",
            1)
        {
            Details = new JsonObject
            {
                ["option"] = "--address",
                ["address"] = text,
                ["expected"] = "(flowID,eventBlockID,eventID)",
                ["example"] = "(0,106,120108)",
            },
        };

    private static JsonArray Strings(IEnumerable<string> values)
    {
        var array = new JsonArray();

        foreach (var value in values)
        {
            JsonArr.AddText(array, value);
        }

        return array;
    }
}
