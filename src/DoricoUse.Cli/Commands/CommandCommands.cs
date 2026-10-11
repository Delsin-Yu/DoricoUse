using System.CommandLine;
using System.Text.Json;
using System.Text.Json.Nodes;
using DoricoUse.Cli.Dorico;
using DoricoUse.Cli.Ipc;
using DoricoUse.Cli.Ui;

namespace DoricoUse.Cli.Commands;

/// <summary>
/// Dorico's own command surface: the 1090 commands it accepts, their parameters, and the commands themselves.
/// </summary>
internal static class CommandCommands
{
    public static Command Build()
    {
        var command = new Command("command", "Send Dorico commands and search its command catalogue.");
        command.Subcommands.Add(BuildSend());
        command.Subcommands.Add(BuildList());
        command.Subcommands.Add(BuildInfo());
        return command;
    }

    private static Command BuildSend()
    {
        var name = new Option<string>("--name")
        {
            Description = "Dorico command name, e.g. NoteInput.Enter or File.Open.",
            Required = true,
        };

        var parameters = new Option<string[]>("--param")
        {
            Description = "A command parameter as Key=Value. Repeatable: --param File=\"D:\\score.dorico\". A "
                + "value that looks like a Dorico enum spelling (kQuaver, kWriteMode) is checked against the "
                + "vocabulary extracted from Dorico's binary and refused when it is not in it, because Dorico "
                + "answers kOK to an unknown enum and silently substitutes another; see `version` and the "
                + "vocabulary field of any payload that checked something.",
        };

        var timeoutOption = CommandRoot.TimeoutOption();
        var waitOption = new Option<int>("--wait-ms")
        {
            Description = "Wait this long before reading the status back; opening a project takes seconds.",
            DefaultValueFactory = _ => 0,
        };

        var noVerify = new Option<bool>("--no-verify")
        {
            Description = "Do not read the status back after the command.",
        };

        var allowUnknownEnum = new Option<bool>("--allow-unknown-enum")
        {
            Description = "Send an enum-looking value the vocabulary does not hold instead of refusing it. The "
                + "vocabulary is a string scan of one build, so it can miss a spelling that is real.",
        };

        var dryRun = new Option<bool>("--dry-run")
        {
            Description = "Check the name and the parameters and print the composed command line, sending "
                + "nothing: no host is started and Dorico is not contacted.",
        };

        var allowModalDialog = new Option<bool>("--allow-modal-dialog")
        {
            Description = "Send even while a blocking dialog is up in Dorico. Without it the command is refused "
                + "with ModalDialogUp and nothing is sent, because a command sent in that state is queued rather "
                + "than run, and a command that raises a dialog in that state has crashed Dorico here twice. Pass "
                + "it only when you mean the command to go out with a modal up.",
        };

        var expectScore = CommandRoot.ExpectScoreOption();

        var command = new Command(
            "send",
            "Send one Dorico command. The status is read back afterwards, because Dorico answers kOK even for "
            + "commands that then fail. Enum-looking parameter values are checked against the vocabulary "
            + "extracted from Dorico's binary, when one has been extracted on this machine; without one they are "
            + "sent unchecked, and the payload says so. While a blocking dialog is up the send is refused with "
            + "ModalDialogUp unless --allow-modal-dialog is passed.")
        {
            name,
            parameters,
            timeoutOption,
            waitOption,
            noVerify,
            allowUnknownEnum,
            dryRun,
            allowModalDialog,
            expectScore,
        };

        command.SetAction(parse => CommandRoot.RunAsync("command send", async () =>
        {
            var waitMs = Math.Clamp(parse.GetValue(waitOption), 0, 120_000);
            var timeout = CommandRoot.Timeout(parse, timeoutOption);
            var values = ParseParameters(parse.GetValue(parameters));
            var commandName = parse.GetValue(name) ?? string.Empty;

            // The name the user typed, checked before anything is composed and before the dry-run branch, so a
            // Script.* command is refused with no host started and no Dorico running - and --dry-run is not a
            // way around it either.
            UnsafeSurface.RefuseCommand(commandName);

            // Before anything else touches the host: an unknown enum value would be accepted with kOK and
            // silently turned into another value inside Dorico, which is the failure this exists to prevent.
            var lookedAt = CheckValues(values, parse.GetValue(allowUnknownEnum));

            if (parse.GetValue(dryRun))
            {
                return DryRun(commandName, values, lookedAt);
            }

            // The guard, and the reason it is here rather than in the host: a refusal must send nothing at all, so
            // it has to happen before the request that would post the command. It comes after --dry-run on purpose
            // - a dry run sends nothing, starts no host and must stay usable while Dorico is stuck behind a dialog,
            // which is exactly when a caller wants to check a command line. UnsafeSurface and the vocabulary checks
            // run first for the same reason: a local usage error is answered without touching Dorico.
            var modal = DialogSurface.ModalGuard(parse.GetValue(allowModalDialog));

            if (modal is not null)
            {
                throw new CliException(DialogModel.ModalDialogUpCode, modal.Message, DialogModel.RefusalExitCode)
                {
                    Details = modal.Data,
                };
            }

            var args = new JsonObject
            {
                ["name"] = commandName,
                ["waitMs"] = waitMs,
                ["verify"] = !parse.GetValue(noVerify),
            };

            if (values.Count > 0)
            {
                args["parameters"] = values;
            }

            CommandRoot.AddExpectScore(args, parse.GetValue(expectScore));

            // The host may exchange twice with Dorico (command, then status) plus the requested wait.
            var budget = (timeout * 2) + TimeSpan.FromMilliseconds(waitMs);

            var payload = await HostClient
                .CallAsync(HostProtocol.OpCommand, args, budget, CancellationToken.None)
                .ConfigureAwait(false);

            if (lookedAt.Count > 0)
            {
                payload["vocabulary"] = EnumVocabulary.Describe(lookedAt);
            }

            return payload;
        }));

        return command;
    }

