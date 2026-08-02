"""
FOUNDRY — enclosure mesh builder.

Turns the closed enclosure schema (PRD §8.5) into a printable STL deterministically.

Preferred path: trimesh + manifold3d CSG — a watertight shell with real boolean cutouts
(USB/buttons/sensors) on the requested faces and screw-boss standoffs. If those libraries
aren't available it falls back to a dependency-free pure-Python open-top box shell, so the
sidecar always runs.
"""
from __future__ import annotations

import struct
from typing import List, Tuple

Vec = Tuple[float, float, float]
Tri = Tuple[Vec, Vec, Vec]


# ----------------------------------------------------------------------------
# Preferred builder: trimesh + manifold3d (boolean CSG)
# ----------------------------------------------------------------------------
def _rounded_box(trimesh, w, h, d, r):
    """A box of size w×h×d with rounded vertical corners (radius r), base at z=0, centered in x/y.
    Falls back to a sharp box if shapely/extrude isn't available."""
    import math
    r = max(0.0, min(r, w / 2 - 0.01, h / 2 - 0.01))
    if r > 0.2:
        try:
            from shapely.geometry import Polygon
            seg = 6
            pts = []
            for (cx, cy, a0) in ((w/2-r, h/2-r, 0), (-w/2+r, h/2-r, 90), (-w/2+r, -h/2+r, 180), (w/2-r, -h/2+r, 270)):
                for i in range(seg + 1):
                    a = math.radians(a0 + i * 90.0 / seg)
                    pts.append((cx + r * math.cos(a), cy + r * math.sin(a)))
            m = trimesh.creation.extrude_polygon(Polygon(pts), height=d)  # z in [0, d]
            return m
        except Exception:
            pass
    box = trimesh.creation.box(extents=[w, h, d])
    box.apply_translation([0, 0, d / 2])
    return box


def _csg_build(inner, wall, cutouts, standoffs, lid_style, vents=None, mount="none", fmt="stl",
               arrange="exploded"):
    import math
    import numpy as np  # noqa: F401  (trimesh pulls it in; kept explicit for PyInstaller)
    import trimesh
    from trimesh.transformations import rotation_matrix

    L, Wd, H = float(inner[0]), float(inner[1]), float(inner[2])
    t = float(wall)
    ox, oy, oz = L + 2 * t, Wd + 2 * t, H + t  # outer base; closed floor, open top
    corner = min(4.0, L / 6, Wd / 6)           # design touch: rounded vertical corners

    # ----- BASE: rounded outer shell, open top -----
    base = _rounded_box(trimesh, ox, oy, oz, corner)
    cav = _rounded_box(trimesh, L, Wd, H + t + 2, max(0.0, corner - t))
    cav.apply_translation([0, 0, t])  # floor stays; open above
    base = base.difference(cav, engine="manifold")

    through = max(4.0, t * 4)
    margin = 2.0

    # port/control cutouts + ventilation slots (expanded into many thin slot cutouts)
    all_cuts = list(cutouts or []) + _vent_cutouts(vents or [], ox, oy, oz)
    # face:"top" features belong to the LID, not the base. The base is open above (oz = H + t), so a
    # top cutter placed at the base's rim pierces empty space and the hole vanishes from the printed
    # part — which is why a reset button or LED window on the top face silently produced a sealed lid.
    top_cuts = [c for c in all_cuts if str(c.get("face", "")).lower() == "top"]
    for c in (c for c in all_cuts if str(c.get("face", "")).lower() != "top"):
        try:
            solid = _cutout_solid(trimesh, rotation_matrix, c, ox, oy, oz, through, margin)
            if solid is not None:
                base = base.difference(solid, engine="manifold")
        except Exception:
            continue  # a bad cutout never breaks the whole build

    n = standoffs if isinstance(standoffs, int) else (len(standoffs) if standoffs else 0)
    boss_xy = _boss_positions(n, L, Wd)
    for post in _standoff_posts(trimesh, boss_xy, t, H):
        try:
            base = base.union(post, engine="manifold")
        except Exception:
            continue

    # external mounting tabs (wall-tabs / flange)
    for tab in _mount_tabs(trimesh, mount, ox, oy, t):
        try:
            base = base.union(tab, engine="manifold")
        except Exception:
            continue

    # ----- LID: rounded cap + locating lip + screw clearance -----
    lid_mesh = _build_lid(trimesh, rotation_matrix, L, Wd, ox, oy, t, corner, boss_xy, top_cuts, margin)

    # ARRANGEMENT. "exploded" stacks the lid above the base for the 3D preview; "print" lays both flat
    # on the plate, side by side.
    #
    # These MUST differ. The exploded offset used to be applied to the mesh that was then EXPORTED, so
    # every STL Foundry ever wrote contained a lid hovering ~7 mm above the base with fully overlapping
    # XY — a slicer either rejects the floating body or builds tens of mm of support under it. The file
    # the user takes away has to be printable; the pretty picture is the special case, not the default.
    if str(arrange).lower() == "print":
        # Flip the lid so the flat cap sits ON the plate and the locating lip points up — no support
        # under the cap, and the lip's overhang becomes a self-supporting rim.
        lid_mesh.apply_transform(rotation_matrix(math.pi, [1, 0, 0]))
        lo = lid_mesh.bounds[0]
        lid_mesh.apply_translation([ox + 10.0, 0, -lo[2]])
    else:
        lid_mesh.apply_translation([0, 0, oz + 10.0])

    model = trimesh.util.concatenate([base, lid_mesh])
    fmt = (fmt or "stl").lower()
    if fmt not in ("stl", "3mf"):
        fmt = "stl"
    data = model.export(file_type=fmt)
    if isinstance(data, str):
        data = data.encode("utf-8")
    stats = {
        "kernel": "manifold",
        "format": fmt,
        "triangles": int(len(model.faces)),
        "outer_mm": [round(ox, 2), round(oy, 2), round(oz, 2)],
        "bytes": len(data),
    }
    return bytes(data), stats


