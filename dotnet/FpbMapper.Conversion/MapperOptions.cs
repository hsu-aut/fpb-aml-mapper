namespace FpbMapper.Conversion;

/// <summary>
/// Cross-cutting configuration the conversion routines (Convert / ImportInto /
/// UpdateInPlace) consult for behaviour that is expected to evolve with the
/// VDI 3682 standard or with the AutomationML library landscape.
///
/// All options have safe defaults that preserve the v0.5.1 behaviour, so
/// existing callers (and the AML editor plugin) keep working without changes.
/// New options should be added here with backward-compatible defaults; callers
/// opt in explicitly.
/// </summary>
public sealed class MapperOptions
{
    /// <summary>
    /// The default options. Same singleton everywhere so cheap equality checks
    /// for "did the caller customise anything" remain valid.
    /// </summary>
    public static readonly MapperOptions Default = new();

    /// <summary>
    /// When true (default since library version 1.1.0), the FPD library types
    /// its three reference attributes with the official
    /// <c>AutomationML_ObjectReferences_AttributeTypeLib</c> (AutomationML e.V.,
    /// v1.1.1-beta) instead of a locally declared generic <c>refObj</c>:
    /// <list type="bullet">
    ///   <item><c>refProcess</c> on a process operator → <c>refDetailObj</c>
    ///         (the sub-process is the more detailed representation)</item>
    ///   <item><c>refObj</c> on a sub-process → <c>refAbstractObj</c>
    ///         (reverse direction, back to the operator)</item>
    ///   <item><c>refObj</c> on a boundary state → <c>refBaseObj</c>
    ///         (same logical object as the top-level state)</item>
    /// </list>
    /// The attribute NAMES stay <c>refObj</c> / <c>refProcess</c>; only the
    /// RefAttributeType changes. The official library is pulled in through an
    /// ExternalReference (alias <see cref="ObjectReferencesLibrary.Alias"/>),
    /// it is not embedded.
    ///
    /// When false, the FPD library declares its own generic <c>refObj</c>
    /// AttributeType under <c>VDI_FPD_AttributeTypeLib</c> (v0.5 layout as
    /// published in the ETFA 2026 paper).
    /// </summary>
    public bool UseObjectReferencesLibrary { get; init; } = true;

    /// <summary>
    /// Override the AttributeTypeLib path stamped on ALL three FPD reference
    /// attributes. Leave null to use the canonical paths (specialised official
    /// types when <see cref="UseObjectReferencesLibrary"/> is true, the
    /// VDI-internal generic type when false).
    /// </summary>
    public string? RefObjAttributeTypePath { get; init; } = null;

    /// <summary>Path of the generic base type (refObj) in the active layout.</summary>
    public string EffectiveRefObjAttributeTypePath
        => RefObjAttributeTypePath
           ?? (UseObjectReferencesLibrary
               ? ObjectReferencesLibrary.RefObjAttributeTypePath
               : FpbMappings.AttrRefs.RefObj);

    /// <summary>Type of <c>refProcess</c> on FPD_ProcessOperator (operator → sub-process).</summary>
    public string EffectiveRefProcessAttributeTypePath
        => RefObjAttributeTypePath
           ?? (UseObjectReferencesLibrary
               ? ObjectReferencesLibrary.RefDetailObjAttributeTypePath
               : FpbMappings.AttrRefs.RefObj);

    /// <summary>Type of <c>refObj</c> on FPD_Process (sub-process → parent operator).</summary>
    public string EffectiveSubProcessRefObjAttributeTypePath
        => RefObjAttributeTypePath
           ?? (UseObjectReferencesLibrary
               ? ObjectReferencesLibrary.RefAbstractObjAttributeTypePath
               : FpbMappings.AttrRefs.RefObj);

    /// <summary>Type of <c>refObj</c> on FPD_State (boundary state → top-level original).</summary>
    public string EffectiveBoundaryStateRefObjAttributeTypePath
        => RefObjAttributeTypePath
           ?? (UseObjectReferencesLibrary
               ? ObjectReferencesLibrary.RefBaseObjAttributeTypePath
               : FpbMappings.AttrRefs.RefObj);

    /// <summary>
    /// AttributeDataType of the reference attributes. The official library types
    /// them as xs:IDREF; the legacy VDI-internal layout used xs:string.
    /// </summary>
    public string ReferenceAttributeDataType
        => UseObjectReferencesLibrary ? "xs:IDREF" : "xs:string";

    /// <summary>
    /// Optional fine-grained trace callback. The mapper invokes it twice for
    /// every interesting operation: once with <c>attempt:</c> describing what
    /// it's about to do, once with <c>result:</c> describing what happened
    /// (success, skip with reason, or failure). Suitable for piping to a
    /// per-line plugin log so a user can reconstruct exactly what the mapper
    /// did during a single UpdateInPlace pass.
    ///
    /// Set to null (default) to disable tracing — the calls are then
    /// near-free because <see cref="MapperTrace"/> short-circuits on null.
    /// </summary>
    public Action<string>? Trace { get; init; } = null;
}

/// <summary>
/// Helpers around <see cref="MapperOptions.Trace"/>. Centralised so call sites
/// stay readable and so adding a verbosity gate later is a single-file edit.
/// </summary>
internal static class MapperTrace
{
    public static void Attempt(MapperOptions? options, string operation, string details) =>
        options?.Trace?.Invoke($"attempt: {operation} — {details}");

    public static void Result(MapperOptions? options, string operation, string outcome) =>
        options?.Trace?.Invoke($"result:  {operation} — {outcome}");

    public static void Info(MapperOptions? options, string message) =>
        options?.Trace?.Invoke($"info:    {message}");
}

/// <summary>
/// Constants for the official
/// <c>AutomationML_ObjectReferences_AttributeTypeLib</c> released by the
/// AutomationML e.V. (v1.1.1-beta; the published successor of the four-type
/// framework proposed in the ETFA 2026 paper). The library is a separate AML
/// asset and is referenced through an ExternalReference with alias
/// <see cref="Alias"/>; all paths below are alias-qualified accordingly.
/// </summary>
public static class ObjectReferencesLibrary
{
    public const string LibName  = "AutomationML_ObjectReferences_AttributeTypeLib";
    public const string Version  = "1.1.1-beta";
    public const string FileName = "AutomationML_ObjectReferences_AttributeTypeLib_AMLEd2_1.1.1-beta.aml";
    public const string Alias    = "ObjectReferences";

    /// <summary>Alias-qualified library name as used in RefAttributeType paths.</summary>
    public const string QualifiedLibName = Alias + "@" + LibName;

    public const string RefObjAttributeTypePath         = QualifiedLibName + "/refObj";
    public const string RefBaseObjAttributeTypePath     = QualifiedLibName + "/refBaseObj";
    public const string RefAspectObjAttributeTypePath   = QualifiedLibName + "/refAspectObj";
    public const string RefDetailObjAttributeTypePath   = QualifiedLibName + "/refDetailObj";
    public const string RefAbstractObjAttributeTypePath = QualifiedLibName + "/refAbstractObj";
}
