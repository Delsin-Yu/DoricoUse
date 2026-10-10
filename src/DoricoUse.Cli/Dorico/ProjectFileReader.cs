using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml.Linq;

namespace DoricoUse.Cli.Dorico;

/// <summary>
/// Reads a <c>.dorico</c> project FILE from disk — no Dorico, no host, no Remote Control API.
/// </summary>
/// <remarks>
/// <para>
/// A <c>.dorico</c> file is a ZIP package (the first four bytes are <c>PK\x03\x04</c>) whose
/// <c>META-INF/container.xml</c> names the one member that holds the project: <c>score.dtn</c>,
/// <c>media-type="application/vnd.steinberg.dorico-project"</c>. Alongside it are a second binary
/// (<c>scorelibrary.dtn</c>, the score library) and a handful of XML members, of which
/// <c>supplementary_data/scoreinfo/scoreinfo.xml</c> is the useful one: title, composer, players, flows.
/// </para>
/// <para>
/// Nothing about a score's notes is XML. Those live in <c>score.dtn</c>, which
/// <see cref="ProjectFileScoreReader"/> decodes. This class only opens the package and reads the XML members, so a
/// project whose binary cannot be decoded still reports its title, its players and its layouts' names.
/// </para>
/// </remarks>
internal static class ProjectFileReader
{
    /// <summary>The member <c>container.xml</c> points at when the file is a Dorico project.</summary>
    public const string ScoreMember = "score.dtn";

    /// <summary>The XML member Dorico writes the title page from.</summary>
    public const string ScoreInfoMember = "supplementary_data/scoreinfo/scoreinfo.xml";

    private const string ContainerMember = "META-INF/container.xml";

    public static ProjectFile Open(string path)
    {
        string full;

        try
        {
            full = Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new CliException("InvalidArgument", $"--path is not a usable path: {ex.Message}", 1);
        }

        if (!File.Exists(full))
        {
            throw new CliException("NotFound", $"No file at '{full}'.", 2);
        }

        long bytes;

        try
        {
            bytes = new FileInfo(full).Length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            bytes = 0;
        }

        using var archive = OpenArchive(full);

        var members = new List<ProjectFileMember>();
        var container = new ProjectFileContainer(null, null, null);
        ProjectFileMetadata? metadata = null;
        byte[]? scoreData = null;

        foreach (var entry in archive.Entries)
        {
            if (entry.FullName.EndsWith('/'))
            {
                continue;
            }

            members.Add(new ProjectFileMember(entry.FullName, entry.Length, entry.CompressedLength));

            if (entry.FullName.Equals(ContainerMember, StringComparison.OrdinalIgnoreCase))
            {
                container = ReadContainer(Read(entry));
            }
            else if (entry.FullName.Equals(ScoreInfoMember, StringComparison.OrdinalIgnoreCase))
            {
                metadata = ReadScoreInfo(Read(entry));
            }
            else if (entry.FullName.Equals(ScoreMember, StringComparison.OrdinalIgnoreCase))
            {
                scoreData = Read(entry);
            }
        }

        members.Sort(static (left, right) => string.CompareOrdinal(left.Path, right.Path));

        if (scoreData is null)
        {
            throw new CliException(
                "MissingScoreData",
                $"'{full}' is a ZIP package but has no '{ScoreMember}': {members.Count} members, none of them the "
                + "project. This is not a Dorico project file (a Dorico project always carries score.dtn).",
                2);
        }

        metadata ??= EmptyMetadata();

        return new ProjectFile(
            full,
            bytes,
            container,
            members,
            metadata,
            ProjectFileScoreReader.Read(scoreData),
            scoreData);
    }

    private static ZipArchive OpenArchive(string path)
    {
        try
        {
            return ZipFile.OpenRead(path);
        }
        catch (InvalidDataException ex)
        {
            throw new CliException(
                "NotAContainer",
                $"'{path}' is not a ZIP package, so it is not a Dorico project: {ex.Message}",
                2);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new CliException("NotFound", $"'{path}' could not be opened: {ex.Message}", 2);
        }
    }

