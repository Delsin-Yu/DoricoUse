using System.CommandLine;
using System.Text.Json.Nodes;
using DoricoUse.Cli.Dorico;
using DoricoUse.Cli.Ipc;
using DoricoUse.Cli.Json;
using DoricoUse.Cli.Ui;
using DoricoUse.Cli.Views;

namespace DoricoUse.Cli.Commands;

/// <summary>
/// One field of one event: setting it, and finding the address that says which event.
/// </summary>
/// <remarks>
/// <para>
/// This is the command a dogfood run asked for. Setting one event's duration by hand took four steps and two of
/// them were where it went wrong: the block an event lives in had to be reverse-looked-up out of
/// <c>file read --tree block --depth 3</c>, and the edit had to carry all four of its parameters — measured
/// 2026-10-13, <c>MouseEdit.ChangeEventPositionOrDuration</c> with only <c>NewDuration</c> answered <c>kOK</c>,
/// raised Dorico's 错误发生 box, changed nothing, and made every later command look like it ran while it sat on a
/// stalled queue (the run's own batch corrupted two trills with the other route, whose step is not constant: 0.5
/// crotchets on a tempo event, 1/6 on one trill).
/// </para>
/// <para>
/// So the composition is <see cref="EventEditRequest"/>'s job and never the caller's: this file chooses when to
/// send, what to report and what to read back, and it can only send what that class composes. A wrong <c>--field</c>,
/// a missing <c>--event-type</c> or an address that is not three numbers is refused here, before the host is
/// contacted.
/// </para>
/// </remarks>
internal static class EventCommands
{
    public static Command Build()
    {
        var events = new Command("event", "Set one field of one event, and list the addresses those commands need.");
        events.Subcommands.Add(BuildSet());
        events.Subcommands.Add(BuildList());
        return events;
    }

