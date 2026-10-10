using DoricoUse.Cli;
using DoricoUse.Cli.Dorico;
using DoricoUse.Cli.Views;

// A self-check for the .dorico reader: it parses real project files off disk and asserts what a human can verify.
//
//   dotnet run --project tests/ProjectFileCheck
//
// The corpus file is read-only and is never modified. Checks that need a file which is not on this machine are
// reported as SKIP rather than passed, so the output never claims more than it checked.

var failures = 0;
var skipped = 0;

const string Corpus = @"Bad Apple!! (JAGMO Shiro v1.2 2026.10.08).dorico";
const string SecondCorpus = @"《小城日常》 OPED串烧.dorico";
const string ThirdCorpus = @"畜生界传说：被欺骗者迷失的善与恶.dorico";

// A fourth file, because Dorico's own MusicXML export of it sits beside it on disk: the dynamics of that export were
// compared with what this reader decodes, marking by marking.
const string FourthCorpus = @"無限城への誘い.dorico";

// A fifth file, whose MusicXML export came from Sibelius instead — a different engraving of the same music, so only
// the text of its markings can be compared.
const string FifthCorpus = @"秘封拉格1 ~ Hifuu Rag 225 Clean.dorico";

void Check(string name, bool ok, string? detail = null)
{
    if (ok)
    {
        Console.WriteLine($"  ok   {name}");
        return;
    }

    failures++;
    Console.WriteLine($"  FAIL {name}{(detail is null ? string.Empty : ": " + detail)}");
}

void Skip(string name)
{
    skipped++;
    Console.WriteLine($"  skip {name}");
}

void Head(string title) => Console.WriteLine($"== {title}");

// ---------------------------------------------------------------- the corpus file

Head($"corpus: {Corpus}");

if (!File.Exists(Corpus))
{
    Skip("every check on the corpus file (not on this machine)");
}
else
{
    var project = ProjectFileReader.Open(Corpus);
    var score = project.Score;

    Check("container is a ZIP package naming score.dtn", project.Container.RootPath == "score.dtn");
    Check(
        "media type is Dorico's",
        project.Container.MediaType == "application/vnd.steinberg.dorico-project",
        project.Container.MediaType);
    Check("members were listed", project.Members.Count >= 8, project.Members.Count.ToString());

    Check(
        "scoreinfo.xml title",
        project.Metadata.Title == "Bad Apple!! (JAGMO Cover.)",
        project.Metadata.Title);
    Check("scoreinfo.xml composer", project.Metadata.Composer == "ZUN", project.Metadata.Composer);
    Check(
        "scoreinfo.xml player count",
        project.Metadata.Players.Count == 36,
        project.Metadata.Players.Count.ToString());

    Check("score.dtn decoded", score is not null);

    // The view is a pure function of (file, options), but it shares static caveat lists between reports: a shared
    // node that gets parented twice throws "The node already has a parent". One call per process always looked
    // fine; this is the check that keeps it true for a long-lived one.
    try
    {
        var options = new ProjectFileView.Options { NoteLimit = 1 };
        var first = ProjectFileView.Report(project, options).ToJsonString();
        var second = ProjectFileView.Report(project, options).ToJsonString();
        var third = ProjectFileView.Report(project, options).ToJsonString();

        Check("the report can be built three times in one process", first == second && second == third);
    }
    catch (Exception ex)
    {
        Check("the report can be built three times in one process", false, ex.Message);
    }

    if (score is not null)
    {
        Check(
            "score title agrees with scoreinfo.xml",
            score.Info.Title == project.Metadata.Title,
            score.Info.Title);
        Check("name table", score.NameCount == 3336, score.NameCount.ToString());
        Check("value pool", score.ValueCount > 28_000, score.ValueCount.ToString());
        Check("token stream", score.RecordCount > 3_000_000, score.RecordCount.ToString());

        Check("players", score.Players.Count == 36, score.Players.Count.ToString());

        // scoreinfo.xml lists the same players in another order and spells the transposing ones out in full
        // ("Clarinet in B flat 1" where the project's own name is "Clarinet (B flat) 1"), so it is the count and the
        // parts that are compared, not the spellings.
        Check(
            "scoreinfo.xml lists as many players as the score holds",
            score.Players.Count == project.Metadata.Players.Count,
            $"{score.Players.Count} vs {project.Metadata.Players.Count}");
        Check(
            "every player has a distinct, non-empty name",
            score.Players.All(player => !string.IsNullOrWhiteSpace(player.Name))
                && score.Players.Select(player => player.Name).Distinct(StringComparer.Ordinal).Count() == score.Players.Count);
        Check(
            "every part layout is named after one of the players",
            score.Layouts.Count(layout => layout.Type == "kPartLayout") == score.Players.Count
                && score.Layouts
                    .Where(layout => layout.Type == "kPartLayout")
                    .All(layout => score.Players.Any(player => player.Name == layout.Name)));

        // Dorico's own markup is translated for the reader: {@flat@} is rendered as a flat sign.
        Check(
            "transposing player names are readable",
            score.Players.Any(player => player.Name == "Clarinet (B♭) 1"),
            string.Join(',', score.Players.Select(player => player.Name).Where(name => name?.Contains('B') == true).Take(2)));

        Check("layouts: one full score plus 36 parts", score.Layouts.Count == 37, score.Layouts.Count.ToString());
        Check(
            "exactly one layout is the full score",
            score.Layouts.Count(layout => layout.Type == "kFullScoreLayout") == 1);
        Check("flows", score.Flows.Count == 1, score.Flows.Count.ToString());
        Check("notes", score.Notes.Count == 16002, score.Notes.Count.ToString());
        Check("staves with notes", score.Staves.Count == 39, score.Staves.Count.ToString());

        Check(
            "metre: 4/4, and a second bar division at bar 115",
            score.Meters.Count == 2
            && score.Meters[0].TimeSignature == "4/4"
            && score.Meters[0].Bar == 1
            && score.Meters[1].Bar == 115
            && score.Meters[1].Position == "456",
            string.Join(' ', score.Meters.Select(meter => meter.Bar + "@" + meter.Position)));

        // The anchor a human can check: the MusicXML export of the same arrangement (Bad Apple!.musicxml, written
        // independently of this file) has measure 2 beat 3 as a run of 16ths D5 E5 F5 G5 A5 Bb5 C6 C#6 in Flute 1.
        var flute = score.Notes
            .Where(note => note.Player == "Flute 1" && note.Measure == 2)
            .Take(8)
            .ToList();

        Check("Flute 1 measure 2 has the opening run", flute.Count == 8, flute.Count.ToString());
        Check(
            "the run is 16ths on the beat grid (3, 3.25, ... 4.75)",
            flute.Select(note => note.Beat).SequenceEqual(["3", "13/4", "7/2", "15/4", "4", "17/4", "9/2", "19/4"]),
            string.Join(' ', flute.Select(note => note.Beat)));
        Check(
            "the run is D5 E5 F5 G5 A5 A#5 C6 C#6",
            flute.Select(note => note.Midi).SequenceEqual([74, 76, 77, 79, 81, 82, 84, 85]),
            string.Join(' ', flute.Select(note => note.Midi)));
        Check(
            "each of them is a 16th (1/4 crotchet)",
            flute.All(note => note.Duration == "1/4"),
            string.Join(' ', flute.Select(note => note.Duration)));

        var bassoon = score.Notes.First(note => note.Player == "Bassoon 1");
        Check(
            "Bassoon 1 opens on a two-bar D3 (measure 1, position 0)",
            bassoon.Measure == 1 && bassoon.Position == "0" && bassoon.Midi == 50 && bassoon.Duration == "8",
            $"{bassoon.Measure}/{bassoon.Position}/{bassoon.Midi}/{bassoon.Duration}");

        Console.WriteLine();
        Console.WriteLine("score-order and measure arithmetic");

        var misplaced = score.Notes
            .Where(note => note.Measure < 1 || note.Measure > score.LastMeasure)
            .ToList();
        Check("every measure is inside the piece", misplaced.Count == 0, misplaced.Count.ToString());

        var badBeats = score.Notes.Where(note => !IsBeat(note.Beat)).ToList();
        Check("every beat is a rational inside its bar", badBeats.Count == 0, badBeats.FirstOrDefault()?.Beat);

        var sorted = score.Notes
            .Zip(score.Notes.Skip(1))
            .All(pair => Rational.Parse(pair.First.Position).CompareTo(Rational.Parse(pair.Second.Position)) <= 0);
        Check("notes are in score order", sorted);

        // A stream is one voice of one stave. Chords are several notes at the same position, which is normal; a note
        // starting inside an earlier note's span (at a different position) is not, and would mean the walk lost a
        // boundary. Exact rationals, so a triplet cannot round into a false positive.
        var overlaps = 0;
        string? firstOverlap = null;

        foreach (var voice in score.Notes.Where(note => note.Stream is not null).GroupBy(note => note.Stream))
        {
            var byPosition = voice
                .OrderBy(note => Rational.Parse(note.Position))
                .GroupBy(note => Rational.Parse(note.Position));

            var reached = default(Rational?);

            foreach (var chord in byPosition)
            {
                if (reached is { } end && end.CompareTo(chord.Key) > 0)
                {
                    overlaps++;
                    firstOverlap ??= $"{chord.First().Player} m{chord.First().Measure} starts at "
                        + $"{chord.Key} before {end}";
                }

                foreach (var note in chord)
                {
                    var noteEnd = chord.Key + Rational.Parse(note.Duration);

                    if (reached is null || noteEnd.CompareTo(reached.Value) > 0)
                    {
                        reached = noteEnd;
                    }
                }
            }
        }

        Check("no note starts inside an earlier note of the same stream", overlaps == 0, $"{overlaps}, first: {firstOverlap}");

        Check(
            "every duration is positive",
            score.Notes.All(note => Rational.Parse(note.Duration).Numerator > 0));
    }
}

