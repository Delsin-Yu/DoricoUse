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

## Quickstart

```bash
dorico-use session connect      # the first run asks Dorico's user to click Allow; the token is then stored
dorico-use session status       # score loaded, selection, note input, mode - this is your feedback
dorico-use file read --path "C:\scores\my score.dorico" --limit 20   # works with Dorico closed
dorico-use command list --family NoteInput --filter "*Pitch*"        # what can be sent, and its parameters
dorico-use note insert --notes "C5 D5 E5" --duration crotchet        # writes at the caret
```

Only `note insert` needs a project with at least one player; `file read` needs neither Dorico nor the host.
Every command prints one JSON object, and the ones with a `--raw` option can add Dorico's own payload.

## Requirements

- Dorico running, with **Preferences > Remote Control** enabled (port 4560 by default).
- The **first** connection makes Dorico ask its user to allow the client; the session token it then returns is
  stored **in plain text** in `%LOCALAPPDATA%\dorico-use\session.json`, and every later connection is authorized
  silently in ~1 ms.

## Build

```powershell
dotnet build src/DoricoUse.Cli/DoricoUse.Cli.csproj
dotnet publish src/DoricoUse.Cli/DoricoUse.Cli.csproj -c Release -r win-x64
```

The published Native AOT binary lands in `src/DoricoUse.Cli/bin/Release/net10.0/win-x64/publish/dorico-use.exe`
(~8.5 MB, no `hostfxr`, `version` reports `nativeAot:true`) and the publish is clean — no IL2026/IL3050 trim
warnings. With `--artifacts-path <dir>` the binary lands in `<dir>/publish/DoricoUse.Cli/release_win-x64/`, not in
the `bin` tree. The source is plain `net10.0` (no Win32 dependency), so the same code can target macOS later.

**Stop the host before rebuilding**: a running host holds the executable open and the build fails with
`MSB3027 ... being used by another process`. `host stop` before building, `host restart` after.

## Install, upgrade, uninstall

This is a single self-contained executable: copy `dorico-use.exe` anywhere on `PATH` (or call it by full path).
There is no installer, no service, and no scheduled task — nothing is registered anywhere.

Two things live outside the executable:

