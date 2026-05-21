/* ============================================================
   FOUNDRY — demo project data (soil moisture sensor)
   the canonical Project document — every tab reads from this
   ============================================================ */

const PROJECT = {
  id: "p_0142",
  title: "Cap. Soil Moisture Sentinel",
  prompt: "A battery-powered soil-moisture sensor that texts me when my plants are dry. Outdoor enclosure. Should run at least a month on a single charge.",
  status: "READY",
  validation: "warn",
  updated: "2026-05-20 14:21",

  kpis: {
    parts: 8,
    cost: 38.42,
    current_ma: 84,
    battery_days: 41,
    print_g: 38,
    findings: { warn: 2, info: 1 },
  },

  subsystems: [
    {
      id: "mcu", role: "Controller", name: "ESP32 DevKit v1",
      mpn: "ESP32-DEVKITC-32E",
      specs: [["Logic", "3.3 V"], ["Idle", "12 mA"], ["Wi-Fi", "802.11 b/g/n"], ["Pins", "30"]],
    },
    {
      id: "sensor", role: "Sensor", name: "Capacitive Soil v1.2",
      mpn: "SEN-CAP-01",
      specs: [["Output", "AOUT 0-3V"], ["Supply", "3.3 V"], ["Draw", "5 mA"], ["IP", "67"]],
    },
    {
      id: "power", role: "Power", name: "18650 + TP4056",
      mpn: "LI-18650-3000",
      specs: [["Capacity", "3000 mAh"], ["Charge", "USB-C"], ["Protect", "yes"], ["Voltage", "3.7 V"]],
    },
    {
      id: "regulator", role: "Regulator", name: "MCP1700-3302E",
      mpn: "MCP1700-3302E/TO",
      specs: [["Vout", "3.3 V"], ["Iq", "1.6 µA"], ["Imax", "250 mA"], ["Dropout", "178 mV"]],
    },
  ],

  bom: [
    { qty: 1, name: "ESP32 DevKit v1",            mpn: "ESP32-DEVKITC-32E",  price: 8.50, stock: 1442, lead: "Stock",    dist: "DigiKey", note: "Wi-Fi MCU"   },
    { qty: 1, name: "Capacitive Soil Sensor v1.2",mpn: "SEN-CAP-01",         price: 4.20, stock:  312, lead: "Stock",    dist: "Amazon",  note: "Analog out"  },
    { qty: 1, name: "18650 Li-ion 3000mAh",       mpn: "LI-18650-3000",      price: 7.95, stock:   62, lead: "2 wk",     dist: "Mouser",  note: "Protected"   },
    { qty: 1, name: "TP4056 USB-C Charger",       mpn: "TP4056-USB-C",       price: 1.40, stock:  984, lead: "Stock",    dist: "DigiKey", note: "1A charge"   },
    { qty: 1, name: "MCP1700 3.3V LDO",           mpn: "MCP1700-3302E/TO",   price: 0.48, stock: 5210, lead: "Stock",    dist: "Mouser",  note: "TO-92"       },
    { qty: 1, name: "18650 Holder, single",       mpn: "HLD-18650-1S",       price: 0.85, stock:  140, lead: "Stock",    dist: "DigiKey", note: "PCB mount"   },
    { qty: 2, name: "Tactile Switch 6×6mm",       mpn: "TL3301AF260QG",      price: 0.18, stock: 9999, lead: "Stock",    dist: "Mouser",  note: "Reset/Mode"  },
    { qty: 1, name: "Cable Gland M12",            mpn: "M12-GLAND-PG7",      price: 0.85, stock:   28, lead: "low",      dist: "Amazon",  note: "Sensor lead" },
  ],

  connections: [
    { from: "MCU.3V3",     to: "SENSOR.VCC",   net: "power"  },
    { from: "MCU.GND",     to: "SENSOR.GND",   net: "ground" },
    { from: "MCU.GPIO34",  to: "SENSOR.AOUT",  net: "signal" },
    { from: "BAT.+",       to: "REG.VIN",      net: "power"  },
    { from: "BAT.-",       to: "REG.GND",      net: "ground" },
    { from: "REG.VOUT",    to: "MCU.5V",       net: "power"  },
    { from: "REG.GND",     to: "MCU.GND",      net: "ground" },
    { from: "MCU.GPIO0",   to: "BTN1.A",       net: "signal" },
    { from: "BTN1.B",      to: "MCU.GND",      net: "ground" },
  ],

  enclosure: {
    inner: [62, 48, 26],
    wall: 2.0,
    lid: "snap",
    cutouts: [
      { face: "side", shape: "rect",   size: [9.5, 6.5], pos: [12, 18], label: "USB-C" },
      { face: "top",  shape: "circle", d: 6,             pos: [40, 10], label: "Reset" },
      { face: "side", shape: "circle", d: 12,            pos: [50, 13], label: "M12 gland" },
    ],
    standoffs: 4,
    mass_g: 38,
    print_h: "2h 14m",
  },

  firmware: {
    platform: "Arduino C++",
    board: "esp32:esp32:esp32",
    files: [
      { name: "main.ino",  path: "/foundry/firmware/", active: true },
      { name: "pinmap.h",  path: "/foundry/firmware/" },
      { name: "wifi.h",    path: "/foundry/firmware/" },
      { name: "platformio.ini", path: "/foundry/firmware/" },
    ],
    libs: [
      ["WiFi",            "built-in"],
      ["HTTPClient",      "built-in"],
      ["ArduinoJson",     "7.1.0"],
      ["esp32-hal-adc",   "built-in"],
      ["ESP32 Deep Sleep","built-in"],
    ],
  },

  findings: [
    {
      sev: "warn", code: "PWR-02", num: "W·02",
      title: "Battery life sensitive to Wi-Fi duty cycle",
      desc: "Current draw of 84 mA active dominates the budget. At the configured 1 sample / 6 h, estimated life is 41 days. Reducing TX power to 8.5 dBm or batching reports would push past 60 days.",
      refs: ["MCU.WIFI", "BAT.+"],
      fix: "Auto-tune duty",
    },
    {
      sev: "warn", code: "PIN-04", num: "W·04",
      title: "GPIO0 used as input — boot strap pin",
      desc: "GPIO0 is a strapping pin on ESP32. If the user-button is pressed during boot the chip will enter download mode. Consider GPIO13 or add a 10kΩ pull-up.",
      refs: ["MCU.GPIO0", "BTN1"],
      fix: "Remap to GPIO13",
    },
    {
      sev: "info", code: "BOM-01", num: "i·01",
      title: "Cable gland M12 has limited stock at preferred distributor",
      desc: "28 units at Amazon; lead time may slip. Mouser MPN PG7-GLD substitute is in stock at $0.92.",
      refs: ["M12-GLAND-PG7"],
      fix: "Swap to PG7-GLD",
    },
    {
      sev: "pass", code: "VLT-00", num: "OK",
      title: "Voltage / logic levels consistent",
      desc: "All signal lines operate at 3.3V. Sensor output range (0–3V) is within MCU ADC range (0–3.3V). No 5V→3.3V mismatches detected.",
      refs: [],
      fix: null,
    },
    {
      sev: "pass", code: "I2C-00", num: "OK",
      title: "No I²C address collisions",
      desc: "Project does not use I²C — check skipped.",
      refs: [],
      fix: null,
    },
  ],

  assembly: [
    { n: 1, title: "Prepare the enclosure",
      body: "Slice the generated lid and base STL with 0.2 mm layer height, 20% infill, no supports needed. Use PETG for outdoor durability. Print time ≈ 2 h 14 m at 38 g of filament.",
      chips: ["enclosure.stl", "lid.stl", "PETG · 0.2mm"] },
    { n: 2, title: "Solder the regulator",
      body: "Mount the MCP1700-3302E in TO-92 footprint. Pin 1 → VIN (battery +), Pin 2 → GND, Pin 3 → VOUT (3.3V to ESP32). Add 1µF ceramic on input and output.",
      chips: ["MCP1700-3302E/TO", "1µF × 2"] },
    { n: 3, title: "Wire the sensor",
      body: "Run the capacitive sensor's three-wire lead through the M12 cable gland. VCC→ESP32 3V3, GND→ESP32 GND, AOUT→ESP32 GPIO34. Use 24 AWG silicone wire.",
      chips: ["SEN-CAP-01", "M12 gland", "GPIO34"] },
    { n: 4, title: "Battery + charger",
      body: "Press-fit the 18650 holder into the standoffs. Wire TP4056 OUT+/OUT– to the regulator input. Route USB-C through the side cutout. Verify polarity before inserting the cell.",
      chips: ["TP4056", "18650", "USB-C cutout"] },
    { n: 5, title: "Flash the firmware",
      body: "Open the exported project folder in Arduino IDE 2.x or PlatformIO. Set your Wi-Fi credentials and webhook in `wifi.h` (`// TODO: SSID` markers). Flash at 460800 baud. The board should boot, sample once, and deep-sleep within ~3s.",
      chips: ["main.ino", "wifi.h", "460800 baud"] },
    { n: 6, title: "Close & deploy",
      body: "Snap the lid on. Verify the cable gland is finger-tight (no thread sealant needed for IP65). Place the sensor 5–8 cm from the plant root. The device will text you when moisture drops below 32% for >2 readings.",
      chips: ["snap lid", "IP65", "5–8 cm depth"] },
  ],
};

