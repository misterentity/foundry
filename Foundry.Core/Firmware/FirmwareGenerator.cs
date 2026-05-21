using System.Text;
using Foundry.Core.Kb;
using Foundry.Core.Project;

namespace Foundry.Core.Firmware;

public enum FirmwarePlatform { ArduinoCpp, MicroPython }

/// <summary>
/// Generates a starter firmware project from the netlist + parts (PRD §8.6, F4). The pin map is
/// derived from <see cref="PinMap"/> (single source of truth — no hand-typed pins); the sketch,
/// secrets header, and build config are templated around it. Arduino C++ is the default; a
/// MicroPython variant is produced when requested.
/// </summary>
public static class FirmwareGenerator
{
    public static Foundry.Core.Project.Firmware Generate(
        IReadOnlyList<Connection> connections, ComponentKb kb, FirmwarePlatform platform = FirmwarePlatform.ArduinoCpp)
    {
        var entries = PinMap.Build(connections, kb);
        return platform == FirmwarePlatform.MicroPython
            ? Micropython(entries)
            : Arduino(entries);
    }

    // ---------- Arduino C++ ----------
    private static Foundry.Core.Project.Firmware Arduino(IReadOnlyList<PinMapEntry> entries)
    {
        var analog = entries.FirstOrDefault(e => e.Net == "signal");
        var sketch = new StringBuilder();
        sketch.AppendLine("// FOUNDRY · generated starter sketch");
        sketch.AppendLine("// Pin map is GENERATED from the netlist — do not edit pins by hand. See pinmap.h");
        sketch.AppendLine();
        sketch.AppendLine("#include <WiFi.h>");
        sketch.AppendLine("#include <HTTPClient.h>");
        sketch.AppendLine("#include <ArduinoJson.h>");
        sketch.AppendLine("#include \"pinmap.h\"");
        sketch.AppendLine("#include \"wifi.h\"");
        sketch.AppendLine();
        sketch.AppendLine("constexpr uint64_t SLEEP_US = 6ULL * 60ULL * 60ULL * 1000000ULL;  // 6 h");
        sketch.AppendLine("constexpr float DRY_THRESHOLD = 0.32f;");
        sketch.AppendLine();
        sketch.AppendLine("void setup() {");
        sketch.AppendLine("  Serial.begin(115200);");
        foreach (var e in entries)
            sketch.AppendLine($"  pinMode({e.Macro}, {(e.Net == "signal" ? "INPUT" : "INPUT_PULLUP")});");
        sketch.AppendLine("  analogReadResolution(12);");
        sketch.AppendLine();
        if (analog is not null)
        {
            sketch.AppendLine("  float reading = readAnalog();");
            sketch.AppendLine("  if (reading < DRY_THRESHOLD) {");
            sketch.AppendLine("    alertWebhook(reading);  // TODO: configure webhook in wifi.h");
            sketch.AppendLine("  }");
            sketch.AppendLine();
        }
        sketch.AppendLine("  esp_deep_sleep(SLEEP_US);");
        sketch.AppendLine("}");
        sketch.AppendLine();
        sketch.AppendLine("void loop() {}");
        if (analog is not null)
        {
            sketch.AppendLine();
            sketch.AppendLine("float readAnalog() {");
            sketch.AppendLine($"  int raw = analogRead({analog.Macro});");
            sketch.AppendLine("  return 1.0f - ((float)raw / 4095.0f);  // dry→0, wet→1");
            sketch.AppendLine("}");
        }

        return new Foundry.Core.Project.Firmware
        {
            Platform = "Arduino C++",
            Board = "esp32:esp32:esp32",
            Files = new()
            {
                new FirmwareFile { Name = "main.ino", Path = "/foundry/firmware/", Active = true, Content = sketch.ToString() },
                new FirmwareFile { Name = "pinmap.h", Path = "/foundry/firmware/", Content = PinMap.RenderHeader(entries) },
                new FirmwareFile { Name = "wifi.h",   Path = "/foundry/firmware/", Content = WifiHeader() },
                new FirmwareFile { Name = "platformio.ini", Path = "/foundry/firmware/", Content = PlatformIo() },
            },
            Libraries = new()
            {
                new("WiFi", "built-in"), new("HTTPClient", "built-in"), new("ArduinoJson", "7.1.0"),
                new("esp32-hal-adc", "built-in"), new("ESP32 Deep Sleep", "built-in"),
            },
        };
    }

