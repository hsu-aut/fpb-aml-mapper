using Aml.Engine.CAEX;

namespace FpbMapper.Conversion;

/// <summary>
/// Centralised CAEX traversal helpers. Previously the same depth-first walk
/// over an IH's nested InternalElements was re-implemented in three places
/// (Vdi3682Validator, FpbJsonToCaex, CaexToFpbJson). One implementation here
/// keeps the recursion behaviour identical for everyone and gives a single
/// hook point if the traversal strategy ever needs to change (e.g. to honour
/// CAEX mirror objects).
///
/// Not a full visitor framework — we deliberately stay with plain
/// <see cref="Action{T}"/> callbacks because the call sites are short and the
/// indirection of an interface would obscure them.
/// </summary>
public static class CaexElementWalker
{
    /// <summary>
    /// Visit every InternalElement reachable from the given roots, depth-first.
    /// Each IE is visited exactly once; the visitor may itself call into its
    /// children but isn't required to.
    /// </summary>
    public static void WalkInternalElements(IEnumerable<InternalElementType> roots, Action<InternalElementType> visit)
    {
        foreach (var ie in roots)
        {
            visit(ie);
            WalkInternalElements(ie.InternalElement, visit);
        }
    }

    /// <summary>Visit every InternalElement under every InstanceHierarchy of the document.</summary>
    public static void WalkInternalElements(CAEXDocument doc, Action<InternalElementType> visit)
    {
        if (doc?.CAEXFile == null) return;
        foreach (var ih in doc.CAEXFile.InstanceHierarchy)
            WalkInternalElements(ih.InternalElement, visit);
    }

    /// <summary>Collect every IE whose RefBaseSystemUnitPath matches <paramref name="sucPath"/>.</summary>
    public static List<InternalElementType> CollectIesBySuc(CAEXDocument doc, string sucPath)
    {
        var result = new List<InternalElementType>();
        WalkInternalElements(doc, ie =>
        {
            if (ie.RefBaseSystemUnitPath == sucPath) result.Add(ie);
        });
        return result;
    }
}
