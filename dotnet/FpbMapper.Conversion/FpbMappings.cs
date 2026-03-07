namespace FpbMapper.Conversion;

/// <summary>
/// Type mappings between FPB.JS and AutomationML (port of mappings.js).
/// </summary>
public static class FpbMappings
{
    // Element type -> AML SystemUnitClass path
    public static readonly Dictionary<string, string> ElementToSuc = new()
    {
        ["fpb:Product"]           = "FPD_SystemUnitClassLib/FPD_Product",
        ["fpb:Energy"]            = "FPD_SystemUnitClassLib/FPD_Energy",
        ["fpb:Information"]       = "FPD_SystemUnitClassLib/FPD_Information",
        ["fpb:ProcessOperator"]   = "FPD_SystemUnitClassLib/FPD_ProcessOperator",
        ["fpb:TechnicalResource"] = "FPD_SystemUnitClassLib/FPD_TechnicalResource",
        ["fpb:SystemLimit"]       = "FPD_SystemUnitClassLib/FPD_SystemLimit",
        ["fpb:Process"]           = "FPD_SystemUnitClassLib/FPD_Process",
    };

    // Reverse: AML SUC path -> FPB.JS type
    public static readonly Dictionary<string, string> SucToElement =
        ElementToSuc.ToDictionary(kv => kv.Value, kv => kv.Key);

    // Flow type -> AML InterfaceClass paths (Out + In)
    public static readonly Dictionary<string, (string Out, string In)> FlowToInterface = new()
    {
        ["fpb:Flow"]            = ("FPD_InterfaceClassLib/FPD_FlowOut",            "FPD_InterfaceClassLib/FPD_FlowIn"),
        ["fpb:ParallelFlow"]    = ("FPD_InterfaceClassLib/FPD_ParallelFlowOut",    "FPD_InterfaceClassLib/FPD_ParallelFlowIn"),
        ["fpb:AlternativeFlow"] = ("FPD_InterfaceClassLib/FPD_AlternativeFlowOut", "FPD_InterfaceClassLib/FPD_AlternativeFlowIn"),
        ["fpb:Usage"]           = ("FPD_InterfaceClassLib/FPD_Usage",              "FPD_InterfaceClassLib/FPD_Usage"),
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
        "fpb:Product", "fpb:Energy", "fpb:Information",
        "fpb:ProcessOperator", "fpb:TechnicalResource", "fpb:SystemLimit",
    };

    // Connection types (have sourceRef + targetRef)
    public static readonly HashSet<string> ConnectionTypes = new()
    {
        "fpb:Flow", "fpb:ParallelFlow", "fpb:AlternativeFlow", "fpb:Usage",
    };

    // State types
    public static readonly HashSet<string> StateTypes = new()
    {
        "fpb:Product", "fpb:Energy", "fpb:Information",
    };

    // AML AttributeType references
    public static class AttrRefs
    {
        public const string Identification = "FPD_AttributeTypeLib/FPD_Identification";
        public const string Characteristic = "FPD_AttributeTypeLib/FPD_Characteristic";
        public const string RefObj         = "FPD_AttributeTypeLib/refObj";
        public const string Bounds         = "FPD_DI_AttributeTypeLib/FPD_Bounds";
        public const string Point          = "FPD_DI_AttributeTypeLib/FPD_Point";
        public const string Waypoint       = "FPD_DI_AttributeTypeLib/FPD_Waypoint";
    }

    // Library names
    public static class LibNames
    {
        public const string InterfaceClassLib    = "FPD_InterfaceClassLib";
        public const string RoleClassLib         = "FPD_RoleClassLib";
        public const string SystemUnitClassLib   = "FPD_SystemUnitClassLib";
        public const string AttributeTypeLib     = "FPD_AttributeTypeLib";
        public const string DIAttributeTypeLib   = "FPD_DI_AttributeTypeLib";
    }
}
