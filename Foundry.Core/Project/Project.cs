using System.Text.Json.Serialization;

namespace Foundry.Core.Project;

/// <summary>
/// The canonical Project document (PRD §6) — the single source of truth every
/// generator reads from and writes to. Shaped to also carry the presentation
/// fields the prototype binds (Kpis, Subsystems, Bom, Findings) so the UI maps
/// 1:1 without an extra view-model translation layer.
///
/// Invariant (PRD §6): <see cref="Connections"/> and component pins are
/// authoritative; wiring, pin maps, validation, and enclosure cutouts are derived.
/// </summary>
public sealed class Project
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string Prompt { get; set; } = "";

    /// <summary>READY | DRAFT | GENERATING …</summary>
    public string Status { get; set; } = "DRAFT";

    /// <summary>pass | warn | fail (overall validation rollup).</summary>
    public string Validation { get; set; } = "pass";

    public string Updated { get; set; } = "";

    public ProjectKpis Kpis { get; set; } = new();
    public List<Subsystem> Subsystems { get; set; } = new();
    public List<BomLine> Bom { get; set; } = new();
    public List<Connection> Connections { get; set; } = new();

    /// <summary>Component specs (pins, logic levels) — the KB for validation/firmware/wiring (PRD §6).</summary>
    public List<Kb.ComponentSpec> Components { get; set; } = new();
    public Enclosure Enclosure { get; set; } = new();
    public Firmware Firmware { get; set; } = new();
    public List<Finding> Findings { get; set; } = new();
    public List<AssemblyStep> Assembly { get; set; } = new();
    public List<ChatMessage> Chat { get; set; } = new();
}

public sealed class ProjectKpis
{
    public int Parts { get; set; }
    public double Cost { get; set; }
    public int CurrentMa { get; set; }
    public int BatteryDays { get; set; }
    public int PrintGrams { get; set; }
}

public sealed class Subsystem
{
    public string Id { get; set; } = "";
    public string Role { get; set; } = "";
    public string Name { get; set; } = "";
    public string Mpn { get; set; } = "";
    /// <summary>label/value spec pairs shown in the Overview architecture grid.</summary>
    public List<SpecPair> Specs { get; set; } = new();
}

public sealed class SpecPair
{
    public SpecPair() { }
    public SpecPair(string key, string value) { Key = key; Value = value; }
    public string Key { get; set; } = "";
    public string Value { get; set; } = "";
}

public sealed class BomLine
{
    public int Qty { get; set; }
    public string Name { get; set; } = "";
    public string Mpn { get; set; } = "";
    public double Price { get; set; }
    public int Stock { get; set; }
    public string Lead { get; set; } = "";
    public string Dist { get; set; } = "";
    public string Note { get; set; } = "";

    [JsonIgnore] public double Extended => Qty * Price;
    [JsonIgnore] public bool LowStock => Stock < 100;
}

/// <summary>A net in the authoritative netlist (PRD §6 connections).</summary>
public sealed class Connection
{
    public string From { get; set; } = "";
    public string To { get; set; } = "";
    /// <summary>power | ground | signal | i2c</summary>
    public string Net { get; set; } = "signal";
}

public sealed class Enclosure
{
    /// <summary>inner [L, W, H] mm.</summary>
    public double[] Inner { get; set; } = new double[] { 0, 0, 0 };
    public double Wall { get; set; } = 2.0;
    /// <summary>snap | screw</summary>
    public string Lid { get; set; } = "snap";
    public List<Cutout> Cutouts { get; set; } = new();
    public int Standoffs { get; set; }
    /// <summary>Ventilation slot groups.</summary>
    public List<Vent> Vents { get; set; } = new();
    /// <summary>none | wall-tabs | flange — external mounting style.</summary>
    public string Mount { get; set; } = "none";
    public int MassGrams { get; set; }
    public string PrintTime { get; set; } = "";
    /// <summary>Optional AI-generated parametric OpenSCAD (v2 Phase B "Advanced" mode); empty for schema-only.</summary>
    public string Scad { get; set; } = "";
}

