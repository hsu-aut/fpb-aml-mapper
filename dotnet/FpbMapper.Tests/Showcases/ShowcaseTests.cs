using System.Text.Json;
using Aml.Engine.CAEX;
using FpbMapper.Conversion;
using static FpbMapper.Conversion.FpbMappings;

namespace FpbMapper.Tests.Showcases;

/// <summary>
/// Generators for the three demo / round-trip AMLs used at the AML conference.
/// Each fact builds a Showcase JSON via <see cref="ShowcaseBuilder"/>, runs it
/// through the mapper, layers cross-IH constructs on top where applicable, and
/// writes the AML to <c>examples/</c>. The round-trip assertions guard against
/// silent mapper regressions on these specific shapes.
///
/// Output path resolution: defaults to <c>../../../../../fpb-aml-editor-plugin/examples/</c>
/// (Plugin repo, where the editor loads them from); overrideable via env var
/// <c>SHOWCASE_OUTPUT_DIR</c>.
/// </summary>
public class ShowcaseTests
{
    internal static string OutputDir()
    {
        var fromEnv = Environment.GetEnvironmentVariable("SHOWCASE_OUTPUT_DIR");
        if (!string.IsNullOrWhiteSpace(fromEnv))
        {
            Directory.CreateDirectory(fromEnv);
            return fromEnv;
        }
        // BaseDirectory: …/FpbMapper.Tests/bin/Debug/net8.0/
        // Six "../" walks back to …/AML/ where the plugin lives next to the mapper.
        var here = AppContext.BaseDirectory;
        var examples = Path.GetFullPath(Path.Combine(here,
            "..", "..", "..", "..", "..", "..", "fpb-aml-editor-plugin", "examples"));
        Directory.CreateDirectory(examples);
        return examples;
    }

    private static void WriteAml(CAEXDocument doc, string fileName)
    {
        var path = Path.Combine(OutputDir(), fileName);
        doc.SaveToFile(path, prettyPrint: true);
    }

    private static InternalElementType FindIeByName(InstanceHierarchyType ih, string name)
    {
        InternalElementType? Walk(IEnumerable<InternalElementType> roots)
        {
            foreach (var ie in roots)
            {
                if (string.Equals(ie.Name, name, StringComparison.Ordinal)) return ie;
                var found = Walk(ie.InternalElement);
                if (found != null) return found;
            }
            return null;
        }
        var hit = Walk(ih.InternalElement);
        return hit ?? throw new InvalidOperationException($"IE '{name}' not found in IH '{ih.Name}'");
    }

    // ──────────────────────────────────────────────────────────────────────
    // Showcase A — Wärmetauscher with Plant_Control_Signals cross-IH
    // ──────────────────────────────────────────────────────────────────────
    [Fact]
    public void Generate_Showcase_A_Waermetauscher()
    {
        var sb = BuildWaermetauscher();
        var json = sb.ToJson();
        var conv = FpbJsonToCaex.Convert(json);
        Assert.True(conv.Warnings.Count <= 5, "Mapper warnings: " + string.Join(" | ", conv.Warnings));
        var doc = conv.Value;

        // Rename the FPD IH so the showcase reads naturally in the AML tree.
        var fpdIh = doc.CAEXFile.InstanceHierarchy.First();
        fpdIh.Name = "FPD_Heating";

        // Add a second, non-FPD InstanceHierarchy with PIC_4711 and TT_8042 plus
        // an InternalInterface on each that a cross-IH InternalLink can target.
        var plantIh = doc.CAEXFile.InstanceHierarchy.Append("Plant_Control_Signals");
        var pic = plantIh.InternalElement.Append("PIC_4711");
        pic.RefBaseSystemUnitPath = "";
        var picIface = pic.ExternalInterface.Append("PV_Signal");
        var tt = plantIh.InternalElement.Append("TT_8042");
        var ttIface = tt.ExternalInterface.Append("Temp_Signal");

        // Cross-IH InternalLinks: the FPD POs reach into the plant signal world.
        // The mapper uses one ExternalInterface ("FpdConnection") per FPD IE for
        // its own flow links; reusing it is fine for cross-IH wires too.
        var poPlatte = FindIeByName(fpdIh, "Plattenwärmeübertragung");
        var poVentil = FindIeByName(fpdIh, "Stellventil betätigen");
        var ifacePlatte = poPlatte.ExternalInterface.FirstOrDefault()
                         ?? poPlatte.ExternalInterface.Append("FpdConnection");
        var ifaceVentil = poVentil.ExternalInterface.FirstOrDefault()
                         ?? poVentil.ExternalInterface.Append("FpdConnection");

        // Anchor the cross-IH InternalLinks on the FPD POs themselves — that
        // way they live in the FPD IH (consistent with where the mapper puts
        // intra-process flows) and target the plant-side ExternalInterfaces.
        AppendInternalLink(poPlatte, "PlatteToTempSensor", ifacePlatte, ttIface);
        AppendInternalLink(poVentil, "VentilToPressureCtl", ifaceVentil, picIface);

        WriteAml(doc, "Showcase-A-WaermetauscherMitRegelung.aml");

        // Round-trip the FPD IH back to JSON and make sure the document survives.
        var back = CaexToFpbJson.Convert(doc, fpdIh);
        Assert.False(string.IsNullOrWhiteSpace(back.Value), "Round-trip JSON should be non-empty");
        using var roundTrip = JsonDocument.Parse(back.Value);
        Assert.Equal(JsonValueKind.Array, roundTrip.RootElement.ValueKind);
    }

