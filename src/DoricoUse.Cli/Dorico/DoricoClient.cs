using System.Text.Json.Nodes;

namespace DoricoUse.Cli.Dorico;

/// <param name="Store">Where the connection settings and session token live.</param>
/// <param name="Timeout">How long to wait for each protocol answer.</param>
/// <param name="ResetToken">Ignore the stored token and let Dorico ask the user to authorize again.</param>
internal sealed record DoricoConnectOptions(SessionStore Store, TimeSpan Timeout, bool ResetToken = false);

/// <summary>
/// A connected, authorized Dorico session.
/// </summary>
/// <remarks>
/// The first connection under a client name makes Dorico ask its user to allow that name; the session token
/// Dorico then returns authorizes every later connection without a dialog. Connecting is therefore cheap, and a
/// CLI invocation can open a session, do its work and close it again.
/// </remarks>
internal sealed class DoricoClient : IAsyncDisposable
{
    private DoricoClient(DoricoSession session, JsonObject handshake)
    {
        Session = session;
        Handshake = handshake;
    }

    public DoricoSession Session { get; }

    /// <summary>How this connection was authorized, in agent-friendly terms.</summary>
    public JsonObject Handshake { get; }

    public string Address => Session.Address;

    public static async Task<DoricoClient> ConnectAsync(DoricoConnectOptions options, CancellationToken cancellationToken)
    {
        var store = options.Store;
        var session = new DoricoSession();
        var handshake = new JsonObject();

        try
        {
            try
            {
                await session.OpenAsync(store.Address, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not CliException and not OperationCanceledException)
            {
                throw new CliException(
                    "NotReachable",
                    $"Could not reach Dorico at {store.Address}: {ex.Message}. Check that Dorico is running and that "
                    + "Preferences > Remote Control has its server enabled on that port (--address overrides it).",
                    2);
            }

            session.Start();

            session.SessionToken = await AuthorizeAsync(session, store, options, handshake, cancellationToken)
                .ConfigureAwait(false);

            handshake["clientName"] = store.ClientName;
            handshake["address"] = store.Address;

            // What this really says is "the token this client is authorized with is one the disk kept". A build that
            // does not persist keeps the token in memory instead (see SessionKeeper), and there store.HasToken is true
            // after the handshake too - reporting just that would claim storage on disk that does not exist.
            handshake["sessionTokenStored"] = SessionStore.PersistsToken && store.HasToken;

            return new DoricoClient(session, handshake);
        }
        catch
        {
            await session.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public Task<JsonObject> AppInfoAsync(TimeSpan timeout, CancellationToken cancellationToken)
        => Session.RequestAsync(DoricoProtocol.RidVersion, DoricoProtocol.GetAppInfo(), timeout, cancellationToken);

    public Task<JsonObject> GetStatusAsync(TimeSpan timeout, CancellationToken cancellationToken)
        => Session.RequestAsync(DoricoProtocol.RidStatus, DoricoProtocol.GetStatus(), timeout, cancellationToken);

    public Task<JsonObject> GetCommandsAsync(TimeSpan timeout, CancellationToken cancellationToken)
        => Session.RequestAsync(DoricoProtocol.RidCommandList, DoricoProtocol.GetCommands(), timeout, cancellationToken);

    /// <summary>Sends a Dorico command and returns Dorico's acknowledgement ("kOK" is a receipt, not proof of success).</summary>
    public Task<JsonObject> SendCommandAsync(
        string name,
        IReadOnlyList<KeyValuePair<string, string>>? parameters,
        TimeSpan timeout,
        CancellationToken cancellationToken)
        => Session.RequestAsync(
            DoricoProtocol.RidResponse,
            DoricoProtocol.Command(name, parameters),
            timeout,
            cancellationToken);

    public async ValueTask DisposeAsync()
    {
        try
        {
            await Session.DisconnectAsync(TimeSpan.FromSeconds(3), CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // Dorico may already be gone; disposing the socket below still cleans up.
        }

        await Session.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>Performs the handshake and returns the session token to keep using.</summary>
    private static async Task<string> AuthorizeAsync(
        DoricoSession session,
        SessionStore store,
        DoricoConnectOptions options,
        JsonObject handshake,
        CancellationToken cancellationToken)
    {
        var token = options.ResetToken ? null : store.SessionToken;

        if (string.IsNullOrWhiteSpace(token))
        {
            // First connection under this client name: Dorico asks its user, then hands out a token.
            handshake["authorization"] = "consent-dialog";

            var connected = await session.RequestAsync(
                DoricoProtocol.RidSessionToken,
                DoricoProtocol.Connect(store.ClientName),
                options.Timeout,
                cancellationToken).ConfigureAwait(false);

            token = NodeRead.Str(connected, "sessionToken");

            if (string.IsNullOrWhiteSpace(token))
            {
                throw new CliException(
                    "HandshakeFailed",
                    "Dorico did not return a session token; the connection was probably denied in Dorico.",
                    2);
            }

            var accepted = await session.RequestAsync(
                [DoricoProtocol.RidResponse, DoricoProtocol.RidSessionToken],
                DoricoProtocol.AcceptSessionToken(token),
                options.Timeout,
                cancellationToken).ConfigureAwait(false);

            if (NodeRead.IsError(accepted))
            {
                throw new CliException(
                    "HandshakeFailed",
                    $"Dorico rejected acceptsessiontoken: {NodeRead.Str(accepted, "detail") ?? "no detail"}",
                    2);
            }

            store.SessionToken = token;
            store.Save();
            handshake["accepted"] = true;

            return token;
        }

        // Later connections: the stored token authorizes us with no dialog.
        handshake["authorization"] = "stored-token";

        var response = await session.RequestAsync(
            [DoricoProtocol.RidResponse, DoricoProtocol.RidSessionToken],
            DoricoProtocol.ConnectWithToken(store.ClientName, token),
            options.Timeout,
            cancellationToken).ConfigureAwait(false);

        var code = NodeRead.Str(response, "code");
        var reissued = NodeRead.Str(response, "sessionToken");

        if (NodeRead.IsError(response) || (code is not null && code != DoricoProtocol.CodeConnected && reissued is null))
        {
            store.ClearToken();
            throw new CliException(
                "SessionTokenRejected",
                $"Dorico refused the stored session token ({NodeRead.Str(response, "detail") ?? code ?? "no detail"}). "
                + "The token has been cleared; run `dorico-use session connect` to authorize this client again.",
                2);
        }

        if (reissued is not null)
        {
            store.SessionToken = reissued;
            store.Save();
            return reissued;
        }

        handshake["tokenReused"] = true;
        handshake["code"] = code ?? DoricoProtocol.CodeConnected;

        return token;
    }
}
