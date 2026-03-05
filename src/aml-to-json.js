// AutomationML (CAEX 3.0) → FPB.JS JSON converter

const { randomUUID } = require('crypto');
const { XMLParser } = require('fast-xml-parser');
const {
  SUC_TO_ELEMENT, INTERFACE_TO_FLOW,
  OBJECT_TYPES, ATTR_REFS,
} = require('./mappings.js');

const PARSER_OPTIONS = {
  ignoreAttributes: false,
  attributeNamePrefix: '@_',
  isArray: (name) => {
    return [
      'InternalElement', 'ExternalInterface', 'InternalLink',
      'Attribute', 'InstanceHierarchy',
    ].includes(name);
  },
};

/**
 * Convert AML XML string to FPB.JS JSON array.
 * @param {string} xmlString - CAEX 3.0 XML
 * @returns {Array} FPB.JS JSON array (Project header + process entries)
 */
function amlToJson(xmlString) {
  const parser = new XMLParser(PARSER_OPTIONS);
  const doc = parser.parse(xmlString);
  const caex = doc.CAEXFile;

  // Find the first InstanceHierarchy
  const ihs = caex.InstanceHierarchy;
  if (!ihs || ihs.length === 0) throw new Error('No InstanceHierarchy found');
  const ih = ihs[0];

  // Collect all FPD_Process InternalElements (flat structure)
  const allProcessIEs = (ih.InternalElement || []).filter(
    ie => ie['@_RefBaseSystemUnitPath'] === 'FPD_SystemUnitClassLib/FPD_Process'
  );

  if (allProcessIEs.length === 0) throw new Error('No FPD_Process found in InstanceHierarchy');

  // Build refObj lookup: process refObj → parent PO AML ID
  // and PO refObj → child process AML ID
  const processRefObjMap = new Map(); // process AML ID → parent PO ID (from refObj)
  const poRefObjMap = new Map(); // PO AML ID → child process AML ID (from refObj)

  for (const procIE of allProcessIEs) {
    const procRefObj = getRefObjValue(procIE);
    if (procRefObj) {
      processRefObjMap.set(procIE['@_ID'], procRefObj);
    }

    // Scan POs inside this process for refObj → child process
    const children = procIE.InternalElement || [];
    for (const ie of children) {
      if (ie['@_RefBaseSystemUnitPath'] === 'FPD_SystemUnitClassLib/FPD_ProcessOperator') {
        const poRefObj = getRefObjValue(ie);
        if (poRefObj) {
          poRefObjMap.set(ie['@_ID'], poRefObj);
        }
      }
    }
  }

  // Determine entry process (the one without a refObj pointing to a parent PO)
  const entryProcess = allProcessIEs.find(
    procIE => !getRefObjValue(procIE)
  ) || allProcessIEs[0];

  // Parse all processes
  const processEntries = [];
  const processIdMap = new Map(); // AML Process ID → FPB.JS process ID
  const amlToFpbId = new Map();   // AML element ID → FPB.JS element ID (for cross-refs)

  for (const procIE of allProcessIEs) {
    parseProcess(procIE, allProcessIEs, processRefObjMap, poRefObjMap, processEntries, processIdMap, amlToFpbId);
  }

  // ── Post-processing: resolve cross-process references ──────────────
  // FPB.JS convention: PO ID = child process ID (they share the same ID).
  // Build PO FPB.JS ID → child process FPB.JS ID mapping, then unify.
  const poFpbToChildFpb = new Map(); // PO FPB.JS ID → child process FPB.JS ID

  for (const entry of processEntries) {
    for (const elem of entry.elementDataInformation) {
      if (elem.$type === 'fpb:ProcessOperator' && elem._amlId) {
        const childProcessAmlId = poRefObjMap.get(elem._amlId);
        if (childProcessAmlId) {
          const childFpbId = processIdMap.get(childProcessAmlId);
          if (childFpbId) {
            poFpbToChildFpb.set(elem.id, childFpbId);
          }
        }
      }
    }
  }

  for (const entry of processEntries) {
    const proc = entry.process;

    // isDecomposedProcessOperator: resolve AML PO ID → FPB.JS PO ID
    if (proc.isDecomposedProcessOperator) {
      const poFpbId = amlToFpbId.get(proc.isDecomposedProcessOperator);
      proc.isDecomposedProcessOperator = poFpbId || proc.isDecomposedProcessOperator;
      proc.parent = proc.isDecomposedProcessOperator;

      // FPB.JS convention: child process ID = PO ID
      proc.id = proc.isDecomposedProcessOperator;
    }

    // decomposedView on POs: set to PO's own ID (= child process ID in FPB.JS)
    for (const elem of entry.elementDataInformation) {
      if (elem.$type === 'fpb:ProcessOperator' && elem.decomposedView) {
        elem.decomposedView = elem.id; // FPB.JS: decomposedView = PO's own ID
        delete elem._amlId;
      }
    }

    // consistsOfProcesses: collect decomposedView IDs (= PO IDs)
    proc.consistsOfProcesses = entry.elementDataInformation
      .filter(e => e.decomposedView)
      .map(e => e.decomposedView);
  }

  // Build Project header
  const entryProcessFpbId = processIdMap.get(entryProcess['@_ID']);
  const project = {
    $type: 'fpb:Project',
    name: ih['@_Name'] || 'FPBJS_Project',
    targetNamespace: 'http://www.hsu-ifa.de/fpbjs',
    entryPoint: entryProcessFpbId,
  };

  return [project, ...processEntries];
}

