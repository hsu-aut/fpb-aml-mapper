using Aml.Engine.CAEX;

namespace FpbMapper.Conversion;

/// <summary>An InternalLink of the document: its name and the InternalElement that holds it.</summary>
public sealed record ForeignLink(string Link, string Owner);

/// <summary>The links deleting one InternalElement of an FPD hierarchy removes from the document.</summary>
/// <param name="ElementId">AML id of the element, without braces.</param>
public sealed record ElementLinks(string ElementId, string ElementName, IReadOnlyList<ForeignLink> Links);

/// <summary>
/// For each element of an FPD InstanceHierarchy, the InternalLinks that
/// <see cref="FpbJsonToCaex.UpdateInPlace(CAEXDocument, string, InstanceHierarchyType?, MapperOptions?)"/>
/// removes from the document when that element is deleted (016f E4, V19), so
/// a host can name them before the user deletes it. Flows of the model do not
/// count: FPB.js deletes them with the element and shows that itself.
/// <list type="bullet">
/// <item>An element (a direct child of a process with an FPD class) goes with
/// everything below it: a link counts when it is stored outside and one side
/// lies in the element, its child elements or their interfaces (the update
/// removes it and warns), or when it is stored inside and not both sides lie
/// inside (it goes with the element, without a warning).</item>
/// <item>A process (the layer of a decomposed operator) counts only for
/// itself and its interfaces; its elements have their own entries. A link
/// stored in the process counts when no side lies in one of its elements.</item>
/// </list>
/// Partner sides resolve like <c>RemovedLinkTargets</c>: an interface id
/// first, else the form "element id:interface name" of CAEX 2.15.
/// </summary>
public static class ForeignLinks
{
    private static readonly string FpdPort = $"{FpbMappings.LibNames.InterfaceClassLib}/FPD_Port";

