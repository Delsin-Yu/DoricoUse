# Dorico scripting — what the Lua layer really is

Measured on **Dorico Pro 6.2.30 b6245** (Windows 11, Chinese UI) on 2026-10-10, against the running
application, plus a read-only excavation of `Dorico6.exe` (154 MB, v6.2.30, build dated 2025-05-29).

**Verdict on the claim that "Dorico scripting is deliberately limited — scripts can essentially only replay
recorded user actions":**

* **As a description of what the *recorder* produces, the claim is exactly right** — I recorded a macro and the
  file it wrote was three lines of `app:doCommand(...)` and nothing else (§3F).
* **As a description of the *API*, the claim is wrong, but only just.** There is a real luabind object model with
  score → flow → stream → event navigation, and it can read notes (`EventStream:dump()` returned
  `488 (488) dur: 2: kNoteEvent (id:68941) at 488, duration 2  G3 (ends 490)`), and it has genuine
  *writing* methods (`createNote`, `copyRange`, `moveRange`, `deleteEventsInRange`, `addPlayer`, …).
* **The reason the claim feels true is that the useful half is stubbed on purpose.** The accessor everyone wants —
  `voiceStream.events` — throws
  `Not implemented - this may leave the score in an unpredictable state`, and `Instrument.name` throws the same.
  There is no documentation, no selection API, and no way to see any of this in the app. The experienced
  practitioners on the Steinberg forum describe it exactly as a macro layer (§4).

So: **not a macro recorder, but a deliberately half-built object model with a macro recorder bolted on the front.**
Both halves are real and both are measurable.

---

## 1. Where it is, and what a script file is

| Thing | Finding | Evidence |
|---|---|---|
| Script directory | **There is no (non-localized) `Scripts` folder and no scripts-folder preference.** `preferences.xml` has exactly two script settings: `lastRunScriptPath` and `scriptConsoleAlwaysOnTop`. The `Scripts` string in `Dorico6.exe` is ICU/CLDR locale data (`Languages`/`Scripts`/`Countries`), not a path. | §2 |
| Script plug-ins | Plug-ins live in a **`Script Plug-ins`** user-settings folder next to `Project Templates`/`Space Templates`. The folder name is **localized** (`AutoSave` appears on this machine as `自动保存`), and the folder does not exist here because nothing has been installed. Import/Export User Settings has a matching `ScriptPlugins` group (`setImportScriptPlugins` / `hasScriptPlugins`). | §2 |
| What a script file is | Two shapes. (a) A **recorded/plain script**: free-standing Lua, run with `Run Script…`. (b) A **plug-in**: a directory holding `Info.lua` whose table declares `MenuCommands` entries with `name` and `handler`; those appear in a dynamic submenu (`Steinberg::Steam::ScriptPluginsDynamicMenuContentProvider`). | §2 |
| Recorder output | `usermacro.lua`, written wherever the Script menu's Save As dialog points (it defaulted to the folder of the previous `Run Script…`). | §3F |
| Runtime | Lua **5.2** in-process. Full `io`, `os`, `debug`, `load/loadfile/dofile/require`, `coroutine`, `bit32`. Bundled Lua modules on disk: **Penlight** (`lua/pl/*`) and **LuaSocket** (`lua/socket.lua`, `ltn12`, `url`, `mime`, `smtp`, `ftp`, `http`, `tp`). A bundled **`debugger.lua`** + `debugintrospection.lua` are compiled into the exe and wired to `Script.AttachDebugger`. | §3A |
| Commands | `Script.RunScript?ScriptPath=…&ShowConsole=false`, `Script.RunLastScript`, `Script.ShowConsole`, `Script.StartRecordingMacro`, `Script.EndRecordingMacro`, `Script.AttachDebugger`. Default key command: **only** `Script.RunLastScript` = `Ctrl+Alt+Shift+R`. There is **no** default shortcut for the console. | §2 |

**Where a script goes: nowhere in particular.** The app never looks in a fixed folder for ordinary scripts — you
point `Run Script…` (or `Script.RunScript?ScriptPath=`) at any `.lua` file anywhere, and the recorder ends by asking
you where to put its output. The only folder with a fixed meaning is the localized **`Script Plug-ins`** folder in
`%APPDATA%\Steinberg\Dorico 6`.

---

## 2. Offline evidence (read-only, from the binary)

Method: one `grep -a -o -E '[ -~]{4,}' Dorico6.exe > exe-strings.txt` (1,856,118 strings, ~3 min), then greps over
that. Nothing was written to Dorico's own directories.

* **Lua/luabind are statically linked**, not a DLL: `luaL_openlibs`, `luaL_newstate`, `lua_pushcclosure`,
  `Unable to get main thread, luabind::open() not called?`, `luabind does not support class names with extra nulls`.
