"""Auto-update via Velopack/GitHub voor de geinstalleerde app.

De gebundelde Velopack `Update.exe` kan alleen een al-gedownload pakket
toepassen (`apply`); het zoeken en downloaden doen we hier zelf:

  1. lees de release-feed (`releases.win.json`) van de laatste GitHub-release
  2. vergelijk de nieuwste `Full`-versie met de huidige (`APP_VERSION`)
  3. download de bijbehorende `.nupkg`
  4. `Update.exe apply -p <nupkg> --waitPid <pid>`  -> installeert + herstart

Bij draaien vanuit broncode (niet geinstalleerd) is updaten niet beschikbaar;
`is_update_supported()` geeft dan False.
"""
from __future__ import annotations

import json
import os
import subprocess
import sys
import urllib.request
from dataclasses import dataclass
from pathlib import Path
from typing import Callable, Optional

# Wordt bij de build in _version.py gezet (zie tools/build_release.ps1).
try:
    from _version import APP_VERSION
except Exception:
    APP_VERSION = "0.0.0"

GITHUB_REPO = "DuXupreme/Kabelaar"
CHANNEL = "win"
_FEED_ASSET = f"releases.{CHANNEL}.json"
_USER_AGENT = "KabelboomTekenstudio-Updater"
_TIMEOUT = 30


@dataclass
class UpdateInfo:
    version: str
    file_name: str
    download_url: str
    size: int


# --------------------------------------------------------------------------
# Locatie / detectie
# --------------------------------------------------------------------------
def _is_frozen() -> bool:
    return getattr(sys, "frozen", False)


def _root_dir() -> Optional[Path]:
    """Velopack-installatiemap (bovenliggend aan 'current')."""
    if not _is_frozen():
        return None
    # exe staat in <root>/current/Kabelboom Tekenstudio.exe
    return Path(sys.executable).resolve().parent.parent


def _update_exe() -> Optional[Path]:
    root = _root_dir()
    if root is None:
        return None
    exe = root / "Update.exe"
    return exe if exe.exists() else None


def is_update_supported() -> bool:
    """True als de app als Velopack-installatie draait (Update.exe aanwezig)."""
    return _update_exe() is not None


# --------------------------------------------------------------------------
# Versievergelijking
# --------------------------------------------------------------------------
def parse_version(text: str) -> tuple:
    """'1.2.3-beta' -> (1, 2, 3). Pre-release-suffix wordt genegeerd."""
    core = (text or "").strip().lstrip("vV").split("+", 1)[0].split("-", 1)[0]
    parts = []
    for chunk in core.split("."):
        try:
            parts.append(int(chunk))
        except ValueError:
            parts.append(0)
    while len(parts) < 3:
        parts.append(0)
    return tuple(parts[:3])


def is_newer(candidate: str, current: str) -> bool:
    return parse_version(candidate) > parse_version(current)


# --------------------------------------------------------------------------
# Netwerk
# --------------------------------------------------------------------------
def _http_json(url: str):
    req = urllib.request.Request(url, headers={
        "User-Agent": _USER_AGENT,
        "Accept": "application/json",
    })
    with urllib.request.urlopen(req, timeout=_TIMEOUT) as resp:
        return json.loads(resp.read().decode("utf-8"))


def _http_bytes(url: str) -> bytes:
    req = urllib.request.Request(url, headers={"User-Agent": _USER_AGENT})
    with urllib.request.urlopen(req, timeout=_TIMEOUT) as resp:
        return resp.read()


def check_for_updates() -> Optional[UpdateInfo]:
    """Geeft UpdateInfo als er een nieuwere versie is, anders None.

    Gooit een uitzondering bij netwerk-/feedproblemen (de aanroeper beslist
    of dat stil genegeerd of getoond wordt).
    """
    latest = _http_json(
        f"https://api.github.com/repos/{GITHUB_REPO}/releases/latest"
    )
    assets = {a["name"]: a for a in latest.get("assets", [])}
    feed_asset = assets.get(_FEED_ASSET)
    if not feed_asset:
        return None  # nog geen Velopack-release gepubliceerd

    feed = json.loads(
        _http_bytes(feed_asset["browser_download_url"]).decode("utf-8")
    )
    best: Optional[UpdateInfo] = None
    for entry in feed.get("Assets", []):
        if entry.get("Type") != "Full":
            continue
        version = entry.get("Version", "0.0.0")
        if best is None or is_newer(version, best.version):
            gh_asset = assets.get(entry.get("FileName", ""))
            if gh_asset:
                best = UpdateInfo(
                    version=version,
                    file_name=entry["FileName"],
                    download_url=gh_asset["browser_download_url"],
                    size=int(entry.get("Size", 0)),
                )
    if best and is_newer(best.version, APP_VERSION):
        return best
    return None


# --------------------------------------------------------------------------
# Downloaden + toepassen
# --------------------------------------------------------------------------
def download_package(
    info: UpdateInfo,
    progress: Optional[Callable[[int, int], None]] = None,
) -> Path:
    """Downloadt de nupkg naar de packages-map en geeft het pad terug."""
    root = _root_dir()
    if root is None:
        raise RuntimeError("Niet geinstalleerd; updaten niet mogelijk.")
    packages = root / "packages"
    packages.mkdir(parents=True, exist_ok=True)
    dest = packages / info.file_name

    req = urllib.request.Request(
        info.download_url, headers={"User-Agent": _USER_AGENT}
    )
    with urllib.request.urlopen(req, timeout=_TIMEOUT) as resp:
        total = int(resp.headers.get("Content-Length") or info.size or 0)
        done = 0
        tmp = dest.with_suffix(dest.suffix + ".partial")
        with open(tmp, "wb") as fh:
            while True:
                chunk = resp.read(262144)
                if not chunk:
                    break
                fh.write(chunk)
                done += len(chunk)
                if progress:
                    progress(done, total)
        tmp.replace(dest)
    return dest


def apply_and_restart(nupkg: Path) -> None:
    """Roept Update.exe aan om het pakket toe te passen en de app te herstarten.

    De updater wacht tot dit proces is afgesloten (--waitPid); sluit de app
    daarom meteen na deze aanroep af.
    """
    update_exe = _update_exe()
    if update_exe is None:
        raise RuntimeError("Update.exe niet gevonden.")

    flags = 0
    if os.name == "nt":
        flags = getattr(subprocess, "DETACHED_PROCESS", 0) | getattr(
            subprocess, "CREATE_NO_WINDOW", 0
        )
    subprocess.Popen(
        [str(update_exe), "apply", "--waitPid", str(os.getpid()),
         "--package", str(nupkg)],
        cwd=str(update_exe.parent),
        creationflags=flags,
        close_fds=True,
    )
