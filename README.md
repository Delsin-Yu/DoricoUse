# dorico-use

A JSON CLI that lets an agent steer a running Dorico through its Remote Control API, and read scores back from
disk. One invocation runs one action, prints a single JSON object to stdout, and exits. There is no MCP server: an
agent drives it from the shell.

Two processes, by design:

- **`dorico-use`** — the CLI. Short-lived, stateless from the agent's point of view.
- **`dorico-use host serve`** — a long-lived process that owns the WebSocket to Dorico, so the unsolicited
  status and selection stream keeps arriving between CLI invocations. The CLI starts it on demand and stops it
  with `host stop`; nothing else ends it.

`file read` is the exception: it parses a project file on disk and touches neither Dorico nor the host.

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
| `host start [--address] [--client-name]` / `host status` / `host stop` / `host restart` | The long-lived process that owns the connection. |
| `command send --name <Name> [--param K=V]... [--wait-ms n] [--no-verify]` | Send a Dorico command; the status is read back afterwards. |
| `command list [--family X] [--filter "*Glob*"] [--limit 100]` | Search the catalogue of 1090 commands. |
| `command info --name <Name>` | One entry: display name and the parameters it accepts. |
| `project flows` / `project layouts [--kind full-score\|part]` | Flow and layout IDs, which several options need. |
| `options get --type engraving\|notation\|layout [--layout n] [--flow n] [--filter glob] [--limit n]` | The option paths and their current values (1565 engraving options in the reference project). |
| `options set --type ... --value path=value... [--layout n] [--flow n] [--no-verify]` | Set option values and read them back to prove it. |
| `library collections` / `library entities --collection <name>` | The 50 library collections and their entities. |
| `playback techniques` / `properties get` | Expression maps and drum kit note maps; what can be read from the selection. |
| `note insert ...` | Enter notes, chords, tuplets and rests (see below). |
| `file read --path <project.dorico> [--limit n\|all] [--measure a-b] [--player <glob>]` | Read a project file from disk: notes, pitches, durations, staves. No Dorico needed. |
| `export musicxml [--path <dir>] [--wait-ms n]` | Export MusicXML. **Needs a click on 确定 inside Dorico** (see below). |
| `export text --path <file.csv>` | Export project text/comments as CSV. Fully unattended. |
| `raw send --json '{"message":...}' --expect <id\|any>` | Escape hatch for protocol messages this CLI does not model. |

Shared options: `--timeout-ms` (default 15000; 120000 for `session connect`) and `--raw` (add the unmodified
Dorico payload). `--address` and `--client-name` belong to the host, since it owns the connection; only
`session probe` takes them directly.

### Entering notes

```bash
dorico-use note insert --pitch F# --octave 5                              # one note
dorico-use note insert --notes "C5 D5 E5 F5" --duration crotchet          # a melodic line
dorico-use note insert --notes "C5+E5+G5 D5" --duration crotchet          # a chord, then a note
dorico-use note insert --chord "C4 E4 G4" --chord "D4 F4 A4" --duration minim   # a chord sequence
dorico-use note insert --staff down --tuplet 3:2 --notes "C3+E3+G3 D3+F3+A3 B2+D3+F3" --duration quaver
dorico-use note insert --rest --duration minim
dorico-use note insert --notes "C5" --grid kSemiquaver                    # the grid the caret snaps to
dorico-use note insert --notes "C5 E5" --duration minim --dry-run         # the plan, sent nowhere
```

- `+` joins the pitches of **one chord** (one rhythmic position); a space still separates events. `--chord` takes
  a whole chord per occurrence.
- `--tuplet 3:2` wraps this call's notes in a tuplet (`triplet`, `quintuplet`, ... are also accepted).
- `--staff up|down` moves the caret to the other stave of the instrument first. It is **relative**: the API has no
  absolute stave addressing and no caret query, so on a piano grand stave it means "the other hand".
- `--duration` is the **note value** (`NoteInput.NoteValue?LogDuration=`): `crotchet`, `quaver`, `minim`,
  `semibreve`, `dottedcrotchet`, ... or a Dorico name such as `kQuaver`. `--grid` is the rhythm grid
  (`NoteInput.SetGridResolution`) and is a different thing.
- `--pitch`/`--octave` use Dorico's octave numbering: **C4 is middle C** (MIDI 60), C5 is MIDI 72.
- `note insert` switches to Write mode and turns note input on only when the status says they are needed, because
  `NoteInput.Enter` is a toggle. `--stay` leaves note input on.
- **A fresh project is in overwrite mode**, so writing at a position that already holds notes replaces them. The
  CLI neither changes nor reports that mode; clear the caret's bar first, or write into fresh bars.