public sealed class Vent
{
    /// <summary>front | back | left | right | top | bottom</summary>
    public string Face { get; set; } = "left";
    public int Count { get; set; } = 4;
}

public sealed class Cutout
{
    /// <summary>side | top | bottom | front …</summary>
    public string Face { get; set; } = "side";
    /// <summary>rect | circle</summary>
    public string Shape { get; set; } = "rect";
    /// <summary>[w, h] for rect.</summary>
    public double[]? Size { get; set; }
    /// <summary>diameter for circle.</summary>
    public double? D { get; set; }
    public double[] Pos { get; set; } = new double[] { 0, 0 };
    public string Label { get; set; } = "";

    /// <summary>Human dimension string for the readout, e.g. "9.5 × 6.5 mm" or "⌀ 12 mm".</summary>
    [JsonIgnore]
    public string DimsText => Shape == "circle"
        ? $"⌀ {D} mm"
        : Size is { Length: >= 2 } s ? $"{s[0]} × {s[1]} mm" : "";
}

public sealed class Firmware
{
    public string Platform { get; set; } = "Arduino C++";
    public string Board { get; set; } = "";
    public List<FirmwareFile> Files { get; set; } = new();
    /// <summary>[name, version] library pairs.</summary>
    public List<SpecPair> Libraries { get; set; } = new();
}

public sealed class FirmwareFile
{
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public bool Active { get; set; }
    /// <summary>Generated source (Phase 3 fills this from the netlist).</summary>
    public string Content { get; set; } = "";
}

/// <summary>A deterministic validation finding (PRD §8.8).</summary>
public sealed class Finding
{
    /// <summary>info | warn | fail | pass | unproven.
    /// <c>unproven</c> means the engine could not obtain a fact it needed — NOT that the design is fine.
    /// It must never roll up to "pass"; see <see cref="Foundry.Core.Validation.ProjectValidator.Rollup"/>.</summary>
    public string Severity { get; set; } = "info";
    public string Code { get; set; } = "";
    /// <summary>Short display token, e.g. "W·02" / "OK".</summary>
    public string Num { get; set; } = "";
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public List<string> Refs { get; set; } = new();
    /// <summary>Suggested auto-fix label, or null when no action needed.</summary>
    public string? Fix { get; set; }

    /// <summary>True when the rules engine can apply this fix deterministically (pin remap / rail connect).</summary>
    [JsonIgnore]
    public bool AutoFixable => Code is "PIN-04" or "PIN-IO" or "PIN-CONF" or "PWR-NC" or "GND-NC";

    /// <summary>
    /// Advisory findings have no design-edit resolution — they depend on firmware behaviour or usage
    /// (e.g. battery life vs. sleep duty cycle) or are sourcing notes. The UI shows them as guidance and
    /// does NOT offer "Apply &amp; re-run", since an AI design edit can never clear them.
    /// </summary>
    [JsonIgnore]
    // FIT-UNDER is guidance until standoff height is a real parameter (see PLAN-v3 A1): the resolution is
    // a geometry change, not a design edit, so offering "Apply & re-run" would route to an AI fix that
    // cannot satisfy it.
    public bool Advisory => Severity is "info" or "unproven" || Code is "PWR-02" or "BOM-01" or "FIT-UNDER";
}

public sealed class AssemblyStep
{
    public int N { get; set; }
    public string Title { get; set; } = "";
    public string Body { get; set; } = "";
    public List<string> Chips { get; set; } = new();
}

public sealed class ChatMessage
{
    /// <summary>user | assistant</summary>
    public string Role { get; set; } = "assistant";
    public string Text { get; set; } = "";
    public string Time { get; set; } = "";
    /// <summary>Per-stage pipeline state shown under assistant turns.</summary>
    public List<PipelineStage>? Pipeline { get; set; }
}

public sealed class PipelineStage
{
    public PipelineStage() { }
    public PipelineStage(string stage, string state) { Stage = stage; State = state; }
    public string Stage { get; set; } = "";
    /// <summary>done | live | pending</summary>
    public string State { get; set; } = "pending";
}
