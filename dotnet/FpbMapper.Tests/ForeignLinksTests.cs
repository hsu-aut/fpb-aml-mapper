using System.Text.Json.Nodes;
using Aml.Engine.CAEX;
using FpbMapper.Conversion;

namespace FpbMapper.Tests;

/// <summary>
/// Which links of the file deleting an FPD element removes (016f E4, V19):
/// links from elsewhere into the element and links stored in it that point
/// outside, flows of the model excluded. The measure is what UpdateInPlace
/// removes from the document, not only its warnings.
/// </summary>
public class ForeignLinksTests
{
    private static string LoadTestData(string name) =>
        File.ReadAllText(Path.Combine("TestData", name));

    /// <summary>Temperieren as AML: the entry process and the layer of "Erhitzen".</summary>
    private sealed record Model(CAEXDocument Doc, InstanceHierarchyType Fpd, InternalElementType Top, InternalElementType Sub)
    {
        public InternalElementType InTop(string name) => Top.InternalElement.First(ie => ie.Name == name);
        public InternalElementType InSub(string name) => Sub.InternalElement.First(ie => ie.Name == name);
    }

    private static Model Temperieren()
    {
        var doc = FpbJsonToCaex.Convert(LoadTestData("Temperieren.json")).Value;
        var fpd = doc.CAEXFile.InstanceHierarchy.First();
        var top = fpd.InternalElement.First(ie => string.IsNullOrEmpty(ie.GetRefObjOrDerived()));
        var sub = fpd.InternalElement.First(ie => !string.IsNullOrEmpty(ie.GetRefObjOrDerived()));
        return new Model(doc, fpd, top, sub);
    }

    private static InternalElementType Tank(CAEXDocument doc)
    {
        var plant = doc.CAEXFile.InstanceHierarchy.Append("Plant");
        return plant.InternalElement.Append("Tank");
    }

    private static InternalLinkType Link(InternalElementType owner, string name, ExternalInterfaceType a, ExternalInterfaceType b)
    {
        var link = owner.InternalLink.Append(name);
        link.AInterface = a;
        link.BInterface = b;
        return link;
    }

    private static string Bare(string? id) => (id ?? "").Trim().TrimStart('{').TrimEnd('}');

    /// <summary>The links named for the element, as "link@owner", sorted.</summary>
    private static List<string> LinksOf(IReadOnlyList<ElementLinks> found, InternalElementType ie) =>
        found.Where(e => string.Equals(e.ElementId, Bare(ie.ID), StringComparison.OrdinalIgnoreCase))
             .SelectMany(e => e.Links.Select(l => l.Link + "@" + l.Owner))
             .OrderBy(s => s, StringComparer.Ordinal)
             .ToList();

    private static IEnumerable<string> AllNamed(IReadOnlyList<ElementLinks> found) =>
        found.SelectMany(e => e.Links.Select(l => l.Link));

    [Fact]
    public void ALinkFromThePlantIntoAStateIsNamedForTheState()
    {
        var m = Temperieren();
        var scrap = m.InTop("Ausschuss");
        var scrapIf = scrap.ExternalInterface.Append("PlantLink");
        var tank = Tank(m.Doc);
        var toScrap = tank.ExternalInterface.Append("ToScrap");
        var toNothing = tank.ExternalInterface.Append("ToNothing");
        Link(tank, "TankToScrap", toScrap, scrapIf);
        var legacy = tank.InternalLink.Append("TankToScrapLegacy");
        legacy.RefPartnerSideA = toScrap.ID;
        legacy.RefPartnerSideB = scrap.ID + ":PlantLink";
        var stale = tank.InternalLink.Append("TankToGone");
        stale.RefPartnerSideA = toNothing.ID;
        stale.RefPartnerSideB = "{00000000-0000-4000-8000-00000000dead}";

        var found = ForeignLinks.Find(m.Doc, m.Fpd);

        Assert.Equal(new[] { "TankToScrap@Tank", "TankToScrapLegacy@Tank" }, LinksOf(found, scrap));
        Assert.DoesNotContain("TankToGone", AllNamed(found));
    }

    [Fact]
    public void FlowsAreNeverNamed()
    {
        var m = Temperieren();

        Assert.Empty(ForeignLinks.Find(m.Doc, m.Fpd));
    }

    [Fact]
    public void ALinkStoredInAnOperatorThatPointsOutsideIsNamedForTheOperator()
    {
        var m = Temperieren();
        var check = m.InTop("Prüfen");
        var tank = Tank(m.Doc);
        Link(check, "OpToTank", check.ExternalInterface.First(), tank.ExternalInterface.Append("FromOp"));

        var found = ForeignLinks.Find(m.Doc, m.Fpd);

        Assert.Equal(new[] { "OpToTank@Prüfen" }, LinksOf(found, check));
        Assert.Single(AllNamed(found));
    }

