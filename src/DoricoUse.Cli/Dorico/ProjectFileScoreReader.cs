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
internal static partial class ProjectFileScoreReader
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

    private sealed partial class Decoder
    {
        private readonly byte[] _data;
        private readonly IReadOnlyList<string> _names;
        private readonly int _poolStart;
        private readonly int _poolCount;

        private readonly List<string> _pool;
        private readonly InfoDraft _info = new();
        private readonly List<PlayerDraft> _players = [];
        private readonly List<PlayerGroupDraft> _playerGroups = [];
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

        private static bool IsLeafTag(byte tag) => ProjectFileBinary.IsLeafTag(tag);

        private static bool IsNodeTag(byte tag) => ProjectFileBinary.IsNodeTag(tag);

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
                "scorePlayerGroups" => WalkPlayerGroups(count),
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
                    accidental?.Steps,
                    // The note's own logical-stave override, straight out of the event: -1 in the file means "none",
                    // and a note that never stated the leaf is -1 here too (NoteDraft's default), so nothing is
                    // reported for either case.
                    note.Stave < 0 ? null : note.Stave));
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
                    player.IsSoloist,
                    player.SoloPlayer,
                    [.. player.Instruments.Select(instrument => new ProjectFileInstrument(
                        (int)instrument.Id,
                        instrument.EntityId,
                        DisplayName(instrument.Name),
                        instrument.Staves,
                        instrument.Range))]))],
                [.. _layouts.Select(layout => new ProjectFileLayout(
                    (int)layout.Id, (int)layout.Number, DisplayName(layout.Name), layout.Type))],
                [.. _playerGroups.Select(group => new ProjectFilePlayerGroup(
                    (int)group.Id,
                    DisplayName(group.Name),
                    DisplayName(group.ShortName),
                    [.. group.PlayerIds.Select(id => (int)id)],
                    group.InsertionIndex))],
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
                        layout.TranspositionType,
                        layout.Condensing,
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

    /// <summary>One player group as the file writes it.</summary>
    private sealed class PlayerGroupDraft
    {
        public long Id = -1;
        public string? Name;
        public string? ShortName;
        public int InsertionIndex;
        public List<long> PlayerIds { get; } = [];
    }

    private sealed class PlayerDraft
    {
        public long Id;
        public string? Name;
        public string? ShortName;
        public bool? IsSoloist;

        /// <summary>
        /// The player's kind, as the file's own <c>soloPlayer</c> leaf states it: true for a solo (single) player,
        /// false for a section player. Kept raw here — what the report calls it is the report's business.
        /// </summary>
        public bool? SoloPlayer;
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
        public string? TranspositionType;
        public bool? Condensing;
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

        /// <summary>
        /// The note event's own <c>logicalStaveIDOverride</c>: <c>-1</c> when the file states none, and the logical
        /// stave id the note is put on otherwise.
        /// </summary>
        /// <remarks>
        /// Measured on the corpus fixture, whose Piano part has two staves: all three notes read <c>-1</c>; with a
        /// selection made by <c>Edit.SelectAll</c> in Write mode (<c>status.hasSelection</c> became true),
        /// <c>Edit.CrossStaffBelow</c> turned the two notes that were on the upper stave into <c>1</c>,
        /// <c>Edit.CrossStaffAbove</c> turned all three into <c>0</c>, and <c>Edit.CrossStaffReset</c> put all three
        /// back to <c>-1</c>, with the events' own <c>stave</c>/voice streams untouched. So this leaf is what the
        /// file stores about cross-staff notation, and the default here is the file's own "none" rather than zero,
        /// so a note that never stated the leaf cannot look like a note on stave 0.
        /// </remarks>
        public int Stave = -1;

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
