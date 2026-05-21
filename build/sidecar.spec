# PyInstaller spec for the Foundry CAD sidecar (PRD §11, §16).
# Build from sidecar/ with the venv active:  pyinstaller --noconfirm ../build/sidecar.spec
# Produces a single-folder bundle dist/foundry-cad/ that ships inside the app package and is
# spawned over localhost. build123d (if installed) is picked up automatically as a hidden import.

# -*- mode: python ; coding: utf-8 -*-
import os

block_cipher = None
sidecar_dir = os.path.abspath(os.path.join(os.getcwd()))  # run from sidecar/

a = Analysis(
    ['server.py'],
    pathex=[sidecar_dir],
    binaries=[],
    datas=[],
    hiddenimports=['uvicorn', 'uvicorn.logging', 'uvicorn.loops.auto',
                   'uvicorn.protocols.http.auto', 'fastapi'],
    hookspath=[],
    runtime_hooks=[],
    excludes=[],
    win_no_prefer_redirects=False,
    win_private_assemblies=False,
    cipher=block_cipher,
)
pyz = PYZ(a.pure, a.zipped_data, cipher=block_cipher)
exe = EXE(pyz, a.scripts, [], exclude_binaries=True, name='foundry-cad',
          debug=False, strip=False, upx=True, console=True)
coll = COLLECT(exe, a.binaries, a.zipfiles, a.datas, strip=False, upx=True, name='foundry-cad')
