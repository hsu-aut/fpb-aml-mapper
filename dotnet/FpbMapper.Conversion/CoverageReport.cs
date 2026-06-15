using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace FpbMapper.Conversion;

/// <summary>
/// Coverage of the VDI 3682 Blatt 2 rule catalogue by the executable rule set.
/// Each of the 60 catalogue rules is bucketed by where (and whether) it runs
/// today; consumers can emit a CSV/JSON breakdown for the conference slide or
/// pin numbers to a regression test so the catalogue, the OCL files and the
/// executable validators don't silently drift.
/// </summary>
public sealed class CoverageReport
{
    [JsonPropertyName("schema")]       public string Schema { get; init; } = "amlfpbjs.vdi3682.coverage/v1";
    [JsonPropertyName("generated_at")] public string GeneratedAt { get; init; } = "";
    [JsonPropertyName("totals")]       public Dictionary<string,int> Totals { get; init; } = new();
    [JsonPropertyName("rules")]        public List<Row> Rules { get; init; } = new();

    public sealed class Row
    {
        [JsonPropertyName("catalog_id")]      public string CatalogId { get; init; } = "";
        [JsonPropertyName("title")]           public string Title { get; init; } = "";
        [JsonPropertyName("category")]        public string Category { get; init; } = "";
        [JsonPropertyName("ocl_constraint")]  public string? OclConstraint { get; init; }
        [JsonPropertyName("bucket")]          public string Bucket { get; init; } = "";
        [JsonPropertyName("note")]            public string? Note { get; init; }
    }

    public string ToJson() => JsonSerializer.Serialize(this, new JsonSerializerOptions
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    });

    /// <summary>
    /// Render the report as a CSV (header + one row per catalogue rule plus a
    /// totals block at the bottom). The CSV is sized for direct paste into the
    /// conference slide deck — sorted by catalog id, columns selected for the
    /// stacked-bar visualisation (bucket is the colour dimension).
    /// </summary>
    public string ToCsv()
    {
        var sb = new StringBuilder();
        sb.AppendLine("catalog_id,title,category,bucket,ocl_constraint,note");
        foreach (var r in Rules)
            sb.AppendLine(string.Join(",",
                Q(r.CatalogId), Q(r.Title), Q(r.Category), Q(r.Bucket),
                Q(r.OclConstraint ?? ""), Q(r.Note ?? "")));

        sb.AppendLine();
        sb.AppendLine("# totals");
        foreach (var (k, v) in Totals.OrderBy(kv => kv.Key))
            sb.AppendLine($"# {k},{v}");
        return sb.ToString();
    }

    private static string Q(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        if (s.IndexOfAny(new[] { ',', '"', '\n', '\r' }) < 0) return s;
        return "\"" + s.Replace("\"", "\"\"") + "\"";
    }
}

