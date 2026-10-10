# dorico-use

A JSON CLI that lets an agent steer a running Dorico through its Remote Control API. One invocation runs one
action, prints a single JSON object to stdout, and exits. There is no MCP server: an agent drives it from the
shell.

Two processes, by design:

- **`dorico-use`** — the CLI. Short-lived, stateless from the agent's point of view.
- **`dorico-use host serve`** — a long-lived process that owns the WebSocket to Dorico, so the unsolicited
  status and selection stream keeps arriving between CLI invocations. The CLI starts it on demand and stops it
  with `host stop`; nothing else ends it.

## Requirements

- Dorico running, with **Preferences > Remote Control** enabled (port 4560 by default).
- The **first** connection makes Dorico ask its user to allow the client; the session token it then returns is
  stored in `%LOCALAPPDATA%\dorico-use\session.json`, and every later connection is authorized silently in ~1 ms.

## Build

```powershell
dotnet build src/DoricoUse.Cli/DoricoUse.Cli.csproj
dotnet publish src/DoricoUse.Cli/DoricoUse.Cli.csproj -c Release -r win-x64
```

The published Native AOT binary lands in `src/DoricoUse.Cli/bin/Release/net10.0/win-x64/publish/dorico-use.exe`.
The source is plain `net10.0` (no Win32 dependency), so the same code can target macOS later.

**Stop the host before rebuilding**: a running host holds the executable open, and the build fails with
`MSB3027 ... being used by another process`. `dorico-use host stop` (or `host restart` after building) is enough.

## Commands

| Command | Purpose |
|---|---|
| `session status [--raw]` | Score loaded, selection, note input, mode, playback. This is your feedback. |
| `session info [--raw]` | Which Dorico (Pro 6.2.30) and how the host reaches it. |
| `session connect [--reset]` | Open the session and store its token; `--reset` forces the Allow dialog again. |
| `session events [--limit 20] [--payload]` | What Dorico pushed unprompted while nobody was asking. |
| `session probe [--rounds 3] [--observe-ms 1500]` | Diagnostics for the connection itself; talks to Dorico directly, not through the host. |
| `host start [--address] [--client-name]` | Start the host if it is not running, and wait until it answers. |
| `host status` | Is the host up, is it connected, how many events has it collected? |
| `host stop` / `host restart` | Stop it (disconnecting politely), or stop and start again. |
| `command send --name <Name> [--param K=V]... [--wait-ms n] [--no-verify]` | Send a Dorico command; the status is read back afterwards. |
| `command list [--family X] [--filter "*Glob*"] [--limit 100]` | Search the catalogue of 1090 commands. |
| `command info --name <Name>` | One entry: display name and the parameters it accepts. |
| `raw send --json '{"message":...}' --expect <messageId>` | Escape hatch for protocol messages this CLI does not model. |

Shared options: `--timeout-ms` (default 15000; 120000 for `session connect`) and `--raw` (add the unmodified
Dorico payload). `--address` and `--client-name` belong to the host, since it owns the connection; only
`session probe` takes them directly.

### The agent loop

```bash
dorico-use session status                                     # where am I?
dorico-use command list --family NoteInput --filter "*Pitch*" # what can I send, and with which parameters?
dorico-use command send --name NoteInput.Pitch --param Pitch=F --param OctaveValue=5
dorico-use session status                                     # read the effect back
dorico-use session events --limit 20                          # what happened between my calls
```

Commands are silent about failure: Dorico answers `kOK` to a command it will then refuse, so `command send`
reads the status back by default. Add `--wait-ms 5000` for something slow like `File.Open`, whose effect is not
visible immediately.

## JSON contract

Success puts the payload fields at the top level, so nothing has to be unwrapped:

```json
{"ok":true,"command":"session status","status":{"hasScore":true,"scoreId":1,"mode":{"window":"kWriteMode"},"noteInput":{"active":false},"selection":{"duration":""},"zoomPercent":144}}
```

Failure names a stable code, and the exit code separates cause from usage:

```json
{"ok":false,"command":"session status","error":{"code":"NotReachable","message":"..."}}
```

Exit codes: `0` success, `1` argument/usage error, `2` protocol, connection or Dorico failure, `3` a host was
already running when `host serve` was asked to start.

`session status` returns the fields an agent can act on; `--raw` adds the whole 67-field status Dorico sends.
Numbers Dorico sends as strings (`"zoomPercent":"144"`) are normalised to numbers in the view.

## What is true about Dorico's API

Verified against Dorico Pro 6.2.30 on Windows 11:

- WebSocket at `ws://127.0.0.1:4560`, one JSON object per message, **answers come back in request order**.
- Requests: `connect`, `acceptsessiontoken`, `disconnect`, `getstatus`, `getappinfo`, `getcommands`, `command`.
- Dorico pushes messages nobody asked for: `status`, `selectionchanged` and friends. A status push is a
  **partial patch** and so is a `getstatus` answer sent while a modal dialog is up, so every status is merged
  into the last known one (this is what the host is for: 24 patches arrived during one mode switch).
- `command` is the workhorse: `{"message":"command","command":"Name?Key=Value,Key2=Value2"}`. Dorico 6.2.30 Pro
  exposes **1090** commands (`Edit.*` 228, `NoteInput.*` 129, `Project.*` 114, `Play.*` 107, ...).
- A `kOK` answer is a **receipt, not proof**: `File.Open` with a bad path still answers `kOK` and then fails
  inside Dorico.
- There is **no query for bars, staves, the note stream or the project at large**. Selection information is
  limited to duration, dots, accidental and rest mode.
- `displayName` is localized (a Chinese UI returns `输入音高` for `NoteInput.Pitch`); `name` is the stable key.
- `session probe` measures the useful part: the token survives a disconnect, so a connection per invocation costs
  about 1 ms — the host exists for the event stream and for serialising access, not to save handshakes.
- The best documentation is `application.log` (`%APPDATA%\Steinberg\Dorico 6`): every action Dorico performs is
  echoed there as the command and parameters that produce it.

## Architecture

```
agent shell ──> dorico-use <command>  ──JSON lines over \\.\pipe\dorico-use-<user>──>  dorico-use host serve
                                                                                        │
                                                                                        └──WebSocket──> Dorico
```

- One JSON object per line in both directions. The CLI owns the agent-facing envelope (`ok`/`command`/`error`);
  the host answers `{"ok":true,"payload":{...}}` or `{"ok":false,"error":{...},"exitCode":n}`.
- The host connects lazily on the first request that needs Dorico and keeps the connection open, reconnecting
  once if Dorico drops it. Requests are serialised through a gate, because Dorico pairs answers with requests in
  order.
- The host announces itself in `%LOCALAPPDATA%\dorico-use\host.json` (pid, pipe, settings) and writes
  `host.log`. `host stop` asks it to stop, then kills it by pid if it does not.
- The host is launched through the shell, never as an inherited child: a child holds the caller's stdout open,
  and whoever reads that pipe (an agent's shell) would wait for EOF forever — the command looks hung even though
  the CLI has exited.

## Roadmap

1. **Done** — lean WebSocket client (request/response pairing, status merging), session store, `session` group.
2. **Done** — host process, pipe protocol, `host` group, `command send|list|info`, `raw send`, `session events`.
3. **Next** — musical sugar on top of the command channel: `note insert`, `selection get`, `project flows|layouts`,
   `options get|set`, `library ...`, plus a cached catalogue so `command list` costs nothing and can be searched
   offline.
