// FPB.JS JSON → AutomationML (CAEX 3.0) converter

const { v4: uuidv4 } = require('uuid');
const { create } = require('xmlbuilder2');
const {
  ELEMENT_TO_SUC, FLOW_TO_INTERFACE,
  OBJECT_TYPES, CONNECTION_TYPES, ATTR_REFS,
} = require('./mappings.js');
const { appendLibraries } = require('./aml-libraries.js');

/**
 * Convert FPB.JS JSON array to AML XML string.
 * @param {Array} jsonData - The FPB.JS JSON array (Project header + process entries)
 * @returns {string} CAEX 3.0 XML string
 */
function jsonToAml(jsonData) {
  // ── 1. Parse input ──────────────────────────────────────────────────
  const project = jsonData.find(e => e.$type === 'fpb:Project');
  const processEntries = jsonData.filter(e => e.process);

  // Build lookup: processId → entry
  const processMap = new Map();
  for (const entry of processEntries) {
    processMap.set(entry.process.id, entry);
  }

  const entryProcessId = project.entryPoint;

  // ── 2. Pre-assign AML IDs ───────────────────────────────────────────
  // Strategy: use FPB.JS IDs as AML IDs wherever possible.
  // New UUIDs only where collisions occur:
  //   - Processes (FPB.JS child process ID = PO ID → collision)
  //   - Boundary states in child processes (same FPB.JS ID as parent state)
  const processAmlIds = new Map(); // FPB.JS processId → AML ID
  const poToChildProcess = new Map(); // FPB.JS PO elementId → child processId

  const allProcessIds = collectProcessIds(entryProcessId, processMap);

  for (const pid of allProcessIds) {
    processAmlIds.set(pid, uuidv4()); // Processes always get new UUIDs
    const entry = processMap.get(pid);
    if (!entry) continue;
    for (const obj of entry.elementDataInformation) {
      if (obj.$type === 'fpb:ProcessOperator' && obj.decomposedView) {
        poToChildProcess.set(obj.id, obj.decomposedView);
      }
    }
  }

  // ── 3. Build AML document ──────────────────────────────────────────
  const doc = create({ version: '1.0', encoding: 'utf-8' });
  const caex = doc.ele('CAEXFile', {
    'xmlns': 'http://www.dke.de/CAEX',
    'xmlns:xsi': 'http://www.w3.org/2001/XMLSchema-instance',
    'xsi:schemaLocation': 'http://www.dke.de/CAEX CAEX_ClassModel_V.3.0.xsd',
    SchemaVersion: '3.0',
    FileName: 'fpb-export.aml',
  });

  caex.ele('SuperiorStandardVersion').txt('AutomationML 2.1').up();
  caex.ele('SourceDocumentInformation', {
    OriginName: 'fpb-aml-mapper',
    OriginID: 'fpb-aml-mapper-1.0',
    OriginVersion: '0.1.0',
    LastWritingDateTime: new Date().toISOString(),
  }).up();

  // ── 4. InstanceHierarchy ───────────────────────────────────────────
  const ihName = project.name || 'InstanceHierarchy';
  const ih = caex.ele('InstanceHierarchy', { Name: ihName, ID: uuidv4() })
    .ele('Version').txt('1.0.0').up();

  // Track which FPB.JS IDs have been emitted as AML IDs (shared across processes)
  const usedAmlIds = new Set();

  // Build each process as a peer InternalElement in the IH
  for (const pid of allProcessIds) {
    buildProcess(ih, pid, processMap, processAmlIds, poToChildProcess, usedAmlIds);
  }

  // ── 5. Append library definitions ──────────────────────────────────
  appendLibraries(caex);

  return doc.end({ prettyPrint: true, indent: '  ' });
}

/**
 * Collect all process IDs recursively (entry + decomposed children) in order.
 */
function collectProcessIds(processId, processMap) {
  const result = [processId];
  const entry = processMap.get(processId);
  if (!entry) return result;

  for (const obj of entry.elementDataInformation) {
    if (obj.$type === 'fpb:ProcessOperator' && obj.decomposedView) {
      const childProcessId = obj.decomposedView;
      if (processMap.has(childProcessId)) {
        result.push(...collectProcessIds(childProcessId, processMap));
      }
    }
  }
  return result;
}

// ══════════════════════════════════════════════════════════════════════════
// Process builder
// ══════════════════════════════════════════════════════════════════════════

