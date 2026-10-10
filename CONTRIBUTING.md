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

`dialog` and `execution` are on every payload, and `ui dialog` / `ui press` answer a dialog. The design decisions are
in the handover plan that produced it; what a contributor needs is three rules.

**Every file that needs a Windows API lives under `src/DoricoUse.Cli/Ui/`, and only the CLI project compiles it.**
`ProjectFileCheck` and `VocabularyCheck` build CLI sources **by hand**, on `net10.0`, by listing them in
`<Compile Include>` items — so a Win32 or UI Automation type in one of *those* files breaks both checks. The two
files they do compile are the pure ones: `Ui/DialogModel.cs` (the ids, the ordering, the policy, the payload shape)
and `Ui/DialogCatalogue.cs` (the whitelist). Both are plain records and rules, and that is deliberate: it is what
makes the ids and the refusals testable without a dialog. **A rule that decides what an agent may press belongs in
`DialogModel`, not in the Windows code.**

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

## Releasing

`.github/workflows/ci.yml` builds, runs both checks and publishes the Native AOT binary on every push. A `v*` tag
additionally runs `.github/workflows/release.yml`: the same build and checks, the publish, an assertion that the
published binary's `version.build` flags are both `false`, a zip of the exe with `README.md`, `LICENSE`, `NOTICE`
and `CHANGELOG.md`, and `gh release create` with that version's `CHANGELOG.md` section as its notes (falling back to
generated notes). A release is that tag plus the `dorico-use.exe` from it (there is no installer), with
`CHANGELOG.md` updated and the `<Version>` in `DoricoUse.Cli.csproj` bumped. Dorico compatibility is *not* a version
number: it is reported by `dorico-use version` (`compatibility.testedAgainst`), which is the fact a user needs.
