"""Foundry Track B v2.4 — import a routed Specctra SES back into a .kicad_pcb.

Run against KiCad's bundled Python (the only interpreter where `import pcbnew` works):
    <kicad>\\bin\\python.exe import_ses.py job.json

kicad-cli has no SES-import verb, so import goes through the SWIG pcbnew module. Reads the job
{"inPcb": path, "ses": path, "outPcb": path}, loads the placed board, imports the SES (which
relocates modules and replaces all vias/tracks), then BuildConnectivity to report routing outcome —
board-derived stats are authoritative. Saves the routed board. Prints one JSON result line.
"""

import json
import sys

import pcbnew


def unconnected_count(board):
    """KiCad 8/9: BOARD.GetUnconnectedNetCount(). KiCad 10 removed it — read the connectivity object,
    whose GetUnconnectedCount takes a required aVisibleOnly bool in v10 (count all = False)."""
    if hasattr(board, "GetUnconnectedNetCount"):
        return board.GetUnconnectedNetCount()
    conn = board.GetConnectivity()
    if conn is None:
        return 0
    try:
        return conn.GetUnconnectedCount(False)   # KiCad 10 signature
    except TypeError:
        return conn.GetUnconnectedCount()        # older/other bindings


def import_ses(job):
    in_pcb = job["inPcb"]
    ses = job["ses"]
    out_pcb = job["outPcb"]

    if not hasattr(pcbnew, "ImportSpecctraSES"):
        return {"ok": False, "error": "pcbnew has no ImportSpecctraSES binding in this KiCad version."}

    board = pcbnew.LoadBoard(in_pcb)

    # Arity has varied: try board+filename, fall back to filename-only.
    try:
        ok = pcbnew.ImportSpecctraSES(board, ses)
    except TypeError:
        ok = pcbnew.ImportSpecctraSES(ses)
    ok = True if ok is None else bool(ok)

    board.BuildConnectivity()
    unconnected = unconnected_count(board)

    tracks = board.GetTracks()
    track_count = 0
    via_count = 0
    for t in tracks:
        if isinstance(t, pcbnew.PCB_VIA):
            via_count += 1
        else:
            track_count += 1

    pcbnew.SaveBoard(out_pcb, board)

    return {
        "ok": ok and track_count > 0,
        "out": out_pcb,
        "unconnected": unconnected,
        "tracks": track_count,
        "vias": via_count,
    }


def main():
    if len(sys.argv) < 2:
        print(json.dumps({"ok": False, "error": "usage: import_ses.py <job.json>"}))
        return 2
    try:
        with open(sys.argv[1], "r", encoding="utf-8") as fh:
            job = json.load(fh)
        print(json.dumps(import_ses(job)))
        return 0
    except Exception as ex:  # noqa: BLE001 — report as JSON, never crash the caller's parse
        print(json.dumps({"ok": False, "error": str(ex)}))
        return 1


if __name__ == "__main__":
    sys.exit(main())
