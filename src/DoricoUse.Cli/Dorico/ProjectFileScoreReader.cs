using System.Globalization;
using System.Text;

namespace DoricoUse.Cli.Dorico;

/// <summary>
/// Decodes <c>score.dtn</c> — the member of a <c>.dorico</c> package that holds the notes.
/// </summary>
/// <remarks>
/// <para>
/// <b>The member is a name table, a value pool and a token stream.</b>
/// </para>
/// <list type="number">
/// <item><description>
/// A 12-byte header: <c>u32 4</c> (format version), <c>u32 1</c>, <c>u32 nameCount-1</c> (3335 in the corpus file).
/// </description></item>
/// <item><description>
/// A <b>name table</b> from offset 12: null-terminated UTF-8 identifiers, ended by an empty string — every class
/// name, field name, option name and enum name the project uses. 3336 entries in the corpus file. The indices into
/// this table are what the token stream means by "key", so this reader looks every field up by <i>name</i> and never
/// hard-codes an index: the table is built in first-use order and differs from file to file.
/// </description></item>
/// <item><description>
/// A <b>value pool</b>: null-terminated UTF-8 <i>values</i>, written as text — <c>1.1452</c>, <c>kScoreInC</c>,
/// <c>总谱</c>, <c>1/4</c>, <c>74</c>, <c>steamVariantList:list: {int: 1}, …</c>. Values are deduplicated against
/// this pool, so a value in the token stream is a <b>1-based index</b> into it (<c>0</c> = absent). 28745 entries in
/// the corpus file.
/// </description></item>
/// <item><description>
/// A <b>token stream</b> from the end of the pool to the last byte of the member: 580,826 records in the corpus
/// file, each starting with one byte and using LEB128 varints throughout.
/// A record's first byte carries the kind in bit 1: <c>0x90</c>/<c>0x91</c> in the corpus file are leaves —
/// <c>key value</c>, where <c>key</c> is a name-table index and <c>value</c> a 1-based value-pool index — and
/// <c>0x92</c>/<c>0x93</c> are nodes: <c>key aux count ids[count]</c>, then that many children, each either a leaf
/// or another node. Bit 0 is a variant, and the rest of the byte is a marker constant for the file (0x90 in the
/// corpus file, 0x10 in <i>《小城日常》</i>, 0x08 in <i>畜生界传说</i>). The child count is what makes the tree
/// unambiguous: from the root, the stream consumes the member exactly to its last byte.
/// </description></item>
/// </list>
/// <para>
/// The search for the stream — header, name table, value pool, and which byte the token stream starts at — is shared
/// with <see cref="ProjectFileTreeReader"/>, which walks the very same records without knowing anything about music;
/// it lives in <see cref="ProjectFileBinary"/>.
/// </para>
/// <para>
/// <b>What a note looks like.</b> Notes are children of a <c>NoteEventDefinition</c> node on the path
/// <c>flow → blocks → block → events</c>. The block's <c>blockInstance</c> names the stream it sounds in, and the
/// stream's <c>flowPlayer → flowPlayerInstrument → voiceStreamInfos / staveStreamInfos</c> chain names the player,
/// the instrument and the stave. A note carries <c>position</c> and <c>duration</c> as exact rationals in
/// <b>crotchets</b> (a 16th is <c>1/4</c>; a 4/4 bar is 4 of them) and <c>pitch</c> as a MIDI note number — the
/// <b>sounding</b> pitch: a horn in F that reads a written B4 stores the E4 it sounds.
/// </para>
/// <para>
/// <b>The letter a note is spelled with is a property of the event</b>: <c>propertyTable → entries → entry</c> writes
/// <c>propertyType: kNoteSpelling, value: string: "B"</c>. That letter is the only notation the file stores about
/// the pitch — there is no accidental beside it — and it is what lets a Bb be told from an A# at the same MIDI
/// number. It is reported as <c>ProjectFileNote.Spelling</c>, exactly as the file writes it.
/// </para>
/// <para>
/// <b>Bars are not stored.</b> A project stores bar divisions — time-signature events with positions — not bar
/// lines, so a measure number is derived here by replaying the metre, and is reported as derived.
/// </para>
/// <para>
/// Everything is validated rather than assumed. A candidate start for the token stream is accepted only if the parse
/// ends exactly on the last byte, the root is a <c>kScore</c>, the root states the fields a project always states,
/// and every value index really points into the pool. A file this reader cannot decode reports a code, never
/// invented notes.
/// </para>
/// </remarks>
internal static class ProjectFileScoreReader
{
    private const int HeaderBytes = ProjectFileBinary.HeaderBytes;

    private const string RootKind = "kScore";

    private static readonly string[] ExpectedRootChildren =
    [
        "fileVersion", "layoutSortOrder", "idGenerators", "info", "scoreOptions", "library",
        "scorePlayers", "flows", "layouts", "playModeData",
    ];

    /// <summary>
    /// Reads the score out of a <c>score.dtn</c> member. Throws a <see cref="CliException"/> with the stable code
    /// <c>UnsupportedScoreData</c> when the bytes are not a score this reader understands.
    /// </summary>
    public static ProjectFileScore Read(byte[] data)
    {
        if (data.Length < HeaderBytes + 4)
        {
            throw Unsupported("the member is shorter than its header");
        }

        var formatVersion = ProjectFileBinary.FormatVersion(data);
        var names = ProjectFileBinary.ReadNames(data, HeaderBytes, out var afterNames);

        if (names.Count == 0)
        {
            throw Unsupported("the name table is empty");
        }

        var poolStart = ProjectFileBinary.SkipNulls(data, afterNames);

        var tried = 0;

        foreach (var candidate in ProjectFileBinary.Candidates(data, poolStart))
        {
            // A real score begins at the first position that parses to the end; trying a bounded number of
            // candidates keeps a file that is not a score from costing a quadratic scan.
            if (++tried > ProjectFileBinary.MaxCandidates)
            {
                break;
            }

            var poolCount = ProjectFileBinary.CountTokens(data, poolStart, candidate);
            var decoder = new Decoder(data, names, poolStart, poolCount);

            if (decoder.TryRead(candidate))
            {
                return decoder.Build(formatVersion);
            }
        }

        throw Unsupported(
            $"{names.Count} names and {ProjectFileBinary.CountTokens(data, poolStart, data.Length)} value-pool entries were read, but "
            + "no position yields a token stream that parses to the last byte with a kScore root");
    }

    private static CliException Unsupported(string why)
        => new(
            "UnsupportedScoreData",
            $"score.dtn is not a Dorico score this reader understands: {why}.",
            2);

    /// <summary>
    /// The payload of one of the file's variant-encoded values: <c>string: "pt.accent"</c> carries
    /// <c>pt.accent</c>, <c>int: 52</c> carries <c>52</c>, and <c>null: </c> carries nothing. A list keeps its
    /// braces, because its items are what matters.
    /// </summary>
    internal static string? VariantText(string? text)
    {
        if (text is null)
        {
            return null;
        }

        var trimmed = text.Trim();
        var colon = trimmed.IndexOf(':');

        if (colon < 0)
        {
            return null;
        }

        var payload = trimmed[(colon + 1)..].Trim();

        if (payload.Length == 0)
        {
            return null;
        }

        return payload.Length >= 2 && payload[0] == '"' && payload[^1] == '"'
            ? payload[1..^1]
            : payload;
    }

    /// <summary>
    /// Which family a note property belongs to. The file writes the property with a prefix that differs by kind
    /// (<c>kNote</c>, <c>kEvent</c>, <c>kArticulation</c>), so the name is matched by its tail and a version that
    /// renames the prefix still lands here.
    /// </summary>
    internal static string MarkFamily(string property)
        => property switch
        {
            _ when Ends(property, "ArticulationForceTieChainPosition") => "tie",
            _ when Ends(property, "ArticulationDuration") || Ends(property, "ArticulationForce") => "articulation",
            _ when Ends(property, "EndHasLVTie")
                || Ends(property, "TiedToEvent")
                || Ends(property, "ForcedTieDirection")
                || Ends(property, "SplitDurations")
                || Ends(property, "ForcedSplitPositions") => "tie",
            _ when Ends(property, "BeamStart")
                || Ends(property, "BeamEnd")
                || Ends(property, "BeamNone")
                || Ends(property, "BeamPartialBeamDirection") => "beam",
            _ when Ends(property, "PlayingTechnique") => "technique",
            _ => "other",
        };

    private static bool Ends(string property, string tail)
        => property.Length > tail.Length && property.EndsWith(tail, StringComparison.Ordinal);

    /// <summary>
    /// The notation a note property stands for, when the file's own library names it. An articulation's value is an
    /// enum (<c>kStaccato</c>) and the catalogue spells the same thing <c>pt.staccato</c>, so the name is looked up
    /// rather than invented; a value the catalogue does not hold — a combination such as <c>kStaccatoTenuto</c>, 24
    /// of them in the corpus — stays null and is reported as the file writes it.
    /// </summary>
    internal static string? MarkName(string property, string? value, Dictionary<string, string?> catalogue)
    {
        var text = VariantText(value);

        if (text is null)
        {
            return null;
        }

        if (Ends(property, "PlayingTechnique"))
        {
            return catalogue.GetValueOrDefault(text);
        }

        if (!property.Contains("Articulation", StringComparison.Ordinal)
            || text.Length < 2
            || text[0] != 'k')
        {
            return null;
        }

        return catalogue.GetValueOrDefault("pt." + char.ToLowerInvariant(text[1]) + text[2..]);
    }

    // ---------------------------------------------------------------- the tables in front of the stream

    // ---------------------------------------------------------------- the decoder

    private sealed class Decoder
    {
        private readonly byte[] _data;
        private readonly IReadOnlyList<string> _names;
        private readonly int _poolStart;
        private readonly int _poolCount;

        private readonly List<string> _pool;
        private readonly InfoDraft _info = new();
        private readonly List<PlayerDraft> _players = [];
        private readonly List<LayoutDraft> _layouts = [];
        private readonly List<FlowDraft> _flows = [];
        private readonly List<NoteDraft> _notes = [];
        private readonly List<DynamicDraft> _dynamicEvents = [];
        private readonly List<MeterEventDraft> _barEvents = [];
        private readonly Dictionary<long, ElementDraft> _barElements = [];
        private readonly Dictionary<long, StaveDraft> _streamStaves = [];
        private readonly Dictionary<long, string> _streamKinds = [];
        private readonly List<SlurDraft> _slurs = [];
        private readonly List<LyricEventDraft> _lyricEvents = [];
        private readonly List<TechniqueEventDraft> _techniqueEvents = [];

        /// <summary>The score library's own names: the playing techniques and their appearances.</summary>
        private readonly List<TechniqueDefinitionDraft> _techniqueDefinitions = [];
        private readonly List<TechniqueAppearanceDraft> _techniqueAppearances = [];

        /// <summary>The library's page templates: which frames each page of a page pair holds.</summary>
        private readonly List<PageSetDraft> _pageSets = [];

        /// <summary>Block id → the stream its <c>blockInstance</c> places it on.</summary>
        private readonly Dictionary<long, long> _blockStreams = [];

        private int _pos;
        private int _depth;
        private byte _marker;
        private long _records;
        private long _maxValue;
        private int _rootChildrenSeen;
        private string? _fileVersion;

        public Decoder(byte[] data, IReadOnlyList<string> names, int poolStart, int poolCount)
        {
            _data = data;
            _names = names;
            _poolStart = poolStart;
            _poolCount = poolCount;
            _pool = [];
        }

        /// <summary>
        /// One attempt at the whole member: decode every record, and fail at the first inconsistency so that a wrong
        /// start position cannot pass for a score.
        /// </summary>
        public bool TryRead(int streamStart)
        {
            _pos = streamStart;

            if (_pos >= _data.Length)
            {
                return false;
            }

            _marker = (byte)(_data[_pos] & ProjectFileBinary.MarkerMask);

            if (!IsNodeTag(_data[_pos]))
            {
                return false;
            }

            if (!ReadHeader(out var rootKey, out var rootCount) || NameAt(rootKey) != RootKind)
            {
                return false;
            }

            PopulatePool(streamStart);

            for (long index = 0; index < rootCount; index++)
            {
                if (!WalkRootChild())
                {
                    return false;
                }
            }

            // The tree must end exactly on the last byte of the member, and the root must look like a project's.
            return _pos == _data.Length
                && _records > 0
                && _rootChildrenSeen >= 3
                && _flows.Count > 0
                && _maxValue <= _poolCount;
        }

