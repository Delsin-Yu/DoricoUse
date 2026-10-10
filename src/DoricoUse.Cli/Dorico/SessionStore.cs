using System.Text.Json;
using System.Text.Json.Nodes;

namespace DoricoUse.Cli.Dorico;

/// <summary>
/// What outlives a single invocation: where Dorico listens, the client name we authorize as, and the session
/// token Dorico handed out when the user first allowed this client.
/// </summary>
/// <remarks>
/// Whether the token is one of the things that outlives the invocation is decided at compile time, by
/// <see cref="PersistsToken"/>; the address and the client name are not sensitive and are always kept.
/// </remarks>
internal sealed class SessionStore
{
    /// <summary>Per-user state directory: %LOCALAPPDATA%\dorico-use.</summary>
    public static string StateDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "dorico-use");

    public static string FilePath => Path.Combine(StateDirectory, "session.json");

    /// <summary>False only in a NEVER_PERSIST_SESSION build, where the token lives in the host's memory instead.</summary>
    /// <remarks>
    /// Keeping the token is what makes every run after the first cost a millisecond instead of an authorization
    /// dialog, so it is the default and the user is told about it; <c>-p:NeverPersistSession=true</c> is for anyone
    /// who would rather click Allow again than have the token in a file. Everything that reports on the token reads
    /// this property, so the two builds cannot disagree about which one is running.
    /// </remarks>
    public static bool PersistsToken =>
#if NEVER_PERSIST_SESSION
        false;
#else
        true;
#endif

    public string ClientName { get; set; } = DoricoProtocol.DefaultClientName;

    public string Address { get; set; } = DoricoProtocol.DefaultAddress;

    public string? SessionToken { get; set; }

    public bool HasToken => !string.IsNullOrWhiteSpace(SessionToken);

    /// <summary>
    /// True when the file named by <see cref="FilePath"/> held a session token that this build deliberately did
    /// not use. Only a NEVER_PERSIST_SESSION build reaches it.
    /// </summary>
    /// <remarks>
    /// That build authorizes with no token from disk, so nothing else would ever mention a leftover one: this flag
    /// is what lets a payload say "a token from an earlier build is on disk at &lt;path&gt;; this build does not use
    /// it" instead of leaving the user to wonder why Dorico asks again. It describes the file as
    /// <see cref="Load"/> found it.
    /// </remarks>
    public bool StoredTokenIgnored { get; private set; }

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

            var storedToken = NodeRead.Str(json, "sessionToken");

            if (PersistsToken)
            {
                store.SessionToken = storedToken;
            }
            else
            {
                // This build never authorizes with a token from disk. Saying that it found one - and not just
                // dropping it - is the difference between a silence the user cannot explain and a note naming the
                // file they can delete.
                store.StoredTokenIgnored = !string.IsNullOrWhiteSpace(storedToken);
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // A store we cannot read only costs one extra authorization dialog.
        }

        return store;
    }

    /// <summary>
    /// Writes what a later run needs: the address and the client name always, the session token only in a build
    /// that keeps one.
    /// </summary>
    /// <remarks>
    /// A NEVER_PERSIST_SESSION build writes the file without the token, so a token an earlier build left in it is
    /// erased by the first handshake of such a build. The payload that mentions the leftover reports what
    /// <see cref="Load"/> found, which is the file the user is being told they can delete.
    /// </remarks>
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

            if (PersistsToken && !string.IsNullOrWhiteSpace(SessionToken))
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
