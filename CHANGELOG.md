# Changelog

All notable changes to this project are documented here. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and the project aims to follow [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

Compatibility with Dorico is reported by `dorico-use version` (`testedAgainst`, `apiCompatibility`) rather than
encoded in the version number, because the same build works against any Dorico that exposes the same Remote
Control API — see `README.md`.


## [Unreleased]

### Added
- **Modal-dialog awareness.** Every payload now carries `dialog`: the dialog Dorico has up, its title, its body
  text and every button with a stable id, or `null` when nothing is modal. A modal dialog stalls Dorico's whole
  command queue, and the API could not report that until now.
- **`ui dialog [--raw]`** and **`ui press --dialog <id> --button <id> [--allow-destructive]
  [--allow-unknown-dialog] [--path <p>] [--timeout-ms n]`**: read the dialog that is up, and press one of its
  buttons. The press re-probes first, refuses when the dialog named is not the one up (`WrongDialog`), and reports
  what is up afterwards, because a press that closes its own window can report an error after succeeding.
- **`execution`** on every payload that posted a command to Dorico: `ran`, `queued`, `refused` or `unknown`, read
  out of Dorico's own `application.log`. `queued` is the state that used to be invisible — a command posted behind
  a dialog still answers `kOK` and then runs minutes later.
- A measured whitelist in `DialogCatalogue`: `dorico.export-musicxml`, the `dorico.folder-picker` it raises, and
  `dorico.unsaved-changes`. Anything else is reported in full and refused (`UnknownDialog`), and every button of an
  unknown dialog is unsafe, so pressing one needs both `--allow-unknown-dialog` and `--allow-destructive`.
- `export musicxml` and the live read now report the dialog they saw while waiting (`dialogWhileWaiting`).
- 140 checks in `VocabularyCheck` over the two pure model files, which need no Dorico, no dialog and no Windows:
  id stability, the unknown-dialog hash, the button ordering rules, every catalogue entry's policy, every refusal
  code with its exit code, and the payload shape.
- Eleven more checks over the execution verdict, driven by a fixture log that a check writes Dorico's own lines
  into while the verdict waits — the one way to measure the retry and the offset rule with no dialog to raise:
  the four verdicts, the offset at a line boundary and inside a line, and the payload that carries no `execution`
  field. With the old offset rule put back, they fail with the payload this release fixes.

### Changed
- **The CLI project targets `net10.0-windows`.** Reading Dorico's dialogs and pressing their buttons uses Win32 and
  UI Automation, and there is no compiler switch: the capability is in every build. `version.compatibility` states
  that as a fact. A macOS port now has to exclude or replace that surface, which the README and `CONTRIBUTING.md`
  say outright instead of promising that nothing uses a Windows-only API.
- `version.compatibility` reports `uiAutomation`; the two existing `version.build` flags are unchanged.

### Fixed
- **`execution` reported `unknown` in exactly the case it exists for.** The delta is read from the end of
  `application.log`, and the line the verdict is made of — the command's own `Posting` line — is the *first* line
  the log gains, so the rule that drops a half-written leading line dropped it every time. Measured against a live
  Dorico 6.2.30 with the export dialog up: `command send --name NoteInput.Enter --no-verify` answered `kOK` and the
  payload said `unknown` while the log held `Posting command (requested): NoteInput.Enter` at that exact offset.
  The verdict is now read in two bounded phases — wait for the `Posting` line, then a second for the matching
  `Executing` line — and an invocation that posted nothing by name carries no `execution` field at all instead of
  an `unknown` standing on a log line of somebody else's.
- **`ui press` reported the dialog it had just answered.** The re-probe ran immediately after the press, while the
  Qt window was still on screen — measured: 取消 closed the export dialog 82 ms *after* the press returned — so the
  payload's `dialog` and `pressed.afterDialog` named the dialog that had just gone. The re-probe now waits, for up
  to a second, until that window is gone or another dialog has taken its place, and reports what is up then. The
  fast path is untouched: `ui dialog` with nothing modal still measures 9 ms.

### The engraving job's surface (layers 1 and 2)

### Added
- **`project players`**: the players of the **open** document, read live. There is no Remote Control request that
  returns players — measured, the request table in the application has none, and `getflows` answers
  `{flowID, flowName}` per flow on a one-player fixture and on a 38-player project alike — so this goes through the
  scripting layer, which reports each player's index, the text Dorico describes it with, and which members that
  layer refuses (`id`, `name`, `shortName`, `playerID`, `soloPlayer` and `isSoloist` all answer nil; `#instruments`
  answers a number). The refusals are fields, not silence.
- **`project flows --raw`** and **`project layouts --raw`**: the unmodified Dorico answer, the same escape hatch the
  other reads have. How the two payloads above were read.
- **`score.playerGroups`** on every `file read`: each group's `name`, its `shortName` (the abbreviation a staff label
  prints), its file id and the player ids in it. Measured on a 34-player project: Woodwinds/Ww., Brass/Br.,
  Percussion/Perc., Strings/Str. and Band/Bnd. This is the read the checklist's group item needs, and it is the only
  one: `Project.PlayerGroup.Rename?PlayerGroupID=&Name=` is refused outright (`kCommandNotAllowed`).
- **`score.players[].isSoloist`** and the layout view state on **`file read --layout`**: each layout's
  `transpositionType` (`kScoreInC` / `kTransposingScore`) and its `condensing`, which are the stored half of the
  "enable the transposing view" and "enable the optimised score" checklist items. Measured both ways: a project's
  full score wrote `kScoreInC` and its part layout `kTransposingScore`, and toggling each setting in Dorico and
  saving a copy changed exactly those fields on that layout.
