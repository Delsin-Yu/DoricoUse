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

**Stop the host before rebuilding**: a running host holds the executable open and the build fails with
`MSB3027 ... being used by another process`. `host stop` before building, `host restart` after.

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
| `project flows` / `project layouts [--kind full-score\|part]` | Flow and layout IDs, which several options need. |
| `options get --type engraving\|notation\|layout [--layout n] [--flow n] [--filter glob] [--limit n]` | The option paths and their current values (1565 engraving options in the reference project). |
| `options set --type ... --value path=value... [--layout n] [--flow n] [--no-verify]` | Set option values and read them back to prove it. |
| `library collections` / `library entities --collection <name>` | The 50 library collections and their entities. |
| `playback techniques` | The expression maps and drum kit note maps in use. |
| `properties get` | What can be read from the current selection. |
| `note insert ...` | Enter notes and rests (see below). |
| `raw send --json '{"message":...}' --expect <id\|any>` | Escape hatch for protocol messages this CLI does not model. |

Shared options: `--timeout-ms` (default 15000; 120000 for `session connect`) and `--raw` (add the unmodified
Dorico payload). `--address` and `--client-name` belong to the host, since it owns the connection; only
`session probe` takes them directly.

### Entering notes

```bash
# one note
dorico-use note insert --pitch F# --octave 5

# a melodic line, one call
dorico-use note insert --notes "C5 D5 E5 F5" --duration crotchet
dorico-use note insert --notes "G5 A5 B5 C6 D6 E6 F6 G6" --duration quaver

# a rest, and the rhythm grid the caret snaps to
dorico-use note insert --rest --duration minim
dorico-use note insert --notes "C5" --grid kSemiquaver

# see the plan without sending anything
dorico-use note insert --notes "C5 E5" --duration minim --dry-run
```

- `--duration` is the **note value** (`NoteInput.NoteValue?LogDuration=`): `crotchet`, `quaver`, `minim`,
  `semibreve`, `dottedcrotchet`, ... or a Dorico name such as `kQuaver`. An unknown value makes Dorico fall back
  to a semibreve, so the CLI rejects it instead.
- `--grid` is the **rhythm grid** the caret snaps to (`NoteInput.SetGridResolution`). It is not the note value;
  setting one does not change the other.
- `--pitch`/`--octave` use Dorico's octave numbering, where **C4 is middle C** (MIDI 60) and C5 is MIDI 72.
- `--notes` writes the notes one after another: entering a pitch moves the caret on by itself, so no advance is
  sent (sending one would put a rest after every note). Use `--rest` for a rest.
- `note insert` switches to Write mode and turns note input on only when the status says they are needed, because
  `NoteInput.Enter` is a toggle. `--stay` leaves note input on.
- **It writes at the caret.** The API does not say which stave the caret is on, and there is no command to move
  it to a named stave: on a piano grand stave, place the caret (click the stave in the window) before inserting,
  or the notes land in the other hand. `dorico-use` cannot detect this mistake.
- Chords, tuplets and microtones are not covered yet; `command send` reaches the commands behind them.

### The agent loop

```bash
dorico-use session status                                     # where am I?
dorico-use project layouts --kind part                        # which layout IDs exist?
dorico-use command list --family NoteInput --filter "*Pitch*" # what can I send, and with which parameters?
dorico-use note insert --notes "C5 D5 E5" --duration crotchet
dorico-use session status                                     # read the effect back
dorico-use session events --limit 20                          # what happened between my calls
```

Commands are silent about failure: Dorico answers `kOK` to a command it will then refuse, so everything that
changes the score reads the status back. For note entry, though, **status is not enough** — it reports the caret
and the selection, not which notes landed. Look at the window or undo when it matters.

## JSON contract

Success puts the payload fields at the top level, so nothing has to be unwrapped:

```json
{"ok":true,"command":"note insert","note":{"duration":"kCrotchet","enteredWriteMode":true,"steps":[{"name":"NoteInput.Pitch","ack":"kOK"}],"status":{"hasScore":true,...}}}
```

Failure names a stable code, and the exit code separates cause from usage:

```json
{"ok":false,"command":"session status","error":{"code":"NotReachable","message":"..."}}
```

Exit codes: `0` success, `1` argument/usage error, `2` protocol, connection or Dorico failure, `3` a host was
already running when `host serve` was asked to start.

## What is true about Dorico's API

Verified against Dorico Pro 6.2.30 on Windows 11:

- WebSocket at `ws://127.0.0.1:4560`, one JSON object per message, **answers come back in request order**.
- Requests: `connect`, `acceptsessiontoken`, `disconnect`, `getstatus`, `getappinfo`, `getcommands` (`commandlist`),
  `getflows` (`flowslist`), `getlayouts` (`layoutslist`), `getoptions` (`optionslist`), `setoptions` (`response`),
  `getproperties` (`propertieslist`), `getplaybacktechniques` (`playbacktechniqueslist`),
  `getlibrarycollections` (`librarycollectionslist`), **`getlibraryentities`** (`libraryentitieslist`), `command`.
- Dorico pushes messages nobody asked for: `status`, `selectionchanged` and friends. A status push is a **partial
  patch**, and so is a `getstatus` answer sent while a modal dialog is up, so every status is merged into the last
  known one. This is what the host is for: 24 patches arrived during a single mode switch.
- `command` is the workhorse: `{"message":"command","command":"Name?Key=Value,Key2=Value2"}`. Dorico 6.2.30 Pro
  exposes **1090** commands (`Edit.*` 228, `NoteInput.*` 129, `Project.*` 114, `Play.*` 107, ...).
- A `kOK` answer is a **receipt, not proof**: `File.Open` with a bad path still answers `kOK` and then fails
  inside Dorico.
- Note entry: `NoteInput.Enter` toggles note input, `NoteInput.Pitch?Pitch=F,OctaveValue=5` enters a note and
  moves the caret, `NoteInput.MoveAdvance` inserts a rest, `NoteInput.NoteValue?LogDuration=kCrotchet` sets the
  note value, `NoteInput.SetGridResolution` sets the grid, `NoteInput.SetAccidental?Type=kSharp` arms an
  accidental for the next note.
- There is **no query for bars, staves, the note stream or the project at large**, and no way to create a project
  (`File.New` is not even in the catalogue), add a player or add an instrument. Selection information is limited
  to duration, dots, accidental and rest mode.
- `displayName` is localized (a Chinese UI returns `输入音高` for `NoteInput.Pitch`); `name` is the stable key.
- `session probe` measures the useful part: the token survives a disconnect, so a connection per invocation costs
  about 1 ms — the host exists for the event stream and for serialising access, not to save handshakes.
- The best documentation is `application.log` (`%APPDATA%\Steinberg\Dorico 6`): every action Dorico performs is
  echoed there as the command and parameters that produce it. When a message's answer id is unknown,
  `raw send --expect any` sends it and shows whatever comes back.

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
3. **Done** — `project`, `options`, `library`, `playback`, `properties` and `note insert`, all verified live.
4. **Next** — chords and tuplets in `note insert`, a cached catalogue so `command list` works with Dorico closed,
   `File.Export` for reading a score back, and staff-aware note entry once the API offers a way to address a
   stave.
