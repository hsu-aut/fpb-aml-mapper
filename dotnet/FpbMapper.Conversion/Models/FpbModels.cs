using System.Text.Json;
using System.Text.Json.Serialization;

namespace FpbMapper.Conversion.Models;

/// <summary>
/// FPB.JS JSON data models. The top-level JSON is a heterogeneous array:
///   [FpbProject, ProcessEntry, ProcessEntry, ...]
/// Parsed manually via JsonDocument due to the mixed types.
/// </summary>
public record FpbProject
{
    [JsonPropertyName("$type")]  public string Type { get; set; } = "fpb:Project";
    [JsonPropertyName("name")]   public string Name { get; set; } = "";
    [JsonPropertyName("targetNamespace")] public string TargetNamespace { get; set; } = "http://www.hsu-ifa.de/fpbjs";
    [JsonPropertyName("entryPoint")]      public string EntryPoint { get; set; } = "";
}

public record FpbProcess
{
    [JsonPropertyName("$type")] public string Type { get; set; } = "fpb:Process";
    [JsonPropertyName("id")]    public string Id { get; set; } = "";
    [JsonPropertyName("isDecomposedProcessOperator")] public string? IsDecomposedProcessOperator { get; set; }
    [JsonPropertyName("parent")] public string? Parent { get; set; }
    [JsonPropertyName("consistsOfStates")] public List<string> ConsistsOfStates { get; set; } = new();
    [JsonPropertyName("consistsOfSystemLimit")] public string? ConsistsOfSystemLimit { get; set; }
    [JsonPropertyName("consistsOfProcesses")] public List<string> ConsistsOfProcesses { get; set; } = new();
    [JsonPropertyName("consistsOfProcessOperator")] public List<string> ConsistsOfProcessOperator { get; set; } = new();
    [JsonPropertyName("elementsContainer")] public List<string> ElementsContainer { get; set; } = new();
}

public record ProcessEntry
{
    [JsonPropertyName("process")] public FpbProcess Process { get; set; } = new();
    [JsonPropertyName("elementDataInformation")] public List<JsonElement> ElementDataRaw { get; set; } = new();
    [JsonPropertyName("elementVisualInformation")] public List<JsonElement> ElementVisualRaw { get; set; } = new();

    // Parsed from raw JSON
    [JsonIgnore] public List<ElementData> ElementData { get; set; } = new();
    [JsonIgnore] public List<VisualInfo> ElementVisual { get; set; } = new();
}

public class ElementData
{
    public string Type { get; set; } = "";       // $type
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string? SourceRef { get; set; }       // flows
    public string? TargetRef { get; set; }       // flows
    public string? DecomposedView { get; set; }  // PO
    public List<string> InTandemWith { get; set; } = new();
    public Identification? Identification { get; set; }
    public List<Characteristic> Characteristics { get; set; } = new();
}

public class Identification
{
    public string UniqueIdent { get; set; } = "";
    public string LongName { get; set; } = "";
    public string ShortName { get; set; } = "";
    public string VersionNumber { get; set; } = "";
    public string RevisionNumber { get; set; } = "";
}

public class Characteristic
{
    public CategoryInfo? Category { get; set; }
    public DescriptiveElement? DescriptiveElement { get; set; }
    public RelationalElement? RelationalElement { get; set; }
}

public class CategoryInfo
{
    public string UniqueIdent { get; set; } = "";
    public string LongName { get; set; } = "";
    public string ShortName { get; set; } = "";
    public string VersionNumber { get; set; } = "";
    public string RevisionNumber { get; set; } = "";
}

public class DescriptiveElement
{
    public string ValueDeterminationProcess { get; set; } = "";
    public string Representivity { get; set; } = "";
    public string SetpointValue { get; set; } = "";
    public string ValidityLimits { get; set; } = "";
    public string ActualValues { get; set; } = "";
}

public class RelationalElement
{
    public string View { get; set; } = "";
    public string Model { get; set; } = "";
    public string RegulationsForRelationalGeneration { get; set; } = "";
}

public class VisualInfo
{
    public string Id { get; set; } = "";
    public string Type { get; set; } = "";
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
    public List<WaypointInfo> Waypoints { get; set; } = new();
}

public class WaypointInfo
{
    public double X { get; set; }
    public double Y { get; set; }
    public WaypointInfo? Original { get; set; }
}

