"""Warm 'Klei & inkt' design-tokens voor de UI-chrome (licht/donker).

Alleen *chrome* (vensters, panelen, knoppen, randen, accent) hoort hier.
Teken-/inktkleuren die in een project worden opgeslagen blijven in het model;
het snelkleur-palet onderaan is bewust thema-onafhankelijk, zodat een tekening
er in licht- en donkermodus identiek uitziet.

Palet: warme cremes en zand met een terracotta-klei accent en diepe inkt-tekst.
De sleutels vormen een klein design-system dat `warm_theme.py` verbruikt:

  app_bg / surface / surface_alt / surface_active  - oppervlakken (donker->licht)
  canvas_bg                                         - de tekenmat achter het blad
  text / subtle_fg / status_fg / header_fg          - typografische tinten
  border / border_strong                            - randen
  field_bg / field_border                           - invoervelden
  accent / accent_hover / accent_active / accent_fg - het klei-accent
  selection_bg / selection_fg                       - selecties (lijsten/tekst)
  grip / grip_active                                - de zijpaneel-sleepgreep
  disabled_fg / disabled_bg                         - uitgeschakelde controls
  scrollbar / scrollbar_thumb / scrollbar_active    - schuifbalken
  menu_bg / menu_fg / menu_active_bg / menu_active_fg - (context)menu's
  tooltip_bg / tooltip_fg                           - zwevende tooltips
"""

from __future__ import annotations

# --- Klei & inkt, warm daglicht -------------------------------------------
LIGHT = {
    # Oppervlakken
    "app_bg": "#F3ECE0",        # venster + zijpaneel (warm zand)
    "surface": "#FBF6EC",       # knoppen / kaarten (cremewit)
    "surface_alt": "#F0E6D6",   # subtiele hover-baan / koppen
    "surface_active": "#EADFCC",
    "canvas_bg": "#E7DECF",     # tekenmat achter het witte blad (warm taupe)
    # Typografie
    "text": "#2A2520",          # diepe inkt
    "subtle_fg": "#6E6253",     # gedempt taupe-grijs
    "status_fg": "#4C4234",
    "header_fg": "#3A3024",     # serif-koppen / wordmerk
    # Randen
    "border": "#E2D7C4",
    "border_strong": "#CCBCA1",
    # Velden
    "field_bg": "#FFFCF6",
    "field_border": "#D8CBB4",
    # Accent (klei)
    "accent": "#C2674A",
    "accent_hover": "#B0573B",
    "accent_active": "#9C4A30",
    "accent_fg": "#FFF7EF",
    "accent_soft": "#EFD9CC",
    # Selectie
    "selection_bg": "#C2674A",
    "selection_fg": "#FFF7EF",
    # Sleepgreep
    "grip": "#DCD0BC",
    "grip_active": "#C2674A",
    # Uitgeschakeld
    "disabled_fg": "#B2A78F",
    "disabled_bg": "#EEE7DA",
    # Schuifbalken
    "scrollbar": "#EBE2D2",
    "scrollbar_thumb": "#D4C6AE",
    "scrollbar_active": "#BFAD90",
    # Menu's
    "menu_bg": "#FBF6EC",
    "menu_fg": "#2A2520",
    "menu_active_bg": "#C2674A",
    "menu_active_fg": "#FFF7EF",
    # Tooltip
    "tooltip_bg": "#2A2520",
    "tooltip_fg": "#F4ECDD",
}

# --- Klei & inkt, warm espresso -------------------------------------------
DARK = {
    "app_bg": "#242019",
    "surface": "#2F2A22",
    "surface_alt": "#383128",
    "surface_active": "#423A2E",
    "canvas_bg": "#1F1B15",
    "text": "#F1E7D6",
    "subtle_fg": "#B2A48D",
    "status_fg": "#CDBFA9",
    "header_fg": "#ECE0CA",
    "border": "#3C342A",
    "border_strong": "#4E4435",
    "field_bg": "#2A251E",
    "field_border": "#473E31",
    "accent": "#D67E58",
    "accent_hover": "#E28A63",
    "accent_active": "#C06C49",
    "accent_fg": "#241813",
    "accent_soft": "#46342A",
    "selection_bg": "#D67E58",
    "selection_fg": "#241813",
    "grip": "#3C342A",
    "grip_active": "#D67E58",
    "disabled_fg": "#71634F",
    "disabled_bg": "#2A251E",
    "scrollbar": "#28231C",
    "scrollbar_thumb": "#473E31",
    "scrollbar_active": "#5D5141",
    "menu_bg": "#2F2A22",
    "menu_fg": "#F1E7D6",
    "menu_active_bg": "#D67E58",
    "menu_active_fg": "#241813",
    "tooltip_bg": "#15110C",
    "tooltip_fg": "#F4ECDD",
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
