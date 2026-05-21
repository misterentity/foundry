using Foundry.Core.Kb;
using Foundry.Core.Validation;

namespace Foundry.Core.Project;

/// <summary>
/// The canonical demo project (soil-moisture sensor) and the library/chat samples,
/// ported from the design prototype's data.jsx. Used as Phase-0/1 seed data so the
/// whole UI is exercised before the AI pipeline is live.
/// </summary>
public static class DemoData
{
    public static Project CreateSoilMoistureProject() => new()
    {
        Id = "p_0142",
        Title = "Cap. Soil Moisture Sentinel",
        Prompt = "A battery-powered soil-moisture sensor that texts me when my plants are dry. " +
                 "Outdoor enclosure. Should run at least a month on a single charge.",
        Status = "READY",
        Validation = "warn",
        Updated = "2026-05-20 14:21",

        Kpis = new ProjectKpis
        {
            Parts = 8, Cost = 38.42, CurrentMa = 84, BatteryDays = 41, PrintGrams = 38,
        },

        Subsystems = new()
        {
            new Subsystem { Id = "mcu", Role = "Controller", Name = "ESP32 DevKit v1", Mpn = "ESP32-DEVKITC-32E",
                Specs = new() { new("Logic","3.3 V"), new("Idle","12 mA"), new("Wi-Fi","802.11 b/g/n"), new("Pins","30") } },
            new Subsystem { Id = "sensor", Role = "Sensor", Name = "Capacitive Soil v1.2", Mpn = "SEN-CAP-01",
                Specs = new() { new("Output","AOUT 0-3V"), new("Supply","3.3 V"), new("Draw","5 mA"), new("IP","67") } },
            new Subsystem { Id = "power", Role = "Power", Name = "18650 + TP4056", Mpn = "LI-18650-3000",
                Specs = new() { new("Capacity","3000 mAh"), new("Charge","USB-C"), new("Protect","yes"), new("Voltage","3.7 V") } },
            new Subsystem { Id = "regulator", Role = "Regulator", Name = "MCP1700-3302E", Mpn = "MCP1700-3302E/TO",
                Specs = new() { new("Vout","3.3 V"), new("Iq","1.6 µA"), new("Imax","250 mA"), new("Dropout","178 mV") } },
        },

        Bom = new()
        {
            new BomLine { Qty=1, Name="ESP32 DevKit v1",             Mpn="ESP32-DEVKITC-32E", Price=8.50, Stock=1442, Lead="Stock", Dist="DigiKey", Note="Wi-Fi MCU" },
            new BomLine { Qty=1, Name="Capacitive Soil Sensor v1.2", Mpn="SEN-CAP-01",        Price=4.20, Stock=312,  Lead="Stock", Dist="Amazon",  Note="Analog out" },
            new BomLine { Qty=1, Name="18650 Li-ion 3000mAh",        Mpn="LI-18650-3000",     Price=7.95, Stock=62,   Lead="2 wk",  Dist="Mouser",  Note="Protected" },
            new BomLine { Qty=1, Name="TP4056 USB-C Charger",        Mpn="TP4056-USB-C",      Price=1.40, Stock=984,  Lead="Stock", Dist="DigiKey", Note="1A charge" },
            new BomLine { Qty=1, Name="MCP1700 3.3V LDO",            Mpn="MCP1700-3302E/TO",  Price=0.48, Stock=5210, Lead="Stock", Dist="Mouser",  Note="TO-92" },
            new BomLine { Qty=1, Name="18650 Holder, single",        Mpn="HLD-18650-1S",      Price=0.85, Stock=140,  Lead="Stock", Dist="DigiKey", Note="PCB mount" },
            new BomLine { Qty=2, Name="Tactile Switch 6×6mm",        Mpn="TL3301AF260QG",     Price=0.18, Stock=9999, Lead="Stock", Dist="Mouser",  Note="Reset/Mode" },
            new BomLine { Qty=1, Name="Cable Gland M12",             Mpn="M12-GLAND-PG7",     Price=0.85, Stock=28,   Lead="low",   Dist="Amazon",  Note="Sensor lead" },
        },

        Connections = SoilMoistureConnections(),

        Enclosure = new Enclosure
        {
            Inner = new double[] { 62, 48, 26 },
            Wall = 2.0,
            Lid = "snap",
            Cutouts = new()
            {
                new Cutout { Face="side", Shape="rect",   Size=new double[]{9.5,6.5}, Pos=new double[]{12,18}, Label="USB-C" },
                new Cutout { Face="top",  Shape="circle", D=6,                        Pos=new double[]{40,10}, Label="Reset" },
                new Cutout { Face="side", Shape="circle", D=12,                       Pos=new double[]{50,13}, Label="M12 gland" },
            },
            Standoffs = 4,
            MassGrams = 38,
            PrintTime = "2h 14m",
        },

        Firmware = new Firmware
        {
            Platform = "Arduino C++",
            Board = "esp32:esp32:esp32",
            Files = new()
            {
                new FirmwareFile { Name="main.ino",       Path="/foundry/firmware/", Active=true },
                new FirmwareFile { Name="pinmap.h",       Path="/foundry/firmware/" },
                new FirmwareFile { Name="wifi.h",         Path="/foundry/firmware/" },
                new FirmwareFile { Name="platformio.ini", Path="/foundry/firmware/" },
            },
            Libraries = new()
            {
                new("WiFi","built-in"), new("HTTPClient","built-in"), new("ArduinoJson","7.1.0"),
                new("esp32-hal-adc","built-in"), new("ESP32 Deep Sleep","built-in"),
            },
        },

        Findings = BuildDemoFindings(),

        Assembly = new()
        {
            new AssemblyStep { N=1, Title="Prepare the enclosure",
                Body="Slice the generated lid and base STL with 0.2 mm layer height, 20% infill, no supports needed. Use PETG for outdoor durability. Print time ≈ 2 h 14 m at 38 g of filament.",
                Chips=new(){ "enclosure.stl","lid.stl","PETG · 0.2mm" } },
            new AssemblyStep { N=2, Title="Solder the regulator",
                Body="Mount the MCP1700-3302E in TO-92 footprint. Pin 1 → VIN (battery +), Pin 2 → GND, Pin 3 → VOUT (3.3V to ESP32). Add 1µF ceramic on input and output.",
                Chips=new(){ "MCP1700-3302E/TO","1µF × 2" } },
            new AssemblyStep { N=3, Title="Wire the sensor",
                Body="Run the capacitive sensor's three-wire lead through the M12 cable gland. VCC→ESP32 3V3, GND→ESP32 GND, AOUT→ESP32 GPIO34. Use 24 AWG silicone wire.",
                Chips=new(){ "SEN-CAP-01","M12 gland","GPIO34" } },
            new AssemblyStep { N=4, Title="Battery + charger",
                Body="Press-fit the 18650 holder into the standoffs. Wire TP4056 OUT+/OUT– to the regulator input. Route USB-C through the side cutout. Verify polarity before inserting the cell.",
                Chips=new(){ "TP4056","18650","USB-C cutout" } },
            new AssemblyStep { N=5, Title="Flash the firmware",
                Body="Open the exported project folder in Arduino IDE 2.x or PlatformIO. Set your Wi-Fi credentials and webhook in `wifi.h` (`// TODO: SSID` markers). Flash at 460800 baud. The board should boot, sample once, and deep-sleep within ~3s.",
                Chips=new(){ "main.ino","wifi.h","460800 baud" } },
            new AssemblyStep { N=6, Title="Close & deploy",
                Body="Snap the lid on. Verify the cable gland is finger-tight (no thread sealant needed for IP65). Place the sensor 5–8 cm from the plant root. The device will text you when moisture drops below 32% for >2 readings.",
                Chips=new(){ "snap lid","IP65","5–8 cm depth" } },
        },

        Chat = CreateChatHistory(),
    };