const RECENT_PROJECTS = [
  { id: "p_0142", title: "Cap. Soil Moisture Sentinel",  prompt: "Battery-powered moisture sensor with SMS alert", updated: "2 hours ago", parts: 8,  status: "warn",  cost: 38.42, current: true },
  { id: "p_0141", title: "Pico Weather Station",         prompt: "Raspberry Pi Pico, OLED, temp/humidity/press", updated: "Yesterday",   parts: 11, status: "ok",    cost: 56.10, current: false },
  { id: "p_0140", title: "Garage Door Reporter",         prompt: "ESP32 reed switch → Home Assistant",          updated: "3 d ago",     parts: 6,  status: "ok",    cost: 14.85, current: false },
  { id: "p_0138", title: "Under-Desk Motion Strip",      prompt: "PIR + WS2812 strip, ~2 wks/charge",           updated: "Last week",   parts: 9,  status: "warn",  cost: 42.90, current: false },
  { id: "p_0136", title: "E-Ink Bus Arrival Sign",       prompt: "ESP32 + 4.2\" e-paper, deep sleep",          updated: "Last week",   parts: 7,  status: "ok",    cost: 71.20, current: false },
  { id: "p_0133", title: "Cat Feeder Servo Hub",         prompt: "Scheduled servo dispense, 4× zones",          updated: "2 wks ago",   parts: 14, status: "fail",  cost: 89.55, current: false },
  { id: "p_0130", title: "Workshop Air Quality Lamp",    prompt: "SGP40 + RGB ring, ambient indicator",         updated: "3 wks ago",   parts: 10, status: "ok",    cost: 47.30, current: false },
  { id: "p_0127", title: "Mailbox Open Notifier",        prompt: "Reed switch, LoRa, super low power",          updated: "Apr 18",       parts: 8,  status: "warn",  cost: 33.10, current: false },
];