| Path | What | Removing it |
|---|---|---|
| `%LOCALAPPDATA%\dorico-use\` | `session.json` (connection settings and the session token Dorico granted this client, kept **in plain text**), `host.json` (the running host's pid and pipe) and `host.log` | delete the folder |
| a running `host` process | `host start` leaves one background process alive by design: the connection has to stay open for the status stream Dorico pushes | `dorico-use host stop` |

**Upgrading: stop the host first.** A running host holds the executable open, so replacing it while it runs fails;
`dorico-use host stop`, replace the file, and the next command starts the new host. Nothing else needs migrating.

## Versioning and compatibility

The tool version is SemVer and says nothing about Dorico; what it was tested against is a fact, reported by
`version` (`compatibility.testedAgainst`, `compatibility.api`). Windows is the only platform tested, on Dorico
6.2.30 Pro. Nothing in the CLI uses a Windows-only API — the pipe, the WebSocket and the file decoder are plain
.NET — so a macOS port is a contribution rather than a rewrite; see `CONTRIBUTING.md`.

## Commands

| Command | Purpose |
|---|---|
| `session status [--raw]` | Score loaded, selection, note input, mode, playback. This is your feedback. |
| `session info [--raw]` | Which Dorico (Pro 6.2.30) and how the host reaches it. |
| `session connect [--reset]` | Open the session and store its token; `--reset` forces the Allow dialog again. |
| `session read [--limit n] [--rebind] [--to <file>] [--measure a-b] [--player <glob>]` | What the open score really contains: read from the **running** document by default, with no save and no side effect. `--rebind` reads a saved copy instead (exact data, but Dorico rebinds the document to it). |
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
| `note insert ...` | Enter notes, chords, tuplets and rests; `--from-start` writes from the top, `--read` proves it (see below). |
| `file read --path <project.dorico> [--limit n\|all] [--measure a-b] [--player <glob>] [--tree <name\|*>] [--dynamics] [--lyrics] [--slurs] [--articulations] [--ties] [--beams] [--techniques] [--layout] [--mark-limit n]` | Read a project file from disk: notes, dynamics, slurs, articulations, ties, beams, techniques, layout, or any subtree. No Dorico needed. |
| `export musicxml [--path <watch dir>] [--wait-ms n]` | Export MusicXML. **Needs a click on 确定 inside Dorico** (see below). |
| `export text --path <file.csv>` | Export project text/comments as CSV. Fully unattended. |
| `version` | This build (assembly version, whether it is Native AOT), where its state lives, and the protocol constants. Starts no host, needs no Dorico. |

There is deliberately **no** command that sends an arbitrary protocol message. One existed during development
(it is how some of the message ids below were discovered), and it was removed before release: a tool that can post
anything can post anything at all, including the destructive commands.

Shared options: `--timeout-ms` (default 15000; 120000 for `session connect`) and `--raw` (add the unmodified
Dorico payload). `--address` and `--client-name` belong to the host, since it owns the connection; only
`session probe` takes them directly. `--version` is System.CommandLine's own option and prints a bare version
string, not JSON — the `version` command is the one with the envelope.

### Sharing one Dorico

Every command acts on **whichever document Dorico has active**, and bringing any window to the front changes that —
there is no query for it and no way to address a document. When more than one agent uses the same Dorico, that is
how a save lands in someone else's project. So every **mutating** command (`command send`, `note insert`,
`session read --rebind`, `export musicxml`, `export text`, `options set`) takes `--expect-score <id>`: the host reads
the status first and refuses with `WrongActiveScore` (`error.data {expected, actual}`) when the active document is not
the one named. `session status` and `host status` (`activeScoreId`) report the ID. The check runs inside the same
gated exchange as the action, so the window between them is one round trip — a stolen focus can only cause a
refusal. A live read (`session read`, `note insert --read`) sends the same guard with the `Script.RunScript` command
that does the reading, because that script also reads whichever document is active. Verify it the way it can fail:
`session read --expect-score 999` must refuse, and so must every other command in that list.

### Entering notes

```bash
dorico-use note insert --pitch F# --octave 5                              # one note
dorico-use note insert --notes "C5 D5 E5 F5" --duration crotchet          # a melodic line
dorico-use note insert --notes "C5+E5+G5 D5" --duration crotchet          # a chord, then a note
dorico-use note insert --chord "C4 E4 G4" --chord "D4 F4 A4" --duration minim   # a chord sequence
dorico-use note insert --staff down --tuplet 3:2 --notes "C3+E3+G3 D3+F3+A3 B2+D3+F3" --duration quaver
dorico-use note insert --rest --duration minim
dorico-use note insert --notes "C5" --grid kSemiquaver                    # the grid the caret snaps to
dorico-use note insert --notes "C5" --mode insert                         # push existing music right
dorico-use note insert --notes "C5 E5" --duration minim --dry-run         # the plan, sent nowhere
```

- `+` joins the pitches of **one chord** (one rhythmic position); a space still separates events. `--chord` takes
  a whole chord per occurrence.
- `--tuplet 3:2` wraps this call's notes in a tuplet (`triplet`, `quintuplet`, ... are also accepted).
- `--staff up|down` moves the caret to the other stave of the instrument first. It is **relative**: the API has no
  absolute stave addressing and no caret query, so on a piano grand stave it means "the other hand".
- `--mode insert|overwrite` sets `NoteInput.Mode` — **and only sends it when the status says the mode differs**,
  so omitting the flag never changes anything. `overwrite` replaces the notes at the caret, `insert` pushes them
  right. The payload always reports `mode.before` and `mode.effective`, and a fresh project starts in overwrite.
- `--duration` is the **note value** (`NoteInput.NoteValue?LogDuration=`): `crotchet`, `quaver`, `minim`,
  `semibreve`, `breve`, `long`, `maxima`, `semiquaver`, `demisemiquaver`, `hemidemisemiquaver`, a dotted form
  (`dottedcrotchet`), a fraction (`1/8`), or a Dorico spelling. The accepted spellings are the ten the binary's
  own `LogDuration` template lists (`kMaxima`, `kLong`, `kBreve`, `kSemibreve`, `kMinim`, `kCrotchet`, `kQuaver`,
  `kSemiQuaver`, `kDemiSemiQuaver`, `kHemiDemiSemiQuaver`) — note that the *grid* command spells 16ths
  `kSemiquaver`, and both spellings exist, so `--grid kSemiquaver` and `--duration semiquaver` are correct as
  written. `--grid` is the rhythm grid (`NoteInput.SetGridResolution`) and is a different thing.
- `--pitch`/`--octave` use Dorico's octave numbering: **C4 is middle C** (MIDI 60), C5 is MIDI 72.
- `note insert` switches to Write mode and turns note input on only when the status says they are needed, because
  `NoteInput.Enter` is a toggle. `--stay` leaves note input on.
- **Where the caret is decides where the music goes**, and the API cannot report it. `Edit.SelectNone` alone does
  **not** move the caret; `--from-start` does, by cycling `NoteInput.Exit` → `Edit.SelectNone` → `NoteInput.Enter`,
  which returns the caret to the start of the flow *and* to the instrument's top stave. That is the one way to
  write from a known place, and it is what makes a phrase repeatable.
  Nothing else moves the caret: `NoteInput.EnterAtPoint` ignores its X/Y (12 probes across four coordinate
  guesses), `Edit.GoTo` either does nothing or opens a modal 前往小节 dialog, `NoteInput.SetPosition` is accepted
  and ignored, and passing `Position`/`EventStreamID`/`InstrumentID` to `NoteInput.Pitch` is ignored too. Only the
  mouse addresses a point.
- **A click in the window is not a safe way to place the caret while note input is on**: Dorico treats it as
  entering a note at that point (position from x, pitch from y, tied to zoom and scroll). Turn note input off
  first, or accept the note.
- `--read [--limit n|all]` writes and proves in one call: after the notes, the CLI has Dorico run the same
  read-only script `session read` does by default and parses the file it wrote, so the payload carries the notes
  that are really in the score while **nothing is saved and the document is not rebound**. `read.source` is
  `"live"`, and `read.*` has exactly `session read`'s shape. `--limit` works on both routes; on the live route `0`
  means every note.
- `--read --read-rebind [--read-to <path>] [--limit n|all] [--measure m|a-b] [--player <glob>]` is the other route
  to the same proof, kept for the exact data: Dorico saves a copy inside the same gated exchange (`File.SaveAs`)
  and the CLI decodes it, so `read.score.notes` carries positions, MIDI numbers, accidentals and layout — at the
  price of **the open document being rebound to that copy**. `--read-to`, `--measure` and `--player` only mean
  anything here: without `--read-rebind` they are an `InvalidArgument` (exit 1) that points at
  `session read --rebind`, and `--read-rebind` without `--read` is refused the same way. `--limit 0` means the
  shape alone on this route.
- **Chord input is a toggle with no query** (`NoteInput.StartEndChord`; its `Set` parameter is ignored and the
  status dumps are identical with it on and off). `note insert` toggles it around each chord group, so a chord
  comes out stacked unless the user had left chord input on by hand — which the CLI cannot detect.
- Microtones, grace notes and pasting from a file are not covered; `command send` reaches the commands behind them.

### Reading a score back

Three independent routes, because Dorico cannot be queried for notes:

**`session read`** reads the document Dorico has **open**, right now. By default it does that by handing Dorico a
small read-only Lua script (`Script.RunScript`) that walks the score and writes what it finds to a file — nothing
is saved, nothing is rebound, and the document is untouched. In one invocation, in half a second:

```json
{"ok":true,"command":"session read","source":"live",
 "score":{"openScoreId":1,"version":"6.2.30b6245"},
 "counts":{"flows":8,"streams":824,"events":37827,"notes":4,"unparsedLines":0,"noteLimit":4},
 "kinds":{"kNoteEvent":30811,"kSlurEvent":1674,"kTextEvent":20,"kTrillEvent":45,...},
 "notes":[{"flow":1,"stream":196,"eventId":239342,"position":"0,","duration":"2","pitch":"C4"}]}
