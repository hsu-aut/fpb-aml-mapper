using System.Reflection;

namespace FpbMapper.Conversion;

/// <summary>
/// Loader for the embedded FPD validation rule set (OCL).
///
/// The rule files ship as embedded resources inside FpbMapper.Conversion so
/// every consumer — the AML Editor Plugin, the web backend on aml.fpbjs.net,
/// or any CI pipeline that references the library — runs against the exact
/// same constraints. The strings are loaded once and cached for the lifetime
/// of the process.
///
/// The rule set is informed by VDI 3682 Parts 1 and 2 plus tool-level
/// conventions for the AML/CAEX serialization. It is the implementation
/// profile this project uses for validation — not a normative document.
/// </summary>
public static class FpbValidationRules
{
    private const string ResourcePrefix = "FpbMapper.Conversion.Rules.";

    private static readonly Lazy<string> _pureRules =
        new(() => LoadResource("vdi3682-pure-rules.ocl"));
    private static readonly Lazy<string> _helpers =
        new(() => LoadResource("vdi3682-helpers.ocl"));
    private static readonly Lazy<string> _phase2Rules =
        new(() => LoadResource("vdi3682-phase2-rules.ocl"));

    /// <summary>
    /// Phase-1 PURE rule set: the 26 structural constraints (cardinalities,
    /// connection typing, naming, reference resolution, semantics) that are
    /// executable against the CAEX serialization today.
    /// </summary>
    public static string PureRules => _pureRules.Value;

    /// <summary>
    /// OCL `def:` helper operations used by the spatial rules
    /// (bounds.isWithin, overlapsWith, isOnBorderOf, ...). Load alongside the
    /// pure rules so an OCL engine can resolve the helpers.
    /// </summary>
    public static string Helpers => _helpers.Value;

    /// <summary>
    /// Phase-2 rules covering FPD_Characteristic constraints. Not yet
    /// executable against the current CAEX binding; ship them so the engine
    /// can report "context type unknown" diagnostics rather than silently
    /// skipping them.
    /// </summary>
    public static string Phase2Rules => _phase2Rules.Value;

    private static string LoadResource(string fileName)
    {
        var resourceName = ResourcePrefix + fileName;
        var assembly = typeof(FpbValidationRules).Assembly;
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException(
                $"Embedded rule file '{resourceName}' not found in {assembly.GetName().Name}.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
