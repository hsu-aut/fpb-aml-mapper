using Aml.Engine.CAEX;
using FpbMapper.Conversion;
using System.Text.Json;

namespace FpbMapper.Tests;

/// <summary>
/// Hand-authored AMLs often carry no diagram-interchange data (no PortCoordinate /
/// Waypoint_* attributes, sometimes no ViewInformation either). The mapper emits
/// only the layout the AML actually stores: elements and links without it get no
/// visual entry at all. FPB.JS arranges whatever lacks layout when it imports the
/// JSON, so synthesizing geometry here would only get in its way.
/// </summary>
public class LayoutFreeConversionTests
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

    private static List<(string Id, bool IsConnection, JsonElement? Visual)> CollectElements(string json)
    {
        var elements = new List<(string, bool, JsonElement?)>();
        using var parsed = JsonDocument.Parse(json);
        foreach (var entry in parsed.RootElement.EnumerateArray())
        {
            if (!entry.TryGetProperty("process", out _)) continue;

            var visualsById = entry.GetProperty("elementVisualInformation").EnumerateArray()
                .ToDictionary(v => v.GetProperty("id").GetString()!, v => v.Clone());

            foreach (var data in entry.GetProperty("elementDataInformation").EnumerateArray())
            {
                var id = data.GetProperty("id").GetString()!;
                var isConnection = data.TryGetProperty("sourceRef", out _);
                elements.Add((id, isConnection,
                    visualsById.TryGetValue(id, out var vis) ? vis : (JsonElement?)null));
            }
        }
        return elements;
    }

    private static string ConvertSingle(CAEXDocument doc)
    {
        var ih = Assert.Single(CaexToFpbJson.FindFpdInstanceHierarchies(doc));
        return CaexToFpbJson.Convert(doc, ih).Value;
    }

    [Fact]
    public void Convert_WithStoredLayout_EveryElementAndConnectionHasAVisual()
    {
        var elements = CollectElements(ConvertSingle(BuildDoc()));

        Assert.NotEmpty(elements);
        Assert.All(elements, e => Assert.True(e.Visual.HasValue, $"'{e.Id}' has no visual entry"));
        Assert.All(elements.Where(e => e.IsConnection), e =>
            Assert.True(e.Visual!.Value.GetProperty("waypoints").GetArrayLength() >= 2,
                $"connection '{e.Id}' has fewer than 2 waypoints"));
    }

    [Fact]
    public void Convert_WithoutConnectionLayout_ConnectionsGetNoVisual_ShapesKeepTheirs()
    {
        var doc = BuildDoc();
        StripConnectionLayout(doc);

        var elements = CollectElements(ConvertSingle(doc));

        Assert.NotEmpty(elements.Where(e => e.IsConnection));
        Assert.All(elements.Where(e => e.IsConnection), e =>
            Assert.False(e.Visual.HasValue, $"connection '{e.Id}' got a synthesized visual entry"));
        Assert.All(elements.Where(e => !e.IsConnection), e =>
            Assert.True(e.Visual.HasValue, $"shape '{e.Id}' lost its visual entry"));
    }

    [Fact]
    public void Convert_WithoutAnyLayout_EmitsNoVisualInformationButAllData()
    {
        var withLayout = CollectElements(ConvertSingle(BuildDoc()));

        var doc = BuildDoc();
        StripConnectionLayout(doc);
        StripShapeLayout(doc);
        var without = CollectElements(ConvertSingle(doc));

        Assert.Equal(withLayout.Select(e => e.Id).OrderBy(id => id), without.Select(e => e.Id).OrderBy(id => id));
        Assert.All(without, e => Assert.False(e.Visual.HasValue, $"'{e.Id}' got a visual entry without stored layout"));
    }
}
