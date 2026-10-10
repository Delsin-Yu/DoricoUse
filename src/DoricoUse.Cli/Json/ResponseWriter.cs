using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DoricoUse.Cli.Json;

/// <summary>
/// Writes the single JSON object every invocation prints to stdout.
/// </summary>
/// <remarks>
/// Success is <c>{ "ok": true, "command": "...", ...payload }</c> with the payload fields at the top level, so an
/// agent reads them without unwrapping. Failure is
/// <c>{ "ok": false, "command": "...", "error": { "code", "message", "help"? } }</c>.
/// </remarks>
internal static class ResponseWriter
{
    private static readonly JsonWriterOptions WriterOptions = new()
    {
        Indented = false,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static int Success(string command, JsonObject? payload = null, JsonObject? extras = null)
    {
        var root = new JsonObject
        {
            ["ok"] = true,
            ["command"] = command,
        };

        if (payload is not null)
        {
            foreach (var (key, value) in payload)
            {
                root[key] = value?.DeepClone();
            }
        }

        Merge(root, extras);
        Write(root);
        return 0;
    }

    public static int Fail(string command, Exception error, JsonObject? extras = null)
    {
        if (error is CliException cli)
        {
            var failure = Error(command, cli.Code, cli.Message, null, cli.Details, null);
            Merge(failure, extras);
            Write(failure);
            return cli.ExitCode;
        }

        // A stack trace is what a bug report needs and what the message alone never has. Off by default because
        // the output contract is one JSON object an agent reads, and a 20-line trace in it is noise.
        var stack = Environment.GetEnvironmentVariable(DebugVariable) is { Length: > 0 }
            ? error.ToString()
            : null;

        var report = Error(command, "RuntimeError", error.Message, null, null, stack);
        Merge(report, extras);
        Write(report);
        return 2;
    }

    /// <summary>
    /// Adds the fields a command did not set itself — today the dialog probe and the command-queue verdict. A field
    /// the payload already carries wins: <c>ui press</c> reports its own <c>dialog</c>, and it is the better answer.
    /// </summary>
    private static void Merge(JsonObject root, JsonObject? extras)
    {
        if (extras is null)
        {
            return;
        }

        foreach (var (key, value) in extras)
        {
            if (!root.ContainsKey(key))
            {
                root[key] = value?.DeepClone();
            }
        }
    }

    /// <summary>Set this to anything to have unexpected failures carry their stack trace in <c>error.data.stack</c>.</summary>
    public const string DebugVariable = "DORICO_USE_DEBUG";

    public static int FailParse(string command, string message, string? help = null)
    {
        Write(Error(command, "InvalidArgument", message, help, null, null));
        return 1;
    }

    private static JsonObject Error(
        string command,
        string code,
        string message,
        string? help,
        JsonObject? data,
        string? stack)
    {
        var error = new JsonObject
        {
            ["code"] = code,
            ["message"] = message,
        };

        if (help is not null)
        {
            error["help"] = help;
        }

        if (data is not null || stack is not null)
        {
            var details = data?.DeepClone() as JsonObject ?? [];

            if (stack is not null)
            {
                details["stack"] = stack;
            }

            error["data"] = details;
        }

        return new JsonObject
        {
            ["ok"] = false,
            ["command"] = command,
            ["error"] = error,
        };
    }

    private static void Write(JsonObject root)
    {
        var buffer = new MemoryStream();

        using (var writer = new Utf8JsonWriter(buffer, WriterOptions))
        {
            root.WriteTo(writer);
        }

        Console.Out.WriteLine(Encoding.UTF8.GetString(buffer.ToArray()));
    }
}
