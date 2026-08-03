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
        bool hasAnalog = entries.Any(e => e.Dir == "analog");
        bool hasI2c = entries.Any(e => e.Dir == "i2c");
        var ios = entries.Where(e => e.Dir != "i2c").ToList();

        var s = new StringBuilder();
        s.AppendLine("// FOUNDRY · generated starter sketch");
        s.AppendLine("// setup()/loop() are scaffolded from the netlist; pin map is in pinmap.h (do not hand-edit pins).");
        s.AppendLine();
        s.AppendLine("#include <Arduino.h>");
        s.AppendLine("#include \"pinmap.h\"");
        if (hasI2c) s.AppendLine("#include <Wire.h>");
        s.AppendLine();
        s.AppendLine("void setup() {");
        s.AppendLine("  Serial.begin(115200);");
        if (hasAnalog) s.AppendLine("  analogReadResolution(12);");
        if (hasI2c) s.AppendLine("  Wire.begin();  // I2C bus");
        foreach (var e in ios)
        {
            var mode = e.Dir == "output" ? "OUTPUT" : "INPUT";
            s.AppendLine($"  pinMode({e.Macro}, {mode});  // {e.ToPin}");
        }
        s.AppendLine("}");
        s.AppendLine();
        s.AppendLine("void loop() {");
        foreach (var e in ios)
        {
            if (e.Dir == "analog")
                s.AppendLine($"  int {VarName(e)} = analogRead({e.Macro});  Serial.printf(\"{VarName(e)}=%d\\n\", {VarName(e)});");
            else if (e.Dir == "input")
                s.AppendLine($"  int {VarName(e)} = digitalRead({e.Macro});  Serial.printf(\"{VarName(e)}=%d\\n\", {VarName(e)});");
            else // output
                s.AppendLine($"  digitalWrite({e.Macro}, HIGH);  // TODO: drive {e.ToPin} as needed");
        }
        if (hasI2c) s.AppendLine("  // TODO: talk to your I2C device(s) over Wire");
        s.AppendLine("  delay(1000);");
        s.AppendLine("}");

        return new Foundry.Core.Project.Firmware
        {
            Platform = "Arduino C++",
            Board = "esp32:esp32:esp32",
            Files = new()
            {
                new FirmwareFile { Name = "main.ino", Path = "/foundry/firmware/", Active = true, Content = s.ToString() },
                new FirmwareFile { Name = "pinmap.h", Path = "/foundry/firmware/", Content = PinMap.RenderHeader(entries) },
                new FirmwareFile { Name = "platformio.ini", Path = "/foundry/firmware/", Content = PlatformIo(hasI2c) },
                new FirmwareFile { Name = "README.md", Path = "/foundry/firmware/", Content = Readme(entries) },
            },
            Libraries = hasI2c
                ? new() { new("Arduino core", "built-in"), new("Wire (I2C)", "built-in") }
                : new() { new("Arduino core", "built-in") },
        };
    }

    private static string VarName(PinMapEntry e)
    {
        var n = e.Macro.StartsWith("PIN_") ? e.Macro[4..] : e.Macro;
        return n.ToLowerInvariant();
    }

    private static string Readme(IReadOnlyList<PinMapEntry> entries) =>
        "# Firmware (generated)\n\n" +
        "Open this folder in the Arduino IDE 2.x or PlatformIO and flash to your board.\n\n" +
        "- `pinmap.h` is generated from the netlist — change the wiring, not the header.\n" +
        "- `main.ino` scaffolds setup()/loop() for every connected pin; fill in the TODOs.\n\n" +
        $"Pins: {entries.Count} mapped from the netlist.\n";

    private static string PlatformIo(bool hasI2c) =>
        "[env:esp32dev]\nplatform   = espressif32@^6.5.0\nboard      = esp32dev\n" +
        "framework  = arduino\nmonitor_speed = 115200\nupload_speed  = 460800\n";

    // ---------- MicroPython ----------
    private static Foundry.Core.Project.Firmware Micropython(IReadOnlyList<PinMapEntry> entries)
    {
        var pinmap = new StringBuilder();
        pinmap.AppendLine("# GENERATED — derived from Project.connections. Do not edit.");
        foreach (var e in entries)
            pinmap.AppendLine($"{e.Macro} = {e.PyEmit}  # {e.Net}: {e.FromPin} <-> {e.ToPin}" + (e.Strapping ? "  [strapping]" : ""));

        bool hasAnalog = entries.Any(e => e.Dir == "analog");
        bool hasI2c = entries.Any(e => e.Dir == "i2c");
        var ios = entries.Where(e => e.Dir != "i2c").ToList();

        var main = new StringBuilder();
        main.AppendLine("# FOUNDRY · generated MicroPython starter");
        main.AppendLine("# Scaffolded from the netlist; pin numbers live in pinmap.py.");
        main.AppendLine("import time, machine");
        main.AppendLine("from pinmap import *");
        main.AppendLine();
        foreach (var e in ios)
        {
            var v = VarName(e);
            if (e.Dir == "analog")
                main.AppendLine($"{v} = machine.ADC(machine.Pin({e.Macro})); {v}.atten(machine.ADC.ATTN_11DB)  # {e.ToPin}");
            else if (e.Dir == "output")
                main.AppendLine($"{v} = machine.Pin({e.Macro}, machine.Pin.OUT)  # {e.ToPin}");
            else
                main.AppendLine($"{v} = machine.Pin({e.Macro}, machine.Pin.IN)  # {e.ToPin}");
        }
        if (hasI2c) main.AppendLine("i2c = machine.I2C(0)  # I2C bus");
        main.AppendLine();
        main.AppendLine("while True:");
        if (ios.Count == 0 && !hasI2c) main.AppendLine("    pass  # no pins mapped from the netlist yet");
        foreach (var e in ios)
        {
            var v = VarName(e);
            if (e.Dir == "analog") main.AppendLine($"    print('{v}', {v}.read())");
            else if (e.Dir == "input") main.AppendLine($"    print('{v}', {v}.value())");
            else main.AppendLine($"    {v}.value(1)  # TODO: drive {e.ToPin} as needed");
        }
        if (hasI2c) main.AppendLine("    # TODO: i2c.readfrom(addr, n) / writeto(addr, buf)");
        main.AppendLine("    time.sleep(1)");

        return new Foundry.Core.Project.Firmware
        {
            Platform = "MicroPython",
            Board = "esp32",
            Files = new()
            {
                new FirmwareFile { Name = "main.py", Path = "/foundry/firmware/", Active = true, Content = main.ToString() },
                new FirmwareFile { Name = "pinmap.py", Path = "/foundry/firmware/", Content = pinmap.ToString() },
            },
            Libraries = new() { new("machine", "built-in"), new("time", "built-in") },
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