function buildProcess(parent, processId, processMap, processAmlIds, poToChildProcess, usedAmlIds) {
  const entry = processMap.get(processId);
  if (!entry) return;

  const { process, elementDataInformation, elementVisualInformation } = entry;

  // Build visual lookup: id → visual info
  const visualMap = new Map();
  for (const vi of elementVisualInformation) {
    visualMap.set(vi.id, vi);
  }

  // Build data lookup: id → element data
  const dataMap = new Map();
  for (const di of elementDataInformation) {
    dataMap.set(di.id, di);
  }

  // Determine process name:
  // - Child processes: named after their parent PO
  // - Entry process: named after the SystemLimit
  const slData = elementDataInformation.find(e => e.$type === 'fpb:SystemLimit');
  const parentPOId = process.isDecomposedProcessOperator;
  let processName;
  if (parentPOId) {
    // Find the PO's name in the parent process
    for (const [, pe] of processMap) {
      const po = pe.elementDataInformation.find(e => e.id === parentPOId);
      if (po) { processName = po.name; break; }
    }
  }
  if (!processName) processName = slData?.name || 'Process';

  // Create the FPD_Process InternalElement with pre-assigned AML ID
  const processAmlId = processAmlIds.get(processId);
  const procIE = parent.ele('InternalElement', {
    Name: processName,
    ID: processAmlId,
    RefBaseSystemUnitPath: ELEMENT_TO_SUC['fpb:Process'],
  });

  // refObj: for child processes, points back to the parent PO's AML ID.
  // Since POs use their FPB.JS ID as AML ID, this is just the PO's FPB.JS ID.
  if (parentPOId) {
    addRefObjAttr(procIE, parentPOId);
  } else {
    addRefObjAttr(procIE, '');
  }

  // ── SystemLimit ────────────────────────────────────────────────────
  if (slData) {
    const slVisual = visualMap.get(slData.id);
    const slIE = procIE.ele('InternalElement', {
      Name: 'SystemLimit_' + (processName || 'Process').replace(/\s+/g, ''),
      ID: slData.id,
      RefBaseSystemUnitPath: ELEMENT_TO_SUC['fpb:SystemLimit'],
    });
    addIdentification(slIE, slData.identification, processName);
    if (slVisual) {
      addViewInformation(slIE, slVisual);
    }
    slIE.up();
  }

  // ── Collect interface IDs for InternalLinks ────────────────────────
  const linkMap = new Map();

  // Track interface name counters per element for numbering (_2, _3, ...)
  const ifaceCounters = new Map();

  function getNextInterfaceName(elementId, baseName) {
    if (!ifaceCounters.has(elementId)) {
      ifaceCounters.set(elementId, new Map());
    }
    const counters = ifaceCounters.get(elementId);
    const count = counters.get(baseName) || 0;
    counters.set(baseName, count + 1);
    return count === 0 ? baseName : `${baseName}_${count + 1}`;
  }

  // ── Object elements (States, POs, TRs) ────────────────────────────
  const flows = elementDataInformation.filter(e => CONNECTION_TYPES.has(e.$type));
  const objects = elementDataInformation.filter(e => OBJECT_TYPES.has(e.$type));

  // Group flows by source and target element
  const flowsBySource = new Map();
  const flowsByTarget = new Map();
  for (const flow of flows) {
    if (!flowsBySource.has(flow.sourceRef)) flowsBySource.set(flow.sourceRef, []);
    flowsBySource.get(flow.sourceRef).push(flow);
    if (!flowsByTarget.has(flow.targetRef)) flowsByTarget.set(flow.targetRef, []);
    flowsByTarget.get(flow.targetRef).push(flow);
  }

  // Determine if this is a child process (has boundary states with shared IDs)
  const isChildProcess = !!parentPOId;
  // For child processes, find the parent process to resolve boundary state refs
  let parentEntry = null;
  if (isChildProcess) {
    for (const [, pe] of processMap) {
      const parentPO = pe.elementDataInformation.find(
        e => e.$type === 'fpb:ProcessOperator' && e.decomposedView === processId
      );
      if (parentPO) {
        parentEntry = pe;
        break;
      }
    }
  }

  const elementIEs = new Map();

  for (const obj of objects) {
    if (obj.$type === 'fpb:SystemLimit') continue;

    const visual = visualMap.get(obj.id);
    const sucPath = ELEMENT_TO_SUC[obj.$type];
    if (!sucPath) continue;

    const elemName = obj.name || obj.$type.split(':')[1];
    const cleanName = elemName.replace(/\n/g, '');

    // Use FPB.JS ID as AML ID if not yet used; boundary states in child
    // processes get a new UUID (their FPB.JS ID is taken by the parent).
    let elemAmlId;
    if (usedAmlIds.has(obj.id)) {
      elemAmlId = uuidv4(); // Collision → boundary state in child process
    } else {
      elemAmlId = obj.id;   // First occurrence → use FPB.JS ID directly
    }
    usedAmlIds.add(elemAmlId);

    const ie = procIE.ele('InternalElement', {
      Name: cleanName,
      ID: elemAmlId,
      RefBaseSystemUnitPath: sucPath,
    });

    // Identification (full fields)
    addIdentification(ie, obj.identification, cleanName);

    // Characteristics
    addCharacteristics(ie, obj.characteristics);

    // refObj: depends on element type
    if (obj.$type === 'fpb:ProcessOperator') {
      if (obj.decomposedView && poToChildProcess.has(obj.id)) {
        // refObj → child process's AML ID
        const childProcessId = poToChildProcess.get(obj.id);
        const childProcessAmlId = processAmlIds.get(childProcessId);
        addRefObjAttr(ie, childProcessAmlId || '');
      } else {
        addRefObjAttr(ie, '');
      }
    } else if (['fpb:Product', 'fpb:Energy', 'fpb:Information'].includes(obj.$type)) {
      // Boundary states share the same FPB.JS ID across all decomposition
      // levels. The top-level original uses that ID as its AML ID, while
      // deeper boundary copies get new UUIDs. Therefore obj.id always
      // equals the top-level original's AML ID, regardless of depth.
      if (isChildProcess && parentEntry) {
        const parentState = parentEntry.elementDataInformation.find(
          e => e.id === obj.id && ['fpb:Product', 'fpb:Energy', 'fpb:Information'].includes(e.$type)
        );
        if (parentState) {
          // Boundary state — refObj → top-level original's AML ID
          addRefObjAttr(ie, obj.id);
        } else {
          addRefObjAttr(ie, '');
        }
      } else {
        addRefObjAttr(ie, '');
      }
    }

    // ViewInformation
    if (visual) {
      addViewInformation(ie, visual);
    }

    elementIEs.set(obj.id, ie);

    // ── ExternalInterfaces for outgoing flows ──────────────────────
    const outFlows = flowsBySource.get(obj.id) || [];
    for (const flow of outFlows) {
      const ifacePaths = FLOW_TO_INTERFACE[flow.$type];
      if (!ifacePaths) continue;

      const outBaseName = ifacePaths.out.split('/')[1];
      const ifaceName = getNextInterfaceName(obj.id, outBaseName);
      const ifaceId = uuidv4();

      const extIf = ie.ele('ExternalInterface', {
        Name: ifaceName,
        ID: ifaceId,
        RefBaseClassPath: ifacePaths.out,
      });

      // PortCoordinate + Waypoints from visual info
      const flowVisual = visualMap.get(flow.id);
      if (flowVisual?.waypoints && flowVisual.waypoints.length > 0) {
        // First waypoint's original = source port coordinate
        const firstWp = flowVisual.waypoints[0];
        const portCoord = firstWp.original || firstWp;
        addPortCoordinate(extIf, portCoord.x, portCoord.y);

        // Intermediate waypoints (between first and last) = bends
        const intermediates = flowVisual.waypoints.slice(1, -1);
        for (let i = 0; i < intermediates.length; i++) {
          const wp = intermediates[i];
          if (wp.original) continue;
          addWaypointAttr(extIf, i + 1, wp.x, wp.y);
        }
      } else {
        addEmptyPortCoordinate(extIf);
      }

      extIf.up();

      if (!linkMap.has(flow.id)) linkMap.set(flow.id, {});
      linkMap.get(flow.id).outInterfaceId = ifaceId;
    }

    // ── ExternalInterfaces for incoming flows ──────────────────────
    const inFlows = flowsByTarget.get(obj.id) || [];
    for (const flow of inFlows) {
      const ifacePaths = FLOW_TO_INTERFACE[flow.$type];
      if (!ifacePaths) continue;

      const inBaseName = ifacePaths.in.split('/')[1];
      const ifaceName = getNextInterfaceName(obj.id, inBaseName);
      const ifaceId = uuidv4();

      const extIf = ie.ele('ExternalInterface', {
        Name: ifaceName,
        ID: ifaceId,
        RefBaseClassPath: ifacePaths.in,
      });

      // PortCoordinate from last waypoint
      const flowVisual = visualMap.get(flow.id);
      if (flowVisual?.waypoints && flowVisual.waypoints.length > 0) {
        const lastWp = flowVisual.waypoints[flowVisual.waypoints.length - 1];
        const portCoord = lastWp.original || lastWp;
        addPortCoordinate(extIf, portCoord.x, portCoord.y);
      } else {
        addEmptyPortCoordinate(extIf);
      }

      extIf.up();

      if (!linkMap.has(flow.id)) linkMap.set(flow.id, {});
      linkMap.get(flow.id).inInterfaceId = ifaceId;
    }

    ie.up();
  }

  // ── InternalLinks ──────────────────────────────────────────────────
  for (const [flowId, ids] of linkMap) {
    if (ids.outInterfaceId && ids.inInterfaceId) {
      const flow = dataMap.get(flowId);
      const sourceData = flow ? dataMap.get(flow.sourceRef) : null;
      const targetData = flow ? dataMap.get(flow.targetRef) : null;
      const sourceName = sourceData?.name?.replace(/[\s\n]/g, '') || 'Source';
      const targetName = targetData?.name?.replace(/[\s\n]/g, '') || 'Target';

      let linkName;
      if (flow?.$type === 'fpb:Usage') {
        linkName = `${sourceName}_uses_${targetName}`;
      } else {
        linkName = `${sourceName}_to_${targetName}`;
      }

      procIE.ele('InternalLink', {
        Name: linkName,
        RefPartnerSideA: ids.outInterfaceId,
        RefPartnerSideB: ids.inInterfaceId,
      }).up();
    }
  }

  procIE.up();
}