```

`source` says which route produced the payload, and `caveats` says what that route cannot see: the numbers come
from Dorico's own printer (`dump()`), parsed with regular expressions, so **no MIDI number, accidental, dynamic
value or layout** comes out of it, and a line this build cannot read is counted in `counts.unparsedLines` rather
than guessed at. `pitch` here is the name the score prints, so it is the spelled name (a B flat is `Bb`, not
`A#`). A live read also cannot filter by measure or player — those options belong to `--rebind`: `session read
--rebind`, or `note insert --read --read-rebind` for the same proof in one call with the write.

```bash
dorico-use command send --name Edit.SelectNone          # caret back to the top
dorico-use note insert --notes "B4" --duration quaver --mode insert
dorico-use session read --limit all                     # live: writes and proves, no save
#  ...and to see the exact data instead:
dorico-use session read --rebind --measure 1 --limit all
```

**`session read --rebind`** saves a copy and decodes it with the same reader `file read` uses, which is what gives
the exact data (positions, MIDI numbers, accidentals, dynamics, layout). The price is real and opt-in: **Dorico
rebinds the open document to that copy** — autosave, backups and later `File.Save` follow it — reported in
`save.document` and `sideEffects`. It saves whichever document is active, so bring the window you mean to the front
first. `--to` defaults to a timestamped file under the system temporary directory and never overwrites an existing
file (`WouldOverwrite`, exit 1).

**`file read`** decodes the project file itself — no Dorico, no host, ~0.3 s:

```bash
dorico-use file read --path "C:\scores\Bad Apple!! (JAGMO Shiro v1.2 2026.10.08).dorico" --measure 1-2 --limit 6
dorico-use file read --path <same> --limit 0 --dynamics --lyrics      # 754 dynamics, 305 groups
dorico-use file read --path <same> --limit 0 --tree "*" --tree-limit 5 # census: 464 node kinds, 2906 leaf keys
dorico-use file read --path <same> --limit 0 --tree DynamicGroupEventDefinition --depth 1
```

```json
{"ok":true,"command":"file read","file":{"kind":"Dorico project (ZIP package)","bytes":2458207},
 "metadata":{"title":"Bad Apple!! (JAGMO Cover.)","composer":"ZUN","players":[…36…]},
 "score":{"counts":{"notes":16002,"measures":185,"players":36,"layouts":37,"stavesWithNotes":39},
          "notes":[{"measure":1,"beat":"1","duration":"8","pitch":"D3","midi":50,"player":"Bassoon 1","stave":7,"voice":1}]},
 "tree":{"kind":"census","nodes":140465,"leaves":440361,"names":[{"name":"xOffset","kind":"leaf","count":28346}]}}
