using Aml.Engine.CAEX;
using FpbMapper.Conversion;

namespace FpbMapper.Tests;

/// <summary>
/// Documents that pull the FPD libraries in through an ExternalReference use
/// alias-qualified class paths ("VDI_FPD_DomainLibrary@VDI_FPD_SystemUnitClassLib/FPD_Process")
/// per IEC 62714. The mapper's mapping tables store the document-internal form.
/// These tests pin that both layouts convert identically.
/// </summary>
public class AliasToleranceTests
{
    private const string Alias = "VDI_FPD_DomainLibrary";

    private static string LoadTestData(string name) =>
        File.ReadAllText(Path.Combine("TestData", name));

    private static CAEXDocument BuildDoc()
    {
        var json = LoadTestData("Temperieren.json");
        return FpbJsonToCaex.Convert(json).Value;
    }

    private static void PrefixFpdPaths(CAEXDocument doc)
    {
        CaexElementWalker.WalkInternalElements(doc, ie =>
        {
            if (ie.RefBaseSystemUnitPath is { } suc
                && suc.StartsWith("VDI_FPD_", StringComparison.Ordinal))
                ie.RefBaseSystemUnitPath = Alias + "@" + suc;

            foreach (var extIf in ie.ExternalInterface)
            {
                if (extIf.RefBaseClassPath is { } icl
                    && icl.StartsWith("VDI_FPD_", StringComparison.Ordinal))
                    extIf.RefBaseClassPath = Alias + "@" + icl;
            }
        });
    }

    [Fact]
    public void StripAlias_HandlesAliasedUnaliasedAndEmpty()
    {
        Assert.Equal("VDI_FPD_SystemUnitClassLib/FPD_Process",
            FpbMappings.StripAlias("VDI_FPD_DomainLibrary@VDI_FPD_SystemUnitClassLib/FPD_Process"));
        Assert.Equal("VDI_FPD_SystemUnitClassLib/FPD_Process",
            FpbMappings.StripAlias("VDI_FPD_SystemUnitClassLib/FPD_Process"));
        Assert.Equal(string.Empty, FpbMappings.StripAlias(null));
        Assert.Equal(string.Empty, FpbMappings.StripAlias(""));
    }

    [Fact]
    public void FindFpdInstanceHierarchies_RecognizesAliasQualifiedPaths()
    {
        var doc = BuildDoc();
        PrefixFpdPaths(doc);

        var found = CaexToFpbJson.FindFpdInstanceHierarchies(doc);

        Assert.Single(found);
    }

    [Fact]
    public void Convert_AliasQualifiedDocument_MatchesUnaliasedResult()
    {
        // Same document before and after alias rewriting, so generated IDs match
        // and the JSON outputs are comparable as strings.
        var doc = BuildDoc();
        var baselineIh = Assert.Single(CaexToFpbJson.FindFpdInstanceHierarchies(doc));
        var baselineJson = CaexToFpbJson.Convert(doc, baselineIh).Value;

        PrefixFpdPaths(doc);
        var aliasedIh = Assert.Single(CaexToFpbJson.FindFpdInstanceHierarchies(doc));
        var aliasedJson = CaexToFpbJson.Convert(doc, aliasedIh).Value;

        Assert.Equal(baselineJson, aliasedJson);
    }
}
