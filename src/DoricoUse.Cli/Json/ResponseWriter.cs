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

    public static int Success(string command, JsonObject? payload = null)
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

        Write(root);
        return 0;
    }

    public static int Fail(string command, Exception error)
    {
        if (error is CliException cli)
        {
            Write(Error(command, cli.Code, cli.Message, null, cli.Details));
            return cli.ExitCode;
        }

        Write(Error(command, "RuntimeError", error.Message, null, null));
        return 2;
    }

    public static int FailParse(string command, string message, string? help = null)
    {
        Write(Error(command, "InvalidArgument", message, help, null));
        return 1;
    }

    private static JsonObject Error(string command, string code, string message, string? help, JsonObject? data)
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

        if (data is not null)
        {
            error["data"] = data.DeepClone();
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