```

A `.dorico` file is a ZIP package whose `META-INF/container.xml` names `score.dtn`; that member is a header, a
name table, a deduplicated value pool and a token stream (a node/leaf tree, children counted, so the parse ends
exactly on the last byte). Notes come out as `flow → blocks → events → NoteEventDefinition{eventID, pitch,
position, duration}`, with position and duration as exact rationals in crotchets, and the stave resolved through
`stream → flowPlayer → voiceStreamInfos/staveStreamInfos`. Dynamics come from `DynamicGroupEventDefinition` plus
its immediate/gradual/force marks. `--tree` is the escape hatch for everything else: `*` is a census of every node
kind and leaf key, a name dumps each occurrence with its root path and fields, and a miss returns the nearest names.

The other blocks read the same way, and each states in its own `caveats` what it rests on:

| Option | What it reads | Verification |
|---|---|---|
| `--dynamics` | 754 events, 305 groups, `cresc`/`dim`/`f`/`pp`… | every mark, position and measure matched Dorico's MusicXML export (325 markings, 0 differences) |
| `--slurs` | 987 slur events, 438 elements, noteheads per slur | the (start, length) pairs match the export's 214 `<slur>` pairs exactly |
| `--articulations` | 7839 marks (Staccato 3914, Accent 3576, Tenuto 349) | matches the export's `<articulations>` counts; names come from the score's own library |
| `--ties` | what *is* stored: LV ties, tie-to-event, split values | the export's `<tied type="let-ring"/>` count matches (17) |
| `--beams` | the 16 beam properties (Bad Apple) / 17325 (another file) | single source — no corpus file has both |
| `--techniques` | 35 technique events + 45 note techniques, with ids, texts and names | single source — the appearance → technique → name chain is the file's own library |
| `--layout` | page box, margins, casting off, page sets and frames | page size and margin match the export's tenths exactly |

Verified against independent MusicXML exports and against 20 project files from Dorico 6.0.0 through 6.2.30.
Limits, reported as `caveats`:

- **Pitch spelling is a letter, and it is now a field — and the accidental is derived from it.** The project
  stores a MIDI number plus a per-note spelling *letter* (`kNoteSpelling`) and no accidental, so each note carries
  `spelling`, `accidental` and `accidentalSteps`: MIDI 82 spelled `B` is a B flat. The derivation is checked
  against Dorico's own MusicXML export — its `<alter>` against `accidentalSteps`, note for note, **6968 of 6968**
  in the 13 concert-pitch parts of the reference project, with `tools/verify_accidentals.py` — and it is
  **withheld where the two inputs cannot agree**: on a transposing instrument, whose written and sounding pitches
  differ by the interval the file states at `instrument/transposition/intervalForTransposingScores` (13
  instruments in the reference score), and on unpitched percussion, where the MIDI value is a drum-map position.
  11387 of the reference score's 16002 notes get a derived accidental, 4615 get null, and the flats (1171 of them)
  are exactly what the sharp-only `pitch` name cannot express. Grace notes carry a spelling letter too but are a
  separate event kind this reader does not list.
- **Ties and beams are not events**: a tie chain is one note event with the sounding duration, the beaming a score
  is printed with is computed at layout time, and no corpus file has both beam properties and an export.
- **A click in the window is the only way to address a point**, so anything about where music sits on a page
  (frame geometry → measure mapping) is not derivable here: casting off is a layout computation and the file's own
  cache of it is empty.

- **Lyrics are read, and that reading was checked against a score that has them.** The non-empty path was written
  from the *binary's* vocabulary (`LyricElementDefinition`, `LyricEventDefinition`, `elementID`, `text`,
  `kLyricSyllableType`, …) with no file to try it on, and then one file settled it: a score with 30810 notes and 43
  players holds **8 lyric tables, 4710 elements and 4710 events**, and the guesses held — the id and the text sit in
  `elementID` and `text`, a syllable carries `syllableType: kWholeWord`, `lineType: kVerse`, `lineLocation: kBelow`,
  the first two syllables are the ones that were typed (`A-`, `lice`), text outside Latin-1 survives (the score
  carries Chinese lines), and every event resolves to a real measure, beat and stave (event 129187 → measure 6,
  beat 2, element 954, stave 834). The link is 1:1 across the whole file: 4710 `LyricElementDefinition` against
  4710 `LyricEventDefinition`. `ProjectFileCheck` asserts exactly that in **15 checks**, which run wherever that
  file is — where it is not, the block reports a single `skip`, which is what CI sees. `--lyrics` still returns
  `read`/`events`/`fields` plus **5 caveats** that separate what is measured from what is inferred. Still
  unverified: **which** lyric line is a verse and which is a translation (the checks only pin that the lines are
  told apart), and no second file with lyrics has been read — every other file of the reference corpus still
  answers `elements: 0`.
- **Rests are gaps**, not events; **measures are derived** from the stored metre/bar-division events.
- Not modelled: the score library's engraving options, frame → measure mapping, playback/mixer data and tremolo.
- `scorelibrary.dtn` and the other supplementary members are listed but not parsed.

**`export`** asks Dorico to write a file. `export text` is unattended (`File.Export.Text?File=...&Show=false`,
~20 ms, writes a CSV, and silently does nothing if the folder is bad). Every other filter — including
`MusicXMLExportFilter` — answers `kOK`, opens a **modal dialog** and writes nothing until 确定 is pressed:

```bash
dorico-use export musicxml --path .\export --wait-ms 120000   # waits, watching --path
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

