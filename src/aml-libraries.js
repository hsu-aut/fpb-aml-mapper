// AML Library definitions matching VDI3682_Lib_v0.1 RD.aml (Nabizada corrections)
// These are embedded statically in every generated AML file.

/**
 * Build the FPD library branches using xmlbuilder2 API.
 * @param {object} root - the <CAEXFile> node (xmlbuilder2 element)
 */
function appendLibraries(root) {
  appendInterfaceClassLib(root);
  appendRoleClassLib(root);
  appendSystemUnitClassLib(root);
  appendAttributeTypeLib(root);
  appendDIAttributeTypeLib(root);
}

// ── 1. InterfaceClassLib ──────────────────────────────────────────────

function appendInterfaceClassLib(root) {
  const icl = root.ele('InterfaceClassLib', { Name: 'FPD_InterfaceClassLib' })
    .ele('Version').txt('1.0.0').up();

  // Base class: FPD_Port (carries PortCoordinate)
  const port = icl.ele('InterfaceClass', { Name: 'FPD_Port' })
    .ele('Version').txt('1.0.0').up();
  addPointAttr(port, 'PortCoordinate');
  port.up();

  // Flow / Usage interface classes (all inherit from FPD_Port)
  for (const name of [
    'FPD_FlowIn', 'FPD_FlowOut',
    'FPD_Usage',
    'FPD_ParallelFlowIn', 'FPD_ParallelFlowOut',
    'FPD_AlternativeFlowIn', 'FPD_AlternativeFlowOut',
  ]) {
    icl.ele('InterfaceClass', {
      Name: name,
      RefBaseClassPath: 'FPD_InterfaceClassLib/FPD_Port',
    }).ele('Version').txt('1.0.0').up().up();
  }
}

// ── 2. RoleClassLib (nested = inheritance) ────────────────────────────

function appendRoleClassLib(root) {
  const rcl = root.ele('RoleClassLib', { Name: 'FPD_RoleClassLib' })
    .ele('Version').txt('1.0.0').up()
    .ele('Description').txt('Semantic model of the FPD per VDI/VDE 3682. Nesting = inheritance.').up();

  // FPD_Object (abstract base)
  const obj = rcl.ele('RoleClass', { Name: 'FPD_Object' })
    .ele('Version').txt('1.0.0').up()
    .ele('Description').txt('Abstract base for all FPB objects (Part 1, p. 4: product, energy, information, process operator, technical resource).').up();
  addIdentificationAttr(obj);
  const charAttr = obj.ele('Attribute', { Name: 'Characteristics', AttributeDataType: 'xs:string' });
  charAttr.ele('Description').txt('Container for characteristics (Part 2, Fig. 3).').up();
  charAttr.up();
  addBoundsAttr(obj, 'ViewInformation');

  // FPD_State (inherits FPD_Object)
  const state = obj.ele('RoleClass', { Name: 'FPD_State' })
    .ele('Version').txt('1.0.0').up()
    .ele('Description').txt('Abstract state (Part 2, Fig. 2). Inherits Identification and Characteristics from FPD_Object.').up();
  addRefObjAttr(state, 'IDREF to the original state instance that this boundary state represents. Always points to the top-level original, regardless of decomposition depth.');

  // Concrete states
  for (const name of ['FPD_Product', 'FPD_Energy', 'FPD_Information']) {
    state.ele('RoleClass', { Name: name })
      .ele('Version').txt('1.0.0').up().up();
  }
  state.up();

  // FPD_ProcessOperator (inherits FPD_Object)
  const po = obj.ele('RoleClass', { Name: 'FPD_ProcessOperator' })
    .ele('Version').txt('1.0.0').up()
    .ele('Description').txt('Process operator (Part 2, Fig. 2). Inherits Identification and Characteristics from FPD_Object.').up();
  addRefObjAttr(po, 'IDREF to the child process that decomposes this operator. Empty if the operator is not further decomposed.');
  po.up();

  // FPD_TechnicalResource (inherits FPD_Object)
  obj.ele('RoleClass', { Name: 'FPD_TechnicalResource' })
    .ele('Version').txt('1.0.0').up()
    .ele('Description').txt('Technical resource (Part 1, p. 9). Located outside the system limit, associated via usage.').up()
    .up();

  obj.up();

  // FPD_SystemLimit (NOT under FPD_Object)
  const sl = rcl.ele('RoleClass', { Name: 'FPD_SystemLimit' })
    .ele('Version').txt('1.0.0').up()
    .ele('Description').txt('System limit (Part 1, p. 9). Peer aggregate of the process, not a container.').up();
  addIdentificationAttr(sl);
  addBoundsAttr(sl, 'ViewInformation');
  sl.up();

  // FPD_Process (NOT under FPD_Object)
  const proc = rcl.ele('RoleClass', { Name: 'FPD_Process' })
    .ele('Version').txt('1.0.0').up()
    .ele('Description').txt('Process (Part 2, Fig. 2). Aggregates states (2..*), system limit (1), and process operators (1..*).').up();
  addRefObjAttr(proc, 'IDREF to the parent process operator whose decomposition this process represents.');
  proc.up();
}

