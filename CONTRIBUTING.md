# Contributing

Thanks for looking. This is a small tool with one rule that matters more than the rest: **a claim about Dorico is
worth nothing until it is measured.** Everything in `README.md` and `docs/` that says "verified" or "measured" was
checked against a running Dorico, against a file Dorico wrote, or against an export Dorico produced — and the
numbers are in the text. Keep it that way.

## Getting started

```bash
dotnet build src/DoricoUse.Cli/DoricoUse.Cli.csproj            # 0 warnings is the bar
dotnet build tests/ProjectFileCheck/ProjectFileCheck.csproj
dotnet build tests/VocabularyCheck/VocabularyCheck.csproj
./tests/ProjectFileCheck/bin/Debug/net10.0/project-file-check.exe
./tests/VocabularyCheck/bin/Debug/net10.0/vocabulary-check.exe
```

**Stop the host before rebuilding** (`dorico-use host stop`): a running host holds the executable open and the
build fails with `MSB3027 ... being used by another process`. This bites everyone once.

The style is in `.editorconfig`; the CLI project is `net10.0-windows` and each check project is `net10.0`; XML
comments explain *why* rather than *what*, so a reader can tell a measured fact from an inference.

The three files that used to be too long to read are now sets of `partial` class parts, split by concern rather
than by size: `HostServer` keeps the server and the dispatch, `HostServer.Notes.cs` the note entry,
`HostServer.Score.cs` the score reads; `ProjectFileScoreReader` keeps the decoder's core and
`ProjectFileScoreReader.Walks.cs` the tree walking; `ProjectFileView` keeps the report and its helpers and
`ProjectFileView.Marks.cs` the dynamics, slurs, articulations, ties, beams, techniques and layout. Adding a part
means adding the file **and** the compiler item in the test project that compiles those sources by hand.

## Building with the unsafe surface

`command send` refuses any name in the `Script.` family: that layer is undocumented and probing it has crashed
Dorico here (`docs/scripting.md` §8). The refusal is compile-time, not policy — `UnsafeSurface.ScriptRunExposed` is
`false` unless the build defines `EXPOSE_UNSAFE_SCRIPT_RUN_API` — and only a build that asks for it gets the
capability back:

```powershell
dotnet build src/DoricoUse.Cli/DoricoUse.Cli.csproj -p:ExposeUnsafeScriptRunApi=true
```

That is for working on the scripting layer against a scratch document. **A release build must not pass it:**
`release.yml` refuses to publish a binary whose `version.build.exposeUnsafeScriptRunApi` is not `false`, and
`ci.yml` compiles the check project both ways so the flag cannot quietly rot.

## Building without the stored session token

By default the token Dorico grants is written to `%LOCALAPPDATA%\dorico-use\session.json`, in plain text, which is
what makes the second run cost a millisecond instead of a dialog. `-p:NeverPersistSession=true` builds the other
one: the token lives in the host's memory only, `session.json` keeps no token, a leftover from an earlier build is
ignored (and named in `version.state.session.token`), and each `host start` — or the first command after Dorico
restarts — asks Dorico's user to allow the client again. `session probe` connects directly rather than through the
host, so in that build it shows the dialog on every run, and its payload says so. Release builds do not pass this
switch either.

## The modal-dialog surface

`dialog` is on every payload and `execution` on one that posted a command, and `ui dialog` / `ui press` answer a
dialog. The design decisions are in the handover plan that produced it; what a contributor needs is three rules.

