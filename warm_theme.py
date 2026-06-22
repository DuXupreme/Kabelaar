"""Een eigen, warm 'Klei & inkt' ttk-thema.

Bouwt boven op de volledig herkleurbare ``clam``-basis (in tegenstelling tot een
image-thema zoals sv-ttk is clam met ``style.configure``/``style.map`` tot in de
puntjes te kleuren). Hier wordt:

* één warm, humanistisch lettertype (Corbel) voor de hele UI gekozen - koppen,
  body en cijfers delen dezelfde familie, alleen grootte/gewicht verschillen -
  met nette fallbacks per beschikbaarheid;
* elke ttk-widgetklasse en de app-specifieke stijlen (Tool/Primary/Accent/
  PanelHeader/Subtle/Status/Coord) gekleurd vanuit ``theme.py``-tokens;
* de niet-ttk chrome (menu's, listboxes, combobox-popdowns) via de option-db
  meegekleurd.

Eén bron van waarheid voor kleur is ``theme.py``; dit bestand vertaalt die
tokens naar Tk/ttk.
"""

from __future__ import annotations

import tkinter as tk
from tkinter import font as tkfont, ttk

import theme as ui_theme

# Lettertype-voorkeuren, in volgorde van smaak; de eerste die op het systeem
# beschikbaar is wint. Allemaal warm/humanistisch i.p.v. generiek. De hele UI
# gebruikt bewust één familie (koppen/body/cijfers), alleen grootte en gewicht
# variëren.
UI_FONT_CANDIDATES = ("Corbel", "Segoe UI Variable Text", "Candara", "Segoe UI", "Calibri")

# Named fonts die de UI-letter (face) moeten volgen. Maten worden elders
# geschaald; hier zetten we alleen de familie.
_UI_FACE_FONTS = (
    "TkDefaultFont",
    "TkTextFont",
    "TkMenuFont",
    "TkHeadingFont",
    "TkCaptionFont",
    "TkSmallCaptionFont",
    "TkIconFont",
    "TkTooltipFont",
)


def _first_available(root, candidates, fallback="TkDefaultFont"):
    try:
        families = set(tkfont.families(root))
    except tk.TclError:
        return candidates[0] if candidates else fallback
    for name in candidates:
        if name in families:
            return name
    return fallback


def resolve_fonts(root) -> dict:
    """Kies (en cache) het UI-lettertype voor dit Tk-proces.

    De hele UI deelt één familie; ``heading`` en ``mono`` zijn aliassen voor
    ``ui`` zodat de stijl-code dezelfde sleutels kan blijven gebruiken.
    """
    cached = getattr(root, "_warm_fonts", None)
    if cached:
        return cached
    ui = _first_available(root, UI_FONT_CANDIDATES)
    fonts = {"ui": ui, "heading": ui, "mono": ui}
    try:
        root._warm_fonts = fonts
    except Exception:
        pass
    return fonts


def _btn_map(style, name, t, *, base, hover, pressed, border, border_hover):
    style.map(
        name,
        background=[("disabled", t["disabled_bg"]), ("pressed", pressed), ("active", hover)],
        foreground=[("disabled", t["disabled_fg"])],
        bordercolor=[("disabled", t["border"]), ("pressed", border_hover), ("active", border_hover)],
        lightcolor=[("pressed", border_hover), ("active", border_hover)],
        darkcolor=[("pressed", border_hover), ("active", border_hover)],
    )


