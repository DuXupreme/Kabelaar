# -*- mode: python ; coding: utf-8 -*-

# One-dir build (een map met de exe + afhankelijkheden). Dit is de structuur
# die Velopack verwacht; one-file werkt niet goed met delta-updates.

from importlib.metadata import distribution
from pathlib import Path

from PyInstaller.utils.hooks import collect_data_files

# sv-ttk levert zijn thema's als .tcl-databestanden; die moeten mee in de build,
# anders valt de app in de geinstalleerde versie terug op het standaard ttk-thema.
sv_ttk_datas = collect_data_files('sv_ttk')

# cascadio is bewust één native .pyd (geen package) met de OpenCASCADE-DLL's
# ernaast. Verzamel exact de binaries uit de geïnstalleerde wheel-metadata;
# collect_all/collect_dynamic_libs slaan single-module wheels over.
cascadio_dist = distribution('cascadio')
cascadio_binaries = []
for entry in cascadio_dist.files or []:
    source = Path(cascadio_dist.locate_file(entry))
    if source.suffix.lower() in {'.pyd', '.dll'} and source.is_file():
        cascadio_binaries.append((str(source), '.'))

a = Analysis(
    ['kabelboom_tekenstudio.py'],
    pathex=[],
    binaries=cascadio_binaries,
    datas=sv_ttk_datas,
    hiddenimports=['sv_ttk', 'cascadio'],
    hookspath=[],
    hooksconfig={},
    runtime_hooks=[],
    excludes=[],
    noarchive=False,
    optimize=0,
)
pyz = PYZ(a.pure)

exe = EXE(
    pyz,
    a.scripts,
    [],
    exclude_binaries=True,
    name='Kabelboom Tekenstudio',
    debug=False,
    bootloader_ignore_signals=False,
    strip=False,
    upx=True,
    console=False,
    disable_windowed_traceback=False,
    argv_emulation=False,
    target_arch=None,
    codesign_identity=None,
    entitlements_file=None,
    icon='assets/icon.ico',
)

coll = COLLECT(
    exe,
    a.binaries,
    a.datas,
    strip=False,
    upx=True,
    upx_exclude=[],
    name='Kabelboom Tekenstudio',
)