    [Fact]
    public void ALinkWithBothSidesInsideTheElementIsNotNamed()
    {
        var m = Temperieren();
        var scrap = m.InTop("Ausschuss");
        var partA = scrap.InternalElement.Append("PartA").ExternalInterface.Append("Out");
        var partB = scrap.InternalElement.Append("PartB").ExternalInterface.Append("In");
        Link(scrap, "Inner", partA, partB);
        var tank = Tank(m.Doc);
        Link(tank, "TankToScrap", tank.ExternalInterface.Append("ToScrap"), scrap.ExternalInterface.Append("PlantLink"));

        var found = ForeignLinks.Find(m.Doc, m.Fpd);

        Assert.Equal(new[] { "TankToScrap@Tank" }, LinksOf(found, scrap));
    }

    [Fact]
    public void ALinkOntoAChildElementCountsForTheElement()
    {
        var m = Temperieren();
        var warm = m.InTop("Warmprodukt");
        var sensor = warm.InternalElement.Append("Sensor").ExternalInterface.Append("Out");
        var tank = Tank(m.Doc);
        Link(tank, "TankToSensor", tank.ExternalInterface.Append("ToSensor"), sensor);

        var found = ForeignLinks.Find(m.Doc, m.Fpd);

        Assert.Equal(new[] { "TankToSensor@Tank" }, LinksOf(found, warm));
    }

    [Fact]
    public void ACopyOfABoundaryStateHasItsOwnEntry()
    {
        var m = Temperieren();
        var warm = m.InTop("Warmprodukt");
        var copy = m.InSub("Warmprodukt");
        Assert.NotEqual(Bare(warm.ID), Bare(copy.ID));
        copy.ExternalInterface.Append("PlantLink");
        var tank = Tank(m.Doc);
        // CAEX 2.15 form "element id:interface name", as in files of other tools
        var legacy = tank.InternalLink.Append("TankToCopy");
        legacy.RefPartnerSideA = tank.ExternalInterface.Append("ToCopy").ID;
        legacy.RefPartnerSideB = copy.ID + ":PlantLink";

        var found = ForeignLinks.Find(m.Doc, m.Fpd);

        Assert.Equal(new[] { "TankToCopy@Tank" }, LinksOf(found, copy));
        Assert.Empty(LinksOf(found, warm));
    }

    [Fact]
    public void ALinkOntoTheProcessOfALowerLayerIsNamedForThatProcess()
    {
        var m = Temperieren();
        var tank = Tank(m.Doc);
        Link(tank, "TankToProcess", tank.ExternalInterface.Append("ToProcess"), m.Sub.ExternalInterface.Append("PlantLink"));

        var found = ForeignLinks.Find(m.Doc, m.Fpd);

        Assert.Equal(new[] { "TankToProcess@Tank" }, LinksOf(found, m.Sub));
        Assert.Single(AllNamed(found));
    }

    // ── against UpdateInPlace ────────────────────────────────────────────

    private static readonly string[] Added =
        { "TankToScrap", "TankToScrapLegacy", "TankToGone", "OpToTank", "Inner", "TankToSensor", "TankToCopy", "TankToProcess" };

    /// <summary>Links with both sides inside one element: they go with it as its content and are not named.</summary>
    private static readonly string[] Content = { "Inner" };

    /// <summary>Temperieren with every kind of link of the tests above.</summary>
    private static Model Rich()
    {
        var m = Temperieren();
        var tank = Tank(m.Doc);

        var scrap = m.InTop("Ausschuss");
        var toScrap = tank.ExternalInterface.Append("ToScrap");
        Link(tank, "TankToScrap", toScrap, scrap.ExternalInterface.Append("PlantLink"));
        var legacy = tank.InternalLink.Append("TankToScrapLegacy");
        legacy.RefPartnerSideA = toScrap.ID;
        legacy.RefPartnerSideB = scrap.ID + ":PlantLink";
        var stale = tank.InternalLink.Append("TankToGone");
        stale.RefPartnerSideA = tank.ExternalInterface.Append("ToNothing").ID;
        stale.RefPartnerSideB = "{00000000-0000-4000-8000-00000000dead}";

        var check = m.InTop("Prüfen");
        Link(check, "OpToTank", check.ExternalInterface.First(), tank.ExternalInterface.Append("FromOp"));

        var partA = scrap.InternalElement.Append("PartA").ExternalInterface.Append("Out");
        var partB = scrap.InternalElement.Append("PartB").ExternalInterface.Append("In");
        Link(scrap, "Inner", partA, partB);

        var warm = m.InTop("Warmprodukt");
        Link(tank, "TankToSensor", tank.ExternalInterface.Append("ToSensor"),
            warm.InternalElement.Append("Sensor").ExternalInterface.Append("Out"));

        var copy = m.InSub("Warmprodukt");
        copy.ExternalInterface.Append("PlantLink");
        var toCopy = tank.InternalLink.Append("TankToCopy");
        toCopy.RefPartnerSideA = tank.ExternalInterface.Append("ToCopy").ID;
        toCopy.RefPartnerSideB = copy.ID + ":PlantLink";

        Link(tank, "TankToProcess", tank.ExternalInterface.Append("ToProcess"), m.Sub.ExternalInterface.Append("PlantLink"));
        return m;
    }