// ══════════════════════════════════════════════════════════════════════════
// Process parser
// ══════════════════════════════════════════════════════════════════════════

function parseProcess(processIE, allProcessIEs, processRefObjMap, poRefObjMap, processEntries, processIdMap, amlToFpbId) {
  const children = processIE.InternalElement || [];
  const links = processIE.InternalLink || [];

  // ── Collect all child InternalElements ──────────────────────────────
  const systemLimitIE = findByRefSUC(children, 'FPD_SystemUnitClassLib/FPD_SystemLimit');
  const objectIEs = children.filter(ie => {
    const suc = ie['@_RefBaseSystemUnitPath'];
    return suc && SUC_TO_ELEMENT[suc] && suc !== 'FPD_SystemUnitClassLib/FPD_Process';
  });

  // ── Build interface → element lookup for link resolution ───────────
  const interfaceMap = new Map();

  // ── Parse object elements ──────────────────────────────────────────
  const elementDataInformation = [];
  const elementVisualInformation = [];
  const stateIds = [];
  const poIds = [];
  const elementsContainerIds = [];

  // Process SystemLimit first
  let systemLimitId = null;
  if (systemLimitIE) {
    const slName = parseShortName(systemLimitIE) || systemLimitIE['@_Name'] || 'SystemLimit';
    systemLimitId = generateId();
    const slVisual = parseViewInformation(systemLimitIE);

    elementDataInformation.push({
      $type: 'fpb:SystemLimit',
      id: systemLimitId,
      elementsContainer: [],
      name: slName,
    });

    if (slVisual) {
      elementVisualInformation.push({
        id: systemLimitId,
        ...slVisual,
        type: 'fpb:SystemLimit',
        markers: {},
      });
    }
  }

  // Assign process ID
  // For child processes (with refObj to parent PO), use the PO's element ID
  const processRefObj = getRefObjValue(processIE);
  let processId;
  if (processRefObj) {
    // This is a child process — use same ID approach as FPB.JS
    processId = generateId();
  } else {
    processId = generateId();
  }
  processIdMap.set(processIE['@_ID'], processId);

  // Determine parent PO FPB.JS ID (for isDecomposedProcessOperator)
  const parentPOId = processRefObj ? processRefObj : null;

  // ── Parse each object IE ──────────────────────────────────────────
  const elementIdMap = new Map(); // AML IE ID → assigned FPB.JS ID

  for (const ie of objectIEs) {
    const sucPath = ie['@_RefBaseSystemUnitPath'];
    const fpbType = SUC_TO_ELEMENT[sucPath];
    if (!fpbType || fpbType === 'fpb:SystemLimit' || fpbType === 'fpb:Process') continue;

    const elemId = generateId();
    const name = parseShortName(ie) || ie['@_Name'] || '';

    elementIdMap.set(ie['@_ID'], elemId);
    amlToFpbId.set(ie['@_ID'], elemId);

    // Collect ExternalInterfaces for this element
    const extInterfaces = ie.ExternalInterface || [];
    for (const extIf of extInterfaces) {
      const refClass = extIf['@_RefBaseClassPath'];
      const info = INTERFACE_TO_FLOW[refClass];
      if (info) {
        interfaceMap.set(extIf['@_ID'], {
          elementId: elemId,
          direction: info.direction,
          flowType: info.flowType,
          portCoordinate: parsePortCoordinate(extIf),
          waypoints: parseWaypoints(extIf),
        });
      }
    }

    // Check for decomposition via refObj
    let decomposedView = null;
    let amlIdForPostProcess = null;
    if (fpbType === 'fpb:ProcessOperator') {
      const poRefObj = getRefObjValue(ie);
      if (poRefObj) {
        decomposedView = '__pending__'; // Resolved in post-processing
        amlIdForPostProcess = ie['@_ID'];
      }
    }

    // Build element data
    const elemData = {
      $type: fpbType,
      id: elemId,
    };

    // Identification
    const identification = parseIdentification(ie);
    if (identification) {
      elemData.identification = identification;
    }

    // Characteristics
    elemData.characteristics = parseCharacteristics(ie);

    elemData.incoming = [];
    elemData.outgoing = [];
    elemData.isAssignedTo = [];
    elemData.name = name;

    if (decomposedView) {
      elemData.decomposedView = decomposedView;
      elemData._amlId = amlIdForPostProcess; // Temp: for post-processing resolution
    }

    elementDataInformation.push(elemData);

    // Visual
    const visual = parseViewInformation(ie);
    if (visual) {
      elementVisualInformation.push({
        id: elemId,
        ...visual,
        type: fpbType,
        markers: {},
      });
    }

    // Track IDs
    elementsContainerIds.push(elemId);
    if (['fpb:Product', 'fpb:Energy', 'fpb:Information'].includes(fpbType)) {
      stateIds.push(elemId);
    }
    if (fpbType === 'fpb:ProcessOperator') {
      poIds.push(elemId);
    }
  }

  // ── Parse InternalLinks → Flows ────────────────────────────────────
  const flowDataMap = new Map();

  for (const link of links) {
    const sideAId = link['@_RefPartnerSideA'];
    const sideBId = link['@_RefPartnerSideB'];

    const sideA = interfaceMap.get(sideAId);
    const sideB = interfaceMap.get(sideBId);
    if (!sideA || !sideB) continue;

    // For Usage (both sides are 'out' in INTERFACE_TO_FLOW), SideA = source
    const outSide = sideA.direction === 'out' ? sideA : sideB;
    const inSide = sideA.direction === 'in' ? sideA : sideB;

    const flowId = generateId();
    const flowType = outSide.flowType;

    const flowData = {
      $type: flowType,
      id: flowId,
      sourceRef: outSide.elementId,
      targetRef: inSide.elementId,
    };

    if (flowType !== 'fpb:Flow' && flowType !== 'fpb:Usage') {
      flowData.inTandemWith = [];
    }

    flowDataMap.set(flowId, flowData);

    // Build waypoints visual info
    const waypoints = buildWaypoints(outSide, inSide);
    if (waypoints.length > 0) {
      elementVisualInformation.push({
        id: flowId,
        type: flowType,
        waypoints,
        markers: {},
      });
    }

    // Update element references
    const sourceElem = elementDataInformation.find(e => e.id === outSide.elementId);
    const targetElem = elementDataInformation.find(e => e.id === inSide.elementId);
    if (sourceElem) sourceElem.outgoing.push(flowId);
    if (targetElem) targetElem.incoming.push(flowId);

    // isAssignedTo
    if (sourceElem && ['fpb:Product', 'fpb:Energy', 'fpb:Information'].includes(sourceElem.$type)) {
      if (targetElem && targetElem.$type === 'fpb:ProcessOperator') {
        if (!sourceElem.isAssignedTo.includes(targetElem.id)) {
          sourceElem.isAssignedTo.push(targetElem.id);
        }
      }
    }
    if (targetElem && ['fpb:Product', 'fpb:Energy', 'fpb:Information'].includes(targetElem.$type)) {
      if (sourceElem && sourceElem.$type === 'fpb:ProcessOperator') {
        if (!targetElem.isAssignedTo.includes(sourceElem.id)) {
          targetElem.isAssignedTo.push(sourceElem.id);
        }
      }
    }
    if (flowType === 'fpb:Usage') {
      if (sourceElem && targetElem) {
        if (!sourceElem.isAssignedTo.includes(targetElem.id)) {
          sourceElem.isAssignedTo.push(targetElem.id);
        }
        if (!targetElem.isAssignedTo.includes(sourceElem.id)) {
          targetElem.isAssignedTo.push(sourceElem.id);
        }
      }
    }

    elementsContainerIds.push(flowId);
  }

  // ── Compute inTandemWith ───────────────────────────────────────────
  const sourceGroups = new Map();
  for (const [flowId, flow] of flowDataMap) {
    if (flow.inTandemWith === undefined) continue;
    const key = flow.sourceRef;
    if (!sourceGroups.has(key)) sourceGroups.set(key, []);
    sourceGroups.get(key).push(flowId);
  }
  for (const group of sourceGroups.values()) {
    if (group.length <= 1) continue;
    for (const flowId of group) {
      const flow = flowDataMap.get(flowId);
      flow.inTandemWith = group.filter(id => id !== flowId);
    }
  }

  for (const flow of flowDataMap.values()) {
    elementDataInformation.push(flow);
  }

  // ── Update SystemLimit's elementsContainer ─────────────────────────
  if (systemLimitId) {
    const sl = elementDataInformation.find(e => e.id === systemLimitId);
    if (sl) sl.elementsContainer = elementsContainerIds;
  }

  // ── Build process entry ────────────────────────────────────────────
  const processEntry = {
    process: {
      $type: 'fpb:Process',
      id: processId,
      elementsContainer: systemLimitId
        ? [systemLimitId, ...elementDataInformation.filter(e =>
          e.$type === 'fpb:TechnicalResource').map(e => e.id)]
        : [],
      isDecomposedProcessOperator: parentPOId || null,
      consistsOfStates: stateIds,
      consistsOfSystemLimit: systemLimitId,
      consistsOfProcesses: [],
      consistsOfProcessOperator: poIds,
      parent: parentPOId || null,
    },
    elementDataInformation,
    elementVisualInformation,
  };

  // consistsOfProcesses is resolved in post-processing

  processEntries.push(processEntry);
  return processId;
}

