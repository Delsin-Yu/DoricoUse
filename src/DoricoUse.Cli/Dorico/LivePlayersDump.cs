using System.Globalization;
using System.Text.RegularExpressions;
using System.Text.Json.Nodes;
using DoricoUse.Cli.Json;

namespace DoricoUse.Cli.Dorico;

/// <summary>
/// Turns the text the live player read writes into JSON.
/// </summary>
/// <remarks>
/// <para>
/// Pure, like <see cref="LiveReadDump"/> and for the same reason: the input is Dorico's Lua text, so it can be
/// asserted without Dorico. The lines are tab-separated and the script writes one <c>player</c> line per player plus
/// one <c>member</c> (or <c>memberfail</c>) line per member the object was asked for, which is how a probe whose
/// answer is "this layer does not expose that" is reported instead of hidden.
/// </para>
/// <para>
/// Nothing here invents an id. The Remote Control API publishes no request that returns players (measured: the
/// request table in the application has none, and <c>getflows</c> answers a flow id and a name and nothing else),
/// so the only ids on this route are the ones the scripting layer actually answers.
/// </para>
/// </remarks>
internal static class LivePlayersDump
{
    /// <summary>The first line of the dump, so a truncated or foreign file is caught rather than half-read.</summary>
    public const string Header = "dorico-use live players";

    private static readonly Regex ScoreLine = new(
        @"^score\t(?<id>[^\t]*)\t(?<version>[^\t]*)$",
        RegexOptions.CultureInvariant);

    private static readonly Regex CountLine = new(
        @"^count\t(?<count>-?\d+)$",
        RegexOptions.CultureInvariant);

    private static readonly Regex PlayerLine = new(
        @"^player\t(?<index>\d+)\t(?<text>.*)$",
        RegexOptions.CultureInvariant);

    private static readonly Regex MemberLine = new(
        @"^(?<ok>member|memberfail)\t(?<index>\d+)\t(?<key>[^\t]+)\t(?<value>.*)$",
        RegexOptions.CultureInvariant);

    /// <summary>
    /// Reads one dump. A member the layer refused is kept in <c>unavailable</c> on that player, so a payload that
    /// cannot answer "is this player a soloist?" says so instead of leaving the field looking null by accident.
    /// </summary>
    /// <param name="lines">The dump, as the script wrote it.</param>
    public static JsonObject Parse(string[] lines)
    {
        var players = new JsonArray();
        var unparsed = new JsonArray();
        var header = false;
        var reported = 0;
        string? error = null;
        string? version = null;
        int? scoreId = null;
        JsonObject? current = null;

        foreach (var line in lines)
        {
            if (!header)
            {
                header = line.StartsWith(Header, StringComparison.Ordinal);
                continue;
            }

            if (line.StartsWith("error\t", StringComparison.Ordinal))
            {
                error = line["error\t".Length..];
                continue;
            }

            var score = ScoreLine.Match(line);

            if (score.Success)
            {
                scoreId = int.TryParse(score.Groups["id"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id)
                    ? id
                    : null;
                version = score.Groups["version"].Value;

                continue;
            }

            var count = CountLine.Match(line);

            if (count.Success)
            {
                reported = int.Parse(count.Groups["count"].Value, CultureInfo.InvariantCulture);
                continue;
            }

            var player = PlayerLine.Match(line);

            if (player.Success)
            {
                current = new JsonObject
                {
                    ["index"] = int.Parse(player.Groups["index"].Value, CultureInfo.InvariantCulture),
                    ["described"] = player.Groups["text"].Value,
                    ["unavailable"] = new JsonArray(),
                };

                JsonArr.AddNode(players, current);

                continue;
            }

            var member = MemberLine.Match(line);

            if (member.Success)
            {
                var target = Target(players, int.Parse(member.Groups["index"].Value, CultureInfo.InvariantCulture));

                if (target is null)
                {
                    JsonArr.AddText(unparsed, line);
                    continue;
                }

                var key = member.Groups["key"].Value;
                var value = member.Groups["value"].Value;

                if (member.Groups["ok"].Value == "member")
                {
                    // `instruments` carries a count, not a name, and a count is a number an agent can compare.
                    target[key] = key == "instruments" && long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var many)
                        ? JsonValue.Create(many)
                        : value;
                }
                else
                {
                    JsonArr.AddText((JsonArray)target["unavailable"]!, key + ": " + value);
                }

                continue;
            }

            JsonArr.AddText(unparsed, line);
        }

        var payload = new JsonObject
        {
            ["source"] = "live",
            ["score"] = new JsonObject
            {
                ["openScoreId"] = scoreId is null ? null : JsonValue.Create(scoreId.Value),
                ["version"] = version,
            },
            ["count"] = players.Count,
            ["reported"] = reported,
            ["players"] = players,
        };

        var caveats = new JsonArray();

        JsonArr.AddText(caveats,
            "live: read from the running document by a script handed to Dorico through Script.RunScript. Nothing is "
            + "saved and the document is not rebound. The listing is what the scripting layer exposes: `described` is "
            + "Dorico's own text for the player object, and every other field comes from a member the script asked "
            + "that object for by name.");

        JsonArr.AddText(caveats,
            "There is no Remote Control request that returns players. Measured: the request ids the application "
            + "answers are connect, acceptsessiontoken, disconnect, getstatus, getappinfo, getcommands, getflows, "
            + "getlayouts, getoptions, setoptions, getproperties, getplaybacktechniques, getlibrarycollections and "
            + "getlibraryentities, and `getflows` answers `{flowID, flowName}` per flow and nothing else — so a player "
            + "list cannot be built from `project flows`.");

        JsonArr.AddText(caveats,
            "The ids the player commands take (`Project.Player.SetAsSoloist?PlayerID=`, `Project.Player.Rename"
            + "?PlayerID=`, `Project.Player.AddToGroup?PlayerID=&PlayerGroupID=`) are therefore not published to an "
            + "agent. `file read` on the saved project reports the exact per-player ids, names, short names and the "
            + "stored solo flag, and `project groups` reports the group ids the same way; use those when the command "
            + "needs an id, and check a rename or a soloist change by saving and reading the file back.");

        if (players.Count == 0)
        {
            JsonArr.AddText(caveats,
                "No player at all: Dorico has no document open, or the scripting layer answered an empty table. "
                + "`session status` says whether a document is open.");
        }

        if (unparsed.Count > 0)
        {
            payload["unparsedLines"] = unparsed;
        }

        if (error is not null)
        {
            payload["error"] = error;
        }

        payload["caveats"] = caveats;

        return payload;
    }

    private static JsonObject? Target(JsonArray players, int index)
    {
        foreach (var player in players)
        {
            if (player is JsonObject candidate && (int?)candidate["index"] == index)
            {
                return candidate;
            }
        }

        return null;
    }
}
