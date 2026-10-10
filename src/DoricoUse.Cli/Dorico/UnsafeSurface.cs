using System.Text.Json.Nodes;

namespace DoricoUse.Cli.Dorico;

/// <summary>
/// The one gate in front of Dorico's scripting layer, and the reason a user's <c>Script.*</c> command does not
/// reach it. That layer is undocumented and not defensive: <c>docs/scripting.md</c> section 8 records a probe
/// that killed Dorico from inside the C++ exception machinery, where a Lua <c>pcall</c> cannot help. So a name
/// in the <c>Script.</c> family is refused unless the build was compiled with
/// <c>EXPOSE_UNSAFE_SCRIPT_RUN_API</c> defined, which no released build is.
/// </summary>
/// <remarks>
/// This gates what a <em>user</em> sends, not what the tool may do for itself: <c>session read</c> and note
/// insert drive <c>Script.RunScript</c> through the host, with a script this repository wrote, and never come
/// through here. The file depends on nothing but <see cref="CliException"/> and
/// <c>System.Text.Json.Nodes</c>, because a check project compiles it by hand instead of referencing the CLI.
/// </remarks>
internal static class UnsafeSurface
{
    /// <summary>True only in a build that was asked for it (EXPOSE_UNSAFE_SCRIPT_RUN_API); never in a release.</summary>
    public static bool ScriptRunExposed =>
#if EXPOSE_UNSAFE_SCRIPT_RUN_API
        true;
#else
        false;
#endif

    /// <summary>The same reasoning as the message, in one line, for <c>error.data.reason</c>.</summary>
    private const string Reason = "the scripting layer is undocumented and not defensive, and probing it from "
        + "here has crashed Dorico; this build does not expose it";

    /// <summary>
    /// Refuses a command that reaches the scripting layer, unless this build exposes it. It is called with the
    /// name a user typed and before anything is composed or sent, so the refusal needs no host and no running
    /// Dorico; a name outside the <c>Script.</c> family - <c>NoteInput.Enter</c>, anything in the catalogue - is
    /// left alone, because it is the family, not the name, that is unsafe.
    /// </summary>
    public static void RefuseCommand(string name)
    {
        if (ScriptRunExposed || !name.StartsWith("Script.", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        throw new CliException(
            "ScriptingDisabled",
            $"'{name}' reaches Dorico's scripting layer, which this build deliberately does not expose: the layer "
            + "is undocumented and not defensive, and probing it from here has crashed Dorico. The command is "
            + "refused before a host is started and before anything is sent, so nothing reached the application. "
            + "docs/scripting.md section 8 records what happened - the write path is real, and so is the crash - "
            + "and it should be read before anything in this repository tries that layer again.",
            1)
        {
            Details = new JsonObject
            {
                ["command"] = name,
                ["reason"] = Reason,
            },
        };
    }
}
