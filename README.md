# dorico-use

A JSON CLI that lets an agent steer a running Dorico through its Remote Control API. It also reads scores back
from disk. One invocation runs one action, prints a single JSON object to stdout, and exits. There is no MCP
server. An agent drives it from the shell.

Two processes work together:

- **`dorico-use`**: the CLI. It is short-lived, and stateless from the agent's point of view.
- **`dorico-use host serve`**: a long-lived process that owns the WebSocket to Dorico. It keeps the unsolicited
  status and selection stream alive between CLI invocations. The CLI starts it on demand. The `host stop` command
  is the only thing that ends it.

The `file read` command is the exception. It parses a project file on disk and touches neither Dorico nor the host.

## Quickstart

```bash
dorico-use session connect      # the first run asks Dorico's user to click Allow; the token is then stored
dorico-use session status       # score loaded, selection, note input, mode - this is your feedback
dorico-use file read --path "C:\scores\my score.dorico" --limit 20   # works with Dorico closed
dorico-use command list --family NoteInput --filter "*Pitch*"        # what can be sent, and its parameters
dorico-use note insert --notes "C5 D5 E5" --duration crotchet        # writes at the caret
```

Only `note insert` needs a project with at least one player. The `file read` command needs neither Dorico nor the
host. Every command prints one JSON object. The commands with a `--raw` option can add Dorico's own payload.

## Requirements

- Dorico runs, with **Preferences > Remote Control** enabled (port 4560 by default).
- The **first** connection makes Dorico ask its user to allow the client. The token that Dorico returns is stored
  **in plain text** in `%LOCALAPPDATA%\dorico-use\session.json`. Every later connection is authorized silently in
  about 1 ms.

## Build

```powershell
dotnet build src/DoricoUse.Cli/DoricoUse.Cli.csproj
dotnet publish src/DoricoUse.Cli/DoricoUse.Cli.csproj -c Release -r win-x64
```

The published Native AOT binary lands in
`src/DoricoUse.Cli/bin/Release/net10.0-windows/win-x64/publish/dorico-use.exe`.
It is about 8.5 MB, and it carries no `hostfxr`. The `version` command reports `nativeAot:true`. The publish is
clean, with no IL2026/IL3050 trim warnings. With `--artifacts-path <dir>` the binary lands in
`<dir>/publish/DoricoUse.Cli/release_win-x64/`, not in the `bin` tree.

