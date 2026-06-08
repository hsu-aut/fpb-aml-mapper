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
    public static void EnsureLibraries(CAEXFileType caex)
    {
        EnsureExternalReference(caex);
        EnsureInterfaceClassLib(caex);
        EnsureRoleClassLib(caex);
        EnsureAttributeTypeLib(caex);
        EnsureDIAttributeTypeLib(caex);
        EnsureSystemUnitClassLib(caex);
    }

    // -- 0. ExternalReference to AML Base Libraries ----------------------------

    private static void EnsureExternalReference(CAEXFileType caex)
    {
        // Check if the reference already exists
        foreach (var er in caex.ExternalReference)
            if (er.Alias == AmlBase.Alias) return;

        var extRef = caex.ExternalReference.Append();
        extRef.Alias = AmlBase.Alias;
        extRef.Path = AmlBase.Path;
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

    private static void EnsureRoleClassLib(CAEXFileType caex)
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
        AddRefObjAttr(proc, "IDREF to the parent process operator whose decomposition this process represents.");

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
        AddRefObjAttr(state, "IDREF to the original state instance that this boundary state represents. Always points to the top-level original, regardless of decomposition depth.");

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
        AddRefProcessAttr(po, "IDREF to the child process that decomposes this operator. Empty if the operator is not further decomposed.");

        // FPD_TechnicalResource (inherits FPD_Object)
        var tr = rcl.RoleClass.Append("FPD_TechnicalResource");
        tr.Description = "Technical resource (Part 1, p. 9). Located outside the system limit, associated via usage.";
        tr.Version = LibNames.Version;
        tr.RefBaseClassPath = $"{LibNames.RoleClassLib}/FPD_Object";
    }

    // -- 3. AttributeTypeLib -------------------------------------------------

    private static void EnsureAttributeTypeLib(CAEXFileType caex)
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

        var desc = AddAttr(charac, "DescriptiveElement", "xs:string");
        foreach (var f in new[] { "valueDeterminationProcess", "representivity", "setpointValue", "validityLimits", "actualValues" })
            AddAttr(desc, f, "xs:string");

        var rel = AddAttr(charac, "RelationalElement", "xs:string");
        foreach (var f in new[] { "view", "model", "regulationsForRelationalGeneration" })
            AddAttr(rel, f, "xs:string");

        var refObjType = atl.AttributeType.Append("refObj");
        refObjType.AttributeDataType = "xs:string";
        refObjType.Description = "Generic IDREF attribute. Semantics depend on the carrying element (see RoleClassLib descriptions).";
        refObjType.Version = LibNames.Version;
    }

    // -- 4. FPD_DI_AttributeTypeLib ------------------------------------------

    private static void EnsureDIAttributeTypeLib(CAEXFileType caex)
    {
        if (caex.AttributeTypeLib[LibNames.DIAttributeTypeLib] != null) return;

        var diatl = caex.AttributeTypeLib.Append(LibNames.DIAttributeTypeLib);
        diatl.Description = "Diagram Interchange attributes, aligned with OMG DD/DI terminology (DC::Bounds, DC::Point, DI::Waypoint).";
        diatl.Version = LibNames.Version;

        var bounds = diatl.AttributeType.Append("FPD_Bounds");
        bounds.AttributeDataType = "xs:string";
        bounds.Description = "A rectangular area defined by a top-left (x, y) location and a size (width, height) along the x-y axes (cf. DC::Bounds).";
        bounds.Version = LibNames.Version;
        AddPointAttr(bounds, "position");
        AddAttr(bounds, "width", "xs:double");
        AddAttr(bounds, "height", "xs:double");

        var wp = diatl.AttributeType.Append("FPD_Waypoint");
        wp.AttributeDataType = "xs:string";
        wp.Description = "A routing point along a connection path (cf. DI::Waypoint).";
        wp.Version = LibNames.Version;
        AddPointAttr(wp, "position");

        var pt = diatl.AttributeType.Append("FPD_Point");
        pt.AttributeDataType = "xs:string";
        pt.Description = "A two-dimensional point in a coordinate system (cf. DC::Point).";
        pt.Version = LibNames.Version;
        AddAttr(pt, "x", "xs:double");
        AddAttr(pt, "y", "xs:double");
    }

    // -- 5. SystemUnitClassLib (hierarchical, mirrored attributes) -----------

    private static void EnsureSystemUnitClassLib(CAEXFileType caex)
    {
        if (caex.SystemUnitClassLib[LibNames.SystemUnitClassLib] != null) return;

        var sucl = caex.SystemUnitClassLib.Append(LibNames.SystemUnitClassLib);
        sucl.Description = "Instantiation templates with mirrored attributes and SUC inheritance.";
        sucl.Version = LibNames.Version;

        // FPD_Process (standalone)
        var procSuc = sucl.SystemUnitClass.Append("FPD_Process");
        procSuc.Version = LibNames.Version;
        AddRefObjAttr(procSuc, null);
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
        AddRefObjAttr(stateSuc, null);
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
        AddRefProcessAttr(poSuc, null);
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

    private static void AddRefObjAttr(IObjectWithAttributes parent, string? description)
    {
        var attr = parent.Attribute.Append("refObj");
        attr.AttributeDataType = "xs:string";
        attr.RefAttributeType = AttrRefs.RefObj;
        if (description != null)
            attr.Description = description;
    }

    private static void AddRefProcessAttr(IObjectWithAttributes parent, string? description)
    {
        var attr = parent.Attribute.Append("refProcess");
        attr.AttributeDataType = "xs:string";
        attr.RefAttributeType = AttrRefs.RefObj;
        if (description != null)
            attr.Description = description;
    }
}
