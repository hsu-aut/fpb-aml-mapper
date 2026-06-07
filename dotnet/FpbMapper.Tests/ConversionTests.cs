using System.Text.Json;
using System.Text.Json.Nodes;
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

public class IdStabilityTests
{
    private static string LoadTestData(string name) =>
        File.ReadAllText(Path.Combine("TestData", name));

    [Fact]
    public void Roundtrip_PreservesElementIds()
    {
        var json = LoadTestData("Temperieren.json");
        var aml = FpbJsonToCaex.Convert(json).Value;
        var roundtripped = CaexToFpbJson.Convert(aml).Value;

        // Collect IDs from original and roundtripped JSON
        using var orig = JsonDocument.Parse(json);
        using var rt = JsonDocument.Parse(roundtripped);

        var origIds = CollectElementIds(orig.RootElement);
        var rtIds = CollectElementIds(rt.RootElement);

        // All original element IDs must be present in the roundtrip — this is the
        // core promise of Phase 2.A (CaexToFpbJson takes AML IDs raw).
        foreach (var id in origIds)
            Assert.Contains(id, rtIds);
    }

    [Fact]
    public void Roundtrip_ElementIdsHaveNoBraces()
    {
        var json = LoadTestData("Temperieren.json");
        var aml = FpbJsonToCaex.Convert(json).Value;
        var roundtripped = CaexToFpbJson.Convert(aml).Value;

        using var rt = JsonDocument.Parse(roundtripped);
        var rtIds = CollectElementIds(rt.RootElement);

        foreach (var id in rtIds)
        {
            Assert.False(id.StartsWith('{'), $"ID '{id}' should not have leading brace");
            Assert.False(id.EndsWith('}'), $"ID '{id}' should not have trailing brace");
        }
    }

    private static HashSet<string> CollectElementIds(JsonElement root)
    {
        var ids = new HashSet<string>();
        foreach (var entry in root.EnumerateArray())
        {
            if (!entry.TryGetProperty("elementDataInformation", out var edi)) continue;
            foreach (var elem in edi.EnumerateArray())
            {
                if (elem.TryGetProperty("id", out var idProp))
                    ids.Add(idProp.GetString() ?? "");
            }
        }
        return ids;
    }
}

public class UpdateInPlaceTests
{
    private static string LoadTestData(string name) =>
        File.ReadAllText(Path.Combine("TestData", name));

    [Fact]
    public void UpdateInPlace_NoChanges_LeavesElementCountStable()
    {
        var json = LoadTestData("Temperieren.json");
        var aml = FpbJsonToCaex.Convert(json).Value;

        var beforeCount = CountAllInternalElements(aml.CAEXFile.InstanceHierarchy.First());

        // Take the AML and roundtrip JSON, then UpdateInPlace with the same payload.
        var rt = CaexToFpbJson.Convert(aml).Value;
        var result = FpbJsonToCaex.UpdateInPlace(aml, rt);

        var afterCount = CountAllInternalElements(result.Value.CAEXFile.InstanceHierarchy.First());

        Assert.Equal(beforeCount, afterCount);
    }

    [Fact]
    public void UpdateInPlace_NoFpdIh_FallsBackToAppendWithWarning()
    {
        // Build a fresh empty CAEX document — no FPD IH.
        var doc = CAEXDocument.New_CAEXDocument();
        var json = LoadTestData("Temperieren.json");

        var result = FpbJsonToCaex.UpdateInPlace(doc, json);

        Assert.NotNull(result.Value);
        Assert.Contains(result.Warnings, w => w.Contains("No existing FPD"));
        Assert.True(result.Value.CAEXFile.InstanceHierarchy.Any(),
            "Should have appended a fresh hierarchy as fallback");
    }

