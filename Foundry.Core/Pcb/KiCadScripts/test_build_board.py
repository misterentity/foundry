"""Pure unit tests for build_board.assign_pads — run WITHOUT KiCad/pcbnew.

    python -m pytest Foundry.Core/Pcb/KiCadScripts/test_build_board.py -q

These pin the net-to-pad assignment contract that the C# side (PcbResult) trusts. They load
build_board.py by path so the `import pcbnew` guard lets them run on any box.

The gate: ordinal-by-position is only allowed for a GENERIC PLACEHOLDER footprint (is_fallback) or a
numeric positional pin. A RESOLVED real footprint (is_fallback=False) addressed by a logical name that
matches no pad is left UNMAPPED — never silently mis-wired by position.
"""
import importlib.util
import os

_HERE = os.path.dirname(os.path.abspath(__file__))
_spec = importlib.util.spec_from_file_location("build_board", os.path.join(_HERE, "build_board.py"))
bb = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(bb)


def test_real_footprint_logical_pin_is_unmapped_not_ordinal():
    # THE ESP32 case: a resolved real footprint with all-numeric pads ("1".."39") addressed by GPIO name.
    # The logical pins must NOT be ordinal-mapped by position — they're recorded unmapped so the build fails.
    assignments, notes, unmapped, by_position = bb.assign_pads(
        ["1", "2", "3"],
        [{"pin": "GPIO34", "net": "SIG"}, {"pin": "3V3", "net": "+3V3"}, {"pin": "GND", "net": "GND"}],
        {"SIG", "+3V3", "GND"}, "U1", "RF_Module:ESP32-WROOM-32", is_fallback=False)
    assert assignments == []                 # nothing placed — no pin matched a pad and ordinal is refused
    assert by_position == []
    assert {u["pin"] for u in unmapped} == {"GPIO34", "3V3", "GND"}


def test_fallback_placeholder_ordinal_maps_logical_pins():
    # A generic placeholder header (Foundry couldn't resolve a real part): ordinal mapping IS allowed.
    assignments, notes, unmapped, by_position = bb.assign_pads(
        ["1", "2", "3"],
        [{"pin": "VCC", "net": "+3V3"}, {"pin": "AOUT", "net": "SIG"}, {"pin": "GND", "net": "GND"}],
        {"+3V3", "SIG", "GND"}, "S1", "Connector_PinHeader_2.54mm:PinHeader_1x03", is_fallback=True)
    assert assignments == [(0, "+3V3"), (1, "SIG"), (2, "GND")]
    assert unmapped == []
    assert len(by_position) == 3
    assert notes[:3] == [
        "component S1: pin 'VCC' -> pad '1' by position",
        "component S1: pin 'AOUT' -> pad '2' by position",
        "component S1: pin 'GND' -> pad '3' by position",
    ]


def test_numeric_positional_pin_ordinal_ok_on_real_footprint():
    # A real header addressed positionally (J1.1, J1.2) whose pads happen to be named differently:
    # a numeric pin is a positional reference, so ordinal mapping is allowed even on a real footprint.
    assignments, notes, unmapped, by_position = bb.assign_pads(
        ["P1", "P2"],
        [{"pin": "1", "net": "NA"}, {"pin": "2", "net": "NB"}],
        {"NA", "NB"}, "J1", "Connector:Real_1x02", is_fallback=False)
    assert assignments == [(0, "NA"), (1, "NB")]
    assert unmapped == []


def test_exact_name_match_takes_precedence_no_ordinal_needed():
    # pads carry real names matching the netlist pins -> pass 1 matches all (is_fallback irrelevant).
    assignments, notes, unmapped, by_position = bb.assign_pads(
        ["VCC", "GND", "SDA", "SCL"],
        [{"pin": "SDA", "net": "I2C_SDA"}, {"pin": "VCC", "net": "+3V3"}, {"pin": "GND", "net": "GND"}],
        {"I2C_SDA", "+3V3", "GND"}, "U1", "Sensor:BME280", is_fallback=False)
    assert sorted(assignments) == [(0, "+3V3"), (1, "GND"), (2, "I2C_SDA")]
    assert notes == []
    assert unmapped == []
    assert by_position == []


def test_fallback_more_pins_than_pads_emits_no_free_pad_and_unmapped():
    assignments, notes, unmapped, by_position = bb.assign_pads(
        ["1", "2"],
        [{"pin": "A", "net": "NA"}, {"pin": "B", "net": "NB"}, {"pin": "C", "net": "NC"}],
        {"NA", "NB", "NC"}, "J1", "Connector:Hdr", is_fallback=True)
    assert assignments == [(0, "NA"), (1, "NB")]
    assert "component J1: no free pad for net node 'C' (Connector:Hdr has 2 pads)" in notes
    assert any(u["pin"] == "C" for u in unmapped)


def test_unknown_net_is_not_assigned_on_fallback():
    assignments, _, _, _ = bb.assign_pads(
        ["1", "2"],
        [{"pin": "VCC", "net": "+3V3"}, {"pin": "NC", "net": "FLOATING"}],
        {"+3V3"}, "U2", "X:Y", is_fallback=True)
    assert assignments == [(0, "+3V3")]
    assert (1, "FLOATING") not in assignments


def test_pad_name_match_is_case_insensitive():
    assignments, notes, unmapped, by_position = bb.assign_pads(
        ["Vcc", "Gnd"],
        [{"pin": "vcc", "net": "+5V"}, {"pin": "GND", "net": "GND"}],
        {"+5V", "GND"}, "U3", "X:Y", is_fallback=False)
    assert sorted(assignments) == [(0, "+5V"), (1, "GND")]
    assert notes == []
    assert unmapped == []