- **`score.players[].soloPlayer`** and the derived **`playerType`** (`solo` / `section`) on every `file read`: the
  player's *kind*, which is a different flag from `isSoloist` and the one a Sibelius import gets wrong. Measured on
  a 44-player orchestral import: 40 solo players and 4 section players — Violin I, Viola, Violoncello and
  Contrabass — while Violin II came in as a solo player and every one of the 44 wrote `isSoloist: false`. The two
  names come from the pair of commands the application's own catalogue publishes, `Project.SoloPlayer.New` and
  `Project.SectionPlayer.New`; the mapping is an inference from that name and from the measured distribution, so
  the payload carries the raw leaf and a caveat says which half is inferred. Cross-checked against two finished
  projects (34 players: all five strings sections; 35 players: Violin 1 section against Violin 2 solo, the same
  defect in a finished score) and against the committed fixture (one solo player, `isSoloist` false).
- **`session status` reports `condensingEnabled`**, which is the live half of the same item.
- **Rehearsal marks in the live read.** `session read` (and `note insert --read`) now walks the flow's **timebase**
  stream as well as its voice streams, so `counts.rehearsals`, `counts.timebaseStreams`, `timebaseKinds` and a
  `rehearsals` array appear. Measured with one mark created live:
  `[{flow:1, stream:0, eventId:9, position:"1", text:null, line:"1 (1): kRehearsalMarkEvent (id:9) at 1 "}]`. `text`
  is null because Dorico's printer prints no text for a mark and the saved file stores none either: the letters are
  the sequence Dorico computes.
- **`panels` on every payload**, and **`ui panels [--panel <id>] [--raw]`**: every visible top-level window of
  Dorico's own process, with its handle, class, title, owner, foreground state, whether it is the document window or
  the modal one, and `blocksQueue` — a hint from the facts, never the queue measurement. Cheap Win32: measured at
  9 to 17 ms with the library manager up, and it reads no elements.
- **`ui press --panel <id> --element <id> [--value <text>]`**: work one element of a modeless panel — toggle a check
  box, choose a tab, select a list or tree item, expand or collapse, invoke a button, or write a text field through
  the value pattern. The action is chosen from what the element really exposes (toggle, selection, expand, invoke,
  value, in that order) and an element with none of them is refused with `ElementNotActionable`. Measured on the
  library manager: three check boxes with `action: toggle` and real states, two buttons with `action: invoke`, and
  `ui press --panel dorico.library-manager --element button-4` closes the panel — which this tool could not do at
  all before.
- **`ui press --element <id>`** is accepted for a dialog as an alias of `--button`, and **`--value <text>`** is the
  general form of `--path`.
- **`Ui/PanelModel.cs`** and **`Ui/PanelCatalogue.cs`**: the panel layer's pure half, compiled into
  `VocabularyCheck` like the dialog model, with 48 new checks over the element ids, the roles, the states, the
  action choice, the window classification and the element cap.
- **`dorico.crash-report`** in `DialogCatalogue`: Dorico's own crash reporter, measured (向 Steinberg 发送崩溃报告,
  with 发送到 Steinberg, 不发送 and 不再询问; only 不发送 is safe). It owns nothing and yet disables every other
  window of the process, which is how the block rule was corrected.
- **`dorico.library-manager`** in `PanelCatalogue`: the first panel read off a live window.

### Changed
- **`dialog.blocksQueue` is no longer the class alone.** A window that is up while the document window is disabled
  is holding the queue, and that is now what decides it — measured against Dorico's crash reporter, a
  `Qt6101QWindowIcon` that the class hint called modeless while it had disabled the document window *and* the
  library manager.
- The document window is resolved by size when a modal that owns nothing is up: measured with the crash reporter
  showing, the process's main window was the 1768x1077 library manager and the 3286x1841 document window was beside
  it, unowned and disabled.

### Fixed
- **The tool can now close Dorico's library manager.** `ui press` used to refuse it with `DialogNotFound` because
  the panel was not a dialog; it is reported as a panel and worked by element id (`... --element button-4`).

### Documented, not fixed
- **`Project.Player.SetAsSoloist?PlayerID=` without its optional `Set` parameter crashes Dorico 6.2.30.** Measured:
  the log's last line is the `Executing` line and no `notifyPostCommandExecute` follows it. With `Set=true` it runs
  and converts the player: player 34 of a 34-player project went from `Violin II`/`isSoloist false` to
  `Solo Violin`/`isSoloist true` in the saved file. `Set=false` answered `kOK`, ran, and left the flag `true`.
- **`Project.Layout.Rename?LayoutID=&Name=` is refused** in Write mode and in Setup mode, for both kinds of layout,
  with and without a `Name`: `kError kCommandNotAllowed`, never posted. Renaming a layout is therefore a manual step,
  and `Project.Layouts.Renumber` (which answers `kOK` and runs) is the only layout bookkeeping the API offers.
- **A tremolo is a note property, not an event, and the import that raised the question lost none of them.** The
  live read reports no tremolo event kind because there is none to report: the mark is an entry in the note event's
  `propertyTable` (`propertyType: kNoteSingleStemTremolo`, value the variant list `list: {int: N}, {bool: false}`
  where N is the slash count). Measured against the export that was imported: its 150 `<tremolo type="single">`
  markings in 79 measures sit on 120 tie chains, the file writes one entry per chain (120 note events, over the 72
  measures those chains start in), and the count agrees with the export's slash count. The 7 measures with no entry
  are measures the export marks only on a tied continuation, and a tie continuation is not an event of its own.
  13 of the 120 events carry a second entry: 11 read a count of 0, which this build does not name, and two in one
  measure repeat their own count. The `--beams` caveat now says all of that.
- **The note count is a tie-chain count, and Dorico lost nothing.** Dorico's 34,284 note events against the
  import's 61,034 `<note>` elements is not a discrepancy: 24,281 of those elements are `<rest>` (Dorico counts no
  rests at all), 2,077 are extra tied segments of a chain (a chain is one note event whose duration is the whole
  chain), and 392 are grace notes, which Dorico reports as their own kind (`kGraceNoteEvent` 392, matching element
  for element). What remains — 34,284 chains against 34,284 events — agrees in every one of the 44 players and in
  all 531 measures: 0 differing (player, measure) cells, checked after the tie-chain rule was applied.

