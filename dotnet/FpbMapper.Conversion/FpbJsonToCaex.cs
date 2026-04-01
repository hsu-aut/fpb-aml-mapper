using Aml.Engine.CAEX;
using Aml.Engine.CAEX.Extensions;
using FpbMapper.Conversion.Models;
using static FpbMapper.Conversion.FpbMappings;

namespace FpbMapper.Conversion;

/// <summary>
/// Convert FPB.JS JSON to CAEX 3.0 using Aml.Engine (port of json-to-aml.js).
/// Uses CreateClassInstance() for CAEX-conformant instantiation with
/// automatic RoleRequirements and inherited attributes.
/// </summary>
public static class FpbJsonToCaex
{
    public static ConversionResult<CAEXDocument> Convert(string json)
    {
        var (project, entries) = FpbJsonParser.Parse(json);
        return Convert(project, entries);
    }

    public static ConversionResult<CAEXDocument> Convert(FpbProject project, List<ProcessEntry> entries)
    {
        var warnings = new List<string>();
        Validate(project, entries, warnings);
        var processMap = entries.ToDictionary(e => e.Process.Id, e => e);
        var entryProcessId = project.EntryPoint;

        // Pre-assign AML IDs for processes
        var processAmlIds = new Dictionary<string, string>();
        var poToChildProcess = new Dictionary<string, string>();
        var allProcessIds = CollectProcessIds(entryProcessId, processMap);

        foreach (var pid in allProcessIds)
        {
            processAmlIds[pid] = NewId();
            if (!processMap.TryGetValue(pid, out var entry)) continue;
            foreach (var obj in entry.ElementData)
            {
                if (obj.Type == "fpb:ProcessOperator" && !string.IsNullOrEmpty(obj.DecomposedView))
                    poToChildProcess[obj.Id] = obj.DecomposedView;
            }
        }

        // Build AML document
        var doc = CAEXDocument.New_CAEXDocument();
        var caex = doc.CAEXFile;
        caex.FileName = "fpb-export.aml";

        var sdi = caex.SourceDocumentInformation.FirstOrDefault() ?? caex.SourceDocumentInformation.Append();
        sdi.OriginName = "fpb-aml-mapper";
        sdi.OriginID = "fpb-aml-mapper-1.0";
        sdi.OriginVersion = "0.1.0";
        sdi.LastWritingDateTime = DateTime.UtcNow;

        // Libraries MUST be created before CreateClassInstance() can work
        FpdLibraries.EnsureLibraries(caex);

        // Build SUC lookup for CreateClassInstance()
        var sucLib = caex.SystemUnitClassLib[LibNames.SystemUnitClassLib]!;
        var sucLookup = new Dictionary<string, SystemUnitFamilyType>();
        foreach (var suc in sucLib.SystemUnitClass)
            sucLookup[suc.Name] = suc;

        // InstanceHierarchy
        var ihName = !string.IsNullOrEmpty(project.Name) ? project.Name : "InstanceHierarchy";
        var ih = caex.InstanceHierarchy.Append(ihName);
        ih.ID = NewId();
        ih.Version = "1.0.0";

        // Track which FPB.JS IDs have been emitted as AML IDs
        var usedAmlIds = new HashSet<string>();

        foreach (var pid in allProcessIds)
        {
            BuildProcess(ih, pid, processMap, processAmlIds, poToChildProcess, usedAmlIds, sucLookup, warnings);
        }

        return new ConversionResult<CAEXDocument>(doc, warnings);
    }

    private const int MaxDecompositionDepth = 50;

    private static List<string> CollectProcessIds(string processId, Dictionary<string, ProcessEntry> processMap,
        HashSet<string>? visited = null, int depth = 0)
    {
        if (depth > MaxDecompositionDepth)
            throw new InvalidOperationException($"Decomposition depth exceeds {MaxDecompositionDepth} levels — possible circular reference");

        visited ??= new HashSet<string>();
        if (!visited.Add(processId))
            throw new InvalidOperationException($"Circular decomposition detected: process '{processId}' references itself");

        var result = new List<string> { processId };
        if (!processMap.TryGetValue(processId, out var entry)) return result;

        foreach (var obj in entry.ElementData)
        {
            if (obj.Type == "fpb:ProcessOperator" && !string.IsNullOrEmpty(obj.DecomposedView))
            {
                if (processMap.ContainsKey(obj.DecomposedView))
                    result.AddRange(CollectProcessIds(obj.DecomposedView, processMap, visited, depth + 1));
            }
        }
        return result;
    }