    private static string WifiHeader() =>
        "#pragma once\n\n" +
        "// TODO: fill in your secrets — these are NEVER written to the Project file.\n" +
        "#define WIFI_SSID          \"YOUR_SSID\"\n#define WIFI_PASS          \"YOUR_PASSWORD\"\n\n" +
        "// HTTPS webhook (e.g. Twilio)\n#define WEBHOOK_URL        \"https://example.com/alert\"\n";

    private static string PlatformIo() =>
        "[env:esp32dev]\nplatform   = espressif32@^6.5.0\nboard      = esp32dev\n" +
        "framework  = arduino\nmonitor_speed = 115200\nupload_speed  = 460800\n" +
        "lib_deps =\n  bblanchon/ArduinoJson@^7.1.0\nbuild_flags =\n  -D CONFIG_DEEP_SLEEP\n";

    // ---------- MicroPython ----------
    private static Foundry.Core.Project.Firmware Micropython(IReadOnlyList<PinMapEntry> entries)
    {
        var pinmap = new StringBuilder();
        pinmap.AppendLine("# GENERATED — derived from Project.connections. Do not edit.");
        foreach (var e in entries)
            pinmap.AppendLine($"{e.Macro} = {e.Gpio}  # {e.Net}: {e.FromPin} <-> {e.ToPin}" + (e.Strapping ? "  [strapping]" : ""));

        var main = new StringBuilder();
        main.AppendLine("# FOUNDRY · generated MicroPython starter");
        main.AppendLine("import time, machine");
        main.AppendLine("from pinmap import *");
        main.AppendLine("import config  # TODO: fill in secrets in config.py");
        main.AppendLine();
        var analog = entries.FirstOrDefault(e => e.Net == "signal");
        if (analog is not null)
        {
            main.AppendLine($"adc = machine.ADC(machine.Pin({analog.Macro}))");
            main.AppendLine("adc.atten(machine.ADC.ATTN_11DB)");
            main.AppendLine();
            main.AppendLine("def read_analog():");
            main.AppendLine("    return 1.0 - (adc.read() / 4095.0)  # dry->0, wet->1");
            main.AppendLine();
            main.AppendLine("reading = read_analog()");
            main.AppendLine("if reading < 0.32:");
            main.AppendLine("    pass  # TODO: send webhook alert (see config.py)");
            main.AppendLine();
        }
        main.AppendLine("machine.deepsleep(6 * 60 * 60 * 1000)  # 6 h");

        return new Foundry.Core.Project.Firmware
        {
            Platform = "MicroPython",
            Board = "esp32",
            Files = new()
            {
                new FirmwareFile { Name = "main.py", Path = "/foundry/firmware/", Active = true, Content = main.ToString() },
                new FirmwareFile { Name = "pinmap.py", Path = "/foundry/firmware/", Content = pinmap.ToString() },
                new FirmwareFile { Name = "config.py", Path = "/foundry/firmware/",
                    Content = "# TODO: secrets — never written to the Project file.\nWIFI_SSID = \"YOUR_SSID\"\nWIFI_PASS = \"YOUR_PASSWORD\"\nWEBHOOK_URL = \"https://example.com/alert\"\n" },
            },
            Libraries = new() { new("urequests", "built-in"), new("machine", "built-in"), new("network", "built-in") },
        };
    }
}

/// <summary>Writes a generated <see cref="Foundry.Core.Project.Firmware"/> to a project folder (F7).</summary>
public static class FirmwareExporter
{
    public static void Export(Foundry.Core.Project.Firmware firmware, string folder)
    {
        Directory.CreateDirectory(folder);
        foreach (var f in firmware.Files)
            File.WriteAllText(Path.Combine(folder, f.Name), f.Content);
    }
}