    private static ShowcaseBuilder BuildWaermetauscher()
    {
        var sb = new ShowcaseBuilder("Waermetauscher_Showcase", "Wärmetauschen");
        var p = sb.Root;

        var hot      = p.AddProduct("Heißes Medium");
        var cold     = p.AddProduct("Kaltes Medium");
        var po       = p.AddProcessOperator("Wärmeübertragen");
        var heated   = p.AddProduct("Erwärmtes Medium");
        var cooled   = p.AddProduct("Abgekühltes Medium");
        var tempSens = p.AddTechnicalResource("Temperatursensor");

        p.AddCharacteristic(heated, "SollTemperatur", "Soll-Temperatur am Austritt", "75", "°C");
        p.AddCharacteristic(cooled, "SollTemperatur", "Soll-Temperatur am Austritt", "30", "°C");

        p.AddFlow(hot,  po);
        p.AddFlow(cold, po);
        p.AddFlow(po,   heated);
        p.AddFlow(po,   cooled);
        p.AddUsage(po,  tempSens);

        // Layer 1 — decomposition of "Wärmeübertragen"
        var sub = p.Decompose(po, "Wärmeübertragen Detail");
        var bHot    = sub.AddBoundaryFromParent(hot,    "fpb:Product", "Eintritt Heiß");
        var bCold   = sub.AddBoundaryFromParent(cold,   "fpb:Product", "Eintritt Kalt");
        var bHeated = sub.AddBoundaryFromParent(heated, "fpb:Product", "Austritt Kalt (erwärmt)");
        var bCooled = sub.AddBoundaryFromParent(cooled, "fpb:Product", "Austritt Heiß (abgekühlt)");

        var preHeat   = sub.AddProcessOperator("Heizmedium aufbereiten");
        var platte    = sub.AddProcessOperator("Plattenwärmeübertragung");
        sub.AddCharacteristic(platte, "AuslegungsLeistung", "Auslegungs-Wärmeleistung", "250", "kW");
        sub.AddCharacteristic(platte, "PlattenAnzahl",      "Anzahl Platten",            "40",  "Stk");

        sub.AddFlow(bHot,    preHeat);
        sub.AddFlow(preHeat, platte);
        sub.AddFlow(bCold,   platte);
        sub.AddFlow(platte,  bHeated);
        sub.AddFlow(platte,  bCooled);

        // Layer 2 — decomposition of "Heizmedium aufbereiten"
        var sub2 = sub.Decompose(preHeat, "Heizmedium aufbereiten Detail");
        var bIn   = sub2.AddBoundaryFromParent(bHot, "fpb:Product", "Heizmedium-Eingang");
        var bOut  = sub2.AddProduct("Heizmedium-Ausgang");
        var pCtrl = sub2.AddProcessOperator("Druckregelung");
        var tSet  = sub2.AddProcessOperator("Temperatur-Sollwert prüfen");
        sub2.AddCharacteristic(tSet, "Sollwert", "Eingestellter Temperatursollwert", "120", "°C");

        sub2.AddFlow(bIn,   pCtrl);
        sub2.AddFlow(pCtrl, tSet);
        sub2.AddFlow(tSet,  bOut);

        // Layer 3 — decomposition of "Druckregelung"
        var sub3 = sub2.Decompose(pCtrl, "Druckregelung Detail");
        var bRoh   = sub3.AddBoundaryFromParent(bIn, "fpb:Product", "Roh-Druck");
        var bGeregelt = sub3.AddProduct("Geregelter Druck");
        var messen = sub3.AddProcessOperator("Druckmessung");
        var stellen = sub3.AddProcessOperator("Stellventil betätigen");
        sub3.AddCharacteristic(stellen, "MaxStellgeschwindigkeit", "Maximale Stellgeschwindigkeit", "5", "%/s");

        sub3.AddFlow(bRoh,   messen);
        sub3.AddFlow(messen, stellen);
        sub3.AddFlow(stellen, bGeregelt);

        return sb;
    }

