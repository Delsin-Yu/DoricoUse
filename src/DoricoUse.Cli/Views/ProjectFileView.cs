using System.Globalization;
using System.Text.Json.Nodes;
using DoricoUse.Cli.Dorico;
using DoricoUse.Cli.Json;

namespace DoricoUse.Cli.Views;

/// <summary>
/// The report <c>file read</c> prints: what a <c>.dorico</c> file contains, decoded from disk.
/// </summary>
/// <remarks>
/// The shape follows the agent's question. <c>file</c> says which container this is, <c>metadata</c> is what
/// scoreinfo.xml says, <c>score</c> is what the binary holds: players, layouts, flows, the staves notes were found
/// on, the metre, and then the notes themselves — pitches, rational durations, measures and beats.
/// </remarks>
internal static class ProjectFileView
{
    /// <summary>How many notes the report carries unless the caller asks for more.</summary>
    public const int DefaultNoteLimit = 100;

    /// <summary>
    /// What the reader derives rather than reads. An agent that trusts "pitch" as notation, or "measure" as a stored
    /// number, would be wrong in ways the JSON cannot show.
    /// </summary>
    /// <remarks>
    /// These lists are assigned into every report through <c>DeepClone</c>: a shared node that gets parented twice
    /// throws <c>InvalidOperationException: The node already has a parent</c>, so cloning is what keeps
    /// <c>Report</c> callable more than once in one process.
    /// </remarks>
    private static readonly JsonArray Caveats =
    [
        (JsonNode?)JsonValue.Create(
            "pitch: the project stores a MIDI note number, and 'pitch' is the name derived from that number alone, so "
            + "it uses sharps only and reads Bb5 as A#5. Read 'midi' when the exact pitch matters, and 'spelling' for "
            + "the letter the score actually notated."),
        (JsonNode?)JsonValue.Create(
            "spelling: the letter the score itself spells the note with, read from the note event's kNoteSpelling "
            + "property (the file writes it as a variant string, e.g. string: \"B\" -> \"B\"). It is a *letter only*: "
            + "the project stores no accidental for the note, so 'spelling' plus 'midi' is what pins the written "
            + "pitch (midi 82 with letter B is Bb5, with letter A it is A#5) — neither is derived from the other. "
            + "Every note event in all 20 corpus files states exactly one kNoteSpelling and its value is always a "
            + "single letter A-G (199,893 notes, 0 exceptions), so null means the file stated none at all; a grace "
            + "note carries one too and is a separate event kind that this list does not report (22 of the 16,024 "
            + "kNoteSpelling properties in Bad Apple!!). Verified note by note against Dorico's own MusicXML export "
            + "of 無限城への誘い, a second source: the letter agrees with the export's <step> and 'midi' with the "
            + "export's sounding pitch on all 8,476 pitched note events of that score (8,316 onsets), 0 differences "
            + "— once the export's own <transpose> is applied, because the export writes the transposed pitch for its "
            + "F and Bb instruments while the file stores the sounding one. The remaining 129 note events are "
            + "unpitched percussion, which the export writes as <unpitched> with only a staff display position and "
            + "so no sounding pitch to compare with: there the letter is the drum-mapped pitch (a bass drum is B4 "
            + "here) and it was not checked against anything. For the non-transposing parts the accidental implied "
            + "by the letter and the MIDI number is the export's own <alter> on all 6,968 of their notes."),
        (JsonNode?)JsonValue.Create(
            "duration: an exact rational in crotchets (a 16th is 1/4, a 4/4 bar is 4). It is the sounding duration of "
            + "the tie chain, so a note tied across a bar line is one event."),
        (JsonNode?)JsonValue.Create(
            "measure/beat: bar lines are not stored. Measures are replayed from the bar-division events in 'meters' "
            + "and every note's 'position', so they are derived, and rests are gaps rather than events."),
        (JsonNode?)JsonValue.Create(
            "stave: notes are attributed to the player and instrument their stream belongs to; the sounding pitch is "
            + "the note event's, and a part layout applies its own transposition when it is printed."),
        (JsonNode?)JsonValue.Create(
            "marks: what the score stores about a note that is not a note — its articulations, its tie's engraving, a "
            + "beam break, the playing technique attached to it — is a *property of the event*, not an event of its "
            + "own. --articulations, --ties, --beams and --techniques report those properties; 'name' is derived from "
            + "the score's own playing-technique catalogue and is null where the catalogue has no entry, and every raw "
            + "value is reported beside it. Property names this reader does not model are counted under 'other'."),
    ];

    /// <summary>What to include, and what to leave out.</summary>
    public sealed record Options
    {
        public int NoteLimit { get; init; } = DefaultNoteLimit;

        public int? FirstMeasure { get; init; }

        public int? LastMeasure { get; init; }

        public string? Player { get; init; }

        /// <summary>Include the dynamic markings the flows hold.</summary>
        public bool Dynamics { get; init; }

        /// <summary>How many dynamics to list. Null is the default, -1 is every one of them.</summary>
        public int? DynamicLimit { get; init; }

        /// <summary>Include the lyric element tables.</summary>
        public bool Lyrics { get; init; }

        /// <summary>Include the slurs: where each starts and how long it is.</summary>
        public bool Slurs { get; init; }

        /// <summary>Include the articulations stored on the notes.</summary>
        public bool Articulations { get; init; }

        /// <summary>Include what the file says about ties: the LV tie, a forced direction, a split.</summary>
        public bool Ties { get; init; }

        /// <summary>Include what the file says about beams: the beam breaks it stores.</summary>
        public bool Beams { get; init; }

        /// <summary>Include the playing techniques, as events and as properties of notes.</summary>
        public bool Techniques { get; init; }

        /// <summary>Include the layout: the page boxes, their margins and the frames of the page templates.</summary>
        public bool Layout { get; init; }

        /// <summary>How many slurs, articulations, ties, beams or techniques to list. Null is the default, -1 all.</summary>
        public int? MarkLimit { get; init; }

        /// <summary>A node name to dump the decoded tree for, or <c>*</c> for a census of every name in the file.</summary>
        public string? Tree { get; init; }

        /// <summary>How many levels of child nodes a tree dump shows. Null is the default.</summary>
        public int? Depth { get; init; }

        /// <summary>How many occurrences, fields, children or names a tree dump lists. Null is the default, -1 everything.</summary>
        public int? TreeLimit { get; init; }
    }

    /// <summary>How many slurs, articulations, ties, beams or techniques the report carries unless asked for more.</summary>
    public const int DefaultMarkLimit = 100;

    /// <summary>How many dynamics the report carries unless the caller asks for more.</summary>
    public const int DefaultDynamicLimit = 100;

    /// <summary>How deep a tree dump goes, and how many occurrences, fields and children it lists.</summary>
    public const int DefaultDepth = 2;

    public const int DefaultOccurrenceLimit = 8;

    /// <summary>How many node names the "what does this file hold?" census lists by default.</summary>
    public const int DefaultCensusLimit = 200;

    public static JsonObject Report(ProjectFile file, Options options)
    {
        var report = new JsonObject
        {
            ["file"] = File(file),
            ["metadata"] = Metadata(file.Metadata),
            ["members"] = Members(file.Members),
        };

        report["score"] = Score(file.Score, options);

        if (options.Tree is { Length: > 0 } tree)
        {
            report["tree"] = Tree(file.ScoreData, tree, options);
        }

        return report;
    }