    [Fact]
    public void UpdateInPlace_PreservesCustomAttributeOnExistingElement()
    {
        var json = LoadTestData("Temperieren.json");
        var aml = FpbJsonToCaex.Convert(json).Value;
        var ih = aml.CAEXFile.InstanceHierarchy.First();

        // Find any FPD_ProcessOperator and stamp a custom attribute on it.
        var po = FindFirstByRefSuc(ih, "VDI_FPD_SystemUnitClassLib/FPD_ProcessOperator");
        Assert.NotNull(po);
        var customAttr = po!.Attribute.Append("PluginUserAnnotation");
        customAttr.Value = "do-not-touch";

        var beforeId = po.ID;

        // Roundtrip then update in place — custom attribute must survive.
        var rt = CaexToFpbJson.Convert(aml).Value;
        FpbJsonToCaex.UpdateInPlace(aml, rt);

        var ih2 = aml.CAEXFile.InstanceHierarchy.First();
        var poAgain = WalkAll(ih2.InternalElement).FirstOrDefault(ie => ie.ID == beforeId);
        Assert.NotNull(poAgain);
        var preserved = poAgain!.Attribute["PluginUserAnnotation"];
        Assert.NotNull(preserved);
        Assert.Equal("do-not-touch", preserved!.Value);
    }

    [Fact]
    public void UpdateInPlace_RemovesOrphanedFpdElement()
    {
        var json = LoadTestData("Temperieren.json");
        var aml = FpbJsonToCaex.Convert(json).Value;

        // Capture an existing element ID and then submit an empty payload to force orphaning.
        // Simulate "delete every element from the FPB.JS side" by passing an empty entry.
        using var origDoc = JsonDocument.Parse(json);
        var firstProcId = origDoc.RootElement[1].GetProperty("process").GetProperty("id").GetString();
        var stripped =
            $"[{{\"$type\":\"fpb:Project\",\"name\":\"Test\",\"entryPoint\":\"{firstProcId}\"}}," +
            $"{{\"process\":{{\"$type\":\"fpb:Process\",\"id\":\"{firstProcId}\"}}," +
            "\"elementDataInformation\":[],\"elementVisualInformation\":[]}]";

        var beforeFpdCount = CountFpdInternalElements(aml.CAEXFile.InstanceHierarchy.First());
        FpbJsonToCaex.UpdateInPlace(aml, stripped);
        var afterFpdCount = CountFpdInternalElements(aml.CAEXFile.InstanceHierarchy.First());

        Assert.True(afterFpdCount < beforeFpdCount,
            $"Expected FPD element count to drop after orphaning. before={beforeFpdCount} after={afterFpdCount}");
    }

    private static InternalElementType? FindFirstByRefSuc(InstanceHierarchyType ih, string suc) =>
        WalkAll(ih.InternalElement).FirstOrDefault(ie => ie.RefBaseSystemUnitPath == suc);

    private static IEnumerable<InternalElementType> WalkAll(IEnumerable<InternalElementType> roots)
    {
        foreach (var ie in roots)
        {
            yield return ie;
            foreach (var c in WalkAll(ie.InternalElement)) yield return c;
        }
    }

    private static int CountAllInternalElements(InstanceHierarchyType ih) =>
        WalkAll(ih.InternalElement).Count();

    private static int CountFpdInternalElements(InstanceHierarchyType ih)
    {
        var fpdSucs = new HashSet<string>(ElementToSuc.Values);
        return WalkAll(ih.InternalElement)
            .Count(ie => ie.RefBaseSystemUnitPath is { } s && fpdSucs.Contains(s));
    }
}

public class UpdateInPlacePhase2EFTests
{
    private static string LoadTestData(string name) =>
        File.ReadAllText(Path.Combine("TestData", name));

    [Fact]
    public void UpdateInPlace_StripConnections_RemovesInternalLinks()
    {
        var json = LoadTestData("Temperieren.json");
        var aml = FpbJsonToCaex.Convert(json).Value;

        var beforeLinks = CountAllInternalLinks(aml.CAEXFile.InstanceHierarchy.First());
        Assert.True(beforeLinks > 0, "Test setup: original document must have at least one flow.");

        var stripped = StripConnectionsFromJson(CaexToFpbJson.Convert(aml).Value);
        FpbJsonToCaex.UpdateInPlace(aml, stripped);

        var afterLinks = CountAllInternalLinks(aml.CAEXFile.InstanceHierarchy.First());
        Assert.True(afterLinks < beforeLinks,
            $"Expected connection count to drop after orphaning. before={beforeLinks} after={afterLinks}");
    }

