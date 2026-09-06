using Aml.Engine.CAEX;
using static FpbMapper.Conversion.FpbMappings;

namespace FpbMapper.Conversion;

/// <summary>
/// Build FPD library definitions using Aml.Engine.
/// RCL: flat with explicit RefBaseClassPath inheritance.
/// SUCL: hierarchical (FPD_Object → FPD_State → concrete) with mirrored attributes + SupportedRoleClass.
/// Instances created via CreateClassInstance() get RoleRequirements automatically.
/// </summary>
public static class FpdLibraries
{
    public static void EnsureLibraries(CAEXFileType caex, MapperOptions? options = null)
    {
        options ??= MapperOptions.Default;
        // A document that already carries the FPD libraries keeps its layout
        // (the Ensure* calls below are no-ops), so the ObjectReferences
        // ExternalReference is only added when the libraries are created here.
        var fresh = caex.SystemUnitClassLib[LibNames.SystemUnitClassLib] == null;
        EnsureExternalReference(caex, AmlBase.Alias, AmlBase.Path);
        if (options.UseObjectReferencesLibrary && fresh)
            EnsureExternalReference(caex, ObjectReferencesLibrary.Alias, ObjectReferencesLibrary.FileName);
        // Diagram-interchange types come from the shared OMG_DD_AttributeTypeLib,
        // pulled in by ExternalReference like the ObjectReferences library.
        if (fresh)
            EnsureExternalReference(caex, DiagramInterchangeLibrary.Alias, DiagramInterchangeLibrary.FileName);
        EnsureInterfaceClassLib(caex);
        EnsureRoleClassLib(caex, options);
        EnsureAttributeTypeLib(caex, options);
        EnsureSystemUnitClassLib(caex, options);
    }

    // -- 0. ExternalReferences (AML Base Libraries, ObjectReferences lib) -------

    private static void EnsureExternalReference(CAEXFileType caex, string alias, string path)
    {
        // Check if the reference already exists
        foreach (var er in caex.ExternalReference)
            if (er.Alias == alias) return;

        var extRef = caex.ExternalReference.Append();
        extRef.Alias = alias;
        extRef.Path = path;
    }

    // -- 1. InterfaceClassLib ------------------------------------------------

    private static void EnsureInterfaceClassLib(CAEXFileType caex)
    {
        if (caex.InterfaceClassLib[LibNames.InterfaceClassLib] != null) return;

        var icl = caex.InterfaceClassLib.Append(LibNames.InterfaceClassLib);
        icl.Description = "Flow and usage port interfaces for the Formalized Process Description (FPD).";
        icl.Version = LibNames.Version;

        var port = icl.InterfaceClass.Append("FPD_Port");
        port.Description = "Abstract base port for all FPD connections.";
        port.Version = LibNames.Version;
        port.RefBaseClassPath = AmlBase.Port;
        AddPointAttr(port, "PortCoordinate");

        foreach (var name in new[]
        {
            "FPD_FlowIn", "FPD_FlowOut",
            "FPD_Usage",
            "FPD_ParallelFlowIn", "FPD_ParallelFlowOut",
            "FPD_AlternativeFlowIn", "FPD_AlternativeFlowOut",
        })
        {
            var ic = icl.InterfaceClass.Append(name);
            ic.Version = LibNames.Version;
            ic.RefBaseClassPath = $"{LibNames.InterfaceClassLib}/FPD_Port";
        }
    }

    // -- 2. RoleClassLib (FLAT with explicit RefBaseClassPath) ----------------

