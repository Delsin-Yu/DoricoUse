using System.Text.Json.Nodes;

namespace DoricoUse.Cli;

/// <summary>
/// An error that maps to a stable machine-readable code and a process exit code.
/// </summary>
internal sealed class CliException : Exception
{
    public CliException(string code, string message, int exitCode = 1)
        : base(message)
    {
        Code = code;
        ExitCode = exitCode;
    }

    public string Code { get; }

    public int ExitCode { get; }

    /// <summary>
    /// Structured facts about the failure, reported as <c>error.data</c>. An agent should not have to read a
    /// sentence to find out which path, layout or option a command refused.
    /// </summary>
    public JsonObject? Details { get; init; }
}
