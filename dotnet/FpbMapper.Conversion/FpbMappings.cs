namespace FpbMapper.Conversion;

/// <summary>
/// Type mappings between FPB.JS and AutomationML (port of mappings.js).
///
/// Schema cross-references (all entries derive from VDI 3682 Blatt 2):
/// <list type="bullet">
///   <item>Element types &amp; SUC paths — VDI 3682 Bild 2 (process structure); FPD_Product / FPD_Energy / FPD_Information are the three State types per Tabelle 3.</item>
///   <item>Flow types &amp; InterfaceClass paths — VDI 3682 Bild 3 (connection semantics): Flow / ParallelFlow / AlternativeFlow connect State↔ProcessOperator; Usage connects ProcessOperator↔TechnicalResource.</item>
///   <item>Identification attribute — VDI 3682 Bild 4 (object identification); structure defined in <see cref="IdentificationSchema"/>.</item>
///   <item>refObj &amp; derivatives — VDI 3682 Blatt 2 §6 (sub-process decomposition); refBaseObj / refExtendedObj / refComposedObj from the ETFA 2026 Object-References framework.</item>
/// </list>
/// </summary>
public static class FpbMappings
{
    // Element type -> AML SystemUnitClass path (VDI 3682 Bild 2 / Tabelle 3)
    public static readonly Dictionary<string, string> ElementToSuc = new()
    {
        [FpbTypes.Product]           = "VDI_FPD_SystemUnitClassLib/FPD_Product",            // VDI 3682 Bild 2, State (material)
        [FpbTypes.Energy]            = "VDI_FPD_SystemUnitClassLib/FPD_Energy",             // VDI 3682 Bild 2, State (energy)
        [FpbTypes.Information]       = "VDI_FPD_SystemUnitClassLib/FPD_Information",        // VDI 3682 Bild 2, State (information)
        [FpbTypes.ProcessOperator]   = "VDI_FPD_SystemUnitClassLib/FPD_ProcessOperator",    // VDI 3682 Bild 2, central transformation node
        [FpbTypes.TechnicalResource] = "VDI_FPD_SystemUnitClassLib/FPD_TechnicalResource",  // VDI 3682 Bild 2, resource outside SystemLimit
        [FpbTypes.SystemLimit]       = "VDI_FPD_SystemUnitClassLib/FPD_SystemLimit",        // VDI 3682 Bild 2, process boundary container
        [FpbTypes.Process]           = "VDI_FPD_SystemUnitClassLib/FPD_Process",            // VDI 3682 Bild 2, top-level process IE
    };

    // Reverse: AML SUC path -> FPB.JS type
    public static readonly Dictionary<string, string> SucToElement =
        ElementToSuc.ToDictionary(kv => kv.Value, kv => kv.Key);

    /// <summary>
    /// Strips a CAEX document alias prefix ("alias@path") from a class path.
    /// Documents that pull the FPD libraries in through an ExternalReference
    /// use alias-qualified paths per IEC 62714, while documents with embedded
    /// libraries use the document-internal form the mapping tables store.
    /// Read-side comparisons go through this helper so both layouts resolve
    /// to the same FPB.JS types.
    /// </summary>
    public static string StripAlias(string? path)
    {
        if (string.IsNullOrEmpty(path)) return string.Empty;
        var at = path.IndexOf('@');
        return at >= 0 ? path[(at + 1)..] : path;
    }

    // Flow type -> AML InterfaceClass paths (Out + In) (VDI 3682 Bild 3)
    public static readonly Dictionary<string, (string Out, string In)> FlowToInterface = new()
    {
        // Flow: sequential State→PO transition (VDI 3682 Bild 3, Tabelle 4 row 1)
        [FpbTypes.Flow]            = ("VDI_FPD_InterfaceClassLib/FPD_FlowOut",            "VDI_FPD_InterfaceClassLib/FPD_FlowIn"),
        // ParallelFlow: AND-split / AND-join (VDI 3682 Bild 3, Tabelle 4 row 2)
        [FpbTypes.ParallelFlow]    = ("VDI_FPD_InterfaceClassLib/FPD_ParallelFlowOut",    "VDI_FPD_InterfaceClassLib/FPD_ParallelFlowIn"),
        // AlternativeFlow: XOR-split / XOR-join (VDI 3682 Bild 3, Tabelle 4 row 3)
        [FpbTypes.AlternativeFlow] = ("VDI_FPD_InterfaceClassLib/FPD_AlternativeFlowOut", "VDI_FPD_InterfaceClassLib/FPD_AlternativeFlowIn"),
        // Usage: PO↔TR resource binding (VDI 3682 Bild 3, Tabelle 4 row 4 — symmetric)
        [FpbTypes.Usage]           = ("VDI_FPD_InterfaceClassLib/FPD_Usage",              "VDI_FPD_InterfaceClassLib/FPD_Usage"),
    };