**Every file that needs a Windows API lives under `src/DoricoUse.Cli/Ui/`, and only the CLI project compiles it.**
`ProjectFileCheck` and `VocabularyCheck` build CLI sources **by hand**, on `net10.0`, by listing them in
`<Compile Include>` items — so a Win32 or UI Automation type in one of *those* files breaks both checks. The files
they do compile from `Ui/` are the ones with no Windows API in them: `DialogModel.cs` (the ids, the ordering, the
policy, the payload shape), `DialogCatalogue.cs` (the whitelist) and `LogQueue.cs` (the queue verdict, which reads
a file and waits for it, and is checked against a fixture log the check writes Dorico's lines into). The first two
are plain records and rules, and that is deliberate: it is what makes the ids and the refusals testable without a
dialog. **A rule that decides what an agent may press belongs in `DialogModel`, not in the Windows code.**

**There is no compiler switch for this surface.** The maintainer removed it from scope: every build carries the
probe, the reader and the presser, and `version.compatibility.uiAutomation` states that as a fact rather than
reporting a flag that would be a constant. Safety comes from the catalogue and from the two `--allow-*` flags at
the call. `AllowUnsafeBlocks` is on for the CLI project because UI Automation is called through raw COM vtable
pointers — the built-in COM marshaller generates its stubs at run time, and the release image has no code
generation at all.

**To add a catalogue entry, measure the dialog first.** Run the thing that raises it, read it with
`dorico-use ui dialog --raw`, and take the caption and the labels from that output — never from a description of the
dialog. Then add the entry to `DialogCatalogue.Entries` with a `Note` saying what you measured, mark the buttons
that cannot lose work `safe`, and leave every other button uncatalogued (it will be reported as
`role: unknown, safe: false`, which is the safe answer). The vocabulary check then tests the entry for you: that it
has a matcher, that its required labels are its own buttons' labels, that a destructive button is not safe, that it
recognises the dialog it describes, and that the entry offers a `pressWith` line. Nothing else is needed. An entry
for a dialog nobody has read is worse than no entry: the ids in `DialogCatalogue.Reserved` are waiting for exactly
that measurement, and a check asserts that none of them has one yet.

The captions and labels in the catalogue are the maintainer's Chinese UI. A dialog on another UI language reports as
`unknown` and is refused; that is the intended failure and not a bug to paper over with a looser matcher.

## The panel layer

`ui panels` reports every window Dorico has up besides its document window, and `ui panels --panel <id>` reads the
elements of one of them. The rules are the same shape as the dialog surface's, with two additions.

**An element's value is part of its state, and `--allow-destructive` has nothing to say about it.** Every element
that has a value pattern reports `value`, `valueFrom` and `valueReadOnly`, and a press on one reports
`worked.stateAfter` — the same element, read again in the same invocation. A write is therefore checked with the
payload that reports it, and there is no flag in that path on purpose: `--allow-destructive` is a rule about
**buttons** (the one control whose purpose is to commit something, and the catalogue is what makes "safe" mean
anything), while a check box, a list row, a tab and a text field are worked through their own patterns and the guard
is the dialog they live in — `--allow-unknown-dialog` for a window nobody measured. A value write has no catalogue
entry to consult, so a flag asking "is this write safe?" would have no answer to give; where the line is drawn is
"a button may be refused, a control may be worked", and the README says so in one sentence.

**A panel is a window, not a dialog, and the difference is measured, not assumed.** A window that is up while the
*document window is disabled* is holding Dorico's command queue; a window that is up while the document window is
enabled is not. That is the whole of `blocksQueue`, and it is a hint: `execution` on a command's payload stays the
measurement. Never decide by the window class — measured, Dorico's crash reporter is a `Qt6101QWindowIcon` like the
modeless library manager, and it disabled every other window of the process.

**The rules live in the pure half.** `Ui/PanelModel.cs` (ids, roles, states, the action choice, the window
classification) and `Ui/PanelCatalogue.cs` (the panels that have been read) have no Windows API in them, so
`VocabularyCheck` compiles them and asserts them without Dorico. The probe, the element reader and the surface are
under `Ui/` in files only the CLI project compiles. **A rule that decides what an agent may act on belongs in
`PanelModel`, not in the Windows code.**

**To add a panel to the catalogue**, raise it, read it with `ui panels --panel <id> --raw`, and take the caption and
the class from that output — the catalogue's matcher is a caption glob *and* a window-class prefix, because the class
is what the two measured kinds of Dorico window differ in. A panel that is not in the catalogue is still reported in
full with an `unknown.<hash>` id; acting on one of its elements needs `--allow-unknown-dialog`. There is no safety
table for panels: the danger is in the pattern, not the window, and the pattern is chosen from what the element
really exposes.

**An element's action comes from its own patterns**, in this order: toggle, selection, expand or collapse, invoke,
value. Measured, a Qt check box exposes an invoke pattern *and* a toggle pattern, and invoking it does not always
change its state, while toggling does — which is why toggle is asked for first. An element with none of them is
refused with `ElementNotActionable`; there is no coordinate clicking and no synthetic typing anywhere in this build.

**Which control types are elements lives in `Ui/ControlTypes.cs`, a pure file, and that is a bug fix as much as a
design.** `ListItem` was written as `50029` in the Windows half for a whole release; `50029` is `DataItem`, and
`ControlType.ListItem` is `50007`. Every row of Dorico's Setup lists and of the 繁衍声部格式 dialog was therefore
filtered out of every read while the reader walked straight past it, which is exactly the kind of mistake a rule
kept in a Win32 file cannot be tested for. The table, `IsActionable`, `Name` and `Role` are all there now, and
`VocabularyCheck` asserts the numbers and the actionable set. `IsRow` and `IsRowContainer` joined them for the same
reason: the row ceiling is decided from those two rules, and a rule that decides what a payload *claims* is not a
rule to keep in a file the checks cannot compile.

**An element reports its value, and a press reports what it left behind.** `PanelModel.Describe` fills `value`,
`valueFrom` and `valueReadOnly` from the value pattern, and `PanelModel.StateAfter` / `ControlAfter` turn a fresh
read into the `stateAfter` a press owes. Both are pure and both are checked, because the alternative — a payload that
reports `state: "value"` and the string that was sent — is a write no caller can verify. `Ui/UiaCalls.State` returns
the read-only flag beside the text for that reason; it used to throw the flag away.

**A read that realised rows must say that it is a viewport.** `PanelModel.Lists` counts the rows per container,
`PanelElementInput.Container` carries which list a row came from, and `PanelModel.RowCeiling` is the measured sentence
the payload adds. Do not add paging to this layer without a measurement that a list view exposes the scroll pattern;
the four lists read so far do not, on themselves, on any ancestor, or on a row — see the row-ceiling section of the
README for the counts and for the range-value experiment on the only scrollbars that exist.

**The document window is a panel with a constant id.** `dorico.document-window` is not in `PanelCatalogue`, because
that table matches on a caption glob and the document window's caption is the document's name (plus a `*` once it is
unsaved). Measured: the same window answered to two different `unknown.<hash>` ids as its title changed. The
constant comes from `PanelModel.DocumentWindowId`, and the probe decides *which* window it is — an unowned, visible
window of Dorico's process, the largest by its **restored** rectangle, because `GetWindowRect` on a minimized window
answers the 158x26 icon rectangle. If you change that resolution, take all three measurements again: nothing else up,
the library manager up, and the document window minimized.

**A dialog's non-button controls are called `controls`, and `--raw`'s `elements` was left alone.** A panel publishes
its actionable elements as `elements`; a dialog has published the whole tree it walked as `elements` under `--raw`
since the modal layer shipped, and a published field is never repurposed. So a dialog's edit fields, check boxes,
list rows and tabs are `controls`, with the same id/state/action shape a panel element has, and `ui press --dialog
<id> --element <id>` works them through the same `ElementActions` code a panel uses. Two rules follow from
measurements and both are in the checks: a control nested inside another control is still walked (the file picker's
`1148` name box is an `Edit` inside a `ComboBox`), and a catalogue button has to match **every** criterion its entry
declares — matched by the label `打开` alone, the Open picker's accept entry also claimed the two `DropDown` buttons
beside it, so `--button accept` would have pressed a combo box's arrow.

**One more trap, and it is in Dorico rather than in this tool.** `File.Save` writes whatever is unsaved, not only
what you changed, so a save "to verify something" can commit somebody else's arrangement — and raising
`File.Import.OtherProjectFlow` and cancelling its picker marks the document unsaved with no content change. Use
`Project.ImportLibrary` when you need a picker up, work on a copy when you need to save, and never save a document
you did not open for the purpose.

## Three protocol facts that cost a crash and a morning

All were measured while building this layer, and none is guessable from the catalogue:

- **`Project.Player.SetAsSoloist?PlayerID=` without its optional `Set` parameter crashes Dorico 6.2.30.** The log's
  last line is the `Executing command: Project.Player.SetAsSoloist?PlayerID=34` line with no
  `notifyPostCommandExecute` after it, and the process was gone. `Set` is optional in the published catalogue and is
  not optional in practice. The same command *with* `Set=true` works, and it renames the player it converts: measured
  on a 34-player project, player 34 (Violin II) went from `baseName "Violin II", isSoloist false` to
  `baseName "Solo Violin", isSoloist true` in the saved file.
- **Raising a rename dialog that is already up crashes Dorico 6.2.30.** Measured: `Project.PlayerRenameShowDialog`
  with no parameter raised 重命名演奏者 and worked; the next invocation,
  `Project.PlayerRenameShowDialog?PlayerID=0`, executed while that dialog was still on screen and the process died —
  the log's last line is that command's `Executing` line with no `notifyPostCommandExecute` after it,
  `%LOCALAPPDATA%\CrashDumps\Dorico6.exe.<pid>.dmp` was written, and the next start showed Dorico's own crash
  reporter. So a `*ShowDialog` command is one raise at a time; answer or cancel the dialog before sending another.
- **`Project.Layout.Rename?LayoutID=&Name=` is refused outright** — `kError kCommandNotAllowed`, with no `Posting`
  line in the log, in Write mode and in Setup mode, for the full score and for a part layout, with a `Name` and
  without one. A layout's name cannot be set through the API; `Project.Layouts.Renumber` answers `kOK` and runs. The
  same shape was measured for `Project.PlayerGroup.Rename`, whose two **required** parameters (a real `PlayerGroupID`
  and a `Name`) make no difference, and for `Project.PlayerGroupRenameShowDialog`, which either refuses or raises no
  window at all.

## The test corpus

`ProjectFileCheck` runs against two kinds of `.dorico` file. `tests/corpus/fixture.dorico` **is** in the
repository - a small project the maintainers made for testing, dedicated to the public domain, with one Piano
player, two staves and three notes (a flat, a sharp and a natural), so the checks that matter most run on every
push. The rest are the maintainer's own scores, which are **not** here: a check whose file is missing reports
`skip` and the run still exits 0.

To run those ones too, put the files under `tests/corpus/` — the checks look there first, by file name — or point
`DORICO_USE_TEST_CORPUS` at the folder (or folders, separated by the platform's path separator) that holds them.
Nothing in the repository names a drive: the names live in `tests/ProjectFileCheck/Program.cs` and are resolved at
run time, so a machine that has the scores runs all 187 checks and one that does not reports `skip` for the 13
that need them. A good corpus file is small, has one or two players, and — if you want the export cross-checks to
run — comes with a MusicXML export written by Dorico itself (File > Export > MusicXML) beside it. If you contribute
a score, say where it came from and under which licence in your pull request: it ends up in the repository, so it
has to be redistributable.

The strongest checks compare this decoder against a second source: Dorico's own MusicXML export. `--alter` against
the derived accidental, `<articulations>` against the articulation marks, `<slur>` pairs against the slur events,
the page box in tenths against the layout numbers. That is the pattern to copy when you add a feature.

## Verifying by hand

Nothing in CI can reach Dorico, so the live surface — `session`, `host`, `note`, `export`, and the protocol itself —
has no automatic cover. `tools/smoke.ps1` is that cover: it runs the whole surface against a **running** Dorico and
prints one PASS/FAIL line per check with the JSON it saw, exiting non-zero when anything failed.

```powershell
powershell -File tools/smoke.ps1                             # read-only; the two write checks print SKIP
powershell -File tools/smoke.ps1 -AllowWrite                 # also note insert and session read --rebind
powershell -File tools/smoke.ps1 -AllowUnsafeScriptRunApi    # only for -p:ExposeUnsafeScriptRunApi=true builds
```

Run it before a release, and after touching the host or the protocol.

The dialog surface has two halves there. The automatic checks are read-only and need no dialog: that the probe runs
and reports `dialog: null` on a Dorico with nothing modal, and that `ui press` refuses when nothing is up
(`DialogNotFound`, exit 1). The real dialogs cannot be raised by a check, so they are a **documented by-hand
section** at the end of the script's output: raise one dialog at a time, read it with `ui dialog`, and compare what
comes back with what the window shows — the caption, the body text and every button label, verbatim. Add a line to
that section when you add a catalogue entry, and record in the commit message which dialogs you actually raised,
because a dialog nobody raised is a dialog nobody measured. It takes `-Exe <path>` (else the published
binary, then the Debug build, then `dorico-use` on PATH) and `-JsonWidth 0` to print whole payloads. It prints the
active document's title and score id first and writes nothing without `-AllowWrite`; `session read --rebind` saves
a copy **and rebinds the open document to it**, so use a scratch project for that half. Exit `0` means every check
ran and passed, `1` at least one failed, `2` nothing failed but something could not run (Dorico closed, or no
document open) — a `not run` line is never success, which is what keeps a green run from being faked.

For a value read, the by-hand check is one round trip on any measured dialog: read it with `ui dialog`, pick a field
whose `value` you can see, write something into it with `ui press --dialog <id> --element <id> --value <text>`, and
confirm the payload's `worked.stateAfter.value` is what the field now shows — then answer the dialog with 取消 unless
you meant the change. The picker's file-type box is the cheapest one of all: raise `Project.ImportLibrary` and read
`ui dialog`'s `controls` for the id `1136`, which reports the filter the picker is really applying.

## Adding a feature

- **Protocol work starts in `application.log`** (`%APPDATA%\Steinberg\Dorico 6`): do the thing in Dorico's UI and
  the log shows the command and the parameters it sent. The command catalogue (`dorico-use command list`) gives the
  parameter names; Dorico's own XML exports give a second source for anything about notation.
- **Never invent a protocol fact.** If you cannot measure it, say in the XML comment that it is an inference, and
  put it behind `caveats` in the JSON the way the file reader does.
- **A `kOK` is not a result.** It is a receipt. Every command that changes the score reads the status back, and
  note entry is proved with `session read`.
- **Anything that changes a document takes `--expect-score`**, because "the active document" is session-wide state
  that a window focus can change under you. `EnsureExpectedScore` is wired into the host, not the CLI, so the check
  happens inside the same exchange as the action.
- **Do not add a way to send an arbitrary protocol message.** One shipped during development and was removed
  before release.

## Things that are known to be unfinished

Contributions welcome, in rough order of value:

1. **Reading the live document without `File.SaveAs`.** `session read` must rebind the open document to a copy —
   a real footgun — and `docs/scripting.md` §8 shows a Lua script can read the live score instead
   (`score.flows → voiceStreams → dump()`); the work is parsing that printer format into the same JSON
   `file read` prints, and this repo has no shipped scripting command yet.
2. **A macOS port.** The portable part is the pipe, the WebSocket and the file decoder; the dialog probe and the
   button pressing are Windows-only and are compiled into every build, so a port has to exclude or replace that
   surface rather than add a target framework. Only Windows was ever tested: named pipes, console encoding, the
   `host start` launch path (`UseShellExecute`) and paths all need a pass too, and Dorico users are heavily Mac.
3. **Dorico SE / Elements behaviour.** Only Pro 6.2.30 was tested; the editions differ in which commands and
   options they accept. The catalogue-based features should degrade, but nobody has checked.
4. **The scripting channel as a shipped command.** `session read` uses it internally (see `LiveRead`), and
   `docs/scripting.md` §8 shows what else a script can reach, but there is no supported command that runs a user's
   own Lua. That is deliberate for now: probing that layer blind has crashed Dorico here.
5. **A list view's unrealised rows.** The ceiling is measured and reported (`lists`, `rows`, `viewportLimited`,
   `rowCeiling`) but not removed: no list, ancestor or row this build has read exposes the scroll pattern, so a row
   outside the viewport can be neither read nor chosen. Two routes remain unmeasured, and either would need a
   measurement before it could be promised — a Qt delegate that exposes a value pattern on the row itself (a list
   row *does* expose one; nothing has shown that writing there edits the row), and the scripting layer, which
   enumerates every player and layout but does not act on the Setup trees.

## Releasing

`.github/workflows/ci.yml` builds, runs both checks and publishes the Native AOT binary on every push. A `v*` tag
additionally runs `.github/workflows/release.yml`: the same build and checks, the publish, an assertion that the
published binary's `version.build` flags are both `false`, a zip of the exe with `README.md`, `LICENSE`, `NOTICE`
and `CHANGELOG.md`, and `gh release create` with that version's `CHANGELOG.md` section as its notes (falling back to
generated notes). A release is that tag plus the `dorico-use.exe` from it (there is no installer), with
`CHANGELOG.md` updated and the `<Version>` in `DoricoUse.Cli.csproj` bumped. Dorico compatibility is *not* a version
number: it is reported by `dorico-use version` (`compatibility.testedAgainst`), which is the fact a user needs.
