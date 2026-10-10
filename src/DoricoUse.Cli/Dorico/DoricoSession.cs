using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;

namespace DoricoUse.Cli.Dorico;

/// <summary>
/// One WebSocket connection to Dorico: request/response pairing plus the messages Dorico pushes on its own.
/// </summary>
/// <remarks>
/// Dorico answers in request order, so pending requests are queued and the oldest one that matches an incoming
/// message id completes. The getstatus reply is a complete status; every other unsolicited status is a patch.
/// </remarks>
internal sealed class DoricoSession : IAsyncDisposable
{
    private readonly ClientWebSocket _socket = new();
    private readonly ConcurrentQueue<PendingRequest> _pending = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly TaskCompletionSource<bool> _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly List<JsonObject> _events = [];
    private readonly Lock _eventGate = new();

    private Task? _pump;
    private bool _disposed;

    public StatusTracker Status { get; } = new();

    public string Address { get; private set; } = string.Empty;

    public string? SessionToken { get; set; }

    /// <summary>How many unsolicited messages to keep for `session events`.</summary>
    public int MaxEvents { get; set; } = 250;

    public bool IsOpen => _socket.State == WebSocketState.Open;

    public bool IsClosed => _closed.Task.IsCompleted;

    /// <summary>Messages Dorico sent without being asked (status patches, selection changes, ...).</summary>
    public IReadOnlyList<JsonObject> Events
    {
        get
        {
            lock (_eventGate)
            {
                return [.. _events];
            }
        }
    }

    public async Task OpenAsync(string address, CancellationToken cancellationToken)
    {
        Address = address;
        await _socket.ConnectAsync(new Uri(address), cancellationToken).ConfigureAwait(false);
    }

    public void Start()
    {
        _pump ??= Task.Run(PumpAsync);
    }

    public async Task<JsonObject> RequestAsync(
        string expectedId,
        string message,
        TimeSpan timeout,
        CancellationToken cancellationToken)
        => await RequestAsync([expectedId], message, timeout, cancellationToken).ConfigureAwait(false);

    /// <param name="expectedIds">Message ids that may answer this request; the first one to arrive wins.</param>
    public async Task<JsonObject> RequestAsync(
        string[] expectedIds,
        string message,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        if (!IsOpen)
        {
            throw new CliException("NotConnected", "The connection to Dorico is not open.", 2);
        }

        var pending = new PendingRequest(expectedIds);
        _pending.Enqueue(pending);

        try
        {
            await SendRawAsync(message, cancellationToken).ConfigureAwait(false);
            return await pending.Completion.Task.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            pending.Abandon();
            throw new CliException(
                "Timeout",
                $"No {string.Join("/", expectedIds)} answer within {timeout.TotalMilliseconds:0} ms. "
                + "Dorico may be busy, or showing a dialog that needs a click.",
                2);
        }
        finally
        {
            pending.Abandon();
        }
    }

    public async Task SendRawAsync(string message, CancellationToken cancellationToken)
    {
        var bytes = Encoding.UTF8.GetBytes(message);
        await _socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Sends the disconnect message and waits for Dorico to close the socket, which is its answer.</summary>
    public async Task<bool> DisconnectAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (!IsOpen)
        {
            return IsClosed;
        }

        await SendRawAsync(DoricoProtocol.Disconnect(), cancellationToken).ConfigureAwait(false);
        return await WaitForCloseAsync(timeout, cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> WaitForCloseAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (IsClosed)
        {
            return true;
        }

        try
        {
            await _closed.Task.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        _cts.Cancel();
        FailPending(new CliException("NotConnected", "The session was disposed.", 2));

        try
        {
            if (_socket.State == WebSocketState.Open)
            {
                using var closeTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "dorico-use closing", closeTimeout.Token)
                    .ConfigureAwait(false);
            }
        }
        catch
        {
            // Closing is best effort; the socket is discarded either way.
        }
        finally
        {
            _socket.Dispose();
        }

        if (_pump is not null)
        {
            try
            {
                await _pump.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            }
            catch
            {
                // Pump shutdown is not worth surfacing.
            }
        }

        _cts.Dispose();
    }

    private async Task PumpAsync()
    {
        var buffer = new byte[16 * 1024];

        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var (isClose, content) = await ReceiveAsync(buffer, _cts.Token).ConfigureAwait(false);

                if (isClose)
                {
                    break;
                }

                if (content.Length > 0)
                {
                    Dispatch(content);
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            FailPending(new CliException("ConnectionLost", $"The connection to Dorico failed: {ex.Message}", 2));
        }
        finally
        {
            FailPending(new CliException("NotConnected", "Dorico closed the connection.", 2));
            _closed.TrySetResult(true);
        }
    }

    private async Task<(bool IsClose, string Content)> ReceiveAsync(byte[] buffer, CancellationToken cancellationToken)
    {
        var text = new StringBuilder();

        while (true)
        {
            var result = await _socket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken)
                .ConfigureAwait(false);

            if (result.MessageType == WebSocketMessageType.Close)
            {
                return (true, string.Empty);
            }

            if (result.Count > 0)
            {
                text.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
            }

            if (result.EndOfMessage)
            {
                return (false, text.ToString());
            }
        }
    }

