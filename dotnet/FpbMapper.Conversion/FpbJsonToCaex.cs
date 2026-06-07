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
        var doc = CAEXDocument.New_CAEXDocument();
        var caex = doc.CAEXFile;
        caex.FileName = "fpb-export.aml";

        var sdi = caex.SourceDocumentInformation.FirstOrDefault() ?? caex.SourceDocumentInformation.Append();
        sdi.OriginName = "fpb-aml-mapper";
        sdi.OriginID = "fpb-aml-mapper-1.0";
        sdi.OriginVersion = "0.1.0";
        sdi.LastWritingDateTime = DateTime.UtcNow;

        var warnings = AppendInto(doc, project, entries);
        return new ConversionResult<CAEXDocument>(doc, warnings);
    }

    /// <summary>
    /// Inject FPD structures from FPB.JS JSON into an existing CAEX document.
    /// Ensures FPD libraries exist (idempotent) and appends a new InstanceHierarchy.
    /// Existing SourceDocumentInformation and FileName are preserved.
    /// </summary>
    /// <summary>
    /// Create a fresh, empty FPD InstanceHierarchy inside an existing CAEX document.
    /// Returns the new IH so the caller can hand it to the per-IH viewer (Phase 1.B+
    /// multi-IH plugin). The IH contains a single FPD_Process named
    /// <paramref name="processName"/> with a single FPD_SystemLimit child — i.e. an
    /// otherwise empty canvas the user can populate via the FPB.JS palette before
    /// clicking "Update InstanceHierarchy" to persist.
    /// </summary>
    public static InstanceHierarchyType CreateEmptyFpdInstanceHierarchy(
        CAEXDocument doc, string ihName, string processName = "NewProcess")
    {
        if (doc == null) throw new ArgumentNullException(nameof(doc));
        if (string.IsNullOrWhiteSpace(ihName)) ihName = "FpdInstanceHierarchy";
        if (string.IsNullOrWhiteSpace(processName)) processName = "NewProcess";

        FpdLibraries.EnsureLibraries(doc.CAEXFile);

        var ih = doc.CAEXFile.InstanceHierarchy.Append(ihName);

        var sucLib = doc.CAEXFile.SystemUnitClassLib[LibNames.SystemUnitClassLib]!;
        var sucLookup = sucLib.SystemUnitClass.ToDictionary(s => s.Name, s => s);

        var procIE = CreateInstance(sucLookup, "FPD_Process");
        procIE.ID = Guid.NewGuid().ToString("B");
        procIE.Name = processName;
        ih.Insert(procIE);
        SetAttrValue(procIE, "refObj", "");

        var slIE = CreateInstance(sucLookup, "FPD_SystemLimit");
        slIE.ID = Guid.NewGuid().ToString("B");
        slIE.Name = "SystemLimit_" + processName.Replace(" ", "");
        procIE.Insert(slIE);

        return ih;
    }

    public static ConversionResult<CAEXDocument> ImportInto(CAEXDocument existing, string json)
    {
        var (project, entries) = FpbJsonParser.Parse(json);
        return ImportInto(existing, project, entries);
    }

    /// <summary>
    /// Inject FPD structures into an existing CAEX document. See <see cref="ImportInto(CAEXDocument,string)"/>.
    /// </summary>
    public static ConversionResult<CAEXDocument> ImportInto(CAEXDocument existing, FpbProject project, List<ProcessEntry> entries)
    {
        var warnings = AppendInto(existing, project, entries);
        return new ConversionResult<CAEXDocument>(existing, warnings);
    }

    /// <summary>
    /// Synchronise the FPD content of an existing CAEX document with an FPB.JS JSON
    /// payload. Reuses the existing FPD InstanceHierarchy if one is present (heuristic:
    /// "any IH containing ≥1 FPD_Process IE") and updates AML elements in place by
    /// matching the FPB.JS element IDs to the AML element IDs (round-trip stable via
    /// CaexToFpbJson.NormalizeId). Custom user-added attributes / links / mappings on
    /// existing elements are preserved — only FPD-defined attributes are touched.
    /// <para>
    /// If no FPD IH is found, falls back to <see cref="ImportInto(CAEXDocument,string)"/>
    /// (append a fresh hierarchy) and logs a warning.
    /// </para>
    /// </summary>
    public static ConversionResult<CAEXDocument> UpdateInPlace(CAEXDocument existing, string json)
    {
        var (project, entries) = FpbJsonParser.Parse(json);
        return UpdateInPlace(existing, project, entries);
    }

    /// <summary>
    /// Same as <see cref="UpdateInPlace(CAEXDocument,string)"/> but targets a specific
    /// InstanceHierarchy by reference. Use when the document has several FPD IHs and
    /// each is being edited independently (e.g. one viewer-tab per IH).
    /// </summary>
    public static ConversionResult<CAEXDocument> UpdateInPlace(CAEXDocument existing, string json, InstanceHierarchyType targetIh)
    {
        var (project, entries) = FpbJsonParser.Parse(json);
        return UpdateInPlace(existing, project, entries, targetIh);
    }

    /// <inheritdoc cref="UpdateInPlace(CAEXDocument,string)"/>
    public static ConversionResult<CAEXDocument> UpdateInPlace(CAEXDocument existing, FpbProject project, List<ProcessEntry> entries)
        => UpdateInPlace(existing, project, entries, targetIh: null);

    /// <inheritdoc cref="UpdateInPlace(CAEXDocument,string,InstanceHierarchyType)"/>
    public static ConversionResult<CAEXDocument> UpdateInPlace(
        CAEXDocument existing,
        FpbProject project,
        List<ProcessEntry> entries,
        InstanceHierarchyType? targetIh)
    {
        var warnings = new List<string>();
        Validate(project, entries, warnings);

        var caex = existing.CAEXFile;

        // Make sure FPD libraries are available (idempotent — no-op if already present).
        FpdLibraries.EnsureLibraries(caex);

        var fpdIH = targetIh ?? FindFpdInstanceHierarchy(caex);
        if (fpdIH is null)
        {
            warnings.Add("No existing FPD InstanceHierarchy found — appending a fresh hierarchy instead.");
            AppendInto(existing, project, entries);
            return new ConversionResult<CAEXDocument>(existing, warnings);
        }

        var elementIndex = BuildElementIndex(fpdIH);
        var linkIndex = BuildInternalLinkIndex(fpdIH);

        // Build SUC lookup once — required by CreateInstance for new IEs (Phase 2.D add).
        var sucLib = caex.SystemUnitClassLib[LibNames.SystemUnitClassLib]!;
        var sucLookup = sucLib.SystemUnitClass.ToDictionary(s => s.Name, s => s);

        // Collect every FPB.JS-side ID we see across the incoming payload — used for
        // the orphan-removal pass at the end. (Process IDs included so a process
        // that no longer exists also gets cleaned up via its children's parents.)
        var incomingIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            incomingIds.Add(entry.Process.Id);
            foreach (var d in entry.ElementData) incomingIds.Add(d.Id);
        }

        var updatedCount = 0;
        var addedCount = 0;
        var connectionUpdatedCount = 0;
        var connectionAddedCount = 0;
        var processAddedCount = 0;
        var unmatchedSkippedCount = 0;

        foreach (var entry in entries)
        {
            var visualMap = entry.ElementVisual
                .GroupBy(v => v.Id)
                .ToDictionary(g => g.Key, g => g.First());

            // Find the AML Process IE this entry belongs to. Two FPB.JS conventions:
            //   (a) Top-level process: entry.Process.Id == process AML ID (stripped).
            //   (b) Decomposed sub-process: entry.Process.Id == parent-PO FPB-ID
            //       (CaexToFpbJson convention); sub-process IE located via refObj.
            // Phase 2.F: if no AML process exists yet for a decomposed entry, create
            // a fresh FPD_Process IE bound to the parent PO via refObj/refProcess.
            InternalElementType? procAml = ResolveProcessForEntry(fpdIH, entry, elementIndex);
            if (procAml is null && !string.IsNullOrEmpty(entry.Process.IsDecomposedProcessOperator))
            {
                procAml = AddProcess(fpdIH, entry.Process, elementIndex, sucLookup, warnings);
                if (procAml is not null) processAddedCount++;
            }

            foreach (var data in entry.ElementData)
            {
                // Phase 2.E.1 (update existing) + Phase 2.E.2 (add new).
                // P2 #2 — endpoint-swap detection: if the FPB.JS user reversed a flow's
                // direction, AInterface/BInterface no longer match sourceRef/targetRef.
                // Updating waypoints alone would silently desync; instead drop+recreate.
                if (ConnectionTypes.Contains(data.Type))
                {
                    if (linkIndex.TryGetValue(data.Id, out var link))
                    {
                        if (LinkEndpointsMatch(link, data, elementIndex))
                        {
                            UpdateExistingConnection(link, data, visualMap);
                            connectionUpdatedCount++;
                        }
                        else
                        {
                            link.Remove();
                            if (procAml is not null
                                && AddConnection(procAml, data, elementIndex, visualMap, warnings) is not null)
                            {
                                connectionAddedCount++;
                            }
                            else
                            {
                                warnings.Add($"Flow '{data.Id}' endpoints changed but could not be recreated.");
                            }
                        }
                    }
                    else if (procAml is not null)
                    {
                        if (AddConnection(procAml, data, elementIndex, visualMap, warnings) is not null)
                            connectionAddedCount++;
                    }
                    else
                    {
                        warnings.Add($"Flow '{data.Id}' could not be added: no host process resolved.");
                    }
                    continue;
                }

                if (elementIndex.TryGetValue(data.Id, out var ie))
                {
                    UpdateExistingElement(ie, data, visualMap);
                    updatedCount++;
                    continue;
                }

                // No matching AML element — try to add it under the matching process.
                if (procAml is null)
                {
                    unmatchedSkippedCount++;
                    continue;
                }

                if (AddElement(procAml, data, sucLookup, visualMap) is not null)
                {
                    addedCount++;
                }
                else
                {
                    warnings.Add($"Could not add element '{data.Id}' of type '{data.Type}' " +
                                 $"(SUC '{data.Type}' missing in library?)");
                    unmatchedSkippedCount++;
                }
            }
        }

        // Phase 2.D — remove orphaned FPD elements (AML elements whose ID no longer
        // appears in the incoming payload). Crucial: only delete FPD-classed IEs —
        // any custom user-added IE (different RefBaseSystemUnitPath) is kept.
        var removedCount = RemoveOrphanedFpdElements(fpdIH, elementIndex, incomingIds);

        // Phase 2.E.2 — remove orphaned InternalLinks (flows whose ID no longer
        // appears in the incoming payload).
        var connectionsRemoved = RemoveOrphanedConnections(linkIndex, incomingIds);

        // Phase 2.F.2 — remove orphaned sub-processes (Compose case): if the FPB.JS
        // side no longer carries a decomposed entry whose parent-PO ID matches an
        // AML sub-process's refObj, drop the sub-process subtree and clear the
        // parent PO's refProcess.
        var incomingDecomposedParentPoIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in entries)
        {
            if (!string.IsNullOrEmpty(e.Process.IsDecomposedProcessOperator))
                incomingDecomposedParentPoIds.Add(e.Process.IsDecomposedProcessOperator);
        }
        var processesRemoved = RemoveOrphanedSubProcesses(fpdIH, incomingDecomposedParentPoIds, elementIndex);

        warnings.Add(
            $"UpdateInPlace summary: updated={updatedCount}, added={addedCount}, " +
            $"connection-waypoints-updated={connectionUpdatedCount}, " +
            $"connections-added={connectionAddedCount}, " +
            $"processes-added={processAddedCount}, " +
            $"sub-processes-removed={processesRemoved}, " +
            $"removed-element-orphans={removedCount}, " +
            $"removed-connection-orphans={connectionsRemoved}, " +
            $"unmatched-skipped={unmatchedSkippedCount}.");

        return new ConversionResult<CAEXDocument>(existing, warnings);
    }

    /// <summary>
    /// Create a fresh FPD InternalElement matching <paramref name="data"/> as a child
    /// of <paramref name="parent"/>, mirroring the same SUC/instantiation path used by
    /// BuildProcess for a green-field Convert. Returns null if the data type has no
    /// SUC mapping.
    /// </summary>
    private static InternalElementType? AddElement(
        InternalElementType parent,
        ElementData data,
        Dictionary<string, SystemUnitFamilyType> sucLookup,
        Dictionary<string, VisualInfo> visualMap)
    {
        if (!ElementToSuc.TryGetValue(data.Type, out var sucPath)) return null;
        var sucName = sucPath.Split('/')[1];
        if (!sucLookup.ContainsKey(sucName)) return null;

        var ie = CreateInstance(sucLookup, sucName);
        ie.ID = WrapBraces(data.Id);
        ie.Name = !string.IsNullOrEmpty(data.Name) ? data.Name : sucName;
        parent.Insert(ie);

        SetIdentification(ie, data.Identification, data.Name ?? "");
        if (visualMap.TryGetValue(data.Id, out var visual))
            SetViewInformation(ie, visual);

        // P2 #3 — Characteristics. Safe to call on a freshly-created IE: there are no
        // existing characteristic attributes to duplicate (the non-idempotence flagged
        // for UpdateExistingElement only matters when the IE already carries them).
        if (data.Characteristics != null && data.Characteristics.Count > 0)
            SetCharacteristics(ie, data.Characteristics);

        return ie;
    }

    /// <summary>
    /// Remove FPD-classed InternalElements from the IH whose bare ID does not appear
    /// in the incoming payload. Non-FPD elements (custom user IEs, foreign-class IEs)
    /// are always kept, even if their ID is missing — the FPB.JS side does not own them.
    /// <para>
    /// FPD_Process IEs are also kept regardless of incoming-id match because the FPB.JS
    /// convention overwrites a sub-process's ID with its parent-PO ID, so a sub-process
    /// in CAEX would always look "orphaned" by ID. Process lifecycle (creation /
    /// removal of FPD_Processes) is handled in Phase 2.F.
    /// </para>
    /// </summary>
    private static int RemoveOrphanedFpdElements(
        InstanceHierarchyType fpdIH,
        Dictionary<string, InternalElementType> elementIndex,
        HashSet<string> incomingIds)
    {
        var fpdSucPaths = new HashSet<string>(ElementToSuc.Values);
        var processSuc = ElementToSuc["fpb:Process"];

        var toRemove = elementIndex
            .Where(kv => !incomingIds.Contains(kv.Key)
                         && kv.Value.RefBaseSystemUnitPath is { } suc
                         && fpdSucPaths.Contains(suc)
                         && suc != processSuc)
            .Select(kv => kv.Value)
            .ToList();

        foreach (var ie in toRemove)
            ie.Remove();

        return toRemove.Count;
    }

    private static string WrapBraces(string id)
    {
        if (string.IsNullOrEmpty(id)) return Guid.NewGuid().ToString("B");
        return (id.Length >= 2 && id[0] == '{' && id[^1] == '}') ? id : "{" + id + "}";
    }

    /// <summary>
    /// Derive a deterministic CAEX-B-format ID for a sub-process from its parent PO.
    /// Same parent PO → same sub-process ID across Convert+UpdateInPlace runs and
    /// even across Decompose→Compose→Decompose cycles. Uses a Version-5 (name-based
    /// SHA-1) UUID under the namespace "fpd-subprocess-of:" so the value is fixed
    /// for the lifetime of the convention.
    /// </summary>
    private static string DeriveSubProcessId(string parentPoBareId)
    {
        if (string.IsNullOrEmpty(parentPoBareId)) return NewId();
        using var sha = System.Security.Cryptography.SHA1.Create();
        var bytes = sha.ComputeHash(
            System.Text.Encoding.UTF8.GetBytes("fpd-subprocess-of:" + parentPoBareId));
        var guidBytes = new byte[16];
        Array.Copy(bytes, guidBytes, 16);
        // Version-5 (name-based SHA-1) marker.
        guidBytes[6] = (byte)((guidBytes[6] & 0x0F) | 0x50);
        // RFC 4122 variant marker.
        guidBytes[8] = (byte)((guidBytes[8] & 0x3F) | 0x80);
        return new Guid(guidBytes).ToString("B");
    }

    /// <summary>
    /// Map an incoming FPB.JS ProcessEntry to its AML Process IE. Handles both
    /// top-level processes (entry.Process.Id is the process AML ID) and decomposed
    /// sub-processes (entry.Process.Id equals the parent ProcessOperator's FPB ID
    /// by CaexToFpbJson convention; the actual sub-process IE is located by walking
    /// refObj from the parent PO).
    /// </summary>
    private static InternalElementType? ResolveProcessForEntry(
        InstanceHierarchyType fpdIH,
        ProcessEntry entry,
        Dictionary<string, InternalElementType> elementIndex)
    {
        var processSuc = ElementToSuc["fpb:Process"];

        // Top-level case: entry.Process.Id is directly the process AML ID.
        if (string.IsNullOrEmpty(entry.Process.IsDecomposedProcessOperator)
            && elementIndex.TryGetValue(entry.Process.Id, out var directHit)
            && directHit.RefBaseSystemUnitPath == processSuc)
        {
            return directHit;
        }

        // Sub-process case: find via refObj on a Process IE pointing back at the parent PO.
        var parentPoId = string.IsNullOrEmpty(entry.Process.IsDecomposedProcessOperator)
            ? entry.Process.Id
            : entry.Process.IsDecomposedProcessOperator;
        if (!elementIndex.TryGetValue(parentPoId, out var parentPo)) return null;
        var parentPoAmlId = parentPo.ID;
        return fpdIH.InternalElement.FirstOrDefault(ie =>
            ie.RefBaseSystemUnitPath == processSuc
            && (ie.Attribute["refObj"]?.Value ?? "") == parentPoAmlId);
    }

    // ──────────────────────────────────────────────────────────────────────
    // Phase 2.E.1: connection-waypoint diff
    // ──────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Walk the FPD IH and index every InternalLink by its bare AML ID (Phase 2.A
    /// stamps each link's CAEX ID with the FPB.JS flow ID in B-format).
    /// </summary>
    private static Dictionary<string, InternalLinkType> BuildInternalLinkIndex(InstanceHierarchyType fpdIH)
    {
        var index = new Dictionary<string, InternalLinkType>(StringComparer.OrdinalIgnoreCase);
        WalkInternalLinks(fpdIH.InternalElement, link =>
        {
            var bare = StripBraces(link.ID);
            if (!string.IsNullOrEmpty(bare))
                index[bare] = link;
        });
        return index;
    }

    private static void WalkInternalLinks(IEnumerable<InternalElementType> roots, Action<InternalLinkType> visit)
    {
        foreach (var ie in roots)
        {
            foreach (var link in ie.InternalLink) visit(link);
            WalkInternalLinks(ie.InternalElement, visit);
        }
    }

    /// <summary>Walk CAEXParent upwards and test whether <paramref name="ancestor"/> is on the path (P2 #1).</summary>
    private static bool IsDescendantOf(CAEXBasicObject? node, InternalElementType ancestor)
    {
        for (var n = node?.CAEXParent; n != null; n = n.CAEXParent)
            if (ReferenceEquals(n, ancestor)) return true;
        return false;
    }

    /// <summary>
    /// True if the existing AML link's source/target sides still match the FPB.JS
    /// flow's sourceRef/targetRef. False if the user swapped the flow direction or
    /// rerouted it to different endpoints (P2 #2).
    /// </summary>
    private static bool LinkEndpointsMatch(
        InternalLinkType link,
        ElementData flowData,
        Dictionary<string, InternalElementType> elementIndex)
    {
        if (string.IsNullOrEmpty(flowData.SourceRef) || string.IsNullOrEmpty(flowData.TargetRef))
            return true;   // not enough info to disprove — keep the link, let waypoint update run.
        if (!elementIndex.TryGetValue(flowData.SourceRef, out var expectedSource)) return false;
        if (!elementIndex.TryGetValue(flowData.TargetRef, out var expectedTarget)) return false;

        var aIface = link.AInterface as ExternalInterfaceType;
        var bIface = link.BInterface as ExternalInterfaceType;
        if (aIface is null || bIface is null) return false;

        return ReferenceEquals(aIface.CAEXParent, expectedSource)
            && ReferenceEquals(bIface.CAEXParent, expectedTarget);
    }

    /// <summary>
    /// Update PortCoordinate (both ends) + Waypoint_n attributes (source side) of
    /// an existing InternalLink so the diagram path the user dragged in FPB.JS
    /// shows up in CAEX. Custom user attributes on the ExternalInterfaces stay
    /// untouched — we only mutate the FPD-defined coordinate attributes.
    /// </summary>
    private static void UpdateExistingConnection(
        InternalLinkType link,
        ElementData flowData,
        Dictionary<string, VisualInfo> visualMap)
    {
        if (!visualMap.TryGetValue(flowData.Id, out var visual)) return;
        if (visual.Waypoints.Count == 0) return;

        var aIface = link.AInterface as ExternalInterfaceType;
        var bIface = link.BInterface as ExternalInterfaceType;

        if (aIface != null)
        {
            var first = visual.Waypoints[0];
            var firstSrc = first.Original ?? first;
            UpdatePortCoordinate(aIface, firstSrc.X, firstSrc.Y);

            // Replace the intermediate Waypoint_n set. Skip first and last (they
            // live as PortCoordinate on AInterface / BInterface).
            var intermediates = visual.Waypoints.Skip(1).SkipLast(1).ToList();
            ReplaceWaypointAttrs(aIface, intermediates);
        }

        if (bIface != null && visual.Waypoints.Count >= 2)
        {
            var last = visual.Waypoints[^1];
            var lastSrc = last.Original ?? last;
            UpdatePortCoordinate(bIface, lastSrc.X, lastSrc.Y);
        }
    }

    private static void UpdatePortCoordinate(ExternalInterfaceType iface, double x, double y)
    {
        var portCoord = iface.Attribute["PortCoordinate"];
        if (portCoord == null) return; // Old export shape — Phase 2.E.2 may add it.
        SetDoubleSubAttr(portCoord, "x", x);
        SetDoubleSubAttr(portCoord, "y", y);
    }

    private static void ReplaceWaypointAttrs(ExternalInterfaceType iface, List<WaypointInfo> waypoints)
    {
        // Remove existing Waypoint_n attributes — they are managed solely by the FPB.JS
        // viewer geometry, never user-annotated, so a wholesale rewrite is safe.
        var existing = iface.Attribute
            .Where(a => a.Name != null && a.Name.StartsWith("Waypoint_", StringComparison.Ordinal))
            .ToList();
        foreach (var attr in existing) attr.Remove();

        for (int i = 0; i < waypoints.Count; i++)
        {
            AddWaypointAttr(iface, i + 1, waypoints[i].X, waypoints[i].Y);
        }
    }

    // ──────────────────────────────────────────────────────────────────────
    // Phase 2.E.2: connection add / remove
    // ──────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Create a new InternalLink under the given AML process, mirroring the
    /// ExternalInterface / link / waypoint layout that BuildProcess uses for a
    /// green-field Convert. Returns null and logs a warning if the flow can't
    /// be wired (unknown type, missing endpoints).
    /// </summary>
    private static InternalLinkType? AddConnection(
        InternalElementType procAml,
        ElementData flowData,
        Dictionary<string, InternalElementType> elementIndex,
        Dictionary<string, VisualInfo> visualMap,
        List<string> warnings)
    {
        if (!FlowToInterface.TryGetValue(flowData.Type, out var ifacePaths))
        {
            warnings.Add($"Cannot add flow '{flowData.Id}': unknown type '{flowData.Type}'.");
            return null;
        }
        if (string.IsNullOrEmpty(flowData.SourceRef) || string.IsNullOrEmpty(flowData.TargetRef))
        {
            warnings.Add($"Cannot add flow '{flowData.Id}': missing sourceRef or targetRef.");
            return null;
        }
        if (!elementIndex.TryGetValue(flowData.SourceRef, out var sourceIE)
            || !elementIndex.TryGetValue(flowData.TargetRef, out var targetIE))
        {
            warnings.Add($"Cannot add flow '{flowData.Id}': source or target element not found in AML.");
            return null;
        }

        // P2 #1 — cross-process defence. With FPB.JS-side ID collisions being possible
        // across layers, the flat elementIndex could resolve to an IE in a different
        // process subtree. Warn (but proceed) so the maintainer sees it in the log.
        if (!IsDescendantOf(sourceIE, procAml) && !IsDescendantOf(targetIE, procAml))
        {
            warnings.Add($"Flow '{flowData.Id}': neither endpoint lives under process " +
                         $"'{procAml.Name}' — link may target the wrong layer.");
        }

        // Source-side ExternalInterface (carries PortCoordinate + Waypoint_n).
        var outBaseName = ifacePaths.Out.Split('/')[1];
        var sourceIface = sourceIE.ExternalInterface.Append(outBaseName);
        sourceIface.ID = NewId();
        sourceIface.RefBaseClassPath = ifacePaths.Out;

        // Target-side ExternalInterface (carries PortCoordinate only).
        var inBaseName = ifacePaths.In.Split('/')[1];
        var targetIface = targetIE.ExternalInterface.Append(inBaseName);
        targetIface.ID = NewId();
        targetIface.RefBaseClassPath = ifacePaths.In;

        if (visualMap.TryGetValue(flowData.Id, out var visual) && visual.Waypoints.Count > 0)
        {
            var first = visual.Waypoints[0];
            var firstSrc = first.Original ?? first;
            AddPortCoordinate(sourceIface, firstSrc.X, firstSrc.Y);

            var intermediates = visual.Waypoints.Skip(1).SkipLast(1).ToList();
            for (int i = 0; i < intermediates.Count; i++)
            {
                var wp = intermediates[i];
                if (wp.Original != null) continue;
                AddWaypointAttr(sourceIface, i + 1, wp.X, wp.Y);
            }

            if (visual.Waypoints.Count >= 2)
            {
                var last = visual.Waypoints[^1];
                var lastSrc = last.Original ?? last;
                AddPortCoordinate(targetIface, lastSrc.X, lastSrc.Y);
            }
            else
            {
                AddEmptyPortCoordinate(targetIface);
            }
        }
        else
        {
            AddEmptyPortCoordinate(sourceIface);
            AddEmptyPortCoordinate(targetIface);
        }

        // Match the linkName convention BuildProcess uses for green-field links.
        var sourceName = (sourceIE.Name ?? "Source").Replace(" ", "").Replace("\n", "");
        var targetName = (targetIE.Name ?? "Target").Replace(" ", "").Replace("\n", "");
        var linkName = flowData.Type == "fpb:Usage"
            ? $"{sourceName}_uses_{targetName}"
            : $"{sourceName}_to_{targetName}";

        var link = procAml.InternalLink.Append(linkName);
        link.ID = WrapBraces(flowData.Id);
        link.AInterface = sourceIface;
        link.BInterface = targetIface;
        return link;
    }

    /// <summary>
    /// Remove InternalLinks whose ID no longer appears in the incoming payload.
    /// CAEX has no FPD-vs-foreign-class distinction for links (unlike IEs), so a
    /// missing ID is the only signal here — assume FPB.JS owns the link lifecycle.
    /// </summary>
    private static int RemoveOrphanedConnections(
        Dictionary<string, InternalLinkType> linkIndex,
        HashSet<string> incomingIds)
    {
        var toRemove = linkIndex
            .Where(kv => !incomingIds.Contains(kv.Key))
            .Select(kv => kv.Value)
            .ToList();

        foreach (var link in toRemove) link.Remove();
        return toRemove.Count;
    }

    // ──────────────────────────────────────────────────────────────────────
    // Phase 2.F: process lifecycle
    // ──────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Create a fresh FPD_Process IE in the FPD IH for a newly-decomposed PO.
    /// Wires refObj on the sub-process to the parent PO's AML ID and refProcess
    /// on the parent PO back to the new sub-process — matching the layout produced
    /// by a green-field Convert. The sub-process gets a mandatory FPD_SystemLimit
    /// child so subsequent element-add passes have a host for States/POs.
    /// </summary>
    private static InternalElementType? AddProcess(
        InstanceHierarchyType fpdIH,
        Models.FpbProcess incomingProcess,
        Dictionary<string, InternalElementType> elementIndex,
        Dictionary<string, SystemUnitFamilyType> sucLookup,
        List<string> warnings)
    {
        if (string.IsNullOrEmpty(incomingProcess.IsDecomposedProcessOperator))
        {
            warnings.Add($"Cannot add top-level process '{incomingProcess.Id}' via UpdateInPlace " +
                         "— only decomposed sub-processes can be created from FPB.JS edits.");
            return null;
        }
        if (!elementIndex.TryGetValue(incomingProcess.IsDecomposedProcessOperator, out var parentPo))
        {
            warnings.Add($"Cannot decompose: parent PO '{incomingProcess.IsDecomposedProcessOperator}' " +
                         "not found in the AML document.");
            return null;
        }

        // Phase 2.F.2 — remove sub-process IEs whose parent PO no longer carries a
        // decomposedView (the FPB.JS user composed it back). Top-level processes are
        // deliberately untouched: dropping them would dump the whole model.
        return AddProcessImpl(fpdIH, parentPo, sucLookup, elementIndex);
    }

    private static int RemoveOrphanedSubProcesses(
        InstanceHierarchyType fpdIH,
        HashSet<string> incomingDecomposedParentPoIds,
        Dictionary<string, InternalElementType> elementIndex)
    {
        var processSuc = ElementToSuc["fpb:Process"];
        var subProcesses = fpdIH.InternalElement
            .Where(ie => ie.RefBaseSystemUnitPath == processSuc
                         && !string.IsNullOrEmpty(ie.Attribute["refObj"]?.Value))
            .ToList();

        var removed = 0;
        foreach (var sub in subProcesses)
        {
            var parentRef = sub.Attribute["refObj"]?.Value ?? "";
            var parentBare = StripBraces(parentRef);
            if (incomingDecomposedParentPoIds.Contains(parentBare)) continue;

            // Clear the back-link on the parent PO (best-effort).
            if (elementIndex.TryGetValue(parentBare, out var parentPo))
                SetAttrValue(parentPo, "refProcess", "");

            sub.Remove();
            removed++;
        }
        return removed;
    }

    private static InternalElementType AddProcessImpl(
        InstanceHierarchyType fpdIH,
        InternalElementType parentPo,
        Dictionary<string, SystemUnitFamilyType> sucLookup,
        Dictionary<string, InternalElementType> elementIndex)
    {
        var procIE = CreateInstance(sucLookup, "FPD_Process");
        // Deterministic AML ID derived from the parent PO. A Decompose→Compose→
        // Decompose cycle therefore yields the same sub-process IE ID, so external
        // tooling watching the AML file sees a clean diff. The FPB.JS-side ID
        // convention (sub-process FPB ID == parent-PO FPB ID) is unaffected because
        // CaexToFpbJson's post-processing salvages it from refObj regardless.
        procIE.ID = DeriveSubProcessId(StripBraces(parentPo.ID));
        procIE.Name = parentPo.Name ?? "Sub-Process";
        fpdIH.Insert(procIE);
        SetAttrValue(procIE, "refObj", parentPo.ID);

        // NOTE: do NOT pre-create FPD_SystemLimit here. The incoming entry's
        // ElementData carries a fpb:SystemLimit element with the FPB.JS-side ID,
        // and the regular AddElement loop below picks it up and creates the SL
        // with the correct ID. Pre-creating one here would yield two SystemLimits
        // per sub-process (audit P0-1).

        SetAttrValue(parentPo, "refProcess", procIE.ID);

        var procBare = StripBraces(procIE.ID);
        if (!string.IsNullOrEmpty(procBare)) elementIndex[procBare] = procIE;

        return procIE;
    }

    /// <summary>
    /// Update FPD-managed properties on an existing InternalElement, leaving custom
    /// user-added Attributes / ExternalInterfaces / MappingObjects in place.
    /// Phase 2.C scope: name + Identification + ViewInformation.
    /// Characteristics are deferred until SetCharacteristics has an idempotent form.
    /// </summary>
    private static void UpdateExistingElement(
        InternalElementType ie,
        ElementData data,
        Dictionary<string, VisualInfo> visualMap)
    {
        // Name — only overwrite if the incoming side actually carries one (avoid
        // wiping a meaningful AML name with FPB.JS's default "").
        if (!string.IsNullOrEmpty(data.Name))
            ie.Name = data.Name;

        // Identification: SetIdentification's helpers use SetSubAttr which only
        // updates value-of-existing attributes — safe to call on an already-populated
        // element.
        SetIdentification(ie, data.Identification, data.Name);

        // ViewInformation: SetViewInformation uses SetDoubleSubAttr which is idempotent
        // (looks up existing sub-attributes and updates their value).
        if (visualMap.TryGetValue(data.Id, out var visual))
            SetViewInformation(ie, visual);

        // TODO Phase 2.C+: idempotent SetCharacteristics (current impl appends, would dupe).
    }

    // ──────────────────────────────────────────────────────────────────────
    // UpdateInPlace helpers (Phase 2.B foundation, used by 2.C / 2.D below)
    // ──────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Heuristic: the first <see cref="InstanceHierarchyType"/> that contains at least
    /// one direct-child FPD_Process InternalElement. Returns null if no such IH exists.
    /// </summary>
    private static InstanceHierarchyType? FindFpdInstanceHierarchy(CAEXFileType caex)
    {
        var fpdProcessSuc = ElementToSuc["fpb:Process"];
        return caex.InstanceHierarchy.FirstOrDefault(ih =>
            ih.InternalElement.Any(ie => ie.RefBaseSystemUnitPath == fpdProcessSuc));
    }

    /// <summary>
    /// Walk the FPD IH and index every InternalElement by its bare AML ID (CAEX uses
    /// B-format "{xxx-yyy}"; the index uses the raw form without braces so it matches
    /// FPB.JS-side IDs produced by <c>CaexToFpbJson.NormalizeId</c>).
    /// </summary>
    private static Dictionary<string, InternalElementType> BuildElementIndex(InstanceHierarchyType fpdIH)
    {
        var index = new Dictionary<string, InternalElementType>(StringComparer.OrdinalIgnoreCase);
        WalkInternalElements(fpdIH.InternalElement, ie =>
        {
            var bare = StripBraces(ie.ID);
            if (!string.IsNullOrEmpty(bare))
                index[bare] = ie;
        });
        return index;
    }

    private static void WalkInternalElements(IEnumerable<InternalElementType> roots, Action<InternalElementType> visit)
    {
        foreach (var ie in roots)
        {
            visit(ie);
            WalkInternalElements(ie.InternalElement, visit);
        }
    }

    /// <summary>Strip B-format CAEX braces (<c>{xxx}</c>) to get the raw GUID form FPB.JS uses.</summary>
    private static string StripBraces(string? amlId)
    {
        if (string.IsNullOrEmpty(amlId)) return "";
        if (amlId.Length >= 2 && amlId[0] == '{' && amlId[^1] == '}')
            return amlId.Substring(1, amlId.Length - 2);
        return amlId;
    }

    /// <summary>
    /// Core builder: validates, ensures libraries, and appends a fresh InstanceHierarchy
    /// containing the FPD processes. Does not touch FileName or SourceDocumentInformation.
    /// </summary>
    private static List<string> AppendInto(CAEXDocument doc, FpbProject project, List<ProcessEntry> entries)
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

        var caex = doc.CAEXFile;

        // Libraries MUST exist before CreateClassInstance() can work (idempotent)
        FpdLibraries.EnsureLibraries(caex);

        // Build SUC lookup for CreateClassInstance()
        var sucLib = caex.SystemUnitClassLib[LibNames.SystemUnitClassLib]!;
        var sucLookup = new Dictionary<string, SystemUnitFamilyType>();
        foreach (var suc in sucLib.SystemUnitClass)
            sucLookup[suc.Name] = suc;

        // InstanceHierarchy (always appended fresh — keeps imports isolated)
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

        return warnings;
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
            // Stamp the link with the FPB.JS flow ID (B-format) so a CaexToFpbJson
            // round-trip via NormalizeId(link.ID) recovers the same FPB-side ID.
            link.ID = NormalizeId(flowId);
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
