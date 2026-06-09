using System.Text.Json;
using Aml.Engine.CAEX;
using static FpbMapper.Conversion.FpbMappings;

namespace FpbMapper.Conversion;

/// <summary>
/// Convert CAEX 3.0 (AML) to FPB.JS JSON using Aml.Engine (port of aml-to-json.js).
/// </summary>
public static class CaexToFpbJson
{
    /// <summary>
    /// Convert the first InstanceHierarchy in <paramref name="doc"/> to FPB.JS JSON.
    /// Kept for legacy callers (Web mapper, file-based export). Multi-IH users
    /// should call the <see cref="Convert(CAEXDocument, InstanceHierarchyType)"/>
    /// overload and target each IH explicitly.
    /// </summary>
    public static ConversionResult<string> Convert(CAEXDocument doc)
    {
        var caex = doc.CAEXFile;
        var ih = caex.InstanceHierarchy.FirstOrDefault()
            ?? throw new InvalidOperationException("No InstanceHierarchy found");
        return Convert(doc, ih);
    }

    /// <summary>
    /// Find every InstanceHierarchy in <paramref name="doc"/> that holds at least
    /// one FPD_Process IE. Plugins surface one viewer-tab per IH in the returned
    /// order so the user can edit each FPD process independently.
    /// </summary>
    public static IReadOnlyList<InstanceHierarchyType> FindFpdInstanceHierarchies(CAEXDocument doc)
    {
        if (doc?.CAEXFile == null) return Array.Empty<InstanceHierarchyType>();
        var processSuc = ElementToSuc[FpbTypes.Process];
        return doc.CAEXFile.InstanceHierarchy
            .Where(ih => ih.InternalElement.Any(ie => ie.RefBaseSystemUnitPath == processSuc))
            .ToList();
    }

