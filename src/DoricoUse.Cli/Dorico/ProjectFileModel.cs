namespace DoricoUse.Cli.Dorico;

/// <summary>
/// One member of a <c>.dorico</c> package.
/// </summary>
internal sealed record ProjectFileMember(string Path, long Bytes, long CompressedBytes);

/// <summary>
/// <c>META-INF/container.xml</c>: which member holds the project's own data.
/// </summary>
internal sealed record ProjectFileContainer(string? Version, string? RootPath, string? MediaType);

/// <summary>
/// The descriptive half of a project, read from <c>supplementary_data/scoreinfo/scoreinfo.xml</c>. That member is
/// plain XML, so it is the one part of the file that is readable without decoding anything.
/// </summary>
internal sealed record ProjectFileMetadata(
    string? Title,
    string? Subtitle,
    string? Composer,
    string? Arranger,
    string? Lyricist,
    string? Copyright,
    string? CreatedDate,
    string? LastSavedDate,
    string? CreatedVersion,
    string? LastSavedVersion,
    string? ProjectDuration,
    IReadOnlyList<string> Players,
    IReadOnlyList<string> FlowTitles,
    IReadOnlyList<string> FlowDurations);

/// <summary>A .dorico file on disk: its package, its descriptive XML, and — when it could be decoded — its score.</summary>
internal sealed record ProjectFile(
    string Path,
    long Bytes,
    ProjectFileContainer Container,
    IReadOnlyList<ProjectFileMember> Members,
    ProjectFileMetadata Metadata,
    ProjectFileScore Score);

/// <summary>What <c>score.dtn</c> holds, decoded.</summary>
internal sealed record ProjectFileScore(
    string? FileVersion,
    int NameCount,
    int ValueCount,
    long RecordCount,
    ProjectFileScoreInfo Info,
    IReadOnlyList<ProjectFilePlayer> Players,
    IReadOnlyList<ProjectFileLayout> Layouts,
    IReadOnlyList<ProjectFileFlow> Flows,
    IReadOnlyList<ProjectFileStave> Staves,
    IReadOnlyList<ProjectFileMeter> Meters,
    IReadOnlyList<ProjectFileNote> Notes,
    int LastMeasure);

/// <summary>The root object's <c>info</c> block.</summary>
internal sealed record ProjectFileScoreInfo(
    string? Title,
    string? Subtitle,
    string? Composer,
    string? Arranger,
    string? Lyricist,
    string? CreatedDate,
    string? LastSavedDate,
    string? CreatedVersion,
    string? LastSavedVersion,
    string? Originator);

/// <summary>A player (an instrumentalist) and the instruments it holds.</summary>
internal sealed record ProjectFilePlayer(
    int Id,
    string? Name,
    string? ShortName,
    IReadOnlyList<ProjectFileInstrument> Instruments);

internal sealed record ProjectFileInstrument(
    int Id,
    string? EntityId,
    string? Name,
    int Staves,
    string? Range);

/// <summary>A part or the full score: what Dorico prints and exports.</summary>
internal sealed record ProjectFileLayout(
    int Id,
    int Number,
    string? Name,
    string? Type);

internal sealed record ProjectFileFlow(
    int Id,
    string? Name,
    int Streams,
    int Blocks,
    int Notes);

/// <summary>
/// One note stream that notes were found in, named after the player it belongs to. The id is the stream id the
/// notes' blocks belong to.
/// </summary>
internal sealed record ProjectFileStave(
    int Stream,
    string? Kind,
    string? Player,
    string? Instrument,
    int? Stave,
    int? Voice,
    int Notes);

/// <summary>
/// One bar-tick that starts a new measure, with the metre in force from there. Positions are rational multiples of
/// a crotchet, exactly as the file stores them.
/// </summary>
internal sealed record ProjectFileMeter(
    int Bar,
    string Position,
    string TimeSignature,
    int Numerator,
    int Denominator);

/// <summary>
/// One notated note event. <see cref="Position"/> and <see cref="Duration"/> are exact rationals in crotchets;
/// <see cref="Measure"/> and <see cref="Beat"/> are derived from the metre.
/// </summary>
internal sealed record ProjectFileNote(
    long EventId,
    int Measure,
    string Beat,
    string Position,
    string Duration,
    int? Midi,
    string? Pitch,
    int? Stream,
    string? Player,
    string? Instrument,
    int? Stave,
    int? Voice);