    [Fact]
    public void UpdateInPlace_NoChanges_PreservesInternalLinkCount()
    {
        var json = LoadTestData("Temperieren.json");
        var aml = FpbJsonToCaex.Convert(json).Value;

        var before = CountAllInternalLinks(aml.CAEXFile.InstanceHierarchy.First());
        FpbJsonToCaex.UpdateInPlace(aml, CaexToFpbJson.Convert(aml).Value);
        var after = CountAllInternalLinks(aml.CAEXFile.InstanceHierarchy.First());

        Assert.Equal(before, after);
    }

    private static int CountAllInternalLinks(InstanceHierarchyType ih)
    {
        int n = 0;
        WalkAll(ih.InternalElement, ie => n += ie.InternalLink.Count());
        return n;
    }

    private static void WalkAll(IEnumerable<InternalElementType> roots, Action<InternalElementType> visit)
    {
        foreach (var ie in roots)
        {
            visit(ie);
            WalkAll(ie.InternalElement, visit);
        }
    }

    private static string StripConnectionsFromJson(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var connectionTypes = new HashSet<string>(ConnectionTypes, StringComparer.OrdinalIgnoreCase);

        var rebuilt = new List<object>();
        foreach (var entry in doc.RootElement.EnumerateArray())
        {
            if (!entry.TryGetProperty("elementDataInformation", out var edi))
            {
                rebuilt.Add(JsonSerializer.Deserialize<object>(entry.GetRawText())!);
                continue;
            }

            var dict = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(entry.GetRawText())!;
            var keptElems = new List<JsonElement>();
            foreach (var elem in edi.EnumerateArray())
            {
                var t = elem.TryGetProperty("$type", out var tp) ? tp.GetString() ?? "" : "";
                if (!connectionTypes.Contains(t)) keptElems.Add(elem);
            }
            dict["elementDataInformation"] = JsonSerializer.SerializeToElement(keptElems);
            rebuilt.Add(dict);
        }
        return JsonSerializer.Serialize(rebuilt);
    }
}

// ────────────────────────────────────────────────────────────────────────────
// Edit-sync scenario coverage — exercises every branch of UpdateInPlace against
// realistic round-trip JSON derived from Temperieren.aml.
// ────────────────────────────────────────────────────────────────────────────
public class UpdateInPlaceEditTests
{
    private static string LoadTestData(string name) =>
        File.ReadAllText(Path.Combine("TestData", name));

    private static CAEXDocument FreshAml() =>
        FpbJsonToCaex.Convert(LoadTestData("Temperieren.json")).Value;

    private static string RoundtripJson(CAEXDocument doc) =>
        CaexToFpbJson.Convert(doc).Value;

    [Fact]
    public void UpdateInPlace_ChangePosition_UpdatesViewInformation()
    {
        var aml = FreshAml();
        var rt = RoundtripJson(aml);

        // Pick the first state in entry[1].elementVisualInformation and move it.
        const double newX = 4242.0;
        const double newY = 5353.0;
        string? targetId = null;
        var modified = MutateJson(rt, root =>
        {
            var visuals = root[1]!["elementVisualInformation"]!.AsArray();
            foreach (var v in visuals)
            {
                if (v?["x"] is null) continue;
                targetId = v["id"]!.GetValue<string>();
                v["x"] = newX;
                v["y"] = newY;
                break;
            }
        });
        Assert.NotNull(targetId);

        FpbJsonToCaex.UpdateInPlace(aml, modified);

        var ie = FindByBareId(aml.CAEXFile.InstanceHierarchy.First(), targetId!);
        Assert.NotNull(ie);
        var (x, y) = GetIePosition(ie!);
        Assert.Equal(newX, x);
        Assert.Equal(newY, y);
    }

