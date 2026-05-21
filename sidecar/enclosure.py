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
def _csg_build(inner, wall, cutouts, standoffs, lid):
    import numpy as np  # noqa: F401  (trimesh pulls it in; kept explicit for PyInstaller)
    import trimesh
    from trimesh.transformations import rotation_matrix

    L, Wd, H = float(inner[0]), float(inner[1]), float(inner[2])
    t = float(wall)
    ox, oy, oz = L + 2 * t, Wd + 2 * t, H + t  # outer; closed floor, open top

    # solid outer box, base at z=0, centered in x/y
    outer = trimesh.creation.box(extents=[ox, oy, oz])
    outer.apply_translation([0, 0, oz / 2])

    # cavity: open the top by overshooting upward
    cav = trimesh.creation.box(extents=[L, Wd, H + t + 2])
    cav.apply_translation([0, 0, t + (H + t + 2) / 2])
    shell = outer.difference(cav, engine="manifold")

    through = max(4.0, t * 4)
    margin = 2.0

    for c in cutouts or []:
        try:
            solid = _cutout_solid(trimesh, rotation_matrix, c, ox, oy, oz, through, margin)
            if solid is not None:
                shell = shell.difference(solid, engine="manifold")
        except Exception:
            continue  # a bad cutout never breaks the whole build

    n = standoffs if isinstance(standoffs, int) else (len(standoffs) if standoffs else 0)
    for post in _standoff_posts(trimesh, n, L, Wd, t, H):
        try:
            shell = shell.union(post, engine="manifold")
        except Exception:
            continue

    stl = shell.export(file_type="stl")
    if isinstance(stl, str):
        stl = stl.encode("utf-8")
    stats = {
        "kernel": "manifold",
        "triangles": int(len(shell.faces)),
        "outer_mm": [round(ox, 2), round(oy, 2), round(oz, 2)],
        "bytes": len(stl),
    }
    return bytes(stl), stats


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


def _standoff_posts(trimesh, n, L, Wd, t, H):
    """Up to 4 corner screw bosses (outer Ø6.4, M2 pilot Ø2.2), rising from the floor."""
    if n <= 0:
        return []
    height = max(4.0, H - 2.0)
    inset = 6.0
    corners = [
        (L / 2 - inset, Wd / 2 - inset),
        (-(L / 2 - inset), Wd / 2 - inset),
        (L / 2 - inset, -(Wd / 2 - inset)),
        (-(L / 2 - inset), -(Wd / 2 - inset)),
    ]
    posts = []
    for (px, py) in corners[: min(n, 4)]:
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


def build_stl(schema: dict) -> Tuple[bytes, dict]:
    """schema -> (stl_bytes, stats). See PRD §8.5 for the schema shape."""
    inner = [float(x) for x in schema.get("inner", [62, 48, 26])]
    wall = float(schema.get("wall_mm", schema.get("wall", 2.0)))
    cutouts = schema.get("cutouts", []) or []
    standoffs = schema.get("standoffs", 0)
    lid = schema.get("lid")
    try:
        return _csg_build(inner, wall, cutouts, standoffs, lid)
    except Exception:
        return _fallback_build(inner, wall)