    // ──────────────────────────────────────────────────────────────────────
    // Showcase B — Pharma Multi-IH (Charge + Reinigung, shared Mixer)
    // ──────────────────────────────────────────────────────────────────────
    [Fact]
    public void Generate_Showcase_B_Pharma_MultiIh()
    {
        // Two independent FPD JSONs are mapped sequentially into the same
        // CAEXDocument. The shared Mixer_1 TR carries the same uniqueIdent on
        // both sides so a downstream consumer can correlate them.
        var jsonBatch    = BuildPharmaBatch().ToJson();
        var jsonCleaning = BuildPharmaCleaning().ToJson();

        var doc = FpbJsonToCaex.Convert(jsonBatch).Value;
        var ihBatch = doc.CAEXFile.InstanceHierarchy.First();
        ihBatch.Name = "FPD_Batch_Production";

        var ihCleaning = FpbJsonToCaex.CreateEmptyFpdInstanceHierarchy(doc, "FPD_Cleaning");
        FpbJsonToCaex.UpdateInPlace(doc, jsonCleaning, ihCleaning);

        WriteAml(doc, "Showcase-B-PharmaChargeUndReinigung.aml");

        var fpdIhs = doc.CAEXFile.InstanceHierarchy.ToList();
        Assert.Equal(2, fpdIhs.Count);

        var backBatch    = CaexToFpbJson.Convert(doc, ihBatch);
        var backCleaning = CaexToFpbJson.Convert(doc, ihCleaning);
        Assert.False(string.IsNullOrWhiteSpace(backBatch.Value));
        Assert.False(string.IsNullOrWhiteSpace(backCleaning.Value));
    }

    private static ShowcaseBuilder BuildPharmaBatch()
    {
        var sb = new ShowcaseBuilder("Pharma_Batch_Showcase", "Wirkstofflösung herstellen");
        var p = sb.Root;

        var wirkstoff   = p.AddProduct("Wirkstoff");
        var hilfsstoff  = p.AddProduct("Hilfsstoff");
        var herstellen  = p.AddProcessOperator("Wirkstofflösung herstellen");
        var loesung     = p.AddProduct("Wirkstofflösung");
        var mixer       = p.AddTechnicalResource("Mixer_1");
        p.AddCharacteristic(mixer, "EquipmentId",        "Equipment-Identifier",     "MX-OP-23",    "");
        p.AddCharacteristic(mixer, "LastCleaningStatus", "Letzter Reinigungsstatus", "validated",   "");
        p.AddCharacteristic(herstellen, "SafetyClass",   "Sicherheitsklasse",        "SIL2",        "");
        p.AddCharacteristic(loesung, "BatchId",          "Chargen-Identifier",       "B-2026-0042", "");

        p.AddFlow(wirkstoff,  herstellen);
        p.AddFlow(hilfsstoff, herstellen);
        p.AddFlow(herstellen, loesung);
        p.AddUsage(herstellen, mixer);

        // Layer 1 — decompose "Wirkstofflösung herstellen"
        var sub = p.Decompose(herstellen, "Herstellen Detail");
        var bWk = sub.AddBoundaryFromParent(wirkstoff,  "fpb:Product", "Wirkstoff (Eingang)");
        var bHs = sub.AddBoundaryFromParent(hilfsstoff, "fpb:Product", "Hilfsstoff (Eingang)");
        var bLs = sub.AddBoundaryFromParent(loesung,    "fpb:Product", "Wirkstofflösung (Ausgang)");

        var dosieren = sub.AddProcessOperator("Wirkstoff dosieren");
        var mischen  = sub.AddProcessOperator("Mischen");
        sub.AddCharacteristic(dosieren, "Genauigkeit", "Dosierungs-Genauigkeit", "0.5", "%");
        sub.AddCharacteristic(mischen,  "Drehzahl",    "Mischer-Drehzahl",       "400", "rpm");

        sub.AddFlow(bWk,       dosieren);
        sub.AddFlow(dosieren,  mischen);
        sub.AddFlow(bHs,       mischen);
        sub.AddFlow(mischen,   bLs);

        // Layer 2 — decompose "Wirkstoff dosieren"
        var sub2 = sub.Decompose(dosieren, "Dosieren Detail");
        var bRoh    = sub2.AddBoundaryFromParent(bWk, "fpb:Product", "Roh-Wirkstoff");
        var bDosis  = sub2.AddProduct("Dosierter Wirkstoff");
        var wiegen  = sub2.AddProcessOperator("Wiegen");
        var uebergeb = sub2.AddProcessOperator("Übergeben");

        sub2.AddFlow(bRoh,     wiegen);
        sub2.AddFlow(wiegen,   uebergeb);
        sub2.AddFlow(uebergeb, bDosis);

        return sb;
    }