When a failure has facts worth reading, they are in `error.data` in the same shape the success payload would
have, so an agent never has to parse the sentence:

```json
{"ok":false,"command":"export musicxml","error":{"code":"ExportNeedsConfirmation","message":"…","data":{"export":{"pathRole":"watch","checks":1,"destination":{"path":"…/Documents/Dorico 工程","key":"defaultProjectPath"}}}}}
```

Exit codes: `0` success, `1` argument/usage error, `2` protocol, connection or Dorico failure, `3` a host was
already running when `host serve` was asked to start.

## What is true about Dorico's API

The protocol shapes recorded here were learned from and cross-checked against
[Dorico.Net](https://github.com/scott-janssens/Dorico.Net) (MIT), the community library that documented Dorico's
Remote Control API first. This project reimplements a much smaller client instead of depending on it; the enum
spellings it validates come from the same source.

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
  `NoteInput.Mode?NoteInputMode=kInsert|kOverwrite` sets insert/overwrite (the status reports `noteInputMode`,
  and the setting follows the application rather than the document),
  `NoteInput.StartEndChord` toggles chord input, `NoteInput.Enter` toggles note input,
  `NoteInput.MoveUpTop`/`MoveDownBottom` move the caret between the staves of one instrument.
- Saving and closing: `File.SaveAs?File=<path>` is **headless** — it writes the copy and then rebinds the open
  document to it. `File.Save?File=<path>` ignores the path and saves in place, so it can only ever overwrite the
  user's own file. `File.Close?OpenScoreID=<id>` and `File.CloseProject` **do close the document** (an earlier
  note here said otherwise because the commands had been queued behind a modal dialog and only ran minutes later).