### The UI surface's depth (the gaps the engraving run exposed)

### Added
- **Dorico's own document window is reachable**, under the constant id **`dorico.document-window`**. Its UIA tree
  carries the Setup surface — the player list, the flows, the layouts and their action bars — and its rows are
  reported as elements like any other panel's. Measured on a 44-player project: 132 elements, 59 of them rows
  (30 players, 1 flow, 28 layouts), and
  `ui press --panel dorico.document-window --element listItem-39` moved the list's selection from `Piccolo` to
  `Flute 1` with the re-read as evidence.
- **`dialog.controls`**: everything actionable in a dialog that is not a button — an edit field, a check box, a list
  row — with an id, its state and the action it takes, and **`ui press --dialog <id> --element <id> [--value <text>]`**
  works one of them. Measured on the 重命名演奏者 dialog: `fullNameLineEdit` read `Piccolo` and `shortNameLineEdit`
  read `Picc.`, each with a writable value pattern, and a written value read back verbatim. The dialog's buttons are
  unchanged, and `--raw` still publishes the whole walked tree under `elements` — the new array is `controls`
  because a field is never repurposed.
- **`dorico.open-picker`** in the catalogue, with **`Project.ImportLibrary`** and **`File.Import.OtherProjectFlow`**
  as the two commands measured to raise it: the classic Windows Open dialog, caption 打开, class `#32770`, owning the
  document window and disabling it. Accept is the button with classic control id `1` (unsafe: accepting an import
  changes the document), cancel is `2`, the file-name box is an `Edit` with automation id `1148`, the file filter is
  `1136`, the address bar `1001`, and the file type was `Any Readable File (*.doricolib)`. The name box, the filter
  and the search box all take a value through the UI Automation value pattern, so a file picker is driven with no
  mouse and no typing.
- **`session status.view`**: `openScoreID`, `viewContainerID` and a ready-to-paste **`switchWith`** line. Measured:
  `Window.SwitchToLayout?OpenScoreID=&ScoreLayoutViewContainerID=&LayoutID=` makes a specific layout current, and the
  two ids are exactly Dorico's own `activeOpenScoreID` and `activeViewContainerID`. Switching to layout 1 flipped
  `status.condensingEnabled` from true to false and switching back flipped it back.

### Changed
- **A live read waits for a busy queue instead of giving up.** The wait is 20 s while the log shows nothing queued
  and at most 180 s while it shows work that was posted and never executed. Measured before and after:
  a read behind a file picker used to fail after 21.4 s with `LiveReadWroteNothing` while its own script was posted
  at 10:04:50.116 and executed at 10:05:20.180; it now waits and answers — measured 43.9 s and `ok: true, source:
  live, count: 44`. A read that still fails says which of the two it was and names what was stuck on the queue.
- **A catalogue button now has to match *every* criterion its entry declares**, not any of them. Measured on the live
  Open picker, which has two `DropDown` buttons labelled 打开 beside the real accept button: matched by the label
  alone, the entry handed them the `accept` id, so `--button accept` would have pressed a combo box's arrow. The
  entry declares label *and* classic id, and every other entry was tightened the same way.
- **A dialog element is walked past, not stopped at.** The file-name box is an `Edit` nested inside a `ComboBox`;
  a read that kept the combo box and did not descend never reached the control a value goes into.

### Fixed
- **`panels[].document` was on the wrong window.** `DialogProbe` took `Process.MainWindowHandle` as the document
  window, and with two visible unowned windows .NET returns whichever EnumWindows lists first. Measured: with the
  library manager up, `MainWindowHandle` answered the 库管理 window (`0x038A0A34`, normal area 816187) instead of the
  document window (`0x00C91106`, normal area 2688357), so the library manager was flagged `document: true` and the
  real one `false`; with nothing else up it was right. The document window is now the process's visible unowned
  window with the largest **restored** rectangle, taken from `WINDOWPLACEMENT.rcNormalPosition` — measured
  necessary, because while the document window is minimized `GetWindowRect` answers 158x26 at (-21333,-21333) for a
  window that is really 2191x1227.
- **A list row was invisible to every read.** `UiaCalls.ControlTypeListItem` was `50029`, which is `DataItem`;
  `ListItem` is `50007`. Measured with an independent walker and again through this build's own reader: the Setup
  lists expose their rows as 50007, and `PanelReader` filtered every one of them out, which is why Dorico's Setup
  work looked unreachable and why the 繁衍声部格式 dialog's layout lists looked empty. The correction moves the table
  into a pure file, `Ui/ControlTypes.cs`, that the check projects compile, and asserts the number there.
- **A shared classic Win32 control id now gets an id an agent can paste.** Measured on the Open picker, the
  `ComboBoxEx32` pane, the `ComboBox` inside it and the `Edit` inside that all answer automation id `1148`, so an
  unqualified number is not unique and fell through to a position id. It is now `comboBox-1148` and `edit-1148`.

### Documented, not fixed
- **`File.Save` writes whatever is unsaved, not only what you changed.** Observed on the engraved project: the file
  as imported had its layouts and its player table in numeric-id order, the file after a save had them in the
  document's voice order, every record identical field for field, and `application.log` held no move command from
  Dorico's start. **Not established:** which command put the order there — `Project.Layouts.Renumber` is one
  candidate, "a save writes the document's own order" the other. The consequence is in the README: a save is a real
  edit, never a probe.
- **Raising `File.Import.OtherProjectFlow` and cancelling it marks the document unsaved** with no content change:
  measured, it re-activated the document (`Activating document: 2`, `Document setup and activation complete`), and
  the title gained `*`. Not deterministically reproducible — the same command was raised and cancelled an hour
  earlier and the title stayed clean.