def _build_lid(trimesh, rotation_matrix, L, Wd, ox, oy, t, corner, boss_xy, top_cuts=None, margin=2.0):
    """Cap that overlaps the wall tops, with a downward locating lip, top-face ports, and screw holes."""
    capT = max(2.0, t)
    lipH = max(2.0, t + 1.0)
    cap = _rounded_box(trimesh, ox, oy, capT, corner)          # z [0, capT]
    lip = _rounded_box(trimesh, L - 0.5, Wd - 0.5, lipH, max(0.0, corner - t))
    lip.apply_translation([0, 0, -lipH])                       # hangs below the cap
    lid = cap.union(lip, engine="manifold")

    # face:"top" ports and vents are cut HERE, in lid-local coordinates. The lid spans z [-lipH, capT],
    # so the cutter is centred on that span and made long enough to clear both cap and lip — the same
    # geometry the screw holes below already use.
    lid_through = capT + lipH + 2.0
    for c in top_cuts or []:
        try:
            solid = _cutout_solid(trimesh, rotation_matrix, c, ox, oy,
                                  (capT - lipH) / 2.0, lid_through, margin)
            if solid is not None:
                lid = lid.difference(solid, engine="manifold")
        except Exception:
            continue  # a bad cutout never breaks the lid

    # screw clearance holes (Ø3.4) above each standoff
    for (px, py) in boss_xy:
        try:
            hole = trimesh.creation.cylinder(radius=1.7, height=capT + lipH + 2, sections=24)
            hole.apply_translation([px, py, (capT - lipH) / 2])
            lid = lid.difference(hole, engine="manifold")
        except Exception:
            continue
    return lid


def _vent_cutouts(vents, ox, oy, oz):
    """Expand vent groups into thin horizontal slot cutouts on the named face."""
    out = []
    for v in vents or []:
        face = str(v.get("face", "left")).lower()
        count = max(1, min(int(v.get("count", 4) or 4), 12))
        horiz = ox if face in ("front", "back", "top", "bottom") else oy
        slot_w = max(6.0, horiz * 0.5)
        spacing = 3.4
        for i in range(count):
            vpos = (i - (count - 1) / 2.0) * spacing
            out.append({"face": face, "shape": "rect", "size": [slot_w, 1.6], "pos": [0.0, vpos], "label": "vent"})
    return out


def _mount_tabs(trimesh, mount, ox, oy, t):
    """Flanged screw tabs on the left+right walls (wall-tabs / flange)."""
    if str(mount).lower() not in ("wall-tabs", "flange"):
        return []
    tab_out, tab_w, thick = 12.0, 16.0, max(3.0, t)
    tabs = []
    for sx in (-1, 1):
        tab = trimesh.creation.box(extents=[tab_out, tab_w, thick])
        tab.apply_translation([sx * (ox / 2 + tab_out / 2 - 0.5), 0, thick / 2])
        try:
            hole = trimesh.creation.cylinder(radius=2.2, height=thick + 2, sections=24)
            hole.apply_translation([sx * (ox / 2 + tab_out * 0.62), 0, thick / 2])
            tab = tab.difference(hole, engine="manifold")
        except Exception:
            pass
        tabs.append(tab)
    return tabs