// ══════════════════════════════════════════════════════════════════════════
// Attribute parsers
// ══════════════════════════════════════════════════════════════════════════

function findByRefSUC(elements, sucPath) {
  return elements.find(ie => ie['@_RefBaseSystemUnitPath'] === sucPath);
}

function getAttr(ie, name) {
  const attrs = ie.Attribute || [];
  return attrs.find(a => a['@_Name'] === name);
}

function getAttrValue(attr) {
  if (!attr) return '';
  if (attr.Value !== undefined && attr.Value !== null) return String(attr.Value);
  return '';
}

function getSubAttrValue(parent, name) {
  if (!parent) return '';
  const sub = getAttr(parent, name);
  return getAttrValue(sub);
}

function getRefObjValue(ie) {
  const refObjAttr = getAttr(ie, 'refObj');
  if (!refObjAttr) return null;
  const val = getAttrValue(refObjAttr);
  return val || null;
}

function parseShortName(ie) {
  const ident = getAttr(ie, 'Identification');
  if (!ident) return null;
  const sn = getSubAttrValue(ident, 'shortName');
  return sn || null;
}

function parseIdentification(ie) {
  const ident = getAttr(ie, 'Identification');
  if (!ident) return null;

  return {
    $type: 'fpb:Identification',
    uniqueIdent: getSubAttrValue(ident, 'uniqueIdent'),
    longName: getSubAttrValue(ident, 'longName'),
    shortName: getSubAttrValue(ident, 'shortName'),
    versionNumber: getSubAttrValue(ident, 'versionNumber'),
    revisionNumber: getSubAttrValue(ident, 'revisionNumber'),
  };
}

