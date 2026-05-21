"""
FOUNDRY — enclosure mesh builder.

Turns the closed enclosure schema (PRD §8.5) into a printable STL deterministically.
The default builder is dependency-free pure Python (a hollow, open-top box shell sized
from the component footprints + walls). build123d / OpenCASCADE is the fidelity upgrade
(adds boolean cutouts, fillets, STEP output) — see requirements.txt; this module falls
back to the built-in builder when build123d is not installed so the sidecar always runs.
"""
from __future__ import annotations

import struct
from typing import List, Tuple

Vec = Tuple[float, float, float]
Tri = Tuple[Vec, Vec, Vec]


def _sub(a: Vec, b: Vec) -> Vec:
    return (a[0] - b[0], a[1] - b[1], a[2] - b[2])


def _cross(a: Vec, b: Vec) -> Vec:
    return (a[1] * b[2] - a[2] * b[1],
            a[2] * b[0] - a[0] * b[2],
            a[0] * b[1] - a[1] * b[0])


def _normal(t: Tri) -> Vec:
    u, v = _sub(t[1], t[0]), _sub(t[2], t[0])
    n = _cross(u, v)
    m = (n[0] ** 2 + n[1] ** 2 + n[2] ** 2) ** 0.5 or 1.0
    return (n[0] / m, n[1] / m, n[2] / m)


def _quad(tris: List[Tri], p1: Vec, p2: Vec, p3: Vec, p4: Vec) -> None:
    tris.append((p1, p2, p3))
    tris.append((p1, p3, p4))


def box_shell(inner: List[float], wall: float) -> List[Tri]:
    """Hollow open-top box: outer dims = inner + 2*wall, floor thickness = wall."""
    il, iw, ih = inner
    t = wall
    ol, ow, oh = il + 2 * t, iw + 2 * t, ih + t  # closed floor, open top
    tris: List[Tri] = []

    # outer bottom (z=0)
    _quad(tris, (0, 0, 0), (0, ow, 0), (ol, ow, 0), (ol, 0, 0))
    # outer walls
    _quad(tris, (0, 0, 0), (ol, 0, 0), (ol, 0, oh), (0, 0, oh))      # front  y=0
    _quad(tris, (ol, ow, 0), (0, ow, 0), (0, ow, oh), (ol, ow, oh))  # back   y=ow
    _quad(tris, (0, ow, 0), (0, 0, 0), (0, 0, oh), (0, ow, oh))      # left   x=0
    _quad(tris, (ol, 0, 0), (ol, ow, 0), (ol, ow, oh), (ol, 0, oh))  # right  x=ol

    ix0, iy0, ix1, iy1 = t, t, ol - t, ow - t
    # top rim (z=oh) frame
    _quad(tris, (0, 0, oh), (0, t, oh), (ol, t, oh), (ol, 0, oh))            # front rim
    _quad(tris, (0, ow - t, oh), (0, ow, oh), (ol, ow, oh), (ol, ow - t, oh))  # back rim
    _quad(tris, (0, t, oh), (0, ow - t, oh), (t, ow - t, oh), (t, t, oh))      # left rim
    _quad(tris, (ol - t, t, oh), (ol - t, ow - t, oh), (ol, ow - t, oh), (ol, t, oh))  # right rim

    # inner cavity walls (z=t..oh)
    _quad(tris, (ix0, iy0, t), (ix1, iy0, t), (ix1, iy0, oh), (ix0, iy0, oh))  # inner front
    _quad(tris, (ix1, iy1, t), (ix0, iy1, t), (ix0, iy1, oh), (ix1, iy1, oh))  # inner back
    _quad(tris, (ix0, iy1, t), (ix0, iy0, t), (ix0, iy0, oh), (ix0, iy1, oh))  # inner left
    _quad(tris, (ix1, iy0, t), (ix1, iy1, t), (ix1, iy1, oh), (ix1, iy0, oh))  # inner right
    # inner floor (z=t)
    _quad(tris, (ix0, iy0, t), (ix1, iy0, t), (ix1, iy1, t), (ix0, iy1, t))

    return tris


def to_binary_stl(tris: List[Tri], header: str = "FOUNDRY enclosure") -> bytes:
    buf = bytearray()
    head = header.encode("ascii", "ignore")[:80]
    buf += head + b"\x00" * (80 - len(head))
    buf += struct.pack("<I", len(tris))
    for tri in tris:
        n = _normal(tri)
        buf += struct.pack("<3f", *n)
        for v in tri:
            buf += struct.pack("<3f", *v)
        buf += struct.pack("<H", 0)
    return bytes(buf)


def build_stl(schema: dict) -> Tuple[bytes, dict]:
    """schema -> (stl_bytes, stats). See PRD §8.5 for the schema shape."""
    inner = [float(x) for x in schema.get("inner", [62, 48, 26])]
    wall = float(schema.get("wall_mm", schema.get("wall", 2.0)))
    tris = box_shell(inner, wall)
    stl = to_binary_stl(tris)
    stats = {
        "kernel": "builtin",
        "triangles": len(tris),
        "outer_mm": [round(inner[0] + 2 * wall, 2), round(inner[1] + 2 * wall, 2), round(inner[2] + wall, 2)],
        "bytes": len(stl),
    }
    return stl, stats