    private static Command BuildSet()
    {
        var address = new Option<string?>("--address")
        {
            Description = "The event's address, (flowID,eventBlockID,eventID), e.g. \"(0,106,120108)\" — the triple "
                + "`event list` prints, and the one the edit carries as its Address. Quote it in a shell: parentheses "
                + "are syntax to bash. The comma-escaped spelling Dorico's log shows, (0\\,106\\,120108), is accepted "
                + "too.",
        };

        var eventType = new Option<string?>("--event-type")
        {
            Description = "The Dorico event type the address points at: kTrillEvent, kGradualTempoChangeEvent, "
                + "kNoteEvent. It is the edit's EventType parameter, and the one parameter that tells Dorico what "
                + "kind of event it is about — measured, the same command without it raises Dorico's 错误发生 box.",
        };

        var field = new Option<string?>("--field")
        {
            Description = $"What to set: {string.Join(" or ", EventEditRequest.Fields)}. {EventEditRequest.DurationField} "
                + $"sends {EventEditRequest.DurationParameter}, {EventEditRequest.PositionField} sends "
                + $"{EventEditRequest.PositionParameter}. Those are the two fields this build measured on this "
                + "command; anything else is refused with UnknownField rather than sent.",
        };

        var value = new Option<string?>("--value")
        {
            Description = "The new value in crotchets, as a number or a fraction (4, 11/2). Measured: the file states "
                + "an event's duration and position as exact rationals in crotchets, so a triplet is a fraction and a "
                + "4/4 bar is 4 — and the run's own sets sent NewDuration=9/2 and read 9/2 back out of the saved file.",
        };

        var noSelect = new Option<bool>("--no-select")
        {
            Description = "Send the edit alone. By default the event is selected first with EventEdit.Select, the "
                + "same triple, which is what the run's working recipe did; the payload says which of the two "
                + "happened either way.",
        };

        var verify = new Option<string?>("--verify")
        {
            Description = "A saved copy of the project to decode and assert the value on, e.g. the file a "
                + "File.SaveAs wrote after this edit. The payload then carries `verified` — wanted, actual, found, ok "
                + "— the shape `options set` prints. Without it nothing is read back, and `file read --tree "
                + "<RecordName> --tree-limit all` after a save is the caller's proof.",
        };

        var record = new Option<string?>("--record")
        {
            Description = "The name the project file uses for this event kind (TrillEventDefinition), for --verify. "
                + "It defaults to the one --event-type implies, so it is only needed when the two spellings differ.",
        };

        var waitForCompletion = new Option<bool>("--wait-for-completion")
        {
            Description = "Wait until Dorico's log shows the edit's own `notifyPostCommandExecute` line, and report "
                + "how long that took in `completion`. Measured, a command's cost is not guessable (4.4 s to 54 s in "
                + "one session), so the log is the witness and a fixed sleep is a guess. The wait is for the edit: "
                + "the select is over in milliseconds.",
        };

        var completionTimeout = new Option<int>("--completion-timeout-ms")
        {
            Description = "How long --wait-for-completion waits at most, in milliseconds.",
            DefaultValueFactory = _ => LogQueue.DefaultCompletionTimeoutMs,
        };

        var allowModalDialog = new Option<bool>("--allow-modal-dialog")
        {
            Description = "Send even while a blocking dialog is up in Dorico. Without it this is refused with "
                + "ModalDialogUp and nothing is sent — a command raised by a command that is already behind a modal "
                + "has crashed Dorico here twice, and the 错误发生 box this edit used to raise is exactly that case.",
        };

        var timeout = CommandRoot.TimeoutOption();
        var expectScore = CommandRoot.ExpectScoreOption();

        var command = new Command(
            "set",
            "Set one field of one event, by address. The four parameters the edit needs are composed here, never by "
            + "the caller; an incomplete address, an unknown --field or a missing --event-type is refused before "
            + "anything is sent.")
        {
            address,
            eventType,
            field,
            value,
            noSelect,
            verify,
            record,
            waitForCompletion,
            completionTimeout,
            allowModalDialog,
            timeout,
            expectScore,
        };

        command.SetAction(parse => CommandRoot.RunAsync("event set", async () =>
        {
            // Everything below this line is refused locally or sent as the one measured shape. The address, the
            // event type, the field and the value are validated by the composer itself, so there is no path through
            // this command that can compose three of the four parameters: `Compose` is the only way to build them.
            var parsed = EventEditRequest.ParseAddress(parse.GetValue(address));
            var wantedType = EventEditRequest.RequireEventType(parse.GetValue(eventType));
            var wantedField = (parse.GetValue(field) ?? string.Empty).Trim();
            var parameter = EventEditRequest.RequireField(parse.GetValue(field));
            var wantedValue = EventEditRequest.RequireValue(parse.GetValue(value));
            var wantsSelect = !parse.GetValue(noSelect);
            var steps = EventEditRequest.Compose(parsed, wantedType, wantedField, wantedValue, wantsSelect);

            // The same guard `command send` has, for the same measured reason, and before the host is contacted: a
            // refusal must send nothing at all.
            var modal = DialogSurface.ModalGuard(parse.GetValue(allowModalDialog));

            if (modal is not null)
            {
                throw new CliException(DialogModel.ModalDialogUpCode, modal.Message, DialogModel.RefusalExitCode)
                {
                    Details = modal.Data,
                };
            }

            // Every command this invocation will post is recorded before it goes out, so the payload's `execution`
            // is about these two and nothing else. `blockedByDialog` is only reported beside `execution`, and the
            // whole point of this command is that "looks like it ran, did nothing" must not be readable.
            foreach (var step in steps)
            {
                DialogSurface.MarkCommand(step.Name);
            }

            var args = new JsonObject
            {
                ["steps"] = Steps(steps),
                ["verify"] = true,
            };

            CommandRoot.AddExpectScore(args, parse.GetValue(expectScore));

            var timeoutValue = CommandRoot.Timeout(parse, timeout);
            var wantsCompletion = parse.GetValue(waitForCompletion);
            var completionMs = (long)Math.Clamp(parse.GetValue(completionTimeout), 250, 600_000);

            // The host spends one exchange per step plus a status read inside its own bound of four timeouts; the
            // completion wait is added on top so this invocation is never cut off while it is deliberately waiting.
            var budget = (timeoutValue * 4) + (wantsCompletion ? TimeSpan.FromMilliseconds(completionMs) : TimeSpan.Zero);

            var result = await HostClient
                .CallAsync(HostProtocol.OpSequence, args, budget, CancellationToken.None)
                .ConfigureAwait(false);

            Acknowledge(result);

            var payload = new JsonObject
            {
                ["address"] = new JsonObject
                {
                    ["flowID"] = parsed.FlowId,
                    ["eventBlockID"] = parsed.EventBlockId,
                    ["eventID"] = parsed.EventId,
                },
                ["addressText"] = EventEditRequest.AddressText(parsed),
                ["eventType"] = wantedType,
                ["field"] = wantedField,
                ["parameter"] = parameter,
                ["value"] = wantedValue,
                ["steps"] = result["steps"]?.DeepClone(),
                ["roundTripMs"] = result["roundTripMs"]?.DeepClone(),
                ["select"] = new JsonObject
                {
                    ["sent"] = wantsSelect,
                    ["command"] = wantsSelect ? Line(steps[0]) : null,
                    ["note"] = wantsSelect
                        ? "The event was selected first, with the same triple the edit carries as its Address "
                            + "(EventEdit.Select FlowID/EventID/EventBlockID) — the run's working recipe, and what "
                            + "makes Dorico's own status show which event the next command is about. --no-select "
                            + "sends the edit alone."
                        : "--no-select: the edit was sent alone. It carries the address, so it does not need a "
                            + "selection first; the run selected first and this is the difference.",
                },
                ["edit"] = new JsonObject
                {
                    ["command"] = Line(steps[^1]),
                    ["parameters"] = Parameters(steps[^1]),
                    ["note"] = "All four parameters the edit was measured to need, composed by this command rather "
                        + "than by the caller: Address (with its commas escaped as \\,, because the protocol "
                        + "separates parameters with commas), EventType, the new value, and MouseEditWithSideEffect="
                        + EventEditRequest.SideEffectValue
                        + ". Measured, the same command with only the new value answers kOK, raises Dorico's "
                        + "错误发生 box and changes nothing.",
                },
                ["vocabulary"] = new JsonObject
                {
                    ["eventType"] = wantedType,
                    ["verdict"] = EnumVocabulary.VerdictName(EnumVocabulary.Check("EventType", wantedType, EventEditRequest.EditCommand)),
                    ["note"] = "The event type is checked for its shape (k and a capital letter) and not against a "
                        + "table: this build's vocabulary holds no event type, and a spelling-shaped value it has "
                        + "never heard of is far more likely to be newer than wrong. A 'unknown' verdict here is "
                        + "therefore not a refusal — nothing else was checked, and Dorico answers kOK to an event "
                        + "type it does not know.",
                },
            };

            if (result["stoppedAt"] is not null)
            {
                payload["stoppedAt"] = result["stoppedAt"]?.DeepClone();
                payload["stoppedAtNote"] = "Dorico refused that step, so this invocation stopped there and the steps "
                    + "after it were not sent. `execution` below says what its log shows about the queue.";
            }

            if (result["status"] is not null)
            {
                payload["status"] = result["status"]?.DeepClone();
            }

            if (wantsCompletion)
            {
                payload["completion"] = LogQueue.WaitForCompletion(
                    steps[^1].Name,
                    TimeSpan.FromMilliseconds(completionMs),
                    Thread.Sleep);
            }

            AddVerification(payload, parse.GetValue(verify), parse.GetValue(record), wantedType, parsed, wantedField, parameter, wantedValue);

            return payload;
        }));

        return command;
    }

