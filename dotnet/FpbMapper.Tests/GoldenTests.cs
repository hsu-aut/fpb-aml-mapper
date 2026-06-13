using System.Text;
using System.Text.Json;
using Aml.Engine.CAEX;
using FpbMapper.Conversion;
using Xunit;

namespace FpbMapper.Tests;

/// <summary>
/// Safety net (N2) for the planned JSON v2 / internal-model rework: locks the FULL
/// structure the mapper produces, not just element counts. A golden mismatch means the
/// conversion behaviour changed — intended changes are adopted by regenerating with
/// the UPDATE_GOLDEN=1 environment variable; unintended changes are caught.
///
/// Each test also self-checks determinism (convert twice, assert identical) so a golden
/// can never be made stable by accident over a nondeterministic output.
/// </summary>
public class GoldenTests
{
    // Resolve TestData in the SOURCE tree (not bin/) so regenerated goldens land where
    // they're committed. Compile-time path; valid wherever the suite builds from source.
    private static string TestDataDir([System.Runtime.CompilerServices.CallerFilePath] string thisFile = "") =>
        Path.Combine(Path.GetDirectoryName(thisFile)!, "TestData");

    private static string LoadTestData(string name) =>
        File.ReadAllText(Path.Combine(TestDataDir(), name));

    private static readonly bool UpdateGolden =
        Environment.GetEnvironmentVariable("UPDATE_GOLDEN") == "1";

    [Fact]
    public void JsonToAml_StructuralDump_MatchesGolden()
    {
        // The Convert path mints fresh GUIDs for process IEs and flow interfaces
        // (the JSON process id aliases its parent PO id — a v1 quirk). Structure and
        // ordering are deterministic; only those GUID VALUES vary. Canonicalizing
        // GUIDs by first-occurrence locks the structure + reference wiring while
        // ignoring the random values — and lets the determinism self-check hold.
        string Dump() => CanonicalizeGuids(StructuralDump(FpbJsonToCaex.Convert(LoadTestData("Temperieren.json")).Value!));

        var dump = Dump();
        Assert.Equal(Dump(), dump); // determinism modulo GUID values

        CompareToGolden("Temperieren.aml-structure.golden.txt", dump);
    }

    [Fact]
    public void Roundtrip_JsonToAmlToJson_MatchesGolden()
    {
        string Roundtrip()
        {
            var aml = FpbJsonToCaex.Convert(LoadTestData("Temperieren.json")).Value!;
            var json = CaexToFpbJson.Convert(aml).Value!;
            return CanonicalizeGuids(Normalize(json));
        }

        var rt = Roundtrip();
        Assert.Equal(Roundtrip(), rt); // determinism modulo GUID values

        CompareToGolden("Temperieren.roundtrip.golden.json", rt);
    }

    /// <summary>Replace each distinct GUID (braced or bare) with a stable #N placeholder
    /// in order of first appearance — folds away the Convert path's random ID minting.</summary>
    private static string CanonicalizeGuids(string s)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        return System.Text.RegularExpressions.Regex.Replace(
            s,
            @"\{?[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\}?",
            m =>
            {
                var braced = m.Value.StartsWith("{");
                var key = m.Value.Trim('{', '}');
                if (!map.TryGetValue(key, out var ph)) { ph = $"#{map.Count + 1}"; map[key] = ph; }
                return braced ? "{" + ph + "}" : ph;
            });
    }

    // ---- helpers --------------------------------------------------------------------

    private static void CompareToGolden(string goldenName, string actual)
    {
        var path = Path.Combine(TestDataDir(), goldenName);
        actual = actual.Replace("\r\n", "\n");

        if (UpdateGolden || !File.Exists(path))
        {
            File.WriteAllText(path, actual, new UTF8Encoding(false));
            Assert.Fail($"Golden '{goldenName}' was (re)generated — review the diff, commit it, and re-run without UPDATE_GOLDEN.");
        }

        var expected = File.ReadAllText(path).Replace("\r\n", "\n");
        Assert.Equal(expected, actual);
    }

    /// <summary>Reparse + re-serialize with sorted keys → stable, diff-friendly.</summary>
    private static string Normalize(string json)
    {
        using var doc = JsonDocument.Parse(json);
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
            WriteSorted(doc.RootElement, w);
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private static void WriteSorted(JsonElement el, Utf8JsonWriter w)
    {
        switch (el.ValueKind)
        {
            case JsonValueKind.Object:
                w.WriteStartObject();
                foreach (var p in el.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
                {
                    w.WritePropertyName(p.Name);
                    WriteSorted(p.Value, w);
                }
                w.WriteEndObject();
                break;
            case JsonValueKind.Array:
                w.WriteStartArray();
                foreach (var item in el.EnumerateArray()) WriteSorted(item, w);
                w.WriteEndArray();
                break;
            default:
                el.WriteTo(w);
                break;
        }
    }

    /// <summary>Deterministic IE-tree + attribute + link dump (attributes sorted by path).</summary>
    private static string StructuralDump(CAEXDocument doc)
    {
        var sb = new StringBuilder();
        foreach (var ih in doc.CAEXFile.InstanceHierarchy)
        {
            sb.Append("IH ").Append(ih.Name).Append('\n');
            foreach (var ie in ih.InternalElement) DumpIe(ie, 1, sb);
        }
        return sb.ToString();
    }

    private static void DumpIe(InternalElementType ie, int depth, StringBuilder sb)
    {
        var ind = new string(' ', depth * 2);
        sb.Append(ind).Append("IE name='").Append(ie.Name)
          .Append("' suc='").Append(ie.RefBaseSystemUnitPath).Append("'\n");

        foreach (var line in DumpAttrs(ie.Attribute, "").OrderBy(s => s, StringComparer.Ordinal))
            sb.Append(ind).Append("  @").Append(line).Append('\n');

        foreach (var link in ie.InternalLink.OrderBy(l => l.Name, StringComparer.Ordinal))
            sb.Append(ind).Append("  LINK name='").Append(link.Name)
              .Append("' a='").Append(link.RefPartnerSideA)
              .Append("' b='").Append(link.RefPartnerSideB).Append("'\n");

        foreach (var child in ie.InternalElement) DumpIe(child, depth + 1, sb);
    }

    private static IEnumerable<string> DumpAttrs(IEnumerable<AttributeType> attrs, string prefix)
    {
        foreach (var a in attrs)
        {
            var path = prefix.Length == 0 ? a.Name : $"{prefix}/{a.Name}";
            yield return $"{path} = '{a.Value}'";
            foreach (var sub in DumpAttrs(a.Attribute, path)) yield return sub;
        }
    }
}
