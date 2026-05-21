using Foundry.Core.Project;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace Foundry.Core.Export;

/// <summary>
/// Branded PDF deliverables (PRD §8.4, F7) — a full project spec sheet and a validation report,
/// rendered with the Foundry look (accent orange, serif headings, mono data) via QuestPDF.
/// </summary>
public static class PdfExporter
{
    // Foundry palette (print-friendly light theme)
    private const string Accent = "#FF5A1F";
    private const string Ink = "#16161C";
    private const string Mute = "#6A6A72";
    private const string Faint = "#9A9AA2";
    private const string Hair = "#E3E3E8";
    private const string Wash = "#FFF3EE";
    private const string Serif = "Georgia";
    private const string Mono = "Consolas";

    static PdfExporter() => QuestPDF.Settings.License = LicenseType.Community;

    public static byte[] ProjectPdf(Project.Project p) =>
        Document.Create(c => c.Page(page =>
        {
            Setup(page);
            page.Header().Element(h => Header(h, "PROJECT SPEC", p.Title));
            page.Footer().Element(Footer);
            page.Content().PaddingVertical(14).Column(col =>
            {
                col.Spacing(16);
                Title(col, p);
                Kpis(col, p);
                Architecture(col, p);
                BomTable(col, p);
                Netlist(col, p);
                Findings(col, p);
                Assembly(col, p);
            });
        })).GeneratePdf();

    public static byte[] ValidationPdf(Project.Project p) =>
        Document.Create(c => c.Page(page =>
        {
            Setup(page);
            page.Header().Element(h => Header(h, "VALIDATION REPORT", p.Title));
            page.Footer().Element(Footer);
            page.Content().PaddingVertical(14).Column(col =>
            {
                col.Spacing(16);
                Title(col, p);
                int fail = p.Findings.Count(f => f.Severity == "fail"), warn = p.Findings.Count(f => f.Severity == "warn"), pass = p.Findings.Count(f => f.Severity == "pass");
                col.Item().Text(t =>
                {
                    t.Span($"Overall: {(fail > 0 ? "FAIL" : warn > 0 ? "WARN" : "PASS")}").FontFamily(Serif).FontSize(20).FontColor(fail > 0 ? "#C0392B" : warn > 0 ? "#B8860B" : "#2E7D32");
                    t.Span($"   {fail} failures · {warn} warnings · {pass} passing · {p.Findings.Count} checks").FontFamily(Mono).FontSize(9).FontColor(Mute);
                });
                Findings(col, p);
            });
        })).GeneratePdf();

    // ---- shared chrome ----
    private static void Setup(PageDescriptor page)
    {
        page.Size(PageSizes.A4);
        page.Margin(40);
        page.DefaultTextStyle(x => x.FontSize(10).FontColor(Ink).FontFamily("Segoe UI"));
    }

    private static void Header(IContainer c, string kind, string project)
    {
        c.PaddingBottom(10).BorderBottom(1).BorderColor(Hair).Row(row =>
        {
            row.AutoItem().PaddingRight(8).AlignMiddle().Width(16).Height(16).Background(Accent);
            row.RelativeItem().AlignMiddle().Text(t =>
            {
                t.Span("FOUNDRY").FontFamily(Mono).FontSize(11).FontColor(Ink).LetterSpacing(0.06f);
                t.Span("  ·  AI hardware design").FontFamily(Mono).FontSize(8).FontColor(Faint);
            });
            row.AutoItem().AlignMiddle().Text(kind).FontFamily(Mono).FontSize(9).FontColor(Accent);
        });
    }

    private static void Footer(IContainer c) =>
        c.PaddingTop(8).BorderTop(1).BorderColor(Hair).Row(row =>
        {
            row.RelativeItem().Text("DESIGN AID · VERIFY BEFORE YOU BUILD").FontFamily(Mono).FontSize(7.5f).FontColor(Faint);
            row.AutoItem().Text(t =>
            {
                t.Span("Foundry · ").FontFamily(Mono).FontSize(7.5f).FontColor(Faint);
                t.CurrentPageNumber().FontFamily(Mono).FontSize(7.5f).FontColor(Mute);
                t.Span(" / ").FontFamily(Mono).FontSize(7.5f).FontColor(Faint);
                t.TotalPages().FontFamily(Mono).FontSize(7.5f).FontColor(Mute);
            });
        });

