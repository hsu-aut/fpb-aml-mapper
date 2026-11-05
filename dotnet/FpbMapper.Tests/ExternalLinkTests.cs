using System.Text.Json.Nodes;
using Aml.Engine.CAEX;
using FpbMapper.Conversion;

namespace FpbMapper.Tests;

/// <summary>
/// Links from other parts of the document (a plant hierarchy, user-added links)
/// into FPD elements that the FPB.JS side deleted. Before, UpdateInPlace removed
/// the element and left such links pointing at nothing. They are now removed
/// with it and named in the warnings; links that were dangling before the
/// update, and links to surviving elements, stay as they are.
/// </summary>
public class ExternalLinkTests
{
    private static string LoadTestData(string name) =>
        File.ReadAllText(Path.Combine("TestData", name));

    private sealed record Setup(
        CAEXDocument Doc,
        InternalElementType Tank,
        InternalElementType Deleted,
        InternalElementType Kept);

    /// <summary>
    /// Temperieren as AML, a state that goes ("Ausschuss") and one that stays
    /// ("Warmprodukt"), each with a user interface, and a plant hierarchy whose
    /// tank links to both.
    /// </summary>
    private static Setup Build()
    {
        var doc = FpbJsonToCaex.Convert(LoadTestData("Temperieren.json")).Value;
        var fpd = doc.CAEXFile.InstanceHierarchy.First();
        var deleted = Walk(fpd.InternalElement).First(ie => ie.Name == "Ausschuss");
        var kept = Walk(fpd.InternalElement).First(ie => ie.Name == "Warmprodukt");
        var deletedIf = deleted.ExternalInterface.Append("PlantLink");
        var keptIf = kept.ExternalInterface.Append("PlantLink");

        var plant = doc.CAEXFile.InstanceHierarchy.Append("Plant");
        var tank = plant.InternalElement.Append("Tank");
        var toDeleted = tank.ExternalInterface.Append("ToScrap");
        var toKept = tank.ExternalInterface.Append("ToProduct");
        var toNothing = tank.ExternalInterface.Append("ToNothing");

        var a = tank.InternalLink.Append("TankToScrap");
        a.AInterface = toDeleted;
        a.BInterface = deletedIf;
        var b = tank.InternalLink.Append("TankToProduct");
        b.AInterface = toKept;
        b.BInterface = keptIf;
        // CAEX 2.15 form "element id:interface name", as in files of other tools
        var legacy = tank.InternalLink.Append("TankToScrapLegacy");
        legacy.RefPartnerSideA = toDeleted.ID;
        legacy.RefPartnerSideB = deleted.ID + ":PlantLink";
        // dangling before the update; not ours to clean up
        var stale = tank.InternalLink.Append("TankToGone");
        stale.RefPartnerSideA = toNothing.ID;
        stale.RefPartnerSideB = "{00000000-0000-4000-8000-00000000dead}";

        return new Setup(doc, tank, deleted, kept);
    }

    /// <summary>The round-trip JSON without the element named <paramref name="name"/> and its flows.</summary>
    private static string Without(CAEXDocument doc, string name)
    {
        var root = JsonNode.Parse(CaexToFpbJson.Convert(doc).Value)!.AsArray();
        foreach (var entry in root.Skip(1))
        {
            var data = entry!["elementDataInformation"]!.AsArray();
            var gone = data.Where(e => (string?)e!["name"] == name).Select(e => (string)e!["id"]!).ToHashSet();
            if (gone.Count == 0) continue;
            var flows = data
                .Where(e => gone.Contains((string?)e!["sourceRef"] ?? "") || gone.Contains((string?)e!["targetRef"] ?? ""))
                .Select(e => (string)e!["id"]!)
                .ToHashSet();
            gone.UnionWith(flows);
            foreach (var node in data.Where(e => gone.Contains((string)e!["id"]!)).ToList()) data.Remove(node);
            var visual = entry!["elementVisualInformation"]!.AsArray();
            foreach (var node in visual.Where(e => gone.Contains((string?)e!["id"] ?? "")).ToList()) visual.Remove(node);
        }
        return root.ToJsonString();
    }