    private static JsonObject File(ProjectFile file) => new()
    {
        ["path"] = file.Path,
        ["bytes"] = file.Bytes,
        ["kind"] = "Dorico project (ZIP package)",
        ["container"] = new JsonObject
        {
            ["version"] = file.Container.Version,
            ["rootFile"] = file.Container.RootPath,
            ["mediaType"] = file.Container.MediaType,
        },
        ["members"] = file.Members.Count,
    };

    private static JsonObject Metadata(ProjectFileMetadata metadata) => new()
    {
        ["title"] = metadata.Title,
        ["subtitle"] = metadata.Subtitle,
        ["composer"] = metadata.Composer,
        ["arranger"] = metadata.Arranger,
        ["lyricist"] = metadata.Lyricist,
        ["copyright"] = metadata.Copyright,
        ["createdDate"] = metadata.CreatedDate,
        ["lastSavedDate"] = metadata.LastSavedDate,
        ["createdVersion"] = metadata.CreatedVersion,
        ["lastSavedVersion"] = metadata.LastSavedVersion,
        ["projectDuration"] = metadata.ProjectDuration,
        ["players"] = Strings(metadata.Players),
        ["flows"] = FlowTitles(metadata.FlowTitles, metadata.FlowDurations),
    };

    private static JsonArray Strings(IReadOnlyList<string> values)
    {
        var array = new JsonArray();

        foreach (var value in values)
        {
            JsonArr.AddText(array, value);
        }

        return array;
    }

    private static JsonArray FlowTitles(IReadOnlyList<string> titles, IReadOnlyList<string> durations)
    {
        var array = new JsonArray();

        for (var index = 0; index < titles.Count; index++)
        {
            JsonArr.AddNode(array, new JsonObject
            {
                ["title"] = titles[index],
                ["duration"] = index < durations.Count ? durations[index] : null,
            });
        }

        return array;
    }

    private static JsonArray Members(IReadOnlyList<ProjectFileMember> members)
    {
        var array = new JsonArray();

        foreach (var member in members)
        {
            JsonArr.AddNode(array, new JsonObject
            {
                ["path"] = member.Path,
                ["bytes"] = member.Bytes,
                ["compressedBytes"] = member.CompressedBytes,
            });
        }

        return array;
    }

    private static JsonObject Score(ProjectFileScore score, Options options)
    {
        var (notes, matched) = Select(score.Notes, options);

        var report = new JsonObject
        {
            ["info"] = new JsonObject
            {
                ["fileVersion"] = score.FileVersion,
                ["title"] = score.Info.Title,
                ["subtitle"] = score.Info.Subtitle,
                ["composer"] = score.Info.Composer,
                ["arranger"] = score.Info.Arranger,
                ["lyricist"] = score.Info.Lyricist,
                ["createdDate"] = score.Info.CreatedDate,
                ["lastSavedDate"] = score.Info.LastSavedDate,
                ["createdVersion"] = score.Info.CreatedVersion,
                ["lastSavedVersion"] = score.Info.LastSavedVersion,
                ["originator"] = score.Info.Originator,
            },
            ["players"] = Players(score.Players),
            ["layouts"] = Layouts(score.Layouts),
            ["flows"] = Flows(score.Flows),
            ["staves"] = Staves(score.Staves),
            ["meters"] = Meters(score.Meters),
            ["measures"] = new JsonObject
            {
                ["count"] = score.LastMeasure,
                ["derived"] = true,
                ["note"] = "bar lines are not stored; measures are replayed from the metre events below",
            },
            ["counts"] = new JsonObject
            {
                ["notes"] = score.Notes.Count,
                ["notesMatched"] = matched,
                ["notesReturned"] = notes.Count,
                ["players"] = score.Players.Count,
                ["layouts"] = score.Layouts.Count,
                ["flows"] = score.Flows.Count,
                ["stavesWithNotes"] = score.Staves.Count,
                ["measures"] = score.LastMeasure,
                ["slurs"] = score.Slurs.Count,
                ["noteMarks"] = score.NoteMarks.Count,
                ["techniqueEvents"] = score.TechniqueEvents.Count,
            },
            ["caveats"] = Caveats.DeepClone(),
            ["parser"] = new JsonObject
            {
                ["names"] = score.NameCount,
                ["values"] = score.ValueCount,
                ["records"] = score.RecordCount,
            },
        };

        if (options.Dynamics)
        {
            report["dynamics"] = Dynamics(score, options);
        }

        if (options.Slurs)
        {
            report["slurs"] = Slurs(score, options);
        }

        if (options.Articulations)
        {
            report["articulations"] = Marks(score, options, "articulation");
        }

        if (options.Ties)
        {
            report["ties"] = Marks(score, options, "tie");
        }

        if (options.Beams)
        {
            report["beams"] = Marks(score, options, "beam");
        }

        if (options.Techniques)
        {
            report["techniques"] = Techniques(score, options);
        }

        if (options.Layout)
        {
            report["layout"] = Layout(score);
        }

        if (options.Lyrics)
        {
            report["lyrics"] = Lyrics(score.Lyrics);
        }

        report["notes"] = Notes(notes);

        return report;
    }

    private static (List<ProjectFileNote> Notes, int Matched) Select(
        IReadOnlyList<ProjectFileNote> notes,
        Options options)
    {
        var matched = 0;
        var selected = new List<ProjectFileNote>();

        foreach (var note in notes)
        {
            if (options.FirstMeasure is { } first && note.Measure < first)
            {
                continue;
            }

            if (options.LastMeasure is { } last && note.Measure > last)
            {
                continue;
            }

            if (options.Player is { } player
                && !Pattern.Matches(note.Player, player)
                && !Pattern.Matches(note.Instrument, player))
            {
                continue;
            }

            matched++;

            if (options.NoteLimit < 0 || selected.Count < options.NoteLimit)
            {
                selected.Add(note);
            }
        }

        return (selected, matched);
    }

    private static JsonArray Players(IReadOnlyList<ProjectFilePlayer> players)
    {
        var array = new JsonArray();

        foreach (var player in players)
        {
            var instruments = new JsonArray();

            foreach (var instrument in player.Instruments)
            {
                JsonArr.AddNode(instruments, new JsonObject
                {
                    ["id"] = instrument.Id,
                    ["name"] = instrument.Name,
                    ["entityId"] = instrument.EntityId,
                    ["staves"] = instrument.Staves,
                    ["range"] = instrument.Range,
                });
            }

            JsonArr.AddNode(array, new JsonObject
            {
                ["id"] = player.Id,
                ["name"] = player.Name,
                ["shortName"] = player.ShortName,
                ["instruments"] = instruments,
            });
        }

        return array;
    }

    private static JsonArray Layouts(IReadOnlyList<ProjectFileLayout> layouts)
    {
        var array = new JsonArray();

        foreach (var layout in layouts)
        {
            JsonArr.AddNode(array, new JsonObject
            {
                ["id"] = layout.Id,
                ["number"] = layout.Number,
                ["name"] = layout.Name,
                ["type"] = layout.Type,
            });
        }

        return array;
    }

