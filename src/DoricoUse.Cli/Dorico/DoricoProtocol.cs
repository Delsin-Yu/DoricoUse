using System.Text;
using System.Text.Json.Nodes;
using DoricoUse.Cli.Json;

namespace DoricoUse.Cli.Dorico;

/// <summary>
/// Wire format of Dorico's Remote Control API: a WebSocket carrying one JSON object per message.
/// Each request is identified by its "message" field; Dorico answers in request order and may also send
/// unsolicited messages (status, selectionchanged, ...).
/// </summary>
/// <remarks>
/// Shapes follow Dorico 6 as observed through Dorico.Net 0.3.0-beta. Anything not verified against the live
/// application is deliberately not here yet.
/// </remarks>
internal static class DoricoProtocol
{
    public const string HandshakeVersion = "1.0";

    /// <summary>
    /// The Dorico build everything in this repository was measured against. Reported by the <c>version</c> command
    /// rather than encoded in the tool's own SemVer number, because the tool's version and Dorico compatibility are
    /// two different facts.
    /// </summary>
    public const string TestedAgainst = "6.2.30";

    public const string DefaultAddress = "ws://127.0.0.1:4560";

    public const string DefaultClientName = "dorico-use";

    // Request ids ("message" field of a request).
    public const string IdConnect = "connect";
    public const string IdAcceptSessionToken = "acceptsessiontoken";
    public const string IdDisconnect = "disconnect";
    public const string IdGetStatus = "getstatus";
    public const string IdGetAppInfo = "getappinfo";
    public const string IdGetCommands = "getcommands";
    public const string IdCommand = "command";
    public const string IdGetFlows = "getflows";
    public const string IdGetLayouts = "getlayouts";
    public const string IdGetOptions = "getoptions";
    public const string IdSetOptions = "setoptions";
    public const string IdGetProperties = "getproperties";
    public const string IdGetPlaybackTechniques = "getplaybacktechniques";
    public const string IdGetLibraryCollections = "getlibrarycollections";

    /// <summary>The library entries request is called "entities", not "entries".</summary>
    public const string IdGetLibraryEntities = "getlibraryentities";

    // Response ids ("message" field of a response).
    public const string RidSessionToken = "sessiontoken";
    public const string RidStatus = "status";
    public const string RidVersion = "version";
    public const string RidCommandList = "commandlist";
    public const string RidResponse = "response";
    public const string RidFlowsList = "flowslist";
    public const string RidLayoutsList = "layoutslist";
    public const string RidOptionsList = "optionslist";
    public const string RidPropertiesList = "propertieslist";
    public const string RidPlaybackTechniquesList = "playbacktechniqueslist";
    public const string RidLibraryCollectionsList = "librarycollectionslist";
    public const string RidLibraryEntitiesList = "libraryentitieslist";
    public const string RidSelectionChanged = "selectionchanged";
    public const string RidOptionsChanged = "optionschanged";
    public const string RidLibraryChanged = "librarychanged";
    public const string RidPlaybackConfigurationChanged = "playbackconfigurationchanged";

    /// <summary>A message this client does not know, or one that could not be parsed.</summary>
    public const string Unknown = "unknown";

    // Response codes.
    public const string CodeError = "kError";
    public const string CodeConnected = "kConnected";

    /// <summary>First-ever connection: Dorico asks the user to authorize this client and replies with a session token.</summary>
    public static string Connect(string clientName)
        => Object(("message", IdConnect), ("clientName", clientName), ("handshakeVersion", HandshakeVersion));

    /// <summary>Later connections: a stored session token authorizes the client without a dialog.</summary>
    public static string ConnectWithToken(string clientName, string sessionToken)
        => Object(
            ("message", IdConnect),
            ("clientName", clientName),
            ("handshakeVersion", HandshakeVersion),
            ("sessionToken", sessionToken));

    public static string AcceptSessionToken(string sessionToken)
        => Object(("message", IdAcceptSessionToken), ("sessionToken", sessionToken));

