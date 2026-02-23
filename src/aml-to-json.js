// AutomationML (CAEX 3.0) → FPB.JS JSON converter

import { XMLParser } from 'fast-xml-parser';
import {
  SUC_TO_ELEMENT, INTERFACE_TO_FLOW,
  OBJECT_TYPES, ATTR_REFS,
} from './mappings.js';

const PARSER_OPTIONS = {
  ignoreAttributes: false,
  attributeNamePrefix: '@_',
  isArray: (name) => {
    // These elements can appear multiple times
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
export function amlToJson(xmlString) {
  const parser = new XMLParser(PARSER_OPTIONS);
  const doc = parser.parse(xmlString);
  const caex = doc.CAEXFile;

  // Find the first InstanceHierarchy
  const ihs = caex.InstanceHierarchy;
  if (!ihs || ihs.length === 0) throw new Error('No InstanceHierarchy found');
  const ih = ihs[0];

  // Find the top-level FPD_Process
  const topProcess = findByRefSUC(ih.InternalElement || [], 'FPD_SystemUnitClassLib/FPD_Process');
  if (!topProcess) throw new Error('No FPD_Process found in InstanceHierarchy');

  // Collect all process entries recursively
  const processEntries = [];
  const entryProcessId = parseProcess(topProcess, null, processEntries);

  // Build Project header
  const project = {
    $type: 'fpb:Project',
    name: 'FPBJS_Project',
    targetNamespace: 'http://www.hsu-ifa.de/fpbjs',
    entryPoint: entryProcessId,
  };

  return [project, ...processEntries];
}

// ══════════════════════════════════════════════════════════════════════════
// Process parser (recursive for decomposition)
// ══════════════════════════════════════════════════════════════════════════

/**
 * Parse a FPD_Process InternalElement into a process entry.
 * @returns {string} The process ID assigned to this process
 */
function parseProcess(processIE, parentProcessId, processEntries) {
  const children = processIE.InternalElement || [];
  const links = processIE.InternalLink || [];

  // ── Collect all child InternalElements ──────────────────────────────
  const systemLimitIE = findByRefSUC(children, 'FPD_SystemUnitClassLib/FPD_SystemLimit');
  const objectIEs = children.filter(ie => {
    const suc = ie['@_RefBaseSystemUnitPath'];
    return suc && SUC_TO_ELEMENT[suc] && suc !== 'FPD_SystemUnitClassLib/FPD_Process';
  });

  // ── Build interface → element lookup for link resolution ───────────
  // interfaceId → { elementId, direction, flowType, interfaceClass }
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
    const slName = systemLimitIE['@_Name'] || 'SystemLimit';
    systemLimitId = generateId();
    const slVisual = parseVisualAttr(systemLimitIE);

    elementDataInformation.push({
      $type: 'fpb:SystemLimit',
      id: systemLimitId,
      elementsContainer: [], // filled later
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

  // Assign a process ID
  // For the entry process, use a fresh ID. For decomposed processes, reuse the PO's ID (as FPB.JS does)
  const processId = parentProcessId || generateId();

  // Parse each object IE
  const elementIdMap = new Map(); // AML IE Name/ID → assigned FPB.JS ID (for consistency)

  for (const ie of objectIEs) {
    const sucPath = ie['@_RefBaseSystemUnitPath'];
    const fpbType = SUC_TO_ELEMENT[sucPath];
    if (!fpbType || fpbType === 'fpb:SystemLimit' || fpbType === 'fpb:Process') continue;

    const elemId = parseIdentificationId(ie) || generateId();
    const name = ie['@_Name'] || '';

    elementIdMap.set(ie['@_ID'], elemId);

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

    // Check for decomposition (nested FPD_Process)
    const nestedProcess = findByRefSUC(ie.InternalElement || [], 'FPD_SystemUnitClassLib/FPD_Process');
    let decomposedView = null;
    if (nestedProcess) {
      // Recursively parse the child process, using this PO's ID
      decomposedView = elemId;
      parseProcess(nestedProcess, elemId, processEntries);
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

    // Flow-related properties (filled after link processing)
    elemData.incoming = [];
    elemData.outgoing = [];
    elemData.isAssignedTo = [];
    elemData.name = name;

    if (decomposedView) {
      elemData.decomposedView = decomposedView;
    }

    elementDataInformation.push(elemData);

    // Visual
    const visual = parseVisualAttr(ie);
    if (visual) {
      elementVisualInformation.push({
        id: elemId,
        ...visual,
        type: fpbType,
        markers: {},
      });
    }

    // Track IDs for process metadata
    elementsContainerIds.push(elemId);
    if (['fpb:Product', 'fpb:Energy', 'fpb:Information'].includes(fpbType)) {
      stateIds.push(elemId);
    }
    if (fpbType === 'fpb:ProcessOperator') {
      poIds.push(elemId);
    }
  }

  // ── Parse InternalLinks → Flows ────────────────────────────────────
  const flowDataMap = new Map(); // flowId → flow data object

  for (const link of links) {
    const sideAId = link['@_RefPartnerSideA'];
    const sideBId = link['@_RefPartnerSideB'];

    const sideA = interfaceMap.get(sideAId);
    const sideB = interfaceMap.get(sideBId);
    if (!sideA || !sideB) continue;

    // SideA should be Out, SideB should be In
    const outSide = sideA.direction === 'out' ? sideA : sideB;
    const inSide = sideA.direction === 'in' ? sideA : sideB;

    const flowId = generateId();
    const flowType = outSide.flowType;

    // Build flow data
    const flowData = {
      $type: flowType,
      id: flowId,
      sourceRef: outSide.elementId,
      targetRef: inSide.elementId,
    };

    // inTandemWith will be computed after all flows are created
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
    if (sourceElem) {
      sourceElem.outgoing.push(flowId);
      if (flowType !== 'fpb:Usage') {
        // For flows, target isAssignedTo source PO (or vice versa)
      }
    }
    if (targetElem) {
      targetElem.incoming.push(flowId);
    }

    // isAssignedTo: states are assigned to POs they connect to
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
    // Usage: PO isAssignedTo TR and TR isAssignedTo PO
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

    // Add flow to elements container
    elementsContainerIds.push(flowId);
  }

  // ── Compute inTandemWith ───────────────────────────────────────────
  // Flows sharing the same source form a tandem group
  const sourceGroups = new Map(); // sourceId → [flowId, ...]
  for (const [flowId, flow] of flowDataMap) {
    if (flow.inTandemWith === undefined) continue; // Skip Flow and Usage
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

  // Add all flow data to elementDataInformation
  for (const flow of flowDataMap.values()) {
    elementDataInformation.push(flow);
  }

  // ── Update SystemLimit's elementsContainer ─────────────────────────
  if (systemLimitId) {
    const sl = elementDataInformation.find(e => e.id === systemLimitId);
    if (sl) {
      sl.elementsContainer = elementsContainerIds;
    }
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
      isDecomposedProcessOperator: parentProcessId || null,
      consistsOfStates: stateIds,
      consistsOfSystemLimit: systemLimitId,
      consistsOfProcesses: [], // filled if there are decomposed POs
      consistsOfProcessOperator: poIds,
      parent: parentProcessId || null, // will be patched for entry process
    },
    elementDataInformation,
    elementVisualInformation,
  };

  // Track decomposed child processes
  for (const elemData of elementDataInformation) {
    if (elemData.decomposedView) {
      processEntry.process.consistsOfProcesses.push(elemData.decomposedView);
    }
  }

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

function parseIdentificationId(ie) {
  const ident = getAttr(ie, 'Identification');
  if (!ident) return null;
  const uid = getSubAttrValue(ident, 'uniqueIdent');
  return uid || null;
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

    const cIdent = getAttr(cAttr, 'Identification');
    if (cIdent) {
      c.identification = {
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

function parseVisualAttr(ie) {
  const visual = getAttr(ie, 'Visual');
  if (!visual) return null;

  const pos = getAttr(visual, 'position');
  const x = pos ? parseFloat(getSubAttrValue(pos, 'x')) : 0;
  const y = pos ? parseFloat(getSubAttrValue(pos, 'y')) : 0;
  const width = parseFloat(getSubAttrValue(visual, 'width')) || 0;
  const height = parseFloat(getSubAttrValue(visual, 'height')) || 0;

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

function parseWaypoints(extIf) {
  const attrs = extIf.Attribute || [];
  const waypoints = [];

  for (const attr of attrs) {
    const name = attr['@_Name'];
    if (!name || !name.startsWith('FPD_Waypoint')) continue;

    const pos = getAttr(attr, 'position');
    if (!pos) continue;

    const x = parseFloat(getSubAttrValue(pos, 'x'));
    const y = parseFloat(getSubAttrValue(pos, 'y'));
    if (isNaN(x) || isNaN(y)) continue;

    // Extract index from name: FPD_Waypoint → 0, FPD_Waypoint1 → 1, etc.
    const indexStr = name.replace('FPD_Waypoint', '');
    const index = indexStr === '' ? 0 : parseInt(indexStr, 10);

    waypoints.push({ index, x, y });
  }

  // Sort by index
  waypoints.sort((a, b) => a.index - b.index);
  return waypoints.map(({ x, y }) => ({ x, y }));
}

/**
 * Build FPB.JS waypoint array from out-interface and in-interface data.
 * Out: PortCoordinate → first waypoint (original), waypoints → middle points
 * In: PortCoordinate → last waypoint (original)
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

  // Middle waypoints (from out-interface FPD_Waypoint attributes)
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

let idCounter = 0;
function generateId() {
  // Generate UUID-like IDs
  return crypto.randomUUID?.() || `gen-${Date.now()}-${idCounter++}`;
}