// ══════════════════════════════════════════════════════════════════════════
// Attribute builders
// ══════════════════════════════════════════════════════════════════════════

function addIdentification(parent, ident, fallbackName) {
  const attr = parent.ele('Attribute', {
    Name: 'Identification',
    AttributeDataType: 'xs:string',
    RefAttributeType: ATTR_REFS.identification,
  });
  const fields = ['uniqueIdent', 'longName', 'shortName', 'versionNumber', 'revisionNumber'];
  for (const f of fields) {
    const val = ident?.[f] || (f === 'shortName' ? fallbackName : '') || '';
    const sub = attr.ele('Attribute', { Name: f, AttributeDataType: 'xs:string' });
    if (val) sub.ele('Value').txt(val).up();
    sub.up();
  }
  attr.up();
}

function addCharacteristics(parent, characteristics) {
  if (!characteristics || characteristics.length === 0) return;

  const container = parent.ele('Attribute', {
    Name: 'Characteristics',
    AttributeDataType: 'xs:string',
  });

  for (let i = 0; i < characteristics.length; i++) {
    const c = characteristics[i];
    const cAttr = container.ele('Attribute', {
      Name: `Characteristic_${i + 1}`,
      AttributeDataType: 'xs:string',
      RefAttributeType: ATTR_REFS.characteristic,
    });

    // Category (Kategorie gemäß VDI 3682 Blatt 2, Bild 5)
    const cat = c.category || {};
    const identAttr = cAttr.ele('Attribute', {
      Name: 'Category',
      AttributeDataType: 'xs:string',
      RefAttributeType: ATTR_REFS.identification,
    });
    for (const f of ['uniqueIdent', 'longName', 'shortName', 'versionNumber', 'revisionNumber']) {
      const val = cat[f] || '';
      const sub = identAttr.ele('Attribute', { Name: f, AttributeDataType: 'xs:string' });
      if (val) sub.ele('Value').txt(val).up();
      sub.up();
    }
    identAttr.up();

    // DescriptiveElement
    const desc = c.descriptiveElement || {};
    const descAttr = cAttr.ele('Attribute', { Name: 'DescriptiveElement', AttributeDataType: 'xs:string' });
    addStringSubAttr(descAttr, 'valueDeterminationProcess', desc.valueDeterminationProcess);
    addStringSubAttr(descAttr, 'representivity', desc.representivity);
    addStringSubAttr(descAttr, 'setpointValue', formatValueWithUnit(desc.setpointValue));
    addStringSubAttr(descAttr, 'validityLimits', formatValidityLimits(desc.validityLimits));
    addStringSubAttr(descAttr, 'actualValues', formatActualValues(desc.actualValues));
    descAttr.up();

    // RelationalElement
    const rel = c.relationalElement || {};
    const relAttr = cAttr.ele('Attribute', { Name: 'RelationalElement', AttributeDataType: 'xs:string' });
    addStringSubAttr(relAttr, 'view', rel.view);
    addStringSubAttr(relAttr, 'model', rel.model);
    addStringSubAttr(relAttr, 'regulationsForRelationalGeneration', rel.regulationsForRelationalGeneration);
    relAttr.up();

    cAttr.up();
  }

  container.up();
}