* **The script sources are named in the binary**, and give the shape of the layer:
  `D:\T1\work\112ea1c09b2f11cc\steam\source\script\{scripthost,scorewrapper,streamwrapper}.cpp`,
  `...\apps\dorico\source\ui\dialogs\scriptconsole.cpp`, `...\ui\controls\qscriptconsole.cpp`.
* **The registration table is in the strings**, in order, and is the single best map of the API (each class name
  followed by its member names, in registration order). Extracted names, grouped:

  ```
  DoApp   : doCommand, save, versionString, currentScore, currentLayoutID
  DoUI    : showFolderChooser
  ScriptHost (DoCore) : currentScore, engine, save, showFolderChooser
  ScoringEngine       : defaultLibrary, userLibrary
  Score (DoScore)     : openScoreID, versionString, currentLayoutID, libraries,
                        flows, players, layouts, instruments, addPlayer, addEnsemble
  Flow                : id, players, instruments, allStreams, voiceStreams, timebaseStreams,
                        addPlayer, removePlayer
  Layout              : id, players, addPlayer, removePlayer, addFlow, removeFlow
  EventStream         : id, events, ossiaType, dump, delete, getPropertyNames,
                        findBlockInstanceAtPoint, addBlockInstance, removeBlockInstance,
                        splitStream, deleteEventsInRange, copyRange, moveRange, copyRangeLinked, joinBlocks,
                        createNote, createRest, createGraceNote, createBarRest, createTuplet,
                        createBarDivision, createTonalityDivision, createDynamicGroup, createText,
                        createClef, createTransposingClef, createOttavaLine, createFrameBreak, createSystemBreak
  events              : GraceNoteEvent, BarRestEvent, TupletEvent(tupletDef), BarDivisionEvent,
                        TonalityDivisionEvent, DynamicGroupEvent, TextEvent, ClefEvent, OttavaLineEvent,
                        BreakEvent(isFrameBreak, isSystemBreak), Event/EventBlock/EventBlockInstance,
                        PropertyContext, EventPropertyEntry, EventPropertyTable
  libraries           : ClefLibrary, OttavaLineLibrary, EnsembleLibrary, PenStyleLibrary, FontStyleLibrary,
                        BrushStyleLibrary, InstrumentLibrary  (each: items, find)
  helpers             : WesternPitchTransposer.{transpose,ensureLegalPitchAndNoteNameCombination,transposeNoteNameAndAccidental},
                        LengthUtils.getRhythmicDurationForRational, MIDIPitch, MIDIPitchWithSpelling,
                        NoteName(kNoteC…kNoteB, fromString), Interval, TupletDefinition, NotatedLength,
                        DynamicIntensity, AccidentalType, LogDuration, CaretLocation, StepTimeInput, Variant
  ```
* **The dumper's own output formats** are in the binary too, which is how `dump()` can be recognised as Dorico's
  own printer rather than something a script invented: `Start block id: … position: …`, `kNoteEvent`, `, pitch: `,
  `Notehead at pos: `, `EventStream ID: … kGlobalTimebaseStream`, `Flow ID: %d`, `Layout ID: …`,
  `FlowInstrument, Flow ID: …`, `VoiceNotationGraph for logical staveID: …`.
* **Script plug-ins**: `Steinberg::Steam::ScriptPluginsDynamicMenuContentProvider`,
  `Info.lua`, `.+lua$` (the file-matching regex), `Lua Script Files`, `Choose a Lua Script to Run`,
  `hasScriptPlugins`, `setImportScriptPlugins`, `kScriptPlugins`.
* **Deliberate stubs are visible as a message**: `Not implemented - this may leave the score in an unpredictable
  state` (emitted from `scorewrapper.cpp:671` and `streamwrapper.cpp:162` at run time — see §3D).

---

## 3. Live measurement (transcript)

### 3.0 How the console was driven

The Script menu (Chinese UI: **脚本**) is the **last** menu-bar item and its access key is **Alt+P**
(`accessKey: "Alt+P"`, UIA automationId `DoricoQApplication.DocumentWindow.menuBar.QAction`). It contains, in order:

```
运行脚本…                       (Run Script…)
运行最新脚本   Ctrl+Alt+Shift+R  (Run Last Script)
────
开始录制宏                       (Start Recording Macro)
结束录制宏                       (End Recording Macro)
────
控制台                           (Console)
附加调试程序                     (Attach Debugger)
```

There is **no "Script Plug-ins" entry** — it appears only when a plug-in is present.

The console window (`脚本控制台`, class `Qt6101QWindow`) is a splitter with **two panes**:

| UIA element | automationId | Meaning |
|---|---|---|
| top, read-only | `…ScriptConsole.QSplitter.LogViewWidget` | output; its `Value` is the **machine-readable transcript** |
| bottom, editable | `…ScriptConsole.QSplitter.CommandEntryWidget` | the code editor (line-numbered) |
| checkbox | `…ScriptConsole.DoricoCheckBox` | 总是靠上 / always on top (`scriptConsoleAlwaysOnTop`, default **true**) |
| button | `…ScriptConsole.QPushButton`, label `执行 (x)`, accessKey 回车 | **execute** |

