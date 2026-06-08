namespace FpbMapper.Conversion;

/// <summary>
/// Names and field list for the VDI 3682 <c>Identification</c> compound
/// attribute. Centralised so that a future revision of the standard (e.g.
/// adding a <c>fingerprint</c> field, or renaming <c>uniqueIdent</c>) is a
/// single-file edit instead of a hunt through both mappers, the library
/// generator, and the validator.
/// </summary>
public static class IdentificationSchema
{
    /// <summary>The compound attribute's own name on the IE.</summary>
    public const string AttributeName = "Identification";

    public const string UniqueIdent    = "uniqueIdent";
    public const string LongName       = "longName";
    public const string ShortName      = "shortName";
    public const string VersionNumber  = "versionNumber";
    public const string RevisionNumber = "revisionNumber";

    /// <summary>
    /// All fields defined by the current VDI 3682 Bild 4 schema, in the order
    /// the library generator should emit them. Library-version migrations add
    /// at the end so existing tooling continues to find earlier fields by name.
    /// </summary>
    public static readonly IReadOnlyList<string> Fields = new[]
    {
        UniqueIdent, LongName, ShortName, VersionNumber, RevisionNumber,
    };
}
