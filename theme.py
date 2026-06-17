"""Centrale kleur-tokens voor de UI-chrome, met licht/donker varianten.

Alleen *chrome* (canvas-achtergrond, resize-grip, subtiele labels) hoort hier.
Teken-/inktkleuren die in een project worden opgeslagen blijven in het model;
het snelkleur-palet hieronder is bewust thema-onafhankelijk.
"""

from __future__ import annotations

LIGHT = {
    "canvas_bg": "#e9eef5",
    "grip": "#cbd5e1",
    "grip_active": "#94a3b8",
    "subtle_fg": "#475569",
    "status_fg": "#334155",
}

DARK = {
    "canvas_bg": "#1b1f27",
    "grip": "#3a4150",
    "grip_active": "#55607a",
    "subtle_fg": "#9aa6b8",
    "status_fg": "#c3ccda",
}

_VALID = {"light": LIGHT, "dark": DARK}

# Snelkleur-palet in het eigenschappenpaneel. Dit zijn inktkleuren voor
# getekende objecten en staan los van het UI-thema, zodat een tekening er
# in licht- en donkermodus identiek uitziet.
DRAWING_PALETTE = [
    "#111111",
    "#ffffff",
    "#c92a2a",
    "#1d4ed8",
    "#2f9e44",
    "#e7b416",
    "#e67700",
    "#7b2cbf",
    "#25364a",
    "#1f4e79",
]


def normalize_theme(name) -> str:
    return name if name in _VALID else "light"


def tokens(name) -> dict:
    return _VALID[normalize_theme(name)]


def color(name, token: str) -> str:
    return tokens(name)[token]