**The assembly targets `net10.0-windows`, and it carries a Windows-only surface.** Reading Dorico's modal dialogs
and pressing their buttons uses Win32 and UI Automation. There is no build of this tool without it: there is no
compiler switch, and `version` reports the surface as a fact (`compatibility.uiAutomation`) rather than as a flag.
The pipe, the WebSocket and the file decoder are still plain .NET, but **a macOS port has to exclude or replace
that surface** — it is no longer a matter of adding a target framework. See [Modal dialogs](#modal-dialogs).

**Stop the host before you rebuild.** A running host holds the executable open. The build then fails with
`MSB3027 ... being used by another process`. Run `host stop` before the build, and `host restart` after it.

## Install, upgrade, uninstall

This is a single self-contained executable. Copy `dorico-use.exe` anywhere on `PATH`, or call it by full path.
There is no installer, no service, and no scheduled task. Nothing is registered anywhere.

Two things live outside the executable:

| Path | What | Removing it |
|---|---|---|
| `%LOCALAPPDATA%\dorico-use\` | `session.json` holds the connection settings and the session token Dorico granted this client, kept **in plain text**. It also holds `host.json` (the running host's pid and pipe) and `host.log`. | delete the folder |
| a running `host` process | The `host start` command leaves one background process alive by design. The connection must stay open for the status stream Dorico pushes. | `dorico-use host stop` |

**Upgrade: stop the host first.** A running host holds the executable open, so you cannot replace the file while
it runs. Run `dorico-use host stop`, replace the file, and the next command starts the new host. Nothing else
needs migrating.

## Versioning and compatibility

The tool version is SemVer, and it says nothing about Dorico. What this build was tested against is a fact. The
`version` command reports it (`compatibility.testedAgainst`, `compatibility.api`). Windows is the only platform
tested, on Dorico 6.2.30 Pro. The pipe, the WebSocket and the file decoder are plain .NET and portable; the
dialog probe and the button pressing are Windows-only and are in every build (`compatibility.uiAutomation` says so
in one sentence). A macOS port is therefore a contribution to the portable part and a replacement of that one
surface. See `CONTRIBUTING.md`.

## Commands

| Command | Purpose |
|---|---|
| `session status [--raw]` | Score loaded, selection, note input, mode, playback. This is your feedback. |
| `session info [--raw]` | Which Dorico (Pro 6.2.30) and how the host reaches it. |
| `session connect [--reset]` | Open the session and store its token. `--reset` forces the Allow dialog again. |
| `session read [--limit n] [--rebind] [--to <file>] [--measure a-b] [--player <glob>]` | What the open score really holds. The default reads the **running** document, with no save and no side effect. `--rebind` reads a saved copy instead. That copy is exact data, but Dorico rebinds the document to it. |
| `session events [--limit 20] [--payload]` | What Dorico pushed unprompted while nobody was asking. |
| `session probe [--rounds 3] [--observe-ms 1500]` | Diagnostics for the connection itself. It talks to Dorico directly, not through the host. |
| `host start [--address] [--client-name]` / `host status` / `host stop` / `host restart` | The long-lived process that owns the connection. |
| `command send --name <Name> [--param K=V]... [--wait-ms n] [--no-verify]` | Send a Dorico command. The status is read back afterwards. |
| `command list [--family X] [--filter "*Glob*"] [--limit 100]` | Search the catalogue of 1090 commands. |
| `command info --name <Name>` | One entry: display name and the parameters it accepts. |
| `project flows [--raw]` / `project layouts [--kind full-score\|part] [--raw]` | Flow and layout IDs, which several options need. |
| `project players [--expect-score n]` | The players of the **open** document. There is no protocol request for players, so this is a live read through the scripting layer: it reports each player's index, the text Dorico describes it with, and which members the layer refuses. |
| `options get --type engraving\|notation\|layout [--layout n] [--flow n] [--filter glob] [--limit n]` | The option paths and their current values (1565 engraving options in the reference project). |
| `options set --type ... --value path=value... [--layout n] [--flow n] [--no-verify]` | Set option values, then read them back to prove them. |
| `library collections` / `library entities --collection <name>` | The 50 library collections and their entities. |
| `playback techniques` / `properties get` | Expression maps and drum kit note maps. The `properties get` command reads what it can from the selection. |
| `note insert ...` | Enter notes, chords, tuplets and rests. `--from-start` writes from the top, and `--read` proves the result (see below). |
| `file read --path <project.dorico> [--limit n\|all] [--measure a-b] [--player <glob>] [--tree <name\|*>] [--dynamics] [--lyrics] [--slurs] [--articulations] [--ties] [--beams] [--techniques] [--layout] [--mark-limit n]` | Read a project file from disk: notes, dynamics, slurs, articulations, ties, beams, techniques, layout, or any subtree. No Dorico needed. |
| `export musicxml [--path <watch dir>] [--wait-ms n]` | Export MusicXML. **Needs a button pressed in Dorico's export dialog** — `ui press` does it, and the payload names the button (see below). |
| `export text --path <file.csv>` | Export project text/comments as CSV. Fully unattended. |
| `ui dialog [--raw]` | The modal dialog Dorico has up, verbatim: its title, its body text and every button with a stable id. `dialog` is null when nothing is modal. Windows only. |
| `ui panels [--panel <id>] [--raw]` | Every window Dorico has up besides its document window, and — when `--panel` names one — every element an action could target, with its role, **its current value**, its state, the container it came from and the pattern an action would use. A read that realised list rows also reports the row ceiling: see [Modeless panels](#modeless-panels). Windows only. |
| `ui press --dialog <id> --button <id> \| --panel <id> --element <id> [--value <t>] [--allow-destructive] [--allow-unknown-dialog] [--path <p>] [--timeout-ms n]` | Work one element: press a button of the dialog that is up, or toggle a check box, choose a tab, select a list or tree item, expand, or write a text field. The payload says what is up afterwards **and what the element holds now** (`worked.stateAfter`), which is how a write is verified. Windows only. See [Modal dialogs](#modal-dialogs) and [Modeless panels](#modeless-panels). |
| `version` | This build: assembly version, whether it is Native AOT, where its state lives, and the protocol constants. It starts no host and needs no Dorico. |

There is deliberately **no** command that sends an arbitrary protocol message. One existed during development, and
it is how some of the message ids below were found. It was removed before release. A tool that can post anything
can post anything at all. That includes the destructive commands.

The shared options are `--timeout-ms` (default 15000, and 120000 for `session connect`) and `--raw`, which adds the
unmodified Dorico payload. The options `--address` and `--client-name` belong to the host, because the host owns
the connection. Only `session probe` takes them directly. The option `--version` is System.CommandLine's own. It
prints a bare version string, not JSON. The `version` command is the one with the envelope.

### Sharing one Dorico

Every command acts on **whichever document Dorico has active**, and bringing any window to the front changes that.
There is no query for it, and no way to address a document. When more than one agent uses the same Dorico, that is
how a save lands in someone else's project.

So every **mutating** command (`command send`, `note insert`, `session read --rebind`, `export musicxml`,
`export text`, `options set`) takes `--expect-score <id>`. The host reads the status first. It refuses with
`WrongActiveScore` (`error.data {expected, actual}`) when the active document is not the one named. The commands
`session status` and `host status` report the ID as `activeScoreId`.

The check runs inside the same gated exchange as the action. The window between the two is therefore one round
trip, so a stolen focus can only cause a refusal. A live read (`session read`, `note insert --read`) sends the same
guard with the `Script.RunScript` command that does the reading. That script also reads whichever document is
active.

Check it the way it can fail. `session read --expect-score 999` must refuse, and so must every other command in
that list.

### Modal dialogs

Dorico stalls its **whole command queue** while a modal dialog is up, and the Remote Control API cannot see that: a
command posted behind a dialog still answers `kOK`, and then runs minutes later. Measured, with timestamps:
`NoteInput.Enter` was posted at 05:16:54.727 and executed at 05:17:18.757, twenty-four seconds later, after 取消
closed the export dialog. That blind spot is why every payload carries two more fields and why `ui` exists.

**Every payload carries `dialog`**, on success and on failure, next to the existing fields:

```json
{"ok":true,"command":"session status","status":{"…":"…"},
 "execution":{"state":"queued","evidence":"NoteInput.Enter: queued - posted, with no Executing line 1 s later: the command is on Dorico's queue, not run, which is what a modal dialog does","log":"C:\Users\<you>\AppData\Roaming\Steinberg\Dorico 6\application.log","postings":1},
 "dialog":{"id":"dorico.export-musicxml","kind":"known","shape":"qt","class":"Qt6101QWindow","owner":"0x006C1132",
   "blocksQueue":true,"title":"导出MusicXML","description":"导出到哪里\n格式","textSource":"uia",
   "buttons":[{"id":"accept","alias":"yes","name":"确定","automationId":"…buttonBox.QPushButton","index":5,"role":"default","safe":true,"invokable":true},
              {"id":"cancel","alias":"no","name":"取消","automationId":"…buttonBox.QPushButton","index":6,"role":"dismiss","safe":true,"invokable":true},
              {"id":"folder-picker","name":"\ue0c8","automationId":"…chooseFolderPathPushButton","index":4,"role":"unknown","safe":false,"invokable":true}],
   "pressWith":"dorico-use ui press --dialog dorico.export-musicxml --button cancel",
   "probe":{"how":"win32+uia","ms":12,"degraded":false,"nodes":41,"truncated":false,"doricoWindows":1,"documentEnabled":false}}}
```

- **`dialog` is `null` when nothing is modal**, and that is a measurement rather than silence: the probe ran and
  found nothing. `probe.doricoWindows` says how many visible windows Dorico had (0 would mean it is not running,
  which is a different answer) and `probe.documentEnabled` says whether its document window was enabled, which is
  the fact the modality rule turns on. It is cheap Win32 that runs on every invocation — 8 ms measured against a
  Dorico sitting at the Hub, which reported `doricoWindows: 1, documentEnabled: true`.
- **`execution` is the queue fact**: `ran`, `queued`, `refused` or `unknown`, present on a command that posted
  something to Dorico. It is read out of Dorico's own `application.log`, which echoes
  `Posting command (requested): <name>` and `Executing command: <name>`. A command that was posted with no
  `Executing` line has not run. A command Dorico refused leaves no `Posting` line at all, which is a different
  outcome and is reported as one. Both waits are bounded — a second for the `Posting` line, then a second for the
  `Executing` one — so `queued` means "posted, and no `Executing` line within a second", and `unknown` means the
  log showed neither, which is what a missing log or a second Dorico installation looks like.
- **`blocksQueue` is a hint from the window class only**, never the queue fact: the modeless library manager
  (`Qt6101QWindowIcon`) is a window a user can see while Dorico keeps working 31 ms per command, and it is reported
  as not blocking. `execution` is the measurement.
- **The text is verbatim.** The title, the body and the button labels are the strings the dialog really shows, in
  whatever language and encoding. Nothing is translated or summarised; only the **ids** are normalised.
- **The ids are stable and derived.** A button id comes from the catalogue, then from a unique automation-id tail,
  then from the classic Win32 control id (`1` is accept and `2` is cancel in a `#32770` dialog), then from a unique
  label, then from the button's position. The id reported in one invocation works in the next, unlike a positional
  snapshot.
- **`pressWith`** is a line an agent can paste without reading `--help`. It names the safest button, and for a
  dialog that is holding the queue that is also the button which releases it.

#### Answering a dialog

Two invocations, never more: act, then press.

```bash
dorico-use export musicxml --path .\export --wait-ms 120000
# its payload says export.count 0, names the dialog, and carries pressWith
dorico-use ui press --dialog dorico.export-musicxml --button accept     # or --button cancel
# -> {"pressed":{…},"dialog":null}, and the command Dorico had queued runs
```

- **The press re-probes first.** When the dialog that is up is not the one named, the press is refused with
  `WrongDialog` and nothing is pressed; when nothing is modal, `DialogNotFound`.
- **The payload's `dialog` is the state after the press**, and it is the only evidence there is. A press that
  closes its own window reported an error *after* it succeeded (`UIA_E_ELEMENTNOTAVAILABLE`), measured on the
  library manager's 关闭 button, so neither its acknowledgement nor its error is trusted: `pressed.elementGone`
  records that case, and `dialog` says what is up now. That re-probe waits for the press to have taken effect —
  measured: 取消 closed the export dialog 82 ms *after* the press returned, so an immediate probe named the dialog
  that had just been answered — up to a second, and a dialog still there after that second is reported as still
  there rather than guessed away.
- **Only measured dialogs are pressable.** The catalogue holds the dialogs whose real captions and labels were
  read — `dorico.export-musicxml`, the `dorico.folder-picker` it raises, `dorico.unsaved-changes` (discard is
  丢弃, cancel is 取消, and discard is not safe) and `dorico.crash-report`, Dorico's own crash reporter
  (向 Steinberg 发送崩溃报告, with 发送到 Steinberg, 不发送 and 不再询问; only 不发送 is safe, and all three buttons share
  one automation id, so their ids come from their labels). A dialog that is not in it is reported in full and
  refused with `UnknownDialog`.
- **Two flags, and only for a caller that means it.** `--allow-unknown-dialog` allows a button of an unmeasured
  dialog; `--allow-destructive` allows one this build does not call safe (discard, overwrite). Every button of an
  unknown dialog is unsafe — cancel included, because "cancel is harmless" is a claim about a specific dialog — so
  an unknown dialog needs **both** flags, and the payload then carries a `warning`.
- **`--allow-destructive` is a rule about buttons, and only buttons.** It answers "may I press the one control whose
  whole purpose is to commit something", and the catalogue is what makes the answer safe to give. An element worked
  through `--element` — a check box, a list row, a tab, a text field, a combo — is asked for a different reason and
  gets a different guard: the guard is the **element's own patterns**, not a safety flag. Toggling a check box
  flips a setting in a dialog the caller already opened and can see; selecting a row changes a selection; writing a
  text field puts text where the caller said. None of them commits anything by itself, and all of them stay inside a
  dialog nobody has answered yet — measured on the 重命名演奏者 dialog, where `Probe.` was written into
  `shortNameLineEdit` and 取消 then closed the dialog with 确定 never pressed: the text was in the field, and the
  field is where the payload read it. What a document holds afterwards is `file read`'s answer, not this flag's.
  And the flag would be *unusable* there: a value write has no catalogue entry to consult, so a flag asking "is this
  safe?" would have no answer to give, and a flag that is always passed is not a guard.

  What still needs the flags is the dialog itself: `--allow-unknown-dialog` for a window that is not in the
  catalogue, and `--allow-destructive` for an element that would press a button — which is exactly the case this
  build refuses to guess about. Where the line is drawn, in one sentence: **a button may be refused; a control may
  be worked.**
- **There is no third way to press a button.** Invoke, then toggle, then the legacy accessibility default action;
  an element with none of the three is refused with `ButtonNotInvokable`. This build never posts a mouse event and
  never clicks a coordinate.

Everything here is **Windows only**, and it is in every build: there is no compiler switch (see
[Build](#build)). The captions and labels above are what a Chinese Dorico shows, and on another UI language the same
dialog reports as `unknown` and is refused. That is the intended failure: read it, report it, do not guess.

#### The codes `ui press` refuses with

| Code (exit 1) | When |
|---|---|
| `DialogNotFound` | nothing is modal, so there is nothing to press in |
| `WrongDialog` | the dialog that is up is not the one named |
| `ButtonNotFound` | the id is not in that dialog now; `error.data.ids` lists the ones that are |
| `ButtonNotInvokable` | the element exposes no pressable pattern |
| `UnknownDialog` | not in the catalogue and no `--allow-unknown-dialog` |
| `DestructiveRefused` | the button is not catalogued as safe and no `--allow-destructive` |
| `PickerNotSettable` | no value pattern on the file-name box, so `--path` cannot be written into it |

A probe or press failure is exit 2 (`DialogProbeFailed`, `PressFailed`), never a refusal: a refusal is the caller's
fault and a runtime failure is not.

### Modeless panels

Dorico's panels are windows a user can see while Dorico keeps working — the library manager is one — and they are a
different species from a modal dialog. Measured, with timestamps in `application.log`: the MusicXML export dialog
disables the document window and stalls the whole command queue for 56 seconds, while the library manager leaves the
document window enabled and a command posted while it is up runs in 31 ms. Neither `IsWindowEnabled` nor `GW_OWNER`
tells the two apart on its own, and the window class does not either: the crash reporter is a `Qt6101QWindowIcon`
like the library manager and it disabled every other window of the process.

So **every payload carries `panels` beside `dialog`**, and the two never mix:

```json
{"ok":true,"command":"session status", "dialog":null,
 "panels":[{"hwnd":"0x0DEB12A0","id":"dorico.library-manager","kind":"known","shape":"qt",
            "class":"Qt6101QWindowIcon","title":"库管理","owner":null,"foreground":false,"enabled":true,
            "document":false,"dialog":false,"blocksQueue":false,
            "description":"Measured: the library manager …"}]}
```

- **A panel is never reported as `dialog`.** `dialog` keeps exactly the meaning it had: the one blocking modal that
  is up, or `null`. An agent that only knows about dialogs cannot be surprised by a panel appearing where a dialog
  was expected, and an agent that needs the library manager has a name for it.
- **`blocksQueue` is the fact this layer turns on**, and it is a hint from the window facts: a window that is up
  while the *document window is disabled* is holding the queue (`documentEnabled: false` in `probe`), and one that
  is up while the document window is enabled is not. `execution` on a command's payload stays the measurement.
- **The list is every visible top-level window** of Dorico's own process, with `document` and `dialog` flagging the
  two the probe resolved. It is cheap Win32 — **9 to 17 ms measured** — and it carries no elements, so it is on
  every invocation.
- **Reading a panel's elements is UI Automation, so it happens only when you ask**: `ui panels --panel <id>`
  reports every element an action could target — buttons, check boxes, tabs, list and tree items, edits and combo
  boxes — with a stable `id`, its `role`, its `state` and the `action` an invocation would use. Measured, on the
  library manager:

  ```
  {"elements":[{"id":"collectionFactoryCheckBox","role":"checkBox","name":"出厂设置","action":"toggle","state":"off"},
               {"id":"collectionUserCheckBox","role":"checkBox","name":"用户库","action":"toggle","state":"on"},
               {"id":"dorico","role":"checkBox","name":"Dorico 工程","action":"toggle","state":"off"},
               {"id":"button-4","role":"button","name":"关闭","action":"invoke","state":null},
               {"id":"button-5","role":"button","name":"应用","action":"invoke","state":null}],
   "elementCount":5,"readMs":30}
  ```

- **One element, one invocation.** `ui press --panel dorico.library-manager --element dorico` toggles the check
  box; `--element button-4` presses 关闭, which closes the panel — the thing this tool could not do at all before.
  The pattern is chosen by the element, never by the caller: toggle for a check box, selection for a tab or a list
  or tree item, expand or collapse for an expander, invoke for a button, and the value pattern for a text field,
  which needs `--value <text>`. An element with none of those is refused with `ElementNotActionable`, and this build
  never posts a mouse event and never types at a coordinate.
- **Every element that has a value reports it.** A field, a combo box or anything else a value pattern answers for
  carries `value`, `valueFrom` (`valuePattern`) and `valueReadOnly`, and a button, a check box or a list row carries
  none of the three — the key's presence is the fact that a value pattern answered, so `value: ""` is a field that is
  really empty and not a missing answer. Measured on the Open picker: the file-name box (`edit-1148`) read `""` and
  then the whole path that was written into it, and the file-type box read `Any Readable File (*.doricolib)`.

  ```json
  {"elements":[{"id":"edit-1148","role":"edit","name":"文件名(N):","index":70,"state":"value","action":"value",
                "value":"C:/scores/example.doricolib","valueFrom":"valuePattern","valueReadOnly":false},
               {"id":"button-4","role":"button","name":"关闭","index":80,"state":null,"action":"invoke"}],
   "elementCount":2,"returned":2,"truncated":false}
  ```

  This is the read a write is checked with, and it is why a *dialog* press reports `worked.stateAfter` beside the
  string it sent: **a write that cannot be read back cannot be verified**, and until this round a payload could only
  say `state: "value"` — that a field holds *something*, never what.
- **A read that realised list rows says so, and says it is a viewport.** See
  [the row ceiling](#the-row-ceiling-a-viewport-is-not-a-list) below: it is the one capability this round measured as
  impossible, so the payload states it instead of leaving a short list looking complete.
- **The evidence is the second read.** A press that destroys its own window reported an error after it had worked,
  measured on this panel's 关闭 button, so `worked.stateAfter` is a fresh read of the same element in the same
  invocation and the `panels` array beside it is the same fresh read of the whole window. Measured:
  `stateBefore: "off"`, `stateAfter: {"toggle":"on"}` on the 工程 check box — and on a text field, `stateAfter.value`
  is the text the field really holds after the write, so the same field verifies a write and a check box's flip.
- **A panel nobody measured is still reported in full**, with an `unknown.<hash>` id, and working one of its
  elements needs `--allow-unknown-dialog`: `PanelNotFound`, `ElementNotFound`, `ElementNotActionable`,
  `ElementNotSettable` and `ValueRequired` are the codes, all exit 1 like every other refusal.

#### Dorico's own document window is a panel too

Dorico's Setup work — the player list, the flows, the layouts, the instrument cards — lives **inside the document
window**, not in a window of its own, and its id used to be a hash of the caption, so a document rename or an
unsaved marker changed it. It now has a constant id, `dorico.document-window`, and its caption is not part of it:

```bash
dorico-use ui panels --panel dorico.document-window
#   elementCount 132, of which 59 are the rows: 30 in the players list, 1 flow, 28 layouts
```

Measured, the rows are real UI Automation list items (`ControlType.ListItem`, **50007**) and each one carries a
selection pattern, so a row can be read and chosen:

```bash
dorico-use ui press --panel dorico.document-window --element listItem-39
#   worked.element "listItem-39", name "Flute 1", action "select", stateBefore null, stateAfter {"state":"selected"}
```

##### The row ceiling: a viewport is not a list

A list view realises only the rows around its viewport, and this build cannot reach the rest. That is measured, in
full:

| List | Rows realised | Rows the document has |
|---|---|---|
| Setup players | 30 | 49 |
| Setup layouts | 28 | 45 |
| 繁衍声部格式 dialog, source | 15 | 44 |
| 繁衍声部格式 dialog, destination | 14 | 44 |

* The counts do not change when the window is minimized or restored.
* **No `ScrollPattern`** on the list view, on any of its ancestors (the whole chain was walked) or on any other
  element of the dialog; **no `ScrollItemPattern`** on a row. So there is no pattern to page with.
* The only two scrollbars in the document window belong to the **score canvas**
  (`scoreCanvasViewportScrollArea.qt_scrollarea_h/vcontainer.QScrollBar`) and carry a range value, not a scroll
  pattern. Driving the vertical one — `SetValue` 1212 → 0 → 1212 of 1574 — left the player and layout rows at 30
  and 28.

So the payload says what it saw rather than what it hopes: a read that realised rows carries

```json
{"rows": 59, "viewportLimited": true,
 "lists": [{"container":"playersListView","rows":30,"scrollPattern":false},
           {"container":"layoutsListView","rows":28,"scrollPattern":false}],
 "rowCeiling": "The rows with a `container` are the rows that list view has realised, which is the viewport and not the list. …"}
```

and every row carries the `container` it came from — which is what keeps two lists apart when a dialog shows two,
and what makes "select all" in that dialog a claim about the rows that are realised, never about every layout. The
read that sees all of it is `project players`, `project layouts` and `file read`: the scripting layer enumerates
players and layouts in full, so a decision that needs every one of them is made there, and a row outside the
viewport can neither be read nor chosen.

A row's id is derived from the rows that were realised (the automation id of a row is the list view's own path, so
the fallback rules apply), so read a panel and act on it **in the same invocation**, the way the rest of this tool is
meant to be used.

##### When Dorico has two documents open

`dorico.document-window` is resolved as the largest unowned visible window of Dorico's process. Measured with one
document open that is the document window; **measured with two, it is the wrong one**: with a scratch copy open and
active beside the maintainer's original, the payload flagged the larger, *inactive* window as `document: true` and
gave the active document's window an `unknown.<hash>` id. Both windows are readable and both are reachable —
`ui panels --panel unknown.<hash>` and `ui press --panel unknown.<hash> --element …` work on the one that is not
flagged, and acting on it needs `--allow-unknown-dialog` — but a caller that trusts `document: true` reads the wrong
document's Setup lists. Not fixed: the rule that would replace it needs its own measurement.

#### Edit fields and list rows in a dialog

A dialog's **buttons** are reported in `buttons`, exactly as before, and everything else actionable in it is reported
beside them in `controls` — an edit field, a check box, a list row, a tab — with an id, its state and the action it
takes. A control is worked with the same `--element` an element of a panel is:

```bash
dorico-use ui press --dialog unknown.1e4222903d39 --element fullNameLineEdit --value "Piccolo"
#   worked.role "edit", action "value", value "Piccolo"; worked.stateAfter / the re-read proves the field took it
```

Measured on the 重命名演奏者 dialog: `fullNameLineEdit` reads `Piccolo` and `shortNameLineEdit` reads `Picc.`, both
with a value pattern that is not read-only, and both carry their text as `value` in `controls`:

```json
{"controls":[{"id":"fullNameLineEdit","role":"edit","name":"全名：","state":"value","action":"value",
              "value":"Piccolo","valueFrom":"valuePattern","valueReadOnly":false},
             {"id":"shortNameLineEdit","role":"edit","name":"短名：","state":"value","action":"value",
              "value":"Picc.","valueFrom":"valuePattern","valueReadOnly":false}]}
```

That dialog's fields were always *visible* in `ui dialog --raw`; what was missing was an id and an action, and
`--raw` still publishes the whole walked tree under `elements` — the new array is `controls` precisely because a
field is never repurposed.

**A write is verified by the same payload that reports it**, and that is the one thing a field needed:

```bash
dorico-use ui press --dialog unknown.1e4222903d39 --element shortNameLineEdit --value "Probe."
#   worked.action "value", worked.value "Probe."  (the string that was sent)
#   worked.stateAfter {"state":"value","value":"Probe.","valueReadOnly":false,"role":"edit","name":"短名："}
#   the second read is the evidence; the first is only what was asked for
```

#### A file picker, and the library import it is for

The library manager (`库管理`) has no file control at all: its whole UI Automation tree is four levels of panes,
labels and groups around three check boxes and two buttons, and its collection rows have no name and no value in this
build. The route to a file is a **command**, and it is measured:

```bash
dorico-use command send --name Project.ImportLibrary --expect-score 2
#   dialog: id dorico.open-picker, kind known, class #32770, owner the document window, blocksQueue true
#   -> pressWith: dorico-use ui press --dialog dorico.open-picker --button cancel
dorico-use ui press --dialog dorico.open-picker --element edit-1148 --value "H:/scores/library.doricolib"
dorico-use ui press --dialog dorico.open-picker --button accept --allow-destructive
```

`Project.ImportLibrary` (and `File.Import.OtherProjectFlow`) raise the classic Windows **Open** dialog: accept is the
button with the classic control id `1`, cancel is `2`, the file-name box is an `Edit` with automation id **`1148`**
(inside a `ComboBox` that carries the same id, which is why the ids here are `edit-1148` and `comboBox-1148`), the
file filter is `1136` and the address bar is `1001`. The name box, the file filter and the search box all take a
value through the UI Automation value pattern — measured, the box read the written path back verbatim — so **a file
picker can be driven without a mouse and without typing**, which is what checklist items 2 and 11 needed.

**The picker filters to `*.doricolib`, and that is now a measurement rather than a suspicion.** The file-type box
carries a value pattern like any other field, so it can simply be read:

```bash
dorico-use ui dialog
#   controls: {"id":"1136","role":"comboBox","name":"文件类型(T):","state":"value","action":"value",
#              "value":"Any Readable File (*.doricolib)","valueFrom":"valuePattern","valueReadOnly":false}
```

So `Project.ImportLibrary` will only ever accept a Dorico **library** file. That is what the earlier round could not
settle — it wrote a project path into the name box, accepted, and Dorico reported no error while the library came
back byte-identical — and it is the reason the answer is "export a `.doricolib` from the reference project in
Dorico's library manager" rather than "point the picker at a `.dorico` file". The tool does not invent the library:
the source file is the caller's to produce.

Note what accepting means: `accept` is `safe: false` and refuses without `--allow-destructive`, because accepting an
import changes the open document. Cancelling needs no flag. The command itself does not return until the picker is
answered — measured, `Project.ImportLibrary` ran 45 to 106 seconds because nobody had answered it — so read
`execution` on the payload of whatever comes next.

**`File.Import.OtherProjectFlow` is not a safe way to look at a picker.** Measured: raising it and pressing 取消
re-activated the document (`Activating document: 2`, `Document setup and activation complete` in `application.log`)
and left the title marked unsaved (`*`) with no content change. Use `Project.ImportLibrary` when you need a picker
up, and never save a document you did not open for the purpose.

#### Which layout is current, and how to make one current

`Edit.ToggleLayoutTransposition` and `Edit.ToggleLayoutCondensing` act on the **current** layout, which is why they
looked unusable. Measured: a specific layout can be made current, and Dorico's own status carries the two ids the
command needs:

```bash
dorico-use session status
#   status.view: {"openScoreID":2,"viewContainerID":2,
#                 "switchWith":"dorico-use command send --name Window.SwitchToLayout --param OpenScoreID=2
#                              --param ScoreLayoutViewContainerID=2 --param LayoutID=<layout id>"}
```

Switching to layout 1 flipped `condensingEnabled` from `true` (its stored `enableCondensing`) to `false` and back
again, which is how the change was verified. **No payload reads which layout is current** — not `session status`,
not `project layouts --raw`, not the window title, not the Setup layout list, which marks no row selected. For a
layout's own stored settings, `options set --type layout --layout N` is the route that never needs a current layout
at all.

#### A read that follows a long command waits for it now

Dorico answers `kOK` to a command it has only queued, and four of the commands measured here take 11–55 seconds
(`Instrument.MoveToPlayer` 54.8 s, a transposition toggle 52.7 s, `AddToNewGroup` 11 s). A live read sent after one
of those used to give up after 20 seconds and report `LiveReadWroteNothing` while its own script was still on the
queue — measured, its script was posted at 10:04:50.116 and executed at 10:05:20.180, **30.1 s later**.

The read's wait now asks Dorico's log whether the queue is still busy and moves its bound out while it is, so the
same case now ends in a real answer: measured, a live read behind a file picker waited 43.9 s
(script posted 10:20:31.048, executed 10:21:14.911) and returned `ok: true, source: live, count: 44`. The bound is
**not** open-ended: 20 s while nothing is queued, and at most 180 s while the log shows work that was posted and
never executed. A read that still fails says which of the two it was, names the commands that were stuck on the
queue, and costs one invocation to retry.

Everything here is **Windows only**, and it is in every build, exactly like the modal surface.

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

- The `+` sign joins the pitches of **one chord**, that is one rhythmic position. A space still separates events.
  The option `--chord` takes a whole chord per occurrence.
- The option `--tuplet 3:2` wraps the notes of this call in a tuplet. The names `triplet`, `quintuplet` and the
  others work too.
- The option `--staff up|down` moves the caret to the other stave of the instrument first. It is **relative**. The
  API has no absolute stave addressing and no caret query, so on a piano grand stave it means "the other hand".
- The option `--mode insert|overwrite` sets `NoteInput.Mode`. It sends the command only when the status says the
  mode differs, so omitting the flag never changes anything. `overwrite` replaces the notes at the caret, and
  `insert` pushes them right. The payload always reports `mode.before` and `mode.effective`. A fresh project
  starts in overwrite.
- The option `--duration` is the **note value** (`NoteInput.NoteValue?LogDuration=`). Accepted values are
  `crotchet`, `quaver`, `minim`, `semibreve`, `breve`, `long`, `maxima`, `semiquaver`, `demisemiquaver`,
  `hemidemisemiquaver`, a dotted form (`dottedcrotchet`), a fraction (`1/8`), or a Dorico spelling. The accepted
  spellings are the ten the binary's own `LogDuration` template lists: `kMaxima`, `kLong`, `kBreve`, `kSemibreve`,
  `kMinim`, `kCrotchet`, `kQuaver`, `kSemiQuaver`, `kDemiSemiQuaver`, `kHemiDemiSemiQuaver`. Note that the *grid*
  command spells 16ths `kSemiquaver`, and both spellings exist. So `--grid kSemiquaver` and
  `--duration semiquaver` are correct as written. The option `--grid` is the rhythm grid
  (`NoteInput.SetGridResolution`), which is a different thing.
- The options `--pitch` and `--octave` use Dorico's octave numbering. **C4 is middle C** (MIDI 60), and C5 is
  MIDI 72.
- The `note insert` command switches to Write mode and turns note input on only when the status says it is needed,
  because `NoteInput.Enter` is a toggle. The option `--stay` leaves note input on.
- **Where the caret is decides where the music goes**, and the API cannot report it. The `Edit.SelectNone` command
  alone does **not** move the caret. The option `--from-start` does, by cycling `NoteInput.Exit` →
  `Edit.SelectNone` → `NoteInput.Enter`. That returns the caret to the start of the flow *and* to the instrument's
  top stave. It is the one way to write from a known place, and it is what makes a phrase repeatable. Nothing else
  moves the caret. `NoteInput.EnterAtPoint` ignores its X/Y (12 probes across four coordinate guesses). `Edit.GoTo`
  either does nothing or opens a modal 前往小节 dialog. `NoteInput.SetPosition` is accepted and ignored. Passing
  `Position`/`EventStreamID`/`InstrumentID` to `NoteInput.Pitch` is ignored too. Only the mouse addresses a point.
- **A click in the window is not a safe way to place the caret while note input is on**. Dorico treats the click
  as entering a note at that point: position from x, pitch from y, tied to zoom and scroll. Turn note input off
  first, or accept the note.
- The option `--read [--limit n|all]` writes and proves in one call. After the notes, the CLI has Dorico run the
  same read-only script that `session read` runs by default. It then parses the file that script wrote. The payload
  therefore carries the notes that are really in the score. **Nothing is saved and the document is not rebound.**
  The field `read.source` is `"live"`, and `read.*` has exactly `session read`'s shape. The option `--limit` works
  on both routes. On the live route, `0` means every note.
- The other route is `--read --read-rebind [--read-to <path>] [--limit n|all] [--measure m|a-b] [--player <glob>]`.
  It gives the same proof, and it is kept for the exact data. Dorico saves a copy inside the same gated exchange
  (`File.SaveAs`), and the CLI decodes it. The field `read.score.notes` then carries positions, MIDI numbers,
  accidentals and layout. The price is that **Dorico rebinds the open document to that copy**. The options
  `--read-to`, `--measure` and `--player` only mean anything on this route. Without `--read-rebind` they are an
  `InvalidArgument` (exit 1) that points at `session read --rebind`. A `--read-rebind` without `--read` is refused
  the same way. The value `--limit 0` means the shape alone on this route.
- **Chord input is a toggle with no query**. The command is `NoteInput.StartEndChord`, its `Set` parameter is
  ignored, and the status dumps are identical with it on and off. The `note insert` command toggles it around each
  chord group, so a chord comes out stacked unless the user had left chord input on by hand. The CLI cannot detect
  that.
- Microtones, grace notes and pasting from a file are not covered. The `command send` command reaches the commands
  behind them.

### Reading a score back

Three independent routes exist, because Dorico cannot be queried for notes.

**`session read`** reads the document Dorico has **open**, right now. By default it hands Dorico a small read-only
Lua script (`Script.RunScript`). That script walks the score, and writes what it finds to a file. Nothing is saved,
nothing is rebound, and the document is untouched. One invocation takes half a second:

```json
{"ok":true,"command":"session read","source":"live",
 "score":{"openScoreId":1,"version":"6.2.30b6245"},
 "counts":{"flows":8,"streams":824,"events":37827,"notes":4,"unparsedLines":0,"noteLimit":4},
 "kinds":{"kNoteEvent":30811,"kSlurEvent":1674,"kTextEvent":20,"kTrillEvent":45,...},
 "notes":[{"flow":1,"stream":196,"eventId":239342,"position":"0,","duration":"2","pitch":"C4"}]}
```

Rehearsal marks come from the flow's **timebase** stream, where Dorico's printer writes them, and they are counted
and listed separately from the notes:

```json
{"counts":{"flows":1,"streams":2,"timebaseStreams":1,"events":3,"notes":3,"rehearsals":1,...},
 "timebaseKinds":{"kRehearsalMarkEvent":1},
 "rehearsals":[{"flow":1,"stream":0,"eventId":9,"position":"1","text":null,
                "line":"1 (1): kRehearsalMarkEvent (id:9) at 1 "}]}
```

`text` is null because the printer prints no text for a mark: measured, the line is the position and the event id and
nothing else, and the saved file's own `RehearsalMarkEventDefinition` carries an empty property table beside them.
The letters a mark shows (A, B, AA) are the sequence Dorico computes from the rehearsal-mark options, so both routes
can count and place the marks and neither can read the letter. `counts.rehearsals` is zero for a document with none,
which is a measurement and not silence.

The field `source` says which route produced the payload, and `caveats` says what that route cannot see. The
numbers come from Dorico's own printer (`dump()`), parsed with regular expressions. So **no MIDI number,
accidental, dynamic value or layout** comes out of it. A line this build cannot read is counted in
`counts.unparsedLines`, not guessed at. The field `pitch` is the name the score prints, so it is the spelled name:
a B flat is `Bb`, not `A#`. A live read also cannot filter by measure or player, because those options belong to
`--rebind`. Use `session read --rebind`, or `note insert --read --read-rebind`, for the same proof in one call with
the write.

```bash
dorico-use command send --name Edit.SelectNone          # caret back to the top
dorico-use note insert --notes "B4" --duration quaver --mode insert
dorico-use session read --limit all                     # live: writes and proves, no save
#  ...and to see the exact data instead:
dorico-use session read --rebind --measure 1 --limit all
```

**`session read --rebind`** saves a copy, and decodes it with the same reader that `file read` uses. That is what
gives the exact data: positions, MIDI numbers, accidentals, dynamics and layout. The price is real and opt-in.
**Dorico rebinds the open document to that copy**, so autosave, backups and a later `File.Save` follow it. The
payload reports that in `save.document` and `sideEffects`. The command saves whichever document is active, so
bring the window you mean to the front first. The option `--to` defaults to a timestamped file under the system
temporary directory. It never overwrites an existing file (`WouldOverwrite`, exit 1).

**`file read`** decodes the project file itself, with no Dorico and no host, in about 0.3 s:

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
          "notes":[{"measure":1,"beat":"1","duration":"8","pitch":"D3","midi":50,"player":"Bassoon 1","stave":7,"staveOverride":null,"voice":1}]},
 "tree":{"kind":"census","nodes":140465,"leaves":440361,"names":[{"name":"xOffset","kind":"leaf","count":28346}]}}
```

A `.dorico` file is a ZIP package whose `META-INF/container.xml` names `score.dtn`. That member holds a header, a
name table, a deduplicated value pool and a token stream. The stream is a node/leaf tree with children counted, so
the parse ends exactly on the last byte. Notes come out as `flow → blocks → events →
NoteEventDefinition{eventID, pitch, position, duration}`. Position and duration are exact rationals in crotchets.
The stave is resolved through `stream → flowPlayer → voiceStreamInfos/staveStreamInfos`. Dynamics come from
`DynamicGroupEventDefinition` plus its immediate, gradual and force marks.

The option `--tree` is the escape hatch for everything else. The value `*` is a census of every node kind and leaf
key. A name dumps each occurrence with its root path and fields, and a miss returns the nearest names.

Every `file read` payload also carries **`score.playerGroups`**, the score's own groups: each one's `name`, its
`shortName` (the abbreviation a staff label prints), its file `id` and the player ids in it. Measured on a 34-player
project: Woodwinds/Ww., Brass/Br., Percussion/Perc., Strings/Str. and Band/Bnd. That is the read the engraving
checklist's group item needs, and it is the only one there is. Measured again, harder: `Project.PlayerGroup.Rename`
**is refused outright** (`kError kCommandNotAllowed`, and no `Posting` line in its log) even with both of its
required parameters — `PlayerGroupID` set to the id `file read` reports for a group the tool itself created — and
`Project.PlayerGroupRenameShowDialog` either refuses the same way (with a `PlayerGroupID`) or runs and raises **no
window at all** (with the group row selected in the Setup player list). The player route, by contrast, works: with a
player row selected, `Project.PlayerRenameShowDialog` with no parameter raises 重命名演奏者 with both fields
prefilled. So a group's name and abbreviation stay manual, and `file read` is what verifies them.

**`score.players[].soloPlayer`** is the other half of a player's identity, and the one a Sibelius import gets wrong:
each player carries the file's own `soloPlayer` leaf (read raw) beside **`playerType`**, this build's name for its two
values — `solo` for a solo (single) player and `section` for a section player, derived from the pair of commands
Dorico's own catalogue publishes (`Project.SoloPlayer.New` against `Project.SectionPlayer.New`). Measured on a
44-player orchestral import: 40 solo players and 4 section players, and the four are Violin I, Viola, Violoncello and
Contrabass — so Violin II, which came in as a solo player, is the one that stands out. It is **not** `isSoloist`:
that flag (`Project.Player.SetAsSoloist`) is the soloist-in-front-of-a-section mark, and all 44 players of that file
wrote it `false`. The mapping is an inference from the leaf's name and from that distribution rather than a
certificate, so both the raw leaf and the name are reported, and the caveat says so.

The other blocks read the same way. Each one states in its own `caveats` what it rests on:

| Option | What it reads | Verification |
|---|---|---|
| `--dynamics` | 754 events, 305 groups, `cresc`/`dim`/`f`/`pp`… | Every mark, position and measure matched Dorico's MusicXML export (325 markings, 0 differences). |
| `--slurs` | 987 slur events, 438 elements, noteheads per slur | The (start, length) pairs match the export's 214 `<slur>` pairs exactly. |
| `--articulations` | 7839 marks (Staccato 3914, Accent 3576, Tenuto 349) | Matches the export's `<articulations>` counts. The names come from the score's own library. |
| `--ties` | What *is* stored: LV ties, tie-to-event, split values | The export's `<tied type="let-ring"/>` count matches (17). |
| `--beams` | The 16 beam properties (Bad Apple) / 17325 (another file) | Single source. No corpus file has both. |
| `--techniques` | 35 technique events + 45 note techniques, with ids, texts and names | Single source. The appearance → technique → name chain is the file's own library. |
| `--layout` | Page box, margins, casting off, page sets and frames, plus each layout's `transpositionType` and `condensing` | Page size and margin match the export's tenths exactly. Measured for the two view-state fields: a project's full score wrote `kScoreInC` and its part layout `kTransposingScore`; toggling that layout's transposition in Dorico (`options get --type layout --filter transpositionType` reads the new value live) changed exactly that field in the saved file, and toggling condensing wrote `condensing: true` on that layout and left the part layout `false`. |

The reader was verified against independent MusicXML exports and against 20 project files from Dorico 6.0.0
through 6.2.30. The limits follow, and the payload reports them as `caveats`.

**Pitch spelling is a letter, and it is now a field. The accidental is derived from it.** The project stores a MIDI
number plus a per-note spelling *letter* (`kNoteSpelling`), and no accidental. Each note therefore carries
`spelling`, `accidental` and `accidentalSteps`. MIDI 82 spelled `B` is a B flat. The derivation is checked against
Dorico's own MusicXML export, its `<alter>` against `accidentalSteps`, note for note. The result is **6968 of
6968** in the 13 concert-pitch parts of the reference project, with `tools/verify_accidentals.py`.

The reader **withholds the accidental where the two inputs cannot agree**. That happens on a transposing
instrument, whose written and sounding pitches differ by the interval the file states at
`instrument/transposition/intervalForTransposingScores` (13 instruments in the reference score). It also happens on
unpitched percussion, where the MIDI value is a drum-map position. 11387 of the reference score's 16002 notes get
a derived accidental, and 4615 get null. The flats (1171 of them) are exactly what the sharp-only `pitch` name
cannot express. Grace notes carry a spelling letter too, but they are a separate event kind that this reader does
not list.

**Ties and beams are not events.** A tie chain is one note event with the sounding duration. The beaming a score is
printed with is computed at layout time. No corpus file has both beam properties and an export.

**Cross-staff notation is stored, and it is now a field: a note's `staveOverride`.** It is the note event's own
`logicalStaveIDOverride`, null when the file states none (the leaf is `-1`). Measured on a copy of the corpus fixture,
whose Piano part has two staves: every note read null; `Edit.SelectAll` in Write mode sets `status.hasSelection`
(**so a note selection needs no mouse**), `Edit.CrossStaffBelow` then wrote `1` on the two notes that were on the
upper stave and left the one on the lower stave alone, `Edit.CrossStaffAbove` wrote `0` on all three, and
`Edit.CrossStaffReset` took all three back to null — while each event's own `stave`, `voice` and `stream` stayed
exactly as they were. Reading `0` and `1` as the logical stave id (0-based) is an inference from those three commands
on one two-stave instrument, and the payload's caveat says so. Checklist item 12 is therefore a CLI item:
`Edit.SelectAll` → `Edit.CrossStaffBelow|Above|Reset` → save → `file read`, and `staveOverride` is the answer.

**A click in the window is the only way to address a point**, for everything else. Anything about where music sits on
a page (frame geometry → measure mapping) is therefore not derivable here. Casting off is a layout computation, and
the file's own cache of it is empty.

**Lyrics are read, and that reading was checked against a score that has them.** The non-empty path was written
from the *binary's* vocabulary (`LyricElementDefinition`, `LyricEventDefinition`, `elementID`, `text`,
`kLyricSyllableType`, …), with no file to try it on. Then one file settled it. A score with 30810 notes and 43
players holds **8 lyric tables, 4710 elements and 4710 events**, and the guesses held. The id and the text sit in
`elementID` and `text`. A syllable carries `syllableType: kWholeWord`, `lineType: kVerse` and
`lineLocation: kBelow`, and the first two syllables are the ones that were typed (`A-`, `lice`). Text outside
Latin-1 survives, because the score carries Chinese lines. Every event resolves to a real measure, beat and stave
(event 129187 → measure 6, beat 2, element 954, stave 834). The link is 1:1 across the whole file: 4710
`LyricElementDefinition` against 4710 `LyricEventDefinition`. The `ProjectFileCheck` program asserts exactly that
in **15 checks**, and they run wherever that file is. Where it is not, the block reports a single `skip`, which is
what CI sees. The option `--lyrics` still returns `read`/`events`/`fields` plus **5 caveats** that separate what is
measured from what is inferred.

Two things stay unverified. The first is **which** lyric line is a verse and which is a translation, because the
checks only pin that the lines are told apart. The second is that no other file with lyrics has been read. Every
other file of the reference corpus still answers `elements: 0`.

- **Rests are gaps**, not events, and **measures are derived** from the stored metre/bar-division events.
- Not modelled: the score library's engraving options, frame → measure mapping, playback/mixer data and tremolo.
- `scorelibrary.dtn` and the other supplementary members are listed but not parsed.

**`export`** asks Dorico to write a file. The `export text` command is unattended:
`File.Export.Text?File=...&Show=false` takes about 20 ms, writes a CSV, and does nothing at all if the folder is
bad. Every other filter, including `MusicXMLExportFilter`, answers `kOK` and then opens a **modal dialog**. That
dialog writes nothing until 确定 is pressed:

```bash
dorico-use export musicxml --path .\export --wait-ms 120000   # waits, watching --path
# Its payload reports the dialog that is up, its buttons and pressWith. Answer it in one more command:
dorico-use ui press --dialog dorico.export-musicxml --button accept
```

The dialog has a path label (not editable) and a "..." folder picker, and it ignores any `File=` parameter, so the
destination can only be set through that picker — `ui press --dialog dorico.export-musicxml --button folder-picker`
opens the native `选择文件夹` window, which the next payload names as `dorico.folder-picker`. See
[Modal dialogs](#modal-dialogs).

While that dialog is open, Dorico's whole command queue stalls. Queued commands then run late, and their acks and
statuses are stale. The `export musicxml` command therefore reports the files that really appeared, and returns
`ExportNeedsConfirmation` when none did. An export that produced no file is not reported as success.

### The agent loop

```bash
dorico-use session status                                     # where am I?
dorico-use project layouts --kind part                        # which layout IDs exist?
dorico-use command list --family NoteInput --filter "*Pitch*" # what can I send, with which parameters?
dorico-use note insert --notes "C5 D5 E5" --duration crotchet
dorico-use file read --path out.dorico --measure 1-4          # what is actually in the score now?
dorico-use export text --path out.csv                         # or read text/annotations back
```

A `kOK` answer is a receipt, never proof, so everything that changes the score reads the status back. For note
entry that is still not enough, because status reports the caret and the selection, not which notes landed. The
honest check is `file read` after a save, or a look at the window.

## JSON contract

Success puts the payload fields at the top level, so nothing has to be unwrapped:

```json
{"ok":true,"command":"note insert","note":{"duration":"kCrotchet","enteredWriteMode":true,"chords":[["C5","E5","G5"]],"steps":[{"name":"NoteInput.Pitch","ack":"kOK"}],"status":{"hasScore":true}}}
```

Failure names a stable code, and the exit code separates cause from usage:

```json
{"ok":false,"command":"file read","error":{"code":"NotAContainer","message":"..."}}
```

When a failure has facts worth reading, they are in `error.data`, in the same shape the success payload would
have. An agent then never has to parse the sentence:

```json
{"ok":false,"command":"export musicxml","error":{"code":"ExportNeedsConfirmation","message":"…","data":{"export":{"pathRole":"watch","checks":1,"destination":{"path":"…/Documents/Dorico 工程","key":"defaultProjectPath"}}}}}
```

The exit codes are `0` for success, `1` for an argument or usage error, `2` for a protocol, connection or Dorico
failure, and `3` when a host was already running and `host serve` was asked to start.

Three more fields can sit beside `ok`/`command`, success and failure alike: `dialog` is on every payload — the modal
dialog Dorico has up, or `null` — `panels` is on every payload and lists Dorico's other windows, and `execution` is
on the payloads that posted a command to Dorico, because what it says is whether that command actually ran. All
three come from the same funnel every command goes through, so no command can omit them. See
[Modal dialogs](#modal-dialogs) and [Modeless panels](#modeless-panels).

## What is true about Dorico's API

This project learned the protocol shapes recorded here from
[Dorico.Net](https://github.com/scott-janssens/Dorico.Net) (MIT). That is the community library which documented
Dorico's Remote Control API first, and this project cross-checked the shapes against it. This project reimplements
a much smaller client instead of depending on that library. The enum spellings it validates come from the same
source.

All of the following was verified against Dorico Pro 6.2.30 on Windows 11:

- The WebSocket is at `ws://127.0.0.1:4560`, with one JSON object per message. **Answers come back in request
  order.**
- Requests: `connect`, `acceptsessiontoken`, `disconnect`, `getstatus`, `getappinfo`, `getcommands` (`commandlist`),
  `getflows` (`flowslist`), `getlayouts` (`layoutslist`), `getoptions` (`optionslist`), `setoptions` (`response`),
  `getproperties` (`propertieslist`), `getplaybacktechniques` (`playbacktechniqueslist`),
  `getlibrarycollections` (`librarycollectionslist`), **`getlibraryentities`** (`libraryentitieslist`), `command`.
- Dorico pushes messages nobody asked for, such as `status` and `selectionchanged`. A status push is a **partial
  patch**, and so is a `getstatus` answer sent while a modal dialog is up. Every status is therefore merged into
  the last known one. The host collects this stream, and 24 patches arrived during a single mode switch.
- The `command` message is the workhorse: `{"message":"command","command":"Name?Key=Value,Key2=Value2"}`. Dorico
  6.2.30 Pro exposes **1090** commands (`Edit.*` 228, `NoteInput.*` 129, `Project.*` 114, `Play.*` 107, ...).
- A `kOK` answer is a **receipt, not proof**. `File.Open` with a bad path still answers `kOK`, and then fails
  inside Dorico.
- Note entry: the command `NoteInput.Pitch?Pitch=F,OctaveValue=5` enters a note and advances the caret by itself.
  Never send `MoveAdvance` after it. The command `NoteInput.MoveAdvance` inserts a rest. A call to
  `NoteInput.NoteValue?LogDuration=kCrotchet` sets the note value as an enum spelling, and an unknown value
  silently becomes a semibreve. The command `NoteInput.SetGridResolution` sets the grid. A call to
  `NoteInput.SetAccidental?Type=kSharp` arms an accidental. A call to
  `NoteInput.Mode?NoteInputMode=kInsert|kOverwrite` sets insert or overwrite, the status reports `noteInputMode`,
  and the setting follows the application rather than the document. The command `NoteInput.StartEndChord` toggles
  chord input, and `NoteInput.Enter` toggles note input. The commands `NoteInput.MoveUpTop` and
  `NoteInput.MoveDownBottom` move the caret between the staves of one instrument.
- Saving and closing: `File.SaveAs?File=<path>` is **headless**. It writes the copy, and then rebinds the open
  document to it. The command `File.Save?File=<path>` ignores the path and saves in place, so it can only ever
  overwrite the user's own file. The commands `File.Close?OpenScoreID=<id>` and `File.CloseProject` **do close the
  document**. An earlier note here said otherwise, because those commands had been queued behind a modal dialog
  and only ran minutes later.
- **A modal dialog stalls the whole command queue, and queued commands run later, out of sight.** A queued command
  still answers `kOK` when it is posted, so an ack says nothing about when the command ran. While an export dialog
  was open, a `File.Close` queued earlier closed a document minutes afterwards. Never queue something destructive.
  When a command seems to do nothing, look in `application.log` for when it really ran.
- There is **no query for bars, staves, the note stream or the project at large**, no absolute stave addressing and
  no caret query. There is also no way to create a project through the **published** API. `File.New` is absent from
  the catalogue, although the application carries it (bound to Ctrl+N). The application also carries 161 further
  names the API never publishes, including the whole `Debug.*` and `Custom.*` families (see
  `docs/hidden-commands.md`). Whether those execute when sent was deliberately not tested.
- Export: `File.Export?FilterID=<filter>` with `MusicXMLExportFilter`, `MIDIFileExportFilter`,
  `AudioFileExportFilter`, `DoricoFileExportFilter` or `TempoTrackExportFilter`. All of them are dialog-bound,
  except `File.Export.Text?File=...&Show=false`, which is not. The dialog ignores any `File=` parameter. Its 导出到
  label shows the **folder of the document's own file**, or `defaultProjectPath` from
  `%APPDATA%\Steinberg\Dorico 6\preferences.xml` for a project that has never been saved. A choice made in its
  `...` picker lasts for that one export. While the dialog is open, Dorico's commands are queued, not run, and the
  API cannot tell you a dialog is up. The `getstatus` answer comes from the modal loop.
- The command `Script.RunScript?ScriptPath=...&ShowConsole=false` runs Lua 5.2 in-process with `io` and `os`.
  Anyone who can reach the Remote Control port can run arbitrary code as the user, so treat that port as full
  trust.
- The field `displayName` is localized (a Chinese UI returns `输入音高` for `NoteInput.Pitch`), and `name` is the
  stable key.
- The `session probe` command measures the useful part: the token survives a disconnect, so a connection per
  invocation costs about 1 ms. The host exists for the event stream and for serialising access, not to save
  handshakes.
- The best documentation is `application.log` (`%APPDATA%\Steinberg\Dorico 6`). Every action Dorico performs is
  echoed there as the command and parameters that produce it. That log, the command catalogue and the XML files
  Dorico itself exports are how every fact above was established. A generic send-any-message command is not
  shipped.

### Beyond the published API

The application carries far more than the remote API publishes. Two of the three additions here come from reading
that surface rather than from the protocol. The repo ships the **extractors, never the extracted data**.

**Enum validation.** Dorico answers `kOK` to an unknown enum value and silently substitutes another, so an unknown
`LogDuration` becomes a semibreve. A typo in `--param` would therefore stay invisible until you heard the wrong
music. The values of the parameters this build knows are **built into the CLI**: the note values, the note-input
modes, the window modes, the grid resolutions, the accidental types and the option types. The command
`command send --param LogDuration=kQuavor` is refused before anything is sent, with the nearest spellings in the
message and in `error.data` (`kQuaver`). The option `--allow-unknown-enum` sends it anyway when you mean it.
Nothing has to be installed, extracted or kept up to date for this to work.

**Only those parameters are checked.** A value passed to any other parameter goes to Dorico unchecked. A spelling
this build has never heard of is far more likely to be something a newer Dorico accepts than a typo, and refusing
it would break working commands. The payload always says what happened: `vocabulary.source` is `"built in"`, and
`vocabulary.lookedAt` lists each value that was checked and the verdict.

**Unpublished commands.** `docs/hidden-commands.md` diffs the binary's command-name pool against the live
catalogue: **1191 names in the application, 1029 of them published, 162 not**. That includes `File.New` (Ctrl+N),
`File.NewFromTemplate`, `Play.NavigateForwards` and the entire `Debug.*` and `Custom.*` families. Nothing was sent
to Dorico to see whether they execute.

**The scripting layer.** `docs/scripting.md` records what Dorico's Lua console really exposes, measured. The
recorder is exactly a keystroke log: a recorded macro is three `app:doCommand([[EventEdit.NavigateDown]])` lines.
The object model behind it is real but half-built. The expressions `score.flows/players/layouts` and
`EventStream:dump()` read fine, and the convenient getters answer `Not implemented - this may leave the score in an
unpredictable state @ streamwrapper.cpp:162`. The community's verdict ("scripting has no meaningful access to
score data") describes the front door, not the layer. A script **can write**.

`docs/scripting.md` §8 has the verified run. A script built its arguments (`RhythmicLength(0)`,
`MIDIPitchWithSpelling(MIDIPitch(60), NoteName.fromString('C'))`), and called `stream:createNote(...)`. The
`session read` command then found the note in the file Dorico wrote, field for field: event id, measure, position
0, duration 2, C4, spelling C, Flute 1, stream 196. The score went from 30810 to 30811 notes.

The catch is that nothing there is documented or safe. Probing the constructors blind **crashed Dorico once**. The
program `tools/analyze_minidump.py` reads the dump, and §8 carries the ACCESS_VIOLATION inside the C++ exception
machinery. The write path is real, but you have to walk it one zero-argument call at a time.

## Architecture

```
agent shell ──> dorico-use <command>  ──JSON lines over \\.\pipe\dorico-use-<user>──>  dorico-use host serve
                     │                                                                          │
                     └── file read ──> project file on disk                     └──WebSocket──> Dorico
```

- One JSON object per line, in both directions. The CLI owns the agent-facing envelope (`ok`/`command`/`error`).
  The host answers `{"ok":true,"payload":{...}}` or `{"ok":false,"error":{...},"exitCode":n}`.
- The host connects lazily on the first request that needs Dorico, and keeps the connection open. It reconnects
  once if Dorico drops it. Requests pass through one gate, because Dorico pairs answers with requests in order.
- The host announces itself in `%LOCALAPPDATA%\dorico-use\host.json` (pid, pipe, settings) and writes `host.log`.
  The `host stop` command asks it to stop, and then kills it by pid if it does not.
- The host starts through the shell, never as an inherited child. A child holds the caller's stdout open, and
  whoever reads that pipe (an agent's shell) would wait for EOF forever. The command looks hung, although the CLI
  has exited.

## Tests

```bash
dotnet build tests/ProjectFileCheck/ProjectFileCheck.csproj -v q --nologo
./tests/ProjectFileCheck/bin/Debug/net10.0/project-file-check.exe
```

The `ProjectFileCheck` program compiles the reader's sources **and the view that renders them**. Seventeen of its
checks run against `tests/corpus/fixture.dorico`, a small project committed with the repository. It holds one Piano
player, two staves and three notes: a B flat, an F sharp and a C. Those checks exercise the decoder for real in
continuous integration. The rest assert facts about the maintainer's own scores, which are not in the repository,
and they report `skip` when you do not have them.

It asserts the three tag markers seen in the corpus, and a note run cross-checked against a MusicXML export. It
checks the dynamics histogram and positions against Dorico's own export of two pieces, and slurs, articulations,
ties and layout against another. It matches the per-note spelling letter onset by onset against a third (8476
notes, 0 differences, with the export's `<transpose>` table asserted). It checks score-order and overlap
invariants, and compares the generic tree walker with the typed reader. It proves that the report can be built
three times in one process (a shared node parented twice throws), and it covers every error path (`NotFound`,
`NotAContainer`, `MissingScoreData`, `MalformedMember`, `UnsupportedScoreData`).

187 checks run, and none is skipped on a machine that has the maintainer's scores. The ones that need a file which
is not in the repository report `skip` instead, which is what CI sees. The lyric block, for instance, is 15 checks
reported there as a single `skip`.

```bash
dotnet build tests/VocabularyCheck/VocabularyCheck.csproj -v q --nologo
./tests/VocabularyCheck/bin/Debug/net10.0/vocabulary-check.exe    # 327 checks, no setup at all
```

The `VocabularyCheck` program needs nothing but the framework: no Dorico, no host, no extraction step. It checks
the table against the code that uses it, so the note values the CLI resolves are exactly the ones it validates. It
checks the shape rule, the refusal and its suggestions, and the one behaviour that matters for forward
compatibility: a value for a parameter this build does not know is never refused.

It also compiles the two pure files of the dialog model (`Ui/DialogModel.cs`, `Ui/DialogCatalogue.cs`) by hand and
asserts the rules an agent depends on without a dialog, a COM pointer or Windows: the id of every button of the
measured export dialog (two of its seven share a label and one has none), three times over, so stability is a check
and not a hope; the classic control ids of a native `#32770` dialog; the unknown-dialog hash; the position
fallback; every catalogue entry's roles and safety; and that none of the reserved ids has an entry, because an
entry needs a measurement and a check refuses to let one be guessed. That asymmetry is why the CLI's Windows-only
code lives under `Ui/` in files this project does not compile.

## Troubleshooting

| Symptom | What it is |
|---|---|
| `NotReachable` on every live command | Dorico is not running, its `Preferences > Remote Control` server is off, or it listens elsewhere (set `--address` on `host start`). `file read` and `version` still work without Dorico. |
| The build fails with `MSB3027 ... being used by another process` | The host runs from that binary and holds it open. Run `dorico-use host stop`, build, and the next command starts the host again. |
| A command seems to do nothing | Something is modal in Dorico. While a dialog is open, Dorico **queues** commands instead of running them. Each one still answered `kOK` when it was posted, so they run minutes later, out of sight. The payload says so: `execution.state` is `queued`, and `dialog` names the dialog and carries the `pressWith` line that answers it. When `execution` is absent or `unknown`, read `%APPDATA%\Steinberg\Dorico 6\application.log` for the `Posting` and `Executing` lines. |
| `WrongActiveScore {expected, actual}` | The `--expect-score` check refused, because the document Dorico has active is not the one you named. Bring the right window to the front, or drop the flag. |
| `kOK` but nothing happened | `kOK` is a receipt, not proof, and a bad `File.Open` path answers it too. Every command that changes the score reads the status back. For note entry, the honest proof is `session read`. |
| `session read` changed which file my document points at | Only `session read --rebind` does that, because `File.SaveAs` rebinds the open document to the copy it writes. The default read saves nothing and has no side effect. If you did use `--rebind`, re-open the original file afterwards. The copy is under your system temporary directory. |
| Dorico crashed while running a script | The scripting layer (`Script.RunScript` via `command send`) is undocumented and not defensive. Building its C++ argument objects blind has crashed Dorico here. See `docs/scripting.md` §8 before using it. |
| `file read` reports `NotAContainer` / `UnsupportedScoreData` | The file is not a Dorico project, or it is a newer format than this build knows. The reader refuses rather than guessing. |
| How do I revoke this client? | Delete `%LOCALAPPDATA%\dorico-use\` and remove the client in Dorico if it is listed there. The session token Dorico granted this client sits in `session.json` there, in plain text. The `host stop` command does not remove it, but deleting the folder does. |
| I only wanted to check something, and Dorico now says the document is unsaved | **`File.Save` writes whatever is unsaved, not just what you changed**, so a save "to verify something" can commit somebody else's arrangement. Observed on a 44-player project: the project file as imported had its 45 layouts and its player table in numeric-id order, and the file after a save had them in the document's own voice order, with every player and layout record otherwise identical field for field and no move command anywhere in `application.log` from Dorico's start. **Not established:** which command put the order there — `Project.Layouts.Renumber` was run in that session and is one candidate, "the save simply writes the document's order" is the other. The consequence is what matters: treat `File.Save` as a real edit, never as a probe. `--expect-score` and a throwaway copy are the way to be sure; a command that only reads (`session status`, `session read`, `project layouts`, `file read`) saves nothing. |
| A command I ran left the document marked unsaved without changing it | Measured: raising `File.Import.OtherProjectFlow` and cancelling its file picker re-activated the document (`Activating document: 2` in `application.log`) and set the unsaved marker, with no score command sent and nothing changed in the music. Use `Project.ImportLibrary` if you need a picker up. There is no way to clear the marker without saving, and this tool will not save a document it did not open for the purpose. |
| Dorico vanished while I was working a dialog | Measured: `Project.PlayerRenameShowDialog?PlayerID=<n>` **while the rename dialog is already up** kills Dorico 6.2.30. The log's last line is that command's `Executing` line with no `notifyPostCommandExecute` after it, `%LOCALAPPDATA%\CrashDumps\Dorico6.exe.<pid>.dmp` appears, and the next start shows Dorico's own crash reporter (`向 Steinberg 发送崩溃报告`, catalogued as `dorico.crash-report`; 不发送 is the safe button). One raise of that command at a time — and the same shape as the `Project.Player.SetAsSoloist` crash above: a second call into a dialog that is already open. |
| Dorico offered to recover autosaves after that | Its `自动保存工程恢复` dialog lists the autosaves and says in its own text that **any autosave not selected for recovery will be deleted**. It has 放弃全部 and 恢复选定工程 and **no cancel**, so answering it is a decision, not a dismissal. It is not in the catalogue: it takes both `--allow-unknown-dialog` and `--allow-destructive`, and both of its buttons are `safe: false` for that reason. Nothing here picks for you — but read the list before you press, and prefer recovering what you may need over discarding it. |
| `ui panels` named a window as the document that is not the active document | With two documents open, `dorico.document-window` resolves to the largest unowned window, which was measured to be the **inactive** one. The active document's window is reported with an `unknown.<hash>` id instead and is reachable with `ui press --panel <that id> --allow-unknown-dialog`. See [When Dorico has two documents open](#when-dorico-has-two-documents-open). |

## Roadmap

1. **Done**: lean WebSocket client (request/response pairing, status merging), session store, `session` group.
2. **Done**: host process, pipe protocol, `host` group, `command send|list|info`, `session events`.
   A raw sender existed here, and it was removed before release.
3. **Done**: `project`, `options`, `library`, `playback`, `properties`.
4. **Done**: `note insert` with chords, tuplets and relative staff targeting, `file read` (the project-file
   decoder), and `export musicxml`/`export text`.
5. **Done**: insert/overwrite control, `session read` (save a copy and read it back in one command), the tree
   escape hatch and dynamics in `file read`, and an honest export destination report.
6. **Done**: `--expect-score` (refuse to act on the wrong document, on every mutating command), `note insert
   --from-start` and `--read` (write and prove in one call), the per-note spelling letter as a field, a `version`
   command, a proven Native AOT publish, and slurs, articulations, ties, beams, techniques and layout in
   `file read`.
7. **Done**: the vocabulary extractor with enum validation, the unpublished-command diff
   (`docs/hidden-commands.md`), the scripting-layer analysis (`docs/scripting.md`), and lyric reading checked
   against a score that has lyrics.
8. **Next**: a `script read` command built on what §8 of `docs/scripting.md` proved. A Lua script can read the
   live document and write to it, which is a channel the Remote Control API does not have. The remaining work is
   parsing `dump()` into the same JSON that `file read` prints. Also next: a phrase that spans calls without
   restarting at the top, positional entry if Dorico ever exposes it, and `session read` gaining the same block
   options as the `file read` command.

9. **Done**: the modal-dialog surface — the `dialog` field on every payload, the `execution` verdict beside it,
   `ui dialog`, and `ui press` with a measured whitelist, two safety flags and stable derived button ids.
10. **Done**: the file picker. `Project.ImportLibrary` and `File.Import.OtherProjectFlow` raise the classic
   Windows Open dialog, and it is catalogued as `dorico.open-picker`: accept is the button with classic control id
   `1`, cancel is `2`, and the file-name box is an `Edit` with automation id `1148`, settable through the value
   pattern. The library manager turned out to have no file control at all, so this is the route for a library
   import — see the panel section.

11. **Next**: the rest of the dialog catalogue. Four dialogs are in it because their captions and labels were
   actually read, and four more are named in the code as reserved (`dorico.allow-remote-client`,
   `dorico.import-musicxml`, `dorico.save-picker`, `dorico.overwrite-file`) and have **no** entry, because an entry
   needs a measurement and a check refuses to let one be guessed. Until they are read they are reported in full and
   refused, which is safe but not useful.

12. **Next**: a list view's unrealised rows. Dorico's Setup lists expose only the rows around their viewport (30 of
   49, 28 of 45 measured, and 15 and 14 of the 44 in the 繁衍声部格式 dialog's two lists) and carry no scroll
   pattern on the list, on any ancestor or on a row, so this build can read and choose what is shown and no more.
   The payload now says so for every read that realised rows (`lists`, `rows`, `viewportLimited`, `rowCeiling`), and
   the scripting layer can enumerate a project's players and layouts in full, which is why `project players` and
   `file read` exist; acting on a row that is not realised would need the list to scroll, and nothing measured so
   far scrolls a Qt list from UI Automation.

13. **Next**: the row-ceiling work a later round could still do. The ceiling is reported now, not removed. The two
   routes not measured to the end are (i) an in-place edit of a list row, which a Qt delegate might expose as a value
   pattern on the row itself — measured, a Qt list row does expose a value pattern, but this build does not ask it
   for one, and nothing has shown that writing there edits the row — and (ii) the scripting layer, which reaches
   every player and layout but acts on none of the Setup trees. Both would need a measurement on a real list before
   this build could promise anything.

Two things are tested and settled, so they are no longer "next". The **unpublished commands** are blocked on the
remote channel and usable from inside. Over the API, `File.New` answers `kError` with no log entry, and it
executes through a script's `doCommand` (1599 ms, and a new project really opens). The **scripting write path**
works end to end (`docs/scripting.md` §8).

One thing is deliberately **not** planned: a cached command catalogue, on disk or otherwise. Dorico answers the
`getcommands` request in 4 ms (98 KB, 1090 commands). A `command list` call costs about 86 ms end to end, and
nearly all of it is process start-up. A cache would save about 5 ms. Worse, the catalogue is version- and
edition-specific. Serving a snapshot while Dorico is closed would hand an agent a list that the Dorico it later
talks to may not have. When Dorico is not reachable, failing is the honest answer.