- **A group's name and short name cannot be set.** `Project.PlayerGroup.Rename?PlayerGroupID=&Name=` and
  `Project.PlayerGroupRenameShowDialog?PlayerGroupID=` are both refused (`kError`, no `Posting` line), and no
  command publishes the short name. The Setup tree does expose the group rows as list items, and each is selectable,
  but `InvokePattern.Invoke()` on one does nothing measurable and there is no in-place edit. A group's abbreviation
  stays a manual step.
- **A list view realises only the rows around its viewport.** Measured: 30 of 49 player rows and 28 of 45 layout rows,
  the same count minimized and restored, with no scroll pattern on the list and no scroll-item pattern on a row. A
  row that is not realised cannot be read or chosen by this build; `project players` and `file read` are the reads
  that see all of it.
- **No payload reads which layout is current.** Not `session status`, not `project layouts --raw`, not the window
  title, not the Setup layout list (no row selected). The closest thing is `status.condensingEnabled`, which mirrors
  the current layout's own `enableCondensing` and identifies it only against a layout that differs.

### Reading a value, reaching rows, and the two open questions closed (round 3)

The three needs the second engraving run left, each measured before it was implemented, plus the two questions the
earlier rounds left open.

### Added
- **Every element reports its current value.** `ui panels` and `ui dialog` elements carry `value`,
  `valueFrom` (`valuePattern`) and `valueReadOnly` when the element has a value pattern, and a button, a check box or
  a list row carries none of them. Measured: the Open picker's file-name box (`edit-1148`) read `""` and then the
  written path back, the 重命名演奏者 dialog's `shortNameLineEdit` read `Picc.` and then `Probe.`, and the file-type
  box read `Any Readable File (*.doricolib)` — which is the answer to the question the earlier round could not
  settle, and the reason `Project.ImportLibrary` imported nothing from a `.dorico` file.
- **A write is now verified in the payload it was made in.** `ui press --dialog … --element … --value …` reports
  `worked.stateAfter.value`: the same element, read again in the same invocation, carrying the text the field really
  holds. It used to report the string that was sent and nothing else, so no caller could check a write.
- **The row ceiling is stated where a caller cannot miss it.** A panel read that realised rows now carries
  `lists` (each row container with how many rows it gave and whether it exposes a scroll pattern), `rows`,
  `viewportLimited` and `rowCeiling`, and every row carries the `container` it came from — which matters most in a
  dialog with two lists side by side.
- **`file read` reports `staveOverride` per note**: the note event's own `logicalStaveIDOverride`, null when the
  file states none. Measured on a copy of the fixture: `Edit.SelectAll` in Write mode sets `status.hasSelection`,
  `Edit.CrossStaffBelow` wrote `1` on the two notes of the upper stave, `Edit.CrossStaffAbove` wrote `0` on all
  three, and `Edit.CrossStaffReset` took them back to null, with the events' own stave/voice/stream untouched.
- **28 more checks in `VocabularyCheck`** over the two rules the round added — the value shaping (present for a
  field, absent for a button and for a row, read-only carried) and the row ceiling (per-container counts, the
  `viewportLimited` flag, the ceiling sentence, the container name) — and one more in `ProjectFileCheck` for the
  fixture's `staveOverride`.

### Changed
- **`ui press` on a dialog's control reports `stateAfter`, exactly as a panel element's press does**, so one payload
  answers "what does it hold now" wherever the element lives.
- `Ui/UiaCalls.State` now asks the value pattern for read-only as well as for the text, and `Ui/ControlTypes` gained
  `IsRow`/`IsRowContainer` plus the `DataGrid`/`Table` numbers, so the container rule is a tested rule rather than a
  Win32-file guess.

### Documented, not fixed
- **A group's name and abbreviation stay manual, and now that is measured rather than suspected.** With a group row
  selected in the Setup player list, `Project.PlayerGroupRenameShowDialog` (no parameter) runs and raises **no**
  window at all; with `PlayerGroupID=-1`, `0`, `1` or `2` — one of them the id `file read` reports for a group the
  tool itself created — it is refused outright (`kError`, no `Posting` line). The player route works, by contrast:
  with a player row selected, `Project.PlayerRenameShowDialog` (no parameter) raises 重命名演奏者 with both fields
  prefilled, which is what proves a UI Automation row selection really is Dorico's own selection. `file read`'s
  `score.playerGroups` remains the only read for a group's name and short name.
- **Two document windows make `document: true` name the wrong one.** The probe resolves the document window as the
  largest unowned visible window of Dorico's process, which was measured correct for one document and is wrong for
  two: with a second document open and active (the scratch copy this round used), the payload flagged the
  *inactive*, larger window as `document: true` and gave the active one an `unknown.<hash>` id. Both windows are
  readable and both are reachable by id — `ui press --panel unknown.<hash>` works on the one that is not flagged —
  but a caller that trusts `document: true` acts on the wrong document's Setup lists. Not fixed here: the rule needs
  its own measurement (which window Dorico really has active) rather than another guess.
- **`Project.PlayerRenameShowDialog?PlayerID=` while the rename dialog is already up crashes Dorico 6.2.30.** The
  log's last line is `Executing command: Project.PlayerRenameShowDialog?PlayerID=0` with no
  `notifyPostCommandExecute` after it, `Dorico6.exe.<pid>.dmp` was written, and Dorico's own crash reporter offered
  发送到 Steinberg / 不发送 on the next start — the same shape as the `Project.Player.SetAsSoloist` crash the
  README already carries. The first raise (no parameter, with a selected player row) is fine and was used
  repeatedly; it is the second one, with an explicit id, that killed the process.
