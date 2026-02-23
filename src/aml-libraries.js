// AML Library definitions extracted from VDI3682_Lib_v0.1.aml
// These are embedded statically in every generated AML file.

/**
 * Build the 4 FPD library branches using xmlbuilder2 API.
 * @param {object} root - the <CAEXFile> node (xmlbuilder2 element)
 */
export function appendLibraries(root) {
  // ── 1. InterfaceClassLib ──────────────────────────────────────────────
  const icl = root.ele('InterfaceClassLib', { Name: 'FPD_InterfaceClassLib' })
    .ele('Version').txt('1.0.0').up();

  // Base class: FPD_Port (carries PortCoordinate)
  const port = icl.ele('InterfaceClass', { Name: 'FPD_Port' })
    .ele('Version').txt('1.0.0').up();
  addCoordinateAttr(port, 'PortCoordinate', 'FPD_VisualAttributeTypeLib/FPD_Coordinate');
  port.up();

  // Flow / Usage interface classes (all inherit from FPD_Port)
  const flowICs = [
    'FPD_FlowIn', 'FPD_FlowOut',
    'FPD_Usage',
    'FPD_ParallelFlowIn', 'FPD_ParallelFlowOut',
    'FPD_AlternativeFlowIn', 'FPD_AlternativeFlowOut',
  ];
  for (const name of flowICs) {
    icl.ele('InterfaceClass', {
      Name: name,
      RefBaseClassPath: 'FPD_InterfaceClassLib/FPD_Port',
    }).ele('Version').txt('1.0.0').up().up();
  }

  // ── 2. SystemUnitClassLib ─────────────────────────────────────────────
  const sucl = root.ele('SystemUnitClassLib', { Name: 'FPD_SystemUnitClassLib' })
    .ele('Version').txt('1.0.0').up();

  // FPD_Object (abstract base with Identification + Characteristics + Visual)
  const obj = sucl.ele('SystemUnitClass', { Name: 'FPD_Object' })
    .ele('Version').txt('1.0.0').up();
  addIdentificationAttr(obj);
  obj.ele('Attribute', { Name: 'Characteristics', AttributeDataType: 'xs:string' })
    .ele('Description').txt('Container for characteristics').up().up();
  addVisualAttr(obj);
  obj.up();

  // Concrete SUCs inheriting from FPD_Object
  for (const name of ['FPD_SystemLimit', 'FPD_ProcessOperator', 'FPD_TechnicalResource']) {
    sucl.ele('SystemUnitClass', {
      Name: name,
      RefBaseClassPath: 'FPD_SystemUnitClassLib/FPD_Object',
    }).ele('Version').txt('1.0.0').up().up();
  }

  // FPD_State (abstract, inherits FPD_Object)
  sucl.ele('SystemUnitClass', {
    Name: 'FPD_State',
    RefBaseClassPath: 'FPD_SystemUnitClassLib/FPD_Object',
  }).ele('Version').txt('1.0.0').up().up();

  // Concrete states inheriting from FPD_State
  for (const name of ['FPD_Product', 'FPD_Information', 'FPD_Energy']) {
    sucl.ele('SystemUnitClass', {
      Name: name,
      RefBaseClassPath: 'FPD_SystemUnitClassLib/FPD_State',
    }).ele('Version').txt('1.0.0').up().up();
  }

  // FPD_Process (contains a default SystemLimit child)
  const proc = sucl.ele('SystemUnitClass', { Name: 'FPD_Process' })
    .ele('Version').txt('1.0.0').up();
  proc.ele('InternalElement', {
    Name: 'SystemLimit',
    RefBaseSystemUnitPath: 'FPD_SystemUnitClassLib/FPD_SystemLimit',
  });
  proc.up();

  // ── 3. AttributeTypeLib ───────────────────────────────────────────────
  const atl = root.ele('AttributeTypeLib', { Name: 'FPD_AttributeTypeLib' })
    .ele('Version').txt('1.0.0').up();

  // FPD_Identification
  const ident = atl.ele('AttributeType', {
    Name: 'FPD_Identification',
    AttributeDataType: 'xs:string',
  }).ele('Version').txt('1.0.0').up();
  for (const f of ['uniqueIdent', 'longName', 'shortName', 'versionNumber', 'revisionNumber']) {
    ident.ele('Attribute', { Name: f, AttributeDataType: 'xs:string' }).up();
  }
  ident.up();

  // FPD_Characteristic
  const charac = atl.ele('AttributeType', {
    Name: 'FPD_Characteristic',
    AttributeDataType: 'xs:string',
  }).ele('Version').txt('1.0.0').up();

  // Characteristic → Identification sub-attribute
  const cIdent = charac.ele('Attribute', {
    Name: 'Identification',
    AttributeDataType: 'xs:string',
    RefAttributeType: 'FPD_AttributeTypeLib/FPD_Identification',
  });
  for (const f of ['uniqueIdent', 'longName', 'shortName', 'versionNumber', 'revisionNumber']) {
    cIdent.ele('Attribute', { Name: f, AttributeDataType: 'xs:string' }).up();
  }
  cIdent.up();

  // Characteristic → DescriptiveElement
  const desc = charac.ele('Attribute', { Name: 'DescriptiveElement', AttributeDataType: 'xs:string' });
  for (const f of ['valueDeterminationProcess', 'representivity', 'setpointValue', 'validityLimits', 'actualValues']) {
    desc.ele('Attribute', { Name: f, AttributeDataType: 'xs:string' }).up();
  }
  desc.up();

  // Characteristic → RelationalElement
  const rel = charac.ele('Attribute', { Name: 'RelationalElement', AttributeDataType: 'xs:string' });
  for (const f of ['view', 'model', 'regulationsForRelationalGeneration']) {
    rel.ele('Attribute', { Name: f, AttributeDataType: 'xs:string' }).up();
  }
  rel.up();
  charac.up();

  // ── 4. VisualAttributeTypeLib ─────────────────────────────────────────
  const vatl = root.ele('AttributeTypeLib', { Name: 'FPD_VisualAttributeTypeLib' })
    .ele('Version').txt('1.0.0').up();

  // FPD_ElementVisual
  const ev = vatl.ele('AttributeType', {
    Name: 'FPD_ElementVisual',
    AttributeDataType: 'xs:string',
  }).ele('Version').txt('1.0.0').up();
  addCoordinateAttr(ev, 'position', 'FPD_VisualAttributeTypeLib/FPD_Coordinate');
  ev.ele('Attribute', { Name: 'width', AttributeDataType: 'xs:double' }).up();
  ev.ele('Attribute', { Name: 'height', AttributeDataType: 'xs:double' }).up();
  ev.up();

  // FPD_Waypoint
  const wp = vatl.ele('AttributeType', {
    Name: 'FPD_Waypoint',
    AttributeDataType: 'xs:string',
  }).ele('Version').txt('1.0.0').up();
  addCoordinateAttr(wp, 'position', 'FPD_VisualAttributeTypeLib/FPD_Coordinate');
  wp.up();

  // FPD_Coordinate
  const coord = vatl.ele('AttributeType', {
    Name: 'FPD_Coordinate',
    AttributeDataType: 'xs:string',
  }).ele('Version').txt('1.0.0').up();
  coord.ele('Attribute', { Name: 'x', AttributeDataType: 'xs:double' }).up();
  coord.ele('Attribute', { Name: 'y', AttributeDataType: 'xs:double' }).up();
  coord.up();
}

