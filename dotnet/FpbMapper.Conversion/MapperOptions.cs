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
    /// When false (default), the FPD library defines its own <c>refObj</c>
    /// AttributeType under <c>VDI_FPD_AttributeTypeLib</c>.
    ///
    /// When true, the mapper expects to find the broader Object-References
    /// framework from the ETFA 2026 paper (refObj, refBaseObj, refExtendedObj,
    /// refComposedObj) in the central
    /// <c>AutomationML_ObjectReferences_AttributeTypeLib</c> and references
    /// THAT for the refObj attribute on FPD_Process instead of declaring its
    /// own. The actual ExternalReference + library detection lands in a later
    /// phase; this flag is the configuration anchor.
    /// </summary>
    public bool UseObjectReferencesLibrary { get; init; } = false;

    /// <summary>
    /// Override the AttributeTypeLib path the mapper uses when emitting the
    /// refObj attribute on a sub-process. Leave null to use the canonical path
    /// (VDI-internal when <see cref="UseObjectReferencesLibrary"/> is false,
    /// central library when true).
    /// </summary>
    public string? RefObjAttributeTypePath { get; init; } = null;

    /// <summary>
    /// Resolve the actual path the mapper should stamp on the refObj attribute
    /// of a FPD_Process IE, honouring the override above when set.
    /// </summary>
    public string EffectiveRefObjAttributeTypePath
        => RefObjAttributeTypePath
           ?? (UseObjectReferencesLibrary
               ? ObjectReferencesLibrary.RefObjAttributeTypePath
               : FpbMappings.AttrRefs.RefObj);

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
/// Constants for the central
/// <c>AutomationML_ObjectReferences_AttributeTypeLib</c> from the ETFA 2026
/// paper. The library itself is shipped as a separate AML asset; this class
/// holds the paths the mapper needs to reference from inside an FPD document
/// when <see cref="MapperOptions.UseObjectReferencesLibrary"/> is enabled.
///
/// Phase-2 follow-up (tracked in plugin-robustness-todo): emit an
/// ExternalReference to that asset from the FPD CAEXFile so consumers can
/// resolve the paths without extra setup.
/// </summary>
public static class ObjectReferencesLibrary
{
    public const string LibName = "AutomationML_ObjectReferences_AttributeTypeLib";

    public const string RefObjAttributeTypePath         = LibName + "/refObj";
    public const string RefBaseObjAttributeTypePath     = LibName + "/refBaseObj";
    public const string RefExtendedObjAttributeTypePath = LibName + "/refExtendedObj";
    public const string RefComposedObjAttributeTypePath = LibName + "/refComposedObj";
}
