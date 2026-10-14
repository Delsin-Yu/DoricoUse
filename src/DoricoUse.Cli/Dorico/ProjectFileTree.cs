using System.Text;

namespace DoricoUse.Cli.Dorico;

/// <summary>
/// The part of a <c>score.dtn</c> member that comes <i>before</i> the token stream: the 12-byte header, the name table
/// and the value pool. Both the typed reader (<see cref="ProjectFileScoreReader"/>) and the generic tree walker
/// (<see cref="ProjectFileTreeReader"/>) have to find that stream the same way, so the search lives here once.
/// </summary>
/// <remarks>
/// A record's first byte carries the kind in bit 1 (0 = leaf, 1 = node) and a per-file marker in the rest, which is
/// what makes a wrong start position detectable at all.
/// </remarks>
internal static class ProjectFileBinary
{
    public const int HeaderBytes = 12;

    /// <summary>Everything but the kind bit and the variant bit: the marker a whole file shares.</summary>
    public const byte MarkerMask = 0xFC;

    public const byte KindBit = 0x02;

    /// <summary>How many start positions are tried before the member is declared unreadable.</summary>
    public const int MaxCandidates = 64;

    /// <summary>How deep the record tree may nest. A real project stays around twelve levels.</summary>
    public const int MaxDepth = 256;

    /// <summary>The highest value-pool index a member may reference; a guard against a nonsense index.</summary>
    public const int MaxPoolEntries = 4_000_000;

    public static uint FormatVersion(byte[] data) => ReadUInt32(data, 0);

    public static uint ReadUInt32(byte[] data, int offset)
        => (uint)(data[offset] | (data[offset + 1] << 8) | (data[offset + 2] << 16) | (data[offset + 3] << 24));

    /// <summary>The name table: null-terminated UTF-8 identifiers up to the first empty one.</summary>
    public static List<string> ReadNames(byte[] data, int start, out int end)
    {
        var names = new List<string>(4096);
        var position = start;

        while (position < data.Length && names.Count < 1_000_000)
        {
            var terminator = Array.IndexOf(data, (byte)0, position);

            if (terminator < 0)
            {
                break;
            }

            if (terminator == position)
            {
                end = position + 1;

                return names;
            }

            names.Add(Encoding.UTF8.GetString(data, position, terminator - position));
            position = terminator + 1;
        }

        end = position;

        return names;
    }

    public static int SkipNulls(byte[] data, int position)
    {
        while (position < data.Length && data[position] == 0)
        {
            position++;
        }

        return position;
    }

    /// <summary>How many null-terminated entries a value pool holds in <c>[start, end)</c>.</summary>
    public static int CountTokens(byte[] data, int start, int end)
    {
        var count = 0;
        var position = start;

        while (position < end)
        {
            var terminator = Array.IndexOf(data, (byte)0, position, end - position);

            if (terminator < 0)
            {
                break;
            }

            count++;
            position = terminator + 1;
        }

        return count;
    }

    /// <summary>
    /// Every position where the token stream could start: a node record whose key is <c>kScore</c> — name index 0,
    /// the first entry of the name table, which is what the file's first byte says — and whose <c>aux</c> is 0.
    /// </summary>
    public static IEnumerable<int> Candidates(byte[] data, int poolStart)
    {
        for (var position = poolStart; position + 2 < data.Length; position++)
        {
            if ((data[position] & KindBit) != 0 && data[position + 1] == 0 && data[position + 2] == 0)
            {
                yield return position;
            }
        }
    }

    /// <summary>
    /// A record starts with one byte whose <b>bit 1</b> says what it is: 0 = a leaf (<c>key value</c>), 1 = a node
    /// (<c>key aux count ids[count]</c>). Bit 0 is a variant, and the remaining bits are a marker that stays the same
    /// for the whole file — <c>0x90</c> in <i>Bad Apple!!</i>, <c>0x10</c> in <i>《小城日常》</i>, <c>0x08</c> in
    /// <i>畜生界传说</i> — so every project seen so far uses four tag bytes.
    /// </summary>
    public static bool IsLeafTag(byte tag) => (tag & KindBit) == 0;