    // ========================================================================
    // Process builder
    // ========================================================================

    private static void BuildProcess(
        InstanceHierarchyType ih,
        string processId,
        Dictionary<string, ProcessEntry> processMap,
        Dictionary<string, string> processAmlIds,
        Dictionary<string, string> poToChildProcess,
        HashSet<string> usedAmlIds,
        Dictionary<string, SystemUnitFamilyType> sucLookup,
        List<string> warnings)
    {
        if (!processMap.TryGetValue(processId, out var entry)) return;

        var process = entry.Process;
        var visualMap = entry.ElementVisual.ToDictionary(v => v.Id, v => v);
        var dataMap = entry.ElementData.ToDictionary(d => d.Id, d => d);

        // Determine process name
        var slData = entry.ElementData.FirstOrDefault(e => e.Type == "fpb:SystemLimit");
        var parentPOId = process.IsDecomposedProcessOperator;
        string? processName = null;

        if (!string.IsNullOrEmpty(parentPOId))
        {
            foreach (var pe in processMap.Values)
            {
                var po = pe.ElementData.FirstOrDefault(e => e.Id == parentPOId);
                if (po != null) { processName = po.Name; break; }
            }
        }
        processName ??= slData?.Name ?? "Process";

        // Create FPD_Process via CreateClassInstance
        var procIE = CreateInstance(sucLookup, "FPD_Process");
        procIE.Name = processName;
        procIE.ID = processAmlIds[processId];
        ih.Insert(procIE);

        // refObj: child processes point to parent PO
        SetAttrValue(procIE, "refObj", !string.IsNullOrEmpty(parentPOId) ? NormalizeId(parentPOId) : "");

        // SystemLimit
        if (slData != null)
        {
            var slIE = CreateInstance(sucLookup, "FPD_SystemLimit");
            slIE.Name = "SystemLimit_" + processName.Replace(" ", "");
            slIE.ID = NormalizeId(slData.Id);
            procIE.Insert(slIE);
            SetIdentification(slIE, slData.Identification, processName);
            if (visualMap.TryGetValue(slData.Id, out var slVisual))
                SetViewInformation(slIE, slVisual);
        }

        // Collect ExternalInterface references for InternalLinks
        var linkMap = new Dictionary<string, (ExternalInterfaceType? OutIf, ExternalInterfaceType? InIf)>();
        var ifaceCounters = new Dictionary<string, Dictionary<string, int>>();

        string GetNextInterfaceName(string elementId, string baseName)
        {
            if (!ifaceCounters.TryGetValue(elementId, out var counters))
            {
                counters = new Dictionary<string, int>();
                ifaceCounters[elementId] = counters;
            }
            counters.TryGetValue(baseName, out var count);
            counters[baseName] = count + 1;
            return count == 0 ? baseName : $"{baseName}_{count + 1}";
        }

        // Separate flows and objects
        var flows = entry.ElementData.Where(e => ConnectionTypes.Contains(e.Type)).ToList();
        var objects = entry.ElementData.Where(e => ObjectTypes.Contains(e.Type)).ToList();

        // Group flows by source/target
        var flowsBySource = flows.Where(f => f.SourceRef != null).GroupBy(f => f.SourceRef!).ToDictionary(g => g.Key, g => g.ToList());
        var flowsByTarget = flows.Where(f => f.TargetRef != null).GroupBy(f => f.TargetRef!).ToDictionary(g => g.Key, g => g.ToList());

        // Determine if child process
        var isChildProcess = !string.IsNullOrEmpty(parentPOId);
        ProcessEntry? parentEntry = null;
        if (isChildProcess)
        {
            parentEntry = processMap.Values.FirstOrDefault(pe =>
                pe.ElementData.Any(e =>
                    e.Type == "fpb:ProcessOperator" && e.DecomposedView == processId));
        }

        // Build object InternalElements
        foreach (var obj in objects)
        {
            if (obj.Type == "fpb:SystemLimit") continue;
            if (!ElementToSuc.TryGetValue(obj.Type, out var sucPath)) continue;

            var sucName = sucPath.Split('/')[1]; // e.g. "FPD_Product"
            var elemName = !string.IsNullOrEmpty(obj.Name) ? obj.Name : obj.Type.Split(':')[1];
            elemName = elemName.Replace("\n", "");

            // AML ID: normalize FPB.JS ID to {GUID} format
            string elemAmlId;
            var normalizedId = NormalizeId(obj.Id);
            if (usedAmlIds.Contains(normalizedId))
                elemAmlId = NewId();
            else
                elemAmlId = normalizedId;
            usedAmlIds.Add(elemAmlId);

            // CreateClassInstance — gets attributes + RoleRequirements automatically
            var ie = CreateInstance(sucLookup, sucName);
            ie.Name = elemName;
            ie.ID = elemAmlId;
            procIE.Insert(ie);

            // Set attribute values on the auto-created attributes
            SetIdentification(ie, obj.Identification, elemName);
            SetCharacteristics(ie, obj.Characteristics);

            // refProcess (on ProcessOperator only)
            if (obj.Type == "fpb:ProcessOperator")
            {
                if (!string.IsNullOrEmpty(obj.DecomposedView) && poToChildProcess.ContainsKey(obj.Id))
                {
                    var childProcessId = poToChildProcess[obj.Id];
                    SetAttrValue(ie, "refProcess", processAmlIds.GetValueOrDefault(childProcessId, ""));
                }
                else
                {
                    SetAttrValue(ie, "refProcess", "");
                }
            }
            else if (StateTypes.Contains(obj.Type))
            {
                if (isChildProcess && parentEntry != null)
                {
                    var parentState = parentEntry.ElementData.FirstOrDefault(e =>
                        e.Id == obj.Id && StateTypes.Contains(e.Type));
                    SetAttrValue(ie, "refObj", parentState != null ? NormalizeId(obj.Id) : "");
                }
                else
                {
                    SetAttrValue(ie, "refObj", "");
                }
            }

            // ViewInformation
            if (visualMap.TryGetValue(obj.Id, out var visual))
                SetViewInformation(ie, visual);

            // ExternalInterfaces for outgoing flows
            if (flowsBySource.TryGetValue(obj.Id, out var outFlows))
            {
                foreach (var flow in outFlows)
                {
                    if (!FlowToInterface.TryGetValue(flow.Type, out var ifacePaths)) continue;

                    var outBaseName = ifacePaths.Out.Split('/')[1];
                    var ifaceName = GetNextInterfaceName(obj.Id, outBaseName);
                    var ifaceId = NewId();

                    var extIf = ie.ExternalInterface.Append(ifaceName);
                    extIf.ID = ifaceId;
                    extIf.RefBaseClassPath = ifacePaths.Out;

                    if (visualMap.TryGetValue(flow.Id, out var flowVisual) && flowVisual.Waypoints.Count > 0)
                    {
                        var firstWp = flowVisual.Waypoints[0];
                        var portCoord = firstWp.Original ?? firstWp;
                        AddPortCoordinate(extIf, portCoord.X, portCoord.Y);

                        var intermediates = flowVisual.Waypoints.Skip(1).SkipLast(1).ToList();
                        for (int i = 0; i < intermediates.Count; i++)
                        {
                            var wp = intermediates[i];
                            if (wp.Original != null) continue;
                            AddWaypointAttr(extIf, i + 1, wp.X, wp.Y);
                        }
                    }
                    else
                    {
                        AddEmptyPortCoordinate(extIf);
                    }

                    if (!linkMap.ContainsKey(flow.Id))
                        linkMap[flow.Id] = (null, null);
                    var cur = linkMap[flow.Id];
                    linkMap[flow.Id] = (extIf, cur.InIf);
                }
            }

            // ExternalInterfaces for incoming flows
            if (flowsByTarget.TryGetValue(obj.Id, out var inFlows))
            {
                foreach (var flow in inFlows)
                {
                    if (!FlowToInterface.TryGetValue(flow.Type, out var ifacePaths)) continue;

                    var inBaseName = ifacePaths.In.Split('/')[1];
                    var ifaceName = GetNextInterfaceName(obj.Id, inBaseName);
                    var ifaceId = NewId();

                    var extIf = ie.ExternalInterface.Append(ifaceName);
                    extIf.ID = ifaceId;
                    extIf.RefBaseClassPath = ifacePaths.In;

                    if (visualMap.TryGetValue(flow.Id, out var flowVisual) && flowVisual.Waypoints.Count > 0)
                    {
                        var lastWp = flowVisual.Waypoints[^1];
                        var portCoord = lastWp.Original ?? lastWp;
                        AddPortCoordinate(extIf, portCoord.X, portCoord.Y);
                    }
                    else
                    {
                        AddEmptyPortCoordinate(extIf);
                    }

                    if (!linkMap.ContainsKey(flow.Id))
                        linkMap[flow.Id] = (null, null);
                    var cur = linkMap[flow.Id];
                    linkMap[flow.Id] = (cur.OutIf, extIf);
                }
            }
        }

        // InternalLinks — use AInterface/BInterface for correct CAEX path resolution
        foreach (var (flowId, ifs) in linkMap)
        {
            if (ifs.OutIf == null || ifs.InIf == null)
            {
                warnings.Add($"Flow '{flowId}' skipped: missing {(ifs.OutIf == null ? "source" : "target")} interface.");
                continue;
            }

            var flow = dataMap.GetValueOrDefault(flowId);
            var sourceData = flow?.SourceRef != null ? dataMap.GetValueOrDefault(flow.SourceRef) : null;
            var targetData = flow?.TargetRef != null ? dataMap.GetValueOrDefault(flow.TargetRef) : null;
            var sourceName = sourceData?.Name.Replace(" ", "").Replace("\n", "") ?? "Source";
            var targetName = targetData?.Name.Replace(" ", "").Replace("\n", "") ?? "Target";

            var linkName = flow?.Type == "fpb:Usage"
                ? $"{sourceName}_uses_{targetName}"
                : $"{sourceName}_to_{targetName}";

            var link = procIE.InternalLink.Append(linkName);
            link.AInterface = ifs.OutIf;
            link.BInterface = ifs.InIf;
        }
    }