    private static void EnsureRoleClassLib(CAEXFileType caex, MapperOptions options)
    {
        if (caex.RoleClassLib[LibNames.RoleClassLib] != null) return;

        var rcl = caex.RoleClassLib.Append(LibNames.RoleClassLib);
        rcl.Description = "Semantic model of the FPD per VDI/VDE 3682. Flat layout with explicit inheritance via RefBaseClassPath.";
        rcl.Version = LibNames.Version;

        // FPD_Process (inherits AML Structure)
        var proc = rcl.RoleClass.Append("FPD_Process");
        proc.Description = "Process (Part 2, Fig. 2). Aggregates states (2..*), system limit (1), and process operators (1..*).";
        proc.Version = LibNames.Version;
        proc.RefBaseClassPath = AmlBase.Structure;
        AddRefAttr(proc, "refObj", options.EffectiveSubProcessRefObjAttributeTypePath, options,
            "Reference to the parent process operator whose decomposition this process represents (refAbstractObj: this process is the detail representation of the operator).");

        // FPD_SystemLimit
        var sl = rcl.RoleClass.Append("FPD_SystemLimit");
        sl.Description = "System limit (Part 1, p. 9). Peer aggregate of the process, not a container.";
        sl.Version = LibNames.Version;
        AddIdentificationAttr(sl);
        AddBoundsAttr(sl, "ViewInformation");

        // FPD_Object (abstract base, inherits AML BaseRole)
        var obj = rcl.RoleClass.Append("FPD_Object");
        obj.Description = "Abstract base for all FPB objects (Part 1, p. 4: product, energy, information, process operator, technical resource).";
        obj.Version = LibNames.Version;
        obj.RefBaseClassPath = AmlBase.BaseRole;
        AddIdentificationAttr(obj);
        var charAttr = AddAttr(obj, "Characteristics", "xs:string");
        charAttr.Description = "Container for characteristics (Part 2, Fig. 3).";
        AddBoundsAttr(obj, "ViewInformation");

        // FPD_State (inherits FPD_Object)
        var state = rcl.RoleClass.Append("FPD_State");
        state.Description = "Abstract state (Part 2, Fig. 2). Inherits Identification and Characteristics from FPD_Object.";
        state.Version = LibNames.Version;
        state.RefBaseClassPath = $"{LibNames.RoleClassLib}/FPD_Object";
        AddRefAttr(state, "refObj", options.EffectiveBoundaryStateRefObjAttributeTypePath, options,
            "Reference to the original state instance that this boundary state represents (refBaseObj: same logical object, complementary view). Always points to the top-level original, regardless of decomposition depth.");

        // Concrete states (inherit FPD_State)
        foreach (var name in new[] { "FPD_Product", "FPD_Energy", "FPD_Information" })
        {
            var s = rcl.RoleClass.Append(name);
            s.Version = LibNames.Version;
            s.RefBaseClassPath = $"{LibNames.RoleClassLib}/FPD_State";
        }

        // FPD_ProcessOperator (inherits FPD_Object)
        var po = rcl.RoleClass.Append("FPD_ProcessOperator");
        po.Description = "Process operator (Part 2, Fig. 2). Inherits Identification and Characteristics from FPD_Object.";
        po.Version = LibNames.Version;
        po.RefBaseClassPath = $"{LibNames.RoleClassLib}/FPD_Object";
        AddRefAttr(po, "refProcess", options.EffectiveRefProcessAttributeTypePath, options,
            "Reference to the child process that decomposes this operator (refDetailObj: the child process is the more detailed representation). Empty if the operator is not further decomposed.");

        // FPD_TechnicalResource (inherits FPD_Object)
        var tr = rcl.RoleClass.Append("FPD_TechnicalResource");
        tr.Description = "Technical resource (Part 1, p. 9). Located outside the system limit, associated via usage.";
        tr.Version = LibNames.Version;
        tr.RefBaseClassPath = $"{LibNames.RoleClassLib}/FPD_Object";
    }

    // -- 3. AttributeTypeLib -------------------------------------------------