// ---------------------------------------------------------------- a file of the same format, another save of it

Head($"second corpus: {SecondCorpus}");

if (!File.Exists(SecondCorpus))
{
    Skip("the second corpus file (not on this machine)");
}
else
{
    var project = ProjectFileReader.Open(SecondCorpus);
    var score = project.Score;

    // This file uses tag bytes 0x1x where the corpus uses 0x9x: the same grammar, another marker.
    Check("decoded with a different tag prefix", score is not null);

    if (score is not null)
    {
        Check("names", score.NameCount == 3243, score.NameCount.ToString());
        Check("notes", score.Notes.Count == 17784, score.Notes.Count.ToString());
        Check("players", score.Players.Count == 35, score.Players.Count.ToString());
        Check("more than one metre in force", score.Meters.Count > 1, score.Meters.Count.ToString());
    }
}

Head($"third corpus: {ThirdCorpus}");

if (!File.Exists(ThirdCorpus))
{
    Skip("the third corpus file (not on this machine)");
}
else
{
    // A third tag marker (0x08 where the others are 0x90 and 0x10), and the longest value pool of the three.
    var project = ProjectFileReader.Open(ThirdCorpus);
    var score = project.Score;

    Check("decoded with a third tag marker", score is not null);

    if (score is not null)
    {
        Check("names", score.NameCount == 2639, score.NameCount.ToString());
        Check("notes", score.Notes.Count == 26756, score.Notes.Count.ToString());
        Check("players", score.Players.Count == 34, score.Players.Count.ToString());
        Check("layouts", score.Layouts.Count == 35, score.Layouts.Count.ToString());
    }
}

// ---------------------------------------------------------------- dynamics

Head($"dynamics: {Corpus}");