    // ========================================================================
    // CAEX instantiation helper
    // ========================================================================

    private static InternalElementType CreateInstance(Dictionary<string, SystemUnitFamilyType> sucLookup, string sucName)
    {
        if (!sucLookup.TryGetValue(sucName, out var suc))
            throw new InvalidOperationException($"SystemUnitClass '{sucName}' not found in library");
        var ie = (InternalElementType)suc.CreateClassInstance(sucName);

        // CreateClassInstance only copies the first SupportedRoleClass as RoleRequirement.
        // Add any additional SupportedRoleClasses (e.g. AML base roles) manually.
        var existingRRs = new HashSet<string>(ie.RoleRequirements.Select(r => r.RefBaseRoleClassPath));
        foreach (var src in suc.SupportedRoleClass)
        {
            if (!existingRRs.Contains(src.RefRoleClassPath))
                ie.RoleRequirements.Append().RefBaseRoleClassPath = src.RefRoleClassPath;
        }

        return ie;
    }

    // ========================================================================
    // Attribute setters (work on auto-created attributes from CreateClassInstance)
    // ========================================================================

    private static void SetAttrValue(InternalElementType ie, string name, string value)
    {
        var attr = ie.Attribute[name];
        if (attr != null)
        {
            if (!string.IsNullOrEmpty(value))
                attr.Value = value;
        }
        else
        {
            // Fallback: create if missing (shouldn't happen with proper SUC)
            var newAttr = ie.Attribute.Append(name);
            newAttr.AttributeDataType = "xs:string";
            if (!string.IsNullOrEmpty(value))
                newAttr.Value = value;
        }
    }