    private static JsonArray Flows(IReadOnlyList<ProjectFileFlow> flows)
    {
        var array = new JsonArray();

        foreach (var flow in flows)
        {
            JsonArr.AddNode(array, new JsonObject
            {
                ["id"] = flow.Id,
                ["name"] = flow.Name,
                ["streams"] = flow.Streams,
                ["blocks"] = flow.Blocks,
                ["notes"] = flow.Notes,
            });
        }

        return array;
    }

    private static JsonArray Staves(IReadOnlyList<ProjectFileStave> staves)
    {
        var array = new JsonArray();

        foreach (var stave in staves)
        {
            JsonArr.AddNode(array, new JsonObject
            {
                ["stream"] = stave.Stream,
                ["kind"] = stave.Kind,
                ["player"] = stave.Player,
                ["instrument"] = stave.Instrument,
                ["stave"] = stave.Stave,
                ["voice"] = stave.Voice,
                ["notes"] = stave.Notes,
            });
        }

        return array;
    }

    private static JsonArray Meters(IReadOnlyList<ProjectFileMeter> meters)
    {
        var array = new JsonArray();

        foreach (var meter in meters)
        {
            JsonArr.AddNode(array, new JsonObject
            {
                ["bar"] = meter.Bar,
                ["position"] = meter.Position,
                ["timeSignature"] = meter.TimeSignature,
            });
        }

        return array;
    }

    private static JsonArray Notes(IReadOnlyList<ProjectFileNote> notes)
    {
        var array = new JsonArray();

        foreach (var note in notes)
        {
            JsonArr.AddNode(array, new JsonObject
            {
                ["eventId"] = note.EventId,
                ["measure"] = note.Measure,
                ["beat"] = note.Beat,
                ["position"] = note.Position,
                ["duration"] = note.Duration,
                ["pitch"] = note.Pitch,
                ["midi"] = note.Midi,
                ["spelling"] = note.Spelling,
                ["player"] = note.Player,
                ["instrument"] = note.Instrument,
                ["stave"] = note.Stave,
                ["voice"] = note.Voice,
                ["stream"] = note.Stream,
            });
        }

        return array;
    }

    /// <summary>
    /// The dynamic markings, filtered by the same measure and player the notes are — a report about bars 12-16 says
    /// what is written in bars 12-16.
    /// </summary>
    private static JsonObject Dynamics(ProjectFileScore score, Options options)
    {
        var limit = options.DynamicLimit ?? DefaultDynamicLimit;
        var selected = new List<ProjectFileDynamic>();
        var marks = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var unnamed = 0;
        var matched = 0;

        foreach (var dynamic in score.Dynamics)
        {
            if (options.FirstMeasure is { } first && dynamic.Measure < first)
            {
                continue;
            }

            if (options.LastMeasure is { } last && dynamic.Measure > last)
            {
                continue;
            }

            if (options.Player is { } player
                && !Pattern.Matches(dynamic.Player, player)
                && !Pattern.Matches(dynamic.Instrument, player))
            {
                continue;
            }

            matched++;

            foreach (var mark in dynamic.Marks)
            {
                if (mark.Mark is { } text)
                {
                    marks[text] = marks.GetValueOrDefault(text) + 1;
                }
                else
                {
                    unnamed++;
                }
            }

            if (limit < 0 || selected.Count < limit)
            {
                selected.Add(dynamic);
            }
        }

        var items = new JsonArray();

        foreach (var dynamic in selected)
        {
            var list = new JsonArray();

            foreach (var mark in dynamic.Marks)
            {
                JsonArr.AddNode(list, DynamicMark(mark));
            }

            JsonArr.AddNode(items, new JsonObject
            {
                ["eventId"] = dynamic.EventId,
                ["measure"] = dynamic.Measure,
                ["beat"] = dynamic.Beat,
                ["position"] = dynamic.Position,
                ["duration"] = dynamic.Duration,
                ["group"] = dynamic.Group,
                ["player"] = dynamic.Player,
                ["instrument"] = dynamic.Instrument,
                ["stave"] = dynamic.Stave,
                ["voice"] = dynamic.Voice,
                ["stream"] = dynamic.Stream,
                ["marks"] = list,
            });
        }

        var counts = new JsonObject();

        foreach (var pair in marks)
        {
            counts[pair.Key] = pair.Value;
        }

        return new JsonObject
        {
            ["events"] = score.Dynamics.Count,
            ["matched"] = matched,
            ["returned"] = items.Count,
            ["groups"] = score.DynamicGroups.Count,
            ["marks"] = counts,
            ["unnamedMarks"] = unnamed,
            ["caveats"] = DynamicCaveats.DeepClone(),
            ["items"] = items,
        };
    }

    private static JsonObject DynamicMark(ProjectFileDynamicMark mark)
    {
        var fields = new JsonArray();

        foreach (var field in mark.Fields)
        {
            JsonArr.AddNode(fields, new JsonObject
            {
                ["key"] = field.Key,
                ["value"] = field.Value,
            });
        }

        return new JsonObject
        {
            ["kind"] = mark.Kind,
            ["mark"] = mark.Mark,
            ["position"] = mark.Position,
            ["at"] = mark.At,
            ["measure"] = mark.Measure,
            ["beat"] = mark.Beat,
            ["duration"] = mark.Duration,
            ["intensity"] = mark.Intensity,
            ["intensityLevel"] = mark.IntensityLevel,
            ["direction"] = mark.Direction,
            ["subito"] = mark.Subito,
            ["fields"] = fields,
        };
    }

    /// <summary>Whether an event is inside the measure range and matches the player filter.</summary>
    private static bool Matches(ProjectFileLocation location, Options options)
    {
        if (options.FirstMeasure is { } first && location.Measure < first)
        {
            return false;
        }

        if (options.LastMeasure is { } last && location.Measure > last)
        {
            return false;
        }

        return options.Player is not { } player
            || Pattern.Matches(location.Player, player)
            || Pattern.Matches(location.Instrument, player);
    }

    /// <summary>Where an event sits, as the notes and the dynamics report it too.</summary>
    private static JsonObject Location(ProjectFileLocation location) => new()
    {
        ["eventId"] = location.EventId,
        ["measure"] = location.Measure,
        ["beat"] = location.Beat,
        ["position"] = location.Position,
        ["duration"] = location.Duration,
        ["pitch"] = location.Pitch,
        ["midi"] = location.Midi,
        ["player"] = location.Player,
        ["instrument"] = location.Instrument,
        ["stave"] = location.Stave,
        ["voice"] = location.Voice,
        ["stream"] = location.Stream,
    };

    /// <summary>
    /// The slurs: each one is an event with a position and a length, and the slurs of one object (a slur over a
    /// chord) are one element with one event per notehead.
    /// </summary>
    private static JsonObject Slurs(ProjectFileScore score, Options options)
    {
        var limit = options.MarkLimit ?? DefaultMarkLimit;
        var items = new JsonArray();
        var matched = 0;
        var shared = new SortedDictionary<int, int>();
        var directions = new SortedDictionary<string, int>(StringComparer.Ordinal);

        foreach (var slur in score.Slurs)
        {
            if (!Matches(slur.Location, options))
            {
                continue;
            }

            matched++;
            shared[slur.Noteheads] = shared.GetValueOrDefault(slur.Noteheads) + 1;

            if (slur.Direction is { } direction)
            {
                directions[direction] = directions.GetValueOrDefault(direction) + 1;
            }

            if (limit < 0 || items.Count < limit)
            {
                JsonArr.AddNode(items, new JsonObject
                {
                    ["location"] = Location(slur.Location),
                    ["element"] = slur.Element,
                    ["noteheads"] = slur.Noteheads,
                    ["direction"] = slur.Direction,
                    ["endVoice"] = slur.EndVoice,
                    ["fields"] = Fields(slur.Fields),
                });
            }
        }

        return new JsonObject
        {
            ["events"] = score.Slurs.Count,
            ["elements"] = score.SlurElements.Count,
            ["matched"] = matched,
            ["returned"] = items.Count,
            ["noteheadsPerSlur"] = Numbers(shared),
            ["directions"] = Strings(directions),
            ["caveats"] = SlurCaveats.DeepClone(),
            ["items"] = items,
        };
    }

