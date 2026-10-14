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
/// <remarks>
/// <see cref="ScoreData"/> keeps the <c>score.dtn</c> member's bytes, so <c>file read --tree</c> can walk the very
/// same records generically after the typed reader has had its pass. A project file is a couple of megabytes.
/// </remarks>
internal sealed record ProjectFile(
    string Path,
    long Bytes,
    ProjectFileContainer Container,
    IReadOnlyList<ProjectFileMember> Members,
    ProjectFileMetadata Metadata,
    ProjectFileScore Score,
    byte[] ScoreData);

/// <summary>What <c>score.dtn</c> holds, decoded.</summary>
internal sealed record ProjectFileScore(
    string? FileVersion,
    int NameCount,
    int ValueCount,
    long RecordCount,
    ProjectFileScoreInfo Info,
    IReadOnlyList<ProjectFilePlayer> Players,
    IReadOnlyList<ProjectFileLayout> Layouts,
    IReadOnlyList<ProjectFilePlayerGroup> PlayerGroups,
    IReadOnlyList<ProjectFileFlow> Flows,
    IReadOnlyList<ProjectFileStave> Staves,
    IReadOnlyList<ProjectFileMeter> Meters,
    IReadOnlyList<ProjectFileNote> Notes,
    IReadOnlyList<ProjectFileDynamic> Dynamics,
    IReadOnlyList<ProjectFileDynamicGroup> DynamicGroups,
    IReadOnlyList<ProjectFileSlur> Slurs,
    IReadOnlyList<ProjectFileSlurElement> SlurElements,
    IReadOnlyList<ProjectFileNoteMark> NoteMarks,
    IReadOnlyList<ProjectFileTechniqueEvent> TechniqueEvents,
    IReadOnlyList<ProjectFileTechniqueDefinition> TechniqueDefinitions,
    IReadOnlyList<ProjectFileTechniqueAppearance> TechniqueAppearances,
    ProjectFileLayoutData Layout,
    ProjectFileLyrics Lyrics,
    int LastMeasure,
    IReadOnlyList<ProjectFileEventAddress> EventAddresses,
    bool EventAddressesTruncated);

/// <summary>
/// One event of a flow, as the address a command needs rather than as music: the file's own record name for it, the
/// flow and block it lives in, its <c>eventID</c>, and the two fields every event of one of these kinds states —
/// <c>position</c> and <c>duration</c>, in crotchets, exactly as the file writes them.
/// </summary>
/// <remarks>
/// This is the reverse lookup the dogfood run did by hand with <c>file read --tree block --depth 3</c>, made a
/// reading: the block an event lives in is what <c>EventEdit.Select</c> and the <c>Address</c> parameter need, and
/// nothing in the file says it beside the event. The address is the event's own triple
/// (<c>flowID,eventBlockID,eventID</c>); <see cref="Measure"/> and <see cref="Beat"/> are derived from the metre,
/// and the player, instrument, stave and voice come from the stream the block is placed on, so a reader can tell
/// that the event at the address is the one they meant (m59's trill is the Piccolo's, not the Timpani's).
/// </remarks>
/// <param name="Record">The file's own node name for the event, e.g. <c>TrillEventDefinition</c>.</param>
/// <param name="Stream">The stream the block's <c>blockInstance</c> names, or -1 when the file names none.</param>
/// <param name="StreamKind">The block's <c>parentEventStreamType</c>, e.g. <c>kGlobalTimebaseStream</c>.</param>
/// <param name="Position">The event's position in crotchets, or null when the event states none.</param>
/// <param name="Duration">The event's duration in crotchets, or null when the event states none — a bar-division or
/// tempo event has a position and no length, and null is that fact rather than a zero.</param>
/// <param name="Measure">The measure the position falls in, or null when there is no position to place.</param>
internal sealed record ProjectFileEventAddress(
    string Record,
    long FlowId,
    long BlockId,
    long Stream,
    string? StreamKind,
    long EventId,
    string? Position,
    string? Duration,
    int? Measure,
    string? Beat,
    string? Player,
    string? Instrument,
    int? Stave,
    int? Voice);

