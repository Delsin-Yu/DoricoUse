using System.Text.RegularExpressions;

namespace DoricoUse.Cli.Views;

/// <summary>Filter matching shared by the catalogue and option lists.</summary>
internal static class Pattern
{
    /// <summary>A glob when the pattern has * or ?, a case-insensitive substring otherwise.</summary>
    public static bool Matches(string? value, string pattern)
    {
        if (value is null)
        {
            return false;
        }

        if (!pattern.Contains('*', StringComparison.Ordinal) && !pattern.Contains('?', StringComparison.Ordinal))
        {
            return value.Contains(pattern, StringComparison.OrdinalIgnoreCase);
        }

        var regex = "^" + Regex.Escape(pattern)
            .Replace("\\*", ".*", StringComparison.Ordinal)
            .Replace("\\?", ".", StringComparison.Ordinal) + "$";

        return Regex.IsMatch(value, regex, RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));
    }
}