    /// <summary>
    /// Convert a specific InstanceHierarchy to FPB.JS JSON. Use when a document
    /// holds several independent FPD models that the plugin renders side-by-side.
    /// </summary>
    public static ConversionResult<string> Convert(CAEXDocument doc, InstanceHierarchyType ih)
    {
        var warnings = new List<string>();
        if (ih == null)
            throw new ArgumentNullException(nameof(ih));

        // Collect all FPD_Process InternalElements (flat in IH)
        var allProcessIEs = ih.InternalElement
            .Where(ie => ie.RefBaseSystemUnitPath == ElementToSuc[FpbTypes.Process])
            .ToList();

        if (allProcessIEs.Count == 0)
            throw new InvalidOperationException("No FPD_Process found in InstanceHierarchy");

        // Build refObj lookups
        var processRefObjMap = new Dictionary<string, string>(); // process AML ID -> parent PO ID
        var poRefObjMap = new Dictionary<string, string>();       // PO AML ID -> child process AML ID
        var poToProcessAmlId = new Dictionary<string, string>();  // PO AML ID -> parent process AML ID

        foreach (var procIE in allProcessIEs)
        {
            var procRefObj = GetRefObjValue(procIE);
            if (procRefObj != null)
                processRefObjMap[procIE.ID] = procRefObj;

            foreach (var ie in procIE.InternalElement)
            {
                if (ie.RefBaseSystemUnitPath == ElementToSuc[FpbTypes.ProcessOperator])
                {
                    poToProcessAmlId[ie.ID] = procIE.ID;
                    var poRefProcess = GetRefProcessValue(ie);
                    if (poRefProcess != null)
                        poRefObjMap[ie.ID] = poRefProcess;
                }
            }
        }

        // Determine entry process (no refObj)
        var entryProcess = allProcessIEs.FirstOrDefault(p => GetRefObjValue(p) == null)
            ?? allProcessIEs[0];

        // Parse all processes
        var processEntries = new List<Dictionary<string, object>>();
        var processIdMap = new Dictionary<string, string>(); // AML ID -> FPB.JS ID
        var amlToFpbId = new Dictionary<string, string>();   // AML element ID -> FPB.JS ID

        foreach (var procIE in allProcessIEs)
        {
            ParseProcess(procIE, allProcessIEs, processRefObjMap, poRefObjMap,
                processEntries, processIdMap, amlToFpbId, warnings);
        }

        // Post-processing: resolve cross-process references
        var poFpbToChildFpb = new Dictionary<string, string>();
        foreach (var entry in processEntries)
        {
            var ediList = (List<Dictionary<string, object>>)entry["elementDataInformation"];
            foreach (var elem in ediList)
            {
                if ((string)elem["$type"] == FpbTypes.ProcessOperator && elem.ContainsKey("_amlId"))
                {
                    var amlId = (string)elem["_amlId"];
                    if (poRefObjMap.TryGetValue(amlId, out var childProcessAmlId) &&
                        processIdMap.TryGetValue(childProcessAmlId, out var childFpbId))
                    {
                        poFpbToChildFpb[(string)elem["id"]] = childFpbId;
                    }
                }
            }
        }

        foreach (var entry in processEntries)
        {
            var proc = (Dictionary<string, object>)entry["process"];

            // isDecomposedProcessOperator: resolve AML PO ID -> FPB.JS PO ID
            // parent should point at the process containing the parent PO, NOT the PO itself.
            // If any lookup fails we still emit a brace-free ID via NormalizeId so the
            // JSON stays consistent with Phase 2.A (no {xxx} sneaks through).
            if (proc.TryGetValue("isDecomposedProcessOperator", out var iDPO) && iDPO is string idpo && !string.IsNullOrEmpty(idpo))
            {
                proc["isDecomposedProcessOperator"] = amlToFpbId.TryGetValue(idpo, out var poFpbId)
                    ? poFpbId
                    : NormalizeId(idpo);

                if (poToProcessAmlId.TryGetValue(idpo, out var parentProcAmlId)
                    && processIdMap.TryGetValue(parentProcAmlId, out var parentProcFpbId))
                {
                    proc["parent"] = parentProcFpbId;
                }
                else
                {
                    proc["parent"] = proc["isDecomposedProcessOperator"];
                }

                proc["id"] = proc["isDecomposedProcessOperator"];
            }

            // decomposedView on POs
            var ediList = (List<Dictionary<string, object>>)entry["elementDataInformation"];
            foreach (var elem in ediList)
            {
                if ((string)elem["$type"] == FpbTypes.ProcessOperator && elem.ContainsKey("decomposedView"))
                {
                    elem["decomposedView"] = elem["id"];
                    elem.Remove("_amlId");
                }
            }

            // consistsOfProcesses
            proc["consistsOfProcesses"] = ediList
                .Where(e => e.ContainsKey("decomposedView"))
                .Select(e => e["decomposedView"])
                .ToList();
        }

        // Build Project header
        var entryProcessFpbId = processIdMap[entryProcess.ID];
        var result = new List<object>
        {
            new Dictionary<string, object>
            {
                ["$type"] = FpbTypes.Project,
                ["name"] = ih.Name ?? "FPBJS_Project",
                ["targetNamespace"] = "http://www.hsu-ifa.de/fpbjs",
                ["entryPoint"] = entryProcessFpbId,
            }
        };
        result.AddRange(processEntries);

        var json = JsonSerializer.Serialize(result, new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        });
        return new ConversionResult<string>(json, warnings);
    }

    // ========================================================================
    // Process parser
    // ========================================================================

    private static void ParseProcess(
        InternalElementType processIE,
        List<InternalElementType> allProcessIEs,
        Dictionary<string, string> processRefObjMap,
        Dictionary<string, string> poRefObjMap,
        List<Dictionary<string, object>> processEntries,
        Dictionary<string, string> processIdMap,
        Dictionary<string, string> amlToFpbId,
        List<string> warnings)
    {
        var elementDataInformation = new List<Dictionary<string, object>>();
        var elementVisualInformation = new List<Dictionary<string, object>>();
        var stateIds = new List<string>();
        var poIds = new List<string>();
        var elementsContainerIds = new List<string>();

        // Interface map for link resolution
        var interfaceMap = new Dictionary<string, InterfaceInfo>();

        // Process SystemLimit first
        string? systemLimitId = null;
        var systemLimitIE = processIE.InternalElement
            .FirstOrDefault(ie => ie.RefBaseSystemUnitPath == ElementToSuc[FpbTypes.SystemLimit]);

        if (systemLimitIE != null)
        {
            var slName = ParseShortName(systemLimitIE) ?? systemLimitIE.Name ?? "SystemLimit";
            systemLimitId = NormalizeId(systemLimitIE.ID);
            var slVisual = ParseViewInformation(systemLimitIE);

            var slData = new Dictionary<string, object>
            {
                ["$type"] = FpbTypes.SystemLimit,
                ["id"] = systemLimitId,
                ["elementsContainer"] = new List<string>(),
                ["name"] = slName,
            };
            elementDataInformation.Add(slData);

            if (slVisual != null)
            {
                slVisual["id"] = systemLimitId;
                slVisual["type"] = FpbTypes.SystemLimit;
                slVisual["markers"] = new Dictionary<string, object>();
                elementVisualInformation.Add(slVisual);
            }
        }

        // Assign process ID — take the AML ID (stripped) so round-trips stay stable.
        var processId = NormalizeId(processIE.ID);
        processIdMap[processIE.ID] = processId;

        var processRefObj = GetRefObjValue(processIE);
        var parentPOId = processRefObj;

        // Parse each object IE
        var objectIEs = processIE.InternalElement
            .Where(ie =>
            {
                var suc = ie.RefBaseSystemUnitPath;
                return !string.IsNullOrEmpty(suc) && SucToElement.ContainsKey(suc)
                    && suc != ElementToSuc[FpbTypes.Process];
            }).ToList();

        var elementIdMap = new Dictionary<string, string>();

        // Pre-build the set of valid element IDs so the refObj export can
        // validate its target lives in the current scope before emitting.
        // Otherwise a stale refObj pointing at a removed PO leaks into the
        // JSON file.
        var validElementIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var ie in objectIEs)
            validElementIds.Add(NormalizeId(ie.ID));

        foreach (var ie in objectIEs)
        {
            var sucPath = ie.RefBaseSystemUnitPath;
            if (!SucToElement.TryGetValue(sucPath, out var fpbType)) continue;
            if (fpbType == FpbTypes.SystemLimit || fpbType == FpbTypes.Process) continue;

            // Element ID takes the AML ID (stripped) so UpdateInPlace can match later.
            var elemId = NormalizeId(ie.ID);
            var name = ParseShortName(ie) ?? ie.Name ?? "";

            elementIdMap[ie.ID] = elemId;
            amlToFpbId[ie.ID] = elemId;

            // Collect ExternalInterfaces
            foreach (var extIf in ie.ExternalInterface)
            {
                var refClass = extIf.RefBaseClassPath;
                if (!string.IsNullOrEmpty(refClass) && InterfaceToFlow.TryGetValue(refClass, out var info))
                {
                    interfaceMap[extIf.ID] = new InterfaceInfo
                    {
                        ElementId = elemId,
                        Direction = info.Direction,
                        FlowType = info.FlowType,
                        PortCoordinate = ParsePortCoordinate(extIf),
                        Waypoints = ParseWaypoints(extIf),
                    };
                }
            }

            // Check decomposition
            string? decomposedView = null;
            string? amlIdForPostProcess = null;
            if (fpbType == FpbTypes.ProcessOperator)
            {
                var poRefProcess = GetRefProcessValue(ie);
                if (poRefProcess != null)
                {
                    decomposedView = "__pending__";
                    amlIdForPostProcess = ie.ID;
                }
            }

            var elemData = new Dictionary<string, object>
            {
                ["$type"] = fpbType,
                ["id"] = elemId,
                ["incoming"] = new List<string>(),
                ["outgoing"] = new List<string>(),
                ["isAssignedTo"] = new List<string>(),
                ["name"] = name,
            };

            // Identification
            var identification = ParseIdentification(ie);
            if (identification != null)
                elemData["identification"] = identification;

            // Characteristics
            elemData["characteristics"] = ParseCharacteristics(ie);

            // Audit-fix #4: export refObj on states so the FPB.JS side (and
            // round-trip consumers like the export-button JSON file) can see
            // which sub-process states are boundary states (refObj set) vs
            // pure child states (refObj empty). FPB.JS itself derives boundary
            // semantics from geometry today, but downstream tooling reading
            // the export should not have to.
            if (ElementMetadataRegistry.Get(fpbType)?.IsState ?? false)
            {
                var refObjValue = ie.GetRefObjOrDerived();
                if (!string.IsNullOrEmpty(refObjValue))
                {
                    var normalisedTarget = NormalizeId(refObjValue);
                    // Only emit refObj if the target actually
                    // exists in this IH. Dangling refObjs come from POs that
                    // were removed without clearing the back-references.
                    if (validElementIds.Contains(normalisedTarget))
                        elemData["refObj"] = normalisedTarget;
                    // else: silently skip — stale ref shouldn't pollute the JSON.
                }
            }

            if (decomposedView != null)
            {
                elemData["decomposedView"] = decomposedView;
                elemData["_amlId"] = amlIdForPostProcess!;
            }

            elementDataInformation.Add(elemData);

            // Visual
            var visual = ParseViewInformation(ie);
            if (visual != null)
            {
                visual["id"] = elemId;
                visual["type"] = fpbType;
                visual["markers"] = new Dictionary<string, object>();
                elementVisualInformation.Add(visual);
            }

            // Behaviour flags routed through the element metadata registry —
            // one row per FPB type instead of scattered fpbType == "fpb:X"
            // string comparisons. Adding a new VDI 3682 element type is a
            // single registry entry plus its FpbMappings.ElementToSuc row.
            var meta = ElementMetadataRegistry.Get(fpbType);
            if (meta == null)
            {
                // Unknown type — still add to containers to preserve the
                // previous behaviour (warnings will pick up dangling refs).
                elementsContainerIds.Add(elemId);
            }
            else
            {
                if (!meta.LivesOutsideSystemLimit) elementsContainerIds.Add(elemId);
                if (meta.IsState) stateIds.Add(elemId);
                if (fpbType == FpbTypes.ProcessOperator) poIds.Add(elemId);
            }
        }

        // Parse InternalLinks -> Flows
        var flowDataMap = new Dictionary<string, Dictionary<string, object>>();

        foreach (var link in processIE.InternalLink)
        {
            var sideAId = ExtractInterfaceId(link.RefPartnerSideA);
            var sideBId = ExtractInterfaceId(link.RefPartnerSideB);

            if (!interfaceMap.TryGetValue(sideAId, out var sideA))
            {
                warnings.Add($"InternalLink '{link.Name}' skipped: interface '{sideAId}' not found.");
                continue;
            }
            if (!interfaceMap.TryGetValue(sideBId, out var sideB))
            {
                warnings.Add($"InternalLink '{link.Name}' skipped: interface '{sideBId}' not found.");
                continue;
            }

            var outSide = sideA.Direction == "out" ? sideA : sideB;
            var inSide = sideA.Direction == "in" ? sideA : sideB;

            // Flow ID takes the InternalLink ID (stripped) so updates can match.
            // CAEX InternalLink IDs are optional — NormalizeId falls back to NewId().
            var flowId = NormalizeId(link.ID);
            var flowType = outSide.FlowType;

            var flowData = new Dictionary<string, object>
            {
                ["$type"] = flowType,
                ["id"] = flowId,
                ["sourceRef"] = outSide.ElementId,
                ["targetRef"] = inSide.ElementId,
            };

            if (flowType != FpbTypes.Flow && flowType != FpbTypes.Usage)
                flowData["inTandemWith"] = new List<string>();

            flowDataMap[flowId] = flowData;

            // Build waypoints
            var waypoints = BuildWaypoints(outSide, inSide);
            if (waypoints.Count > 0)
            {
                elementVisualInformation.Add(new Dictionary<string, object>
                {
                    ["id"] = flowId,
                    ["type"] = flowType,
                    ["waypoints"] = waypoints,
                    ["markers"] = new Dictionary<string, object>(),
                });
            }

            // Update element references
            var sourceElem = elementDataInformation.FirstOrDefault(e => (string)e["id"] == outSide.ElementId);
            var targetElem = elementDataInformation.FirstOrDefault(e => (string)e["id"] == inSide.ElementId);
            if (sourceElem != null) ((List<string>)sourceElem["outgoing"]).Add(flowId);
            if (targetElem != null) ((List<string>)targetElem["incoming"]).Add(flowId);

            // isAssignedTo
            if (sourceElem != null && StateTypes.Contains((string)sourceElem["$type"]) &&
                targetElem != null && (string)targetElem["$type"] == FpbTypes.ProcessOperator)
            {
                var list = (List<string>)sourceElem["isAssignedTo"];
                if (!list.Contains((string)targetElem["id"])) list.Add((string)targetElem["id"]);
            }
            if (targetElem != null && StateTypes.Contains((string)targetElem["$type"]) &&
                sourceElem != null && (string)sourceElem["$type"] == FpbTypes.ProcessOperator)
            {
                var list = (List<string>)targetElem["isAssignedTo"];
                if (!list.Contains((string)sourceElem["id"])) list.Add((string)sourceElem["id"]);
            }
            if (flowType == FpbTypes.Usage && sourceElem != null && targetElem != null)
            {
                var srcList = (List<string>)sourceElem["isAssignedTo"];
                var tgtList = (List<string>)targetElem["isAssignedTo"];
                if (!srcList.Contains((string)targetElem["id"])) srcList.Add((string)targetElem["id"]);
                if (!tgtList.Contains((string)sourceElem["id"])) tgtList.Add((string)sourceElem["id"]);
            }

            // Usage connects to TechnicalResources outside the SystemLimit
            if (flowType != FpbTypes.Usage)
                elementsContainerIds.Add(flowId);
        }

        // Compute inTandemWith
        var sourceGroups = new Dictionary<string, List<string>>();
        foreach (var (flowId, flow) in flowDataMap)
        {
            if (!flow.ContainsKey("inTandemWith")) continue;
            var key = (string)flow["sourceRef"];
            if (!sourceGroups.TryGetValue(key, out var group))
            {
                group = new List<string>();
                sourceGroups[key] = group;
            }
            group.Add(flowId);
        }
        foreach (var group in sourceGroups.Values.Where(g => g.Count > 1))
        {
            foreach (var flowId in group)
                flowDataMap[flowId]["inTandemWith"] = group.Where(id => id != flowId).ToList();
        }

        foreach (var flow in flowDataMap.Values)
            elementDataInformation.Add(flow);

        // Sanity check: every ID we put into elementsContainer / consistsOfStates
        // / consistsOfProcessOperator MUST exist as an entry in
        // elementDataInformation. Otherwise FPB.JS will look up `undefined.type`
        // during import and crash with "Cannot read properties of undefined
        // (reading 'type')". Strip references to elements we never emitted
        // (typically AML elements with an unrecognised SUC).
        var emittedIds = new HashSet<string>(elementDataInformation
            .Select(e => (string)e["id"]));

        int strippedCount = 0;
        int beforeCount = elementsContainerIds.Count;
        elementsContainerIds.RemoveAll(id => !emittedIds.Contains(id));
        strippedCount += beforeCount - elementsContainerIds.Count;

        beforeCount = stateIds.Count;
        stateIds.RemoveAll(id => !emittedIds.Contains(id));
        strippedCount += beforeCount - stateIds.Count;

        beforeCount = poIds.Count;
        poIds.RemoveAll(id => !emittedIds.Contains(id));
        strippedCount += beforeCount - poIds.Count;

        if (strippedCount > 0)
        {
            warnings.Add($"Process '{processIE.Name}': stripped {strippedCount} dangling element " +
                         "reference(s) (IDs listed in container/states/POs but no matching elementData " +
                         "entry — typically an AML InternalElement whose SUC could not be resolved to " +
                         "an FPD type).");
        }

        // Update SystemLimit's elementsContainer
        if (systemLimitId != null)
        {
            var sl = elementDataInformation.FirstOrDefault(e => (string)e["id"] == systemLimitId);
            if (sl != null) sl["elementsContainer"] = elementsContainerIds;
        }

        // Build process entry
        var processEntry = new Dictionary<string, object>
        {
            ["process"] = new Dictionary<string, object>
            {
                ["$type"] = FpbTypes.Process,
                ["id"] = processId,
                ["elementsContainer"] = systemLimitId != null
                    ? new List<string>(new[] { systemLimitId }.Concat(
                        elementDataInformation
                            .Where(e => (string)e["$type"] == FpbTypes.TechnicalResource
                                     || (string)e["$type"] == FpbTypes.Usage)
                            .Select(e => (string)e["id"])))
                    : new List<string>(),
                ["isDecomposedProcessOperator"] = parentPOId ?? (object)"",
                ["consistsOfStates"] = stateIds,
                ["consistsOfSystemLimit"] = systemLimitId ?? (object)"",
                ["consistsOfProcesses"] = new List<object>(),
                ["consistsOfProcessOperator"] = poIds,
                ["parent"] = parentPOId ?? (object)"",
            },
            ["elementDataInformation"] = elementDataInformation,
            ["elementVisualInformation"] = elementVisualInformation,
        };

        processEntries.Add(processEntry);
    }

    // ========================================================================
    // Attribute parsers
    // ========================================================================

    private static string? GetRefObjValue(InternalElementType ie)
    {
        // Routed through the reference-type abstraction so that documents which
        // use refBaseObj / refExtendedObj / refComposedObj as the decomposition
        // link (per the ETFA 2026 Object-References framework) are recognised
        // without further code edits. refObj itself still wins when both are
        // present, preserving back-compat with current VDI 3682 documents.
        return ie.GetRefObjOrDerived();
    }

    private static string? GetRefProcessValue(InternalElementType ie)
    {
        var attr = ie.Attribute["refProcess"];
        if (attr == null) return null;
        var val = attr.Value;
        return string.IsNullOrEmpty(val) ? null : val;
    }

    private static string? ParseShortName(InternalElementType ie)
    {
        var ident = ie.Attribute[IdentificationSchema.AttributeName];
        if (ident == null) return null;
        var sn = ident.Attribute[IdentificationSchema.ShortName];
        return string.IsNullOrEmpty(sn?.Value) ? null : sn.Value;
    }

    private static Dictionary<string, object>? ParseIdentification(InternalElementType ie)
    {
        var ident = ie.Attribute[IdentificationSchema.AttributeName];
        if (ident == null) return null;

        var result = new Dictionary<string, object> { ["$type"] = FpbTypes.Identification };
        foreach (var field in IdentificationSchema.Fields)
            result[field] = ident.Attribute[field]?.Value ?? "";
        return result;
    }

    private static List<Dictionary<string, object>> ParseCharacteristics(InternalElementType ie)
    {
        var container = ie.Attribute["Characteristics"];
        if (container == null) return new List<Dictionary<string, object>>();

        var characteristics = new List<Dictionary<string, object>>();

        foreach (var cAttr in container.Attribute.Where(a => a.Name.StartsWith("Characteristic")))
        {
            var c = new Dictionary<string, object>();

            var cIdent = cAttr.Attribute["Category"];
            if (cIdent != null)
            {
                c["category"] = new Dictionary<string, object>
                {
                    ["uniqueIdent"] = cIdent.Attribute["uniqueIdent"]?.Value ?? "",
                    ["longName"] = cIdent.Attribute["longName"]?.Value ?? "",
                    ["shortName"] = cIdent.Attribute["shortName"]?.Value ?? "",
                    ["versionNumber"] = cIdent.Attribute["versionNumber"]?.Value ?? "",
                    ["revisionNumber"] = cIdent.Attribute["revisionNumber"]?.Value ?? "",
                };
            }

            var desc = cAttr.Attribute["DescriptiveElement"];
            if (desc != null)
            {
                c["descriptiveElement"] = new Dictionary<string, object>
                {
                    ["valueDeterminationProcess"] = desc.Attribute["valueDeterminationProcess"]?.Value ?? "",
                    ["representivity"] = desc.Attribute["representivity"]?.Value ?? "",
                    ["setpointValue"] = desc.Attribute["setpointValue"]?.Value ?? "",
                    ["validityLimits"] = desc.Attribute["validityLimits"]?.Value ?? "",
                    ["actualValues"] = desc.Attribute["actualValues"]?.Value ?? "",
                };
            }

            var rel = cAttr.Attribute["RelationalElement"];
            if (rel != null)
            {
                c["relationalElement"] = new Dictionary<string, object>
                {
                    ["view"] = rel.Attribute["view"]?.Value ?? "",
                    ["model"] = rel.Attribute["model"]?.Value ?? "",
                    ["regulationsForRelationalGeneration"] = rel.Attribute["regulationsForRelationalGeneration"]?.Value ?? "",
                };
            }

            characteristics.Add(c);
        }

        return characteristics;
    }

    private static Dictionary<string, object>? ParseViewInformation(InternalElementType ie)
    {
        var vi = ie.Attribute["ViewInformation"];
        if (vi == null) return null;

        var pos = vi.Attribute["position"];
        double x = 0, y = 0;
        if (pos != null)
        {
            double.TryParse(pos.Attribute["x"]?.Value, System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out x);
            double.TryParse(pos.Attribute["y"]?.Value, System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out y);
        }

        double.TryParse(vi.Attribute["width"]?.Value, System.Globalization.NumberStyles.Any,
            System.Globalization.CultureInfo.InvariantCulture, out double width);
        double.TryParse(vi.Attribute["height"]?.Value, System.Globalization.NumberStyles.Any,
            System.Globalization.CultureInfo.InvariantCulture, out double height);

        if (x == 0 && y == 0 && width == 0 && height == 0) return null;

        return new Dictionary<string, object>
        {
            ["x"] = x, ["y"] = y, ["width"] = width, ["height"] = height,
        };
    }

    private static double[]? ParsePortCoordinate(ExternalInterfaceType extIf)
    {
        var pcAttr = extIf.Attribute["PortCoordinate"];
        if (pcAttr == null) return null;

        if (!double.TryParse(pcAttr.Attribute["x"]?.Value, System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out double x)) return null;
        if (!double.TryParse(pcAttr.Attribute["y"]?.Value, System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out double y)) return null;

        return new[] { x, y };
    }

    private static List<double[]> ParseWaypoints(ExternalInterfaceType extIf)
    {
        var waypoints = new List<(int Index, double X, double Y)>();

        foreach (var attr in extIf.Attribute.Where(a => a.Name.StartsWith("Waypoint_")))
        {
            var pos = attr.Attribute["position"];
            if (pos == null) continue;

            if (!double.TryParse(pos.Attribute["x"]?.Value, System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture, out double x)) continue;
            if (!double.TryParse(pos.Attribute["y"]?.Value, System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture, out double y)) continue;

            var indexStr = attr.Name.Replace("Waypoint_", "");
            if (int.TryParse(indexStr, out var index))
                waypoints.Add((index, x, y));
        }

        return waypoints.OrderBy(w => w.Index).Select(w => new[] { w.X, w.Y }).ToList();
    }

    private static List<Dictionary<string, object>> BuildWaypoints(InterfaceInfo outSide, InterfaceInfo inSide)
    {
        var waypoints = new List<Dictionary<string, object>>();

        if (outSide.PortCoordinate != null)
        {
            waypoints.Add(new Dictionary<string, object>
            {
                ["original"] = new Dictionary<string, object>
                {
                    ["x"] = outSide.PortCoordinate[0],
                    ["y"] = outSide.PortCoordinate[1],
                },
                ["x"] = outSide.PortCoordinate[0],
                ["y"] = outSide.PortCoordinate[1],
            });
        }

        foreach (var wp in outSide.Waypoints)
        {
            waypoints.Add(new Dictionary<string, object>
            {
                ["x"] = wp[0], ["y"] = wp[1],
            });
        }

        if (inSide.PortCoordinate != null)
        {
            waypoints.Add(new Dictionary<string, object>
            {
                ["original"] = new Dictionary<string, object>
                {
                    ["x"] = inSide.PortCoordinate[0],
                    ["y"] = inSide.PortCoordinate[1],
                },
                ["x"] = inSide.PortCoordinate[0],
                ["y"] = inSide.PortCoordinate[1],
            });
        }

        return waypoints;
    }

    /// <summary>
    /// Extract the ExternalInterface ID from a RefPartnerSide value.
    /// CAEX 3.0 format: "InternalElementID:ExternalInterfaceID" or just "ExternalInterfaceID".
    /// GUIDs (with or without braces) never contain colons, so the separator is unambiguous.
    /// </summary>
    private static string ExtractInterfaceId(string refPartnerSide)
    {
        if (string.IsNullOrEmpty(refPartnerSide)) return "";
        var colonIdx = refPartnerSide.LastIndexOf(':');
        if (colonIdx < 0) return refPartnerSide;
        return refPartnerSide[(colonIdx + 1)..];
    }

    private static string NewId() => Guid.NewGuid().ToString("B");

    /// <summary>
    /// Convert an AML element ID (typically CAEX B-format like "{xxx-yyy}") to the
    /// raw FPB.JS-side ID format (no braces). Preserves IDs for downstream round-trips
    /// — UpdateInPlace in FpbJsonToCaex can match by the same value back to the AML
    /// element. Falls back to a fresh raw GUID if the AML ID is null/empty.
    /// </summary>
    /// <remarks>
    /// NOTE: the fallback path must NOT call <see cref="NewId"/>, which formats as
    /// "{xxx-yyy}". If we leaked a braced ID into the JSON output, FPB.JS would
    /// store it internally with braces and the next UpdateInPlace lookup would
    /// miss every connection (linkIndex keys are bare). This bit users hard on
    /// AML files whose InternalLinks had no explicit ID.
    /// </remarks>
    private static string NormalizeId(string? amlId)
    {
        if (string.IsNullOrEmpty(amlId)) return Guid.NewGuid().ToString();  // bare, no braces
        if (amlId.Length >= 2 && amlId[0] == '{' && amlId[^1] == '}')
            return amlId.Substring(1, amlId.Length - 2);
        return amlId;
    }

    private class InterfaceInfo
    {
        public string ElementId { get; set; } = "";
        public string Direction { get; set; } = "";
        public string FlowType { get; set; } = "";
        public double[]? PortCoordinate { get; set; }
        public List<double[]> Waypoints { get; set; } = new();
    }
}
