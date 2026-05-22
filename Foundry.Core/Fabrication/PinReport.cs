using System.Text;

namespace Foundry.Core.Fabrication;

/// <summary>Human-readable netlist / pin-assignment report (PRD v2 G6) as CSV.</summary>
public static class PinReport
{
    public static string Csv(Project.Project project)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Component,Pin,Net,Connected To");
        foreach (var c in project.Connections)
        {
            var dot = c.From.IndexOf('.');
            var comp = dot < 0 ? c.From : c.From[..dot];
            var pin = dot < 0 ? "" : c.From[(dot + 1)..];
            sb.AppendLine($"{Csv(comp)},{Csv(pin)},{Csv(c.Net)},{Csv(c.To)}");
        }
        return sb.ToString();
    }

    private static string Csv(string s)
    {
        s ??= "";
        return s.Contains(',') || s.Contains('"') ? $"\"{s.Replace("\"", "\"\"")}\"" : s;
    }
}
