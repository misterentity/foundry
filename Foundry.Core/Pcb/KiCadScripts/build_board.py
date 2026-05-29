"""Foundry Track B v2.2 — build a .kicad_pcb from a JSON job document.

Run against KiCad's bundled Python (the only interpreter where `import pcbnew` works):
    <kicad>\\bin\\python.exe build_board.py job.json

Reads the job (spec §C): footprint dirs, nets, components (footprint lib id + grid xy + padNets),
and a rectangular Edge.Cuts outline. Creates the board, loads each footprint, assigns every pad to
its net, places parts on the grid, draws the outline, and Save()s. Prints one JSON result line.

No routing, no DRC, no gerbers — the ratsnest is implicit (rendered by KiCad from pad→net membership).
"""

import json
import os
import sys

import pcbnew


def resolve_lib_dir(lib, fp_dirs):
    """Map a lib nickname to its <dir>\\<lib>.pretty directory across the given footprint roots."""
    for d in fp_dirs:
        cand = os.path.join(d, lib + ".pretty")
        if os.path.isdir(cand):
            return cand
    # fall back to the first root so the loader raises a clear, locatable error
    return os.path.join(fp_dirs[0], lib + ".pretty") if fp_dirs else lib + ".pretty"


def footprint_loader():
    """Resolve a FootprintLoad(libDir, name) callable across KiCad versions.

    KiCad 10 dropped the static PCB_IO_MGR.FootprintLoad; the module-level
    pcbnew.FootprintLoad works in 8/9/10, so prefer it and fall back to the managers."""
    if hasattr(pcbnew, "FootprintLoad"):
        return pcbnew.FootprintLoad
    for mgr_name in ("PCB_IO_MGR", "IO_MGR"):
        mgr = getattr(pcbnew, mgr_name, None)
        if mgr is not None and hasattr(mgr, "FootprintLoad"):
            return mgr.FootprintLoad
    raise RuntimeError("pcbnew has no FootprintLoad (module or PCB_IO_MGR/IO_MGR) — unsupported KiCad version.")


def build(job):
    notes = []
    fp_dirs = job.get("footprintDirs") or []
    load_fp = footprint_loader()

    board = pcbnew.BOARD()

    # 1. one NETINFO_ITEM per Foundry net
    nets = {}
    for net in job["nets"]:
        name = net["name"]
        ni = pcbnew.NETINFO_ITEM(board, name)
        board.Add(ni)
        nets[name] = ni

    # 2. components: load footprint, set ref, assign pads, place
    placed = 0
    for comp in job["components"]:
        lib_id = comp["footprint"]
        if ":" not in lib_id:
            notes.append("component %s: bad footprint id '%s'" % (comp["ref"], lib_id))
            continue
        lib, name = lib_id.split(":", 1)
        lib_dir = resolve_lib_dir(lib, fp_dirs)
        try:
            fp = load_fp(lib_dir, name)
        except Exception as ex:  # noqa: BLE001 — surfaced as a note, not a crash
            notes.append("footprint %s not found (%s): %s" % (lib_id, lib_dir, ex))
            continue
        if fp is None:
            notes.append("footprint %s not found in %s" % (lib_id, lib_dir))
            continue

        fp.SetReference(comp["ref"])
        board.Add(fp)

        # Assign every net node to a real pad. The netlist addresses pins by NAME (VCC/AOUT/GPIO0/…) but a
        # footprint's pads are named "1".."N" (generic headers) or by its own scheme — so match by pad name
        # first, then fall back to ORDINAL pad position for the rest. Without this, fallback-header parts get
        # no nets, the board has no connectivity, and routing/DRC operate on an empty board.
        pad_net_list = comp.get("padNetList")
        if pad_net_list is None:  # legacy job: derive an (unordered) list from the name-keyed dict
            pad_net_list = [{"pin": k, "net": v} for k, v in (comp.get("padNets") or {}).items()]

        pads = list(fp.Pads())
        used = set()  # indices of pads already assigned

        def assign(pad, net_name):
            if net_name and net_name in nets:
                pad.SetNet(nets[net_name])
                return True
            return False

        # pass 1: exact pad-name match (case-insensitive)
        deferred = []
        for item in pad_net_list:
            pin = str(item.get("pin", ""))
            net_name = item.get("net")
            matched = False
            for i, pad in enumerate(pads):
                if i not in used and pad.GetName().lower() == pin.lower():
                    if assign(pad, net_name):
                        used.add(i)
                    matched = True
                    break
            if not matched:
                deferred.append((pin, net_name))

        # pass 2: ordinal — assign each unmatched (pin, net) to the next free pad in order
        free = [i for i in range(len(pads)) if i not in used]
        fi = 0
        for pin, net_name in deferred:
            if fi >= len(free):
                notes.append("component %s: no free pad for net node '%s' (%s has %d pads)" % (comp["ref"], pin, lib_id, len(pads)))
                continue
            i = free[fi]
            fi += 1
            if assign(pads[i], net_name):
                used.add(i)
            if pads[i].GetName().lower() != pin.lower():
                notes.append("component %s: pin '%s' -> pad '%s' by position" % (comp["ref"], pin, pads[i].GetName()))

        fp.SetPosition(pcbnew.VECTOR2I(pcbnew.FromMM(comp["x_mm"]), pcbnew.FromMM(comp["y_mm"])))
        if comp.get("rot"):
            fp.SetOrientationDegrees(comp["rot"])
        placed += 1

    # 3. rectangular Edge.Cuts outline
    for seg_pts in job.get("outlineSegments_mm", []):
        x1, y1, x2, y2 = seg_pts
        seg = pcbnew.PCB_SHAPE(board)
        seg.SetShape(pcbnew.SHAPE_T_SEGMENT)
        seg.SetStart(pcbnew.VECTOR2I(pcbnew.FromMM(x1), pcbnew.FromMM(y1)))
        seg.SetEnd(pcbnew.VECTOR2I(pcbnew.FromMM(x2), pcbnew.FromMM(y2)))
        seg.SetLayer(pcbnew.Edge_Cuts)
        seg.SetWidth(pcbnew.FromMM(0.15))
        board.Add(seg)

    out_path = job["outPath"]
    board.Save(out_path)
    return {"ok": True, "out": out_path, "components": placed, "nets": len(nets), "notes": notes}


def main():
    if len(sys.argv) < 2:
        print(json.dumps({"ok": False, "error": "usage: build_board.py <job.json>"}))
        return 2
    try:
        with open(sys.argv[1], "r", encoding="utf-8") as fh:
            job = json.load(fh)
        result = build(job)
        print(json.dumps(result))
        return 0
    except Exception as ex:  # noqa: BLE001 — report as JSON, never crash the caller's parse
        print(json.dumps({"ok": False, "error": str(ex)}))
        return 1


if __name__ == "__main__":
    sys.exit(main())
