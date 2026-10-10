using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DoricoUse.Cli.Dorico;
using DoricoUse.Cli.Ipc;
using DoricoUse.Cli.Json;
using DoricoUse.Cli.Views;

namespace DoricoUse.Cli.Host;

internal sealed partial class HostServer
{
    private Task<JsonObject> FlowsAsync(JsonObject args, TimeSpan timeout)
        => FetchAsync(
            "flows",
            DoricoProtocol.RidFlowsList,
            DoricoProtocol.GetFlows(),
            response => WithRaw(ScoreView.Flows(response), response, args),
            timeout);

    private Task<JsonObject> LayoutsAsync(JsonObject args, TimeSpan timeout)
    {
        var kind = NodeRead.Str(args, "kind");

        return FetchAsync(
            "layouts",
            DoricoProtocol.RidLayoutsList,
            DoricoProtocol.GetLayouts(),
            response => WithRaw(ScoreView.Layouts(response, kind), response, args),
            timeout);
    }

    /// <summary>
    /// Adds the unmodified Dorico answer when the caller asked for it (<c>--raw</c>). Every other read has this
    /// escape hatch, and these three do too: a field this build does not model yet stays reachable instead of
    /// being invisible until a release adds it.
    /// </summary>
    private static JsonObject WithRaw(JsonObject payload, JsonObject response, JsonObject args)
    {
        if (NodeRead.Bool(args, "raw") == true)
        {
            payload["raw"] = response.DeepClone();
        }

        return payload;
    }

    /// <summary>
    /// The players of the open document.
    /// </summary>
    /// <remarks>
    /// Not a protocol fetch like the two above it, because there is no request to fetch from: the Remote Control API
    /// publishes no players request, and <c>getflows</c> answers a flow id and a name. The CLI reads them through
    /// the scripting layer instead — the same route the live read takes — which is why there is no host operation
    /// here: a host operation would have to send a command through the host's own gate and deadlock on it.
    /// </remarks>
    private Task<JsonObject> PropertiesAsync(TimeSpan timeout)
        => FetchAsync(
            "properties",
            DoricoProtocol.RidPropertiesList,
            DoricoProtocol.GetProperties(),
            ScoreView.Properties,
            timeout);

    private Task<JsonObject> TechniquesAsync(TimeSpan timeout)
        => FetchAsync(
            "playback techniques",
            DoricoProtocol.RidPlaybackTechniquesList,
            DoricoProtocol.GetPlaybackTechniques(),
            ScoreView.Techniques,
            timeout);

    private Task<JsonObject> LibraryCollectionsAsync(TimeSpan timeout)
        => FetchAsync(
            "library collections",
            DoricoProtocol.RidLibraryCollectionsList,
            DoricoProtocol.GetLibraryCollections(),
            ScoreView.LibraryCollections,
            timeout);

    private Task<JsonObject> LibraryEntitiesAsync(JsonObject args, TimeSpan timeout)
    {
        var collection = NodeRead.Str(args, "collection");

        if (string.IsNullOrWhiteSpace(collection))
        {
            throw new CliException("InvalidArgument", "Library entities need a collection; see `library collections`.", 1);
        }

        return FetchAsync(
            $"library entities {collection}",
            DoricoProtocol.RidLibraryEntitiesList,
            DoricoProtocol.GetLibraryEntities(collection),
            ScoreView.LibraryEntities,
            timeout);
    }

    private Task<JsonObject> OptionsGetAsync(JsonObject args, TimeSpan timeout)
    {
        var optionsType = ReadOptionsType(args);
        var targetId = NodeRead.Int(args, "targetId");
        var filter = NodeRead.Str(args, "filter");
        var limit = (int)Math.Clamp(NodeRead.Long(args, "limit") ?? 200, 1, 5000);

        GuardOptionsTarget(optionsType, targetId is not null ? [targetId.Value] : []);

        return FetchAsync(
            "options get",
            DoricoProtocol.RidOptionsList,
            DoricoProtocol.GetOptions(optionsType, targetId),
            response => ScoreView.Options(response, filter, limit),
            timeout);
    }

