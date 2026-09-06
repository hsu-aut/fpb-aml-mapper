using Aml.Engine.CAEX;
using Aml.Engine.CAEX.Extensions;
using FpbMapper.Conversion;

namespace FpbMapper.Tests;

/// <summary>
/// The FPD library types its reference attributes with the official
/// AutomationML_ObjectReferences_AttributeTypeLib (v1.1.1-beta) by default:
/// refProcess → refDetailObj, sub-process refObj → refAbstractObj, boundary
/// state refObj → refBaseObj. These tests pin the emitted library shape, the
/// ExternalReference, the inheritance onto instances, and the legacy opt-out.
/// </summary>
public class ObjectReferencesLibraryTests
{
    private static string LoadTestData(string name) =>
        File.ReadAllText(Path.Combine("TestData", name));

    private static CAEXDocument Convert(MapperOptions? options = null) =>
        FpbJsonToCaex.Convert(LoadTestData("Temperieren.json"), options).Value!;

    private static string? RefType(CAEXDocument doc, string suc, string attr) =>
        doc.CAEXFile.SystemUnitClassLib[FpbMappings.LibNames.SystemUnitClassLib]!
            .SystemUnitClass[suc]!.Attribute[attr]!.RefAttributeType;

    [Fact]
    public void Default_EmitsExternalReferenceToOfficialLibrary()
    {
        var doc = Convert();
        var er = doc.CAEXFile.ExternalReference.FirstOrDefault(e => e.Alias == ObjectReferencesLibrary.Alias);
        Assert.NotNull(er);
        Assert.Equal(ObjectReferencesLibrary.FileName, er!.Path);
    }

    [Fact]
    public void Default_DeclaresNoLocalRefObjType()
    {
        var doc = Convert();
        var atl = doc.CAEXFile.AttributeTypeLib[FpbMappings.LibNames.AttributeTypeLib]!;
        Assert.Null(atl.AttributeType["refObj"]);
    }

    [Fact]
    public void Default_TypesTheThreeReferenceAttributesWithSpecialisedTypes()
    {
        var doc = Convert();
        Assert.Equal(ObjectReferencesLibrary.RefDetailObjAttributeTypePath,   RefType(doc, "FPD_ProcessOperator", "refProcess"));
        Assert.Equal(ObjectReferencesLibrary.RefAbstractObjAttributeTypePath, RefType(doc, "FPD_Process", "refObj"));
        Assert.Equal(ObjectReferencesLibrary.RefBaseObjAttributeTypePath,     RefType(doc, "FPD_State", "refObj"));

        var rcl = doc.CAEXFile.RoleClassLib[FpbMappings.LibNames.RoleClassLib]!;
        Assert.Equal(ObjectReferencesLibrary.RefDetailObjAttributeTypePath,   rcl.RoleClass["FPD_ProcessOperator"]!.Attribute["refProcess"]!.RefAttributeType);
        Assert.Equal(ObjectReferencesLibrary.RefAbstractObjAttributeTypePath, rcl.RoleClass["FPD_Process"]!.Attribute["refObj"]!.RefAttributeType);
        Assert.Equal(ObjectReferencesLibrary.RefBaseObjAttributeTypePath,     rcl.RoleClass["FPD_State"]!.Attribute["refObj"]!.RefAttributeType);
    }

    [Fact]
    public void Default_InstancesInheritTypeAndDataType()
    {
        var doc = Convert();
        var ih = doc.CAEXFile.InstanceHierarchy.First();
        var subProcess = ih.Descendants<InternalElementType>()
            .First(ie => FpbMappings.StripAlias(ie.RefBaseSystemUnitPath).EndsWith("/FPD_Process")
                         && !string.IsNullOrEmpty(ie.Attribute["refObj"]?.Value));
        var refObj = subProcess.Attribute["refObj"]!;
        Assert.Equal(ObjectReferencesLibrary.RefAbstractObjAttributeTypePath, refObj.RefAttributeType);
        Assert.Equal("xs:IDREF", refObj.AttributeDataType);

        var po = ih.Descendants<InternalElementType>()
            .First(ie => FpbMappings.StripAlias(ie.RefBaseSystemUnitPath).EndsWith("/FPD_ProcessOperator")
                         && !string.IsNullOrEmpty(ie.Attribute["refProcess"]?.Value));
        Assert.Equal(ObjectReferencesLibrary.RefDetailObjAttributeTypePath, po.Attribute["refProcess"]!.RefAttributeType);

        var stateSucs = new[] { "/FPD_Product", "/FPD_Energy", "/FPD_Information" };
        var boundary = ih.Descendants<InternalElementType>()
            .First(ie => stateSucs.Any(s => FpbMappings.StripAlias(ie.RefBaseSystemUnitPath).EndsWith(s))
                         && !string.IsNullOrEmpty(ie.Attribute["refObj"]?.Value));
        Assert.Equal(ObjectReferencesLibrary.RefBaseObjAttributeTypePath, boundary.Attribute["refObj"]!.RefAttributeType);
    }

    [Fact]
    public void Default_RoundTripIsUnaffected()
    {
        var doc = Convert();
        var back = CaexToFpbJson.Convert(doc);
        Assert.NotNull(back.Value);
        Assert.Contains("decomposedView", back.Value!);
    }

    [Fact]
    public void Legacy_KeepsLocalRefObjTypeAndNoExternalReference()
    {
        var doc = Convert(new MapperOptions { UseObjectReferencesLibrary = false });
        Assert.DoesNotContain(doc.CAEXFile.ExternalReference, e => e.Alias == ObjectReferencesLibrary.Alias);
        var atl = doc.CAEXFile.AttributeTypeLib[FpbMappings.LibNames.AttributeTypeLib]!;
        Assert.NotNull(atl.AttributeType["refObj"]);
        Assert.Equal(FpbMappings.AttrRefs.RefObj, RefType(doc, "FPD_ProcessOperator", "refProcess"));
        Assert.Equal(FpbMappings.AttrRefs.RefObj, RefType(doc, "FPD_Process", "refObj"));
        Assert.Equal(FpbMappings.AttrRefs.RefObj, RefType(doc, "FPD_State", "refObj"));
    }

    /// <summary>
    /// UpdateInPlace on a document that still carries the v0.5 libraries must
    /// not inject the new layout next to the old one: EnsureLibraries is a
    /// no-op when the libraries exist, so new instances follow the document.
    /// </summary>
    [Fact]
    public void UpdateInPlace_OnLegacyDocument_KeepsLegacyLayout()
    {
        var legacy = Convert(new MapperOptions { UseObjectReferencesLibrary = false });
        var result = FpbJsonToCaex.UpdateInPlace(legacy, LoadTestData("Temperieren.json"));
        Assert.NotNull(result.Value);
        var atl = result.Value!.CAEXFile.AttributeTypeLib[FpbMappings.LibNames.AttributeTypeLib]!;
        Assert.NotNull(atl.AttributeType["refObj"]);
        Assert.DoesNotContain(result.Value.CAEXFile.ExternalReference, e => e.Alias == ObjectReferencesLibrary.Alias);
    }
}