function parseCharacteristics(ie) {
  const container = getAttr(ie, 'Characteristics');
  if (!container) return [];

  const characteristics = [];
  const attrs = container.Attribute || [];
  for (const cAttr of attrs) {
    if (!cAttr['@_Name']?.startsWith('Characteristic')) continue;
    const c = {};

    const cIdent = getAttr(cAttr, 'Category');
    if (cIdent) {
      c.category = {
        uniqueIdent: getSubAttrValue(cIdent, 'uniqueIdent'),
        longName: getSubAttrValue(cIdent, 'longName'),
        shortName: getSubAttrValue(cIdent, 'shortName'),
        versionNumber: getSubAttrValue(cIdent, 'versionNumber'),
        revisionNumber: getSubAttrValue(cIdent, 'revisionNumber'),
      };
    }

    const desc = getAttr(cAttr, 'DescriptiveElement');
    if (desc) {
      c.descriptiveElement = {
        valueDeterminationProcess: getSubAttrValue(desc, 'valueDeterminationProcess'),
        representivity: getSubAttrValue(desc, 'representivity'),
        setpointValue: getSubAttrValue(desc, 'setpointValue'),
        validityLimits: getSubAttrValue(desc, 'validityLimits'),
        actualValues: getSubAttrValue(desc, 'actualValues'),
      };
    }

    const rel = getAttr(cAttr, 'RelationalElement');
    if (rel) {
      c.relationalElement = {
        view: getSubAttrValue(rel, 'view'),
        model: getSubAttrValue(rel, 'model'),
        regulationsForRelationalGeneration: getSubAttrValue(rel, 'regulationsForRelationalGeneration'),
      };
    }

    characteristics.push(c);
  }

  return characteristics;
}