/// <summary>
/// Where a notated event sits, in the terms every event of a flow shares: its own position and sounding duration,
/// the measure and beat derived from the metre, and the player, instrument, stave and voice of its stream.
/// </summary>
internal sealed record ProjectFileLocation(
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

/// <summary>
/// One slur, as the file writes it: an event with a position and a length, plus the element it shares with the other
/// slurs of the same object (a slur over a chord is one element with one event per notehead).
/// </summary>
internal sealed record ProjectFileSlur(
    ProjectFileLocation Location,
    long? Element,
    int Noteheads,
    string? Direction,
    int? EndVoice,
    IReadOnlyList<ProjectFileTreeField> Fields);

/// <summary>
/// A slur element: the shared definition the slur events point at. Its only content is the list of the events that
/// reference it, which is the other end of the same link.
/// </summary>
internal sealed record ProjectFileSlurElement(long Id, IReadOnlyList<long> Events);

/// <summary>
/// One decoration the file stores as a property on a note event: an articulation, a tie, a beam break, a playing
/// technique, or a property this reader does not model. <see cref="Name"/> is derived from the score's own
/// playing-technique catalogue and is null when the catalogue has no entry for the value.
/// </summary>
internal sealed record ProjectFileNoteMark(
    ProjectFileLocation Location,
    string Family,
    string Property,
    string? Value,
    string? Name);

/// <summary>
/// A playing technique placed in the music as its own event. The file writes the <i>appearance</i>
/// (<c>pta.*</c>, e.g. the text "pizz"); the appearance's own definition names the technique (<c>pt.*</c>) it shows,
/// and the score's playing-technique catalogue names that.
/// </summary>
internal sealed record ProjectFileTechniqueEvent(
    ProjectFileLocation Location,
    string? AppearanceId,
    string? AppearanceType,
    string? TechniqueId,
    string? TechniqueName,
    string? Text,
    string? AppearanceName,
    IReadOnlyList<ProjectFileTreeField> Fields);

/// <summary>One entry of the score's own playing-technique catalogue (<c>pt.accent</c> → "Accent").</summary>
internal sealed record ProjectFileTechniqueDefinition(
    string EntityId,
    string? Name,
    string? ArticulationType,
    string? GroupType,
    string? AliasFor);

/// <summary>One entry of the score's own playing-technique appearance catalogue (<c>pta.pizzText</c>).</summary>
internal sealed record ProjectFileTechniqueAppearance(
    string EntityId,
    string? Name,
    string? Text,
    string? TechniqueId,
    string? Type,
    string? Category,
    string? Language);

/// <summary>What the file says about layout: the page boxes and their frames. Not which pages a measure lands on.</summary>
internal sealed record ProjectFileLayoutData(
    IReadOnlyList<ProjectFileLayoutPage> Layouts,
    IReadOnlyList<ProjectFilePageSet> PageSets,
    int FrameChainOverrides);

/// <summary>One layout's page setup, in the file's own units (points — see the report's caveats).</summary>
/// <param name="TranspositionType">
/// The layout's view state: <c>kScoreInC</c> for a concert-pitch layout and <c>kTransposingScore</c> for one that
/// shows transposing instruments at written pitch. This is the stored half of "enable the transposing view"
/// (checklist item 6), and it is the field <c>options get --type layout --filter transpositionType</c> reads live.
/// </param>
/// <param name="Condensing">
/// The layout's <c>enableCondensing</c>, which is the stored half of "enable the optimised score" (checklist item
/// 5) and the same fact the live status reports as <c>condensingEnabled</c>. Null when the file states none.
/// </param>
internal sealed record ProjectFileLayoutPage(
    int LayoutId,
    string? Name,
    string? Type,
    int Number,
    string? PageSet,
    string? PageWidth,
    string? PageHeight,
    string? SpaceSize,
    string? TranspositionType,
    bool? Condensing,
    IReadOnlyList<ProjectFileTreeField> Margins,
    IReadOnlyList<ProjectFileTreeField> CastingOff,
    int FrameChainOverrides);

/// <summary>A page template from the library: which frames each page of a page pair holds.</summary>
internal sealed record ProjectFilePageSet(
    string? EntityId,
    string? Name,
    string? Type,
    IReadOnlyList<long> MusicChains,
    IReadOnlyList<long> TextChains,
    IReadOnlyList<ProjectFileFrame> Frames);

/// <summary>One frame of a page template, with the constraints that place it on the page.</summary>
internal sealed record ProjectFileFrame(
    string Kind,
    string Side,
    long? Chain,
    bool? LayoutSpecific,
    int? Index,
    bool? ContentRelative,
    IReadOnlyList<ProjectFileTreeField> Position,
    IReadOnlyList<ProjectFileTreeField> Size);

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
/// <param name="IsSoloist">
/// The file's own <c>isSoloist</c> on the <c>scorePlayer</c> node: the flag that marks a player as a *soloist* in
/// front of its section, which is a different thing from the player's kind. Measured: a section string player wrote
/// <c>false</c>, and <c>Project.Player.SetAsSoloist?PlayerID=&amp;Set=true</c> turned it into <c>true</c> and renamed
/// the player to <c>Solo Violin</c> in the saved file; the same command with <c>Set=false</c> left it <c>true</c>.
/// Null when the file states none.
/// </param>
/// <param name="SoloPlayer">
/// The file's own <c>soloPlayer</c> on the same node, which is the leaf that separates the two kinds of player
/// Dorico's API names in its own catalogue (<c>Project.SoloPlayer.New</c> against <c>Project.SectionPlayer.New</c>):
/// measured, a 44-player orchestral import writes <c>true</c> for every single-instrument wind, brass, percussion
/// and keyboard player and <c>false</c> for the four string section players, so a player that came in as the wrong
/// kind is exactly a player whose <c>soloPlayer</c> disagrees with its neighbours. It is <em>not</em> the same flag
/// as <c>isSoloist</c>: in that same file every one of the 44 players wrote <c>isSoloist: false</c>, including the
/// one that is a solo player where a section belongs. Null when the file states none. The report derives
/// <c>playerType</c> from this leaf; see its caveat for what that derivation rests on.
/// </param>
internal sealed record ProjectFilePlayer(
    int Id,
    string? Name,
    string? ShortName,
    bool? IsSoloist,
    bool? SoloPlayer,
    IReadOnlyList<ProjectFileInstrument> Instruments);

internal sealed record ProjectFileInstrument(
    int Id,
    string? EntityId,
    string? Name,
    int Staves,
    string? Range);

/// <summary>
/// One player group: the name and abbreviation a score gives a section of players.
/// </summary>
/// <param name="Id">The file's own <c>groupID</c>. Measured: a 34-player project writes 0 to 4 plus one placeholder at -1.</param>
/// <param name="Name">The group's full name, e.g. <c>Woodwinds</c>.</param>
/// <param name="ShortName">The group's abbreviation, e.g. <c>Ww.</c> — the one a staff label prints.</param>
/// <param name="PlayerIds">The file's player entity ids in this group, in the order the group wrote them.</param>
/// <param name="InsertionIndex">Where the group sits in the score's own order.</param>
internal sealed record ProjectFilePlayerGroup(
    int Id,
    string? Name,
    string? ShortName,
    IReadOnlyList<int> PlayerIds,
    int InsertionIndex);

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
/// <remarks>
/// <see cref="Pitch"/> is derived from <see cref="Midi"/>. <see cref="Spelling"/> is not derived: it is the letter
/// the file's own <c>kNoteSpelling</c> property states for this note, and it is null when the event states none.
/// <see cref="Accidental"/> is derived from those two together — the file stores no accidental, but the letter and
/// the sounding pitch leave exactly one, and that derivation was checked against Dorico's own MusicXML export
/// (its <c>&lt;alter&gt;</c>) on a project that has one. It is null when the two do not agree on a written pitch,
/// which is what happens to unpitched percussion: there the MIDI value is a drum-map position, not a pitch.
/// </remarks>
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
    int? Voice,
    string? Spelling,
    string? Accidental,
    int? AccidentalSteps,
    /// <summary>
    /// The file's own <c>logicalStaveIDOverride</c> for this note: null when the file states none (the leaf is
    /// <c>-1</c>), and the logical stave id the note is drawn on otherwise. This is where cross-staff notation
    /// lives — measured, and the reason the contract has grown a field rather than a note in the caveats.
    /// </summary>
    int? StaveOverride = null);