def configure_ttk(style: ttk.Style, t: dict, fonts: dict):
    """Kleur de hele ttk-laag (clam-basis) vanuit de tokens ``t``."""
    ui = fonts["ui"]
    heading = fonts["heading"]
    mono = fonts["mono"]
    body = (ui, 10)
    small = (ui, 9)

    try:
        style.theme_use("clam")
    except tk.TclError:
        pass

    # Basis voor alle widgets
    style.configure(
        ".",
        background=t["app_bg"],
        foreground=t["text"],
        fieldbackground=t["field_bg"],
        bordercolor=t["border"],
        lightcolor=t["border"],
        darkcolor=t["border"],
        troughcolor=t["surface_alt"],
        focuscolor=t["accent"],
        selectbackground=t["selection_bg"],
        selectforeground=t["selection_fg"],
        relief="flat",
        font=body,
    )
    style.map(".", foreground=[("disabled", t["disabled_fg"])])

    # Frames & labels
    style.configure("TFrame", background=t["app_bg"])
    style.configure("Card.TFrame", background=t["surface"])
    style.configure("Canvas.TFrame", background=t["canvas_bg"])
    style.configure("TLabel", background=t["app_bg"], foreground=t["text"], font=body)
    style.configure("Subtle.TLabel", background=t["app_bg"], foreground=t["subtle_fg"], font=small)
    style.configure("Status.TLabel", background=t["app_bg"], foreground=t["status_fg"], font=(heading, 11))
    style.configure("Coord.TLabel", background=t["app_bg"], foreground=t["subtle_fg"], font=(mono, 9))

    # Neutrale "chip"-knoppen (warm cremewit met zachte rand; accent-rand bij hover)
    for name, pad in (("TButton", (12, 7)), ("Tool.TButton", (10, 6))):
        style.configure(
            name,
            background=t["surface"],
            foreground=t["text"],
            bordercolor=t["border_strong"],
            lightcolor=t["border_strong"],
            darkcolor=t["border_strong"],
            focuscolor=t["accent"],
            relief="flat",
            anchor="center",
            padding=pad,
            font=body,
        )
        _btn_map(
            style, name, t,
            base=t["surface"], hover=t["surface_alt"], pressed=t["surface_active"],
            border=t["border_strong"], border_hover=t["accent"],
        )

    # Accent / primaire knoppen (gevuld klei)
    for name, pad, font in (
        ("Accent.TButton", (12, 6), (ui, 10, "bold")),
        ("Primary.TButton", (16, 7), (ui, 10, "bold")),
    ):
        style.configure(
            name,
            background=t["accent"],
            foreground=t["accent_fg"],
            bordercolor=t["accent"],
            lightcolor=t["accent"],
            darkcolor=t["accent"],
            focuscolor=t["accent_fg"],
            relief="flat",
            anchor="center",
            padding=pad,
            font=font,
        )
        style.map(
            name,
            background=[("disabled", t["disabled_bg"]), ("pressed", t["accent_active"]), ("active", t["accent_hover"])],
            foreground=[("disabled", t["disabled_fg"])],
            bordercolor=[("pressed", t["accent_active"]), ("active", t["accent_hover"])],
            lightcolor=[("pressed", t["accent_active"]), ("active", t["accent_hover"])],
            darkcolor=[("pressed", t["accent_active"]), ("active", t["accent_hover"])],
        )

    # Paneelkoppen (inklap-knoppen): ogen als serif-koppen, niet als knop
    style.configure(
        "PanelHeader.TButton",
        background=t["app_bg"],
        foreground=t["header_fg"],
        bordercolor=t["app_bg"],
        lightcolor=t["app_bg"],
        darkcolor=t["app_bg"],
        focuscolor=t["app_bg"],
        relief="flat",
        anchor="w",
        padding=(2, 7),
        font=(heading, 12),
    )
    style.map(
        "PanelHeader.TButton",
        background=[("pressed", t["surface_active"]), ("active", t["surface_alt"])],
        foreground=[("active", t["accent"]), ("pressed", t["accent_active"])],
        bordercolor=[("active", t["surface_alt"]), ("pressed", t["surface_active"])],
        lightcolor=[("active", t["surface_alt"]), ("pressed", t["surface_active"])],
        darkcolor=[("active", t["surface_alt"]), ("pressed", t["surface_active"])],
    )

    # Invoervelden
    style.configure(
        "TEntry",
        fieldbackground=t["field_bg"],
        foreground=t["text"],
        bordercolor=t["field_border"],
        lightcolor=t["field_border"],
        darkcolor=t["field_border"],
        insertcolor=t["accent"],
        selectbackground=t["selection_bg"],
        selectforeground=t["selection_fg"],
        padding=(8, 5),
        relief="flat",
    )
    style.map(
        "TEntry",
        bordercolor=[("focus", t["accent"]), ("hover", t["border_strong"])],
        lightcolor=[("focus", t["accent"])],
        darkcolor=[("focus", t["accent"])],
        fieldbackground=[("disabled", t["disabled_bg"]), ("readonly", t["surface"])],
        foreground=[("disabled", t["disabled_fg"])],
    )

    style.configure("TSpinbox", arrowcolor=t["subtle_fg"], **_entry_like(t))
    style.map("TSpinbox", bordercolor=[("focus", t["accent"])], arrowcolor=[("active", t["accent"])])

    # Comboboxen
    style.configure(
        "TCombobox",
        fieldbackground=t["field_bg"],
        foreground=t["text"],
        background=t["surface"],
        bordercolor=t["field_border"],
        lightcolor=t["field_border"],
        darkcolor=t["field_border"],
        arrowcolor=t["subtle_fg"],
        selectbackground=t["selection_bg"],
        selectforeground=t["selection_fg"],
        padding=(8, 4),
        relief="flat",
    )
    style.map(
        "TCombobox",
        fieldbackground=[("readonly", t["field_bg"]), ("disabled", t["disabled_bg"])],
        foreground=[("disabled", t["disabled_fg"])],
        bordercolor=[("focus", t["accent"]), ("active", t["border_strong"])],
        lightcolor=[("focus", t["accent"])],
        darkcolor=[("focus", t["accent"])],
        arrowcolor=[("active", t["accent"]), ("disabled", t["disabled_fg"])],
    )

    # Selectievakjes & radioknoppen
    for cls in ("TCheckbutton", "TRadiobutton"):
        style.configure(
            cls,
            background=t["app_bg"],
            foreground=t["text"],
            indicatorbackground=t["field_bg"],
            indicatorforeground=t["accent_fg"],
            bordercolor=t["border_strong"],
            lightcolor=t["border_strong"],
            darkcolor=t["border_strong"],
            focuscolor=t["accent"],
            padding=(3, 3),
            font=body,
        )
        style.map(
            cls,
            background=[("active", t["app_bg"])],
            foreground=[("disabled", t["disabled_fg"])],
            indicatorbackground=[
                ("disabled", t["disabled_bg"]),
                ("selected", t["accent"]),
                ("active", t["surface_alt"]),
            ],
            indicatorforeground=[("selected", t["accent_fg"])],
            bordercolor=[("selected", t["accent"]), ("active", t["accent"])],
            lightcolor=[("selected", t["accent"]), ("active", t["accent"])],
            darkcolor=[("selected", t["accent"]), ("active", t["accent"])],
        )

    # Menubutton
    style.configure(
        "TMenubutton",
        background=t["surface"],
        foreground=t["text"],
        bordercolor=t["border_strong"],
        lightcolor=t["border_strong"],
        darkcolor=t["border_strong"],
        arrowcolor=t["subtle_fg"],
        relief="flat",
        padding=(10, 6),
        font=body,
    )
    style.map(
        "TMenubutton",
        background=[("active", t["surface_alt"]), ("pressed", t["surface_active"])],
        arrowcolor=[("active", t["accent"])],
    )

    # Schuifbalken
    for orient in ("Vertical", "Horizontal"):
        name = f"{orient}.TScrollbar"
        style.configure(
            name,
            background=t["scrollbar_thumb"],
            troughcolor=t["scrollbar"],
            bordercolor=t["scrollbar"],
            lightcolor=t["scrollbar_thumb"],
            darkcolor=t["scrollbar_thumb"],
            arrowcolor=t["subtle_fg"],
            relief="flat",
        )
        style.map(
            name,
            background=[("active", t["scrollbar_active"]), ("pressed", t["scrollbar_active"])],
            arrowcolor=[("active", t["accent"])],
        )

    # Scheidingslijnen / labelframes / voortgang
    style.configure("TSeparator", background=t["border"])
    style.configure(
        "TLabelframe",
        background=t["app_bg"],
        bordercolor=t["border"],
        lightcolor=t["border"],
        darkcolor=t["border"],
        relief="flat",
    )
    style.configure("TLabelframe.Label", background=t["app_bg"], foreground=t["header_fg"], font=(heading, 10))
    for name in ("TProgressbar", "Horizontal.TProgressbar", "Vertical.TProgressbar"):
        style.configure(
            name,
            background=t["accent"],
            troughcolor=t["surface_alt"],
            bordercolor=t["border"],
            lightcolor=t["accent"],
            darkcolor=t["accent"],
        )

    # Notebook (voor de zekerheid, mocht het ooit gebruikt worden)
    style.configure("TNotebook", background=t["app_bg"], bordercolor=t["border"])
    style.configure(
        "TNotebook.Tab",
        background=t["surface_alt"],
        foreground=t["subtle_fg"],
        padding=(14, 7),
        font=body,
    )
    style.map(
        "TNotebook.Tab",
        background=[("selected", t["surface"])],
        foreground=[("selected", t["accent"])],
    )


