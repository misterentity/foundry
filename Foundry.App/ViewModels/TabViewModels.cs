using CommunityToolkit.Mvvm.ComponentModel;
using Foundry.Core.Project;

namespace Foundry.App.ViewModels;

/// <summary>Base for the workspace tab view models — all read the canonical Project.</summary>
public abstract class TabViewModelBase : ObservableObject
{
    protected TabViewModelBase(Project project) => Project = project;
    public Project Project { get; }
}

// ---------------- Overview ----------------
public sealed class SourcingRow
{
    public required string Distributor { get; init; }
    public required int Lines { get; init; }
    public required double Cost { get; init; }
    public required string Status { get; init; } // ok | warn
    public string CostText => $"${Cost:0.00}";
    public string LinesText => $"{Lines} lines";
    public string StatusText => Status == "ok" ? "ready" : "low stock";
}

public sealed class OverviewViewModel : TabViewModelBase
{
    public OverviewViewModel(Project project) : base(project)
    {
        TopFindings = project.Findings.Take(3).ToList();
    }

    public IReadOnlyList<Finding> TopFindings { get; }
    public string CostText => $"${Project.Kpis.Cost:0.00}";

    public IReadOnlyList<SourcingRow> Sourcing { get; } = new[]
    {
        new SourcingRow { Distributor="DigiKey", Lines=4, Cost=18.13, Status="ok" },
        new SourcingRow { Distributor="Mouser",  Lines=3, Cost=8.61,  Status="ok" },
        new SourcingRow { Distributor="Amazon",  Lines=2, Cost=11.68, Status="warn" },
    };
}

// ---------------- BOM ----------------
public sealed class BomViewModel : TabViewModelBase
{
    public BomViewModel(Project project) : base(project) { }
    public double Total => Project.Bom.Sum(l => l.Extended);
    public string TotalText => $"${Total:0.00}";
    public int Units => Project.Bom.Sum(l => l.Qty);
    public string SubtotalLabel => $"Subtotal · {Project.Bom.Count} lines · {Units} units";
}

// ---------------- Wiring ----------------
public sealed class WiringViewModel : TabViewModelBase
{
    public WiringViewModel(Project project) : base(project) { }
    public int NetCount => Project.Connections.Count;
}

// ---------------- Enclosure ----------------
public sealed partial class EnclosureViewModel : TabViewModelBase
{
    [ObservableProperty] private string _view = "ISO";
    public EnclosureViewModel(Project project) : base(project) { }
    public Enclosure E => Project.Enclosure;
    public string WallText => E.Wall.ToString("0.0");
    public string LengthText => E.Inner[0].ToString("0");
    public string WidthText => E.Inner[1].ToString("0");
    public string HeightText => E.Inner[2].ToString("0");
}

// ---------------- Firmware ----------------
public sealed partial class FirmwareViewModel : TabViewModelBase
{
    [ObservableProperty] private FirmwareFile _activeFile;

    public FirmwareViewModel(Project project) : base(project)
    {
        // populate code bodies for the demo (Phase 3 will generate these from the netlist)
        for (int i = 0; i < project.Firmware.Files.Count && i < FileBodies.Length; i++)
            project.Firmware.Files[i].Content = FileBodies[i];
        _activeFile = project.Firmware.Files[0];
    }

    public Firmware F => Project.Firmware;