    private static IEnumerable<InternalElementType> Walk(IEnumerable<InternalElementType> roots)
    {
        foreach (var ie in roots)
        {
            yield return ie;
            foreach (var child in Walk(ie.InternalElement)) yield return child;
        }
    }

    private static List<string> LinkNames(InternalElementType ie) => ie.InternalLink.Select(l => l.Name).ToList();

    [Fact]
    public void LinksIntoADeletedElementAreRemovedAndReported()
    {
        var s = Build();
        var deletedId = s.Deleted.ID;

        var result = FpbJsonToCaex.UpdateInPlace(s.Doc, Without(s.Doc, "Ausschuss"));

        Assert.DoesNotContain(Walk(s.Doc.CAEXFile.InstanceHierarchy.First().InternalElement), ie => ie.ID == deletedId);
        Assert.Equal(new[] { "TankToProduct", "TankToGone" }, LinkNames(s.Tank));
        Assert.Contains(result.Warnings, w => w.Contains("TankToScrap") && w.Contains("Ausschuss") && w.Contains("Tank"));
        Assert.Contains(result.Warnings, w => w.Contains("TankToScrapLegacy") && w.Contains("Ausschuss"));
    }

    [Fact]
    public void WithoutDeletionsNoLinkIsTouched()
    {
        var s = Build();

        var result = FpbJsonToCaex.UpdateInPlace(s.Doc, CaexToFpbJson.Convert(s.Doc).Value);

        Assert.Equal(new[] { "TankToScrap", "TankToProduct", "TankToScrapLegacy", "TankToGone" }, LinkNames(s.Tank));
        Assert.DoesNotContain(result.Warnings, w => w.StartsWith("Removed link"));
    }

    [Fact]
    public void AUserLinkInsideTheProcessIntoADeletedElementGoesToo()
    {
        var s = Build();
        // a link the user drew inside the FPD hierarchy, not an FPD flow
        var holder = s.Kept.CAEXParent as InternalElementType;
        Assert.NotNull(holder);
        var own = holder!.InternalLink.Append("UserNote");
        own.AInterface = s.Kept.ExternalInterface.First(i => i.Name == "PlantLink");
        own.BInterface = s.Deleted.ExternalInterface.First(i => i.Name == "PlantLink");

        var result = FpbJsonToCaex.UpdateInPlace(s.Doc, Without(s.Doc, "Ausschuss"));

        Assert.DoesNotContain("UserNote", LinkNames(holder));
        Assert.Contains(result.Warnings, w => w.Contains("UserNote"));
    }

    // ── a port that a link from outside shares with a flow (016f E4, V29) ──

    /// <summary>The flow from "Warmprodukt" to "Prüfen" of Temperieren.</summary>
    private const string FlowId = "d5dc692d-61ac-4b4b-aea5-3d3044345aa7";

    private static InternalLinkType FlowLink(CAEXDocument doc)
    {
        InternalLinkType? found = null;
        foreach (var ie in Walk(doc.CAEXFile.InstanceHierarchy.First().InternalElement))
            foreach (var link in ie.InternalLink)
                if ((link.ID ?? "").Trim('{', '}').Equals(FlowId, StringComparison.OrdinalIgnoreCase)) found = link;
        return found ?? throw new InvalidOperationException("flow not found");
    }

    /// <summary>The round-trip JSON without the flow <paramref name="id"/>.</summary>
    private static string WithoutFlow(CAEXDocument doc, string id)
    {
        var root = JsonNode.Parse(CaexToFpbJson.Convert(doc).Value)!.AsArray();
        foreach (var entry in root.Skip(1))
        {
            var data = entry!["elementDataInformation"]!.AsArray();
            foreach (var node in data.Where(e => string.Equals((string?)e!["id"], id, StringComparison.OrdinalIgnoreCase)).ToList()) data.Remove(node);
            var visual = entry!["elementVisualInformation"]!.AsArray();
            foreach (var node in visual.Where(e => string.Equals((string?)e!["id"], id, StringComparison.OrdinalIgnoreCase)).ToList()) visual.Remove(node);
        }
        return root.ToJsonString();
    }

