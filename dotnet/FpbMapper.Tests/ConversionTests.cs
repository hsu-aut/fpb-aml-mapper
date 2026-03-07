using System.Text.Json;
using Aml.Engine.CAEX;
using FpbMapper.Conversion;
using static FpbMapper.Conversion.FpbMappings;

namespace FpbMapper.Tests;

public class JsonToAmlTests
{
    private static string LoadTestData(string name) =>
        File.ReadAllText(Path.Combine("TestData", name));

    [Fact]
    public void Convert_Temperieren_ProducesValidCaex()
    {
        var json = LoadTestData("Temperieren.json");
        var result = FpbJsonToCaex.Convert(json);
        var caex = result.Value.CAEXFile;

        Assert.NotNull(caex);
        Assert.True(caex.InstanceHierarchy.Any(), "Should have at least one InstanceHierarchy");
    }

    [Fact]
    public void Convert_Temperieren_HasAllLibraries()
    {
        var json = LoadTestData("Temperieren.json");
        var result = FpbJsonToCaex.Convert(json);
        var caex = result.Value.CAEXFile;

        Assert.NotNull(caex.SystemUnitClassLib[LibNames.SystemUnitClassLib]);
        Assert.NotNull(caex.RoleClassLib[LibNames.RoleClassLib]);
        Assert.NotNull(caex.InterfaceClassLib[LibNames.InterfaceClassLib]);
        Assert.NotNull(caex.AttributeTypeLib[LibNames.AttributeTypeLib]);
        Assert.NotNull(caex.AttributeTypeLib[LibNames.DIAttributeTypeLib]);
    }

    [Fact]
    public void Convert_Temperieren_ContainsFpdProcess()
    {
        var json = LoadTestData("Temperieren.json");
        var result = FpbJsonToCaex.Convert(json);
        var ih = result.Value.CAEXFile.InstanceHierarchy.First();

        var processes = ih.InternalElement
            .Where(ie => ie.RefBaseSystemUnitPath == ElementToSuc["fpb:Process"])
            .ToList();

        Assert.True(processes.Count >= 1, "Should have at least one FPD_Process");
    }

    [Fact]
    public void Convert_Temperieren_ProcessHasSystemLimit()
    {
        var json = LoadTestData("Temperieren.json");
        var result = FpbJsonToCaex.Convert(json);
        var ih = result.Value.CAEXFile.InstanceHierarchy.First();
        var proc = ih.InternalElement.First(ie =>
            ie.RefBaseSystemUnitPath == ElementToSuc["fpb:Process"]);

        var sl = proc.InternalElement.FirstOrDefault(ie =>
            ie.RefBaseSystemUnitPath == ElementToSuc["fpb:SystemLimit"]);

        Assert.NotNull(sl);
    }

    [Fact]
    public void Convert_Temperieren_HasInternalLinks()
    {
        var json = LoadTestData("Temperieren.json");
        var result = FpbJsonToCaex.Convert(json);
        var ih = result.Value.CAEXFile.InstanceHierarchy.First();
        var proc = ih.InternalElement.First(ie =>
            ie.RefBaseSystemUnitPath == ElementToSuc["fpb:Process"]);

        Assert.True(proc.InternalLink.Any(), "Process should have InternalLinks (flows)");
    }

    [Fact]
    public void Convert_Temperieren_NoWarnings()
    {
        var json = LoadTestData("Temperieren.json");
        var result = FpbJsonToCaex.Convert(json);

        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void Convert_EmptyEntries_ThrowsValidationError()
    {
        var json = "[{\"$type\":\"fpb:Project\",\"name\":\"Test\",\"entryPoint\":\"abc\"}]";
        var ex = Assert.Throws<InvalidOperationException>(() => FpbJsonToCaex.Convert(json));
        Assert.Contains("No process entries", ex.Message);
    }
}

public class AmlToJsonTests
{
    private static string LoadTestData(string name) =>
        File.ReadAllText(Path.Combine("TestData", name));

    private CAEXDocument ConvertToAml(string jsonFile)
    {
        var json = LoadTestData(jsonFile);
        return FpbJsonToCaex.Convert(json).Value;
    }

    [Fact]
    public void Convert_ProducesValidJson()
    {
        var doc = ConvertToAml("Temperieren.json");
        var result = CaexToFpbJson.Convert(doc);

        Assert.NotEmpty(result.Value);
        // Should be valid JSON
        var parsed = JsonDocument.Parse(result.Value);
        Assert.NotNull(parsed);
    }

    [Fact]
    public void Convert_HasProjectHeader()
    {
        var doc = ConvertToAml("Temperieren.json");
        var result = CaexToFpbJson.Convert(doc);

        using var parsed = JsonDocument.Parse(result.Value);
        var root = parsed.RootElement;
        Assert.Equal(JsonValueKind.Array, root.ValueKind);
        Assert.True(root.GetArrayLength() >= 2, "Should have project header + at least one process");

        var project = root[0];
        Assert.Equal("fpb:Project", project.GetProperty("$type").GetString());
        Assert.True(project.TryGetProperty("entryPoint", out _));
    }

    [Fact]
    public void Convert_ProcessHasElementData()
    {
        var doc = ConvertToAml("Temperieren.json");
        var result = CaexToFpbJson.Convert(doc);

        using var parsed = JsonDocument.Parse(result.Value);
        var processEntry = parsed.RootElement[1];
        Assert.True(processEntry.TryGetProperty("elementDataInformation", out var edi));
        Assert.True(edi.GetArrayLength() > 0, "Should have element data");
    }

