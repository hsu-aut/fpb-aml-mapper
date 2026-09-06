using Aml.Engine.CAEX;
using Aml.Engine.CAEX.Extensions;
using FpbMapper.Conversion;

namespace FpbMapper.Tests;

/// <summary>
/// The FPD library types its ViewInformation / PortCoordinate / Waypoint
/// attributes with the shared OMG_DD_AttributeTypeLib (DD_Bounds, DD_Point,
/// DD_Waypoint) instead of a language-specific DI library.
/// </summary>
public class DiagramInterchangeLibraryTests
{
    private static string LoadTestData(string name) =>
        File.ReadAllText(Path.Combine("TestData", name));

    [Fact]
    public void StandaloneDocument_ContainsTheThreeTypes()
    {
        var doc = DiagramInterchangeLibrary.BuildDocument();
        var atl = doc.CAEXFile.AttributeTypeLib[DiagramInterchangeLibrary.LibName];
        Assert.NotNull(atl);
        Assert.NotNull(atl!.AttributeType["DD_Bounds"]);
        Assert.NotNull(atl.AttributeType["DD_Point"]);
        Assert.NotNull(atl.AttributeType["DD_Waypoint"]);
        Assert.Equal(DiagramInterchangeLibrary.LibName + "/DD_Point", atl.AttributeType["DD_Bounds"]!.Attribute["position"]!.RefAttributeType);
    }

    [Fact]
    public void Convert_TypesViewInformationWithSharedLibrary()
    {
        var doc = FpbJsonToCaex.Convert(LoadTestData("Temperieren.json")).Value!;
        var caex = doc.CAEXFile;
        Assert.Contains(caex.ExternalReference, e => e.Alias == DiagramInterchangeLibrary.Alias && e.Path == DiagramInterchangeLibrary.FileName);
        Assert.Null(caex.AttributeTypeLib["VDI_FPD_DI_AttributeTypeLib"]);

        var suc = caex.SystemUnitClassLib[FpbMappings.LibNames.SystemUnitClassLib]!.SystemUnitClass["FPD_Object"]!;
        Assert.Equal(DiagramInterchangeLibrary.BoundsAttributeTypePath, suc.Attribute["ViewInformation"]!.RefAttributeType);
        Assert.Equal(DiagramInterchangeLibrary.PointAttributeTypePath, suc.Attribute["ViewInformation"]!.Attribute["position"]!.RefAttributeType);

        var anyIe = caex.InstanceHierarchy.First().Descendants<InternalElementType>()
            .First(ie => ie.Attribute["ViewInformation"] != null);
        Assert.Equal(DiagramInterchangeLibrary.BoundsAttributeTypePath, anyIe.Attribute["ViewInformation"]!.RefAttributeType);
    }

    [Fact]
    public void RoundTrip_KeepsPositions()
    {
        var doc = FpbJsonToCaex.Convert(LoadTestData("Temperieren.json")).Value!;
        var back = CaexToFpbJson.Convert(doc);
        Assert.NotNull(back.Value);
        Assert.Contains("elementVisualInformation", back.Value!);
    }
}
