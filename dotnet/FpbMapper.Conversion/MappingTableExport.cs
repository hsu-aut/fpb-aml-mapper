using System.Text.Json;
using System.Text.Json.Serialization;

namespace FpbMapper.Conversion;

/// <summary>
/// Machine-readable dump of the mapper's static mapping table — every
/// FPB.js type ↔ AML SUC, every flow ↔ InterfaceClass, every reference-type
/// attribute, plus the AttributeType references and library names. Intended
/// for paper appendices, audits, and external schema-conformance reviews.
/// </summary>
public sealed class MappingTableExport
{
    [JsonPropertyName("schema")]              public string Schema { get; init; } = "amlfpbjs.mapping_table/v1";
    [JsonPropertyName("library_version")]     public string LibraryVersion { get; init; } = "";
    [JsonPropertyName("element_mappings")]    public List<ElementMappingRow> ElementMappings { get; init; } = new();
    [JsonPropertyName("flow_mappings")]       public List<FlowMappingRow> FlowMappings { get; init; } = new();
    [JsonPropertyName("reference_types")]     public List<ReferenceTypeRow> ReferenceTypeRows { get; init; } = new();
    [JsonPropertyName("attribute_type_refs")] public Dictionary<string, string> AttributeTypeRefs { get; init; } = new();
    [JsonPropertyName("library_names")]       public Dictionary<string, string> LibraryNames { get; init; } = new();

    public sealed class ElementMappingRow
    {
        [JsonPropertyName("fpb_type")]   public string FpbType { get; init; } = "";
        [JsonPropertyName("aml_suc")]    public string AmlSucPath { get; init; } = "";
        [JsonPropertyName("is_state")]   public bool IsState { get; init; }
        [JsonPropertyName("is_object")]  public bool IsObject { get; init; }
    }

    public sealed class FlowMappingRow
    {
        [JsonPropertyName("fpb_type")]                public string FpbType { get; init; } = "";
        [JsonPropertyName("interface_out_path")]      public string InterfaceOutPath { get; init; } = "";
        [JsonPropertyName("interface_in_path")]       public string InterfaceInPath { get; init; } = "";
        [JsonPropertyName("symmetric")]               public bool Symmetric { get; init; }
    }

    public sealed class ReferenceTypeRow
    {
        [JsonPropertyName("attribute_name")]    public string AttributeName { get; init; } = "";
        [JsonPropertyName("attribute_type_path")] public string AttributeTypePath { get; init; } = "";
        [JsonPropertyName("parent")]            public string? Parent { get; init; }
        [JsonPropertyName("allows_heterogeneous_target")] public bool AllowsHeterogeneousTarget { get; init; }
    }

    public string ToJson() => JsonSerializer.Serialize(this, new JsonSerializerOptions
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    });

    /// <summary>Build the export from the current in-memory mapping definitions.</summary>
    public static MappingTableExport Build()
    {
        return new MappingTableExport
        {
            LibraryVersion = FpbMappings.LibNames.Version,
            ElementMappings = FpbMappings.ElementToSuc.Select(kv => new ElementMappingRow
            {
                FpbType    = kv.Key,
                AmlSucPath = kv.Value,
                IsState    = FpbMappings.StateTypes.Contains(kv.Key),
                IsObject   = FpbMappings.ObjectTypes.Contains(kv.Key),
            }).ToList(),
            FlowMappings = FpbMappings.FlowToInterface.Select(kv => new FlowMappingRow
            {
                FpbType          = kv.Key,
                InterfaceOutPath = kv.Value.Out,
                InterfaceInPath  = kv.Value.In,
                Symmetric        = kv.Value.Out == kv.Value.In,
            }).ToList(),
            ReferenceTypeRows = ReferenceTypes.All.Select(rt => new ReferenceTypeRow
            {
                AttributeName             = rt.AttributeName,
                AttributeTypePath         = rt.AttributeTypePath,
                Parent                    = rt.Parent?.AttributeName,
                AllowsHeterogeneousTarget = rt.AllowsHeterogeneousTargetType,
            }).ToList(),
            AttributeTypeRefs = new Dictionary<string, string>
            {
                [nameof(FpbMappings.AttrRefs.Identification)] = FpbMappings.AttrRefs.Identification,
                [nameof(FpbMappings.AttrRefs.Characteristic)] = FpbMappings.AttrRefs.Characteristic,
                [nameof(FpbMappings.AttrRefs.RefObj)]         = FpbMappings.AttrRefs.RefObj,
                [nameof(FpbMappings.AttrRefs.Bounds)]         = FpbMappings.AttrRefs.Bounds,
                [nameof(FpbMappings.AttrRefs.Point)]          = FpbMappings.AttrRefs.Point,
                [nameof(FpbMappings.AttrRefs.Waypoint)]       = FpbMappings.AttrRefs.Waypoint,
            },
            LibraryNames = new Dictionary<string, string>
            {
                [nameof(FpbMappings.LibNames.InterfaceClassLib)]  = FpbMappings.LibNames.InterfaceClassLib,
                [nameof(FpbMappings.LibNames.RoleClassLib)]       = FpbMappings.LibNames.RoleClassLib,
                [nameof(FpbMappings.LibNames.SystemUnitClassLib)] = FpbMappings.LibNames.SystemUnitClassLib,
                [nameof(FpbMappings.LibNames.AttributeTypeLib)]   = FpbMappings.LibNames.AttributeTypeLib,
                [nameof(FpbMappings.LibNames.DIAttributeTypeLib)] = FpbMappings.LibNames.DIAttributeTypeLib,
            },
        };
    }
}
