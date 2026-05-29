"""Foundry Track B — build (or measure) a .kicad_pcb from a JSON job document.

Run against KiCad's bundled Python (the only interpreter where `import pcbnew` works):
    <kicad>\\bin\\python.exe build_board.py job.json
    <kicad>\\bin\\python.exe build_board.py measure measure_job.json

BUILD mode reads the job (spec §C): footprint dirs, nets, components (footprint lib id + grid xy +
padNets), and a rectangular Edge.Cuts outline. Creates the board, loads each footprint, assigns every
pad to its net, places parts on the grid, draws the outline, and Save()s. Prints one JSON result line.
No routing, no DRC, no gerbers — the ratsnest is implicit (rendered by KiCad from pad→net membership).

MEASURE mode (`measure <job>`) loads each requested footprint and emits its REAL courtyard W×H in mm
(F.CrtYd ∪ B.CrtYd; pad+edge hull when no courtyard), so the C# placer packs using true geometry
instead of approximations. A missing footprint is recorded in `notes` and OMITTED from `sizes` (the
caller falls back to FootprintMap.CourtyardOf for that id).
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


def measure_footprint(fp):
    """Real courtyard W×H (mm). Union F.CrtYd/B.CrtYd; fall back to the pad+edge hull (excludes silk
    text, unlike GetBoundingBox). Returns (wMm, hMm, src)."""
    w = h = 0.0
    for layer in (pcbnew.F_CrtYd, pcbnew.B_CrtYd):
        poly = fp.GetCourtyard(layer)
        if poly is not None and poly.OutlineCount() > 0:
            bb = poly.BBox()
            w = max(w, pcbnew.ToMM(bb.GetWidth()))
            h = max(h, pcbnew.ToMM(bb.GetHeight()))
    if w == 0 or h == 0:
        bb = fp.GetBoundingHull().BBox()
        return pcbnew.ToMM(bb.GetWidth()), pcbnew.ToMM(bb.GetHeight()), "hull"
    return w, h, "courtyard"


def measure(job):
    """Emit real courtyard sizes for the requested lib ids. Missing footprints go to `notes` and are
    omitted from `sizes` (C# falls back to CourtyardOf)."""
    notes = []
    fp_dirs = job.get("footprintDirs") or []
    load_fp = footprint_loader()
    sizes = {}
    for lib_id in job.get("libIds") or []:
        if ":" not in lib_id:
            notes.append("measure: bad footprint id '%s'" % lib_id)
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
        w, h, src = measure_footprint(fp)
        sizes[lib_id] = {"wMm": round(w, 3), "hMm": round(h, 3), "pads": len(list(fp.Pads())), "src": src}
    return {"ok": True, "sizes": sizes, "notes": notes}


def build(job):
    notes = []
    fp_dirs = job.get("footprintDirs") or []
    load_fp = footprint_loader()

    board = pcbnew.BOARD()

    # 0. fab-standard via + drill rules so FreeRouting routes vias the DRC accepts. Without this the
    #    router emits 0.2 mm drills while the board min is 0.3 mm → every via trips drill_out_of_range.
    #    Set the Default netclass via (Ø0.6 / drill0.3) — the Specctra DSN carries it to FreeRouting —
    #    and align the design-rule minimums so build/route/DRC all agree.
    try:
        ds = board.GetDesignSettings()
        ds.m_ViasMinSize = pcbnew.FromMM(0.6)
        ds.m_MinThroughDrill = pcbnew.FromMM(0.3)
        ds.m_TrackMinWidth = pcbnew.FromMM(0.2)
        ncs = board.GetAllNetClasses()
        default = ncs["Default"] if ncs and "Default" in ncs else None
        if default is not None:
            default.SetViaDiameter(pcbnew.FromMM(0.6))
            default.SetViaDrill(pcbnew.FromMM(0.3))
            default.SetTrackWidth(pcbnew.FromMM(0.25))
    except Exception as ex:  # noqa: BLE001 — non-fatal; board still builds with KiCad defaults
        notes.append("via/drill rules not applied: %s" % ex)

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
        print(json.dumps({"ok": False, "error": "usage: build_board.py [measure] <job.json>"}))
        return 2
    # "measure <job>" dispatches to measure mode; anything else is the build path (no behavior change).
    is_measure = sys.argv[1] == "measure"
    job_path = sys.argv[2] if is_measure else sys.argv[1]
    if is_measure and len(sys.argv) < 3:
        print(json.dumps({"ok": False, "error": "usage: build_board.py measure <measure_job.json>"}))
        return 2
    try:
        with open(job_path, "r", encoding="utf-8") as fh:
            job = json.load(fh)
        result = measure(job) if is_measure else build(job)
        print(json.dumps(result))
        return 0
    except Exception as ex:  # noqa: BLE001 — report as JSON, never crash the caller's parse
        print(json.dumps({"ok": False, "error": str(ex)}))
        return 1


if __name__ == "__main__":
    sys.exit(main())
