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

The style is in `.editorconfig`; the code is `net10.0`, no Windows-only API, and XML comments explain *why* rather
than *what* — a reader should be able to tell a measured fact from an inference.

## The test corpus

`ProjectFileCheck` runs against real `.dorico` files, which are **not** in this repository: they are other
people's music. A check whose file is missing reports `skip` and the run still exits 0, so a fresh clone passes
with a lot of skips.

To run the real checks, put a score you are allowed to redistribute under `tests/corpus/` and adjust the paths at
the top of `tests/ProjectFileCheck/Program.cs` (they are `const string`s). A good corpus file is small, has one or
two players, and — if you want the export cross-checks to run — comes with a MusicXML export written by Dorico
itself (File > Export > MusicXML) beside it. If you contribute a score, say where it came from and under which
licence in your pull request: it ends up in the repository, so it has to be redistributable.

The strongest checks compare this decoder against a second source: Dorico's own MusicXML export. `--alter` against
the derived accidental, `<articulations>` against the articulation marks, `<slur>` pairs against the slur events,
the page box in tenths against the layout numbers. That is the pattern to copy when you add a feature.

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
2. **A macOS port.** Nothing uses a Windows-only API, but only Windows was tested: named pipes, console encoding,
   the `host start` launch path (`UseShellExecute`) and paths all need a pass, and Dorico users are heavily Mac.
3. **Dorico SE / Elements behaviour.** Only Pro 6.2.30 was tested; the editions differ in which commands and
   options they accept. The catalogue-based features should degrade, but nobody has checked.
4. **Splitting the three biggest files** (`ProjectFileScoreReader`, `ProjectFileView`, `HostServer`) into
   coherent parts. They are correct but long, and that is the main obstacle to reading the code.

## Releasing

`.github/workflows/ci.yml` builds, runs both checks and publishes the Native AOT binary on every push. A release
is a tag plus the `dorico-use.exe` from that artifact (there is no installer), with `CHANGELOG.md` updated and the
`<Version>` in `DoricoUse.Cli.csproj` bumped. Dorico compatibility is *not* a version number: it is reported by
`dorico-use version` (`compatibility.testedAgainst`), which is the fact a user needs.