    public static List<Connection> SoilMoistureConnections() => new()
    {
        new Connection { From="MCU.3V3",    To="SENSOR.VCC",  Net="power" },
        new Connection { From="MCU.GND",    To="SENSOR.GND",  Net="ground" },
        new Connection { From="MCU.GPIO34", To="SENSOR.AOUT", Net="signal" },
        new Connection { From="BAT.+",      To="REG.VIN",     Net="power" },
        new Connection { From="BAT.-",      To="REG.GND",     Net="ground" },
        new Connection { From="REG.VOUT",   To="MCU.5V",      Net="power" },
        new Connection { From="REG.GND",    To="MCU.GND",     Net="ground" },
        new Connection { From="MCU.GPIO0",  To="BTN1.A",      Net="signal" },
        new Connection { From="BTN1.B",     To="MCU.GND",     Net="ground" },
    };

    /// <summary>
    /// Demo findings = real engine output (electrical) + one curated sourcing info finding.
    /// Demonstrates Phase 2: the warnings and passes are computed deterministically, not hand-written.
    /// </summary>
    public static List<Finding> BuildDemoFindings()
    {
        var findings = RulesEngine.Validate(SoilMoistureConnections(), ComponentKb.Demo(), batteryGoalDays: 60);

        var sourcing = new Finding
        {
            Severity = "info", Code = "BOM-01", Num = "i·01",
            Title = "Cable gland M12 has limited stock at preferred distributor",
            Description = "28 units at Amazon; lead time may slip. Mouser MPN PG7-GLD substitute is in stock at $0.92.",
            Refs = new() { "M12-GLAND-PG7" }, Fix = "Swap to PG7-GLD",
        };
        int firstPass = findings.FindIndex(f => f.Severity == "pass");
        findings.Insert(firstPass < 0 ? findings.Count : firstPass, sourcing);
        return findings;
    }

