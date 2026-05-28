"""Foundry Track B v2.4 — export a placed .kicad_pcb to a Specctra DSN for FreeRouting.

Run against KiCad's bundled Python (the only interpreter where `import pcbnew` works):
    <kicad>\\bin\\python.exe export_dsn.py job.json

kicad-cli has no specctra/dsn verb, so export goes through the SWIG pcbnew module — the same
binding build_board.py uses. Reads the job {"inPcb": path, "dsn": path}, loads the board, and calls
the frame-free ExportSpecctraDSN(board, dsn) overload. Prints one JSON result line.
"""

import json
import sys

import pcbnew


def export(job):
    in_pcb = job["inPcb"]
    dsn = job["dsn"]

    if not hasattr(pcbnew, "ExportSpecctraDSN"):
        return {"ok": False, "error": "pcbnew has no ExportSpecctraDSN binding in this KiCad version."}

    board = pcbnew.LoadBoard(in_pcb)

    # board+filename is the standalone (frame-free) overload; older bindings only took the filename.
    try:
        ok = pcbnew.ExportSpecctraDSN(board, dsn)
    except TypeError:
        ok = pcbnew.ExportSpecctraDSN(dsn)

    # The binding returns bool on success in most versions; treat None as success too (void overloads).
    ok = True if ok is None else bool(ok)
    return {"ok": ok, "dsn": dsn}


def main():
    if len(sys.argv) < 2:
        print(json.dumps({"ok": False, "error": "usage: export_dsn.py <job.json>"}))
        return 2
    try:
        with open(sys.argv[1], "r", encoding="utf-8") as fh:
            job = json.load(fh)
        print(json.dumps(export(job)))
        return 0
    except Exception as ex:  # noqa: BLE001 — report as JSON, never crash the caller's parse
        print(json.dumps({"ok": False, "error": str(ex)}))
        return 1


if __name__ == "__main__":
    sys.exit(main())
