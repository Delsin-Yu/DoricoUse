# Changelog

All notable changes to this project are documented here. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and the project aims to follow [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

Compatibility with Dorico is reported by `dorico-use version` (`testedAgainst`, `apiCompatibility`) rather than
encoded in the version number, because the same build works against any Dorico that exposes the same Remote
Control API — see `README.md`.

## [Unreleased]

### Added
- Continuous integration: build, the two test projects, and a Native AOT publish on every push.

## [0.1.0] - 2026-10-10

The first release: a stateless JSON CLI that lets an agent or a script steer a running Dorico, and a from-scratch
decoder for `.dorico` files that needs no Dorico at all.

### Added
- **`session`** — `status`, `info`, `connect`, `events`, `probe`. A long-lived socket with the status and
  selection stream that Dorico pushes unprompted; `session probe` measures the connection itself.
- **`host`** — `start`, `status`, `stop`, `restart`, `serve`. The long-lived process that owns the WebSocket and
  answers CLI requests over a per-user named pipe, so repeated invocations cost no handshake.
- **`command`** — `send`, `list`, `info`: the 1090 commands Dorico publishes, with their parameters, and a
  status read-back after anything that changes the score.
- **`note`** — `insert` with melodies, chords (`C5+E5+G5`), tuplets (`--tuplet 3:2`), rests, insert/overwrite mode,
  relative staff moves, `--from-start` to write from the top of the flow, and `--read` to prove the result from
  the file Dorico writes.
- **`file`** — `read`: decode a `.dorico` project from disk with no Dorico and no host. Notes with their MIDI
  number, spelling letter and derived accidental; dynamics; slurs; articulations; ties; beams; playing
  techniques; layout; lyrics; and a generic `--tree` escape hatch for everything else.
- **`project`, `options`, `library`, `playback`, `properties`** — flows and layouts, the 1565 engraving options
  (written values read back to prove them), the 50 library collections and their entities, expression maps, and
  what can be read from the current selection.
- **`export`** — `text` (unattended CSV of the score's text, including lyrics) and `musicxml` (which needs one
  click on 确定 in Dorico's dialog; the command watches the folder and reports the files that really appeared).
- **`version`** — the build, whether it is Native AOT, where state lives, and the protocol constants.
- **`--expect-score <id>`** on every command that changes a document: the host refuses to act when Dorico's active
  document is not the one named.
- **Tests** — `ProjectFileCheck` (153 checks against real projects and against Dorico's own MusicXML exports) and
  `VocabularyCheck` (96 checks; it extracts from a synthetic executable and needs no Dorico).

### Note
- Windows is the only platform tested. Nothing in the tool uses a Windows-only API, so a macOS port is a
  contribution waiting to happen rather than a rewrite — see `CONTRIBUTING.md`.
- A generic "send any protocol message" command is deliberately **not** shipped.