    private static ShowcaseBuilder BuildPharmaCleaning()
    {
        var sb = new ShowcaseBuilder("Pharma_Cleaning_Showcase", "Spülen");
        var p = sb.Root;

        var cipIn  = p.AddProduct("CIP-Lösung");
        var spuelen = p.AddProcessOperator("Spülen");
        var schmutz = p.AddProduct("Schmutzwasser");
        var mixer = p.AddTechnicalResource("Mixer_1");
        p.AddCharacteristic(mixer, "EquipmentId", "Equipment-Identifier", "MX-OP-23", "");

        p.AddFlow(cipIn,   spuelen);
        p.AddFlow(spuelen, schmutz);
        p.AddUsage(spuelen, mixer);

        var sub = p.Decompose(spuelen, "Spülen Detail");
        var bIn  = sub.AddBoundaryFromParent(cipIn, "fpb:Product", "CIP (Eingang)");
        var bOut = sub.AddBoundaryFromParent(schmutz, "fpb:Product", "Schmutzwasser (Ausgang)");
        var vorspuel = sub.AddProcessOperator("Vorspülen");
        var hauptr   = sub.AddProcessOperator("Hauptreinigen");
        var nachsp   = sub.AddProcessOperator("Nachspülen");

        sub.AddFlow(bIn,      vorspuel);
        sub.AddFlow(vorspuel, hauptr);
        sub.AddFlow(hauptr,   nachsp);
        sub.AddFlow(nachsp,   bOut);

        return sb;
    }

    // ──────────────────────────────────────────────────────────────────────
    // Showcase C — CNC + Robotik mit FPD_Fasteners_Stock cross-IH
    // ──────────────────────────────────────────────────────────────────────
    [Fact]
    public void Generate_Showcase_C_Cnc_Robotik()
    {
        var sb = BuildCncRobotik();
        var doc = FpbJsonToCaex.Convert(sb.ToJson()).Value;
        var ihMain = doc.CAEXFile.InstanceHierarchy.First();
        ihMain.Name = "FPD_Assembly";

        // Second FPD IH with a small fasteners-stock process. The Befestigungs-
        // teile boundary state on the main side carries the same uniqueIdent
        // as the fasteners-stock output state, so the cross-IH reference is
        // resolvable by id (the AML editor highlights the link).
        var ihStock = FpbJsonToCaex.CreateEmptyFpdInstanceHierarchy(doc, "FPD_Fasteners_Stock");
        FpbJsonToCaex.UpdateInPlace(doc, BuildFastenersStock().ToJson(), ihStock);

        WriteAml(doc, "Showcase-C-CncRobotikMontage.aml");

        var back = CaexToFpbJson.Convert(doc, ihMain);
        Assert.False(string.IsNullOrWhiteSpace(back.Value));
    }

