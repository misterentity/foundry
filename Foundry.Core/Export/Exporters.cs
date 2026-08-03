using System.Text;
using Foundry.Core.Project;

namespace Foundry.Core.Export;

/// <summary>Disk exporters for the Project's outputs (PRD §8.4, F7).</summary>
public static class Exporters
{
    /// <summary>
    /// Full BOM as CSV. This is the file someone actually orders from, so an estimate must not leave here
    /// looking like a distributor lookup: <c>Source</c> says LIVE or EST per line, and Stock/Lead are left
    /// EMPTY on an estimate rather than exporting a number the model invented.
    /// </summary>
    public static string BomCsv(Project.Project project)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Qty,Component,MPN,Unit,Extended,Source,Stock,Distributor,Lead,Note");
        foreach (var l in project.Bom)
            sb.AppendLine(string.Join(",",
                l.Qty, Csv(l.Name), Csv(l.Mpn), l.Price.ToString("0.00"),
                (l.Qty * l.Price).ToString("0.00"),
                Sourcing.BomPricing.SourceTag(l),
                l.IsLive ? l.Stock.ToString() : "",
                Csv(l.Dist), Csv(l.IsLive ? l.Lead : ""), Csv(l.Note)));
        var total = project.Bom.Sum(l => l.Qty * l.Price);
        sb.AppendLine($",,,,{total:0.00},,,,,Subtotal");
        return sb.ToString();
    }

    /// <summary>Assembly guide as Markdown, with the design-aid disclaimer (PRD §10/§13).</summary>
    public static string GuideMarkdown(Project.Project project)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# {project.Title} — Assembly Guide");
        sb.AppendLine();
        sb.AppendLine($"> **Design aid — verify before you build.** Foundry's outputs are a starting point, " +
                      "not a manufacturable spec. Verify polarity, voltage, and your power supply before applying power.");
        sb.AppendLine();
        sb.AppendLine($"_{project.Prompt}_");
        sb.AppendLine();
        foreach (var step in project.Assembly)
        {
            sb.AppendLine($"## {step.N:00} · {step.Title}");
            sb.AppendLine();
            sb.AppendLine(step.Body);
            if (step.Chips.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine(string.Join(" · ", step.Chips.Select(c => $"`{c}`")));
            }
            sb.AppendLine();
        }
        return sb.ToString();
    }

    /// <summary>Validation report as Markdown: rollup + every finding with its fix.</summary>
    public static string ValidationReport(Project.Project project)
    {
        var f = project.Findings;
        int fail = f.Count(x => x.Severity == "fail"), warn = f.Count(x => x.Severity == "warn"), pass = f.Count(x => x.Severity == "pass");
        var sb = new StringBuilder();
        sb.AppendLine($"# {project.Title} — Validation Report");
        sb.AppendLine();
        sb.AppendLine($"> **Design aid — verify before you build.** Deterministic checks over the netlist; not a substitute for review.");
        sb.AppendLine();
        sb.AppendLine($"**Overall: {(fail > 0 ? "FAIL" : warn > 0 ? "WARN" : "PASS")}** — {fail} failures · {warn} warnings · {pass} passing · {f.Count} checks.");
        sb.AppendLine();
        foreach (var x in f)
        {
            sb.AppendLine($"## [{x.Severity.ToUpperInvariant()}] {x.Code} — {x.Title}");
            sb.AppendLine();
            if (!string.IsNullOrWhiteSpace(x.Description)) { sb.AppendLine(x.Description); sb.AppendLine(); }
            if (x.Refs.Count > 0) sb.AppendLine($"Refs: {string.Join(", ", x.Refs.Select(r => $"`{r}`"))}");
            if (!string.IsNullOrWhiteSpace(x.Fix)) sb.AppendLine($"Suggested fix: **{x.Fix}**");
            sb.AppendLine();
        }
        return sb.ToString();
    }

    private static string Csv(string field) =>
        field.Contains(',') || field.Contains('"') || field.Contains('\n')
            ? "\"" + field.Replace("\"", "\"\"") + "\""
            : field;
}