    public static List<ChatMessage> CreateChatHistory() => new()
    {
        new ChatMessage { Role="user", Time="14:08",
            Text="A battery-powered soil-moisture sensor that texts me when my plants are dry. Outdoor enclosure. Should run at least a month on a single charge." },
        new ChatMessage { Role="assistant", Time="14:08",
            Text="On it. Picking parts for low-duty-cycle Wi-Fi, IP65 enclosure, and a single 18650 with USB-C charging. I'll favor capacitive over resistive sensors for outdoor lifetime.",
            Pipeline=new(){ new("Spec","done"), new("Architecture","done"), new("Wiring","done"), new("Firmware","done"), new("Enclosure","done"), new("Validation","done") } },
        new ChatMessage { Role="user", Time="14:14", Text="Can it use Twilio SMS instead of email?" },
        new ChatMessage { Role="assistant", Time="14:14",
            Text="Yes — swapping the alert path. I'll re-run firmware and the assembly guide. BOM, wiring, and enclosure are unaffected.",
            Pipeline=new(){ new("Firmware","done"), new("Assembly","done") } },
        new ChatMessage { Role="user", Time="14:20", Text="Make the enclosure wall-mountable." },
        new ChatMessage { Role="assistant", Time="14:21",
            Text="Adding two M3 keyholes to the back face. Regenerating the enclosure schema and revalidating standoff clearances.",
            Pipeline=new(){ new("Enclosure","live"), new("Validation","live") } },
    };

    public static List<ProjectSummary> RecentProjects() => new()
    {
        new() { Id="p_0142", Title="Cap. Soil Moisture Sentinel", Prompt="Battery-powered moisture sensor with SMS alert", Updated="2 hours ago", Parts=8,  Status="warn", Cost=38.42, Current=true },
        new() { Id="p_0141", Title="Pico Weather Station",        Prompt="Raspberry Pi Pico, OLED, temp/humidity/press",  Updated="Yesterday",   Parts=11, Status="ok",   Cost=56.10 },
        new() { Id="p_0140", Title="Garage Door Reporter",        Prompt="ESP32 reed switch → Home Assistant",            Updated="3 d ago",     Parts=6,  Status="ok",   Cost=14.85 },
        new() { Id="p_0138", Title="Under-Desk Motion Strip",     Prompt="PIR + WS2812 strip, ~2 wks/charge",             Updated="Last week",   Parts=9,  Status="warn", Cost=42.90 },
        new() { Id="p_0136", Title="E-Ink Bus Arrival Sign",      Prompt="ESP32 + 4.2\" e-paper, deep sleep",             Updated="Last week",   Parts=7,  Status="ok",   Cost=71.20 },
        new() { Id="p_0133", Title="Cat Feeder Servo Hub",        Prompt="Scheduled servo dispense, 4× zones",            Updated="2 wks ago",   Parts=14, Status="fail", Cost=89.55 },
        new() { Id="p_0130", Title="Workshop Air Quality Lamp",   Prompt="SGP40 + RGB ring, ambient indicator",           Updated="3 wks ago",   Parts=10, Status="ok",   Cost=47.30 },
        new() { Id="p_0127", Title="Mailbox Open Notifier",       Prompt="Reed switch, LoRa, super low power",            Updated="Apr 18",      Parts=8,  Status="warn", Cost=33.10 },
    };
}