public static class CoverageReportBuilder
{
    /// <summary>
    /// The 60 catalogue rules from FPB.JS_Docs/Standards/Drafts/
    /// VDI3682-Blatt3-Regelkatalog.md (snapshot 2026-06-15). Each entry pins
    /// the catalogue id to its OCL invariant name (when one exists in the
    /// catalogue) and a coarse category for the slide colour-coding.
    /// </summary>
    private static readonly (string Id, string Title, string Category, string? Inv, string? Note)[] Catalogue =
    {
        // ── A — Cardinalities ───────────────────────────────────────────────
        ("A1", "Project-Process-Kardinalität",     "Cardinality", "ProjectMinimumProcess",           null),
        ("A2", "SystemLimit-Kardinalität",         "Cardinality", "SystemLimitCardinality",          null),
        ("A3", "State-Mindest-Kardinalität",       "Cardinality", "StateMinimumCardinality",         null),
        ("A4", "ProcessOperator-Mindest-Kard.",    "Cardinality", "ProcessOperatorMinimumCardinality", null),
        ("A5", "TechnicalResource-Kardinalität",   "Cardinality", null,                              "informativ, keine Validierung"),

        // ── B — Containment & Geometry ──────────────────────────────────────
        ("B1", "State innerhalb SystemLimit",      "Geometry",    "StateWithinSystemLimit",          "Plugin-Layer"),
        ("B2", "PO innerhalb SystemLimit",         "Geometry",    "ProcessOperatorWithinSystemLimit", "Plugin-Layer"),
        ("B3", "TR außerhalb SystemLimit",         "Geometry",    "TechnicalResourceOutsideSystemLimit", "Plugin-Layer"),
        ("B4", "SystemLimit umfasst Inhalt",       "Geometry",    "SystemLimitBoundingContainer",    "Plugin-Layer"),
        ("B5", "Keine Überlappung SL ↔ TR",        "Geometry",    "SystemLimitTechnicalResourceNoOverlap", "Plugin-Layer"),
        ("B6", "Boundary-State auf SL-Rand",       "Geometry",    "BoundaryStateOnSystemLimitBorder", "Plugin-Layer"),

        // ── C — Connection typing ───────────────────────────────────────────
        ("C1",  "Flow State↔PO",                   "Connection",  "FlowEndpointsTyped",              null),
        ("C2",  "Kein Flow State→State",           "Connection",  "NoStateToStateFlow",              null),
        ("C3",  "Kein Flow PO→PO",                 "Connection",  "NoProcessOperatorToProcessOperatorFlow", null),
        ("C4",  "Usage PO↔TR",                     "Connection",  "UsageEndpointsTyped",             null),
        ("C5",  "Keine doppelten Connections",     "Connection",  "NoDuplicateConnections",          null),
        ("C6",  "Flow ist directed",               "Connection",  "FlowDirected",                    null),
        ("C7",  "Kanonische FPD-Interfaces",       "Connection",  "CanonicalFpdInterfaces",          "durch Mapper sichergestellt"),
        ("C8",  "ParallelFlow-Konsistenz",         "Connection",  null,                              "Phase 2: requires-pair semantics"),
        ("C9",  "Keine gemischten Flow-Typen",     "Connection",  "NoMixedFlowTypes",                null),
        ("C10", "AlternativeFlow-Konsistenz",      "Connection",  null,                              "Phase 2: requires-pair semantics"),

        // ── D — Identification & Naming ─────────────────────────────────────
        ("D1", "uniqueIdent-Unikalität",           "Identity",    "UniqueIdentifiers",               null),
        ("D2", "IDs unikal in Hierarchie",         "Identity",    "UniqueIdsInHierarchy",            "Phase 2: cross-IH scope"),
        ("D3", "PO benannt",                       "Identity",    "ProcessOperatorNamed",            null),
        ("D4", "State benannt",                    "Identity",    "StateNamed",                      null),
        ("D5", "TR benannt",                       "Identity",    "TechnicalResourceNamed",          null),
        ("D6", "Process benannt",                  "Identity",    "ProcessNamed",                    null),
        ("D7", "longName pflicht",                 "Identity",    "LongNameMandatory",               null),
        ("D8", "Version+Revision vorhanden",       "Identity",    "VersionRevisionPresent",          null),

        // ── E — Characteristics ─────────────────────────────────────────────
        ("E1", "Characteristic.category vorh.",    "Characteristics", "CharacteristicCategoryPresent",   "Phase-2-Binding pending"),
        ("E2", "Characteristic.descriptive vorh.", "Characteristics", "CharacteristicDescriptivePresent", "Phase-2-Binding pending"),
        ("E3", "Value entspricht DataType",        "Characteristics", "ValueConformsToDataType",         "informativ"),
        ("E4", "RelationalElement resolvbar",      "Characteristics", "RelationalElementResolvable",     "Phase-2-Binding pending"),
        ("E5", "ValidityLimits konsistent",        "Characteristics", "ValidityLimitsConsistent",        "Phase-2-Binding pending"),
        ("E6", "Boundary-State Characteristics",   "Characteristics", "BoundaryStateCharacteristicsShared", "Phase 2"),

        // ── F — References / Decomposition ──────────────────────────────────
        ("F1", "refObj auflösbar",                 "References",  "RefObjResolvable",                null),
        ("F2", "refProcess auflösbar",             "References",  "RefProcessResolvable",            null),
        ("F3", "Alle Referenzen auflösbar",        "References",  "AllReferencesResolvable",         null),
        ("F4", "Keine zirkuläre Dekomposition",    "References",  "NoCircularDecomposition",         "Phase 2"),
        ("F5", "Dekomposition erhält I/O",         "References",  "DecompositionPreservesIO",        "Phase 2"),
        ("F6", "Boundary-State-ID geteilt",        "References",  "BoundaryStateIdShared",           "Mapper-Konvention"),
        ("F7", "Dekompositions-Tiefenlimit",       "References",  "DecompositionDepthLimit",         "informativ"),

        // ── G — Semantic ────────────────────────────────────────────────────
        ("G1", "PO hat I/O",                       "Semantic",    "ProcessOperatorHasIO",            null),
        ("G2", "State hat konkreten Typ",          "Semantic",    "StateHasConcreteType",            null),
        ("G3", "Keine Selbstreferenz",             "Semantic",    "NoSelfReference",                 null),
        ("G4", "Keine verwaisten Elemente",        "Semantic",    "NoOrphanedElements",              null),
        ("G5", "SubProcess nicht leer",            "Semantic",    "SubProcessNotEmpty",              "Phase 2"),

        // ── H — Geometry / Layout ───────────────────────────────────────────
        ("H1", "Bounds vorhanden",                 "Geometry",    "BoundsPresent",                   "Plugin-Layer"),
        ("H2", "Bounds positiv",                   "Geometry",    "BoundsPositive",                  "Plugin-Layer"),
        ("H3", "Waypoints am Rand",                "Geometry",    "WaypointsAtBorder",               "Plugin-Layer"),
        ("H4", "Waypoints geordnet",               "Geometry",    "WaypointsOrdered",                "Plugin-Layer"),
        ("H5", "Positionen nicht-negativ",         "Geometry",    "PositionsNonNegative",            "Plugin-Layer"),

        // ── I — Cross-cutting ───────────────────────────────────────────────
        ("I1", "Allgemeine Strukturintegrität",    "CrossCut",    null,                              "Sammelposten"),
        ("I2", "Project-weit unikale IDs",         "CrossCut",    "ProjectWideUniqueIds",            null),
        ("I3", "Source+Target gleicher Prozess",   "CrossCut",    "SourceTargetSameProcess",         null),
        ("I4", "Konsistenz Cross-IH",              "CrossCut",    null,                              "Phase 2: AML-Profil"),

        // ── J — Schema / Resolution ─────────────────────────────────────────
        ("J1", "RoleClassPath existiert",          "Schema",      "RoleClassPathExists",             "Mapper-Bibliothek"),
        ("J2", "SystemUnitClassPath auflösbar",    "Schema",      "SystemUnitClassPathResolvable",   "Mapper-Bibliothek"),
        ("J3", "AttributeDataType auflösbar",      "Schema",      "AttributeDataTypeResolvable",     "Mapper-Bibliothek"),
        ("J4", "ExternalReference-Files existent", "Schema",      "ExternalReferenceFilesExist",     "Out-of-scope für FPD-Profil"),
    };