    // Reverse: AML InterfaceClass path -> (flowType, direction)
    public static readonly Dictionary<string, (string FlowType, string Direction)> InterfaceToFlow;

    static FpbMappings()
    {
        InterfaceToFlow = new Dictionary<string, (string, string)>();
        foreach (var (flowType, paths) in FlowToInterface)
        {
            InterfaceToFlow[paths.Out] = (flowType, "out");
            if (paths.In != paths.Out)
                InterfaceToFlow[paths.In] = (flowType, "in");
        }
    }

    // Object types (have Identification + Characteristics + Visual)
    public static readonly HashSet<string> ObjectTypes = new()
    {
        FpbTypes.Product, FpbTypes.Energy, FpbTypes.Information,
        FpbTypes.ProcessOperator, FpbTypes.TechnicalResource, FpbTypes.SystemLimit,
    };

    // Connection types (have sourceRef + targetRef)
    public static readonly HashSet<string> ConnectionTypes = new()
    {
        FpbTypes.Flow, FpbTypes.ParallelFlow, FpbTypes.AlternativeFlow, FpbTypes.Usage,
    };

    // State types
    public static readonly HashSet<string> StateTypes = new()
    {
        FpbTypes.Product, FpbTypes.Energy, FpbTypes.Information,
    };

    // AML AttributeType references
    public static class AttrRefs
    {
        public const string Identification = "VDI_FPD_AttributeTypeLib/FPD_Identification";  // VDI 3682 Bild 4, object identification compound
        public const string Characteristic = "VDI_FPD_AttributeTypeLib/FPD_Characteristic";  // VDI 3682 Bild 4, characteristic compound (Category + Value + Unit)
        public const string RefObj         = "VDI_FPD_AttributeTypeLib/refObj";              // Legacy generic reference type (MapperOptions.UseObjectReferencesLibrary = false); default layout types refObj/refProcess with the official ObjectReferences library
        // Diagram interchange — shared, language-agnostic OMG_DD_AttributeTypeLib (see DiagramInterchangeLibrary)
        public const string Bounds         = DiagramInterchangeLibrary.BoundsAttributeTypePath;    // bounding box for visual layout
        public const string Point          = DiagramInterchangeLibrary.PointAttributeTypePath;     // single coordinate point
        public const string Waypoint       = DiagramInterchangeLibrary.WaypointAttributeTypePath;  // flow connection waypoints
    }

    // Library names
    public static class LibNames
    {
        public const string InterfaceClassLib    = "VDI_FPD_InterfaceClassLib";
        public const string RoleClassLib         = "VDI_FPD_RoleClassLib";
        public const string SystemUnitClassLib   = "VDI_FPD_SystemUnitClassLib";
        public const string AttributeTypeLib     = "VDI_FPD_AttributeTypeLib";
        /// <summary>Shared diagram-interchange library (external asset, see <see cref="DiagramInterchangeLibrary"/>).</summary>
        public const string DIAttributeTypeLib   = DiagramInterchangeLibrary.LibName;

        /// <summary>
        /// Version stamped onto every emitted FPD library and class. Bumped when
        /// the VDI 3682 mapping or the generated CAEX schema changes in a way
        /// downstream tooling needs to notice (a new role class, a changed
        /// attribute type, an additional reference type derived from refObj).
        /// </summary>
        public const string Version = "1.2.0";
    }

    // AML Base Library references (AutomationML Edition 2, v2.11.0)
    public static class AmlBase
    {
        public const string Alias = "AutomationMLBaseLibrariesAMLEd22_11_0";
        // Relative reference — AML-conforming tools resolve this through their
        // configured library search path. A public mirror under fpbjs.net/libs
        // is planned (see roadmap) for hosting an immutable copy.
        public const string Path  = "AutomationML_Base_Libraries_AMLEd2_2.11.0.aml";

        // InterfaceClassLib base
        public const string Port      = Alias + "@AutomationMLInterfaceClassLib/AutomationMLBaseInterface/Port";

        // RoleClassLib bases
        public const string BaseRole  = Alias + "@AutomationMLBaseRoleClassLib/AutomationMLBaseRole";
        public const string Structure = Alias + "@AutomationMLBaseRoleClassLib/AutomationMLBaseRole/Structure";
        public const string Product   = Alias + "@AutomationMLBaseRoleClassLib/AutomationMLBaseRole/Product";
        public const string Process   = Alias + "@AutomationMLBaseRoleClassLib/AutomationMLBaseRole/Process";
        public const string Resource  = Alias + "@AutomationMLBaseRoleClassLib/AutomationMLBaseRole/Resource";
    }
}