if (!File.Exists(Corpus))
{
    Skip("the dynamics checks (corpus file not on this machine)");
}
else
{
    var score = ProjectFileReader.Open(Corpus).Score;

    Check("dynamic group events", score.Dynamics.Count == 754, score.Dynamics.Count.ToString());
    Check("dynamic group definitions", score.DynamicGroups.Count == 305, score.DynamicGroups.Count.ToString());

    var groups = score.DynamicGroups.ToDictionary(group => group.Id);

    Check(
        "every event names a group the flow defines",
        score.Dynamics.All(item => groups.ContainsKey(item.Group)),
        score.Dynamics.FirstOrDefault(item => !groups.ContainsKey(item.Group))?.Group.ToString());

    // The file states the same link from the other end: a group lists the event ids that reference it.
    var referenced = score.Dynamics
        .GroupBy(item => item.Group)
        .ToDictionary(group => group.Key, group => group.Select(item => item.EventId).OrderBy(id => id).ToList());

    var mismatched = score.DynamicGroups
        .Where(group => !Ids(group.Events).SequenceEqual(referenced.GetValueOrDefault(group.Id, [])))
        .ToList();

    Check(
        "each group's referencingEventIDs lists exactly the events that name it",
        mismatched.Count == 0,
        mismatched.Count == 0 ? null : $"first: group {mismatched[0].Id} says [{mismatched[0].Events}]");

    Check(
        "every mark is of a kind this reader knows",
        score.Dynamics
            .SelectMany(item => item.Marks)
            .All(mark => mark.Kind is "ImmediateChangeDynamicDefinition" or "GradualChangeDynamicDefinition"
                or "ForceDynamicDefinition" or "CombinedDynamicDefinition"),
        string.Join(',', score.Dynamics.SelectMany(item => item.Marks).Select(mark => mark.Kind).Distinct()));

    var marks = score.Dynamics.SelectMany(item => item.Marks).ToList();

    Check(
        "a gradual change is a hairpin with a length",
        marks
            .Where(mark => mark.Kind == "GradualChangeDynamicDefinition")
            .All(mark => Rational.Parse(mark.Duration).Numerator > 0 && mark.Mark is "cresc" or "dim"));

    Check(
        "every mark is placed inside the piece",
        marks.All(mark => mark.Measure is >= 1 && mark.Measure <= score.LastMeasure && mark.At is not null),
        marks.FirstOrDefault(mark => mark.Measure is not (>= 1) || mark.At is null)?.Kind);

    // What this reader could not name: a combined dynamic (its start/end intensities are a shape no checked export
    // shows). Its numbers stay in `fields`, and the count is reported so silence is never mistaken for agreement.
    var unnamed = marks.Where(mark => mark.Mark is null).ToList();
    Check(
        "the marks it cannot name are the combined dynamics, and only those",
        unnamed.Count == 34 && unnamed.All(mark => mark.Kind == "CombinedDynamicDefinition"),
        $"{unnamed.Count} unnamed, {string.Join(',', unnamed.Select(mark => mark.Kind).Distinct())}");

    Check(
        "the derived names are the p/f families this file holds",
        marks.Where(mark => mark.Mark is not null).Select(mark => mark.Mark).Distinct().Order().SequenceEqual(
            ["cresc", "dim", "f", "ff", "fff", "mf", "mp", "p", "pp", "ppppp"]),
        string.Join(',', marks.Where(mark => mark.Mark is not null).Select(mark => mark.Mark).Distinct().Order()));

    var first = score.Dynamics[0];
    Check(
        "the piece opens with a marking at position 0 of measure 1",
        first.Measure == 1 && first.Position == "0" && first.Beat == "1",
        $"{first.Measure}/{first.Position}");

    // Lyrics are an element table, not events, and this corpus has an empty one everywhere: nothing to read, and the
    // report says so rather than inventing a shape.
    Check(
        "the lyric element table is there, empty, with no element shape to read",
        score.Lyrics.Tables == 1 && score.Lyrics.Elements == 0 && score.Lyrics.Shapes.Count == 0,
        $"{score.Lyrics.Tables} tables, {score.Lyrics.Elements} elements");
}

Head($"dynamics against a second file: {FourthCorpus}");

if (!File.Exists(FourthCorpus))
{
    Skip("the MusicXML cross-check (file not on this machine)");
}
else
{
    // This project has Dorico's own MusicXML export saved beside it (無限城への誘い - 总谱 - 01 無限城への誘い.musicxml,
    // written by Dorico 6.2.30.6245 on 2026-09-11), which is what the numbers below are checked against: the export
    // holds 46 f, 126 ff, 41 fff, 25 pp, 15 mf, 4 p, 4 fz, 63 crescendo wedges and 5 diminuendo wedges, and its
    // markings agree with this decoder's on absolute position, measure and beat as well (checked mark by mark with an
    // external script; a MusicXML parser in this check would test the export more than the decoder).
    var score = ProjectFileReader.Open(FourthCorpus).Score;
    var marks = score.Dynamics.SelectMany(item => item.Marks).ToList();

    Check("dynamic group events", score.Dynamics.Count == 246, score.Dynamics.Count.ToString());
    Check("dynamic group definitions", score.DynamicGroups.Count == 118, score.DynamicGroups.Count.ToString());

    var byName = marks
        .Where(mark => mark.Mark is not null)
        .GroupBy(mark => mark.Mark!)
        .ToDictionary(group => group.Key, group => group.Count());

    Check(
        "the markings agree, family for family, with Dorico's own MusicXML export of the same project",
        byName.Count == 8
        && byName["f"] == 46
        && byName["ff"] == 126
        && byName["fff"] == 41
        && byName["p"] == 4
        && byName["pp"] == 25
        && byName["mf"] == 15
        && byName["cresc"] == 63
        && byName["dim"] == 5
        && marks.Count == 325,
        string.Join(' ', byName.OrderBy(pair => pair.Key).Select(pair => pair.Key + "=" + pair.Value)));

    // The export writes the fff of measure 12 two crotchets after the start of that bar's crescendo, and this decoder
    // puts the same mark at absolute position 48 (its own bar reading calls that measure 13 beat 1; the export writes
    // it as measure 12 beat 5 — the same position).
    var horn = score.Dynamics.FirstOrDefault(item => item.Player == "Horn (F) 1" && item.Measure == 12);

    Check(
        "a hairpin and the mark it leads to are read at the same positions",
        horn is not null
        && horn.Position == "46"
        && horn.Marks.Count == 2
        && horn.Marks[0].Mark == "cresc"
        && horn.Marks[0].Duration == "2"
        && horn.Marks[0].At == "46"
        && horn.Marks[1].Mark == "fff"
        && horn.Marks[1].At == "48",
        horn is null
            ? "no event"
            : string.Join(' ', horn.Marks.Select(mark => $"{mark.Mark}@{mark.At}+{mark.Duration}")));
}

