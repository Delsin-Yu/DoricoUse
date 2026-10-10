using System.Text.Json.Nodes;
using DoricoUse.Cli.Dorico;
using DoricoUse.Cli.Json;

namespace DoricoUse.Cli.Views;

/// <summary>
/// Agent-first projections of the score-level payloads: flows, layouts, options, library, properties and playback
/// techniques. Nothing is invented: a field Dorico did not send stays null, and shapes that were not verified
/// against the live application are passed through rather than guessed at.
/// </summary>
internal static class ScoreView
{
    public static JsonObject Flows(JsonObject response)
    {
        var flows = new JsonArray();
        var items = NodeRead.Arr(response, "flows") ?? [];

        foreach (var flow in items)
        {
            JsonArr.AddNode(flows, new JsonObject
            {
                ["id"] = NodeRead.Int(flow, "flowID"),
                ["name"] = NodeRead.Str(flow, "flowName"),
            });
        }

        return new JsonObject
        {
            ["scoreId"] = NodeRead.Int(response, "openScoreID"),
            ["count"] = items.Count,
            ["flows"] = flows,
        };
    }

    public static JsonObject Layouts(JsonObject response, string? kindFilter)
    {
        var layouts = new JsonArray();
        var items = NodeRead.Arr(response, "layouts") ?? [];
        var matched = 0;

        foreach (var layout in items)
        {
            var kind = Kind(NodeRead.Str(layout, "layoutType"));

            if (kindFilter is not null && !kindFilter.Equals(kind, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            matched++;

            JsonArr.AddNode(layouts, new JsonObject
            {
                ["id"] = NodeRead.Int(layout, "layoutID"),
                ["name"] = NodeRead.Str(layout, "layoutName"),
                ["number"] = NodeRead.Int(layout, "layoutNumber"),
                ["kind"] = kind,
                ["type"] = NodeRead.Str(layout, "layoutType"),
            });
        }

        return new JsonObject
        {
            ["scoreId"] = NodeRead.Int(response, "openScoreID"),
            ["count"] = matched,
            ["total"] = items.Count,
            ["layouts"] = layouts,
        };
    }

    public static JsonObject Options(JsonObject response, string? filter, int limit)
    {
        var options = new JsonArray();
        var items = NodeRead.Arr(response, "options") ?? [];
        var matched = 0;

        foreach (var option in items)
        {
            var path = NodeRead.Str(option, "path");

            if (filter is not null && !Pattern.Matches(path, filter))
            {
                continue;
            }

            matched++;

            if (options.Count >= limit)
            {
                continue;
            }

            JsonArr.AddNode(options, new JsonObject
            {
                ["path"] = path,
                ["type"] = NodeRead.Str(option, "valueType"),
                ["value"] = NodeRead.Str(option, "currentValue"),
            });
        }

        return new JsonObject
        {
            ["scoreId"] = NodeRead.Int(response, "openScoreID"),
            ["optionsType"] = NodeRead.Str(response, "optionsType"),
            ["total"] = items.Count,
            ["count"] = matched,
            ["returned"] = options.Count,
            ["truncated"] = matched > options.Count,
            ["options"] = options,
        };
    }

    public static JsonObject LibraryCollections(JsonObject response)
    {
        var collections = NodeRead.Arr(response, "collections") ?? [];

        return new JsonObject
        {
            ["scoreId"] = NodeRead.Int(response, "openScoreID"),
            ["count"] = collections.Count,
            ["collections"] = collections.DeepClone(),
        };
    }

    public static JsonObject LibraryEntities(JsonObject response)
    {
        var entities = new JsonArray();
        var items = NodeRead.Arr(response, "libraryentities") ?? [];

        foreach (var entity in items)
        {
            JsonArr.AddNode(entities, new JsonObject
            {
                ["id"] = NodeRead.Str(entity, "entityID"),
                ["name"] = NodeRead.Str(entity, "name"),
            });
        }

        return new JsonObject
        {
            ["scoreId"] = NodeRead.Int(response, "openScoreID"),
            ["count"] = items.Count,
            ["entities"] = entities,
        };
    }

    public static JsonObject Properties(JsonObject response)
    {
        var items = NodeRead.Arr(response, "properties") ?? [];

        return new JsonObject
        {
            ["scoreId"] = NodeRead.Int(response, "openScoreID"),
            ["scope"] = NodeRead.Str(response, "currentScope"),
            ["count"] = items.Count,
            ["properties"] = items.DeepClone(),
        };
    }

    public static JsonObject Techniques(JsonObject response)
    {
        var expressionMaps = NodeRead.Arr(response, "expressionmaps") ?? [];
        var drumMaps = NodeRead.Arr(response, "drumkitnotemaps") ?? [];

        return new JsonObject
        {
            ["scoreId"] = NodeRead.Int(response, "openScoreID"),
            ["expressionMapCount"] = expressionMaps.Count,
            ["drumKitNoteMapCount"] = drumMaps.Count,
            ["expressionMaps"] = expressionMaps.DeepClone(),
            ["drumKitNoteMaps"] = drumMaps.DeepClone(),
        };
    }

    private static string Kind(string? layoutType) => layoutType switch
    {
        "kFullScoreLayout" => "full-score",
        "kPartLayout" => "part",
        null => "unknown",
        _ => layoutType,
    };
}