    /// <summary>
    /// Puts every enum-looking value of this call through the vocabulary: a value it does not hold is a usage
    /// error (exit 1) with the nearest spellings, and a value it holds — or one that cannot be checked at all,
    /// because no vocabulary was extracted on this machine — is returned with its verdict for the payload.
    /// </summary>
    private static List<(string Key, string Value, VocabularyVerdict Verdict)> CheckValues(
        JsonObject values,
        bool allowUnknown)
    {
        var lookedAt = new List<(string Key, string Value, VocabularyVerdict Verdict)>();

        foreach (var (key, node) in values)
        {
            if (node is not JsonValue json || !json.TryGetValue<string>(out var value)
                || !EnumVocabulary.IsSpelling(value))
            {
                continue;
            }

            EnumVocabulary.Refuse("--param", key, value, allowUnknown);
            lookedAt.Add((key, value, EnumVocabulary.Check(value)));
        }

        return lookedAt;
    }

    /// <summary>
    /// The plan, sent nowhere: the command line as Dorico would receive it, and what the vocabulary had to say
    /// about its values. Because nothing is sent, no host is started and Dorico is not contacted — this is also
    /// the way to check a value on a machine where Dorico must not be disturbed.
    /// </summary>
    private static JsonObject DryRun(
        string commandName,
        JsonObject values,
        List<(string Key, string Value, VocabularyVerdict Verdict)> lookedAt)
    {
        var pairs = new List<string>();

        foreach (var (key, node) in values)
        {
            pairs.Add($"{key}={NodeRead.Text(node)}");
        }

        var plan = new JsonObject
        {
            ["dryRun"] = true,
            ["name"] = commandName,
            ["parameters"] = values.DeepClone(),
            ["command"] = pairs.Count == 0 ? commandName : $"{commandName}?{string.Join(",", pairs)}",
            ["vocabulary"] = EnumVocabulary.Describe(lookedAt),
            ["note"] = "Nothing was sent: no host was started and Dorico was not contacted. Drop --dry-run to "
                + "send it, and read `status` afterwards — Dorico answers kOK even to a command that then fails.",
        };

        return new JsonObject { ["plan"] = plan };
    }

    private static Command BuildList()
    {
        var family = new Option<string?>("--family")
        {
            Description = "Only commands whose name starts with this family, e.g. NoteInput.",
        };

        var filter = new Option<string?>("--filter")
        {
            Description = "Match the command name or its display name: a substring, or a glob with * and ?.",
        };

        var limit = new Option<int>("--limit")
        {
            Description = "How many matches to return.",
            DefaultValueFactory = _ => 100,
        };

        var timeout = CommandRoot.TimeoutOption();

        var command = new Command("list", "Search the command catalogue Dorico reports.")
        {
            family,
            filter,
            limit,
            timeout,
        };

        command.SetAction(parse => CommandRoot.RunAsync("command list", async () =>
        {
            var args = new JsonObject
            {
                ["limit"] = Math.Clamp(parse.GetValue(limit), 1, 5000),
            };

            AddIfSet(args, "family", parse.GetValue(family));
            AddIfSet(args, "filter", parse.GetValue(filter));

            return await HostClient.CallAsync(
                HostProtocol.OpCommands,
                args,
                CommandRoot.Timeout(parse, timeout),
                CancellationToken.None);
        }));

        return command;
    }

    private static Command BuildInfo()
    {
        var name = new Option<string>("--name")
        {
            Description = "Exact command name, e.g. NoteInput.Pitch.",
            Required = true,
        };

        var timeout = CommandRoot.TimeoutOption();

        var command = new Command("info", "One catalogue entry: display name and the parameters it accepts.")
        {
            name,
            timeout,
        };

        command.SetAction(parse => CommandRoot.RunAsync("command info", async () =>
        {
            var commandName = parse.GetValue(name);
            var args = new JsonObject
            {
                ["name"] = commandName,
                ["limit"] = 1,
            };

            var payload = await HostClient.CallAsync(
                HostProtocol.OpCommands,
                args,
                CommandRoot.Timeout(parse, timeout),
                CancellationToken.None);

            var matches = NodeRead.Arr(payload, "matches");

            if (matches is null || matches.Count == 0)
            {
                throw new CliException("CommandNotFound", $"Dorico has no command named '{commandName}'.", 2);
            }

            return new JsonObject
            {
                ["command"] = matches[0]?.DeepClone(),
                ["catalogue"] = NodeRead.Obj(payload, "catalogue")?.DeepClone(),
            };
        }));

        return command;
    }

    /// <summary>Turns repeated --param Key=Value into a JSON object.</summary>
    private static JsonObject ParseParameters(string[]? values)
    {
        var parameters = new JsonObject();

        foreach (var value in values ?? [])
        {
            var separator = value.IndexOf('=', StringComparison.Ordinal);

            if (separator <= 0)
            {
                throw new CliException("InvalidArgument", $"--param expects Key=Value, but got '{value}'.", 1);
            }

            parameters[value[..separator]] = value[(separator + 1)..];
        }

        return parameters;
    }

    private static void AddIfSet(JsonObject args, string key, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            args[key] = value;
        }
    }
}
