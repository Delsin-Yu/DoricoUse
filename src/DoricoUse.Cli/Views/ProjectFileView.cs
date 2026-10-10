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
    private static readonly JsonArray Caveats =
    [
        (JsonNode?)JsonValue.Create(
            "pitch: the project stores a MIDI note number, not a spelling; the name here is derived from it and uses "
            + "sharps only. Read 'midi' when the exact pitch matters."),
        (JsonNode?)JsonValue.Create(
            "duration: an exact rational in crotchets (a 16th is 1/4, a 4/4 bar is 4). It is the sounding duration of "
            + "the tie chain, so a note tied across a bar line is one event."),
        (JsonNode?)JsonValue.Create(
            "measure/beat: bar lines are not stored. Measures are replayed from the bar-division events in 'meters' "
            + "and every note's 'position', so they are derived, and rests are gaps rather than events."),
        (JsonNode?)JsonValue.Create(
            "stave: notes are attributed to the player and instrument their stream belongs to; the sounding pitch is "
            + "the note event's, and a part layout applies its own transposition when it is printed."),
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

        /// <summary>Include what the lyric element tables hold.</summary>
        public bool Lyrics { get; init; }

        /// <summary>A node name to dump the decoded tree for, or <c>*</c> for a census of every name in the file.</summary>
        public string? Tree { get; init; }

        /// <summary>How many levels of child nodes a tree dump shows. Null is the default.</summary>
        public int? Depth { get; init; }

        /// <summary>How many occurrences, fields, children or names a tree dump lists. Null is the default, -1 everything.</summary>
        public int? TreeLimit { get; init; }
    }

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
            },
            ["caveats"] = Caveats,
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
            ["caveats"] = DynamicCaveats,
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

    /// <summary>
    /// What the lyric element tables hold. Lyrics are elements in a table, not events on notes, and no file in the
    /// corpus has a single one — so this reports presence and size, and the field names of an element when there is
    /// one, rather than pretending to read syllables.
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

        return new JsonObject
        {
            ["tables"] = lyrics.Tables,
            ["elements"] = lyrics.Elements,
            ["modelled"] = false,
            ["caveats"] = LyricsCaveats,
            ["elementFields"] = shapes,
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

    /// <summary>What <c>--lyrics</c> can and cannot say.</summary>
    private static readonly JsonArray LyricsCaveats =
    [
        (JsonNode?)JsonValue.Create(
            "lyrics are an element table (LyricElementTableDefinition), one per flow, not events on notes. This reader "
            + "reports whether the table is there and how many elements it holds; a table with nothing in it is "
            + "written as a leaf, which is what every file in the corpus has."),
        (JsonNode?)JsonValue.Create(
            "the contents of a lyric element are not read: no file in this corpus has a single one, so the shape of a "
            + "lyric was never seen. `--tree LyricElementTableDefinition --depth 3` shows what a file with lyrics "
            + "actually holds."),
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