    private async Task<JsonObject> OptionsSetAsync(JsonObject args, TimeSpan timeout)
    {
        var optionsType = ReadOptionsType(args);
        var values = ReadParameterMap(NodeRead.Obj(args, "values"));
        var targets = ReadIntArray(args, "targets");
        var verify = NodeRead.Bool(args, "verify") ?? true;

        if (values.Count == 0)
        {
            throw new CliException("InvalidArgument", "options set needs at least one --value path=value.", 1);
        }

        GuardOptionsTarget(optionsType, targets);

        return await _keeper.ExecuteAsync("options set", async client =>
        {
            // Setting an option changes the document Dorico has active, so it takes the same document guard.
            await EnsureExpectedScoreAsync(client, args, timeout).ConfigureAwait(false);

            var response = await client.Session.RequestAsync(
                DoricoProtocol.RidResponse,
                DoricoProtocol.SetOptions(optionsType, values, targets),
                timeout,
                _cts.Token).ConfigureAwait(false);

            if (NodeRead.IsError(response))
            {
                throw new CliException(
                    "DoricoError",
                    $"Dorico refused setoptions: {NodeRead.Str(response, "detail") ?? "no detail"}",
                    2);
            }

            var payload = new JsonObject
            {
                ["optionsType"] = optionsType,
                ["ack"] = NodeRead.Str(response, "code") ?? "unknown",
                ["set"] = ParametersJson(values),
            };

            if (!verify)
            {
                return payload;
            }

            var current = await client.Session.RequestAsync(
                DoricoProtocol.RidOptionsList,
                DoricoProtocol.GetOptions(optionsType, targets.Count > 0 ? targets[0] : null),
                timeout,
                _cts.Token).ConfigureAwait(false);

            payload["verified"] = VerifyOptions(current, values);

            return payload;
        }, timeout * 2, _cts.Token).ConfigureAwait(false);
    }

    /// <summary>One protocol exchange projected into agent-friendly JSON.</summary>
    private async Task<JsonObject> FetchAsync(
        string what,
        string expectedId,
        string message,
        Func<JsonObject, JsonObject> project,
        TimeSpan timeout)
        => await _keeper.ExecuteAsync(what, async client =>
        {
            var started = Stopwatch.GetTimestamp();
            var response = await client.Session.RequestAsync(expectedId, message, timeout, _cts.Token)
                .ConfigureAwait(false);

            if (NodeRead.IsError(response))
            {
                throw new CliException(
                    "DoricoError",
                    $"Dorico refused {what}: {NodeRead.Str(response, "detail") ?? "no detail"}",
                    2);
            }

            var payload = project(response);
            payload["fetchMs"] = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;

            return payload;
        }, timeout, _cts.Token).ConfigureAwait(false);

    private static JsonObject VerifyOptions(JsonObject response, List<KeyValuePair<string, string>> values)
    {
        var options = NodeRead.Arr(response, "options") ?? [];
        var verification = new JsonObject();

        foreach (var (path, wanted) in values)
        {
            string? actual = null;
            var found = false;

            foreach (var option in options)
            {
                if (NodeRead.Str(option, "path") == path)
                {
                    actual = NodeRead.Str(option, "currentValue");
                    found = true;
                    break;
                }
            }

            verification[path] = new JsonObject
            {
                ["wanted"] = wanted,
                ["actual"] = actual,
                ["found"] = found,
                ["ok"] = found && actual == wanted,
            };
        }

        return verification;
    }

    private static string ReadOptionsType(JsonObject args) => NodeRead.Str(args, "optionsType") switch
    {
        DoricoProtocol.OptionsLayout => DoricoProtocol.OptionsLayout,
        DoricoProtocol.OptionsNotation => DoricoProtocol.OptionsNotation,
        _ => DoricoProtocol.OptionsEngraving,
    };

    private static void GuardOptionsTarget(string optionsType, List<int> targets)
    {
        if (optionsType == DoricoProtocol.OptionsEngraving)
        {
            return;
        }

        var name = optionsType == DoricoProtocol.OptionsLayout ? "--layout" : "--flow";

        if (targets.Count == 0)
        {
            throw new CliException(
                "InvalidArgument",
                $"{optionsType} options are per layout or flow: pass {name} with an ID (`project layouts` lists them).",
                1);
        }
    }

    private static List<int> ReadIntArray(JsonObject args, string key)
    {
        var values = new List<int>();
        var array = NodeRead.Arr(args, key);

        if (array is null)
        {
            return values;
        }

        foreach (var item in array)
        {
            if (item is JsonValue value && value.TryGetValue<int>(out var number))
            {
                values.Add(number);
            }
        }

        return values;
    }

}