- **A modal dialog stalls the whole command queue, and queued commands run later, out of sight.** They still
  answer `kOK` when they are posted, so an ack says nothing about when a command ran — while an export dialog was
  open, a `File.Close` queued earlier closed a document minutes afterwards. Never queue something destructive, and
  when a command seems to do nothing, check `application.log` for when it really ran.
- There is **no query for bars, staves, the note stream or the project at large**, no absolute stave addressing and
  no caret query. There is also no way to create a project through the **published** API: `File.New` is absent from
  the catalogue while the application carries it (bound to Ctrl+N) and 161 further names the API never publishes —
  including the whole `Debug.*` and `Custom.*` families (see `docs/hidden-commands.md`). Whether those execute when
  sent was deliberately not tested.
- Export: `File.Export?FilterID=<filter>` with `MusicXMLExportFilter`, `MIDIFileExportFilter`,
  `AudioFileExportFilter`, `DoricoFileExportFilter`, `TempoTrackExportFilter` — all dialog-bound, except
  `File.Export.Text?File=...&Show=false`, which is not. The dialog ignores any `File=` parameter: its 导出到
  label shows the **folder of the document's own file**, or `defaultProjectPath` from
  `%APPDATA%\Steinberg\Dorico 6\preferences.xml` for a project that has never been saved, and a choice made in
  its `...` picker lasts for that one export. While it is open Dorico's commands are queued, not run, and the API
  cannot tell you a dialog is up (`getstatus` is answered from the modal loop).
- **`Script.RunScript?ScriptPath=...&ShowConsole=false` runs Lua 5.2 in-process with `io` and `os`.** Anyone who
  can reach the Remote Control port can run arbitrary code as the user, so treat that port as full trust.
- `displayName` is localized (a Chinese UI returns `输入音高` for `NoteInput.Pitch`); `name` is the stable key.
- `session probe` measures the useful part: the token survives a disconnect, so a connection per invocation costs
  about 1 ms — the host exists for the event stream and for serialising access, not to save handshakes.
- The best documentation is `application.log` (`%APPDATA%\Steinberg\Dorico 6`): every action Dorico performs is
  echoed there as the command and parameters that produce it. That, the command catalogue and XML files Dorico
  itself exports are how every fact below was established — a generic send-any-message command is not shipped.

### Beyond the published API

The application carries far more than the remote API publishes, and two of the three additions here come from
reading that surface rather than from the protocol. The repo ships the **extractors, never the extracted data**.

**Enum validation.** Dorico answers `kOK` to an unknown enum value and silently substitutes another — an unknown
`LogDuration` becomes a semibreve — so a typo in `--param` would otherwise be invisible until you heard the wrong
music. The values of the parameters this build knows are **built into the CLI**: the note values, the note-input
modes, the window modes, the grid resolutions, the accidental types and the option types. So
`command send --param LogDuration=kQuavor` is refused before anything is sent, with the nearest spellings in the
message and in `error.data` (`kQuaver`), and `--allow-unknown-enum` sends it anyway when you mean it. Nothing has
to be installed, extracted or kept up to date for this to work.

