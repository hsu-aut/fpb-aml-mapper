using System.Text.Json;
using FpbMapper.Conversion;

namespace FpbMapper.Tests;

/// <summary>
/// Pins the catalogue ↔ OCL ↔ executable-rule-set wiring. If the OCL files
/// gain or drop a constraint the totals here change and the test fails —
/// catching silent drift before it lands on the conference slide.
/// </summary>
public class CoverageReportTests
{
    [Fact]
    public void Build_ReportsExactly60CataloguedRules()
    {
        var report = CoverageReportBuilder.Build(DateTimeOffset.UtcNow);
        Assert.Equal(60, report.Rules.Count);
    }

    [Fact]
    public void Build_PinsExactBucketCountsSoSlideStaysHonest()
    {
        var report = CoverageReportBuilder.Build(DateTimeOffset.UtcNow);
        var totals = report.Totals;

        Assert.Equal(26, totals["PURE_OCL_LIVE"]);
        Assert.Equal(4,  totals["PHASE2_OCL_PENDING"]);
        Assert.Equal(11, totals["OPEN_PLUGIN_GEOMETRY"]);
        Assert.Equal(4,  totals["OPEN_MAPPER_SCHEMA"]);
        Assert.Equal(15, totals["OPEN_PHASE2_OR_INFORMATIVE"]);
        Assert.Equal(60, totals["TOTAL_CATALOGUE"]);
    }

    // (Intentionally no test in the other direction: many catalogue entries
    // *carry* an OCL constraint as illustration but don't ship in the executable
    // rule files yet — that gap *is* the coverage report's whole point.)

    [Fact]
    public void Ocl_NeverShipsAConstraintNotInTheCatalogue()
    {
        // Inverse check: every constraint the engine actually loads must be
        // referenced by a catalogue row, otherwise we'd ship rules the slide
        // can't account for.
        var report = CoverageReportBuilder.Build(DateTimeOffset.UtcNow);
        var listed = report.Rules.Select(r => r.OclConstraint)
                                 .Where(s => !string.IsNullOrEmpty(s))
                                 .ToHashSet(StringComparer.Ordinal);

        var pure   = CoverageReportBuilder.ExtractInvariantNames(FpbValidationRules.PureRules);
        var phase2 = CoverageReportBuilder.ExtractInvariantNames(FpbValidationRules.Phase2Rules);

        var unmapped = pure.Concat(phase2).Where(n => !listed.Contains(n)).ToList();
        Assert.True(unmapped.Count == 0,
            "OCL ships constraints unknown to the catalogue snapshot: " + string.Join(", ", unmapped));
    }

    [Fact]
    public void ReportSerialises_AsValidJsonAndCsv()
    {
        var report = CoverageReportBuilder.Build(DateTimeOffset.UtcNow);

        // JSON round-trips through System.Text.Json without exception
        using var doc = JsonDocument.Parse(report.ToJson());
        Assert.Equal(JsonValueKind.Object, doc.RootElement.ValueKind);

        var csv = report.ToCsv();
        var lines = csv.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        // 1 header + 60 rule rows + "# totals" + N totals comments
        Assert.True(lines.Length >= 60 + 1 + 1);
        Assert.StartsWith("catalog_id,title,category,bucket,ocl_constraint,note", lines[0]);
    }

    /// <summary>
    /// Emits the latest CSV + JSON next to the AML showcases so consumers (the
    /// slide build, paper appendix, audit log) have a fresh artefact tracked
    /// in git. Runs as part of the normal test pass; output path is the same
    /// examples/ folder used for the showcase AMLs.
    /// </summary>
    [Fact]
    public void Emit_CoverageArtifacts_ToExamples()
    {
        var report = CoverageReportBuilder.Build(new DateTimeOffset(2026, 6, 15, 0, 0, 0, TimeSpan.Zero));

        var fromEnv = Environment.GetEnvironmentVariable("SHOWCASE_OUTPUT_DIR");
        var outDir = !string.IsNullOrWhiteSpace(fromEnv) ? fromEnv : Path.GetFullPath(
            Path.Combine(AppContext.BaseDirectory,
                "..", "..", "..", "..", "..", "..", "fpb-aml-editor-plugin", "examples"));
        Directory.CreateDirectory(outDir);

        File.WriteAllText(Path.Combine(outDir, "vdi3682-coverage.csv"),  report.ToCsv());
        File.WriteAllText(Path.Combine(outDir, "vdi3682-coverage.json"), report.ToJson());
    }
}