    [Fact]
    public void Convert_NoWarnings()
    {
        var doc = ConvertToAml("Temperieren.json");
        var result = CaexToFpbJson.Convert(doc);
        Assert.Empty(result.Warnings);
    }
}

public class RoundtripTests
{
    private static string LoadTestData(string name) =>
        File.ReadAllText(Path.Combine("TestData", name));

    [Fact]
    public void Roundtrip_JsonToAmlToJson_PreservesStructure()
    {
        var originalJson = LoadTestData("Temperieren.json");

        // JSON -> AML
        var amlResult = FpbJsonToCaex.Convert(originalJson);
        var doc = amlResult.Value;

        // AML -> JSON
        var jsonResult = CaexToFpbJson.Convert(doc);

        using var original = JsonDocument.Parse(originalJson);
        using var roundtripped = JsonDocument.Parse(jsonResult.Value);

        // Same number of top-level entries
        Assert.Equal(original.RootElement.GetArrayLength(), roundtripped.RootElement.GetArrayLength());

        // Project type preserved
        Assert.Equal("fpb:Project", roundtripped.RootElement[0].GetProperty("$type").GetString());

        // Count element types in original vs roundtripped
        var originalTypes = CountElementTypes(original.RootElement);
        var roundtrippedTypes = CountElementTypes(roundtripped.RootElement);

        foreach (var (type, count) in originalTypes)
        {
            Assert.True(roundtrippedTypes.ContainsKey(type), $"Type {type} missing after roundtrip");
            Assert.Equal(count, roundtrippedTypes[type]);
        }
    }

    [Fact]
    public void Roundtrip_PreservesProcessCount()
    {
        var json = LoadTestData("Temperieren.json");
        var amlResult = FpbJsonToCaex.Convert(json);
        var jsonResult = CaexToFpbJson.Convert(amlResult.Value);

        using var original = JsonDocument.Parse(json);
        using var roundtripped = JsonDocument.Parse(jsonResult.Value);

        var originalProcessCount = original.RootElement.EnumerateArray()
            .Count(e => e.TryGetProperty("process", out _));
        var roundtrippedProcessCount = roundtripped.RootElement.EnumerateArray()
            .Count(e => e.TryGetProperty("process", out _));

        Assert.Equal(originalProcessCount, roundtrippedProcessCount);
    }

    private static Dictionary<string, int> CountElementTypes(JsonElement root)
    {
        var counts = new Dictionary<string, int>();
        foreach (var entry in root.EnumerateArray())
        {
            if (!entry.TryGetProperty("elementDataInformation", out var edi)) continue;
            foreach (var elem in edi.EnumerateArray())
            {
                if (!elem.TryGetProperty("$type", out var typeProp)) continue;
                var type = typeProp.GetString() ?? "";
                counts.TryGetValue(type, out var c);
                counts[type] = c + 1;
            }
        }
        return counts;
    }
}

public class CycleDetectionTests
{
    [Fact]
    public void CircularReference_ThrowsWithMessage()
    {
        // Build a JSON with PO A -> Process B -> PO C -> Process A (circular)
        var json = @"[
            {""$type"":""fpb:Project"",""name"":""Test"",""entryPoint"":""proc-a""},
            {
                ""process"":{""$type"":""fpb:Process"",""id"":""proc-a""},
                ""elementDataInformation"":[
                    {""$type"":""fpb:SystemLimit"",""id"":""sl-a"",""name"":""SL""},
                    {""$type"":""fpb:ProcessOperator"",""id"":""po-a"",""name"":""PO"",""decomposedView"":""proc-b""}
                ],
                ""elementVisualInformation"":[]
            },
            {
                ""process"":{""$type"":""fpb:Process"",""id"":""proc-b"",""isDecomposedProcessOperator"":""po-a""},
                ""elementDataInformation"":[
                    {""$type"":""fpb:SystemLimit"",""id"":""sl-b"",""name"":""SL""},
                    {""$type"":""fpb:ProcessOperator"",""id"":""po-b"",""name"":""PO"",""decomposedView"":""proc-a""}
                ],
                ""elementVisualInformation"":[]
            }
        ]";

        var ex = Assert.Throws<InvalidOperationException>(() => FpbJsonToCaex.Convert(json));
        Assert.Contains("Circular decomposition", ex.Message);
    }
}

public class ValidationTests
{
    [Fact]
    public void MissingSystemLimit_ProducesWarning()
    {
        var json = @"[
            {""$type"":""fpb:Project"",""name"":""Test"",""entryPoint"":""proc-1""},
            {
                ""process"":{""$type"":""fpb:Process"",""id"":""proc-1""},
                ""elementDataInformation"":[
                    {""$type"":""fpb:ProcessOperator"",""id"":""po-1"",""name"":""PO""}
                ],
                ""elementVisualInformation"":[]
            }
        ]";

        var result = FpbJsonToCaex.Convert(json);
        Assert.Contains(result.Warnings, w => w.Contains("no SystemLimit"));
    }

    [Fact]
    public void MissingEntryPoint_Throws()
    {
        var json = @"[
            {""$type"":""fpb:Project"",""name"":""Test"",""entryPoint"":""nonexistent""},
            {
                ""process"":{""$type"":""fpb:Process"",""id"":""proc-1""},
                ""elementDataInformation"":[],
                ""elementVisualInformation"":[]
            }
        ]";

        var ex = Assert.Throws<InvalidOperationException>(() => FpbJsonToCaex.Convert(json));
        Assert.Contains("Entry point", ex.Message);
    }
}
