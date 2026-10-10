using DoricoUse.Cli;
using DoricoUse.Cli.Dorico;

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