    private static void Title(ColumnDescriptor col, Project.Project p)
    {
        col.Item().Text(p.Title).FontFamily(Serif).FontSize(28).FontColor(Ink);
        if (!string.IsNullOrWhiteSpace(p.Prompt))
            col.Item().PaddingTop(2).Text(p.Prompt).FontSize(10).Italic().FontColor(Mute);
    }

    private static void SectionTitle(ColumnDescriptor col, string text) =>
        col.Item().PaddingTop(6).Text(text.ToUpperInvariant()).FontFamily(Mono).FontSize(8.5f).FontColor(Accent).LetterSpacing(0.08f);

    private static void Kpis(ColumnDescriptor col, Project.Project p)
    {
        col.Item().Row(row =>
        {
            void Cell(string label, string value)
            {
                row.RelativeItem().Background(Wash).Padding(10).Column(cc =>
                {
                    cc.Item().Text(label.ToUpperInvariant()).FontFamily(Mono).FontSize(7.5f).FontColor(Mute);
                    cc.Item().Text(value).FontFamily(Serif).FontSize(18).FontColor(Ink);
                });
            }
            Cell("Parts", p.Kpis.Parts.ToString());
            row.ConstantItem(8);
            Cell("Cost", $"${p.Kpis.Cost:0.00}");
            row.ConstantItem(8);
            Cell("Active draw", $"{p.Kpis.CurrentMa} mA");
            row.ConstantItem(8);
            Cell("Print", $"{p.Kpis.PrintGrams} g");
        });
    }

    private static void Architecture(ColumnDescriptor col, Project.Project p)
    {
        if (p.Subsystems.Count == 0) return;
        SectionTitle(col, "Architecture");
        foreach (var s in p.Subsystems)
            col.Item().BorderBottom(1).BorderColor(Hair).PaddingVertical(5).Row(row =>
            {
                row.ConstantItem(90).Text(s.Role).FontFamily(Mono).FontSize(9).FontColor(Accent);
                row.RelativeItem().Text(s.Name).FontSize(10).FontColor(Ink);
                row.AutoItem().Text(s.Mpn).FontFamily(Mono).FontSize(8.5f).FontColor(Mute);
            });
    }

    private static void BomTable(ColumnDescriptor col, Project.Project p)
    {
        if (p.Bom.Count == 0) return;
        SectionTitle(col, "Bill of materials");
        col.Item().Table(table =>
        {
            table.ColumnsDefinition(c => { c.ConstantColumn(34); c.RelativeColumn(); c.ConstantColumn(110); c.ConstantColumn(55); c.ConstantColumn(60); });
            table.Cell().BorderBottom(1).BorderColor(Hair).PaddingVertical(4).Text("QTY").FontFamily(Mono).FontSize(7.5f).FontColor(Mute);
            table.Cell().BorderBottom(1).BorderColor(Hair).PaddingVertical(4).Text("COMPONENT").FontFamily(Mono).FontSize(7.5f).FontColor(Mute);
            table.Cell().BorderBottom(1).BorderColor(Hair).PaddingVertical(4).Text("MPN").FontFamily(Mono).FontSize(7.5f).FontColor(Mute);
            table.Cell().BorderBottom(1).BorderColor(Hair).PaddingVertical(4).AlignRight().Text("UNIT").FontFamily(Mono).FontSize(7.5f).FontColor(Mute);
            table.Cell().BorderBottom(1).BorderColor(Hair).PaddingVertical(4).AlignRight().Text("EXT").FontFamily(Mono).FontSize(7.5f).FontColor(Mute);

            foreach (var b in p.Bom)
            {
                table.Cell().BorderBottom(1).BorderColor("#F2F2F4").PaddingVertical(4).Text($"{b.Qty}×").FontFamily(Mono).FontSize(9);
                table.Cell().BorderBottom(1).BorderColor("#F2F2F4").PaddingVertical(4).Text(b.Name).FontSize(9.5f);
                table.Cell().BorderBottom(1).BorderColor("#F2F2F4").PaddingVertical(4).Text(b.Mpn).FontFamily(Mono).FontSize(8).FontColor(Mute);
                table.Cell().BorderBottom(1).BorderColor("#F2F2F4").PaddingVertical(4).AlignRight().Text($"${b.Price:0.00}").FontFamily(Mono).FontSize(9);
                table.Cell().BorderBottom(1).BorderColor("#F2F2F4").PaddingVertical(4).AlignRight().Text($"${b.Qty * b.Price:0.00}").FontFamily(Mono).FontSize(9);
            }
            table.Cell().ColumnSpan(4).PaddingVertical(5).AlignRight().Text("Subtotal").FontFamily(Mono).FontSize(8.5f).FontColor(Mute);
            table.Cell().PaddingVertical(5).AlignRight().Text($"${p.Bom.Sum(b => b.Qty * b.Price):0.00}").FontFamily(Serif).FontSize(12).FontColor(Accent);
        });
    }