    private static void EnsureAttributeTypeLib(CAEXFileType caex, MapperOptions options)
    {
        if (caex.AttributeTypeLib[LibNames.AttributeTypeLib] != null) return;

        var atl = caex.AttributeTypeLib.Append(LibNames.AttributeTypeLib);
        atl.Version = LibNames.Version;

        var ident = atl.AttributeType.Append("FPD_Identification");
        ident.AttributeDataType = "xs:string";
        ident.Version = LibNames.Version;
        foreach (var f in IdentFields)
            AddAttr(ident, f, "xs:string");

        var charac = atl.AttributeType.Append("FPD_Characteristic");
        charac.AttributeDataType = "xs:string";
        charac.Version = LibNames.Version;

        var cIdent = AddAttr(charac, "Category", "xs:string");
        cIdent.RefAttributeType = AttrRefs.Identification;
        foreach (var f in IdentFields)
            AddAttr(cIdent, f, "xs:string");

        // DescriptiveElement per VDI 3682 Blatt 2 Bild 6. setpointValue is a
        // value/unit compound; validityLimits/actualValues hold indexed children
        // (validityLimit_N / actualValue_N) added per instance — the templates here
        // document the child shape.
        var desc = AddAttr(charac, "DescriptiveElement", "xs:string");
        AddAttr(desc, "valueDeterminationProcess", "xs:string");
        AddAttr(desc, "representivity", "xs:string");
        var setpoint = AddAttr(desc, "setpointValue", "xs:string");
        AddAttr(setpoint, "value", "xs:string");
        AddAttr(setpoint, "unit", "xs:string");
        var validity = AddAttr(desc, "validityLimits", "xs:string");
        var validityTpl = AddAttr(validity, "validityLimit", "xs:string");
        AddAttr(validityTpl, "limitType", "xs:string");
        AddAttr(validityTpl, "from", "xs:double");
        AddAttr(validityTpl, "to", "xs:double");
        var actual = AddAttr(desc, "actualValues", "xs:string");
        var actualTpl = AddAttr(actual, "actualValue", "xs:string");
        AddAttr(actualTpl, "value", "xs:string");
        AddAttr(actualTpl, "unit", "xs:string");

        var rel = AddAttr(charac, "RelationalElement", "xs:string");
        foreach (var f in new[] { "view", "model", "regulationsForRelationalGeneration" })
            AddAttr(rel, f, "xs:string");

        // Legacy layout only: with the official ObjectReferences library the
        // reference attributes are typed by that library and no local type exists.
        if (!options.UseObjectReferencesLibrary)
        {
            var refObjType = atl.AttributeType.Append("refObj");
            refObjType.AttributeDataType = "xs:string";
            refObjType.Description = "Generic IDREF attribute. Semantics depend on the carrying element (see RoleClassLib descriptions).";
            refObjType.Version = LibNames.Version;
        }
    }

    // -- 4. SystemUnitClassLib (hierarchical, mirrored attributes) -----------

    private static void EnsureSystemUnitClassLib(CAEXFileType caex, MapperOptions options)
    {
        if (caex.SystemUnitClassLib[LibNames.SystemUnitClassLib] != null) return;

        var sucl = caex.SystemUnitClassLib.Append(LibNames.SystemUnitClassLib);
        sucl.Description = "Instantiation templates with mirrored attributes and SUC inheritance.";
        sucl.Version = LibNames.Version;

        // FPD_Process (standalone)
        var procSuc = sucl.SystemUnitClass.Append("FPD_Process");
        procSuc.Version = LibNames.Version;
        AddRefAttr(procSuc, "refObj", options.EffectiveSubProcessRefObjAttributeTypePath, options, null);
        procSuc.SupportedRoleClass.Append().RefRoleClassPath = $"{LibNames.RoleClassLib}/FPD_Process";
        procSuc.SupportedRoleClass.Append().RefRoleClassPath = AmlBase.Structure;

        // FPD_SystemLimit (standalone)
        var slSuc = sucl.SystemUnitClass.Append("FPD_SystemLimit");
        slSuc.Version = LibNames.Version;
        AddIdentificationAttr(slSuc);
        AddBoundsAttr(slSuc, "ViewInformation");
        slSuc.SupportedRoleClass.Append().RefRoleClassPath = $"{LibNames.RoleClassLib}/FPD_SystemLimit";

        // FPD_Object (abstract base SUC)
        var objSuc = sucl.SystemUnitClass.Append("FPD_Object");
        objSuc.Version = LibNames.Version;
        AddIdentificationAttr(objSuc);
        AddAttr(objSuc, "Characteristics", "xs:string");
        AddBoundsAttr(objSuc, "ViewInformation");
        objSuc.SupportedRoleClass.Append().RefRoleClassPath = $"{LibNames.RoleClassLib}/FPD_Object";

        // FPD_State (inherits FPD_Object, adds refObj)
        var stateSuc = sucl.SystemUnitClass.Append("FPD_State");
        stateSuc.Version = LibNames.Version;
        stateSuc.RefBaseClassPath = $"{LibNames.SystemUnitClassLib}/FPD_Object";
        AddRefAttr(stateSuc, "refObj", options.EffectiveBoundaryStateRefObjAttributeTypePath, options, null);
        stateSuc.SupportedRoleClass.Append().RefRoleClassPath = $"{LibNames.RoleClassLib}/FPD_State";

        // Concrete states (inherit FPD_State)
        // Only FPD_Product gets AML base Product role (Energy/Information have no AML base equivalent)
        foreach (var name in new[] { "FPD_Product", "FPD_Energy", "FPD_Information" })
        {
            var suc = sucl.SystemUnitClass.Append(name);
            suc.Version = LibNames.Version;
            suc.RefBaseClassPath = $"{LibNames.SystemUnitClassLib}/FPD_State";
            suc.SupportedRoleClass.Append().RefRoleClassPath = $"{LibNames.RoleClassLib}/{name}";
            if (name == "FPD_Product")
                suc.SupportedRoleClass.Append().RefRoleClassPath = AmlBase.Product;
        }

        // FPD_ProcessOperator (inherits FPD_Object, adds refProcess)
        var poSuc = sucl.SystemUnitClass.Append("FPD_ProcessOperator");
        poSuc.Version = LibNames.Version;
        poSuc.RefBaseClassPath = $"{LibNames.SystemUnitClassLib}/FPD_Object";
        AddRefAttr(poSuc, "refProcess", options.EffectiveRefProcessAttributeTypePath, options, null);
        poSuc.SupportedRoleClass.Append().RefRoleClassPath = $"{LibNames.RoleClassLib}/FPD_ProcessOperator";
        poSuc.SupportedRoleClass.Append().RefRoleClassPath = AmlBase.Process;

        // FPD_TechnicalResource (inherits FPD_Object)
        var trSuc = sucl.SystemUnitClass.Append("FPD_TechnicalResource");
        trSuc.Version = LibNames.Version;
        trSuc.RefBaseClassPath = $"{LibNames.SystemUnitClassLib}/FPD_Object";
        trSuc.SupportedRoleClass.Append().RefRoleClassPath = $"{LibNames.RoleClassLib}/FPD_TechnicalResource";
        trSuc.SupportedRoleClass.Append().RefRoleClassPath = AmlBase.Resource;
    }

