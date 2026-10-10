using System.Text.RegularExpressions;
using System.Text.Json.Nodes;
using DoricoUse.Cli.Dorico;
using DoricoUse.Cli.Json;

namespace DoricoUse.Cli.Views;

/// <summary>
/// The command catalogue Dorico reports through getcommands: 1090 commands in Dorico 6.2.30 Pro, each with a
/// stable name, a localized display name, and the parameters it accepts.
/// </summary>
internal static class CatalogueView
{
    public static JsonObject Report(JsonObject response, long fetchMilliseconds)
    {
        var commands = NodeRead.Arr(response, "commands");

        var report = new JsonObject
        {
            ["count"] = commands?.Count ?? 0,
            ["fetchMs"] = fetchMilliseconds,
        };

        if (commands is null || commands.Count == 0)
        {
            return report;
        }

        report["families"] = Families(commands);

        return report;
    }

    /// <summary>
    /// Picks commands by exact name, by family prefix, or by a wildcard match on the name or the display name.
    /// </summary>
    public static JsonArray Select(
        JsonObject response,
        string? name,
        string? family,
        string? filter,
        int limit)
    {
        var matches = new JsonArray();
        var commands = NodeRead.Arr(response, "commands");

        if (commands is null)
        {
            return matches;
        }

        foreach (var command in commands)
        {
            var commandName = NodeRead.Str(command, "name");

            if (commandName is null)
            {
                continue;
            }

            if (name is not null && !commandName.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (family is not null && !commandName.StartsWith(family + ".", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (filter is not null
                && !Matches(commandName, filter)
                && !Matches(NodeRead.Str(command, "displayName"), filter))
            {
                continue;
            }

            JsonArr.AddNode(matches, command?.DeepClone());

            if (matches.Count >= limit)
            {
                break;
            }
        }

        return matches;
    }

    public static JsonObject Families(JsonArray commands)
    {
        var families = new JsonObject();

        foreach (var command in commands)
        {
            var name = NodeRead.Str(command, "name");

            if (name is null)
            {
                continue;
            }

            var dot = name.IndexOf('.', StringComparison.Ordinal);
            var family = dot > 0 ? name[..dot] : name;
            families[family] = (NodeRead.Int(families, family) ?? 0) + 1;
        }

        return families;
    }

    /// <summary>A glob when the pattern has * or ?, a case-insensitive substring otherwise.</summary>
    private static bool Matches(string? value, string pattern)
    {
        if (value is null)
        {
            return false;
        }

        if (!pattern.Contains('*', StringComparison.Ordinal) && !pattern.Contains('?', StringComparison.Ordinal))
        {
            return value.Contains(pattern, StringComparison.OrdinalIgnoreCase);
        }

        var regex = "^" + Regex.Escape(pattern).Replace("\\*", ".*", StringComparison.Ordinal)
            .Replace("\\?", ".", StringComparison.Ordinal) + "$";

        return Regex.IsMatch(value, regex, RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));
    }
}