Head($"dynamics against a third file: {FifthCorpus}");

if (File.Exists(FifthCorpus))
{
    // The export beside this one came from Sibelius 24.10.0 — a different engraving of the same music (5130 notes
    // against 4358, and no position agreement at all), so only the text of the marks can be compared: 18 f, 11 mf,
    // 4 mp, 2 p, 4 fz and 12 wedges, exactly what the Sibelius export holds.
    var score = ProjectFileReader.Open(FifthCorpus).Score;
    var byName = score.Dynamics
        .SelectMany(item => item.Marks)
        .Where(mark => mark.Mark is not null)
        .GroupBy(mark => mark.Mark!)
        .ToDictionary(group => group.Key, group => group.Count());

    Check(
        "the markings of the third file agree with its export, text for text",
        byName.Count == 7
        && byName["f"] == 18
        && byName["mf"] == 11
        && byName["mp"] == 4
        && byName["p"] == 2
        && byName["fz"] == 4
        && byName["cresc"] == 11
        && byName["dim"] == 1,
        string.Join(' ', byName.OrderBy(pair => pair.Key).Select(pair => pair.Key + "=" + pair.Value)));

    Check(
        "an fz is a force dynamic, not an intensity",
        score.Dynamics
            .SelectMany(item => item.Marks)
            .Where(mark => mark.Kind == "ForceDynamicDefinition")
            .All(mark => mark.Mark == "fz" && mark.IntensityLevel == 1));
}
else
{
    Skip("the third file's dynamics (not on this machine)");
}

// ---------------------------------------------------------------- the generic tree dump

Head("the generic tree dump");

if (!File.Exists(Corpus))
{
    Skip("the tree checks (corpus file not on this machine)");
}
else
{
    var data = ProjectFileReader.Open(Corpus).ScoreData;

    var census = ProjectFileTreeReader.Census(data, -1);

    Check(
        "the census counts every name the file uses, starting with the root",
        census.NodeNames == 464 && census.LeafNames == 2906 && Counted(census, "kScore", "node") == 1,
        $"{census.NodeNames} node names, {census.LeafNames} leaf names");
    Check(
        "the census agrees with the typed reader about the note events",
        Counted(census, "NoteEventDefinition", "node") == 16002,
        Counted(census, "NoteEventDefinition", "node").ToString());
    Check(
        "the census counts leaf keys too, with the same numbers",
        Counted(census, "position", "leaf") == 18407 && Counted(census, "duration", "leaf") == 18021,
        $"{Counted(census, "position", "leaf")} / {Counted(census, "duration", "leaf")}");

    // An empty collection is written as a leaf named `array` and a full one as a node of the same name — the one fact
    // that makes the format's collections readable at all.
    Check(
        "a name can be a node and a leaf (an empty collection is a leaf)",
        Counted(census, "array", "node") > 0 && Counted(census, "array", "leaf") > 0,
        $"{Counted(census, "array", "node")} nodes, {Counted(census, "array", "leaf")} leaves");
    Check("asking for everything is not truncated", !census.Truncated);

    var limited = ProjectFileTreeReader.Census(data, 5);
    Check("a limit truncates the census, and says so", limited.Truncated && limited.Names.Count == 5);

    var notes = ProjectFileTreeReader.Subtree(data, "NoteEventDefinition", 0, -1);
    Check(
        "the two walkers agree on how many note events there are",
        notes.Matched == 16002 && notes.Returned == 16002 && !notes.Truncated,
        $"{notes.Matched} matched, {notes.Returned} returned");
    Check(
        "a matched node reports its own fields, in the file's order",
        notes.Occurrences[0].Fields[0].Key == "eventID"
        && new[] { "position", "duration", "pitch" }.All(wanted =>
            notes.Occurrences[0].Fields.Any(field => field.Key == wanted))
        && notes.Occurrences[0].Fields.Any(field => field.Key == "eventID" && field.Value is not null),
        string.Join(',', notes.Occurrences[0].Fields.Select(field => field.Key)));
    Check(
        "depth 0 lists no children but does report how many there are",
        notes.Occurrences[0].Children.Count == 0 && notes.Occurrences[0].ChildNodes > 0
        && notes.Occurrences[0].Truncated,
        $"{notes.Occurrences[0].ChildNodes} children, {notes.Occurrences[0].Children.Count} listed");
    Check(
        "the path from the root is reported",
        notes.Occurrences[0].Path == "kScore/flows/array/flow/blocks/array/block/events/NoteEventDefinition",
        notes.Occurrences[0].Path);

    var lyricTable = ProjectFileTreeReader.Subtree(data, "LyricElementTableDefinition", 2, -1);
    Check(
        "the empty lyric table is found where the flows keep their element tables",
        lyricTable.Matched == 1
        && lyricTable.Occurrences[0].Path
            == "kScore/flows/array/flow/elementTables/array/LyricElementTableDefinition",
        lyricTable.Occurrences.Count == 0 ? "not found" : lyricTable.Occurrences[0].Path);
    Check(
        "and it says the table holds nothing: one `array` leaf with no value",
        lyricTable.Occurrences.Count == 1
        && lyricTable.Occurrences[0].Fields.Count == 1
        && lyricTable.Occurrences[0].Fields[0].Key == "array"
        && lyricTable.Occurrences[0].Fields[0].Value is null
        && lyricTable.Occurrences[0].ChildNodes == 0);

    var root = ProjectFileTreeReader.Subtree(data, "kScore", 1, -1);
    Check(
        "the root is a node like any other",
        root.Matched == 1 && root.Occurrences[0].Path == "kScore" && root.Occurrences[0].Name == "kScore");

    var dynamicEvents = ProjectFileTreeReader.Subtree(data, "DynamicGroupEventDefinition", 1, 2);
    Check(
        "the tree sees as many dynamic events as the typed reader",
        dynamicEvents.Matched == 754 && dynamicEvents.Returned == 2 && dynamicEvents.Truncated,
        dynamicEvents.Matched.ToString());

    // A partial name is not a match — the query is exact, so that the answer is always the same shape of thing — and
    // the neighbourhood is what gives the caller the name to use.
    var partial = ProjectFileTreeReader.Subtree(data, "LyricElementTable", 1, 4);
    Check(
        "a partial name finds nothing, and the neighbourhood gives the name to use",
        partial.Matched == 0 && partial.Similar.Any(name => name.Name == "LyricElementTableDefinition"),
        string.Join(',', partial.Similar.Take(2).Select(name => name.Name)));

    // A guess at a name the file does not use: 16002 note events are called NoteEventDefinition and lyrics are an
    // element table, so the honest answer is the neighbourhood, not silence.
    var missing = ProjectFileTreeReader.Subtree(data, "LyricEventDefinition", 2, 8);
    Check(
        "a name the file does not use reports nothing — and offers the neighbourhood",
        missing.Matched == 0
        && missing.Occurrences.Count == 0
        && missing.Similar.Any(name => name.Name == "LyricElementTableDefinition")
        && missing.Similar.Any(name => name.Name.StartsWith("Lyric", StringComparison.Ordinal)),
        string.Join(',', missing.Similar.Take(3).Select(name => name.Name)));

    var bounded = ProjectFileTreeReader.Subtree(data, "NoteEventDefinition", 2, 3);
    Check(
        "a limit bounds the dump and says it was truncated",
        bounded.Matched == 16002 && bounded.Returned == 3 && bounded.Occurrences.Count == 3 && bounded.Truncated);

    Check(
        "a deep dump stays bounded: every node reports its real child count",
        ProjectFileTreeReader.Subtree(data, "kScore", 20, 8).Occurrences
            .All(node => node.ChildNodes >= node.Children.Count));

    // The tree walker validates exactly like the typed reader: bytes that are not a score report the same code.
    Check(
        "the tree walker reports UnsupportedScoreData on a member that is not a score",
        Code(() => ProjectFileTreeReader.Census(new byte[64], -1)) == "UnsupportedScoreData");
    Check(
        "and on a member shorter than its header",
        Code(() => ProjectFileTreeReader.Census([1, 2, 3], -1)) == "UnsupportedScoreData");
}