**Enter inside the editor inserts a newline; it does not execute** (measured: the editor went from line 1 to
line 2 and the log pane did not change). Execution is the 执行 button.

Everything below was run by typing **one short line** into the editor and clicking 执行:

```lua
dofile("<repo>/.spinney/scripting/consoleN.lua")
```

with the actual probes in files on disk. Full transcripts are in `.spinney/scripting/consoleN.out`
(that directory is gitignored, so the decisive ones are quoted verbatim here).

### 3A. Environment and the DoApp instance

Input (`console1.lua`), verbatim output:

```
=== console1 ===
_VERSION Lua 5.2
io table os table dofile function require function
DoApp table DoCore table DoScore table DoUI table
DoApp.DoApp() -> userdata
--- names on the DoApp instance ---
app.doCommand -> function
app.engine -> nil
app.versionString -> string
app.currentScore -> userdata
app.createNewScore -> nil
app.save -> function
app.showFolderChooser -> nil
app.convertFolderOfScores -> nil
app.openScoreID -> nil
app.currentLayoutID -> number
app.libraries -> nil
app.addEnsemble -> nil
app.flows -> nil
app.flowElements -> nil
app.players -> nil
app.layouts -> nil
... (all other probed names -> nil)
```

Reading, in the same run: `app.versionString = 6.2.30b6245`, `app.currentLayoutID = 0`,
`app.currentScore.openScoreID = 10`.

Notes: on an **instance**, an unknown member returns `nil` (no error). On a **class-rep** (`DoApp.DoApp` the table
itself, `DoCore.NoteEvent`, …) it raises `no static 'X' in class 'Y'` — which is why the earlier probing pass in
this repo concluded "nothing resolves": it was only ever poking class-reps, never an instance.

### 3B. The object graph — real, navigable, and it *is* the score

`console2/3/7` walked the graph. Verbatim (`console3.out`):

```
app.versionString 6.2.30b6245 app.currentLayoutID 0
score.openScoreID 10
score.flows :: table len=1
score.players :: table len=36
score.layouts :: table len=37
score.instruments :: table len=36
...
score.players (table, len=36) {
  [1] Flute 1
  [2] Flute 2
  [3] Oboe 1
  [4] Oboe 2
...
score.flows (table, len=1) {
  [1] Flow ID: 0, 1 timebase streams, 37 stave streams, 39 voice streams,  36 player streams
...
score.layouts (table, len=37) {
  [1] Layout ID: 0, 36 players, 1 flows
  [2] Layout ID: 37, 1 players, 1 flows
...
```

Those numbers — 36 players, 37 layouts, one flow, and the player names — are the `Bad Apple!!` score this machine
has open, and they agree with what `file read` reports for the same file (`players: 36`, `layouts: 37`). The object
strings (`Flute 1`, `Flow ID: 0, …`) are Dorico's own `tostring` output; a script needs no parsing to enumerate
players/layouts/streams.

Streams are addressable and countable:

```
  allStreams[3] EventStream ID: 2 kVoiceStream, 574 events
  allStreams[31] EventStream ID: 30 kPlayerStream, 0 events
  allStreams[110] EventStream ID: 122 kAutomationStream, 0 events
```

Libraries are navigable too: `score.libraries` → `ScoreLibraries` → `instruments.items` (len 28), `clefs.items`
(len 6), `ensembles.items` (len 0), `ottavaLines.items` (len 1), each with `find`.

### 3C. Reading notes — the thing the Remote Control API cannot do

`voiceStream.events` is refused (§3D), but **`dump()` is not**. `console5.out`, verbatim:

```
=== console5 ===
stream = EventStream ID: 123 kVoiceStream, 5 events
dump ok=true type=string bytes=434
DUMP| ------------------------------
DUMP| Start block id: 1125 position: 0
DUMP| 488 (488) dur: 2: kNoteEvent (id:68941) at 488, duration 2  G3 (ends 490)
DUMP| 492 (492) dur: 2: kNoteEvent (id:68953) at 492, duration 2  G3 (ends 494)
DUMP| 496 (496) dur: 2: kNoteEvent (id:68965) at 496, duration 2  G3 (ends 498)
DUMP| 500 (500) dur: 2: kNoteEvent (id:68977) at 500, duration 2  G3 (ends 502)
DUMP| 504 (504) dur: 2: kNoteEvent (id:68989) at 504, duration 2  G3 (ends 506)
dump lines = 7
stream EventStream ID: 2 kVoiceStream, 574 events dump lines=576 event kinds:
   kNoteEvent x516
   kSlurEvent x57
   kTrillEvent x1
```

