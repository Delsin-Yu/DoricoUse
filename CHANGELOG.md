# Changelog

All notable changes to this project are documented here. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and the project aims to follow [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

Compatibility with Dorico is reported by `dorico-use version` (`testedAgainst`, `apiCompatibility`) rather than
encoded in the version number, because the same build works against any Dorico that exposes the same Remote
Control API — see `README.md`.

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