    [Fact]
    public void UpdateInPlace_ChangeName_UpdatesIdentification()
    {
        var aml = FreshAml();
        var rt = RoundtripJson(aml);

        // Rename the first PO. Capture its id so we can look it up afterwards.
        string? targetId = null;
        const string newName = "RENAMED_BY_TEST";
        var modified = MutateJson(rt, root =>
        {
            var data = root[1]!["elementDataInformation"]!.AsArray();
            foreach (var d in data)
            {
                if (d!["$type"]!.GetValue<string>() != "fpb:ProcessOperator") continue;
                targetId = d["id"]!.GetValue<string>();
                d["name"] = newName;
                if (d["identification"] is JsonObject ident)
                    ident["shortName"] = newName;
                break;
            }
        });
        Assert.NotNull(targetId);

        FpbJsonToCaex.UpdateInPlace(aml, modified);

        var ie = FindByBareId(aml.CAEXFile.InstanceHierarchy.First(), targetId!);
        Assert.NotNull(ie);
        Assert.Equal(newName, ie!.Name);
        var shortName = GetSubAttrValue(ie, "Identification", "shortName");
        Assert.Equal(newName, shortName);
    }

    [Fact]
    public void UpdateInPlace_RemoveElement_DropsFpdInternalElement()
    {
        var aml = FreshAml();
        var rt = RoundtripJson(aml);

        // Drop the first fpb:Energy element from the payload.
        string? targetId = null;
        var modified = MutateJson(rt, root =>
        {
            var data = root[1]!["elementDataInformation"]!.AsArray();
            for (int i = 0; i < data.Count; i++)
            {
                if (data[i]!["$type"]!.GetValue<string>() == "fpb:Energy")
                {
                    targetId = data[i]!["id"]!.GetValue<string>();
                    data.RemoveAt(i);
                    return;
                }
            }
        });
        Assert.NotNull(targetId);

        FpbJsonToCaex.UpdateInPlace(aml, modified);

        Assert.Null(FindByBareId(aml.CAEXFile.InstanceHierarchy.First(), targetId!));
    }

    [Fact]
    public void UpdateInPlace_NewElement_GetsCharacteristics()
    {
        var aml = FreshAml();
        var rt = RoundtripJson(aml);

        var newId = Guid.NewGuid().ToString();
        var modified = MutateJson(rt, root =>
        {
            var data = root[1]!["elementDataInformation"]!.AsArray();
            data.Add(new JsonObject
            {
                ["$type"] = "fpb:Product",
                ["id"] = newId,
                ["name"] = "NEW_PRODUCT",
                ["identification"] = new JsonObject
                {
                    ["$type"] = "fpb:Identification",
                    ["uniqueIdent"] = newId,
                    ["shortName"] = "NEW_PRODUCT",
                    ["longName"] = "",
                    ["versionNumber"] = "",
                    ["revisionNumber"] = "",
                },
                ["characteristics"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["$type"] = "fpb:Characteristic",
                        // Characteristic in fpb-moddle is structured: category +
                        // descriptiveElement + relationalElement, each their own object.
                        ["category"] = new JsonObject
                        {
                            ["uniqueIdent"] = "test-cat",
                            ["shortName"] = "Mass",
                            ["longName"] = "",
                            ["versionNumber"] = "",
                            ["revisionNumber"] = "",
                        },
                        ["descriptiveElement"] = new JsonObject
                        {
                            ["valueDeterminationProcess"] = "measured",
                            ["representivity"] = "",
                            ["setpointValue"] = "12.5",
                            ["actualValue"] = "12.4",
                            ["unit"] = "kg",
                        },
                    },
                },
            });
        });

        FpbJsonToCaex.UpdateInPlace(aml, modified);