So a script can read, for every voice stream in the score: event kind, event id, block id, position, duration,
pitch and end position — and can summarise 574 events by kind in one pass. `dump()` **returns the string**, so it
can be parsed with Lua patterns rather than merely printed. Positions and durations are in the score's own rational
units — the same units `file read` reports (crotchets); I did **not** cross-check the unit or the voice-stream →
stave mapping against the project file here, so treat the absolute values as Dorico's, and the shape as the finding.

The same works on the timebase stream (bar divisions, tonality, tempi, rehearsal marks):

```
--- timebaseStreams[1]:dump() ---
dump() -> ok=true ret=------------------------------
Start block id: 1000 position: 0
0 (0): kTonalityDivisionEvent (id:29540) at 0  element ID = 0
0 (0): kBarDivisionEvent (id:29539) at 0  element ID = 0
0 (0): kImmediateTempoChangeEvent (id:60990) at 0
184 (184): kTonalityDivisionEvent (id:49797) at 184  element ID = 11
...
300 (300): kRehearsalMarkEvent (id:69445) at 300
...
```

### 3D. What is deliberately stubbed

Not everything registered is implemented. `console4.out`, verbatim:

```
FULL STUB ERROR: ok=false err=std::exception: 'Not implemented - this may leave the score in an unpredictable
state @ D:\T1\work\112ea1c09b2f11cc\steam\source\script\scorewrapper.cpp:671'

--- stream EventStream ID: 2 kVoiceStream, 574 events ---
stream.events -> ok=false type=string err=std::exception: 'Not implemented - this may leave the score in an
unpredictable state @ D:\T1\work\112ea1c09b2f11cc\steam\source\script\streamwrapper.cpp:162
```

* `Instrument.name` → **stubbed** (same message, `scorewrapper.cpp:671`). `Instrument.id` is real (`number`).
* `kVoiceStream.events` → **stubbed** (`streamwrapper.cpp:162`). This is the accessor a script author would reach
  for first.
* `DoCore.ScriptHost.engine` → `ScoringEngine` resolves, but of the names registered for it only
  `defaultLibrary` and `userLibrary` actually resolve; `createNewScore`, `convertFolderOfScores`, `save`,
  `showFolderChooser`, `currentScore` resolve as `ScriptHost` members instead
  (`sh.currentScore`, `sh.save`, `sh.showFolderChooser` are all real).
* **`DoUI` is essentially empty.** `DoUI.DoUI()` gives an object printed `DoUI` whose only member among ~50
  probed names is `showFolderChooser`. There is no `showMessage`, no `getUserInput`, no dialog control, and — the
  one that matters for scripting — **no selection API anywhere in the layer**. This is why recorded macros select
  by screen coordinates (`Edit.SelectAtPoint?X=…&Y=…`) and why a script cannot ask Dorico what you have selected.
  (Transcript: `console6.out`, `console7.out`.)

The wording is the finding: these are not missing bindings, they are **decisions** — Dorico refuses to hand out
state it cannot hand out safely.

Real callable statics do exist, and their C++ signatures leak through luabind's own error messages, which is how
you can discover the API without documentation:

```
LengthUtils.getRhythmicDurationForRational() -> ERR No matching overload found, candidates:
bool getRhythmicDurationForRational(RhythmicLength const&,NotatedLength&)
WesternPitchTransposer.transpose() -> ERR No matching overload found, candidates:
custom [class Steinberg::Steam::OptionalStackValue<class Steinberg::Steam::MIDIPitchWithSpelling>]
  transpose(MIDIPitchWithSpelling const&,Interval const&,bool)
DoCore.NoteName.fromString -> function
DoCore.NoteName.kNoteC -> number
```

### 3E. `app:doCommand` — scripting ⊇ Remote Control

A single console line was enough:

```lua
print(DoApp.DoApp():doCommand("File.Export.Text?File=<repo>/.spinney/scripting/from_lua.csv&Show=false"))
```

`application.log`:

```
2026-10-10 22:39:25.704 [info] Executing command: File.Export.Text?File=<repo>/.spinney/scripting/from_lua.csv&Show=false
2026-10-10 22:39:27.598 [info] notifyPostCommandExecute: File.Export.Text?...&Show=false (1894 ms)
```

and a 133 KB CSV appeared whose first rows name the open project
(`C:/scores/Bad Apple!! (JAGMO Shiro v1.2 2026.10.08).dorico`). `doCommand` is the **same 1090-command
catalogue** the Remote Control API posts to, from inside the process, so a script can invoke every action the API
can — and it is not rate-limited by a socket.

### 3F. The recorder — the user's claim, measured

I started the recorder from the Script menu and pressed one harmless key. The Script menu's own state is the first
evidence: **结束录制宏 (End Recording Macro) is greyed out normally and enables while recording.**

Then the recorder asked where to save, via a standard Save As dialog:

```
title    : 另存为
folder   : the folder of the previously run script
文件名    : usermacro.lua
保存类型  : Lua Script Files (*.lua)
```

and the file it wrote was, **in full** (154 bytes):