    /// <summary>
    /// The marks one note property family holds, with what the file says and what this reader could name. The
    /// properties the family does not cover are counted too, so a file that stores an articulation somewhere else
    /// cannot look like a file without articulations.
    /// </summary>
    private static JsonObject Marks(ProjectFileScore score, Options options, string family)
    {
        var limit = options.MarkLimit ?? DefaultMarkLimit;
        var items = new JsonArray();
        var matched = 0;
        var unnamed = 0;
        var names = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var properties = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var values = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var families = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var others = new SortedDictionary<string, int>(StringComparer.Ordinal);

        foreach (var mark in score.NoteMarks)
        {
            families[mark.Family] = families.GetValueOrDefault(mark.Family) + 1;

            if (mark.Family != family)
            {
                if (family == "articulation")
                {
                    others[mark.Property] = others.GetValueOrDefault(mark.Property) + 1;
                }

                continue;
            }

            if (!Matches(mark.Location, options))
            {
                continue;
            }

            matched++;
            properties[mark.Property] = properties.GetValueOrDefault(mark.Property) + 1;

            var text = ProjectFileScoreReader.VariantText(mark.Value) ?? "<empty>";
            values[text] = values.GetValueOrDefault(text) + 1;

            if (mark.Name is { } name)
            {
                names[name] = names.GetValueOrDefault(name) + 1;
            }
            else
            {
                unnamed++;
            }

            if (limit < 0 || items.Count < limit)
            {
                JsonArr.AddNode(items, new JsonObject
                {
                    ["location"] = Location(mark.Location),
                    ["property"] = mark.Property,
                    ["value"] = mark.Value,
                    ["text"] = ProjectFileScoreReader.VariantText(mark.Value),
                    ["name"] = mark.Name,
                });
            }
        }

        var report = new JsonObject
        {
            ["marks"] = families.GetValueOrDefault(family),
            ["matched"] = matched,
            ["returned"] = items.Count,
            ["properties"] = Strings(properties),
            ["values"] = Strings(values),
            ["names"] = Strings(names),
            ["families"] = Strings(families),
            ["caveats"] = (family switch
            {
                "articulation" => ArticulationCaveats,
                "tie" => TieCaveats,
                _ => BeamCaveats,
            }).DeepClone(),
            ["items"] = items,
        };

        if (family == "articulation")
        {
            report["unnamed"] = unnamed;
            report["otherProperties"] = Strings(Take(others, 24));
        }

        return report;
    }

    /// <summary>The playing techniques: the events that place one, and the techniques stored on the notes.</summary>
    private static JsonObject Techniques(ProjectFileScore score, Options options)
    {
        var limit = options.MarkLimit ?? DefaultMarkLimit;
        var items = new JsonArray();
        var matched = 0;
        var names = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var techniques = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var texts = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var noteCount = 0;

        var rows = new List<(string Source, string Position, JsonObject Item)>();

        foreach (var technique in score.TechniqueEvents)
        {
            if (!Matches(technique.Location, options))
            {
                continue;
            }

            var name = technique.TechniqueName ?? technique.TechniqueId;
            techniques[technique.TechniqueId ?? "<none>"] = techniques.GetValueOrDefault(technique.TechniqueId ?? "<none>") + 1;

            if (name is { } text)
            {
                names[text] = names.GetValueOrDefault(text) + 1;
            }

            if (technique.Text is { Length: > 0 } printed)
            {
                texts[printed] = texts.GetValueOrDefault(printed) + 1;
            }

            rows.Add((
                "event",
                technique.Location.Position,
                new JsonObject
                {
                    ["source"] = "event",
                    ["location"] = Location(technique.Location),
                    ["appearanceId"] = technique.AppearanceId,
                    ["appearanceType"] = technique.AppearanceType,
                    ["appearanceName"] = technique.AppearanceName,
                    ["text"] = technique.Text,
                    ["techniqueId"] = technique.TechniqueId,
                    ["techniqueName"] = technique.TechniqueName,
                    ["fields"] = Fields(technique.Fields),
                }));
        }

        foreach (var mark in score.NoteMarks.Where(mark => mark.Family == "technique"))
        {
            noteCount++;

            if (!Matches(mark.Location, options))
            {
                continue;
            }

            var id = ProjectFileScoreReader.VariantText(mark.Value);
            techniques[id ?? "<none>"] = techniques.GetValueOrDefault(id ?? "<none>") + 1;

            if (mark.Name is { } name)
            {
                names[name] = names.GetValueOrDefault(name) + 1;
            }

            rows.Add((
                "note",
                mark.Location.Position,
                new JsonObject
                {
                    ["source"] = "note",
                    ["location"] = Location(mark.Location),
                    ["property"] = mark.Property,
                    ["value"] = mark.Value,
                    ["techniqueId"] = id,
                    ["techniqueName"] = mark.Name,
                }));
        }

        matched = rows.Count;

        foreach (var row in rows.OrderBy(row => row, Comparer<(string Source, string Position, JsonObject Item)>.Create(
            (left, right) => CompareRational(left.Position, right.Position) is var order && order != 0
                ? order
                : string.CompareOrdinal(left.Source, right.Source))))
        {
            if (limit < 0 || items.Count < limit)
            {
                JsonArr.AddNode(items, row.Item);
            }
        }

        return new JsonObject
        {
            ["events"] = score.TechniqueEvents.Count,
            ["noteTechniques"] = noteCount,
            ["matched"] = matched,
            ["returned"] = items.Count,
            ["names"] = Strings(names),
            ["texts"] = Strings(texts),
            ["techniques"] = Strings(techniques),
            ["definitions"] = Definitions(score),
            ["caveats"] = TechniqueCaveats.DeepClone(),
            ["items"] = items,
        };
    }

    /// <summary>
    /// The score library's own technique catalogue, which is what the derived names come from: it is the file's own
    /// statement that <c>pt.accent</c> is called "Accent".
    /// </summary>
    private static JsonArray Definitions(ProjectFileScore score)
    {
        var array = new JsonArray();

        foreach (var definition in score.TechniqueDefinitions.Take(400))
        {
            JsonArr.AddNode(array, new JsonObject
            {
                ["id"] = definition.EntityId,
                ["name"] = definition.Name,
                ["articulationType"] = definition.ArticulationType,
                ["groupType"] = definition.GroupType,
                ["aliasFor"] = definition.AliasFor,
            });
        }

        return array;
    }

