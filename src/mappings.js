// Type mappings between FPB.JS and AutomationML

// Element type → AML SystemUnitClass path
export const ELEMENT_TO_SUC = {
  'fpb:Product':           'FPD_SystemUnitClassLib/FPD_Product',
  'fpb:Energy':            'FPD_SystemUnitClassLib/FPD_Energy',
  'fpb:Information':       'FPD_SystemUnitClassLib/FPD_Information',
  'fpb:ProcessOperator':   'FPD_SystemUnitClassLib/FPD_ProcessOperator',
  'fpb:TechnicalResource': 'FPD_SystemUnitClassLib/FPD_TechnicalResource',
  'fpb:SystemLimit':       'FPD_SystemUnitClassLib/FPD_SystemLimit',
  'fpb:Process':           'FPD_SystemUnitClassLib/FPD_Process',
};

// Reverse: AML SUC path → FPB.JS type
export const SUC_TO_ELEMENT = Object.fromEntries(
  Object.entries(ELEMENT_TO_SUC).map(([k, v]) => [v, k])
);

// Flow type → AML InterfaceClass paths (Out + In)
export const FLOW_TO_INTERFACE = {
  'fpb:Flow':            { out: 'FPD_InterfaceClassLib/FPD_FlowOut',            in: 'FPD_InterfaceClassLib/FPD_FlowIn' },
  'fpb:ParallelFlow':    { out: 'FPD_InterfaceClassLib/FPD_ParallelFlowOut',    in: 'FPD_InterfaceClassLib/FPD_ParallelFlowIn' },
  'fpb:AlternativeFlow': { out: 'FPD_InterfaceClassLib/FPD_AlternativeFlowOut', in: 'FPD_InterfaceClassLib/FPD_AlternativeFlowIn' },
  'fpb:Usage':           { out: 'FPD_InterfaceClassLib/FPD_Usage',              in: 'FPD_InterfaceClassLib/FPD_Usage' },
};

// Reverse: AML InterfaceClass path → { flowType, direction }
export const INTERFACE_TO_FLOW = {};
for (const [flowType, paths] of Object.entries(FLOW_TO_INTERFACE)) {
  INTERFACE_TO_FLOW[paths.out] = { flowType, direction: 'out' };
  if (paths.in !== paths.out) {
    INTERFACE_TO_FLOW[paths.in] = { flowType, direction: 'in' };
  }
}

// Element types that are "objects" (have Identification + Characteristics + Visual)
export const OBJECT_TYPES = new Set([
  'fpb:Product', 'fpb:Energy', 'fpb:Information',
  'fpb:ProcessOperator', 'fpb:TechnicalResource', 'fpb:SystemLimit',
]);

// Element types that are connections (have sourceRef + targetRef)
export const CONNECTION_TYPES = new Set([
  'fpb:Flow', 'fpb:ParallelFlow', 'fpb:AlternativeFlow', 'fpb:Usage',
]);

// AML AttributeType references
export const ATTR_REFS = {
  identification: 'FPD_AttributeTypeLib/FPD_Identification',
  characteristic: 'FPD_AttributeTypeLib/FPD_Characteristic',
  elementVisual:  'FPD_VisualAttributeTypeLib/FPD_ElementVisual',
  coordinate:     'FPD_VisualAttributeTypeLib/FPD_Coordinate',
  waypoint:       'FPD_VisualAttributeTypeLib/FPD_Waypoint',
};