```lua
local app=DoApp.DoApp()
app:doCommand([[EventEdit.NavigateDown]])
app:doCommand([[EventEdit.NavigateDown]])
app:doCommand([[EventEdit.NavigateDown]])
```

That is the whole artifact: one `doCommand` line per recorded action, no state, no arguments beyond the command's
own `?Key=Value` syntax, no branches, no reading of anything. A recorded macro is a **keystroke log in Lua syntax**.
This is why the community's description ("macros only", "replays recorded actions") is accurate about everything a
user normally sees.

### 3G. What I did to the running Dorico (full disclosure)

Never saved, never closed, never edited the user's score. Specifically:

* The console window (`0x00D70C6E`) was found **hidden**, was shown twice, and is **hidden again** now.
* `Script.StartRecordingMacro` / `Script.EndRecordingMacro` ran once; recording is **stopped** (the menu item is
  greyed again). The macro was saved to `.spinney/scripting/usermacro.lua`, not to the dialog's default folder.
* Seven keystrokes intended for an open menu landed in the score instead, while the menu was **not** actually open:
  `EventEdit.NavigateDown` ×7 in `application.log` (22:37:05 and 22:38:14–15). That **moved the selection and
  auditioned notes** — it did not modify, insert or delete anything, and nothing was saved. The score still shows
  `*` exactly as it did before.
* `File.Export.Text` was called once to prove `doCommand`; it wrote a CSV of text/tokens into
  `.spinney/scripting/from_lua.csv`. Read-only with respect to the score.
* `run-in-console.sh` was learned the hard way: **Alt+P is unreliable for opening the Script menu**; a
  message-click on the 脚本 menu item at window (746, 49) opens it every time, and after a mouse click the
  highlight may not start on the first item — always confirm with a screenshot before pressing Enter.

---

## 4. What the web says (clearly separated — none of this is my measurement)

Links are to the Steinberg forum unless noted.

* **"Any Updates on Robust Lua Support?"** — https://forums.steinberg.net/t/any-updates-on-robust-lua-support/1036817
  (2026-07). A Finale refugee: *"I have a large number of Dorico automation macros but they only cover a tiny
  fraction of the ground for my Sibelius scripts."* Replies report that improving scripting is **on the roadmap**
  (Daniel Spreadbury is quoted as having said so repeatedly), with the counter-argument that Dorico deliberately
  avoids the "vicious cycle" where third-party plug-ins paper over gaps in the app. Also noted: Robert Piéchaud,
  who designed Finale's scripting language, was hired — for **playback**, per the Scoring Notes interview.
* **Alexander Ploetz, "Introducing ConsoleTools"** — https://forums.steinberg.net/t/introducing-consoletools-a-lua-framework-to-make-dorico-scripts-more-accessible/848477
  (2023-05). The most experienced Dorico-Lua developer, writing about his framework:
  *"What ConsoleTools is NOT capable of is getting around the current limitations of scripting. It does not have
  meaningful access to data from Dorico, because that kind of access simply does not exist for the Lua
  environment."* ConsoleTools exists to make key-command binding, argument passing and cross-script composition
  work — i.e. to compensate for the *missing* API, not to extend it.
* **"Scripting with Lua"** — https://forums.steinberg.net/t/scripting-with-lua/916315 (2024-05). *"There is no
  documentation of the Lua Dorico setup"* (Derrek). On reading state (benwiggy): *"there is no mechanism for
  'reading' the status of a Dorico document, e.g. what is currently selected; what properties are set."* My §3C
  shows that is **not literally true** — but nobody has documented or publicised the way in, and the obvious door
  (`stream.events`) is nailed shut, so the community's working assumption is what it is.
* **"I wonder, has anyone successfully written a LUA script in Dorico 6 that takes user input?"** —
  https://forums.steinberg.net/t/i-wonder-has-anyone-successfully-written-a-lua-script-in-dorico-6-that-takes-user-input/1009034
  (2025-09). No `dorico.getUserInput` / `showMessage` exists; `DoUI` really does expose almost nothing (§3D:
  exactly `showFolderChooser`). Accepted answer: you cannot prompt; edit constants in the file and re-run.
* **"Surprises with Lua scripts"** — https://forums.steinberg.net/t/surprises-with-lua-scripts/998242 (2025-07).
  Practical catalogue of `doCommand` behaviour, including a genuine bug: `NoteInput.RestMode?Set=false` behaves as
  a *toggle*, not an assignment, so it cannot be used safely from a script. Confirms the everyday style is
  `local app=DoApp.DoApp()` + `app:doCommand([[…]])`.
* **"Scripting project"** — https://forums.steinberg.net/t/scripting-project/149053 (2020-05). Pre-history: because
  "complete scripting support is not in 3.5 yet and nowhere near on the roadmap", the accepted workaround in the
  community was to export MusicXML, process it in Python/`music21`, and re-import.