- **Dorico's automatic-recovery dialog (`自动保存工程恢复`) deletes what is not recovered.** Its own text says so
  ("任何列表中未被选定恢复的自动保存工程将被删除"), it lists the autosaves in a table with `放弃全部` and
  `恢复选定工程` and **no cancel**, and it is not in the catalogue, so both `--allow-unknown-dialog` and
  `--allow-destructive` are needed to answer it. See the README for what that means before answering one.

### The library manager read honestly, the layout defaults restored, and a guard against the measured crash (round 4)

Three jobs, each decided by a measurement made first: what the library manager really is, what "restore to user
defaults" is, and what must stop `command send` from repeating the two crashes.

### Added
- **`command send` refuses while a blocking dialog is up.** With a modal up it answers `ModalDialogUp` (exit 1) and
  sends nothing — measured, Dorico's log gains no `Posting` line for the refused command — and `--allow-modal-dialog`
  sends anyway. `error.data` carries the dialog's `id`, `title`, `kind`, `class` and the `pressWith` line that answers
  it, plus the override's name, so an agent never parses a sentence. The rule turns on the **window**, not on the UI
  Automation read (a dialog whose content cannot be read still refuses), and a **degraded probe proceeds**: a broken
  instrument must not block a caller's command, and every payload already says `probe.degraded`. The scope is that one
  command: `note insert`, `options set`, `session read` and the exports keep working with a dialog up — measured with
  the export dialog on screen, `note insert --dry-run` returned its plan and `options set` answered `kOK` with
  `execution: queued`. `command send --dry-run` is not guarded either: it sends nothing, and it is what a caller
  reaches for while Dorico is stuck. The reasoning is in `DialogModel.CommandSendGuard`, in the README and in
  `CONTRIBUTING.md`.
- **`project layout-defaults (--part-layouts | --layout <id>) [--expect-score n]`**: the measured route for the
  engraving checklist's part-layout item. It raises 全部声部布局选项 (`Project.Layout.AllPartOptions`, every part layout
  selected) or 布局选项 for one layout (`Project.Layout.Options?LayoutIDArray=<id>`), presses `重置到保存的默认值`
  (`resetToSavedDefaults`) in the dialog it just raised, and reports the panel as it is afterwards. (The dialog used
  to be left open on purpose, because its 关闭/应用 buttons have no unique automation id in Qt — measured, their
  element ids moved between two reads of the same dialog once its page changed; it now closes it itself, by label.)
  See the round below.
- **`dorico.layout-options`** in `PanelCatalogue`: the 布局选项 dialog, measured (162 elements; the option pages and
  the layout list's 64 rows, 74 option controls, and the four default buttons `saveAsDefault` /
  `resetToSavedDefaults` / `resetToFactory` / `removeSavedDefaults`). It gets a constant id that survives a document
  rename, and working its elements no longer needs `--allow-unknown-dialog`.
- **Every panel read says how many nodes the walk visited** (`visited`), and when it kept fewer it says why
  (`walkNote`); **`ui panels --raw` lists the whole walk** under `nodes`, each node with its `role`, `controlType`,
  `name`, `automationId`, `depth` and **`kept`**. Measured on the library manager: 28 nodes for 5 elements. That
  difference is exactly what a "there is no such control" conclusion used to hide, and it is now a field.
- **26 more checks in `VocabularyCheck`**: the modal guard's rule (the code, the exit code, `error.data` and its
  identity fields, the override, the null cases — including a dialog object that cannot be named, which must not
  refuse), the walk-count rule (`visited`, `walkNote`, the "walk kept everything" case, and the node payload with its
  `kept` flag and named roles), and the two new catalogue entries.

### Changed
- **`Ui/ControlTypes.Role` names every control type a real window uses.** `Window`, `ScrollBar`, `MenuBar`, `ToolBar`,
  `Custom`, `Header` and `HeaderItem` fell through to `controlType-<number>`; the raw node walk made that visible on
  its first run (a window node read as `controlType-50032`).

### Fixed
- **A panel read no longer hides a full window behind a short element list.** Measured: the library manager's tree is
  28 nodes, 5 of them actionable, and its 此工程 rows, its 显示 filter, its 搜索 field and its `.dorico` drop target
  are not in the tree at all — so `elementCount: 5` read as "an empty dialog" and stayed that way for a release. The
  read now reports the walk's own count and, when it dropped nodes, says what they were.

### Documented, not fixed
- **A `.dorico` file cannot reach the library manager's drop target without a mouse.** Measured three ways: nothing in
  the dialog's UI Automation tree raises a file picker; the window carries no `WS_EX_ACCEPTFILES` (its extended style
  is `0x00000100`, `WS_EX_WINDOWEDGE`), so the legacy `WM_DROPFILES` protocol is not the one it registered for — and a
  posted `WM_DROPFILES` changed nothing on screen; and no command in the catalogue sets a project as the comparison
  source (`Library` is `Project.ImportLibrary`, `Project.ExportLibrary`,
  `Project.ShowLibraryImportExportDialog`, and the import picker filters to `*.doricolib`). The maintainer's route is
  therefore a documented manual recipe — 库管理 → 比较: `Dorico 工程` → drop → `显示: 不同` → mark → `应用` — and the
  tool's part is reading the library on both sides of it: the dialog's 集合 rows are the protocol's 50 collections, so
  `library collections` and `library entities` report the same data, and `file read` reads the file. Invoking one of
  the 13 unnamed `CollectionListDelegate` groups the tree does carry changes nothing (measured twice).
- **`options set --type layout --layout N` writes to the layout that is current, not to N.** Measured: the request
  carried `layoutIDs` with the id asked for and, in a second run, the singular `layoutID` as well, and Dorico executed
  `Project.Layout.SetOptions?…&LayoutIDArray=<the current layout>` — layout 0 while the full score was current, layout
  2 once `Window.SwitchToLayout` had made Flute 1 current. The write landed on the current layout both times and the
  command's own read-back said so (`verified.<path>.ok: false`). The protocol has no working layout target for a write;
  the README now gives the route that works (make the layout current first) instead of the claim that used to be there.
