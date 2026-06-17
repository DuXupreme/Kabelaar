from __future__ import annotations

import json
import os
import sys
from pathlib import Path
from typing import Any

from project_io import write_text_atomic


def default_settings_path() -> Path:
    if getattr(sys, "frozen", False):
        appdata = os.environ.get("APPDATA")
        base = Path(appdata) if appdata else Path.home() / "AppData" / "Roaming"
        return base / "Kabelboom Studio" / "settings.json"
    return Path(__file__).with_name("settings.json")


SETTINGS_PATH = default_settings_path()


def load_settings() -> dict[str, Any]:
    try:
        data = json.loads(SETTINGS_PATH.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError):
        return {}
    return data if isinstance(data, dict) else {}


def save_settings(settings: dict[str, Any]) -> None:
    SETTINGS_PATH.parent.mkdir(parents=True, exist_ok=True)
    write_text_atomic(SETTINGS_PATH, json.dumps(settings, indent=2, ensure_ascii=False), backup=False)


def load_app_settings(app_key: str) -> dict[str, Any]:
    settings = load_settings().get(app_key, {})
    return dict(settings) if isinstance(settings, dict) else {}


def update_app_settings(app_key: str, **values: Any) -> dict[str, Any]:
    settings = load_settings()
    app_settings = settings.get(app_key, {})
    if not isinstance(app_settings, dict):
        app_settings = {}
    app_settings.update(values)
    settings[app_key] = app_settings
    save_settings(settings)
    return dict(app_settings)


def existing_dir(path_text: object) -> str | None:
    if not path_text:
        return None
    path = Path(str(path_text))
    return str(path) if path.is_dir() else None


def parent_dir(path_text: object) -> str:
    return str(Path(str(path_text)).expanduser().parent)