* A user's real recorded macro (independent confirmation of §3F's shape, including the `--[[ generated by macro ]]`
  comments and the coordinate-based selection that the absence of a selection API forces):
  https://github.com/Tamzejian/luadorico — `myFirstMacro.lua`, README ("Console : CTRL+SHFT+C" — that is the
  author's *own* shortcut, not a Dorico default).

---

## 5. What I could not determine

1. **Whether the write methods actually write.** `createNote`, `createRest`, `createText`, `createDynamicGroup`,
   `createTuplet`, `copyRange`, `moveRange`, `deleteEventsInRange`, `splitStream`, `joinBlocks`, `delete`,
   `Score.addPlayer`/`addEnsemble`, `Flow.addPlayer`/`removePlayer`, `Layout.addPlayer`/`addFlow`… are all present
   as bound functions (measured), but I called none of them: the only document I was allowed to touch was the
   user's, and a failed write there is not recoverable by me. The runbook (§6, step 9) tests this on a throwaway
   project.
2. **`sh.engine:createNewScore`** — the Remote Control API cannot create a project at all (`File.New` is not even
   in the catalogue), and `createNewScore` is registered, but I did not call it (it would have added a document,
   and I could not rule out a modal template chooser).
3. **The plug-in contract, end to end.** No `Script Plug-ins` folder exists on this machine, so I have
   `Info.lua` + `MenuCommands` + `name`/`handler` + `ScriptPluginsDynamicMenuContentProvider` from the binary and
   nothing executed. The menu shows no plug-in entry today, which is consistent.
4. **The full member list of every class.** I probed ~50 candidate names per object, not an exhaustive enumeration:
   luabind's own `class_names`/`class_info`/`class_info_data` are not usable from Lua (measured errors in
   `console2.out`: `No matching overload found, candidates: luabind::object class_names(lua_State*)`), and the
   luabind registry keys (`__luabind_class_map`, `__luabind_classes`) are sealed `userdata`. So the binary's string
   table (§2) plus a name-guessing sweep is the best available map; **names I did not guess may exist.**
5. **`CaretLocation` / `StepTimeInput`.** Both classes are registered (`DoCore.CaretLocation`,
   `DoCore.StepTimeInput` with `advance`, `caret`, `fromText`) but I never obtained an instance, and there is no
   visible route to one — which is the same wall the Remote Control API hits on caret positioning.
6. **Whether `dump()` output is guaranteed/stable.** It is Dorico's internal debug printer, not a published
   format; it could change between builds.

---

## 6. Runbook — what you have to do by hand

Everything here is safe to run. Steps 1–8 are read-only. **Step 9 is the only one that writes, and it must be run
on a throwaway project.**

### 1. Open the console (no mouse needed, but the mouse is more reliable)

* Fastest reliable way: **click the 脚本 (Script) menu** at the far right of Dorico's menu bar (it is the last
  item, right of 帮助). Then press `↓` until **控制台** is highlighted — it is the 5th item, below 结束录制宏 —
  and press `Enter`.
* Alternative: `Alt+P`, then `↓`×4, then `Enter`. Verified working, but the Alt+P route failed intermittently.
* There is no default key command for the console. If you want one, set it yourself in
  **编辑 ▸ 快捷键** (Preferences ▸ Key Commands) by searching for `Script.ShowConsole`.
* What you should see: a window titled **脚本控制台**, split in two — dark output pane on top, numbered code
  editor below, a **总是靠上** (always on top) checkbox bottom-left and **执行 (x)** bottom-right.

### 2. Get a transcript you can copy

The output pane is a read-only text box: `Ctrl+A`, `Ctrl+C` copies everything. Or use the
`dofile(...)`-a-file trick in step 4 and have the file write its own `.out` next to it (see the probes in
`.spinney/scripting/console*.lua` for the exact pattern: build a `lines` table, `print` each line, and also
`io.open(path,"w"):write(table.concat(lines,"\n"))`).

### 3. Paste this first

```lua
local app = DoApp.DoApp()
print(_VERSION, type(app), app.versionString, app.currentLayoutID, app.currentScore.openScoreID)
```

**Look for:** `Lua 5.2 userdata 6.2.30b6245 <n> <n>`. If the second value is not `userdata`, the layer is not
loaded. Then press **执行 (x)**. **Enter inside the editor does not run anything — it only adds a line.**

### 4. The whole-score census (paste, run, read)

Save this as e.g. `H:/tmp/census.lua` (any path) and paste one line into the editor:

```lua
dofile("H:/tmp/census.lua")
```

`census.lua`:

```lua
local app = DoApp.DoApp()
local s = app.currentScore
print("score id", s.openScoreID, "build", app.versionString)
print("flows", #s.flows, "players", #s.players, "layouts", #s.layouts, "instruments", #s.instruments)
for i, f in ipairs(s.flows) do print("flow", i, f) end
for i, p in ipairs(s.players) do print("player", i, p) end
for i, l in ipairs(s.layouts) do print("layout", i, l) end
local fl = s.flows[1]
print("streams: all", #fl.allStreams, "voice", #fl.voiceStreams, "timebase", #fl.timebaseStreams)
```