        var ie = FindByBareId(aml.CAEXFile.InstanceHierarchy.First(), newId);
        Assert.NotNull(ie);
        Assert.Equal("NEW_PRODUCT", ie!.Name);
        // The Characteristics attribute should now exist (Phase 2.D P2 #3 fix).
        Assert.NotNull(ie.Attribute["Characteristics"]);
    }

    [Fact]
    public void UpdateInPlace_ChangeWaypoint_UpdatesWaypointAttr()
    {
        var aml = FreshAml();
        var rt = RoundtripJson(aml);

        const double newX = 7777.0;
        const double newY = 8888.0;
        string? flowId = null;
        var modified = MutateJson(rt, root =>
        {
            var visuals = root[1]!["elementVisualInformation"]!.AsArray();
            foreach (var v in visuals)
            {
                if (v?["waypoints"] is not JsonArray wps || wps.Count < 3) continue;
                flowId = v["id"]!.GetValue<string>();
                // Move the FIRST intermediate waypoint (index 1).
                var wp = wps[1] as JsonObject;
                if (wp == null) continue;
                wp["x"] = newX;
                wp["y"] = newY;
                // Don't keep an "original" — that would tell the mapper to ignore the move.
                wp.Remove("original");
                break;
            }
        });
        Assert.NotNull(flowId);

        FpbJsonToCaex.UpdateInPlace(aml, modified);

        var link = FindLinkById(aml.CAEXFile.InstanceHierarchy.First(), flowId!);
        Assert.NotNull(link);
        var srcIface = link!.AInterface as ExternalInterfaceType;
        Assert.NotNull(srcIface);
        var wpAttr = srcIface!.Attribute["Waypoint_1"];
        Assert.NotNull(wpAttr);
        var posAttr = wpAttr!.Attribute["position"];
        Assert.NotNull(posAttr);
        Assert.Equal(newX.ToString(System.Globalization.CultureInfo.InvariantCulture),
                     posAttr!.Attribute["x"]?.Value);
        Assert.Equal(newY.ToString(System.Globalization.CultureInfo.InvariantCulture),
                     posAttr.Attribute["y"]?.Value);
    }

    [Fact]
    public void UpdateInPlace_EndpointSwap_RecreatesLink()
    {
        var aml = FreshAml();
        var rt = RoundtripJson(aml);

        string? flowId = null;
        string? originalSource = null;
        string? originalTarget = null;
        var modified = MutateJson(rt, root =>
        {
            var data = root[1]!["elementDataInformation"]!.AsArray();
            foreach (var d in data)
            {
                var t = d?["$type"]?.GetValue<string>() ?? "";
                if (t != "fpb:Flow" && t != "fpb:Usage" && t != "fpb:ParallelFlow" && t != "fpb:AlternativeFlow")
                    continue;
                flowId = d!["id"]!.GetValue<string>();
                originalSource = d["sourceRef"]?.GetValue<string>();
                originalTarget = d["targetRef"]?.GetValue<string>();
                // Swap.
                d["sourceRef"] = originalTarget;
                d["targetRef"] = originalSource;
                break;
            }
        });
        Assert.NotNull(flowId);

        FpbJsonToCaex.UpdateInPlace(aml, modified);

        var link = FindLinkById(aml.CAEXFile.InstanceHierarchy.First(), flowId!);
        Assert.NotNull(link);
        // After recreate the link's A-side interface must live on the NEW source IE
        // (originalTarget). We check by collection membership rather than CAEXParent
        // identity because Aml.Engine may return a fresh wrapper on each Parent read.
        var aside = link!.AInterface as ExternalInterfaceType;
        var newSourceIE = FindByBareId(aml.CAEXFile.InstanceHierarchy.First(), originalTarget!);
        Assert.NotNull(aside);
        Assert.NotNull(newSourceIE);
        Assert.True(
            newSourceIE!.ExternalInterface.Any(ei => string.Equals(ei.ID, aside!.ID, StringComparison.OrdinalIgnoreCase)),
            $"After swap, the NEW source IE ({originalTarget}) should host the link's A-side interface.");
    }

    [Fact]
    public void UpdateInPlace_DecomposeCycle_YieldsStableSubProcessAmlId()
    {
        // Step 1: convert from JSON — the existing sub-process IE in Temperieren
        // already has some AML ID. We want to verify the cycle behaviour for a
        // fresh decompose, not for the pre-existing one. Pick an arbitrary PO
        // that has NO decomposedView, then synthesise an entry that decomposes it.
        var aml = FreshAml();
        var rt = RoundtripJson(aml);

        // Find a candidate parent PO (one that's currently NOT decomposed).
        string? candidateParentPoId = null;
        using (var doc = JsonDocument.Parse(rt))
        {
            foreach (var entry in doc.RootElement.EnumerateArray())
            {
                if (!entry.TryGetProperty("elementDataInformation", out var edi)) continue;
                foreach (var d in edi.EnumerateArray())
                {
                    if (d.TryGetProperty("$type", out var t)
                        && t.GetString() == "fpb:ProcessOperator"
                        && (!d.TryGetProperty("decomposedView", out var dv)
                            || dv.ValueKind == JsonValueKind.Null
                            || string.IsNullOrEmpty(dv.GetString())))
                    {
                        candidateParentPoId = d.GetProperty("id").GetString();
                        break;
                    }
                }
                if (candidateParentPoId != null) break;
            }
        }
        Assert.NotNull(candidateParentPoId);

        // Build a modified payload that decomposes the candidate PO by adding a new
        // process entry plus pointing the PO's decomposedView at it.
        var decompose = AppendSubProcessEntry(rt, candidateParentPoId!);
        FpbJsonToCaex.UpdateInPlace(aml, decompose);

        var firstSub = FindSubProcessByRefObj(aml.CAEXFile.InstanceHierarchy.First(), candidateParentPoId!);
        Assert.NotNull(firstSub);
        var firstId = firstSub!.ID;

        // Now compose it back (remove the sub-process entry + reset decomposedView).
        var compose = ResetDecompose(rt, candidateParentPoId!);
        FpbJsonToCaex.UpdateInPlace(aml, compose);
        Assert.Null(FindSubProcessByRefObj(aml.CAEXFile.InstanceHierarchy.First(), candidateParentPoId!));

        // Re-decompose. The sub-process IE should come back with the SAME AML ID.
        FpbJsonToCaex.UpdateInPlace(aml, decompose);
        var secondSub = FindSubProcessByRefObj(aml.CAEXFile.InstanceHierarchy.First(), candidateParentPoId!);
        Assert.NotNull(secondSub);
        Assert.Equal(firstId, secondSub!.ID);
    }

    [Fact]
    public void UpdateInPlace_PreservesIdsAcrossMultipleCalls()
    {
        var aml = FreshAml();
        var rt = RoundtripJson(aml);

        // 5 idempotent re-applications should leave the AML element count unchanged.
        var ih = aml.CAEXFile.InstanceHierarchy.First();
        var beforeCount = CountAllIes(ih);
        for (int i = 0; i < 5; i++)
            FpbJsonToCaex.UpdateInPlace(aml, rt);
        Assert.Equal(beforeCount, CountAllIes(ih));
    }

    // ── helpers ───────────────────────────────────────────────────────────

    private static string MutateJson(string json, Action<JsonArray> mutate)
    {
        var node = JsonNode.Parse(json)!.AsArray();
        mutate(node);
        return node.ToJsonString();
    }

    private static InternalElementType? FindByBareId(InstanceHierarchyType ih, string bareId) =>
        WalkAll(ih.InternalElement).FirstOrDefault(ie =>
        {
            var aml = ie.ID ?? "";
            if (aml.Length >= 2 && aml[0] == '{' && aml[^1] == '}')
                aml = aml.Substring(1, aml.Length - 2);
            return string.Equals(aml, bareId, StringComparison.OrdinalIgnoreCase);
        });

    private static InternalLinkType? FindLinkById(InstanceHierarchyType ih, string bareId)
    {
        InternalLinkType? hit = null;
        WalkAll(ih.InternalElement, ie =>
        {
            foreach (var l in ie.InternalLink)
            {
                var lid = l.ID ?? "";
                if (lid.Length >= 2 && lid[0] == '{' && lid[^1] == '}')
                    lid = lid.Substring(1, lid.Length - 2);
                if (string.Equals(lid, bareId, StringComparison.OrdinalIgnoreCase))
                {
                    hit = l;
                    return;
                }
            }
        });
        return hit;
    }

    private static IEnumerable<InternalElementType> WalkAll(IEnumerable<InternalElementType> roots)
    {
        foreach (var ie in roots)
        {
            yield return ie;
            foreach (var c in WalkAll(ie.InternalElement)) yield return c;
        }
    }

    private static void WalkAll(IEnumerable<InternalElementType> roots, Action<InternalElementType> visit)
    {
        foreach (var ie in roots)
        {
            visit(ie);
            WalkAll(ie.InternalElement, visit);
        }
    }

    private static int CountAllIes(InstanceHierarchyType ih) => WalkAll(ih.InternalElement).Count();

    private static (double x, double y) GetIePosition(InternalElementType ie)
    {
        var view = ie.Attribute["ViewInformation"];
        if (view == null) return (double.NaN, double.NaN);
        var pos = view.Attribute["position"];
        if (pos == null) return (double.NaN, double.NaN);
        var x = pos.Attribute["x"]?.Value ?? "";
        var y = pos.Attribute["y"]?.Value ?? "";
        return (
            double.Parse(x, System.Globalization.CultureInfo.InvariantCulture),
            double.Parse(y, System.Globalization.CultureInfo.InvariantCulture));
    }

    private static string? GetSubAttrValue(InternalElementType ie, string attrName, string subName) =>
        ie.Attribute[attrName]?.Attribute[subName]?.Value;

    private static InternalElementType? FindSubProcessByRefObj(InstanceHierarchyType ih, string parentPoBareId)
    {
        var processSuc = ElementToSuc["fpb:Process"];
        var wrapped = "{" + parentPoBareId + "}";
        return ih.InternalElement.FirstOrDefault(ie =>
            ie.RefBaseSystemUnitPath == processSuc
            && string.Equals(ie.Attribute["refObj"]?.Value, wrapped, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Patch the JSON: flip <paramref name="parentPoId"/>'s decomposedView, append
    /// a fresh sub-process entry with a SystemLimit so AddProcess + AddElement
    /// land it correctly.
    /// </summary>
    private static string AppendSubProcessEntry(string json, string parentPoId)
    {
        var node = JsonNode.Parse(json)!.AsArray();
        // Patch the PO's decomposedView in entry[1] (top-level process).
        var data = node[1]!["elementDataInformation"]!.AsArray();
        foreach (var d in data)
        {
            if (d?["id"]?.GetValue<string>() == parentPoId)
            {
                d["decomposedView"] = parentPoId;
                break;
            }
        }
        // Append a new process entry whose Process.id == parentPoId (FPB.JS convention)
        // and that holds a single SystemLimit.
        var slId = Guid.NewGuid().ToString();
        node.Add(new JsonObject
        {
            ["process"] = new JsonObject
            {
                ["$type"] = "fpb:Process",
                ["id"] = parentPoId,
                ["isDecomposedProcessOperator"] = parentPoId,
                ["elementsContainer"] = new JsonArray { slId },
                ["consistsOfSystemLimit"] = slId,
                ["consistsOfStates"] = new JsonArray(),
                ["consistsOfProcessOperator"] = new JsonArray(),
                ["consistsOfProcesses"] = new JsonArray(),
            },
            ["elementDataInformation"] = new JsonArray
            {
                new JsonObject
                {
                    ["$type"] = "fpb:SystemLimit",
                    ["id"] = slId,
                    ["name"] = "Sub_SL",
                    ["elementsContainer"] = new JsonArray(),
                },
            },
            ["elementVisualInformation"] = new JsonArray
            {
                new JsonObject
                {
                    ["id"] = slId,
                    ["type"] = "fpb:SystemLimit",
                    ["x"] = 0,
                    ["y"] = 0,
                    ["width"] = 400,
                    ["height"] = 400,
                },
            },
        });
        return node.ToJsonString();
    }

    private static string ResetDecompose(string json, string parentPoId)
    {
        var node = JsonNode.Parse(json)!.AsArray();
        var data = node[1]!["elementDataInformation"]!.AsArray();
        foreach (var d in data)
        {
            if (d?["id"]?.GetValue<string>() == parentPoId)
            {
                d["decomposedView"] = null;
                break;
            }
        }
        return node.ToJsonString();
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