    private static bool InterfaceExists(CAEXDocument doc, string? id)
    {
        var bare = (id ?? "").Trim('{', '}');
        bool Has(IEnumerable<ExternalInterfaceType> list) =>
            list.Any(i => (i.ID ?? "").Trim('{', '}').Equals(bare, StringComparison.OrdinalIgnoreCase) || Has(i.ExternalInterface));
        return doc.CAEXFile.InstanceHierarchy.Any(ih => Walk(ih.InternalElement).Any(ie => Has(ie.ExternalInterface)));
    }

    [Fact]
    public void APortStaysWhileALinkFromOutsideUsesIt()
    {
        var doc = FpbJsonToCaex.Convert(LoadTestData("Temperieren.json")).Value;
        var port = (ExternalInterfaceType)FlowLink(doc).AInterface;
        var tank = doc.CAEXFile.InstanceHierarchy.Append("Plant").InternalElement.Append("Tank");
        var toPort = tank.InternalLink.Append("TankToPort");
        toPort.AInterface = tank.ExternalInterface.Append("ToPort");
        toPort.BInterface = port;
        var portId = port.ID;

        FpbJsonToCaex.UpdateInPlace(doc, WithoutFlow(doc, FlowId));

        Assert.Throws<InvalidOperationException>(() => FlowLink(doc));
        Assert.True(InterfaceExists(doc, portId));
        Assert.True(InterfaceExists(doc, tank.InternalLink.First(l => l.Name == "TankToPort").RefPartnerSideB));
    }

    [Fact]
    public void APortStaysWhileALinkInTheFormElementIdColonNameUsesIt()
    {
        var doc = FpbJsonToCaex.Convert(LoadTestData("Temperieren.json")).Value;
        var port = (ExternalInterfaceType)FlowLink(doc).AInterface;
        var owner = (InternalElementType)port.CAEXParent;
        var tank = doc.CAEXFile.InstanceHierarchy.Append("Plant").InternalElement.Append("Tank");
        var toPort = tank.InternalLink.Append("TankToPortLegacy");
        toPort.RefPartnerSideA = tank.ExternalInterface.Append("ToPort").ID;
        toPort.RefPartnerSideB = owner.ID + ":" + port.Name;
        var portId = port.ID;

        FpbJsonToCaex.UpdateInPlace(doc, WithoutFlow(doc, FlowId));

        Assert.True(InterfaceExists(doc, portId));
    }

    [Fact]
    public void APortSharedByTwoFlowsAndALinkStaysWhenOneFlowGoes()
    {
        var doc = FpbJsonToCaex.Convert(LoadTestData("Temperieren.json")).Value;
        var flow = FlowLink(doc);
        var port = (ExternalInterfaceType)flow.AInterface;
        var holder = (InternalElementType)flow.CAEXParent;
        var second = holder.InternalLink.Append("SecondFlow");
        second.ID = "{00000000-0000-4000-8000-0000000000f2}";
        second.AInterface = port;
        second.BInterface = flow.BInterface;
        var tank = doc.CAEXFile.InstanceHierarchy.Append("Plant").InternalElement.Append("Tank");
        var toPort = tank.InternalLink.Append("TankToPort");
        toPort.AInterface = tank.ExternalInterface.Append("ToPort");
        toPort.BInterface = port;
        var portId = port.ID;

        FpbJsonToCaex.UpdateInPlace(doc, WithoutFlow(doc, FlowId));

        Assert.Throws<InvalidOperationException>(() => FlowLink(doc));
        Assert.Contains("SecondFlow", LinkNames(holder));
        Assert.True(InterfaceExists(doc, portId));
        Assert.True(InterfaceExists(doc, holder.InternalLink.First(l => l.Name == "SecondFlow").RefPartnerSideA));
    }

    [Fact]
    public void APortGoesWithItsFlowWhenNothingElseUsesIt()
    {
        var doc = FpbJsonToCaex.Convert(LoadTestData("Temperieren.json")).Value;
        var portId = ((ExternalInterfaceType)FlowLink(doc).AInterface).ID;

        FpbJsonToCaex.UpdateInPlace(doc, WithoutFlow(doc, FlowId));

        Assert.False(InterfaceExists(doc, portId));
    }
}