    private static byte[] Read(ZipArchiveEntry entry)
    {
        try
        {
            using var stream = entry.Open();
            using var buffer = new MemoryStream(entry.Length > 0 && entry.Length < int.MaxValue ? (int)entry.Length : 0);
            stream.CopyTo(buffer);

            return buffer.ToArray();
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            throw new CliException(
                "MalformedMember",
                $"The member '{entry.FullName}' of the package could not be read: {ex.Message}",
                2);
        }
    }

    private static ProjectFileContainer ReadContainer(byte[] xml)
    {
        try
        {
            var document = XDocument.Load(new MemoryStream(xml));
            var root = document.Root;

            if (root is null)
            {
                return new ProjectFileContainer(null, null, null);
            }

            var rootFile = root.Descendants()
                .FirstOrDefault(element => element.Name.LocalName == "rootfile");

            return new ProjectFileContainer(
                Attribute(root, "version"),
                Attribute(rootFile, "full-path"),
                Attribute(rootFile, "media-type"));
        }
        catch (Exception ex) when (ex is System.Xml.XmlException or InvalidOperationException)
        {
            return new ProjectFileContainer(null, null, null);
        }
    }

    private static ProjectFileMetadata ReadScoreInfo(byte[] xml)
    {
        XElement root;

        try
        {
            var document = XDocument.Load(new MemoryStream(xml));
            root = document.Root ?? throw new System.Xml.XmlException("no root element");
        }
        catch (Exception ex) when (ex is System.Xml.XmlException or InvalidOperationException)
        {
            return EmptyMetadata();
        }

        var players = root.Descendants()
            .Where(element => element.Name.LocalName == "Player")
            .Select(element => Text(element, "name"))
            .Where(name => !string.IsNullOrEmpty(name))
            .Select(name => name!)
            .ToList();

        var flowTitles = new List<string>();
        var flowDurations = new List<string>();

        foreach (var flow in root.Descendants().Where(element => element.Name.LocalName == "Flow"))
        {
            flowTitles.Add(Text(flow, "title") ?? string.Empty);
            flowDurations.Add(Text(flow, "flowDuration") ?? string.Empty);
        }

        return new ProjectFileMetadata(
            Text(root, "title"),
            Text(root, "subtitle"),
            Text(root, "composer"),
            Text(root, "arranger"),
            Text(root, "lyricist"),
            Text(root, "copyright"),
            Text(root, "createdDate"),
            Text(root, "lastSavedDate"),
            Text(root, "createdVersion"),
            Text(root, "lastSavedVersion"),
            Text(root, "projectDuration"),
            players,
            flowTitles,
            flowDurations);
    }

    private static ProjectFileMetadata EmptyMetadata() => new(
        null, null, null, null, null, null, null, null, null, null, null, [], [], []);

    private static string? Attribute(XElement? element, string name)
        => element?.Attributes().FirstOrDefault(a => a.Name.LocalName == name)?.Value;

    /// <summary>The first child element with this local name, or null. Local names, because Dorico is XML too.</summary>
    private static string? Text(XElement element, string name)
    {
        var child = element.Elements().FirstOrDefault(candidate => candidate.Name.LocalName == name);
        var text = child?.Value.Trim();

        return string.IsNullOrEmpty(text) ? (child is null ? null : string.Empty) : text;
    }

    /// <summary>Formats the byte count of a package for a human reading the JSON.</summary>
    public static string Describe(long bytes)
        => bytes.ToString(CultureInfo.InvariantCulture) + " bytes";

    /// <summary>Decodes a UTF-8 member that is expected to be text, for diagnostics.</summary>
    public static string Text(byte[] data) => new UTF8Encoding(false).GetString(data);
}