// ── Helpers ───────────────────────────────────────────────────────────────

function addCoordinateAttr(parent, name, refType) {
  const attr = parent.ele('Attribute', {
    Name: name,
    AttributeDataType: 'xs:string',
    RefAttributeType: refType,
  });
  attr.ele('Attribute', { Name: 'x', AttributeDataType: 'xs:double' }).up();
  attr.ele('Attribute', { Name: 'y', AttributeDataType: 'xs:double' }).up();
  attr.up();
}

function addIdentificationAttr(parent) {
  const attr = parent.ele('Attribute', {
    Name: 'Identification',
    AttributeDataType: 'xs:string',
    RefAttributeType: 'FPD_AttributeTypeLib/FPD_Identification',
  });
  for (const f of ['uniqueIdent', 'longName', 'shortName', 'versionNumber', 'revisionNumber']) {
    attr.ele('Attribute', { Name: f, AttributeDataType: 'xs:string' }).up();
  }
  attr.up();
}

function addVisualAttr(parent) {
  const attr = parent.ele('Attribute', {
    Name: 'Visual',
    AttributeDataType: 'xs:string',
    RefAttributeType: 'FPD_VisualAttributeTypeLib/FPD_ElementVisual',
  });
  addCoordinateAttr(attr, 'position', 'FPD_VisualAttributeTypeLib/FPD_Coordinate');
  attr.ele('Attribute', { Name: 'width', AttributeDataType: 'xs:double' }).up();
  attr.ele('Attribute', { Name: 'height', AttributeDataType: 'xs:double' }).up();
  attr.up();
}