    public static IReadOnlyList<ElementLinks> Find(CAEXDocument doc, InstanceHierarchyType ih)
    {
        var processSuc = FpbMappings.ElementToSuc[FpbTypes.Process];
        var processes = ih.InternalElement.Where(ie => FpbMappings.StripAlias(ie.RefBaseSystemUnitPath) == processSuc).ToList();

        // the deletable unit each object belongs to, and the process each element lies in
        var unitOfElement = new Dictionary<string, Unit>(StringComparer.OrdinalIgnoreCase);
        var unitOfInterface = new Dictionary<string, Unit>(StringComparer.OrdinalIgnoreCase);
        var processOfObject = new Dictionary<string, InternalElementType>(StringComparer.OrdinalIgnoreCase);
        var fpdElements = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var units = new List<Unit>();

        foreach (var process in processes)
        {
            var processUnit = new Unit(process, null);
            units.Add(processUnit);
            unitOfElement[Bare(process.ID)] = processUnit;
            ForEachInterface(process.ExternalInterface, iface => unitOfInterface[Bare(iface.ID)] = processUnit);
            CaexElementWalker.WalkInternalElements(process.InternalElement, ie => processOfObject[Bare(ie.ID)] = process);

            foreach (var element in process.InternalElement)
            {
                if (!FpbMappings.SucToElement.TryGetValue(FpbMappings.StripAlias(element.RefBaseSystemUnitPath), out var type)
                    || type == FpbTypes.Process) continue;
                var unit = new Unit(element, process);
                units.Add(unit);
                fpdElements.Add(Bare(element.ID));
                CaexElementWalker.WalkInternalElements(new[] { element }, ie =>
                {
                    unitOfElement[Bare(ie.ID)] = unit;
                    ForEachInterface(ie.ExternalInterface, iface => unitOfInterface[Bare(iface.ID)] = unit);
                });
            }
        }

        // every interface of the document by id, and every element by id, to resolve partner sides
        var interfaces = new Dictionary<string, (ExternalInterfaceType Iface, InternalElementType Owner)>(StringComparer.OrdinalIgnoreCase);
        var elements = new Dictionary<string, InternalElementType>(StringComparer.OrdinalIgnoreCase);
        CaexElementWalker.WalkInternalElements(doc, ie =>
        {
            if (!string.IsNullOrEmpty(ie.ID)) elements[Bare(ie.ID)] = ie;
            ForEachInterface(ie.ExternalInterface, iface =>
            {
                if (!string.IsNullOrEmpty(iface.ID)) interfaces[Bare(iface.ID)] = (iface, ie);
            });
        });

        Side Resolve(string? reference)
        {
            if (string.IsNullOrEmpty(reference)) return Side.None;
            var bare = Bare(reference);
            // an interface id first: ids such as "urn:uuid:..." contain colons too
            if (interfaces.TryGetValue(bare, out var hit))
                return new Side(unitOfInterface.GetValueOrDefault(bare), IsFlowPort(hit.Iface, hit.Owner));
            var colon = reference.LastIndexOf(':');
            if (colon <= 0) return Side.None;
            var elementId = Bare(reference[..colon]);
            var name = reference[(colon + 1)..];
            var unit = unitOfElement.GetValueOrDefault(elementId);
            var iface = elements.TryGetValue(elementId, out var owner)
                ? owner.ExternalInterface.FirstOrDefault(i => i.Name == name)
                : null;
            return new Side(unit, iface != null && IsFlowPort(iface, owner!));
        }

        bool IsFlowPort(ExternalInterfaceType iface, InternalElementType owner)
        {
            if (!fpdElements.Contains(Bare(owner.ID))) return false;
            var path = FpbMappings.StripAlias(iface.RefBaseClassPath);
            return FpbMappings.InterfaceToFlow.ContainsKey(path) || path == FpdPort;
        }

        CaexElementWalker.WalkInternalElements(doc, holder =>
        {
            foreach (var link in holder.InternalLink)
            {
                var a = Resolve(link.RefPartnerSideA);
                var b = Resolve(link.RefPartnerSideB);
                if (a.FlowPort && b.FlowPort) continue;
                var stored = unitOfElement.GetValueOrDefault(Bare(holder.ID));
                var entry = new ForeignLink(link.Name ?? "", holder.Name ?? holder.ID ?? "");

                foreach (var unit in units)
                {
                    var sideIn = a.Unit == unit || b.Unit == unit;
                    bool counts;
                    if (unit.Process == null)
                    {
                        // a process: stored outside its layer and pointing at it, or stored in it and touching none of its elements
                        var storedOnLayer = stored == unit || (processOfObject.TryGetValue(Bare(holder.ID), out var p) && p == unit.Element);
                        var touchesElement = a.Unit?.Process == unit.Element || b.Unit?.Process == unit.Element;
                        counts = storedOnLayer ? stored == unit && !touchesElement : sideIn;
                    }
                    else
                    {
                        counts = stored == unit ? !(a.Unit == unit && b.Unit == unit) : sideIn;
                    }
                    if (counts && !unit.Links.Contains(entry)) unit.Links.Add(entry);
                }
            }
        });

        return units
            .Where(u => u.Links.Count > 0)
            .Select(u => new ElementLinks(Bare(u.Element.ID), u.Element.Name ?? "", u.Links))
            .ToList();
    }

    private static void ForEachInterface(IEnumerable<ExternalInterfaceType> roots, Action<ExternalInterfaceType> visit)
    {
        foreach (var iface in roots)
        {
            visit(iface);
            ForEachInterface(iface.ExternalInterface, visit);
        }
    }

    private static string Bare(string? id) => (id ?? "").Trim().TrimStart('{').TrimEnd('}');

    /// <summary>What deleting <see cref="Element"/> takes along; <see cref="Process"/> is null for a process itself.</summary>
    private sealed class Unit(InternalElementType element, InternalElementType? process)
    {
        public InternalElementType Element { get; } = element;
        public InternalElementType? Process { get; } = process;
        public List<ForeignLink> Links { get; } = new();
    }

    private readonly record struct Side(Unit? Unit, bool FlowPort)
    {
        public static readonly Side None = new(null, false);
    }
}
