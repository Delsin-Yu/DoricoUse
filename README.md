# dorico-use

A JSON CLI that lets an agent steer a running Dorico through its Remote Control API. One invocation runs one
action, prints a single JSON object to stdout, and exits. There is no MCP server: an agent drives it from the
shell.

Status: phase 1 — connection, handshake, status. See "Roadmap" below.

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
Source is plain `net10.0` (no Win32 dependency), so the same code can target macOS later.

## Commands

| Command | Purpose |
|---|---|
| `session status [--raw]` | Score loaded? selection, note input, mode, playback. |
| `session info [--raw]` | Which Dorico (`Pro 6.2.30`) and how the CLI reaches it. |
| `session connect [--reset] [--raw]` | Open a session and store its token. `--reset` forces the Allow dialog again. |
| `session probe [--rounds 3] [--observe-ms 1500] [--raw]` | Diagnostics: time the handshake, prove the token is reused, count the command catalogue, watch unsolicited messages. |

Shared options: `--address` (default `ws://127.0.0.1:4560`), `--client-name`, `--timeout-ms` (default 15000),
`--raw` (add the unmodified Dorico payload).

## JSON contract

Success puts the payload fields at the top level, so nothing has to be unwrapped:

```json
{"ok":true,"command":"session status","session":{"authorization":"stored-token","code":"kConnected"},"status":{"hasScore":true,...}}
```

Failure names a stable code, and the exit code separates cause from usage:

```json
{"ok":false,"command":"session status","error":{"code":"NotReachable","message":"..."}}
```

Exit codes: `0` success, `1` argument/usage error, `2` protocol, connection or Dorico failure.

`session status` returns the fields an agent can act on (`hasScore`, `hasSelection`, `mode.window`,
`noteInput.active`, `selection.duration`, ...); `--raw` adds the 67-field status Dorico actually sends.

## What is true about Dorico's API

Verified against Dorico Pro 6.2.30 on Windows 11 by `session probe`:

- WebSocket at `ws://127.0.0.1:4560`, one JSON object per message, **answers come back in request order**.
- Requests: `connect`, `acceptsessiontoken`, `disconnect`, `getstatus`, `getappinfo`, `getcommands`, `command`.
- Dorico also pushes messages nobody asked for: `status` (a **partial patch** of the last known status),
  `selectionchanged`, and friends. A client that stays connected can watch them; a one-shot invocation cannot.
- `command` is the workhorse: `{"message":"command","command":"Name?Key=Value,Key2=Value2"}`. Dorico 6.2.30 Pro
  exposes **1090** commands (`Edit.*` 228, `NoteInput.*` 129, `Project.*` 114, `Play.*` 107, ...).
- A `kOK` answer is a **receipt, not proof**: a bad path in `File.Open` still answers `kOK` and then fails inside
  Dorico. Real feedback means reading the status or looking at the window.
- There is **no query for bars, staves, the note stream or the project at large**. Selection information is limited
  to duration, dots, accidental and rest mode.
- `displayName` is localized (a Chinese UI returns `设备设置 (S)...`); `name` is the stable identifier.
- The best documentation is `application.log` (`%APPDATA%\Steinberg\Dorico 6`): every action Dorico performs is
  echoed there as the command and parameters that produce it.

## Roadmap

1. **Done** — lean WebSocket client (request/response pairing, status patch merging), session store, `session`
   group, probe.
2. **Host + CLI interop** — a `dorico-use host` process that owns the connection (`host start|stop|status`), and
   every other command talking to it, so that commands cost no handshake, the unsolicited stream is collected,
   and rapid sequences (note entry) do not reconnect each time.
3. **Command surface** — `command send|batch|list|info`, `note insert`, `selection get`, `project flows|layouts`,
   `options get|set`, `lib ...`, `raw send`, with the command catalogue cached on disk.