/// <summary>
/// One dynamic marking placed in the music: the events carry <i>where</i> (and which group), the group definition
/// carries <i>what</i>. <see cref="Position"/> is an exact rational in crotchets, like a note's.
/// </summary>
internal sealed record ProjectFileDynamic(
    long EventId,
    int Measure,
    string Beat,
    string Position,
    string Duration,
    long Group,
    int? Stream,
    string? Player,
    string? Instrument,
    int? Stave,
    int? Voice,
    IReadOnlyList<ProjectFileDynamicMark> Marks);

/// <summary>
/// One part of a dynamic group: an immediate change of intensity (<c>p</c>, <c>ff</c>, …), a gradual one (a hairpin),
/// a force (<c>fz</c>) or a combination. <see cref="Kind"/> is the file's own node name; <see cref="Mark"/> is the
/// notation this reader derives from it, and is null when the shape is not one this reader has seen. Every value is
/// reported as the file writes it — see <see cref="Mark"/> for the derived half.
/// </summary>
/// <remarks>
/// <see cref="Position"/> is the offset inside the dynamic group, which is how the file writes it. <see cref="At"/>,
/// <see cref="Measure"/> and <see cref="Beat"/> are that offset resolved against the event that placed the group —
/// where the mark actually sits in the score. They are all null for a group reported on its own.
/// </remarks>
internal sealed record ProjectFileDynamicMark(
    string Kind,
    string? Mark,
    string? Position,
    string? At,
    int? Measure,
    string? Beat,
    string? Duration,
    string? Intensity,
    int? IntensityLevel,
    string? Direction,
    string? Subito,
    IReadOnlyList<ProjectFileTreeField> Fields);