const CHAT_HISTORY = [
  {
    role: "user",
    text: "A battery-powered soil-moisture sensor that texts me when my plants are dry. Outdoor enclosure. Should run at least a month on a single charge.",
    time: "14:08",
  },
  {
    role: "assistant",
    text: "On it. Picking parts for low-duty-cycle Wi-Fi, IP65 enclosure, and a single 18650 with USB-C charging. I'll favor capacitive over resistive sensors for outdoor lifetime.",
    time: "14:08",
    pipeline: [
      { stage: "Spec",         state: "done" },
      { stage: "Architecture", state: "done" },
      { stage: "Wiring",       state: "done" },
      { stage: "Firmware",     state: "done" },
      { stage: "Enclosure",    state: "done" },
      { stage: "Validation",   state: "done" },
    ],
  },
  {
    role: "user",
    text: "Can it use Twilio SMS instead of email?",
    time: "14:14",
  },
  {
    role: "assistant",
    text: "Yes — swapping the alert path. I'll re-run firmware and the assembly guide. BOM, wiring, and enclosure are unaffected.",
    time: "14:14",
    pipeline: [
      { stage: "Firmware",     state: "done" },
      { stage: "Assembly",     state: "done" },
    ],
  },
  {
    role: "user",
    text: "Make the enclosure wall-mountable.",
    time: "14:20",
  },
  {
    role: "assistant",
    text: "Adding two M3 keyholes to the back face. Regenerating the enclosure schema and revalidating standoff clearances.",
    time: "14:21",
    pipeline: [
      { stage: "Enclosure",    state: "live" },
      { stage: "Validation",   state: "live" },
    ],
  },
];

window.PROJECT = PROJECT;
window.RECENT_PROJECTS = RECENT_PROJECTS;
window.CHAT_HISTORY = CHAT_HISTORY;