    public static bool IsNodeTag(byte tag) => (tag & KindBit) != 0;

    /// <summary>A tag that belongs to the file being read, given that file's marker.</summary>
    public static bool IsRecordTag(byte tag, byte marker) => (tag & MarkerMask) == marker;
}

/// <summary>One leaf of a node: the field's name and its value as the file writes it.</summary>
internal sealed record ProjectFileTreeField(string Key, string? Value);

/// <summary>
/// One node of <c>score.dtn</c>'s record tree, decoded generically — the escape hatch that lets an agent read a part
/// of the file this CLI does not model. <see cref="Fields"/> and <see cref="Children"/> are capped by the caller's
/// limit; <see cref="FieldCount"/> and <see cref="ChildNodes"/> are the real totals, and <see cref="Truncated"/> says
/// whether anything was left out.
/// </summary>
internal sealed record ProjectFileTreeNode(
    string Name,
    string Path,
    int FieldCount,
    int ChildNodes,
    bool Truncated,
    IReadOnlyList<ProjectFileTreeField> Fields,
    IReadOnlyList<ProjectFileTreeNode> Children);

/// <summary>One name the tree uses — a node kind or a leaf key — and how often it occurs.</summary>
internal sealed record ProjectFileTreeNodeName(string Name, string Kind, long Count);

/// <summary>
/// Every distinct name a whole <c>score.dtn</c> uses — the answer to "what does this file hold?", one call, without
/// a new release of this tool. Nodes and leaf keys are both listed and told apart.
/// </summary>
internal sealed record ProjectFileTreeCensus(
    long Nodes,
    long Leaves,
    int NodeNames,
    int LeafNames,
    bool Truncated,
    IReadOnlyList<ProjectFileTreeNodeName> Names);

/// <summary>Every node in the file whose name was asked for, up to the caller's caps.</summary>
/// <param name="Limit">How many records were listed at most: matched nodes, and children of each matched node.</param>
/// <param name="FieldLimit">
/// How many fields of each record were listed at most, or -1 for all of them. A cap of its own because the record
/// cap and the field cap are two different questions, and one number answering both is what made a reader conclude
/// an event had no <c>duration</c> field: measured 2026-10-13, reading <c>GradualTempoChangeEventDefinition</c> with
/// <c>--tree-limit 2</c> showed the first two fields of each event and hid <c>duration</c>, which the file does hold.
/// </param>
internal sealed record ProjectFileTreeSubtree(
    string Query,
    int Matched,
    int Returned,
    int Depth,
    int Limit,
    int FieldLimit,
    int ValueChars,
    long Nodes,
    long Leaves,
    bool Truncated,
    IReadOnlyList<ProjectFileTreeNode> Occurrences,
    IReadOnlyList<ProjectFileTreeNodeName> Similar);

/// <summary>
/// Dumps the decoded record tree of a <c>score.dtn</c> member: a census of every name it uses, or every occurrence of
/// one node name with a bounded view of its fields and children.
/// </summary>
/// <remarks>
/// <para>
/// This is the exploration half of <c>file read</c>. The typed reader knows notes, staves, metres and dynamics; this
/// walker knows nothing about music at all — it walks the same record stream and reports names, values, counts and
/// nesting, so an agent can find out what a project holds even when this CLI has no code for it, and can hand the
/// shape back instead of asking for a new release.
/// </para>
/// <para>
/// The output is bounded by construction: occurrences per name, children per node and the length of a value are
/// capped, and the caps are reported as numbers so nothing has to be inferred. A record's <b>fields</b> are the one
/// thing not capped with the records: measured 2026-10-13, one number bounding both is what made a reader report
/// that <c>GradualTempoChangeEventDefinition</c> has no <c>duration</c> field, because <c>--tree-limit 2</c> showed
/// the first two fields of each event. <c>--tree-fields</c> bounds the fields, and its default is every one of them.
/// The tree itself is always walked in full, because the walk is what validates the stream: a candidate start
/// position is accepted only if the walk ends exactly on the member's last byte, the root is a <c>kScore</c>, and
/// every value index points into the pool. A file this walker cannot decode reports <c>UnsupportedScoreData</c>,
/// never invented structure.
/// </para>
/// </remarks>
internal static class ProjectFileTreeReader
{
    /// <summary>How much of a long value is kept. A pool value can be an embedded structure, hundreds of characters long.</summary>
    public const int ValueChars = 256;