    private static readonly string[] FileBodies =
    {
        // main.ino
        "// FOUNDRY · Cap. Soil Moisture Sentinel\n" +
        "// Pin map is GENERATED from the netlist — do not edit by hand.\n" +
        "// See pinmap.h\n\n" +
        "#include <WiFi.h>\n#include <HTTPClient.h>\n#include <ArduinoJson.h>\n" +
        "#include \"pinmap.h\"\n#include \"wifi.h\"\n\n" +
        "constexpr uint64_t SLEEP_US = 6ULL * 60ULL * 60ULL * 1000000ULL;  // 6 h\n" +
        "constexpr float DRY_THRESHOLD = 0.32f;\n\n" +
        "void setup() {\n  Serial.begin(115200);\n  pinMode(PIN_SENSOR_AOUT, INPUT);\n" +
        "  analogReadResolution(12);\n\n  float moisture = readMoisture();\n" +
        "  if (moisture < DRY_THRESHOLD) {\n    alertTwilio(moisture);\n  }\n\n" +
        "  esp_deep_sleep(SLEEP_US);\n}\n\n" +
        "float readMoisture() {\n  int raw = analogRead(PIN_SENSOR_AOUT);\n" +
        "  return 1.0f - ((float)raw / 4095.0f);  // dry→0, wet→1\n}\n",
        // pinmap.h
        "// GENERATED — derived from Project.connections\n" +
        "// Do not edit; re-runs on every wiring change.\n\n#pragma once\n\n" +
        "// from net: SIGNAL · MCU.GPIO34 ↔ SENSOR.AOUT\n#define PIN_SENSOR_AOUT  34\n\n" +
        "// from net: SIGNAL · MCU.GPIO0 ↔ BTN1.A    [strapping pin — see W·04]\n#define PIN_BUTTON_RST   0\n\n" +
        "// Power · Ground rails — informational\n#define RAIL_3V3_MV       3300\n#define RAIL_GND_MV       0\n\n" +
        "// ADC reference (ESP32 default attenuation 11dB → ~3.3V)\n#define ADC_REF_MV        3300\n",
        // wifi.h
        "#pragma once\n\n" +
        "// TODO: fill in your secrets — these are NEVER written to the Project file.\n" +
        "#define WIFI_SSID          \"YOUR_SSID\"\n#define WIFI_PASS          \"YOUR_PASSWORD\"\n\n" +
        "// Twilio HTTPS webhook\n#define TWILIO_SID         \"ACxxxxxxxxxxxxxxxxxx\"\n" +
        "#define TWILIO_TOKEN       \"xxxxxxxxxxxxxxxxxxxx\"\n#define TWILIO_FROM        \"+15555550100\"\n" +
        "#define ALERT_TO           \"+15555550199\"\n",
        // platformio.ini
        "[env:esp32dev]\nplatform   = espressif32@^6.5.0\nboard      = esp32dev\n" +
        "framework  = arduino\nmonitor_speed = 115200\nupload_speed  = 460800\n" +
        "lib_deps =\n  bblanchon/ArduinoJson@^7.1.0\nbuild_flags =\n  -D CONFIG_DEEP_SLEEP\n",
    };
}

// ---------------- Validation ----------------
public sealed class PowerSlice
{
    public required string Label { get; init; }
    public required int Ma { get; init; }
    public required string BrushKey { get; init; }
}

public sealed class ValidationViewModel : TabViewModelBase
{
    public ValidationViewModel(Project project) : base(project) { }

    public int FailCount => Project.Findings.Count(f => f.Severity == "fail");
    public int WarnCount => Project.Findings.Count(f => f.Severity == "warn");
    public int PassCount => Project.Findings.Count(f => f.Severity == "pass");
    public string OverallStatus => FailCount > 0 ? "FAIL" : WarnCount > 0 ? "WARN" : "PASS";
    public string PassText => $"{PassCount} / 27";

    public IReadOnlyList<PowerSlice> PowerBudget { get; } = new[]
    {
        new PowerSlice { Label="Wi-Fi TX",    Ma=48, BrushKey="Brush.Accent" },
        new PowerSlice { Label="MCU active",  Ma=18, BrushKey="Brush.Info" },
        new PowerSlice { Label="Sensor read", Ma=12, BrushKey="Brush.Ok" },
        new PowerSlice { Label="ADC + boost", Ma=4,  BrushKey="Brush.Warn" },
        new PowerSlice { Label="Quiescent",   Ma=2,  BrushKey="Brush.InkMute" },
    };
    public int PowerTotal => 84;
}

// ---------------- Guide ----------------
public sealed class GuideViewModel : TabViewModelBase
{
    public GuideViewModel(Project project) : base(project) { }
}