/**
 * Parse ViewInformation (FPD_Bounds) from an InternalElement.
 */
function parseViewInformation(ie) {
  const vi = getAttr(ie, 'ViewInformation');
  if (!vi) return null;

  const pos = getAttr(vi, 'position');
  const x = pos ? parseFloat(getSubAttrValue(pos, 'x')) : 0;
  const y = pos ? parseFloat(getSubAttrValue(pos, 'y')) : 0;
  const width = parseFloat(getSubAttrValue(vi, 'width')) || 0;
  const height = parseFloat(getSubAttrValue(vi, 'height')) || 0;

  if (!width && !height && !x && !y) return null;

  return { x, y, width, height };
}

function parsePortCoordinate(extIf) {
  const attrs = extIf.Attribute || [];
  const pcAttr = attrs.find(a => a['@_Name'] === 'PortCoordinate');
  if (!pcAttr) return null;

  const x = parseFloat(getSubAttrValue(pcAttr, 'x'));
  const y = parseFloat(getSubAttrValue(pcAttr, 'y'));
  if (isNaN(x) || isNaN(y)) return null;

  return { x, y };
}

/**
 * Parse Waypoint_1, Waypoint_2, ... from an ExternalInterface.
 */
function parseWaypoints(extIf) {
  const attrs = extIf.Attribute || [];
  const waypoints = [];

  for (const attr of attrs) {
    const name = attr['@_Name'];
    if (!name || !name.startsWith('Waypoint_')) continue;

    const pos = getAttr(attr, 'position');
    if (!pos) continue;

    const x = parseFloat(getSubAttrValue(pos, 'x'));
    const y = parseFloat(getSubAttrValue(pos, 'y'));
    if (isNaN(x) || isNaN(y)) continue;

    // Extract index from name: Waypoint_1 → 1, Waypoint_2 → 2, etc.
    const indexStr = name.replace('Waypoint_', '');
    const index = parseInt(indexStr, 10);

    waypoints.push({ index, x, y });
  }

  // Sort by index
  waypoints.sort((a, b) => a.index - b.index);
  return waypoints.map(({ x, y }) => ({ x, y }));
}

/**
 * Build FPB.JS waypoint array from out-interface and in-interface data.
 * Out-port: PortCoordinate → first waypoint (original), Waypoint_N → intermediate bends
 * In-port: PortCoordinate → last waypoint (original)
 */
function buildWaypoints(outSide, inSide) {
  const waypoints = [];

  // First waypoint: out-side PortCoordinate (with original marker)
  if (outSide.portCoordinate) {
    waypoints.push({
      original: { x: outSide.portCoordinate.x, y: outSide.portCoordinate.y },
      x: outSide.portCoordinate.x,
      y: outSide.portCoordinate.y,
    });
  }

  // Intermediate waypoints (from out-interface Waypoint_N attributes)
  for (const wp of outSide.waypoints || []) {
    waypoints.push({ x: wp.x, y: wp.y });
  }

  // Last waypoint: in-side PortCoordinate (with original marker)
  if (inSide.portCoordinate) {
    waypoints.push({
      original: { x: inSide.portCoordinate.x, y: inSide.portCoordinate.y },
      x: inSide.portCoordinate.x,
      y: inSide.portCoordinate.y,
    });
  }

  return waypoints;
}

// ── Utility ──────────────────────────────────────────────────────────────

function generateId() {
  return randomUUID();
}

module.exports = { amlToJson };