// ── 3. SystemUnitClassLib (flat, SupportedRoleClass only) ─────────────

function appendSystemUnitClassLib(root) {
  const sucl = root.ele('SystemUnitClassLib', { Name: 'FPD_SystemUnitClassLib' })
    .ele('Version').txt('1.0.0').up()
    .ele('Description').txt('Flat instantiation templates. No inheritance – semantics reside in the RoleClassLib.').up();

  const entries = [
    ['FPD_Product',           'FPD_RoleClassLib/FPD_Object/FPD_State/FPD_Product'],
    ['FPD_Energy',            'FPD_RoleClassLib/FPD_Object/FPD_State/FPD_Energy'],
    ['FPD_Information',       'FPD_RoleClassLib/FPD_Object/FPD_State/FPD_Information'],
    ['FPD_ProcessOperator',   'FPD_RoleClassLib/FPD_Object/FPD_ProcessOperator'],
    ['FPD_TechnicalResource', 'FPD_RoleClassLib/FPD_Object/FPD_TechnicalResource'],
    ['FPD_SystemLimit',       'FPD_RoleClassLib/FPD_SystemLimit'],
    ['FPD_Process',           'FPD_RoleClassLib/FPD_Process'],
  ];

  for (const [name, roleClassPath] of entries) {
    const suc = sucl.ele('SystemUnitClass', { Name: name })
      .ele('Version').txt('1.0.0').up();
    suc.ele('SupportedRoleClass', { RefRoleClassPath: roleClassPath }).up();
    suc.up();
  }
}

// ── 4. FPD_AttributeTypeLib ───────────────────────────────────────────

function appendAttributeTypeLib(root) {
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

  const cIdent = charac.ele('Attribute', {
    Name: 'Category',
    AttributeDataType: 'xs:string',
    RefAttributeType: 'FPD_AttributeTypeLib/FPD_Identification',
  });
  for (const f of ['uniqueIdent', 'longName', 'shortName', 'versionNumber', 'revisionNumber']) {
    cIdent.ele('Attribute', { Name: f, AttributeDataType: 'xs:string' }).up();
  }
  cIdent.up();

  const desc = charac.ele('Attribute', { Name: 'DescriptiveElement', AttributeDataType: 'xs:string' });
  for (const f of ['valueDeterminationProcess', 'representivity', 'setpointValue', 'validityLimits', 'actualValues']) {
    desc.ele('Attribute', { Name: f, AttributeDataType: 'xs:string' }).up();
  }
  desc.up();

  const rel = charac.ele('Attribute', { Name: 'RelationalElement', AttributeDataType: 'xs:string' });
  for (const f of ['view', 'model', 'regulationsForRelationalGeneration']) {
    rel.ele('Attribute', { Name: f, AttributeDataType: 'xs:string' }).up();
  }
  rel.up();
  charac.up();

  // refObj
  const refObjType = atl.ele('AttributeType', {
    Name: 'refObj',
    AttributeDataType: 'xs:string',
  }).ele('Version').txt('1.0.0').up();
  refObjType.ele('Description').txt('Generic IDREF attribute. Semantics depend on the carrying element (see RoleClassLib descriptions).').up();
  refObjType.up();
}