**Only those parameters are checked.** A value passed to any other parameter goes to Dorico unchecked, because a
spelling this build has never heard of is far more likely to be something a newer Dorico accepts than a typo —
refusing it would break working commands. The payload always says what happened: `vocabulary.source` is
`"built in"`, and `vocabulary.lookedAt` lists each value that was checked and the verdict.

**Unpublished commands.** `docs/hidden-commands.md` diffs the binary's command-name pool against the live
catalogue: **1191 names in the application, 1029 of them published, 162 not** — including `File.New` (Ctrl+N),
`File.NewFromTemplate`, `Play.NavigateForwards` and the entire `Debug.*` and `Custom.*` families. Nothing was sent
to Dorico to see whether they execute.

**The scripting layer.** `docs/scripting.md` records what Dorico's Lua console really exposes, measured: the
recorder is exactly a keystroke log (a recorded macro is three `app:doCommand([[EventEdit.NavigateDown]])` lines),
while the object model behind it is real but half-built — `score.flows/players/layouts` and `EventStream:dump()`
read fine, and the convenient getters answer `Not implemented - this may leave the score in an unpredictable state
@ streamwrapper.cpp:162`. The community's verdict ("scripting has no meaningful access to score data") describes
the front door, not the layer: a script **can write**. `docs/scripting.md` §8 has the verified run — a script built
its arguments (`RhythmicLength(0)`, `MIDIPitchWithSpelling(MIDIPitch(60), NoteName.fromString('C'))`), called
`stream:createNote(...)`, and `session read` found the note in the file Dorico wrote, field for field (event id,
measure, position 0, duration 2, C4, spelling C, Flute 1, stream 196), with the score going 30810 → 30811 notes.
The catch is that nothing there is documented or safe: probing the constructors blind **crashed Dorico once**
(`tools/analyze_minidump.py` reads the dump, and §8 carries the ACCESS_VIOLATION inside the C++ exception machinery),
so the write path is real but has to be walked one zero-argument call at a time.

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

