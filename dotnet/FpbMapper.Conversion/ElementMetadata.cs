namespace FpbMapper.Conversion;

/// <summary>
/// Per-element-type behaviour flags. Today the mapper consults these flags
/// instead of doing scattered <c>fpbType == "fpb:X"</c> comparisons; adding a
/// future VDI 3682 element type is one new row here plus the corresponding
/// entry in <see cref="FpbMappings.ElementToSuc"/>.
///
/// The behaviour fields cover the questions the mapper actually asks during a
/// CAEX→JSON walk: is this element a state, an object, a connection, does it
/// belong inside the SystemLimit's elementsContainer, can it be decomposed,
/// does it live OUTSIDE the SystemLimit (the TechnicalResource quirk)?
/// </summary>
public sealed class ElementMetadata
{
    public string FpbType { get; init; } = "";
    public bool IsState { get; init; }
    public bool IsObject { get; init; }
    public bool IsConnection { get; init; }
    public bool CanBeDecomposed { get; init; }
    /// <summary>
    /// True for elements that live OUTSIDE the SystemLimit on the canvas and
    /// must therefore NOT be added to the SystemLimit's elementsContainer.
    /// Today only fpb:TechnicalResource has this trait.
    /// </summary>
    public bool LivesOutsideSystemLimit { get; init; }
}

public static class ElementMetadataRegistry
{
    public static readonly IReadOnlyDictionary<string, ElementMetadata> ByFpbType =
        new Dictionary<string, ElementMetadata>
        {
            [FpbTypes.Product] = new()
            {
                FpbType = FpbTypes.Product, IsState = true, IsObject = true,
            },
            [FpbTypes.Energy] = new()
            {
                FpbType = FpbTypes.Energy, IsState = true, IsObject = true,
            },
            [FpbTypes.Information] = new()
            {
                FpbType = FpbTypes.Information, IsState = true, IsObject = true,
            },
            [FpbTypes.ProcessOperator] = new()
            {
                FpbType = FpbTypes.ProcessOperator, IsObject = true, CanBeDecomposed = true,
            },
            [FpbTypes.TechnicalResource] = new()
            {
                FpbType = FpbTypes.TechnicalResource, IsObject = true,
                LivesOutsideSystemLimit = true,
            },
            [FpbTypes.SystemLimit] = new()
            {
                FpbType = FpbTypes.SystemLimit, IsObject = true,
            },

            [FpbTypes.Flow]            = new() { FpbType = FpbTypes.Flow,            IsConnection = true },
            [FpbTypes.ParallelFlow]    = new() { FpbType = FpbTypes.ParallelFlow,    IsConnection = true },
            [FpbTypes.AlternativeFlow] = new() { FpbType = FpbTypes.AlternativeFlow, IsConnection = true },
            [FpbTypes.Usage]           = new() { FpbType = FpbTypes.Usage,           IsConnection = true },
        };

    public static ElementMetadata? Get(string fpbType) =>
        ByFpbType.TryGetValue(fpbType, out var m) ? m : null;
}
