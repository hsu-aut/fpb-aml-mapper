using System.Text.Json.Nodes;
using Aml.Engine.CAEX;
using FpbMapper.Conversion;
using Xunit;
using static FpbMapper.Conversion.FpbMappings;

namespace FpbMapper.Tests;

/// <summary>
/// B10: decomposing an operator in the editor and updating the AML file adds
/// copies of its boundary states to the new sub-process. Each copy must carry
/// refObj = the state it shows; RemapSubProcessBoundaryStateIds gave the copy
/// its derived id first, so SyncBoundaryStateRefObjs no longer recognised it
/// and the copy came out with an empty refObj.
/// </summary>
public class BoundaryRefObjTests
{
    private static string LoadTestData(string name) =>
        File.ReadAllText(Path.Combine("TestData", name));

    [Fact]
    public void UpdateInPlace_FreshDecompose_GivesTheBoundaryCopyRefObjToItsState()
    {
        var aml = FpbJsonToCaex.Convert(LoadTestData("Temperieren.json")).Value;
        var json = CaexToFpbJson.Convert(aml).Value;

        var (poId, stateId, stateType, stateName) = FindOperatorWithInputState(json);
        FpbJsonToCaex.UpdateInPlace(aml, Decompose(json, poId, stateId, stateType, stateName));

        var ih = aml.CAEXFile.InstanceHierarchy.First();
        var sub = ih.InternalElement.FirstOrDefault(ie =>
            ie.RefBaseSystemUnitPath == ElementToSuc["fpb:Process"]
            && string.Equals((ie.Attribute["refObj"]?.Value ?? "").Trim('{', '}'), poId, StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(sub);

        var copy = sub!.InternalElement.Single(ie => ie.RefBaseSystemUnitPath == ElementToSuc[stateType]);
        Assert.NotEqual(stateId, copy.ID.Trim('{', '}'), StringComparer.OrdinalIgnoreCase);
        Assert.Equal(stateId, (copy.Attribute["refObj"]?.Value ?? "").Trim('{', '}'), StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>A top level operator without decomposition and a state flowing into it</summary>
    private static (string po, string state, string type, string name) FindOperatorWithInputState(string json)
    {
        var data = JsonNode.Parse(json)!.AsArray()[1]!["elementDataInformation"]!.AsArray();
        var states = data.Where(d => d?["$type"]?.GetValue<string>() is "fpb:Product" or "fpb:Energy" or "fpb:Information")
            .ToDictionary(d => d!["id"]!.GetValue<string>(), d => d!);
        foreach (var po in data.Where(d => d?["$type"]?.GetValue<string>() == "fpb:ProcessOperator"
                     && string.IsNullOrEmpty(d?["decomposedView"]?.GetValue<string>())))
        {
            var poId = po!["id"]!.GetValue<string>();
            var flow = data.FirstOrDefault(d => d?["$type"]?.GetValue<string>() == "fpb:Flow"
                && d?["targetRef"]?.GetValue<string>() == poId
                && states.ContainsKey(d?["sourceRef"]?.GetValue<string>() ?? ""));
            if (flow is null) continue;
            var state = states[flow["sourceRef"]!.GetValue<string>()];
            return (poId, state["id"]!.GetValue<string>(), state["$type"]!.GetValue<string>(), state["name"]?.GetValue<string>() ?? "");
        }
        throw new InvalidOperationException("no undecomposed operator with an input state");
    }

    /// <summary>
    /// The payload FPB.JS sends after decomposing: the operator points to its
    /// new sub-process (id = operator id, the v1 convention), which holds a
    /// system limit and the copy of the input state under the shared id.
    /// </summary>
    private static string Decompose(string json, string poId, string stateId, string stateType, string stateName)
    {
        var node = JsonNode.Parse(json)!.AsArray();
        foreach (var d in node[1]!["elementDataInformation"]!.AsArray())
            if (d?["id"]?.GetValue<string>() == poId) { d["decomposedView"] = poId; break; }

        var slId = "0b0b0b0b-0000-4000-8000-000000000001";
        node.Add(new JsonObject
        {
            ["process"] = new JsonObject
            {
                ["$type"] = "fpb:Process",
                ["id"] = poId,
                ["isDecomposedProcessOperator"] = poId,
                ["elementsContainer"] = new JsonArray { slId },
                ["consistsOfSystemLimit"] = slId,
                ["consistsOfStates"] = new JsonArray { stateId },
                ["consistsOfProcessOperator"] = new JsonArray(),
                ["consistsOfProcesses"] = new JsonArray(),
            },
            ["elementDataInformation"] = new JsonArray
            {
                new JsonObject { ["$type"] = "fpb:SystemLimit", ["id"] = slId, ["name"] = "SL_sub", ["elementsContainer"] = new JsonArray { stateId } },
                new JsonObject { ["$type"] = stateType, ["id"] = stateId, ["name"] = stateName },
            },
            ["elementVisualInformation"] = new JsonArray
            {
                new JsonObject { ["id"] = slId, ["type"] = "fpb:SystemLimit", ["x"] = 0, ["y"] = 0, ["width"] = 400, ["height"] = 400 },
                new JsonObject { ["id"] = stateId, ["type"] = stateType, ["x"] = 100, ["y"] = -25, ["width"] = 50, ["height"] = 50 },
            },
        });
        return node.ToJsonString();
    }
}