**Look for:** the counts and names of *your* project (36 / 37 / 36 / 1 on the Bad Apple score), lines like
`EventStream ID: 2 kVoiceStream, 574 events`.

### 5. Read the notes (the part the Remote Control API cannot do)

```lua
local st = DoApp.DoApp().currentScore.flows[1].voiceStreams[1]
local d = st:dump()
print(d:sub(1, 800))
```

**Look for:** `kNoteEvent … at 488, duration 2  G3 (ends 490)`. `dump()` returns the string, so you can filter it
in Lua instead of printing it:

```lua
local st = DoApp.DoApp().currentScore.flows[1].voiceStreams[1]
local n = 0
for line in st:dump():gmatch("[^\r\n]+") do
  if line:find("kNoteEvent") then n = n + 1; if n <= 10 then print(line) end end
end
print("note events:", n)
```

**Look for:** note count ≈ the number of note events in that voice, each line carrying position, duration, pitch
and the event id.

### 6. See the wall, so you know where it is

```lua
local st = DoApp.DoApp().currentScore.flows[1].voiceStreams[1]
print(pcall(function() return st.events end))
```

**Look for:** `false  std::exception: 'Not implemented - this may leave the score in an unpredictable state @
…\streamwrapper.cpp:162'`. That message is the honest summary of this whole document.

### 7. Prove `doCommand` (harmless, writes a file)

```lua
print(DoApp.DoApp():doCommand("File.Export.Text?File=H:/tmp/from_lua.csv&Show=false"))
```

**Look for:** `kOK` printed, then `H:/tmp/from_lua.csv` a moment later, and
`Executing command: File.Export.Text?…` in `%APPDATA%\Steinberg\Dorico 6\application.log`.
Read-only with respect to the score.

### 8. Record a macro and look at what it is

1. 脚本 menu ▸ **开始录制宏**. (The **结束录制宏** item enables — that is the only on-screen sign it is recording.)
2. Do two or three ordinary things in the score, e.g. press `↓` twice.
3. 脚本 menu ▸ **结束录制宏**. A **另存为** dialog appears with the file name pre-filled as **`usermacro.lua`**
   and the folder defaulting to wherever you last ran a script. Point it anywhere, e.g. `H:/tmp/usermacro.lua`,
   and press **保存**.
4. Open the file. **Expect:** `local app=DoApp.DoApp()` followed by one
   `app:doCommand([[…]])` line per action you performed — and nothing else. No state, no conditions, no reading.

### 9. The only writing experiment — **on a scratch project only**

**First** make a throwaway project (`Ctrl+N`, then add any player) and confirm its title does not contain your
real score's name. Then run, one statement at a time, checking after each:

```lua
-- 1. look at the API without calling it
local st = DoApp.DoApp().currentScore.flows[1].voiceStreams[1]
for _, n in ipairs({"createNote","createRest","createText","createDynamicGroup","copyRange",
                    "moveRange","deleteEventsInRange","splitStream","joinBlocks","delete"}) do
  print(n, type(st[n]))
end
```

**Look for:** `function` for each. Then, to find the real signature without guessing, call one with the **wrong
number of arguments** — luabind resolves overloads before it invokes anything, so a mismatch cannot touch the
score and answers with the signature (this is exactly how `LengthUtils.getRhythmicDurationForRational` and
`WesternPitchTransposer.transpose` were identified here):

```lua
print(pcall(function() return st:createNote() end))
```

**Look for:** `No matching overload found, candidates: …` followed by the C++ signature. Then re-run it **once**
with correct arguments on the throwaway project, and check the result with this repo's own reader:

```bash
./src/DoricoUse.Cli/bin/Debug/net10.0/dorico-use.exe session read --limit all --expect-score <the scratch id>
```

If the note you created shows up, §5.1 stops being an open question — and it becomes the first thing this project
can do that the Remote Control API cannot.

### 10. Script plug-ins (untested here — no plug-in exists on this machine)

1. In `%APPDATA%\Steinberg\Dorico 6`, create the folder whose name matches the **Script Plug-ins** entry of the
   Import/Export User Settings dialog (the folder name is localized, as `AutoSave` → `自动保存` shows; an English
   UI uses `Script Plug-ins`).
2. Put a directory in it containing `Info.lua`, returning a table with a `MenuCommands` array whose entries have
   `name` and `handler`.
3. Restart Dorico. **Look for:** extra entries in the 脚本 menu (today it has exactly the six items listed in §3.0;
   a plug-in adds a dynamic submenu via `ScriptPluginsDynamicMenuContentProvider`).

### 11. When you are done