- **The library manager's content is readable; its rows are not.** Its 选项 and 集合 columns, its 此工程 list, its
  filter, its search field and its drop target have no UI Automation node, and the 13 Groups that are there have an
  empty name, an empty value and an `Invoke` that does nothing. What the collections *mean* is available
  (`library collections` — 50 — and `library entities`), which is what makes a comparison made by hand checkable by
  name afterwards.

### The dialog's window handle, and an error box whose caption is not an identity (round 5)

Two changes to the same surface: every `dialog` object now says *which window* it is describing and for how long,
and Dorico's error message box is catalogued instead of being reported as unknown.

### Added
- **`dialog.hwnd`, `dialog.openMs` and `dialog.instance` on every payload.** `hwnd` is the dialog's own window
  handle in the `0x00C91106` spelling the panel payload has always used — the dialog payload did not carry one, which
  was the inconsistency. `openMs` is how long that window has been up, in whole milliseconds, and `instance` is the
  1-based occurrence counter of its handle. Together they answer what a press leaves behind: a dialog that ignored the
  press is the same `hwnd` with the same `instance` and a larger `openMs`, while a dialog the press raised is a
  different handle — and a handle the memory has forgotten counts from 1 again, even when it hosts the same
  `dialog.id`. That last clause is why the handle is needed at all: the id is the same on every raise of the same
  dialog, and measured on the error box below the caption is not an identity either.
- **The dialog memory**: `%LOCALAPPDATA%\dorico-use\dialogs.json`, one entry per window handle
  (`{"dialogs":[{"hwnd":"0x00C91106","firstSeenUtc":"…","instance":1}]}`). A handle seen first gets
  `firstSeenUtc = now` and `instance = 1`; a handle already in it keeps both; a handle whose window is gone — or a
  probe that found no dialog at all — is forgotten; an entry that has not been the dialog for six hours is pruned so
  the file cannot grow without bound; and the file is written only when it changes. It exists because this CLI exits
  between the two observations `openMs` is about: the moment a window went up belongs to a process that has already
  gone. A memory file this build cannot parse is replaced on the next sighting rather than treated as a failure, and
  one it cannot read or write at all leaves the two fields off the payload — degraded, never wrong.
- **`dorico.error` is catalogued.** Measured, captured verbatim from a real payload: Dorico's error message box is
  class `Qt6101QWindowIcon`, its caption is the bare word `Dorico`, its body says `错误发生`, and its two buttons 关闭
  and 确定 share the button-box path `buttonBox.QPushButton`. Both buttons are `safe` — an error report only needs
  closing — so answering it needs no flag at all, where it used to need both `--allow-unknown-dialog` and
  `--allow-destructive`, and `pressWith` offers the 关闭 button as `dismiss`.
- **35 more checks in `VocabularyCheck`**, 433 now, 0 failed, 0 skipped: the error box's fingerprint, one criterion
  taken away at a time — a window that shares only the caption `Dorico`, one that shares the caption and the class but
  shows no body text, one that shares the caption and the body text in another class, one whose 关闭 and 确定 carry no
  button-box path, and one whose caption says something else must each stay `unknown` — plus the three new payload
  fields with their order and the store that could not say, and the dialog memory's rules: a first sighting, a handle
  that keeps its `firstSeen`, a recycled handle that counts from 1 again, a handle whose window is gone, the prune
  that keeps the file bounded, the file that is not created when there is nothing to remember, a memory file that
  says nothing usable being replaced rather than treated as a failure, and a store that cannot be written degrading
  to no fields rather than to a wrong number. The memory is asserted with a clock the check passes in, because the
  alternative is sleeping six hours for a prune.

### Changed
- **`DialogCatalogue.Match` takes the whole fingerprint** (`DialogFingerprint`: caption, window class, body text,
  button labels, button automation ids) instead of three loose arguments, and `DialogCatalogueEntry` gained the
  window class, the required body text and the required button automation-id path. Every criterion is optional and an
  undeclared one is not checked, so the entries written before this match exactly what they matched before — and
  `PanelModel` now formats handles through the one `DialogModel.HandleText`, so a handle cannot have two spellings in
  one payload.
- **A `dialog` whose store could not be read or written omits `openMs` and `instance`** rather than reporting a zero
  that would read like "just opened". A state file that will not cooperate is no reason for a probe to fail.

### Documented, not fixed
- **The queue sentence on a blocking `Qt…Icon` window is a class hint it did not earn.** `dialog.note` says "this
  window's class is one whose dialogs stall Dorico's command queue" whenever `blocksQueue` is true, and for
  `dorico.error` — a `Qt6101QWindowIcon`, the class the payloads read as modeless — that is true of the *document
  window being disabled* and not of the class. The same sentence has been on the crash reporter since it was
  catalogued. `blocksQueue` itself is right (`documentEnabled: false` or the class, and only the queue measurement in
  `execution` settles it); the sentence is the part that overstates how it was decided.

### The dogfood run's tool-side items: a record cap that hid a field, a dialog that faked a success, and real input (round 6)

Everything here comes from the engraving job's own report (`§15`, `§16`, `§21`), where each item is a measurement
made while driving this tool against a real project.

### Added
- **`file read --tree --tree-fields <n|all>`, and `--tree-limit` bounds records only.** Measured: with
  `--tree-limit 2` every event showed its first two fields, so a reader concluded that
  `GradualTempoChangeEventDefinition` has no `duration` — it has one, and the note events of the committed fixture
  carry it too. A record's field list is the shape of that record, so it is reported whole unless the caller says
  otherwise, and the payload carries `fieldLimit` beside `limit` because they are two numbers now.
