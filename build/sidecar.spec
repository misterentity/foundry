# PyInstaller spec for the Foundry CAD sidecar (PRD §11, §16).
# Build from sidecar/ with the venv active:  pyinstaller --noconfirm ../build/sidecar.spec
# Produces a single-folder bundle dist/foundry-cad/ (foundry-cad.exe) that ships inside the
# installer and is spawned over localhost. FastAPI/uvicorn need their submodules + metadata
# collected, so we use collect_all for the web stack. build123d (if installed) is bundled too.

# -*- mode: python ; coding: utf-8 -*-
import os
from PyInstaller.utils.hooks import collect_all, collect_submodules

datas, binaries, hiddenimports = [], [], []
for pkg in ("uvicorn", "fastapi", "starlette", "pydantic", "pydantic_core", "anyio", "click", "h11",
            "trimesh", "manifold3d", "numpy"):
    try:
        d, b, h = collect_all(pkg)
        datas += d; binaries += b; hiddenimports += h
    except Exception:
        pass
hiddenimports += collect_submodules("uvicorn")

# Optional CAD kernel — only bundled if installed in the build env.
try:
    d, b, h = collect_all("build123d")
    datas += d; binaries += b; hiddenimports += h
except Exception:
    pass

block_cipher = None
sidecar_dir = os.path.abspath(os.getcwd())  # run from sidecar/
server_py = os.path.join(sidecar_dir, 'server.py')  # absolute — spec dir differs from cwd

a = Analysis(
    [server_py],
    pathex=[sidecar_dir],
    binaries=binaries,
    datas=datas,
    hiddenimports=hiddenimports,
    hookspath=[],
    runtime_hooks=[],
    excludes=[],
    cipher=block_cipher,
)
pyz = PYZ(a.pure, a.zipped_data, cipher=block_cipher)
exe = EXE(pyz, a.scripts, [], exclude_binaries=True, name='foundry-cad',
          debug=False, strip=False, upx=False, console=True)
coll = COLLECT(exe, a.binaries, a.zipfiles, a.datas, strip=False, upx=False, name='foundry-cad')
