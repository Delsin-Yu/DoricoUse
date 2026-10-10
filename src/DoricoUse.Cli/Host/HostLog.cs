using DoricoUse.Cli.Ipc;

namespace DoricoUse.Cli.Host;

/// <summary>
/// The host runs without a console, so its log is a file: %LOCALAPPDATA%\dorico-use\host.log.
/// </summary>
internal sealed class HostLog(bool toConsole)
{
    private readonly Lock _gate = new();

    public static string LogPath => HostRuntimeFile.LogPath;

    public void Write(string message)
    {
        var line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff} {message}";

        lock (_gate)
        {
            try
            {
                Directory.CreateDirectory(StateDirectory());
                File.AppendAllText(LogPath, line + Environment.NewLine);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Logging must never take the host down.
            }
        }

        if (!toConsole)
        {
            return;
        }

        try
        {
            Console.Error.WriteLine(line);
        }
        catch (IOException)
        {
            // A detached process has no console to write to.
        }
    }

    private static string StateDirectory() => System.IO.Path.GetDirectoryName(LogPath) ?? ".";
}