// ── 5. FPD_DI_AttributeTypeLib ────────────────────────────────────────

function appendDIAttributeTypeLib(root) {
  const diatl = root.ele('AttributeTypeLib', { Name: 'FPD_DI_AttributeTypeLib' })
    .ele('Version').txt('1.0.0').up()
    .ele('Description').txt('Diagram Interchange attributes, aligned with OMG DD/DI terminology (DC::Bounds, DC::Point, DI::Waypoint).').up();

  // FPD_Bounds
  const bounds = diatl.ele('AttributeType', {
    Name: 'FPD_Bounds',
    AttributeDataType: 'xs:string',
  }).ele('Version').txt('1.0.0').up()
    .ele('Description').txt('A rectangular area defined by a top-left (x, y) location and a size (width, height) along the x-y axes (cf. DC::Bounds).').up();
  addPointAttr(bounds, 'position');
  bounds.ele('Attribute', { Name: 'width', AttributeDataType: 'xs:double' }).up();
  bounds.ele('Attribute', { Name: 'height', AttributeDataType: 'xs:double' }).up();
  bounds.up();

  // FPD_Waypoint
  const wp = diatl.ele('AttributeType', {
    Name: 'FPD_Waypoint',
    AttributeDataType: 'xs:string',
  }).ele('Version').txt('1.0.0').up()
    .ele('Description').txt('A routing point along a connection path (cf. DI::Waypoint).').up();
  addPointAttr(wp, 'position');
  wp.up();

  // FPD_Point
  const pt = diatl.ele('AttributeType', {
    Name: 'FPD_Point',
    AttributeDataType: 'xs:string',
  }).ele('Version').txt('1.0.0').up()
    .ele('Description').txt('A two-dimensional point in a coordinate system (cf. DC::Point).').up();
  pt.ele('Attribute', { Name: 'x', AttributeDataType: 'xs:double' }).up();
  pt.ele('Attribute', { Name: 'y', AttributeDataType: 'xs:double' }).up();
  pt.up();
}

// ── Helpers ───────────────────────────────────────────────────────────

function addPointAttr(parent, name) {
  const attr = parent.ele('Attribute', {
    Name: name,
    AttributeDataType: 'xs:string',
    RefAttributeType: 'FPD_DI_AttributeTypeLib/FPD_Point',
  });
  attr.ele('Attribute', { Name: 'x', AttributeDataType: 'xs:double' }).up();
  attr.ele('Attribute', { Name: 'y', AttributeDataType: 'xs:double' }).up();
  attr.up();
}

function addBoundsAttr(parent, name) {
  const attr = parent.ele('Attribute', {
    Name: name,
    AttributeDataType: 'xs:string',
    RefAttributeType: 'FPD_DI_AttributeTypeLib/FPD_Bounds',
  });
  addPointAttr(attr, 'position');
  attr.ele('Attribute', { Name: 'width', AttributeDataType: 'xs:double' }).up();
  attr.ele('Attribute', { Name: 'height', AttributeDataType: 'xs:double' }).up();
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

function addRefObjAttr(parent, description) {
  const attr = parent.ele('Attribute', {
    Name: 'refObj',
    AttributeDataType: 'xs:string',
    RefAttributeType: 'FPD_AttributeTypeLib/refObj',
  });
  if (description) {
    attr.ele('Description').txt(description).up();
  }
  attr.up();
}

module.exports = { appendLibraries };
