"""
FOUNDRY — CAD sidecar (PRD §5, §8.5, §11).

A tiny FastAPI service on 127.0.0.1 that the .NET app spawns as a child process and talks
to over localhost. POST /enclosure turns the enclosure schema into an STL; GET /health is the
startup probe. Kept deliberately small and dependency-light so it bundles cleanly (PyInstaller,
Phase 6). build123d is the optional fidelity upgrade — enclosure.py falls back to a built-in
mesh builder when it isn't installed.
"""
from __future__ import annotations

import io
import os
import re
import shutil

from fastapi import FastAPI
from fastapi.responses import JSONResponse, StreamingResponse
from pydantic import BaseModel

import enclosure

app = FastAPI(title="Foundry CAD sidecar", version="0.4.1")


class EnclosureSchema(BaseModel):
    type: str = "box_enclosure"
    inner: list[float] = [62, 48, 26]
    wall_mm: float = 2.0
    lid: dict | str | None = None
    cutouts: list[dict] = []
    standoffs: list[dict] | int | None = None
    vents: list[dict] = []
    mount: str = "none"
    format: str = "stl"   # stl | 3mf
    arrange: str = "exploded"   # exploded (preview: lid above base) | print (both flat on the plate)
    board: dict | None = None   # {widthMm, depthMm, thicknessMm, standoffMm, holes:[[x,y],...]}


def _kernel_name() -> str:
    """What the CSG path will ACTUALLY use. This used to be the constant "builtin" regardless, so a
    sidecar running without manifold3d reported the same string as one with it — the health endpoint
    asserting a fact it never checked."""
    try:
        import trimesh  # noqa: F401
        import manifold3d  # noqa: F401
        return "manifold"
    except Exception:
        return "builtin"


@app.get("/health")
def health() -> JSONResponse:
    # The token is per-spawn and comes from the parent's environment. Echoing it is what lets the host
    # tell ITS child apart from any other Foundry sidecar already listening on this machine.
    return JSONResponse({
        "status": "ok",
        "service": "foundry-cad",
        "token": os.environ.get("FOUNDRY_SIDECAR_TOKEN", ""),
        "kernel": _kernel_name(),
    })


@app.post("/enclosure")
def build_enclosure(schema: EnclosureSchema) -> StreamingResponse:
    data, stats = enclosure.build_stl(schema.model_dump())
    fmt = str(stats.get("format", "stl")).lower()
    media = "model/3mf" if fmt == "3mf" else "model/stl"
    headers = {
        "X-Foundry-Kernel": str(stats["kernel"]),
        "X-Foundry-Format": fmt,
        "X-Foundry-Triangles": str(stats["triangles"]),
        "X-Foundry-Bytes": str(stats["bytes"]),
        "X-Foundry-Outer": ",".join(str(x) for x in stats["outer_mm"]),
        # Features the face bounds forced out of position. Clamping is silent in the geometry, so it has
        # to travel with the response or a corner port quietly ends up somewhere else.
        "X-Foundry-Moved": str(len(stats.get("movedCutouts") or [])),
        "Content-Disposition": f'attachment; filename="enclosure.{fmt}"',
    }
    return StreamingResponse(io.BytesIO(data), media_type=media, headers=headers)


# ----------------------------------------------------------------------------
# /enclosure/scad — AI-written OpenSCAD code → STL/3MF via OpenSCAD CLI (PRD v2 Phase A)
# ----------------------------------------------------------------------------
class ScadRequest(BaseModel):
    scad: str
    format: str = "stl"   # stl | 3mf


# OpenSCAD file-access directives — refuse them so an AI-authored/prompt-injected script can't read or
# exfiltrate local files through the renderer (the C# client checks too; this is defense in depth).
_SCAD_UNSAFE = re.compile(r"\b(include\s*<|use\s*<|import\s*\(|surface\s*\()", re.IGNORECASE)


def _openscad_exe() -> str | None:
    p = os.environ.get("OPENSCAD")
    if p and os.path.exists(p):
        return p
    p = shutil.which("openscad")
    if p:
        return p
    base = os.path.join(os.environ.get("LOCALAPPDATA", ""), "Foundry", "tools", "openscad")
    candidates = [os.path.join(base, "openscad.exe")]
    if os.path.isdir(base):   # extracted zip puts the binary under openscad-XXXX/
        for name in os.listdir(base):
            candidates.append(os.path.join(base, name, "openscad.exe"))
    for c in candidates:
        if os.path.isfile(c):
            return c
    return None


@app.post("/enclosure/scad")
def build_scad(req: ScadRequest):
    if _SCAD_UNSAFE.search(req.scad or ""):
        return JSONResponse({"detail": "refused: script uses a file-access directive (include/use/import/surface)"}, status_code=400)
    exe = _openscad_exe()
    if exe is None:
        return JSONResponse({"detail": "openscad not installed"}, status_code=503)
    try:
        data, stats = enclosure.build_scad(req.scad, req.format, exe)
    except RuntimeError as ex:
        return JSONResponse({"detail": "openscad error", "stderr": str(ex)}, status_code=400)
    fmt = stats["format"]
    media = "model/3mf" if fmt == "3mf" else "model/stl"
    headers = {
        "X-Foundry-Kernel": "openscad",
        "X-Foundry-Format": fmt,
        "X-Foundry-Bytes": str(stats["bytes"]),
        "Content-Disposition": f'attachment; filename="enclosure.{fmt}"',
    }
    return StreamingResponse(io.BytesIO(data), media_type=media, headers=headers)


if __name__ == "__main__":
    import argparse
    import uvicorn

    parser = argparse.ArgumentParser()
    parser.add_argument("--host", default="127.0.0.1")
    parser.add_argument("--port", type=int, default=8731)
    args = parser.parse_args()
    uvicorn.run(app, host=args.host, port=args.port, log_level="warning")