    private static HashSet<string> AddedLinksIn(CAEXDocument doc)
    {
        var present = new HashSet<string>(StringComparer.Ordinal);
        CaexElementWalker.WalkInternalElements(doc, ie =>
        {
            foreach (var link in ie.InternalLink)
                if (Added.Contains(link.Name)) present.Add(link.Name);
        });
        return present;
    }

    /// <summary>The round-trip JSON without the element <paramref name="id"/> on every layer, with its flows.</summary>
    private static string WithoutId(CAEXDocument doc, InstanceHierarchyType ih, string id)
    {
        var root = JsonNode.Parse(CaexToFpbJson.Convert(doc, ih).Value)!.AsArray();
        foreach (var entry in root.Skip(1))
        {
            var data = entry!["elementDataInformation"]!.AsArray();
            var gone = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { id };
            gone.UnionWith(data
                .Where(e => gone.Contains((string?)e!["sourceRef"] ?? "") || gone.Contains((string?)e!["targetRef"] ?? ""))
                .Select(e => (string)e!["id"]!));
            foreach (var node in data.Where(e => gone.Contains((string)e!["id"]!)).ToList()) data.Remove(node);
            var visual = entry!["elementVisualInformation"]!.AsArray();
            foreach (var node in visual.Where(e => gone.Contains((string?)e!["id"] ?? "")).ToList()) visual.Remove(node);
        }
        return root.ToJsonString();
    }

    public static IEnumerable<object[]> TopElements()
    {
        var m = Temperieren();
        var systemLimit = FpbMappings.ElementToSuc[FpbTypes.SystemLimit];
        foreach (var ie in m.Top.InternalElement)
        {
            if (FpbMappings.StripAlias(ie.RefBaseSystemUnitPath) == systemLimit) continue;
            if (ie.Name == "Erhitzen") continue; // decomposed: its own test below
            yield return new object[] { ie.Name };
        }
    }

    [Theory]
    [MemberData(nameof(TopElements))]
    public void WhatUpdateInPlaceRemovesIsWhatFindNames(string name)
    {
        var m = Rich();
        var element = m.InTop(name);
        var id = Bare(element.ID);
        var found = ForeignLinks.Find(m.Doc, m.Fpd);
        var copies = m.Fpd.InternalElement.SelectMany(p => p.InternalElement)
            .Where(ie => string.Equals(Bare(ie.GetRefObjOrDerived()), id, StringComparison.OrdinalIgnoreCase));
        var expected = found
            .Where(e => string.Equals(e.ElementId, id, StringComparison.OrdinalIgnoreCase)
                     || copies.Any(c => string.Equals(e.ElementId, Bare(c.ID), StringComparison.OrdinalIgnoreCase)))
            .SelectMany(e => e.Links.Select(l => l.Link))
            .ToHashSet(StringComparer.Ordinal);
        var before = AddedLinksIn(m.Doc);

        FpbJsonToCaex.UpdateInPlace(m.Doc, WithoutId(m.Doc, m.Fpd, id), m.Fpd);

        var missing = before.Except(AddedLinksIn(m.Doc)).Except(Content).ToHashSet(StringComparer.Ordinal);
        Assert.DoesNotContain(expected, Content.Contains);
        Assert.Equal(expected.OrderBy(s => s, StringComparer.Ordinal), missing.OrderBy(s => s, StringComparer.Ordinal));
    }

    [Fact]
    public void WhatRemovingADecompositionRemovesIsWhatFindNames()
    {
        var m = Rich();
        var subId = Bare(m.Sub.ID);
        var onLayer = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { subId };
        onLayer.UnionWith(m.Sub.InternalElement.Select(ie => Bare(ie.ID)));
        var expected = ForeignLinks.Find(m.Doc, m.Fpd)
            .Where(e => onLayer.Contains(e.ElementId))
            .SelectMany(e => e.Links.Select(l => l.Link))
            .ToHashSet(StringComparer.Ordinal);
        Assert.NotEmpty(expected);
        var before = AddedLinksIn(m.Doc);

        var root = JsonNode.Parse(CaexToFpbJson.Convert(m.Doc, m.Fpd).Value)!.AsArray();
        var operatorId = Bare(m.Sub.GetRefObjOrDerived());
        foreach (var entry in root.Skip(1).ToList())
        {
            if (string.Equals((string?)entry!["process"]!["isDecomposedProcessOperator"], operatorId, StringComparison.OrdinalIgnoreCase))
            {
                root.Remove(entry);
                continue;
            }
            foreach (var e in entry["elementDataInformation"]!.AsArray())
                if (string.Equals((string?)e!["id"], operatorId, StringComparison.OrdinalIgnoreCase))
                    e!.AsObject().Remove("decomposedView");
        }
        FpbJsonToCaex.UpdateInPlace(m.Doc, root.ToJsonString(), m.Fpd);

        var missing = before.Except(AddedLinksIn(m.Doc)).ToHashSet(StringComparer.Ordinal);
        Assert.Equal(expected.OrderBy(s => s, StringComparer.Ordinal), missing.OrderBy(s => s, StringComparer.Ordinal));
    }
}