    private static Command BuildList()
    {
        var from = new Option<string?>("--from")
        {
            Description = "Path to the .dorico project file to read. This command needs no Dorico and starts no "
                + "host: the answer is in the file.",
        };

        var record = new Option<string?>("--record")
        {
            Description = "The event kind to list, as the command spells it (kTrillEvent, kGradualTempoChangeEvent) "
                + "or as the file names it (TrillEventDefinition).",
        };

        var measure = new Option<string?>("--measure")
        {
            Description = "Only events in one measure (59) or a range (59-386). The measure is derived from the "
                + "file's metre, as every other measure this tool reports is.",
        };

        var flow = new Option<int?>("--flow")
        {
            Description = "Only events of this flow.",
        };

        var limit = new Option<string?>("--limit")
        {
            Description = "How many events to list: a count, or 'all'/'-1'. Default 200. `count` says how many "
                + "matched, whether or not they were all printed.",
            DefaultValueFactory = _ => "200",
        };

        var command = new Command(
            "list",
            "Every event of one kind in a project file, with the address `event set` needs: flowID, eventBlockID, "
            + "eventID, the position and the duration in crotchets. This is the reverse lookup the run did by hand "
            + "with `file read --tree block --depth 3`.")
        {
            from,
            record,
            measure,
            flow,
            limit,
        };

        command.SetAction(parse => CommandRoot.RunAsync("event list", () =>
        {
            var path = parse.GetValue(from);

            if (string.IsNullOrWhiteSpace(path))
            {
                throw new CliException(
                    "InvalidArgument",
                    "--from is missing. It names the project file to read (event list reads a file, so it needs "
                    + "neither Dorico nor the host) — e.g. the copy a File.SaveAs wrote.",
                    1);
            }

            var recordName = EventEditRequest.RecordName(parse.GetValue(record));
            var limits = ProjectFileView.ParseMeasures(parse.GetValue(measure));

            var file = ProjectFileReader.Open(path, collectEvents: true);
            var payload = ProjectFileView.EventList(
                file,
                recordName,
                EventEditRequest.EventTypeFor(recordName),
                parse.GetValue(flow),
                limits?.First,
                limits?.Last,
                ProjectFileView.ParseLimit(parse.GetValue(limit)));

            payload["recordAsked"] = (parse.GetValue(record) ?? string.Empty).Trim();

            return Task.FromResult(payload);
        }));

        return command;
    }