    /// <summary>Every distinct name in the file, with how often it occurs.</summary>
    public static ProjectFileTreeCensus Census(byte[] data, int limit)
    {
        var walker = NewWalker(data);
        var names = new Dictionary<(string Name, bool Node), long>();
        var tally = new Tally();

        if (!walker.Scan((rootKey, count) =>
            {
                // The root is a node of the tree like any other, and it is the one name every file has.
                tally.Nodes++;
                Bump(names, walker.Name(rootKey), node: true);

                return Count(walker, count, names, tally);
            }))
        {
            throw Unsupported(walker);
        }

        var ordered = Order(names);
        var returned = limit < 0 ? ordered.Count : Math.Min(limit, ordered.Count);

        return new ProjectFileTreeCensus(
            tally.Nodes,
            tally.Leaves,
            names.Count(pair => pair.Key.Node),
            names.Count(pair => !pair.Key.Node),
            returned < ordered.Count,
            [.. ordered.Take(returned)]);
    }

    /// <summary>
    /// Every node whose name is <paramref name="query"/> (case-insensitive, exact — no globbing), each with its path
    /// from the root and up to <paramref name="depth"/> levels of children.
    /// </summary>
    /// <param name="limit">How many matched nodes to list, and how many children of each — the record cap.</param>
    /// <param name="fieldLimit">
    /// How many fields of each listed record to keep, or -1 for every one of them. It is separate from
    /// <paramref name="limit"/> because a field is not a record: the two used to share one number, and measured
    /// 2026-10-13 a reader who passed <c>--tree-limit 2</c> saw two fields per event and concluded that
    /// <c>GradualTempoChangeEventDefinition</c> has no <c>duration</c>. It does.
    /// </param>
    public static ProjectFileTreeSubtree Subtree(byte[] data, string query, int depth, int limit, int fieldLimit = -1)
    {
        var walker = NewWalker(data);
        var occurrences = new List<ProjectFileTreeNode>();
        var tally = new Tally();

        if (!walker.Scan((rootKey, count) => Match(
            walker,
            rootKey,
            count,
            string.Empty,
            query,
            depth,
            limit,
            fieldLimit,
            occurrences,
            tally)))
        {
            throw Unsupported(walker);
        }

        var returned = limit < 0 ? occurrences.Count : Math.Min(limit, occurrences.Count);

        return new ProjectFileTreeSubtree(
            query,
            tally.Matched,
            returned,
            depth,
            limit,
            fieldLimit,
            ValueChars,
            tally.Nodes,
            tally.Leaves,
            returned < tally.Matched,
            [.. occurrences.Take(returned)],
            tally.Matched == 0 ? Similar(walker, query) : []);
    }

    // ---------------------------------------------------------------- walking

    private static Walker NewWalker(byte[] data)
    {
        if (data.Length < ProjectFileBinary.HeaderBytes + 4)
        {
            throw Unsupported("the member is shorter than its header");
        }

        var names = ProjectFileBinary.ReadNames(data, ProjectFileBinary.HeaderBytes, out var afterNames);

        if (names.Count == 0)
        {
            throw Unsupported("the name table is empty");
        }

        return new Walker(data, names, ProjectFileBinary.SkipNulls(data, afterNames));
    }

    /// <summary>Counts every name in the tree, and how many nodes and leaves the whole member holds.</summary>
    private static bool Count(
        Walker walker,
        long count,
        Dictionary<(string Name, bool Node), long> names,
        Tally tally)
        => walker.Children(
            count,
            (_, key, _) =>
            {
                tally.Leaves++;
                Bump(names, walker.Name(key), node: false);

                return true;
            },
            (_, key, children) =>
            {
                tally.Nodes++;
                Bump(names, walker.Name(key), node: true);

                return Count(walker, children, names, tally);
            });

