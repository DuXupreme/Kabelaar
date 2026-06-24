"""Autosave en crash-recovery (Batch 8.3).

Schrijft periodiek een herstelbestand in de app-data map zodat niet-opgeslagen
werk na een onverwachte afsluiting (crash, stroomuitval) terug te halen is.

Bevat geen Tkinter-code: dit zijn pure helpers (pad, envelope, parsen,
beschrijven) die los te testen zijn. De timer- en dialoog-wiring zit in de
hoofd-app (`HarnessDrawingStudio`).
"""

from __future__ import annotations

import json
import time
from pathlib import Path
from typing import Optional

RECOVERY_FILE_NAME = "autosave_recovery.json"
RECOVERY_KIND = "kabelboom-autosave"


def recovery_path(appdata_dir: Path) -> Path:
    """Pad van het herstelbestand binnen de app-data map."""
    return Path(appdata_dir) / RECOVERY_FILE_NAME


def recovery_envelope(project_dict: dict, project_path: Optional[str], saved_at: Optional[float] = None) -> dict:
    """Verpak een project-dict met herstelmetadata (tijdstip + oorspronkelijk pad)."""
    return {
        "kind": RECOVERY_KIND,
        "saved_at": float(saved_at if saved_at is not None else time.time()),
        "project_path": project_path or "",
        "project": project_dict,
    }


def parse_recovery(text: str) -> Optional[dict]:
    """Lees een herstelbestand; geef ``None`` bij ongeldige of vreemde inhoud."""
    try:
        data = json.loads(text)
    except (ValueError, TypeError):
        return None
    if not isinstance(data, dict) or data.get("kind") != RECOVERY_KIND:
        return None
    if not isinstance(data.get("project"), dict):
        return None
    return data


def describe_recovery(envelope: dict) -> str:
    """Menselijke omschrijving voor de hersteldialoog (bestandsnaam + tijdstip)."""
    raw_path = str(envelope.get("project_path", "")).strip()
    name = Path(raw_path).name if raw_path else "(niet-opgeslagen project)"
    saved_at = envelope.get("saved_at")
    try:
        when = time.strftime("%Y-%m-%d %H:%M", time.localtime(float(saved_at)))
    except (TypeError, ValueError):
        when = "onbekend tijdstip"
    return f"{name} — laatst automatisch bewaard op {when}"
