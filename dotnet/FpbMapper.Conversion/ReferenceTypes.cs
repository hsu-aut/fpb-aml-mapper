// Reference-type abstraction modelled after the official
// AutomationML_ObjectReferences_AttributeTypeLib (AutomationML e.V., v1.1.1-beta),
// the published successor of the four-type framework proposed in the ETFA 2026
// paper "A Flexible Reference Framework for Multi-Hierarchical AutomationML Models".
//
// The library defines `refObj` as the abstract root and four derivatives:
// refBaseObj (aspect → base object, same logical object), refAspectObj (reverse),
// refDetailObj (abstract → detail representation), refAbstractObj (reverse).
//
// The FPD library keeps the VDI 3682 attribute NAMES (`refObj` on a sub-process
// and on a boundary state, `refProcess` on a process operator) and types them
// with refAbstractObj / refBaseObj / refDetailObj respectively (see
// MapperOptions). The read side keys on attribute names, so the entries below
// let the mapper recognise documents that use the official type names directly
// as attribute names as well.

using Aml.Engine.CAEX;

namespace FpbMapper.Conversion;

/// <summary>
/// Metadata for a single CAEX reference attribute type.
/// </summary>
public sealed class ReferenceType
{
    /// <summary>The XML attribute name as it appears on an InternalElement.</summary>
    public string AttributeName { get; }

    /// <summary>The AML AttributeTypeLib path that defines this attribute's type.</summary>
    public string AttributeTypePath { get; }

    /// <summary>The reference type this one inherits from (null for refObj).</summary>
    public ReferenceType? Parent { get; }

    /// <summary>
    /// True if the referencing object and the referenced object may have
    /// different types/structures (refDetailObj, refAbstractObj). False for
    /// same-identity references like refBaseObj / refAspectObj.
    /// </summary>
    public bool AllowsHeterogeneousTargetType { get; }

    public ReferenceType(string attributeName,
                         string attributeTypePath,
                         ReferenceType? parent = null,
                         bool allowsHeterogeneousTargetType = false)
    {
        AttributeName = attributeName;
        AttributeTypePath = attributeTypePath;
        Parent = parent;
        AllowsHeterogeneousTargetType = allowsHeterogeneousTargetType;
    }

    /// <summary>True if <see langword="this"/> equals or transitively derives from <paramref name="ancestor"/>.</summary>
    public bool IsOrInheritsFrom(ReferenceType ancestor)
    {
        var t = this;
        while (t != null)
        {
            if (ReferenceEquals(t, ancestor)) return true;
            t = t.Parent;
        }
        return false;
    }

    public override string ToString() => AttributeName;
}

/// <summary>
/// Built-in reference types known to the mapper. Adding a new type here is the
/// single edit needed to make the rest of the mapper recognise it (reads route
/// through the helpers below; writes will be routed in Stage D).
/// </summary>
public static class ReferenceTypes
{
    public static readonly ReferenceType RefObj = new(
        attributeName: "refObj",
        attributeTypePath: ObjectReferencesLibrary.RefObjAttributeTypePath);

    public static readonly ReferenceType RefBaseObj = new(
        attributeName: "refBaseObj",
        attributeTypePath: ObjectReferencesLibrary.RefBaseObjAttributeTypePath,
        parent: RefObj);

    public static readonly ReferenceType RefAspectObj = new(
        attributeName: "refAspectObj",
        attributeTypePath: ObjectReferencesLibrary.RefAspectObjAttributeTypePath,
        parent: RefObj);

    public static readonly ReferenceType RefDetailObj = new(
        attributeName: "refDetailObj",
        attributeTypePath: ObjectReferencesLibrary.RefDetailObjAttributeTypePath,
        parent: RefObj,
        allowsHeterogeneousTargetType: true);

    public static readonly ReferenceType RefAbstractObj = new(
        attributeName: "refAbstractObj",
        attributeTypePath: ObjectReferencesLibrary.RefAbstractObjAttributeTypePath,
        parent: RefObj,
        allowsHeterogeneousTargetType: true);

    /// <summary>All reference types known to the mapper, refObj first.</summary>
    public static readonly IReadOnlyList<ReferenceType> All = new[]
    {
        RefObj, RefBaseObj, RefAspectObj, RefDetailObj, RefAbstractObj,
    };
}

/// <summary>
/// Extension methods for reading reference values from CAEX InternalElements
/// without hard-coding the attribute name at every read site.
/// </summary>
public static class InternalElementReferenceExtensions
{
    /// <summary>
    /// Read the value of one specific reference type from the IE. Returns null
    /// if the attribute is missing or empty.
    /// </summary>
    public static string? GetReferenceValue(this InternalElementType ie, ReferenceType refType)
    {
        if (ie == null || refType == null) return null;
        var v = ie.Attribute[refType.AttributeName]?.Value;
        return string.IsNullOrEmpty(v) ? null : v;
    }

    /// <summary>
    /// Enumerate every reference attribute present on the IE (Type + Value).
    /// Useful for validators that need to walk every refObj-family link.
    /// </summary>
    public static IEnumerable<(ReferenceType Type, string Value)> EnumerateReferences(this InternalElementType ie)
    {
        if (ie == null) yield break;
        foreach (var refType in ReferenceTypes.All)
        {
            var v = ie.Attribute[refType.AttributeName]?.Value;
            if (!string.IsNullOrEmpty(v)) yield return (refType, v);
        }
    }

    /// <summary>
    /// Return the value of refObj — or, if absent, the first present value of
    /// any type that derives from refObj. refObj itself takes precedence so the
    /// existing VDI 3682 flow is unchanged; refBaseObj / refAspectObj /
    /// refDetailObj / refAbstractObj are picked up as fall-back to support
    /// documents that use the official type names as attribute names.
    /// </summary>
    public static string? GetRefObjOrDerived(this InternalElementType ie)
    {
        if (ie == null) return null;
        foreach (var refType in ReferenceTypes.All)
        {
            if (!refType.IsOrInheritsFrom(ReferenceTypes.RefObj)) continue;
            var v = ie.Attribute[refType.AttributeName]?.Value;
            if (!string.IsNullOrEmpty(v)) return v;
        }
        return null;
    }
}