/// <summary>
/// Parser for the heterogeneous FPB.JS JSON array.
/// </summary>
public static class FpbJsonParser
{
    public static (FpbProject Project, List<ProcessEntry> Entries) Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Array)
            throw new ArgumentException("Expected JSON array");

        FpbProject? project = null;
        var entries = new List<ProcessEntry>();

        foreach (var elem in root.EnumerateArray())
        {
            if (elem.TryGetProperty("$type", out var typeEl) && typeEl.GetString() == "fpb:Project")
            {
                project = new FpbProject
                {
                    Name = elem.GetStringProp("name"),
                    TargetNamespace = elem.GetStringProp("targetNamespace"),
                    EntryPoint = elem.GetStringProp("entryPoint"),
                };
            }
            else if (elem.TryGetProperty("process", out _))
            {
                var entry = ParseProcessEntry(elem);
                entries.Add(entry);
            }
        }

        return (project ?? throw new ArgumentException("No fpb:Project found"), entries);
    }

    private static ProcessEntry ParseProcessEntry(JsonElement elem)
    {
        var procEl = elem.GetProperty("process");
        var process = new FpbProcess
        {
            Id = procEl.GetStringProp("id"),
            IsDecomposedProcessOperator = procEl.GetStringPropOrNull("isDecomposedProcessOperator"),
            Parent = procEl.GetStringPropOrNull("parent"),
            ConsistsOfSystemLimit = procEl.GetStringPropOrNull("consistsOfSystemLimit"),
        };
        if (procEl.TryGetProperty("consistsOfStates", out var sts))
            process.ConsistsOfStates = sts.EnumerateArray().Select(e => e.GetString()!).ToList();
        if (procEl.TryGetProperty("consistsOfProcessOperator", out var pos))
            process.ConsistsOfProcessOperator = pos.EnumerateArray().Select(e => e.GetString()!).ToList();

        var entry = new ProcessEntry { Process = process };

        // Parse elementDataInformation
        if (elem.TryGetProperty("elementDataInformation", out var ediArr))
        {
            foreach (var edi in ediArr.EnumerateArray())
                entry.ElementData.Add(ParseElementData(edi));
        }

        // Parse elementVisualInformation
        if (elem.TryGetProperty("elementVisualInformation", out var eviArr))
        {
            foreach (var evi in eviArr.EnumerateArray())
                entry.ElementVisual.Add(ParseVisualInfo(evi));
        }

        return entry;
    }

    private static ElementData ParseElementData(JsonElement el)
    {
        var data = new ElementData
        {
            Type = el.GetStringProp("$type"),
            Id = el.GetStringProp("id"),
            Name = el.GetStringProp("name"),
            SourceRef = el.GetStringPropOrNull("sourceRef"),
            TargetRef = el.GetStringPropOrNull("targetRef"),
            DecomposedView = el.GetStringPropOrNull("decomposedView"),
        };

        if (el.TryGetProperty("inTandemWith", out var itw))
            data.InTandemWith = itw.EnumerateArray().Select(e => e.GetString()!).ToList();

        if (el.TryGetProperty("identification", out var identEl))
            data.Identification = ParseIdentification(identEl);

        if (el.TryGetProperty("characteristics", out var charArr))
        {
            foreach (var c in charArr.EnumerateArray())
                data.Characteristics.Add(ParseCharacteristic(c));
        }

        return data;
    }

    private static Identification ParseIdentification(JsonElement el)
    {
        return new Identification
        {
            UniqueIdent = el.GetStringProp("uniqueIdent"),
            LongName = el.GetStringProp("longName"),
            ShortName = el.GetStringProp("shortName"),
            VersionNumber = el.GetStringProp("versionNumber"),
            RevisionNumber = el.GetStringProp("revisionNumber"),
        };
    }

    private static Characteristic ParseCharacteristic(JsonElement el)
    {
        var c = new Characteristic();

        if (el.TryGetProperty("category", out var catEl))
        {
            c.Category = new CategoryInfo
            {
                UniqueIdent = catEl.GetStringProp("uniqueIdent"),
                LongName = catEl.GetStringProp("longName"),
                ShortName = catEl.GetStringProp("shortName"),
                VersionNumber = catEl.GetStringProp("versionNumber"),
                RevisionNumber = catEl.GetStringProp("revisionNumber"),
            };
        }

        if (el.TryGetProperty("descriptiveElement", out var descEl))
        {
            c.DescriptiveElement = new DescriptiveElement
            {
                ValueDeterminationProcess = descEl.GetStringProp("valueDeterminationProcess"),
                Representivity = descEl.GetStringProp("representivity"),
                SetpointValue = descEl.GetStringProp("setpointValue"),
                ValidityLimits = descEl.GetStringProp("validityLimits"),
                ActualValues = descEl.GetStringProp("actualValues"),
            };
        }

        if (el.TryGetProperty("relationalElement", out var relEl))
        {
            c.RelationalElement = new RelationalElement
            {
                View = relEl.GetStringProp("view"),
                Model = relEl.GetStringProp("model"),
                RegulationsForRelationalGeneration = relEl.GetStringProp("regulationsForRelationalGeneration"),
            };
        }

        return c;
    }

    private static VisualInfo ParseVisualInfo(JsonElement el)
    {
        var vi = new VisualInfo
        {
            Id = el.GetStringProp("id"),
            Type = el.GetStringProp("type"),
            X = el.GetDoubleProp("x"),
            Y = el.GetDoubleProp("y"),
            Width = el.GetDoubleProp("width"),
            Height = el.GetDoubleProp("height"),
        };

        if (el.TryGetProperty("waypoints", out var wpArr))
        {
            foreach (var wp in wpArr.EnumerateArray())
            {
                var wpi = new WaypointInfo
                {
                    X = wp.GetDoubleProp("x"),
                    Y = wp.GetDoubleProp("y"),
                };
                if (wp.TryGetProperty("original", out var orig))
                {
                    wpi.Original = new WaypointInfo
                    {
                        X = orig.GetDoubleProp("x"),
                        Y = orig.GetDoubleProp("y"),
                    };
                }
                vi.Waypoints.Add(wpi);
            }
        }

        return vi;
    }

    // Extension helpers
    private static string GetStringProp(this JsonElement el, string name)
    {
        if (el.TryGetProperty(name, out var val) && val.ValueKind == JsonValueKind.String)
            return val.GetString() ?? "";
        return "";
    }

    private static string? GetStringPropOrNull(this JsonElement el, string name)
    {
        if (el.TryGetProperty(name, out var val) && val.ValueKind == JsonValueKind.String)
        {
            var s = val.GetString();
            return string.IsNullOrEmpty(s) ? null : s;
        }
        return null;
    }

    private static double GetDoubleProp(this JsonElement el, string name)
    {
        if (el.TryGetProperty(name, out var val))
        {
            if (val.ValueKind == JsonValueKind.Number) return val.GetDouble();
            if (val.ValueKind == JsonValueKind.String && double.TryParse(val.GetString(), out var d)) return d;
        }
        return 0;
    }
}