- **`blockedByDialog` on every payload that carries `execution`.** True when the dialog the payload reports is one
  *this invocation* raised (`openMs == 0`, i.e. no earlier invocation had seen that window). Measured: a
  `command send` of `MouseEdit.ChangeEventPositionOrDuration?NewDuration=4` answered `kOK`, `execution.state` read
  `ran` — the log shows the posting and the execution — and nothing in the document changed, because the send had
  raised Dorico's `dorico.error` box and every later command was posted and not executed. The field is always there
  (false included, so a caller can assert its presence), and `blockedByDialogNote` spells out what it voids.
- **`enabled` and `invokable` on every element** of `ui panels`, `ui dialog` and their re-reads (`stateAfter`,
  `dialog.controls`), and `enabled` on a dialog's buttons. `enabled` is UI Automation's own `IsEnabled` — the fact
  the window states for itself as `button "应用" [disabled]` while this tool could not — and a
  disabled target is now refused with **`ElementDisabled`** (exit 1) instead of answering `ok: true` for an invoke
  that does nothing.
- **`ui press --commit-input`**: after the value-pattern write, the control is clicked and the text goes in as real
  keystrokes (`SendInput`), because a Qt field accepts a value-pattern write without emitting the change signal its
  dialog listens for — measured on the 工程信息 dialog, the write read back while 应用 stayed disabled and the value
  reverted. The payload separates the two claims: `written` (the value pattern took the text), `commit` (the click,
  the key count, the value read back afterwards and whether it matches) and `committed` (both). It needs an
  interactive desktop, needs `--value` (`--path` is refused), is refused for anything that is not a text field
  (`InvalidArgument`), and exits 2 with `InputNotCommitted` when the input pass cannot run at all — carrying the
  write that did happen, so a caller cannot read it as "nothing was written".
- **`command send --wait-for-completion [--completion-timeout-ms n]`**: waits, bounded (default 90 s), for this
  command's own `notifyPostCommandExecute` line in `%APPDATA%\Steinberg\Dorico *\application.log` — the recipe the
  report's runbook used by hand — and reports `completion` with `completed`, `timedOut`, `waitedMs`, `budgetMs`,
  `posted` and `completedLines`. Measured command times span 4.4 s (`Instrument.MoveToPlayer`) to 54 s against 25 s
  for `Players.DeleteEmpty`, so a caller that sleeps a fixed amount either comes back early or wastes the
  difference. A completion line whose logged name refines the command's (`File.Export.Text` for `File.Export`, which
  is the line in `docs/scripting.md`) counts as that command; the reverse does not.
- **`project layout-defaults` closes the dialog it raised**: 关闭 is matched by its label in a fresh read
  (`PanelSurface.PressByLabel`), never 应用, and the payload's `closed` says what was pressed, how many reads it took
  and why nothing was pressed when the label was missing or shared. The exact, unique label match is the rule, so an
  ambiguous label presses nothing rather than the first candidate.
- **The enum check is command-aware.** `Type` was one global row of seven accidentals, so
  `EventEdit.ChangeProperty?Type=…` and `UI.InvokePropertyChangeValue?Type=…` — whose `Type` is a property name, and
  which are the route the report used to set an event's length — were refused. A parameter whose meaning depends on
  the command is checked only for the commands it was measured on (`NoteInput.SetAccidental` for the accidentals),
  a value for any other command is reported as `not checked for this command`, and the refusal still names
  `--allow-unknown-enum`. `vocabulary.commandScoped` lists which parameters are command-scoped.
- **35 more checks in `VocabularyCheck`** (462 `ok`, 0 failed): a disabled button refused with `ElementDisabled` (and
  the enabled one beside it still pressable), the same refusal for a panel element, `--commit-input` applicability
  and its refusal, `enabled`/`invokable` in both element payloads and in a dialog's buttons,
  `blockedByDialog`'s four cases (its own dialog, one that was already up, one the store could not describe, none at
  all), the completion wait (a line arriving on a named poll, the refinement rule and its reverse, a timeout that
  still reports the posting, and an invocation that sent nothing by name), and the command-scoped `Type`.
- **Checks in `ProjectFileCheck`**: the committed fixture pins that one listed record still carries every field of
  that record (and `duration` among them) one record cap down, that `--tree-fields` is the cap that does truncate
  them and says so, and — on the reference corpus — that a tempo event's fields include `duration` whatever the
  record limit is.

### Changed
- **`--tree-limit`'s own description and the README say what it bounds** (records: the matched nodes and the children
  of each) and point at `--tree-fields` for the other thing.
- The two refusals that used to say "this build never clicks a coordinate" — the value write's and a dialog
  control's — are unchanged in the paths they describe, and `--commit-input` is the one place anything is clicked or
  typed, always opt-in, and the deviation from the older "no synthetic input" rule is named in the README.

### Documented, not fixed
- **`--commit-input` has not been run against a live Dorico here.** The measurement that justifies it is the failure
  it works around (a value-pattern write a Qt field accepts without telling its dialog), and the shape of the pass —
  click the control, Ctrl+A, type — is the one the report used through another tool. Until a live run exists, the
  payload's own report (`ran`, `foreground`, `valueAfter`, `valueMatches`) is the only claim it makes.
- **`command send --wait-for-completion` reads a delta of a file other writers share.** A completion line for the
  same command name from another invocation that overlaps this one would satisfy the wait, and the count of
  like-named lines is not tracked because one `command send` sends one command. `posted` and `waitedMs` are what a
  caller checks.

## [0.1.0] - 2026-10-11

The first release: a stateless JSON CLI that lets an agent or a script steer a running Dorico, and a from-scratch
decoder for `.dorico` files that needs no Dorico at all.

### Added
- **`session`** — `status`, `info`, `connect`, `events`, `probe`, `read`. A long-lived socket with the status and
  selection stream that Dorico pushes unprompted; `session probe` measures the connection itself, and `session
  read` reads the open score without saving it.