function addStringSubAttr(parent, name, value) {
  const attr = parent.ele('Attribute', { Name: name, AttributeDataType: 'xs:string' });
  if (value) attr.ele('Value').txt(String(value)).up();
  attr.up();
}

function formatValueWithUnit(v) {
  if (!v) return '';
  if (typeof v === 'string') return v;
  const val = v.value != null ? String(v.value) : '';
  const unit = v.unit || '';
  return unit ? `${val} ${unit}` : val;
}

function formatValidityLimits(arr) {
  if (!arr || !Array.isArray(arr)) return '';
  return arr
    .filter(v => v.from || v.to)
    .map(v => `${v.from}-${v.to}`)
    .join(', ');
}

function formatActualValues(arr) {
  if (!arr || !Array.isArray(arr)) return '';
  return arr
    .filter(v => v.value)
    .map(v => formatValueWithUnit(v))
    .join(', ');
}

function addRefObjAttr(parent, value) {
  const attr = parent.ele('Attribute', {
    Name: 'refObj',
    AttributeDataType: 'xs:string',
    RefAttributeType: ATTR_REFS.refObj,
  });
  if (value) {
    attr.ele('Value').txt(value).up();
  }
  attr.up();
}

function addViewInformation(parent, visual) {
  const attr = parent.ele('Attribute', {
    Name: 'ViewInformation',
    AttributeDataType: 'xs:string',
    RefAttributeType: ATTR_REFS.bounds,
  });

  const pos = attr.ele('Attribute', {
    Name: 'position',
    AttributeDataType: 'xs:string',
    RefAttributeType: ATTR_REFS.point,
  });
  addDoubleSubAttr(pos, 'x', visual.x);
  addDoubleSubAttr(pos, 'y', visual.y);
  pos.up();

  addDoubleSubAttr(attr, 'width', visual.width);
  addDoubleSubAttr(attr, 'height', visual.height);

  attr.up();
}

