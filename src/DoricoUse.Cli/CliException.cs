namespace DoricoUse.Cli;

/// <summary>
/// An error that maps to a stable machine-readable code and a process exit code.
/// </summary>
internal sealed class CliException : Exception
{
    public string Code { get; }

    public int ExitCode { get; }

    public CliException(string code, string message, int exitCode = 1)
        : base(message)
    {
        Code = code;
        ExitCode = exitCode;
    }
}
