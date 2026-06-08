// Reference-type abstraction modelled after the ETFA 2026 paper
// "A Flexible Reference Framework for Multi-Hierarchical AutomationML Models".
//
// The paper defines a generalised hierarchy of reference attribute types with
// `refObj` as the root and `refBaseObj` / `refExtendedObj` / `refComposedObj` as
// concrete derivatives. The framework is explicitly designed to be extended in
// the future with additional types (e.g. domain-specific has/dependsOn refs)
// without breaking existing tooling.
//
// VDI 3682 currently uses a single attribute, `refObj`, on FPD_Process IEs to
// link a sub-process back to its parent ProcessOperator. As the FPD library
// adopts the broader Object-References framework, the same decomposition slot
// may be expressed via refBaseObj (mirror with local attributes) or via
// refExtendedObj/refComposedObj (semantically distinct relations).
//
// This abstraction lets the mapper read ANY refObj-derived attribute without
// hard-coding the attribute name in every read site. Writes still target the
// canonical `refObj` for now; the Stage-D refactor will route writes through
// the same type system once the semantic choice is exposed.

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
    /// different types/structures (refExtendedObj, refComposedObj). False for
    /// same-identity references like refBaseObj.
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
        attributeTypePath: FpbMappings.AttrRefs.RefObj);

    public static readonly ReferenceType RefBaseObj = new(
        attributeName: "refBaseObj",
        attributeTypePath: ObjectReferencesLibrary.RefBaseObjAttributeTypePath,
        parent: RefObj);

    public static readonly ReferenceType RefExtendedObj = new(
        attributeName: "refExtendedObj",
        attributeTypePath: ObjectReferencesLibrary.RefExtendedObjAttributeTypePath,
        parent: RefObj,
        allowsHeterogeneousTargetType: true);

    public static readonly ReferenceType RefComposedObj = new(
        attributeName: "refComposedObj",
        attributeTypePath: ObjectReferencesLibrary.RefComposedObjAttributeTypePath,
        parent: RefObj,
        allowsHeterogeneousTargetType: true);

    /// <summary>All reference types known to the mapper, refObj first.</summary>
    public static readonly IReadOnlyList<ReferenceType> All = new[]
    {
        RefObj, RefBaseObj, RefExtendedObj, RefComposedObj,
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
    /// existing VDI 3682 flow is unchanged; refBaseObj / refExtendedObj /
    /// refComposedObj are picked up as fall-back to support documents that use
    /// the Object-References library directly.
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
