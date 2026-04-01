using System.Text.Json;
using Aml.Engine.CAEX;
using static FpbMapper.Conversion.FpbMappings;

namespace FpbMapper.Conversion;

/// <summary>
/// Convert CAEX 3.0 (AML) to FPB.JS JSON using Aml.Engine (port of aml-to-json.js).
/// </summary>
public static class CaexToFpbJson
{
    public static ConversionResult<string> Convert(CAEXDocument doc)
    {
        var warnings = new List<string>();
        var caex = doc.CAEXFile;

        // Find the first InstanceHierarchy
        var ih = caex.InstanceHierarchy.FirstOrDefault()
            ?? throw new InvalidOperationException("No InstanceHierarchy found");

        // Collect all FPD_Process InternalElements (flat in IH)
        var allProcessIEs = ih.InternalElement
            .Where(ie => ie.RefBaseSystemUnitPath == ElementToSuc["fpb:Process"])
            .ToList();

        if (allProcessIEs.Count == 0)
            throw new InvalidOperationException("No FPD_Process found in InstanceHierarchy");

        // Build refObj lookups
        var processRefObjMap = new Dictionary<string, string>(); // process AML ID -> parent PO ID
        var poRefObjMap = new Dictionary<string, string>();       // PO AML ID -> child process AML ID

        foreach (var procIE in allProcessIEs)
        {
            var procRefObj = GetRefObjValue(procIE);
            if (procRefObj != null)
                processRefObjMap[procIE.ID] = procRefObj;

            foreach (var ie in procIE.InternalElement)
            {
                if (ie.RefBaseSystemUnitPath == ElementToSuc["fpb:ProcessOperator"])
                {
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
                if ((string)elem["$type"] == "fpb:ProcessOperator" && elem.ContainsKey("_amlId"))
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
            if (proc.TryGetValue("isDecomposedProcessOperator", out var iDPO) && iDPO is string idpo && !string.IsNullOrEmpty(idpo))
            {
                if (amlToFpbId.TryGetValue(idpo, out var poFpbId))
                    proc["isDecomposedProcessOperator"] = poFpbId;
                proc["parent"] = proc["isDecomposedProcessOperator"];
                proc["id"] = proc["isDecomposedProcessOperator"];
            }

            // decomposedView on POs
            var ediList = (List<Dictionary<string, object>>)entry["elementDataInformation"];
            foreach (var elem in ediList)
            {
                if ((string)elem["$type"] == "fpb:ProcessOperator" && elem.ContainsKey("decomposedView"))
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
                ["$type"] = "fpb:Project",
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
            .FirstOrDefault(ie => ie.RefBaseSystemUnitPath == ElementToSuc["fpb:SystemLimit"]);

        if (systemLimitIE != null)
        {
            var slName = ParseShortName(systemLimitIE) ?? systemLimitIE.Name ?? "SystemLimit";
            systemLimitId = NewId();
            var slVisual = ParseViewInformation(systemLimitIE);

            var slData = new Dictionary<string, object>
            {
                ["$type"] = "fpb:SystemLimit",
                ["id"] = systemLimitId,
                ["elementsContainer"] = new List<string>(),
                ["name"] = slName,
            };
            elementDataInformation.Add(slData);

            if (slVisual != null)
            {
                slVisual["id"] = systemLimitId;
                slVisual["type"] = "fpb:SystemLimit";
                slVisual["markers"] = new Dictionary<string, object>();
                elementVisualInformation.Add(slVisual);
            }
        }

        // Assign process ID
        var processId = NewId();
        processIdMap[processIE.ID] = processId;

        var processRefObj = GetRefObjValue(processIE);
        var parentPOId = processRefObj;

        // Parse each object IE
        var objectIEs = processIE.InternalElement
            .Where(ie =>
            {
                var suc = ie.RefBaseSystemUnitPath;
                return !string.IsNullOrEmpty(suc) && SucToElement.ContainsKey(suc)
                    && suc != ElementToSuc["fpb:Process"];
            }).ToList();

        var elementIdMap = new Dictionary<string, string>();

        foreach (var ie in objectIEs)
        {
            var sucPath = ie.RefBaseSystemUnitPath;
            if (!SucToElement.TryGetValue(sucPath, out var fpbType)) continue;
            if (fpbType == "fpb:SystemLimit" || fpbType == "fpb:Process") continue;

            var elemId = NewId();
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
            if (fpbType == "fpb:ProcessOperator")
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

            elementsContainerIds.Add(elemId);
            if (StateTypes.Contains(fpbType)) stateIds.Add(elemId);
            if (fpbType == "fpb:ProcessOperator") poIds.Add(elemId);
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

            var flowId = NewId();
            var flowType = outSide.FlowType;

            var flowData = new Dictionary<string, object>
            {
                ["$type"] = flowType,
                ["id"] = flowId,
                ["sourceRef"] = outSide.ElementId,
                ["targetRef"] = inSide.ElementId,
            };

            if (flowType != "fpb:Flow" && flowType != "fpb:Usage")
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
                targetElem != null && (string)targetElem["$type"] == "fpb:ProcessOperator")
            {
                var list = (List<string>)sourceElem["isAssignedTo"];
                if (!list.Contains((string)targetElem["id"])) list.Add((string)targetElem["id"]);
            }
            if (targetElem != null && StateTypes.Contains((string)targetElem["$type"]) &&
                sourceElem != null && (string)sourceElem["$type"] == "fpb:ProcessOperator")
            {
                var list = (List<string>)targetElem["isAssignedTo"];
                if (!list.Contains((string)sourceElem["id"])) list.Add((string)sourceElem["id"]);
            }
            if (flowType == "fpb:Usage" && sourceElem != null && targetElem != null)
            {
                var srcList = (List<string>)sourceElem["isAssignedTo"];
                var tgtList = (List<string>)targetElem["isAssignedTo"];
                if (!srcList.Contains((string)targetElem["id"])) srcList.Add((string)targetElem["id"]);
                if (!tgtList.Contains((string)sourceElem["id"])) tgtList.Add((string)sourceElem["id"]);
            }

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
                ["$type"] = "fpb:Process",
                ["id"] = processId,
                ["elementsContainer"] = systemLimitId != null
                    ? new List<string>(new[] { systemLimitId }.Concat(
                        elementDataInformation.Where(e => (string)e["$type"] == "fpb:TechnicalResource")
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
        var attr = ie.Attribute["refObj"];
        if (attr == null) return null;
        var val = attr.Value;
        return string.IsNullOrEmpty(val) ? null : val;
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
        var ident = ie.Attribute["Identification"];
        if (ident == null) return null;
        var sn = ident.Attribute["shortName"];
        return string.IsNullOrEmpty(sn?.Value) ? null : sn.Value;
    }

    private static Dictionary<string, object>? ParseIdentification(InternalElementType ie)
    {
        var ident = ie.Attribute["Identification"];
        if (ident == null) return null;

        return new Dictionary<string, object>
        {
            ["$type"] = "fpb:Identification",
            ["uniqueIdent"] = ident.Attribute["uniqueIdent"]?.Value ?? "",
            ["longName"] = ident.Attribute["longName"]?.Value ?? "",
            ["shortName"] = ident.Attribute["shortName"]?.Value ?? "",
            ["versionNumber"] = ident.Attribute["versionNumber"]?.Value ?? "",
            ["revisionNumber"] = ident.Attribute["revisionNumber"]?.Value ?? "",
        };
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

    private class InterfaceInfo
    {
        public string ElementId { get; set; } = "";
        public string Direction { get; set; } = "";
        public string FlowType { get; set; } = "";
        public double[]? PortCoordinate { get; set; }
        public List<double[]> Waypoints { get; set; } = new();
    }
}
