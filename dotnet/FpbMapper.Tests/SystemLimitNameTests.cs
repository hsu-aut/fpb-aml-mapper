using System.Text.Json.Nodes;
using FpbMapper.Conversion;

namespace FpbMapper.Tests;

/// <summary>
/// The name of a sub-process's SystemLimit has to survive JSON -> AML -> JSON.
///
/// Temperieren.json decomposes the operator "Erhitzen" into a sub-process whose
/// SystemLimit is called "SL_Erhitzen". The SystemLimit's shortName used to be
/// written from the process name, which for a sub-process is the operator's
/// name, so it came back as "Erhitzen". The top-level process hid the defect,
/// since its process name is taken from its SystemLimit.
/// </summary>
public class SystemLimitNameTests
{
    private static string LoadTestData(string name) =>
        File.ReadAllText(Path.Combine("TestData", name));

    /// <summary>
    /// SystemLimit name per process, as the JSON states it. A sub-process is
    /// keyed by the operator it decomposes, the top-level process by "top": its
    /// own id is not kept across a conversion.
    /// </summary>
    private static Dictionary<string, string?> SystemLimitNames(string json)
    {
        var names = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in JsonNode.Parse(json)!.AsArray())
        {
            var process = entry?["process"];
            if (process == null) continue;

            var parent = process["isDecomposedProcessOperator"]?.GetValue<string>();
            var key = string.IsNullOrEmpty(parent) ? "top" : parent.Trim('{', '}');

            var systemLimit = entry!["elementDataInformation"]!.AsArray()
                .FirstOrDefault(d => d?["$type"]?.GetValue<string>() == FpbTypes.SystemLimit);
            names[key] = systemLimit?["name"]?.GetValue<string>();
        }
        return names;
    }

    /// <summary>The same project without its sub-process: the operator is not decomposed.</summary>
    private static string WithoutSubProcesses(string json)
    {
        var entries = JsonNode.Parse(json)!.AsArray();
        foreach (var sub in entries.Where(e => e?["process"]?["isDecomposedProcessOperator"] != null).ToList())
            entries.Remove(sub);

        foreach (var entry in entries)
        foreach (var data in entry?["elementDataInformation"]?.AsArray() ?? new JsonArray())
        {
            if (data?["decomposedView"] != null) data["decomposedView"] = null;
        }
        return entries.ToJsonString();
    }

    [Fact]
    public void TheFixtureHasASubProcessWhoseSystemLimitIsNotNamedAfterItsOperator()
    {
        var names = SystemLimitNames(LoadTestData("Temperieren.json"));

        Assert.Equal(2, names.Count);
        Assert.Contains("SL_Erhitzen", names.Values);
    }

    [Fact]
    public void Convert_KeepsTheSystemLimitNamesOfSubProcesses()
    {
        var json = LoadTestData("Temperieren.json");

        var doc = FpbJsonToCaex.Convert(json).Value;
        var back = CaexToFpbJson.Convert(doc).Value;

        Assert.Equal(SystemLimitNames(json), SystemLimitNames(back));
    }

    [Fact]
    public void UpdateInPlace_AddingASubProcessKeepsItsSystemLimitName()
    {
        var json = LoadTestData("Temperieren.json");

        // The decomposition happens in the viewer after the document exists.
        var doc = FpbJsonToCaex.Convert(WithoutSubProcesses(json)).Value;
        FpbJsonToCaex.UpdateInPlace(doc, json);
        var back = CaexToFpbJson.Convert(doc).Value;

        Assert.Equal(SystemLimitNames(json), SystemLimitNames(back));
    }

    [Fact]
    public void UpdateInPlace_UpdatingASubProcessKeepsItsSystemLimitName()
    {
        var json = LoadTestData("Temperieren.json");

        var doc = FpbJsonToCaex.Convert(json).Value;
        FpbJsonToCaex.UpdateInPlace(doc, CaexToFpbJson.Convert(doc).Value);
        var back = CaexToFpbJson.Convert(doc).Value;

        Assert.Equal(SystemLimitNames(json), SystemLimitNames(back));
    }
}