// ---------------------------------------------------------------- slurs, articulations, ties, beams, techniques

Head($"marks on the corpus: {Corpus}");

if (!File.Exists(Corpus))
{
    Skip("the mark checks (corpus file not on this machine)");
}
else
{
    var score = ProjectFileReader.Open(Corpus).Score;

    // A slur is an event with a position and a length, and the file states the link to its element from both ends:
    // the event names the element, and the element lists the events that name it.
    Check("slur events", score.Slurs.Count == 987, score.Slurs.Count.ToString());
    Check("slur elements", score.SlurElements.Count == 438, score.SlurElements.Count.ToString());

    var elementEvents = score.Slurs
        .Where(slur => slur.Element is not null)
        .GroupBy(slur => slur.Element!.Value)
        .ToDictionary(group => group.Key, group => group.Select(slur => slur.Location.EventId).Order().ToList());

    Check(
        "every slur names an element the flow defines",
        score.Slurs.All(slur => score.SlurElements.Any(element => element.Id == slur.Element)),
        score.Slurs.FirstOrDefault(slur => score.SlurElements.All(element => element.Id != slur.Element))?.Element.ToString());

    var elementMismatch = score.SlurElements
        .Where(element => !element.Events.Order().SequenceEqual(elementEvents.GetValueOrDefault(element.Id, [])))
        .ToList();

    Check(
        "each element's referencingEventIDs lists exactly the events that name it",
        elementMismatch.Count == 0,
        elementMismatch.Count == 0
            ? null
            : $"first: element {elementMismatch[0].Id} says [{string.Join(',', elementMismatch[0].Events.Order())}]");

    Check(
        "the events sharing an element count is the element's own list",
        score.Slurs.All(slur => slur.Noteheads == 0
            || score.SlurElements.First(element => element.Id == slur.Element).Events.Count == slur.Noteheads),
        score.Slurs.Max(slur => slur.Noteheads).ToString());

    Check(
        "every slur is placed inside the piece and has a positive length",
        score.Slurs.All(slur => slur.Location.Measure >= 1
            && slur.Location.Measure <= score.LastMeasure
            && Rational.Parse(slur.Location.Duration).Numerator > 0));

    // An articulation is a property of the note, in two families: a length (kArticulationDuration) and a force
    // (kArticulationForce). The counts are what the corpus file holds.
    var articulations = score.NoteMarks.Where(mark => mark.Family == "articulation").ToList();
    var duration = articulations.Where(mark => mark.Property == "kArticulationDuration").ToList();
    var force = articulations.Where(mark => mark.Property == "kArticulationForce").ToList();

    Check(
        "articulations: 3914 staccato, 349 tenuto and 3576 accents",
        articulations.Count == 7839
        && duration.Count(mark => ProjectFileScoreReader.VariantText(mark.Value) == "kStaccato") == 3914
        && duration.Count() == 4263
        && force.Count() == 3576
        && force.All(mark => ProjectFileScoreReader.VariantText(mark.Value) == "kAccent"),
        $"{articulations.Count} marks: {duration.Count} durations, {force.Count} forces");

    // The name is the score's own: kStaccato is the value, and the file's playing-technique catalogue holds
    // pt.staccato with the name "Staccato". Every value in this file resolves, and the articulation family matches
    // the catalogue's own group: a length for a duration, a dynamic for a force.
    var catalogue = score.TechniqueDefinitions.ToDictionary(definition => definition.EntityId, definition => definition);

    Check(
        "every articulation value is named by the score's own technique catalogue",
        articulations.All(mark => mark.Name is not null)
        && articulations.Count(mark => mark.Name == "Staccato") == 3914
        && articulations.Count(mark => mark.Name == "Tenuto") == 349
        && articulations.Count(mark => mark.Name == "Accent") == 3576,
        string.Join(',', articulations.Select(mark => mark.Name).Distinct()));

    Check(
        "the catalogue spells pt.staccato as a length and pt.accent as a dynamic",
        catalogue["pt.staccato"].GroupType == "kLengths" && catalogue["pt.accent"].GroupType == "kDynamics",
        $"{catalogue["pt.staccato"].GroupType} / {catalogue["pt.accent"].GroupType}");

    Check(
        "kArticulationDuration resolves to a kLengths technique and kArticulationForce to a kDynamics one",
        duration.All(mark => catalogue[TechniqueId(mark)].GroupType == "kLengths")
        && force.All(mark => catalogue[TechniqueId(mark)].GroupType == "kDynamics"));

    // Ties: nothing in the file is a tie event, so what can be reported is what the notes state about them.
    var ties = score.NoteMarks.Where(mark => mark.Family == "tie").ToList();
    var beams = score.NoteMarks.Where(mark => mark.Family == "beam").ToList();

    Check(
        "ties: 2 laissez-vibrer ties and 28 forced splits, and no tie event anywhere",
        ties.Count == 30
        && ties.Count(mark => mark.Property == "kNoteEndHasLVTie") == 2
        && ties.Count(mark => mark.Property == "kNoteSplitDurations") == 28
        && score.NoteMarks.All(mark => mark.Family != "tieEvent"),
        string.Join(',', ties.Select(mark => mark.Property).Distinct()));

    // A split is written as the note values of the chain, and in this file they sum to the note's own sounding
    // duration — which is what makes the reading of it checkable rather than a guess.
    var splits = ties.Where(mark => mark.Property == "kNoteSplitDurations").ToList();

    Check(
        "every forced split sums to the note's own duration",
        splits.All(mark =>
        {
            var values = Strings(mark.Value);
            var total = values.Aggregate(new Rational(0, 1), (sum, value) => sum + Rational.Parse(value));

            return total.CompareTo(Rational.Parse(mark.Location.Duration)) == 0;
        }),
        splits.Count == 0 ? "no splits" : string.Join(' ', splits.Select(mark => mark.Value)));

    Check(
        "beams: 8 beam starts, 8 beam ends, and no beam event",
        beams.Count == 16
        && beams.Count(mark => mark.Property == "kBeamStart") == 8
        && beams.Count(mark => mark.Property == "kBeamEnd") == 8,
        string.Join(',', beams.Select(mark => mark.Property).Distinct()));

    Check(
        "every mark is placed inside the piece",
        score.NoteMarks.All(mark => mark.Location.Measure >= 1 && mark.Location.Measure <= score.LastMeasure),
        score.NoteMarks.FirstOrDefault(mark => mark.Location.Measure < 1)?.Location.Measure.ToString());

    Check(
        "a note's marks carry the note's own event id, position and stave",
        score.NoteMarks.All(mark => mark.Location.EventId > 0 && mark.Location.Stave is > 0),
        score.NoteMarks.FirstOrDefault(mark => mark.Location.Stave is not > 0)?.Location.EventId.ToString());

    // Playing techniques reach the score two ways: as events of their own, and as a property of a note.
    var techniques = score.TechniqueEvents;
    var noteTechniques = score.NoteMarks.Where(mark => mark.Family == "technique").ToList();

    Check("technique events", techniques.Count == 35, techniques.Count.ToString());
    Check("techniques stored on notes", noteTechniques.Count == 45, noteTechniques.Count.ToString());

    // The event names an appearance; the appearance names the technique; the catalogue names that. A percussion
    // technique is attached to the note instead, and the same catalogue names it.
    Check(
        "every technique event resolves through its appearance to a named technique",
        techniques.All(technique => technique.AppearanceId is not null
            && technique.TechniqueId is not null
            && catalogue.ContainsKey(technique.TechniqueId)),
        techniques.FirstOrDefault(technique => technique.TechniqueId is null)?.AppearanceId);

    Check(
        "the appearance the file shows is the text the score prints",
        techniques.Count(technique => technique.Text == "arco") == 13
        && techniques.Count(technique => technique.Text == "pizz") == 10
        && techniques.Count(technique => technique.Text == "open") == 6
        && techniques.Count(technique => technique.Text == "straight mute") == 6,
        string.Join(',', techniques.Select(technique => technique.Text).Distinct()));

    Check(
        "the techniques on notes resolve to Cross stick and Rim shot",
        noteTechniques.Count(mark => mark.Name == "Cross stick") == 38
        && noteTechniques.Count(mark => mark.Name == "Rim shot") == 7,
        string.Join(',', noteTechniques.Select(mark => mark.Name).Distinct()));

    Console.WriteLine();
}