def _boss_positions(n, L, Wd):
    if n <= 0:
        return []
    inset = 6.0
    corners = [
        (L / 2 - inset, Wd / 2 - inset), (-(L / 2 - inset), Wd / 2 - inset),
        (L / 2 - inset, -(Wd / 2 - inset)), (-(L / 2 - inset), -(Wd / 2 - inset)),
    ]
    return corners[: min(n, 4)]


def _cutout_solid(trimesh, rotation_matrix, c, ox, oy, oz, through, margin):
    """A prism/cylinder positioned to pierce the named face. pos = offset (mm) from face center."""
    import math

    face = str(c.get("face", "side")).lower()
    shape = str(c.get("shape", "rect")).lower()
    pos = c.get("pos") or [0.0, 0.0]
    pu = float(pos[0]) if len(pos) > 0 else 0.0
    pv = float(pos[1]) if len(pos) > 1 else 0.0

    if shape == "circle":
        d = float(c.get("d") or 8.0)
        w = h = d
    else:
        size = c.get("size") or [10.0, 6.0]
        w = float(size[0]); h = float(size[1])

    def clamp(val, half, feat):
        lim = max(0.0, half - feat / 2 - margin)
        return max(-lim, min(lim, val))

    if face in ("top", "bottom"):
        # hole through Z; u=x, v=y
        cu = clamp(pu, ox / 2, w); cv = clamp(pv, oy / 2, h)
        cz = oz if face == "top" else 0.0
        solid = (trimesh.creation.cylinder(radius=d / 2, height=through, sections=48)
                 if shape == "circle" else trimesh.creation.box(extents=[w, h, through]))
        solid.apply_translation([cu, cv, cz])
        return solid

    nx = ny = None
    if face == "back":
        ny = oy / 2
    elif face in ("front", "side"):
        ny = -oy / 2; face = "front"
    elif face == "left":
        nx = -ox / 2
    elif face == "right":
        nx = ox / 2
    else:
        ny = -oy / 2; face = "front"

    cz = clamp(pv, oz / 2, h) + oz / 2  # vertical center on the wall

    if ny is not None:  # front / back face (Y normal); u=x, v=z
        cx = clamp(pu, ox / 2, w)
        if shape == "circle":
            solid = trimesh.creation.cylinder(radius=d / 2, height=through, sections=48)
            solid.apply_transform(rotation_matrix(math.pi / 2, [1, 0, 0]))  # z-axis -> y
        else:
            solid = trimesh.creation.box(extents=[w, through, h])
        solid.apply_translation([cx, ny, cz])
        return solid

    # left / right face (X normal); u=y, v=z
    cy = clamp(pu, oy / 2, w)
    if shape == "circle":
        solid = trimesh.creation.cylinder(radius=d / 2, height=through, sections=48)
        solid.apply_transform(rotation_matrix(math.pi / 2, [0, 1, 0]))  # z-axis -> x
    else:
        solid = trimesh.creation.box(extents=[through, w, h])
    solid.apply_translation([nx, cy, cz])
    return solid


def _standoff_posts(trimesh, boss_xy, t, H):
    """Corner screw bosses (outer Ø6.4, M2 pilot Ø2.2) rising from the floor to just under the lid."""
    height = max(4.0, H - 2.0)
    posts = []
    for (px, py) in boss_xy:
        boss = trimesh.creation.cylinder(radius=3.2, height=height, sections=32)
        pilot = trimesh.creation.cylinder(radius=1.1, height=height + 2, sections=24)
        boss.apply_translation([px, py, t + height / 2])
        pilot.apply_translation([px, py, t + height / 2])
        try:
            posts.append(boss.difference(pilot, engine="manifold"))
        except Exception:
            posts.append(boss)
    return posts


# ----------------------------------------------------------------------------
# Fallback builder: dependency-free pure-Python open-top box shell
# ----------------------------------------------------------------------------
def _sub(a: Vec, b: Vec) -> Vec:
    return (a[0] - b[0], a[1] - b[1], a[2] - b[2])


def _cross(a: Vec, b: Vec) -> Vec:
    return (a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0])


def _normal(tri: Tri) -> Vec:
    u, v = _sub(tri[1], tri[0]), _sub(tri[2], tri[0])
    n = _cross(u, v)
    m = (n[0] ** 2 + n[1] ** 2 + n[2] ** 2) ** 0.5 or 1.0
    return (n[0] / m, n[1] / m, n[2] / m)


def _quad(tris, p1, p2, p3, p4):
    tris.append((p1, p2, p3)); tris.append((p1, p3, p4))