`ProjectFileCheck` compiles the reader's sources **and the view that renders them**. Seventeen of its checks run
against `tests/corpus/fixture.dorico`, a small project committed with the repository (one Piano player, two staves,
three notes: a B flat, an F sharp and a C) so that the decoder is exercised for real in continuous integration; the
rest assert facts about the maintainer's own scores, which are not in the repository and report `skip` when you do
not have them. It asserts: the three tag markers seen in the corpus, a note run cross-checked against a MusicXML export, the dynamics
histogram and positions matched against Dorico's own export of two pieces, slurs/articulations/ties/layout matched
against another, the per-note spelling letter matched onset by onset against a third (8476 notes, 0 differences,
with the export's `<transpose>` table asserted), score-order and overlap invariants, the generic tree walker
compared with the typed reader, that the report can be built three times in one process (a shared node parented
twice throws), and every error path (`NotFound`, `NotAContainer`, `MissingScoreData`, `MalformedMember`,
`UnsupportedScoreData`). 187 checks run and none is skipped on a machine that has the maintainer's scores: the ones
that need a file which is not in the repository report `skip` instead, which is what CI sees — the lyric block, for
instance, is 15 checks reported there as a single `skip`.

```bash
dotnet build tests/VocabularyCheck/VocabularyCheck.csproj -v q --nologo
./tests/VocabularyCheck/bin/Debug/net10.0/vocabulary-check.exe    # 77 checks: the built-in table, no setup at all
```

`VocabularyCheck` needs nothing but the framework: no Dorico, no host, no extraction step. It checks the table
against the code that uses it (the note values the CLI resolves are exactly the ones it validates), the shape rule,
the refusal and its suggestions, and the one behaviour that matters for forward compatibility — a value for a
parameter this build does not know is never refused.

## Troubleshooting

| Symptom | What it is |
|---|---|
| `NotReachable` on every live command | Dorico is not running, its `Preferences > Remote Control` server is off, or it listens elsewhere (set `--address` on `host start`). `file read` and `version` still work without Dorico. |
| The build fails with `MSB3027 ... being used by another process` | The host is running from that binary and holds it open. `dorico-use host stop`, build, and the next command starts the host again. |
| A command seems to do nothing | Something is modal in Dorico. While a dialog is open Dorico **queues** commands instead of running them, and each one still answered `kOK` when it was posted — so they run minutes later, out of sight. Look in `%APPDATA%\Steinberg\Dorico 6\application.log` for when a command really ran, and close the dialog. |
| `WrongActiveScore {expected, actual}` | `--expect-score` refused: the document Dorico has active is not the one you named. Bring the right window to the front, or drop the flag. |
| `kOK` but nothing happened | `kOK` is a receipt, not proof — a bad `File.Open` path answers it too. Every command that changes the score reads the status back; for note entry the honest proof is `session read`. |
| `session read` changed which file my document points at | Only `session read --rebind` does that: `File.SaveAs` rebinds the open document to the copy it writes. The default read does not save anything and has no side effect. If you did use `--rebind`, re-open the original file afterwards; the copy is under your system temporary directory. |
| Dorico crashed while running a script | The scripting layer (`Script.RunScript` via `command send`) is undocumented and not defensive; building its C++ argument objects blind has crashed Dorico here. See `docs/scripting.md` §8 before using it. |
| `file read` reports `NotAContainer` / `UnsupportedScoreData` | The file is not a Dorico project, or is a newer format than this build knows. The reader refuses rather than guessing. |
| How do I revoke this client? | Delete `%LOCALAPPDATA%\dorico-use\` and remove the client in Dorico if it is listed there. The session token Dorico granted this client is in `session.json` there, in plain text — `host stop` does not remove it, but deleting the folder does. |

## Roadmap

1. **Done** — lean WebSocket client (request/response pairing, status merging), session store, `session` group.
2. **Done** — host process, pipe protocol, `host` group, `command send|list|info`, `session events`.
   (A raw sender existed here and was removed before release.)
3. **Done** — `project`, `options`, `library`, `playback`, `properties`.
4. **Done** — `note insert` with chords, tuplets and relative staff targeting; `file read` (the project-file
   decoder); `export musicxml`/`export text`.
5. **Done** — insert/overwrite control, `session read` (save a copy and read it back in one command), the tree
   escape hatch and dynamics in `file read`, and an honest export destination report.
6. **Done** — `--expect-score` (refuse to act on the wrong document, on every mutating command), `note insert
   --from-start` and `--read` (write and prove in one call), the per-note spelling letter as a field, a `version`
   command, a proven Native AOT publish, and slurs/articulations/ties/beams/techniques/layout in `file read`.
7. **Done** — the vocabulary extractor with enum validation, the unpublished-command diff (`docs/hidden-commands.md`)
   and the scripting-layer analysis (`docs/scripting.md`), plus lyric reading, checked against a score that has
   lyrics.
8. **Next** — a `script read` command built on what §8 of `docs/scripting.md` proved (a Lua script can read the
   live document, and write to it, which is a channel the Remote Control API does not have; the remaining work is
   parsing `dump()` into the same JSON `file read` prints), a phrase that spans calls without restarting at the
   top, positional entry if Dorico ever exposes it, and `session read` gaining the same block options `file read`
   has.

Tested and settled, so no longer "next": the **unpublished commands** are blocked on the remote channel and usable
from inside (`File.New` answers `kError` with no log entry over the API, and executes through a script's
`doCommand` — 1599 ms, and a new project really opens), and the **scripting write path** works end to end
(`docs/scripting.md` §8).

Deliberately **not** planned: a cached command catalogue, on disk or otherwise. Dorico answers `getcommands` in
4 ms (98 KB, 1090 commands) and a `command list` call costs about 86 ms end to end, nearly all of it process
start-up — so a cache would save ~5 ms. Worse, the catalogue is version- and edition-specific: serving a snapshot
while Dorico is closed would hand an agent a list that the Dorico it later talks to may not have. When Dorico is
not reachable, failing is the honest answer.