/// <summary>A dynamic group as the file defines it, before the events that place it are resolved.</summary>
internal sealed record ProjectFileDynamicGroup(
    long Id,
    string? Events,
    IReadOnlyList<ProjectFileDynamicMark> Marks);

/// <summary>
/// The lyric element tables of a flow. Lyrics are elements in a table rather than events on notes, and a table with
/// nothing in it is a leaf, so presence and size are both readable even when the shape of one lyric is not.
/// <para>
/// <see cref="Read"/> and <see cref="Events"/> are the attempt to read one, anchored on the field names the
/// application itself uses for lyrics (see <c>--lyrics</c> caveats): every file of the reference corpus has an empty
/// lyric table, so what follows was written from those field names with no file to try it on —
/// <c>ProjectFileCheck</c> reads one that does have lyrics and asserts what comes out. <see cref="Shapes"/> stays
/// exactly what it was — the ordered leaf names of each element — so a file that <i>does</i> have lyrics is readable
/// even where the interpretation below is a guess. <see cref="EventTotal"/> is the number of lyric events found,
/// which <see cref="Events"/> may cap.
/// </para>
/// </summary>
internal sealed record ProjectFileLyrics(
    int Tables,
    int Elements,
    IReadOnlyList<IReadOnlyList<string>> Shapes,
    IReadOnlyList<ProjectFileLyricElement> Read,
    IReadOnlyList<ProjectFileLyricEvent> Events,
    int EventTotal);

/// <summary>
/// One leaf of a lyric element, as the file writes it. <c>Value</c> is the raw text and is null for a leaf with no
/// value (the shape a collection has when it is empty).
/// </summary>
internal sealed record ProjectFileLyricField(string Key, string? Value);

/// <summary>
/// One lyric element, read as far as its own field names allow. Which key held the id and which the text is
/// reported (<see cref="IdKey"/>, <see cref="TextKey"/>) because the exact field a Dorico lyric writes its text in
/// was never seen: the names came from the application, not from data.
/// </summary>
internal sealed record ProjectFileLyricElement(
    long? Id,
    string? IdKey,
    string? Text,
    string? TextKey,
    string? SyllableType,
    string? LineNumber,
    string? LineLocation,
    string? LineType,
    IReadOnlyList<ProjectFileLyricField> Fields,
    IReadOnlyList<string> ChildKinds,
    IReadOnlyList<ProjectFileLyricField> Properties);

/// <summary>
/// One lyric placed in the music, resolved the way a dynamic is: exact position in crotchets, the measure and beat
/// that position falls on, and the player/stave the stream belongs to. <see cref="Element"/> is the
/// <c>lyricElementID</c> the event points at, -1 when the event carries none. <see cref="Fields"/> keeps every other
/// leaf the event wrote, because which side of the element/event pair holds the sung text is one of the things no
/// file here can settle.
/// </summary>
internal sealed record ProjectFileLyricEvent(
    long EventId,
    int Measure,
    string Beat,
    string Position,
    string Duration,
    long Element,
    int? Stream,
    string? Player,
    string? Instrument,
    int? Stave,
    int? Voice,
    IReadOnlyList<ProjectFileLyricField> Fields);