    /// <summary>
    /// The layout the file stores: the page box of every layout, and the frames of every page template in the score
    /// library. Which measure lands on which page is not stored — casting off is computed at layout time — and the
    /// one place the file caches it is empty.
    /// </summary>
    private static JsonObject Layout(ProjectFileScore score)
    {
        var layouts = new JsonArray();

        foreach (var layout in score.Layout.Layouts)
        {
            JsonArr.AddNode(layouts, new JsonObject
            {
                ["id"] = layout.LayoutId,
                ["number"] = layout.Number,
                ["name"] = layout.Name,
                ["type"] = layout.Type,
                ["pageSet"] = layout.PageSet,
                ["spaceSize"] = layout.SpaceSize,
                ["pageWidth"] = layout.PageWidth,
                ["pageHeight"] = layout.PageHeight,
                ["margins"] = Fields(layout.Margins),
                ["castingOff"] = Fields(layout.CastingOff),
                ["frameChainOverrides"] = layout.FrameChainOverrides,
            });
        }

        var pageSets = new JsonArray();

        foreach (var pageSet in score.Layout.PageSets)
        {
            var music = new JsonArray();

            foreach (var chain in pageSet.MusicChains)
            {
                JsonArr.AddNode(music, JsonValue.Create(chain));
            }

            var text = new JsonArray();

            foreach (var chain in pageSet.TextChains)
            {
                JsonArr.AddNode(text, JsonValue.Create(chain));
            }

            var frames = new JsonArray();

            foreach (var frame in pageSet.Frames)
            {
                JsonArr.AddNode(frames, new JsonObject
                {
                    ["page"] = frame.Kind,
                    ["side"] = frame.Side,
                    ["chain"] = frame.Chain,
                    ["layoutSpecific"] = frame.LayoutSpecific,
                    ["index"] = frame.Index,
                    ["contentRelative"] = frame.ContentRelative,
                    ["position"] = Fields(frame.Position),
                    ["size"] = Fields(frame.Size),
                });
            }

            JsonArr.AddNode(pageSets, new JsonObject
            {
                ["entityId"] = pageSet.EntityId,
                ["name"] = pageSet.Name,
                ["type"] = pageSet.Type,
                ["musicChains"] = music,
                ["textChains"] = text,
                ["frames"] = frames,
            });
        }

        return new JsonObject
        {
            ["layouts"] = layouts,
            ["pageSets"] = pageSets,
            ["frameChainOverrides"] = score.Layout.FrameChainOverrides,
            ["caveats"] = LayoutCaveats.DeepClone(),
            ["items"] = pageSets.Count,
        };
    }

    private static JsonArray Fields(IReadOnlyList<ProjectFileTreeField> fields)
    {
        var array = new JsonArray();

        foreach (var field in fields)
        {
            JsonArr.AddNode(array, new JsonObject
            {
                ["key"] = field.Key,
                ["value"] = field.Value,
            });
        }

        return array;
    }

    private static JsonObject Strings(SortedDictionary<string, int> counts)
    {
        var report = new JsonObject();

        foreach (var pair in counts)
        {
            report[pair.Key] = pair.Value;
        }

        return report;
    }

    private static JsonObject Numbers(SortedDictionary<int, int> counts)
    {
        var report = new JsonObject();

        foreach (var pair in counts)
        {
            report[pair.Key.ToString(CultureInfo.InvariantCulture)] = pair.Value;
        }

        return report;
    }