    // -- Helpers --------------------------------------------------------------

    // Identification field list lives in IdentificationSchema as the single
    // source of truth — see IdentificationSchema.cs.
    private static IReadOnlyList<string> IdentFields => IdentificationSchema.Fields;

    private static AttributeType AddAttr(IObjectWithAttributes parent, string name, string dataType)
    {
        var attr = parent.Attribute.Append(name);
        attr.AttributeDataType = dataType;
        return attr;
    }

    private static void AddPointAttr(IObjectWithAttributes parent, string name)
    {
        var attr = parent.Attribute.Append(name);
        attr.AttributeDataType = "xs:string";
        attr.RefAttributeType = AttrRefs.Point;
        AddAttr(attr, "x", "xs:double");
        AddAttr(attr, "y", "xs:double");
    }

    private static void AddBoundsAttr(IObjectWithAttributes parent, string name)
    {
        var attr = parent.Attribute.Append(name);
        attr.AttributeDataType = "xs:string";
        attr.RefAttributeType = AttrRefs.Bounds;
        AddPointAttr(attr, "position");
        AddAttr(attr, "width", "xs:double");
        AddAttr(attr, "height", "xs:double");
    }

    private static void AddIdentificationAttr(IObjectWithAttributes parent)
    {
        var attr = parent.Attribute.Append(IdentificationSchema.AttributeName);
        attr.AttributeDataType = "xs:string";
        attr.RefAttributeType = AttrRefs.Identification;
        foreach (var f in IdentFields)
            AddAttr(attr, f, "xs:string");
    }

    /// <summary>
    /// Append one reference attribute. The attribute name is the VDI 3682 name
    /// (refObj / refProcess); the RefAttributeType carries the semantics, either
    /// one of the official ObjectReferences types or the legacy local refObj.
    /// </summary>
    private static void AddRefAttr(IObjectWithAttributes parent, string name, string attributeTypePath,
                                   MapperOptions options, string? description)
    {
        var attr = parent.Attribute.Append(name);
        attr.AttributeDataType = options.ReferenceAttributeDataType;
        attr.RefAttributeType = attributeTypePath;
        if (description != null)
            attr.Description = description;
    }
}
