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


@app.get("/health")
def health() -> JSONResponse:
    return JSONResponse({"status": "ok", "service": "foundry-cad", "kernel": "builtin"})


@app.post("/enclosure")
def build_enclosure(schema: EnclosureSchema) -> StreamingResponse:
    stl, stats = enclosure.build_stl(schema.model_dump())
    headers = {
        "X-Foundry-Kernel": str(stats["kernel"]),
        "X-Foundry-Triangles": str(stats["triangles"]),
        "X-Foundry-Bytes": str(stats["bytes"]),
        "X-Foundry-Outer": ",".join(str(x) for x in stats["outer_mm"]),
        "Content-Disposition": 'attachment; filename="enclosure.stl"',
    }
    return StreamingResponse(io.BytesIO(stl), media_type="model/stl", headers=headers)


if __name__ == "__main__":
    import argparse
    import uvicorn

    parser = argparse.ArgumentParser()
    parser.add_argument("--host", default="127.0.0.1")
    parser.add_argument("--port", type=int, default=8731)
    args = parser.parse_args()
    uvicorn.run(app, host=args.host, port=args.port, log_level="warning")
