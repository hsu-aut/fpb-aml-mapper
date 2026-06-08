namespace FpbMapper.Conversion;

/// <summary>
/// String constants for the FPB.js side of the mapping. These are the values
/// FPB.js emits as <c>$type</c> in its JSON output, so they appear in many
/// equality checks and dictionary lookups across the mapper.
///
/// Centralising them in one file gives compile-time safety for typos (a renamed
/// constant produces a compile error, a renamed string literal produces a
/// silent runtime mismatch) and a single point of edit when a new VDI 3682
/// revision adds a type. Aliases below also surface the canonical SUC path via
/// <see cref="FpbMappings.ElementToSuc"/> when needed.
/// </summary>
public static class FpbTypes
{
    // Container ──────────────────────────────────────────────────────────
    public const string Project     = "fpb:Project";        // FPB.js project root, not part of VDI 3682
    public const string Process     = "fpb:Process";        // VDI 3682 Bild 2 — top-level process container
    public const string SystemLimit = "fpb:SystemLimit";    // VDI 3682 Bild 2 — process boundary

    // State element types (subset of object types — VDI 3682 Bild 2, Tabelle 3)
    public const string Product     = "fpb:Product";        // Material state
    public const string Energy      = "fpb:Energy";         // Energy state
    public const string Information = "fpb:Information";    // Information state

    // Active and resource object types ───────────────────────────────────
    public const string ProcessOperator   = "fpb:ProcessOperator";    // VDI 3682 Bild 2 — transformation node
    public const string TechnicalResource = "fpb:TechnicalResource";  // VDI 3682 Bild 2 — resource outside SystemLimit

    // Connection (flow) types (VDI 3682 Bild 3, Tabelle 4) ───────────────
    public const string Flow            = "fpb:Flow";             // Sequential State↔PO transition
    public const string ParallelFlow    = "fpb:ParallelFlow";     // AND-split/-join
    public const string AlternativeFlow = "fpb:AlternativeFlow";  // XOR-split/-join
    public const string Usage           = "fpb:Usage";            // PO↔TR resource binding (symmetric)

    // Embedded helper types ──────────────────────────────────────────────
    public const string Identification = "fpb:Identification";  // VDI 3682 Bild 4 — object identification
}