    private void Dispatch(string content)
    {
        JsonObject? message;

        try
        {
            message = JsonNode.Parse(content) as JsonObject;
        }
        catch (System.Text.Json.JsonException)
        {
            Record(DoricoProtocol.Unknown, content);
            return;
        }

        if (message is null)
        {
            Record(DoricoProtocol.Unknown, content);
            return;
        }

        var id = NodeRead.Str(message, "message") ?? DoricoProtocol.Unknown;

        DropAbandoned();

        // An unsolicited status is a partial patch; the one answering getstatus is the complete status.
        if (id == DoricoProtocol.RidStatus && !NextExpects(id))
        {
            Status.Apply(message, full: false);
            Record(id, message);
            return;
        }

        if (IsNotification(id))
        {
            Record(id, message);
            return;
        }

        if (_pending.TryPeek(out var pending) && (pending.Expects(id) || NodeRead.IsError(message)))
        {
            _pending.TryDequeue(out _);

            if (id == DoricoProtocol.RidStatus)
            {
                Status.Apply(message, full: true);
            }

            pending.Complete(message);
            return;
        }

        Record(id, message);
    }

    private static bool IsNotification(string id)
        => id is DoricoProtocol.RidSelectionChanged
            or DoricoProtocol.RidOptionsChanged
            or DoricoProtocol.RidLibraryChanged
            or DoricoProtocol.RidPlaybackConfigurationChanged;

    private bool NextExpects(string id)
    {
        DropAbandoned();
        return _pending.TryPeek(out var pending) && pending.Expects(id);
    }

    private void DropAbandoned()
    {
        while (_pending.TryPeek(out var pending) && pending.Abandoned)
        {
            _pending.TryDequeue(out _);
        }
    }

    private void FailPending(Exception error)
    {
        while (_pending.TryDequeue(out var pending))
        {
            pending.Fail(error);
        }
    }

    private void Record(string kind, JsonObject payload)
    {
        lock (_eventGate)
        {
            _events.Add(new JsonObject
            {
                ["kind"] = kind,
                ["atMs"] = Environment.TickCount64,
                ["payload"] = payload.DeepClone(),
            });

            TrimEvents();
        }
    }

    private void Record(string kind, string raw)
    {
        lock (_eventGate)
        {
            _events.Add(new JsonObject
            {
                ["kind"] = kind,
                ["atMs"] = Environment.TickCount64,
                ["raw"] = raw,
            });

            TrimEvents();
        }
    }

    private void TrimEvents()
    {
        while (_events.Count > MaxEvents)
        {
            _events.RemoveAt(0);
        }
    }

    private sealed class PendingRequest(string[] expectedIds)
    {
        private readonly string[] _expectedIds = expectedIds;

        public TaskCompletionSource<JsonObject> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool Abandoned { get; private set; }

        public bool Expects(string id) => Array.IndexOf(_expectedIds, id) >= 0;

        public void Complete(JsonObject response) => Completion.TrySetResult(response);

        public void Fail(Exception error) => Completion.TrySetException(error);

        public void Abandon() => Abandoned = true;
    }
}
