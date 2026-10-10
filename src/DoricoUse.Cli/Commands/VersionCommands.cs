using System.CommandLine;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using DoricoUse.Cli.Dorico;
using DoricoUse.Cli.Ipc;

namespace DoricoUse.Cli.Commands;

/// <summary>
/// What this build is, where it keeps its state and which protocol constants it speaks.
/// </summary>
/// <remarks>
/// The one command that always answers. It opens no pipe, starts no host, touches neither Dorico nor the network
/// and creates nothing on disk, so an agent can ask "which dorico-use is this, is it the Native AOT build, and
/// where does it keep its session?" before anything else works — with Dorico closed and no host running.
/// </remarks>
internal static class VersionCommands
{
    public static Command Build()
    {
        var command = new Command(
            "version",
            "This build (assembly version, Native AOT or not), where its state lives, and the protocol constants it "
            + "speaks. Starts no host and needs no Dorico.");

        command.SetAction(parse => CommandRoot.RunAsync("version", () => Task.FromResult(Report())));

        return command;
    }

    /// <summary>The whole payload, assembled from the running process and the files it would use.</summary>
    internal static JsonObject Report()
    {
        // SessionStore.Load only reads session.json; it never writes and never contacts Dorico.
        var store = SessionStore.Load();
        var announced = HostRuntimeFile.Read();

        return new JsonObject
        {
            ["version"] = BuildInfo(),
            ["compatibility"] = Compatibility(),
            ["state"] = State(store, announced),
            ["protocol"] = Protocol(store),
        };
    }

    /// <summary>
    /// What this build was tested against — a fact, kept out of the SemVer number so that the two cannot be
    /// confused: the tool version tracks the tool, and Dorico compatibility is reported here.
    /// </summary>
    private static JsonObject Compatibility() => new()
    {
        ["testedAgainst"] = DoricoProtocol.TestedAgainst,
        ["edition"] = "Pro",
        ["platform"] = "windows",
        ["api"] = $"Remote Control handshake {DoricoProtocol.HandshakeVersion}",
        ["note"] = "Only this combination is tested. The CLI uses no Windows-only API, so a macOS port is a "
            + "contribution rather than a rewrite; other Dorico editions differ in which commands and options they "
            + "accept and are untested.",
    };

    private static JsonObject BuildInfo()
    {
        var assembly = typeof(VersionCommands).Assembly;
        var name = assembly.GetName();
        var resource = ReadVersionResource();

        // RuntimeFeature is answered by the runtime itself: a Native AOT image cannot compile code at run time.
        var dynamicCodeSupported = RuntimeFeature.IsDynamicCodeSupported;

        return new JsonObject
        {
            ["tool"] = name.Name ?? "dorico-use",
            ["informational"] = InformationalVersion(assembly, name.Version),
            ["assembly"] = name.Version?.ToString(),
            ["resource"] = resource,
            ["nativeAot"] = !dynamicCodeSupported,
            ["dynamicCodeSupported"] = dynamicCodeSupported,
            ["dynamicCodeCompiled"] = RuntimeFeature.IsDynamicCodeCompiled,
            ["runtime"] = RuntimeInformation.FrameworkDescription,
            ["os"] = RuntimeInformation.OSDescription,
            ["architecture"] = RuntimeInformation.ProcessArchitecture.ToString(),
            ["processId"] = Environment.ProcessId,
            ["executable"] = Environment.ProcessPath,
            ["note"] = "`--version` prints `informational` alone; `nativeAot` is the runtime's own answer, not a "
                + "build property this program set.",
        };
    }

    /// <summary>
    /// The version the framework's own <c>--version</c> option prints, from the entry assembly's metadata. The
    /// assembly version is the fallback for an image whose attribute metadata was trimmed away.
    /// </summary>
    private static string? InformationalVersion(Assembly assembly, Version? assemblyVersion)
        => assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assemblyVersion?.ToString();

    /// <summary>
    /// The same version as the operating system sees it, out of the executable's version resource. An independent
    /// source: it comes from the file on disk, not from the loaded assembly's metadata.
    /// </summary>
    private static JsonObject? ReadVersionResource()
    {
        try
        {
            var path = Environment.ProcessPath;

            if (string.IsNullOrEmpty(path))
            {
                return null;
            }

            var info = FileVersionInfo.GetVersionInfo(path);

            return new JsonObject
            {
                ["fileVersion"] = info.FileVersion,
                ["productVersion"] = info.ProductVersion,
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>Where the per-invocation state lives, whether or not it exists yet. Nothing is created here.</summary>
    private static JsonObject State(SessionStore store, HostRuntimeFile? announced)
    {
        var directory = SessionStore.StateDirectory;
        var sessionPath = SessionStore.FilePath;
        var hostPath = HostRuntimeFile.FilePath;

        var session = new JsonObject
        {
            ["path"] = sessionPath,
            ["exists"] = File.Exists(sessionPath),
            ["address"] = store.Address,
            ["clientName"] = store.ClientName,
            ["hasToken"] = store.HasToken,
            ["note"] = "The session token itself is never printed.",
        };

        var host = new JsonObject
        {
            ["path"] = hostPath,
            ["exists"] = File.Exists(hostPath),
            ["log"] = HostRuntimeFile.LogPath,
            ["pipe"] = HostProtocol.PipeName,
            ["mutexName"] = HostProtocol.HostMutexName,
            ["announced"] = announced is not null,
            ["alive"] = null,
            ["note"] = "`alive` is not answered here: this command answers without touching the host's pipe. "
                + "`host status` is what verifies it.",
        };

        if (announced is not null)
        {
            host["pid"] = announced.Pid;
            host["startedAtUtc"] = announced.StartedAt.ToUniversalTime().ToString("O");
        }

        return new JsonObject
        {
            ["directory"] = directory,
            ["exists"] = Directory.Exists(directory),
            ["session"] = session,
            ["host"] = host,
        };
    }

    private static JsonObject Protocol(SessionStore store) => new()
    {
        ["transport"] = "websocket",
        ["defaultAddress"] = DoricoProtocol.DefaultAddress,
        ["address"] = store.Address,
        ["usesDefaultAddress"] = string.Equals(
            store.Address,
            DoricoProtocol.DefaultAddress,
            StringComparison.OrdinalIgnoreCase),
        ["handshakeVersion"] = DoricoProtocol.HandshakeVersion,
        ["defaultClientName"] = DoricoProtocol.DefaultClientName,
        ["clientName"] = store.ClientName,
    };
}