    /// <summary>The first <paramref name="count"/> entries of a histogram, so a long tail cannot flood a report.</summary>
    private static SortedDictionary<string, int> Take(SortedDictionary<string, int> counts, int count)
        => new(counts.Take(count).ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal), StringComparer.Ordinal);

    /// <summary>
    /// Orders two positions written as the file writes them: exact cross-multiplication, so a triplet cannot sort
    /// into the wrong bar the way a double could.
    /// </summary>
    private static int CompareRational(string left, string right)
    {
        var (leftNumerator, leftDenominator) = SplitRational(left);
        var (rightNumerator, rightDenominator) = SplitRational(right);

        return ((Int128)leftNumerator * rightDenominator).CompareTo((Int128)rightNumerator * leftDenominator);
    }

    private static (long Numerator, long Denominator) SplitRational(string text)
    {
        var slash = text.IndexOf('/');

        if (slash < 0)
        {
            return (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var whole) ? whole : 0, 1);
        }

        return (
            long.TryParse(text[..slash], NumberStyles.Integer, CultureInfo.InvariantCulture, out var numerator) ? numerator : 0,
            long.TryParse(text[(slash + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var denominator) ? denominator : 1);
    }

    /// <summary>
    /// What the slur reading rests on. One independent source checked it end to end: Dorico's own MusicXML export of
    /// 無限城への誘い, beside the project, writes 214 slur start/stop pairs and this reader finds 214 slur events whose
    /// start and length are exactly those of the export, pair for pair.
    /// </summary>
    private static readonly JsonArray SlurCaveats =
    [
        (JsonNode?)JsonValue.Create(
            "a slur is one event: 'position' is where it starts and 'duration' is how long it is, both exact "
            + "rationals in crotchets. Checked against Dorico's own MusicXML export of 無限城への誘い (the project "
            + "saved beside it): its 214 <slur> start/stop pairs and this reader's 214 slur events agree on start and "
            + "length exactly, as multisets, with nothing left over on either side. The same reading on 秘封拉格1, "
            + "whose export came from Sibelius, does not agree (38 events against 41 <slur> elements, and no position "
            + "agreement at all): that file and its export are different engravings of the music, so it is reported "
            + "here rather than counted as evidence."),
        (JsonNode?)JsonValue.Create(
            "An event is one slur as a MusicXML export writes it (one per notehead the slur starts on), and 'element' "
            + "is the slur object those events share: 438 elements against 987 events in Bad Apple!!, 43 against 214 "
            + "in 無限城への誘い. The events of one element carry the same position and duration in 30 of those 30 "
            + "shared elements in 無限城への誘い and in 171 of 178 in Bad Apple!!, and most of them span more than one "
            + "stave, so 'noteheads' (up to 16) counts the events rather than a chord: a slur written for several "
            + "staves at once shares one element. What made the element shared is Dorico's own grouping and is not "
            + "modelled beyond that link; an element's referencingEventIDs were checked to list exactly the events "
            + "that name it, in every file of the corpus."),
        (JsonNode?)JsonValue.Create(
            "the element carries no geometry — the shape is drawn at layout time — so this reader reports where each "
            + "slur event starts and how long it is, and the element id, and nothing about its curve. 'direction' and "
            + "'endVoice' come from the event's own property table (kSlurForcedDirection, kSlurEndVoiceID) and are "
            + "null when the file does not state them; a forced direction is engraving, and no export in the corpus "
            + "was used to confirm more than its own spelling."),
    ];

    /// <summary>What the articulations are and what this reader could not name.</summary>
    private static readonly JsonArray ArticulationCaveats =
    [
        (JsonNode?)JsonValue.Create(
            "an articulation is a property of the note event, not an event of its own: kArticulationDuration "
            + "(a length: staccato, tenuto, staccato+tenuto) and kArticulationForce (a force: accent, marcato). "
            + "Checked against Dorico's own MusicXML export of 無限城への誘い: its 5070 <staccato/> and 1448 <accent/> "
            + "agree exactly with this file's 5070 kStaccato and 1448 kAccent, and both files are the same music. For "
            + "Bad Apple!! there is no export, so its 3914/349/3576 are this reader's reading alone."),
        (JsonNode?)JsonValue.Create(
            "'name' is the score's own name for the value: the file's playing-technique catalogue spells kStaccato as "
            + "pt.staccato with the name 'Staccato', and the family matches too (every kArticulationDuration value "
            + "resolves to a kLengths technique, every kArticulationForce value to a kDynamics one, across the whole "
            + "corpus). A value the catalogue has no entry for stays unnamed — kStaccatoTenuto, 24 of them in [A5] "
            + "Clannad Suite — and the raw value is reported either way. 'definitions' in --techniques is the "
            + "catalogue the names come from."),
        (JsonNode?)JsonValue.Create(
            "a tremolo is a property too (kNoteSingleStemTremolo, whose value is a variant list: a count and a flag) "
            + "and is reported raw: the export of 無限城への誘い writes 34 <tremolo> elements against this file's 36 "
            + "properties, so the count does not stand on its own, and nothing in the corpus fixes what the count "
            + "means when it is 0."),
        (JsonNode?)JsonValue.Create(
            "marks are read from the note events the notes list reports. A grace note is its own event kind "
            + "(GraceNoteEventDefinition: 22 in Bad Apple!!, 145 in another corpus file), is not part of the notes "
            + "list, and is not read here, so a mark on a grace note is not reported."),
    ];

    /// <summary>What the file does and does not say about ties.</summary>
    private static readonly JsonArray TieCaveats =
    [
        (JsonNode?)JsonValue.Create(
            "the file has no tie event: a tie chain is one note event whose 'duration' is the whole sounding length, "
            + "and where the tied noteheads fall is engraving, decided when the score is laid out. Dorico's own "
            + "MusicXML export of 無限城へのい誘い writes 110 such chains as 112 <tie> pairs, which is what a tie chain "
            + "looks like from the outside; this reader reports the events, not the noteheads."),
        (JsonNode?)JsonValue.Create(
            "what the file *does* state about ties is here: kNoteEndHasLVTie (a laissez-vibrer tie — 17 properties "
            + "against Dorico's export of 無限城への誘い and its 17 <tied type=\"let-ring\"/>), kNoteTiedToEvent (a tie "
            + "to a named event, one in the whole corpus), kNoteForcedTieDirection (up/down), kNoteSplitDurations and "
            + "kNoteForcedSplitPositions (the written values of a chain, as strings — one value where the note is "
            + "written as itself). A note with none of these may still be tied; a note with one is stating something. "
            + "The split values are a second reading of the chain when they are there: in Bad Apple!! all 28 sum "
            + "exactly to the note's own sounding duration, and in 残无的命运 391 of 398 do (the other 7 state one "
            + "value that is shorter, so they are not the whole chain)."),
    ];

    /// <summary>What the file does and does not say about beams.</summary>
    private static readonly JsonArray BeamCaveats =
    [
        (JsonNode?)JsonValue.Create(
            "there is no beam event in the file: the beaming a score is printed with is computed at layout time from "
            + "the note values, the metre and the notation options, and this reader does not model that. What a note "
            + "can carry is a beam property — kBeamStart, kBeamEnd, kBeamNone, kBeamPartialBeamDirection — which is "
            + "the group the file had to state rather than compute; an empty list here means no note states one, not "
            + "that the score has no beams (無限城への誘い states none and is a fully beamed orchestral score)."),
        (JsonNode?)JsonValue.Create(
            "the values are reported as the file writes them and the meaning of the numbers inside them is not named "
            + "here — for instance whether the 3 of a kBeamStart value counts beams. No file in the corpus has both "
            + "beam properties and a MusicXML export, so nothing here is checked against an export: the reading of "
            + "these properties as group boundaries rests on the file alone. In Bad Apple!! the 8 starts and 8 ends "
            + "pair a note with a later one (Piano A1→A2 two crotchets on, Xylophone F5→A5), and all 16 also state "
            + "their written values; in 畜生界传说 the properties are frequent (6394 starts), which is what an imported "
            + "beam group looks like."),
    ];

    /// <summary>What the technique reading rests on.</summary>
    private static readonly JsonArray TechniqueCaveats =
    [
        (JsonNode?)JsonValue.Create(
            "a playing technique reaches the score two ways and both are reported: as an event of its own "
            + "(PlayingTechniqueEventDefinition, 35 in Bad Apple!!) and as a property of a note (kEventPlayingTechnique, "
            + "45 there). The event names an *appearance* — pta.pizzText — and the appearance's own definition names "
            + "the technique behind it: the score library spells pta.pizzText as text 'pizz' in Italian, for the "
            + "technique pt.pizzicato, which its catalogue calls 'Pizzicato'. Both lists are the file's own; the link "
            + "between them is reported, not assumed."),
        (JsonNode?)JsonValue.Create(
            "no file in the corpus has both a playing technique and a MusicXML export, so nothing here is checked "
            + "against an export: the counts are this reader's reading of the two places the file keeps a technique. "
            + "A technique a user defined has no name in the catalogue (pt.user.custom.1 in 畜生界传说), and a "
            + "technique whose appearance is missing from the appearance collection is reported with the appearance id "
            + "alone."),
    ];

    /// <summary>
    /// What the layout report can and cannot say. The page box and the frames are stored; which bar lands on which
    /// page is not, because casting off is a layout computation, and the file's own cache of it is empty.
    /// </summary>
    private static readonly JsonArray LayoutCaveats =
    [
        (JsonNode?)JsonValue.Create(
            "'pageWidth'/'pageHeight' and the margins are in points (1/72 inch), which is what the layout options "
            + "write; the values in one corpus file are 594 x 841 pt with 42 pt margins. The unit is not written "
            + "anywhere in the file: it was fixed by checking a layout against Dorico's own MusicXML export of the "
            + "same project, whose <page-layout> gives 2473.53 x 3497.06 tenths for the same page and 123.529 tenths "
            + "for the same margins — the same numbers to within the rounding of the export's tenths (the export's "
            + "scaling is 4.79778 mm per 40 tenths)."),
        (JsonNode?)JsonValue.Create(
            "which measure lands on which page is NOT in the file. Casting off ('castingOff') only says whether the "
            + "bars per system and the systems per frame are fixed; the arrangement itself is computed when the score "
            + "is laid out. The one place the file caches an answer is a context node beside a property "
            + "(layoutIDWithFrameChainID), and it is stale: 16240 of the 16245 of them in Bad Apple!! and 8653 of the "
            + "8664 in 無限城への誘い are -1, and the few that are not name layout 0, not the page a bar lands on."),
        (JsonNode?)JsonValue.Create(
            "the frames come from the score library's page templates (pagePairDefinitionSetEntityID per layout points "
            + "at one), one set for the first page and one for a normal page, left and right. A frame's constraints "
            + "are relative to the page and to the other frames when 'contentRelative' is true, which is why the gaps "
            + "they write are not a rectangle — the numbers are reported as the file writes them, and no independent "
            + "source in the corpus confirms the frame geometry itself."),
    ];

    /// <summary>
    /// What the lyric element tables hold. Lyrics are elements in a table, not events on notes, and no file in the
    /// corpus has a single one — so this reports presence and size, the field names of an element when there is one,
    /// and an attempt to read the element and the events that place it, anchored on the field names the application
    /// itself uses (see <see cref="LyricsCaveats"/>, which separates what is measured from what is inferred).
    /// </summary>
    private static JsonObject Lyrics(ProjectFileLyrics lyrics)
    {
        var shapes = new JsonArray();

        foreach (var shape in lyrics.Shapes.Take(20))
        {
            var names = new JsonArray();

            foreach (var name in shape)
            {
                JsonArr.AddText(names, name);
            }

            JsonArr.AddNode(shapes, names);
        }

        var elements = new JsonArray();

        foreach (var element in lyrics.Read.Take(20))
        {
            var fields = new JsonArray();

            foreach (var field in element.Fields.Take(32))
            {
                JsonArr.AddNode(fields, new JsonObject
                {
                    ["key"] = field.Key,
                    ["value"] = field.Value,
                });
            }

            var children = new JsonArray();

            foreach (var kind in element.ChildKinds.Take(16))
            {
                JsonArr.AddText(children, kind);
            }

            var properties = new JsonArray();

            foreach (var property in element.Properties.Take(24))
            {
                JsonArr.AddNode(properties, new JsonObject
                {
                    ["type"] = property.Key,
                    ["value"] = property.Value,

                    // The tags the application knows for a lyric. Flagged rather than interpreted further: which of
                    // them a real file writes, and in what, was never seen.
                    ["lyricTag"] = property.Key.StartsWith("kLyric", StringComparison.Ordinal),
                });
            }

            JsonArr.AddNode(elements, new JsonObject
            {
                ["id"] = element.Id,
                ["idKey"] = element.IdKey,
                ["text"] = element.Text,
                ["textKey"] = element.TextKey,
                ["syllableType"] = element.SyllableType,
                ["lineNumber"] = element.LineNumber,
                ["lineLocation"] = element.LineLocation,
                ["lineType"] = element.LineType,
                ["fields"] = fields,
                ["children"] = children,
                ["properties"] = properties,
            });
        }

        var events = new JsonArray();

        foreach (var lyric in lyrics.Events.Take(100))
        {
            var fields = new JsonArray();

            foreach (var field in lyric.Fields.Take(16))
            {
                JsonArr.AddNode(fields, new JsonObject
                {
                    ["key"] = field.Key,
                    ["value"] = field.Value,
                });
            }

            JsonArr.AddNode(events, new JsonObject
            {
                ["eventId"] = lyric.EventId,
                ["measure"] = lyric.Measure,
                ["beat"] = lyric.Beat,
                ["position"] = lyric.Position,
                ["duration"] = lyric.Duration,
                ["element"] = lyric.Element < 0 ? null : lyric.Element,
                ["player"] = lyric.Player,
                ["instrument"] = lyric.Instrument,
                ["stave"] = lyric.Stave,
                ["voice"] = lyric.Voice,
                ["stream"] = lyric.Stream,
                ["fields"] = fields,
            });
        }

        return new JsonObject
        {
            ["tables"] = lyrics.Tables,
            ["elements"] = lyrics.Elements,
            ["eventTotal"] = lyrics.EventTotal,
            ["returnedElements"] = elements.Count,
            ["returnedEvents"] = events.Count,
            ["modelled"] = true,
            ["caveats"] = LyricsCaveats.DeepClone(),
            ["elementFields"] = shapes,
            ["read"] = elements,
            ["events"] = events,
        };
    }

    /// <summary>
    /// The escape hatch: the decoded record tree of <c>score.dtn</c>, generically. <c>--tree *</c> is a census of every
    /// name the file uses; any other value is every node with that name, with a bounded view of each one.
    /// </summary>
    private static JsonObject Tree(byte[] data, string query, Options options)
    {
        if (query.Trim() == "*")
        {
            var census = ProjectFileTreeReader.Census(data, options.TreeLimit ?? DefaultCensusLimit);

            var names = new JsonArray();

            foreach (var name in census.Names)
            {
                JsonArr.AddNode(names, new JsonObject
                {
                    ["name"] = name.Name,
                    ["kind"] = name.Kind,
                    ["count"] = name.Count,
                });
            }

            return new JsonObject
            {
                ["kind"] = "census",
                ["query"] = query,
                ["limit"] = options.TreeLimit ?? DefaultCensusLimit,
                ["returned"] = census.Names.Count,
                ["truncated"] = census.Truncated,
                ["nodes"] = census.Nodes,
                ["leaves"] = census.Leaves,
                ["nodeNames"] = census.NodeNames,
                ["leafNames"] = census.LeafNames,
                ["names"] = names,
            };
        }

        var depth = options.Depth ?? DefaultDepth;
        var limit = options.TreeLimit ?? DefaultOccurrenceLimit;
        var subtree = ProjectFileTreeReader.Subtree(data, query, depth, limit);

        var occurrences = new JsonArray();

        foreach (var node in subtree.Occurrences)
        {
            JsonArr.AddNode(occurrences, TreeNode(node));
        }

        var similar = new JsonArray();

        foreach (var name in subtree.Similar)
        {
            JsonArr.AddNode(similar, new JsonObject
            {
                ["name"] = name.Name,
                ["kind"] = name.Kind,
                ["count"] = name.Count,
            });
        }

        return new JsonObject
        {
            ["kind"] = "subtree",
            ["query"] = query,
            ["matched"] = subtree.Matched,
            ["returned"] = subtree.Returned,
            ["truncated"] = subtree.Truncated,
            ["depth"] = depth,
            ["limit"] = limit,
            ["valueChars"] = ProjectFileTreeReader.ValueChars,
            // What the walk looked at, not what the file holds: a matched node whose depth or size cap is reached is
            // skipped whole, so these are the counts the caps allow. `--tree *` counts the whole tree.
            ["nodesWalked"] = subtree.Nodes,
            ["leavesWalked"] = subtree.Leaves,
            ["occurrences"] = occurrences,
            ["similar"] = similar,
        };
    }

    private static JsonObject TreeNode(ProjectFileTreeNode node)
    {
        var values = new JsonArray();

        foreach (var field in node.Fields)
        {
            JsonArr.AddNode(values, new JsonObject
            {
                ["key"] = field.Key,
                ["value"] = field.Value,
            });
        }

        var children = new JsonArray();

        foreach (var child in node.Children)
        {
            JsonArr.AddNode(children, TreeNode(child));
        }

        return new JsonObject
        {
            ["name"] = node.Name,
            ["path"] = node.Path,
            ["fieldCount"] = node.FieldCount,
            ["childNodes"] = node.ChildNodes,
            ["truncated"] = node.Truncated,
            ["values"] = values,
            ["children"] = children,
        };
    }

    /// <summary>
    /// The dynamics' own halves. The extraction is read from the file, but the <c>mark</c> field next to each one is
    /// this reader's reading of it, so it carries the evidence and the limits of that reading.
    /// </summary>
    private static readonly JsonArray DynamicCaveats =
    [
        (JsonNode?)JsonValue.Create(
            "mark: derived from the file's numbers, not stored as text. An immediate change carries its intensity as a "
            + "variant list — family, an empty slot, mezzo, level — so `{int: 0}, {}, {bool: false}, {int: 2}` reads as "
            + "'ff'. Checked against Dorico's own MusicXML export of 無限城への誘い, the same project saved beside it: "
            + "all 325 markings agree with it on text, absolute position, measure and beat (f/ff/fff, p/pp, mf/mp, 12 "
            + "hairpins against 12 <wedge> elements, and 4 fz). In 秘封拉格1 — whose export came from Sibelius, a "
            + "different engraving of the same music — the counts per marking agree exactly even though the positions "
            + "do not. 'intensity' and 'intensityLevel' are the file's own values: read those when the exact marking "
            + "matters."),
        (JsonNode?)JsonValue.Create(
            "mark is null for a shape no checked export showed: a level above 3 (pppp, ppppp — the rule says so, "
            + "but no export in this corpus has one), a family other than f/p, a force whose intensityLevel is not 1 "
            + "(4 of them, all level 2, across the 20-file corpus, and no export says whether that is sfz, rf or "
            + "something else), and every CombinedDynamicDefinition (348 of them across the corpus, written as "
            + "startIntensity/endIntensity). 'unnamedMarks' counts those, and the raw fields are reported either way. "
            + "An intensity of `kALot` on a gradual change (残无的命运) is likewise reported raw while the hairpin is "
            + "named from its direction."),
        (JsonNode?)JsonValue.Create(
            "position is the mark's offset inside its dynamic group, exactly as the file writes it; 'at', 'measure' and "
            + "'beat' are that offset plus the event's position, i.e. where the mark sits in the score. A group holds "
            + "several marks over several positions, which is why the event's own measure and beat can differ from its "
            + "marks'. --measure selects by where the group starts (the event), so a hairpin that runs out of the range "
            + "is still listed, at its own resolved position."),
        (JsonNode?)JsonValue.Create(
            "the events carry no duration (0 for all 754 in Bad Apple!!): a hairpin's length is the gradual change's "
            + "'duration', and the event's 'position' is where the group starts."),
    ];

    /// <summary>What <c>--lyrics</c> can and cannot say. Three kinds of statement, kept apart: what the file
    /// measured, what the application's own vocabulary says (and was therefore inferred), and what stays
    /// ambiguous.</summary>
    private static readonly JsonArray LyricsCaveats =
    [
        (JsonNode?)JsonValue.Create(
            "measured: the lyric element table (LyricElementTableDefinition) is there or not, how many elements it "
            + "holds, every leaf of an element with its key and its raw value, the kinds of its child nodes, the "
            + "property table of a lyric event, and - for a lyric event - its exact position in crotchets, the "
            + "measure/beat that position falls on, and the player, instrument, stave and voice of its stream. The "
            + "report caps itself: up to 20 elements (returnedElements), 32 leaves and 16 child kinds each, and 100 "
            + "lyric events (returnedEvents); `events`/`eventTotal` and `elements` are the full counts."),
        (JsonNode?)JsonValue.Create(
            "inferred from the application's own vocabulary, not from data: no file in this corpus has a lyric, so "
            + "Dorico6.exe is the only source for the shape. Names taken from it: LyricElementTableDefinition (the "
            + "table), LyricElementDefinition (one lyric), LyricEventDefinition (a lyric placed in the music; not in "
            + "the published command catalogue either), lyricElementID (the element the event points at - taken by "
            + "analogy with slurElementID and dynamicGroupID), elementID (the element's own id, used when "
            + "lyricElementID is absent), lyricLineNumberAndStaveIDAndLocation plus LyricLineAddressDefinition, "
            + "LyricLineNumberOffsetDefinition and LyricLineNumberOffsetMap (a lyric's line), lyricLineUserYOffsets "
            + "(the line's engraving offset, which IS seen in every corpus file), and the property tags kLyric* "
            + "(kLyricLineNumber, kLyricLineLocation, kLyricSyllableType, kLyricIsChorus, kLyricIsTranslation, "
            + "kLyricIsItalic, kLyricShowVerseNumber, kLyricParagraphStyle, kLyricLineStartDx/EndDx)."),
        (JsonNode?)JsonValue.Create(
            "inferred, and the weakest claim here: which leaf of a lyric element holds the sung text. The names "
            + "searched are richText, text, lyricText and syllabicText (in that order), and the key that matched is "
            + "reported as textKey - null means none matched, and then `fields` is the whole truth. The application "
            + "has no kLyricText property tag, so the text is stored somewhere this reader has not seen; there is no "
            + "file to check the guess against."),
        (JsonNode?)JsonValue.Create(
            "ambiguous, stated rather than decided: whether lyricElementID sits on the lyric event (the reading here, "
            + "by analogy with SlurEventDefinition and DynamicGroupEventDefinition) or on the element. Both are tried: "
            + "an event is linked by lyricElementID, an element identifies itself by lyricElementID or elementID, and "
            + "idKey says which one was found. Also ambiguous: what kLyricSyllableType and kLyricLineLocation hold "
            + "(the enum types LyricLineType and LyricLineLocation exist in the binary, and LyricsEntryAdvanceType "
            + "with them, but no enum member name for a syllable type was recovered), and whether File.* lyric fields "
            + "are per line, per verse or per translation - kLyricIsChorus and kLyricIsTranslation suggest both exist."),
        (JsonNode?)JsonValue.Create(
            "so: this is a reading of a shape that was never seen. Every value reported comes from the file verbatim; "
            + "only the names and the link between element and event are the application's word. `--tree "
            + "LyricElementDefinition --depth 4` shows the same element without any interpretation at all."),
    ];

    /// <summary>
    /// Parses <c>--limit</c>: a count, or <c>all</c>/<c>-1</c> for every note. Zero means "no notes, just the shape".
    /// </summary>
    public static int ParseLimit(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            return -1;
        }

        if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) || value < -1)
        {
            throw new CliException("InvalidArgument", $"--limit must be a count, 'all' or -1, not '{text}'.", 1);
        }

        return value;
    }

    /// <summary>Parses <c>--depth</c>: how many levels of child nodes a tree dump shows, 0 to 8.</summary>
    public static int? ParseDepth(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        if (!int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            || value < 0
            || value > 8)
        {
            throw new CliException(
                "InvalidArgument",
                $"--depth must be a number of levels between 0 and 8, not '{text}'.",
                1);
        }

        return value;
    }

    /// <summary>
    /// Parses <c>--tree-limit</c> and <c>--dynamics-limit</c>: a count, or <c>all</c>/<c>-1</c> for no cap at all.
    /// An empty value keeps the default, which is not the same number in every mode.
    /// </summary>
    public static int? ParseOptionalLimit(string? text, string option)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var trimmed = text.Trim();

        if (trimmed.Equals("all", StringComparison.OrdinalIgnoreCase) || trimmed == "-1")
        {
            return -1;
        }

        if (!int.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) || value < 0)
        {
            throw new CliException(
                "InvalidArgument",
                $"{option} must be a count, 'all' or -1, not '{text}'.",
                1);
        }

        return value;
    }

    /// <summary>Parses <c>--measure</c>: one measure (<c>12</c>) or a range (<c>12-16</c>).</summary>
    public static (int First, int Last)? ParseMeasures(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var trimmed = text.Trim();
        var dash = trimmed.IndexOf('-', 1);

        if (dash < 0)
        {
            return int.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out var single)
                ? (single, single)
                : throw new CliException(
                    "InvalidArgument",
                    $"--measure must be a measure or a range like 12-16, not '{text}'.",
                    1);
        }

        if (!int.TryParse(trimmed[..dash], NumberStyles.Integer, CultureInfo.InvariantCulture, out var first)
            || !int.TryParse(trimmed[(dash + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var last))
        {
            throw new CliException(
                "InvalidArgument",
                $"--measure must be a measure or a range like 12-16, not '{text}'.",
                1);
        }

        return (first, last);
    }
}