    public static CoverageReport Build(DateTimeOffset generatedAt)
    {
        var pure   = ExtractInvariantNames(FpbValidationRules.PureRules);
        var phase2 = ExtractInvariantNames(FpbValidationRules.Phase2Rules);

        var rows = new List<CoverageReport.Row>();
        foreach (var (id, title, category, inv, note) in Catalogue)
        {
            string bucket;
            if (inv != null && pure.Contains(inv))         bucket = "PURE_OCL_LIVE";
            else if (inv != null && phase2.Contains(inv))  bucket = "PHASE2_OCL_PENDING";
            else if (category is "Geometry")               bucket = "OPEN_PLUGIN_GEOMETRY";
            else if (category is "Schema")                 bucket = "OPEN_MAPPER_SCHEMA";
            else                                           bucket = "OPEN_PHASE2_OR_INFORMATIVE";

            rows.Add(new CoverageReport.Row
            {
                CatalogId = id, Title = title, Category = category,
                OclConstraint = inv, Bucket = bucket, Note = note,
            });
        }

        var totals = rows.GroupBy(r => r.Bucket).ToDictionary(g => g.Key, g => g.Count());
        totals["TOTAL_CATALOGUE"] = rows.Count;

        return new CoverageReport
        {
            GeneratedAt = generatedAt.ToString("o"),
            Rules = rows,
            Totals = totals,
        };
    }

    /// <summary>
    /// Pull every <c>inv &lt;Name&gt;:</c> invariant declared in an OCL source
    /// blob. Used to confirm a catalogue row's <c>Inv</c> field matches a
    /// constraint the engine actually loads — drift between the snapshot table
    /// above and the shipping rule files would silently mis-categorise rules.
    /// </summary>
    public static HashSet<string> ExtractInvariantNames(string oclSource)
    {
        var rx = new Regex(@"\binv\s+([A-Za-z][A-Za-z0-9_]*)\s*:", RegexOptions.Compiled);
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match m in rx.Matches(oclSource))
            set.Add(m.Groups[1].Value);
        return set;
    }
}