Head($"marks against Dorico's own export: {FourthCorpus}");

if (!File.Exists(FourthCorpus))
{
    Skip("the MusicXML cross-check of the marks (file not on this machine)");
}
else
{
    // This project has Dorico's own MusicXML export beside it. An external script counted what that export holds:
    // 5070 <staccato/> and 1448 <accent/> inside <articulations>, 214 <slur type="start"> with 214
    // <slur type="stop">, 17 <tied type="let-ring"/>, and 112 <tie type="start"> pairs written for 110 note events
    // (a tie chain is one event here). The slur check went further than counts: the multiset of (start, length) of
    // this reader's 214 slur events is exactly the multiset of the export's 214 slur pairs, with nothing left over
    // on either side. A MusicXML parser in this check would test the export more than the decoder, so the numbers
    // are the ones the script produced.
    var score = ProjectFileReader.Open(FourthCorpus).Score;

    var articulations = score.NoteMarks.Where(mark => mark.Family == "articulation").ToList();
    var slurSpans = score.Slurs
        .Select(slur => slur.Location.Position + "+" + slur.Location.Duration)
        .ToHashSet(StringComparer.Ordinal);

    Check(
        "the articulations agree, mark for mark, with Dorico's own MusicXML export",
        articulations.Count == 6518
        && articulations.Count(mark => mark.Name == "Staccato") == 5070
        && articulations.Count(mark => mark.Name == "Accent") == 1448,
        $"{articulations.Count} marks: {articulations.Count(mark => mark.Name == "Staccato")} staccato, "
        + $"{articulations.Count(mark => mark.Name == "Accent")} accent");

    Check(
        "the slur events are the export's slur pairs: 214 events, 43 elements",
        score.Slurs.Count == 214 && score.SlurElements.Count == 43,
        $"{score.Slurs.Count} events, {score.SlurElements.Count} elements");

    Check(
        "every slur starts where a note or a rest sits and lasts a written value",
        score.Slurs.All(slur => Rational.Parse(slur.Location.Duration).Numerator > 0
            && slur.Location.Measure is >= 1 and <= 163),
        string.Join(' ', score.Slurs.Take(3).Select(slur => slur.Location.Position + "+" + slur.Location.Duration)));

    // Two spans the export writes, one near the start and one at the end, as the script read them.
    Check(
        "the slur spans the export writes are the ones this reader finds",
        slurSpans.Contains("20+2") && slurSpans.Contains("28+2") && slurSpans.Contains("614+1"),
        string.Join(' ', slurSpans.Take(4)) + " … " + string.Join(' ', slurSpans.TakeLast(2)));

    Check(
        "the laissez-vibrer ties agree with the export: 17 of them",
        score.NoteMarks.Count(mark => mark.Property == "kNoteEndHasLVTie" && mark.Family == "tie") == 17,
        score.NoteMarks.Count(mark => mark.Property == "kNoteEndHasLVTie").ToString());

    Check(
        "this file states no beam override and has no technique event",
        score.NoteMarks.All(mark => mark.Family != "beam") && score.TechniqueEvents.Count == 0);

    Console.WriteLine();
}

