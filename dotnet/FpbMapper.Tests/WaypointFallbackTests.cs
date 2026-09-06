using Aml.Engine.CAEX;
using FpbMapper.Conversion;
using System.Text.Json;

namespace FpbMapper.Tests;

/// <summary>
/// Hand-authored AMLs often carry no diagram-interchange data (no PortCoordinate /
/// Waypoint_* attributes, sometimes no ViewInformation either). FPB.JS's importer
/// dereferences the visual entry of every connection unconditionally — a missing
/// entry aborts the whole import and leaves an empty canvas. These tests pin the
/// mapper-side fallback: every connection ships a visual entry with at least two
/// waypoints, synthesized center-to-center when no stored layout exists.
/// </summary>
public class WaypointFallbackTests
{
    private static CAEXDocument BuildDoc()
    {
        var json = File.ReadAllText(Path.Combine("TestData", "Temperieren.json"));
        return FpbJsonToCaex.Convert(json).Value;
    }

    private static void StripConnectionLayout(CAEXDocument doc)
    {
        CaexElementWalker.WalkInternalElements(doc, ie =>
        {
            foreach (var extIf in ie.ExternalInterface)
            {
                var stored = extIf.Attribute
                    .Where(a => a.Name == "PortCoordinate"
                        || a.Name.StartsWith("Waypoint_", StringComparison.Ordinal))
                    .ToList();
                foreach (var attr in stored) attr.Remove();
            }
        });
    }

    private static void StripShapeLayout(CAEXDocument doc)
    {
        CaexElementWalker.WalkInternalElements(doc, ie =>
        {
            ie.Attribute["ViewInformation"]?.Remove();
        });
    }

    private static List<(string Id, JsonElement Data, JsonElement? Visual)> CollectConnections(string json)
    {
        var connections = new List<(string, JsonElement, JsonElement?)>();
        using var parsed = JsonDocument.Parse(json);
        foreach (var entry in parsed.RootElement.EnumerateArray())
        {
            if (!entry.TryGetProperty("process", out _)) continue;

            var visualsById = entry.GetProperty("elementVisualInformation").EnumerateArray()
                .ToDictionary(v => v.GetProperty("id").GetString()!, v => v.Clone());

            foreach (var data in entry.GetProperty("elementDataInformation").EnumerateArray())
            {
                if (!data.TryGetProperty("sourceRef", out _)) continue;
                var id = data.GetProperty("id").GetString()!;
                connections.Add((id, data.Clone(),
                    visualsById.TryGetValue(id, out var vis) ? vis : (JsonElement?)null));
            }
        }
        return connections;
    }

    [Fact]
    public void Convert_WithoutConnectionLayout_EveryConnectionGetsFallbackWaypoints()
    {
        var doc = BuildDoc();
        StripConnectionLayout(doc);

        var ih = Assert.Single(CaexToFpbJson.FindFpdInstanceHierarchies(doc));
        var json = CaexToFpbJson.Convert(doc, ih).Value;

        var connections = CollectConnections(json);
        Assert.NotEmpty(connections);
        Assert.All(connections, c =>
        {
            Assert.True(c.Visual.HasValue, $"connection '{c.Id}' has no visual entry");
            Assert.True(c.Visual.Value.GetProperty("waypoints").GetArrayLength() >= 2,
                $"connection '{c.Id}' has fewer than 2 waypoints");
        });
    }

    [Fact]
    public void Convert_WithoutConnectionLayout_WaypointsHitShapeCenters()
    {
        var doc = BuildDoc();
        StripConnectionLayout(doc);

        var ih = Assert.Single(CaexToFpbJson.FindFpdInstanceHierarchies(doc));
        var json = CaexToFpbJson.Convert(doc, ih).Value;

        using var parsed = JsonDocument.Parse(json);
        foreach (var entry in parsed.RootElement.EnumerateArray())
        {
            if (!entry.TryGetProperty("process", out _)) continue;

            var visualsById = entry.GetProperty("elementVisualInformation").EnumerateArray()
                .ToDictionary(v => v.GetProperty("id").GetString()!, v => v.Clone());

            foreach (var data in entry.GetProperty("elementDataInformation").EnumerateArray())
            {
                if (!data.TryGetProperty("sourceRef", out var sourceRef)) continue;

                var visual = visualsById[data.GetProperty("id").GetString()!];
                var first = visual.GetProperty("waypoints").EnumerateArray().First();
                var sourceVisual = visualsById[sourceRef.GetString()!];

                var expectedX = sourceVisual.GetProperty("x").GetDouble()
                    + sourceVisual.GetProperty("width").GetDouble() / 2;
                var expectedY = sourceVisual.GetProperty("y").GetDouble()
                    + sourceVisual.GetProperty("height").GetDouble() / 2;

                Assert.Equal(expectedX, first.GetProperty("x").GetDouble(), 3);
                Assert.Equal(expectedY, first.GetProperty("y").GetDouble(), 3);
            }
        }
    }

    [Fact]
    public void Convert_WithoutAnyLayout_StillEmitsTwoWaypointsPerConnection()
    {
        var doc = BuildDoc();
        StripConnectionLayout(doc);
        StripShapeLayout(doc);

        var ih = Assert.Single(CaexToFpbJson.FindFpdInstanceHierarchies(doc));
        var json = CaexToFpbJson.Convert(doc, ih).Value;

        var connections = CollectConnections(json);
        Assert.NotEmpty(connections);
        Assert.All(connections, c =>
        {
            Assert.True(c.Visual.HasValue, $"connection '{c.Id}' has no visual entry");
            Assert.True(c.Visual.Value.GetProperty("waypoints").GetArrayLength() >= 2,
                $"connection '{c.Id}' has fewer than 2 waypoints");
        });
    }
}
