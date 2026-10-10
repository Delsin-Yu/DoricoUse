using System.Text;
using System.Text.Json.Nodes;

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

    // Response ids ("message" field of a response).
    public const string RidSessionToken = "sessiontoken";
    public const string RidStatus = "status";
    public const string RidVersion = "version";
    public const string RidCommandList = "commandlist";
    public const string RidResponse = "response";
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