    private static void SetIdentification(InternalElementType ie, Identification? ident, string fallbackName)
    {
        var attr = ie.Attribute["Identification"];
        if (attr == null) return;

        SetSubAttr(attr, "uniqueIdent", ident?.UniqueIdent);
        SetSubAttr(attr, "longName", ident?.LongName);
        SetSubAttr(attr, "shortName", ident?.ShortName ?? fallbackName);
        SetSubAttr(attr, "versionNumber", ident?.VersionNumber);
        SetSubAttr(attr, "revisionNumber", ident?.RevisionNumber);
    }

    private static void SetCharacteristics(InternalElementType ie, List<Characteristic> characteristics)
    {
        if (characteristics.Count == 0) return;

        var container = ie.Attribute["Characteristics"];
        if (container == null)
        {
            container = ie.Attribute.Append("Characteristics");
            container.AttributeDataType = "xs:string";
        }

        for (int i = 0; i < characteristics.Count; i++)
        {
            var c = characteristics[i];
            var cAttr = container.Attribute.Append($"Characteristic_{i + 1}");
            cAttr.AttributeDataType = "xs:string";
            cAttr.RefAttributeType = AttrRefs.Characteristic;

            var cat = c.Category;
            var identAttr = cAttr.Attribute.Append("Category");
            identAttr.AttributeDataType = "xs:string";
            identAttr.RefAttributeType = AttrRefs.Identification;
            foreach (var f in new[] { "uniqueIdent", "longName", "shortName", "versionNumber", "revisionNumber" })
            {
                var val = f switch
                {
                    "uniqueIdent" => cat?.UniqueIdent ?? "",
                    "longName" => cat?.LongName ?? "",
                    "shortName" => cat?.ShortName ?? "",
                    "versionNumber" => cat?.VersionNumber ?? "",
                    "revisionNumber" => cat?.RevisionNumber ?? "",
                    _ => ""
                };
                var sub = identAttr.Attribute.Append(f);
                sub.AttributeDataType = "xs:string";
                if (!string.IsNullOrEmpty(val)) sub.Value = val;
            }

            var desc = c.DescriptiveElement;
            var descAttr = cAttr.Attribute.Append("DescriptiveElement");
            descAttr.AttributeDataType = "xs:string";
            AddStringSubAttr(descAttr, "valueDeterminationProcess", desc?.ValueDeterminationProcess);
            AddStringSubAttr(descAttr, "representivity", desc?.Representivity);
            AddStringSubAttr(descAttr, "setpointValue", desc?.SetpointValue);
            AddStringSubAttr(descAttr, "validityLimits", desc?.ValidityLimits);
            AddStringSubAttr(descAttr, "actualValues", desc?.ActualValues);

            var rel = c.RelationalElement;
            var relAttr = cAttr.Attribute.Append("RelationalElement");
            relAttr.AttributeDataType = "xs:string";
            AddStringSubAttr(relAttr, "view", rel?.View);
            AddStringSubAttr(relAttr, "model", rel?.Model);
            AddStringSubAttr(relAttr, "regulationsForRelationalGeneration", rel?.RegulationsForRelationalGeneration);
        }
    }

