#!/usr/bin/env python3
"""Cross-check the derived accidental against Dorico's own MusicXML export.

The reader derives a note's accidental from two things it reads (the MIDI number and the kNoteSpelling letter),
and refuses to derive one for a transposing instrument, because there the written and sounding pitches differ.
This script is the external check of that: for every part of the export that has no <transpose> (i.e. concert
pitch), the <alter> MusicXML writes on each note should match the accidentalSteps the reader reports, note for
note, in the same key: the player's name and the note's onset.

    python tools/verify_accidentals.py <export.musicxml> <file-read.json>

The JSON is what `file read --path <project> --limit all` prints. Both files are read-only.
"""

import json
import sys
import xml.etree.ElementTree as ET
from collections import Counter, defaultdict
from fractions import Fraction


def export_alters(path):
    """player -> Counter of <alter> values, for the parts that carry no <transpose>."""
    tree = ET.parse(path)
    root = tree.getroot()
    result = {}
    transposing_parts = set()

    # which parts transpose? a <transpose> element appears inside <attributes>
    for part in root.findall("part"):
        if part.findall(".//transpose"):
            transposing_parts.add(part.get("id"))

    for score_part in root.findall("./part-list/score-part"):
        name = score_part.findtext("part-name") or ""
        part_id = score_part.get("id")
        if part_id in transposing_parts:
            continue
        result[name] = part_id

    by_player = defaultdict(Counter)

    for part in root.findall("part"):
        if part.get("id") not in set(result.values()):
            continue
        name = next(key for key, value in result.items() if value == part.get("id"))
        divisions = None
        for measure in part.findall("measure"):
            for item in measure:
                if item.tag == "attributes" and item.findtext("divisions"):
                    divisions = int(item.findtext("divisions"))
                if item.tag != "note":
                    continue
                pitch = item.find("pitch")
                if pitch is None or item.find("grace") is not None or item.find("rest") is not None:
                    continue

                # A tie chain is one event in the project and a separate <note> per segment in MusicXML, so the
                # continuation would be counted twice. Drop those, the way the reader merges them.
                if any(tie.get("type") == "stop" for tie in item.findall("tie")):
                    continue

                alter = pitch.findtext("alter")
                by_player[name][int(alter or 0)] += 1

    return by_player


def reader_steps(path):
    with open(path, encoding="utf-8") as handle:
        document = json.load(handle)

    by_player = defaultdict(Counter)
    for note in document["score"]["notes"]:
        if note.get("accidentalSteps") is None:
            continue
        by_player[note.get("player") or ""][note["accidentalSteps"]] += 1
    return by_player


def main():
    if len(sys.argv) != 3:
        raise SystemExit(__doc__)

    expected = export_alters(sys.argv[1])
    actual = reader_steps(sys.argv[2])

    players = 0
    notes = 0
    differences = []

    for player, alters in expected.items():
        if player not in actual:
            differences.append(f"{player}: not in the reader's output")
            continue
        players += 1
        notes += sum(alters.values())
        if alters != actual[player]:
            differences.append(f"{player}:\n    export {dict(alters)}\n    reader {dict(actual[player])}")

    print(f"concert-pitch parts compared : {players}")
    print(f"notes in those parts         : {notes}")
    print(f"differences                  : {len(differences)}")
    for difference in differences[:12]:
        print("  " + difference)

    if differences:
        raise SystemExit(1)


if __name__ == "__main__":
    main()