def _entry_like(t: dict) -> dict:
    return dict(
        fieldbackground=t["field_bg"],
        foreground=t["text"],
        bordercolor=t["field_border"],
        lightcolor=t["field_border"],
        darkcolor=t["field_border"],
        insertcolor=t["accent"],
        padding=(8, 5),
        relief="flat",
    )


def _apply_named_fonts(root, fonts: dict):
    for name in _UI_FACE_FONTS:
        try:
            tkfont.nametofont(name).configure(family=fonts["ui"])
        except tk.TclError:
            continue
    try:
        tkfont.nametofont("TkFixedFont").configure(family=fonts["mono"])
    except tk.TclError:
        pass


def _apply_options(root, t: dict, fonts: dict):
    """Kleur niet-ttk chrome (menu's, listboxes, popdowns) via de option-db.

    Werkt op widgets die *na* deze aanroep worden gemaakt; bestaande widgets
    worden door ``UIBuilderMixin._apply_theme_colors`` direct herkleurd.
    """
    ui = fonts["ui"]
    opt = root.option_add
    # Context- en menubalk-menu's
    opt("*Menu.background", t["menu_bg"])
    opt("*Menu.foreground", t["menu_fg"])
    opt("*Menu.activeBackground", t["menu_active_bg"])
    opt("*Menu.activeForeground", t["menu_active_fg"])
    opt("*Menu.activeBorderWidth", 0)
    opt("*Menu.borderWidth", 1)
    opt("*Menu.relief", "flat")
    opt("*Menu.font", (ui, 10))
    # Standaard listboxes
    opt("*Listbox.background", t["field_bg"])
    opt("*Listbox.foreground", t["text"])
    opt("*Listbox.selectBackground", t["selection_bg"])
    opt("*Listbox.selectForeground", t["selection_fg"])
    opt("*Listbox.borderWidth", 0)
    opt("*Listbox.highlightThickness", 0)
    opt("*Listbox.font", (ui, 10))
    # Combobox-popdown (een tk Listbox achter de schermen)
    opt("*TCombobox*Listbox.background", t["surface"])
    opt("*TCombobox*Listbox.foreground", t["text"])
    opt("*TCombobox*Listbox.selectBackground", t["selection_bg"])
    opt("*TCombobox*Listbox.selectForeground", t["selection_fg"])
    opt("*TCombobox*Listbox.font", (ui, 10))


def apply(root, theme_name: str) -> dict:
    """Pas het volledige warme thema toe op ``root`` en geef de fonts terug."""
    t = ui_theme.tokens(theme_name)
    fonts = resolve_fonts(root)
    style = ttk.Style(root)
    configure_ttk(style, t, fonts)
    _apply_named_fonts(root, fonts)
    _apply_options(root, t, fonts)
    try:
        root.configure(background=t["app_bg"])
    except tk.TclError:
        pass
    return fonts
