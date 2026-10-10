using System.Text.Json;
using System.Text.Json.Nodes;
using DoricoUse.Cli.Dorico;

namespace DoricoUse.Cli.Ipc;

/// <summary>
/// Where the running host announces itself: its pid, its pipe, and the connection settings it was started with.
/// The CLI reads this to find the host, to report on it, and to kill it if it stops answering.
/// </summary>
internal sealed class HostRuntimeFile
{
    public int Pid { get; init; }

    public string Pipe { get; init; } = HostProtocol.PipeName;

    public string Address { get; init; } = DoricoProtocol.DefaultAddress;

    public string ClientName { get; init; } = DoricoProtocol.DefaultClientName;

    public DateTimeOffset StartedAt { get; init; }

    public static string FilePath => Path.Combine(SessionStore.StateDirectory, "host.json");

    public static string LogPath => Path.Combine(SessionStore.StateDirectory, "host.log");

    public static HostRuntimeFile? Read()
    {
        try
        {
            if (!File.Exists(FilePath) || JsonNode.Parse(File.ReadAllText(FilePath)) is not JsonObject json)
            {
                return null;
            }

            return new HostRuntimeFile
            {
                Pid = NodeRead.Int(json, "pid") ?? 0,
                Pipe = NodeRead.Str(json, "pipe") ?? HostProtocol.PipeName,
                Address = NodeRead.Str(json, "address") ?? DoricoProtocol.DefaultAddress,
                ClientName = NodeRead.Str(json, "clientName") ?? DoricoProtocol.DefaultClientName,
                StartedAt = DateTimeOffset.TryParse(NodeRead.Str(json, "startedAtUtc"), out var started)
                    ? started
                    : DateTimeOffset.MinValue,
            };
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public void Write()
    {
        try
        {
            Directory.CreateDirectory(SessionStore.StateDirectory);

            var json = new JsonObject
            {
                ["pid"] = Pid,
                ["pipe"] = Pipe,
                ["address"] = Address,
                ["clientName"] = ClientName,
                ["startedAtUtc"] = StartedAt.ToUniversalTime().ToString("O"),
            };

            File.WriteAllText(FilePath, json.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A host that cannot announce itself is still reachable by pipe name; only `host status` suffers.
        }
    }

    public static void Delete()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                File.Delete(FilePath);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Stale announcement; a later `host status` still verifies liveness over the pipe.
        }
    }
}
