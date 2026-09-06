using Aml.Engine.CAEX;
using FpbMapper.Conversion;

namespace FpbMapper.Tests.Showcases;

/// <summary>
/// Pins the exact cycle the AML editor plugin runs during a live demo:
///   open showcase AML -> CaexToFpbJson.Convert(doc, ih)  [viewer render]
///   -> FpbJsonToCaex.UpdateInPlace(doc, json, ih)         [Update button / echo]
///   -> CaexToFpbJson.Convert(doc, ih)                     [next render]
/// If the echo cycle is not lossless and idempotent, every live sync tick
/// washes corruption deeper into the user's AML file (the "Spaghetti bug"
/// class: jumping states, vanished boundary states, cleared refObjs).
/// </summary>
public class ShowcaseRoundTripTests
{
    public static IEnumerable<object[]> ShowcaseFiles()
    {
        yield return new object[] { "Showcase-A-WaermetauscherMitRegelung.aml" };
        yield return new object[] { "Showcase-B-PharmaChargeUndReinigung.aml" };
        yield return new object[] { "Showcase-C-CncRobotikMontage.aml" };
    }

    private static string ExamplesDir()
    {
        // Walk up from the test bin dir until the plugin examples folder is found.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, "fpb-aml-editor-plugin", "examples");
            if (Directory.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException(
            "fpb-aml-editor-plugin/examples not found above " + AppContext.BaseDirectory);
    }

    [Theory]
    [MemberData(nameof(ShowcaseFiles))]
    public void Showcase_EchoCycle_IsIdempotentPerIh(string fileName)
    {
        var path = Path.Combine(ExamplesDir(), fileName);
        Assert.True(File.Exists(path), $"Showcase file missing: {path}");

        var doc = CAEXDocument.LoadFromFile(path);
        var ihs = CaexToFpbJson.FindFpdInstanceHierarchies(doc);
        Assert.True(ihs.Count > 0, $"{fileName}: no FPD InstanceHierarchy found");

        foreach (var ih in ihs)
        {
            var first = CaexToFpbJson.Convert(doc, ih);
            var json1 = first.Value;
            Assert.False(string.IsNullOrEmpty(json1),
                $"{fileName}/{ih.Name}: initial Convert empty. Warnings: {string.Join("; ", first.Warnings)}");

            var ieCountBefore = CountIes(ih);

            FpbJsonToCaex.UpdateInPlace(doc, json1, ih);

            var ieCountAfter = CountIes(ih);
            Assert.True(ieCountBefore == ieCountAfter,
                $"{fileName}/{ih.Name}: echo cycle changed IE count {ieCountBefore} -> {ieCountAfter}");

            var second = CaexToFpbJson.Convert(doc, ih);

            Assert.True(json1 == second.Value,
                $"{fileName}/{ih.Name}: echo cycle is not idempotent — the JSON drifted. " +
                FirstDiff(json1, second.Value));
        }
    }

    [Theory]
    [MemberData(nameof(ShowcaseFiles))]
    public void Showcase_EchoCycle_TwiceProducesStableXml(string fileName)
    {
        var path = Path.Combine(ExamplesDir(), fileName);
        var doc = CAEXDocument.LoadFromFile(path);
        var ihs = CaexToFpbJson.FindFpdInstanceHierarchies(doc);

        // Two full echo cycles per IH, then compare the serialized document
        // against a single-cycle copy: cycle 2..n must be byte-stable.
        foreach (var ih in ihs)
        {
            var json = CaexToFpbJson.Convert(doc, ih).Value;
            FpbJsonToCaex.UpdateInPlace(doc, json, ih);
        }
        var afterOne = Serialize(doc);

        foreach (var ih in ihs)
        {
            var json = CaexToFpbJson.Convert(doc, ih).Value;
            FpbJsonToCaex.UpdateInPlace(doc, json, ih);
        }
        var afterTwo = Serialize(doc);

        Assert.True(afterOne == afterTwo,
            $"{fileName}: second echo cycle changed the AML — sync is unstable. " +
            FirstDiff(afterOne, afterTwo));
    }

    private static string Serialize(CAEXDocument doc)
    {
        // Fixed file name: SaveToFile stamps the target name into the CAEXFile
        // FileName attribute, so a random temp name would fake a diff.
        var tmp = Path.Combine(Path.GetTempPath(), "fpb-roundtrip-probe.aml");
        try
        {
            doc.SaveToFile(tmp, prettyPrint: false);
            return File.ReadAllText(tmp);
        }
        finally
        {
            try { File.Delete(tmp); } catch { }
        }
    }

    private static int CountIes(InstanceHierarchyType ih)
    {
        var count = 0;
        void Walk(IEnumerable<InternalElementType> ies)
        {
            foreach (var ie in ies) { count++; Walk(ie.InternalElement); }
        }
        Walk(ih.InternalElement);
        return count;
    }

    private static string FirstDiff(string a, string b)
    {
        var min = Math.Min(a.Length, b.Length);
        for (var i = 0; i < min; i++)
        {
            if (a[i] != b[i])
            {
                var from = Math.Max(0, i - 80);
                var lenA = Math.Min(160, a.Length - from);
                var lenB = Math.Min(160, b.Length - from);
                return $"First diff at char {i}:\n  A: …{a.Substring(from, lenA)}…\n  B: …{b.Substring(from, lenB)}…";
            }
        }
        return $"Length differs: {a.Length} vs {b.Length}. Tail A: …{a[Math.Max(0, a.Length - 160)..]}\nTail B: …{b[Math.Max(0, b.Length - 160)..]}";
    }
}
