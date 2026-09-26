using System.Text.Json.Nodes;
using FpbMapper.Conversion;

namespace FpbMapper.Tests;

/// <summary>
/// A flow added by UpdateInPlace between two elements of the same process must
/// not be reported as crossing processes. The check compared the wrappers of
/// Aml.Engine by reference; the engine hands out a new wrapper for every
/// CAEXParent, so every added flow was reported.
/// </summary>
public class EndpointWarningTests
{
    [Fact]
    public void AFlowWithinTheProcessIsNotReportedAsCrossingProcesses()
    {
        var json = File.ReadAllText(Path.Combine("TestData", "Temperieren.json"));
        var doc = FpbJsonToCaex.Convert(json).Value;

        var root = JsonNode.Parse(CaexToFpbJson.Convert(doc).Value)!.AsArray();
        var top = root.Skip(1).OfType<JsonObject>().First(e => (string?)e["process"]?["id"] == (string?)root[0]!["entryPoint"]);
        var data = top["elementDataInformation"]!.AsArray();
        var state = data.First(e => (string?)e!["$type"] == "fpb:Product")!;
        var po = data.First(e => (string?)e!["$type"] == "fpb:ProcessOperator")!;
        data.Add(new JsonObject
        {
            ["$type"] = "fpb:Flow", ["id"] = "0badd000-0000-4000-8000-00000000f10a",
            ["sourceRef"] = (string)state["id"]!, ["targetRef"] = (string)po["id"]!
        });

        var result = FpbJsonToCaex.UpdateInPlace(doc, root.ToJsonString());

        Assert.DoesNotContain(result.Warnings, w => w.Contains("not all endpoints live under process"));
    }
}
