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

    public JsonObject Snapshot() => new()
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

        var client = await DoricoClient.ConnectAsync(
            new DoricoConnectOptions(store, timeout, resetToken),
            cancellationToken).ConfigureAwait(false);

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