function addPortCoordinate(parent, x, y) {
  const attr = parent.ele('Attribute', {
    Name: 'PortCoordinate',
    AttributeDataType: 'xs:string',
    RefAttributeType: ATTR_REFS.point,
  });
  addDoubleSubAttr(attr, 'x', x);
  addDoubleSubAttr(attr, 'y', y);
  attr.up();
}

function addEmptyPortCoordinate(parent) {
  const attr = parent.ele('Attribute', {
    Name: 'PortCoordinate',
    AttributeDataType: 'xs:string',
    RefAttributeType: ATTR_REFS.point,
  });
  attr.ele('Attribute', { Name: 'x', AttributeDataType: 'xs:double' }).up();
  attr.ele('Attribute', { Name: 'y', AttributeDataType: 'xs:double' }).up();
  attr.up();
}

function addWaypointAttr(parent, index, x, y) {
  const wpAttr = parent.ele('Attribute', {
    Name: `Waypoint_${index}`,
    AttributeDataType: 'xs:string',
    RefAttributeType: ATTR_REFS.waypoint,
  });
  const wpPos = wpAttr.ele('Attribute', {
    Name: 'position',
    AttributeDataType: 'xs:string',
    RefAttributeType: ATTR_REFS.point,
  });
  addDoubleSubAttr(wpPos, 'x', x);
  addDoubleSubAttr(wpPos, 'y', y);
  wpPos.up();
  wpAttr.up();
}

function addDoubleSubAttr(parent, name, value) {
  const attr = parent.ele('Attribute', { Name: name, AttributeDataType: 'xs:double' });
  if (value !== undefined && value !== null) {
    attr.ele('Value').txt(String(value)).up();
  }
  attr.up();
}

module.exports = { jsonToAml };