    /// <summary>
    /// The read-back <c>--verify</c> asks for: decode the copy the caller named, find the address in it, and report
    /// the value the way <c>options set</c> reports one.
    /// </summary>
    /// <remarks>
    /// It runs after the edit rather than instead of it, and the value it asserts on is the copy's, so the copy has
    /// to be one that already holds the edit — a save made after the change. That is why re-running an absolute set
    /// is safe: setting a field to what it already holds is the same set, which is the recipe the run used to
    /// confirm m16 before it touched anything else.
    /// </remarks>
    private static void AddVerification(
        JsonObject payload,
        string? path,
        string? record,
        string eventType,
        EventAddress address,
        string field,
        string parameter,
        string value)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            payload["verified"] = null;
            payload["verifyNote"] = "Nothing was read back: --verify <saved.dorico> names the copy to decode, and "
                + "without it this invocation sent its commands and read no file. A write Dorico accepted is not a "
                + "write it has saved, so the proof is `File.Save` and then `file read --tree <RecordName> "
                + "--tree-limit all` (the run measured that a save can take seconds and that a copy saved here is "
                + "the only thing on disk) — or save a copy with File.SaveAs and re-run this command with --verify "
                + "<that copy>. Re-running an absolute set is safe: it sets the same number again.";
            return;
        }

        var recordName = EventEditRequest.RecordFor(record, eventType);
        var file = ProjectFileReader.Open(path, collectEvents: true);

        ProjectFileEventAddress? found = null;
        var addressFound = false;

        foreach (var candidate in file.Score.EventAddresses)
        {
            if (candidate.FlowId != address.FlowId
                || candidate.BlockId != address.EventBlockId
                || candidate.EventId != address.EventId)
            {
                continue;
            }

            addressFound = true;

            if (string.Equals(candidate.Record, recordName, StringComparison.OrdinalIgnoreCase))
            {
                found = candidate;
                break;
            }
        }

        var actual = found is null
            ? null
            : field == EventEditRequest.DurationField ? found.Duration : found.Position;

        var verified = new JsonObject
        {
            [EventEditRequest.AddressText(address)] = EventEditRequest.Verified(
                value,
                actual,
                found is not null,
                addressFound,
                field,
                parameter,
                recordName,
                found?.Measure,
                found?.Beat),
        };

        payload["verified"] = verified;
        payload["verifyFile"] = new JsonObject
        {
            ["path"] = file.Path,
            ["bytes"] = file.Bytes,
            ["lastSavedDate"] = file.Metadata.LastSavedDate,
            ["scoreVersion"] = file.Score.FileVersion,
            ["note"] = "This file was read, not Dorico: an edit in the open document is in a file only after a save, "
                + "and `lastSavedDate` is the file's own answer to when that was.",
        };
        payload["verifyNote"] = "The value was compared as an exact rational, so a file that writes 4 and a caller "
            + "who asked for 8/2 agree: the claim is the number of crotchets, not the spelling.";
    }

    /// <summary>Records Dorico's answer to each step, which is what tells "never posted" from "posted but not run".</summary>
    private static void Acknowledge(JsonObject result)
    {
        foreach (var step in NodeRead.Arr(result, "steps") ?? [])
        {
            DialogSurface.AcknowledgeCommand(
                NodeRead.Str(step, "name"),
                NodeRead.Str(step, "ack") ?? NodeRead.Str(step, "detail"));
        }
    }

    /// <summary>The steps as the host's <c>sequence</c> operation takes them.</summary>
    private static JsonArray Steps(IReadOnlyList<EventEditStep> steps)
    {
        var array = new JsonArray();

        foreach (var step in steps)
        {
            JsonArr.AddNode(array, new JsonObject
            {
                ["name"] = step.Name,
                ["parameters"] = Parameters(step),
            });
        }

        return array;
    }

    private static JsonObject Parameters(EventEditStep step)
    {
        var parameters = new JsonObject();

        foreach (var (key, value) in step.Parameters)
        {
            parameters[key] = value;
        }

        return parameters;
    }

    /// <summary>
    /// One step as the line Dorico receives it, parameters joined with commas — the shape the protocol composes and
    /// the shape the log echoes, so a reader can compare this with what <c>application.log</c> says was posted.
    /// </summary>
    private static string Line(EventEditStep step)
        => step.Parameters.Count == 0
            ? step.Name
            : step.Name + "?" + string.Join(",", step.Parameters.Select(parameter => $"{parameter.Key}={parameter.Value}"));
}
