using System.Text.Json;
using System.Text.Json.Nodes;

namespace DoricoUse.Cli.Dorico;

/// <summary>
/// What outlives a single invocation: where Dorico listens, the client name we authorize as, and the session
/// token Dorico handed out when the user first allowed this client.
/// </summary>
internal sealed class SessionStore
{
    /// <summary>Per-user state directory: %LOCALAPPDATA%\dorico-use.</summary>
    public static string StateDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "dorico-use");

    public static string FilePath => Path.Combine(StateDirectory, "session.json");

    public string ClientName { get; set; } = DoricoProtocol.DefaultClientName;

    public string Address { get; set; } = DoricoProtocol.DefaultAddress;

    public string? SessionToken { get; set; }

    public bool HasToken => !string.IsNullOrWhiteSpace(SessionToken);

    public static SessionStore Load()
    {
        var store = new SessionStore();

        try
        {
            if (!File.Exists(FilePath))
            {
                return store;
            }

            if (JsonNode.Parse(File.ReadAllText(FilePath)) is not JsonObject json)
            {
                return store;
            }

            store.ClientName = NodeRead.Str(json, "clientName") ?? store.ClientName;
            store.Address = NodeRead.Str(json, "address") ?? store.Address;
            store.SessionToken = NodeRead.Str(json, "sessionToken");
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // A store we cannot read only costs one extra authorization dialog.
        }

        return store;
    }

    public void Save()
    {
        try
        {
            System.IO.Directory.CreateDirectory(StateDirectory);

            var json = new JsonObject
            {
                ["clientName"] = ClientName,
                ["address"] = Address,
            };

            if (!string.IsNullOrWhiteSpace(SessionToken))
            {
                json["sessionToken"] = SessionToken;
            }

            File.WriteAllText(FilePath, json.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Losing the store is not worth failing a command over.
        }
    }

    public void ClearToken()
    {
        SessionToken = null;
        Save();
    }
}