    private static void Bump(Dictionary<(string Name, bool Node), long> names, string? name, bool node)
    {
        var key = (name ?? "(unnamed)", node);
        names[key] = names.GetValueOrDefault(key) + 1;
    }

    private static List<ProjectFileTreeNodeName> Order(Dictionary<(string Name, bool Node), long> names)
        => [.. names
            .OrderByDescending(pair => pair.Value)
            .ThenBy(pair => pair.Key.Name, StringComparer.Ordinal)
            .Select(pair => new ProjectFileTreeNodeName(pair.Key.Name, pair.Key.Node ? "node" : "leaf", pair.Value))];

    /// <summary>
    /// One node: taken if its name is the query, descended into otherwise. <paramref name="parentPath"/> is empty for
    /// the root, which is a candidate like any other node.
    /// </summary>
    private static bool Match(
        Walker walker,
        long key,
        long count,
        string parentPath,
        string query,
        int depth,
        int limit,
        int fieldLimit,
        List<ProjectFileTreeNode> occurrences,
        Tally tally)
    {
        tally.Nodes++;

        var name = walker.Name(key) ?? "(unnamed)";

        if (name.Equals(query, StringComparison.OrdinalIgnoreCase))
        {
            tally.Matched++;

            if (limit < 0 || occurrences.Count < limit)
            {
                occurrences.Add(Capture(walker, count, parentPath, name, depth, limit, fieldLimit));
            }
            else
            {
                walker.SkipChildren(count);
            }

            return true;
        }

        return Find(
            walker,
            count,
            parentPath.Length == 0 ? name : parentPath + "/" + name,
            query,
            depth,
            limit,
            fieldLimit,
            occurrences,
            tally);
    }

    /// <summary>Finds every node with the query name, descending through everything that is not one.</summary>
    private static bool Find(
        Walker walker,
        long count,
        string path,
        string query,
        int depth,
        int limit,
        int fieldLimit,
        List<ProjectFileTreeNode> occurrences,
        Tally tally)
        => walker.Children(
            count,
            (_, _, _) =>
            {
                tally.Leaves++;

                return true;
            },
            (_, key, children) => Match(walker, key, children, path, query, depth, limit, fieldLimit, occurrences, tally));

    /// <summary>A matched node: its own fields, and its child nodes down to the depth budget.</summary>
    private static ProjectFileTreeNode Capture(
        Walker walker,
        long count,
        string parentPath,
        string name,
        int depth,
        int limit,
        int fieldLimit)
    {
        var path = parentPath.Length == 0 ? name : parentPath + "/" + name;
        var fields = new List<ProjectFileTreeField>();
        var children = new List<ProjectFileTreeNode>();
        var fieldCount = 0;
        var childCount = 0;

        walker.Children(
            count,
            (_, key, value) =>
            {
                fieldCount++;

                // A field is not a record, so the record cap must not reach it: the number the caller gives for
                // records is not a statement about how many fields to show, and truncating the two together is what
                // hid `duration` from a reader who passed a small --tree-limit.
                if (fieldLimit < 0 || fields.Count < fieldLimit)
                {
                    fields.Add(new ProjectFileTreeField(walker.Name(key) ?? "(unnamed)", Trim(walker.Value(value))));
                }

                return true;
            },
            (_, key, inner) =>
            {
                childCount++;

                if (depth > 0 && (limit < 0 || children.Count < limit))
                {
                    children.Add(Capture(walker, inner, path, walker.Name(key) ?? "(unnamed)", depth - 1, limit, fieldLimit));
                }
                else
                {
                    walker.SkipChildren(inner);
                }

                return true;
            });

        return new ProjectFileTreeNode(
            name,
            path,
            fieldCount,
            childCount,
            fields.Count < fieldCount || children.Count < childCount || (depth == 0 && childCount > 0),
            fields,
            children);
    }