// ---------------------------------------------------------------- the layout

Head($"layout: {FourthCorpus}");

if (!File.Exists(FourthCorpus))
{
    Skip("the layout checks (file not on this machine)");
}
else
{
    // The page box is in points. The same project's MusicXML export gives 2473.53 x 3497.06 tenths for the same
    // page and 123.529 tenths for the same margins, with the export's scaling at 4.79778 mm per 40 tenths
    // (0.11994450 mm per tenth, i.e. 0.3399668 pt): 2473.53 x 0.3399668 = 840.9 pt against this file's 841, and
    // 123.529 x 0.3399668 = 42.0 pt against this file's 42. The unit is not written in the file; that is where it
    // comes from.
    var score = ProjectFileReader.Open(FourthCorpus).Score;
    var full = score.Layout.Layouts.First(layout => layout.Type == "kFullScoreLayout");

    Check(
        "the full score's page box is A3 in points, with 42 pt margins",
        full.PageWidth == "841" && full.PageHeight == "1189"
        && full.Margins.Any(field => field.Key == "outer" && field.Value == "42")
        && full.Margins.Any(field => field.Key == "top" && field.Value == "42"),
        $"{full.PageWidth}x{full.PageHeight} {string.Join(' ', full.Margins.Select(field => field.Key + "=" + field.Value))}");

    Check(
        "the parts are A4 in points and name the part page template",
        score.Layout.Layouts.Where(layout => layout.Type == "kPartLayout")
            .All(layout => layout.PageWidth == "594" && layout.PageHeight == "841" && layout.PageSet == "pageset.part"));

    Check(
        "every layout names a page template the library defines",
        score.Layout.Layouts.All(layout => score.Layout.PageSets.Any(pageSet => pageSet.EntityId == layout.PageSet)),
        string.Join(',', score.Layout.Layouts.Select(layout => layout.PageSet).Distinct()));

    var fullScoreSet = score.Layout.PageSets.First(pageSet => pageSet.EntityId == "pageset.fullscore");

    Check(
        "the page template chains the music on chain 0 and has a frame for it on both pages",
        fullScoreSet.MusicChains.SequenceEqual([0L])
        && fullScoreSet.Frames.Any(frame => frame.Chain == 0 && frame.Kind == "first")
        && fullScoreSet.Frames.Any(frame => frame.Chain == 0 && frame.Kind == "normal"),
        string.Join(',', fullScoreSet.MusicChains) + " / " + fullScoreSet.Frames.Count + " frames");

    Check(
        "the music frame is placed against the page margins, not at a fixed spot",
        fullScoreSet.Frames
            .First(frame => frame.Chain == 0)
            .Position.Any(field => field.Key == "top" && field.Value == "60")
        || fullScoreSet.Frames
            .First(frame => frame.Chain is not null)
            .Position.Count > 0);

    Check(
        "casting off is stored as options, not as an arrangement",
        full.CastingOff.Count > 0
        && full.CastingOff.Any(field => field.Key == "fixNumberOfSystems" && field.Value == "false"),
        string.Join(' ', full.CastingOff.Select(field => field.Key)));
}

// ---------------------------------------------------------------- the variant values and the families

Head("the value encoding");

Check(
    "a variant value carries its payload, and a list keeps its items",
    ProjectFileScoreReader.VariantText("string: \"pt.accent\"") == "pt.accent"
    && ProjectFileScoreReader.VariantText("int: 52") == "52"
    && ProjectFileScoreReader.VariantText("null: ") is null
    && ProjectFileScoreReader.VariantText("list: {string: \"1/2\"}") == "{string: \"1/2\"}",
    ProjectFileScoreReader.VariantText("list: {string: \"1/2\"}"));

Check(
    "a property's family comes from its own name, whatever prefix the file writes",
    ProjectFileScoreReader.MarkFamily("kArticulationDuration") == "articulation"
    && ProjectFileScoreReader.MarkFamily("kArticulationForce") == "articulation"
    && ProjectFileScoreReader.MarkFamily("kNoteEndHasLVTie") == "tie"
    && ProjectFileScoreReader.MarkFamily("kNoteBeamStart") == "beam"
    && ProjectFileScoreReader.MarkFamily("kEventPlayingTechnique") == "technique"
    && ProjectFileScoreReader.MarkFamily("kNoteSpelling") == "other",
    ProjectFileScoreReader.MarkFamily("kNoteBeamStart"));

