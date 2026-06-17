"""Logging-opzet met een roterend logbestand in de app-data map.

Losgetrokken zodat de hoofd-app alleen ``configure_logging()`` hoeft aan te
roepen. Bij een crash (niet-afgevangen exceptie) wordt de traceback naar het
logbestand geschreven, zodat problemen achteraf te onderzoeken zijn.
"""

from __future__ import annotations

import logging
import sys
from logging.handlers import RotatingFileHandler
from pathlib import Path
from typing import Optional

from app_settings import default_settings_path

LOG_DIR_NAME = "logs"
LOG_FILE_NAME = "kabelboom.log"
MAX_BYTES = 1_000_000
BACKUP_COUNT = 3

_configured = False


def log_dir() -> Path:
    """Map waarin logbestanden komen (naast de instellingen, in app-data)."""
    return default_settings_path().parent / LOG_DIR_NAME


def log_path() -> Path:
    return log_dir() / LOG_FILE_NAME


def configure_logging(level: int = logging.INFO) -> Optional[Path]:
    """Zet logging op met een roterend bestand en een excepthook voor crashes.

    Geeft het pad van het logbestand terug, of ``None`` als het bestand niet
    aangemaakt kon worden (de app blijft dan gewoon werken).
    """
    global _configured
    if _configured:
        return log_path()

    root = logging.getLogger()
    root.setLevel(level)
    formatter = logging.Formatter("%(asctime)s [%(levelname)s] %(name)s: %(message)s")

    path: Optional[Path] = None
    try:
        directory = log_dir()
        directory.mkdir(parents=True, exist_ok=True)
        path = directory / LOG_FILE_NAME
        file_handler = RotatingFileHandler(path, maxBytes=MAX_BYTES, backupCount=BACKUP_COUNT, encoding="utf-8")
        file_handler.setFormatter(formatter)
        root.addHandler(file_handler)
    except OSError:
        path = None

    stream_handler = logging.StreamHandler()
    stream_handler.setFormatter(formatter)
    root.addHandler(stream_handler)

    _install_excepthook()
    _configured = True
    logging.getLogger(__name__).info("Logging gestart (bestand: %s)", path)
    return path


def _install_excepthook() -> None:
    previous = sys.excepthook

    def handle_exception(exc_type, exc_value, exc_traceback):
        if issubclass(exc_type, KeyboardInterrupt):
            previous(exc_type, exc_value, exc_traceback)
            return
        logging.getLogger("crash").critical(
            "Niet-afgevangen exceptie", exc_info=(exc_type, exc_value, exc_traceback)
        )
        previous(exc_type, exc_value, exc_traceback)

    sys.excepthook = handle_exception
