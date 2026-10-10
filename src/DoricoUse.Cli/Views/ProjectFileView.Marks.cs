using System.Globalization;
using System.Text.Json.Nodes;
using DoricoUse.Cli.Dorico;
using DoricoUse.Cli.Json;

namespace DoricoUse.Cli.Views;

internal static partial class ProjectFileView
{
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
                ["transpositionType"] = layout.TranspositionType,
                ["condensing"] = layout.Condensing,
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

}