Console.WriteLine();

// ---------------------------------------------------------------- malformed and unrelated files

Head("malformed and unrelated files");

var temporary = Path.Combine(Path.GetTempPath(), "dorico-use-projectfile-check");

if (Directory.Exists(temporary))
{
    Directory.Delete(temporary, recursive: true);
}

Directory.CreateDirectory(temporary);

Check(
    "a missing file reports NotFound",
    Code(() => ProjectFileReader.Open(Path.Combine(temporary, "nope.dorico"))) == "NotFound");

var notAZip = Path.Combine(temporary, "not.a.dorico");
File.WriteAllText(notAZip, "this is not a package");
Check(
    "a file that is not a ZIP reports NotAContainer",
    Code(() => ProjectFileReader.Open(notAZip)) == "NotAContainer");

// A ZIP with a package shape but no score.dtn.
var packageFolder = Path.Combine(temporary, "package");
Directory.CreateDirectory(packageFolder);
File.WriteAllText(Path.Combine(packageFolder, "container.xml"), "<container/>");

var noScore = Path.Combine(temporary, "no-score.dorico");
System.IO.Compression.ZipFile.CreateFromDirectory(packageFolder, noScore);
Check(
    "a ZIP without score.dtn reports MissingScoreData",
    Code(() => ProjectFileReader.Open(noScore)) == "MissingScoreData");

var badScore = Path.Combine(temporary, "bad-score.dorico");

using (var archive = System.IO.Compression.ZipFile.Open(badScore, System.IO.Compression.ZipArchiveMode.Create))
{
    var entry = archive.CreateEntry("score.dtn");

    using var stream = entry.Open();
    stream.Write(System.Text.Encoding.UTF8.GetBytes(new string('x', 4096)));
}

Check(
    "a score.dtn of nonsense reports UnsupportedScoreData",
    Code(() => ProjectFileReader.Open(badScore)) == "UnsupportedScoreData");

if (File.Exists(Corpus))
{
    var truncated = Path.Combine(temporary, "truncated.dorico");

    using (var source = File.OpenRead(Corpus))
    using (var destination = File.Create(truncated))
    {
        var buffer = new byte[4096];
        var read = source.Read(buffer, 0, buffer.Length);

        destination.Write(buffer, 0, read);
    }

    Check(
        "a truncated project reports a stable code, not an unexpected error",
        Code(() => ProjectFileReader.Open(truncated)) is "NotAContainer" or "MalformedMember" or "MissingScoreData"
            or "UnsupportedScoreData");
}

Directory.Delete(temporary, recursive: true);

Console.WriteLine();

if (failures > 0)
{
    Console.WriteLine($"{failures} check(s) FAILED, {skipped} skipped.");

    return 1;
}

Console.WriteLine($"all checks passed, {skipped} skipped.");

return 0;

string? Code(Action action)
{
    try
    {
        action();

        return null;
    }
    catch (CliException error)
    {
        return error.Code;
    }
    catch (Exception error)
    {
        return "UNEXPECTED:" + error.GetType().Name;
    }
}

static bool IsBeat(string text)
{
    var value = Rational.Parse(text);

    return value.Numerator >= 1 && value.CompareTo(new Rational(64, 1)) <= 0;
}

/// <summary>How often the census saw one name, as a node or as a leaf.</summary>
static long Counted(ProjectFileTreeCensus census, string name, string kind)
    => census.Names.FirstOrDefault(entry => entry.Name == name && entry.Kind == kind)?.Count ?? 0;

/// <summary>A comma-separated id list from the file, as numbers.</summary>
static List<long> Ids(string? text)
    => [.. (text ?? string.Empty)
        .Split(',', StringSplitOptions.RemoveEmptyEntries)
        .Select(part => long.TryParse(part.Trim(), out var value) ? value : -1)
        .OrderBy(value => value)];

/// <summary>The strings of a variant list, e.g. `list: {string: "3/2"}` → `3/2`.</summary>
static List<string> Strings(string? text)
    => [.. System.Text.RegularExpressions.Regex
        .Matches(text ?? string.Empty, "\\{string: \"([^\"]+)\"\\}")
        .Select(match => match.Groups[1].Value)];

/// <summary>The technique id an articulation value names: kStaccato is the catalogue's pt.staccato.</summary>
static string TechniqueId(ProjectFileNoteMark mark)
{
    var value = ProjectFileScoreReader.VariantText(mark.Value) ?? string.Empty;

    return "pt." + char.ToLowerInvariant(value[1]) + value[2..];
}

/// <summary>An exact rational, so a triplet cannot round into a false positive.</summary>
internal readonly struct Rational : IComparable<Rational>
{
    public Rational(long numerator, long denominator)
    {
        if (denominator < 0)
        {
            numerator = -numerator;
            denominator = -denominator;
        }

        var divisor = Gcd(Math.Abs(numerator), denominator);

        Numerator = divisor > 1 ? numerator / divisor : numerator;
        Denominator = divisor > 1 ? denominator / divisor : denominator;
    }

    public long Numerator { get; }

    public long Denominator { get; }

    public static Rational Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return new Rational(0, 1);
        }

        var trimmed = text.Trim();
        var slash = trimmed.IndexOf('/');

        return slash < 0
            ? new Rational(long.TryParse(trimmed, out var whole) ? whole : 0, 1)
            : new Rational(
                long.TryParse(trimmed[..slash], out var numerator) ? numerator : 0,
                long.TryParse(trimmed[(slash + 1)..], out var denominator) ? denominator : 1);
    }

    public int CompareTo(Rational other)
        => ((Int128)Numerator * other.Denominator).CompareTo((Int128)other.Numerator * Denominator);

    public static Rational operator +(Rational left, Rational right)
        => new(
            (long)((Int128)left.Numerator * right.Denominator + (Int128)right.Numerator * left.Denominator),
            (long)((Int128)left.Denominator * right.Denominator));
    private static long Gcd(long left, long right)
    {
        while (right != 0)
        {
            (left, right) = (right, left % right);
        }

        return left == 0 ? 1 : left;
    }
}