    private static void SetViewInformation(InternalElementType ie, VisualInfo visual)
    {
        var attr = ie.Attribute["ViewInformation"];
        if (attr == null) return;

        var pos = attr.Attribute["position"];
        if (pos != null)
        {
            SetDoubleSubAttr(pos, "x", visual.X);
            SetDoubleSubAttr(pos, "y", visual.Y);
        }
        SetDoubleSubAttr(attr, "width", visual.Width);
        SetDoubleSubAttr(attr, "height", visual.Height);
    }

    private static void SetSubAttr(AttributeType parent, string name, string? value)
    {
        var attr = parent.Attribute[name];
        if (attr != null && !string.IsNullOrEmpty(value))
            attr.Value = value;
    }

    private static void SetDoubleSubAttr(AttributeType parent, string name, double value)
    {
        var attr = parent.Attribute[name];
        if (attr != null)
            attr.Value = value.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    private static void AddStringSubAttr(AttributeType parent, string name, string? value)
    {
        var attr = parent.Attribute.Append(name);
        attr.AttributeDataType = "xs:string";
        if (!string.IsNullOrEmpty(value)) attr.Value = value;
    }

    private static void AddPortCoordinate(ExternalInterfaceType extIf, double x, double y)
    {
        var attr = extIf.Attribute.Append("PortCoordinate");
        attr.AttributeDataType = "xs:string";
        attr.RefAttributeType = AttrRefs.Point;
        AddDoubleAttr(attr, "x", x);
        AddDoubleAttr(attr, "y", y);
    }

    private static void AddEmptyPortCoordinate(ExternalInterfaceType extIf)
    {
        var attr = extIf.Attribute.Append("PortCoordinate");
        attr.AttributeDataType = "xs:string";
        attr.RefAttributeType = AttrRefs.Point;
        var xAttr = attr.Attribute.Append("x");
        xAttr.AttributeDataType = "xs:double";
        var yAttr = attr.Attribute.Append("y");
        yAttr.AttributeDataType = "xs:double";
    }

    private static void AddWaypointAttr(ExternalInterfaceType extIf, int index, double x, double y)
    {
        var wpAttr = extIf.Attribute.Append($"Waypoint_{index}");
        wpAttr.AttributeDataType = "xs:string";
        wpAttr.RefAttributeType = AttrRefs.Waypoint;
        var wpPos = wpAttr.Attribute.Append("position");
        wpPos.AttributeDataType = "xs:string";
        wpPos.RefAttributeType = AttrRefs.Point;
        AddDoubleAttr(wpPos, "x", x);
        AddDoubleAttr(wpPos, "y", y);
    }

    private static void AddDoubleAttr(AttributeType parent, string name, double value)
    {
        var attr = parent.Attribute.Append(name);
        attr.AttributeDataType = "xs:double";
        attr.Value = value.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    private static string NewId() => Guid.NewGuid().ToString("B");

    private static string NormalizeId(string id)
    {
        if (string.IsNullOrEmpty(id)) return NewId();
        if (id.StartsWith('{') && id.EndsWith('}')) return id;
        if (Guid.TryParse(id, out var guid)) return guid.ToString("B");
        return id;
    }

    // ========================================================================
    // Input validation
    // ========================================================================

    private static void Validate(FpbProject project, List<ProcessEntry> entries, List<string> warnings)
    {
        if (entries.Count == 0)
            throw new InvalidOperationException("No process entries found in JSON input.");

        if (string.IsNullOrEmpty(project.EntryPoint))
            throw new InvalidOperationException("Project has no entryPoint.");

        if (!entries.Any(e => e.Process.Id == project.EntryPoint))
            throw new InvalidOperationException($"Entry point '{project.EntryPoint}' not found in process entries.");

        foreach (var entry in entries)
        {
            var hasSL = entry.ElementData.Any(e => e.Type == "fpb:SystemLimit");
            if (!hasSL)
                warnings.Add($"Process '{entry.Process.Id}' has no SystemLimit.");

            var hasPO = entry.ElementData.Any(e => e.Type == "fpb:ProcessOperator");
            if (!hasPO)
                warnings.Add($"Process '{entry.Process.Id}' has no ProcessOperator.");

            // Check for flows referencing unknown elements
            var elementIds = entry.ElementData.Select(e => e.Id).ToHashSet();
            foreach (var flow in entry.ElementData.Where(e => ConnectionTypes.Contains(e.Type)))
            {
                if (!string.IsNullOrEmpty(flow.SourceRef) && !elementIds.Contains(flow.SourceRef))
                    warnings.Add($"Flow '{flow.Id}' references unknown source '{flow.SourceRef}'.");
                if (!string.IsNullOrEmpty(flow.TargetRef) && !elementIds.Contains(flow.TargetRef))
                    warnings.Add($"Flow '{flow.Id}' references unknown target '{flow.TargetRef}'.");
            }
        }
    }
}