    private static string? Trim(string? value)
        => value is null || value.Length <= ValueChars ? value : value[..ValueChars] + "…";

    /// <summary>
    /// Names that look like the one that was asked for, with how often the whole file uses them. A wrong guess is the
    /// normal way to explore, so the answer to one is the neighbourhood instead of a bare "nothing found": the longest
    /// prefix of the guess that anything in the file matches, and then every name that carries that prefix.
    /// </summary>
    private static List<ProjectFileTreeNodeName> Similar(Walker walker, string query)
    {
        var names = new Dictionary<(string Name, bool Node), long>();
        var tally = new Tally();

        if (!walker.Scan((_, count) => Count(walker, count, names, tally)))
        {
            return [];
        }

        var needle = query.Trim();

        // Narrow as little as possible: a miss should show the honest neighbourhood, so the search falls back to
        // whatever the query shares with a name the file does use — "LyricEventDefinition" misses on every corpus
        // file, and "Lyric" is what is offered instead. (That name is not a guess: it is the application's own IO
        // type, next to LyricElementDefinition and LyricElementTableDefinition in Dorico6.exe, which is why the
        // lyric reader looks for it. See docs/hidden-commands.md for how the exe's vocabulary is read.)
        for (var length = needle.Length; length >= 3; length--)
        {
            var prefix = needle[..length];
            var matches = names
                .Where(pair => pair.Key.Name.Contains(prefix, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (matches.Count == 0)
            {
                continue;
            }

            return
            [
                .. matches
                    .OrderByDescending(pair => pair.Value)
                    .ThenBy(pair => pair.Key.Name, StringComparer.Ordinal)
                    .Take(20)
                    .Select(pair => new ProjectFileTreeNodeName(
                        pair.Key.Name,
                        pair.Key.Node ? "node" : "leaf",
                        pair.Value)),
            ];
        }

        return [];
    }

    private static CliException Unsupported(string why)
        => new(
            "UnsupportedScoreData",
            $"score.dtn is not a Dorico score this reader understands: {why}.",
            2);

    private static CliException Unsupported(Walker walker)
        => Unsupported(
            $"{walker.Names.Count} names and "
            + $"{ProjectFileBinary.CountTokens(walker.Data, walker.PoolStart, walker.Data.Length)} value-pool entries "
            + "were read, but no position yields a token stream that parses to the last byte with a kScore root");

    /// <summary>What the walk counted, as fields so that the lambdas can add to them.</summary>
    private sealed class Tally
    {
        public long Nodes;
        public long Leaves;
        public int Matched;
    }

    // ---------------------------------------------------------------- the walker itself

    /// <summary>
    /// One attempt at the whole member, generic: no music, just records. The same validation the typed reader applies
    /// is applied here, because the walk is what proves a candidate start position is the right one.
    /// </summary>
    private sealed class Walker
    {
        private readonly List<string> _pool = [];

        private int _pos;
        private int _depth;
        private byte _marker;
        private long _maxValue;
        private int _tried;

        public Walker(byte[] data, IReadOnlyList<string> names, int poolStart)
        {
            Data = data;
            Names = names;
            PoolStart = poolStart;
        }

        public byte[] Data { get; }

        public IReadOnlyList<string> Names { get; }

        public int PoolStart { get; }

        /// <summary>
        /// Finds the token stream and walks it whole, handing the root's key and child count to
        /// <paramref name="scan"/>. Returns false when no candidate start position parses to the last byte — the
        /// caller reports that.
        /// </summary>
        public bool Scan(Func<long, long, bool> scan)
        {
            _tried = 0;

            foreach (var candidate in ProjectFileBinary.Candidates(Data, PoolStart))
            {
                // A real score begins at the first position that parses to the end; trying a bounded number of
                // candidates keeps a file that is not a score from costing a quadratic scan.
                if (++_tried > ProjectFileBinary.MaxCandidates)
                {
                    break;
                }

                _pos = candidate;
                _marker = (byte)(Data[_pos] & ProjectFileBinary.MarkerMask);
                _maxValue = 0;
                _depth = 0;

                if (!ProjectFileBinary.IsNodeTag(Data[_pos])
                    || !ReadHeader(out var rootKey, out var count)
                    || Name(rootKey) != "kScore")
                {
                    continue;
                }

                PopulatePool(PoolStart, candidate);

                // The walk is the proof: it must end on the member's last byte, and every value index it used must
                // point into the pool that was read in front of the stream.
                if (scan(rootKey, count) && _pos == Data.Length && _maxValue <= _pool.Count)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>The value pool in front of the stream: null-terminated UTF-8, 1-based when referenced.</summary>
        private void PopulatePool(int from, int to)
        {
            _pool.Clear();

            var position = from;

            while (position < to && _pool.Count < ProjectFileBinary.MaxPoolEntries)
            {
                var terminator = Array.IndexOf(Data, (byte)0, position, Math.Max(to - position, 0));

                if (terminator < 0)
                {
                    break;
                }

                _pool.Add(Encoding.UTF8.GetString(Data, position, terminator - position));
                position = terminator + 1;
            }
        }

        public string? Name(long index)
            => index >= 0 && index < Names.Count ? Names[(int)index] : null;

        /// <summary>A value-pool entry, and the bookkeeping that proves the pool's length fits every index used.</summary>
        public string? Value(long value)
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

        /// <summary>
        /// A node's children in order. A leaf handler sees <c>(index, key, value)</c>; a node handler sees
        /// <c>(index, key, childCount)</c> and must consume the children — with <see cref="SkipChildren"/> or by
        /// recursing through this method. The index is the child's ordinal among all of the parent's children.
        /// </summary>
        public bool Children(
            long count,
            Func<int, long, long, bool>? onLeaf,
            Func<int, long, long, bool> onNode)
        {
            if (++_depth > ProjectFileBinary.MaxDepth)
            {
                _depth--;

                return false;
            }

            try
            {
                for (var index = 0; index < count; index++)
                {
                    if (_pos >= Data.Length)
                    {
                        return false;
                    }

                    if (ProjectFileBinary.IsLeafTag(Data[_pos]))
                    {
                        if (!ReadLeaf(out var key, out var value))
                        {
                            return false;
                        }

                        // Every value passes through Value(), so a leaf nobody looks at is still range-checked.
                        Value(value);

                        if (onLeaf is not null && !onLeaf((int)index, key, value))
                        {
                            return false;
                        }
                    }
                    else
                    {
                        if (!ReadHeader(out var key, out var children))
                        {
                            return false;
                        }

                        if (!onNode((int)index, key, children))
                        {
                            return false;
                        }
                    }
                }

                return true;
            }
            finally
            {
                _depth--;
            }
        }

        public bool SkipChildren(long count) => Children(count, null, (_, _, children) => SkipChildren(children));

        private bool ReadLeaf(out long key, out long value)
        {
            key = 0;
            value = 0;

            if (_pos >= Data.Length
                || !ProjectFileBinary.IsLeafTag(Data[_pos])
                || !ProjectFileBinary.IsRecordTag(Data[_pos], _marker))
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

            if (_pos >= Data.Length
                || !ProjectFileBinary.IsNodeTag(Data[_pos])
                || !ProjectFileBinary.IsRecordTag(Data[_pos], _marker))
            {
                return false;
            }

            _pos++;

            if (!TryVarint(out key) || !TryVarint(out _) || !TryVarint(out count))
            {
                return false;
            }

            if (count < 0 || count > Data.Length)
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

        private bool TryVarint(out long value)
        {
            value = 0;
            var shift = 0;

            while (_pos < Data.Length)
            {
                var current = Data[_pos++];

                if (shift > 62)
                {
                    return false;
                }

                value |= (long)(current & 0x7F) << shift;

                if ((current & 0x80) == 0)
                {
                    return true;
                }

                shift += 7;
            }

            return false;
        }
    }
}
