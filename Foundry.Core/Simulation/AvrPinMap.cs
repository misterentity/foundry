namespace Foundry.Core.Simulation;

/// <summary>
/// Maps Arduino digital pin numbers to ATmega PORT-letter + bit and back, for the two AVR boards the
/// avr8js engine covers: ATmega328P (Uno/Nano — ports B/C/D) and ATmega2560 (Mega — ports A..L).
/// avr8js reports edges per (port, bit); <see cref="GpioPinMap"/> works in Arduino pin numbers (the
/// trailing number of the MCU endpoint, e.g. mcu.D13 -> 13). This table bridges the two so a
/// <see cref="PinLevel"/> built from an avr8js edge carries the same Net/Endpoint the breadboard expects.
/// The bundle stays board-agnostic: we hand it the forward table (<see cref="PortMap"/>) at runtime.
/// Pure / deterministic — derived from the recipe's ATmega328P/2560 pin tables.
/// </summary>
public static class AvrPinMap
{
    /// <summary>ATmega328P (Uno/Nano): Arduino digital pin -> (port letter, bit). D0..D13 + A0..A5 (=D14..D19).</summary>
    private static readonly IReadOnlyDictionary<int, (char Port, int Bit)> Uno = Build(new[]
    {
        // PORTD: D0..D7
        (0,'D',0),(1,'D',1),(2,'D',2),(3,'D',3),(4,'D',4),(5,'D',5),(6,'D',6),(7,'D',7),
        // PORTB: D8..D13 (PB0..PB5; PB6/PB7 are the crystal — not mapped). D13 = LED_BUILTIN = PB5.
        (8,'B',0),(9,'B',1),(10,'B',2),(11,'B',3),(12,'B',4),(13,'B',5),
        // PORTC: A0..A5 = D14..D19 (PC0..PC5; PC6 is reset — not mapped).
        (14,'C',0),(15,'C',1),(16,'C',2),(17,'C',3),(18,'C',4),(19,'C',5),
    });

    /// <summary>ATmega2560 (Mega): Arduino digital pin -> (port letter, bit). D0..D53 + A0..A15 (=D54..D69). D13 = PB7.</summary>
    private static readonly IReadOnlyDictionary<int, (char Port, int Bit)> Mega = Build(new[]
    {
        (0,'E',0),(1,'E',1),(2,'E',4),(3,'E',5),(4,'G',5),(5,'E',3),(6,'H',3),(7,'H',4),
        (8,'H',5),(9,'H',6),(10,'B',4),(11,'B',5),(12,'B',6),(13,'B',7),
        (14,'J',1),(15,'J',0),(16,'H',1),(17,'H',0),(18,'D',3),(19,'D',2),(20,'D',1),(21,'D',0),
        (22,'A',0),(23,'A',1),(24,'A',2),(25,'A',3),(26,'A',4),(27,'A',5),(28,'A',6),(29,'A',7),
        (30,'C',7),(31,'C',6),(32,'C',5),(33,'C',4),(34,'C',3),(35,'C',2),(36,'C',1),(37,'C',0),
        (38,'D',7),(39,'G',2),(40,'G',1),(41,'G',0),
        (42,'L',7),(43,'L',6),(44,'L',5),(45,'L',4),(46,'L',3),(47,'L',2),(48,'L',1),(49,'L',0),
        (50,'B',3),(51,'B',2),(52,'B',1),(53,'B',0),
        // A0..A7 = D54..D61 = PF0..PF7
        (54,'F',0),(55,'F',1),(56,'F',2),(57,'F',3),(58,'F',4),(59,'F',5),(60,'F',6),(61,'F',7),
        // A8..A15 = D62..D69 = PK0..PK7
        (62,'K',0),(63,'K',1),(64,'K',2),(65,'K',3),(66,'K',4),(67,'K',5),(68,'K',6),(69,'K',7),
    });

    /// <summary>True when the FQBN targets the Mega (ATmega2560) table rather than ATmega328P.</summary>
    public static bool IsMega(string fqbn) => fqbn.Contains("mega", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The forward (port-letter+bit) -> Arduino-pin table for the bundle's <c>createRunner</c>. Keys are
    /// strings like "B5", "D2"; values are the Arduino pin number that becomes <see cref="SimPin.Gpio"/>.
    /// Pass this straight to the JS runner so the bundle never bakes in a board.
    /// </summary>
    public static IReadOnlyDictionary<string, int> PortMap(bool mega)
    {
        var table = mega ? Mega : Uno;
        var map = new Dictionary<string, int>(table.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var (gpio, pos) in table) map[$"{pos.Port}{pos.Bit}"] = gpio;
        return map;
    }

    private static Dictionary<int, (char, int)> Build(IEnumerable<(int Gpio, char Port, int Bit)> rows)
    {
        var d = new Dictionary<int, (char, int)>();
        foreach (var (gpio, port, bit) in rows) d[gpio] = (port, bit);
        return d;
    }
}
