using System.Text.Json.Nodes;

namespace DoricoUse.Cli.Ipc;

/// <summary>
/// The contract between the CLI process and the host process: one JSON object per line over a named pipe.
/// </summary>
/// <remarks>
/// The CLI owns the agent-facing envelope (ok/command/error); the host only reports
/// <c>{"ok":true,"payload":{...}}</c> or <c>{"ok":false,"error":{...},"exitCode":n}</c>.
/// </remarks>
internal static class HostProtocol
{
    public const string OpPing = "ping";
    public const string OpStatus = "status";
    public const string OpInfo = "info";
    public const string OpConnect = "connect";
    public const string OpCommands = "commands";
    public const string OpCommand = "command";
    public const string OpEvents = "events";
    public const string OpShutdown = "shutdown";

    /// <summary>How long the host may take to answer before the CLI gives up on it.</summary>
    public static readonly TimeSpan PipeGrace = TimeSpan.FromSeconds(5);

    /// <summary>Per-user pipe name, so two users on one machine never share a host.</summary>
    public static string PipeName { get; } = $"dorico-use-{Sanitize(Environment.UserName)}";

    /// <summary>Held by the host for its whole life, so a second `host serve` refuses to start.</summary>
    public static string HostMutexName { get; } = $"dorico-use-host-{Sanitize(Environment.UserName)}";

    public static string Request(string op, JsonObject? args, TimeSpan timeout)
    {
        var request = new JsonObject
        {
            ["op"] = op,
            ["timeoutMs"] = (long)timeout.TotalMilliseconds,
        };

        if (args is not null && args.Count > 0)
        {
            request["args"] = args;
        }

        return request.ToJsonString();
    }

    public static JsonObject Ok(JsonObject payload) => new()
    {
        ["ok"] = true,
        ["payload"] = payload,
    };

    public static JsonObject Error(string code, string message, int exitCode = 2) => new()
    {
        ["ok"] = false,
        ["error"] = new JsonObject
        {
            ["code"] = code,
            ["message"] = message,
        },
        ["exitCode"] = exitCode,
    };

    private static string Sanitize(string value)
    {
        var cleaned = new string(value
            .Select(character => char.IsLetterOrDigit(character) ? char.ToLowerInvariant(character) : '-')
            .ToArray());

        return cleaned.Length == 0 ? "user" : cleaned;
    }
}