        private void PopulatePool(int streamStart)
        {
            var position = _poolStart;

            while (position < streamStart && _pool.Count < 4_000_000)
            {
                var terminator = Array.IndexOf(_data, (byte)0, position, streamStart - position);

                if (terminator < 0)
                {
                    break;
                }

                _pool.Add(Encoding.UTF8.GetString(_data, position, terminator - position));
                position = terminator + 1;
            }
        }

        // ------------------------------------------------------------ bytes and values

        private string? NameAt(long index)
            => index >= 0 && index < _names.Count ? _names[(int)index] : null;

        /// <summary>
        /// The text of a value-pool entry. Also records the highest index used, which is what proves the pool's
        /// length: every value in a real score indexes an entry that exists.
        /// </summary>
        private string? Value(long value)
        {
            if (value > _maxValue)
            {
                _maxValue = value;
            }

            if (value is 0 or > int.MaxValue)
            {
                return null;
            }

            var index = (int)value - 1;

            return index >= 0 && index < _pool.Count ? _pool[index] : null;
        }

        /// <summary>A whole number written into the value pool, e.g. a player or stream id.</summary>
        private long Number(long value)
            => long.TryParse(Value(value), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : -1;

        private bool IsLeafTag(byte tag) => ProjectFileBinary.IsLeafTag(tag);

        private bool IsNodeTag(byte tag) => ProjectFileBinary.IsNodeTag(tag);

        /// <summary>A record's tag: one of the file's four tags.</summary>
        private bool IsRecordTag(byte tag) => (tag & ProjectFileBinary.MarkerMask) == _marker;

        private bool TryVarint(out long value)
        {
            value = 0;
            var shift = 0;

            while (_pos < _data.Length)
            {
                var current = _data[_pos++];

                if (shift > 62)
                {
                    return false;
                }

                value |= (long)(current & 0x7F) << shift;

                if ((current & 0x80) == 0)
                {
                    _records++;

                    return true;
                }

                shift += 7;
            }

            return false;
        }

        private bool ReadLeaf(out long key, out long value)
        {
            key = 0;
            value = 0;

            if (_pos >= _data.Length || !IsLeafTag(_data[_pos]) || !IsRecordTag(_data[_pos]))
            {
                return false;
            }

            _pos++;

            return TryVarint(out key) && TryVarint(out value);
        }

        /// <summary>Reads a node header, consuming the element-id list that follows <c>count</c>.</summary>
        private bool ReadHeader(out long key, out long count)
        {
            key = 0;
            count = 0;

            if (_pos >= _data.Length || !IsNodeTag(_data[_pos]) || !IsRecordTag(_data[_pos]))
            {
                return false;
            }

            _pos++;

            if (!TryVarint(out key) || !TryVarint(out _) || !TryVarint(out count))
            {
                return false;
            }

            if (count < 0 || count > _data.Length)
            {
                return false;
            }

            for (long index = 0; index < count; index++)
            {
                if (!TryVarint(out _))
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Iterates a node's children. Leaves go to <paramref name="onLeaf"/> with their value-pool index; nodes go to
        /// <paramref name="onNode"/> with their kind, child count and offset, and must consume the children themselves
        /// — with <see cref="WalkChildren"/>, or with <see cref="SkipChildren"/> when they are of no interest.
        /// </summary>
        private bool WalkChildren(long count, Func<long, long, bool>? onLeaf, Func<long, long, int, bool> onNode)
        {
            if (++_depth > ProjectFileBinary.MaxDepth)
            {
                _depth--;

                return false;
            }

            try
            {
                return WalkChildrenCore(count, onLeaf, onNode);
            }
            finally
            {
                _depth--;
            }
        }

        private bool WalkChildrenCore(long count, Func<long, long, bool>? onLeaf, Func<long, long, int, bool> onNode)
        {
            for (long index = 0; index < count; index++)
            {
                if (_pos >= _data.Length)
                {
                    return false;
                }

                if (IsLeafTag(_data[_pos]))
                {
                    if (!ReadLeaf(out var key, out var value))
                    {
                        return false;
                    }

                    // Every value passes through Value(), so a leaf nobody looks at is still range-checked.
                    Value(value);

                    if (onLeaf is not null && !onLeaf(key, value))
                    {
                        return false;
                    }
                }
                else
                {
                    var start = _pos;

                    if (!ReadHeader(out var key, out var children))
                    {
                        return false;
                    }

                    if (!onNode(key, children, start))
                    {
                        return false;
                    }
                }
            }

            return true;
        }

        private bool SkipChildren(long count) => WalkChildren(count, null, (_, children, _) => SkipChildren(children));

        /// <summary>
        /// Walks a collection: Dorico writes it as <c>wrapper → array → items</c>, so the objects sit one level down.
        /// The item handler receives each item's child count and must consume the item's children.
        /// </summary>
        private bool WalkItems(long count, Func<long, long, bool> onItem)
        {
            if (count == 1)
            {
                var save = _pos;

                if (!ReadHeader(out var key, out var inner))
                {
                    return false;
                }

                if (NameAt(key) == "array")
                {
                    return WalkChildren(inner, null, (itemKey, itemCount, _) => onItem(itemKey, itemCount));
                }

                _pos = save;
            }

            return WalkChildren(count, null, (itemKey, itemCount, _) => onItem(itemKey, itemCount));
        }

        // ------------------------------------------------------------ root

        private bool WalkRootChild()
        {
            if (_pos < _data.Length && IsLeafTag(_data[_pos]))
            {
                if (!ReadLeaf(out var key, out var value))
                {
                    return false;
                }

                var name = NameAt(key);
                CountRootChild(name);

                if (name == "fileVersion")
                {
                    _fileVersion = Value(value);
                }

                return true;
            }

            if (!ReadHeader(out var nodeKey, out var count))
            {
                return false;
            }

            var kind = NameAt(nodeKey);
            CountRootChild(kind);

            return kind switch
            {
                "info" => WalkInfo(count),
                "layouts" => WalkLayouts(count),
                "scorePlayers" => WalkPlayers(count),
                "flows" => WalkFlows(count),
                "library" => WalkLibrary(count),
                _ => SkipChildren(count),
            };
        }

        private void CountRootChild(string? name)
        {
            if (name is not null && Array.IndexOf(ExpectedRootChildren, name) >= 0)
            {
                _rootChildrenSeen++;
            }
        }

        private bool WalkInfo(long count)
            => WalkChildren(
                count,
                (key, value) =>
                {
                    switch (NameAt(key))
                    {
                        case "title": _info.Title = Value(value); break;
                        case "subtitle": _info.Subtitle = Value(value); break;
                        case "composer": _info.Composer = Value(value); break;
                        case "arranger": _info.Arranger = Value(value); break;
                        case "lyricist": _info.Lyricist = Value(value); break;
                        case "createdDate": _info.CreatedDate = Value(value); break;
                        case "lastSavedDate": _info.LastSavedDate = Value(value); break;
                        case "createdVersion": _info.CreatedVersion = Value(value); break;
                        case "lastSavedVersion": _info.LastSavedVersion = Value(value); break;
                        case "originator": _info.Originator = Value(value); break;
                    }

                    return true;
                },
                (_, children, _) => SkipChildren(children));

        // ------------------------------------------------------------ players, instruments, layouts

        private bool WalkLayouts(long count)
            => WalkItems(count, (_, children) =>
            {
                var layout = new LayoutDraft();

                var ok = WalkChildren(
                    children,
                    (key, value) =>
                    {
                        switch (NameAt(key))
                        {
                            case "layoutID": layout.Id = Number(value); break;
                            case "layoutType": layout.Type = Value(value); break;
                            case "layoutName": layout.Name = Value(value); break;
                            case "layoutNumber": layout.Number = Number(value); break;
                        }

                        return true;
                    },
                    (key, childCount, _) => NameAt(key) switch
                    {
                        "layoutOptions" => WalkLayoutOptions(childCount, layout),
                        "pageSequenceOverrides" => WalkPageSequenceOverrides(childCount, layout),
                        _ => SkipChildren(childCount),
                    });

                _layouts.Add(layout);

                return ok;
            });

        /// <summary>
        /// One layout's engraving options. Only the page box and the casting off are read: the rest is the engraving
        /// and notation option tree, which <c>options get</c> reaches through Dorico itself.
        /// </summary>
        private bool WalkLayoutOptions(long count, LayoutDraft layout)
            => WalkChildren(
                count,
                (key, value) =>
                {
                    switch (NameAt(key))
                    {
                        case "pagePairSetEntityID": layout.PageSet = Value(value); break;
                        case "defaultSpaceSize": layout.SpaceSize = Value(value); break;
                    }

                    return true;
                },
                (key, childCount, _) => NameAt(key) switch
                {
                    "pageSizeAndMarginOptions" => WalkPageSize(childCount, layout),
                    "castingOffOptions" => WalkCastingOff(childCount, layout),
                    _ => SkipChildren(childCount),
                });

        /// <summary>The page box of one layout: <c>pageWidth</c>/<c>pageHeight</c> are in points, as are the margins.</summary>
        private bool WalkPageSize(long count, LayoutDraft layout)
            => WalkChildren(
                count,
                (key, value) =>
                {
                    switch (NameAt(key))
                    {
                        case "pageWidth": layout.PageWidth = Value(value); break;
                        case "pageHeight": layout.PageHeight = Value(value); break;
                    }

                    return true;
                },
                (key, childCount, _) => NameAt(key) == "margins"
                    ? WalkMargins(childCount, layout.Margins)
                    : SkipChildren(childCount));

        private bool WalkMargins(long count, List<ProjectFileTreeField> into)
            => WalkChildren(
                count,
                (key, value) =>
                {
                    // `type` says whether the four margins are identical or mirrored; the numbers are the margins.
                    if (NameAt(key) is "outer" or "inner" or "top" or "bottom" or "left" or "right" or "type")
                    {
                        Keep(into, NameAt(key), Value(value));
                    }

                    return true;
                },
                (key, childCount, _) => NameAt(key) == "margins"
                    ? WalkMargins(childCount, into)
                    : SkipChildren(childCount));

        /// <summary>
        /// The casting off: how the bars are made into systems and the systems into frames. A whole node of options,
        /// kept as the file writes them.
        /// </summary>
        private bool WalkCastingOff(long count, LayoutDraft layout)
            => WalkChildren(
                count,
                (key, value) =>
                {
                    if (layout.CastingOff.Count < 12)
                    {
                        Keep(layout.CastingOff, NameAt(key), Value(value));
                    }

                    return true;
                },
                (_, childCount, _) => SkipChildren(childCount));

        /// <summary>
        /// A layout's page-sequence overrides: the frames it moves or adds. The chain definitions themselves are
        /// counted, not read: the geometry of a frame lives in the page template it belongs to.
        /// </summary>
        private bool WalkPageSequenceOverrides(long count, LayoutDraft layout)
            => WalkChildren(
                count,
                null,
                (key, childCount, _) => NameAt(key) switch
                {
                    "frameChainDefinitions" => WalkFrameChainDefinitions(childCount, layout),
                    _ => SkipChildren(childCount),
                });

        private bool WalkFrameChainDefinitions(long count, LayoutDraft layout)
            => WalkChildren(
                count,
                null,
                (key, childCount, _) => NameAt(key) switch
                {
                    "musicFrameChains" => WalkChainList(childCount, layout, music: true),
                    "textFrameChains" => WalkChainList(childCount, layout, music: false),
                    _ => SkipChildren(childCount),
                });

        private bool WalkChainList(long count, LayoutDraft layout, bool music)
            => WalkChildren(
                count,
                null,
                (key, childCount, _) =>
                {
                    if (NameAt(key) != "musicFrameChains" && NameAt(key) != "textFrameChains")
                    {
                        return SkipChildren(childCount);
                    }

                    if (music)
                    {
                        layout.FrameChainOverrides++;
                    }

                    return SkipChildren(childCount);
                });

        /// <summary>The score library: its playing techniques, their appearances, and the page templates.</summary>
        private bool WalkLibrary(long count)
            => WalkChildren(
                count,
                null,
                (key, childCount, _) => NameAt(key) switch
                {
                    "playingTechniques" => WalkCollection(childCount, "PlayingTechniqueDefinition", WalkTechniqueDefinition),
                    "playingTechniqueAppearanceCollectionDefinition" => WalkCollection(
                        childCount,
                        "PlayingTechniqueAppearanceDefinition",
                        WalkTechniqueAppearance),
                    "pagePairDefinitionSets" => WalkCollection(
                        childCount,
                        "PagePairDefinitionSetDefinition",
                        WalkPageSet),
                    _ => SkipChildren(childCount),
                });

        /// <summary>Walks <c>a → entities → objects</c> and calls the handler for each object of one kind.</summary>
        private bool WalkCollection(long count, string kind, Func<long, bool> onItem)
            => WalkChildren(
                count,
                null,
                (key, childCount, _) => NameAt(key) == "entities"
                    ? WalkChildren(
                        childCount,
                        null,
                        (itemKey, itemCount, _) => NameAt(itemKey) == kind
                            ? onItem(itemCount)
                            : SkipChildren(itemCount))
                    : SkipChildren(childCount));

        private bool WalkTechniqueDefinition(long count)
        {
            var definition = new TechniqueDefinitionDraft();

            var ok = WalkChildren(
                count,
                (key, value) =>
                {
                    switch (NameAt(key))
                    {
                        case "entityID": definition.EntityId = Value(value); break;
                        case "name": definition.Name = Value(value); break;
                        case "articulationType": definition.ArticulationType = Value(value); break;
                        case "groupType": definition.GroupType = Value(value); break;
                        case "aliasForPlayingTechniqueID": definition.AliasFor = Value(value); break;
                    }

                    return true;
                },
                (_, childCount, _) => SkipChildren(childCount));

            if (ok && definition.EntityId is not null)
            {
                _techniqueDefinitions.Add(definition);
            }

            return ok;
        }

        private bool WalkTechniqueAppearance(long count)
        {
            var appearance = new TechniqueAppearanceDraft();

            var ok = WalkChildren(
                count,
                (key, value) =>
                {
                    switch (NameAt(key))
                    {
                        case "entityID": appearance.EntityId = Value(value); break;
                        case "name": appearance.Name = Value(value); break;
                        case "text": appearance.Text = Value(value); break;
                        case "playingTechniqueEntityID": appearance.TechniqueId = Value(value); break;
                        case "playingTechniqueAppearanceType": appearance.Type = Value(value); break;
                        case "playingTechniqueAppearanceCategory": appearance.Category = Value(value); break;
                        case "language": appearance.Language = Value(value); break;
                    }

                    return true;
                },
                (_, childCount, _) => SkipChildren(childCount));

            if (ok && appearance.EntityId is not null)
            {
                _techniqueAppearances.Add(appearance);
            }

            return ok;
        }

        /// <summary>
        /// One page template: the frames of the first page and of a normal page, left and right, and the frame chains
        /// (a music chain carries the music, a text chain carries a heading or a page number).
        /// </summary>
        private bool WalkPageSet(long count)
        {
            var pageSet = new PageSetDraft();

            var ok = WalkChildren(
                count,
                (key, value) =>
                {
                    switch (NameAt(key))
                    {
                        case "entityID": pageSet.EntityId = Value(value); break;
                        case "name": pageSet.Name = Value(value); break;
                        case "type": pageSet.Type = Value(value); break;
                    }

                    return true;
                },
                (key, childCount, _) => NameAt(key) switch
                {
                    "frameChainDefinitions" => WalkPageSetChains(childCount, pageSet),
                    "firstPageDefinition" => WalkPageDefinition(childCount, pageSet, "first"),
                    "normalPageDefinition" => WalkPageDefinition(childCount, pageSet, "normal"),
                    _ => SkipChildren(childCount),
                });

            if (ok)
            {
                _pageSets.Add(pageSet);
            }

            return ok;
        }

        private bool WalkPageSetChains(long count, PageSetDraft pageSet)
            => WalkChildren(
                count,
                null,
                (key, childCount, _) => NameAt(key) switch
                {
                    "musicFrameChains" => WalkChainItems(childCount, pageSet.MusicChains),
                    "textFrameChains" => WalkChainItems(childCount, pageSet.TextChains),
                    _ => SkipChildren(childCount),
                });

        /// <summary>
        /// A chain list is written twice over: an outer node named after the list, then one entry per chain. The outer
        /// node and the entries share their name, so a name test alone would land on the wrong level.
        /// </summary>
        private bool WalkChainItems(long count, List<long> into)
            => WalkChildren(
                count,
                null,
                (key, childCount, _) => NameAt(key) is "musicFrameChains" or "textFrameChains"
                    ? WalkChildren(
                        childCount,
                        (leafKey, leafValue) =>
                        {
                            if (NameAt(leafKey) == "frameChainDefinitionID")
                            {
                                into.Add(Number(leafValue));
                            }

                            return true;
                        },
                        (nodeKey, nodeCount, _) => NameAt(nodeKey) is "musicFrameChains" or "textFrameChains"
                            ? WalkChainItems(nodeCount, into)
                            : SkipChildren(nodeCount))
                    : SkipChildren(childCount));

        private bool WalkPageDefinition(long count, PageSetDraft pageSet, string kind)
            => WalkChildren(
                count,
                null,
                (key, childCount, _) => NameAt(key) switch
                {
                    "left" => WalkPageSide(childCount, pageSet, kind, "left"),
                    "right" => WalkPageSide(childCount, pageSet, kind, "right"),
                    _ => SkipChildren(childCount),
                });

        private bool WalkPageSide(long count, PageSetDraft pageSet, string kind, string side)
            => WalkChildren(
                count,
                null,
                (key, childCount, _) => NameAt(key) == "frames"
                    ? WalkPageFrames(childCount, pageSet, kind, side)
                    : SkipChildren(childCount));

        private bool WalkPageFrames(long count, PageSetDraft pageSet, string kind, string side)
            => WalkChildren(
                count,
                null,
                (key, childCount, _) => NameAt(key) == "frame"
                    ? WalkFrame(childCount, pageSet, kind, side)
                    : SkipChildren(childCount));

        private bool WalkFrame(long count, PageSetDraft pageSet, string kind, string side)
        {
            var frame = new FrameDraft { Kind = kind, Side = side };

            var ok = WalkChildren(
                count,
                (key, value) =>
                {
                    switch (NameAt(key))
                    {
                        case "indexInFrameChainOnPage": frame.Index = (int)Number(value); break;
                        case "contentRelative": frame.ContentRelative = Value(value) == "true"; break;
                    }

                    return true;
                },
                (key, childCount, _) => NameAt(key) switch
                {
                    "frameChainDefinitionID" => WalkFrameChain(childCount, frame),
                    "constraints" => WalkFrameConstraints(childCount, frame),
                    _ => SkipChildren(childCount),
                });

            if (ok)
            {
                pageSet.Frames.Add(frame);
            }

            return ok;
        }

        private bool WalkFrameChain(long count, FrameDraft frame)
            => WalkChildren(
                count,
                (key, value) =>
                {
                    switch (NameAt(key))
                    {
                        case "frameChainDefinitionID": frame.Chain = Number(value); break;
                        case "isLayoutSpecific": frame.LayoutSpecific = Value(value) == "true"; break;
                    }

                    return true;
                },
                (_, childCount, _) => SkipChildren(childCount));

        private bool WalkFrameConstraints(long count, FrameDraft frame)
            => WalkChildren(
                count,
                null,
                (key, childCount, _) => NameAt(key) switch
                {
                    "positionConstraints" => WalkConstraints(childCount, frame.Position),
                    "dimensionConstraints" => WalkConstraints(childCount, frame.Size),
                    _ => SkipChildren(childCount),
                });

        private bool WalkConstraints(long count, List<ProjectFileTreeField> into)
            => WalkChildren(
                count,
                (key, value) =>
                {
                    if (NameAt(key) is "left" or "right" or "top" or "bottom" or "width" or "height")
                    {
                        Keep(into, NameAt(key), Value(value));
                    }

                    return true;
                },
                (_, childCount, _) => SkipChildren(childCount));

        private static void Keep(List<ProjectFileTreeField> into, string? key, string? value)
        {
            if (into.Count < 16)
            {
                into.Add(new ProjectFileTreeField(key ?? "?", value));
            }
        }

        private bool WalkPlayers(long count)
            => WalkItems(count, (_, children) =>
            {
                var player = new PlayerDraft();

                var ok = WalkChildren(
                    children,
                    (key, value) =>
                    {
                        switch (NameAt(key))
                        {
                            case "playerID": player.Id = Number(value); break;
                            case "displayName": player.Name = Value(value); break;
                            case "baseName": player.Name ??= Value(value); break;
                            case "displayShortName": player.ShortName = Value(value); break;
                            case "baseShortName": player.ShortName ??= Value(value); break;
                        }

                        return true;
                    },
                    (key, childCount, _) => NameAt(key) == "instruments"
                        ? WalkInstruments(childCount, player)
                        : SkipChildren(childCount));

                _players.Add(player);

                return ok;
            });

        private bool WalkInstruments(long count, PlayerDraft player)
            => WalkItems(count, (_, children) =>
            {
                var instrument = new InstrumentDraft();

                var ok = WalkChildren(
                    children,
                    (key, value) =>
                    {
                        switch (NameAt(key))
                        {
                            case "instrumentID": instrument.Id = Number(value); break;
                            case "entityID": instrument.EntityId = Value(value); break;
                            case "numStaves": instrument.Staves = (int)Number(value); break;
                            case "standardRange": instrument.Range = Value(value); break;
                        }

                        return true;
                    },
                    (key, childCount, _) => NameAt(key) switch
                    {
                        "nameData" => WalkNameData(childCount, instrument),
                        "transposition" => WalkTransposition(childCount, instrument),
                        _ => SkipChildren(childCount),
                    });

                player.Instruments.Add(instrument);

                return ok;
            });

        /// <summary>
        /// The instrument's transposition, which the file states as
        /// <c>intervalForTransposingScores = "steamVariantList:list: {int: 2}, {string: "kIntervalMajorOrPerfect"}"</c>
        /// — a major-or-perfect interval of two (a major second), five (a perfect fifth) and so on. Only its
        /// presence matters here: with it, a note's written pitch and its sounding pitch differ, so the note's MIDI
        /// number says nothing about the accidental its spelling implies.
        /// </summary>
        private bool WalkTransposition(long count, InstrumentDraft instrument)
            => WalkChildren(
                count,
                (key, value) =>
                {
                    if (NameAt(key) == "intervalForTransposingScores")
                    {
                        instrument.Transposing = true;
                        instrument.Transposition = Value(value);
                    }

                    return true;
                },
                (_, childCount, _) => SkipChildren(childCount));

        private bool WalkNameData(long count, InstrumentDraft instrument)            => WalkChildren(
                count,
                (key, value) =>
                {
                    if (NameAt(key) == "uiName")
                    {
                        instrument.Name = Value(value);
                    }

                    return true;
                },
                (_, childCount, _) => SkipChildren(childCount));

        // ------------------------------------------------------------ flows

        private bool WalkFlows(long count)
            => WalkItems(count, (_, children) => WalkFlow(children));

        private bool WalkFlow(long count)
        {
            var flow = new FlowDraft();
            var blocksOffset = -1;
            long blocksCount = 0;

            var ok = WalkChildren(
                count,
                (key, value) =>
                {
                    switch (NameAt(key))
                    {
                        case "flowID": flow.Id = Number(value); break;
                        case "flowName": flow.Name = Value(value); break;
                    }

                    return true;
                },
                (key, childCount, start) =>
                {
                    switch (NameAt(key))
                    {
                        case "flowPlayers": return WalkFlowPlayers(childCount);
                        case "blockInstances": return WalkBlockInstances(childCount);
                        case "eventStreams": return WalkEventStreams(childCount, flow);
                        case "elementTables": return WalkElementTables(childCount, flow);

                        // Blocks hold the notes and the bar-division events, and they need the stream map the other
                        // children build. Remember where they start and come back to them below.
                        case "blocks":
                            blocksOffset = start;
                            blocksCount = childCount;

                            return SkipChildren(childCount);
                        default: return SkipChildren(childCount);
                    }
                });

            if (!ok)
            {
                return false;
            }

            if (blocksOffset >= 0)
            {
                // Come back to the end of the flow's children afterwards: the blocks sit in the middle of them, and a
                // walk leaves the cursor wherever the subtree ended.
                var resume = _pos;

                _pos = blocksOffset;

                if (!ReadHeader(out _, out var children))
                {
                    return false;
                }

                var walked = WalkBlocks(children, flow);

                _pos = resume;

                if (!walked)
                {
                    return false;
                }
            }

            _flows.Add(flow);

            return true;
        }

        /// <summary>
        /// The stave and voice streams of every player in the flow: the map that turns a note's stream into a player,
        /// an instrument, a stave and a voice.
        /// </summary>
        private bool WalkFlowPlayers(long count)
            => WalkItems(count, (_, children) =>
            {
                long playerId = -1;
                long playerStream = -1;

                var ok = WalkChildren(
                    children,
                    (key, value) =>
                    {
                        switch (NameAt(key))
                        {
                            case "playerID": playerId = Number(value); break;
                            case "eventStreamID": playerStream = Number(value); break;
                        }

                        return true;
                    },
                    (key, childCount, _) => NameAt(key) == "flowPlayerInstruments"
                        ? WalkFlowPlayerInstruments(childCount, playerId)
                        : SkipChildren(childCount));

                if (playerStream >= 0)
                {
                    _streamStaves.TryAdd(
                        playerStream,
                        new StaveDraft(playerStream, "kPlayerStream", playerId, null, null, null));
                }

                return ok;
            });

        private bool WalkFlowPlayerInstruments(long count, long playerId)
            => WalkItems(count, (_, children) =>
            {
                long instrumentId = -1;

                return WalkChildren(
                    children,
                    (key, value) =>
                    {
                        if (NameAt(key) == "instrumentID")
                        {
                            instrumentId = Number(value);
                        }

                        return true;
                    },
                    (key, childCount, _) => NameAt(key) switch
                    {
                        "staveStreamInfos" => WalkStreamInfos(childCount, playerId, instrumentId, stave: true),
                        "voiceStreamInfos" => WalkStreamInfos(childCount, playerId, instrumentId, stave: false),
                        _ => SkipChildren(childCount),
                    });
            });

        private bool WalkStreamInfos(long count, long playerId, long instrumentId, bool stave)
        {
            var voice = 0;

            return WalkItems(count, (_, children) =>
            {
                long stream = -1;
                var staveIndex = (int?)null;
                var logicalStave = (int?)null;

                var ok = WalkChildren(
                    children,
                    (key, value) =>
                    {
                        switch (NameAt(key))
                        {
                            case "staveStreamID": stream = Number(value); break;
                            case "voiceStreamID": stream = Number(value); break;
                            case "staveID": staveIndex = (int)Number(value); break;
                            case "nthStaveInInstrument": staveIndex ??= (int)Number(value); break;
                            case "defaultLogicalStaveID": logicalStave = (int)Number(value); break;
                        }

                        return true;
                    },
                    (_, childCount, _) => SkipChildren(childCount));

                if (stream < 0)
                {
                    return ok;
                }

                if (stave)
                {
                    _streamStaves[stream] = new StaveDraft(
                        stream,
                        "kInstrumentStaveStream",
                        playerId,
                        instrumentId,
                        staveIndex is null ? null : staveIndex + 1,
                        null);
                }
                else
                {
                    voice++;

                    _streamStaves[stream] = new StaveDraft(
                        stream,
                        "kVoiceStream",
                        playerId,
                        instrumentId,
                        logicalStave is null ? null : logicalStave + 1,
                        voice);
                }

                return ok;
            });
        }

        private bool WalkBlockInstances(long count)
            => WalkItems(count, (_, children) =>
            {
                long blockId = -1;
                long streamId = -1;

                var ok = WalkChildren(
                    children,
                    (key, value) =>
                    {
                        switch (NameAt(key))
                        {
                            case "blockID": blockId = Number(value); break;
                            case "streamID": streamId = Number(value); break;
                        }

                        return true;
                    },
                    (_, childCount, _) => SkipChildren(childCount));

                if (blockId >= 0 && streamId >= 0)
                {
                    _blockStreams[blockId] = streamId;
                }

                return ok;
            });

        private bool WalkEventStreams(long count, FlowDraft flow)
            => WalkItems(count, (_, children) =>
            {
                long streamId = -1;
                string? kind = null;

                var ok = WalkChildren(
                    children,
                    (key, value) =>
                    {
                        switch (NameAt(key))
                        {
                            case "streamID": streamId = Number(value); break;
                            case "eventStreamType": kind = Value(value); break;
                        }

                        return true;
                    },
                    (_, childCount, _) => SkipChildren(childCount));

                if (streamId >= 0)
                {
                    flow.Streams++;

                    if (kind is not null)
                    {
                        _streamKinds[streamId] = kind;

                        if (_streamStaves.TryGetValue(streamId, out var existing))
                        {
                            _streamStaves[streamId] = existing with { Kind = kind };
                        }
                    }
                }

                return ok;
            });

        // ------------------------------------------------------------ element tables

        /// <summary>
        /// A flow's element tables. Three of them are read: the bar divisions (the metre), the dynamic groups (what a
        /// dynamic marking looks like) and the lyrics. Their items live in an <c>array</c> child, and an <b>empty</b>
        /// table writes that <c>array</c> as a leaf instead of a node — which is why a table is read through
        /// <see cref="WalkElementItems"/> and not through <see cref="WalkItems"/>.
        /// </summary>
        private bool WalkElementTables(long count, FlowDraft flow)
            => WalkItems(count, (kind, children) =>
            {
                switch (NameAt(kind))
                {
                    case "BarDivisionElementTableDefinition":
                        return WalkElementItems(children, WalkBarDivisionElement, out _);

                    case "DynamicGroupElementTableDefinition":
                        return WalkElementItems(children, element => WalkDynamicGroupElement(element, flow), out _);

                    case "LyricElementTableDefinition":
                        var ok = WalkElementItems(children, element => WalkLyricElement(element, flow), out var elements);
                        flow.LyricTables++;
                        flow.LyricElements += elements;

                        return ok;

                    case "SlurElementTableDefinition":
                        return WalkElementItems(children, element => WalkSlurElement(element, flow), out _);

                    default:
                        return SkipChildren(children);
                }
            });

        /// <summary>
        /// The items of one element table, with how many there were. Dorico writes the collection as <c>array</c>: a
        /// node holding the items, or — when the table has nothing in it — a leaf, which is exactly what every corpus
        /// file's lyric table is.
        /// </summary>
        private bool WalkElementItems(long count, Func<long, bool> onItem, out int items)
        {
            items = 0;

            if (count == 1 && _pos < _data.Length && IsLeafTag(_data[_pos]))
            {
                var save = _pos;

                if (!ReadLeaf(out var key, out _) || NameAt(key) != "array")
                {
                    _pos = save;
                }
                else
                {
                    return true;
                }
            }

            var seen = 0;

            var ok = WalkItems(count, (_, children) =>
            {
                seen++;

                return onItem(children);
            });

            items = seen;

            return ok;
        }

        private bool WalkBarDivisionElement(long count)
        {
            long elementId = -1;

            return WalkChildren(
                count,
                (key, value) =>
                {
                    if (NameAt(key) == "elementID")
                    {
                        elementId = Number(value);
                    }

                    return true;
                },
                (key, childCount, _) => NameAt(key) == "barDivisionData"
                    ? WalkBarDivisionData(childCount, elementId)
                    : SkipChildren(childCount));
        }

        /// <summary>
        /// One dynamic group definition: the markings that make it up. The group itself has no position — the events
        /// that name it carry that.
        /// </summary>
        private bool WalkDynamicGroupElement(long count, FlowDraft flow)
        {
            var group = new DynamicGroupDraft();

            var ok = WalkChildren(
                count,
                (key, value) =>
                {
                    switch (NameAt(key))
                    {
                        case "elementID": group.Id = Number(value); break;
                        case "referencingEventIDs": group.Events = Value(value); break;
                    }

                    return true;
                },
                (key, childCount, _) => NameAt(key) == "subElements"
                    ? WalkDynamicSubElements(childCount, group)
                    : SkipChildren(childCount));

            if (ok && group.Id >= 0)
            {
                flow.Groups[group.Id] = group;
            }

            return ok;
        }

        private bool WalkDynamicSubElements(long count, DynamicGroupDraft group)
            => WalkItems(count, (kind, children) =>
            {
                var mark = new DynamicMarkDraft(NameAt(kind) ?? "unknown");

                var ok = WalkChildren(
                    children,
                    (key, value) =>
                    {
                        switch (NameAt(key))
                        {
                            case "position": mark.Position = Value(value); break;
                            case "duration": mark.Duration = Value(value); break;
                            case "intensity": mark.Intensity = Value(value); break;
                            case "intensityLevel": mark.IntensityLevel = (int)Number(value); break;

                            // Only a gradual change has a hairpin direction; another kind that carries a field of the
                            // same name means something else by it, so it stays raw.
                            case "direction" when mark.Kind == "GradualChangeDynamicDefinition":
                                mark.Direction = Value(value);
                                break;

                            // A field this reader has not seen (another kind of dynamic, a version that adds
                            // fields): keep it rather than drop it, so the report can still show what the file says.
                            default: mark.Keep(NameAt(key), Value(value)); break;
                        }

                        return true;
                    },
                    (key, childCount, _) => NameAt(key) == "commonDef"
                        ? WalkDynamicCommonDef(childCount, mark)
                        : SkipChildren(childCount));

                group.Marks.Add(mark);

                return ok;
            });

        private bool WalkDynamicCommonDef(long count, DynamicMarkDraft mark)
            => WalkChildren(
                count,
                (key, value) =>
                {
                    if (NameAt(key) == "subito")
                    {
                        mark.Subito = Value(value);
                    }
                    else
                    {
                        mark.Keep(NameAt(key), Value(value));
                    }

                    return true;
                },
                (_, childCount, _) => SkipChildren(childCount));

        /// <summary>
        /// One lyric of a lyric element table.
        /// <para>
        /// No file in the corpus has one, so the shape is not known from data — but the application's own vocabulary
        /// is: its IO type is <c>LyricElementDefinition</c>, its event is <c>LyricEventDefinition</c>, the event
        /// points at an element with <c>lyricElementID</c>, and a lyric's line is addressed by
        /// <c>lyricLineNumberAndStaveIDAndLocation</c>. Everything below is read <i>by name</i> on that basis, keeps
        /// the raw leaves as they are, and records which key it took each reading from, so a real lyric element
        /// cannot be silently misread: either the expected key is there and is used, or the raw
        /// <see cref="ProjectFileLyricElement.Fields"/> say exactly what the file holds.
        /// </para>
        /// </summary>
        private bool WalkLyricElement(long count, FlowDraft flow)
        {
            var element = new LyricElementDraft();
            var fields = new List<string>();

            var ok = WalkChildren(
                count,
                (key, value) =>
                {
                    var name = NameAt(key);

                    Note(fields, name);
                    element.Keep(name, Value(value));

                    // The element's own id. lyricElementID is the name the application uses for a lyric; elementID is
                    // what the other element tables of this format write, kept as a fallback for a file that differs.
                    if (element.Id < 0 && name is "lyricElementID" or "elementID")
                    {
                        var id = Number(value);

                        if (id >= 0)
                        {
                            element.Id = id;
                            element.IdKey = name;
                        }
                    }

                    return true;
                },
                (key, children, _) =>
                {
                    var name = NameAt(key);

                    Note(fields, name);
                    element.KeepChild(name);

                    return name == "propertyTable"
                        ? WalkPropertyTable(children, element.Properties)
                        : SkipChildren(children);
                });

            if (ok)
            {
                flow.LyricShapes.Add(fields);
                flow.LyricReadings.Add(element);
            }

            return ok;
        }

        /// <summary>
        /// One lyric placed in the music, by the same shape as a dynamic: the event carries where it sits, and the
        /// element it names (through <c>lyricElementID</c>, by analogy with <c>slurElementID</c> and
        /// <c>dynamicGroupID</c>) carries the text. The event's own property table is read as properties, because the
        /// property tags the application knows for a lyric (<c>kLyricLineNumber</c>, <c>kLyricLineLocation</c>,
        /// <c>kLyricSyllableType</c>, <c>kLyricIsChorus</c>, <c>kLyricIsTranslation</c>, ...) are what could say
        /// which line and which verse a syllable belongs to.
        /// </summary>
        private bool WalkLyricEvent(long count, long stream, FlowDraft flow)
        {
            var lyric = new LyricEventDraft { Stream = stream, Flow = flow };

            var ok = WalkChildren(
                count,
                (key, value) =>
                {
                    switch (NameAt(key))
                    {
                        case "eventID": lyric.EventId = Number(value); break;
                        case "position": lyric.Position = ScoreRational.Parse(Value(value)); break;
                        case "duration": lyric.Duration = Value(value) ?? string.Empty; break;
                        case "lyricElementID": lyric.Element = Number(value); break;

                        // A lyric's text may sit on the event rather than on the element: keep whatever else the
                        // event carries so the report can show it, whichever side the file puts it on.
                        default: lyric.Keep(NameAt(key), Value(value)); break;
                    }

                    return true;
                },
                (key, childCount, _) => NameAt(key) == "propertyTable"
                    ? WalkPropertyTable(childCount, lyric.Properties)
                    : SkipChildren(childCount));

            if (ok)
            {
                flow.LyricEventCount++;
                _lyricEvents.Add(lyric);
            }

            return ok;
        }

        /// <summary>
        /// One slur element: the shared definition a slur's events point at. Its own content is the list of the events
        /// that reference it — a slur over a chord is one element referenced by one event per notehead.
        /// </summary>
        private bool WalkSlurElement(long count, FlowDraft flow)
        {
            var element = new SlurElementDraft();

            var ok = WalkChildren(
                count,
                (key, value) =>
                {
                    switch (NameAt(key))
                    {
                        case "elementID": element.Id = Number(value); break;
                        case "referencingEventIDs": element.Events = Value(value); break;
                    }

                    return true;
                },
                (_, childCount, _) => SkipChildren(childCount));

            if (ok && element.Id >= 0)
            {
                flow.SlurElements[element.Id] = element;
            }

            return ok;
        }

        /// <summary>
        /// The field names of a lyric, and nothing else: what a lyric holds is not modelled here (no file in the
        /// corpus has one), so the honest report is the shape and not a reading of it.
        /// </summary>
        private static void Note(List<string> fields, string? name)
        {
            if (name is not null && !fields.Contains(name))
            {
                fields.Add(name);
            }
        }

        private bool WalkBarDivisionData(long count, long elementId)
        {
            var signature = new TimeSignatureDraft();

            var ok = WalkChildren(
                count,
                null,
                (key, childCount, _) => NameAt(key) == "timeSignature"
                    ? WalkTimeSignature(childCount, signature)
                    : SkipChildren(childCount));

            if (ok && elementId >= 0 && signature.Numerator > 0 && signature.Denominator > 0)
            {
                _barElements[elementId] = new ElementDraft(signature.Numerator, signature.Denominator);
            }

            return ok;
        }

        private bool WalkTimeSignature(long count, TimeSignatureDraft signature)
            => WalkChildren(
                count,
                null,
                (key, childCount, _) => NameAt(key) == "timeSignaturesAndDivisions"
                    ? WalkTimeSignatureAndDivisions(childCount, signature)
                    : SkipChildren(childCount));

        private bool WalkTimeSignatureAndDivisions(long count, TimeSignatureDraft signature)
            => WalkItems(count, (_, division) =>
            {
                var local = new TimeSignatureDraft();

                var ok = WalkChildren(
                    division,
                    null,
                    (key, childCount, _) => NameAt(key) == "timeSignature"
                        ? WalkTimeSignatureNumbers(childCount, local)
                        : SkipChildren(childCount));

                if (local.Numerator > 0 && local.Denominator > 0)
                {
                    signature.Numerator = local.Numerator;
                    signature.Denominator = local.Denominator;
                }

                return ok;
            });

        private bool WalkTimeSignatureNumbers(long count, TimeSignatureDraft signature)
            => WalkChildren(
                count,
                (key, value) =>
                {
                    switch (NameAt(key))
                    {
                        case "numerator": signature.Numerator = (int)Number(value); break;
                        case "denominator": signature.Denominator = (int)Number(value); break;
                    }

                    return true;
                },
                (_, childCount, _) => SkipChildren(childCount));

        // ------------------------------------------------------------ blocks, events, notes

        private bool WalkBlocks(long count, FlowDraft flow)
            => WalkItems(count, (_, children) =>
            {
                flow.Blocks++;

                return WalkBlock(children, flow);
            });

        private bool WalkBlock(long count, FlowDraft flow)
        {
            long blockId = -1;
            string? streamKind = null;
            var eventsOffset = -1;
            long eventsCount = 0;

            var ok = WalkChildren(
                count,
                (key, value) =>
                {
                    switch (NameAt(key))
                    {
                        case "blockID": blockId = Number(value); break;
                        case "parentEventStreamType": streamKind = Value(value); break;
                    }

                    return true;
                },
                (key, childCount, start) =>
                {
                    if (NameAt(key) != "events")
                    {
                        return SkipChildren(childCount);
                    }

                    eventsOffset = start;
                    eventsCount = childCount;

                    return SkipChildren(childCount);
                });

            if (!ok || eventsOffset < 0)
            {
                return ok;
            }

            var stream = _blockStreams.GetValueOrDefault(blockId, -1);

            if (stream >= 0 && streamKind is not null)
            {
                _streamKinds[stream] = streamKind;

                if (_streamStaves.TryGetValue(stream, out var existing))
                {
                    _streamStaves[stream] = existing with { Kind = streamKind };
                }
            }

            _pos = eventsOffset;

            if (!ReadHeader(out _, out var events) || events != eventsCount)
            {
                return false;
            }

            return WalkEvents(events, stream, flow);
        }

        private bool WalkEvents(long count, long stream, FlowDraft flow)
            => WalkChildren(
                count,
                null,
                (key, childCount, _) => NameAt(key) switch
                {
                    "NoteEventDefinition" => WalkNote(childCount, stream, flow),
                    "SlurEventDefinition" => WalkSlurEvent(childCount, stream, flow),
                    "PlayingTechniqueEventDefinition" => WalkTechniqueEvent(childCount, stream, flow),
                    "BarDivisionEventDefinition" => WalkBarDivisionEvent(childCount),
                    "DynamicGroupEventDefinition" => WalkDynamicEvent(childCount, stream, flow),
                    "LyricEventDefinition" => WalkLyricEvent(childCount, stream, flow),
                    _ => SkipChildren(childCount),
                });

        /// <summary>
        /// One slur, placed in the music. The event carries where it starts and how long it is; the element it names
        /// carries nothing but the list of the events that share it, so two events of the same element are two
        /// noteheads of one slur.
        /// </summary>
        private bool WalkSlurEvent(long count, long stream, FlowDraft flow)
        {
            var slur = new SlurDraft { Stream = stream, Flow = flow };

            var ok = WalkChildren(
                count,
                (key, value) =>
                {
                    switch (NameAt(key))
                    {
                        case "eventID": slur.EventId = Number(value); break;
                        case "position": slur.Position = ScoreRational.Parse(Value(value)); break;
                        case "duration": slur.Duration = Value(value) ?? string.Empty; break;
                        case "slurElementID": slur.Element = Number(value); break;
                    }

                    return true;
                },
                (key, childCount, _) => NameAt(key) == "propertyTable"
                    ? WalkPropertyTable(childCount, slur.Properties)
                    : SkipChildren(childCount));

            if (ok)
            {
                flow.Slurs++;
                _slurs.Add(slur);
            }

            return ok;
        }

        /// <summary>
        /// A playing technique placed in the music as its own event. The file writes which <i>appearance</i> is shown
        /// (<c>pta.pizzText</c>); the appearance's definition names the technique (<c>pt.pizzicato</c>) behind it.
        /// </summary>
        private bool WalkTechniqueEvent(long count, long stream, FlowDraft flow)
        {
            var technique = new TechniqueEventDraft { Stream = stream };

            var ok = WalkChildren(
                count,
                (key, value) =>
                {
                    switch (NameAt(key))
                    {
                        case "eventID": technique.EventId = Number(value); break;
                        case "position": technique.Position = ScoreRational.Parse(Value(value)); break;
                        case "duration": technique.Duration = Value(value) ?? string.Empty; break;
                        case "appearanceEntityID": technique.AppearanceId = Value(value); break;
                        case "appearanceType": technique.AppearanceType = Value(value); break;
                        case "data": technique.Data = Value(value); break;
                    }

                    return true;
                },
                (key, childCount, _) => NameAt(key) == "propertyTable"
                    ? WalkPropertyTable(childCount, technique.Properties)
                    : SkipChildren(childCount));

            if (ok)
            {
                flow.Techniques++;
                _techniqueEvents.Add(technique);
            }

            return ok;
        }

        /// <summary>
        /// A note's property table: the decorations that are not events of their own — articulations, a tie's
        /// engraving, a beam break, a playing technique attached to the note. An empty table is written as a leaf
        /// named <c>entries</c> with no value, so it is read through the leaf as well as the node.
        /// </summary>
        private bool WalkPropertyTable(long count, List<PropertyDraft> into)
            => WalkChildren(
                count,
                null,
                (key, childCount, _) => NameAt(key) == "entries"
                    ? WalkPropertyEntries(childCount, into)
                    : SkipChildren(childCount));

        private bool WalkPropertyEntries(long count, List<PropertyDraft> into)
            => WalkChildren(
                count,
                null,
                (key, childCount, _) => NameAt(key) == "entry"
                    ? WalkPropertyEntry(childCount, into)
                    : SkipChildren(childCount));

        private bool WalkPropertyEntry(long count, List<PropertyDraft> into)
        {
            string? type = null;
            string? value = null;

            var ok = WalkChildren(
                count,
                (key, leafValue) =>
                {
                    switch (NameAt(key))
                    {
                        case "propertyType": type = Value(leafValue); break;
                        case "value": value = Value(leafValue); break;
                    }

                    return true;
                },
                (_, childCount, _) => SkipChildren(childCount));

            if (ok && type is not null)
            {
                into.Add(new PropertyDraft(type, value));
            }

            return ok;
        }

        /// <summary>
        /// A dynamic marking placed in the music. The event says where it goes and which group it is; the group
        /// definition in the flow's element tables says what it looks like.
        /// </summary>
        private bool WalkDynamicEvent(long count, long stream, FlowDraft flow)
        {
            var mark = new DynamicDraft { Stream = stream, Flow = flow };

            var ok = WalkChildren(
                count,
                (key, value) =>
                {
                    switch (NameAt(key))
                    {
                        case "eventID": mark.EventId = Number(value); break;
                        case "position": mark.Position = ScoreRational.Parse(Value(value)); break;
                        case "duration": mark.Duration = Value(value) ?? string.Empty; break;
                        case "dynamicGroupID": mark.Group = Number(value); break;
                    }

                    return true;
                },
                (_, childCount, _) => SkipChildren(childCount));

            if (ok && mark.Group >= 0)
            {
                flow.Dynamics++;
                _dynamicEvents.Add(mark);
            }

            return ok;
        }

        private bool WalkNote(long count, long stream, FlowDraft flow)
        {
            var note = new NoteDraft { Stream = stream };

            var ok = WalkChildren(
                count,
                (key, value) =>
                {
                    switch (NameAt(key))
                    {
                        case "eventID": note.EventId = Number(value); break;
                        case "position": note.Position = Value(value) is { } position ? ScoreRational.Parse(position) : ScoreRational.Zero; break;
                        case "duration": note.Duration = Value(value) ?? string.Empty; break;
                        case "pitch": note.Pitch = Value(value); break;
                        case "logicalStaveIDOverride": note.Stave = (int)Number(value); break;
                    }

                    return true;
                },
                (key, childCount, _) => NameAt(key) == "propertyTable"
                    ? WalkPropertyTable(childCount, note.Properties)
                    : SkipChildren(childCount));

            if (ok)
            {
                flow.Notes++;
                _notes.Add(note);
            }

            return ok;
        }

        private bool WalkBarDivisionEvent(long count)
        {
            long elementId = -1;
            var position = ScoreRational.Zero;

            var ok = WalkChildren(
                count,
                (key, value) =>
                {
                    switch (NameAt(key))
                    {
                        case "barDivisionElementID": elementId = Number(value); break;
                        case "position": position = ScoreRational.Parse(Value(value)); break;
                    }

                    return true;
                },
                (_, childCount, _) => SkipChildren(childCount));

            if (elementId >= 0)
            {
                _barEvents.Add(new MeterEventDraft(elementId, position));
            }

            return ok;
        }

        // ------------------------------------------------------------ output

        public ProjectFileScore Build(uint formatVersion)
        {
            var spans = MeterSpans();
            var notes = new List<ProjectFileNote>(_notes.Count);
            var names = _players.ToDictionary(player => player.Id, player => DisplayName(player.Name));

            var instruments = new Dictionary<(long Player, long Instrument), string?>();
            var transposing = new HashSet<(long Player, long Instrument)>();

            foreach (var player in _players)
            {
                foreach (var instrument in player.Instruments)
                {
                    instruments[(player.Id, instrument.Id)] = DisplayName(instrument.Name);

                    if (instrument.Transposing)
                    {
                        transposing.Add((player.Id, instrument.Id));
                    }
                }
            }

            var counts = new Dictionary<long, int>();

            foreach (var note in _notes)
            {
                if (note.Stream >= 0)
                {
                    counts[note.Stream] = counts.GetValueOrDefault(note.Stream) + 1;
                }
            }

            var lastMeasure = 0;

            // Score order: by when the note sounds, then by stream, then by event id. Document order in the file is
            // by stream, which would fill the first page of a report with one player.
            _notes.Sort(static (left, right) =>
            {
                var byPosition = left.Position.CompareTo(right.Position);

                if (byPosition != 0)
                {
                    return byPosition;
                }

                var byStream = left.Stream.CompareTo(right.Stream);

                return byStream != 0 ? byStream : left.EventId.CompareTo(right.EventId);
            });

            foreach (var note in _notes)
            {
                var (measure, beat) = Locate(spans, note.Position);
                _streamStaves.TryGetValue(note.Stream, out var stave);

                var midi = int.TryParse(note.Pitch, NumberStyles.Integer, CultureInfo.InvariantCulture, out var pitch)
                    ? pitch
                    : (int?)null;

                if (measure > lastMeasure)
                {
                    lastMeasure = (int)measure;
                }

                var spelling = Spelling(note.Properties);

                // Only a concert-pitch instrument lets the sounding MIDI number and the written letter agree: on a
                // transposing instrument the two differ by the instrument's interval, so any accidental derived from
                // them would look plausible and be wrong. The file says which instruments transpose, so those notes
                // get null instead of a guess.
                var concertPitch = stave is null
                    || stave.PlayerId is null
                    || stave.InstrumentId is null
                    || !transposing.Contains((stave.PlayerId.Value, stave.InstrumentId.Value));

                var accidental = concertPitch ? AccidentalOf(midi, spelling) : null;

                notes.Add(new ProjectFileNote(
                    note.EventId,
                    (int)measure,
                    beat.ToText(),
                    note.Position.ToText(),
                    note.Duration,
                    midi,
                    midi is null ? note.Pitch : PitchName(midi.Value),
                    note.Stream < 0 ? null : (int)note.Stream,
                    stave is null || stave.PlayerId is null ? null : names.GetValueOrDefault(stave.PlayerId.Value),
                    stave is null || stave.PlayerId is null || stave.InstrumentId is null
                        ? null
                        : instruments.GetValueOrDefault((stave.PlayerId.Value, stave.InstrumentId.Value)),
                    stave?.Stave,
                    stave?.Voice,
                    spelling,
                    accidental?.Name,
                    accidental?.Steps));
            }

            var staves = _streamStaves.Values
                .Where(stave => counts.GetValueOrDefault(stave.Stream) > 0)
                .OrderBy(stave => stave.Stream)
                .Select(stave => new ProjectFileStave(
                    (int)stave.Stream,
                    stave.Kind,
                    stave.PlayerId is { } playerId ? names.GetValueOrDefault(playerId) : null,
                    stave.PlayerId is { } owner && stave.InstrumentId is { } instrument
                        ? instruments.GetValueOrDefault((owner, instrument))
                        : null,
                    stave.Stave,
                    stave.Voice,
                    counts.GetValueOrDefault(stave.Stream)))
                .ToList();

            // Dynamics come off the same streams as notes, so the player/stave map is the one already built.
            var dynamics = _dynamicEvents
                .OrderBy(mark => mark.Position)
                .ThenBy(mark => mark.Stream)
                .ThenBy(mark => mark.EventId)
                .Select(item =>
                {
                    var (measure, beat) = Locate(spans, item.Position);
                    _streamStaves.TryGetValue(item.Stream, out var stave);

                    var group = item.Flow is { } flow
                        && item.Group >= 0
                        && flow.Groups.TryGetValue(item.Group, out var found)
                            ? found
                            : null;

                    return new ProjectFileDynamic(
                        item.EventId,
                        (int)measure,
                        beat.ToText(),
                        item.Position.ToText(),
                        item.Duration,
                        item.Group,
                        item.Stream < 0 ? null : (int)item.Stream,
                        stave is null || stave.PlayerId is null ? null : names.GetValueOrDefault(stave.PlayerId.Value),
                        stave is null || stave.PlayerId is null || stave.InstrumentId is null
                            ? null
                            : instruments.GetValueOrDefault((stave.PlayerId.Value, stave.InstrumentId.Value)),
                        stave?.Stave,
                        stave?.Voice,
                        [.. (group?.Marks ?? []).Select(mark => AsMark(mark, item.Position, spans))]);
                })
                .ToList();

            var groups = _flows
                .SelectMany(flow => flow.Groups.Values)
                .OrderBy(group => group.Id)
                .Select(group => new ProjectFileDynamicGroup(
                    group.Id,
                    group.Events,
                    [.. group.Marks.Select(mark => AsMark(mark))]))
                .ToList();

            var lyrics = new ProjectFileLyrics(
                _flows.Sum(flow => flow.LyricTables),
                _flows.Sum(flow => flow.LyricElements),
                _flows.SelectMany(flow => flow.LyricShapes).ToList(),
                [.. _flows
                    .SelectMany(flow => flow.LyricReadings)
                    .Select(ReadLyricElement)],
                [.. _lyricEvents
                    .OrderBy(lyric => lyric.Position)
                    .ThenBy(lyric => lyric.Stream)
                    .ThenBy(lyric => lyric.EventId)
                    .Select(lyric =>
                    {
                        var (measure, beat) = Locate(spans, lyric.Position);
                        _streamStaves.TryGetValue(lyric.Stream, out var stave);

                        return new ProjectFileLyricEvent(
                            lyric.EventId,
                            (int)measure,
                            beat.ToText(),
                            lyric.Position.ToText(),
                            lyric.Duration,
                            lyric.Element,
                            lyric.Stream < 0 ? null : (int)lyric.Stream,
                            stave is null || stave.PlayerId is null ? null : names.GetValueOrDefault(stave.PlayerId.Value),
                            stave is null || stave.PlayerId is null || stave.InstrumentId is null
                                ? null
                                : instruments.GetValueOrDefault((stave.PlayerId.Value, stave.InstrumentId.Value)),
                            stave?.Stave,
                            stave?.Voice,
                            lyric.Fields);
                    })],
                _lyricEvents.Count);

            var slurs = _slurs
                .OrderBy(slur => slur.Position)
                .ThenBy(slur => slur.Stream)
                .ThenBy(slur => slur.EventId)
                .Select(slur =>
                {
                    var element = Element(slur.Flow, slur.Element);

                    return new ProjectFileSlur(
                        Locate(spans, slur.Position, slur.EventId, slur.Duration, slur.Stream, names, instruments),
                        slur.Element < 0 ? null : slur.Element,
                        element is null ? 0 : Ids(element.Events).Count,
                        Property(slur.Properties, "kSlurForcedDirection") is { } direction
                            ? ProjectFileScoreReader.VariantText(direction)
                            : null,
                        AsInt(Property(slur.Properties, "kSlurEndVoiceID")),
                        [.. slur.Properties.Select(property => AsField(property))]);
                })
                .ToList();

            var slurElements = _flows
                .SelectMany(flow => flow.SlurElements.Values)
                .OrderBy(element => element.Id)
                .Select(element => new ProjectFileSlurElement(element.Id, Ids(element.Events)))
                .ToList();

            // Every decoration a note carries as a property, in the file's own words. Notes are already sorted into
            // score order, so the marks come out in the same order and can be read beside them.
            var marks = new List<ProjectFileNoteMark>();
            var catalogue = TechniqueCatalogue();

            foreach (var note in _notes)
            {
                if (note.Properties.Count == 0)
                {
                    continue;
                }

                var midi = Midi(note.Pitch);
                var location = Locate(spans, note.Position, note.EventId, note.Duration, note.Stream, names, instruments, midi);

                foreach (var property in note.Properties)
                {
                    marks.Add(new ProjectFileNoteMark(
                        location,
                        MarkFamily(property.Type),
                        property.Type,
                        property.Value,
                        MarkName(property.Type, property.Value, catalogue)));
                }
            }

            var techniqueEvents = _techniqueEvents
                .OrderBy(technique => technique.Position)
                .ThenBy(technique => technique.Stream)
                .ThenBy(technique => technique.EventId)
                .Select(technique =>
                {
                    var appearance = _techniqueAppearances
                        .FirstOrDefault(item => item.EntityId == technique.AppearanceId);
                    var defined = appearance is null
                        ? null
                        : catalogue.GetValueOrDefault(appearance.TechniqueId ?? string.Empty);

                    return new ProjectFileTechniqueEvent(
                        Locate(spans, technique.Position, technique.EventId, technique.Duration, technique.Stream, names, instruments),
                        technique.AppearanceId,
                        technique.AppearanceType,
                        appearance?.TechniqueId,
                        defined,
                        appearance?.Text,
                        appearance?.Name,
                        [.. technique.Properties.Select(property => AsField(property))]);
                })
                .ToList();

            return new ProjectFileScore(
                _fileVersion,
                _names.Count,
                _pool.Count,
                _records,
                new ProjectFileScoreInfo(
                    _info.Title, _info.Subtitle, _info.Composer, _info.Arranger, _info.Lyricist,
                    _info.CreatedDate, _info.LastSavedDate, _info.CreatedVersion, _info.LastSavedVersion,
                    _info.Originator),
                [.. _players.Select(player => new ProjectFilePlayer(
                    (int)player.Id,
                    DisplayName(player.Name),
                    DisplayName(player.ShortName),
                    [.. player.Instruments.Select(instrument => new ProjectFileInstrument(
                        (int)instrument.Id,
                        instrument.EntityId,
                        DisplayName(instrument.Name),
                        instrument.Staves,
                        instrument.Range))]))],
                [.. _layouts.Select(layout => new ProjectFileLayout(
                    (int)layout.Id, (int)layout.Number, DisplayName(layout.Name), layout.Type))],
                [.. _flows.Select(flow => new ProjectFileFlow(
                    (int)flow.Id, flow.Name, flow.Streams, flow.Blocks, flow.Notes))],
                staves,
                MeterRows(spans),
                notes,
                dynamics,
                groups,
                slurs,
                slurElements,
                marks,
                techniqueEvents,
                [.. _techniqueDefinitions.Select(definition => new ProjectFileTechniqueDefinition(
                    definition.EntityId ?? string.Empty,
                    definition.Name,
                    definition.ArticulationType,
                    definition.GroupType,
                    definition.AliasFor))],
                [.. _techniqueAppearances.Select(appearance => new ProjectFileTechniqueAppearance(
                    appearance.EntityId ?? string.Empty,
                    appearance.Name,
                    appearance.Text,
                    appearance.TechniqueId,
                    appearance.Type,
                    appearance.Category,
                    appearance.Language))],
                new ProjectFileLayoutData(
                    [.. _layouts.Select(layout => new ProjectFileLayoutPage(
                        (int)layout.Id,
                        DisplayName(layout.Name),
                        layout.Type,
                        (int)layout.Number,
                        layout.PageSet,
                        layout.PageWidth,
                        layout.PageHeight,
                        layout.SpaceSize,
                        [.. layout.Margins],
                        [.. layout.CastingOff],
                        layout.FrameChainOverrides))],
                    [.. _pageSets.Select(pageSet => new ProjectFilePageSet(
                        pageSet.EntityId,
                        pageSet.Name,
                        pageSet.Type,
                        [.. pageSet.MusicChains],
                        [.. pageSet.TextChains],
                        [.. pageSet.Frames.Select(frame => new ProjectFileFrame(
                            frame.Kind,
                            frame.Side,
                            frame.Chain,
                            frame.LayoutSpecific,
                            frame.Index,
                            frame.ContentRelative,
                            [.. frame.Position],
                            [.. frame.Size]))]))],
                    _layouts.Sum(layout => layout.FrameChainOverrides)),
                lyrics,
                lastMeasure);
        }

        /// <summary>The flow a slur's element table belongs to: the flow whose blocks hold the slur event.</summary>
        private static SlurElementDraft? Element(FlowDraft? flow, long element)
            => flow is not null && element >= 0 && flow.SlurElements.TryGetValue(element, out var found) ? found : null;

        /// <summary>The MIDI number a note event writes, or null when the field is not one.</summary>
        private static int? Midi(string? pitch)
            => int.TryParse(pitch, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : null;

        /// <summary>
        /// The letter the score spells a note with: the note event's <c>kNoteSpelling</c> property, written as a
        /// variant string holding a letter. It is reported exactly as the file writes it and is null when the event
        /// states none — it is <i>read</i>, never derived from the MIDI number, which is the whole point of it.
        /// </summary>
        private static string? Spelling(List<PropertyDraft> properties)
            => VariantText(Property(properties, "kNoteSpelling"));

        /// <summary>
        /// The accidental a spelling implies, derived from the letter and the sounding pitch: the file stores
        /// none, but the two together pin it. Natural semitone offsets are C=0 D=2 E=4 F=5 G=7 A=9 B=11, the octave
        /// follows from Dorico's C4 = middle C, and the difference between the note's MIDI number and that natural
        /// is the accidental (0 natural, +1 sharp, -1 flat, and so on). Anything beyond a triple accidental is not a
        /// written pitch at all - unpitched percussion keeps a drum-map position in the same field - so those notes
        /// get null rather than a guess.
        /// </summary>
        private static (string Name, int Steps)? AccidentalOf(int? midi, string? spelling)
        {
            if (midi is not { } value || string.IsNullOrWhiteSpace(spelling))
            {
                return null;
            }

            var naturalOffset = char.ToUpperInvariant(spelling.Trim()[0]) switch
            {
                'C' => 0,
                'D' => 2,
                'E' => 4,
                'F' => 5,
                'G' => 7,
                'A' => 9,
                'B' => 11,
                _ => -1,
            };

            if (naturalOffset < 0)
            {
                return null;
            }

            var octave = (int)Math.Round((value - naturalOffset - 12) / 12.0, MidpointRounding.AwayFromZero);
            var naturalMidi = (octave * 12) + 12 + naturalOffset;
            var steps = value - naturalMidi;

            var name = steps switch
            {
                0 => "kNatural",
                1 => "kSharp",
                2 => "kDoubleSharp",
                3 => "kTripleSharp",
                -1 => "kFlat",
                -2 => "kDoubleFlat",
                -3 => "kTripleFlat",
                _ => null,
            };

            return name is null ? null : (name, steps);
        }

        /// <summary>A property of an event, by the file's own name for it.</summary>
        private static string? Property(List<PropertyDraft> properties, string name)
            => properties.FirstOrDefault(property => property.Type == name)?.Value;

        private static int? AsInt(string? value)
            => int.TryParse(VariantText(value), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : null;

        private static ProjectFileTreeField AsField(PropertyDraft property)
            => new(property.Type, property.Value);

        /// <summary>
        /// Reads one lyric element by name. The four names that are looked for are the ones the application uses for
        /// a lyric's own content (<c>richText</c>, <c>text</c>, <c>syllableType</c>) and for the line it belongs to
        /// (<c>lineNumber</c>, <c>lineLocation</c>, <c>lineType</c>); the text is only claimed when the name matches,
        /// and the key it came from is reported beside it. Everything else the element holds is kept verbatim in
        /// <see cref="ProjectFileLyricElement.Fields"/>, so a file whose names differ still says what it contains.
        /// </summary>
        private static ProjectFileLyricElement ReadLyricElement(LyricElementDraft element)
        {
            var text = First(element, "richText", "text", "lyricText", "syllabicText");
            var syllable = First(element, "syllableType", "syllabic");
            var line = First(element, "lineNumber", "lyricLineNumber");
            var location = First(element, "lineLocation", "lyricLineLocation");
            var lineType = First(element, "lineType", "lyricLineType");

            return new ProjectFileLyricElement(
                element.Id < 0 ? null : element.Id,
                element.IdKey,
                text?.Value,
                text?.Key,
                syllable?.Value,
                line?.Value,
                location?.Value,
                lineType?.Value,
                element.Fields,
                element.ChildKinds,
                [.. element.Properties.Select(property => new ProjectFileLyricField(property.Type, property.Value))]);
        }

        /// <summary>The first leaf of an element whose key is one of the given names, or null when none is.</summary>
        private static ProjectFileLyricField? First(LyricElementDraft element, params string[] keys)
            => element.Fields.FirstOrDefault(field => keys.Contains(field.Key, StringComparer.Ordinal));

        /// <summary>
        /// The location of one event: its own position and duration, the measure and beat the metre puts it at, and
        /// the player, instrument, stave and voice of the stream it sounds in. The note text and the pitch name are
        /// the ones the note reader already derived, or null for an event that is not a note.
        /// </summary>
        private ProjectFileLocation Locate(
            List<MeterSpan> spans,
            ScoreRational position,
            long eventId,
            string duration,
            long stream,
            Dictionary<long, string?> names,
            Dictionary<(long Player, long Instrument), string?> instruments,
            int? midi = null)
        {
            var (measure, beat) = Locate(spans, position);
            _streamStaves.TryGetValue(stream, out var stave);

            return new ProjectFileLocation(
                eventId,
                (int)measure,
                beat.ToText(),
                position.ToText(),
                duration,
                midi,
                midi is null ? null : PitchName(midi.Value),
                stream < 0 ? null : (int)stream,
                stave is null || stave.PlayerId is null ? null : names.GetValueOrDefault(stave.PlayerId.Value),
                stave is null || stave.PlayerId is null || stave.InstrumentId is null
                    ? null
                    : instruments.GetValueOrDefault((stave.PlayerId.Value, stave.InstrumentId.Value)),
                stave?.Stave,
                stave?.Voice);
        }

        /// <summary>The score's own playing-technique catalogue, by entity id.</summary>
        private Dictionary<string, string?> TechniqueCatalogue()
        {
            var catalogue = new Dictionary<string, string?>(StringComparer.Ordinal);

            foreach (var definition in _techniqueDefinitions)
            {
                if (definition.EntityId is not null)
                {
                    catalogue[definition.EntityId] = definition.Name;
                }
            }

            return catalogue;
        }

        /// <summary>A comma-separated id list from the file, as numbers.</summary>
        private static List<long> Ids(string? text)
            => [.. (text ?? string.Empty)
                .Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(part => long.TryParse(part.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
                    ? value
                    : -1)
                .Where(value => value >= 0)];

        private static ProjectFileDynamicMark AsMark(
            DynamicMarkDraft mark,
            ScoreRational? origin = null,
            List<MeterSpan>? spans = null)
        {
            // Where the mark sits: the event's position plus the mark's offset inside its group. A group reported on
            // its own has no origin, and a shape whose offset is not a rational is left unresolved rather than
            // guessed at.
            ScoreRational? at = null;
            int? measure = null;
            string? beat = null;

            if (origin is { } start && ScoreRational.TryParse(mark.Position, out var offset))
            {
                var absolute = start + offset;
                at = absolute;

                if (spans is not null)
                {
                    var (located, atBeat) = Locate(spans, absolute);
                    measure = (int)located;
                    beat = atBeat.ToText();
                }
            }

            return new ProjectFileDynamicMark(
                mark.Kind,
                DynamicMarkName(mark.Kind, mark.Intensity, mark.IntensityLevel, mark.Direction),
                mark.Position,
                at?.ToText(),
                measure,
                beat,
                mark.Duration,
                mark.Intensity,
                mark.IntensityLevel,
                mark.Direction,
                mark.Subito,
                mark.Fields);
        }

        /// <summary>
        /// The notation a part of a dynamic group stands for, as far as this reader can prove it.
        /// </summary>
        /// <remarks>
        /// An immediate change carries its intensity as a small variant list — family, an empty slot, mezzo, level —
        /// so <c>{int: 0}, {}, {bool: false}, {int: 2}</c> is a forte family, not mezzo, level 2, i.e. <c>ff</c>. That
        /// reading was checked against the MusicXML export of two corpus projects: 257 markings of one and 35 of the
        /// other, family for family (<c>f/ff/fff</c> and <c>p/pp</c>), <c>mf</c>/<c>mp</c>, and the 12 hairpins of the
        /// first against 12 <c>&lt;wedge&gt;</c> elements. A shape that was not in a checked export — another level, a
        /// family this reader has not seen, a force other than level 1 — returns null rather than a guess; the raw
        /// fields are reported either way.
        /// </remarks>
        internal static string? DynamicMarkName(string kind, string? intensity, int? level, string? direction)
            => kind switch
            {
                "ImmediateChangeDynamicDefinition" => IntensityName(intensity),
                "ForceDynamicDefinition" => level == 1 ? "fz" : null,
                "GradualChangeDynamicDefinition" => direction switch
                {
                    "kGradualDynamicUp" => "cresc",
                    "kGradualDynamicDown" => "dim",
                    _ => null,
                },
                _ => null,
            };

        /// <summary>The p/f family of an immediate change: family, _, mezzo, level.</summary>
        private static string? IntensityName(string? intensity)
        {
            var items = Variants(intensity);

            if (items.Count < 4 || items[0].Kind != "int" || items[2].Kind != "bool")
            {
                return null;
            }

            var family = items[0].Value;

            if (!int.TryParse(family, NumberStyles.Integer, CultureInfo.InvariantCulture, out var side)
                || side is < 0 or > 1)
            {
                return null;
            }

            var letter = side == 0 ? 'f' : 'p';

            if (items[2].Value == "true")
            {
                // mezzo: `mf` or `mp`, and every file seen writes no level for one.
                return items[3].Kind == "int" ? null : "m" + letter;
            }

            return int.TryParse(items[3].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var count)
                && count is >= 1 and <= 6
                    ? new string(letter, count)
                    : null;
        }

        /// <summary>
        /// The items of a variant list: <c>steamVariantList:list: {int: 0}, {}, {bool: true}, {}</c> becomes
        /// <c>(int, 0) (empty, "") (bool, true) (empty, "")</c>. Nesting is tracked so a value with braces in it
        /// cannot be split in the wrong place.
        /// </summary>
        private static List<(string Kind, string Value)> Variants(string? text)
        {
            var items = new List<(string Kind, string Value)>();

            if (text is null)
            {
                return items;
            }

            var depth = 0;
            var start = -1;

            for (var index = 0; index < text.Length; index++)
            {
                switch (text[index])
                {
                    case '{':
                        if (depth == 0)
                        {
                            start = index + 1;
                        }

                        depth++;
                        break;

                    case '}':
                        depth--;

                        if (depth == 0 && start >= 0)
                        {
                            var item = text[start..index];
                            var colon = item.IndexOf(':');

                            items.Add(colon < 0
                                ? (string.Empty, string.Empty)
                                : (item[..colon].Trim(), item[(colon + 1)..].Trim()));
                        }

                        break;
                }
            }

            return items;
        }

        /// <summary>The bar divisions in force, in order: what turns a position into a measure number.</summary>
        private List<MeterSpan> MeterSpans()
        {
            var spans = _barEvents
                .Where(item => _barElements.ContainsKey(item.ElementId))
                .OrderBy(item => item.Position)
                .Select(item => new MeterSpan(
                    item.Position,
                    _barElements[item.ElementId].Numerator,
                    _barElements[item.ElementId].Denominator))
                .ToList();

            if (spans.Count == 0)
            {
                // A project always writes a bar division; if one is missing, 4/4 from the start is the only reading
                // left, and the report says the metre was assumed.
                spans.Add(new MeterSpan(ScoreRational.Zero, 4, 4));
            }

            for (var index = 0; index < spans.Count; index++)
            {
                if (index == 0)
                {
                    continue;
                }

                var previous = spans[index - 1];
                var length = BarLength(previous.Numerator, previous.Denominator);
                var delta = spans[index].Position - previous.Position;
                var bars = (long)Math.Round((delta / length).ToDouble(), MidpointRounding.AwayFromZero);

                spans[index] = spans[index] with { Bar = previous.Bar + Math.Max(bars, 0) };
            }

            return spans;
        }

        private static List<ProjectFileMeter> MeterRows(List<MeterSpan> spans)
            => [.. spans.Select(span => new ProjectFileMeter(
                (int)span.Bar,
                span.Position.ToText(),
                span.Numerator + "/" + span.Denominator,
                span.Numerator,
                span.Denominator))];

        private static ScoreRational BarLength(int numerator, int denominator)
            => ScoreRational.FromFraction(4L * numerator, denominator);

        /// <summary>Which measure a position falls in, and where in it: 1-based, in crotchets.</summary>
        private static (long Measure, ScoreRational Beat) Locate(List<MeterSpan> spans, ScoreRational position)
        {
            var span = spans[0];

            foreach (var candidate in spans)
            {
                if (candidate.Position.CompareTo(position) <= 0)
                {
                    span = candidate;
                }
                else
                {
                    break;
                }
            }

            var length = BarLength(span.Numerator, span.Denominator);
            var offset = position - span.Position;

            if (offset.Numerator < 0)
            {
                offset = ScoreRational.Zero;
            }

            var index = (long)Math.Floor((offset / length).ToDouble());
            var start = span.Position + (length * index);

            return (span.Bar + index, position - start + ScoreRational.One);
        }

        /// <summary>
        /// Turns Dorico's markup in a name into what its own window shows: the internal player name of a B flat
        /// clarinet is <c>Clarinet (B{@flat@}) 1</c>, and the UI renders the token as a flat sign. A token this
        /// reader does not know is left alone rather than guessed at.
        /// </summary>
        internal static string? DisplayName(string? text)
        {
            if (text is null || !text.Contains("{@", StringComparison.Ordinal))
            {
                return text;
            }

            return text
                .Replace("{@flat@}", "♭", StringComparison.Ordinal)
                .Replace("{@sharp@}", "♯", StringComparison.Ordinal)
                .Replace("{@natural@}", "♮", StringComparison.Ordinal);
        }

        private static string PitchName(int midi)
        {
            string[] names = ["C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B"];

            return names[((midi % 12) + 12) % 12] + ((midi / 12) - 1).ToString(CultureInfo.InvariantCulture);
        }
    }

    // ---------------------------------------------------------------- drafts

    private sealed class InfoDraft
    {
        public string? Title;
        public string? Subtitle;
        public string? Composer;
        public string? Arranger;
        public string? Lyricist;
        public string? CreatedDate;
        public string? LastSavedDate;
        public string? CreatedVersion;
        public string? LastSavedVersion;
        public string? Originator;
    }

    private sealed class PlayerDraft
    {
        public long Id;
        public string? Name;
        public string? ShortName;
        public List<InstrumentDraft> Instruments { get; } = [];
    }

    private sealed class InstrumentDraft
    {
        public long Id;
        public string? EntityId;
        public string? Name;
        public int Staves;
        public string? Range;

        /// <summary>
        /// True when the instrument node states a transposition (<c>transposition → intervalForTransposingScores</c>),
        /// which is what makes a written pitch and a sounding pitch differ. The reader keeps the raw text for a
        /// later pass that wants the interval itself; for the note spelling all that matters is that it is there.
        /// </summary>
        public bool Transposing;
        public string? Transposition;
    }

    private sealed class LayoutDraft
    {
        public long Id;
        public long Number;
        public string? Name;
        public string? Type;
        public string? PageSet;
        public string? PageWidth;
        public string? PageHeight;
        public string? SpaceSize;
        public int FrameChainOverrides;
        public List<ProjectFileTreeField> Margins { get; } = [];
        public List<ProjectFileTreeField> CastingOff { get; } = [];
    }

    private sealed class FlowDraft
    {
        public long Id;
        public string? Name;
        public int Streams;
        public int Blocks;
        public int Notes;
        public int Dynamics;
        public int Slurs;
        public int Techniques;
        public int LyricTables;
        public int LyricElements;
        public int LyricEventCount;
        public List<List<string>> LyricShapes { get; } = [];
        public List<LyricElementDraft> LyricReadings { get; } = [];
        public Dictionary<long, DynamicGroupDraft> Groups { get; } = [];
        public Dictionary<long, SlurElementDraft> SlurElements { get; } = [];
    }

    /// <summary>A slur element: an id and the events that reference it.</summary>
    private sealed class SlurElementDraft
    {
        public long Id = -1;
        public string? Events;
    }

    /// <summary>
    /// One lyric element as the file writes it: its leaves in order, the kinds of its child nodes, and any property
    /// table. <see cref="Id"/> stays -1 until a leaf named like an id turns up, so "no id found" stays
    /// distinguishable from "id = 0". <see cref="IdKey"/> says which key the id came from, because the element's own
    /// id field has not been seen in data.
    /// </summary>
    private sealed class LyricElementDraft
    {
        public long Id = -1;
        public string? IdKey;

        public List<ProjectFileLyricField> Fields { get; } = [];
        public List<string> ChildKinds { get; } = [];
        public List<PropertyDraft> Properties { get; } = [];

        /// <summary>A leaf: the key the file uses, and its raw text (null when the leaf has no value).</summary>
        public void Keep(string? key, string? value)
        {
            if (key is not null)
            {
                Fields.Add(new ProjectFileLyricField(key, value));
            }
        }

        public void KeepChild(string? key)
        {
            if (key is not null && !ChildKinds.Contains(key))
            {
                ChildKinds.Add(key);
            }
        }
    }

    /// <summary>A lyric placed in the music, before its measure, its stave and its element are resolved.</summary>
    private sealed class LyricEventDraft
    {
        public long EventId;
        public ScoreRational Position;
        public string Duration = string.Empty;
        public long Element = -1;
        public long Stream = -1;
        public FlowDraft? Flow;
        public List<ProjectFileLyricField> Fields { get; } = [];
        public List<PropertyDraft> Properties { get; } = [];

        public void Keep(string? key, string? value)
        {
            if (key is not null)
            {
                Fields.Add(new ProjectFileLyricField(key, value));
            }
        }
    }

    /// <summary>One property of an event: the file's own name for it and its value, encoded as a variant.</summary>
    private sealed record PropertyDraft(string Type, string? Value);

    /// <summary>A slur placed in the music, before its measure, its stave and its element are resolved.</summary>
    private sealed class SlurDraft
    {
        public long EventId;
        public ScoreRational Position;
        public string Duration = string.Empty;
        public long Element = -1;
        public long Stream = -1;
        public FlowDraft? Flow;
        public List<PropertyDraft> Properties { get; } = [];
    }

    /// <summary>A playing technique placed in the music, before its appearance and technique are resolved.</summary>
    private sealed class TechniqueEventDraft
    {
        public long EventId;
        public ScoreRational Position;
        public string Duration = string.Empty;
        public string? AppearanceId;
        public string? AppearanceType;
        public string? Data;
        public long Stream = -1;
        public List<PropertyDraft> Properties { get; } = [];
    }

    private sealed class TechniqueDefinitionDraft
    {
        public string? EntityId;
        public string? Name;
        public string? ArticulationType;
        public string? GroupType;
        public string? AliasFor;
    }

    private sealed class TechniqueAppearanceDraft
    {
        public string? EntityId;
        public string? Name;
        public string? Text;
        public string? TechniqueId;
        public string? Type;
        public string? Category;
        public string? Language;
    }

    private sealed class PageSetDraft
    {
        public string? EntityId;
        public string? Name;
        public string? Type;
        public List<long> MusicChains { get; } = [];
        public List<long> TextChains { get; } = [];
        public List<FrameDraft> Frames { get; } = [];
    }

    private sealed class FrameDraft
    {
        public string Kind = "normal";
        public string Side = "left";
        public long? Chain;
        public bool? LayoutSpecific;
        public int? Index;
        public bool? ContentRelative;
        public List<ProjectFileTreeField> Position { get; } = [];
        public List<ProjectFileTreeField> Size { get; } = [];
    }

    /// <summary>A dynamic group as the flow defines it: an id, the events that name it, and its parts.</summary>
    private sealed class DynamicGroupDraft
    {
        public long Id = -1;
        public string? Events;
        public List<DynamicMarkDraft> Marks { get; } = [];
    }

    /// <summary>One part of a dynamic group, as the file writes it.</summary>
    private sealed class DynamicMarkDraft(string kind)
    {
        public string Kind { get; } = kind;

        public string? Position;
        public string? Duration;
        public string? Intensity;
        public int? IntensityLevel;
        public string? Direction;
        public string? Subito;
        public List<ProjectFileTreeField> Fields { get; } = [];

        /// <summary>Keeps a field this reader does not model, up to a bound, instead of dropping it.</summary>
        public void Keep(string? key, string? value)
        {
            if (Fields.Count < 16)
            {
                Fields.Add(new ProjectFileTreeField(key ?? "?", value));
            }
        }
    }

    /// <summary>A dynamic marking event, before its bar, its stave and its group are resolved.</summary>
    private sealed class DynamicDraft
    {
        public long EventId;
        public ScoreRational Position;
        public string Duration = string.Empty;
        public long Group = -1;
        public long Stream = -1;
        public FlowDraft? Flow;
    }

    /// <summary>A note as it comes off the stream, before its bar and its stave are known.</summary>
    private sealed class NoteDraft
    {
        public long EventId;
        public ScoreRational Position;
        public string Duration = string.Empty;
        public string? Pitch;
        public int Stave;
        public long Stream = -1;
        public List<PropertyDraft> Properties { get; } = [];
    }

    private sealed record MeterEventDraft(long ElementId, ScoreRational Position);

    private sealed record ElementDraft(int Numerator, int Denominator);

    /// <summary>The metre a bar-division element declares, filled in from the XML-like nesting inside it.</summary>
    private sealed class TimeSignatureDraft
    {
        public int Numerator;
        public int Denominator;
    }

    private sealed record StaveDraft(
        long Stream,
        string? Kind,
        long? PlayerId,
        long? InstrumentId,
        int? Stave,
        int? Voice);

    private sealed record MeterSpan(
        ScoreRational Position,
        int Numerator,
        int Denominator,
        long Bar = 1);

    // ---------------------------------------------------------------- exact rationals

    /// <summary>
    /// An exact rational: positions and durations in a Dorico score are rationals (<c>25/4</c>, <c>1/4</c>), so they
    /// are kept exact instead of being rounded into a double — a triplet must not drift.
    /// </summary>
    private readonly struct ScoreRational : IComparable<ScoreRational>
    {
        public static readonly ScoreRational Zero = new(0, 1);

        public static readonly ScoreRational One = new(1, 1);

        private ScoreRational(long numerator, long denominator)
        {
            Numerator = numerator;
            Denominator = denominator;
        }

        public long Numerator { get; }

        public long Denominator { get; }

        public static ScoreRational FromFraction(long numerator, long denominator)
        {
            if (denominator == 0)
            {
                return Zero;
            }

            if (denominator < 0)
            {
                numerator = -numerator;
                denominator = -denominator;
            }

            var divisor = Gcd(Math.Abs(numerator), denominator);

            return divisor > 1
                ? new ScoreRational(numerator / divisor, denominator / divisor)
                : new ScoreRational(numerator, denominator);
        }

        public static ScoreRational Parse(string? text)
            => TryParse(text, out var value) ? value : Zero;

        /// <summary>Whether the text really is a position or a duration, rather than something that only looks like one.</summary>
        public static bool TryParse(string? text, out ScoreRational value)
        {
            value = Zero;

            if (string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            var trimmed = text.Trim();
            var slash = trimmed.IndexOf('/');

            if (slash < 0)
            {
                if (!long.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out var whole))
                {
                    return false;
                }

                value = new ScoreRational(whole, 1);

                return true;
            }

            if (!long.TryParse(trimmed[..slash], NumberStyles.Integer, CultureInfo.InvariantCulture, out var numerator)
                || !long.TryParse(trimmed[(slash + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var denominator))
            {
                return false;
            }

            value = FromFraction(numerator, denominator);

            return true;
        }

        public double ToDouble() => (double)Numerator / Denominator;

        public string ToText()
            => Denominator == 1
                ? Numerator.ToString(CultureInfo.InvariantCulture)
                : Numerator.ToString(CultureInfo.InvariantCulture) + "/" + Denominator.ToString(CultureInfo.InvariantCulture);

        public int CompareTo(ScoreRational other)
            => ((Int128)Numerator * other.Denominator).CompareTo((Int128)other.Numerator * Denominator);

        public static ScoreRational operator +(ScoreRational left, ScoreRational right)
            => FromFraction(
                (long)((Int128)left.Numerator * right.Denominator + (Int128)right.Numerator * left.Denominator),
                (long)((Int128)left.Denominator * right.Denominator));

        public static ScoreRational operator -(ScoreRational left, ScoreRational right)
            => FromFraction(
                (long)((Int128)left.Numerator * right.Denominator - (Int128)right.Numerator * left.Denominator),
                (long)((Int128)left.Denominator * right.Denominator));

        public static ScoreRational operator *(ScoreRational value, long factor)
            => FromFraction((long)((Int128)value.Numerator * factor), value.Denominator);

        public static ScoreRational operator /(ScoreRational left, ScoreRational right)
            => right.Numerator == 0
                ? Zero
                : FromFraction(
                    (long)((Int128)left.Numerator * right.Denominator),
                    (long)((Int128)left.Denominator * right.Numerator));

        private static long Gcd(long left, long right)
        {
            while (right != 0)
            {
                (left, right) = (right, left % right);
            }

            return left == 0 ? 1 : left;
        }
    }
}