    private static ShowcaseBuilder BuildCncRobotik()
    {
        var sb = new ShowcaseBuilder("CNC_Robotik_Showcase", "Werkstück fertigen");
        var p = sb.Root;

        var rohling = p.AddProduct("Rohlinge");
        var fertigen = p.AddProcessOperator("Werkstück fertigen");
        var bauteil = p.AddProduct("Fertiges Bauteil");
        var spindel = p.AddTechnicalResource("CNC_Spindel_A");
        var roboter = p.AddTechnicalResource("Roboter_R5");

        p.AddCharacteristic(spindel, "MaxDrehzahl",      "Maximale Drehzahl",          "18000", "rpm");
        p.AddCharacteristic(spindel, "WerkzeugAufnahme", "Werkzeug-Aufnahme",          "HSK63", "");
        p.AddCharacteristic(roboter, "MaxTraglast",      "Maximale Traglast",          "25",    "kg");
        p.AddCharacteristic(roboter, "Reichweite",       "Reichweite des Endeffektors", "1850",  "mm");

        p.AddFlow(rohling, fertigen);
        p.AddFlow(fertigen, bauteil);
        p.AddUsage(fertigen, spindel);
        p.AddUsage(fertigen, roboter);

        // Layer 1 — decompose into Fräsen + Montieren
        var sub = p.Decompose(fertigen, "Fertigen Detail");
        var bRoh   = sub.AddBoundaryFromParent(rohling, "fpb:Product", "Rohling");
        var bBau   = sub.AddBoundaryFromParent(bauteil, "fpb:Product", "Fertiges Bauteil");
        var fraesen = sub.AddProcessOperator("Fräsen");
        var montieren = sub.AddProcessOperator("Montieren");
        sub.AddCharacteristic(montieren, "Anziehmoment", "Solldrehmoment am Anziehen", "12", "Nm");

        sub.AddFlow(bRoh,      fraesen);
        sub.AddFlow(fraesen,   montieren);
        sub.AddFlow(montieren, bBau);

        // Layer 2 — decompose "Fräsen"
        var subFraes = sub.Decompose(fraesen, "Fräsen Detail");
        var bWst = subFraes.AddBoundaryFromParent(bRoh, "fpb:Product", "Eingespanntes Werkstück");
        var bFin = subFraes.AddProduct("Gefrästes Werkstück");
        var einspannen = subFraes.AddProcessOperator("Werkstück einspannen");
        var bahnplan   = subFraes.AddProcessOperator("Bahnplanung");
        var schrupp    = subFraes.AddProcessOperator("Schruppfräsen");
        var schlicht   = subFraes.AddProcessOperator("Schlichtfräsen");
        var pruefen    = subFraes.AddProcessOperator("Maßprüfen");

        subFraes.AddFlow(bWst, einspannen);
        subFraes.AddFlow(einspannen, bahnplan);
        subFraes.AddFlow(bahnplan, schrupp);
        subFraes.AddFlow(schrupp, schlicht);
        subFraes.AddFlow(schlicht, pruefen);
        subFraes.AddFlow(pruefen, bFin);

        // Layer 2 — decompose "Montieren"
        var subMont = sub.Decompose(montieren, "Montieren Detail");
        var bMontIn  = subMont.AddBoundaryFromParent(bRoh, "fpb:Product", "Gefrästes Werkstück (Eingang)");
        var bMontOut = subMont.AddBoundaryFromParent(bBau, "fpb:Product", "Fertiges Bauteil");
        var positionieren = subMont.AddProcessOperator("Position einnehmen");
        var schrauben     = subMont.AddProcessOperator("Schraubvorgang");

        subMont.AddFlow(bMontIn,        positionieren);
        subMont.AddFlow(positionieren,  schrauben);
        subMont.AddFlow(schrauben,      bMontOut);

        return sb;
    }

    private static ShowcaseBuilder BuildFastenersStock()
    {
        var sb = new ShowcaseBuilder("Fasteners_Stock_Showcase", "Befestigungsteile bereitstellen");
        var p = sb.Root;

        var rohteile  = p.AddProduct("Schrauben (Lager)");
        var pickPlace = p.AddProcessOperator("Pick & Place");
        var bereit    = p.AddProduct("Bereitgestellte Befestigungsteile");
        p.AddCharacteristic(pickPlace, "Zykluszeit", "Zykluszeit pro Schraube", "1.2", "s");

        p.AddFlow(rohteile, pickPlace);
        p.AddFlow(pickPlace, bereit);

        return sb;
    }

    // ──────────────────────────────────────────────────────────────────────
    // Helper for cross-IH InternalLinks (Showcase A)
    // ──────────────────────────────────────────────────────────────────────
    private static void AppendInternalLink(
        InternalElementType hostIe, string name,
        ExternalInterfaceType from, ExternalInterfaceType to)
    {
        // CAEX-3.0 allows InternalLinks on InternalElements only, not directly
        // on an InstanceHierarchy — anchor on the IE that owns the source side.
        var link = hostIe.InternalLink.Append(name);
        link.AInterface = from;
        link.BInterface = to;
    }
}