Close the 脚本控制台 window (its ✕, or `Alt+F4` while it has focus — Dorico logs that as `Script.ShowConsole`).
Because **总是靠上** is on by default, an open console sits over the main window and will swallow clicks on the
menu bar; either close it or untick **总是靠上**.

---

## 7. Artifacts

| Path | What |
|---|---|
| `.spinney/scripting/console1.out` … `console8.out` | verbatim probe output (§3A–§3E) |
| `.spinney/scripting/console1.lua` … `console8.lua` | the probes themselves |
| `.spinney/scripting/usermacro.lua` | the macro the recorder wrote (§3F) |
| `.spinney/scripting/from_lua.csv` | the file `doCommand` produced (§3E) |
| `.spinney/scripting/exe-strings.txt` | 1.86 M printable strings from `Dorico6.exe` (24 MB) |
| `.spinney/scripting/run-in-console.sh`, `snap-invoke.sh`, `wins.ps1` | the driving scripts used here |

`.spinney/` is gitignored — the decisive transcripts are quoted in full in §3.

---

## 8. Addendum: the write methods' signatures, and what a `RunScript` run costs

Measured after §1–§7, by calling the scripts through the **Remote Control API** instead of the console —
`Script.RunScript?ScriptPath=…&ShowConsole=false` — with the probe writing its findings to a file through `io`.
This needed no console and no mouse.

**A `RunScript` with `ShowConsole=false` has no output channel, and a script that throws leaves no trace.** The
command answers `kOK` in 6 ms whether the script succeeded or died on the first line; `application.log` records
only the command itself. One probe here produced no file and no log line, and there was nothing to tell the
difference between "the script did nothing" and "the script failed". Write findings to a file **incrementally**
(open it, write as you go) if you want evidence of a partial run.

**Wrong-argument calls are safe and answer with the C++ signature.** luabind resolves overloads before it invokes
anything, so each of these returned an error and wrote nothing:

```
stream.createNote()        -> luabind::object createNote(EventStream&,lua_State*,RhythmicLength,RhythmicLength,MIDIPitchWithSpelling const&)
stream.createRest()        -> luabind::object createRest(EventStream&,lua_State*,RhythmicLength,RhythmicLength)
stream.createBarRest()     -> luabind::object createBarRest(EventStream&,lua_State*,RhythmicLength)
stream.createTuplet()      -> luabind::object createTuplet(EventStream&,lua_State*,RhythmicLength,TupletDefinition const&)
stream.createGraceNote()   -> luabind::object createGraceNote(EventStream&,lua_State*,RhythmicLength,RhythmicLength,RhythmicLength,MIDIPitchWithSpelling const&)
stream.createText()        -> luabind::object createText(EventStream&,lua_State*,RhythmicLength,RhythmicLength,std::string)
stream.createDynamicGroup() -> luabind::object createDynamicGroup(EventStream&,lua_State*,RhythmicLength,DynamicGroupElement&)
stream.deleteEventsInRange() -> void deleteEventsInRange(EventStream&,RhythmicLength,RhythmicLength)
stream.copyRange()         -> void copyRange(EventStream&,RhythmicLength,RhythmicLength,EventStream&,RhythmicLength,int)
stream.moveRange()         -> void moveRange(EventStream&,RhythmicLength,RhythmicLength,EventStream&,RhythmicLength,int)
stream.addBlockInstance()  -> void addBlockInstance(EventStream&,RhythmicLength,EventBlock&)
stream.findBlockInstanceAtPoint() -> luabind::object findBlockInstanceAtPoint(EventStream&,lua_State*,RhythmicLength)
```

So the binding is real and complete: `createNote(start, duration, pitch)` is a three-argument call on a stream
that exists (`flow.voiceStreams[1]` → `EventStream ID: 196 kVoiceStream, 34 events`). What is **not** established
is whether the argument types can be built from Lua — `RhythmicLength` and `MIDIPitchWithSpelling` are C++ types,
and the registered helpers (`LengthUtils.getRhythmicDurationForRational(RhythmicLength const&, NotatedLength&)`,
`MIDIPitch`, `MIDIPitchWithSpelling`, `NotatedLength`, `LogDuration`, `TupletDefinition`) are themselves the
candidates for doing it. That is the next probe, and it is cheap: call each candidate constructor with the wrong
argument count and read the signature, exactly as above.

**Reading, for contrast, works without any of this**: `stream.dump()` returned 3409 bytes for that 34-event
stream, in Dorico's own printer format —

```
Start block id: 1196 position: 0
4.25 (17/4 (4.25)) dur: 1/4 (0.25): kNoteEvent (id:147911) at 17/4 (4.25), duration 1/4 (0.25)  D5 (ends 9/2 (4.5))
```

— while `stream.events`, the structured accessor, is still the deliberate stub
(`Not implemented - this may leave the score in an unpredictable state @ …streamwrapper.cpp:162`). A script can
therefore read a stream by parsing `dump()`, and cannot yet be shown to write one.