- **`host`** — `start`, `status`, `stop`, `restart`, `serve`. The long-lived process that owns the WebSocket and
  answers CLI requests over a per-user named pipe, so repeated invocations cost no handshake.
- **`command`** — `send`, `list`, `info`: the 1090 commands Dorico publishes, with their parameters, and a
  status read-back after anything that changes the score.
- **`note`** — `insert` with melodies, chords (`C5+E5+G5`), tuplets (`--tuplet 3:2`), rests, insert/overwrite mode,
  relative staff moves, `--from-start` to write from the top of the flow, and `--read` to prove the result by
  reading the running document (`--read --read-rebind` to decode a saved copy instead, which is exact and rebinds
  the document to it).
- **`file`** — `read`: decode a `.dorico` project from disk with no Dorico and no host. Notes with their MIDI
  number, spelling letter and derived accidental; dynamics; slurs; articulations; ties; beams; playing
  techniques; layout; lyrics; and a generic `--tree` escape hatch for everything else.
- **`project`, `options`, `library`, `playback`, `properties`** — flows and layouts, the 1565 engraving options
  (written values read back to prove them), the 50 library collections and their entities, expression maps, and
  what can be read from the current selection.
- **`export`** — `text` (unattended CSV of the score's text, including lyrics) and `musicxml` (which needs one
  click on 确定 in Dorico's dialog; the command watches the folder and reports the files that really appeared).
- **`version`** — the build, whether it is Native AOT, where state lives, the protocol constants, and `build`:
  `exposeUnsafeScriptRunApi` and `neverPersistSession`, read from the constants the code branches on, so a
  downloaded binary can be audited instead of trusted.
- **`--expect-score <id>`** on every command that changes a document: the host refuses to act when Dorico's active
  document is not the one named.
- **Tests** — `ProjectFileCheck` (187 checks against real projects and against Dorico's own MusicXML exports) and
  `VocabularyCheck` (77 checks; the table is built in, so it needs no Dorico and no extraction step). The checks
  look the maintainer's own scores up by file name — `tests/corpus/` first, then the folder(s) named by
  `DORICO_USE_TEST_CORPUS` — so the repository names no drive, and a machine that does not have those files reports
  `skip` for the 13 checks that need them instead of failing.
- Continuous integration: build, the two test projects, and a Native AOT publish on every push.
- `error.data.stack` when `DORICO_USE_DEBUG` is set: an unexpected failure then carries its stack trace, which is
  what a bug report needs and what the JSON message alone never has.
- `tools/smoke.ps1` — the live surface (`session`, `host`, `project`, `options`, `library`, `playback`,
  `properties`, `command`, `note`, `export`, `session read`) run by hand against a running Dorico, one PASS/FAIL
  line per check with the JSON it saw. It writes nothing without `-AllowWrite`.
- `.github/workflows/release.yml` — a `v*` tag builds, checks, publishes, refuses to publish a binary whose build
  flags are not both false, and attaches the exe with `README.md`, `LICENSE`, `NOTICE` and `CHANGELOG.md` to the
  release.

### Changed
- **`session read` no longer saves anything by default.** It reads the open document through a small read-only Lua
  script Dorico runs (`Script.RunScript`), so a read has no side effect: the previous behaviour — `File.SaveAs`,
  which rebinds the open document to the copy it writes — is now `session read --rebind`, opt-in and reported.
  The live payload says `source: "live"` and comes with caveats naming what that route cannot see (no MIDI number,
  accidental, dynamic value or layout; the note lines are Dorico's printer text, parsed, with unreadable lines
  counted in `counts.unparsedLines`).
- **`note insert --read` no longer rebinds the document.** Its default read is now the live read — the same
  read-only script `session read` runs — so a write-and-prove call has no side effect at all; the previous
  behaviour, `File.SaveAs` followed by a decode of the copy, is now `--read --read-rebind`, opt-in and reported.
  `--read-to`, `--measure` and `--player` belong to that route: without `--read-rebind` they are an
  `InvalidArgument` that points at `session read --rebind`.
- **`command send` refuses any name in the `Script.` family.** The layer is undocumented and probing it has crashed
  Dorico here (`docs/scripting.md` §8), so it is compiled out of every released build and only a build made with
  `-p:ExposeUnsafeScriptRunApi=true` can send it. `version` reports the flag.
- **The session token is stored in plain text, and the tool now says so.** `session connect`'s payload and
  `version.state.session.token` name the file and say that `host stop` does not remove it — and a build made with
  `-p:NeverPersistSession=true` keeps the token in the host's memory instead, ignores a leftover one, and reports
  that too.
- The enum values the CLI validates are now a **built-in table** instead of a per-user file produced by an
  extractor script: `command send`, `note insert --duration/--grid` and `options set --value` catch a typo out of
  the box, with no setup step, and only for the parameters this build knows — a value for any other parameter goes
  to Dorico unchecked, so a newer Dorico's spellings are never refused.
- The vocabulary extractor and the `%LOCALAPPDATA%\dorico-use\vocabulary.json` mechanism are gone, along with the
  need for Python in the tests.
- The build enforces the house style and the analyzers in all three projects (`AnalysisLevel`,
  `EnforceCodeStyleInBuild`, `TreatWarningsAsErrors`): a warning now fails the build, which is how the 0-warning
  bar stays where it is.

### Thanks
- Thanks to [Dorico.Net](https://github.com/scott-janssens/Dorico.Net) and the Dorico forum authors whose
  measurements made this possible.

### Note
- Windows is the only platform tested. Nothing in the tool uses a Windows-only API, so a macOS port is a
  contribution waiting to happen rather than a rewrite — see `CONTRIBUTING.md`.
- A generic "send any protocol message" command is deliberately **not** shipped.
- Tested against Dorico Pro 6.2.30 on Windows 11.
