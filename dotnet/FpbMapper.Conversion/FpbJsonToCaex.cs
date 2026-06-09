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
    public static ConversionResult<CAEXDocument> Convert(string json, MapperOptions? options = null)
    {
        var (project, entries) = FpbJsonParser.Parse(json);
        return Convert(project, entries, options);
    }

    public static ConversionResult<CAEXDocument> Convert(FpbProject project, List<ProcessEntry> entries, MapperOptions? options = null)
    {
        var doc = CAEXDocument.New_CAEXDocument();
        var caex = doc.CAEXFile;
        caex.FileName = "fpb-export.aml";

        var sdi = caex.SourceDocumentInformation.FirstOrDefault() ?? caex.SourceDocumentInformation.Append();
        sdi.OriginName = "fpb-aml-mapper";
        sdi.OriginID = "fpb-aml-mapper-1.0";
        sdi.OriginVersion = "0.1.0";
        sdi.LastWritingDateTime = DateTime.UtcNow;

        var warnings = AppendInto(doc, project, entries, options);
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

        // Default visual layout — without explicit ViewInformation the FPB.JS
        // canvas renders the SystemLimit as a 0×0 invisible box. These values
        // give the user a reasonable empty canvas to start dropping shapes
        // onto from the palette.
        SetViewInformation(slIE, new VisualInfo
        {
            X = 100,
            Y = 100,
            Width = 600,
            Height = 400,
        });

        return ih;
    }

    public static ConversionResult<CAEXDocument> ImportInto(CAEXDocument existing, string json, MapperOptions? options = null)
    {
        var (project, entries) = FpbJsonParser.Parse(json);
        return ImportInto(existing, project, entries, options);
    }

    /// <summary>
    /// Inject FPD structures into an existing CAEX document. See <see cref="ImportInto(CAEXDocument,string,MapperOptions)"/>.
    /// </summary>
    public static ConversionResult<CAEXDocument> ImportInto(CAEXDocument existing, FpbProject project, List<ProcessEntry> entries, MapperOptions? options = null)
    {
        var warnings = AppendInto(existing, project, entries, options);
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
    public static ConversionResult<CAEXDocument> UpdateInPlace(CAEXDocument existing, string json, MapperOptions? options = null)
    {
        var (project, entries) = FpbJsonParser.Parse(json);
        return UpdateInPlace(existing, project, entries, targetIh: null, options);
    }

    /// <summary>
    /// Same as <see cref="UpdateInPlace(CAEXDocument,string,MapperOptions)"/> but
    /// targets a specific InstanceHierarchy by reference. Use when the document
    /// has several FPD IHs and each is being edited independently.
    /// </summary>
    public static ConversionResult<CAEXDocument> UpdateInPlace(CAEXDocument existing, string json, InstanceHierarchyType targetIh, MapperOptions? options = null)
    {
        var (project, entries) = FpbJsonParser.Parse(json);
        return UpdateInPlace(existing, project, entries, targetIh, options);
    }

    /// <inheritdoc cref="UpdateInPlace(CAEXDocument,string,MapperOptions)"/>
    public static ConversionResult<CAEXDocument> UpdateInPlace(CAEXDocument existing, FpbProject project, List<ProcessEntry> entries)
        => UpdateInPlace(existing, project, entries, targetIh: null, options: null);

    /// <inheritdoc cref="UpdateInPlace(CAEXDocument,string,InstanceHierarchyType,MapperOptions)"/>
    public static ConversionResult<CAEXDocument> UpdateInPlace(
        CAEXDocument existing,
        FpbProject project,
        List<ProcessEntry> entries,
        InstanceHierarchyType? targetIh,
        MapperOptions? options = null)
    {
        var warnings = new List<string>();
        MapperTrace.Info(options, $"UpdateInPlace ENTRY: project='{project.Name}' entries={entries.Count}");

        // Eigene Erweiterung: snapshot statistics. Tells you at a glance whether
        // the incoming snapshot is what you'd expect (5 states + 2 POs + 3 flows
        // etc.). Catches "FPB.JS sent us garbage" vs "Mapper dropped things".
        if (options?.Trace != null)
        {
            var totalElements = entries.Sum(e => e.ElementData.Count);
            var byType = entries
                .SelectMany(e => e.ElementData)
                .GroupBy(d => d.Type)
                .OrderBy(g => g.Key)
                .ToDictionary(g => g.Key, g => g.Count());
            var perEntry = string.Join("; ",
                entries.Select((e, i) => $"#{i}:{e.Process.Id.Substring(0, Math.Min(8, e.Process.Id.Length))} " +
                                          $"(sub={!string.IsNullOrEmpty(e.Process.IsDecomposedProcessOperator)}, data={e.ElementData.Count}, visual={e.ElementVisual.Count})"));
            var perType = string.Join(", ", byType.Select(kv => $"{kv.Key}={kv.Value}"));
            MapperTrace.Info(options, $"  snapshot stats: totalElements={totalElements} byType=[{perType}] entries=[{perEntry}]");
        }

        Validate(project, entries, warnings);

        var caex = existing.CAEXFile;

        // Make sure FPD libraries are available (idempotent — no-op if already present).
        FpdLibraries.EnsureLibraries(caex);

        var fpdIH = targetIh ?? FindFpdInstanceHierarchy(caex);
        if (fpdIH is null)
        {
            warnings.Add("No existing FPD InstanceHierarchy found — appending a fresh hierarchy instead.");
            MapperTrace.Info(options, "No FPD IH found — falling back to AppendInto");
            AppendInto(existing, project, entries, options);
            return new ConversionResult<CAEXDocument>(existing, warnings);
        }

        var elementIndex = BuildElementIndex(fpdIH, options, warnings);
        var linkIndex = BuildInternalLinkIndex(fpdIH);
        MapperTrace.Info(options, $"Target IH='{fpdIH.Name}' elementIndex.size={elementIndex.Count} linkIndex.size={linkIndex.Count}");

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

        // TWO-PASS loop.
        //   Pass 1: resolve/add Process IEs + add/update Element IEs (NO connections).
        //   Pass 2: process connections (now every source/target is guaranteed in elementIndex).
        // Previously the order inside entry.ElementData was load-bearing — a flow listed
        // before its source/target element silently failed with "not found in AML".
        // Same shape for cross-entry decomposition: if a sub-process entry appears
        // before the parent entry that defines its parent PO, AddProcess used to fail.
        var procByEntryIndex = new Dictionary<int, InternalElementType?>();
        var visualByEntryIndex = new Dictionary<int, Dictionary<string, VisualInfo>>();

        // ── Pass 1: Processes + Elements ─────────────────────────────────
        // First sub-pass: ensure all top-level entries are resolved BEFORE we
        // try to add sub-process entries (the latter need their parent PO).
        var orderedEntries = entries
            .Select((e, i) => (entry: e, index: i))
            .OrderBy(t => string.IsNullOrEmpty(t.entry.Process.IsDecomposedProcessOperator) ? 0 : 1)
            .ToList();

        foreach (var (entry, entryIndex) in orderedEntries)
        {
            var visualMap = entry.ElementVisual
                .GroupBy(v => v.Id)
                .ToDictionary(g => g.Key, g => g.First());
            visualByEntryIndex[entryIndex] = visualMap;

            MapperTrace.Attempt(options, "ResolveProcess",
                $"entry.process.id='{entry.Process.Id}' isDecomposedOf='{entry.Process.IsDecomposedProcessOperator}' elementData.count={entry.ElementData.Count}");
            InternalElementType? procAml = ResolveProcessForEntry(fpdIH, entry, elementIndex, options);
            if (procAml is null && !string.IsNullOrEmpty(entry.Process.IsDecomposedProcessOperator))
            {
                MapperTrace.Result(options, "ResolveProcess",
                    $"no existing AML process for sub-process entry, attempting AddProcess(parentPo='{entry.Process.IsDecomposedProcessOperator}')");
                var warningsBefore = warnings.Count;
                procAml = AddProcess(fpdIH, entry.Process, elementIndex, sucLookup, warnings, options);
                if (procAml is not null)
                {
                    processAddedCount++;
                    elementIndex[StripBraces(procAml.ID)] = procAml;
                    MapperTrace.Result(options, "AddProcess", $"created sub-process id='{procAml.ID}' for parent PO '{entry.Process.IsDecomposedProcessOperator}'");
                }
                else
                {
                    var lastWarning = warnings.Count > warningsBefore ? warnings[^1] : "(no warning recorded)";
                    MapperTrace.Result(options, "AddProcess", $"FAILED — {lastWarning}");
                }
            }
            else
            {
                MapperTrace.Result(options, "ResolveProcess", procAml is null ? "NULL — entry will have unmatched elements" : $"found AML process '{procAml.Name}' id='{procAml.ID}'");
            }
            procByEntryIndex[entryIndex] = procAml;

            // Add/Update ELEMENTS only (skip connections in Pass 1).
            foreach (var data in entry.ElementData)
            {
                if (ConnectionTypes.Contains(data.Type)) continue;

                if (elementIndex.TryGetValue(data.Id, out var ie))
                {
                    MapperTrace.Attempt(options, "UpdateElement", $"id='{data.Id}' type='{data.Type}' name='{data.Name}'");
                    UpdateExistingElement(ie, data, visualMap, elementIndex, options);
                    updatedCount++;
                    MapperTrace.Result(options, "UpdateElement", "ok");
                    continue;
                }

                if (procAml is null)
                {
                    unmatchedSkippedCount++;
                    MapperTrace.Result(options, "ResolveElement", $"SKIP — element id='{data.Id}' type='{data.Type}' has no AML match and no host process to add it under");
                    continue;
                }

                MapperTrace.Attempt(options, "AddElement", $"id='{data.Id}' type='{data.Type}' name='{data.Name}' into process='{procAml.Name}'");
                var newIe = AddElement(procAml, data, sucLookup, visualMap, options);
                if (newIe is not null)
                {
                    addedCount++;
                    elementIndex[StripBraces(newIe.ID)] = newIe;
                    MapperTrace.Result(options, "AddElement", $"ok — registered in elementIndex as '{StripBraces(newIe.ID)}'");
                }
                else
                {
                    warnings.Add($"Could not add element '{data.Id}' of type '{data.Type}' " +
                                 $"(SUC '{data.Type}' missing in library?)");
                    unmatchedSkippedCount++;
                    MapperTrace.Result(options, "AddElement", $"FAILED — SUC '{data.Type}' missing");
                }
            }
        }

        // ── Pass 2: Connections ─────────────────────────────────────────
        // elementIndex is now fully populated with every Process + Element IE
        // that this UpdateInPlace will materialize. Connection lookups by
        // source/target can succeed regardless of where they appear in the
        // entry.ElementData ordering — fixes the "source not in AML" warning
        // storm we saw on Decompose round-trips.
        // Per-element interface counters reused across the whole pass so
        // multiple parallel flows from the same element don't collide on
        // ExternalInterface names.
        var ifaceCounters = new Dictionary<string, Dictionary<string, int>>();
        foreach (var (entry, entryIndex) in orderedEntries)
        {
            var procAml = procByEntryIndex.GetValueOrDefault(entryIndex);
            var visualMap = visualByEntryIndex[entryIndex];

            foreach (var data in entry.ElementData)
            {
                if (!ConnectionTypes.Contains(data.Type)) continue;

                if (linkIndex.TryGetValue(data.Id, out var link))
                {
                    if (LinkEndpointsMatch(link, data, elementIndex))
                    {
                        MapperTrace.Attempt(options, "UpdateConnection", $"flow id='{data.Id}' type='{data.Type}' (endpoints unchanged → waypoint refresh)");
                        UpdateExistingConnection(link, data, visualMap);
                        connectionUpdatedCount++;
                        MapperTrace.Result(options, "UpdateConnection", "ok");
                    }
                    else
                    {
                        MapperTrace.Attempt(options, "RecreateConnection", $"flow id='{data.Id}' endpoints swapped → drop+readd");
                        link.Remove();
                        linkIndex.Remove(StripBraces(data.Id));
                        if (procAml is not null
                            && AddConnection(procAml, data, elementIndex, visualMap, warnings, ifaceCounters) is not null)
                        {
                            connectionAddedCount++;
                            MapperTrace.Result(options, "RecreateConnection", "ok");
                        }
                        else
                        {
                            warnings.Add($"Flow '{data.Id}' endpoints changed but could not be recreated.");
                            MapperTrace.Result(options, "RecreateConnection", "FAILED");
                        }
                    }
                }
                else if (procAml is not null)
                {
                    MapperTrace.Attempt(options, "AddConnection", $"flow id='{data.Id}' type='{data.Type}' source='{data.SourceRef}' target='{data.TargetRef}'");
                    var warningsBefore = warnings.Count;
                    if (AddConnection(procAml, data, elementIndex, visualMap, warnings, ifaceCounters) is not null)
                    {
                        connectionAddedCount++;
                        MapperTrace.Result(options, "AddConnection", "ok");
                    }
                    else
                    {
                        var lastWarning = warnings.Count > warningsBefore ? warnings[^1] : "(no warning recorded)";
                        MapperTrace.Result(options, "AddConnection", $"FAILED — {lastWarning}");
                    }
                }
                else
                {
                    warnings.Add($"Flow '{data.Id}' could not be added: no host process resolved.");
                    MapperTrace.Result(options, "AddConnection", $"SKIP — no host process for flow id='{data.Id}'");
                }
            }
        }

        MapperTrace.Attempt(options, "RemoveOrphans", "FPD elements not in incoming snapshot");
        var removedCount = RemoveOrphanedFpdElements(fpdIH, elementIndex, incomingIds, options);
        MapperTrace.Result(options, "RemoveOrphans", $"removed {removedCount} element(s)");

        MapperTrace.Attempt(options, "RemoveOrphans", "InternalLinks not in incoming snapshot");
        var connectionsRemoved = RemoveOrphanedConnections(linkIndex, incomingIds);
        MapperTrace.Result(options, "RemoveOrphans", $"removed {connectionsRemoved} link(s)");

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
        MapperTrace.Attempt(options, "RemoveOrphanSubProcesses", $"sub-processes whose parent PO no longer carries decomposedView ({incomingDecomposedParentPoIds.Count} kept-alive parent POs)");
        var processesRemoved = RemoveOrphanedSubProcesses(fpdIH, incomingDecomposedParentPoIds, elementIndex, options);
        MapperTrace.Result(options, "RemoveOrphanSubProcesses", $"removed {processesRemoved} sub-process(es)");

        // Audit-fix #2+#3 — keep boundary-state refObj in sync across the
        // decomposition. UpdateExistingElement / AddElement don't touch refObj
        // on states (they don't have parent-entry context), so after the main
        // loop we re-derive every sub-process state's refObj from the snapshot:
        // a state is a boundary state iff the same state-ID also appears in
        // the parent entry's ElementData. Mirrors the green-field Convert
        // logic at lines 1186-1198.
        MapperTrace.Attempt(options, "SyncBoundaryStateRefObjs", "post-pass re-deriving refObj on every sub-process state from the snapshot");
        var boundarySyncs = SyncBoundaryStateRefObjs(entries, elementIndex, options);
        MapperTrace.Result(options, "SyncBoundaryStateRefObjs", $"synced refObj on {boundarySyncs} state(s) across decomposition");

        // Post-pass — keep PO ⇄ child Process names in sync. Runs after every
        // other update so the loop order can't undo the sync.
        MapperTrace.Attempt(options, "SyncDecompositionNames", "post-pass syncing PO name -> child Process name");
        var nameSyncs = SyncDecompositionNamesPostPass(elementIndex, warnings, options);
        MapperTrace.Result(options, "SyncDecompositionNames", $"synced {nameSyncs} PO ⇄ sub-process name pair(s)");

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
        Dictionary<string, VisualInfo> visualMap,
        MapperOptions? options = null)
    {
        if (!ElementToSuc.TryGetValue(data.Type, out var sucPath)) return null;
        var sucName = sucPath.Split('/')[1];
        if (!sucLookup.ContainsKey(sucName)) return null;

        var ie = CreateInstance(sucLookup, sucName);
        ie.ID = WrapBraces(data.Id);
        // Use the same fallback as BuildProcess so a missing FPB.JS name
        // yields "ProcessOperator" / "Product" / etc. consistently across
        // green-field Convert and UpdateInPlace+AddElement code paths.
        var typeLocalName = data.Type.Contains(':') ? data.Type.Split(':')[1] : sucName;
        ie.Name = !string.IsNullOrEmpty(data.Name) ? data.Name : typeLocalName;
        parent.Insert(ie);

        SetIdentification(ie, data.Identification, data.Name ?? "", options);
        if (visualMap.TryGetValue(data.Id, out var visual))
        {
            SetViewInformation(ie, visual, options);
        }
        else if (data.Type == FpbTypes.SystemLimit)
        {
            // A freshly-added SystemLimit without visual data would render
            // as a 0×0 invisible box in FPB.JS. Apply sensible defaults so
            // the user gets a usable canvas.
            SetViewInformation(ie, new VisualInfo { X = 100, Y = 100, Width = 600, Height = 400 }, options);
            MapperTrace.Info(options, $"AddElement: SystemLimit '{ie.ID}' had no visual data in snapshot — applied default ViewInformation (100,100,600,400)");
        }

        if (data.Characteristics != null && data.Characteristics.Count > 0)
            SetCharacteristics(ie, data.Characteristics, options);

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
        HashSet<string> incomingIds,
        MapperOptions? options = null)
    {
        var fpdSucPaths = new HashSet<string>(ElementToSuc.Values);
        var processSuc = ElementToSuc[FpbTypes.Process];

        var toRemove = elementIndex
            .Where(kv => !incomingIds.Contains(kv.Key)
                         && kv.Value.RefBaseSystemUnitPath is { } suc
                         && fpdSucPaths.Contains(suc)
                         && suc != processSuc)
            .Select(kv => (Key: kv.Key, Ie: kv.Value))
            .ToList();

        foreach (var (key, ie) in toRemove)
        {
            MapperTrace.Attempt(options, "RemoveOrphanElement",
                $"id='{ie.ID}' name='{ie.Name}' type='{ie.RefBaseSystemUnitPath}' (no longer in incoming snapshot)");
            ie.Remove();
            // Audit-fix #1: keep elementIndex consistent with the AML tree.
            elementIndex.Remove(key);
            MapperTrace.Result(options, "RemoveOrphanElement", "removed + dropped from elementIndex");
        }

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
        Dictionary<string, InternalElementType> elementIndex,
        MapperOptions? options = null)
    {
        var processSuc = ElementToSuc[FpbTypes.Process];

        // elementIndex is keyed by bare IDs (StripBraces). If the snapshot
        // supplies a braced ID (legacy data, interop, manual edit), the raw
        // lookup misses and we silently fall through to "no match". Mirror
        // the defensive raw-then-bare pattern AddProcess already uses.
        bool TryLookup(string id, out InternalElementType hit)
        {
            if (elementIndex.TryGetValue(id, out hit!)) return true;
            var bare = StripBraces(id);
            return !string.IsNullOrEmpty(bare) && elementIndex.TryGetValue(bare, out hit!);
        }

        // Top-level case: entry.Process.Id is directly the process AML ID.
        if (string.IsNullOrEmpty(entry.Process.IsDecomposedProcessOperator)
            && TryLookup(entry.Process.Id, out var directHit)
            && directHit.RefBaseSystemUnitPath == processSuc)
        {
            MapperTrace.Info(options, $"  ResolveProcess: matched TOP-LEVEL by ID — entry.process.id='{entry.Process.Id}' -> AML '{directHit.ID}' name='{directHit.Name}'");
            return directHit;
        }

        // Sub-process case: find via refObj on a Process IE pointing back at the parent PO.
        var parentPoId = string.IsNullOrEmpty(entry.Process.IsDecomposedProcessOperator)
            ? entry.Process.Id
            : entry.Process.IsDecomposedProcessOperator;
        if (!TryLookup(parentPoId, out var parentPo))
        {
            MapperTrace.Info(options, $"  ResolveProcess: parent PO '{parentPoId}' not in elementIndex — no match");
            return null;
        }
        var parentPoAmlId = parentPo.ID;
        var subHit = fpdIH.InternalElement.FirstOrDefault(ie =>
            ie.RefBaseSystemUnitPath == processSuc
            && (ie.GetRefObjOrDerived() ?? "") == parentPoAmlId);
        if (subHit != null)
            MapperTrace.Info(options, $"  ResolveProcess: matched SUB-PROCESS via refObj — parent PO '{parentPo.ID}' name='{parentPo.Name}' -> sub-process '{subHit.ID}' name='{subHit.Name}'");
        else
            MapperTrace.Info(options, $"  ResolveProcess: parent PO '{parentPo.ID}' has no sub-process with refObj pointing back yet — will trigger AddProcess");
        return subHit;
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
    /// <summary>
    /// Produce a unique ExternalInterface name per (element, baseName) across
    /// a single conversion pass. Mirrors BuildProcess's GetNextInterfaceName
    /// local helper so green-field Convert and incremental UpdateInPlace
    /// produce consistent shapes.
    /// </summary>
    private static string GetUniqueInterfaceName(
        Dictionary<string, Dictionary<string, int>> counters,
        string elementId,
        string baseName)
    {
        if (!counters.TryGetValue(elementId, out var byBase))
        {
            byBase = new Dictionary<string, int>();
            counters[elementId] = byBase;
        }
        byBase.TryGetValue(baseName, out var n);
        n++;
        byBase[baseName] = n;
        return n == 1 ? baseName : $"{baseName}_{n}";
    }

    private static bool IsDescendantOf(CAEXBasicObject? node, InternalElementType ancestor)
    {
        for (var n = node?.CAEXParent; n != null; n = n.CAEXParent)
            if (ReferenceEquals(n, ancestor)) return true;
        return false;
    }

    /// <summary>
    /// Diagnostic helper: walk up from an IE until we hit a FPD_Process ancestor
    /// (or root) so cross-process-flow warnings can name which process the
    /// endpoint actually lives in.
    /// </summary>
    private static string FindHostProcessName(InternalElementType? ie)
    {
        if (ie == null) return "<null>";
        var processSuc = ElementToSuc[FpbTypes.Process];
        for (CAEXWrapper? n = ie; n != null; n = n.CAEXParent)
        {
            if (n is InternalElementType candidate && candidate.RefBaseSystemUnitPath == processSuc)
                return $"{candidate.Name} (id={candidate.ID})";
        }
        return "<no FPD_Process ancestor>";
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
        // Treat empty endpoints as a MISMATCH — same semantics as AddConnection
        // (which rejects them) and Validate (which only checks non-empty).
        // Returning true here would keep stale links alive even when the
        // snapshot has explicitly cleared the refs.
        if (string.IsNullOrEmpty(flowData.SourceRef) || string.IsNullOrEmpty(flowData.TargetRef))
            return false;
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
        List<string> warnings,
        Dictionary<string, Dictionary<string, int>>? ifaceCounters = null)
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

        // Cross-process defence: warn when EITHER endpoint is outside procAml.
        // A flow whose source is in procAml but target is in a different
        // process tree is wrong; an ID collision can resolve to the wrong copy.
        if (!IsDescendantOf(sourceIE, procAml) || !IsDescendantOf(targetIE, procAml))
        {
            var sourceHost = FindHostProcessName(sourceIE);
            var targetHost = FindHostProcessName(targetIE);
            warnings.Add($"Flow '{flowData.Id}': not all endpoints live under process " +
                         $"'{procAml.Name}' — source lives in '{sourceHost}', target lives in '{targetHost}' — link may target the wrong layer.");
        }

        // ExternalInterface name uniqueness across the whole pass. Two
        // parallel flows from the same element would otherwise both get e.g.
        // "FPD_FlowOut" — AML allows it but the round-trip reader can't tell
        // them apart. Suffix with _2, _3, … matching BuildProcess's
        // GetNextInterfaceName scheme.
        ifaceCounters ??= new Dictionary<string, Dictionary<string, int>>();
        var outBaseName = ifacePaths.Out.Split('/')[1];
        var sourceIface = sourceIE.ExternalInterface.Append(
            GetUniqueInterfaceName(ifaceCounters, flowData.SourceRef, outBaseName));
        sourceIface.ID = NewId();
        sourceIface.RefBaseClassPath = ifacePaths.Out;

        var inBaseName = ifacePaths.In.Split('/')[1];
        var targetIface = targetIE.ExternalInterface.Append(
            GetUniqueInterfaceName(ifaceCounters, flowData.TargetRef, inBaseName));
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
        var linkName = flowData.Type == FpbTypes.Usage
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
        List<string> warnings,
        MapperOptions? options = null)
    {
        if (string.IsNullOrEmpty(incomingProcess.IsDecomposedProcessOperator))
        {
            warnings.Add($"Cannot add top-level process '{incomingProcess.Id}' via UpdateInPlace " +
                         "— only decomposed sub-processes can be created from FPB.JS edits.");
            return null;
        }
        // Diagnostic: dump the index keys + the looked-up key so we can see if
        // the snapshot ID matches what BuildElementIndex actually produced.
        var rawKey = incomingProcess.IsDecomposedProcessOperator;
        var bareKey = StripBraces(rawKey);
        if (!elementIndex.TryGetValue(rawKey, out var parentPo)
            && !elementIndex.TryGetValue(bareKey, out parentPo))
        {
            var sampleKeys = string.Join(", ",
                elementIndex.Keys.Take(8).Select(k => $"'{k}'"));
            warnings.Add($"Cannot decompose: parent PO raw='{rawKey}' bare='{bareKey}' " +
                         $"not found in elementIndex (size={elementIndex.Count}). " +
                         $"Sample keys: [{sampleKeys}{(elementIndex.Count > 8 ? ", …" : "")}].");
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
        Dictionary<string, InternalElementType> elementIndex,
        MapperOptions? options = null)
    {
        var processSuc = ElementToSuc[FpbTypes.Process];
        var subProcesses = fpdIH.InternalElement
            .Where(ie => ie.RefBaseSystemUnitPath == processSuc
                         && !string.IsNullOrEmpty(ie.GetRefObjOrDerived()))
            .ToList();

        var removed = 0;
        foreach (var sub in subProcesses)
        {
            var parentRef = sub.GetRefObjOrDerived() ?? "";
            var parentBare = StripBraces(parentRef);
            if (incomingDecomposedParentPoIds.Contains(parentBare)) continue;

            MapperTrace.Attempt(options, "RemoveSubProcess",
                $"sub-process id='{sub.ID}' name='{sub.Name}' parentPO bare='{parentBare}' (parent no longer carries decomposedView)");

            // Clear the back-link on the parent PO. Audit-fix #1: previously
            // SetAttrValue ignored empty strings, so the parent PO kept a stale
            // refProcess pointing at a detached IE — SyncDecompositionNamesPostPass
            // then operated on the orphan.
            if (elementIndex.TryGetValue(parentBare, out var parentPo))
                SetAttrValue(parentPo, "refProcess", "");

            // Recurse: remove every descendant IE from the elementIndex too,
            // otherwise SyncDecompositionNamesPostPass (and anything else
            // reading elementIndex post-removal) sees detached elements.
            PurgeSubtreeFromIndex(sub, elementIndex);
            sub.Remove();
            removed++;
            MapperTrace.Result(options, "RemoveSubProcess", $"removed (plus purged subtree from elementIndex)");
        }
        return removed;
    }

    /// <summary>
    /// Audit-fix #1: keep the elementIndex consistent with the AML tree after
    /// any IE removal. Without this, later passes (SyncDecompositionNamesPostPass,
    /// validators, debug dumps) operate on detached IEs whose CAEXParent is
    /// null and whose mutations have no effect on the saved file.
    /// </summary>
    private static void PurgeSubtreeFromIndex(
        InternalElementType subtreeRoot,
        Dictionary<string, InternalElementType> elementIndex)
    {
        var rootId = StripBraces(subtreeRoot.ID);
        if (!string.IsNullOrEmpty(rootId)) elementIndex.Remove(rootId);
        WalkInternalElements(subtreeRoot.InternalElement, child =>
        {
            var id = StripBraces(child.ID);
            if (!string.IsNullOrEmpty(id)) elementIndex.Remove(id);
        });
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
    /// Update FPD-managed properties on an existing InternalElement, leaving
    /// custom user-added Attributes / ExternalInterfaces / MappingObjects in
    /// place. Covers Name, Identification, ViewInformation, Characteristics
    /// (idempotent), and the Process⇄ProcessOperator name-sync for decomposed
    /// pairs.
    /// </summary>
    private static void UpdateExistingElement(
        InternalElementType ie,
        ElementData data,
        Dictionary<string, VisualInfo> visualMap,
        Dictionary<string, InternalElementType> elementIndex,
        MapperOptions? options = null)
    {
        // Audit-add: log name diffs so a "why did the name change" question
        // can be answered straight from the log without diffing the AML.
        if (!string.IsNullOrEmpty(data.Name) && !string.Equals(ie.Name, data.Name, StringComparison.Ordinal))
        {
            MapperTrace.Info(options, $"UpdateExistingElement: ie='{ie.ID}' name '{ie.Name}' -> '{data.Name}'");
            ie.Name = data.Name;
        }

        SetIdentification(ie, data.Identification, data.Name, options);

        if (visualMap.TryGetValue(data.Id, out var visual))
            SetViewInformation(ie, visual, options);

        // Characteristics — SetCharacteristics is now idempotent (it strips
        // existing Characteristic_N entries before re-writing), so changes the
        // user made in the FPB.js viewer round-trip back through the AML.
        if (data.Characteristics != null)
            SetCharacteristics(ie, data.Characteristics, options);
    }

    /// <summary>
    /// Post-pass that propagates ProcessOperator names to their decomposed
    /// child Process IEs. Runs AFTER the main update loop so that
    /// UpdateExistingElement's own data.Name write on the sub-process IE
    /// cannot overwrite the sync (the loop order can hit child before parent
    /// or vice versa). PO → child direction only; the reverse path is rare in
    /// real workflows because renames typically happen on the primary
    /// (PO-side) view.
    /// </summary>
    private static int SyncDecompositionNamesPostPass(Dictionary<string, InternalElementType> elementIndex, List<string>? warnings = null, MapperOptions? options = null)
    {
        var poSuc = ElementToSuc[FpbTypes.ProcessOperator];
        var syncs = 0;

        foreach (var ie in elementIndex.Values)
        {
            if (ie.RefBaseSystemUnitPath != poSuc) continue;
            if (string.IsNullOrEmpty(ie.Name)) continue;

            var refProcess = ie.Attribute["refProcess"]?.Value;
            if (string.IsNullOrEmpty(refProcess)) continue;

            var key = StripBraces(refProcess);
            if (!elementIndex.TryGetValue(key, out var child))
            {
                MapperTrace.Info(options,
                    $"SyncDecompositionNames: PO id='{ie.ID}' name='{ie.Name}' refProcess='{refProcess}' — child not in elementIndex (likely orphaned)");
                continue;
            }
            // Audit-fix: don't mutate a detached IE — the actual mutation has
            // no effect on the saved file and produces a misleading trace.
            if (child.CAEXParent == null)
            {
                MapperTrace.Info(options,
                    $"SyncDecompositionNames: child id='{child.ID}' is detached (CAEXParent=null) — skipping name sync");
                continue;
            }
            if (!string.Equals(child.Name, ie.Name, StringComparison.Ordinal))
            {
                MapperTrace.Info(options,
                    $"SyncDecompositionNames: PO id='{ie.ID}' name '{child.Name}' -> '{ie.Name}'");
                child.Name = ie.Name;
                syncs++;
            }
        }
        return syncs;
    }

    /// <summary>
    /// Audit-fix #2 + #3: re-derive <c>refObj</c> on every sub-process state
    /// from the incoming snapshot. <see cref="UpdateExistingElement"/> and
    /// <see cref="AddElement"/> are oblivious to whether their target IE is
    /// a state inside a sub-process; they update Name/Identification/Visuals
    /// only. Without this post-pass, decomposed-state refObj drifts silently
    /// when the FPB.JS user changes which states are boundary states.
    ///
    /// Convention (mirrors green-field <see cref="BuildProcess"/> at lines
    /// 1186-1198): a state ID that appears in BOTH the parent entry's
    /// elementDataInformation AND the sub-process entry's elementDataInformation
    /// is a boundary state and carries refObj = the normalised state ID.
    /// Pure-child states (only in the sub-process) get refObj = "".
    /// </summary>
    private static int SyncBoundaryStateRefObjs(
        List<ProcessEntry> entries,
        Dictionary<string, InternalElementType> elementIndex,
        MapperOptions? options = null)
    {
        // Every entry (top-level OR sub-process) can be a parent for a deeper
        // sub-process — grandchild decompositions need to find their parent
        // which is itself a sub-process. Filtering to top-level entries only
        // would silently drop multi-level state syncs.
        var parentEntries = entries.ToList();

        int updated = 0;
        foreach (var entry in entries)
        {
            if (string.IsNullOrEmpty(entry.Process.IsDecomposedProcessOperator)) continue;
            var parentPoBare = StripBraces(entry.Process.IsDecomposedProcessOperator);

            // Find any entry whose elementData contains this parent PO.
            var parentEntry = parentEntries.FirstOrDefault(p =>
                p.ElementData.Any(d => StripBraces(d.Id) == parentPoBare));
            if (parentEntry is null)
            {
                MapperTrace.Info(options,
                    $"SyncBoundaryStateRefObjs: sub-process entry parent='{parentPoBare}' has no matching parent entry — leaving refObjs untouched");
                continue;
            }

            var parentStateIds = new HashSet<string>(
                parentEntry.ElementData
                    .Where(d => StateTypes.Contains(d.Type))
                    .Select(d => StripBraces(d.Id)),
                StringComparer.OrdinalIgnoreCase);

            foreach (var data in entry.ElementData)
            {
                if (!StateTypes.Contains(data.Type)) continue;
                var stateBare = StripBraces(data.Id);
                if (!elementIndex.TryGetValue(stateBare, out var stateIe)) continue;
                if (stateIe.CAEXParent == null) continue;

                var expected = parentStateIds.Contains(stateBare) ? NormalizeId(data.Id) : "";
                var current  = stateIe.Attribute["refObj"]?.Value ?? "";
                if (!string.Equals(current, expected, StringComparison.Ordinal))
                {
                    MapperTrace.Info(options,
                        $"SyncBoundaryStateRefObjs: state id='{stateIe.ID}' name='{stateIe.Name}' refObj '{current}' -> '{expected}' ({(expected == "" ? "no longer boundary" : "boundary state")})");
                    SetAttrValue(stateIe, "refObj", expected);
                    updated++;
                }
            }
        }
        return updated;
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
        var fpdProcessSuc = ElementToSuc[FpbTypes.Process];
        return caex.InstanceHierarchy.FirstOrDefault(ih =>
            ih.InternalElement.Any(ie => ie.RefBaseSystemUnitPath == fpdProcessSuc));
    }

    /// <summary>
    /// Walk the FPD IH and index every InternalElement by its bare AML ID (CAEX uses
    /// B-format "{xxx-yyy}"; the index uses the raw form without braces so it matches
    /// FPB.JS-side IDs produced by <c>CaexToFpbJson.NormalizeId</c>).
    /// </summary>
    private static Dictionary<string, InternalElementType> BuildElementIndex(
        InstanceHierarchyType fpdIH,
        MapperOptions? options = null,
        List<string>? warnings = null)
    {
        var index = new Dictionary<string, InternalElementType>(StringComparer.OrdinalIgnoreCase);
        WalkInternalElements(fpdIH.InternalElement, ie =>
        {
            var bare = StripBraces(ie.ID);
            if (string.IsNullOrEmpty(bare)) return;
            // Duplicate IDs in sibling sub-process branches would silently
            // overwrite an earlier IE in the flat index, then SyncBoundaryState
            // RefObjs / AddConnection / etc. would mutate the wrong element.
            // Surface the collision.
            if (index.TryGetValue(bare, out var existing))
            {
                var msg = $"BuildElementIndex: duplicate ID '{bare}' detected — existing parent='{existing.CAEXParent?.ToString() ?? "?"}' name='{existing.Name}', " +
                          $"new parent='{ie.CAEXParent?.ToString() ?? "?"}' name='{ie.Name}'. " +
                          $"Keeping the first; the second one cannot be addressed via elementIndex lookups.";
                warnings?.Add(msg);
                MapperTrace.Info(options, msg);
                return; // do NOT overwrite — keep the first occurrence stable.
            }
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
    private static List<string> AppendInto(CAEXDocument doc, FpbProject project, List<ProcessEntry> entries, MapperOptions? options = null)
    {
        var warnings = new List<string>();
        MapperTrace.Info(options, $"AppendInto ENTRY: project='{project.Name}' entries={entries.Count}");
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
                if (obj.Type == FpbTypes.ProcessOperator && !string.IsNullOrEmpty(obj.DecomposedView))
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
            BuildProcess(ih, pid, processMap, processAmlIds, poToChildProcess, usedAmlIds, sucLookup, warnings, options);
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
            if (obj.Type == FpbTypes.ProcessOperator && !string.IsNullOrEmpty(obj.DecomposedView))
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
        List<string> warnings,
        MapperOptions? options = null)
    {
        if (!processMap.TryGetValue(processId, out var entry)) return;

        var process = entry.Process;
        var visualMap = entry.ElementVisual.ToDictionary(v => v.Id, v => v);
        var dataMap = entry.ElementData.ToDictionary(d => d.Id, d => d);

        // Determine process name
        var slData = entry.ElementData.FirstOrDefault(e => e.Type == FpbTypes.SystemLimit);
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
            SetIdentification(slIE, slData.Identification, processName, options);
            if (visualMap.TryGetValue(slData.Id, out var slVisual))
            {
                SetViewInformation(slIE, slVisual, options);
            }
            else
            {
                // Defaults for the SystemLimit when the snapshot lacks visual
                // data — otherwise it renders as a 0×0 invisible box in FPB.JS.
                SetViewInformation(slIE, new VisualInfo { X = 100, Y = 100, Width = 600, Height = 400 }, options);
                MapperTrace.Info(options, $"BuildProcess: SystemLimit '{slIE.ID}' had no visual data — applied default ViewInformation (100,100,600,400)");
            }
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
                    e.Type == FpbTypes.ProcessOperator && e.DecomposedView == processId));
        }

        // Build object InternalElements
        foreach (var obj in objects)
        {
            if (obj.Type == FpbTypes.SystemLimit) continue;
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
            SetIdentification(ie, obj.Identification, elemName, options);
            SetCharacteristics(ie, obj.Characteristics, options);

            // refProcess (on ProcessOperator only)
            if (obj.Type == FpbTypes.ProcessOperator)
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
                SetViewInformation(ie, visual, options);

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

            var linkName = flow?.Type == FpbTypes.Usage
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

    /// <summary>
    /// Set or CLEAR an attribute value on an IE. Audit-fix: the previous
    /// implementation silently ignored empty values, which meant
    /// <c>SetAttrValue(po, "refProcess", "")</c> didn't actually clear the
    /// reference after a sub-process was removed — the parent PO kept pointing
    /// at a detached IE and SyncDecompositionNamesPostPass crashed/no-op'd on
    /// the dangling pointer. Now empty values write an empty string (which is
    /// what every consumer reads as "cleared").
    /// </summary>
    private static void SetAttrValue(InternalElementType ie, string name, string value)
    {
        var attr = ie.Attribute[name];
        if (attr != null)
        {
            attr.Value = value ?? string.Empty;
        }
        else
        {
            // Fallback: create if missing (shouldn't happen with proper SUC).
            // Mirror the main-path semantics — empty becomes "" not null, so
            // consumers see a consistent state regardless of whether the
            // attribute was pre-existing or freshly created.
            var newAttr = ie.Attribute.Append(name);
            newAttr.AttributeDataType = "xs:string";
            newAttr.Value = value ?? string.Empty;
        }
    }

    private static void SetIdentification(InternalElementType ie, Identification? ident, string fallbackName, MapperOptions? options = null)
    {
        var attr = ie.Attribute[IdentificationSchema.AttributeName];
        if (attr == null)
        {
            MapperTrace.Info(options, $"SetIdentification: ie='{ie.ID}' has no Identification container (SUC malformed?) — skipped");
            return;
        }

        SetSubAttr(attr, IdentificationSchema.UniqueIdent,    ident?.UniqueIdent);
        SetSubAttr(attr, IdentificationSchema.LongName,       ident?.LongName);
        SetSubAttr(attr, IdentificationSchema.ShortName,      ident?.ShortName ?? fallbackName);
        SetSubAttr(attr, IdentificationSchema.VersionNumber,  ident?.VersionNumber);
        SetSubAttr(attr, IdentificationSchema.RevisionNumber, ident?.RevisionNumber);
        MapperTrace.Info(options, $"SetIdentification: ie='{ie.ID}' shortName='{ident?.ShortName ?? fallbackName}'");
    }

    /// <summary>
    /// Idempotent replacement of the Characteristics container on the IE.
    /// Removes any existing Characteristic_N sub-attributes first, then writes
    /// the incoming list. Safe to call on an already-populated IE — that's the
    /// path UpdateExistingElement uses for property edits round-tripping back
    /// from the viewer.
    /// </summary>
    private static void SetCharacteristics(InternalElementType ie, List<Characteristic> characteristics, MapperOptions? options = null)
    {
        var container = ie.Attribute["Characteristics"];
        var removedExisting = 0;

        // Wipe existing Characteristic_N entries before re-adding so the same
        // IE doesn't end up with two parallel copies of the user's edits.
        if (container != null)
        {
            var existing = container.Attribute
                .Where(a => a.Name != null && a.Name.StartsWith("Characteristic_", StringComparison.Ordinal))
                .ToList();
            foreach (var old in existing) old.Remove();
            removedExisting = existing.Count;
        }

        if (characteristics.Count == 0)
        {
            MapperTrace.Info(options, $"SetCharacteristics: ie='{ie.ID}' wiped {removedExisting} existing, no new ones (user cleared)");
            return;
        }

        if (container == null)
        {
            container = ie.Attribute.Append("Characteristics");
            container.AttributeDataType = "xs:string";
        }
        MapperTrace.Info(options, $"SetCharacteristics: ie='{ie.ID}' removed={removedExisting}, adding={characteristics.Count}");

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
            foreach (var f in IdentificationSchema.Fields)
            {
                var val = f switch
                {
                    IdentificationSchema.UniqueIdent    => cat?.UniqueIdent ?? "",
                    IdentificationSchema.LongName       => cat?.LongName ?? "",
                    IdentificationSchema.ShortName      => cat?.ShortName ?? "",
                    IdentificationSchema.VersionNumber  => cat?.VersionNumber ?? "",
                    IdentificationSchema.RevisionNumber => cat?.RevisionNumber ?? "",
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

    private static void SetViewInformation(InternalElementType ie, VisualInfo visual, MapperOptions? options = null)
    {
        var attr = ie.Attribute["ViewInformation"];
        if (attr == null)
        {
            MapperTrace.Info(options, $"SetViewInformation: ie='{ie.ID}' has no ViewInformation container — skipped");
            return;
        }

        var pos = attr.Attribute["position"];
        if (pos != null)
        {
            SetDoubleSubAttr(pos, "x", visual.X);
            SetDoubleSubAttr(pos, "y", visual.Y);
        }
        SetDoubleSubAttr(attr, "width", visual.Width);
        SetDoubleSubAttr(attr, "height", visual.Height);
        MapperTrace.Info(options, $"SetViewInformation: ie='{ie.ID}' x={visual.X} y={visual.Y} w={visual.Width} h={visual.Height}");
    }

    private static void SetSubAttr(AttributeType parent, string name, string? value)
    {
        // Empty/null writes used to be silently skipped, which meant the
        // user clearing Identification fields (longName, shortName,
        // versionNumber, revisionNumber) didn't actually clear anything — the
        // old value stuck around in AML. Write "" explicitly so the reader
        // sees the user-intended cleared state.
        var attr = parent.Attribute[name];
        if (attr != null)
            attr.Value = value ?? string.Empty;
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

        // NormalizeId in FpbJsonToCaex (wrap) and CaexToFpbJson
        // (strip) are inverse for GUIDs but not for custom strings. Warn early
        // if we see non-GUID IDs so a round-trip divergence has a paper trail.
        foreach (var entry in entries)
        {
            if (!System.Guid.TryParse(entry.Process.Id, out _))
                warnings.Add($"Non-GUID process ID '{entry.Process.Id}' — round-trip ID normalization may not be idempotent.");
            foreach (var d in entry.ElementData)
                if (!System.Guid.TryParse(d.Id, out _))
                    warnings.Add($"Non-GUID element ID '{d.Id}' (type='{d.Type}') — round-trip ID normalization may not be idempotent.");
        }

        foreach (var entry in entries)
        {
            var hasSL = entry.ElementData.Any(e => e.Type == FpbTypes.SystemLimit);
            if (!hasSL)
                warnings.Add($"Process '{entry.Process.Id}' has no SystemLimit.");

            var hasPO = entry.ElementData.Any(e => e.Type == FpbTypes.ProcessOperator);
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