    private static void Netlist(ColumnDescriptor col, Project.Project p)
    {
        if (p.Connections.Count == 0) return;
        SectionTitle(col, $"Netlist · {p.Connections.Count} nets");
        foreach (var c in p.Connections)
            col.Item().PaddingVertical(2).Row(row =>
            {
                row.RelativeItem().Text($"{c.From}  →  {c.To}").FontFamily(Mono).FontSize(8.5f).FontColor(Ink);
                row.ConstantItem(70).AlignRight().Text(c.Net).FontFamily(Mono).FontSize(8.5f).FontColor(NetColor(c.Net));
            });
    }

    private static void Findings(ColumnDescriptor col, Project.Project p)
    {
        if (p.Findings.Count == 0) return;
        SectionTitle(col, "Validation");
        foreach (var f in p.Findings)
            col.Item().BorderBottom(1).BorderColor(Hair).PaddingVertical(5).Row(row =>
            {
                row.ConstantItem(40).Text(f.Num).FontFamily(Mono).FontSize(9).FontColor(SevColor(f.Severity));
                row.RelativeItem().Column(cc =>
                {
                    cc.Item().Text(f.Title).FontSize(9.5f).FontColor(Ink);
                    if (!string.IsNullOrWhiteSpace(f.Description))
                        cc.Item().Text(f.Description).FontSize(8).FontColor(Mute);
                    if (!string.IsNullOrWhiteSpace(f.Fix))
                        cc.Item().Text($"Fix: {f.Fix}").FontFamily(Mono).FontSize(8).FontColor(Accent);
                });
                row.ConstantItem(50).AlignRight().Text(f.Code).FontFamily(Mono).FontSize(7.5f).FontColor(Faint);
            });
    }

    private static void Assembly(ColumnDescriptor col, Project.Project p)
    {
        if (p.Assembly.Count == 0) return;
        SectionTitle(col, "Assembly guide");
        foreach (var s in p.Assembly)
            col.Item().PaddingVertical(4).Row(row =>
            {
                row.ConstantItem(34).Text($"{s.N:00}").FontFamily(Serif).FontSize(16).FontColor(Accent);
                row.RelativeItem().Column(cc =>
                {
                    cc.Item().Text(s.Title).FontSize(11).FontColor(Ink);
                    if (!string.IsNullOrWhiteSpace(s.Body))
                        cc.Item().Text(s.Body).FontSize(9).FontColor(Mute);
                });
            });
    }

    private static string NetColor(string net) => net switch { "power" => "#C0392B", "ground" => "#6A6A72", "i2c" => "#7D3CC0", _ => "#1F7AA8" };
    private static string SevColor(string sev) => sev switch { "fail" => "#C0392B", "warn" => "#B8860B", "pass" => "#2E7D32", _ => Mute };
}
