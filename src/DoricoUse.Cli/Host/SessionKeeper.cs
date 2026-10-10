using System.Text.Json.Nodes;
using DoricoUse.Cli.Dorico;
using DoricoUse.Cli.Ipc;
using DoricoUse.Cli.Views;

namespace DoricoUse.Cli.Host;

/// <summary>
/// Owns the one long-lived connection to Dorico.
/// </summary>
/// <remarks>
/// The connection is made lazily on the first request that needs it and then kept open, so the stream of
/// unsolicited status and selection messages accumulates here instead of dying with each CLI invocation. Requests
/// are serialised through a gate because Dorico pairs answers with requests in order.
/// </remarks>
internal sealed class SessionKeeper(string address, string clientName)
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    private DoricoClient? _client;
    private JsonObject? _app;

    /// <summary>
    /// The token Dorico granted in this process, for the build that must not write it to disk.
    /// </summary>
    /// <remarks>
    /// A NEVER_PERSIST_SESSION store keeps nothing, so a reconnect would cost the user a second authorization
    /// dialog; holding the token here is what makes "not stored" mean memory-only rather than forgotten at once. It
    /// dies with the host, which is exactly what the note on `session connect` promises.
    /// </remarks>
    private string? _memoryToken;

    /// <summary>What the store on disk held the last time a connection was opened, for the note that reports it.</summary>
    private bool _storedTokenIgnored;

    public string Address { get; } = address;

    public string ClientName { get; } = clientName;

    public bool IsConnected => _client is { Session.IsOpen: true };

    public int Requests { get; private set; }

    public string? LastError { get; private set; }

    /// <summary>Everything Dorico has pushed since the host started, oldest first.</summary>
    public IReadOnlyList<JsonObject> Events => _client?.Session.Events ?? [];

    /// <summary>Runs one protocol exchange on the sticky connection, reconnecting once if Dorico dropped it.</summary>
    public async Task<JsonObject> ExecuteAsync(
        string what,
        Func<DoricoClient, Task<JsonObject>> action,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            var client = await EnsureClientAsync(timeout, resetToken: false, cancellationToken).ConfigureAwait(false);

            try
            {
                return await Complete(action(client), what).ConfigureAwait(false);
            }
            catch (CliException ex) when (ex.Code is "NotConnected" or "ConnectionLost")
            {
                // Dorico closed the socket (quit, restart, or a long idle). Drop it and reconnect once.
                await DropAsync().ConfigureAwait(false);
                client = await EnsureClientAsync(timeout, resetToken: false, cancellationToken).ConfigureAwait(false);
                return await Complete(action(client), what).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            LastError = $"{what}: {ex.Message}";
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Connects, or reconnects after a token reset. Used by `session connect`.</summary>
    public async Task<JsonObject> ConnectAsync(bool resetToken, TimeSpan timeout, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (resetToken)
            {
                await DropAsync().ConfigureAwait(false);
            }

            var client = await EnsureClientAsync(timeout, resetToken, cancellationToken).ConfigureAwait(false);
            Requests++;

            return new JsonObject
            {
                ["address"] = Address,
                ["clientName"] = ClientName,
                ["app"] = SessionView.App(_app),
                ["session"] = client.Handshake.DeepClone(),
                ["storePath"] = SessionStore.FilePath,
                ["note"] = ConnectNote(),
            };
        }
        catch (Exception ex)
        {
            LastError = $"connect: {ex.Message}";
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// The one thing a caller of `session connect` must not miss: whether the token Dorico just handed out
    /// outlives this process, and where it goes if it does.
    /// </summary>
    /// <remarks>
    /// Built from <see cref="SessionStore.FilePath"/> instead of a literal path, so it is right on whoever's machine
    /// is running it. The path is not the secret (the token is), and this is a field in the payload - no prompt,
    /// nothing on stderr. `host stop` is named because it is the first thing a user would try, and it does not touch
    /// the file.
    /// </remarks>
    private string ConnectNote()
    {
        if (SessionStore.PersistsToken)
        {
            return $"The session token Dorico granted is stored in plain text at {SessionStore.FilePath} so that "
                + "later runs do not ask again. `host stop` does not remove it; deleting that folder does.";
        }

        var note = "This build does not store the session token: only the running host keeps it, so every `host "
            + "start` (or the first use after Dorico restarts) asks the user to allow this client again.";

        if (_storedTokenIgnored)
        {
            note += $" A session token from an earlier build is on disk at {SessionStore.FilePath}; this build does "
                + "not use it and that folder can be deleted.";
        }

        return note;
    }

    public JsonObject Snapshot()
    {
        var snapshot = new JsonObject
        {
            ["connected"] = IsConnected,
            ["address"] = Address,
            ["clientName"] = ClientName,
            ["app"] = _app is null ? null : SessionView.App(_app),
            ["requests"] = Requests,
            ["lastError"] = LastError,
            ["events"] = new JsonObject
            {
                ["total"] = Events.Count,
                ["counts"] = SessionView.EventCounts(Events),
            },
        };

        // Which document is active is session-wide state, and every command acts on it: report it so an operator
        // can see whose project is live (and pass the same number to --expect-score).
        if (_client is not null
            && NodeRead.Int(_client.Session.Status.Current, "activeOpenScoreID") is { } activeScoreId)
        {
            snapshot["activeScoreId"] = activeScoreId;
        }

        return snapshot;
    }

    public async Task StopAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);

        try
        {
            await DropAsync().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<JsonObject> Complete(Task<JsonObject> exchange, string what)
    {
        var payload = await exchange.ConfigureAwait(false);
        Requests++;

        return payload;
    }

    private async Task<DoricoClient> EnsureClientAsync(
        TimeSpan timeout,
        bool resetToken,
        CancellationToken cancellationToken)
    {
        if (!resetToken && _client is { Session.IsOpen: true } open)
        {
            return open;
        }

        if (_client is not null)
        {
            await DropAsync().ConfigureAwait(false);
        }

        var store = SessionStore.Load();

        // The host's settings win: they are what the user started it with.
        store.Address = Address;
        store.ClientName = ClientName;

        _storedTokenIgnored = store.StoredTokenIgnored;

        if (!SessionStore.PersistsToken)
        {
            // Nothing on disk can authorize us in this build, so the token this process was already granted does.
            // A reset means asking Dorico for a new one instead.
            store.SessionToken = resetToken ? null : _memoryToken;
        }

        DoricoClient client;

        try
        {
            client = await DoricoClient.ConnectAsync(
                new DoricoConnectOptions(store, timeout, resetToken),
                cancellationToken).ConfigureAwait(false);
        }
        catch (CliException ex) when (ex.Code is "SessionTokenRejected")
        {
            // Dorico refused it. The store it would clear holds nothing here, so drop the token in memory.
            _memoryToken = null;
            throw;
        }

        if (!SessionStore.PersistsToken)
        {
            _memoryToken = client.Session.SessionToken ?? _memoryToken;
        }

        _client = client;

        try
        {
            _app = await client.AppInfoAsync(timeout, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Version info is a nicety; a connected session without it is still useful.
            LastError = $"getappinfo: {ex.Message}";
        }

        return client;
    }

    /// <summary>Tears the connection down. Never called while the gate is held by someone else.</summary>
    private async Task DropAsync()
    {
        var client = _client;
        _client = null;

        if (client is null)
        {
            return;
        }

        try
        {
            await client.DisposeAsync().ConfigureAwait(false);
        }
        catch
        {
            // The socket is discarded either way.
        }
    }
}
