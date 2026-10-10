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
/// <b>crotchets</b> (a 16th is <c>1/4</c>; a 4/4 bar is 4 of them) and <c>pitch</c> as a MIDI note number.
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
                    (_, childCount, _) => SkipChildren(childCount));

                _layouts.Add(layout);

                return ok;
            });

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
                    (key, childCount, _) => NameAt(key) == "nameData"
                        ? WalkNameData(childCount, instrument)
                        : SkipChildren(childCount));

                player.Instruments.Add(instrument);

                return ok;
            });

        private bool WalkNameData(long count, InstrumentDraft instrument)
            => WalkChildren(
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

        /// <summary>One lyric of a lyric element table. The shape is not known — no file in the corpus has one.</summary>
        private bool WalkLyricElement(long count, FlowDraft flow)
        {
            var fields = new List<string>();

            var ok = WalkChildren(
                count,
                (key, _) =>
                {
                    Note(fields, NameAt(key));

                    return true;
                },
                (key, children, _) =>
                {
                    Note(fields, NameAt(key));

                    return SkipChildren(children);
                });

            if (ok)
            {
                flow.LyricShapes.Add(fields);
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
                    "BarDivisionEventDefinition" => WalkBarDivisionEvent(childCount),
                    "DynamicGroupEventDefinition" => WalkDynamicEvent(childCount, stream, flow),
                    _ => SkipChildren(childCount),
                });

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
                (_, childCount, _) => SkipChildren(childCount));

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

            foreach (var player in _players)
            {
                foreach (var instrument in player.Instruments)
                {
                    instruments[(player.Id, instrument.Id)] = DisplayName(instrument.Name);
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
                    stave?.Voice));
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
                _flows.SelectMany(flow => flow.LyricShapes).ToList());

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
                lyrics,
                lastMeasure);
        }

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
    }

    private sealed class LayoutDraft
    {
        public long Id;
        public long Number;
        public string? Name;
        public string? Type;
    }

    private sealed class FlowDraft
    {
        public long Id;
        public string? Name;
        public int Streams;
        public int Blocks;
        public int Notes;
        public int Dynamics;
        public int LyricTables;
        public int LyricElements;
        public List<List<string>> LyricShapes { get; } = [];
        public Dictionary<long, DynamicGroupDraft> Groups { get; } = [];
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