- **Chord input is a toggle with no query** (`NoteInput.StartEndChord`; its `Set` parameter is ignored and the
  status dumps are identical with it on and off). `note insert` toggles it around each chord group, so a chord
  comes out stacked unless the user had left chord input on by hand — which the CLI cannot detect.
- **It writes at the caret**, and the API does not say which stave the caret is on. Decide with `--staff` or by
  clicking the stave in the window first; a mistake is silent.
- Microtones, grace notes and pasting from a file are not covered; `command send` reaches the commands behind them.

### Reading a score back

Two independent routes, because Dorico cannot be queried for notes:

**`file read`** decodes the project file itself — no Dorico, no host, ~0.3 s:

```bash
dorico-use file read --path "C:\scores\Bad Apple!! (JAGMO Shiro v1.2 2026.10.08).dorico" --measure 1-2 --limit 6
```

```json
{"ok":true,"command":"file read","file":{"kind":"Dorico project (ZIP package)","bytes":2458207},
 "metadata":{"title":"Bad Apple!! (JAGMO Cover.)","composer":"ZUN","players":[…36…]},
 "score":{"counts":{"notes":16002,"measures":185,"players":36,"layouts":37,"stavesWithNotes":39},
          "notes":[{"measure":1,"beat":"1","duration":"8","pitch":"D3","midi":50,"player":"Bassoon 1","stave":7,"voice":1}]}}
```

A `.dorico` file is a ZIP package whose `META-INF/container.xml` names `score.dtn`; that member is a header, a
name table, a deduplicated value pool and a token stream (a node/leaf tree, children counted, so the parse ends
exactly on the last byte). Notes come out as `flow → blocks → events → NoteEventDefinition{eventID, pitch,
position, duration}`, with position and duration as exact rationals in crotchets, and the stave resolved through
`stream → flowPlayer → voiceStreamInfos/staveStreamInfos`. Verified against an independent MusicXML export of the
same arrangement, and against 20 project files from Dorico 6.0.0 through 6.2.30. Limits, reported as `caveats`:

- **Pitch spelling is not stored**, only a MIDI number: the CLI reports `midi` plus a sharps-only derived name
  (Bb5 shows as `A#5`); the notation's spelling comes from key signature and spelling rules at layout time.
- A **tie chain is one event** with the sounding duration; written ties are engraving.
- **Rests are gaps**, not events; **measures are derived** from the stored metre/bar-division events.
- Not modelled: the library, options, layout geometry, lyrics, dynamics, slurs/beams and playback data.
- `scorelibrary.dtn` and the other supplementary members are listed but not parsed.

**`export`** asks Dorico to write a file. `export text` is unattended (`File.Export.Text?File=...&Show=false`,
~20 ms, writes a CSV, and silently does nothing if the folder is bad). Every other filter — including
`MusicXMLExportFilter` — answers `kOK`, opens a **modal dialog** and writes nothing until 确定 is pressed:

```bash
dorico-use export musicxml --path .spinney/export --wait-ms 120000   # waits, watching --path
# meanwhile, drive Dorico's dialog: it has a path label (not editable) and a "..." folder picker, and it ignores
# any File= parameter, so set the destination through that picker, then invoke 确定.
```

While that dialog is open Dorico's whole command queue stalls, so queued commands run late and their acks and
statuses are stale. `export musicxml` therefore reports the files that really appeared and returns
`ExportNeedsConfirmation` when none did — an export that produced no file is not reported as success.

### The agent loop

```bash
dorico-use session status                                     # where am I?
dorico-use project layouts --kind part                        # which layout IDs exist?
dorico-use command list --family NoteInput --filter "*Pitch*" # what can I send, with which parameters?
dorico-use note insert --notes "C5 D5 E5" --duration crotchet
dorico-use file read --path out.dorico --measure 1-4          # what is actually in the score now?
dorico-use export text --path out.csv                         # or read text/annotations back
```

`kOK` is a receipt, never proof, so everything that changes the score reads the status back. For note entry that
is still not enough — status reports the caret and the selection, not which notes landed — so the honest check is
`file read` after a save, or a look at the window.

## JSON contract

Success puts the payload fields at the top level, so nothing has to be unwrapped:

```json
{"ok":true,"command":"note insert","note":{"duration":"kCrotchet","enteredWriteMode":true,"chords":[["C5","E5","G5"]],"steps":[{"name":"NoteInput.Pitch","ack":"kOK"}],"status":{"hasScore":true}}}
```

Failure names a stable code, and the exit code separates cause from usage:

```json
{"ok":false,"command":"file read","error":{"code":"NotAContainer","message":"..."}}
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
  known one — the host collects this stream, and 24 patches arrived during a single mode switch.
- `command` is the workhorse: `{"message":"command","command":"Name?Key=Value,Key2=Value2"}`. Dorico 6.2.30 Pro
  exposes **1090** commands (`Edit.*` 228, `NoteInput.*` 129, `Project.*` 114, `Play.*` 107, ...).
- A `kOK` answer is a **receipt, not proof**: `File.Open` with a bad path still answers `kOK` and then fails
  inside Dorico.
- Note entry: `NoteInput.Pitch?Pitch=F,OctaveValue=5` enters a note and advances the caret by itself (never send
  `MoveAdvance` after it), `NoteInput.MoveAdvance` inserts a rest, `NoteInput.NoteValue?LogDuration=kCrotchet`
  sets the note value (enum spelling; an unknown value silently becomes a semibreve),
  `NoteInput.SetGridResolution` sets the grid, `NoteInput.SetAccidental?Type=kSharp` arms an accidental,
  `NoteInput.StartEndChord` toggles chord input, `NoteInput.Enter` toggles note input,
  `NoteInput.MoveUpTop`/`MoveDownBottom` move the caret between the staves of one instrument.
- There is **no query for bars, staves, the note stream or the project at large**, no absolute stave addressing,
  no caret query, and no way to create a project (`File.New` is not even in the catalogue), add a player or add an
  instrument. Selection information is limited to duration, dots, accidental and rest mode.
- Export: `File.Export?FilterID=<filter>` with `MusicXMLExportFilter`, `MIDIFileExportFilter`,
  `AudioFileExportFilter`, `DoricoFileExportFilter`, `TempoTrackExportFilter` — all dialog-bound, except
  `File.Export.Text?File=...&Show=false`, which is not.
- **`Script.RunScript?ScriptPath=...&ShowConsole=false` runs Lua 5.2 in-process with `io` and `os`.** Anyone who
  can reach the Remote Control port can run arbitrary code as the user, so treat that port as full trust.
- `displayName` is localized (a Chinese UI returns `输入音高` for `NoteInput.Pitch`); `name` is the stable key.
- `session probe` measures the useful part: the token survives a disconnect, so a connection per invocation costs
  about 1 ms — the host exists for the event stream and for serialising access, not to save handshakes.
- The best documentation is `application.log` (`%APPDATA%\Steinberg\Dorico 6`): every action Dorico performs is
  echoed there as the command and parameters that produce it. When a message's answer id is unknown,
  `raw send --expect any` sends it and shows whatever comes back.

## Architecture

```
agent shell ──> dorico-use <command>  ──JSON lines over \\.\pipe\dorico-use-<user>──>  dorico-use host serve
                     │                                                                          │
                     └── file read ──> project file on disk                     └──WebSocket──> Dorico
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

## Tests

```bash
dotnet build tests/ProjectFileCheck/ProjectFileCheck.csproj -v q --nologo
./tests/ProjectFileCheck/bin/Debug/net10.0/project-file-check.exe
```

`ProjectFileCheck` compiles the parser sources and asserts the file-format facts: the three tag markers seen in
the corpus, a note run cross-checked against a MusicXML export, score-order and overlap invariants, and every
error path (`NotFound`, `NotAContainer`, `MissingScoreData`, `MalformedMember`, `UnsupportedScoreData`).

## Roadmap

1. **Done** — lean WebSocket client (request/response pairing, status merging), session store, `session` group.
2. **Done** — host process, pipe protocol, `host` group, `command send|list|info`, `raw send`, `session events`.
3. **Done** — `project`, `options`, `library`, `playback`, `properties`.
4. **Done** — `note insert` with chords, tuplets and relative staff targeting; `file read` (the project-file
   decoder); `export musicxml`/`export text`.
5. **Next** — insert-vs-overwrite control for note entry, a save-then-`file read` round trip the agent can run in
   one command, MusicXML `--path` handling once Dorico lets a client set the destination, and the remaining
   layout/lyrics/dynamics data in `file read`.

Deliberately **not** planned: a cached command catalogue, on disk or otherwise. Dorico answers `getcommands` in
4 ms (98 KB, 1090 commands) and a `command list` call costs about 86 ms end to end, nearly all of it process
start-up — so a cache would save ~5 ms. Worse, the catalogue is version- and edition-specific: serving a snapshot
while Dorico is closed would hand an agent a list that the Dorico it later talks to may not have. When Dorico is
not reachable, failing is the honest answer.
