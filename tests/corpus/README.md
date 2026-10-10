# Test corpus

`ProjectFileCheck` runs against real `.dorico` files. Two kinds live here or are pointed at from the test program:

| File | Where it comes from | Licence |
|---|---|---|
| `fixture.dorico` | **This repository.** Made by the maintainers with Dorico 6.2.30 for testing, and it contains nothing but our own notes: one Piano player, one 4/4 bar, three notes (a B flat, an F sharp and a C in the bass). | CC0 — dedicated to the public domain, no attribution needed |
| the other scores named in `tests/ProjectFileCheck/Program.cs` | The maintainer's own music, on the maintainer's machine. **Not** in this repository, and never will be. The check program looks each one up by file name: `tests/corpus/` first, then the folder(s) named by `DORICO_USE_TEST_CORPUS`. | — |

The fixture is the one that always works: continuous integration runs the checks against it, so the decoder is
exercised for real on every push. It is deliberately small (1.3 MB) and deliberately shaped — two staves, a flat,
a sharp and a natural — so that the parts most likely to break (the note path, the spelling letter, the derived
accidental, and the tree walker agreeing with the typed reader) are all covered by it.

A fixture can also be built from scratch: create a project in Dorico with one player, enter a few notes, and save
it here with `File.SaveAs`, or by hand. If you contribute one, say where it came from and under which licence in
your pull request — it ends up in the repository, so it has to be redistributable. A fixture that comes with a
MusicXML export beside it (File > Export > MusicXML) is worth more: the export is a second source, and the test
program has cross-checks that compare this decoder's numbers against Dorico's own.