    public static string Disconnect()
        => Object(("message", IdDisconnect));

    public static string GetStatus()
        => Object(("message", IdGetStatus));

    public static string GetAppInfo()
        => Object(("message", IdGetAppInfo), ("info", "version"));

    public static string GetCommands()
        => Object(("message", IdGetCommands));

    /// <summary>
    /// A Dorico command: "Name?Key=Value,Key2=Value2". The command catalogue is fetched with getcommands;
    /// application.log echoes the commands and parameter values behind any Dorico action.
    /// </summary>
    public static string Command(string name, IReadOnlyList<KeyValuePair<string, string>>? parameters = null)
    {
        var text = new StringBuilder(name);

        if (parameters is { Count: > 0 })
        {
            text.Append('?');
            text.Append(string.Join(',', parameters.Select(parameter => $"{parameter.Key}={parameter.Value}")));
        }

        return Object(("message", IdCommand), ("command", text.ToString()));
    }

    // Option kinds, as Dorico spells them.
    public const string OptionsEngraving = "kEngraving";
    public const string OptionsLayout = "kLayout";
    public const string OptionsNotation = "kNotation";

    public static string GetFlows() => Object(("message", IdGetFlows));

    public static string GetLayouts() => Object(("message", IdGetLayouts));

    public static string GetProperties() => Object(("message", IdGetProperties));

    public static string GetPlaybackTechniques() => Object(("message", IdGetPlaybackTechniques));

    public static string GetLibraryCollections() => Object(("message", IdGetLibraryCollections));

    public static string GetLibraryEntities(string collection)
        => Object(("message", IdGetLibraryEntities), ("collection", collection));

    /// <summary>Engraving options are global; layout options need a layout ID and notation options a flow ID.</summary>
    public static string GetOptions(string optionsType, int? targetId)
    {
        var message = new JsonObject
        {
            ["message"] = IdGetOptions,
            ["optionsType"] = optionsType,
        };

        if (targetId is not null)
        {
            message[IdKey(optionsType, plural: false)] = targetId.Value;
        }

        return message.ToJsonString();
    }

    /// <param name="values">Paths and values, as "path" and "value" per option.</param>
    /// <param name="targetIds">Layout IDs for kLayout, flow IDs for kNotation, nothing for kEngraving.</param>
    public static string SetOptions(
        string optionsType,
        IReadOnlyList<KeyValuePair<string, string>> values,
        IReadOnlyList<int>? targetIds = null)
    {
        var message = new JsonObject
        {
            ["message"] = IdSetOptions,
            ["optionsType"] = optionsType,
        };

        var optionValues = new JsonArray();

        foreach (var (path, value) in values)
        {
            JsonArr.AddNode(optionValues, new JsonObject
            {
                ["path"] = path,
                ["value"] = value,
            });
        }

        message["optionvalues"] = optionValues;

        if (targetIds is { Count: > 0 })
        {
            var ids = new JsonArray();

            foreach (var id in targetIds)
            {
                JsonArr.AddNumber(ids, id);
            }

            // Measured on Dorico 6.2.30: the protocol's setoptions ignores this key, and the singular key the
            // matching getoptions uses as well, and writes to the layout that is current. The executed command in
            // application.log carries LayoutIDArray=<current layout> whatever id was asked for. So a layout option
            // write has to be aimed by making the layout current first (Window.SwitchToLayout); see the README.
            message[IdKey(optionsType, plural: true)] = ids;
        }

        return message.ToJsonString();
    }

    private static string IdKey(string optionsType, bool plural) => optionsType switch
    {
        OptionsLayout => plural ? "layoutIDs" : "layoutID",
        OptionsNotation => plural ? "flowIDs" : "flowID",
        _ => plural ? "ids" : "id",
    };

    private static string Object(params (string Key, string Value)[] pairs)
    {
        var obj = new JsonObject();

        foreach (var (key, value) in pairs)
        {
            obj[key] = value;
        }

        return obj.ToJsonString();
    }
}
