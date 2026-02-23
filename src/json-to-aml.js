// FPB.JS JSON → AutomationML (CAEX 3.0) converter

import { v4 as uuidv4 } from 'uuid';
import { create } from 'xmlbuilder2';
import {
  ELEMENT_TO_SUC, FLOW_TO_INTERFACE,
  OBJECT_TYPES, CONNECTION_TYPES, ATTR_REFS,
} from './mappings.js';
import { appendLibraries } from './aml-libraries.js';

/**
 * Convert FPB.JS JSON array to AML XML string.
 * @param {Array} jsonData - The FPB.JS JSON array (Project header + process entries)
 * @returns {string} CAEX 3.0 XML string
 */
export function jsonToAml(jsonData) {
  // ── 1. Parse input ──────────────────────────────────────────────────
  const project = jsonData.find(e => e.$type === 'fpb:Project');
  const processEntries = jsonData.filter(e => e.process);

  // Build lookup: processId → { process, elementDataInformation, elementVisualInformation }
  const processMap = new Map();
  for (const entry of processEntries) {
    processMap.set(entry.process.id, entry);
  }

  // Find entry point process
  const entryProcessId = project.entryPoint;

  // ── 2. Build AML document ──────────────────────────────────────────
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

  // ── 3. InstanceHierarchy ───────────────────────────────────────────
  const ih = caex.ele('InstanceHierarchy', { Name: 'InstanceHierarchy', ID: uuidv4() })
    .ele('Version').txt('1.0.0').up();

  // Build the entry process (recursively handles decomposition)
  buildProcess(ih, entryProcessId, processMap);

  // ── 4. Append library definitions ──────────────────────────────────
  appendLibraries(caex);

  return doc.end({ prettyPrint: true, indent: '  ' });
}

// ══════════════════════════════════════════════════════════════════════════
// Process builder (recursive for decomposition)
// ══════════════════════════════════════════════════════════════════════════

