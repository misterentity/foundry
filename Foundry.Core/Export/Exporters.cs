using System.Text;
using Foundry.Core.Project;

namespace Foundry.Core.Export;

/// <summary>Disk exporters for the Project's outputs (PRD §8.4, F7).</summary>
public static class Exporters
{
    /// <summary>Full BOM as CSV (qty, component, MPN, unit, extended, distributor, lead).</summary>
    public static string BomCsv(Project.Project project)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Qty,Component,MPN,Unit,Extended,Stock,Distributor,Lead,Note");
        foreach (var l in project.Bom)
            sb.AppendLine(string.Join(",",
                l.Qty, Csv(l.Name), Csv(l.Mpn), l.Price.ToString("0.00"),
                (l.Qty * l.Price).ToString("0.00"), l.Stock, Csv(l.Dist), Csv(l.Lead), Csv(l.Note)));
        var total = project.Bom.Sum(l => l.Qty * l.Price);
        sb.AppendLine($",,,,{total:0.00},,,,Subtotal");
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

    private static string Csv(string field) =>
        field.Contains(',') || field.Contains('"') || field.Contains('\n')
            ? "\"" + field.Replace("\"", "\"\"") + "\""
            : field;
}