def box_shell(inner, wall):
    il, iw, ih = inner
    t = wall
    ol, ow, oh = il + 2 * t, iw + 2 * t, ih + t
    tris: List[Tri] = []
    _quad(tris, (0, 0, 0), (0, ow, 0), (ol, ow, 0), (ol, 0, 0))
    _quad(tris, (0, 0, 0), (ol, 0, 0), (ol, 0, oh), (0, 0, oh))
    _quad(tris, (ol, ow, 0), (0, ow, 0), (0, ow, oh), (ol, ow, oh))
    _quad(tris, (0, ow, 0), (0, 0, 0), (0, 0, oh), (0, ow, oh))
    _quad(tris, (ol, 0, 0), (ol, ow, 0), (ol, ow, oh), (ol, 0, oh))
    _quad(tris, (0, 0, oh), (0, t, oh), (ol, t, oh), (ol, 0, oh))
    _quad(tris, (0, ow - t, oh), (0, ow, oh), (ol, ow, oh), (ol, ow - t, oh))
    _quad(tris, (0, t, oh), (0, ow - t, oh), (t, ow - t, oh), (t, t, oh))
    _quad(tris, (ol - t, t, oh), (ol - t, ow - t, oh), (ol, ow - t, oh), (ol, t, oh))
    ix0, iy0, ix1, iy1 = t, t, ol - t, ow - t
    _quad(tris, (ix0, iy0, t), (ix1, iy0, t), (ix1, iy0, oh), (ix0, iy0, oh))
    _quad(tris, (ix1, iy1, t), (ix0, iy1, t), (ix0, iy1, oh), (ix1, iy1, oh))
    _quad(tris, (ix0, iy1, t), (ix0, iy0, t), (ix0, iy0, oh), (ix0, iy1, oh))
    _quad(tris, (ix1, iy0, t), (ix1, iy1, t), (ix1, iy1, oh), (ix1, iy0, oh))
    _quad(tris, (ix0, iy0, t), (ix1, iy0, t), (ix1, iy1, t), (ix0, iy1, t))
    return tris


def to_binary_stl(tris, header: str = "FOUNDRY enclosure") -> bytes:
    buf = bytearray()
    head = header.encode("ascii", "ignore")[:80]
    buf += head + b"\x00" * (80 - len(head))
    buf += struct.pack("<I", len(tris))
    for tri in tris:
        buf += struct.pack("<3f", *_normal(tri))
        for v in tri:
            buf += struct.pack("<3f", *v)
        buf += struct.pack("<H", 0)
    return bytes(buf)


def _fallback_build(inner, wall):
    tris = box_shell(inner, wall)
    stl = to_binary_stl(tris)
    return stl, {
        "kernel": "builtin",
        "triangles": len(tris),
        "outer_mm": [round(inner[0] + 2 * wall, 2), round(inner[1] + 2 * wall, 2), round(inner[2] + wall, 2)],
        "bytes": len(stl),
    }


def build_scad(scad: str, fmt: str, exe: str) -> Tuple[bytes, dict]:
    """Render an AI-written OpenSCAD script to STL/3MF via the OpenSCAD CLI (PRD v2 Phase A)."""
    import os, subprocess, tempfile, shutil as _sh
    fmt = (fmt or "stl").lower()
    if fmt not in ("stl", "3mf"):
        fmt = "stl"
    work = tempfile.mkdtemp(prefix="foundry_scad_")
    try:
        scad_path = os.path.join(work, "in.scad")
        out_path = os.path.join(work, f"out.{fmt}")
        with open(scad_path, "w", encoding="utf-8") as f:
            f.write(scad or "")
        proc = subprocess.run([exe, "-o", out_path, scad_path],
                              capture_output=True, timeout=180)
        if proc.returncode != 0 or not os.path.isfile(out_path):
            msg = (proc.stderr or proc.stdout or b"").decode("utf-8", errors="replace")[:4000]
            raise RuntimeError(msg or "openscad failed")
        with open(out_path, "rb") as f:
            data = f.read()
        return data, {"kernel": "openscad", "format": fmt, "bytes": len(data)}
    finally:
        _sh.rmtree(work, ignore_errors=True)


def build_stl(schema: dict) -> Tuple[bytes, dict]:
    """schema -> (stl_bytes, stats). See PRD §8.5 for the schema shape."""
    inner = [float(x) for x in schema.get("inner", [62, 48, 26])]
    wall = float(schema.get("wall_mm", schema.get("wall", 2.0)))
    cutouts = schema.get("cutouts", []) or []
    standoffs = schema.get("standoffs", 0)
    lid = schema.get("lid")
    vents = schema.get("vents", []) or []
    mount = schema.get("mount", "none")
    fmt = str(schema.get("format", "stl")).lower()
    arrange = str(schema.get("arrange", "exploded")).lower()
    try:
        return _csg_build(inner, wall, cutouts, standoffs, lid, vents, mount, fmt, arrange)
    except Exception:
        return _fallback_build(inner, wall)   # dependency-free fallback is STL only