function buildProcess(parent, processId, processMap) {
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

  // Determine process name from SystemLimit name or "Process"
  const slData = elementDataInformation.find(e => e.$type === 'fpb:SystemLimit');
  const processName = slData?.name || 'Process';

  // Create the FPD_Process InternalElement
  const procIE = parent.ele('InternalElement', {
    Name: processName,
    ID: uuidv4(),
    RefBaseSystemUnitPath: ELEMENT_TO_SUC['fpb:Process'],
  });

  // ── SystemLimit ────────────────────────────────────────────────────
  if (slData) {
    const slVisual = visualMap.get(slData.id);
    const slIE = procIE.ele('InternalElement', {
      Name: slData.name || 'SystemLimit',
      ID: uuidv4(),
      RefBaseSystemUnitPath: ELEMENT_TO_SUC['fpb:SystemLimit'],
    });
    if (slVisual) {
      addVisualAttr(slIE, slVisual);
    }
    slIE.up();
  }

  // ── Collect interface IDs for InternalLinks ────────────────────────
  // Map: flowId → { outInterfaceId, inInterfaceId }
  const linkMap = new Map();

  // Track interface name counters per element for numbering (FPD_FlowIn, FPD_FlowIn1, ...)
  const ifaceCounters = new Map(); // elementId → Map<baseName, count>

  function getNextInterfaceName(elementId, baseName) {
    if (!ifaceCounters.has(elementId)) {
      ifaceCounters.set(elementId, new Map());
    }
    const counters = ifaceCounters.get(elementId);
    const count = counters.get(baseName) || 0;
    counters.set(baseName, count + 1);
    return count === 0 ? baseName : `${baseName}${count}`;
  }

  // ── Object elements (States, POs, TRs) ────────────────────────────
  // Collect flows first to know which interfaces each element needs
  const flows = elementDataInformation.filter(e => CONNECTION_TYPES.has(e.$type));
  const objects = elementDataInformation.filter(e => OBJECT_TYPES.has(e.$type));

  // Group flows by source and target element
  const flowsBySource = new Map(); // elementId → [flow, ...]
  const flowsByTarget = new Map();
  for (const flow of flows) {
    if (!flowsBySource.has(flow.sourceRef)) flowsBySource.set(flow.sourceRef, []);
    flowsBySource.get(flow.sourceRef).push(flow);
    if (!flowsByTarget.has(flow.targetRef)) flowsByTarget.set(flow.targetRef, []);
    flowsByTarget.get(flow.targetRef).push(flow);
  }

  // Track element IE nodes for adding interfaces
  const elementIEs = new Map(); // elementId → xmlbuilder element

  for (const obj of objects) {
    if (obj.$type === 'fpb:SystemLimit') continue; // already handled

    const visual = visualMap.get(obj.id);
    const sucPath = ELEMENT_TO_SUC[obj.$type];
    if (!sucPath) continue;

    const ie = procIE.ele('InternalElement', {
      Name: obj.name || obj.$type.split(':')[1],
      ID: uuidv4(),
      RefBaseSystemUnitPath: sucPath,
    });

    // Identification
    if (obj.identification) {
      addIdentificationAttr(ie, obj.identification);
    }

    // Characteristics
    addCharacteristicsAttr(ie, obj.characteristics);

    // Visual
    if (visual) {
      addVisualAttr(ie, visual);
    }

    elementIEs.set(obj.id, ie);

    // ── ExternalInterfaces for outgoing flows ──────────────────────
    const outFlows = flowsBySource.get(obj.id) || [];
    for (const flow of outFlows) {
      const ifacePaths = FLOW_TO_INTERFACE[flow.$type];
      if (!ifacePaths) continue;

      const outBaseName = ifacePaths.out.split('/')[1]; // e.g. "FPD_ParallelFlowOut"
      const ifaceName = getNextInterfaceName(obj.id, outBaseName);
      const ifaceId = uuidv4();

      const extIf = ie.ele('ExternalInterface', {
        Name: ifaceName,
        ID: ifaceId,
        RefBaseClassPath: ifacePaths.out,
      });

      // PortCoordinate + Waypoints from visual info
      const flowVisual = visualMap.get(flow.id);
      if (flowVisual?.waypoints) {
        addFlowOutInterface(extIf, flowVisual.waypoints);
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

      const inBaseName = ifacePaths.in.split('/')[1]; // e.g. "FPD_ParallelFlowIn"
      const ifaceName = getNextInterfaceName(obj.id, inBaseName);
      const ifaceId = uuidv4();

      const extIf = ie.ele('ExternalInterface', {
        Name: ifaceName,
        ID: ifaceId,
        RefBaseClassPath: ifacePaths.in,
      });

      // PortCoordinate from last waypoint
      const flowVisual = visualMap.get(flow.id);
      if (flowVisual?.waypoints) {
        addFlowInInterface(extIf, flowVisual.waypoints);
      } else {
        addEmptyPortCoordinate(extIf);
      }

      extIf.up();

      if (!linkMap.has(flow.id)) linkMap.set(flow.id, {});
      linkMap.get(flow.id).inInterfaceId = ifaceId;
    }

    // ── Decomposition: PO with decomposedView ──────────────────────
    if (obj.$type === 'fpb:ProcessOperator' && obj.decomposedView) {
      // The decomposedView ID points to a child process
      buildProcess(ie, obj.decomposedView, processMap);
    }

    ie.up();
  }

  // ── InternalLinks ──────────────────────────────────────────────────
  let linkCounter = 0;
  for (const [flowId, ids] of linkMap) {
    if (ids.outInterfaceId && ids.inInterfaceId) {
      const linkName = linkCounter === 0 ? 'Link' : `Link${linkCounter}`;
      procIE.ele('InternalLink', {
        RefPartnerSideA: ids.outInterfaceId,
        RefPartnerSideB: ids.inInterfaceId,
        Name: linkName,
      }).up();
      linkCounter++;
    }
  }

  procIE.up();
}

// ══════════════════════════════════════════════════════════════════════════
// Attribute builders
// ══════════════════════════════════════════════════════════════════════════

function addIdentificationAttr(parent, identification) {
  const attr = parent.ele('Attribute', {
    Name: 'Identification',
    AttributeDataType: 'xs:string',
    RefAttributeType: ATTR_REFS.identification,
  });
  addStringSubAttr(attr, 'uniqueIdent', identification.uniqueIdent);
  addStringSubAttr(attr, 'longName', identification.longName);
  addStringSubAttr(attr, 'shortName', identification.shortName);
  addStringSubAttr(attr, 'versionNumber', identification.versionNumber);
  addStringSubAttr(attr, 'revisionNumber', identification.revisionNumber);
  attr.up();
}

function addCharacteristicsAttr(parent, characteristics) {
  const container = parent.ele('Attribute', {
    Name: 'Characteristics',
    AttributeDataType: 'xs:string',
  });
  container.ele('Description').txt('Container for characteristics').up();

  if (characteristics && characteristics.length > 0) {
    for (let i = 0; i < characteristics.length; i++) {
      const c = characteristics[i];
      const name = i === 0 ? 'Characteristic' : `Characteristic${i}`;
      const cAttr = container.ele('Attribute', {
        Name: name,
        AttributeDataType: 'xs:string',
        RefAttributeType: ATTR_REFS.characteristic,
      });
      if (c.identification) {
        const cIdent = cAttr.ele('Attribute', {
          Name: 'Identification',
          AttributeDataType: 'xs:string',
          RefAttributeType: ATTR_REFS.identification,
        });
        addStringSubAttr(cIdent, 'uniqueIdent', c.identification.uniqueIdent);
        addStringSubAttr(cIdent, 'longName', c.identification.longName);
        addStringSubAttr(cIdent, 'shortName', c.identification.shortName);
        addStringSubAttr(cIdent, 'versionNumber', c.identification.versionNumber);
        addStringSubAttr(cIdent, 'revisionNumber', c.identification.revisionNumber);
        cIdent.up();
      }
      if (c.descriptiveElement) {
        const desc = cAttr.ele('Attribute', { Name: 'DescriptiveElement', AttributeDataType: 'xs:string' });
        addStringSubAttr(desc, 'valueDeterminationProcess', c.descriptiveElement.valueDeterminationProcess);
        addStringSubAttr(desc, 'representivity', c.descriptiveElement.representivity);
        addStringSubAttr(desc, 'setpointValue', c.descriptiveElement.setpointValue);
        addStringSubAttr(desc, 'validityLimits', c.descriptiveElement.validityLimits);
        addStringSubAttr(desc, 'actualValues', c.descriptiveElement.actualValues);
        desc.up();
      }
      if (c.relationalElement) {
        const rel = cAttr.ele('Attribute', { Name: 'RelationalElement', AttributeDataType: 'xs:string' });
        addStringSubAttr(rel, 'view', c.relationalElement.view);
        addStringSubAttr(rel, 'model', c.relationalElement.model);
        addStringSubAttr(rel, 'regulationsForRelationalGeneration', c.relationalElement.regulationsForRelationalGeneration);
        rel.up();
      }
      cAttr.up();
    }
  }

  container.up();
}

function addVisualAttr(parent, visual) {
  const attr = parent.ele('Attribute', {
    Name: 'Visual',
    AttributeDataType: 'xs:string',
    RefAttributeType: ATTR_REFS.elementVisual,
  });

  const pos = attr.ele('Attribute', {
    Name: 'position',
    AttributeDataType: 'xs:string',
    RefAttributeType: ATTR_REFS.coordinate,
  });
  addDoubleSubAttr(pos, 'x', visual.x);
  addDoubleSubAttr(pos, 'y', visual.y);
  pos.up();

  addDoubleSubAttr(attr, 'width', visual.width);
  addDoubleSubAttr(attr, 'height', visual.height);

  attr.up();
}

/**
 * Add PortCoordinate + Waypoints to an Out-interface.
 * Convention: first waypoint (with original) → PortCoordinate,
 * middle waypoints (without original) → FPD_Waypoint, FPD_Waypoint1, ...
 * Last waypoint is the In-side anchor (not stored here).
 */
function addFlowOutInterface(extIf, waypoints) {
  if (!waypoints || waypoints.length === 0) {
    addEmptyPortCoordinate(extIf);
    return;
  }

  // First waypoint → PortCoordinate (the "original" anchor point on the source)
  const first = waypoints[0];
  const portCoord = first.original || first;
  addPortCoordinate(extIf, portCoord.x, portCoord.y);

  // Middle waypoints → FPD_Waypoint, FPD_Waypoint1, ...
  // (all waypoints between first and last that don't have "original")
  const middleWaypoints = waypoints.slice(1, -1);
  let wpCounter = 0;
  for (const wp of middleWaypoints) {
    if (wp.original) continue; // skip if it's an anchor (shouldn't happen in middle)
    const wpName = wpCounter === 0 ? 'FPD_Waypoint' : `FPD_Waypoint${wpCounter}`;
    const wpAttr = extIf.ele('Attribute', {
      Name: wpName,
      AttributeDataType: 'xs:string',
      RefAttributeType: ATTR_REFS.waypoint,
    });
    const wpPos = wpAttr.ele('Attribute', {
      Name: 'position',
      AttributeDataType: 'xs:string',
      RefAttributeType: ATTR_REFS.coordinate,
    });
    addDoubleSubAttr(wpPos, 'x', wp.x);
    addDoubleSubAttr(wpPos, 'y', wp.y);
    wpPos.up();
    wpAttr.up();
    wpCounter++;
  }
}

/**
 * Add PortCoordinate to an In-interface.
 * Convention: last waypoint (with original) → PortCoordinate
 */
function addFlowInInterface(extIf, waypoints) {
  if (!waypoints || waypoints.length === 0) {
    addEmptyPortCoordinate(extIf);
    return;
  }

  const last = waypoints[waypoints.length - 1];
  const portCoord = last.original || last;
  addPortCoordinate(extIf, portCoord.x, portCoord.y);
}

function addPortCoordinate(parent, x, y) {
  const attr = parent.ele('Attribute', {
    Name: 'PortCoordinate',
    AttributeDataType: 'xs:string',
    RefAttributeType: ATTR_REFS.coordinate,
  });
  addDoubleSubAttr(attr, 'x', x);
  addDoubleSubAttr(attr, 'y', y);
  attr.up();
}

function addEmptyPortCoordinate(parent) {
  const attr = parent.ele('Attribute', {
    Name: 'PortCoordinate',
    AttributeDataType: 'xs:string',
    RefAttributeType: ATTR_REFS.coordinate,
  });
  attr.ele('Attribute', { Name: 'x', AttributeDataType: 'xs:double' }).up();
  attr.ele('Attribute', { Name: 'y', AttributeDataType: 'xs:double' }).up();
  attr.up();
}

// ── Primitive attribute helpers ──────────────────────────────────────────

function addStringSubAttr(parent, name, value) {
  const attr = parent.ele('Attribute', { Name: name, AttributeDataType: 'xs:string' });
  if (value !== undefined && value !== null && value !== '') {
    attr.ele('Value').txt(String(value)).up();
  }
  attr.up();
}

function addDoubleSubAttr(parent, name, value) {
  const attr = parent.ele('Attribute', { Name: name, AttributeDataType: 'xs:double' });
  if (value !== undefined && value !== null) {
    attr.ele('Value').txt(String(value)).up();
  }
  attr.up();
}
